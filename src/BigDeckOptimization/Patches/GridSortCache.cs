using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Sorting the deck view (<c>NCardGrid.SetCards</c>) compares cards with lambdas that recompute per comparison: the
/// position in the previous order with <c>List.IndexOf</c> (O(n) each, the tie-break of every sort), the resolved
/// energy cost, and a culture-aware title comparison. With ~1000 cards a sort click froze the game 30–110 ms.
///
/// Inside <c>SetCards</c>, those lambdas are answered from per-card values computed once: index map (same first-match
/// equality as <c>IndexOf</c>), resolved cost, and culture sort keys (same order as <c>CompareInfo.Compare</c>). Each
/// comparer is only replaced when its fingerprint matches the vanilla code mirrored here. Display order only. The values sit
/// in arrays at each card's slot (<see cref="Scope.Slot"/>: the last two cards' slots kept, as a comparison asks every order
/// with the same two cards and the sort compares one pivot with many), one sort key per distinct title (two cards with the same
/// title compare equal without comparing keys): 10,340 cards took 62 ms of dictionary lookups in <c>List.Sort</c> (2026-11-21).
/// The sort itself is still the game's <c>List.Sort</c> with the game's comparison: every answer has the sign the game's would.
/// The index map is built at the first index comparison of a <c>SetCards</c> call and kept for the rest of it: exact while
/// <c>SetCards</c> only replaces <c>_cards</c> after its sort, which its fingerprint pins (else only the index orders stay vanilla).
/// </summary>
public static class GridSortCache
{
    public const string Name = "GridSortCache";

    public static bool Enabled { get; set; } = true;

    /// <summary>Sorts that ran with the cache.</summary>
    public static long Sorts { get; private set; }

    private enum Kind { Index, Cost, Title }

    private readonly record struct Mirrored(Kind Kind, bool Negated, string Fingerprint);

    /// <summary>The vanilla lambdas (v0.107.1) this mirrors, by sort order.</summary>
    private static readonly Dictionary<SortingOrders, Mirrored> _mirrored = new()
    {
        [SortingOrders.Ascending] = new(Kind.Index, false, "DE12C95E5519E854"),
        [SortingOrders.Descending] = new(Kind.Index, true, "E94902F8936585D0"),
        [SortingOrders.CostAscending] = new(Kind.Cost, false, "648A4CCAAD331018"),
        [SortingOrders.CostDescending] = new(Kind.Cost, true, "D796A804BFE3EB87"),
        [SortingOrders.AlphabetAscending] = new(Kind.Title, false, "04A0BD058E5D07FF"),
        [SortingOrders.AlphabetDescending] = new(Kind.Title, true, "CC335A28B09AE1DA"),
    };

    /// <summary>v0.107.1 to v0.113.1: <c>SetCards</c> sorts <c>_cardsCache</c> and refills <c>_cards</c> only after the sort.</summary>
    private const string SetCardsFingerprint = "224B19A92FC91776";

    private static readonly ConditionalWeakTable<Dictionary<SortingOrders, Func<CardModel, CardModel, int>>, object> _wrapped = new();
    private static readonly Dictionary<(SortingOrders, System.Reflection.MethodInfo), bool> _fingerprintOk = new();
    private static AccessTools.FieldRef<NCardGrid, List<CardModel>>? _cards;
    private static System.Reflection.MethodInfo? _setCards;

    /// <summary>
    /// Per-card values for the SetCards call in progress (main thread only). Each card gets a slot (its number in this scope) the
    /// first time it is compared, and its values live in arrays at that slot, each computed the first time an order needs it. A
    /// comparison asks every order of the priority list with the same two cards, and the sort compares one pivot with many cards:
    /// the last two cards' slots are kept (<see cref="Slot"/>), so most comparisons look up no dictionary at all.
    /// </summary>
    internal sealed class Scope(NCardGrid grid)
    {
        public readonly NCardGrid Grid = grid;
        public Dictionary<CardModel, int>? Index;
        public CompareInfo? Culture;
        /// <summary>Cards given a slot, and the values per slot: not computed yet while <see cref="Unknown"/>.</summary>
        private readonly Dictionary<CardModel, int> _slots = new(ReferenceEqualityComparer.Instance);
        public const int Unknown = int.MinValue;
        public int[] IndexAt = Array.Empty<int>(), CostAt = Array.Empty<int>(), TitleAt = Array.Empty<int>();
        /// <summary>The distinct titles' sort keys, by title id (<see cref="TitleAt"/>; -1 for a null title), and each title's id.</summary>
        public readonly List<SortKey> TitleKeys = new();
        public readonly Dictionary<string, int> TitleIds = new(StringComparer.Ordinal);
        private CardModel? _cardA, _cardB;
        private int _slotA, _slotB;
        /// <summary>Whether A was used last (on a miss, the other one is replaced).</summary>
        private bool _lastA;

        public int Values => _slots.Count;

        public int Slot(CardModel card)
        {
            if (ReferenceEquals(card, _cardA))
            {
                _lastA = true;
                return _slotA;
            }
            if (ReferenceEquals(card, _cardB))
            {
                _lastA = false;
                return _slotB;
            }
            if (!_slots.TryGetValue(card, out int slot))
            {
                slot = _slots.Count;
                _slots.Add(card, slot);
                if (slot == IndexAt.Length)
                {
                    int size = Math.Max(64, slot * 2);
                    IndexAt = Grown(IndexAt, size);
                    CostAt = Grown(CostAt, size);
                    TitleAt = Grown(TitleAt, size);
                }
            }
            if (_lastA)
            {
                _cardB = card;
                _slotB = slot;
                _lastA = false;
            }
            else
            {
                _cardA = card;
                _slotA = slot;
                _lastA = true;
            }
            return slot;
        }

        private static int[] Grown(int[] values, int size)
        {
            int[] grown = new int[size];
            Array.Copy(values, grown, values.Length);
            Array.Fill(grown, Unknown, values.Length, size - values.Length);
            return grown;
        }
        /// <summary>The comparers dictionary already checked in this call: the sort reads the getter at every comparison.</summary>
        public Dictionary<SortingOrders, Func<CardModel, CardModel, int>>? Wrapped;
        private Dictionary<System.Reflection.MethodInfo, bool>? _patched;
        /// <summary>The comparer delegate asked last and its answer: a sort asks about the same delegate at every comparison.</summary>
        private Delegate? _lastComparer;
        private bool _lastPatched;

        /// <summary>
        /// Another mod patches this comparer: the game's comparer answers then, as its patch would see every comparison.
        /// Asked once per comparer and SetCards call (no tolerance); the same delegate again is one reference compare.
        /// </summary>
        public bool Patched(Delegate comparer)
        {
            if (ReferenceEquals(comparer, _lastComparer))
            {
                return _lastPatched;
            }
            System.Reflection.MethodInfo method = comparer.Method;
            _patched ??= new Dictionary<System.Reflection.MethodInfo, bool>();
            if (!_patched.TryGetValue(method, out bool patched))
            {
                patched = PatchSafety.IsPatchedByOthers(method);
                _patched[method] = patched;
            }
            _lastComparer = comparer;
            _lastPatched = patched;
            return patched;
        }
    }

    [ThreadStatic] private static Scope? _scope;

    /// <summary>Per kind, whether its value code loads on this game (its own class: a changed member only loses those orders).</summary>
    private static bool _costLoads, _titleLoads;

    /// <summary>Whether SetCards is the code the index map relies on (<see cref="SetCardsFingerprint"/>).</summary>
    private static bool _indexOk;

    public static void Apply(Harmony harmony)
    {
        _costLoads = Loads(typeof(GridSortCost));
        _titleLoads = Loads(typeof(GridSortTitle));
        _cards = Reflect.FieldRefAccess<NCardGrid, List<CardModel>>("_cards");
        System.Reflection.MethodInfo setCards = Reflect.Method(typeof(NCardGrid), nameof(NCardGrid.SetCards))
            ?? throw new MissingMethodException(nameof(NCardGrid), nameof(NCardGrid.SetCards));
        _setCards = setCards;
        string setCardsActual = MethodFingerprint.Of(setCards);
        _indexOk = PatchSafety.FingerprintMatches(setCardsActual, SetCardsFingerprint);
        if (!_indexOk)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: NCardGrid.SetCards changed (fingerprint {setCardsActual}, expected {SetCardsFingerprint}): index orders left vanilla");
        }
        PatchGuard.Patch(harmony, Reflect.PropertyGetter(typeof(NCardGrid), "SortingAlgorithms"),
            postfix: new HarmonyMethod(typeof(GridSortCache), nameof(SortingAlgorithmsPostfix)));
        // The scope ends in a postfix first of the postfixes (the highest priority Harmony takes, above Priority.First: only a
        // postfix at that same priority registered earlier sorts before it): another mod's postfix sorting the grid again (with
        // the grid's own comparers, after SetCards refilled _cards) runs outside it, on the game's comparers. The finalizer ends
        // it after a throw.
        PatchGuard.Patch(harmony, setCards,
            prefix: new HarmonyMethod(typeof(GridSortCache), nameof(SetCardsPrefix)),
            postfix: new HarmonyMethod(typeof(GridSortCache), nameof(SetCardsPostfix)) { priority = int.MaxValue },
            finalizer: new HarmonyMethod(typeof(GridSortCache), nameof(SetCardsFinalizer)));
    }

#if BENCH
    /// <summary>Bench: runs <paramref name="action"/> as if inside <c>SetCards</c> on <paramref name="grid"/>.</summary>
    internal static void RunInScope(NCardGrid grid, Action action)
    {
        Scope? previous = _scope;
        _scope = Enabled ? new Scope(grid) : null;
        try
        {
            action();
        }
        finally
        {
            _scope = previous;
        }
    }
#endif

    private static void SetCardsPrefix(NCardGrid __instance, out Scope? __state)
    {
        __state = _scope;
        _scope = Enabled && !SetCardsRewritten() ? new Scope(__instance) : null;
    }

    private static int _rewrittenEpoch = -1;
    private static bool _rewritten;

    /// <summary>
    /// Another mod's transpiler (or inner patch) on SetCards: its body may then refill <c>_cards</c> before the sort, which the
    /// index map relies on not happening (the fingerprint pins the game's own body only). No scope then. Prefixes stand (they
    /// run before the sort reads anything), postfixes run after the scope ended. Asked again after each Patch or Unpatch.
    /// </summary>
    private static bool SetCardsRewritten()
    {
        int epoch = HarmonyWatch.Epoch;
        if (epoch != _rewrittenEpoch || !HarmonyWatch.Applied)
        {
            _rewrittenEpoch = epoch;
            HarmonyLib.Patches? info = _setCards == null ? null : Harmony.GetPatchInfo(_setCards);
            _rewritten = info != null && info.Transpilers.Concat(info.InnerPrefixes).Concat(info.InnerPostfixes).Any(p => !PatchGuard.IsOurs(p.owner))
                || (_setCards != null && HarmonyWatch.HiddenOwners(_setCards).Length > 0);
        }
        return _rewritten;
    }

    private static void SetCardsPostfix(Scope? __state)
    {
        if (_scope is { } scope && scope.Values > 0)
        {
            Sorts++;
        }
        _scope = __state;
    }

    private static void SetCardsFinalizer(Scope? __state) => _scope = __state;

    /// <summary>Wraps each grid's comparers once (the dictionary is created lazily per grid).</summary>
    private static void SortingAlgorithmsPostfix(Dictionary<SortingOrders, Func<CardModel, CardModel, int>> __result)
    {
        try
        {
            Scope? scope = _scope;
            if (__result == null || (scope != null && ReferenceEquals(scope.Wrapped, __result)))
            {
                return;
            }
            if (scope != null)
            {
                scope.Wrapped = __result;
            }
            if (_wrapped.TryGetValue(__result, out _))
            {
                return;
            }
            _wrapped.Add(__result, new object());
            foreach ((SortingOrders order, Mirrored mirrored) in _mirrored)
            {
                if (__result.TryGetValue(order, out Func<CardModel, CardModel, int>? original) && IsMirrored(order, original, mirrored))
                {
                    PatchSafety.Watch(Name, original.Method); // Display: what Compare asks about.
                    __result[order] = (a, b) => Compare(mirrored, original, a, b);
                }
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    private static bool Loads(Type values)
    {
        try
        {
            PatchSafety.RequireResolvable(values);
            PatchGuard.Register(Name, values, Array.Empty<PatchGuard.Installed>());
            return true;
        }
        catch (PatchSkippedException e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {values.Name} left vanilla: {e.Message}");
            return false;
        }
    }

    private static bool IsMirrored(SortingOrders order, Func<CardModel, CardModel, int> original, Mirrored mirrored)
    {
        // Per order and comparer method: another mod may put its own comparer for some grids only.
        if (!_fingerprintOk.TryGetValue((order, original.Method), out bool ok))
        {
            string actual = MethodFingerprint.Of(original.Method);
            bool same = PatchSafety.FingerprintMatches(actual, mirrored.Fingerprint);
            ok = mirrored.Kind switch { Kind.Cost => _costLoads, Kind.Title => _titleLoads, _ => _indexOk } && same;
            _fingerprintOk[(order, original.Method)] = ok;
            if (!same)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {order} comparer changed (fingerprint {actual}, expected {mirrored.Fingerprint}): left vanilla");
            }
        }
        return ok;
    }

    private static int Compare(Mirrored mirrored, Func<CardModel, CardModel, int> original, CardModel a, CardModel b)
    {
        Scope? scope = _scope;
        if (scope == null || !Enabled || a == null || b == null || scope.Patched(original))
        {
            return original(a!, b!);
        }
        try
        {
            int result;
            switch (mirrored.Kind)
            {
                case Kind.Index:
                    // The index lambda reads the grid it belongs to: only answer for that grid.
                    if (original.Target != scope.Grid)
                    {
                        return original(a, b);
                    }
                    result = IndexOf(scope, a).CompareTo(IndexOf(scope, b));
                    break;
                case Kind.Cost:
                    result = GridSortCost.Of(scope, a).CompareTo(GridSortCost.Of(scope, b));
                    break;
                default:
                    int ta = GridSortTitle.Of(scope, a), tb = GridSortTitle.Of(scope, b);
                    if (ta < 0 || tb < 0)
                    {
                        return original(a, b);
                    }
                    // One title id per distinct string (ordinal): the same title compares equal, as string.Compare does.
                    result = ta == tb ? 0 : SortKey.Compare(scope.TitleKeys[ta], scope.TitleKeys[tb]);
                    break;
            }
            return mirrored.Negated ? -result : result;
        }
        catch (Exception e)
        {
            Disable(e);
            return original(a, b);
        }
    }

    /// <summary>First index of each card, with the default equality <c>List.IndexOf</c> uses.</summary>
    private static Dictionary<CardModel, int> BuildIndex(List<CardModel> cards)
    {
        Dictionary<CardModel, int> index = new(cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i] != null)
            {
                index.TryAdd(cards[i], i);
            }
        }
        return index;
    }

    /// <summary>The card's first index in the grid's previous order (<c>List.IndexOf</c>), from the index map once per card.</summary>
    private static int IndexOf(Scope scope, CardModel card)
    {
        int slot = scope.Slot(card);
        int i = scope.IndexAt[slot];
        if (i == Scope.Unknown)
        {
            Dictionary<CardModel, int> index = scope.Index ??= BuildIndex(_cards!(scope.Grid));
            i = index.TryGetValue(card, out int at) ? at : -1;
            scope.IndexAt[slot] = i;
        }
        return i;
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
    }
}

/// <summary><see cref="GridSortCache"/>'s cost per card, in its own class: a changed cost API only leaves the cost orders vanilla.</summary>
internal static class GridSortCost
{
    internal static int Of(GridSortCache.Scope scope, CardModel card)
    {
        int slot = scope.Slot(card);
        int cost = scope.CostAt[slot];
        if (cost == GridSortCache.Scope.Unknown)
        {
            cost = card.EnergyCost.GetResolved();
            if (cost == GridSortCache.Scope.Unknown)
            {
                // The marker itself (int.MinValue): not kept, read again (never seen: a cost).
                return cost;
            }
            scope.CostAt[slot] = cost;
        }
        return cost;
    }
}

/// <summary>
/// <see cref="GridSortCache"/>'s title per card as a title id (<see cref="GridSortCache.Scope.TitleKeys"/>: one sort key per
/// distinct title, with the options vanilla compares with, None), -1 for a null title (vanilla handles it). Own class, as
/// <see cref="GridSortCost"/>.
/// </summary>
internal static class GridSortTitle
{
    internal static int Of(GridSortCache.Scope scope, CardModel card)
    {
        int slot = scope.Slot(card);
        int id = scope.TitleAt[slot];
        if (id == GridSortCache.Scope.Unknown)
        {
            string? title = card.Title;
            if (title == null)
            {
                id = -1;
            }
            else if (!scope.TitleIds.TryGetValue(title, out id))
            {
                scope.Culture ??= LocManager.Instance.CultureInfo.CompareInfo;
                id = scope.TitleKeys.Count;
                scope.TitleKeys.Add(scope.Culture.GetSortKey(title, CompareOptions.None));
                scope.TitleIds.Add(title, id);
            }
            scope.TitleAt[slot] = id;
        }
        return id;
    }
}
