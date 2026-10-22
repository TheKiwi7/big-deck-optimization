using System;
using System.Collections.Generic;
using System.Globalization;
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
/// comparer is only replaced when its fingerprint matches the vanilla code mirrored here. Display order only.
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

    private static readonly ConditionalWeakTable<Dictionary<SortingOrders, Func<CardModel, CardModel, int>>, object> _wrapped = new();
    private static readonly Dictionary<SortingOrders, bool> _fingerprintOk = new();
    private static AccessTools.FieldRef<NCardGrid, List<CardModel>>? _cards;

    /// <summary>Per-card values for the SetCards call in progress (main thread only).</summary>
    private sealed class Scope(NCardGrid grid)
    {
        public readonly NCardGrid Grid = grid;
        public Dictionary<CardModel, int>? Index;
        public readonly Dictionary<CardModel, int> Cost = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<CardModel, SortKey?> Title = new(ReferenceEqualityComparer.Instance);
        public CompareInfo? Culture;
    }

    [ThreadStatic] private static Scope? _scope;

    public static void Apply(Harmony harmony)
    {
        _cards = AccessTools.FieldRefAccess<NCardGrid, List<CardModel>>("_cards");
        harmony.Patch(AccessTools.PropertyGetter(typeof(NCardGrid), "SortingAlgorithms"),
            postfix: new HarmonyMethod(typeof(GridSortCache), nameof(SortingAlgorithmsPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NCardGrid), nameof(NCardGrid.SetCards)),
            prefix: new HarmonyMethod(typeof(GridSortCache), nameof(SetCardsPrefix)),
            finalizer: new HarmonyMethod(typeof(GridSortCache), nameof(SetCardsFinalizer)));
    }

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

    private static void SetCardsPrefix(NCardGrid __instance, out Scope? __state)
    {
        __state = _scope;
        _scope = Enabled ? new Scope(__instance) : null;
    }

    private static void SetCardsFinalizer(Scope? __state)
    {
        if (_scope is { } scope && (scope.Index != null || scope.Cost.Count > 0 || scope.Title.Count > 0))
        {
            Sorts++;
        }
        _scope = __state;
    }

    /// <summary>Wraps each grid's comparers once (the dictionary is created lazily per grid).</summary>
    private static void SortingAlgorithmsPostfix(Dictionary<SortingOrders, Func<CardModel, CardModel, int>> __result)
    {
        try
        {
            if (__result == null || _wrapped.TryGetValue(__result, out _))
            {
                return;
            }
            _wrapped.Add(__result, new object());
            foreach ((SortingOrders order, Mirrored mirrored) in _mirrored)
            {
                if (__result.TryGetValue(order, out Func<CardModel, CardModel, int>? original) && IsMirrored(order, original, mirrored))
                {
                    __result[order] = (a, b) => Compare(mirrored, original, a, b);
                }
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    private static bool IsMirrored(SortingOrders order, Func<CardModel, CardModel, int> original, Mirrored mirrored)
    {
        if (!_fingerprintOk.TryGetValue(order, out bool ok))
        {
            string actual = MethodFingerprint.Of(original.Method);
            ok = actual == mirrored.Fingerprint;
            _fingerprintOk[order] = ok;
            if (!ok)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {order} comparer changed (fingerprint {actual}, expected {mirrored.Fingerprint}): left vanilla");
            }
        }
        return ok;
    }

    private static int Compare(Mirrored mirrored, Func<CardModel, CardModel, int> original, CardModel a, CardModel b)
    {
        Scope? scope = _scope;
        if (scope == null || !Enabled || a == null || b == null)
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
                    Dictionary<CardModel, int> index = scope.Index ??= BuildIndex(_cards!(scope.Grid));
                    result = IndexOf(index, a).CompareTo(IndexOf(index, b));
                    break;
                case Kind.Cost:
                    result = Cost(scope, a).CompareTo(Cost(scope, b));
                    break;
                default:
                    SortKey? ka = TitleKey(scope, a), kb = TitleKey(scope, b);
                    if (ka == null || kb == null)
                    {
                        return original(a, b);
                    }
                    result = SortKey.Compare(ka, kb);
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

    private static int IndexOf(Dictionary<CardModel, int> index, CardModel card) => index.TryGetValue(card, out int i) ? i : -1;

    private static int Cost(Scope scope, CardModel card)
    {
        if (!scope.Cost.TryGetValue(card, out int cost))
        {
            cost = card.EnergyCost.GetResolved();
            scope.Cost[card] = cost;
        }
        return cost;
    }

    /// <summary>Sort key with the options vanilla compares with (None); null for a null title (vanilla handles it).</summary>
    private static SortKey? TitleKey(Scope scope, CardModel card)
    {
        if (!scope.Title.TryGetValue(card, out SortKey? key))
        {
            string? title = card.Title;
            scope.Culture ??= LocManager.Instance.CultureInfo.CompareInfo;
            key = title == null ? null : scope.Culture.GetSortKey(title, CompareOptions.None);
            scope.Title[card] = key;
        }
        return key;
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
    }
}
