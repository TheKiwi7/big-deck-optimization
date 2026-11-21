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

namespace BigDeckOptimization.Patches;

/// <summary>
/// Squeeze, Perfected Strike and Crescent Spear count every card of their owner's combat piles each time their value is
/// calculated: for each of them in the hand, at each refresh and preview (~0.35 ms at 10,000 cards, several times per play).
///
/// The count is kept per player and per card, as of a <see cref="FastListeners.PileVersion"/>. When every pile change since is
/// in <see cref="FastListeners"/>' moves, those are applied to it; otherwise the piles are counted again, as the game does.
/// What's counted (tags, star cost) must be fixed in a card: each card type's getters are read once (<see cref="FlagsOf"/>), and
/// while a card whose type may answer differently over time is in the piles (a content mod's card computing its tags or star
/// cost from its state), or is the asking card, the game counts. None of the three reads anything else. Squeeze leaves itself
/// out: <see cref="PileIndex"/> says whether it's in those piles, and when unsure the game's code runs. One answer in
/// <see cref="VerifyEvery"/> lets the game's code run and compares; a difference turns this off.
///
/// Two more kinds the same way: Wah Symphony (game v0.113+: cards with the Wah, Cho or Ki tag, up to three tag checks per card)
/// and Flak Cannon (status cards not in the exhaust pile: every card's <c>Type</c>, then each status card's <c>Pile</c>). Flak
/// Cannon's count is kept per pile (the exhaust pile left out), which is the game's <c>c.Pile.Type</c> only while each status card
/// of the piles is in exactly one pile, of its owner (the asking card's owner): <see cref="PileIndex"/> says so per card
/// (<see cref="PileIndex.PlacesIn"/>), at each walk for every status card and at each update for every status card moved since;
/// any other answer (two piles, another owner, an index unsure or off) leaves the count to the game, as a card of a changing type does.
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
    /// v0.113.1 <c>WahSymphony</c> (not in v0.111.1): its multiplier (<c>AllCards.Count(...)</c>, the counting lambda included) and
    /// what it counts, <c>Tags.Contains</c> of the tags 6, 7 and 8 (<c>Wah</c>, <c>Cho</c>, <c>Ki</c>: the constants pinned here).
    /// </summary>
    private const string WahFingerprint = "4F023E398297CBA2";
    private const string WahCountsFingerprint = "6200908F542A0E45";
    /// <summary>
    /// v0.111.1 and v0.113.1 <c>FlakCannon</c> (the same code): its multiplier (<c>GetStatuses(card.Owner).Count()</c>),
    /// <c>GetStatuses</c> (<c>owner.PlayerCombatState.AllCards.Where(...)</c>) and what it keeps,
    /// <c>c.Type == CardType.Status (4) &amp;&amp; c.Pile.Type != PileType.Exhaust (4)</c>.
    /// </summary>
    private const string FlakFingerprint = "447D8603071A53ED";
    private const string FlakCountsFingerprint = "52F7A6BB995319E0";
    private const string FlakStatusesFingerprint = "7882128CA404FB3D";
    /// <summary><c>CardModel.Type</c>, an auto-property set by the constructor only (v0.107.1 to v0.113.1).</summary>
    private const string BaseTypeFingerprint = "B3671DD3787AE845";
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

    private const int SqueezeKind = 0;
    private const int StrikeKind = 1;
    private const int SpearKind = 2;
    private const int WahKind = 3;
    private const int FlakKind = 4;
    private const int Kinds = 5;

    /// <summary>What Wah Symphony's lambda checks (its fingerprint pins the constants): Wah, Cho, Ki on game v0.113.</summary>
    private const CardTag WahTag = (CardTag)6;
    private const CardTag ChoTag = (CardTag)7;
    private const CardTag KiTag = (CardTag)8;

#if BENCH
    internal static int VerifyEvery { get; set; } = 16;
#else
    internal static int VerifyEvery { get; set; } = 256;
#endif

#if BENCH
    /// <summary>Positive control (<c>bdperf deckcount wrong</c>): added to every answer.</summary>
    internal static int WrongForTest { get; set; }
#endif

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Kept { get; private set; }
    public static long Updated { get; private set; }
    public static long Walks { get; private set; }
    public static long Unsure { get; private set; }
    public static long Checks { get; private set; }
    public static long Mismatches { get; private set; }
    /// <summary>Counts of its own for the two later kinds, answered or compared (<c>bdperf deckcount answered wah|flak</c>).</summary>
    public static long WahSymphonyAnswers { get; private set; }
    public static long FlakCannonAnswers { get; private set; }

    private static readonly Func<CardModel, bool>[] _counted =
    {
        c => c.Tags.Contains(CardTag.OstyAttack),
        c => c.Tags.Contains(CardTag.Strike),
        c => c.CanonicalStarCost >= 0 || c.HasStarCostX,
        c => c.Tags.Contains(WahTag) || c.Tags.Contains(ChoTag) || c.Tags.Contains(KiTag),
        // Flak Cannon: counted in the piles other than the exhaust pile (CountedPiles), as c.Pile.Type != Exhaust while not Odd.
        c => c.Type == CardType.Status,
    };

    /// <summary>What may change over a card's life, per type (<see cref="FlagsOf"/>): its tags, its star cost, its type.</summary>
    private const byte TagsMove = 1;
    private const byte StarMove = 2;
    private const byte TypeMove = 4;
    private static readonly byte[] _moveMask = { TagsMove, TagsMove, StarMove, TagsMove, TypeMove };

    /// <summary>Per kind, the cards whose type may change what that kind counts: only their type is read, nothing of the card.</summary>
    private static readonly Func<CardModel, bool>[] _moving =
    {
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & StarMove) != 0,
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & TypeMove) != 0,
    };

    private sealed class Counts
    {
        public readonly long[] Version = Enumerable.Repeat(-1L, Kinds).ToArray();
        public readonly int[] Count = new int[Kinds];
        /// <summary>
        /// Cards in the piles of a type that may change what the kind counts (Flak Cannon: and status cards <see cref="Odd"/>):
        /// while any, the game counts.
        /// </summary>
        public readonly int[] Moving = new int[Kinds];
        /// <summary><see cref="_flagsEpoch"/> the counts were taken with: the moving counts depend on the type flags.</summary>
        public readonly long[] Epoch = new long[Kinds];
        /// <summary>Flak Cannon's piles: <see cref="PlayerCombatState.AllPiles"/> without the exhaust pile, and the list it was made from.</summary>
        private CardPile[]? _unexhausted, _from;

        public IReadOnlyList<CardPile> Unexhausted(IReadOnlyList<CardPile> piles)
        {
            if (_from != null && _unexhausted != null && _from.Length == piles.Count)
            {
                bool same = true;
                for (int i = 0; i < _from.Length && same; i++)
                {
                    same = ReferenceEquals(_from[i], piles[i]);
                }
                if (same)
                {
                    return _unexhausted;
                }
            }
            _from = piles.ToArray();
            _unexhausted = _from.Where(p => p.Type != PileType.Exhaust).ToArray();
            return _unexhausted;
        }
    }

    /// <summary>Tied to the combat's piles: nothing here outlives them.</summary>
    private static readonly ConditionalWeakTable<PlayerCombatState, Counts> _counts = new();
    private static readonly MethodBase?[] _targets = new MethodBase?[Kinds];
    /// <summary>Per card, what a kept count skips calling: the count lambda, the piles' card list, what it reads of each card.</summary>
    private static readonly MethodBase?[][] _skipped = new MethodBase?[Kinds][];
    private static int _untilCheck;

    private static MethodInfo? _tagsGetter, _canonicalTagsGetter, _starCostGetter, _starXGetter, _typeGetter;
    private static bool _baseTagsKnown, _baseTypeKnown;
    /// <summary><c>CardModel._owner</c>, what the game's <c>Pile</c> searches the piles of (Flak Cannon).</summary>
    private static AccessTools.FieldRef<CardModel, Player?>? _owner;

    public static void Apply(Harmony harmony)
    {
        MethodBase? allCards = Reflect.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "AllCards");
        // What the three cards count is AllCards; the memo walks AllPiles: only while AllCards is that walk (else all three vanilla).
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
        _typeGetter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Type));
        try
        {
            // An auto-property: fixed in a card when only its constructor writes it.
            PatchSafety.RequireFingerprint(_typeGetter ?? throw new MissingMethodException("CardModel.Type"), BaseTypeFingerprint);
            _ = Reflect.Field(typeof(CardModel), "<Type>k__BackingField") ?? throw new MissingFieldException(nameof(CardModel), "<Type>k__BackingField");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "<Type>k__BackingField", ".ctor");
            _baseTypeKnown = true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            _baseTypeKnown = false;
            Log.Info($"[{ModEntry.Id}] {Name}: cards without their own Type counted by the game (Flak Cannon): {e.Message}");
        }
        _readers[SqueezeKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        _readers[StrikeKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        _readers[SpearKind] = new[] { allCards, pileCards, _starCostGetter, _starXGetter };
        _readers[WahKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        bool squeeze = Card(SqueezeKind, typeof(Squeeze), nameof(SqueezePrefix), harmony, SqueezeFingerprint, SqueezeCountsFingerprint);
        bool strike = Card(StrikeKind, typeof(PerfectedStrike), nameof(StrikePrefix), harmony, StrikeFingerprint, StrikeCountsFingerprint);
        bool spear = Card(SpearKind, typeof(CrescentSpear), nameof(SpearPrefix), harmony, SpearFingerprint, SpearCountsFingerprint);
        bool wah = Card(WahKind, Reflect.TypeByName("MegaCrit.Sts2.Core.Models.Cards.WahSymphony"), nameof(WahPrefix), harmony, WahFingerprint, WahCountsFingerprint);
        bool flak = Flak(harmony, allCards, pileCards);
        if (!squeeze && !strike && !spear && !wah && !flak)
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
    }

    /// <summary>One card, on its own: a card that changed only leaves that card to the game.</summary>
    private static bool Card(int kind, Type? card, string prefix, Harmony harmony, string fingerprint, string countsFingerprint,
        string countsIn = "get_CanonicalVars")
    {
        try
        {
            if (card == null)
            {
                throw new PatchSkippedException("not in this game version");
            }
            MethodInfo multiplier = Lambda(card, typeof(decimal), "get_CanonicalVars");
            PatchSafety.RequireFingerprint(multiplier, fingerprint);
            MethodInfo counts = Lambda(card, typeof(bool), countsIn);
            PatchSafety.RequireFingerprint(counts, countsFingerprint);
            _skipped[kind] = _readers[kind].Append(counts).ToArray();
            PatchGuard.Patch(harmony, multiplier, prefix: new HarmonyMethod(typeof(DeckCountMemo), prefix), postfix: new HarmonyMethod(typeof(DeckCountMemo), nameof(Postfix)));
            _targets[kind] = multiplier;
            return true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {card?.Name ?? "card " + kind} left vanilla: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Flak Cannon: its multiplier calls <c>GetStatuses</c>, whose lambda reads each card's <c>Type</c>, then a status card's
    /// <c>Pile</c> (<c>_owner.Piles</c> searched) and that pile's <c>Type</c>: all skipped by a kept count, so all in its readers.
    /// </summary>
    private static bool Flak(Harmony harmony, MethodBase? allCards, MethodBase? pileCards)
    {
        try
        {
            MethodInfo statuses = Reflect.Method(typeof(FlakCannon), "GetStatuses") ?? throw new MissingMethodException(nameof(FlakCannon), "GetStatuses");
            PatchSafety.RequireFingerprint(statuses, FlakStatusesFingerprint);
            _owner = Reflect.FieldRefAccess<CardModel, Player?>("_owner");
            _readers[FlakKind] = new MethodBase?[]
            {
                allCards, pileCards, statuses, _typeGetter, Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)),
                Reflect.PropertyGetter(typeof(CardPile), nameof(CardPile.Type)), Reflect.PropertyGetter(typeof(Player), nameof(Player.Piles)),
            };
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException or ArgumentException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {nameof(FlakCannon)} left vanilla: {e.Message}");
            return false;
        }
        return Card(FlakKind, typeof(FlakCannon), nameof(FlakPrefix), harmony, FlakFingerprint, FlakCountsFingerprint, "GetStatuses");
    }

    /// <summary>
    /// The compiler's method for a lambda of the card's method <paramref name="within"/>, found by what it returns in whichever
    /// generated class holds it: their names carry a number that shifts when the game adds a member, with the same code.
    /// </summary>
    private static MethodInfo Lambda(Type card, Type returns, string within)
    {
        MethodInfo[] found = card.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(holder => holder.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("<" + within + ">") && m.ReturnType == returns).ToArray();
        return found.Length == 1 ? found[0] : throw new MissingMethodException(card.Name, within + " lambda");
    }

    private static bool SqueezePrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(SqueezeKind, __0, ref __result, out __state);

    private static bool StrikePrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(StrikeKind, __0, ref __result, out __state);

    private static bool SpearPrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(SpearKind, __0, ref __result, out __state);

    private static bool WahPrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(WahKind, __0, ref __result, out __state);

    private static bool FlakPrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(FlakKind, __0, ref __result, out __state);

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

    private static bool Answer(int kind, CardModel card, ref decimal result, out decimal? expected)
    {
        expected = null;
        // Inside ListenerCache.RunVanilla the result is a reference others are compared with: the game's own.
        // No tolerance: the count's getter is answered from the memo, its patches wouldn't run.
        if (!Enabled || ListenerCache.IsSuspended || !FastListeners.PilesKnown || PatchSafety.IsPatchedByOthers(_targets[kind]) || SkippedPatched(kind))
        {
            return true;
        }
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
                Unsure++;
                return true;
            }
            int own = 0;
            if (kind == SqueezeKind && _counted[SqueezeKind](card))
            {
                own = PileIndex.PlacesIn(card, state.AllPiles);
                if (own < 0)
                {
                    Unsure++;
                    return true;
                }
            }
            int count = Count(kind, state, card.Owner, out bool moving) - own;
            if (moving)
            {
                Unsure++;
                return true;
            }
#if BENCH
            count += WrongForTest;
#endif
            // Its own count, answered or compared below.
            if (kind == WahKind)
            {
                WahSymphonyAnswers++;
            }
            else if (kind == FlakKind)
            {
                FlakCannonAnswers++;
            }
            if (--_untilCheck <= 0)
            {
                _untilCheck = VerifyEvery;
                expected = count;
                return true;
            }
            result = count;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    private static void Postfix(decimal __result, decimal? __state)
    {
        if (__state is not { } expected)
        {
            return;
        }
        Checks++;
        if (expected != __result)
        {
            Mismatches++;
            Disable(new InvalidOperationException($"counted {expected}, the game {__result}"));
        }
    }

    /// <summary>The kind's count over the piles, and whether a card of a type that may change it is among them.</summary>
    private static int Count(int kind, PlayerCombatState state, Player player, out bool moving)
    {
        Counts counts = _counts.GetOrCreateValue(state);
        long version = FastListeners.PileVersion;
        if (counts.Epoch[kind] != _flagsEpoch)
        {
            // Type flags read again since (another mod's patch came or went): the moving count is the old flags'.
            counts.Version[kind] = -1;
            counts.Epoch[kind] = _flagsEpoch;
        }
        if (counts.Version[kind] == version)
        {
            Kept++;
            moving = counts.Moving[kind] > 0;
            return counts.Count[kind];
        }
        IReadOnlyList<CardPile> piles = state.AllPiles;
        // Flak Cannon counts outside the exhaust pile; the moving cards and Odd ones are looked for in every pile, as it reads them all.
        bool flak = kind == FlakKind;
        IReadOnlyList<CardPile> countedPiles = flak ? counts.Unexhausted(piles) : piles;
        int count = counts.Count[kind];
        int movingCount = counts.Moving[kind];
        // Both from the same moves (MovesSince gives the same answer twice): the count, then the cards of changing types.
        // Only from a count taken with no card of a changing type in the piles: a move is judged when applied, so such a
        // card that entered under one answer and leaves under another would leave the count off once it is gone.
        if (counts.Version[kind] >= 0 && counts.Moving[kind] == 0
            && FastListeners.ApplyMoves(counts.Version[kind], countedPiles, _counted[kind], ref count)
            && FastListeners.ApplyMoves(counts.Version[kind], piles, _moving[kind], ref movingCount)
            && (!flak || !OddMoved(counts.Version[kind], piles, player)))
        {
            Updated++;
        }
        else
        {
            Walks++;
            count = 0;
            movingCount = 0;
            Func<CardModel, bool> counted = _counted[kind];
            byte mask = _moveMask[kind];
            Type? lastType = null;
            byte lastFlags = 0;
            foreach (CardPile pile in piles)
            {
                IReadOnlyList<CardModel> cards = pile.Cards;
                bool countsHere = !flak || pile.Type != PileType.Exhaust;
                for (int i = 0; i < cards.Count; i++)
                {
                    CardModel card = cards[i];
                    if (counted(card))
                    {
                        if (countsHere)
                        {
                            count++;
                        }
                        if (flak && Odd(card, piles, player))
                        {
                            movingCount++;
                        }
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
    /// A status card in these piles whose <c>Pile</c> may not be the pile it is in: not in exactly one pile as far as
    /// <see cref="PileIndex"/> knows (or the index unsure, off), or owned by another player than the asking card's (the game's
    /// <c>Pile</c> searches the owner's piles). <c>PlayerCombatState.AllPiles</c> are the owner's own (<c>Player.Piles</c> starts
    /// with them). For a card met in a walk: it is in these piles, so anything but 1 is odd.
    /// </summary>
    private static bool Odd(CardModel card, IReadOnlyList<CardPile> piles, Player player) =>
        PileIndex.PlacesIn(card, piles) != 1 || !ReferenceEquals(_owner!(card), player);

    private static IReadOnlyList<CardPile>? _oddPiles;
    private static Player? _oddPlayer;
    private static bool _oddFound;

    /// <summary>
    /// Every status card moved since <paramref name="version"/> and still in these piles is not <see cref="Odd"/> (as now): with the
    /// walk's check on the others, every status card of the piles is then where its <c>Pile</c> says. An owner changed without a
    /// pile move isn't seen: the game's owner changes are made out of a pile (<c>CardPileCmd.GiveToAnotherPlayer</c> removes the card
    /// first; the <c>Owner</c> setter refuses a second owner), and an owner-less card in a pile makes the game's own count throw.
    /// </summary>
    private static bool OddMoved(long version, IReadOnlyList<CardPile> piles, Player player)
    {
        _oddPiles = piles;
        _oddPlayer = player;
        _oddFound = false;
        int none = 0;
        try
        {
            return !FastListeners.ApplyMoves(version, piles, _oddCheck, ref none) || _oddFound;
        }
        finally
        {
            _oddPiles = null;
            _oddPlayer = null;
        }
    }

    private static readonly Func<CardModel, bool> _oddCheck = card =>
    {
        if (!_oddFound && _counted[FlakKind](card) && PileIndex.PlacesIn(card, _oddPiles!) != 0 && Odd(card, _oddPiles!, _oddPlayer!))
        {
            _oddFound = true;
        }
        return false;
    };

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
            Log.Info($"[{ModEntry.Id}] {Name}: another mod's patch on a card's tags, star cost or type came or went: card types read again");
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
            // Type fixed: the type keeps CardModel.Type (the constructor's value) or its own Type returns a constant (a mock card);
            // one reading its state (Mad Science's TinkerTimeType) changes.
            MethodInfo? cardType = Implementation(type, _typeGetter);
            if (cardType == null || Watched(cardType)
                || (cardType.DeclaringType == typeof(CardModel) ? !_baseTypeKnown : !ReturnsConstant(cardType)))
            {
                flags |= TypeMove;
            }
        }
        catch (Exception e)
        {
            flags = TagsMove | StarMove | TypeMove;
            Log.Info($"[{ModEntry.Id}] {Name}: {type.Name}'s tags, star cost and type not read, counted by the game: {e.Message}");
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
