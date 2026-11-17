using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// In co-op, every action ends with a checksum of the full combat state on every machine
/// (<c>NetFullCombatState.FromRun</c>): <c>CardState.From</c> calls <c>card.ToSerializable()</c> for every card in
/// combat, and with BaseLib each new SerializableCard and SerializableEnchantment gets its extended save data in a
/// ConditionalWeakTable (with Limitless, stacked enchantments also copy their saved inner cards). At 4,100 cards that is
/// about half of everything allocated while cards are played, and the collections it triggers freeze each action
/// ~0.1 s.
///
/// The call in <c>CardState.From</c> goes through <see cref="ToSerializable"/>: when the card's previous result (kept
/// per card) still holds what <c>ToSerializable</c> would build, compared field by field as
/// <see cref="LimitlessReserialize"/> does (ids, upgrade, floor, enchantment and amount, saved properties, BaseLib's
/// extended data), that object is used again. The snapshot only serializes and keeps it (to print it on a divergence),
/// never changes it: same bytes, same checksum. Anything else, another mod patching what the comparison mirrors, or a
/// BaseLib getter whose result isn't known: the game's call.
///
/// One reuse in <see cref="VerifyEvery"/> is serialized next to a freshly built one; a byte difference or an error turns
/// this off for the session (the fresh one is used).
///
/// The game also keeps every snapshot of the current combat in memory (its replay recording), and each one held a new
/// list of every card of every pile, grown by doubling onto the large object heap: at 20,000 cards the heap grew by
/// ~100 MB per turn and every collection got slower. <c>CombatPileState.From</c> now gives the pile's previous list
/// again while every card's state equals its entry (a list is never changed once built), and a new list sized at once
/// otherwise; cards keep their keyword list while their keywords stay the same.
///
/// At every action, for every card (~100 ms per checksum at 20,000 cards, several per card played, on every machine):
/// a memo per card holds what the last full comparison read (upgrade, floor, enchantment and amount, its saved list);
/// while they hold, the card's snapshot object is the same without comparing it again. Keywords and the energy cost
/// are read without allocating when no listener changes them. <c>CardState.Serialize</c>, which the checksum runs for
/// every card, appends the bits the game's method produced for the same state last time: the serializers that run there are
/// fingerprinted, another mod's patch on them (BaseLib's known postfixes and Unlimited's list-width transpiler aside) leaves bits to
/// the game, and bits written with other id widths or BaseLib saves (<see cref="BitsEpoch"/>) are written again.
///
/// A card that only got upgraded (some relics upgrade thousands of cards per action) gets a new snapshot object sharing
/// the previous one's parts instead of a whole new tree: those trees outlive several collections, which made them slow.
///
/// A pile that didn't change has the same state list and the same bits: while the pile's list version stands, no listener
/// changes keywords or costs, and its <see cref="PileLast.Writes"/> stands still (every write to what a card's state reads,
/// those fields' only writers checked at load, and the only users of the getters handing out their collections, counts on the pile the card was last read in: a card that changes pile
/// changes both list versions), the pile's whole state costs one comparison per checksum instead of a read of every card,
/// and its bits one copy. Cards with no memo (saved properties) are compared every time. One pile in
/// <see cref="PileVerifyEvery"/> is read for real: finding a change then means a write went unseen, and piles stop being
/// trusted for the session. A pile that did change gets its bits from its cards' kept bits, without a call per card. Another mod's
/// patch on a getter those kept states skip (ids, affliction and amount, enchantment and amount, upgrade, floor, cost) reads every card.
///
/// Checks: a card's kept bits, one in <see cref="VerifyEvery"/>, against the game's serialization of that state (<see cref="SameBits"/>);
/// a pile's bits, one in <see cref="PileVerifyEvery"/>, against the game's pile method, whose cards still write their kept bits: that one checks the
/// header and how the bits were put together, not the cards' bits.
/// </summary>
public static class SnapshotCardReuse
{
    public const string Name = "SnapshotCardReuse";

    /// <summary>One reuse in this many is checked byte for byte against a fresh <c>ToSerializable</c>.</summary>
    public const int VerifyEvery = 2048;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    public static long Reused { get; private set; }
    internal static bool ReleaseCombats { get; set; } = true;

    /// <summary>
    /// A reuse found wrong, or this turned off, while building or serializing a snapshot: <see cref="SnapshotVerifier"/> builds or
    /// serializes that snapshot again (the part found wrong is off by then), so the checksum that revealed it isn't sent with other
    /// states taken the same way.
    /// </summary>
    internal static long Detections { get; private set; }

    /// <summary>Entries of finished combats that let go of their card.</summary>
    public static long CombatCardsReleased { get; private set; }
    public static long Built { get; private set; }
    public static long Verified { get; private set; }
    public static long Mismatches { get; private set; }
    public static long PilesReused { get; private set; }
    public static long PilesBuilt { get; private set; }
    public static long BitsReused { get; private set; }
    public static long BitsCaptured { get; private set; }
    /// <summary>Card bits whose only change was the upgrade level (Apotheosis): those 8 bits written into the kept ones.</summary>
    public static long UpgradeBitsPatched { get; private set; }
    public static bool UpgradeBits { get; set; } = true;
#if BENCH
    /// <summary>A wrong level written into patched bits (bdperf upgradebits wrong): the checks must see it.</summary>
    public static bool UpgradeBitsWrongForTest { get; set; }
#endif
    public static long BitChecks { get; private set; }
    public static long LightBuilt { get; private set; }
    public static long PilesTrusted { get; private set; }
    public static long PileBitsReused { get; private set; }
    public static long PileBitsComposed { get; private set; }
    public static long PileMismatches { get; private set; }
    /// <summary>Cards of moved piles given their previous state without a read.</summary>
    public static long MovedReused { get; private set; }
    /// <summary>Piles read with written cards listed: only those read again.</summary>
    public static long DirtyPiles { get; private set; }
    /// <summary>Piles trusted or kept while pure cost or keyword listeners were active.</summary>
    public static long PilesWithHooks { get; private set; }

    /// <summary>Moved piles keep their unwritten cards' states (bdperf snapmoved on|off).</summary>
    public static bool MovedPiles { get; set; } = true;
    /// <summary>A pile with written cards reads only those, the others kept as moved piles keep them (bdperf dirtyreads on|off).</summary>
    public static bool DirtyReads { get; set; } = true;

    /// <summary>One trusted or moved pile in this many is read for real; one pile's kept or composed bits in this many are written by the game's pile method and compared.</summary>
    public const int PileVerifyEvery = 128;
    /// <summary>Below this many cards a pile isn't worth a copy of its bits.</summary>
    private const int MinCardsForPileBits = 32;
    /// <summary>
    /// A pile with more cards to compare every time than this (or than one card in <see cref="LiveShare"/> of a bigger pile) isn't
    /// trusted. A fixed 64 left every big pile of a huge deck read in full at each checksum: 20,000 cards for a few hundred live ones.
    /// </summary>
    private const int MaxLiveCards = 64;
    /// <summary>Written cards a pile lists between two reads; more, and its next read reads every card.</summary>
    private const int MaxDirtyCards = 256;
    private const int LiveShare = 16;

    private static bool _trustPiles;
    private static AccessTools.FieldRef<CardEnergyCost, CardModel>? _costCard;
    private static AccessTools.FieldRef<EnchantmentModel, CardModel?>? _enchantmentCard;
    private static AccessTools.FieldRef<AfflictionModel, CardModel?>? _afflictionCard;
    private static int _sincePileCheck, _sincePileBits;
    private static AccessTools.FieldRef<CardPile, List<CardModel>>? _pileCards;
    private static AccessTools.FieldRef<List<CardModel>, int>? _listVersion;
    private static MethodInfo? _pileSerialize;
    private static readonly ConditionalWeakTable<List<NetFullCombatState.CardState>, PileLast> _byList = new();

    /// <summary>v0.107.1 to v0.113.1 code mirrored by <see cref="PilePrefix"/> and <see cref="CardFrom"/> (same fingerprints on all four).</summary>
    private const string CardFromFingerprint = "96C28FA2E2A751FA";
    private const string PileFromFingerprint = "9112142DF335981C";
    private const string WriteListFingerprint = "7E6764DE8659A79F";

    private static MethodInfo? _toSerializable;
    private static MethodInfo? _cardFrom, _pileFrom;
    private static bool _replaced;
    private static CardKeyword[] _keywordValues = Array.Empty<CardKeyword>();
    /// <summary>No two names of <see cref="_keywordValues"/> share a value (true on v0.107.1 to v0.113.1): a kept list as long as the set is the game's.</summary>
    private static bool _keywordValuesDistinct;

    /// <summary>v0.107.1 to v0.113.1 code read without allocating by <see cref="CardFrom"/> (same fingerprints on all four).</summary>
    private const string KeywordsFingerprint = "0F51A275B1583E96";
    private const string LocalKeywordsFingerprint = "0580A115BC4486E8";
    private const string WithModifiersFingerprint = "60FE75B459B24CFC";
    /// <summary>What <see cref="AppendBits"/> relies on: least significant bit first, the bits above the position zeroed.</summary>
    private const string WriteBytesFingerprint = "3D28DF01F9252954";
    private const string ResizeFingerprint = "4D3C95B30E67A77F";

    private static bool _fastReads;
    private static MethodInfo? _keywordsMethod, _withModifiers, _cardSerialize;
    private static Func<CardModel, HashSet<CardKeyword>>? _localKeywords;
    private static AccessTools.FieldRef<CardEnergyCost, int>? _costBase;
    private static AccessTools.FieldRef<CardEnergyCost, List<LocalCostModifier>>? _costLocal;
    private static AccessTools.FieldRef<List<SerializableCard>, int>? _storedVersion;
    private static Action<PacketWriter, int>? _setBitPosition;
    private static Action<PacketWriter, int>? _reserveBits;

    /// <summary>A card's last snapshot object, and the BaseLib data the comparison found for it.</summary>
    private sealed class Last
    {
        public SerializableCard Saved = null!;
        public LimitlessReserialize.StoredData Data = new();
        /// <summary>The card's last keyword list, in enum order.</summary>
        public List<CardKeyword>? Keywords;
        public CardModel Card = null!;

        /// <summary>What <see cref="Saved"/> was read from (see <see cref="Holds"/>), valid at this stamp; -1: no memo.</summary>
        public long MemoStamp = -1;
        public int Upgrade;
        public int? Floor;
        /// <summary>The card's type saves no property.</summary>
        public bool PlainCard;
        public EnchantmentModel? Enchantment;
        public int Amount;
        public Func<object, List<SerializableCard>?>? ListGetter;
        public List<SerializableCard>? List;
        public int ListVersion;

        /// <summary><see cref="Saved"/> registered in <see cref="_bySaved"/>.</summary>
        public SerializableCard? Registered;

        /// <summary>The pile this card was last read in.</summary>
        public PileLast? In;
        /// <summary>Written since its last read, listed in <see cref="In"/>'s <see cref="PileLast.Dirty"/>: read again, not reused.</summary>
        public bool Dirty;

        /// <summary>The bits <c>CardState.Serialize</c> wrote for this state, from the first bit of <see cref="Bits"/>.</summary>
        public byte[]? Bits;
        public int BitCount;
        /// <summary>The <see cref="BitsEpoch"/> <see cref="Bits"/> were written in.</summary>
        public int BitsEpoch;
        public SerializableCard? BitsCard;
        public ModelId? BitsAffliction;
        public int BitsAfflictionCount;
        public int? BitsEnergyCost;
        public List<CardKeyword>? BitsKeywords;
        /// <summary>The snapshot object <see cref="Saved"/> was made from by <see cref="Upgraded"/> (only its upgrade level differs).</summary>
        public SerializableCard? UpgradedFrom;
    }

    /// <summary>A pile's last snapshot list (never changed once built) and each entry's <see cref="Last"/>.</summary>
    private sealed class PileLast
    {
        public List<NetFullCombatState.CardState>? Cards;
        public Last?[] Lasts = Array.Empty<Last?>();
        public Last?[] Spare = Array.Empty<Last?>();
        public int Count;

        /// <summary>Writes to what the state of this pile's cards reads (<see cref="Last.In"/>).</summary>
        public long Writes;

        /// <summary>
        /// The cards written since the last read (each marked <see cref="Last.Dirty"/>), up to <see cref="MaxDirtyCards"/>: a read
        /// then reads those again and keeps the others' states. More than that: <see cref="DirtyOverflow"/>, every card read.
        /// </summary>
        public readonly List<Last> Dirty = new();
        public bool DirtyOverflow;

        /// <summary>What the last full read saw: trusted until one of these moves. -1: not trustable.</summary>
        public long WritesAt = -1;
        /// <summary>The cost and keyword listeners of that read (see <see cref="Reads.Listeners"/>).</summary>
        public HookListener[]? ListenersAt;
        public long StampAt = -1;
        public int ListVersion;

        /// <summary>Entries of the cards the memo doesn't cover: compared even when the pile is trusted.</summary>
        public int[] Live = new int[8];
        public int LiveCount;

        /// <summary>The bits of <see cref="Cards"/>'s serialization, from the first bit, zeros after the last.</summary>
        public byte[]? Bits;
        public int BitCount;
        public List<NetFullCombatState.CardState>? BitsFor;
        /// <summary>The <see cref="BitsEpoch"/> <see cref="Bits"/> were written in.</summary>
        public int BitsEpoch;

        /// <summary>When <see cref="Compose"/> made <see cref="Bits"/>: where each card of <see cref="BitsFor"/> starts in them, then the end.</summary>
        public int[]? Offsets;
        /// <summary>The other buffer: a compose reads the previous bits while writing the new ones.</summary>
        public byte[]? SpareBits;
        public int[]? SpareOffsets;

        /// <summary>Nothing of a finished combat is kept: the next read of this pile, if any, starts over.</summary>
        public void Release()
        {
            ReleaseEntries(Lasts);
            ReleaseEntries(Spare);
            Lasts = Array.Empty<Last?>();
            Spare = Array.Empty<Last?>();
            Count = 0;
            LiveCount = 0;
            Cards = null;
            BitsFor = null;
            Bits = null;
            BitCount = 0;
            Offsets = null;
            SpareBits = null;
            SpareOffsets = null;
            WritesAt = -1;
            ListenersAt = null;
            Dirty.Clear();
            DirtyOverflow = false;
        }

        private static void ReleaseEntries(Last?[] entries)
        {
            foreach (Last? entry in entries)
            {
                if (entry is { Card: not null })
                {
                    ReleaseEntry(entry);
                }
            }
        }

        public static void ReleaseEntry(Last entry)
        {
            CombatCardsReleased++;
            entry.Card = null!;
            entry.Enchantment = null;
            entry.In = null;
            entry.MemoStamp = -1;
            entry.Dirty = false;
        }

        /// <summary>
        /// The game's bits for this pile with no card (its type, then a count of 0 on its last <see cref="CountBits"/> bits), written in
        /// <see cref="HeaderEpoch"/>: a pile's header with any count is these with the count written in.
        /// </summary>
        public byte[]? Header;
        public int HeaderBits;
        public int CountBits;
        public int HeaderEpoch = -1;
    }

    /// <summary>From a snapshot object to its <see cref="Last"/>: all <c>CardState.Serialize</c> gets is the state.</summary>
    private static readonly ConditionalWeakTable<SerializableCard, Last> _bySaved = new();

    private static readonly ConditionalWeakTable<CardPile, PileLast> _piles = new();

    private static readonly ConditionalWeakTable<CardModel, Last> _last = new();

    public static void Apply(Harmony harmony)
    {
        if (!LimitlessReserialize.ComparisonReady)
        {
            throw new PatchSkippedException("needs LimitlessReserialize's comparison (BaseLib and Limitless; applies with it if Limitless loads later)");
        }
        _toSerializable = Reflect.Method(typeof(CardModel), nameof(CardModel.ToSerializable), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.ToSerializable));
        MethodInfo from = Reflect.Method(typeof(NetFullCombatState.CardState), nameof(NetFullCombatState.CardState.From), new[] { typeof(CardModel) })
            ?? throw new MissingMethodException("NetFullCombatState.CardState", "From");
        // Only that call is replaced: the rest of From (affliction, keywords, cost) stays the game's whatever it becomes.
        int calls = 0;
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(from))
        {
            if (instruction.Calls(_toSerializable))
            {
                calls++;
            }
        }
        if (calls != 1)
        {
            throw new PatchSkippedException($"CardState.From calls ToSerializable {calls} times (expected once): left vanilla");
        }
        PatchGuard.Patch(harmony, from, transpiler: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(Transpiler)));
        if (!_replaced)
        {
            throw new PatchSkippedException("CardState.From: ToSerializable call not replaced");
        }
        Applied = true;
        bool pilesApplied = false;
        try
        {
            _pileFrom = Reflect.Method(typeof(NetFullCombatState.CombatPileState), nameof(NetFullCombatState.CombatPileState.From), new[] { typeof(CardPile) })
                ?? throw new MissingMethodException("NetFullCombatState.CombatPileState", "From");
            PatchSafety.RequirePartResolvable(typeof(SnapshotCardReuse), "piles");
            PatchSafety.RequireFingerprint(from, CardFromFingerprint);
            PatchSafety.RequireFingerprint(_pileFrom, PileFromFingerprint);
            _cardFrom = from;
            _keywordValues = Enum.GetValues<CardKeyword>();
            _keywordValuesDistinct = _keywordValues.Distinct().Count() == _keywordValues.Length;
            _storedVersion = Reflect.FieldRefAccess<List<SerializableCard>, int>("_version");
            ApplyFastReads();
            // The release first: a pile prefix in place is then the pile part applied (the flag below, what the preflight
            // shows); a release without it finds no pile entries. ApplyBits and ApplyTrust catch their own errors.
            PatchGuard.Patch(harmony, Reflect.Method(typeof(Player), nameof(Player.AfterCombatEnd)), postfix: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(ReleaseCombat)));
            PatchGuard.Patch(harmony, _pileFrom, prefix: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(PilePrefix)));
            pilesApplied = true;
            ApplyBits(harmony);
            ApplyTrust(harmony);
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: pile lists left vanilla: {e.Message}");
        }
        WatchForPreflight(pilesApplied);
    }

    /// <summary>
    /// What this class's runtime checks ask about, for the back-off preflight (display only, never throws): the pile and card
    /// snapshot methods, the fast reads' methods and the getters kept states skip, the serializers kept bits stand for (BaseLib's
    /// and Unlimited's allowed writes read at each preflight), LimitlessReserialize's checks this relies on, and the pure
    /// listener overrides of the listener types met so far (who is in a combat isn't known at the menu: those of later
    /// combats show when checked). Notes: other mods' saved data this keeps no memo for.
    /// </summary>
    private static void WatchForPreflight(bool pilesApplied)
    {
        // The pile part's method only once that part applied: left vanilla for another reason, a patch there changes nothing.
        if (pilesApplied)
        {
            PatchSafety.Watch(Name, _pileFrom);
        }
        PatchSafety.Watch(Name, _cardFrom);
        if (_fastReads)
        {
            PatchSafety.Watch(Name, new MethodBase?[] { _withModifiers, _keywordsMethod, _keywordsGetter });
            // The hooks those reads skip (HookFilter's methods, known once it applied).
            PatchSafety.WatchAll(Name, () => new MethodBase?[] { HookFilter.HookMethodOf(HookFilter.Keywords), HookFilter.HookMethodOf(HookFilter.EnergyCost) }
                .Where(m => m != null).Select(m => m!).ToArray());
        }
        PatchSafety.Watch(Name, _skippedGetters);
        Func<MethodInfo[]> allowed = () => RelicStateReuse.UnlimitedTranspilerMethod is { } unlimited ? _baseLibWrites.Append(unlimited).ToArray() : _baseLibWrites;
        PatchSafety.WatchAll(Name, () => (_cardSerializers ?? Array.Empty<MethodBase>()).Concat(_pileSerializers ?? Array.Empty<MethodBase>()).ToArray(), allowed);
        PatchSafety.WatchAll(Name, () => _pureTypes.Values.ToArray().Where(methods => methods != null).SelectMany(methods => methods!).ToArray());
        LimitlessReserialize.WatchFor(Name);
        PatchSafety.WatchNote(Name, () => LimitlessReserialize.NoteSavedData(Name, "those cards' state is built again as the game does at every check"));
        PatchSafety.WatchNote(Name, NoteModListeners);
    }

    /// <summary>
    /// The note (back-off preflight) for other mods' listener types met so far whose cost or keyword hooks aren't known to only
    /// read (<see cref="PureOverrides"/>): card costs and keywords are then read as the game does in those combats.
    /// </summary>
    private static void NoteModListeners()
    {
        string[] mods = PatchSafety.ModNamesOf(_pureTypes.ToArray().Where(entry => entry.Value == null).Select(entry => entry.Key.Assembly));
        if (mods.Length == 0)
        {
            PatchSafety.ClearNote(Name, "mod_listeners");
            return;
        }
        PatchSafety.Note(Name, "mod_listeners", "other mods' cards, relics or powers whose cost or keyword hooks aren't known to only read: "
            + "card costs and keywords are read as the game does in combats with them (seen in the combats so far)", mods);
    }

    private static void ApplyFastReads()
    {
        try
        {
            _keywordsMethod = Reflect.Method(typeof(CardModel), nameof(CardModel.GetKeywordsWithSources)) ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.GetKeywordsWithSources));
            MethodInfo local = Reflect.PropertyGetter(typeof(CardModel), "LocalKeywords") ?? throw new MissingMethodException(nameof(CardModel), "LocalKeywords");
            _withModifiers = Reflect.Method(typeof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers)) ?? throw new MissingMethodException(nameof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers));
            PatchSafety.RequireFingerprint(_keywordsMethod, KeywordsFingerprint);
            PatchSafety.RequireFingerprint(local, LocalKeywordsFingerprint);
            PatchSafety.RequireFingerprint(_withModifiers, WithModifiersFingerprint);
            _localKeywords = AccessTools.MethodDelegate<Func<CardModel, HashSet<CardKeyword>>>(local);
            _costBase = Reflect.FieldRefAccess<CardEnergyCost, int>("_base");
            _costLocal = Reflect.FieldRefAccess<CardEnergyCost, List<LocalCostModifier>>("_localModifiers");
            _fastReads = true;
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: keywords and costs read through the game's own calls: {e.Message}");
        }
    }

    /// <summary>The writes that can change a card's snapshot state without changing its pile: each counts on the card's pile.</summary>
    private static void ApplyTrust(Harmony harmony)
    {
        try
        {
            if (!_fastReads)
            {
                return;
            }
            if (!InlineGuard.Available)
            {
                throw new PatchSkippedException("patched methods can't be kept from being inlined on this runtime: a write could go unseen");
            }
            Type multi = Reflect.TypeByName(LimitlessReserialize.MultiEnchantmentType) ?? throw new TypeLoadException(LimitlessReserialize.MultiEnchantmentType);
            PatchSafety.RequireFieldWriters(multi, "<SerializableEnchantmentsOnCards>k__BackingField", ".ctor", "set_SerializableEnchantmentsOnCards");
            PatchSafety.RequireFieldWriters(typeof(EnchantmentModel), "_amount", "set_Amount");
            PatchSafety.RequireFieldWriters(typeof(AfflictionModel), "_amount", "set_Amount");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_currentUpgradeLevel", "set_CurrentUpgradeLevel");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_floorAddedToDeck", "set_FloorAddedToDeck");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "<Enchantment>k__BackingField", "set_Enchantment");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "<Affliction>k__BackingField", "set_Affliction");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_keywords", "DeepCloneFields", "DowngradeInternal", "get_LocalKeywords");
            // Both getters hand out the collection itself: code changing it through them never touches the field, so the field check above
            // can't see it. Their users are the known ones (v0.107.1 to v0.113.1, Limitless 1.2.1): the writers are AddKeyword, RemoveKeyword
            // (counted below) and the setter, the others only read.
            RequireCallers(typeof(CardModel), Reflect.PropertyGetter(typeof(CardModel), "LocalKeywords"), "GetKeywordsWithSources", "AddKeyword", "RemoveKeyword");
            RequireCallers(multi, Reflect.PropertyGetter(multi, "SerializableEnchantmentsOnCards"), "get_EnchantmentsOnCards", "DeepCloneFields");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_energyCost", "DeepCloneFields", "MockSetEnergyCost", "get_EnergyCost");
            string[] costWriters = { ".ctor", "AddThisCombat", "AddThisTurn", "AddThisTurnOrUntilPlayed", "AddUntilPlayed", "AfterCardPlayedCleanup", "Clone", "EndOfTurnCleanup", "SetThisCombat", "SetThisTurn", "SetThisTurnOrUntilPlayed", "SetUntilPlayed" };
            PatchSafety.RequireFieldWriters(typeof(CardEnergyCost), "_localModifiers", costWriters);
            PatchSafety.RequireFieldWriters(typeof(CardEnergyCost), "_base", ".ctor", "Clone", "GetWithModifiers", "ResetForDowngrade", "SetCustomBaseCost", "UpgradeBy");
            // A cost modifier's amount has a public setter, and changing it in place touches no field of the cost: UpgradeBy (counted) is
            // the only code doing it. Modifiers are reached only through the private _localModifiers, whose users (readers too: a new one
            // could hand a modifier out) are the known ones (v0.107.1 to v0.113.1).
            string[] costUsers = costWriters.Concat(new[] { "GetWithModifiers", "get_HasLocalModifiers", "UpgradeBy" }).ToArray();
            string[] newCostUsers = MethodFingerprint.FieldUsers(typeof(CardEnergyCost), "_localModifiers").Where(user => !costUsers.Contains(user)).ToArray();
            if (newCostUsers.Length > 0)
            {
                throw new PatchSkippedException($"CardEnergyCost._localModifiers is now also used by {string.Join(", ", newCostUsers)} (update?): left vanilla");
            }
            MethodInfo? modifierAmount = Reflect.PropertySetter(typeof(LocalCostModifier), nameof(LocalCostModifier.Amount));
            RequireCallers(typeof(CardEnergyCost), modifierAmount, "UpgradeBy");
            RequireCallers(typeof(LocalCostModifier), modifierAmount, ".ctor");
            PatchSafety.RequireFieldWriters(typeof(LocalCostModifier), "<Amount>k__BackingField", "set_Amount");
            // The pure listeners read the card's type: CardModel's own getter returns what the constructor set (see Note for overrides).
            PatchSafety.RequireFieldWriters(typeof(CardModel), "<Type>k__BackingField", ".ctor");
            MethodInfo typeGetter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Type)) ?? throw new MissingMethodException(nameof(CardModel), "get_Type");
            // The auto-property's getter (returns that field), v0.107.1 to v0.113.1.
            PatchSafety.RequireFingerprint(typeGetter, TypeGetterFingerprint);
            _typeGetter = typeGetter;
            // What a trusted or moved pile's kept states skip reading: another mod's patch on one of them (a postfix changing the value)
            // reads every card as the game does (GettersPatched).
            _skippedGetters = new MethodBase[]
            {
                Getter(typeof(AbstractModel), nameof(AbstractModel.Id)),
                Getter(typeof(CardModel), nameof(CardModel.Affliction)),
                Getter(typeof(CardModel), nameof(CardModel.Enchantment)),
                Getter(typeof(CardModel), nameof(CardModel.CurrentUpgradeLevel)),
                Getter(typeof(CardModel), nameof(CardModel.FloorAddedToDeck)),
                Getter(typeof(CardModel), nameof(CardModel.EnergyCost)),
                Getter(typeof(AfflictionModel), nameof(AfflictionModel.Amount)),
                Getter(typeof(EnchantmentModel), nameof(EnchantmentModel.Amount)),
                Getter(typeof(CardEnergyCost), nameof(CardEnergyCost.Canonical)),
                Getter(typeof(CardEnergyCost), nameof(CardEnergyCost.CostsX)),
                // What the pure cost and keyword listeners read of a card (PureHooks).
                Getter(typeof(CardModel), nameof(CardModel.Type)),
                Getter(typeof(CardModel), nameof(CardModel.Owner)),
                Getter(typeof(CardModel), nameof(CardModel.Pile)),
            };
            _pileCards = Reflect.FieldRefAccess<CardPile, List<CardModel>>("_cards");
            _listVersion = Reflect.FieldRefAccess<List<CardModel>, int>("_version");
            _costCard = Reflect.FieldRefAccess<CardEnergyCost, CardModel>("_card");
            _enchantmentCard = Reflect.FieldRefAccess<EnchantmentModel, CardModel?>("_card");
            _afflictionCard = Reflect.FieldRefAccess<AfflictionModel, CardModel?>("_card");
            void Mark(Type type, string method, string prefix)
            {
#if BENCH
                // Harmony's __originalMethod costs a method lookup per call (~2 µs): only when writes are counted per method.
                if (CountWritesByMethod)
                {
                    prefix += "Counted";
                }
#endif
                PatchGuard.Patch(harmony, Reflect.Method(type, method) ?? throw new MissingMethodException(type.Name, method),
                    prefix: new HarmonyMethod(typeof(SnapshotCardReuse), prefix));
            }
            foreach (string writer in costWriters.Concat(new[] { "ResetForDowngrade", "SetCustomBaseCost", "UpgradeBy" }))
            {
                if (writer is not ("Clone" or ".ctor"))
                {
                    Mark(typeof(CardEnergyCost), writer, nameof(CostWrite));
                }
            }
            Mark(typeof(EnchantmentModel), "set_Amount", nameof(EnchantmentWrite));
            Mark(typeof(AfflictionModel), "set_Amount", nameof(AfflictionWrite));
            foreach (string writer in new[] { "set_CurrentUpgradeLevel", "set_FloorAddedToDeck", "set_Enchantment", "set_Affliction", "AddKeyword", "RemoveKeyword", "DowngradeInternal", "MockSetEnergyCost" })
            {
                Mark(typeof(CardModel), writer, nameof(CardWrite));
            }
            Mark(multi, "set_SerializableEnchantmentsOnCards", nameof(EnchantmentWrite));
            Mark(multi, "AddEnchantment", nameof(EnchantmentWrite));
            _trustPiles = true;
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: unchanged piles read every time: {e.Message}");
        }
    }

    /// <summary>
    /// Throws <see cref="PatchSkippedException"/> when a method of <paramref name="type"/> (or of its nested types: lambdas, state
    /// machines) other than <paramref name="known"/> calls <paramref name="getter"/>: a private getter returning a tracked collection,
    /// or a setter changing a tracked object in place. A new caller could change it unseen by the counted writers and by
    /// <see cref="PatchSafety.RequireFieldWriters"/>.
    /// </summary>
    private static void RequireCallers(Type type, MethodInfo? getter, params string[] known)
    {
        if (getter == null)
        {
            throw new MissingMethodException(type.Name, "a tracked member's accessor");
        }
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var unknown = new List<string>();
        void Scan(Type t, string prefix)
        {
            foreach (MethodBase m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                if (m.Equals(getter) || (prefix.Length == 0 && known.Contains(m.Name)))
                {
                    continue;
                }
                if (MethodFingerprint.UsedMembers(m).Any(u => u is MethodBase used && used.Equals(getter)))
                {
                    unknown.Add(prefix + m.Name);
                }
            }
            foreach (Type nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                Scan(nested, prefix + nested.Name + "+");
            }
        }
        Scan(type, "");
        if (unknown.Count > 0)
        {
            throw new PatchSkippedException($"{type.Name}.{getter.Name} is now also used by {string.Join(", ", unknown)} (update?): left vanilla");
        }
    }

    private static MethodBase Getter(Type type, string property) =>
        Reflect.PropertyGetter(type, property) ?? throw new MissingMethodException(type.Name, "get_" + property);

    private static MethodBase[]? _skippedGetters;

    /// <summary><c>CardModel.Type</c>'s getter (set when piles may be trusted), and per card type whether it keeps that getter.</summary>
    private static MethodInfo? _typeGetter;
    private const string TypeGetterFingerprint = "B3671DD3787AE845";
    private static readonly Dictionary<Type, bool> _keepsTypeGetter = new();

    /// <summary>
    /// Whether this card type answers <c>Type</c> with CardModel's own getter (the constructor's value, see ApplyTrust). An override may
    /// answer differently over time, unseen by the counted writers, while the pure listeners read it: such cards get no memo (they are
    /// read at every snapshot, never kept by a trusted or moved pile).
    /// </summary>
    [PatchPart("piles")]
    private static bool KeepsTypeGetter(Type type)
    {
        if (_typeGetter == null)
        {
            return false;
        }
        if (_keepsTypeGetter.TryGetValue(type, out bool keeps))
        {
            return keeps;
        }
        keeps = true;
        try
        {
            for (Type? t = type; t != null && t != typeof(CardModel) && keeps; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    // An override (same slot), or a method of that name hiding it: either way not CardModel's getter alone.
                    if (m.Name == _typeGetter.Name && m.GetParameters().Length == 0)
                    {
                        keeps = false;
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            keeps = false;
        }
        _keepsTypeGetter[type] = keeps;
        return keeps;
    }
    private static long _gettersCheckedAt = long.MinValue / 2; // So `now - at` can't overflow: the first check runs.
    private static bool _gettersPatched;

    /// <summary>Another mod patches a getter whose value kept states stand for (<see cref="_skippedGetters"/>). Rechecked every 2 s.</summary>
    private static bool GettersPatched()
    {
        if (_skippedGetters == null)
        {
            return false; // Piles aren't trusted then.
        }
        long now = Environment.TickCount64;
        if (now - _gettersCheckedAt >= 2000)
        {
            // No tolerance: getters whose calls trusted piles skip (a patch there would miss them or answer otherwise).
            _gettersPatched = _skippedGetters.Any(getter => PatchSafety.IsPatchedByOthers(getter));
            _gettersCheckedAt = now;
        }
        return _gettersPatched;
    }

    /// <summary>A card no pile read yet is in no trusted pile: nothing to count.</summary>
    private static void Wrote(CardModel? card)
    {
        if (card != null && _last.TryGetValue(card, out Last? entry) && entry.In is { } pile)
        {
            pile.Writes++;
            if (!entry.Dirty)
            {
                if (pile.Dirty.Count < MaxDirtyCards)
                {
                    entry.Dirty = true;
                    pile.Dirty.Add(entry);
                }
                else
                {
                    pile.DirtyOverflow = true;
                }
            }
        }
    }

#if BENCH
    /// <summary>Writes counted per kind, and the method that made them (which ones keep piles from being trusted).</summary>

    /// <summary>Why piles were read rather than trusted.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> WhyRead = new();

    /// <summary>Per method, its name built only when printed: this runs at every write of every card (Apotheosis: 10,000 at once).</summary>
    private static readonly Dictionary<(System.Reflection.MethodBase, PileType?), long> _writes = new();

    private static void CountWrite(System.Reflection.MethodBase method, CardModel? card)
    {
        lock (_writes)
        {
            (System.Reflection.MethodBase, PileType?) key = (method, card?.Pile?.Type);
            _writes[key] = _writes.GetValueOrDefault(key) + 1;
        }
    }

    public static IEnumerable<KeyValuePair<string, long>> WritesByMethod
    {
        get
        {
            lock (_writes)
            {
                return _writes.GroupBy(p => p.Key.Item1.DeclaringType?.Name + "." + p.Key.Item1.Name + " (" + (p.Key.Item2?.ToString() ?? "no pile") + ")").Select(g => KeyValuePair.Create(g.Key, g.Sum(p => p.Value))).ToList();
            }
        }
    }

    /// <summary>
    /// Writes counted per method (the WRITES line) only with <c>--bdwrites</c> on the command line (or <c>BD_WRITES=1</c> in the environment): Harmony looks the method up at
    /// every call for that, which made an Apotheosis at 10,000 cards ~20 ms slower in Debug builds than in Release ones.
    /// </summary>
    private static readonly bool CountWritesByMethod = Godot.OS.GetCmdlineArgs().Concat(Godot.OS.GetCmdlineUserArgs()).Contains("--bdwrites")
        || Environment.GetEnvironmentVariable("BD_WRITES") == "1";

    private static void CardWriteCounted(CardModel __instance, System.Reflection.MethodBase __originalMethod)
    {
        CountWrite(__originalMethod, __instance);
        Wrote(__instance);
    }

    private static void CostWriteCounted(CardEnergyCost __instance, System.Reflection.MethodBase __originalMethod)
    {
        CountWrite(__originalMethod, _costCard!(__instance));
        Wrote(_costCard!(__instance));
    }

    private static void EnchantmentWriteCounted(EnchantmentModel __instance, System.Reflection.MethodBase __originalMethod)
    {
        CountWrite(__originalMethod, _enchantmentCard!(__instance));
        Wrote(_enchantmentCard!(__instance));
    }

    private static void AfflictionWriteCounted(AfflictionModel __instance, System.Reflection.MethodBase __originalMethod)
    {
        CountWrite(__originalMethod, _afflictionCard!(__instance));
        Wrote(_afflictionCard!(__instance));
    }
#endif
    private static void CardWrite(CardModel __instance) => Wrote(__instance);
#if BENCH
    /// <summary>Calls of the cost writers' prefix (bdperf costwrites): does a given path reach it.</summary>
    public static long CostWriteCalls;
    private static void CostWrite(CardEnergyCost __instance)
    {
        CostWriteCalls++;
        Wrote(_costCard!(__instance));
    }
#else
    private static void CostWrite(CardEnergyCost __instance) => Wrote(_costCard!(__instance));
#endif
    private static void EnchantmentWrite(EnchantmentModel __instance) => Wrote(_enchantmentCard!(__instance));
    private static void AfflictionWrite(AfflictionModel __instance) => Wrote(_afflictionCard!(__instance));

    private static void ApplyBits(Harmony harmony)
    {
        try
        {
            PatchSafety.RequirePartResolvable(typeof(SnapshotCardReuse), "bits");
            _cardSerialize = Reflect.Method(typeof(NetFullCombatState.CardState), nameof(NetFullCombatState.CardState.Serialize), new[] { typeof(PacketWriter) })
                ?? throw new MissingMethodException("NetFullCombatState.CardState", "Serialize");
            Type util = Reflect.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Serialization.BitSerializationUtil") ?? throw new TypeLoadException("BitSerializationUtil");
            PatchSafety.RequireFingerprint(Reflect.Method(util, "WriteBytes") ?? throw new MissingMethodException("BitSerializationUtil", "WriteBytes"), WriteBytesFingerprint);
            MethodInfo resize = Reflect.Method(typeof(PacketWriter), "ResizeBufferIfNecessary") ?? throw new MissingMethodException(nameof(PacketWriter), "ResizeBufferIfNecessary");
            PatchSafety.RequireFingerprint(resize, ResizeFingerprint);
            _reserveBits = AccessTools.MethodDelegate<Action<PacketWriter, int>>(resize);
            _setBitPosition = AccessTools.MethodDelegate<Action<PacketWriter, int>>(Reflect.PropertySetter(typeof(PacketWriter), nameof(PacketWriter.BitPosition))
                ?? throw new MissingMethodException(nameof(PacketWriter), "set_BitPosition"));
            ResolveSerializers();
            PatchGuard.Patch(harmony, _cardSerialize, prefix: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(CardSerializePrefix)));
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: card bits not reused: {e.Message}");
            return;
        }
        try
        {
            ApplyUpgradeBits();
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: an upgraded card's bits written in full: {e.Message}");
        }
        try
        {
            _pileSerialize = Reflect.Method(typeof(NetFullCombatState.CombatPileState), nameof(NetFullCombatState.CombatPileState.Serialize), new[] { typeof(PacketWriter) })
                ?? throw new MissingMethodException("NetFullCombatState.CombatPileState", "Serialize");
            // From the caller: once its callers reach tier 1 the JIT inlines this small method, and a patch on it no longer runs.
            MethodInfo writeList = AccessTools.FirstMethod(typeof(PacketWriter), m => m.Name == nameof(PacketWriter.WriteList) && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                ?? throw new MissingMethodException(nameof(PacketWriter), nameof(PacketWriter.WriteList));
            PatchSafety.RequireFingerprint(writeList, WriteListFingerprint);
            // A pile's bits are its type (WriteInt, 32 bits), then WriteList(cards): the count, then each card's bits. Compose builds them that way.
            PatchSafety.RequireFingerprint(_pileSerialize, CombatPileSerializeFingerprint);
            PatchSafety.RequireFingerprint(Reflect.Method(typeof(PacketWriter), nameof(PacketWriter.WriteInt))
                ?? throw new MissingMethodException(nameof(PacketWriter), nameof(PacketWriter.WriteInt)), WriteIntFingerprint);
            // A closed instantiation can be patched apart from the definition (Unlimited does it for some list types): WritePiles stands
            // for one, Compose for the other.
            _pileSerializers = new MethodBase[]
            {
                _pileSerialize,
                writeList.MakeGenericMethod(typeof(NetFullCombatState.CardState)),
                writeList.MakeGenericMethod(typeof(NetFullCombatState.CombatPileState)),
            };
            MethodInfo playerSerialize = Reflect.Method(typeof(NetFullCombatState.PlayerState), nameof(NetFullCombatState.PlayerState.Serialize), new[] { typeof(PacketWriter) })
                ?? throw new MissingMethodException("NetFullCombatState.PlayerState", "Serialize");
            // One call writes the piles (v0.107.1 to v0.113.1): another layout would leave pile bits applied but never used.
            int pileLists = PatchProcessor.GetOriginalInstructions(playerSerialize).Count(instruction => IsPilesWrite(instruction));
            if (pileLists != 1)
            {
                throw new PatchSkippedException($"PlayerState.Serialize writes the piles {pileLists} times (expected once)");
            }
            PatchGuard.Patch(harmony, playerSerialize, transpiler: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(PlayerSerializeTranspiler)));
            // Another mod's transpiler that ran first may have changed that call. The transpiler stays (Harmony would rebuild the
            // method for its removal): the calls it made write the game's way unless this passed (WritePiles, PileBitsActive).
            if (_pilesReplaced != 1)
            {
                throw new PatchSkippedException($"PlayerState.Serialize: {_pilesReplaced} pile list writes replaced (expected one)");
            }
            _pileBitsChecked = true;
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: pile bits not reused, each card's still are: {e.Message}");
        }
    }

    // --- What kept bits rely on: the serializers below write a function of the state alone, in the current id widths ---

    /// <summary><c>CombatPileState.Serialize</c>, v0.107.1 to v0.113.1: <c>WriteInt((int)pileType)</c>, then <c>WriteList(cards)</c>.</summary>
    private const string CombatPileSerializeFingerprint = "78B8142AB5A76C0D";
    /// <summary><c>SerializableEnchantment.Serialize</c>, v0.107.1 to v0.113.1: id, amount, properties.</summary>
    private const string SerializableEnchantmentSerializeFingerprint = "174DFDEC4EA23729";
    /// <summary><c>SavedProperties.Serialize</c>, v0.107.1 to v0.113.1: each list of its fields, names through WritePropertyName.</summary>
    private const string SavedPropertiesSerializeFingerprint = "BAE840068F670DB0";
    /// <summary><c>SavedProperties.WritePropertyName</c>: the name's net id on the cache's width (v0.107.1 SavedPropertiesTypeCache, v0.111+ ModelIdSerializationCache).</summary>
    private static readonly string[] WritePropertyNameFingerprints = { "A8B4ED2BFA4D5515", "056C6A8F3E078C44" };
    /// <summary>
    /// BaseLib 3.4.8's postfixes on <c>SerializableCard.Serialize</c> and <c>SerializableEnchantment.Serialize</c>: the object's extended data
    /// through <c>ExtendedSaveHandlers.Write</c> (each registered save's value, nothing while gameplay mods can't run). That data is set when
    /// the object is made; registering a save or that switch changes <see cref="BitsEpoch"/>.
    /// </summary>
    private const string BaseLibCardWrite = "BaseLib.Patches.Saves.ExtendedSavePatches+SerializeExtendedCardData:WriteExtended";
    private const string BaseLibCardWriteFingerprint = "038E012043C50ECB";
    private const string BaseLibEnchantmentWrite = "BaseLib.Patches.Saves.ExtendedSavePatches+SerializeExtendedEnchantmentData:WriteExtended";
    private const string BaseLibEnchantmentWriteFingerprint = "739429D3B854A2B3";
    private const string BaseLibHandlersType = "BaseLib.Patches.Saves.ExtendedSaveHandlers`2";
    private const string BaseLibHandlersWriteFingerprint = "D1DE163AF605BE38";

    /// <summary>Card bits: <c>CardState.Serialize</c> and what it calls for the card. Pile bits add <see cref="_pileSerializers"/>.</summary>
    private static MethodBase[]? _cardSerializers;
    private static MethodBase[]? _pileSerializers;
    /// <summary>BaseLib's two postfixes there, once their code is the known one.</summary>
    private static MethodInfo[] _baseLibWrites = Array.Empty<MethodInfo>();
    private static Func<uint>? _idHash;
    private static Func<int>? _entryIdWidth, _categoryIdWidth, _propertyIdWidth;
    private static Func<bool>? _baseLibGameplay;
    private static (uint, int, int, int, int, int, bool) _bitsKey;
    private static int _bitsEpoch;

    private static void ResolveSerializers()
    {
        MethodInfo serializableCard = Reflect.Method(typeof(SerializableCard), nameof(SerializableCard.Serialize))
            ?? throw new MissingMethodException(nameof(SerializableCard), nameof(SerializableCard.Serialize));
        MethodInfo serializableEnchantment = Reflect.Method(typeof(SerializableEnchantment), nameof(SerializableEnchantment.Serialize))
            ?? throw new MissingMethodException(nameof(SerializableEnchantment), nameof(SerializableEnchantment.Serialize));
        MethodInfo savedProperties = Reflect.Method(typeof(SavedProperties), nameof(SavedProperties.Serialize))
            ?? throw new MissingMethodException(nameof(SavedProperties), nameof(SavedProperties.Serialize));
        MethodInfo propertyName = Reflect.Method(typeof(SavedProperties), "WritePropertyName")
            ?? throw new MissingMethodException(nameof(SavedProperties), "WritePropertyName");
        PatchSafety.RequireFingerprint(_cardSerialize!, CardStateSerializeFingerprint);
        PatchSafety.RequireFingerprint(serializableCard, SerializableCardSerializeFingerprint);
        PatchSafety.RequireFingerprint(serializableEnchantment, SerializableEnchantmentSerializeFingerprint);
        PatchSafety.RequireFingerprint(savedProperties, SavedPropertiesSerializeFingerprint);
        string nameFingerprint = MethodFingerprint.Of(propertyName);
        if (!WritePropertyNameFingerprints.Any(expected => PatchSafety.FingerprintMatches(nameFingerprint, expected)))
        {
            throw new PatchSkippedException($"SavedProperties.WritePropertyName changed (fingerprint {nameFingerprint}): left vanilla");
        }
        // And the two writers whose layout PatchUpgradeBits writes into (the level's 8 bits right after the entry id): another mod's patch
        // on them could move it. Not WriteBool, WriteEnum or Write<T>: used everywhere, their patches are better left to the sampled checks.
        MethodInfo writeModelEntry = Reflect.Method(typeof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry))
            ?? throw new MissingMethodException(nameof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry));
        MethodInfo writeInt = Reflect.Method(typeof(PacketWriter), nameof(PacketWriter.WriteInt))
            ?? throw new MissingMethodException(nameof(PacketWriter), nameof(PacketWriter.WriteInt));
        _cardSerializers = new MethodBase[] { _cardSerialize!, serializableCard, serializableEnchantment, savedProperties, propertyName, writeModelEntry, writeInt };

        // What the kept bits were written with: ids are written on widths fixed when the caches are built (a mod adding a type to the
        // saved-property cache later, as BaseLib can, may widen the property ids: Limitless's Patch_SavedPropertyNetIdWidth).
        Type cache = typeof(ModelIdSerializationCache);
        _idHash = AccessTools.MethodDelegate<Func<uint>>(Reflect.PropertyGetter(cache, nameof(ModelIdSerializationCache.Hash))
            ?? throw new MissingMethodException(cache.Name, "get_Hash"));
        _entryIdWidth = AccessTools.MethodDelegate<Func<int>>(Reflect.PropertyGetter(cache, nameof(ModelIdSerializationCache.EntryIdBitSize))
            ?? throw new MissingMethodException(cache.Name, "get_EntryIdBitSize"));
        _categoryIdWidth = AccessTools.MethodDelegate<Func<int>>(Reflect.PropertyGetter(cache, nameof(ModelIdSerializationCache.CategoryIdBitSize))
            ?? throw new MissingMethodException(cache.Name, "get_CategoryIdBitSize"));
        // v0.111+: ModelIdSerializationCache.PropertyIdBitSize; v0.107.1: SavedPropertiesTypeCache.NetIdBitSize.
        MethodInfo propertyWidth = Reflect.PropertyGetter(cache, "PropertyIdBitSize")
            ?? (Reflect.TypeByName("MegaCrit.Sts2.Core.Saves.Runs.SavedPropertiesTypeCache") is { } legacy ? Reflect.PropertyGetter(legacy, "NetIdBitSize") : null)
            ?? throw new MissingMethodException(cache.Name, "the saved-property id width");
        _propertyIdWidth = AccessTools.MethodDelegate<Func<int>>(propertyWidth);

        // BaseLib is loaded (LimitlessReserialize's comparison needs it): its postfixes are allowed only with their known code.
        MethodInfo cardWrite = Reflect.Method(BaseLibCardWrite) ?? throw new PatchSkippedException("BaseLib has no " + BaseLibCardWrite);
        MethodInfo enchantmentWrite = Reflect.Method(BaseLibEnchantmentWrite) ?? throw new PatchSkippedException("BaseLib has no " + BaseLibEnchantmentWrite);
        Type handlers = Reflect.TypeByName(BaseLibHandlersType) ?? throw new PatchSkippedException("BaseLib has no " + BaseLibHandlersType);
        MethodInfo canModify = (Reflect.TypeByName("BaseLib.Patches.PostModInitPatch") is { } postModInit ? Reflect.PropertyGetter(postModInit, "CanModifyGameplay") : null)
            ?? throw new PatchSkippedException("BaseLib has no PostModInitPatch.CanModifyGameplay");
        _baseLibGameplay = AccessTools.MethodDelegate<Func<bool>>(canModify);
        if (PatchSafety.FingerprintMatches(MethodFingerprint.Of(cardWrite), BaseLibCardWriteFingerprint)
            && PatchSafety.FingerprintMatches(MethodFingerprint.Of(enchantmentWrite), BaseLibEnchantmentWriteFingerprint)
            && Reflect.Method(handlers, "Write") is { } write && PatchSafety.FingerprintMatches(MethodFingerprint.Of(write), BaseLibHandlersWriteFingerprint))
        {
            _baseLibWrites = new[] { cardWrite, enchantmentWrite };
        }
        else
        {
            // Then BaseLib's postfixes count as another mod's: no card bits kept (logged by PatchSafety when seen).
            Log.Info($"[{ModEntry.Id}] {Name}: BaseLib's serialization postfixes changed, card bits written by the game");
        }
        BitsEpoch();
    }

    private static long _cardGuardsAt = long.MinValue / 2, _pileGuardsAt = long.MinValue / 2; // So `now - at` can't overflow: the first check runs.
    private static bool _cardBlocked = true, _pileBlocked = true;

    /// <summary>
    /// Another mod's patch on what kept card bits stand for (a prefix, postfix or finalizer may write anything; a transpiler, change what
    /// is written): BaseLib's known postfixes and Unlimited's known list-width transpiler (fingerprinted by RelicStateReuse) aside.
    /// Rechecked every 2 s: this runs for every card serialized.
    /// </summary>
    private static bool CardBitsBlocked()
    {
        long now = Environment.TickCount64;
        if (now - _cardGuardsAt >= 2000)
        {
            _cardBlocked = Blocked(_cardSerializers);
            _cardGuardsAt = now;
        }
        return _cardBlocked;
    }

    /// <summary><see cref="CardBitsBlocked"/>, plus the pile's own serialization and the two list writers it stands for.</summary>
    private static bool PileBitsBlocked()
    {
        long now = Environment.TickCount64;
        if (now - _pileGuardsAt >= 2000)
        {
            _pileBlocked = Blocked(_pileSerializers);
            _pileGuardsAt = now;
        }
        return _pileBlocked || CardBitsBlocked();
    }

    private static bool Blocked(MethodBase[]? methods)
    {
        if (methods == null)
        {
            return true;
        }
        MethodInfo[] allowed = RelicStateReuse.UnlimitedTranspilerMethod is { } unlimited ? _baseLibWrites.Append(unlimited).ToArray() : _baseLibWrites;
        foreach (MethodBase method in methods)
        {
            // No tolerance: serializers whose output is reused instead of written again.
            if (PatchSafety.IsPatchedByOthers(method, allowed))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Changes when what kept bits were written with may have: the model id cache (hash, entry and category id widths), the saved-property
    /// id width, BaseLib's registered card and enchantment saves, and whether BaseLib writes them at all. Bits of another epoch are written
    /// again by the game's method.
    /// </summary>
    private static int BitsEpoch()
    {
        (LimitlessReserialize.ExtendedData card, LimitlessReserialize.ExtendedData enchantment) = LimitlessReserialize.Data();
        (uint, int, int, int, int, int, bool) key = (_idHash!(), _entryIdWidth!(), _categoryIdWidth!(), _propertyIdWidth!(), card.SavesCount, enchantment.SavesCount, _baseLibGameplay!());
        if (key != _bitsKey)
        {
            _bitsKey = key;
            _bitsEpoch++;
        }
        return _bitsEpoch;
    }

    /// <summary>
    /// Whether bits may be kept for this object. <c>SavedProperties.FromInternal</c> stores an <c>int[]</c> property's own array, which its
    /// model may change in place: the object then serializes the new values (the game's call, as vanilla's fresh object would), kept bits
    /// the old ones. No card or enchantment saves one in v0.107.1 to v0.113.1 (relics do: Fur Coat); inner cards (Limitless) checked too.
    /// A saved card (<c>props.cards</c>) is the model's own object, whose fields have public setters: never kept. A card array is a new one
    /// from the game (<c>list.ToArray()</c>), but BaseLib's SavedSpireField export gives a field's own <c>SerializableCard[]</c>: arrays are
    /// kept only for types no SavedSpireField targets.
    /// BaseLib's extended data holds what each registered getter returned, written live at each serialization: a collection the model
    /// owns may change in place (and an emptied one compares as unchanged, so the object is given again). Bits are kept only when that
    /// data is known (none, or BaseLib's own empty card modifiers) or holds only primitives, strings and enums.
    /// </summary>
    [PatchPart("bits")]
    private static bool BitsSafe(SerializableCard? card, int depth = 0)
    {
        if (card == null)
        {
            return true;
        }
        if (depth > 8)
        {
            return false;
        }
        return PropsSafe(card.Props, card.Id, depth) && (card.Enchantment == null || PropsSafe(card.Enchantment.Props, card.Enchantment.Id, depth))
            && DataSafe(card);
    }

    [PatchPart("bits")]
    private static bool DataSafe(SerializableCard card)
    {
        if (LimitlessReserialize.StoredDataState(card) >= 0)
        {
            return true;
        }
        (LimitlessReserialize.ExtendedData cardData, LimitlessReserialize.ExtendedData enchantmentData) = LimitlessReserialize.Data();
        return ScalarData(cardData, card) && (card.Enchantment == null || ScalarData(enchantmentData, card.Enchantment));
    }

    [PatchPart("bits")]
    private static bool ScalarData(LimitlessReserialize.ExtendedData data, object saved)
    {
        if (data.Stored(saved) is not { } stored)
        {
            return true;
        }
        foreach (System.Collections.IDictionary dictionary in data.Dictionaries(stored).Values)
        {
            foreach (object? value in dictionary.Values)
            {
                if (value != null && value is not string && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
                {
                    return false;
                }
            }
        }
        return true;
    }

    [PatchPart("bits")]
    private static bool PropsSafe(SavedProperties? props, ModelId? owner, int depth)
    {
        if (props == null)
        {
            return true;
        }
        if (props.intArrays != null || props.cards != null)
        {
            return false;
        }
        if (props.cardArrays != null)
        {
            // Unknown id (no model to judge): not kept.
            if (owner == null || ModelDb.GetByIdOrNull<AbstractModel>(owner) is not { } model || BaseLibTypes.SpireFieldTargets(model.GetType()))
            {
                return false;
            }
            foreach (SavedProperties.SavedProperty<SerializableCard[]> property in props.cardArrays)
            {
                foreach (SerializableCard inner in property.value ?? Array.Empty<SerializableCard>())
                {
                    if (!BitsSafe(inner, depth + 1))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    /// <summary>
    /// What every card of a pile shares in one snapshot. <see cref="Listeners"/>: the cost and keyword listeners of the combat, each
    /// with its amount and owner, when every one of them answers from what a read covers (<see cref="PureHooks"/>); null when one
    /// doesn't, and then no pile is trusted. <see cref="KeywordReadsPatched"/>: <see cref="SnapshotCardReuse.KeywordsPatched"/> (or no
    /// fast reads), read once per pile rather than per card.
    /// </summary>
    private readonly record struct Reads(long Stamp, bool NoKeywordHook, bool NoCostHook, HookListener[]? Listeners, bool KeywordReadsPatched);

    private readonly record struct HookListener(AbstractModel Model, int Amount, object? Owner);

    private static readonly HookListener[] NoListeners = Array.Empty<HookListener>();

    /// <summary>
    /// Cost and keyword listeners whose answer for a card depends only on the card's owner, type, base cost and local modifiers,
    /// keywords, affliction or pile type (all of it written through the counted writers, or fixed for a pile's cards) and on the
    /// listener's own amount and owner (v0.107.1 to v0.113.1, same fingerprints). While the same listeners stand with the same amounts,
    /// a pile's unwritten cards keep the costs and keywords they had. Not Void Form or Brilliant Scarf: they count the cards played this
    /// turn. Also <see cref="CardSortKeys"/>' judgment: a listener of these only reads, so a card's cost
    /// can't change while a sort runs.
    /// </summary>
    internal static readonly (string Type, string Method, string Fingerprint)[] PureHooks =
    {
        ("MegaCrit.Sts2.Core.Models.Powers.CuriousPower", "TryModifyEnergyCostInCombat", "F8A0E3D0504A6C32"),
        ("MegaCrit.Sts2.Core.Models.Powers.BorrowedTimePower", "TryModifyEnergyCostInCombat", "2CFAD6BDBD87020F"),
        ("MegaCrit.Sts2.Core.Models.Powers.CorruptionPower", "TryModifyEnergyCostInCombatLate", "AF1AEC99FB966FDD"),
        ("MegaCrit.Sts2.Core.Models.Relics.SpikedGauntlets", "TryModifyEnergyCostInCombat", "EBEC652116165626"),
        ("MegaCrit.Sts2.Core.Models.Powers.FreeAttackPower", "TryModifyEnergyCostInCombatLate", "1D9B5C1B89979BAB"),
        ("MegaCrit.Sts2.Core.Models.Powers.FreePowerPower", "TryModifyEnergyCostInCombatLate", "801ACD8EF492F80C"),
        ("MegaCrit.Sts2.Core.Models.Powers.FreeSkillPower", "TryModifyEnergyCostInCombatLate", "236DB712F26998E7"),
        ("MegaCrit.Sts2.Core.Models.Powers.TangledPower", "TryModifyEnergyCostInCombat", "821BCEB038AC7E59"),
        ("MegaCrit.Sts2.Core.Models.Powers.HexPower", "TryModifyKeywordsInCombat", "7CE6FD37170D4241"),
        ("MegaCrit.Sts2.Core.Models.Powers.VeilpiercerPower", "TryModifyEnergyCostInCombatLate", "01EA4019AAB80E02"),
    };

    private static readonly string[] HookMethods = { "TryModifyEnergyCostInCombat", "TryModifyEnergyCostInCombatLate", "TryModifyKeywordsInCombat" };

    /// <summary>Per listener type: its overrides of <see cref="HookMethods"/>, when all are known pure ones; null otherwise.</summary>
    private static readonly Dictionary<Type, MethodInfo[]?> _pureTypes = new();

    /// <summary>Piles stay trusted with pure listeners active (bdperf purehooks on|off).</summary>
    public static bool PureHookReads { get; set; } = true;
#if BENCH
    /// <summary>Listeners compared without their amounts (bdperf purehooks blind): the checks must see it.</summary>
    public static bool PureHooksBlindForTest { get; set; }
#endif

    private static MethodInfo[]? PureOverrides(Type type)
    {
        if (_pureTypes.TryGetValue(type, out MethodInfo[]? known))
        {
            return known;
        }
        List<MethodInfo> overrides = new();
        foreach (string name in HookMethods)
        {
            MethodInfo? method;
            try
            {
                method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                // An overload of that name next to the override (a content mod's): not a known pure listener, piles read in full while it listens.
                return _pureTypes[type] = null;
            }
            if (method == null || method.DeclaringType == typeof(AbstractModel))
            {
                continue;
            }
            int entry = Array.FindIndex(PureHooks, h => h.Type == method.DeclaringType?.FullName && h.Method == name);
            if (entry < 0 || method.DeclaringType != type || !PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), PureHooks[entry].Fingerprint))
            {
                return _pureTypes[type] = null;
            }
            overrides.Add(method);
        }
        return _pureTypes[type] = overrides.ToArray();
    }

    /// <summary>The listeners of these lists with their amounts and owners, or null when one may answer from more than a read covers.</summary>
    private static HookListener[]? PureListeners(List<AbstractModel>? keywords, List<AbstractModel>? early, List<AbstractModel>? late)
    {
        if (!PureHookReads || keywords == null || early == null || late == null || HookFilter.CombatHookListenersEmpty)
        {
            return null;
        }
        var listeners = new HookListener[keywords.Count + early.Count + late.Count];
        int i = 0;
        foreach (List<AbstractModel> list in new[] { keywords, early, late })
        {
            foreach (AbstractModel model in list)
            {
                // No tolerance: listener overrides a trusted pile doesn't call again.
                if (PureOverrides(model.GetType()) is not { } methods || methods.Any(m => PatchSafety.IsPatchedByOthers(m)))
                {
                    return null;
                }
                listeners[i++] = model switch
                {
                    PowerModel power => new HookListener(model, power.Amount, power.Owner),
                    RelicModel relic => new HookListener(model, 0, relic.Owner),
                    _ => new HookListener(model, 0, null),
                };
            }
        }
        return listeners;
    }

    private static bool SameListeners(HookListener[]? a, HookListener[]? b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
#if BENCH
            if (PureHooksBlindForTest && ReferenceEquals(a[i].Model, b[i].Model))
            {
                continue;
            }
#endif
            if (!ReferenceEquals(a[i].Model, b[i].Model) || a[i].Amount != b[i].Amount || !ReferenceEquals(a[i].Owner, b[i].Owner))
            {
                return false;
            }
        }
        return true;
    }

    [PatchPart("piles")]
    private static Reads ReadsFor(CardPile pile)
    {
        long stamp = LimitlessReserialize.MemoStamp();
        bool keywordsPatched = !_fastReads || KeywordsPatched();
        // No listeners (null): no pile trusted or kept as moved.
        // No tolerance: GetWithModifiers is answered from kept reads, not called. Nor on the keyword and cost hooks themselves:
        // HookFilter's lists let void prefixes stand there (its mirrors still call the hook), but these reads skip the hook call
        // altogether (local keywords and cost, reused pile states), so any patch there would miss calls.
        if (!_fastReads || !HookFilter.Active || keywordsPatched || PatchSafety.IsPatchedByOthers(_withModifiers) || GettersPatched()
            || PatchSafety.IsPatchedByOthers(HookFilter.HookMethodOf(HookFilter.Keywords)) || PatchSafety.IsPatchedByOthers(HookFilter.HookMethodOf(HookFilter.EnergyCost))
            || pile.Cards.Count == 0 || pile.Cards[0].Owner?.Creature.CombatState is not CombatState combat)
        {
            return new Reads(stamp, false, false, null, keywordsPatched);
        }
        List<AbstractModel>? keywords = HookFilter.ForCombat(combat, HookFilter.Keywords);
        List<AbstractModel>? early = HookFilter.ForCombat(combat, HookFilter.EnergyCost);
        List<AbstractModel>? late = HookFilter.ForCombat(combat, HookFilter.EnergyCostLate);
        bool noKeywords = keywords is { Count: 0 };
        bool noCost = early is { Count: 0 } && late is { Count: 0 };
        return new Reads(stamp, noKeywords, noCost, noKeywords && noCost ? NoListeners : PureListeners(keywords, early, late), false);
    }

    /// <summary><c>CombatPileState.From</c>: the pile's previous list while every card's state equals its entry.</summary>
    [PatchPart("piles")]
    private static bool PilePrefix(CardPile pile, ref NetFullCombatState.CombatPileState __result)
    {
        // Inside RunVanilla the result is the reference this code is compared with (SnapshotVerifier, bdperf): the game's own.
        if (!Enabled || ListenerCache.IsSuspended || !LimitlessReserialize.CanCompareHere || LimitlessReserialize.Faulted
            // No tolerance: From is answered with the previous list, its patches wouldn't run.
            || PatchSafety.IsPatchedByOthers(_pileFrom) || PatchSafety.IsPatchedByOthers(_cardFrom) || LimitlessReserialize.PatchedByOthers())
        {
            return true;
        }
#if BENCH
        long timedFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        string timedPath = "full";
#endif
        try
        {
            PileLast last = _piles.GetValue(pile, static _ => new PileLast());
            List<NetFullCombatState.CardState>? previous = last.Cards;
            IReadOnlyList<CardModel> cards = pile.Cards;
            int count = cards.Count;
            List<NetFullCombatState.CardState>? list = null;
            Reads reads = ReadsFor(pile);
            int listVersion = _trustPiles ? _listVersion!(_pileCards!(pile)) : 0;
            bool trusted = _trustPiles && previous != null && last.WritesAt == last.Writes && last.StampAt == reads.Stamp && reads.Stamp >= 0
                && last.ListVersion == listVersion && SameListeners(last.ListenersAt, reads.Listeners) && LiveSame(cards, last, previous, reads);
#if BENCH
            // Taken only for a pile whose kept states can be made wrong (two different ones at least: an empty or one-card pile
            // can't), else the take would change nothing and the control see nothing.
            if (trusted && TestWrong.IsArmed("reuse.trusted") && WrongStates(previous!) is { } wrongStates && TestWrong.Take("reuse.trusted"))
            {
                // Test: the kept states, one of them another card's, and this pile's full read due now (it must see the difference).
                previous = wrongStates;
                _sincePileCheck = PileVerifyEvery;
            }
#endif
            if (trusted && ++_sincePileCheck < PileVerifyEvery)
            {
                PilesTrusted++;
                if (reads.Listeners is { Length: > 0 })
                {
                    PilesWithHooks++;
                }
                __result = new NetFullCombatState.CombatPileState { pileType = pile.Type, cards = previous! };
#if BENCH
                timedPath = "trusted";
#endif
                return false;
            }
            if (trusted)
            {
                _sincePileCheck = 0;
            }
            // A pile whose list moved (a card added at the end, taken from the top) but whose cards had no write since the last read:
            // a card found again at its place, last read in this pile and with its memo valid, has the state it had then, as in a
            // trusted pile. Only cards that came from elsewhere, or have no memo, are read. One such pile in PileVerifyEvery is read in full.
#if BENCH
            if (!trusted)
            {
                string why = !_trustPiles ? "untrusted" : previous == null ? "first read" : last.WritesAt != last.Writes ? (last.WritesAt < 0 ? "not trustable last time" : "written")
                    : last.StampAt != reads.Stamp || reads.Stamp < 0 ? "stamp" : !SameListeners(last.ListenersAt, reads.Listeners) ? (reads.Listeners == null ? "cost or keyword hook" : "hook listeners changed")
                    : last.ListVersion != listVersion ? "moved" : "live card changed";
                WhyRead.AddOrUpdate($"{pile.Type} {why}" + (why == "not trustable last time" ? $" (live {last.LiveCount}/{Math.Max(MaxLiveCards, last.Count / LiveShare)})" : ""), 1, (_, n) => n + 1);
            }
#endif
            // Written cards since then are all listed (no overflow): those are read again, the others keep their states.
            bool moved = MovedPiles && !trusted && _trustPiles && previous != null && last.WritesAt >= 0 && (last.WritesAt == last.Writes || (DirtyReads && !last.DirtyOverflow))
                && last.StampAt == reads.Stamp && reads.Stamp >= 0 && SameListeners(last.ListenersAt, reads.Listeners);
            if (moved && last.WritesAt != last.Writes)
            {
                DirtyPiles++;
            }
            if (moved && reads.Listeners is { Length: > 0 })
            {
                PilesWithHooks++;
            }
            bool verifyMoved = moved && ++_sincePileCheck >= PileVerifyEvery;
#if BENCH
            // Test: the first card of this moved pile kept from before is given another card's state and checked at once (it must
            // see it). Taken there only, at a card that is really reused with another state to give: a moved pile whose cards all
            // came from elsewhere keeps it armed for the next one.
            bool wrongMoved = moved && previous!.Count > 1 && TestWrong.IsArmed("reuse.moved");
#endif
            if (verifyMoved)
            {
                _sincePileCheck = 0;
            }
#if BENCH
            timedPath = moved ? (last.WritesAt != last.Writes ? "dirty" : "moved") : previous == null ? "first" : last.WritesAt < 0 ? "full, untrustable"
                : !_trustPiles ? "full, piles untrusted" : last.WritesAt != last.Writes && last.DirtyOverflow ? "full, overflow" : last.StampAt != reads.Stamp || reads.Stamp < 0 ? "full, stamp"
                : reads.Listeners == null ? "full, hook" : !SameListeners(last.ListenersAt, reads.Listeners) ? "full, listeners changed" : "full, other";
#endif
            long writesBefore = last.Writes;
            int live = 0;
            int maxLive = Math.Max(MaxLiveCards, count / LiveShare);
            Last?[] lasts = last.Lasts;
            if (last.Spare.Length < count)
            {
                last.Spare = new Last?[count + count / 4];
            }
            Last?[] next = last.Spare;
            int oldCount = last.Count;
            int shift = 0; // Cards taken from the top of a pile move the others by one.
            for (int i = 0; i < count; i++)
            {
                CardModel card = cards[i];
                Last? entry = null;
                int at = i + shift;
                int was = -1;
                if (at >= 0 && at < oldCount && ReferenceEquals(lasts[at]?.Card, card))
                {
                    entry = lasts[at];
                    was = at;
                }
                else if (at + 1 < oldCount && at >= -1 && ReferenceEquals(lasts[at + 1]?.Card, card))
                {
                    entry = lasts[at + 1];
                    was = at + 1;
                    shift++;
                }
                NetFullCombatState.CardState state;
                if (moved && was >= 0 && was < previous!.Count && entry != null && ReferenceEquals(entry.In, last) && entry.MemoStamp == reads.Stamp && !entry.Dirty)
                {
                    state = previous[was];
                    MovedReused++;
                    bool verifyCard = verifyMoved;
#if BENCH
                    if (wrongMoved && OtherState(previous!, was) is { } other && TestWrong.Take("reuse.moved"))
                    {
                        wrongMoved = false;
                        state = other;
                        verifyCard = true;
                    }
#endif
                    if (verifyCard)
                    {
                        Last? fresh = entry;
                        NetFullCombatState.CardState read = CardFrom(card, ref fresh, reads);
                        if (!SameState(read, state))
                        {
                            PileMismatches++;
                            Detections++;
                            _trustPiles = false;
                            moved = false;
                            Log.Error($"[{ModEntry.Id}] {Name}: a moved pile's card changed unseen ({pile.Type}, {count} cards), piles are read every time from now on");
                            state = read;
                        }
                        entry = fresh;
                    }
                }
                else
                {
                    state = CardFrom(card, ref entry, reads);
                    if (entry != null)
                    {
                        entry.Dirty = false;
                    }
                }
                next[i] = entry;
                if (entry != null)
                {
                    entry.In = last;
                }
                if ((entry == null || entry.MemoStamp != reads.Stamp) && live <= maxLive)
                {
                    if (live < maxLive)
                    {
                        if (live == last.Live.Length)
                        {
                            Array.Resize(ref last.Live, live * 2);
                        }
                        last.Live[live] = i;
                    }
                    live++;
                }
                if (list == null)
                {
                    if (previous != null && i < previous.Count && SameState(previous[i], state))
                    {
                        continue;
                    }
                    list = new List<NetFullCombatState.CardState>(count);
                    for (int k = 0; k < i; k++)
                    {
                        list.Add(previous![k]);
                    }
                }
                list.Add(state);
            }
            if (list == null && previous != null && previous.Count == count)
            {
                PilesReused++;
                list = previous;
            }
            else
            {
                if (trusted)
                {
                    // Nothing moved, yet this read found a change: a write went unseen.
                    PileMismatches++;
                    Detections++;
                    _trustPiles = false;
                    Log.Error($"[{ModEntry.Id}] {Name}: a pile's state changed unseen ({pile.Type}, {count} cards), piles are read every time from now on");
                }
                PilesBuilt++;
                if (list == null)
                {
                    // Fewer cards than last time, the first ones unchanged.
                    list = new List<NetFullCombatState.CardState>(count);
                    for (int k = 0; k < count; k++)
                    {
                        list.Add(previous![k]);
                    }
                }
                last.Cards = list;
            }
            Array.Clear(next, count, next.Length - count);
            // Every card of this pile was read or kept unwritten. A listed card read since in another pile is that pile's now.
            foreach (Last written in last.Dirty)
            {
                if (ReferenceEquals(written.In, last))
                {
                    written.Dirty = false;
                }
            }
            last.Dirty.Clear();
            last.DirtyOverflow = false;
            last.Spare = lasts;
            last.Lasts = next;
            last.Count = count;
            last.LiveCount = live;
            // States read while a listener changed keywords or costs hold what it made of them: no base once it is gone (a relic removed).
            last.WritesAt = reads.Stamp >= 0 && reads.Listeners != null && live <= maxLive - 1 && writesBefore == last.Writes ? writesBefore : -1;
            last.ListenersAt = reads.Listeners;
            last.StampAt = reads.Stamp;
            last.ListVersion = listVersion;
            _byList.AddOrUpdate(list, last);
            __result = new NetFullCombatState.CombatPileState { pileType = pile.Type, cards = list };
            return false;
        }
        catch (GameCallException e)
        {
            // The game's From, run now, makes the same call and throws it from there. This pile's read stopped halfway (entries read,
            // its written cards not cleared): its next read reads every card.
            if (_piles.TryGetValue(pile, out PileLast? failed))
            {
                failed.WritesAt = -1;
            }
            GameExceptions++;
            if (!_loggedGameException)
            {
                _loggedGameException = true;
                Log.Warn($"[{ModEntry.Id}] {Name}: the game's own code threw while a pile's snapshot was read, the game's call made instead (stays on): {e.InnerException}");
            }
            return true;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
            return true;
        }
#if BENCH
        finally
        {
            string key = $"{pile.Type} {timedPath}";
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(timedFrom).TotalMilliseconds;
            lock (PileTimes)
            {
                (long n, double total, int cards) = PileTimes.GetValueOrDefault(key);
                PileTimes[key] = (n + 1, total + ms, cards + pile.Cards.Count);
            }
        }
#endif
    }

#if BENCH
    /// <summary>Time in <see cref="PilePrefix"/> per pile type and path: calls, ms, cards walked.</summary>
    public static readonly Dictionary<string, (long Calls, double Ms, int Cards)> PileTimes = new();
#endif

    /// <summary>The cards a trusted pile still has to compare, as a full read would.</summary>
    [PatchPart("piles")]
    private static bool LiveSame(IReadOnlyList<CardModel> cards, PileLast last, List<NetFullCombatState.CardState> previous, in Reads reads)
    {
        for (int k = 0; k < last.LiveCount; k++)
        {
            int i = last.Live[k];
            Last? entry = last.Lasts[i];
            NetFullCombatState.CardState state = CardFrom(cards[i], ref entry, reads);
            last.Lasts[i] = entry;
            if (entry != null)
            {
                entry.In = last;
            }
            if (!SameState(previous[i], state))
            {
                return false;
            }
        }
        return true;
    }

    [PatchPart("piles")]
    private static bool SameState(in NetFullCombatState.CardState a, in NetFullCombatState.CardState b) =>
        ReferenceEquals(a.card, b.card) && Equals(a.affliction, b.affliction) && a.afflictionCount == b.afflictionCount
        && a.energyCost == b.energyCost && ReferenceEquals(a.keywords, b.keywords);

    /// <summary>
    /// <c>CardState.From</c>: the snapshot object from <see cref="Saved"/>, the keyword list reused while the card's
    /// keywords are the same, no allocation while no listener changes keywords or costs.
    /// </summary>
    [PatchPart("piles")]
    private static NetFullCombatState.CardState CardFrom(CardModel card, ref Last? entry, in Reads reads)
    {
#if BENCH
        if (GameExceptionsForTest > 0)
        {
            GameExceptionsForTest--;
            throw new GameCallException(new InvalidOperationException("bdperf: an error of the game's code while a pile is read"));
        }
#endif
        if (entry == null)
        {
            _last.TryGetValue(card, out entry);
        }
        var result = new NetFullCombatState.CardState { card = Saved(card, ref entry, reads.Stamp) };
        AfflictionModel? affliction = card.Affliction;
        if (affliction != null)
        {
            result.affliction = affliction.Id;
            result.afflictionCount = affliction.Amount;
        }
        result.keywords = KeywordsOf(card, entry, reads);
        CardEnergyCost cost = card.EnergyCost;
        int withModifiers = reads.NoCostHook ? LocalCost(card, cost) : WithModifiers(cost);
        if (withModifiers != cost.Canonical)
        {
            result.energyCost = withModifiers;
        }
        return result;
    }

    /// <summary><c>card.ToSerializable()</c>, nothing compared while the memo holds.</summary>
    [PatchPart("piles")]
    private static SerializableCard Saved(CardModel card, ref Last? entry, long stamp)
    {
        if (entry != null && stamp >= 0 && entry.MemoStamp == stamp && Holds(card, entry))
        {
            if (++_sinceVerify < VerifyEvery)
            {
                Reused++;
                return entry.Saved;
            }
            _sinceVerify = 0;
            SerializableCard verified = Verify(card, entry);
            Register(entry, verified);
            return verified;
        }
        if (entry != null && stamp >= 0 && entry.MemoStamp == stamp && entry.PlainCard && card.CurrentUpgradeLevel != entry.Upgrade
            && HoldsExceptUpgrade(card, entry) && !LimitlessReserialize.PatchedByOthers())
        {
            return Upgraded(card, entry);
        }
        SerializableCard saved = ToSerializable(card);
        if (entry == null)
        {
            _last.TryGetValue(card, out entry);
        }
        if (entry != null && ReferenceEquals(entry.Saved, saved))
        {
            entry.Card = card;
            Register(entry, saved);
            Note(card, entry, stamp);
        }
        return saved;
    }

    /// <summary>
    /// What <c>card.ToSerializable()</c> holds when only the upgrade level changed: the same parts (saved properties: none;
    /// the enchantment's object: same fields) and BaseLib's data (known and the same for every object while the stamp stands).
    /// </summary>
    [PatchPart("piles")]
    private static SerializableCard Upgraded(CardModel card, Last entry)
    {
        SerializableCard old = entry.Saved;
        var saved = new SerializableCard
        {
            Id = old.Id,
            CurrentUpgradeLevel = card.CurrentUpgradeLevel,
            Props = null,
            Enchantment = old.Enchantment,
            FloorAddedToDeck = old.FloorAddedToDeck,
        };
        (LimitlessReserialize.ExtendedData cardData, _) = LimitlessReserialize.Data();
        object? data = cardData.Stored(old);
        if (data == null || old.Props != null)
        {
            long stamp = entry.MemoStamp; // Remember clears it with the new object.
            SerializableCard fresh = card.ToSerializable();
            Remember(card, entry, fresh);
            Register(entry, fresh);
            Note(card, entry, stamp);
            return fresh;
        }
        cardData.Store(saved, data);
        entry.UpgradedFrom = old;
        entry.Saved = saved;
        entry.Data = new LimitlessReserialize.StoredData();
        entry.Upgrade = card.CurrentUpgradeLevel;
        Register(entry, saved);
        LightBuilt++;
        return saved;
    }

    /// <summary>
    /// A combat is over: its piles' entries let go of the cards. The game keeps its last 20 checksum states, whose lists and saved cards
    /// lead here (<see cref="_byList"/>, <see cref="_bySaved"/>), so a finished combat's cards would stay alive through the next one.
    /// </summary>
    private static void ReleaseCombat(Player __instance)
    {
        try
        {
            if (!ReleaseCombats || __instance.PlayerCombatState is not { } state)
            {
                return;
            }
            foreach (CardPile pile in state.AllPiles)
            {
                if (_piles.TryGetValue(pile, out PileLast? last))
                {
                    last.Release();
                }
            }
            // Cards that left every pile before the end (transformed, removed from combat) are in no pile's entries: their own entry still
            // held them. An entry whose pile was released (this player's) or that no pile read is no live pile's; another player's are
            // released with that player.
            foreach (KeyValuePair<CardModel, Last> pair in (IEnumerable<KeyValuePair<CardModel, Last>>)_last)
            {
                Last entry = pair.Value;
                if (entry.Card != null && (entry.In == null || entry.In.Cards == null))
                {
                    PileLast.ReleaseEntry(entry);
                }
            }
        }
        catch (Exception e)
        {
            Disable("releasing a finished combat", e.ToString());
        }
    }

    private static void Register(Last entry, SerializableCard saved)
    {
        if (!ReferenceEquals(entry.Registered, saved))
        {
            _bySaved.AddOrUpdate(saved, entry);
            entry.Registered = saved;
        }
    }

    /// <summary>Whether everything <see cref="Note"/> recorded still holds: the rest of the snapshot object can't change for this card object.</summary>
    [PatchPart("piles")]
    private static bool Holds(CardModel card, Last last) => card.CurrentUpgradeLevel == last.Upgrade && HoldsExceptUpgrade(card, last);

    [PatchPart("piles")]
    private static bool HoldsExceptUpgrade(CardModel card, Last last)
    {
        if (card.FloorAddedToDeck != last.Floor)
        {
            return false;
        }
        EnchantmentModel? enchantment = card.Enchantment;
        if (!ReferenceEquals(enchantment, last.Enchantment))
        {
            return false;
        }
        if (enchantment == null)
        {
            return true;
        }
        if (enchantment.Amount != last.Amount)
        {
            return false;
        }
        if (last.ListGetter == null)
        {
            return true;
        }
        List<SerializableCard>? list = last.ListGetter(enchantment);
        return ReferenceEquals(list, last.List) && list != null && _storedVersion!(list) == last.ListVersion;
    }

    /// <summary>Records what <paramref name="last"/>'s snapshot object was built from, when that is everything it depends on.</summary>
    [PatchPart("piles")]
    private static void Note(CardModel card, Last last, long stamp)
    {
        last.MemoStamp = -1;
        EnchantmentModel? enchantment = card.Enchantment;
        Func<object, List<SerializableCard>?>? getter = null;
        Func<object, List<SerializableCard>?>? cardList = null;
        if (stamp < 0 || !card.IsMutable || (_trustPiles && !KeepsTypeGetter(card.GetType())) || !LimitlessReserialize.MemoPlan(card, out cardList)
            || (enchantment != null && (!enchantment.IsMutable || !LimitlessReserialize.MemoPlan(enchantment, out getter))))
        {
            return;
        }
        if (cardList != null)
        {
            return; // Only the enchantment's list is recorded (ListVersion): a card saving its own list is built each time.
        }
        last.PlainCard = true;
        last.Upgrade = card.CurrentUpgradeLevel;
        last.Floor = card.FloorAddedToDeck;
        last.Enchantment = enchantment;
        last.Amount = enchantment?.Amount ?? 0;
        last.ListGetter = getter;
        last.List = getter == null ? null : getter(enchantment!);
        if (getter != null && last.List == null)
        {
            return;
        }
        last.ListVersion = last.List == null ? 0 : _storedVersion!(last.List);
        last.MemoStamp = stamp;
    }

    private static HashSet<CardKeyword>? _keywordScratch;
    private static readonly MethodInfo? _keywordsGetter = Reflect.PropertyGetter(typeof(CardModel), nameof(CardModel.Keywords));

    /// <summary>Another mod patches what <c>card.Keywords</c> runs (the snapshot then reads it as the game does).</summary>
    // No tolerance: keyword reads answered from kept state, not called.
    private static bool KeywordsPatched() => PatchSafety.IsPatchedByOthers(_keywordsMethod) || PatchSafety.IsPatchedByOthers(_keywordsGetter);

    /// <summary><c>card.Keywords</c> (<c>GetKeywordsWithSources(All)</c>) as the snapshot's enum-ordered list; null when empty.</summary>
    [PatchPart("piles")]
    private static List<CardKeyword>? KeywordsOf(CardModel card, Last? last, in Reads reads)
    {
        IReadOnlySet<CardKeyword> keywords;
        if (reads.NoKeywordHook)
        {
            keywords = _localKeywords!(card); // No listener can add or remove any, in or out of combat.
        }
        else if (_fastReads && card.CombatState is { } combat && !card.IsCanonical && !reads.KeywordReadsPatched)
        {
            HashSet<CardKeyword> scratch = _keywordScratch ??= new HashSet<CardKeyword>();
            scratch.Clear();
            scratch.UnionWith(_localKeywords!(card));
            try
            {
                MegaCrit.Sts2.Core.Hooks.Hook.ModifyKeywordsInCombat(combat, card, scratch);
            }
            catch (Exception e)
            {
                throw new GameCallException(e);
            }
            keywords = scratch;
        }
        else
        {
            try
            {
                keywords = card.Keywords;
            }
            catch (Exception e)
            {
                throw new GameCallException(e);
            }
        }
        if (keywords.Count == 0)
        {
            return null;
        }
        List<CardKeyword>? kept = last?.Keywords;
        // The list holds only defined values (as the game's, from Enum.GetValues): a set that also holds a generated one (BaseLib's
        // custom keywords) has more, and is the same list when its defined values are the kept ones.
        if (kept != null && AllIn(kept, keywords) && ((kept.Count == keywords.Count && _keywordValuesDistinct) || kept.Count == DefinedCount(keywords)))
        {
            return kept;
        }
        kept = new List<CardKeyword>(keywords.Count);
        foreach (CardKeyword keyword in _keywordValues)
        {
            if (keywords.Contains(keyword))
            {
                kept.Add(keyword);
            }
        }
        if (last != null)
        {
            last.Keywords = kept;
        }
        return kept;
    }

    /// <summary><c>cost.GetWithModifiers(All)</c>: what its listeners throw is the game's (<see cref="GameCallException"/>).</summary>
    [PatchPart("piles")]
    private static int WithModifiers(CardEnergyCost cost)
    {
        try
        {
            return cost.GetWithModifiers(CostModifiers.All);
        }
        catch (Exception e)
        {
            throw new GameCallException(e);
        }
    }

    /// <summary>
    /// What a call the game's <c>CardState.From</c> also makes threw: a cost or keyword listener, the card's keywords. The game's error (a
    /// content mod's listener), not this part's: <see cref="PilePrefix"/> leaves that pile to the game's <c>From</c>, which throws it again
    /// from the same call (through no frame of this class, so <see cref="PatchGuard"/> doesn't take it for this part's), and stays on.
    /// Not <c>card.ToSerializable()</c>: the game's <c>From</c> makes that one through <see cref="ToSerializable"/>, a frame of this class,
    /// so this part goes off there anyway.
    /// </summary>
    private sealed class GameCallException : Exception
    {
        public GameCallException(Exception inner) : base(inner.Message, inner)
        {
        }
    }

    /// <summary>Piles left to the game's <c>From</c> because the game's own code threw while they were read (<see cref="GameCallException"/>).</summary>
    public static long GameExceptions { get; private set; }
    private static bool _loggedGameException;
#if BENCH
    /// <summary>The next reads of this many cards throw as a listener would (bdperf): the part must stay on, the piles as vanilla.</summary>
    public static int GameExceptionsForTest { get; set; }
#endif

    /// <summary><c>GetWithModifiers(All)</c> when no listener changes costs (the hook would return its input).</summary>
    [PatchPart("piles")]
    private static int LocalCost(CardModel card, CardEnergyCost cost)
    {
        int num = _costBase!(cost);
        if (card.IsCanonical || num < 0 || cost.CostsX)
        {
            return num;
        }
        List<LocalCostModifier> local = _costLocal!(cost);
        for (int i = 0; i < local.Count; i++)
        {
            num = local[i].Modify(num);
        }
        return Math.Max(0, num);
    }

    private static bool IsPilesWrite(CodeInstruction instruction) =>
        instruction.operand is MethodInfo { Name: nameof(PacketWriter.WriteList), IsGenericMethod: true } method
        && method.GetGenericArguments()[0] == typeof(NetFullCombatState.CombatPileState) && method.GetParameters().Length == 2;

    /// <summary>The <c>WriteList&lt;CombatPileState&gt;</c> calls the last run of <see cref="PlayerSerializeTranspiler"/> replaced.</summary>
    private static int _pilesReplaced;

    /// <summary>The check after the transpiler's install passed (one pile list write replaced).</summary>
    private static bool _pileBitsChecked;

    /// <summary>
    /// Pile bits run: the install's check passed, and the transpiler's last run (Harmony runs it again at each re-patch of the
    /// method, another mod patching it later) still replaced exactly one call. Otherwise every replaced call writes the game's way,
    /// as the log says ("pile bits not reused").
    /// </summary>
    private static bool PileBitsActive => _pileBitsChecked && _pilesReplaced == 1;

    private static IEnumerable<CodeInstruction> PlayerSerializeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0; // Harmony runs transpilers again each time the method is re-patched.
        foreach (CodeInstruction instruction in instructions)
        {
            if (IsPilesWrite(instruction))
            {
                replaced++;
                // Same stack: (writer, list, lengthBits).
                yield return new CodeInstruction(OpCodes.Call, Reflect.Method(typeof(SnapshotCardReuse), nameof(WritePiles)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
        _pilesReplaced = replaced;
    }

    /// <summary>
    /// <c>writer.WriteList(piles, lengthBits)</c>, each pile through <see cref="PileSerializePrefix"/>. The count check and header below
    /// mirror <c>PacketWriter.WriteList</c> (fingerprint <see cref="WriteListFingerprint"/>): re-read them before accepting another
    /// fingerprint for it. The verifier's reference (inside <see cref="ListenerCache.RunVanilla"/>) goes through the game's call, so a
    /// difference there shows.
    /// </summary>
    [PatchPart("bits")]
    private static void WritePiles(PacketWriter writer, IReadOnlyList<NetFullCombatState.CombatPileState> piles, int lengthBits)
    {
        // Another mod's patch on WriteList<CombatPileState> (or on what pile bits stand for) runs only through the game's call; so does
        // the vanilla reference SnapshotVerifier serializes.
        if (!Enabled || !PileBitsActive || ListenerCache.IsSuspended || PileBitsBlocked())
        {
            writer.WriteList(piles, lengthBits);
            return;
        }
        if ((ulong)piles.Count >= (ulong)(1L << lengthBits))
        {
            throw new IndexOutOfRangeException($"List length {piles.Count} is too large to fit in bit size {lengthBits}!");
        }
        writer.WriteInt(piles.Count, lengthBits);
        for (int i = 0; i < piles.Count; i++)
        {
            NetFullCombatState.CombatPileState pile = piles[i];
            if (PileSerializePrefix(ref pile, writer, out int start))
            {
                pile.Serialize(writer);
                PileSerializePostfix(ref pile, writer, start);
            }
        }
    }

    /// <summary><c>CombatPileState.Serialize</c>: the whole pile's bits, for the list they were taken from or from its cards' bits.</summary>
    [PatchPart("bits")]
    private static bool PileSerializePrefix(ref NetFullCombatState.CombatPileState __instance, PacketWriter writer, out int __state)
    {
        __state = -1;
        if (!Enabled || __instance.cards == null || __instance.cards.Count < MinCardsForPileBits || !LimitlessReserialize.CanCompareHere
            || PileBitsBlocked())
        {
#if BENCH
            if (__instance.cards is { Count: >= MinCardsForPileBits })
            {
                CountPileBits(!Enabled ? "disabled" : !LimitlessReserialize.CanCompareHere ? "can't compare here" : "patched by others");
            }
#endif
            return true;
        }
        try
        {
            if (_byList.TryGetValue(__instance.cards, out PileLast? last))
            {
#if BENCH
                _composeFail = null;
#endif
                int epoch = BitsEpoch();
                bool kept = ReferenceEquals(last.BitsFor, __instance.cards) && last.BitsEpoch == epoch;
                bool composed = !kept && Compose(ref __instance, last, epoch);
#if BENCH
                CountPileBits((kept ? "same list" : composed ? "composed" : "compose failed: " + _composeFail) + $" {__instance.pileType}");
#endif
                if (kept || composed)
                {
                    if (++_sincePileBits < PileVerifyEvery && !PileBitsCheckForced())
                    {
                        _reserveBits!(writer, last.BitCount);
                        int position = writer.BitPosition;
                        AppendBits(writer.Buffer, position, last.Bits!, last.BitCount);
                        _setBitPosition!(writer, position + last.BitCount);
                        if (composed)
                        {
                            PileBitsComposed++;
                        }
                        else
                        {
                            PileBitsReused++;
                        }
                        return false;
                    }
                    // This one is compared in the postfix (the counter restarts only here: lists the game wrote anyway don't delay it).
                    _sincePileBits = 0;
                }
                if (!Enabled)
                {
                    return true;
                }
                // Kept from what the game's method writes now (compared first when bits were kept or composed for this list).
                __state = writer.BitPosition;
                return true;
            }
#if BENCH
            CountPileBits($"list not from a read {__instance.pileType} {__instance.cards.Count}");
#endif
            // Not a list of a read (the verifier's vanilla state, an old one): nothing to keep or compare.
            return true;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
            return true;
        }
    }

#if BENCH
    /// <summary>Test builds: <c>pilebits</c> armed (<see cref="TestWrong"/>): the next kept or composed pile is compared now, not
    /// in <see cref="PileVerifyEvery"/> (a session reuses a pile's bits a few times only: the sampled compare might never come).</summary>
    private static bool PileBitsCheckForced() => TestWrong.IsArmed("pilebits");
#else
    private static bool PileBitsCheckForced() => false;
#endif

    /// <summary>
    /// Keeps what the game's method just wrote; when bits were kept for this list (in this epoch), they must be the same. The game's method
    /// still writes each card through <see cref="CardSerializePrefix"/>, so this checks the pile's header and how its bits were put together
    /// (copied blocks, upgrade levels, card order), not each card's kept bits: those are checked against the game's own serialization by
    /// <see cref="SameBits"/>, one card in <see cref="VerifyEvery"/> (a whole pile serialized fresh would freeze for tens of ms at 20,000 cards).
    /// </summary>
    [PatchPart("bits")]
    private static void PileSerializePostfix(ref NetFullCombatState.CombatPileState __instance, PacketWriter writer, int __state)
    {
        if (__state < 0)
        {
            return;
        }
        try
        {
            if (!_byList.TryGetValue(__instance.cards, out PileLast? last) || !ReferenceEquals(last.Cards, __instance.cards))
            {
                return;
            }
            int epoch = BitsEpoch();
            int bits = writer.BitPosition - __state;
            byte[] taken = new byte[(bits + 7) >> 3];
            ExtractBits(writer.Buffer, __state, taken, bits);
            byte[]? keptBits = last.Bits;
#if BENCH
            if (ReferenceEquals(last.BitsFor, __instance.cards) && last.BitsEpoch == epoch && keptBits is { Length: > 0 } && TestWrong.Take("pilebits"))
            {
                // Test: the kept bits compared with one bit flipped (the bits written were the game's: only the check is under test).
                keptBits = (byte[])keptBits.Clone();
                keptBits[0] ^= 1;
            }
#endif
            if (ReferenceEquals(last.BitsFor, __instance.cards) && last.BitsEpoch == epoch
                && (last.BitCount != bits || !taken.AsSpan().SequenceEqual(keptBits.AsSpan(0, taken.Length))))
            {
                Mismatches++;
                Disable("the bits kept for a pile differ from the game's", $"{__instance.pileType} ({last.BitCount} vs {bits} bits)");
                return;
            }
            foreach (NetFullCombatState.CardState card in __instance.cards)
            {
                if (!BitsSafe(card.card))
                {
                    // Not kept: a card of it may serialize differently later (BitsSafe), the game's method writes it each time.
                    last.BitsFor = null;
                    last.Bits = null;
                    last.Offsets = null;
                    return;
                }
            }
            last.Bits = taken;
            last.BitCount = bits;
            last.BitsFor = __instance.cards;
            last.BitsEpoch = epoch;
            last.Offsets = null;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
        }
    }

    /// <summary>
    /// A changed pile's bits, without a call per card: what the game's method writes before the first card (its type and the count,
    /// <see cref="CaptureHeader"/>), then each card's kept bits. One pile in <see cref="PileVerifyEvery"/> is written by the game's method
    /// and compared (<see cref="PileSerializePostfix"/>: the assembly, not each card's bits).
    /// A card's bits depend on its state alone: the longest runs of the same states at the start and at the end of the previous
    /// list (a card added at the end, cards drawn from the top) are copied from the previous bits as two blocks (same epoch only).
    /// </summary>
    [PatchPart("bits")]
    private static bool Compose(ref NetFullCombatState.CombatPileState state, PileLast last, int epoch)
    {
        List<NetFullCombatState.CardState> cards = state.cards;
        int count = cards.Count;
        if (!ReferenceEquals(last.Cards, cards) || last.Count != count)
        {
#if BENCH
            _composeFail = "not the last read list";
#endif
            return false;
        }
        Span<NetFullCombatState.CardState> states = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cards);
        Last?[] lasts = last.Lasts;
        List<NetFullCombatState.CardState>? previous = last.BitsFor;
        byte[]? previousBits = last.Bits;
        int[]? previousOffsets = last.Offsets;
        int head = 0, tail = 0;
        // Offsets are set with the bits they describe (BitsFor's cards, then the end); the array may be longer.
        if (BlockCopies && previous != null && previousBits != null && previousOffsets != null && previousOffsets.Length >= previous.Count + 1
            && !ReferenceEquals(previous, cards) && last.BitsEpoch == epoch)
        {
            Span<NetFullCombatState.CardState> before = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(previous);
            int max = Math.Min(before.Length, count);
            while (head < max && SameState(before[head], states[head]))
            {
                head++;
            }
            while (tail < max - head && SameState(before[before.Length - 1 - tail], states[count - 1 - tail]))
            {
                tail++;
            }
        }
        else
        {
            previousOffsets = null;
        }
        long bits = 0;
        if (previousOffsets != null)
        {
            bits += previousOffsets[head] - previousOffsets[0];
            bits += previousOffsets[previous!.Count] - previousOffsets[previous.Count - tail];
        }
        for (int i = head; i < count - tail; i++)
        {
            Last? entry = lasts[i];
            if (entry == null)
            {
#if BENCH
                _composeFail = "a card without entry";
#endif
                return false;
            }
            ref NetFullCombatState.CardState card = ref states[i];
            if (!SameKeptBits(card, entry, epoch))
            {
#if BENCH
                _upgradePath = "pile ";
#endif
                if (!PatchUpgradeBits(ref card, entry, epoch))
                {
                    if (!Capture(ref card, entry, epoch))
                    {
#if BENCH
                        _composeFail = "a card whose bits aren't kept";
#endif
                        return false;
                    }
                }
                else if (!CheckPatchedBits(ref card, entry))
                {
                    return false;
                }
            }
            else if (++_sinceBitCheck >= VerifyEvery)
            {
                _sinceBitCheck = 0;
                if (!SameBits(ref card, entry))
                {
                    return false;
                }
            }
            else
            {
                BitsReused++;
            }
            bits += entry.BitCount;
        }
        if ((last.HeaderEpoch != epoch && !CaptureHeader(ref state, last, epoch)) || (ulong)count >= 1UL << last.CountBits)
        {
#if BENCH
            _composeFail = "header";
#endif
            return false; // Too many cards: WriteList throws, the game's call then does as vanilla.
        }
        bits += last.HeaderBits;
        if (bits > int.MaxValue - 64)
        {
            return false;
        }
        int bytes = (int)((bits + 7) >> 3);
        // Written into the spare buffer: the previous bits are read for the copied blocks.
        byte[] buffer = last.SpareBits is { } spare && spare.Length >= bytes + 8 && !ReferenceEquals(spare, previousBits) ? spare : new byte[bytes + bytes / 8 + 8];
        int[] offsets = last.SpareOffsets is { } spareOffsets && spareOffsets.Length >= count + 1 && !ReferenceEquals(spareOffsets, previousOffsets)
            ? spareOffsets : new int[count + count / 8 + 1];
        Buffer.BlockCopy(last.Header!, 0, buffer, 0, last.Header!.Length);
        // The count, as WriteInt(count, CountBits) writes it: least significant bit first, over the header's zero count.
        for (int i = 0, at = last.HeaderBits - last.CountBits; i < last.CountBits; i++)
        {
            if (((count >> i) & 1) != 0)
            {
                buffer[(at + i) >> 3] |= (byte)(1 << ((at + i) & 7));
            }
        }
        int position = last.HeaderBits;
        if (head > 0)
        {
            int start = previousOffsets![0];
            for (int i = 0; i < head; i++)
            {
                offsets[i] = position + previousOffsets[i] - start;
            }
            position = CopyBlock(buffer, position, previousBits!, start, previousOffsets[head] - start);
        }
        for (int i = head; i < count - tail; i++)
        {
            Last entry = lasts[i]!;
            offsets[i] = position;
            AppendBits(buffer, position, entry.Bits!, entry.BitCount);
            position += entry.BitCount;
        }
        if (tail > 0)
        {
            int start = previousOffsets![previous!.Count - tail];
            for (int i = 0; i < tail; i++)
            {
                offsets[count - tail + i] = position + previousOffsets[previous.Count - tail + i] - start;
            }
            position = CopyBlock(buffer, position, previousBits!, start, previousOffsets[previous.Count] - start);
            BlockCopiedCards += tail;
        }
        BlockCopiedCards += head;
        offsets[count] = position;
        // Zeros after the last bit, as a copy expects.
        if ((position & 7) != 0)
        {
            buffer[position >> 3] &= (byte)((1 << (position & 7)) - 1);
        }
        Array.Clear(buffer, (position + 7) >> 3, buffer.Length - ((position + 7) >> 3));
        last.SpareBits = previousBits;
        last.SpareOffsets = previousOffsets ?? last.Offsets;
        last.Bits = buffer;
        last.Offsets = offsets;
        last.BitCount = position;
        last.BitsFor = cards;
        last.BitsEpoch = epoch;
        return true;
    }

#if BENCH
    /// <summary>What happened to piles of 32+ cards at serialization (bits reused, composed, or why the game's method ran).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> WhyPileBits = new();
    private static string? _composeFail;
    private static void CountPileBits(string why) => WhyPileBits.AddOrUpdate(why, 1, (_, n) => n + 1);
#endif

    /// <summary>Cards whose bits a compose copied as part of a block from the previous bits.</summary>
    public static long BlockCopiedCards { get; private set; }

    /// <summary>Block copies of unchanged runs of cards (bdperf blocks on|off).</summary>
    public static bool BlockCopies { get; set; } = true;

    private static byte[] _blockScratch = Array.Empty<byte>();

    /// <summary>Appends <paramref name="bits"/> bits of <paramref name="source"/> from bit <paramref name="start"/>: the new position.</summary>
    private static int CopyBlock(byte[] destination, int position, byte[] source, int start, int bits)
    {
        if (bits <= 0)
        {
            return position;
        }
        int bytes = (bits + 7) >> 3;
        if (_blockScratch.Length < bytes + 8)
        {
            _blockScratch = new byte[bytes + bytes / 4 + 8];
        }
        ExtractBits(source, start, _blockScratch, bits);
        // AppendBits reads whole words: zeros after the block's last byte.
        Array.Clear(_blockScratch, bytes, Math.Min(8, _blockScratch.Length - bytes));
        AppendBits(destination, position, _blockScratch, bits);
        return position + bits;
    }

    private static readonly List<NetFullCombatState.CardState> NoCards = new();

    /// <summary>
    /// The game's bits for this pile with no card. <c>CombatPileState.Serialize</c> (fingerprinted) writes the type on 32 bits, then
    /// <c>WriteList</c> (fingerprinted, its <c>CardState</c> instantiation unpatched) the count, then each card: any count's header is
    /// these bits with the count written into their last ones. Written once per pile and epoch, not with a call per card (the earlier
    /// way ran the pile's whole serialization, each card skipped, whenever the count changed: most plays for a big draw pile).
    /// </summary>
    [PatchPart("bits")]
    private static bool CaptureHeader(ref NetFullCombatState.CombatPileState state, PileLast last, int epoch)
    {
        _bitWriter ??= new() { WarnOnGrow = false };
        _bitWriter.Reset();
        var empty = new NetFullCombatState.CombatPileState { pileType = state.pileType, cards = NoCards };
        empty.Serialize(_bitWriter);
        _bitWriter.ZeroByteRemainder();
        int countBits = _bitWriter.BitPosition - 32;
        if (countBits is <= 0 or > 32)
        {
            return false; // Not the layout mirrored: the game's method then.
        }
        last.Header = _bitWriter.Buffer.AsSpan(0, _bitWriter.BytePosition).ToArray();
        last.HeaderBits = _bitWriter.BitPosition;
        last.CountBits = countBits;
        last.HeaderEpoch = epoch;
        return true;
    }

    /// <summary>The bits from <paramref name="start"/> of a packet buffer, from the first bit of <paramref name="destination"/>, zeros after the last.</summary>
    private static void ExtractBits(byte[] source, int start, byte[] destination, int bits)
    {
        int at = start >> 3;
        int shift = start & 7;
        int bytes = (bits + 7) >> 3;
        if (shift == 0)
        {
            Buffer.BlockCopy(source, at, destination, 0, bytes);
        }
        else
        {
            for (int i = 0; i < bytes; i++)
            {
                int high = at + i + 1 < source.Length ? source[at + i + 1] : 0;
                destination[i] = (byte)((source[at + i] >> shift) | (high << (8 - shift)));
            }
        }
        if ((bits & 7) != 0)
        {
            destination[bytes - 1] &= (byte)((1 << (bits & 7)) - 1);
        }
    }

    private static bool _capturing;
    private static PacketWriter? _bitWriter;
    private static int _sinceBitCheck;

    // --- Upgrade bits: a card whose only change is its upgrade level keeps its bits but those 8 ---

    /// <summary>v0.107.1 to v0.113.1: the card first (<c>writer.Write(card)</c>), then its affliction, cost and keywords.</summary>
    private const string CardStateSerializeFingerprint = "CD9AA92A00A4338D";
    /// <summary><c>SerializableCard.Serialize</c>: <c>WriteModelEntry(Id)</c>, then <c>WriteInt(CurrentUpgradeLevel, 8)</c>, then the rest.</summary>
    private const string SerializableCardSerializeFingerprint = "043F1A4FC022488F";
    /// <summary><c>WriteModelEntry</c>: the entry's net id on <c>ModelIdSerializationCache.EntryIdBitSize</c> bits.</summary>
    private const string WriteModelEntryFingerprint = "67C0540FD55D5460";
    /// <summary><c>WriteInt(val, bits)</c>: the value's little-endian bytes, their first <c>bits</c> bits (the low byte for 8).</summary>
    private const string WriteIntFingerprint = "2E87784A326757CF";
    /// <summary>The first patched cards of a session are all checked against the game's bits, then one in <see cref="VerifyEvery"/>.</summary>
    private const int UpgradeBitsCheckedFirst = 64;
    private static Func<int>? _entryIdBits;
    private static int _upgradeBitsChecked;

    /// <summary><c>CardState.Serialize</c> and <c>SerializableCard.Serialize</c>: fingerprinted with the card bits (<see cref="ResolveSerializers"/>).</summary>
    private static void ApplyUpgradeBits()
    {
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry))
            ?? throw new MissingMethodException(nameof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry)), WriteModelEntryFingerprint);
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(PacketWriter), nameof(PacketWriter.WriteInt))
            ?? throw new MissingMethodException(nameof(PacketWriter), nameof(PacketWriter.WriteInt)), WriteIntFingerprint);
        _entryIdBits = AccessTools.MethodDelegate<Func<int>>(Reflect.PropertyGetter(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.EntryIdBitSize))
            ?? throw new MissingMethodException(nameof(ModelIdSerializationCache), "get_EntryIdBitSize"));
    }

    /// <summary>
    /// The kept bits of a card <see cref="Upgraded"/> remade from the very object they were written for, with the rest of the state
    /// the same: its upgrade level written into them as <c>WriteInt(level, 8)</c> would (the low byte, least significant bit first,
    /// right after the entry id), instead of serializing the card again with its enchantments. Only while nothing of another mod but
    /// BaseLib's postfixes (after these bits) and Unlimited's list-width transpiler (no list here) patches the card's serialization:
    /// a prefix would write before the entry id and move them (<see cref="CardBitsBlocked"/>, checked by every caller).
    /// </summary>
    [PatchPart("bits")]
    private static bool PatchUpgradeBits(ref NetFullCombatState.CardState state, Last last, int epoch)
    {
        if (!UpgradeBits || _entryIdBits == null || last.Bits == null || last.BitsEpoch != epoch || last.UpgradedFrom == null || !ReferenceEquals(last.BitsCard, last.UpgradedFrom)
            || !ReferenceEquals(last.Saved, state.card) || !Equals(last.BitsAffliction, state.affliction) || last.BitsAfflictionCount != state.afflictionCount
            || last.BitsEnergyCost != state.energyCost || !ReferenceEquals(last.BitsKeywords, state.keywords))
        {
#if BENCH
            if (last.UpgradedFrom == null || !ReferenceEquals(last.Saved, state.card))
            {
                return false; // Not an upgrade: any other card's first or new capture.
            }
            string why = !UpgradeBits || _entryIdBits == null ? "off" : last.Bits == null ? "no bits" : last.BitsEpoch != epoch ? "other epoch" : last.UpgradedFrom == null ? "not upgraded" : !ReferenceEquals(last.BitsCard, last.UpgradedFrom) ? "bits of another object"
                : !ReferenceEquals(last.Saved, state.card) ? "other saved" : last.BitsEnergyCost != state.energyCost ? "cost" : !ReferenceEquals(last.BitsKeywords, state.keywords) ? "keywords" : "affliction";
            why = _upgradePath + why;
            WhyNoUpgradeBits[why] = WhyNoUpgradeBits.GetValueOrDefault(why) + 1;
#endif
            return false;
        }
        int at = _entryIdBits();
        if (at <= 0 || last.BitCount < at + 8)
        {
            return false;
        }
        int level = state.card.CurrentUpgradeLevel & 0xFF;
#if BENCH
        if (UpgradeBitsWrongForTest)
        {
            level ^= 1;
        }
#endif
        for (int i = 0; i < 8; i++)
        {
            int bit = at + i;
            if (((level >> i) & 1) != 0)
            {
                last.Bits[bit >> 3] |= (byte)(1 << (bit & 7));
            }
            else
            {
                last.Bits[bit >> 3] &= (byte)~(1 << (bit & 7));
            }
        }
        last.BitsCard = state.card;
        UpgradeBitsPatched++;
        return true;
    }

#if BENCH
    public static readonly Dictionary<string, long> WhyNoUpgradeBits = new();
    private static string _upgradePath = "";
#endif
    /// <summary>Patched bits against the game's: the first ones of the session, then one in <see cref="VerifyEvery"/>. False: they differed (all of this is off).</summary>
    [PatchPart("bits")]
    private static bool CheckPatchedBits(ref NetFullCombatState.CardState state, Last last)
    {
        if (_upgradeBitsChecked < UpgradeBitsCheckedFirst || ++_sinceBitCheck >= VerifyEvery)
        {
            _upgradeBitsChecked++;
            _sinceBitCheck = 0;
            return SameBits(ref state, last);
        }
        return true;
    }

    /// <summary><c>CardState.Serialize</c>: the bits it wrote for this same state last time.</summary>
    [PatchPart("bits")]
    private static bool CardSerializePrefix(ref NetFullCombatState.CardState __instance, PacketWriter writer)
    {
        // Inside RunVanilla the bits written are the reference (SnapshotVerifier): the game's own.
        if (_capturing || !Enabled || ListenerCache.IsSuspended || __instance.card == null || !LimitlessReserialize.CanCompareHere || CardBitsBlocked())
        {
            return true;
        }
        try
        {
            if (!_bySaved.TryGetValue(__instance.card, out Last? last))
            {
                return true;
            }
            int epoch = BitsEpoch();
            if (!SameKeptBits(__instance, last, epoch))
            {
#if BENCH
                _upgradePath = "card ";
#endif
                if (!PatchUpgradeBits(ref __instance, last, epoch))
                {
                    if (!Capture(ref __instance, last, epoch))
                    {
                        return true;
                    }
                }
                else if (!CheckPatchedBits(ref __instance, last))
                {
                    return true;
                }
            }
            else if (++_sinceBitCheck >= VerifyEvery)
            {
                _sinceBitCheck = 0;
                if (!SameBits(ref __instance, last))
                {
                    return true;
                }
            }
            else
            {
                BitsReused++;
            }
            _reserveBits!(writer, last.BitCount);
            int position = writer.BitPosition;
            AppendBits(writer.Buffer, position, last.Bits!, last.BitCount);
            _setBitPosition!(writer, position + last.BitCount);
            return false;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
            return true;
        }
    }

    [PatchPart("bits")]
    private static void RunSerialize(ref NetFullCombatState.CardState state)
    {
        _bitWriter ??= new() { WarnOnGrow = false };
        _bitWriter.Reset();
        _capturing = true;
        try
        {
            state.Serialize(_bitWriter);
        }
        finally
        {
            _capturing = false;
        }
        _bitWriter.ZeroByteRemainder();
    }

    /// <summary>Whether <paramref name="last"/>'s bits were written, in this epoch, for this very state.</summary>
    [PatchPart("bits")]
    private static bool SameKeptBits(in NetFullCombatState.CardState state, Last last, int epoch) =>
        last.Bits != null && last.BitsEpoch == epoch && ReferenceEquals(last.BitsCard, state.card) && Equals(last.BitsAffliction, state.affliction)
        && last.BitsAfflictionCount == state.afflictionCount && last.BitsEnergyCost == state.energyCost && ReferenceEquals(last.BitsKeywords, state.keywords);

    /// <summary>Keeps the game's bits for this state; false (nothing kept) when its object may serialize differently later (<see cref="BitsSafe"/>).</summary>
    [PatchPart("bits")]
    private static bool Capture(ref NetFullCombatState.CardState state, Last last, int epoch)
    {
        if (!BitsSafe(state.card))
        {
            last.Bits = null;
            last.BitsCard = null;
            return false;
        }
        RunSerialize(ref state);
        int bytes = _bitWriter!.BytePosition;
        if (last.Bits == null || last.Bits.Length != bytes)
        {
            last.Bits = new byte[bytes];
        }
        Buffer.BlockCopy(_bitWriter.Buffer, 0, last.Bits, 0, bytes);
        last.BitCount = _bitWriter.BitPosition;
        last.BitsCard = state.card;
        last.BitsAffliction = state.affliction;
        last.BitsAfflictionCount = state.afflictionCount;
        last.BitsEnergyCost = state.energyCost;
        last.BitsKeywords = state.keywords;
        last.BitsEpoch = epoch;
        BitsCaptured++;
        return true;
    }

    /// <summary>The kept bits against the game's method now; a difference stops all of this for the session.</summary>
    [PatchPart("bits")]
    private static bool SameBits(ref NetFullCombatState.CardState state, Last last)
    {
        RunSerialize(ref state);
        BitChecks++;
        if (_bitWriter!.BitPosition == last.BitCount && _bitWriter.Buffer.AsSpan(0, _bitWriter.BytePosition).SequenceEqual(last.Bits))
        {
            return true;
        }
        Mismatches++;
        Disable("the bits kept for a card differ from the game's", $"{state.card.Id} ({last.BitCount} vs {_bitWriter.BitPosition} bits)");
        return false;
    }

    /// <summary>
    /// <c>BitSerializationUtil.WriteBytes</c> for a whole buffer: bits go least significant first, a write keeps the bits
    /// below its position in their byte and zeroes those above its end. <paramref name="source"/> has zeros after its last bit.
    /// </summary>
    private static void AppendBits(byte[] destination, int bitPosition, byte[] source, int bits)
    {
        int at = bitPosition >> 3;
        int shift = bitPosition & 7;
        int bytes = (bits + 7) >> 3;
        if (shift == 0)
        {
            Buffer.BlockCopy(source, 0, destination, at, bytes);
            return;
        }
        int carry = destination[at] & ((1 << shift) - 1);
        int i = 0;
        // Eight bytes at a time (little-endian, bits least significant first): a whole pile is megabytes.
        if (BitConverter.IsLittleEndian)
        {
            ulong wide = (ulong)carry;
            ReadOnlySpan<ulong> from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(source.AsSpan(0, bytes));
            Span<ulong> to = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(destination.AsSpan(at, destination.Length - at));
            int count = Math.Min(from.Length, to.Length);
            for (int w = 0; w < count; w++)
            {
                ulong value = from[w];
                to[w] = wide | (value << shift);
                wide = value >> (64 - shift);
            }
            i = count * 8;
            carry = (int)wide;
        }
        for (; i < bytes; i++)
        {
            int value = source[i];
            destination[at + i] = (byte)(carry | (value << shift));
            carry = value >> (8 - shift);
        }
        if (((bits + 7) >> 3) * 8 - bits < shift)
        {
            destination[at + bytes] = (byte)carry;
        }
    }

    /// <summary>The length of the game's list for this set: how many of <see cref="_keywordValues"/> it holds.</summary>
    private static int DefinedCount(IReadOnlySet<CardKeyword> keywords)
    {
        int count = 0;
        foreach (CardKeyword keyword in _keywordValues)
        {
            if (keywords.Contains(keyword))
            {
                count++;
            }
        }
        return count;
    }

    private static bool AllIn(List<CardKeyword> kept, IReadOnlySet<CardKeyword> keywords)
    {
        for (int i = 0; i < kept.Count; i++)
        {
            if (!keywords.Contains(kept[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        bool replaced = false; // Harmony runs transpilers again each time the method is re-patched.
        foreach (CodeInstruction instruction in instructions)
        {
            if (!replaced && instruction.Calls(_toSerializable!))
            {
                replaced = true;
                _replaced = true;
                yield return new CodeInstruction(OpCodes.Call, Reflect.Method(typeof(SnapshotCardReuse), nameof(ToSerializable)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static int _sinceVerify;
    private static PacketWriter? _reusedWriter, _freshWriter;

    /// <summary><c>card.ToSerializable()</c> for a combat snapshot: the card's previous result when it still holds the same.</summary>
    private static SerializableCard ToSerializable(CardModel card)
    {
        if (ListenerCache.IsSuspended)
        {
            return card.ToSerializable();
        }
        Last? last = null;
        if (Enabled && !LimitlessReserialize.Faulted && LimitlessReserialize.CanCompareHere)
        {
            try
            {
                if (_last.TryGetValue(card, out last) && !LimitlessReserialize.PatchedByOthers()
                    && LimitlessReserialize.SameSerialized(card, last.Saved, last.Data))
                {
#if BENCH
                    // Taken only when the other card's snapshot serializes differently (two copies of a card serialize alike, and
                    // a take that compared equal bits would prove nothing).
                    if (_otherSaved != null && !ReferenceEquals(_otherSaved, last.Saved) && TestWrong.IsArmed("reuse.card")
                        && !SerializeSame(_otherSaved, last.Saved) && TestWrong.Take("reuse.card"))
                    {
                        _sinceVerify = 0;
                        return Verify(card, last, _otherSaved);
                    }
                    _otherSaved = last.Saved;
#endif
                    if (++_sinceVerify >= VerifyEvery)
                    {
                        _sinceVerify = 0;
                        return Verify(card, last);
                    }
                    Reused++;
                    return last.Saved;
                }
            }
            catch (Exception e)
            {
                Disable("exception", e.ToString());
                last = null;
            }
        }
        SerializableCard fresh = card.ToSerializable();
        if (Enabled)
        {
            Remember(card, last, fresh);
        }
        return fresh;
    }

    private static void Remember(CardModel card, Last? last, SerializableCard fresh)
    {
        try
        {
            last ??= _last.GetValue(card, static _ => new Last());
            if (!ReferenceEquals(last.Saved, fresh))
            {
                last.Saved = fresh;
                last.Data = new LimitlessReserialize.StoredData();
                // Not made by Upgraded: whatever bits are kept, this object may differ from theirs by more than its level.
                last.UpgradedFrom = null;
                // The memo described the previous object: a card back to those values later must not get this one (Note sets it again).
                last.MemoStamp = -1;
                last.PlainCard = false;
            }
            Built++;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
        }
    }

    /// <summary>
    /// The reused object serialized next to a fresh one: the fresh one when they differ. Equal, the reused one stays: a new
    /// object here would change the pile's state list, and with it every unchanged pile would be rebuilt at every checksum.
    /// </summary>
    private static SerializableCard Verify(CardModel card, Last last) => Verify(card, last, last.Saved);

    /// <summary><see cref="Verify(CardModel, Last)"/> of <paramref name="reused"/> (test builds: another card's, the positive control).</summary>
    private static SerializableCard Verify(CardModel card, Last last, SerializableCard reused)
    {
        SerializableCard fresh = card.ToSerializable();
        Verified++;
        _reusedWriter ??= new() { WarnOnGrow = false };
        _freshWriter ??= new() { WarnOnGrow = false };
        _reusedWriter.Reset();
        _freshWriter.Reset();
        reused.Serialize(_reusedWriter);
        fresh.Serialize(_freshWriter);
        // Bit lengths too: a write ends with its byte's higher bits zeroed, so bytes alone don't see 1 to 7 more zero bits at the end,
        // which shift every later bit of the checksum's stream.
        if (_reusedWriter.BitPosition != _freshWriter.BitPosition
            || !_reusedWriter.Buffer.AsSpan(0, _reusedWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition)))
        {
            Mismatches++;
            Disable("a reused card snapshot differs from a fresh one", $"{card.Id} ({_reusedWriter.BitPosition} vs {_freshWriter.BitPosition} bits)");
            return fresh;
        }
        return last.Saved;
    }

#if BENCH
    /// <summary>The last reused snapshot of a card (test: another card's, for <c>reuse.card</c>).</summary>
    private static SerializableCard? _otherSaved;

    /// <summary>Whether two snapshots serialize to the same bits (test: <c>reuse.card</c> only takes a different one). Uses
    /// <see cref="Verify(CardModel, Last, SerializableCard)"/>'s writers, which it resets before each use.</summary>
    private static bool SerializeSame(SerializableCard a, SerializableCard b)
    {
        _reusedWriter ??= new() { WarnOnGrow = false };
        _freshWriter ??= new() { WarnOnGrow = false };
        _reusedWriter.Reset();
        _freshWriter.Reset();
        a.Serialize(_reusedWriter);
        b.Serialize(_freshWriter);
        return _reusedWriter.BitPosition == _freshWriter.BitPosition
            && _reusedWriter.Buffer.AsSpan(0, _reusedWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition));
    }

    /// <summary>A copy of <paramref name="states"/> with its first state replaced by another card's, or null when all are the same.</summary>
    private static List<NetFullCombatState.CardState>? WrongStates(List<NetFullCombatState.CardState> states)
    {
        if (OtherState(states, 0) is not { } other)
        {
            return null;
        }
        var copy = new List<NetFullCombatState.CardState>(states);
        copy[0] = other;
        return copy;
    }

    /// <summary>A state of <paramref name="states"/> different from the one at <paramref name="at"/>, or null.</summary>
    private static NetFullCombatState.CardState? OtherState(List<NetFullCombatState.CardState> states, int at)
    {
        for (int i = 0; i < states.Count; i++)
        {
            if (i != at && !SameState(states[i], states[at]))
            {
                return states[i];
            }
        }
        return null;
    }
#endif

    private static void Disable(string why, string details)
    {
        Detections++;
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled ({why}), snapshots build every card again: {details}");
    }
}
