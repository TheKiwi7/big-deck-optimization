using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <c>PlayerCombatState.RecalculateCardValues</c> calls <c>RecalculateValues</c> on every card's enchantment in combat,
/// after every state change and after every affliction applied or removed (<c>AfflictionModel.Amount</c>,
/// <c>ClearAfflictionInternal</c>): when Hex afflicts a whole deck, once per card over every card, 17 million calls at
/// 4,100 cards. Limitless's <c>MultiEnchantment.RecalculateValues</c> recalculates its inner enchantments, then calls
/// <c>SetSerializableCards</c>, where <see cref="LimitlessReserialize"/> compares the inner cards with the saved list:
/// ~0.5 µs each, 10 s of freeze for that Hex.
///
/// When that comparison keeps the list, this records what it read: the saved list and the inner card list (same
/// objects, unchanged), each inner card's upgrade, floor, enchantment and that enchantment's amount, and BaseLib's
/// registered saves. A later <c>RecalculateValues</c> on the same MultiEnchantment does nothing, as vanilla would, when
/// all of it still holds and every inner enchantment keeps the game's empty <c>RecalculateValues</c>: the inner calls
/// are no-ops and SetSerializableCards would keep the list. Only when the inner types save no property (their saved
/// properties would be more inputs) and BaseLib's extended data is known without running getters.
///
/// Off with <see cref="LimitlessReserialize"/> or another mod patching these methods; one skip in
/// <see cref="VerifyEvery"/> is checked with the full comparison (a mismatch or an error turns it off for the session).
///
/// Even then each call reads every memo: ~6 ms per recalculation at 10,000 cards, several per action. So once a whole
/// pass found every card's enchantment doing nothing (its memo holds, or the empty default), the next passes on that player
/// return at once while <see cref="_quiet"/> stands still: it moves when an enchantment's amount changes, a storage
/// card's upgrade or floor, a card's enchantment, a saved list, an inner enchantment added, or a card with a stacked
/// enchantment without a memo that holds (or another enchantment with its own RecalculateValues) entering a pile (the fields' only writers are checked at load). One skipped pass in
/// <see cref="PassVerifyEvery"/> runs for real: a memo not holding then means a change went unseen, and passes stop being
/// skipped for the session. A pass that runs is the game's own: this only watches what its calls do (an exception from the
/// game's or a mod's code there leaves it as the game would, unwatched).
/// </summary>
public static class LimitlessRecalcSkip
{
    public const string Name = "LimitlessRecalcSkip";

    /// <summary>Limitless 1.1.7 to 1.2.1 <c>MultiEnchantment.RecalculateValues</c>: the inner recalculations, then SetSerializableCards.</summary>
    private const string RecalculateValuesFingerprint = "1180845F3EE40FDB";
    /// <summary>Its <c>EnchantmentsOnCards</c> getter: the field once built (read directly here, never built).</summary>
    private const string EnchantmentsOnCardsFingerprint = "0AA01E688AC72C3C";
    /// <summary>v0.107.1 to v0.113.1 <c>EnchantmentModel.RecalculateValues</c>: empty.</summary>
    private const string DefaultRecalculateFingerprint = "23869126C936D164";

    /// <summary>One skip in this many is checked with the full comparison.</summary>
    public const int VerifyEvery = 1024;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    public static long Skipped { get; private set; }
    public static long Verified { get; private set; }
    public static long Mismatches { get; private set; }
    public static long PassesSkipped { get; private set; }
    public static long PassesRun { get; private set; }
    public static long PassMismatches { get; private set; }

    /// <summary>One skipped pass in this many runs for real.</summary>
    public const int PassVerifyEvery = 64;
    /// <summary>v0.107.1 to v0.113.1: every card of every combat pile, its enchantment's RecalculateValues.</summary>
    private const string RecalculateCardValuesFingerprint = "5F467DE7E5B99951";
    private const string AllCardsFingerprint = "7893F6315D894A5A";
    /// <summary>Limitless 1.1.7 to 1.2.1: resets the inner list of a clone (read back from its saved list).</summary>
    private const string DeepCloneFieldsFingerprint = "B0598AD4D39BD71C";

    /// <summary>
    /// Bumps: the low 32 bits count those from the game's main thread, the high ones those from any other thread (see
    /// <see cref="Bump"/>). One word, so one atomic read gives both and any change moves it.
    /// </summary>
    private static long _quiet;
    private const long OffMainBumpUnit = 1L << 32;
    private static long _quietAt = -1;
    private static long _quietData = -1;
    // Weak: a strong one would keep the whole last run alive once the player has left it.
    private static readonly WeakReference<PlayerCombatState?> _quietState = new(null);
    private static int _sincePassCheck;
    private static bool _passSkip;
    private static MethodInfo? _pass;
    /// <summary>
    /// What the game's pass walks (AllPiles.SelectMany). Quiet passes are judged on the same AllPiles getter, piles a mod adds
    /// through it included (BaseLib's SpecialPileInCombat transpiles it), and on cards entering any pile through AddInternal:
    /// a patch on AllPiles needs no back-off (one would turn passes off for every BaseLib user), a patch on AllCards does.
    /// </summary>
    private static MethodInfo? _allCards;
    private static Type? _multi, _storageCard;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _defaultRecalculates = new();

    /// <summary>A running pass of the game's is watched (main thread, the outermost one): whether every call did nothing so far.</summary>
    private static bool _passTracked, _passQuiet, _passWasQuiet;
    private static long _passVersion, _passData;

    private static MethodInfo? _original;
    /// <summary>The inner list's getter, which a skipped recalculation doesn't call.</summary>
    private static MethodInfo? _getter;
    private static MethodInfo? _defaultRecalculate;
    private static AccessTools.FieldRef<object, List<CardModel>?>? _innerCards;
    private static AccessTools.FieldRef<List<CardModel>, int>? _cardsVersion;
    private static AccessTools.FieldRef<List<SerializableCard>, int>? _storedVersion;
    private static LimitlessReserialize.ExtendedData? _cardData;
    private static LimitlessReserialize.ExtendedData? _enchantmentData;

    public static void Apply(Harmony harmony)
    {
        if (!LimitlessReserialize.Applied)
        {
            throw new PatchSkippedException("needs LimitlessReserialize (applies with it if Limitless loads later)");
        }
        Type multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        MethodInfo original = AccessTools.Method(multi, "RecalculateValues", Type.EmptyTypes)
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "RecalculateValues");
        PatchSafety.RequireFingerprint(original, RecalculateValuesFingerprint);
        _getter = AccessTools.PropertyGetter(multi, "EnchantmentsOnCards")
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "get_EnchantmentsOnCards");
        PatchSafety.RequireFingerprint(_getter, EnchantmentsOnCardsFingerprint);
        MethodInfo defaultRecalculate = AccessTools.Method(typeof(EnchantmentModel), nameof(EnchantmentModel.RecalculateValues), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(EnchantmentModel), nameof(EnchantmentModel.RecalculateValues));
        PatchSafety.RequireFingerprint(defaultRecalculate, DefaultRecalculateFingerprint);
        _innerCards = AccessTools.FieldRefAccess<List<CardModel>?>(multi, "_enchantmentsOnCards");
        // List<T>'s mutation counter: bumped by every change to the list (Add, Remove, indexer set...).
        _cardsVersion = AccessTools.FieldRefAccess<List<CardModel>, int>("_version");
        _storedVersion = AccessTools.FieldRefAccess<List<SerializableCard>, int>("_version");
        (_cardData, _enchantmentData) = LimitlessReserialize.Data();
        _original = original;
        _defaultRecalculate = defaultRecalculate;
        PatchGuard.Patch(harmony, original, prefix: new HarmonyMethod(typeof(LimitlessRecalcSkip), nameof(Prefix)));
        Applied = true;
        _multi = multi;
        // What PatchedByOthers asks about (display only); LimitlessReserialize's part is registered under its own name.
        PatchSafety.Watch(Name, new MethodBase?[] { _original, _defaultRecalculate, _getter });
        PatchSafety.WatchNote(Name, () => BaseLibTypes.NoteSpireFields(Name, "their stacked enchantments are recalculated as Limitless does"));
        try
        {
            ApplyPasses(harmony, multi);
            PatchSafety.Watch(Name, new MethodBase?[] { _pass, _allCards }); // What the pass prefix asks about.
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: recalculation passes not skipped: {e.Message}");
        }
    }

    private static void ApplyPasses(Harmony harmony, Type multi)
    {
        _pass = AccessTools.Method(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues)) ?? throw new MissingMethodException(nameof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues));
        PatchSafety.RequireFingerprint(_pass, RecalculateCardValuesFingerprint);
        _allCards = AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards)) ?? throw new MissingMethodException(nameof(PlayerCombatState), "AllCards");
        PatchSafety.RequireFingerprint(_allCards, AllCardsFingerprint);
        MethodInfo deepClone = AccessTools.Method(multi, "DeepCloneFields") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "DeepCloneFields");
        PatchSafety.RequireFingerprint(deepClone, DeepCloneFieldsFingerprint);
        _storageCard = AccessTools.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.EnchantmentStorageCard")
            ?? AccessTools.AllTypes().FirstOrDefault(t => t.Name == "EnchantmentStorageCard") ?? throw new TypeLoadException("EnchantmentStorageCard");
        // What the memos read changes through these only; the saved list through its setter only (bumped below).
        LimitlessReserialize.RequireKnownSavedListUsers(multi);
        PatchSafety.RequireFieldWriters(typeof(EnchantmentModel), "_amount", "set_Amount");
        PatchSafety.RequireFieldWriters(typeof(CardModel), "_currentUpgradeLevel", "set_CurrentUpgradeLevel");
        PatchSafety.RequireFieldWriters(typeof(CardModel), "_floorAddedToDeck", "set_FloorAddedToDeck");
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Enchantment>k__BackingField", "set_Enchantment");
        PatchSafety.RequireFieldWriters(multi, "_enchantmentsOnCards", "get_EnchantmentsOnCards", "DeepCloneFields");
        // And the list the getter hands out changes only in AddEnchantment (bumped around it): no other code may change it.
        LimitlessHookSkip.RequireKnownInnerListWriters(multi);
        var bump = new HarmonyMethod(typeof(LimitlessRecalcSkip), nameof(BumpQuiet));
        var bumpCard = new HarmonyMethod(typeof(LimitlessRecalcSkip), nameof(BumpQuietIfStorage));
        MethodInfo Setter(Type type, string property) => AccessTools.PropertySetter(type, property) ?? throw new MissingMethodException(type.Name, "set_" + property);
        PatchGuard.Patch(harmony, Setter(typeof(EnchantmentModel), nameof(EnchantmentModel.Amount)), prefix: bump);
        PatchGuard.Patch(harmony, Setter(typeof(CardModel), nameof(CardModel.CurrentUpgradeLevel)), prefix: bumpCard);
        PatchGuard.Patch(harmony, Setter(typeof(CardModel), nameof(CardModel.FloorAddedToDeck)), prefix: bumpCard);
        PatchGuard.Patch(harmony, Setter(typeof(CardModel), nameof(CardModel.Enchantment)), prefix: bump);
        PatchGuard.Patch(harmony, Setter(multi, "SerializableEnchantmentsOnCards"), prefix: bump);
        PatchGuard.Patch(harmony, AccessTools.Method(multi, "AddEnchantment") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "AddEnchantment"), prefix: bump, postfix: bump);
        PatchGuard.Patch(harmony, deepClone, prefix: bump);
        // A card only enters a pile through this method (no other code changes the pile lists).
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        // A finalizer: AddInternal raises its events after the list changed, and a handler throwing there leaves the card in.
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)) ?? throw new MissingMethodException(nameof(CardPile), nameof(CardPile.AddInternal)),
            finalizer: new HarmonyMethod(typeof(LimitlessRecalcSkip), nameof(CardEntered)));
        PatchGuard.Patch(harmony, _pass, prefix: new HarmonyMethod(typeof(LimitlessRecalcSkip), nameof(PassPrefix)),
            finalizer: new HarmonyMethod(typeof(LimitlessRecalcSkip), nameof(PassFinalizer)));
        _passSkip = true;
    }

    /// <summary>
    /// Something a pass reads changed: the next pass runs. Atomic, the setters being global; a bump from a thread other
    /// than the game's main one (none today) during a watched pass makes it not quiet without calling it a mismatch when
    /// it is all that moved (the change was seen, not one the pass's calls made unseen). Both counts share one word: a
    /// separate flag could be cleared by the pass's start between its write and the increment, and a later increment
    /// then read as a mismatch.
    /// </summary>
    private static void Bump()
    {
        System.Threading.Interlocked.Add(ref _quiet, LimitlessReserialize.OnMainThread ? 1 : OffMainBumpUnit);
    }

    private static void BumpQuiet() => Bump();

    private static void BumpQuietIfStorage(CardModel __instance)
    {
        if (ReferenceEquals(__instance.GetType(), _storageCard))
        {
            Bump();
        }
    }

    /// <summary>
    /// Called when a card enters a pile (or tried to): an enchantment whose recalculation may do something, a stacked one
    /// without a memo or any other type with its own RecalculateValues (Adroit, a mod's), means the next pass runs. Until
    /// v1.0.2 only stacked ones counted: a bare enchantment (Limitless's uncapped enchantments off) entering after a quiet
    /// pass wasn't recalculated until a check pass.
    /// </summary>
    private static void CardEntered(Exception? __exception, CardModel card)
    {
        try
        {
            // A stacked one counts as quiet only if its memo still holds: one made before the last quiet pass, while its card
            // was in no pile, may have gone stale unseen by that pass.
            if (_passSkip && card.Enchantment is { } enchantment && !KeepsDefault(enchantment.GetType())
                && !(ReferenceEquals(enchantment.GetType(), _multi) && _memos.TryGetValue(enchantment, out Memo? memo) && memo.Holds(enchantment)))
            {
                Bump();
            }
        }
        catch
        {
            Bump(); // Can't tell: the next pass runs.
        }
    }

    /// <summary>
    /// Skips the pass when the last ones found every call doing nothing and nothing moved since; otherwise lets the game's
    /// pass run (each stacked enchantment's own call then goes through <see cref="Prefix"/>) and watches it. Until v1.0.2
    /// this ran its own copy of the pass: an exception from an enchantment's code then made the game's pass run again from
    /// its first card, and turned the skip off for an error that wasn't this mod's.
    /// </summary>
    private static bool PassPrefix(PlayerCombatState __instance, out bool __state)
    {
        __state = false;
        if (_passTracked)
        {
            _passQuiet = false; // A pass inside a pass: not watched, the outer one counts as not quiet.
            return true;
        }
        if (!_passSkip || !Enabled || !LimitlessReserialize.Enabled || !LimitlessReserialize.CanCompareHere)
        {
            return true;
        }
        try
        {
            // Another mod's patch on AllCards could add cards this never sees enter a pile (OnlyDefaultsBesidesStacked, CardEntered).
            // A mod loaded after the load check that may change an inner list without bumping the quiet state: passes run.
            // No tolerance: a quiet pass is skipped whole.
            if (PatchSafety.IsPatchedByOthers(_pass) || PatchSafety.IsPatchedByOthers(_allCards) || PatchedByOthers()
                || !LimitlessHookSkip.InnerListWritersKnown)
            {
                return true;
            }
            // Also what BaseLib's data depends on (a save registered, a type loaded): a skipped pass would miss it.
            long dataStamp = LimitlessReserialize.DataVersion();
            bool wasQuiet = _quietAt == System.Threading.Interlocked.Read(ref _quiet) && dataStamp >= 0 && dataStamp == _quietData
                && _quietState.TryGetTarget(out PlayerCombatState? quietState) && ReferenceEquals(quietState, __instance);
            if (wasQuiet && ++_sincePassCheck < PassVerifyEvery)
            {
                PassesSkipped++;
                return false;
            }
            _sincePassCheck = 0;
            PassesRun++;
            _passVersion = System.Threading.Interlocked.Read(ref _quiet);
            _passData = dataStamp;
            _passWasQuiet = wasQuiet;
            _passQuiet = true;
            _passTracked = true;
            __state = true;
        }
        catch (Exception e)
        {
            StopPasses(e);
        }
        return true;
    }

    /// <summary>
    /// After a watched pass: quiet when every call did nothing (memos held, the rest keep the empty default) and nothing
    /// moved meanwhile. An exception from the pass is the game's, passed on unchanged: nothing is concluded from that pass.
    /// </summary>
    private static void PassFinalizer(Exception? __exception, PlayerCombatState __instance, bool __state)
    {
        if (!__state)
        {
            return;
        }
        _passTracked = false;
        try
        {
            if (__exception != null)
            {
                _quietAt = -1;
                return;
            }
            long now = System.Threading.Interlocked.Read(ref _quiet);
            bool quiet = _passQuiet && now == _passVersion && OnlyDefaultsBesidesStacked(__instance);
            // A bump from another thread during the pass (a hover tip's new enchantment, say): not quiet, but no mismatch when
            // such bumps are all that moved (no main-thread bump, every call did nothing). Read from the same word as the version.
            bool onlyOffMainMoved = now != _passVersion && (now & (OffMainBumpUnit - 1)) == (_passVersion & (OffMainBumpUnit - 1));
            if (_passWasQuiet && !quiet && !(onlyOffMainMoved && _passQuiet && OnlyDefaultsBesidesStacked(__instance)))
            {
                PassMismatches++;
                _passSkip = false;
                Log.Error($"[{ModEntry.Id}] {Name}: a stacked enchantment changed unseen between recalculations, every pass runs from now on");
            }
            _quietAt = quiet ? _passVersion : -1;
            _quietData = _passData;
            _quietState.SetTarget(__instance);
        }
        catch (Exception e)
        {
            StopPasses(e);
        }
    }

    /// <summary>Whether every card's enchantment in the piles is a stacked one (judged by its calls) or keeps the empty default.</summary>
    private static bool OnlyDefaultsBesidesStacked(PlayerCombatState state)
    {
        IReadOnlyList<CardPile> piles = state.AllPiles;
        Type? last = null;
        for (int l = 0; l < piles.Count; l++)
        {
            IReadOnlyList<CardModel> cards = piles[l].Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                Type? type = cards[i].Enchantment?.GetType();
                if (type == null || ReferenceEquals(type, last) || ReferenceEquals(type, _multi))
                {
                    continue;
                }
                if (!KeepsDefault(type))
                {
                    return false;
                }
                last = type;
            }
        }
        return true;
    }

    /// <summary>This mod's own error around a pass: passes stop being skipped (the game's pass runs, unwatched).</summary>
    private static void StopPasses(Exception e)
    {
        _passTracked = false;
        _passSkip = false;
        Log.Error($"[{ModEntry.Id}] {Name}: recalculation passes not skipped any more, after an error: {e}");
    }

    /// <summary>Whether the type keeps the game's empty RecalculateValues (any thread: card moves may happen off the main one).</summary>
    private static bool KeepsDefault(Type type) => _defaultRecalculates.GetOrAdd(type, static t =>
        AccessTools.Method(t, nameof(EnchantmentModel.RecalculateValues), Type.EmptyTypes)?.DeclaringType == typeof(EnchantmentModel));

    /// <summary>What the last comparison that kept a MultiEnchantment's list read, to tell when it would keep it again.</summary>
    private sealed class Memo
    {
        public List<SerializableCard>? Stored;
        public int StoredVersion;
        public List<CardModel>? Cards;
        public int CardsVersion;
        public CardModel?[] Inner = Array.Empty<CardModel?>();
        public int[] Upgrades = Array.Empty<int>();
        public int?[] Floors = Array.Empty<int?>();
        public EnchantmentModel?[] Enchantments = Array.Empty<EnchantmentModel?>();
        public int[] Amounts = Array.Empty<int>();
        public int Count;
        public int CardSaves, EnchantmentSaves, SpireFields;
        public long BaseLibChanges;
        public bool Valid;

        public bool Holds(object multi)
        {
            if (!Valid)
            {
                return false;
            }
            List<SerializableCard>? stored = LimitlessReserialize.Stored!(multi);
            List<CardModel>? cards = _innerCards!(multi);
            if (!ReferenceEquals(stored, Stored) || stored == null || _storedVersion!(stored) != StoredVersion
                || !ReferenceEquals(cards, Cards) || cards == null || _cardsVersion!(cards) != CardsVersion || cards.Count != Count
                || _cardData!.SavesCount != CardSaves || _enchantmentData!.SavesCount != EnchantmentSaves
                || BaseLibTypes.Changes != BaseLibChanges || BaseLibTypes.SpireFieldCount() != SpireFields)
            {
                return false;
            }
            for (int i = 0; i < Count; i++)
            {
                CardModel card = cards[i];
                if (!ReferenceEquals(card, Inner[i]) || card.CurrentUpgradeLevel != Upgrades[i] || card.FloorAddedToDeck != Floors[i]
                    || !card.IsMutable)
                {
                    return false;
                }
                EnchantmentModel? enchantment = card.Enchantment;
                if (!ReferenceEquals(enchantment, Enchantments[i]) || enchantment!.Amount != Amounts[i] || !enchantment.IsMutable)
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static readonly ConditionalWeakTable<EnchantmentModel, Memo> _memos = new();

    /// <summary>Per enchantment or card type: saves no property, and (enchantments) keeps the empty RecalculateValues.</summary>
    private static readonly Dictionary<Type, bool> _plainTypes = new();

    private static int _plainSpireFields = -1;

    private static bool Plain(Type type, bool enchantment)
    {
        int spireFields = BaseLibTypes.SpireFieldCount();
        if (spireFields != _plainSpireFields)
        {
            _plainTypes.Clear(); // A SavedSpireField registered since may target one of these types.
            _plainSpireFields = spireFields;
        }
        if (!_plainTypes.TryGetValue(type, out bool plain))
        {
            plain = LimitlessReserialize.SavesNoProperty(type) && (!enchantment
                || AccessTools.Method(type, nameof(EnchantmentModel.RecalculateValues), Type.EmptyTypes)?.DeclaringType == typeof(EnchantmentModel));
            _plainTypes[type] = plain;
        }
        return plain;
    }

    /// <summary>
    /// Called by <see cref="LimitlessReserialize"/> when its comparison kept <paramref name="multi"/>'s list for
    /// <paramref name="cards"/>: records what it read, when every input of that comparison is in the record.
    /// </summary>
    internal static void Remember(EnchantmentModel multi, List<CardModel> cards, LimitlessReserialize.ExtendedData cardData,
        LimitlessReserialize.ExtendedData enchantmentData)
    {
        if (!Applied || !Enabled)
        {
            return;
        }
        try
        {
            Memo memo = _memos.GetValue(multi, static _ => new Memo());
            memo.Valid = false;
            List<SerializableCard>? stored = LimitlessReserialize.Stored!(multi);
            if (stored == null || !ReferenceEquals(cards, _innerCards!(multi)) || !cardData.KnownWithoutBuilding(out _, out _)
                || !enchantmentData.KnownWithoutBuilding(out _, out _))
            {
                return;
            }
            int count = cards.Count;
            if (memo.Inner.Length < count)
            {
                // Doubled: a stack grows one enchantment type at a time.
                int size = Math.Max(Math.Max(count, memo.Inner.Length * 2), 4);
                memo.Inner = new CardModel?[size];
                memo.Upgrades = new int[size];
                memo.Floors = new int?[size];
                memo.Enchantments = new EnchantmentModel?[size];
                memo.Amounts = new int[size];
            }
            for (int i = 0; i < count; i++)
            {
                CardModel? card = cards[i];
                EnchantmentModel? enchantment = card?.Enchantment;
                // Inner cards are Limitless's storage cards: the only card type whose upgrade and floor move the passes' quiet state.
                if (card == null || enchantment == null || (_storageCard != null && !ReferenceEquals(card.GetType(), _storageCard))
                    || !Plain(card.GetType(), enchantment: false) || !Plain(enchantment.GetType(), enchantment: true))
                {
                    Array.Clear(memo.Inner, 0, i);
                    Array.Clear(memo.Enchantments, 0, i);
                    return;
                }
                memo.Inner[i] = card;
                memo.Upgrades[i] = card.CurrentUpgradeLevel;
                memo.Floors[i] = card.FloorAddedToDeck;
                memo.Enchantments[i] = enchantment;
                memo.Amounts[i] = enchantment.Amount;
            }
            for (int i = count; i < memo.Inner.Length; i++)
            {
                memo.Inner[i] = null;
                memo.Enchantments[i] = null;
            }
            memo.Count = count;
            memo.Stored = stored;
            memo.StoredVersion = _storedVersion!(stored);
            memo.Cards = cards;
            memo.CardsVersion = _cardsVersion!(cards);
            memo.CardSaves = cardData.SavesCount;
            memo.EnchantmentSaves = enchantmentData.SavesCount;
            memo.BaseLibChanges = BaseLibTypes.Changes;
            // A SavedSpireField registered later may target these types (Plain judged them without it).
            memo.SpireFields = BaseLibTypes.SpireFieldCount();
            memo.Valid = true;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
        }
    }

    /// <summary>Called by <see cref="LimitlessReserialize"/> when its comparison found a change.</summary>
    internal static void Forget(EnchantmentModel multi)
    {
        if (Applied && _memos.TryGetValue(multi, out Memo? memo))
        {
            memo.Valid = false;
        }
    }

    private static int _patchCheckCalls;
    private static bool _patchedByOthers = true;

    /// <summary>
    /// Another mod patching what a skip leaves out or relies on. This runs for every card at every recalculation (17
    /// million times for one Hex at 4,100 cards): rechecked every 1,024 calls instead of reading the clock each time.
    /// </summary>
    private static bool PatchedByOthers()
    {
        if (--_patchCheckCalls < 0)
        {
            _patchCheckCalls = 1023;
            // No tolerance: memoized calls are skipped.
            _patchedByOthers = PatchSafety.IsPatchedByOthers(_original) || PatchSafety.IsPatchedByOthers(_defaultRecalculate)
                || PatchSafety.IsPatchedByOthers(_getter) || LimitlessReserialize.PatchedByOthers();
        }
        return _patchedByOthers;
    }

    private static int _sinceVerify;

    private static bool Prefix(EnchantmentModel __instance)
    {
        bool runs = Runs(__instance);
        if (runs && _passTracked)
        {
            _passQuiet = false; // The watched pass ran this one: something may have changed.
        }
        return runs;
    }

    /// <summary>Whether Limitless's RecalculateValues runs (false: skipped, it would do nothing).</summary>
    private static bool Runs(EnchantmentModel __instance)
    {
        if (!Enabled || !LimitlessReserialize.Enabled || !LimitlessReserialize.CanCompareHere)
        {
            return true;
        }
        try
        {
            if (!_memos.TryGetValue(__instance, out Memo? memo) || !memo.Holds(__instance) || PatchedByOthers())
            {
                return true;
            }
            if (++_sinceVerify >= VerifyEvery)
            {
                _sinceVerify = 0;
                Verified++;
                if (!LimitlessReserialize.WouldKeep(__instance, memo.Cards))
                {
                    Mismatches++;
                    Disable("a skipped recalculation would have changed the saved list", $"{__instance.Id} on {__instance.Card?.Id}");
                    return true;
                }
            }
            Skipped++;
            return false;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
            return true;
        }
    }

    private static void Disable(string why, string details)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled ({why}), Limitless recalculates as usual: {details}");
    }
}
