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
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <c>CardModel.Pile</c> is <c>_owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))</c>: a scan of every card the
/// owner has, on each call. <c>CardModel.CombatState</c> goes through it, and that is read everywhere (keywords, costs,
/// CanPlay, hooks...). Loops over the draw pile become O(n²): the first turn's Innate check alone froze the game 6 s
/// with 8000 cards, and every frame spent milliseconds there.
///
/// <c>CardPile._cards</c> only changes through CardPile's own methods, so we track in which piles each card is:
/// <c>AddInternal</c> / <c>RemoveInternal</c> (also used by <c>Clear</c>) change membership, the Move/Randomize
/// methods only reorder. When a card is in exactly one pile, the answer is that pile if it is one of the owner's
/// <c>Piles</c> (else null, like vanilla); in no pile, null. Anything unusual (several piles, a failed clear, a
/// shuffle hook that changed the list) falls back to the vanilla scan. One fast answer in <see cref="VerifyEvery"/> is
/// compared with the vanilla scan; a difference disables the patch for the session.
///
/// When the code of one of those CardPile methods changed, the index holds as long as they still do what it counts on, so
/// instead of leaving (a freeze of seconds on the first turn) it checks that after every call, at no cost: an add adds one
/// card, a remove removes one, a move keeps the count, a clear goes through the removes. Not doing so disables the patch
/// for the session; a change that keeps the counts but not the membership is left to the sampled comparison.
///
/// Two more scans of every card, once per card added (cloning thousands of cards at a campfire: O(n²)):
/// <c>AddInternal</c>'s duplicate check <c>Cards.Contains(card)</c> answers false at once when the index says the card
/// was in no pile, and <c>RunState.ContainsCard</c> (<c>_allCards.Contains</c>) searches from the end, where a card
/// just created is (same comparer, so the same answer as from the start).
///
/// Inside <c>ListenerCache.RunVanilla</c> (the in-session vanilla references: snapshot verifier, bench) every answer is
/// the game's; the index keeps being updated there.
/// </summary>
public static class PileIndex
{
    public const string Name = "PileIndex";

    /// <summary>v0.107.1 code of every CardPile method that touches the list: another one is checked as it runs (<see cref="_selfCheck"/>).</summary>
    private static readonly (string Method, string Fingerprint)[] Fingerprints =
    {
        ("AddInternal", "0A80C8EF3B697A4E"),
        ("RemoveInternal", "7CF07A4BBD094E41"),
        ("MoveToBottomInternal", "8A6B91EFCD757B3B"),
        ("MoveToTopInternal", "0C41AC449A8E2DBB"),
        ("RandomizeOrderInternal", "FF906423481AC743"),
        ("Clear", "A2B6A279CA18ABC1"),
    };
    /// <summary>The mirrored getter.</summary>
    private const string GetterFingerprint = "6A1FE5E50F9ECAC2";
    /// <summary>v0.107.1 <c>RunState.ContainsCard</c>, mirrored by <see cref="ContainsCardPrefix"/>.</summary>
    private const string ContainsCardFingerprint = "DC5BAC12C08BE24D";

    /// <summary>One fast answer in this many is checked against the vanilla scan (1 = all, for tests).</summary>
    internal static int VerifyEvery { get; set; } = 1024;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Fast { get; private set; }
    public static long Fallbacks { get; private set; }
    public static long Verified { get; private set; }

    private sealed class Entry
    {
        /// <summary>Number of piles holding the card.</summary>
        public int Count;
        /// <summary>The pile when <see cref="Count"/> is 1 and known.</summary>
        public CardPile? Pile;
        /// <summary>Membership no longer known exactly (removed from one of several piles): vanilla scan from now on.</summary>
        public bool Unknown;
        /// <summary>Bumped by every update: a failed or skipped call undoes its own only when no other one came after it.</summary>
        public int Changes;
    }

    /// <summary>A method of <see cref="Fingerprints"/> changed: each call is checked by its effect.</summary>
    private static bool _selfCheck;
    private static MethodBase?[] _listMethods = Array.Empty<MethodBase?>();
    private static long _othersAt = long.MinValue / 2;
    private static bool _others;

    /// <summary>
    /// Every add, remove and clear is checked by its effect: a list method changed (<see cref="_selfCheck"/>), or another
    /// mod patches one (it could skip the original or change the list another way). Rechecked each second.
    /// </summary>
    private static bool SelfChecking()
    {
        if (_selfCheck)
        {
            return true;
        }
        RefreshOthers();
        return _others;
    }

    /// <summary><c>AddInternal</c> and <c>RemoveInternal</c>, whose patches by another mod <see cref="_moveOthers"/> tells.</summary>
    private static MethodBase? _addMethod, _removeMethod;
    /// <summary>Another mod patches AddInternal or RemoveInternal: its patch code may read card.Pile while the index is already updated.</summary>
    private static bool _moveOthers;

    private static void RefreshOthers()
    {
        long now = Environment.TickCount64;
        if (now - _othersAt < 1000)
        {
            return;
        }
        _othersAt = now;
        bool others = false;
        foreach (MethodBase? method in _listMethods)
        {
            // AddInternal and RemoveInternal: postfixes (arguments by value: the finalizer reads card and pile from those slots)
            // and void prefixes Harmony sorts before every prefix of this mod on the method (FastListeners' and CardDbScan's at
            // Priority.Last+1, this class's BeforeAdd/BeforeRemove at Priority.Last; the Watch* count prefixes are order-free)
            // are let stand. They can't skip the original nor change its arguments, so the index is updated by this class's own
            // prefix and finalizer as without them; a card they move goes through these methods again, counted there. While
            // every call is checked by its effect (SelfChecking: a list method below patched or changed), an add or remove
            // expects the pile to change by its own card only: a card such a patch (or a game event handler) moves into that
            // same pile during the call faults the index, the game's own scan from then on (safe, not exact). One they throw is
            // judged by the pile's list (Judge). Not seen: a patch writing CardPile._cards through __instance.Cards cast to its
            // list, reflection or code outside a patch (a ___cards field parameter is refused; DESIGN). The other list methods
            // (clear, moves, shuffle): no tolerance.
            others |= method == _addMethod || method == _removeMethod
                ? PatchSafety.IsPatchedByOthers(method, PatchTolerance.SurroundingOnly)
                : PatchSafety.IsPatchedByOthers(method);
        }
        _others = others;
        // Any other mod's patch, not recorded as a back-off: the index keeps answering, only a card whose own move is under way
        // gets the game's scan (a prefix after this class's could read card.Pile before the list changes).
        _moveOthers = (_addMethod != null && PatchSafety.HasOtherPatches(_addMethod))
            || (_removeMethod != null && PatchSafety.HasOtherPatches(_removeMethod));
    }

    /// <summary>
    /// Cards whose add or remove is under way on this thread while <see cref="_moveOthers"/>: the index is updated before the
    /// original changes the list, so between the two (another mod's prefix running after ours) it answers ahead of the list.
    /// Their pile is the game's scan until the call ends.
    /// </summary>
    [ThreadStatic] private static List<CardModel>? _inFlight;

    private static void Land(CardModel card)
    {
        List<CardModel>? flying = _inFlight;
        int at = flying == null ? -1 : flying.LastIndexOf(card);
        if (at >= 0)
        {
            flying!.RemoveAt(at);
        }
    }

    private static readonly ConditionalWeakTable<CardModel, Entry> _entries = new();
    private static AccessTools.FieldRef<CardModel, Player?>? _owner;
    /// <summary><c>Player._runPiles</c>, null when <see cref="Player.Piles"/> isn't the code <see cref="OwnsPile"/> mirrors.</summary>
    private static AccessTools.FieldRef<Player, CardPile[]?>? _runPiles;
    /// <summary>v0.107.1 and v0.111 <c>Player.Piles</c>: <c>(PlayerCombatState?.AllPiles ?? []).Concat(_runPiles)</c>, _runPiles = [Deck] made on first call.</summary>
    private const string PlayerPilesFingerprint = "1C4E1C85C62DCE1D";
    private static System.Reflection.MethodInfo? _getter;
    /// <summary>Set by the verification and the bench to get the vanilla answer.</summary>
    [ThreadStatic] private static bool _vanilla;

    private static MethodInfo? _playerPiles;
    /// <summary><c>CardPile.Cards</c>: the game's scan and duplicate check read the list through it.</summary>
    private static MethodInfo? _cardsGetter;

    public static void Apply(Harmony harmony)
    {
        // The index starts empty: cards already in piles (a run that exists before this applies) would answer no pile.
        if (MegaCrit.Sts2.Core.Runs.RunManager.Instance?.IsInProgress == true)
        {
            throw new PatchSkippedException("a run already exists: its cards aren't indexed");
        }
        _addMethod = Reflect.Method(typeof(CardPile), nameof(CardPile.AddInternal));
        _removeMethod = Reflect.Method(typeof(CardPile), nameof(CardPile.RemoveInternal));
        _getter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)) ?? throw new MissingMethodException("CardModel.Pile");
        _playerPiles = Reflect.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "Piles") ?? throw new MissingMethodException("Player.Piles");
        _cardsGetter = Reflect.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards)) ?? throw new MissingMethodException("CardPile.Cards");
        PatchSafety.RequireFingerprint(_getter, GetterFingerprint);
        foreach ((string method, string fingerprint) in Fingerprints)
        {
            string actual = MethodFingerprint.Of(Reflect.Method(typeof(CardPile), method) ?? throw new MissingMethodException("CardPile." + method));
            if (!PatchSafety.FingerprintMatches(actual, fingerprint))
            {
                _selfCheck = true;
                Log.Info($"[{ModEntry.Id}] {Name}: CardPile.{method} changed (fingerprint {actual}, expected {fingerprint}): every call checked by its effect");
            }
        }
        // No other code changes the pile lists.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        _owner = Reflect.FieldRefAccess<CardModel, Player?>("_owner");
        try
        {
            // Player.Piles builds a Concat iterator on every call: read the same piles without it while its code is known.
            PatchSafety.RequireFingerprint(Reflect.PropertyGetter(typeof(Player), nameof(Player.Piles))
                ?? throw new MissingMethodException("Player.Piles"), PlayerPilesFingerprint);
            _runPiles = Reflect.FieldRefAccess<Player, CardPile[]?>("_runPiles");
        }
        catch (Exception e)
        {
            _runPiles = null;
            Log.Info($"[{ModEntry.Id}] {Name}: owner's piles read through Player.Piles ({e.Message})");
        }

        // Tracking first, so the getter never answers from an index that missed changes.
        // Updated before the original runs: it fires CardAdded/ContentsChanged (whose listeners read card.Pile) right
        // after changing the list. Last prefix, so the original runs unless a prefix after it skips it. After a throw or a skipped
        // original the finalizer judges from the pile's list (Judge): kept when the card really entered (left), undone when it
        // didn't, a fault when the card was moved again meanwhile.
        // Harmony doesn't call a prefix taking the card once an earlier prefix returned false, so the pile's count is also
        // taken by a first prefix that always runs (only __instance and __state): an add or remove another mod's earlier
        // prefix skipped is judged from it (same __state, shared by this class's patches on a method).
        PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), nameof(CardPile.AddInternal)),
            prefix: new HarmonyMethod(typeof(PileIndex), nameof(WatchAdd)) { priority = Priority.First });
        PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), nameof(CardPile.AddInternal)),
            prefix: new HarmonyMethod(typeof(PileIndex), nameof(BeforeAdd)) { priority = Priority.Last },
            finalizer: Method(nameof(AfterAdd)));
        PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)),
            prefix: new HarmonyMethod(typeof(PileIndex), nameof(WatchRemove)) { priority = Priority.First });
        PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)),
            prefix: new HarmonyMethod(typeof(PileIndex), nameof(BeforeRemove)) { priority = Priority.Last },
            finalizer: Method(nameof(AfterRemove)));
        PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), nameof(CardPile.Clear)), prefix: Method(nameof(BeforeClear)), finalizer: Method(nameof(ClearFinalizer)));
        // Always: another mod patching a reorder (or a changed one) must not change the count unseen.
        foreach (string reorder in new[] { nameof(CardPile.MoveToBottomInternal), nameof(CardPile.MoveToTopInternal) })
        {
            PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), reorder), prefix: Method(nameof(BeforeReorder)), finalizer: Method(nameof(AfterReorder)));
        }
        _listMethods = Fingerprints.Select(f => (MethodBase?)Reflect.Method(typeof(CardPile), f.Method)).ToArray();
        PatchGuard.Patch(harmony, Reflect.Method(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)),
            prefix: Method(nameof(BeforeRandomize)), finalizer: Method(nameof(AfterRandomize)));
        PatchGuard.Patch(harmony, _getter, prefix: Method(nameof(PilePrefix)));
        Applied = true;
        // What RefreshOthers and GetterPatched ask about (display only).
        PatchSafety.Watch(Name, _listMethods.Where(m => m != _addMethod && m != _removeMethod));
        PatchSafety.Watch(Name, new[] { _addMethod, _removeMethod }, PatchTolerance.SurroundingOnly);
        PatchSafety.Watch(Name, _getter, PatchTolerance.BeforeReplace);
        PatchSafety.Watch(Name, PatchSafety.ShapeRules ? new[] { _cardsGetter } : new[] { _cardsGetter, _playerPiles });
        try
        {
            MethodInfo addInternal = Reflect.Method(typeof(CardPile), nameof(CardPile.AddInternal))!;
            PatchGuard.Patch(harmony, addInternal, transpiler: Method(nameof(AddInternalTranspiler)));
            Log.Info($"[{ModEntry.Id}] {Name}: AddInternal duplicate check {(_duplicateCheckPatched ? "patched" : "left vanilla (call not found)")}");
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: AddInternal duplicate check left vanilla: {e.Message}");
        }
        try
        {
            _containsCard = Reflect.Method(typeof(RunState), nameof(RunState.ContainsCard)) ?? throw new MissingMethodException("RunState.ContainsCard");
            PatchSafety.RequireFingerprint(_containsCard, ContainsCardFingerprint);
            _allCards = Reflect.FieldRefAccess<RunState, List<CardModel>>("_allCards");
            PatchGuard.Patch(harmony, _containsCard, prefix: Method(nameof(ContainsCardPrefix)));
            PatchSafety.Watch(Name, _containsCard); // What ContainsCardPrefix asks about (display only).
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: RunState.ContainsCard left vanilla: {e.Message}");
        }
    }

    /// <summary>The card is in no pile (known exactly), or false when unsure.</summary>
    internal static bool InNoPile(CardModel card)
    {
        if (!Enabled || !Applied || ListenerCache.IsSuspended)
        {
            return false;
        }
        // Mid-move while another mod patches it: the index already says no pile, the list may still hold it (as PilePrefix).
        if (_inFlight is { Count: > 0 } flying && flying.Contains(card))
        {
            return false;
        }
        return !_entries.TryGetValue(card, out Entry? entry) || (entry.Count == 0 && !entry.Unknown);
    }

    /// <summary>How many of these piles hold the card (0 or 1), or -1 when unsure.</summary>
    internal static int PlacesIn(CardModel card, IReadOnlyList<CardPile> piles)
    {
        if (!Enabled || !Applied || ListenerCache.IsSuspended || SelfChecking())
        {
            return -1;
        }
        if (!_entries.TryGetValue(card, out Entry? entry))
        {
            return 0;
        }
        if (entry.Unknown || entry.Count > 1)
        {
            return -1;
        }
        if (entry.Count == 0)
        {
            return 0;
        }
        if (entry.Pile == null)
        {
            return -1;
        }
        for (int i = 0; i < piles.Count; i++)
        {
            if (ReferenceEquals(piles[i], entry.Pile))
            {
                return 1;
            }
        }
        return 0;
    }

    private static bool _duplicateCheckPatched;
    private static System.Reflection.MethodInfo? _containsCard;
    private static AccessTools.FieldRef<RunState, List<CardModel>>? _allCards;
    public static long FastDuplicateChecks { get; private set; }

    /// <summary>AddInternal's <c>Cards.Contains(card)</c> (Enumerable.Contains on the pile's list) goes through <see cref="ContainsBeingAdded"/>.</summary>
    private static IEnumerable<CodeInstruction> AddInternalTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo contains = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(CardModel));
        bool replaced = false; // Harmony runs transpilers again each time the method is re-patched.
        foreach (CodeInstruction instruction in instructions)
        {
            if (!replaced && instruction.Calls(contains))
            {
                replaced = true;
                _duplicateCheckPatched = true;
                yield return new CodeInstruction(OpCodes.Call, Reflect.Method(typeof(PileIndex), nameof(ContainsBeingAdded)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    /// <summary>
    /// Inside AddInternal, after <see cref="BeforeAdd"/> counted this add: a count of 1 means the card was in no pile
    /// before, so it isn't in this one. Anything else: the game's scan.
    /// </summary>
    private static bool ContainsBeingAdded(IEnumerable<CardModel> cards, CardModel card)
    {
        // The game's check reads the list through CardPile.Cards: another mod's patch there gets the game's check.
        if (Enabled && !ListenerCache.IsSuspended && !GetterPatched() && _entries.TryGetValue(card, out Entry? entry) && entry.Count == 1 && !entry.Unknown)
        {
            FastDuplicateChecks++;
            if (FastDuplicateChecks % VerifyEvery == 0 && cards.Contains(card))
            {
                Fail($"{card} is already in the pile it is added to, the index said no pile");
                return true;
            }
            return false;
        }
        SlowDuplicateChecks++;
#if BENCH
        if (SlowDuplicateChecks <= 5)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: slow duplicate check for {card}: entry {(_entries.TryGetValue(card, out Entry? e) ? $"count {e.Count}, unknown {e.Unknown}, pile {e.Pile?.Type}" : "none")}, enabled {Enabled}");
        }
#endif
        return cards.Contains(card);
    }

    public static long SlowDuplicateChecks { get; private set; }

    /// <summary>
    /// The <c>RunState.ContainsCard</c> shortcut doesn't read the index, so a stale index (<see cref="Fail"/>) needn't stop it:
    /// set with the pile_lookup switch (master switch and setting, not faults) by <c>Settings</c>. False until then, so the
    /// shortcut follows <see cref="Enabled"/> alone.
    /// </summary>
    public static bool ContainsCardEnabled { get; set; }
#if BENCH
    /// <summary>ContainsCard calls answered here (the effects check).</summary>
    public static long FastContainsCards { get; private set; }
#endif

    /// <summary><c>_allCards.Contains(card)</c> from the end: the same answer, found at once for cards just created.</summary>
    private static bool ContainsCardPrefix(RunState __instance, CardModel card, ref bool __result)
    {
        // No tolerance: this prefix replaces the method (a postfix would see its result, but a void prefix runs after it).
        if (!(Enabled || ContainsCardEnabled) || ListenerCache.IsSuspended || PatchSafety.IsPatchedByOthers(_containsCard))
        {
            return true;
        }
        __result = _allCards!(__instance).LastIndexOf(card) >= 0;
#if BENCH
        FastContainsCards++;
#endif
        return false;
    }

    private static HarmonyMethod Method(string name) => new(typeof(PileIndex), name);

    /// <summary>
    /// The entry as it was before an add/remove (restored if the call turns out not to have done it), whether the card was in
    /// the pile then, and the pile's card count before the call (<see cref="Known"/>: taken by <see cref="WatchAdd"/>/
    /// <see cref="WatchRemove"/>, which always run).
    /// </summary>
    private readonly struct Saved(Entry? entry, int count, CardPile? pile, bool unknown, int changes, bool wasIn, int pileCount, bool known,
        int setChanges = -1, bool inFlight = false)
    {
        /// <summary>The card was put in <see cref="_inFlight"/> by this call: the finalizer takes it out.</summary>
        public bool InFlight => inFlight;

        public Saved Flying() => new(entry, count, pile, unknown, changes, wasIn, pileCount, known, setChanges, true);

        /// <summary>The pile's card count before the call.</summary>
        public int PileCount => pileCount;
        /// <summary>This call was counted in the index (<see cref="BeforeAdd"/>/<see cref="BeforeRemove"/> ran).</summary>
        public bool Set => entry != null;
        public bool Known => known;

        /// <summary>Whether the card was in the pile before the call.</summary>
        public bool WasIn => wasIn;

        /// <summary>No other update of the card's entry since this call's own (a handler moving the card before throwing).</summary>
        public bool Untouched => entry != null && entry.Changes == setChanges;

        public void Restore()
        {
            if (entry != null)
            {
                entry.Count = count;
                entry.Pile = pile;
                entry.Unknown = unknown;
                entry.Changes = changes;
            }
        }

        /// <summary>This, once the call's own update is made: what <see cref="Untouched"/> compares with.</summary>
        public Saved Updated() => new(entry, count, pile, unknown, changes, wasIn, pileCount, known, entry?.Changes ?? -1, inFlight);

        /// <summary>
        /// Before the call's update. Whether the card is in the pile comes from the index when it knows the card's one pile or
        /// none, else from the pile's list (only for cards in several piles or of unknown membership: rare).
        /// </summary>
        public static Saved Of(Entry entry, CardPile pile, CardModel card) => new(entry, entry.Count, entry.Pile, entry.Unknown, entry.Changes,
            entry.Unknown || entry.Count > 1 || (entry.Count == 1 && entry.Pile == null) ? Holds(pile, card)
                : entry.Count == 1 && ReferenceEquals(entry.Pile, pile),
            pile.Cards.Count, true);

        public static Saved CountOf(CardPile pile) => new(null, 0, null, false, 0, false, pile.Cards.Count, true);
    }

    /// <summary>
    /// First prefix, always run (it takes neither the card nor a ref): the pile's count before anything else. Order-free for
    /// another mod's void prefix let stand: its count is only read when <see cref="BeforeAdd"/> didn't run (Judge, !Set):
    /// another mod's prefix returned false, or a prefix before BeforeAdd threw (the original didn't run either). A void prefix
    /// let stand sorts between the two: one that moved another card into this pile and then threw faults the index (Judge
    /// can't tell that nested move from an unseen change): conservative, the game's own scan from then on.
    /// </summary>
    [OrderFreePrefix]
    private static void WatchAdd(CardPile __instance, out Saved __state)
    {
        __state = Saved.CountOf(__instance);
#if BENCH
        EnterCall(__instance);
#endif
    }

    [OrderFreePrefix]
    private static void WatchRemove(CardPile __instance, out Saved __state)
    {
        __state = Saved.CountOf(__instance);
#if BENCH
        EnterCall(__instance);
#endif
    }

#if BENCH
    /// <summary>
    /// Test builds: the adds and removes under way on this thread, outermost first, each with its pile's count when it began and
    /// the net change the calls nested in it made to that pile. Tells the self-check's known limit apart from any other count
    /// mismatch (<see cref="NestedMoveFaults"/>).
    /// </summary>
    [ThreadStatic] private static List<(CardPile Pile, int Start, int Nested)>? _calls;

    /// <summary>
    /// Self-check faults of an add or remove whose pile changed by its own card plus exactly what calls nested in it (a tolerated
    /// postfix, a game event handler) moved into or out of that same pile: the self-check's documented limit (it expects the
    /// call's own card only: the index faults, the game's own scan from then on), seen by the <c>pileindex-nested-limit</c> control.
    /// </summary>
    public static long NestedMoveFaults { get; private set; }

    private static void EnterCall(CardPile pile) => (_calls ??= new()).Add((pile, pile.Cards.Count, 0));

    /// <summary>
    /// Leaves this call's entry: its pile's count when it began and the net change calls nested in it made to that pile; its own
    /// net change is passed on to the nearest enclosing call on the same pile. An unbalanced stack (a finalizer that never ran) is
    /// dropped: nothing is told then.
    /// </summary>
    private static (int Start, int Nested) LeaveCall(CardPile pile)
    {
        List<(CardPile Pile, int Start, int Nested)>? calls = _calls;
        if (calls is not { Count: > 0 } || !ReferenceEquals(calls[^1].Pile, pile))
        {
            calls?.Clear();
            return (-1, 0);
        }
        (CardPile _, int start, int nested) = calls[^1];
        calls.RemoveAt(calls.Count - 1);
        int change = pile.Cards.Count - start;
        for (int i = calls.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(calls[i].Pile, pile))
            {
                calls[i] = (pile, calls[i].Start, calls[i].Nested + change);
                break;
            }
        }
        return (start, nested);
    }

    /// <summary>A self-check fault about to be raised: counted and logged when nested calls on the same pile explain the count.</summary>
    private static void NoteNestedLimit(CardPile pile, (int Start, int Nested) call, int own)
    {
        int nested = call.Nested;
        if (nested == 0 || call.Start < 0 || pile.Cards.Count != call.Start + own + nested)
        {
            return;
        }
        NestedMoveFaults++;
        if (NestedMoveFaults <= 3)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: self-check limit: a nested {(nested > 0 ? "add" : "remove")} of {Math.Abs(nested)} card(s) "
                + $"{(nested > 0 ? "into" : "out of")} the {pile.Type} pile during an {(own > 0 ? "add" : "remove")} (the count expected "
                + $"the call's own card only): faulted, the game's scan from then on (NestedMoveFaults {NestedMoveFaults})");
        }
    }
#endif

    /// <summary>Only called while the original is still to run (Harmony skips it after another prefix returned false).</summary>
    private static void BeforeAdd(CardPile __instance, CardModel card, bool __runOriginal, ref Saved __state)
    {
        if (!__runOriginal)
        {
            return;
        }
        try
        {
            Entry entry = _entries.GetOrCreateValue(card);
            __state = Saved.Of(entry, __instance, card);
            entry.Count++;
            entry.Pile = entry.Count == 1 ? __instance : null;
            entry.Changes++;
            __state = __state.Updated();
            if (_clears is { Count: > 0 } clears)
            {
                NoteAddDuringClear(clears, __instance, card);
            }
            TakeOff(card, ref __state);
        }
        catch (Exception e)
        {
            // Runs on every card move: never let it break the game's own add; the index can't be trusted any more.
            Fail("index update failed: " + e);
        }
    }

    /// <summary>
    /// AddInternal throws on duplicates or canonical cards (before the list changes), or from an event handler after the
    /// list changed; another mod's prefix may skip it (and add the card itself, or not): judged by the pile (<see cref="Judge"/>).
    /// </summary>
    private static void AfterAdd(Exception? __exception, CardPile __instance, CardModel card, Saved __state, bool __runOriginal)
    {
        try
        {
#if BENCH
            (int Start, int Nested) call = __state.Known ? LeaveCall(__instance) : (-1, 0);
#endif
            if (__state.InFlight)
            {
                Land(card);
            }
            if (__exception != null || !__runOriginal)
            {
                Judge(__instance, card, __state, add: true, __exception != null);
            }
            else if (SelfChecking() && __state.Set && __instance.Cards.Count != __state.PileCount + 1)
            {
#if BENCH
                NoteNestedLimit(__instance, call, 1);
#endif
                Fail($"an add left the {__instance.Type} pile with {__instance.Cards.Count} cards, it had {__state.PileCount}");
            }
        }
        catch (Exception e)
        {
            Fail("add check failed: " + e);
        }
    }

    /// <summary>RemoveInternal throws on a card not in the pile (before the list changes): see <see cref="AfterAdd"/>.</summary>
    private static void AfterRemove(Exception? __exception, CardPile __instance, CardModel card, Saved __state, bool __runOriginal)
    {
        try
        {
#if BENCH
            (int Start, int Nested) call = __state.Known ? LeaveCall(__instance) : (-1, 0);
#endif
            if (__state.InFlight)
            {
                Land(card);
            }
            if (__exception != null || !__runOriginal)
            {
                Judge(__instance, card, __state, add: false, __exception != null);
            }
            else if (SelfChecking() && __state.Set && __instance.Cards.Count != __state.PileCount - 1)
            {
#if BENCH
                NoteNestedLimit(__instance, call, -1);
#endif
                Fail($"a remove left the {__instance.Type} pile with {__instance.Cards.Count} cards, it had {__state.PileCount}");
            }
        }
        catch (Exception e)
        {
            Fail("remove check failed: " + e);
        }
    }

    /// <summary>
    /// After an add or remove that threw, or whose original another mod's prefix skipped. Counted here: kept when the card
    /// really entered (left) the pile, undone otherwise, decided by the pile's own list and whether the card was in it before
    /// (<see cref="Saved.WasIn"/>), not by the pile's count (a handler may have moved other cards of that pile before throwing).
    /// Another update of that card's entry meanwhile (a handler moving the card, then throwing) can't be undone apart from
    /// this one: the index stops being trusted. Not counted (a prefix before <see cref="BeforeAdd"/> skipped the original or
    /// threw, so Harmony never called it): the pile must be as it was, or have changed by that card through these methods (a
    /// nested call, counted there); anything else changed the pile unseen, or by a nested move of another card this can't tell
    /// apart from it (a fault: kept strict).
    /// </summary>
    private static void Judge(CardPile pile, CardModel card, Saved state, bool add, bool threw)
    {
        if (!state.Known)
        {
            return; // The watch prefix didn't run: the patch isn't fully applied, nothing to judge with.
        }
        int count = pile.Cards.Count;
        int delta = add ? 1 : -1;
        if (state.Set)
        {
            if (!state.Untouched)
            {
                Fail($"a {(threw ? "failed" : "skipped")} {(add ? "add to" : "remove from")} the {pile.Type} pile of {card}: that card was moved again meanwhile");
                return;
            }
            bool inPile = Holds(pile, card);
            bool happened = add ? !state.WasIn && inPile : state.WasIn && !inPile;
            if (!happened)
            {
                state.Restore();
                if (!add)
                {
                    CountRemove(pile, -1); // Not one of the removes a Clear accounts for.
                }
            }
            return;
        }
        if (count == state.PileCount)
        {
            return;
        }
        if (count == state.PileCount + delta && Holds(pile, card) == add && IndexHolds(card, pile) == add)
        {
            return;
        }
        Fail($"the {pile.Type} pile went from {state.PileCount} to {count} cards while "
            + (threw ? "a prefix before this mod's threw in" : "another mod's prefix skipped") + $" CardPile.{(add ? "AddInternal" : "RemoveInternal")}");
    }

    /// <summary>Whether the pile's list holds this very card (by reference, as the game's Contains: CardModel keeps object equality).</summary>
    private static bool Holds(CardPile pile, CardModel card)
    {
        IReadOnlyList<CardModel> cards = pile.Cards;
        for (int i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i], card))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the index has the card in this pile; null when it doesn't know which pile.</summary>
    private static bool? IndexHolds(CardModel card, CardPile pile)
    {
        if (!_entries.TryGetValue(card, out Entry? entry) || entry.Count == 0)
        {
            return false;
        }
        if (entry.Unknown || entry.Count > 1 || entry.Pile == null)
        {
            return null;
        }
        return ReferenceEquals(entry.Pile, pile);
    }

    private static void BeforeReorder(CardPile __instance, out int __state) => __state = __instance.Cards.Count;

    /// <summary>Checked even while switched off: the index keeps being updated then and is trusted again when switched on.</summary>
    private static void AfterReorder(Exception? __exception, CardPile __instance, int __state)
    {
        if (__exception == null && !_failed && __instance.Cards.Count != __state)
        {
            Fail($"a move inside the {__instance.Type} pile changed its card count");
        }
    }

    /// <summary>Only called while the original is still to run, as <see cref="BeforeAdd"/>.</summary>
    private static void BeforeRemove(CardPile __instance, CardModel card, bool __runOriginal, ref Saved __state)
    {
        if (!__runOriginal)
        {
            return;
        }
        try
        {
            Entry entry = _entries.GetOrCreateValue(card);
            __state = Saved.Of(entry, __instance, card);
            CountRemove(__instance, +1);
            entry.Count--;
            if (entry.Count <= 0)
            {
                entry.Count = 0;
                entry.Pile = null;
                entry.Unknown = false;
            }
            else if (entry.Pile != __instance)
            {
                // It was in several piles and is still in at least one: which one isn't tracked.
                entry.Pile = null;
                entry.Unknown = true;
            }
            entry.Changes++;
            __state = __state.Updated();
            TakeOff(card, ref __state);
        }
        catch (Exception e)
        {
            Fail("index update failed: " + e);
        }
    }

    /// <summary>While another mod patches a move, the card's pile is the game's scan until this call ends (<see cref="_inFlight"/>).</summary>
    private static void TakeOff(CardModel card, ref Saved state)
    {
        RefreshOthers();
        if (_moveOthers)
        {
            (_inFlight ??= new List<CardModel>()).Add(card);
            state = state.Flying();
        }
    }

    /// <summary>
    /// A pile being cleared, and the cards added to it meanwhile (by a handler of one of its removes): <c>Clear</c> ends
    /// with <c>_cards.Clear()</c>, which drops whatever is still in the list without <c>RemoveInternal</c>.
    /// </summary>
    private sealed class ClearFrame(CardPile pile)
    {
        public readonly CardPile Pile = pile;
        public List<CardModel>? Added;
        /// <summary>The finalizer already judged this clear: Harmony reruns finalizers in its catch block when a later one throws.</summary>
        public bool Finalized;
        /// <summary>Removes from this pile during this clear (a handler's removes from other piles aren't its own).</summary>
        public int Removes;
    }

    /// <summary>Clears running on this thread, innermost last (a handler may clear another pile during a clear).</summary>
    [ThreadStatic] private static List<ClearFrame>? _clears;

    /// <summary>The innermost clear of this pile running on this thread, if any.</summary>
    private static ClearFrame? FrameOf(List<ClearFrame> clears, CardPile pile)
    {
        for (int i = clears.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(clears[i].Pile, pile))
            {
                return clears[i];
            }
        }
        return null;
    }

    private static void NoteAddDuringClear(List<ClearFrame> clears, CardPile pile, CardModel card)
    {
        if (FrameOf(clears, pile) is { } frame)
        {
            (frame.Added ??= new List<CardModel>()).Add(card);
        }
    }

    /// <summary>A remove counted (+1) or undone (-1) for the clear of its pile under way, if any (frames are balanced within a call).</summary>
    private static void CountRemove(CardPile pile, int delta)
    {
        if (_clears is { Count: > 0 } clears && FrameOf(clears, pile) is { } frame)
        {
            frame.Removes += delta;
        }
    }

    private readonly record struct ClearState(int Cards, ClearFrame? Frame);

    private static void BeforeClear(CardPile __instance, out ClearState __state)
    {
        var frame = new ClearFrame(__instance);
        (_clears ??= new List<ClearFrame>()).Add(frame);
        __state = new ClearState(__instance.Cards.Count, frame);
    }

    /// <summary>
    /// A clear that throws stops before <c>_cards.Clear()</c> (each remove already judged on its own), but is still taken as
    /// a reason to stop trusting the index. A card added to the pile during the clear and dropped by <c>_cards.Clear()</c>
    /// leaves the index as a remove would. Checked even while switched off.
    ///
    /// Harmony runs every finalizer again, in its catch block, when one of them throws after a normal return: that second
    /// run gets the other finalizer's exception, not the clear's, and the clear was already judged by the first one. Skipped
    /// (<see cref="ClearFrame.Finalized"/>). A throwing finalizer running before this one still faults it (conservative).
    /// </summary>
    private static void ClearFinalizer(Exception? __exception, CardPile __instance, ClearState __state, bool __runOriginal)
    {
        try
        {
            if (__state.Frame is { } frame)
            {
                if (frame.Finalized)
                {
                    return;
                }
                frame.Finalized = true;
                if (_clears is { } clears)
                {
                    int at = clears.LastIndexOf(frame);
                    if (at >= 0)
                    {
                        clears.RemoveAt(at);
                    }
                }
                if (frame.Added != null)
                {
                    Dropped(__instance, frame.Added);
                }
            }
            if (_failed)
            {
                return;
            }
            if (__exception != null)
            {
                Fail("CardPile.Clear or a patch on it threw, the index may be stale: " + __exception.Message);
            }
            // Removes of cards added during the clear make the count differ: those were judged one by one above. The game's
            // clear takes every card through a remove; one another mod's prefix skipped must leave the cards no remove took.
            else if (SelfChecking() && __state.Frame is { Added: null } own
                && own.Removes != (__runOriginal ? __state.Cards : __state.Cards - __instance.Cards.Count))
            {
                Fail($"a {(__runOriginal ? "" : "skipped ")}clear of {__state.Cards} cards made {own.Removes} removes, {__instance.Cards.Count} cards left");
            }
        }
        catch (Exception e)
        {
            Fail("clear check failed: " + e);
        }
    }

    /// <summary>Cards added during a clear: one no longer in the pile but still in it for the index was dropped unseen.</summary>
    private static void Dropped(CardPile pile, List<CardModel> added)
    {
        foreach (CardModel card in added)
        {
            if (Holds(pile, card))
            {
                continue; // The clear threw before emptying the list: still there, as the index says.
            }
            bool? holds = IndexHolds(card, pile);
            if (holds == false)
            {
                continue; // Removed again through RemoveInternal (or moved on): counted.
            }
            if (holds == true && _entries.TryGetValue(card, out Entry? entry))
            {
                entry.Count = 0;
                entry.Pile = null;
                entry.Unknown = false;
                entry.Changes++;
                continue;
            }
            Fail($"{card}, added to the {pile.Type} pile during its clear, was dropped by the clear and its pile isn't known");
            return;
        }
    }

    /// <summary>Shuffle hooks (<c>ModifyShuffleOrder</c>) receive the pile's own list and could add or remove cards.</summary>
    private static void BeforeRandomize(CardPile __instance, out int __state)
    {
        __state = __instance.Cards.Count;
    }

    /// <summary>A finalizer, so a hook that changed the list then threw is seen too. Checked even while switched off.</summary>
    private static void AfterRandomize(Exception? __exception, CardPile __instance, int __state)
    {
        if (_failed)
        {
            return;
        }
        try
        {
            if (__instance.Cards.Count != __state)
            {
                Fail("a shuffle hook changed the pile's card count");
                return;
            }
            foreach (CardModel card in __instance.Cards)
            {
                if (!_entries.TryGetValue(card, out Entry? entry) || (entry.Pile != __instance && !entry.Unknown && entry.Count < 2))
                {
                    Fail($"a shuffle hook put {card} in the {__instance.Type} pile");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Fail("shuffle check failed: " + e);
        }
    }

    private static int _getterCheckCalls;
    private static bool _getterPatched;
    /// <summary>Another mod patches Player.Piles: <see cref="OwnsPile"/> reads the owner's piles through it.</summary>
    private static bool _pilesReadThrough;
    /// <summary>Another mod's patches on CardModel.Pile, let stand: the sampled check compares with the original's scan (<see cref="OriginalScan"/>).</summary>
    private static bool _getterOthers;
    private static int _getterEpoch;

    /// <summary>Another mod's patch on the getter this replaces. Read millions of times a turn: rechecked every 1,024 calls, no clock read per call.</summary>
    private static bool GetterPatched()
    {
        // Also at once after any Patch or Unpatch (HarmonyWatch.Epoch: one int read), not up to 1,024 calls later.
        if (--_getterCheckCalls < 0 || _getterEpoch != HarmonyWatch.Epoch)
        {
            _getterCheckCalls = 1023;
            _getterEpoch = HarmonyWatch.Epoch;
            // Also Player.Piles, which the answer reads the piles of directly (a mod adding a pile there), and CardPile.Cards,
            // through which the game's scan reads each pile (a mod answering another list there).
            // No tolerance: getters answered from the index or read around (a postfix there could answer otherwise).
            // CardModel.Pile: another mod's prefix Harmony runs before PilePrefix (bool or void, ref __result allowed) and
            // postfixes by value are let stand (BeforeReplace): one answering (false) skips PilePrefix as it skips the
            // original, one letting the call through (true) gets the original's answer from PilePrefix. A prefix sorted
            // after it, a transpiler or a finalizer: the game's getter.
            // Player.Piles: not a back-off. The answer is only "the indexed pile, if the owner's piles hold it", so with any
            // other mod's patch there the piles are read through the patched getter, as the original does (OwnsPile), not
            // around it (_runPiles).
            // CardPile.Cards: no tolerance (the game's scan and duplicate check read each pile through it).
            // With the shape rules off (PatchSafety.ShapeRules, a release leaving them out) Player.Piles is a back-off as before.
            _getterPatched = PatchSafety.IsPatchedByOthers(_getter, PatchTolerance.BeforeReplace) || PatchSafety.IsPatchedByOthers(_cardsGetter)
                || (!PatchSafety.ShapeRules && PatchSafety.IsPatchedByOthers(_playerPiles));
            _pilesReadThrough = PatchSafety.HasOtherPatches(_playerPiles);
            _getterOthers = PatchSafety.HasOtherPatches(_getter);
        }
        return _getterPatched;
    }

    private static bool PilePrefix(CardModel __instance, ref CardPile? __result)
    {
        // Inside ListenerCache.RunVanilla the answer is the reference others are compared with: the game's own.
        if (!Enabled || _vanilla || ListenerCache.IsSuspended || GetterPatched())
        {
            return true;
        }
        try
        {
            if (_inFlight is { Count: > 0 } flying && flying.Contains(__instance))
            {
                Fallbacks++;
                return true;
            }
            Player? owner = _owner!(__instance);
            CardPile? answer;
            if (owner == null)
            {
                answer = null;
            }
            else if (!_entries.TryGetValue(__instance, out Entry? entry) || entry.Count == 0)
            {
                answer = null;
            }
            else if (entry.Unknown || entry.Count > 1 || entry.Pile == null)
            {
                Fallbacks++;
                return true;
            }
            else
            {
                answer = OwnsPile(owner, entry.Pile) ? entry.Pile : null;
            }
            Fast++;
            if (Fast % VerifyEvery == 0)
            {
                Verified++;
                // With other mods' patches let stand on the getter, the original's own scan: what PilePrefix answers for (the
                // prefixes before it let the call through), without running them a second time.
                CardPile? vanilla = _getterOthers ? OriginalScan(__instance) : Vanilla(__instance);
                if (vanilla != answer)
                {
                    Fail($"{__instance} index says {answer?.Type.ToString() ?? "none"}, vanilla {vanilla?.Type.ToString() ?? "none"}");
                    __result = vanilla;
                    return false;
                }
            }
            __result = answer;
            return false;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
            return true;
        }
    }

    /// <summary>
    /// The original getter's body (<see cref="GetterFingerprint"/>): <c>_owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(card))</c>,
    /// through the same getters it reads.
    /// </summary>
    private static CardPile? OriginalScan(CardModel card)
    {
        Player? owner = _owner!(card);
        return owner?.Piles.FirstOrDefault(p => p.Cards.Contains(card));
    }

    /// <summary>
    /// Whether <c>owner.Piles</c> holds the pile, read without allocating when that getter's code is known and no other mod
    /// patches it; through the getter otherwise (another mod adding its own piles there: vanilla's scan sees them too).
    /// </summary>
    private static bool OwnsPile(Player owner, CardPile pile)
    {
        CardPile[]? run = _pilesReadThrough ? null : _runPiles?.Invoke(owner);
        if (run == null)
        {
            // Not known, or not made yet (the getter makes it).
            foreach (CardPile candidate in owner.Piles)
            {
                if (candidate == pile)
                {
                    return true;
                }
            }
            return false;
        }
        IReadOnlyList<CardPile>? combat = owner.PlayerCombatState?.AllPiles;
        if (combat != null)
        {
            for (int i = 0; i < combat.Count; i++)
            {
                if (combat[i] == pile)
                {
                    return true;
                }
            }
        }
        for (int i = 0; i < run.Length; i++)
        {
            if (run[i] == pile)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The game's own answer (the getter with this patch bypassed).</summary>
    internal static CardPile? Vanilla(CardModel card)
    {
        bool was = _vanilla;
        _vanilla = true;
        try
        {
            return card.Pile;
        }
        finally
        {
            _vanilla = was;
        }
    }

    /// <summary>
    /// The index can't be trusted any more. Its checks run even while switched off (it keeps being updated then), so this
    /// faults it for the session whatever the switch says: switching it back on can't bring a stale index back.
    /// </summary>
    private static bool _failed;

    private static void Fail(string reason)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        if (_failed)
        {
            return;
        }
        _failed = true;
        Log.Error($"[{ModEntry.Id}] {Name} disabled, falling back to vanilla: {reason}");
    }
}
