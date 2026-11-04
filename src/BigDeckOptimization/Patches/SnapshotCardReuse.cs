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
/// every card, appends the bits the game's method (with other mods' transpilers) produced for the same state last time.
///
/// A card that only got upgraded (some relics upgrade thousands of cards per action) gets a new snapshot object sharing
/// the previous one's parts instead of a whole new tree: those trees outlive several collections, which made them slow.
///
/// A pile that didn't change has the same state list and the same bits: while the pile's list version stands, no listener
/// changes keywords or costs, and its <see cref="PileLast.Writes"/> stands still (every write to what a card's state reads,
/// those fields' only writers checked at load, counts on the pile the card was last read in: a card that changes pile
/// changes both list versions), the pile's whole state costs one comparison per checksum instead of a read of every card,
/// and its bits one copy. Cards with no memo (saved properties) are compared every time. One pile in
/// <see cref="PileVerifyEvery"/> is read for real: finding a change then means a write went unseen, and piles stop being
/// trusted for the session. A pile that did change gets its bits from its cards' kept bits, without a call per card.
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

    /// <summary>One trusted pile in this many is read for real.</summary>
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
    private static bool _headerOnly;
    private static AccessTools.FieldRef<CardEnergyCost, CardModel>? _costCard;
    private static AccessTools.FieldRef<EnchantmentModel, CardModel?>? _enchantmentCard;
    private static AccessTools.FieldRef<AfflictionModel, CardModel?>? _afflictionCard;
    private static int _sincePileCheck, _sincePileBits;
    private static AccessTools.FieldRef<CardPile, List<CardModel>>? _pileCards;
    private static AccessTools.FieldRef<List<CardModel>, int>? _listVersion;
    private static MethodInfo? _pileSerialize;
    private static readonly ConditionalWeakTable<List<NetFullCombatState.CardState>, PileLast> _byList = new();

    /// <summary>v0.107.1 (and v0.111) code mirrored by <see cref="PilePrefix"/> and <see cref="CardFrom"/>.</summary>
    private const string CardFromFingerprint = "96C28FA2E2A751FA";
    private const string PileFromFingerprint = "9112142DF335981C";
    private const string WriteListFingerprint = "7E6764DE8659A79F";

    private static MethodInfo? _toSerializable;
    private static MethodInfo? _cardFrom, _pileFrom;
    private static bool _replaced;
    private static CardKeyword[] _keywordValues = Array.Empty<CardKeyword>();

    /// <summary>v0.107.1 (and v0.111) code read without allocating by <see cref="CardFrom"/>.</summary>
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
                    CombatCardsReleased++;
                    entry.Card = null!;
                    entry.Enchantment = null;
                    entry.In = null;
                    entry.MemoStamp = -1;
                    entry.Dirty = false;
                }
            }
        }

        /// <summary>The game's bits before the first card of a list of <see cref="HeaderCards"/> cards.</summary>
        public byte[]? Header;
        public int HeaderBits;
        public int HeaderCards = -1;
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
        _toSerializable = AccessTools.Method(typeof(CardModel), nameof(CardModel.ToSerializable), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.ToSerializable));
        MethodInfo from = AccessTools.Method(typeof(NetFullCombatState.CardState), nameof(NetFullCombatState.CardState.From), new[] { typeof(CardModel) })
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
        try
        {
            _pileFrom = AccessTools.Method(typeof(NetFullCombatState.CombatPileState), nameof(NetFullCombatState.CombatPileState.From), new[] { typeof(CardPile) })
                ?? throw new MissingMethodException("NetFullCombatState.CombatPileState", "From");
            PatchSafety.RequirePartResolvable(typeof(SnapshotCardReuse), "piles");
            PatchSafety.RequireFingerprint(from, CardFromFingerprint);
            PatchSafety.RequireFingerprint(_pileFrom, PileFromFingerprint);
            _cardFrom = from;
            _keywordValues = Enum.GetValues<CardKeyword>();
            _storedVersion = AccessTools.FieldRefAccess<List<SerializableCard>, int>("_version");
            ApplyFastReads();
            PatchGuard.Patch(harmony, _pileFrom, prefix: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(PilePrefix)));
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(Player), nameof(Player.AfterCombatEnd)), postfix: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(ReleaseCombat)));
            ApplyBits(harmony);
            ApplyTrust(harmony);
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: pile lists left vanilla: {e.Message}");
        }
    }

    private static void ApplyFastReads()
    {
        try
        {
            _keywordsMethod = AccessTools.Method(typeof(CardModel), nameof(CardModel.GetKeywordsWithSources)) ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.GetKeywordsWithSources));
            MethodInfo local = AccessTools.PropertyGetter(typeof(CardModel), "LocalKeywords") ?? throw new MissingMethodException(nameof(CardModel), "LocalKeywords");
            _withModifiers = AccessTools.Method(typeof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers)) ?? throw new MissingMethodException(nameof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers));
            PatchSafety.RequireFingerprint(_keywordsMethod, KeywordsFingerprint);
            PatchSafety.RequireFingerprint(local, LocalKeywordsFingerprint);
            PatchSafety.RequireFingerprint(_withModifiers, WithModifiersFingerprint);
            _localKeywords = AccessTools.MethodDelegate<Func<CardModel, HashSet<CardKeyword>>>(local);
            _costBase = AccessTools.FieldRefAccess<CardEnergyCost, int>("_base");
            _costLocal = AccessTools.FieldRefAccess<CardEnergyCost, List<LocalCostModifier>>("_localModifiers");
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
            Type multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType) ?? throw new TypeLoadException(LimitlessReserialize.MultiEnchantmentType);
            PatchSafety.RequireFieldWriters(multi, "<SerializableEnchantmentsOnCards>k__BackingField", ".ctor", "set_SerializableEnchantmentsOnCards");
            PatchSafety.RequireFieldWriters(typeof(EnchantmentModel), "_amount", "set_Amount");
            PatchSafety.RequireFieldWriters(typeof(AfflictionModel), "_amount", "set_Amount");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_currentUpgradeLevel", "set_CurrentUpgradeLevel");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_floorAddedToDeck", "set_FloorAddedToDeck");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "<Enchantment>k__BackingField", "set_Enchantment");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "<Affliction>k__BackingField", "set_Affliction");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_keywords", "DeepCloneFields", "DowngradeInternal", "get_LocalKeywords");
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_energyCost", "DeepCloneFields", "MockSetEnergyCost", "get_EnergyCost");
            string[] costWriters = { ".ctor", "AddThisCombat", "AddThisTurn", "AddThisTurnOrUntilPlayed", "AddUntilPlayed", "AfterCardPlayedCleanup", "Clone", "EndOfTurnCleanup", "SetThisCombat", "SetThisTurn", "SetThisTurnOrUntilPlayed", "SetUntilPlayed" };
            PatchSafety.RequireFieldWriters(typeof(CardEnergyCost), "_localModifiers", costWriters);
            PatchSafety.RequireFieldWriters(typeof(CardEnergyCost), "_base", ".ctor", "Clone", "GetWithModifiers", "ResetForDowngrade", "SetCustomBaseCost", "UpgradeBy");
            _pileCards = AccessTools.FieldRefAccess<CardPile, List<CardModel>>("_cards");
            _listVersion = AccessTools.FieldRefAccess<List<CardModel>, int>("_version");
            _costCard = AccessTools.FieldRefAccess<CardEnergyCost, CardModel>("_card");
            _enchantmentCard = AccessTools.FieldRefAccess<EnchantmentModel, CardModel?>("_card");
            _afflictionCard = AccessTools.FieldRefAccess<AfflictionModel, CardModel?>("_card");
            void Mark(Type type, string method, string prefix)
            {
#if BENCH
                // Harmony's __originalMethod costs a method lookup per call (~2 µs): only when writes are counted per method.
                if (CountWritesByMethod)
                {
                    prefix += "Counted";
                }
#endif
                PatchGuard.Patch(harmony, AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method),
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
            _cardSerialize = AccessTools.Method(typeof(NetFullCombatState.CardState), nameof(NetFullCombatState.CardState.Serialize), new[] { typeof(PacketWriter) })
                ?? throw new MissingMethodException("NetFullCombatState.CardState", "Serialize");
            Type util = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Serialization.BitSerializationUtil") ?? throw new TypeLoadException("BitSerializationUtil");
            PatchSafety.RequireFingerprint(AccessTools.Method(util, "WriteBytes") ?? throw new MissingMethodException("BitSerializationUtil", "WriteBytes"), WriteBytesFingerprint);
            MethodInfo resize = AccessTools.Method(typeof(PacketWriter), "ResizeBufferIfNecessary") ?? throw new MissingMethodException(nameof(PacketWriter), "ResizeBufferIfNecessary");
            PatchSafety.RequireFingerprint(resize, ResizeFingerprint);
            _reserveBits = AccessTools.MethodDelegate<Action<PacketWriter, int>>(resize);
            _setBitPosition = AccessTools.MethodDelegate<Action<PacketWriter, int>>(AccessTools.PropertySetter(typeof(PacketWriter), nameof(PacketWriter.BitPosition))
                ?? throw new MissingMethodException(nameof(PacketWriter), "set_BitPosition"));
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
            _pileSerialize = AccessTools.Method(typeof(NetFullCombatState.CombatPileState), nameof(NetFullCombatState.CombatPileState.Serialize), new[] { typeof(PacketWriter) })
                ?? throw new MissingMethodException("NetFullCombatState.CombatPileState", "Serialize");
            // From the caller: once its callers reach tier 1 the JIT inlines this small method, and a patch on it no longer runs.
            MethodInfo writeList = AccessTools.FirstMethod(typeof(PacketWriter), m => m.Name == nameof(PacketWriter.WriteList) && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                ?? throw new MissingMethodException(nameof(PacketWriter), nameof(PacketWriter.WriteList));
            PatchSafety.RequireFingerprint(writeList, WriteListFingerprint);
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(NetFullCombatState.PlayerState), nameof(NetFullCombatState.PlayerState.Serialize), new[] { typeof(PacketWriter) })
                ?? throw new MissingMethodException("NetFullCombatState.PlayerState", "Serialize"), transpiler: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(PlayerSerializeTranspiler)));
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: pile bits not reused, each card's still are: {e.Message}");
        }
    }

    /// <summary>What every card of a pile shares in one snapshot.</summary>
    /// <summary>
    /// <see cref="Listeners"/>: the cost and keyword listeners of the combat, each with its amount and owner, when every one of them
    /// answers from what a read covers (<see cref="PureHooks"/>); null when one doesn't, and then no pile is trusted.
    /// </summary>
    private readonly record struct Reads(long Stamp, bool NoKeywordHook, bool NoCostHook, HookListener[]? Listeners);

    private readonly record struct HookListener(AbstractModel Model, int Amount, object? Owner);

    private static readonly HookListener[] NoListeners = Array.Empty<HookListener>();

    /// <summary>
    /// Cost and keyword listeners whose answer for a card depends only on the card's owner, type, base cost and local modifiers,
    /// keywords, affliction or pile type (all of it written through the counted writers, or fixed for a pile's cards) and on the
    /// listener's own amount and owner (v0.107.1 and v0.111). While the same listeners stand with the same amounts, a pile's
    /// unwritten cards keep the costs and keywords they had. Not Void Form or Brilliant Scarf: they count the cards played this turn.
    /// </summary>
    private static readonly (string Type, string Method, string Fingerprint)[] PureHooks =
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
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
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
        if (!_fastReads || !HookFilter.Active || PatchSafety.IsPatchedByOthers(_keywordsMethod) || PatchSafety.IsPatchedByOthers(_withModifiers)
            || pile.Cards.Count == 0 || pile.Cards[0].Owner?.Creature.CombatState is not CombatState combat)
        {
            return new Reads(stamp, false, false, null);
        }
        List<AbstractModel>? keywords = HookFilter.ForCombat(combat, HookFilter.Keywords);
        List<AbstractModel>? early = HookFilter.ForCombat(combat, HookFilter.EnergyCost);
        List<AbstractModel>? late = HookFilter.ForCombat(combat, HookFilter.EnergyCostLate);
        bool noKeywords = keywords is { Count: 0 };
        bool noCost = early is { Count: 0 } && late is { Count: 0 };
        return new Reads(stamp, noKeywords, noCost, noKeywords && noCost ? NoListeners : PureListeners(keywords, early, late));
    }

    /// <summary><c>CombatPileState.From</c>: the pile's previous list while every card's state equals its entry.</summary>
    [PatchPart("piles")]
    private static bool PilePrefix(CardPile pile, ref NetFullCombatState.CombatPileState __result)
    {
        // Inside RunVanilla the result is the reference this code is compared with (SnapshotVerifier, bdperf): the game's own.
        if (!Enabled || ListenerCache.IsSuspended || !LimitlessReserialize.CanCompareHere || LimitlessReserialize.Faulted
            || PatchSafety.IsPatchedByOthers(_pileFrom) || PatchSafety.IsPatchedByOthers(_cardFrom))
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
                    if (verifyMoved)
                    {
                        Last? fresh = entry;
                        NetFullCombatState.CardState read = CardFrom(card, ref fresh, reads);
                        if (!SameState(read, state))
                        {
                            PileMismatches++;
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
        result.keywords = KeywordsOf(card, entry, reads.NoKeywordHook);
        CardEnergyCost cost = card.EnergyCost;
        int withModifiers = reads.NoCostHook ? LocalCost(card, cost) : cost.GetWithModifiers(CostModifiers.All);
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
            SerializableCard fresh = card.ToSerializable();
            Remember(card, entry, fresh);
            Register(entry, fresh);
            Note(card, entry, entry.MemoStamp);
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
        if (stamp < 0 || !card.IsMutable || !LimitlessReserialize.MemoPlan(card, out cardList)
            || (enchantment != null && (!enchantment.IsMutable || !LimitlessReserialize.MemoPlan(enchantment, out getter))))
        {
            return;
        }
        last.PlainCard = cardList == null;
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

    /// <summary><c>card.Keywords</c> (<c>GetKeywordsWithSources(All)</c>) as the snapshot's enum-ordered list; null when empty.</summary>
    [PatchPart("piles")]
    private static List<CardKeyword>? KeywordsOf(CardModel card, Last? last, bool noHook)
    {
        IReadOnlySet<CardKeyword> keywords;
        if (noHook)
        {
            keywords = _localKeywords!(card); // No listener can add or remove any, in or out of combat.
        }
        else if (_fastReads && card.CombatState is { } combat && !card.IsCanonical)
        {
            HashSet<CardKeyword> scratch = _keywordScratch ??= new HashSet<CardKeyword>();
            scratch.Clear();
            scratch.UnionWith(_localKeywords!(card));
            MegaCrit.Sts2.Core.Hooks.Hook.ModifyKeywordsInCombat(combat, card, scratch);
            keywords = scratch;
        }
        else
        {
            keywords = card.Keywords;
        }
        if (keywords.Count == 0)
        {
            return null;
        }
        List<CardKeyword>? kept = last?.Keywords;
        if (kept != null && kept.Count == keywords.Count && AllIn(kept, keywords))
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

    private static IEnumerable<CodeInstruction> PlayerSerializeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo { Name: nameof(PacketWriter.WriteList), IsGenericMethod: true } method
                && method.GetGenericArguments()[0] == typeof(NetFullCombatState.CombatPileState) && method.GetParameters().Length == 2)
            {
                // Same stack: (writer, list, lengthBits).
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SnapshotCardReuse), nameof(WritePiles)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    /// <summary><c>writer.WriteList(piles, lengthBits)</c>, each pile through <see cref="PileSerializePrefix"/>.</summary>
    [PatchPart("bits")]
    private static void WritePiles(PacketWriter writer, IReadOnlyList<NetFullCombatState.CombatPileState> piles, int lengthBits)
    {
        if (!Enabled)
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
            || SerializePatched() || PatchedOthersOnPile())
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
                bool composed = !ReferenceEquals(last.BitsFor, __instance.cards) && Compose(ref __instance, last);
#if BENCH
                CountPileBits((ReferenceEquals(last.BitsFor, __instance.cards) ? (composed ? "composed" : "same list") : "compose failed: " + _composeFail) + $" {__instance.pileType}");
#endif
                if (ReferenceEquals(last.BitsFor, __instance.cards) && ++_sincePileBits < PileVerifyEvery)
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
            }
#if BENCH
            else
            {
                CountPileBits($"list not from a read {__instance.pileType} {__instance.cards.Count}");
            }
#endif
            if (!Enabled)
            {
                return true;
            }
            _sincePileBits = 0;
            __state = writer.BitPosition;
            return true;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
            return true;
        }
    }

    /// <summary>Keeps what the game's method just wrote; when bits were kept for this list, they must be the same.</summary>
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
            int bits = writer.BitPosition - __state;
            byte[] taken = new byte[(bits + 7) >> 3];
            ExtractBits(writer.Buffer, __state, taken, bits);
            if (ReferenceEquals(last.BitsFor, __instance.cards) && (last.BitCount != bits || !taken.AsSpan().SequenceEqual(last.Bits.AsSpan(0, taken.Length))))
            {
                Mismatches++;
                Disable("the bits kept for a pile differ from the game's", $"{__instance.pileType} ({last.BitCount} vs {bits} bits)");
                return;
            }
            last.Bits = taken;
            last.BitCount = bits;
            last.BitsFor = __instance.cards;
            last.Offsets = null;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
        }
    }

    /// <summary>
    /// A changed pile's bits, without a call per card: what the game's method writes before the first card (from that
    /// method, with no card written), then each card's kept bits. <see cref="PileVerifyEvery"/> compares the result with the game's.
    /// A card's bits depend on its state alone: the longest runs of the same states at the start and at the end of the previous
    /// list (a card added at the end, cards drawn from the top) are copied from the previous bits as two blocks.
    /// </summary>
    [PatchPart("bits")]
    private static bool Compose(ref NetFullCombatState.CombatPileState state, PileLast last)
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
            && !ReferenceEquals(previous, cards))
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
            if (!ReferenceEquals(entry.BitsCard, card.card) || entry.Bits == null || !Equals(entry.BitsAffliction, card.affliction)
                || entry.BitsAfflictionCount != card.afflictionCount || entry.BitsEnergyCost != card.energyCost || !ReferenceEquals(entry.BitsKeywords, card.keywords))
            {
#if BENCH
                _upgradePath = "pile ";
#endif
                if (!PatchUpgradeBits(ref card, entry))
                {
                    Capture(ref card, entry);
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
        if (last.HeaderCards != count)
        {
            CaptureHeader(ref state, last);
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

    [PatchPart("bits")]
    private static void CaptureHeader(ref NetFullCombatState.CombatPileState state, PileLast last)
    {
        _bitWriter ??= new() { WarnOnGrow = false };
        _bitWriter.Reset();
        _headerOnly = true;
        try
        {
            state.Serialize(_bitWriter);
        }
        finally
        {
            _headerOnly = false;
        }
        _bitWriter.ZeroByteRemainder();
        last.Header = _bitWriter.Buffer.AsSpan(0, _bitWriter.BytePosition).ToArray();
        last.HeaderBits = _bitWriter.BitPosition;
        last.HeaderCards = state.cards.Count;
    }

    private static long _pileOthersAt = long.MinValue;
    private static bool _pileOthers;

    /// <summary>As for a card's bits: only transpilers (their effect is in the bits) leave a pile's reusable.</summary>
    private static bool PatchedOthersOnPile()
    {
        long now = Environment.TickCount64;
        if (now - _pileOthersAt >= 2000)
        {
            _pileOthersAt = now;
            HarmonyLib.Patches? info = Harmony.GetPatchInfo(_pileSerialize);
            _pileOthers = info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Any(p => !PatchGuard.IsOurs(p.owner));
        }
        return _pileOthers;
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
    private static long _patchedCheckAt = long.MinValue;
    private static bool _serializePatched;
    private static int _sinceBitCheck;

    /// <summary>Another mod's prefix, postfix or finalizer on <c>CardState.Serialize</c> might not be pure: transpilers are, in the captured bits.</summary>
    private static bool SerializePatched()
    {
        long now = Environment.TickCount64;
        if (now - _patchedCheckAt >= 2000)
        {
            _patchedCheckAt = now;
            HarmonyLib.Patches? info = Harmony.GetPatchInfo(_cardSerialize);
            _serializePatched = info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Any(p => !PatchGuard.IsOurs(p.owner));
        }
        return _serializePatched;
    }

    // --- Upgrade bits: a card whose only change is its upgrade level keeps its bits but those 8 ---

    /// <summary>v0.107.1 and v0.111: the card first (<c>writer.Write(card)</c>), then its affliction, cost and keywords.</summary>
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
    private static MethodInfo? _serializableCardSerialize;
    private static int _upgradeBitsChecked;

    private static void ApplyUpgradeBits()
    {
        PatchSafety.RequireFingerprint(_cardSerialize!, CardStateSerializeFingerprint);
        _serializableCardSerialize = AccessTools.Method(typeof(SerializableCard), nameof(SerializableCard.Serialize))
            ?? throw new MissingMethodException(nameof(SerializableCard), nameof(SerializableCard.Serialize));
        PatchSafety.RequireFingerprint(_serializableCardSerialize, SerializableCardSerializeFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry))
            ?? throw new MissingMethodException(nameof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry)), WriteModelEntryFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(PacketWriter), nameof(PacketWriter.WriteInt))
            ?? throw new MissingMethodException(nameof(PacketWriter), nameof(PacketWriter.WriteInt)), WriteIntFingerprint);
        _entryIdBits = AccessTools.MethodDelegate<Func<int>>(AccessTools.PropertyGetter(typeof(ModelIdSerializationCache), nameof(ModelIdSerializationCache.EntryIdBitSize))
            ?? throw new MissingMethodException(nameof(ModelIdSerializationCache), "get_EntryIdBitSize"));
    }

    /// <summary>
    /// The kept bits of a card <see cref="Upgraded"/> remade from the very object they were written for, with the rest of the state
    /// the same: its upgrade level written into them as <c>WriteInt(level, 8)</c> would (the low byte, least significant bit first,
    /// right after the entry id), instead of serializing the card again with its enchantments. Only while the card's own
    /// serialization has no transpiler (prefixes and postfixes write before or after these bits, the same as when they were kept).
    /// </summary>
    [PatchPart("bits")]
    private static bool PatchUpgradeBits(ref NetFullCombatState.CardState state, Last last)
    {
        if (!UpgradeBits || _entryIdBits == null || last.Bits == null || last.UpgradedFrom == null || !ReferenceEquals(last.BitsCard, last.UpgradedFrom)
            || !ReferenceEquals(last.Saved, state.card) || !Equals(last.BitsAffliction, state.affliction) || last.BitsAfflictionCount != state.afflictionCount
            || last.BitsEnergyCost != state.energyCost || !ReferenceEquals(last.BitsKeywords, state.keywords) || CardSerializeTranspiled())
        {
#if BENCH
            if (last.UpgradedFrom == null || !ReferenceEquals(last.Saved, state.card))
            {
                return false; // Not an upgrade: any other card's first or new capture.
            }
            string why = !UpgradeBits || _entryIdBits == null ? "off" : last.Bits == null ? "no bits" : last.UpgradedFrom == null ? "not upgraded" : !ReferenceEquals(last.BitsCard, last.UpgradedFrom) ? "bits of another object"
                : !ReferenceEquals(last.Saved, state.card) ? "other saved" : last.BitsEnergyCost != state.energyCost ? "cost" : !ReferenceEquals(last.BitsKeywords, state.keywords) ? "keywords" : CardSerializeTranspiled() ? "transpiled" : "affliction";
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

    private static long _transpiledCheckAt = long.MinValue;
    private static bool _transpiled;

    /// <summary>
    /// A transpiler on <c>SerializableCard.Serialize</c> could move the upgrade bits; Unlimited's (fingerprinted by RelicStateReuse)
    /// only widens list counts, and this method writes no list.
    /// </summary>
    private static bool CardSerializeTranspiled()
    {
        long now = Environment.TickCount64;
        if (now - _transpiledCheckAt >= 2000)
        {
            _transpiledCheckAt = now;
            MethodInfo? unlimited = RelicStateReuse.UnlimitedTranspilerMethod;
            _transpiled = Harmony.GetPatchInfo(_serializableCardSerialize)?.Transpilers.Any(t => unlimited == null || t.PatchMethod.MethodHandle != unlimited.MethodHandle) == true;
        }
        return _transpiled;
    }

    /// <summary><c>CardState.Serialize</c>: the bits it wrote for this same state last time.</summary>
    [PatchPart("bits")]
    private static bool CardSerializePrefix(ref NetFullCombatState.CardState __instance, PacketWriter writer)
    {
        if (_headerOnly)
        {
            return false;
        }
        if (_capturing || !Enabled || __instance.card == null || !LimitlessReserialize.CanCompareHere || SerializePatched())
        {
            return true;
        }
        try
        {
            if (!_bySaved.TryGetValue(__instance.card, out Last? last))
            {
                return true;
            }
            if (last.Bits == null || !ReferenceEquals(last.BitsCard, __instance.card) || !Equals(last.BitsAffliction, __instance.affliction)
                || last.BitsAfflictionCount != __instance.afflictionCount || last.BitsEnergyCost != __instance.energyCost || !ReferenceEquals(last.BitsKeywords, __instance.keywords))
            {
#if BENCH
                _upgradePath = "card ";
#endif
                if (!PatchUpgradeBits(ref __instance, last))
                {
                    Capture(ref __instance, last);
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

    [PatchPart("bits")]
    private static void Capture(ref NetFullCombatState.CardState state, Last last)
    {
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
        BitsCaptured++;
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
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SnapshotCardReuse), nameof(ToSerializable)))
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
    private static SerializableCard Verify(CardModel card, Last last)
    {
        SerializableCard fresh = card.ToSerializable();
        Verified++;
        _reusedWriter ??= new() { WarnOnGrow = false };
        _freshWriter ??= new() { WarnOnGrow = false };
        _reusedWriter.Reset();
        _freshWriter.Reset();
        last.Saved.Serialize(_reusedWriter);
        fresh.Serialize(_freshWriter);
        if (!_reusedWriter.Buffer.AsSpan(0, _reusedWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition)))
        {
            Mismatches++;
            Disable("a reused card snapshot differs from a fresh one", $"{card.Id} ({_reusedWriter.BytePosition} vs {_freshWriter.BytePosition} bytes)");
            return fresh;
        }
        return last.Saved;
    }

    private static void Disable(string why, string details)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled ({why}), snapshots build every card again: {details}");
    }
}
