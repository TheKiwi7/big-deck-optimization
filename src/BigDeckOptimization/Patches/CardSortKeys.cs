using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Game v0.113 made <c>CardModel.CompareTo</c> compare far more than the id and upgrade level: enchantment and affliction ids,
/// the resolved energy cost (<c>EnergyCost.GetResolved()</c>: in combat, the cost hook walking every listener twice), the
/// replay count, then each dynamic variable in key order (<c>DynamicVars.Keys.Order(...)</c>, allocated and sorted at each
/// call). <c>ListExtensions.StableShuffle</c> sorts a copy of its list with that comparison before shuffling, on every draw
/// pile reshuffle and in Stone Cracker's combat start (856 ms of a 959 ms combat start at ~1,900 cards, 6.5 s with 4 players).
///
/// Inside a <c>StableShuffle</c> over cards (its known callers, transpiled to <see cref="StableShuffleInScope"/>: a Harmony
/// patch on the generic method itself would replace its code shared by every reference type, which breaks the other lists),
/// <c>CompareTo</c> answers with the game's logic, every value read live but two computed once per card for the sort: the
/// resolved cost and the ordered key list. The sort itself is the game's: the same comparisons give the same order.
/// Two cards of one type with the same keys (a big deck's equal cards: most comparisons go down to the variables) share one key
/// array, and their variables (read with the keys) are compared element by element without the game's two string lookups per
/// key, while each card's variable set and its dictionary are still the ones read at its first comparison of the sort
/// (<see cref="SameVars"/>; a variable replaced under its key during the sort is not seen: the sort's premise); the values themselves
/// are read live. Each card's entry is found once per comparison at most (the pivot's kept from the last one).
///
/// Exact because nothing changes while the sort runs: a sort is opened only when every card's type keeps
/// <c>CardModel.CompareTo</c>, and each combat the cards are in has only cost listeners known to just read
/// (<see cref="SnapshotCardReuse.PureHooks"/>; a card in no combat pile runs no cost hook). One of those, Veilpiercer, reads
/// <c>card.Keywords</c>, which runs the keyword hook: while one stands, every keyword listener must be one known to only add
/// to the set it is given (<see cref="KeywordAdders"/>). Otherwise, and when another mod
/// patches what this reproduces or skips, the game's comparison runs. The first answer of each sort and one in
/// <see cref="VerifyEvery"/> after it are compared with the game's (a schedule of the sort alone, the same on every co-op peer);
/// a difference turns this off for the session.
///
/// The sort-cost premise (owner-approved 2026-11-17; DESIGN "Owner-approved premises"): other mods' patches on the cost chain a
/// sort reads once per card (<see cref="_costChain"/>: <c>CardModel.EnergyCost</c>, <c>CardEnergyCost.GetResolved</c> and
/// <c>GetWithModifiers</c>, <c>LocalCostModifier.Modify</c>, <c>Hook.ModifyEnergyCostInCombat</c>, <c>CardModel.Pile</c>) don't
/// change, inside one synchronous <c>List.Sort</c>, what any card's cost reads: the game's own sort already needs a consistent
/// comparer (an inconsistent one throws or scrambles the order in the game too). With the premise the sort is answered while they
/// carry patches: the cost is still read live, through the patched methods, once per card instead of once per comparison (their
/// code runs fewer times: the premise). Strict still: the comparison itself and what it mirrors (<c>CompareTo</c>, its base,
/// <c>ModelId.Compare</c>, <c>StableShuffle</c>, the dynamic variables and their keys), the cost hook defaults (type-gated as
/// before), the listeners' own cost and keyword hooks, the combat hook walk, the keyword reads, the 1-in-<see cref="VerifyEvery"/>
/// check, and the cost chain too with <see cref="PatchSafety.ShapeRules"/> off.
/// </summary>
public static class CardSortKeys
{
    public const string Name = "CardSortKeys";

    /// <summary><c>CardModel.CompareTo</c>, game v0.113.0 and v0.113.1: the code mirrored.</summary>
    private const string CompareToFingerprint = "F6388632B42CF646";
    /// <summary>v0.107.1 and v0.111.0: id and upgrade level only, cheap: nothing to do.</summary>
    private const string CheapCompareToFingerprint = "2CC3990F98A5EF89";
    /// <summary>The rest, the same on v0.107.1, v0.111.0, v0.113.0 and v0.113.1 (ModelId.Compare: v0.113 only).</summary>
    private const string BaseCompareToFingerprint = "BB460DAC433857B6";
    private const string ModelIdCompareFingerprint = "9044DFF12964F53B";
    private const string StableShuffleFingerprint = "68E6CDB3F3082D3C";
    private const string GetResolvedFingerprint = "5FAA75114FDAFC70";
    private const string GetWithModifiersFingerprint = "60FE75B459B24CFC";
    private const string LocalModifyFingerprint = "E11E84E9412802B6";
    private const string ModifyEnergyCostFingerprint = "081395225799F6BD";
    private const string CostHookDefaultFingerprint = "DB289946DCE7F74D";
    private const string EnergyCostGetterFingerprint = "D6AF1AAE1A2FEE0D";
    private const string DynamicVarsGetterFingerprint = "19C079D25E68C079";
    private const string KeysGetterFingerprint = "E7FAD3B477754438";
    /// <summary><c>CardModel.Pile</c>: <c>GetWithModifiers</c> reads it through <c>CombatState</c>, Free*Power and Veilpiercer directly.</summary>
    private const string PileGetterFingerprint = "6A1FE5E50F9ECAC2";
    /// <summary>
    /// <c>CardModel.CombatState</c> (<c>_owner?.Creature.CombatState</c> when the card is in a combat pile or a combat upgrade
    /// preview), <c>Player.Creature</c> and <c>Creature.CombatState</c> (auto-properties), v0.113.0 and v0.113.1: <see cref="Open"/>
    /// reads a card's possible combat from their fields (no getter runs: <see cref="OwnerCombat"/>), so they are strict.
    /// </summary>
    private const string CardCombatStateFingerprint = "19AC49F43ACE99F6";
    private const string PlayerCreatureFingerprint = "0D20F8EB8EC69AA5";
    private const string CreatureCombatStateFingerprint = "18BE5475C459C82D";
    /// <summary>
    /// What <c>CardModel.CombatState</c>'s condition reads besides <c>Pile</c> (R3-2, re-audit round 3): <c>Player.Piles</c> (the
    /// combat piles, <c>PlayerCombatState.AllPiles</c>, then the deck), <c>Player.PlayerCombatState</c>, the five combat piles'
    /// auto-properties, <c>CardPile.Cards</c> and <c>CardModel.UpgradePreviewType</c> (fields only), v0.113.0 and v0.113.1:
    /// <see cref="CombatCards"/> reads their fields.
    /// </summary>
    private static readonly (Type Type, string Method, string Fingerprint)[] CombatPileReads =
    {
        (typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "get_Piles", "1C4E1C85C62DCE1D"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "get_PlayerCombatState", "623F1DCA6A9A8525"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "get_AllPiles", "33A5BD7841727ED1"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "get_Hand", "9A6C1733E99E2BEA"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "get_DrawPile", "8D3988B953E56D8A"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "get_DiscardPile", "9DBBDBB42097CE6A"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "get_ExhaustPile", "BAF15021F0BD94D4"),
        (typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "get_PlayPile", "B5E74B72AFB4C262"),
        (typeof(CardPile), "get_Cards", "C74105B9A028C09A"),
        (typeof(CardModel), "get_UpgradePreviewType", "0C0AF21DECE63325"),
    };
    /// <summary>What <c>card.Keywords</c> runs (<see cref="KeywordReaders"/>), the same on v0.107.1 to v0.113.1.</summary>
    private const string KeywordsGetterFingerprint = "51B4751CE85A8DA1";
    private const string KeywordsWithSourcesFingerprint = "0F51A275B1583E96";
    private const string ModifyKeywordsFingerprint = "ABD6C412D3CBF743";
    private const string KeywordHookDefaultFingerprint = "B9D92F458020A730";
    private const string KeywordHook = "TryModifyKeywordsInCombat";

    /// <summary>The accepted cost hooks (<see cref="SnapshotCardReuse.PureHooks"/>, fingerprinted there) that read <c>card.Keywords</c>.</summary>
    private static readonly string[] KeywordReaders = { "MegaCrit.Sts2.Core.Models.Powers.VeilpiercerPower" };

    /// <summary>
    /// Keyword listeners that only add to the set they are given, from what they read of the card (owner, affliction, tags) and
    /// their own owner: v0.107.1 to v0.113.1 (Phantom Blades: v0.113).
    /// </summary>
    private static readonly (string Type, string Fingerprint)[] KeywordAdders =
    {
        ("MegaCrit.Sts2.Core.Models.Powers.HexPower", "7CE6FD37170D4241"),
        ("MegaCrit.Sts2.Core.Models.Powers.PhantomBladesPower", "71FF3A908FB1A139"),
    };

    /// <summary>The keyword read's code is the known one: false leaves sorts with a <see cref="KeywordReaders"/> listener to the game.</summary>
    private static bool _keywordsKnown;
    /// <summary>The keyword read's methods: another mod's patch on one leaves such sorts to the game.</summary>
    private static MethodBase?[] _keywordWatched = Array.Empty<MethodBase?>();

    /// <summary>
    /// The types whose code shuffles a <c>List&lt;CardModel&gt;</c> with <c>StableShuffle</c> (the 15 methods of v0.113.1; PunchOfJoy and
    /// EcstasyPower shuffle a <c>List&lt;Sadness&gt;</c> of hand cards). A new caller just stays the game's.
    /// </summary>
    private static readonly string[] CallerTypes =
    {
        "MegaCrit.Sts2.Core.Commands.CardPileCmd",
        "MegaCrit.Sts2.Core.Entities.Merchant.BlitzMerchantRandomCardUpgradesEntry",
        "MegaCrit.Sts2.Core.Models.Cards.BeatDown",
        "MegaCrit.Sts2.Core.Models.Cards.Catastrophe",
        "MegaCrit.Sts2.Core.Models.Cards.SeekerStrike",
        "MegaCrit.Sts2.Core.Models.Cards.TheFool",
        "MegaCrit.Sts2.Core.Models.Cards.Uproar",
        "MegaCrit.Sts2.Core.Models.Events.BattlewornDummy",
        "MegaCrit.Sts2.Core.Models.Relics.FragrantMushroom",
        "MegaCrit.Sts2.Core.Models.Relics.PowerCell",
        "MegaCrit.Sts2.Core.Models.Relics.SandCastle",
        "MegaCrit.Sts2.Core.Models.Relics.StoneCracker",
        "MegaCrit.Sts2.Core.Models.Relics.WarHammer",
        "MegaCrit.Sts2.Core.Models.Relics.WarPaint",
        "MegaCrit.Sts2.Core.Models.Relics.Whetstone",
    };

    private static readonly string[] CostHooks = { "TryModifyEnergyCostInCombat", "TryModifyEnergyCostInCombatLate" };

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    /// <summary>This game's comparison is the cheap one: not applied, and nothing missing.</summary>
    public static bool NotNeeded { get; private set; }

    /// <summary>Sorts answered here, comparisons answered, answers compared with the game's and those that differed.</summary>
    public static long Sorts { get; private set; }
    public static long Answered { get; private set; }
    public static long Checks { get; private set; }
    public static long Mismatches { get; private set; }
    /// <summary>Comparisons that reached the variables and compared them from two entries sharing their keys (bench, counters).</summary>
    public static long SharedKeyCompares { get; private set; }
    /// <summary>Card costs read by answered sorts (once per card per sort at most; bench).</summary>
    public static long CostReads { get; private set; }
#if BENCH
    /// <summary>Bench (<c>bdperf sortkeys partonly</c>): the shared-key comparison off, the game's variable loop in every comparison.</summary>
    internal static bool NoSharedKeysForTest { get; set; }
#endif

    /// <summary>Sorts left to the game, and the last reason (bench, log).</summary>
    public static long Refused { get; private set; }
    public static string? LastRefusal { get; private set; }

    /// <summary>
    /// The last refusal was a keyword listener of the combat not known to only add keywords (an unknown keyword override, or a
    /// known adder another mod patches) while a cost hook reads keywords: left to the game by design (the bench's Veilpiercer
    /// case tests this, not the text).
    /// </summary>
    public static bool LastRefusalKeywordListener { get; private set; }

#if BENCH
    internal static int VerifyEvery { get; set; } = 16;
    /// <summary>Positive control (<c>bdperf sortkeys wrong</c>): answers reversed.</summary>
    internal static bool WrongForTest { get; set; }
#else
    internal static int VerifyEvery { get; set; } = 1024;
#endif

    private static bool _verifying;
    private static int _mainThread = -1;

    private static Func<ModelId?, ModelId?, int>? _modelIdCompare;
    private static AccessTools.FieldRef<CardModel, MegaCrit.Sts2.Core.Entities.Players.Player?>? _cardOwner;
    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Players.Player, MegaCrit.Sts2.Core.Entities.Creatures.Creature?>? _playerCreature;
    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Creatures.Creature, ICombatState?>? _creatureCombat;

    /// <summary>
    /// R3-2: <see cref="CombatPileReads"/> are the known code and their fields were found: only cards that can be in a combat pile
    /// (or a combat upgrade preview) have their owner's combat judged. False: every card's owner's combat is (A3's superset).
    /// </summary>
    private static bool _pilesKnown;
    private static MethodBase[] _pileReads = Array.Empty<MethodBase>();
    private static MethodBase? _pileGetter;
    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Players.Player, MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState?>? _playerCombatState;
    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState, CardPile[]?>? _allPiles;
    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState, CardPile?>[] _pileFields = Array.Empty<AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState, CardPile?>>();
    private static AccessTools.FieldRef<CardPile, List<CardModel>?>? _pileCards;
    private static AccessTools.FieldRef<CardModel, CardUpgradePreviewType>? _upgradePreview;
    private static MethodInfo? _inScope;
    /// <summary>What a sort answered here reproduces or skips calling: another mod's patch on one leaves the sort to the game.</summary>
    private static MethodBase?[] _watched = Array.Empty<MethodBase?>();

    /// <summary><c>CardModel.CombatState</c>, one of <see cref="_watched"/>, judged apart (rule null-fill: <see cref="NullFillNow"/>).</summary>
    private static MethodBase? _cardCombat;

    /// <summary>
    /// Rule null-fill (2026-11-18, CardFusion): every other mod's patch on <c>CardModel.CombatState</c> only replaces a null result,
    /// and only reads and sets that result (<see cref="PatchSafety.NullFillOnly"/>: the game may read the getter for fewer cards than
    /// this reads it for, so the fill must do nothing else). <see cref="OwnerCombat"/> mirrors the getter's non-null answers from the fields;
    /// what such a postfix may give where the game's getter gives null isn't there, so each card's real getter is read too and its
    /// combat judged with the others (a superset again: more refusals, never fewer). The getter then runs once per card in a sort
    /// instead of once per cost read: the sort-cost premise's scope (it is read by the cost chain).
    /// CardFusion's own postfix, the rule's first case, is refused since the review of 2026-11-19 (C4: its fill calls
    /// <c>FusedCard.ChildToParentMap.TryGetValue</c> and another card's getter, not read-only): CardSortKeys backs off next to it.
    /// </summary>
    private static bool NullFillNow() => PatchSafety.ShapeRules && PatchSafety.NullFillOnly(_cardCombat);

    /// <summary>The null-fill kept note (display; the getter's back-off otherwise, as the runtime check records it).</summary>
    private static void NoteNullFill()
    {
        if (_cardCombat == null)
        {
            return;
        }
        if (NullFillNow())
        {
            PatchSafety.Kept(Name, "null_fill", "other mods' code on a card's combat (CardModel.CombatState) that only fills in a missing answer: "
                + "each sorted card's own combat is read once per sort and judged too", PatchSafety.NullFillMods(_cardCombat));
            return;
        }
        PatchSafety.ClearKept(Name, "null_fill");
        _ = PatchSafety.IsPatchedByOthers(_cardCombat, out _, Name);
    }

    /// <summary>
    /// The cost chain a sort reads live once per card (<see cref="Cost"/>): strict with the shape rules off, under the sort-cost
    /// premise with them (another mod's patch there runs once per card instead of once per comparison).
    /// </summary>
    private static MethodBase?[] _costChain = Array.Empty<MethodBase?>();

    /// <summary>The methods checked strictly now: <see cref="_watched"/>, and the cost chain with the shape rules off.</summary>
    private static MethodBase?[] Strict() => PatchSafety.ShapeRules ? _watched : _watched.Concat(_costChain).ToArray();

    /// <summary>
    /// The cost hooks' AbstractModel defaults: a sort runs the cost hook once per card instead of once per comparison, so a patch
    /// there would run fewer times. Patches gated on the listener's type are let stand (TypeGated) while no listener of the
    /// combat is of a gate type (then each of their calls does nothing, however many); any other patch: the game's sort.
    /// </summary>
    private static MethodBase?[] _costDefaults = Array.Empty<MethodBase?>();

    /// <summary>
    /// Per card of the sort: its cost and ordered keys once read, and where the variables of those keys are in the sort's pool
    /// (<see cref="VarsAt"/>), with the set, its dictionary, version and count they were read from (<see cref="SameVars"/>). In one
    /// array made when the sort opens: no allocation per card during the sort.
    /// </summary>
    private struct Entry
    {
        public bool HasCost;
        public int Cost;
        /// <summary>The ordered keys, shared with the other cards of the sort with the same type and the same keys (<see cref="Scope.SharedKeys"/>).</summary>
        public string[]? Keys;
        /// <summary>Where <c>DynamicVars[Keys[i]]</c> start in <see cref="Scope.VarPool"/>, read with the keys; -1 when not read (<see cref="Scope.Shared"/>).</summary>
        public int VarsAt;
        public DynamicVarSet? Set;
        public Dictionary<string, DynamicVar>? SetDictionary;
        public int SetVersion;
        public int SetCount;
    }

    /// <summary>
    /// A sort's tables, kept from one sort to the next (<see cref="_spare"/>): at 10,000 cards each is a large-object allocation
    /// (the index ~240 KB, the entries ~480 KB, the pool), and allocating them per sort made collections (gen 2 for the large
    /// object heap) during reshuffles. Emptied when the sort ends, so they hold no card between sorts.
    /// </summary>
    private sealed class Storage
    {
        public readonly Dictionary<CardModel, int> Index = new(ReferenceEqualityComparer.Instance);
        public Entry[] Entries = Array.Empty<Entry>();
        public DynamicVar[] Pool = Array.Empty<DynamicVar>();
    }

    /// <summary>The storage of the last sort, empty, for the next one (a sort nested in a comparison takes a new one).</summary>
    private static Storage? _spare;

    private sealed class Scope(Storage storage, Scope? previous)
    {
        public readonly Storage Storage = storage;
        /// <summary>The sort's cards and where each one's entry is in <see cref="Entries"/>.</summary>
        public readonly Dictionary<CardModel, int> Index = storage.Index;
        public readonly Entry[] Entries = storage.Entries;
        public readonly Scope? Previous = previous;
        public long Answered;
        /// <summary>Per card type, the first ordered key array met: a card of that type with the same keys shares it.</summary>
        public readonly Dictionary<Type, string[]> SharedKeys = new();
        /// <summary>The shared-key comparison may be used in this sort (<see cref="_varsKnown"/>, <see cref="_varReads"/> unpatched).</summary>
        public bool Shared;
        /// <summary>The variables read with the keys, every card's one after another (<see cref="Entry.VarsAt"/>).</summary>
        public DynamicVar[] VarPool = storage.Pool;
        public int VarCount;
        /// <summary>
        /// The last two cards looked up and their entries: the sort compares one pivot with each element in turn, so one of the two
        /// cards of a comparison is nearly always the last one's (one dictionary lookup a comparison instead of three).
        /// </summary>
        public CardModel? Card0, Card1;
        public int At0 = -1, At1 = -1;
        public bool LastWas1;

        /// <summary>Where the card's entry is, or -1 for a card that isn't in the sort.</summary>
        public int Find(CardModel card)
        {
            if (ReferenceEquals(card, Card0))
            {
                LastWas1 = false;
                return At0;
            }
            if (ReferenceEquals(card, Card1))
            {
                LastWas1 = true;
                return At1;
            }
            if (!Index.TryGetValue(card, out int at))
            {
                return -1;
            }
            // The slot not used last: the other card of the comparison under way (the pivot) stays.
            if (LastWas1)
            {
                Card0 = card;
                At0 = at;
                LastWas1 = false;
            }
            else
            {
                Card1 = card;
                At1 = at;
                LastWas1 = true;
            }
            return at;
        }

        /// <summary>Room for <paramref name="count"/> more variables in the pool; where they start.</summary>
        public int Reserve(int count)
        {
            if (VarCount + count > VarPool.Length)
            {
                Array.Resize(ref VarPool, Math.Max(VarPool.Length * 2, Math.Max(64, VarCount + count)));
                Storage.Pool = VarPool;
            }
            int at = VarCount;
            VarCount += count;
            return at;
        }
    }

    /// <summary>
    /// <c>DynamicVarSet.TryGetValue</c> and its indexer, v0.113.0 and v0.113.1: a comparison of two cards with the same keys reads
    /// their variables from the entries (<see cref="Entry.Vars"/>), these then run once per card in a sort instead of once per key
    /// per comparison: strict, as the keys' getter, but for the shared-key comparison only (<see cref="_varReads"/>): a changed
    /// one or another mod's patch on one leaves that comparison the game's loop, the rest of the sort still answered.
    /// </summary>
    private const string TryGetValueFingerprint = "9ACBBAC4326E5D69";
    private const string VarIndexerFingerprint = "3951F7605C1D25AA";
    private static MethodBase?[] _varReads = Array.Empty<MethodBase?>();

    /// <summary>
    /// The shared-key comparison can be used: the two methods above have their known code, and the fields a comparison reads to
    /// know the entry's variables are still the card's were found: <c>CardModel._dynamicVars</c> (the set the getter returns once
    /// made), <c>DynamicVarSet._vars</c>, that dictionary's <c>_version</c> (bumped by an add of a new key) and its count (a remove
    /// or a clear; on .NET Core 3+ neither bumps the version). Not seen: a variable replaced under an existing key
    /// (<c>_vars[key] = newVar</c>, no version bump), which Steve's, shadowverse's, yugioh's and Loadout's code do by reflection
    /// (when a card is made or enchanted): an entry's variables are read at the card's first comparison of each sort, so such a
    /// write between two sorts is seen; one inside a sort isn't, and rests on the sort's premise (nothing writes a card's
    /// variables during one synchronous sort: the comparison and the known pure cost listeners only read). False: every
    /// comparison reads the variables as the game does (the keys still kept).
    /// </summary>
    private static bool _varsKnown;
    private static AccessTools.FieldRef<CardModel, DynamicVarSet?>? _cardVars;
    private static AccessTools.FieldRef<DynamicVarSet, Dictionary<string, DynamicVar>?>? _setVars;
    private static AccessTools.FieldRef<Dictionary<string, DynamicVar>, int>? _dictionaryVersion;

    /// <summary>The sort under way (main thread only).</summary>
    private static Scope? _scope;

    public static void Apply(Harmony harmony)
    {
        MethodInfo compareTo = Reflect.DeclaredMethod(typeof(CardModel), nameof(CardModel.CompareTo), new[] { typeof(AbstractModel) })
            ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.CompareTo));
        if (MethodFingerprint.Of(compareTo) == CheapCompareToFingerprint)
        {
            NotNeeded = true;
            throw new PatchSkippedException("not needed: CardModel.CompareTo compares only the id and upgrade level on this game version");
        }
        PatchSafety.RequireFingerprint(compareTo, CompareToFingerprint);
        MethodInfo baseCompareTo = Require(Reflect.DeclaredMethod(typeof(AbstractModel), nameof(AbstractModel.CompareTo), new[] { typeof(AbstractModel) }), BaseCompareToFingerprint, "AbstractModel.CompareTo");
        MethodInfo modelIdCompare = Require(Reflect.DeclaredMethod(typeof(ModelId), "Compare", new[] { typeof(ModelId), typeof(ModelId) }), ModelIdCompareFingerprint, "ModelId.Compare");
        MethodInfo shuffle = Require(Reflect.DeclaredMethod(typeof(ListExtensions), nameof(ListExtensions.StableShuffle)), StableShuffleFingerprint, "ListExtensions.StableShuffle");
        MethodInfo getResolved = Require(Reflect.DeclaredMethod(typeof(CardEnergyCost), nameof(CardEnergyCost.GetResolved)), GetResolvedFingerprint, "CardEnergyCost.GetResolved");
        MethodInfo withModifiers = Require(Reflect.DeclaredMethod(typeof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers)), GetWithModifiersFingerprint, "CardEnergyCost.GetWithModifiers");
        MethodInfo localModify = Require(Reflect.DeclaredMethod(typeof(LocalCostModifier), nameof(LocalCostModifier.Modify)), LocalModifyFingerprint, "LocalCostModifier.Modify");
        MethodInfo costHook = Require(Reflect.DeclaredMethod(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat)), ModifyEnergyCostFingerprint, "Hook.ModifyEnergyCostInCombat");
        MethodInfo costDefault = Require(Reflect.DeclaredMethod(typeof(AbstractModel), CostHooks[0]), CostHookDefaultFingerprint, "AbstractModel." + CostHooks[0]);
        MethodInfo costLateDefault = Require(Reflect.DeclaredMethod(typeof(AbstractModel), CostHooks[1]), CostHookDefaultFingerprint, "AbstractModel." + CostHooks[1]);
        MethodInfo energyCost = Require(Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.EnergyCost)), EnergyCostGetterFingerprint, "CardModel.EnergyCost");
        MethodInfo dynamicVars = Require(Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.DynamicVars)), DynamicVarsGetterFingerprint, "CardModel.DynamicVars");
        MethodInfo keys = Require(Reflect.PropertyGetter(typeof(DynamicVarSet), nameof(DynamicVarSet.Keys)), KeysGetterFingerprint, "DynamicVarSet.Keys");
        try
        {
            _varReads = new MethodBase?[]
            {
                Require(Reflect.DeclaredMethod(typeof(DynamicVarSet), nameof(DynamicVarSet.TryGetValue), new[] { typeof(string), typeof(DynamicVar).MakeByRefType() }),
                    TryGetValueFingerprint, "DynamicVarSet.TryGetValue"),
                Require(Reflect.DeclaredMethod(typeof(DynamicVarSet), "get_Item", new[] { typeof(string) }), VarIndexerFingerprint, "DynamicVarSet.get_Item"),
            };
            _cardVars = Reflect.FieldRefAccess<CardModel, DynamicVarSet?>("_dynamicVars");
            _setVars = Reflect.FieldRefAccess<DynamicVarSet, Dictionary<string, DynamicVar>?>("_vars");
            _dictionaryVersion = Reflect.FieldRefAccess<Dictionary<string, DynamicVar>, int>("_version");
            _varsKnown = true;
        }
        catch (Exception e)
        {
            _varsKnown = false;
            _varReads = Array.Empty<MethodBase?>();
            Log.Info($"[{ModEntry.Id}] {Name}: dynamic variable reads changed ({e.Message}): the shared-key comparison left vanilla, each comparison reads them as the game does");
        }
        MethodInfo pile = Require(Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), PileGetterFingerprint, "CardModel.Pile");
        MethodInfo cardCombat = Require(Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.CombatState)), CardCombatStateFingerprint, "CardModel.CombatState");
        MethodInfo playerCreature = Require(Reflect.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "Creature"), PlayerCreatureFingerprint, "Player.Creature");
        MethodInfo creatureCombat = Require(Reflect.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), "CombatState"), CreatureCombatStateFingerprint, "Creature.CombatState");
        _cardOwner = Reflect.FieldRefAccess<CardModel, MegaCrit.Sts2.Core.Entities.Players.Player?>("_owner");
        _playerCreature = Reflect.FieldRefAccess<MegaCrit.Sts2.Core.Entities.Players.Player, MegaCrit.Sts2.Core.Entities.Creatures.Creature?>("<Creature>k__BackingField");
        _creatureCombat = Reflect.FieldRefAccess<MegaCrit.Sts2.Core.Entities.Creatures.Creature, ICombatState?>("<CombatState>k__BackingField");
        _modelIdCompare = (Func<ModelId?, ModelId?, int>)Delegate.CreateDelegate(typeof(Func<ModelId?, ModelId?, int>), modelIdCompare);
        _pileGetter = pile;
        _cardCombat = cardCombat;
        try
        {
            _pileReads = CombatPileReads.Select(r => (MethodBase)Require(Reflect.DeclaredMethod(r.Type, r.Method, Type.EmptyTypes), r.Fingerprint, r.Type.Name + "." + r.Method)).ToArray();
            _playerCombatState = Reflect.FieldRefAccess<MegaCrit.Sts2.Core.Entities.Players.Player, MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState?>("<PlayerCombatState>k__BackingField");
            _allPiles = Reflect.FieldRefAccess<MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState, CardPile[]?>("_piles");
            _pileFields = new[] { "Hand", "DrawPile", "DiscardPile", "ExhaustPile", "PlayPile" }
                .Select(n => Reflect.FieldRefAccess<MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState, CardPile?>("<" + n + ">k__BackingField")).ToArray();
            _pileCards = Reflect.FieldRefAccess<CardPile, List<CardModel>?>("_cards");
            _upgradePreview = Reflect.FieldRefAccess<CardModel, CardUpgradePreviewType>("_upgradePreviewType");
            _pilesKnown = true;
        }
        catch (Exception e)
        {
            _pilesKnown = false;
            Log.Info($"[{ModEntry.Id}] {Name}: combat pile reads changed ({e.Message}): every card's owner's combat is judged");
        }
        _inScope = Reflect.Method(typeof(CardSortKeys), nameof(StableShuffleInScope));
        _watched = new MethodBase?[]
        {
            compareTo, baseCompareTo, modelIdCompare, shuffle, shuffle.MakeGenericMethod(typeof(CardModel)), dynamicVars, keys,
            // Bypassed by OwnerCombat's field reads: a patch there could make the cost hook walk another combat than the one judged.
            cardCombat, playerCreature, creatureCombat,
        };
        _costChain = new MethodBase?[] { energyCost, getResolved, withModifiers, localModify, costHook, pile };
        _costDefaults = new MethodBase?[] { costDefault, costLateDefault };
        try
        {
            // Only what a sort with a keyword-reading cost listener relies on: a change leaves those sorts alone to the game.
            _keywordWatched = new MethodBase?[]
            {
                Require(Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Keywords)), KeywordsGetterFingerprint, "CardModel.Keywords"),
                Require(Reflect.DeclaredMethod(typeof(CardModel), "GetKeywordsWithSources"), KeywordsWithSourcesFingerprint, "CardModel.GetKeywordsWithSources"),
                Require(Reflect.DeclaredMethod(typeof(Hook), nameof(Hook.ModifyKeywordsInCombat)), ModifyKeywordsFingerprint, "Hook.ModifyKeywordsInCombat"),
                Require(Reflect.DeclaredMethod(typeof(AbstractModel), KeywordHook), KeywordHookDefaultFingerprint, "AbstractModel." + KeywordHook),
            };
            _keywordsKnown = true;
        }
        catch (Exception e)
        {
            _keywordsKnown = false;
            Log.Info($"[{ModEntry.Id}] {Name}: keyword reads changed ({e.Message}): sorts in a combat with Veilpiercer stay the game's");
        }

        // Callers found before anything is patched: none found leaves the whole patch out.
        List<MethodBase> callers = new();
        foreach (string name in CallerTypes)
        {
            Type? type = typeof(CardModel).Assembly.GetType(name);
            if (type == null)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {name} not found: its shuffles stay the game's");
                continue;
            }
            int before = callers.Count;
            callers.AddRange(Calling(type));
            if (callers.Count == before)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {type.Name} no longer shuffles cards with StableShuffle");
            }
        }
        if (callers.Count == 0)
        {
            throw new PatchSkippedException("no known caller shuffles cards with StableShuffle");
        }
        _mainThread = Environment.CurrentManagedThreadId;
        PatchGuard.Patch(harmony, compareTo, prefix: new HarmonyMethod(typeof(CardSortKeys), nameof(ComparePrefix)));
        foreach (MethodBase caller in callers)
        {
            PatchGuard.Patch(harmony, caller, transpiler: new HarmonyMethod(typeof(CardSortKeys), nameof(CallerTranspiler)) { priority = Priority.Last });
        }
        Applied = true;
        // Display only: what every sort asks about, and the combat hook walk a sort in combat asks HookFilter about. The cost
        // hooks of the combat's listener types (ImpureCostListener) depend on who is in the combat: those of the types met so
        // far are asked again at each preflight. The keyword reads (_keywordWatched) only matter in a combat where a cost hook
        // reads keywords (Veilpiercer): not known at the menu, so they show when such a sort checks them.
        // CardModel.CombatState through the null-fill note (the runtime asks NullFillNow first).
        PatchSafety.Watch(Name, _watched.Where(m => m != _cardCombat), PatchTolerance.TypeGated);
        PatchSafety.WatchNote(Name, NoteNullFill);
        PatchSafety.Watch(Name, Strict().Except(_watched));
        PatchSafety.Watch(Name, _costDefaults, PatchTolerance.TypeGated);
        PatchSafety.WatchAll(Name, HookFilter.CombatHookWalkParts);
        PatchSafety.WatchAll(Name, MetCostHooks);
        PatchSafety.WatchNote(Name, NoteModTypes);
        PatchSafety.WatchNote(Name, NoteCostChain);
    }

    /// <summary>
    /// The kept note of the sort-cost premise: which other mods' patches (or hidden IL hooks) on the cost chain a sort reads once
    /// per card instead of once per comparison. Cleared with the shape rules off, when there is none, and while a strict method of
    /// every sort backs off (no sort is answered then: the back-off shows instead).
    /// </summary>
    private static void NoteCostChain()
    {
        var patched = new List<MethodBase>();
        // Only while sorts can be answered (G, re-audit of the premises): not once faulted, nor before it applied (with the mod off:
        // shown as what would run once on, as before).
        // A patch there gated on the card's type (TypeGated) leaves sorts answered (those holding a gate type aside): asked as the
        // preflight asks (the patch name as caller: not counted as a runtime check).
        if (PatchSafety.ShapeRules && Applied && !PatchSafety.IsFaulted(Name) && !_watched.Any(m => !(m == _cardCombat && NullFillNow()) && PatchSafety.IsPatchedByOthers(m, out _, Name)))
        {
            patched.AddRange(_costChain.OfType<MethodBase>().Where(PatchSafety.HasOtherPatches));
        }
        if (patched.Count == 0)
        {
            PatchSafety.ClearKept(Name, "sort_cost");
            return;
        }
        var mods = new List<string>(PatchSafety.OtherModsPatching(patched));
        foreach (MethodBase method in patched)
        {
            mods.AddRange(HarmonyWatch.HiddenOwners(method));
        }
        string names = string.Join(", ", patched.Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct());
        PatchSafety.Kept(Name, "sort_cost", $"other mods' code on a card's cost ({names}) runs once per card in a reshuffle's sort instead of "
            + "once per comparison: taken as harmless (a stated premise: inside one sort it doesn't change any card's cost, as the game's "
            + $"own sort needs; each sort's first answer and one in {VerifyEvery:N0} after it are still compared with the game's)", mods.ToArray());
    }

    private static MethodInfo Require(MethodInfo? method, string fingerprint, string what)
    {
        if (method == null)
        {
            throw new MissingMethodException(what);
        }
        PatchSafety.RequireFingerprint(method, fingerprint);
        return method;
    }

    /// <summary>The methods of the type (state machines and lambdas included) calling <c>StableShuffle&lt;CardModel&gt;</c>.</summary>
    private static IEnumerable<MethodBase> Calling(Type type)
    {
        List<MethodBase> found = new();
        foreach (MethodBase method in MethodFingerprint.DeclaredMethods(type))
        {
            if (method.ContainsGenericParameters || method.GetMethodBody() == null)
            {
                continue;
            }
            try
            {
                if (MethodFingerprint.UsedMembers(method).Any(member => member is MethodInfo called && IsCardShuffle(called)))
                {
                    found.Add(method);
                }
            }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException or BadImageFormatException or ArgumentException)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {type.Name}.{method.Name} not read ({e.Message}): its shuffles stay the game's");
            }
        }
        return found;
    }

    private static bool IsCardShuffle(MethodInfo method) =>
        method.IsGenericMethod && method.Name == nameof(ListExtensions.StableShuffle) && method.DeclaringType == typeof(ListExtensions)
        && method.GetGenericArguments() is [var t] && t == typeof(CardModel);

    /// <summary>Each <c>StableShuffle&lt;CardModel&gt;</c> call goes through <see cref="StableShuffleInScope"/> (same stack, same result).</summary>
    private static IEnumerable<CodeInstruction> CallerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo called && IsCardShuffle(called))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = _inScope;
            }
            yield return instruction;
        }
    }

    /// <summary>The game's <c>list.StableShuffle(rng)</c>, its sort answered here when that's exact. Its own exceptions reach the game.</summary>
    public static List<CardModel> StableShuffleInScope(List<CardModel> list, Rng rng)
    {
        Scope? scope = Open(list);
        // An answered sort is a ListenerCache scope too: its cost reads' listener walks served from the lists of this state
        // version, and other mods' walk postfixes' output reused within the sort (ListenerCache.SortWalksName).
        int walks = scope != null ? ListenerCache.EnterSort() : 0;
        try
        {
            return list.StableShuffle(rng);
        }
        finally
        {
            ListenerCache.ExitSort(walks);
            if (scope != null)
            {
                Close(scope);
            }
        }
    }

#if BENCH
    /// <summary>Bench: <c>list.Sort()</c> as <c>StableShuffle</c> runs it, inside a scope when one would open.</summary>
    internal static bool SortInScope(List<CardModel> list)
    {
        Scope? scope = Open(list);
        int walks = scope != null ? ListenerCache.EnterSort() : 0;
        try
        {
            list.Sort();
        }
        finally
        {
            ListenerCache.ExitSort(walks);
            if (scope != null)
            {
                Close(scope);
            }
        }
        return scope != null;
    }

    /// <summary>Bench: the first impure cost listener of a combat (what a sort judging it would refuse for), or null.</summary>
    internal static string? ImpureCostListenerForTest(ICombatState combat) => ImpureCostListener(combat, null, out _);

    /// <summary>
    /// For the bench: why R3-2's narrow judgment is off now (every card's owner's combat judged), or null when it is on. Another
    /// mod's patch on one of the reads (BaseLib's transpiler on <c>PlayerCombatState.AllPiles</c>) turns it off.
    /// </summary>
    internal static string? NarrowOffForTest()
    {
        if (!_pilesKnown)
        {
            return "combat pile reads changed";
        }
        MethodBase? patched = PatchSafety.HasOtherPatches(_pileGetter) ? _pileGetter : _pileReads.FirstOrDefault(PatchSafety.HasOtherPatches);
        return patched == null ? null : $"another mod patches {patched.DeclaringType?.Name}.{patched.Name}";
    }

    /// <summary>Bench: the counters as they were, so a bench's sorts don't count as the game's (effects).</summary>
    internal static void RestoreCounters(long sorts, long answered)
    {
        Sorts = sorts;
        Answered = answered;
    }
#endif

    /// <summary>A scope for this list when its sort can be answered exactly; null otherwise (the game's comparison).</summary>
    private static Scope? Open(List<CardModel> list)
    {
        // Inside ListenerCache.RunVanilla the result is a reference others are compared with: the game's own.
        if (!Enabled || ListenerCache.IsSuspended || Environment.CurrentManagedThreadId != _mainThread || list == null)
        {
            return null;
        }
        Storage storage = _spare ?? new Storage();
        _spare = null;
        Scope? scope = OpenWith(list, storage);
        if (scope == null)
        {
            Release(storage, 0, 0);
        }
        return scope;
    }

    /// <summary>The storage emptied (no card held between sorts) and kept for the next sort (the larger one when two are free).</summary>
    private static void Release(Storage storage, int entries, int vars)
    {
        storage.Index.Clear();
        Array.Clear(storage.Entries, 0, Math.Min(entries, storage.Entries.Length));
        Array.Clear(storage.Pool, 0, Math.Min(vars, storage.Pool.Length));
        if (_spare == null || _spare.Entries.Length < storage.Entries.Length)
        {
            _spare = storage;
        }
    }

    private static Scope? OpenWith(List<CardModel> list, Storage storage)
    {
        try
        {
            Type[]? watchedGates = null;
            bool nullFill = false;
            foreach (MethodBase? method in _watched)
            {
                if (method == _cardCombat && NullFillNow())
                {
                    nullFill = true;
                    continue;
                }
                // The comparison and its keys are computed here once per card instead of per comparison: only patches gated on
                // the instance's type stand (TypeGated, rule TG of 2026-11-18: they do nothing unless the instance is of a gate
                // type), and a sort holding a card of a gate type is the game's (below). On a method whose instance isn't a card
                // (ModelId.Compare, StableShuffle, DynamicVarSet.Keys, Player.Creature, Creature.CombatState) TypeGated finds no
                // gate: every patch there backs off, as before.
                if (PatchSafety.IsPatchedByOthers(method, out Type[] gates))
                {
                    return Refuse($"another mod patches {method?.DeclaringType?.Name}.{method?.Name}");
                }
                if (gates.Length > 0)
                {
                    watchedGates = watchedGates == null ? gates : watchedGates.Concat(gates).ToArray();
                }
            }
            if (!PatchSafety.ShapeRules)
            {
                foreach (MethodBase? method in _costChain)
                {
                    // As before the sort-cost premise: the cost read once per card, no tolerance.
                    if (PatchSafety.IsPatchedByOthers(method))
                    {
                        return Refuse($"another mod patches {method?.DeclaringType?.Name}.{method?.Name}");
                    }
                }
            }
            Type[]? costGates = null;
            foreach (MethodBase? method in _costDefaults)
            {
                if (PatchSafety.IsPatchedByOthers(method, out Type[] gates))
                {
                    return Refuse($"another mod patches {method?.DeclaringType?.Name}.{method?.Name}");
                }
                if (gates.Length > 0)
                {
                    costGates = costGates == null ? gates : costGates.Concat(gates).ToArray();
                }
            }
            Dictionary<CardModel, int> cards = storage.Index;
            cards.EnsureCapacity(list.Count);
            List<ICombatState>? combats = null;
            ICombatState? last = null;
            // R3-2: a card's combat is judged only when it can be in a combat pile or a combat preview (no other mod's code on the
            // reads CardModel.CombatState makes, CardModel.Pile included: else every card's, A3's superset).
            bool narrow = _pilesKnown && !PatchSafety.HasOtherPatches(_pileGetter) && !_pileReads.Any(PatchSafety.HasOtherPatches);
            Dictionary<MegaCrit.Sts2.Core.Entities.Players.Player, HashSet<CardModel>>? combatCards = null;
            foreach (CardModel card in list)
            {
                if (card == null || !cards.TryAdd(card, cards.Count))
                {
                    continue;
                }
                if (!Plain(card.GetType()))
                {
                    return Refuse($"{card.GetType().Name} compares itself");
                }
                if (watchedGates != null && GateOf(card.GetType(), watchedGates) is { } gate)
                {
                    return Refuse($"{card.GetType().Name} is a {gate.Name}, whose comparison another mod's gated patch acts on");
                }
                // Its cost runs the cost hook only in a combat pile (GetWithModifiers: CombatState != null), that combat's listeners:
                // its owner's combat, read from the fields (no getter, so no other mod's code on CardModel.Pile runs here, for
                // cards whose sort may never reach the cost; re-audit of the premises, A3). A card outside a combat pile whose owner
                // is in combat adds that combat to the judged ones: more refusals, never fewer.
                // R3-2 (re-audit round 3): only a card that can be in a combat pile or a combat upgrade preview (CombatCards: the
                // owner's combat piles read from the fields) reaches the cost hook: a deck card sorted after a combat ended (WarHammer's
                // AfterCombatVictory shuffle; Creature.CombatState still the ended combat's) has no combat to judge, as before A3.
                ICombatState? combat = OwnerCombat(card);
                if (combat != null && !ReferenceEquals(combat, last) && (combats == null || !combats.Any(c => ReferenceEquals(c, combat)))
                    && (!narrow || InCombat(card, ref combatCards)))
                {
                    last = combat;
                    combats ??= new List<ICombatState>();
                    combats.Add(combat);
                }
                // Null-fill: the real getter, another mod's postfix included (what the cost chain reads).
                if (nullFill && card.CombatState is { } real && (combats == null || !combats.Any(c => ReferenceEquals(c, real))))
                {
                    combats ??= new List<ICombatState>();
                    combats.Add(real);
                }
            }
            if (combats != null)
            {
                // The game's hook walks IterateCombatHookListeners: known code (a subset of IterateHookListeners), not patched.
                if (HookFilter.CombatHookWalkPatched())
                {
                    return Refuse("the combat hook walk changed or is patched");
                }
                foreach (ICombatState combat in combats)
                {
                    if (ImpureCostListener(combat, costGates, out bool keywordListener) is { } impure)
                    {
                        return Refuse($"{impure} may change a cost", keywordListener);
                    }
                }
            }
            if (storage.Entries.Length < cards.Count)
            {
                storage.Entries = new Entry[cards.Count];
            }
            Scope scope = new(storage, _scope);
            for (int i = 0; i < cards.Count; i++)
            {
                scope.Entries[i].VarsAt = -1;
            }
            // The variables' reads, strict for the shared-key comparison only (asked once per sort).
            bool shared = _varsKnown;
#if BENCH
            shared &= !NoSharedKeysForTest;
#endif
            foreach (MethodBase? method in _varReads)
            {
                shared &= !PatchSafety.IsPatchedByOthers(method);
            }
            scope.Shared = shared;
            _scope = scope;
            return scope;
        }
        catch (Exception e)
        {
            Disable(e);
            return null;
        }
    }

    /// <summary>The first gate (TypeGated: a type, or every loaded type of a tested name) the card's runtime type is assignable to, or null.</summary>
    private static Type? GateOf(Type type, Type[] gates)
    {
        foreach (Type gate in gates)
        {
            if (gate.IsAssignableFrom(type))
            {
                return gate;
            }
        }
        return null;
    }

    /// <summary>
    /// <c>_owner?.Creature?.CombatState</c> from the fields: a superset of <c>card.CombatState</c> (that one is the same object
    /// when the card is in a combat pile or a combat upgrade preview, null otherwise), no game or mod code run.
    /// </summary>
    private static ICombatState? OwnerCombat(CardModel card)
    {
        MegaCrit.Sts2.Core.Entities.Players.Player? owner = _cardOwner!(card);
        if (owner == null)
        {
            return null;
        }
        MegaCrit.Sts2.Core.Entities.Creatures.Creature? creature = _playerCreature!(owner);
        return creature == null ? null : _creatureCombat!(creature);
    }

    /// <summary>
    /// Whether <c>card.CombatState</c> may be its owner's combat (R3-2): the card in a combat upgrade preview, or in one of its
    /// owner's combat piles (<c>Player.Piles</c> is the combat piles, then the deck: <c>Pile</c>, the first pile holding it, is a
    /// combat pile only when one holds it). A superset, from the fields (no game or mod code), the owner's piles read once a sort.
    /// </summary>
    private static bool InCombat(CardModel card, ref Dictionary<MegaCrit.Sts2.Core.Entities.Players.Player, HashSet<CardModel>>? combatCards)
    {
        if (_upgradePreview!(card) == CardUpgradePreviewType.Combat)
        {
            return true;
        }
        MegaCrit.Sts2.Core.Entities.Players.Player? owner = _cardOwner!(card);
        if (owner == null)
        {
            return false;
        }
        combatCards ??= new Dictionary<MegaCrit.Sts2.Core.Entities.Players.Player, HashSet<CardModel>>(ReferenceEqualityComparer.Instance);
        if (!combatCards.TryGetValue(owner, out HashSet<CardModel>? set))
        {
            set = CombatCards(owner);
            combatCards[owner] = set;
        }
        return set.Contains(card);
    }

    /// <summary>The cards of the owner's combat piles (<c>PlayerCombatState.AllPiles</c> as its getter builds it), from the fields.</summary>
    private static HashSet<CardModel> CombatCards(MegaCrit.Sts2.Core.Entities.Players.Player owner)
    {
        var set = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);
        if (_playerCombatState!(owner) is not { } combat)
        {
            return set;
        }
        IEnumerable<CardPile?> piles = _allPiles!(combat) ?? _pileFields.Select(f => f(combat));
        foreach (CardPile? pile in piles)
        {
            if (pile != null && _pileCards!(pile) is { } list)
            {
                set.UnionWith(list);
            }
        }
        return set;
    }

    private static Scope? Refuse(string why, bool keywordListener = false)
    {
        Refused++;
        LastRefusal = why;
        LastRefusalKeywordListener = keywordListener;
        return null;
    }

    private static void Close(Scope scope)
    {
        _scope = scope.Previous;
        if (scope.Answered > 0)
        {
            Sorts++;
        }
        Release(scope.Storage, scope.Index.Count, scope.VarCount);
    }

    // --- What a sort may rely on ---

    /// <summary>
    /// Per card type: it compares through <c>CardModel.CompareTo</c> (no override, no comparison interface of its own). The
    /// interface map too: <c>List&lt;CardModel&gt;.Sort</c> calls <c>IComparable&lt;AbstractModel&gt;.CompareTo</c>, which a type
    /// re-implementing that interface explicitly answers with its own code though its public <c>CompareTo</c> is the game's.
    /// </summary>
    private static readonly Dictionary<Type, bool> _plain = new();

    private static bool Plain(Type type)
    {
        if (!_plain.TryGetValue(type, out bool plain))
        {
            try
            {
                MethodInfo? compareTo = type.GetMethod(nameof(CardModel.CompareTo), BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(AbstractModel) }, null);
                plain = compareTo?.DeclaringType == typeof(CardModel)
                    && !type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IComparable<>) && i != typeof(IComparable<AbstractModel>))
                    && type.GetInterfaceMap(typeof(IComparable<AbstractModel>)).TargetMethods is [var target] && target.DeclaringType == typeof(CardModel);
            }
            catch (Exception e) when (e is AmbiguousMatchException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                plain = false; // A CompareTo hiding the game's, or an interface map not read.
            }
            _plain[type] = plain;
        }
        return plain;
    }

    /// <summary>
    /// A listener type's hooks a sort may run: its cost hook overrides, all known to only read (<see cref="SnapshotCardReuse.PureHooks"/>),
    /// whether one of them reads <c>card.Keywords</c>, and its keyword hook override (null: none) and whether that one is known to
    /// only add (<see cref="KeywordAdders"/>).
    /// </summary>
    private sealed record ListenerReads(MethodInfo[] Cost, bool ReadsKeywords, MethodInfo? Keyword, bool KeywordKnown);

    /// <summary>Per listener type: what <see cref="ListenerReads"/> says; null when a cost hook may do more than read.</summary>
    private static readonly Dictionary<Type, ListenerReads?> _costOverrides = new();

    /// <summary>
    /// The first listener of this combat whose cost hooks may do more than read; null when there is none. While a cost hook
    /// reads <c>card.Keywords</c> (<see cref="KeywordReaders"/>), the keyword hook it runs too: every keyword listener of the
    /// combat (the same walk, <c>IterateHookListeners</c>) must only add to the set, and the keyword read must be the known code.
    /// </summary>
    private static string? ImpureCostListener(ICombatState combat, Type[]? costGates, out bool keywordListenerOnly)
    {
        keywordListenerOnly = false;
        Type? lastType = null;
        bool readsKeywords = false;
        string? keywordListener = null;
        foreach (AbstractModel model in combat.IterateHookListeners())
        {
            if (model == null)
            {
                continue;
            }
            Type type = model.GetType();
            if (type != lastType)
            {
                lastType = type;
                if (costGates != null && costGates.Any(gate => gate.IsAssignableFrom(type)))
                {
                    return type.Name + " (another mod's patch on a cost hook default is gated on it)";
                }
                ListenerReads? reads = CostOverrides(type);
                if (reads == null)
                {
                    return type.Name;
                }
                foreach (MethodInfo method in reads.Cost)
                {
                    // No tolerance: cost overrides run once per sort instead of per comparison (a patch would run fewer times).
                    if (PatchSafety.IsPatchedByOthers(method))
                    {
                        return type.Name + " (patched)";
                    }
                }
                readsKeywords |= reads.ReadsKeywords;
                // Only a real override is asked about: IsPatchedByOthers(null) answers true, and most types have none.
                if (keywordListener == null
                    && (!reads.KeywordKnown || (reads.Keyword != null && PatchSafety.IsPatchedByOthers(reads.Keyword))))
                {
                    keywordListener = type.Name + (reads.KeywordKnown ? " (keywords, patched)" : " (keywords)");
                }
            }
        }
        if (!readsKeywords)
        {
            return null;
        }
        if (!_keywordsKnown)
        {
            return "a cost hook reading keywords (keyword code changed)";
        }
        foreach (MethodBase? method in _keywordWatched)
        {
            // No tolerance: keyword reads run once per sort instead of per comparison.
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return $"a cost hook reading keywords ({method?.DeclaringType?.Name}.{method?.Name} patched)";
            }
        }
        keywordListenerOnly = keywordListener != null;
        return keywordListener;
    }

    /// <summary>
    /// The note (back-off preflight) for other mods' types met by sorts so far that leave a sort to the game: cards with their
    /// own sort order (<see cref="Plain"/>), listeners whose cost hooks aren't known to only read (<see cref="ImpureCostListener"/>).
    /// Known once a combat sorted a pile with them: none before.
    /// </summary>
    private static void NoteModTypes()
    {
        string[] cards = PatchSafety.ModNamesOf(_plain.ToArray().Where(entry => !entry.Value).Select(entry => entry.Key.Assembly));
        string[] listeners = PatchSafety.ModNamesOf(_costOverrides.ToArray().Where(entry => entry.Value == null).Select(entry => entry.Key.Assembly));
        if (cards.Length == 0 && listeners.Length == 0)
        {
            PatchSafety.ClearNote(Name, "mod_types");
            return;
        }
        // The types themselves (a few), not only their mods: a run modifier present in every run (ARAM's) is then named as the cause.
        // No purity analysis of other mods' hooks (DESIGN "Needless back-offs made safe"): only the game's fingerprinted pure
        // overrides are trusted, so these keep the game's comparison.
        string[] types = _plain.ToArray().Where(entry => !entry.Value).Select(entry => entry.Key)
            .Concat(_costOverrides.ToArray().Where(entry => entry.Value == null).Select(entry => entry.Key))
            .Where(type => type.Assembly != typeof(AbstractModel).Assembly).Select(type => type.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string named = types.Length == 0 ? "" : " (" + string.Join(", ", types.Take(3)) + (types.Length > 3 ? $" and {types.Length - 3} more" : "") + ")";
        PatchSafety.Note(Name, "mod_types", "other mods' cards with their own sort order, or cards and relics whose cost hooks may change more than a cost"
            + named + ": sorts with them run the game's comparison (seen in the combats so far)", cards.Concat(listeners).ToArray());
    }

    /// <summary>The cost and keyword hook overrides of the listener types sorts met so far (back-off preflight).</summary>
    private static IEnumerable<MethodBase?> MetCostHooks() =>
        _costOverrides.Values.ToArray().Where(reads => reads != null)
            .SelectMany(reads => reads!.Cost.Cast<MethodBase?>().Concat(reads.KeywordKnown && reads.Keyword != null ? new MethodBase?[] { reads.Keyword } : Array.Empty<MethodBase?>()))
            .ToArray();

    private static ListenerReads? CostOverrides(Type type)
    {
        if (_costOverrides.TryGetValue(type, out ListenerReads? known))
        {
            return known;
        }
        List<MethodInfo>? overrides = new();
        bool readsKeywords = false;
        try
        {
            foreach (string name in CostHooks)
            {
                MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
                if (method == null || method.DeclaringType == typeof(AbstractModel))
                {
                    continue;
                }
                int entry = Array.FindIndex(SnapshotCardReuse.PureHooks, h => h.Type == method.DeclaringType?.FullName && h.Method == name);
                if (entry < 0 || method.DeclaringType != type
                    || !PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), SnapshotCardReuse.PureHooks[entry].Fingerprint))
                {
                    overrides = null;
                    break;
                }
                overrides.Add(method);
                readsKeywords |= Array.IndexOf(KeywordReaders, type.FullName) >= 0;
            }
        }
        catch (Exception e) when (e is AmbiguousMatchException or MissingMemberException or TypeLoadException)
        {
            overrides = null;
        }
        if (overrides == null)
        {
            return _costOverrides[type] = null;
        }
        // Judged apart: an unknown keyword listener only matters in a combat where a cost hook reads keywords.
        MethodInfo? keyword = null;
        bool keywordKnown;
        try
        {
            keyword = type.GetMethod(KeywordHook, BindingFlags.Public | BindingFlags.Instance);
            if (keyword == null || keyword.DeclaringType == typeof(AbstractModel))
            {
                keyword = null;
                keywordKnown = true;
            }
            else
            {
                MethodInfo method = keyword;
                int entry = Array.FindIndex(KeywordAdders, a => a.Type == method.DeclaringType?.FullName);
                keywordKnown = entry >= 0 && method.DeclaringType == type
                    && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), KeywordAdders[entry].Fingerprint);
            }
        }
        catch (Exception e) when (e is AmbiguousMatchException or MissingMemberException or TypeLoadException)
        {
            keyword = null;
            keywordKnown = false;
        }
        return _costOverrides[type] = new ListenerReads(overrides.ToArray(), readsKeywords, keyword, keywordKnown);
    }

    // --- The comparison ---

    private static bool ComparePrefix(CardModel __instance, AbstractModel? __0, ref int __result)
    {
        Scope? scope = _scope;
        // The scope is the main thread's sort: a comparison on another thread meanwhile is the game's.
        if (scope == null || _verifying || !Enabled || Environment.CurrentManagedThreadId != _mainThread)
        {
            return true;
        }
        try
        {
            CardModel? other = __0 as CardModel;
            if (__0 != null && other == null)
            {
                return true; // The game's cast throws.
            }
            int mine = scope.Find(__instance);
            int theirs = other == null ? -1 : scope.Find(other);
            if (mine < 0 || (other != null && theirs < 0))
            {
                return true; // Not a card of this sort: nothing was judged about it.
            }
            int result = Compare(scope, __instance, mine, other, theirs);
#if BENCH
            if (WrongForTest && result != 0)
            {
                result = -result;
            }
#endif
            Answered++;
            // The check schedule is the sort's own (re-audit of the premises, A2): the first answer of each sort and every
            // VerifyEvery-th after it. A sort is a synchronized game action (the same list, the same comparisons on every peer),
            // so every peer answering it checks the same comparisons and reaches the same verdict at the same one: a process-wide
            // counter (as before) checked different comparisons per peer, and a difference found by one peer only would have
            // turned it to the game's order alone.
            if (scope.Answered++ % VerifyEvery == 0)
            {
                result = Verify(__instance, __0, result);
            }
            __result = result;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>v0.113's <c>CardModel.CompareTo</c> (with <c>AbstractModel.CompareTo</c>), the same reads in the same order.</summary>
    private static int Compare(Scope scope, CardModel a, int mine, CardModel? b, int theirs)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }
        if (b == null)
        {
            return 1;
        }
        int result = a.Id.CompareTo(b.Id);
        if (result != 0)
        {
            return result;
        }
        result = a.CurrentUpgradeLevel.CompareTo(b.CurrentUpgradeLevel);
        if (result != 0)
        {
            return result;
        }
        result = _modelIdCompare!(a.Enchantment?.Id, b.Enchantment?.Id);
        if (result != 0)
        {
            return result;
        }
        result = _modelIdCompare(a.Affliction?.Id, b.Affliction?.Id);
        if (result != 0)
        {
            return result;
        }
        ref Entry entry = ref scope.Entries[mine];
        ref Entry other = ref scope.Entries[theirs];
        int costA = Cost(a, ref entry);
        result = costA.CompareTo(Cost(b, ref other));
        if (result != 0)
        {
            return result;
        }
        result = a.BaseReplayCount.CompareTo(b.BaseReplayCount);
        if (result != 0)
        {
            return result;
        }
        string[] keys = entry.Keys ?? Read(scope, a, ref entry);
        if (keys.Length == 0)
        {
            return 0; // The game reads nothing of b's variables then: neither is it read here.
        }
        // Two cards with the same keys (one shared array: the same type and the same ordered keys), their variables still the ones
        // read with the keys (SameVars; a variable replaced under its key inside the sort rests on the sort's premise): b has each of
        // a's keys (TryGetValue finds it), and its variable is the one read then. The values are read live, in the game's order, up
        // to the first difference.
        if (other.Keys == null)
        {
            Read(scope, b, ref other);
        }
        if (ReferenceEquals(keys, other.Keys) && SameVars(a, ref entry) && SameVars(b, ref other))
        {
            SharedKeyCompares++;
            DynamicVar[] pool = scope.VarPool;
            int at = entry.VarsAt, atOther = other.VarsAt;
            for (int i = 0; i < keys.Length; i++)
            {
                result = pool[at + i].BaseValue.CompareTo(pool[atOther + i].BaseValue);
                if (result != 0)
                {
                    return result;
                }
            }
            return 0;
        }
        foreach (string key in keys)
        {
            if (b.DynamicVars.TryGetValue(key, out DynamicVar? value))
            {
                result = a.DynamicVars[key].BaseValue.CompareTo(value.BaseValue);
                if (result != 0)
                {
                    return result;
                }
            }
        }
        return 0;
    }

    private static int Cost(CardModel card, ref Entry entry)
    {
        if (!entry.HasCost)
        {
            CostReads++;
            entry.Cost = card.EnergyCost.GetResolved();
            entry.HasCost = true;
        }
        return entry.Cost;
    }

    /// <summary>
    /// The card's keys in <c>DynamicVars.Keys.Order(StringComparer.Ordinal)</c> order (dictionary keys are distinct, so any sort
    /// gives that order), the array shared with an earlier card of the same type with the same keys, and the variables of those
    /// keys with where they were read from (<see cref="SameVars"/>). Read where the game's comparison first reads the card's
    /// variables (its <c>DynamicVars</c> getter, which makes them on a first read, at the same comparison). A card whose keys are
    /// its type's shared ones (the same count, each one in them) allocates nothing.
    /// </summary>
    private static string[] Read(Scope scope, CardModel card, ref Entry entry)
    {
        DynamicVarSet set = card.DynamicVars;
        Type type = card.GetType();
        Dictionary<string, DynamicVar>? vars = _varsKnown && ReferenceEquals(_cardVars!(card), set) ? _setVars!(set) : null;
        string[]? keys = null;
        if (scope.SharedKeys.TryGetValue(type, out string[]? shared) && vars != null && SameKeys(vars, shared))
        {
            keys = shared;
        }
        else
        {
            keys = set.Keys.ToArray();
            Array.Sort(keys, StringComparer.Ordinal);
            if (shared == null)
            {
                scope.SharedKeys[type] = keys;
            }
            else if (shared.AsSpan().SequenceEqual(keys))
            {
                keys = shared;
            }
        }
        entry.Keys = keys;
        if (scope.Shared && vars != null)
        {
            int at = scope.Reserve(keys.Length);
            DynamicVar[] pool = scope.VarPool;
            for (int i = 0; i < keys.Length; i++)
            {
                pool[at + i] = set[keys[i]];
            }
            entry.VarsAt = at;
            entry.Set = set;
            entry.SetDictionary = vars;
            entry.SetVersion = _dictionaryVersion!(vars);
            entry.SetCount = vars.Count;
        }
        return keys;
    }

    /// <summary>
    /// The card's variables are, as far as can be seen at no cost, still those of the entry: its <c>_dynamicVars</c> is the set they
    /// were read from (no clone or downgrade replaced it), that set's dictionary is the same one, with the same version (no key
    /// added) and count (none removed, no clear). A variable replaced under an existing key bumps neither (.NET Core 3+): not seen
    /// here, see <see cref="_varsKnown"/> (the sort's premise).
    /// </summary>
    private static bool SameVars(CardModel card, ref Entry entry)
    {
        if (entry.VarsAt < 0 || !ReferenceEquals(_cardVars!(card), entry.Set))
        {
            return false;
        }
        Dictionary<string, DynamicVar>? vars = _setVars!(entry.Set!);
        return vars != null && ReferenceEquals(vars, entry.SetDictionary) && _dictionaryVersion!(vars) == entry.SetVersion && vars.Count == entry.SetCount;
    }

    /// <summary>
    /// The dictionary's keys are the sorted <paramref name="shared"/> ones: as many, each one in them (keys are distinct). Read from
    /// the dictionary itself (<c>DynamicVarSet.Keys</c> is <c>_vars.Keys</c>, fingerprinted), no allocation.
    /// </summary>
    private static bool SameKeys(Dictionary<string, DynamicVar> vars, string[] shared)
    {
        if (vars.Count != shared.Length)
        {
            return false;
        }
        foreach (string key in vars.Keys)
        {
            if (Array.BinarySearch(shared, key, StringComparer.Ordinal) < 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The game's answer for this pair (nothing in the sort can change what it reads: see <see cref="Open"/>).</summary>
    private static int Verify(CardModel card, AbstractModel? other, int answered)
    {
        int game;
        _verifying = true;
        // The game's comparison with fresh listener walks: not the outputs the sort's walks reuse (ListenerCache.PauseSort).
        bool paused = ListenerCache.PauseSort(true);
        try
        {
            game = card.CompareTo(other);
        }
        finally
        {
            ListenerCache.PauseSort(paused);
            _verifying = false;
        }
        Checks++;
        if (game != answered)
        {
            Mismatches++;
            Disable(new InvalidOperationException($"{card.Id} vs {(other as CardModel)?.Id}: answered {answered}, the game {game}"));
            return game;
        }
        return answered;
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, the game compares its cards: {e}");
    }
}
