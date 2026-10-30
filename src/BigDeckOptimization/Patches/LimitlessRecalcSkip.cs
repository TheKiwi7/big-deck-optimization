using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
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
/// </summary>
public static class LimitlessRecalcSkip
{
    public const string Name = "LimitlessRecalcSkip";

    /// <summary>Limitless v1.1.7 <c>MultiEnchantment.RecalculateValues</c>: the inner recalculations, then SetSerializableCards.</summary>
    private const string RecalculateValuesFingerprint = "1180845F3EE40FDB";
    /// <summary>Its <c>EnchantmentsOnCards</c> getter: the field once built (read directly here, never built).</summary>
    private const string EnchantmentsOnCardsFingerprint = "0AA01E688AC72C3C";
    /// <summary>v0.107.1 and v0.111 <c>EnchantmentModel.RecalculateValues</c>: empty.</summary>
    private const string DefaultRecalculateFingerprint = "23869126C936D164";

    /// <summary>One skip in this many is checked with the full comparison.</summary>
    public const int VerifyEvery = 1024;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    public static long Skipped { get; private set; }
    public static long Verified { get; private set; }
    public static long Mismatches { get; private set; }

    private static MethodInfo? _original;
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
        PatchSafety.RequireFingerprint(AccessTools.PropertyGetter(multi, "EnchantmentsOnCards")
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "get_EnchantmentsOnCards"), EnchantmentsOnCardsFingerprint);
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
    }

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
        public int CardSaves, EnchantmentSaves;
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
                || BaseLibTypes.Changes != BaseLibChanges)
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

    private static bool Plain(Type type, bool enchantment)
    {
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
                int size = Math.Max(count, 4);
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
                if (card == null || enchantment == null || !Plain(card.GetType(), enchantment: false) || !Plain(enchantment.GetType(), enchantment: true))
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
            _patchedByOthers = PatchSafety.IsPatchedByOthers(_original) || PatchSafety.IsPatchedByOthers(_defaultRecalculate)
                || LimitlessReserialize.PatchedByOthers();
        }
        return _patchedByOthers;
    }

    private static int _sinceVerify;

    private static bool Prefix(EnchantmentModel __instance)
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
