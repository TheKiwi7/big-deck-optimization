using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
#if BENCH
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;
#endif

namespace BigDeckOptimization.Patches;

/// <summary>
/// Squeeze, Perfected Strike, Crescent Spear and (game v0.113+) Wah Symphony count every card of their owner's combat piles
/// each time their value is calculated: for each of them in the hand, at each refresh and preview (~0.35 ms at 10,000 cards,
/// several times per play).
///
/// The count is kept per player and per card, as of a <see cref="FastListeners.PileVersion"/>. When every pile change since is
/// in <see cref="FastListeners"/>' moves, those are applied to it; otherwise the piles are counted again, as the game does.
/// What's counted (tags, star cost) must be fixed in a card: each card type's getters are read once (<see cref="FlagsOf"/>), and
/// while a card whose type may answer differently over time is in the piles (a content mod's card computing its tags or star
/// cost from its state), or is the asking card, the game counts. None of the four reads anything else. Squeeze leaves itself
/// out: <see cref="PileIndex"/> says whether it's in those piles, and when unsure the game's code runs. One answer in
/// <see cref="VerifyEvery"/> lets the game's code run and compares; a difference turns this off.
///
/// Answered only inside the card redraw, <c>NCard.UpdateVisuals</c> (<see cref="InRedraw"/>): the dynamic-variable preview and
/// the description formatting that follows it (on v0.111 <c>CalculatedVar.ToString</c> and <c>GetBaseValueForIConvertible</c>
/// calculate while formatting). The game reads <c>PreviewValue</c> only in formatters; its actions call <c>Calculate</c>
/// themselves (<c>AttackCommand</c> once per hit, Perfected Strike's <c>&gt; 12</c>), outside the redraw: those always get the
/// game's own count, whatever the memo holds. Not scoped on <c>UpdateCardPreview</c>: mods call it inside game logic (a potion
/// summing <c>PreviewValue</c> to decide what it does). The premise "a card's tags change only through tracked moves" can't be
/// proven over other mods' code (one adds a tag to a card's <c>Tags</c> set in place): outside the redraw, when the memo holds a
/// count valid at the current pile version (kept, or updated from the moves: never a walk), the game's result is compared with
/// it for free, the game having counted anyway, and a difference turns this off (a card property changed with no pile move, or
/// a missed pile move). A wrong display then lasts at most until the next play of such a card, or the next scheduled check of a
/// display caller.
///
/// The scope covers everything that runs synchronously inside <c>UpdateVisuals</c>' body: another mod's transpiler or inner
/// patch there, a Godot signal emitted from it and handled inline, an await continuation that happens to run inline in it (none
/// of these found calculating game logic). On v0.113 <c>DynamicVar.ToString()</c> and its <c>IConvertible</c> methods return
/// <c>ShownValue = (int)PreviewValue</c> (DynamicVar.cs:101,186,236): the same class of read as <c>PreviewValue</c>.
///
/// Display premise, the residue: a mod reading inside an action the <c>PreviewValue</c> the last redraw left on one of these
/// cards (none found in the top 500 Workshop mods: their <c>PreviewValue</c> reads are each of its own card's variable), or a
/// transpiler or inner patch on <c>UpdateVisuals</c> acting on such a value (the 76 mods of the top 500 patching it do so for
/// visuals by name). Other mods' prefixes and postfixes on <c>UpdateVisuals</c> run outside the scope (it opens in a prefix at
/// <c>int.MinValue</c> and closes in a postfix at <c>int.MaxValue</c>): they get the game's count, as without this. Harmony
/// orders equal priorities by registration, so two run inside it: a prefix at <c>int.MinValue</c> registered after this mod's,
/// and a postfix at <c>int.MaxValue</c> registered before it. Other mods' patches on what the body calls run inside it too
/// (<c>CardModel.UpdateDynamicVarPreview</c>, <c>DynamicVar.UpdateCardPreview</c> and its overrides,
/// <c>CardModel.GetDescriptionForPile</c>, the formatters): they get the memo's answer. Inside the scope, the memo's answer is
/// the game's except when the premise fails.
///
/// Outside the redraw, a display caller is answered too (<see cref="DisplayCaller"/>, part <see cref="DisplayName"/>, 2026-11-21
/// evening): a calculation reached on the main thread from a Godot frame or deferred callback through other mods' code only, no
/// game code above the game's preview chain (SpeedX's damage preview, its <c>LiteralAuditor</c>'s <c>UpdateDynamicVarPreview</c>
/// from <c>TurboOverlay._Process</c>, each frame a counting attack card is hovered or aimed; BetterSpire2's hand panel, its
/// deferred <c>RefreshCards</c> previewing each shown card through <c>MethodInfo.Invoke</c>, a teammate's hand included: with no count
/// held for those piles, a display caller walks them as the redraw does). The engine must have entered through a <c>Callable</c>
/// queued with the managed <c>Callable.CallDeferred</c> or a node's <c>_Process</c>/<c>_PhysicsProcess</c>/<c>_Draw</c>
/// (<see cref="RootAllowed"/>): a signal handler or a <c>SignalAwaiter</c> continuation is refused. Those calls were free checks
/// before they were answered: one display caller per card kind and player's piles in <see cref="DisplayCheckMs"/> gets the game's
/// count instead, compared (<see cref="DisplayCheckDue"/>): the next display call more than that after the last check turns a
/// stale memo off; a panel refreshed only on events (BetterSpire2's) keeps its last label until its next refresh, a per-frame
/// caller (SpeedX's) is corrected within that time. Every other calculation outside the redraw counts as the game does (~0.25-0.35 ms per counting card at
/// 10,000 cards), with the free comparison above.
/// </summary>
public static class DeckCountMemo
{
    public const string Name = "DeckCountMemo";

    /// <summary>v0.107.1 code mirrored: each card's multiplier and what it counts.</summary>
    private const string SqueezeFingerprint = "44A0327D171704CF";
    private const string SqueezeCountsFingerprint = "8E1D145CE41BBD4F";
    private const string StrikeFingerprint = "5763108E0FCB3099";
    private const string StrikeCountsFingerprint = "E8CCC3F56A47163F";
    private const string SpearFingerprint = "21EE28A0E953DF12";
    private const string SpearCountsFingerprint = "DA71AF536A9834A6";
    /// <summary>
    /// v0.113.1 <c>WahSymphony</c> (not in v0.111.1): its multiplier (<c>AllCards.Count(...)</c>) and what it counts,
    /// <c>Tags.Contains</c> of the tags 6, 7 and 8 (<c>Wah</c>, <c>Cho</c>, <c>Ki</c>: the constants pinned here).
    /// </summary>
    private const string WahFingerprint = "4F023E398297CBA2";
    private const string WahCountsFingerprint = "6200908F542A0E45";
    /// <summary>
    /// <c>NCard.UpdateVisuals</c>, the redraw the memo answers in: what it calls (portrait, title, costs, enchantment, the
    /// dynamic-variable preview, the description for the pile or the upgrade preview, the label) is display. v0.107.1; v0.111.1
    /// adds an <c>UpdatePortrait()</c> call; v0.113.1 also sets the description label's <c>TextDirection</c> (right-to-left
    /// languages). Another body: no scope, so no answer of the memo (the game counts everywhere).
    /// </summary>
    private static readonly string[] RedrawFingerprints = { "9FE8AA3ED05C9612", "2DBC4C3E4A2E2363", "83C3FCCC8E69D6A4" };
    /// <summary>
    /// <c>CardModel.Tags</c>: <c>_tags ?? (_tags = CanonicalTags)</c> (v0.107.1, v0.111, v0.113): a card's tags are its type's
    /// <c>CanonicalTags</c>, kept from the first read, so fixed when that getter returns a constant.
    /// </summary>
    private const string BaseTagsFingerprint = "63319788A75CA671";
    /// <summary>
    /// <c>PlayerCombatState.AllCards</c>: <c>AllPiles.SelectMany(p =&gt; p.Cards)</c> (v0.107.1, v0.111, v0.113), what the game counts;
    /// the memo counts <c>AllPiles</c> itself (same constant as <c>LimitlessRecalcSkip</c>).
    /// </summary>
    private const string AllCardsFingerprint = "7893F6315D894A5A";

    internal const int SqueezeKind = 0;
    internal const int StrikeKind = 1;
    internal const int SpearKind = 2;
    internal const int WahKind = 3;
    internal const int Kinds = 4;
    internal static readonly string[] KindNames = { "Squeeze", "PerfectedStrike", "CrescentSpear", "WahSymphony" };

    /// <summary>What Wah Symphony's lambda checks (its fingerprint pins the constants): Wah, Cho, Ki on game v0.113.</summary>
    private const CardTag WahTag = (CardTag)6;
    private const CardTag ChoTag = (CardTag)7;
    private const CardTag KiTag = (CardTag)8;

#if BENCH
    private static int _verifyEvery = 16;
#else
    private static int _verifyEvery = 256;
#endif

    /// <summary>One answer in this many (inside the redraw) lets the game count and compares; set, the next check is that far.</summary>
    internal static int VerifyEvery
    {
        get => _verifyEvery;
        set
        {
            _verifyEvery = value;
            _untilCheck = value;
        }
    }

#if BENCH
    /// <summary>Positive control (<c>bdperf deckcount wrong</c>): added to every answer.</summary>
    internal static int WrongForTest { get; set; }

    /// <summary>Per kind, the game's last result outside the redraw (<c>bdperf deckcount gamecount</c>), or null.</summary>
    internal static readonly decimal?[] LastOutside = new decimal?[Kinds];

    /// <summary>
    /// <c>bdperf deckcount freecheck off</c>: no comparison outside the redraw, so a stale memo stays on and keeps answering the
    /// redraws while the actions still count with the game's code (the damage positive control).
    /// </summary>
    internal static bool FreeCheckOffForTest { get; set; }

    /// <summary>Memo answers (in the redraw) given while a card play action was running: the memo was live during the play.</summary>
    internal static long AnsweredInPlay { get; private set; }
    [ThreadStatic] private static int _benchKind;
    [ThreadStatic] private static bool _benchOutside;
    [ThreadStatic] private static bool _benchDisplay;

    /// <summary>Per kind, the memo's last answer to a display caller outside the redraw, or null (LastOutside stays the game's).</summary>
    internal static readonly decimal?[] LastDisplay = new decimal?[Kinds];

    /// <summary><c>bdperf deckcount stackcost</c>: the stack of the next calculation outside the redraw, kept for the bench.</summary>
    internal static bool StackProbeForTest { get; set; }
    internal static System.Diagnostics.StackTrace? LastProbe { get; set; }
#endif

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    /// <summary>
    /// Counts inside the redraw the memo held as they were (<see cref="Kept"/>) or brought along from the moves
    /// (<see cref="Updated"/>), whatever came of them (an answer, a check, or the game's count when a card of a changing type is
    /// in the piles): not the effect, which is <see cref="Answered"/>.
    /// </summary>
    public static long Kept { get; private set; }
    public static long Updated { get; private set; }
    public static long Walks { get; private set; }
    public static long Unsure { get; private set; }
    /// <summary>Answers compared with the game's inside the redraw (one in <see cref="VerifyEvery"/>).</summary>
    public static long Checks { get; private set; }
    /// <summary>Differences found, inside the redraw or outside it (<see cref="FreeMismatches"/>): each turns this off.</summary>
    public static long Mismatches { get; private set; }
    /// <summary>Answers given from the memo, inside the redraw.</summary>
    public static long Answered { get; private set; }
    /// <summary>Calculations inside the redraw (answered, compared or left to the game when unsure).</summary>
    public static long InRedrawCalls { get; private set; }
    /// <summary>Calculations outside the redraw (an action's, a mod's) the memo didn't answer: the game counts.</summary>
    public static long OutsideCalls { get; private set; }
    /// <summary>Outside the redraw, the game's result compared with a count the memo held (no walk of its own).</summary>
    public static long FreeChecks { get; private set; }
    public static long FreeMismatches { get; private set; }
    /// <summary>
    /// The display callers part (<see cref="DisplayCaller"/>): its own name (fault, <c>bdopt</c> parts) and switch, under
    /// <c>combat_refresh</c> as the memo itself.
    /// </summary>
    public const string DisplayName = Name + ".Display";
    public static bool DisplayEnabled { get; set; } = true;
    /// <summary>Answers given from the memo outside the redraw, to a display caller.</summary>
    public static long AnsweredDisplay { get; private set; }
    /// <summary>Callers looked at (a stack walk each) outside the redraw, and those left to the game's count.</summary>
    public static long DisplayLooks { get; private set; }
    public static long DisplayRefused { get; private set; }
    /// <summary>Display callers left to the game's count without a look, the deck being small (<see cref="DisplayMinCards"/>).</summary>
    public static long DisplaySmallDecks { get; private set; }
    /// <summary>
    /// Calculations outside the redraw with no count held whose piles last held a card of a changing type for that kind: no
    /// display walk, no stack look (<see cref="HeldMoving"/>), the game counts.
    /// </summary>
    public static long DisplaySkippedMoving { get; private set; }
    /// <summary>
    /// Display callers given the game's count on the schedule (<see cref="DisplayCheckDue"/>) and compared with the memo's, and the
    /// differences found (each turns the memo off).
    /// </summary>
    public static long DisplayChecks { get; private set; }
    public static long DisplayMismatches { get; private set; }
    /// <summary>Display callers that found no count held for their piles (a teammate's), walked as the redraw does.</summary>
    public static long DisplayWalks { get; private set; }
    /// <summary>Per kind, counts of its own computed inside the redraw or for a display caller (answered or compared): <c>bdperf deckcount answered</c>.</summary>
    internal static readonly long[] KindCounted = new long[Kinds];

    private static readonly Func<CardModel, bool>[] _counted =
    {
        c => c.Tags.Contains(CardTag.OstyAttack),
        c => c.Tags.Contains(CardTag.Strike),
        c => c.CanonicalStarCost >= 0 || c.HasStarCostX,
        c => c.Tags.Contains(WahTag) || c.Tags.Contains(ChoTag) || c.Tags.Contains(KiTag),
    };

    /// <summary>What may change over a card's life, per type (<see cref="FlagsOf"/>): its tags, its star cost.</summary>
    private const byte TagsMove = 1;
    private const byte StarMove = 2;
    private static readonly byte[] _moveMask = { TagsMove, TagsMove, StarMove, TagsMove };

    /// <summary>Per kind, the cards whose type may change what that kind counts: only their type is read, nothing of the card.</summary>
    private static readonly Func<CardModel, bool>[] _moving =
    {
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & StarMove) != 0,
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
    };

    private sealed class Counts
    {
        public readonly long[] Version = Enumerable.Repeat(-1L, Kinds).ToArray();
        public readonly int[] Count = new int[Kinds];
        /// <summary>Cards in the piles of a type that may change what the kind counts: while any, the game counts.</summary>
        public readonly int[] Moving = new int[Kinds];
        /// <summary><see cref="_flagsEpoch"/> the counts were taken with: the moving counts depend on the type flags.</summary>
        public readonly long[] Epoch = new long[Kinds];
        /// <summary>When a display caller last got the game's count instead of an answer (<see cref="DisplayCheckDue"/>), as a <c>Stopwatch</c> timestamp.</summary>
        public readonly long[] DisplayCheckedAt = new long[Kinds];
    }

    /// <summary>Tied to the combat's piles: nothing here outlives them.</summary>
    private static readonly ConditionalWeakTable<PlayerCombatState, Counts> _counts = new();
    private static readonly MethodBase?[] _targets = new MethodBase?[Kinds];
    /// <summary>Per card, what a kept count skips calling: the count lambda, the piles' card list, what it reads of each card.</summary>
    private static readonly MethodBase?[][] _skipped = new MethodBase?[Kinds][];
    private static int _untilCheck;

    /// <summary>
    /// Inside <c>NCard.UpdateVisuals</c>' own body (main thread): set by the last prefix, put back by the first postfix, or by
    /// the finalizer after a throw. Each call puts back the value it found (its <c>__state</c>), so a finalizer Harmony runs
    /// again, or a postfix and a finalizer both, change nothing more; a call whose prefix didn't run puts back false, which
    /// only leaves the rest of an outer redraw to the game.
    /// </summary>
    [ThreadStatic] private static bool _inRedraw;

    /// <summary>
    /// What the prefix hands its postfix: the count to compare the game's result with, whether outside the redraw, and whether a
    /// display caller's scheduled check (<see cref="DisplayCheckDue"/>).
    /// </summary>
    private readonly record struct Expected(decimal Count, bool Free, bool Display = false);

    private static MethodInfo? _tagsGetter, _canonicalTagsGetter, _starCostGetter, _starXGetter;
    private static bool _baseTagsKnown;

    public static void Apply(Harmony harmony)
    {
        MethodBase? allCards = Reflect.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "AllCards");
        // What the four cards count is AllCards; the memo walks AllPiles: only while AllCards is that walk (else all four vanilla).
        PatchSafety.RequireFingerprint(allCards ?? throw new PatchSkippedException("PlayerCombatState.AllCards not found"), AllCardsFingerprint);
        // The game's count reads each pile through CardPile.Cards: another mod answering another list there gets the game's count.
        MethodBase? pileCards = Reflect.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards));
        _tagsGetter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Tags));
        _canonicalTagsGetter = Reflect.PropertyGetter(typeof(CardModel), "CanonicalTags");
        _starCostGetter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.CanonicalStarCost));
        _starXGetter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.HasStarCostX));
        try
        {
            // Kept from the first read: only while nothing else writes the kept set.
            PatchSafety.RequireFingerprint(_tagsGetter ?? throw new MissingMethodException("CardModel.Tags"), BaseTagsFingerprint);
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_tags", "get_Tags");
            _baseTagsKnown = true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            _baseTagsKnown = false;
            Log.Info($"[{ModEntry.Id}] {Name}: cards without their own Tags counted by the game: {e.Message}");
        }
        _readers[SqueezeKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        _readers[StrikeKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        _readers[SpearKind] = new[] { allCards, pileCards, _starCostGetter, _starXGetter };
        _readers[WahKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        // The redraw first: without it the memo would never answer.
        PatchRedraw(harmony);
        bool squeeze = Card(SqueezeKind, typeof(Squeeze), nameof(SqueezePrefix), harmony, SqueezeFingerprint, SqueezeCountsFingerprint);
        bool strike = Card(StrikeKind, typeof(PerfectedStrike), nameof(StrikePrefix), harmony, StrikeFingerprint, StrikeCountsFingerprint);
        bool spear = Card(SpearKind, typeof(CrescentSpear), nameof(SpearPrefix), harmony, SpearFingerprint, SpearCountsFingerprint);
        bool wah = Card(WahKind, Reflect.TypeByName("MegaCrit.Sts2.Core.Models.Cards.WahSymphony"), nameof(WahPrefix), harmony, WahFingerprint, WahCountsFingerprint);
        if (!squeeze && !strike && !spear && !wah)
        {
            throw new PatchSkippedException("none of its cards has the known code");
        }
        Applied = true;
        // What Answer asks about for each card kind patched (display only), and the card types' getters FlagsOf read so far
        // (types met in combats: asked again at each preflight).
        for (int kind = 0; kind < _targets.Length; kind++)
        {
            if (_targets[kind] != null)
            {
                PatchSafety.Watch(Name, _targets[kind]);
                PatchSafety.Watch(Name, _skipped[kind]);
            }
        }
        PatchSafety.WatchAll(Name, () => _watched.Keys.ToArray());
        _mainThread = Environment.CurrentManagedThreadId;
        // A part of its own: a changed preview chain leaves only the display callers to the game, the redraw keeps its answers.
        ModEntry.TryApply(DisplayName, ApplyDisplay);
    }

    /// <summary>
    /// The scope (<see cref="_inRedraw"/>) around <c>NCard.UpdateVisuals</c>' own body: opened in a prefix sorted after every
    /// other (the lowest priority; Harmony orders the same priority by registration), closed in a postfix sorted before every
    /// other (the highest, as <see cref="GridSortCache"/>'s), and by a finalizer after a throw. Another mod's prefix or postfix
    /// there runs outside it, on the game's count. Not a back-off when other mods patch it: nothing of it is mirrored, and the
    /// scope only says which calculations are display.
    /// </summary>
    private static void PatchRedraw(Harmony harmony)
    {
        MethodInfo redraw = Reflect.Method(typeof(NCard), nameof(NCard.UpdateVisuals), new[] { typeof(PileType), typeof(CardPreviewMode) })
            ?? throw new PatchSkippedException("NCard.UpdateVisuals not found");
        string actual = MethodFingerprint.Of(redraw);
        if (!RedrawFingerprints.Any(expected => PatchSafety.FingerprintMatches(actual, expected)))
        {
            throw new PatchSkippedException($"NCard.UpdateVisuals changed (fingerprint {actual}, expected one of {string.Join(", ", RedrawFingerprints)}): left vanilla");
        }
        PatchGuard.Patch(harmony, redraw,
            prefix: new HarmonyMethod(typeof(DeckCountMemo), nameof(RedrawPrefix)) { priority = int.MinValue },
            postfix: new HarmonyMethod(typeof(DeckCountMemo), nameof(RedrawPostfix)) { priority = int.MaxValue },
            finalizer: new HarmonyMethod(typeof(DeckCountMemo), nameof(RedrawFinalizer)));
    }

    private static void RedrawPrefix(out bool __state)
    {
        __state = _inRedraw;
        _inRedraw = true;
    }

    private static void RedrawPostfix(bool __state) => _inRedraw = __state;

    private static void RedrawFinalizer(bool __state) => _inRedraw = __state;

    /// <summary>Whether a calculation now is the card redraw's (where the memo answers).</summary>
    internal static bool InRedraw => _inRedraw;

    /// <summary>One card, on its own: a card that changed only leaves that card to the game.</summary>
    private static bool Card(int kind, Type? card, string prefix, Harmony harmony, string fingerprint, string countsFingerprint)
    {
        if (card == null)
        {
            // Wah Symphony on v0.107 and v0.111: no such card, nothing to answer.
            Log.Info($"[{ModEntry.Id}] {Name}: {KindNames[kind]} skipped: not needed on this game version");
            return false;
        }
        try
        {
            MethodInfo multiplier = Lambda(card, typeof(decimal));
            PatchSafety.RequireFingerprint(multiplier, fingerprint);
            MethodInfo counts = Lambda(card, typeof(bool));
            PatchSafety.RequireFingerprint(counts, countsFingerprint);
            _skipped[kind] = _readers[kind].Append(counts).ToArray();
            PatchGuard.Patch(harmony, multiplier, prefix: new HarmonyMethod(typeof(DeckCountMemo), prefix), postfix: new HarmonyMethod(typeof(DeckCountMemo), nameof(Postfix)));
            _targets[kind] = multiplier;
            return true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {card.Name} left vanilla: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The compiler's method for a lambda of the card's variables, found by what it returns in whichever generated class holds it: their
    /// names carry a number that shifts when the game adds a member, with the same code.
    /// </summary>
    private static MethodInfo Lambda(Type card, Type returns)
    {
        MethodInfo[] found = card.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(holder => holder.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("get_CanonicalVars") && m.ReturnType == returns).ToArray();
        return found.Length == 1 ? found[0] : throw new MissingMethodException(card.Name, "CanonicalVars lambda");
    }

    private static bool SqueezePrefix(CardModel __0, ref decimal __result, out Expected? __state) => Answer(SqueezeKind, __0, ref __result, out __state);

    private static bool StrikePrefix(CardModel __0, ref decimal __result, out Expected? __state) => Answer(StrikeKind, __0, ref __result, out __state);

    private static bool SpearPrefix(CardModel __0, ref decimal __result, out Expected? __state) => Answer(SpearKind, __0, ref __result, out __state);

    private static bool WahPrefix(CardModel __0, ref decimal __result, out Expected? __state) => Answer(WahKind, __0, ref __result, out __state);

    private static readonly MethodBase?[][] _readers = new MethodBase?[Kinds][];

    /// <summary>Another mod patches what a kept count skips (a missing one counts as patched).</summary>
    private static bool SkippedPatched(int kind)
    {
        foreach (MethodBase? method in _skipped[kind])
        {
            // No tolerance: calls a kept count skips.
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Inside the redraw: the memo's count, one in <see cref="VerifyEvery"/> compared with the game's. Outside it: the game's
    /// count always, compared with the memo's when it holds one valid now without a walk (<see cref="Count"/>).
    /// </summary>
    private static bool Answer(int kind, CardModel card, ref decimal result, out Expected? expected)
    {
        expected = null;
        bool inRedraw = _inRedraw;
#if BENCH
        _benchKind = kind;
        _benchOutside = !inRedraw;
        _benchDisplay = false;
        if (StackProbeForTest && !inRedraw)
        {
            LastProbe = new System.Diagnostics.StackTrace(0, false);
        }
#endif
        // Inside ListenerCache.RunVanilla the result is a reference others are compared with: the game's own.
        // No tolerance: the count's getter is answered from the memo, its patches wouldn't run.
        if (!Enabled || ListenerCache.IsSuspended || !FastListeners.PilesKnown || PatchSafety.IsPatchedByOthers(_targets[kind]) || SkippedPatched(kind))
        {
            return true;
        }
        if (inRedraw)
        {
            InRedrawCalls++;
        }
        // Outside the redraw: counted once the memo didn't answer (a display caller's answer is AnsweredDisplay).
        bool outsideCounted = !inRedraw;
        try
        {
            if (card.Owner.PlayerCombatState is not { } state)
            {
                return true;
            }
            RecheckFlags();
            // The asking card's own tags are read below (Squeeze): only when they can't depend on when they're read.
            if ((FlagsOf(card.GetType()) & _moveMask[kind]) != 0)
            {
                Unsure += inRedraw ? 1 : 0;
                return true;
            }
            int own = 0;
            if (kind == SqueezeKind && _counted[SqueezeKind](card))
            {
                own = PileIndex.PlacesIn(card, state.AllPiles);
                if (own < 0)
                {
                    Unsure += inRedraw ? 1 : 0;
                    return true;
                }
            }
            int? held = Count(kind, state, inRedraw, out bool moving);
            bool display = false;
            // The display walk only where it can end in an answer: not while the piles last held a card of a changing type for this
            // kind (the walk would end `moving` and the game count anyway; a rare missed answer once such cards are gone), and the
            // stack looked at only then (DisplayCaller's own cheap checks first: part on, main thread, deck size).
            bool heldMoving = held == null && !inRedraw && HeldMoving(state, kind);
            DisplaySkippedMoving += heldMoving ? 1 : 0;
            if (held == null && !inRedraw && !heldMoving && DisplayCaller(kind, state))
            {
                // A display caller and no count held for these piles (a teammate's, whose cards this peer doesn't redraw until they
                // are played): walked as the redraw does, then kept or brought along from the moves for the next display callers.
                display = true;
                DisplayWalks++;
                held = Count(kind, state, walk: true, out moving, tally: false);
                // A count just walked is what the scheduled check would compare with: the next check one interval from now.
                if (held != null && _counts.TryGetValue(state, out Counts? walked))
                {
                    walked.DisplayCheckedAt[kind] = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
            if (held is not { } total || moving)
            {
                Unsure += inRedraw ? 1 : 0;
                return true;
            }
            int count = total - own;
#if BENCH
            count += WrongForTest;
#endif
            // Outside the redraw, a display caller (a Godot frame or deferred callback reaching the preview through other mods'
            // code only) is answered as the redraw is; the stack is looked at once a count is held, or before a walk above.
            display = display || !inRedraw && DisplayCaller(kind, state);
            if (!inRedraw && !display)
            {
#if BENCH
                if (FreeCheckOffForTest)
                {
                    return true;
                }
#endif
                // An action's or another mod's calculation: the game's result, compared in the postfix.
                expected = new Expected(count, Free: true);
                return true;
            }
            KindCounted[kind]++;
            if (display && DisplayCheckDue(state, kind))
            {
                // The free check those calls were before they were answered, on a schedule: the game counts, compared in the postfix.
                expected = new Expected(count, Free: true, Display: true);
                return true;
            }
            if (--_untilCheck <= 0)
            {
                _untilCheck = VerifyEvery;
                expected = new Expected(count, Free: false);
                return true;
            }
            if (display)
            {
                AnsweredDisplay++;
                outsideCounted = false;
#if BENCH
                _benchDisplay = true;
#endif
                result = count;
                return false;
            }
            Answered++;
#if BENCH
            if (RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction is PlayCardAction)
            {
                AnsweredInPlay++;
            }
#endif
            result = count;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
        finally
        {
            if (outsideCounted)
            {
                OutsideCalls++;
            }
        }
    }

    private static void Postfix(decimal __result, Expected? __state)
    {
#if BENCH
        if (_benchDisplay)
        {
            LastDisplay[_benchKind] = __result;
        }
        else if (_benchOutside)
        {
            LastOutside[_benchKind] = __result;
        }
#endif
        if (__state is not { } expected)
        {
            return;
        }
        if (expected.Display)
        {
            DisplayChecks++;
        }
        else if (expected.Free)
        {
            FreeChecks++;
        }
        else
        {
            Checks++;
        }
        if (expected.Count != __result)
        {
            Mismatches++;
            if (expected.Display)
            {
                DisplayMismatches++;
                Disable(new InvalidOperationException($"the kept count {expected.Count} differs from the game's count {__result} for a display caller outside the card redraw "
                    + $"(checked at most every {DisplayCheckMs} ms per card and player: a card property changed with no pile move, or a missed pile move)"));
            }
            else if (expected.Free)
            {
                FreeMismatches++;
                Disable(new InvalidOperationException($"the kept count {expected.Count} differs from the game's count {__result} outside the card redraw "
                    + "(a card property changed with no pile move, or a missed pile move): only the display had the memo's count"));
            }
            else
            {
                Disable(new InvalidOperationException($"counted {expected.Count}, the game {__result}"));
            }
        }
    }

    /// <summary>
    /// The kind's count over the piles, and whether a card of a type that may change it is among them. With
    /// <paramref name="walk"/> false (outside the redraw), only a count held at the current pile version or brought to it from
    /// the moves, else null: no walk of its own, and nothing counted in <see cref="Kept"/>, <see cref="Updated"/>, <see cref="Walks"/>.
    /// </summary>
    private static int? Count(int kind, PlayerCombatState state, bool walk, out bool moving, bool tally = true)
    {
        // Kept, Updated and Walks count the redraw's only (a display caller's walk is DisplayWalks).
        tally &= walk;
        moving = false;
        Counts? counts;
        if (walk)
        {
            counts = _counts.GetOrCreateValue(state);
        }
        else if (!_counts.TryGetValue(state, out counts))
        {
            return null;
        }
        long version = FastListeners.PileVersion;
        if (counts.Epoch[kind] != _flagsEpoch)
        {
            if (!walk)
            {
                return null;
            }
            // Type flags read again since (another mod's patch came or went): the moving count is the old flags'.
            counts.Version[kind] = -1;
            counts.Epoch[kind] = _flagsEpoch;
        }
        if (counts.Version[kind] == version)
        {
            Kept += tally ? 1 : 0;
            moving = counts.Moving[kind] > 0;
            return counts.Count[kind];
        }
        IReadOnlyList<CardPile> piles = state.AllPiles;
        int count = counts.Count[kind];
        int movingCount = counts.Moving[kind];
        // Both from the same moves (MovesSince gives the same answer twice): the count, then the cards of changing types.
        // Only from a count taken with no card of a changing type in the piles: a move is judged when applied, so such a
        // card that entered under one answer and leaves under another would leave the count off once it is gone.
        if (counts.Version[kind] >= 0 && counts.Moving[kind] == 0
            && FastListeners.ApplyMoves(counts.Version[kind], piles, _counted[kind], ref count)
            && FastListeners.ApplyMoves(counts.Version[kind], piles, _moving[kind], ref movingCount))
        {
            Updated += tally ? 1 : 0;
        }
        else if (!walk)
        {
            return null;
        }
        else
        {
            Walks += tally ? 1 : 0;
            count = 0;
            movingCount = 0;
            Func<CardModel, bool> counted = _counted[kind];
            byte mask = _moveMask[kind];
            Type? lastType = null;
            byte lastFlags = 0;
            foreach (CardPile pile in piles)
            {
                IReadOnlyList<CardModel> cards = pile.Cards;
                for (int i = 0; i < cards.Count; i++)
                {
                    CardModel card = cards[i];
                    if (counted(card))
                    {
                        count++;
                    }
                    Type type = card.GetType();
                    if (type != lastType)
                    {
                        lastType = type;
                        lastFlags = FlagsOf(type);
                    }
                    if ((lastFlags & mask) != 0)
                    {
                        movingCount++;
                    }
                }
            }
        }
        counts.Count[kind] = count;
        counts.Moving[kind] = movingCount;
        counts.Version[kind] = version;
        moving = movingCount > 0;
        return count;
    }

    /// <summary>
    /// Whether the memo's last count of this kind for these piles (with the current type flags) found a card of a changing type:
    /// a walk now would most likely find it too, and the game counts while one is there.
    /// </summary>
    private static bool HeldMoving(PlayerCombatState state, int kind) =>
        _counts.TryGetValue(state, out Counts? counts) && counts.Epoch[kind] == _flagsEpoch && counts.Version[kind] >= 0 && counts.Moving[kind] > 0;

#if BENCH
    /// <summary>Bench: the memo forgets its counts for these piles (a display caller then finds none held: the display walk).</summary>
    internal static void ForgetForTest(PlayerCombatState state) => _counts.Remove(state);

    /// <summary>Bench: the memo's counts for these piles taken as of an older pile version (as after a move no redraw followed).</summary>
    internal static void AgeForTest(PlayerCombatState state)
    {
        if (_counts.TryGetValue(state, out Counts? counts))
        {
            for (int kind = 0; kind < Kinds; kind++)
            {
                if (counts.Version[kind] >= 0)
                {
                    counts.Version[kind] = Math.Max(0, counts.Version[kind] - 1);
                }
            }
        }
    }

    /// <summary>Bench: whether the memo holds a count of this kind for these piles (as of any pile version).</summary>
    internal static bool HasCount(PlayerCombatState state, int kind) => HeldCount(state, kind) != null;

    /// <summary>Bench: the count of this kind the memo holds for these piles (as of its pile version), or null.</summary>
    internal static int? HeldCount(PlayerCombatState state, int kind) =>
        _counts.TryGetValue(state, out Counts? counts) && counts.Version[kind] >= 0 ? counts.Count[kind] : null;

    /// <summary>Bench: whether cards of this type are left to the game for their tags (their type may change them).</summary>
    internal static bool TagsMayChange(Type type) => (FlagsOf(type) & TagsMove) != 0;
#endif

    // --- Display callers outside the redraw ---

    /// <summary>
    /// The game's preview chain from a card's preview down to the count (v0.107.1, v0.111.1, v0.113.1): what runs between a
    /// display caller's <c>UpdateDynamicVarPreview</c> and the counting lambda, each display only (it writes <c>PreviewValue</c>).
    /// The four cards' variable is a <c>CalculatedDamageVar</c>. Another body: the part is not applied.
    /// </summary>
    private static readonly (Type Type, string Method, Type[] Parameters, string[] Fingerprints)[] ChainMethods =
    {
        (typeof(CardModel), nameof(CardModel.UpdateDynamicVarPreview),
            new[] { typeof(CardPreviewMode), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet) },
            new[] { "15FF6441DB3CA930" }),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.CalculatedDamageVar), "UpdateCardPreview",
            new[] { typeof(CardModel), typeof(CardPreviewMode), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), typeof(bool) },
            new[] { "FB0A2A14BF00C00D", "812F80D69571BD55" }),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.CalculatedVar), "Calculate",
            new[] { typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature) },
            new[] { "205EB92C18FAAA58" }),
    };

    /// <summary>The allowed game frames of a display caller's stack (the chain and the counting lambdas), by module and token.</summary>
    private static readonly HashSet<(Module, int)> _chain = new();
    private static bool _displayReady;
    private static int _mainThread;
    private static readonly Assembly _sts2 = typeof(CardModel).Assembly;

    private static void ApplyDisplay(Harmony harmony)
    {
        foreach ((Type type, string name, Type[] parameters, string[] fingerprints) in ChainMethods)
        {
            MethodInfo method = Reflect.Method(type, name, parameters) is { } found && found.DeclaringType == type
                ? found : throw new MissingMethodException(type.Name, name);
            string actual = MethodFingerprint.Of(method);
            if (!fingerprints.Any(expected => PatchSafety.FingerprintMatches(actual, expected)))
            {
                throw new PatchSkippedException($"{type.Name}.{name} changed (fingerprint {actual}, expected one of {string.Join(", ", fingerprints)}): display callers counted by the game");
            }
            _chain.Add((method.Module, method.MetadataToken));
        }
        foreach (MethodBase? target in _targets)
        {
            if (target != null)
            {
                _chain.Add((target.Module, target.MetadataToken));
            }
        }
        // How the engine enters managed code (RootAllowed): Godot's own glue, the same on every game version supported.
        Assembly godot = typeof(Godot.Callable).Assembly;
        _invokeWithVariantArgs = godot.GetType("Godot.DelegateUtils")?.GetMethod("InvokeWithVariantArgs", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Godot.DelegateUtils", "InvokeWithVariantArgs");
        _instanceCall = godot.GetType("Godot.Bridge.CSharpInstanceBridge")?.GetMethod("Call", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Godot.Bridge.CSharpInstanceBridge", "Call");
        MethodInfo callDeferred = typeof(Godot.Callable).GetMethod(nameof(Godot.Callable.CallDeferred), new[] { typeof(Godot.Variant[]) })
            ?? throw new MissingMethodException(nameof(Godot.Callable), nameof(Godot.Callable.CallDeferred));
        foreach ((MethodBase method, string fingerprint) in new[] { (_invokeWithVariantArgs, InvokeWithVariantArgsFingerprint), (_instanceCall, InstanceCallFingerprint), ((MethodBase)callDeferred, CallDeferredFingerprint) })
        {
            string actual = MethodFingerprint.Of(method);
            if (!PatchSafety.FingerprintMatches(actual, fingerprint))
            {
                throw new PatchSkippedException($"{method.DeclaringType?.Name}.{method.Name} changed (fingerprint {actual}, expected {fingerprint}): display callers counted by the game");
            }
        }
        PatchGuard.Patch(harmony, callDeferred, prefix: new HarmonyMethod(typeof(DeckCountMemo), nameof(DeferredPrefix)));
        _displayReady = true;
    }

    /// <summary>
    /// Whether this calculation outside the redraw is a display caller's, to be answered as the redraw is (owner-approved rule of
    /// 2026-11-21). On the main thread, walking up from the count: the counting lambda, then only the game's preview chain
    /// (<see cref="ChainMethods"/>), then no game code at all up to the top of the stack: a Godot frame or deferred callback
    /// reaching the preview through other mods' code (SpeedX's damage preview, BetterSpire2's teammate hand panel). Refused, so
    /// counted by the game with the free check as before: any game frame above the chain (an action, a hook, an event, the game's
    /// own nodes), any async state machine (actions are async, and a continuation can't tell where it came from), the runtime's
    /// task and continuation machinery (a <c>ContinueWith</c> closure, a continuation posted to a synchronization context, Godot's
    /// included, a thread-pool work item), any frame of this mod's code but this file's at the bottom, a frame without a method, a
    /// Harmony replacement not mapped to its original. Allowed above the chain: reflection (and its <c>InvokeStub_</c> methods), the
    /// rest of the runtime, Godot's glue (a deferred <c>Callable</c>, a node's <c>_Process</c>), other mods. Premise (display): a mod
    /// reaching the preview from a frame or deferred call uses the value to show it (the catalogue of the downloaded mods found no
    /// other use; DESIGN "DeckCountMemo display callers"). How the engine entered (<see cref="RootAllowed"/>): only a deferred
    /// <c>Callable</c> or a per-frame virtual; a mod's lambda connected to a native Godot signal (<c>pressed</c>,
    /// <c>mouse_entered</c>) and a <c>SignalAwaiter</c> continuation are refused. Residue: a lambda both queued deferred and connected
    /// to a signal passes when the signal calls it (noted by method, not by delegate).
    /// </summary>
    private static bool DisplayCaller(int kind, PlayerCombatState state)
    {
        if (!DisplayEnabled || !_displayReady || Environment.CurrentManagedThreadId != _mainThread)
        {
            return false;
        }
        // Cost guard: below DisplayMinCards the game's count (about 21 ns a card) costs about what the walk does (3-10 us).
        int cards = 0;
        foreach (CardPile pile in state.AllPiles)
        {
            cards += pile.Cards.Count;
        }
        if (cards < DisplayMinCards)
        {
            DisplaySmallDecks++;
            return false;
        }
        DisplayLooks++;
        try
        {
            if (Walk(kind))
            {
                return true;
            }
            DisplayRefused++;
            return false;
        }
        catch (Exception e)
        {
            DisplayEnabled = false;
            PatchSafety.Fault(DisplayName);
            Log.Error($"[{ModEntry.Id}] {DisplayName} disabled, display callers counted by the game: {e}");
            return false;
        }
    }

    /// <summary>
    /// The combat's card count (all piles of the asking player) under which a display caller is left to the game's count without a
    /// look at the stack: the count costs about 21 ns a card (190-280 us at 11,247 cards), the walk 3-10 us, i.e. 150-500 cards.
    /// </summary>
    internal const int DisplayMinCards = 512;

    /// <summary>
    /// The display callers' check, as often as this per card kind and player's piles (the free check those calls were before
    /// 2026-11-21): at most one game count per 200 ms where SpeedX's per-frame preview asks ~620 times a second (~5 counts a
    /// second, ~0.25 ms each at 10,000 cards: ~1.3 ms a second, under 0.2% of the frame time), and the next display call more
    /// than 200 ms after the last check turns a stale memo off (a panel refreshed only on events keeps its last label until its
    /// next refresh). Per kind and player because a stale
    /// count is per kind and player: a global clock taken by whichever card asks first would leave another card's count unchecked.
    /// </summary>
    internal const int DisplayCheckMs = 200;
    private static long _displayCheckTicks = System.Diagnostics.Stopwatch.Frequency * DisplayCheckMs / 1000;

#if BENCH
    /// <summary><c>bdperf deckcount display checkms &lt;n&gt;|never</c>: the schedule (test only); never is <c>long.MaxValue</c>.</summary>
    internal static void SetDisplayCheckForTest(long ms) =>
        _displayCheckTicks = ms == long.MaxValue ? long.MaxValue : System.Diagnostics.Stopwatch.Frequency * ms / 1000;
#endif

    /// <summary>
    /// Whether this display caller gets the game's count, compared with the memo's (<see cref="DisplayCheckMs"/>): the first one
    /// of a kind for these piles, then one per interval. The memo holds a count for these piles here (<see cref="Count"/> just gave it).
    /// </summary>
    private static bool DisplayCheckDue(PlayerCombatState state, int kind)
    {
        if (!_counts.TryGetValue(state, out Counts? counts))
        {
            return true;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - counts.DisplayCheckedAt[kind] < _displayCheckTicks)
        {
            return false;
        }
        counts.DisplayCheckedAt[kind] = now;
        return true;
    }

    private const byte FrameChain = 1;
    private const byte FrameOther = 2;
    private const byte FrameRefused = 3;

    /// <summary>Per method met on a stack, what it is (main thread only; emptied when large: replacements come and go).</summary>
    private static readonly Dictionary<MethodBase, byte> _frames = new();
    /// <summary>Per Harmony replacement met, its original (null: not one of Harmony's).</summary>
    private static readonly Dictionary<MethodBase, MethodBase?> _originals = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Walk(int kind)
    {
        System.Diagnostics.StackTrace trace = new(0, false);
        int count = trace.FrameCount;
        int i = 0;
        // This file's own frames at the bottom (this walk, Answer, the card's prefix).
        while (i < count && trace.GetFrame(i)?.GetMethod()?.DeclaringType == typeof(DeckCountMemo))
        {
            i++;
        }
        bool inChain = true, first = true, belowWasReplacement = false;
        for (; i < count; i++)
        {
            System.Diagnostics.StackFrame? frame = trace.GetFrame(i);
            MethodBase? method = frame?.GetMethod();
            if (frame == null || method == null)
            {
                return false;
            }
            bool replacement = false;
            Assembly? assembly = method.DeclaringType?.Assembly;
            if (assembly != null && assembly != _sts2 && assembly != typeof(DeckCountMemo).Assembly)
            {
                // A Harmony replacement whose type MonoMod built (DMDCecilGenerator: an assembly loaded from bytes, not dynamic) is
                // judged as its original: a replaced game method above the chain is game code. Cached per method.
                if (OriginalOf(frame, method) is { } original)
                {
                    method = original;
                    replacement = true;
                }
                else if (assembly.IsDynamic)
                {
                    // A dynamic assembly's method that isn't a mapped replacement: unknown code.
                    return false;
                }
            }
            else if (assembly == null)
            {
                // A dynamic method: a Harmony replacement (judged as its original), MonoMod's glue right above one, or reflection's stub.
                if (OriginalOf(frame, method) is { } original)
                {
                    method = original;
                    replacement = true;
                }
                else if (belowWasReplacement && method.Name.StartsWith("Glue:AbiFixup<", StringComparison.Ordinal))
                {
                    belowWasReplacement = false;
                    continue;
                }
                else if (!first && method.Module == typeof(object).Module && method.Name.StartsWith("InvokeStub_", StringComparison.Ordinal))
                {
                    inChain = false;
                    belowWasReplacement = false;
                    continue;
                }
                else
                {
                    return false;
                }
            }
            belowWasReplacement = replacement;
            if (first)
            {
                // The count is this card's own lambda, reached through its replacement (this file's prefix runs in it).
                first = false;
                if (_targets[kind] is not { } target || method.Module != target.Module || method.MetadataToken != target.MetadataToken)
                {
                    return false;
                }
                continue;
            }
            switch (FrameOf(method))
            {
                case FrameChain:
                    if (!inChain)
                    {
                        // Game code above the chain: not a display caller.
                        return false;
                    }
                    break;
                case FrameOther:
                    inChain = false;
                    break;
                default:
                    return false;
            }
        }
#if BENCH
        if (RootCheckOffForTest)
        {
            return !first;
        }
#endif
        return !first && RootAllowed(trace, count);
    }

#if BENCH
    /// <summary><c>bdperf deckcount display root off</c>: every engine entry allowed (the rule before 2026-11-21's follow-up), test only.</summary>
    internal static bool RootCheckOffForTest { get; set; }
#endif

    /// <summary>
    /// How the engine entered managed code (the stack's top frames): a <c>Callable</c> queued through the managed
    /// <c>Callable.CallDeferred</c> (<see cref="DeferredPrefix"/> noted its delegate's method), or a node's per-frame virtual
    /// (<c>_Process</c>, <c>_PhysicsProcess</c>, <c>_Draw</c>) called by the engine through <c>InvokeGodotClassMethod</c>. A Godot
    /// signal reaches a delegate the same way a deferred call does (<c>DelegateUtils.InvokeWithVariantArgs</c>, then the
    /// <c>Callable</c>'s trampoline): a delegate never queued deferred is a signal handler, a tween callback or a <c>Callable.Call</c>
    /// (refused); a <c>SignalAwaiter</c>'s continuation enters through <c>SignalAwaiter.SignalCallback</c> (refused), as does
    /// anything else the engine calls (a method by name, input events, <c>_Ready</c>).
    /// </summary>
    private static bool RootAllowed(System.Diagnostics.StackTrace trace, int count)
    {
        MethodBase? top = count > 0 ? trace.GetFrame(count - 1)?.GetMethod() : null;
        if (top == null)
        {
            return false;
        }
        if (top == _invokeWithVariantArgs)
        {
            // The Callable's trampoline (Callable.From's local functions), then the delegate's method.
            if (count < 3 || trace.GetFrame(count - 2)?.GetMethod() is not { } trampoline || trampoline.DeclaringType != typeof(Godot.Callable)
                || !trampoline.Name.Contains("Trampoline", StringComparison.Ordinal) || trace.GetFrame(count - 3) is not { } frame
                || frame.GetMethod() is not { } target)
            {
                return false;
            }
            if (OriginalOf(frame, target) is { } original)
            {
                target = original;
            }
            lock (_deferred)
            {
                return _deferred.Contains((target.Module, target.MetadataToken));
            }
        }
        if (top == _instanceCall)
        {
            // The class's InvokeGodotClassMethod (a generated override calls base's for what it doesn't declare), then the method.
            int i = count - 2;
            while (i >= 0 && trace.GetFrame(i)?.GetMethod() is { Name: "InvokeGodotClassMethod" })
            {
                i--;
            }
            if (i == count - 2 || i < 0 || trace.GetFrame(i) is not { } frame || frame.GetMethod() is not { } method)
            {
                return false;
            }
            if (OriginalOf(frame, method) is { } original)
            {
                method = original;
            }
            return method.Name is "_Process" or "_PhysicsProcess" or "_Draw";
        }
        return false;
    }

    /// <summary>
    /// GodotSharp (the same file on v0.111.1 and v0.113.1): <c>DelegateUtils.InvokeWithVariantArgs</c> (the engine calling a
    /// delegate <c>Callable</c>: a deferred call, a signal, a tween), <c>CSharpInstanceBridge.Call</c> (the engine calling a script
    /// method by name), <c>Callable.CallDeferred</c> (queues the Callable). Another body: the part is not applied.
    /// </summary>
    private const string InvokeWithVariantArgsFingerprint = "52CAE9BE7ACAC23B";
    private const string InstanceCallFingerprint = "10D9A02EDBA5E858";
    private const string CallDeferredFingerprint = "436A1C660CF10DED";

    /// <summary>The delegates' methods queued through the managed <c>Callable.CallDeferred</c> (by module and token; any thread).</summary>
    private static readonly HashSet<(Module, int)> _deferred = new();
    private static MethodBase? _invokeWithVariantArgs, _instanceCall;

    /// <summary>
    /// <c>Callable.CallDeferred</c>: notes the delegate's methods (all of a multicast's), so the call the engine makes at the end of
    /// the frame can be told from a signal's. Passive: nothing of the call changes. Emptied when large (a refresh queues again).
    /// </summary>
    private static void DeferredPrefix(ref Godot.Callable __instance)
    {
        try
        {
            if (__instance.Delegate is not { } queued)
            {
                return;
            }
            lock (_deferred)
            {
                if (_deferred.Count > 4096)
                {
                    _deferred.Clear();
                }
                if (queued.HasSingleTarget)
                {
                    _deferred.Add((queued.Method.Module, queued.Method.MetadataToken));
                }
                else
                {
                    foreach (Delegate one in queued.GetInvocationList())
                    {
                        _deferred.Add((one.Method.Module, one.Method.MetadataToken));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Never in the way of the call: an unnoted delegate is only refused as a display caller.
        }
    }

    private static MethodBase? OriginalOf(System.Diagnostics.StackFrame frame, MethodBase method)
    {
        if (_originals.TryGetValue(method, out MethodBase? known))
        {
            return known;
        }
        MethodBase? original = Harmony.GetOriginalMethodFromStackframe(frame);
        if (original == method || original?.DeclaringType == null)
        {
            original = null;
        }
        if (_originals.Count > 4096)
        {
            _originals.Clear();
        }
        _originals[method] = original;
        return original;
    }

    /// <summary>A frame's kind: the game's preview chain, allowed code (other mods, the runtime, Godot), or refused.</summary>
    private static byte FrameOf(MethodBase method)
    {
        if (_frames.TryGetValue(method, out byte known))
        {
            return known;
        }
        Type type = method.DeclaringType!;
        byte kind;
        if (typeof(IAsyncStateMachine).IsAssignableFrom(type) || IsContinuationMachinery(type))
        {
            // An async flow (the game's actions are async) or a continuation run by the runtime: it may come from anywhere.
            kind = FrameRefused;
        }
        else if (type.Assembly == _sts2)
        {
            kind = _chain.Contains((method.Module, method.MetadataToken)) ? FrameChain : FrameRefused;
        }
        else if (type.Assembly == typeof(DeckCountMemo).Assembly)
        {
#if BENCH
            // The test tooling's synthetic display callers stand for another mod's code.
            kind = type.Namespace?.StartsWith(ModEntry.Id + ".Bench", StringComparison.Ordinal) == true ? FrameOther : FrameRefused;
#else
            kind = FrameRefused;
#endif
        }
        else
        {
            kind = FrameOther;
        }
        if (_frames.Count > 4096)
        {
            _frames.Clear();
        }
        _frames[method] = kind;
        return kind;
    }

    /// <summary>
    /// The runtime's task and continuation machinery: tasks and their continuations (<c>System.Threading.Tasks</c>), the async
    /// builders and awaiters, execution contexts, thread-pool work items and timers, and any synchronization context (Godot's
    /// runs posted continuations). A frame of it means the code above was resumed, not called by whoever is below.
    /// </summary>
    private static bool IsContinuationMachinery(Type type)
    {
        if (typeof(System.Threading.SynchronizationContext).IsAssignableFrom(type))
        {
            return true;
        }
        string name = type.FullName ?? "";
        return type.Namespace is { } ns && (ns == "System.Threading.Tasks" || ns.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal))
            || name.StartsWith("System.Runtime.CompilerServices.Async", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.CompilerServices.ValueTaskAwaiter", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.CompilerServices.YieldAwaitable", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.ExecutionContext", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.ThreadPool", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.QueueUserWorkItemCallback", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.TimerQueue", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.PortableThreadPool", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.SynchronizationContext", StringComparison.Ordinal);
    }

    // --- Which card types may answer differently over time ---

    /// <summary>Per card type, <see cref="TagsMove"/> and <see cref="StarMove"/>. Main thread only (<see cref="FastListeners.PilesKnown"/>).</summary>
    private static readonly Dictionary<Type, byte> _flags = new();

    /// <summary>Each type's getter whose patches <see cref="FlagsOf"/> read, and what it found (main thread).</summary>
    private static readonly Dictionary<MethodInfo, bool> _watched = new();
    /// <summary>Bumped when <see cref="_flags"/> are read again: counts taken with the old flags walk the piles again.</summary>
    private static long _flagsEpoch;
    private static long _nextFlagsCheck;
    /// <summary>As often as <see cref="PatchSafety"/> sees a patch come or go on a method.</summary>
    private const long FlagsCheckMs = 2000;

    /// <summary>Whether another mod patches the getter, remembered so a later change reads every type's flags again.</summary>
    private static bool Watched(MethodInfo getter)
    {
        // No tolerance: per-type getters the memo reads instead of calling.
        bool patched = PatchSafety.IsPatchedByOthers(getter);
        _watched[getter] = patched;
        return patched;
    }

    /// <summary>
    /// A type's flags hold its getters' patches as they were when first read: every <see cref="FlagsCheckMs"/>, those getters are
    /// asked again, and any change reads every type's flags again (a patch on one card type's override, which the base
    /// getters <see cref="SkippedPatched"/> checks don't show).
    /// </summary>
    private static void RecheckFlags()
    {
        long now = Environment.TickCount64;
        if (now < _nextFlagsCheck || _watched.Count == 0)
        {
            return;
        }
        _nextFlagsCheck = now + FlagsCheckMs;
        bool changed = false;
        foreach (KeyValuePair<MethodInfo, bool> watched in _watched)
        {
            // No tolerance: as Watched.
            if (PatchSafety.IsPatchedByOthers(watched.Key) != watched.Value)
            {
                changed = true;
                break;
            }
        }
        if (changed)
        {
            _flags.Clear();
            _watched.Clear();
            _flagsEpoch++;
            Log.Info($"[{ModEntry.Id}] {Name}: another mod's patch on a card's tags or star cost came or went: card types read again");
        }
    }

    /// <summary>
    /// Read from the type's code once, whichever assembly it comes from (a content mod loaded later included: its cards are
    /// seen when they reach a pile or ask). Tags fixed: the type keeps <c>CardModel.Tags</c> (known code: kept from the first
    /// read of <c>CanonicalTags</c>) and its <c>CanonicalTags</c> returns a constant, or its own <c>Tags</c> returns a constant.
    /// Star cost fixed: its <c>CanonicalStarCost</c> and <c>HasStarCostX</c> return constants. A getter another mod patches
    /// counts as changing. Anything that can't be read: changing.
    /// </summary>
    private static byte FlagsOf(Type type)
    {
        if (_flags.TryGetValue(type, out byte flags))
        {
            return flags;
        }
        try
        {
            MethodInfo? tags = Implementation(type, _tagsGetter);
            if (tags == null || Watched(tags))
            {
                flags |= TagsMove;
            }
            else if (tags.DeclaringType == typeof(CardModel))
            {
                if (!_baseTagsKnown || Implementation(type, _canonicalTagsGetter) is not { } canonical || !ReturnsConstant(canonical)
                    || Watched(canonical))
                {
                    flags |= TagsMove;
                }
            }
            else if (!ReturnsConstant(tags))
            {
                flags |= TagsMove;
            }
            foreach (MethodInfo? star in new[] { _starCostGetter, _starXGetter })
            {
                if (Implementation(type, star) is not { } getter || !ReturnsConstant(getter) || Watched(getter))
                {
                    flags |= StarMove;
                }
            }
        }
        catch (Exception e)
        {
            flags = TagsMove | StarMove;
            Log.Info($"[{ModEntry.Id}] {Name}: {type.Name}'s tags and star cost not read, counted by the game: {e.Message}");
        }
        _flags[type] = flags;
        return flags;
    }

    /// <summary>The getter a virtual call on a card of this type runs: its most derived override (a <c>new</c> one hides nothing from it).</summary>
    private static MethodInfo? Implementation(Type type, MethodInfo? baseGetter)
    {
        if (baseGetter == null)
        {
            return null;
        }
        MethodInfo definition = baseGetter.GetBaseDefinition();
        for (Type? t = type; t != null; t = t.BaseType)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name == baseGetter.Name && m.GetParameters().Length == 0)
                {
                    MethodInfo root = m.GetBaseDefinition();
                    if (root.MetadataToken == definition.MetadataToken && root.Module == definition.Module)
                    {
                        return m;
                    }
                }
            }
            if (t == baseGetter.DeclaringType)
            {
                break;
            }
        }
        return null;
    }

    private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>
    /// Whether the getter gives the same value whenever it's called: it reads neither the card (<c>this</c>, its only
    /// argument) nor any field, and calls only constructors and methods of the collections the game builds such values with
    /// (<c>new HashSet&lt;CardTag&gt; { CardTag.Strike }</c>, the compiler's read-only collections, array initialization). A
    /// lambda (<c>ldftn</c>) or any other call, field or argument: not constant.
    /// </summary>
    private static bool ReturnsConstant(MethodInfo getter)
    {
        MethodBody? body = getter.GetMethodBody();
        if (body == null)
        {
            return false;
        }
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Type[]? typeArgs = getter.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        int pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                if (pos >= il.Length)
                {
                    return false;
                }
                value = (short)(0xFE00 | il[pos++]);
            }
            if (!_opCodes.TryGetValue(value, out OpCode op))
            {
                return false;
            }
            if (op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_S || op == OpCodes.Ldarg || op == OpCodes.Ldarga_S || op == OpCodes.Ldarga
                || op == OpCodes.Starg_S || op == OpCodes.Starg || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn || op == OpCodes.Calli)
            {
                return false;
            }
            switch (op.OperandType)
            {
                case OperandType.InlineField:
                    return false;
                case OperandType.InlineMethod:
                    if (getter.Module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, null) is not { } called || !BuildsConstants(called.DeclaringType))
                    {
                        return false;
                    }
                    pos += 4;
                    break;
                case OperandType.InlineTok:
                    // ldtoken: a type, or an array initializer's data (a field of <PrivateImplementationDetails>).
                    if (getter.Module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, null) is FieldInfo data
                        && data.DeclaringType?.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal) != true)
                    {
                        return false;
                    }
                    pos += 4;
                    break;
                case OperandType.InlineSwitch:
                    pos += 4 + 4 * BitConverter.ToInt32(il, pos);
                    break;
                default:
                    pos += op.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        _ => 4,
                    };
                    break;
            }
        }
        return true;
    }

    /// <summary>The types a constant getter may call into: collections it fills with literals, nothing reading other state.</summary>
    private static bool BuildsConstants(Type? type)
    {
        if (type == null)
        {
            return false;
        }
        return type.Namespace is "System.Collections.Generic" or "System.Collections.ObjectModel"
            || type.Name.StartsWith("<>z__", StringComparison.Ordinal)
            || type == typeof(RuntimeHelpers) || type == typeof(Array);
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, the game counts its cards: {e}");
    }
}
