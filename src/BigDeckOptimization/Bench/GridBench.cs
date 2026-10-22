using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// bdperf grid [cards]: card grid (deck view) costs on the local deck, vanilla vs optimized.
///   sort — the deck view's sort orders (same comparison as NCardGrid.SetCards), checks the resulting orders match.
///   tabs — re-renders cards one after another on one node, as scrolling does, checks Limitless' enchantment tabs end
///          up in the exact same state.
/// UI only: touches no game state.
/// </summary>
public static class GridBench
{
    /// <summary>What the deck view sorts by after clicking each button (first is the clicked one).</summary>
    private static readonly SortingOrders[][] _priorities =
    {
        new[] { SortingOrders.TypeAscending, SortingOrders.Ascending, SortingOrders.CostAscending, SortingOrders.AlphabetAscending },
        new[] { SortingOrders.CostAscending, SortingOrders.TypeAscending, SortingOrders.Ascending, SortingOrders.AlphabetAscending },
        new[] { SortingOrders.AlphabetAscending, SortingOrders.CostAscending, SortingOrders.TypeAscending, SortingOrders.Ascending },
        new[] { SortingOrders.TypeDescending, SortingOrders.Descending, SortingOrders.CostDescending, SortingOrders.AlphabetDescending },
        new[] { SortingOrders.AlphabetDescending, SortingOrders.CostDescending, SortingOrders.TypeAscending, SortingOrders.Descending },
    };

    public static CmdResult Run(int tabCards)
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        Player? me = state == null ? null : LocalContext.GetMe(state);
        if (me == null || NGame.Instance == null)
        {
            return new CmdResult(success: false, "Start a run first.");
        }
        List<CardModel> deck = me.Deck.Cards.ToList();
        (string sortMsg, bool sortOk) = CompareSort(deck);
        // Spread over the whole deck, so the sample has the deck's mix of enchanted and plain cards.
        int step = Math.Max(1, deck.Count / Math.Max(1, tabCards));
        (string tabMsg, bool tabOk) = CompareTabs(deck.Where((_, i) => i % step == 0).Take(tabCards).ToList());
        (string poolMsg, bool poolOk) = ComparePoolFree(deck.Where((_, i) => i % step == 0).Take(20).ToList());
        bool ok = sortOk && tabOk && poolOk;
        string msg = $"grid — deck: {deck.Count} cards\n{sortMsg}\n{tabMsg}\n{poolMsg}";
        if (ok)
        {
            Log.Info("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        else
        {
            Log.Error("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        return new CmdResult(success: ok, msg);
    }

    private static (string, bool) CompareSort(List<CardModel> deck)
    {
        NCardGrid grid = new();
        bool wasEnabled = GridSortCache.Enabled;
        try
        {
            // SetCards compares against the previously displayed order: use the reversed deck so the index tie-break matters.
            List<CardModel> previous = deck.AsEnumerable().Reverse().ToList();
            AccessTools.FieldRefAccess<NCardGrid, List<CardModel>>("_cards")(grid).AddRange(previous);
            var algorithms = (Dictionary<SortingOrders, Func<CardModel, CardModel, int>>)AccessTools
                .PropertyGetter(typeof(NCardGrid), "SortingAlgorithms").Invoke(grid, null)!;

            double vanillaMs = 0, cachedMs = 0;
            int mismatches = 0;
            foreach (SortingOrders[] priority in _priorities)
            {
                // Same comparison as NCardGrid.SetCards.
                int Compare(CardModel x, CardModel y)
                {
                    foreach (SortingOrders item in priority)
                    {
                        int num = algorithms[item](x, y);
                        if (num != 0)
                        {
                            return num;
                        }
                    }
                    return x.Id.CompareTo(y.Id);
                }

                List<CardModel> vanilla = deck.ToList(), cached = deck.ToList();
                GridSortCache.Enabled = false;
                Stopwatch sw = Stopwatch.StartNew();
                vanilla.Sort(Compare);
                vanillaMs += sw.Elapsed.TotalMilliseconds;
                GridSortCache.Enabled = true;
                sw.Restart();
                GridSortCache.RunInScope(grid, () => cached.Sort(Compare));
                cachedMs += sw.Elapsed.TotalMilliseconds;
                if (!vanilla.SequenceEqual(cached, ReferenceEqualityComparer.Instance))
                {
                    mismatches++;
                }
            }
            int n = _priorities.Length;
            return ($"  sort ({n} orders): vanilla {vanillaMs / n:F1} ms, cached {cachedMs / n:F1} ms per sort — "
                + (mismatches == 0 ? $"OK: identical orders, x{vanillaMs / Math.Max(0.001, cachedMs):F1} faster" : $"MISMATCH: {mismatches} orders differ!"),
                mismatches == 0);
        }
        finally
        {
            GridSortCache.Enabled = wasEnabled;
            grid.Free();
        }
    }

    /// <summary>
    /// Builds grid holders as NCardGrid.InitGrid does, frees half to the pool the vanilla way and half with
    /// PoolSignalScan, and checks every node is left with the same connections.
    /// </summary>
    private static (string, bool) ComparePoolFree(List<CardModel> cards)
    {
        if (cards.Count == 0)
        {
            return ("  pool free: no cards", true);
        }
        Control container = new() { Visible = false };
        bool wasEnabled = PoolSignalScan.Enabled;
        try
        {
            NGame.Instance!.AddChild(container);
            double vanillaMs = 0, scanMs = 0;
            int mismatches = 0;
            string? firstMismatch = null;
            Stopwatch sw = new();
            foreach (CardModel card in cards)
            {
                NGridCardHolder vanillaHolder = CreateHolder(card, container), scanHolder = CreateHolder(card, container);
                container.RemoveChild(vanillaHolder);
                container.RemoveChild(scanHolder);

                PoolSignalScan.Enabled = false;
                sw.Restart();
                NodePool.Free(vanillaHolder);
                vanillaMs += sw.Elapsed.TotalMilliseconds;

                PoolSignalScan.Enabled = true;
                sw.Restart();
                NodePool.Free(scanHolder);
                scanMs += sw.Elapsed.TotalMilliseconds;

                string expected = Connections(vanillaHolder), actual = Connections(scanHolder);
                if (expected != actual)
                {
                    mismatches++;
                    firstMismatch ??= $"\n    vanilla: {expected}\n    scan   : {actual}";
                }
            }
            return ($"  pool free ({cards.Count} grid holders): vanilla {vanillaMs / cards.Count:F2} ms, scan {scanMs / cards.Count:F2} ms per holder — "
                + (mismatches == 0 ? $"OK: identical connections, x{vanillaMs / Math.Max(0.001, scanMs):F1} faster" : $"MISMATCH on {mismatches} holders!{firstMismatch}"),
                mismatches == 0);
        }
        finally
        {
            PoolSignalScan.Enabled = wasEnabled;
            container.QueueFree();
        }
    }

    /// <summary>A grid holder with its card, connected and in the tree like NCardGrid.InitGrid leaves it.</summary>
    private static NGridCardHolder CreateHolder(CardModel card, Node parent)
    {
        NCard nCard = NCard.Create(card) ?? throw new InvalidOperationException("NCard.Create returned null");
        NGridCardHolder holder = NGridCardHolder.Create(nCard) ?? throw new InvalidOperationException("NGridCardHolder.Create returned null");
        holder.Connect(NCardHolder.SignalName.Pressed, Callable.From<NCardHolder>(_ => { }));
        holder.Connect(NCardHolder.SignalName.AltPressed, Callable.From<NCardHolder>(_ => { }));
        parent.AddChild(holder);
        nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        return holder;
    }

    /// <summary>Every node's outgoing connections per signal and incoming connection count, read the vanilla way.</summary>
    private static string Connections(Node root)
    {
        System.Text.StringBuilder sb = new();
        void Visit(Node node, string path)
        {
            int incoming = node.GetIncomingConnections().Count;
            int outgoing = 0;
            foreach (Godot.Collections.Dictionary signal in node.GetSignalList())
            {
                outgoing += node.GetSignalConnectionList(signal["name"].AsStringName()).Count;
            }
            if (incoming + outgoing > 0)
            {
                sb.Append($"{path}:{outgoing}/{incoming} ");
            }
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                Visit(node.GetChild(i), path + "/" + i);
            }
        }
        Visit(root, "");
        return sb.ToString();
    }

    private static (string, bool) CompareTabs(List<CardModel> cards)
    {
        if (!EnchantmentTabReuse.Applied)
        {
            return ("  tabs: EnchantmentTabReuse not applied (Limitless absent or changed): nothing to compare", true);
        }
        if (cards.Count == 0)
        {
            return ("  tabs: no cards", true);
        }
        Control container = new() { Visible = false };
        NCard? vanillaNode = null, reuseNode = null;
        bool wasEnabled = EnchantmentTabReuse.Enabled;
        long reusedBefore = EnchantmentTabReuse.Reused;
        try
        {
            NGame.Instance!.AddChild(container);
            vanillaNode = NCard.Create(cards[0]);
            reuseNode = NCard.Create(cards[0]);
            if (vanillaNode == null || reuseNode == null)
            {
                return ("  tabs: could not create card nodes", false);
            }
            container.AddChild(vanillaNode);
            container.AddChild(reuseNode);

            double vanillaMs = 0, reuseMs = 0;
            int mismatches = 0;
            string? firstMismatch = null;
            Stopwatch sw = new();
            foreach (CardModel card in cards)
            {
                // What NCardGrid.AssignCardsToRow does to a recycled node.
                EnchantmentTabReuse.Enabled = false;
                sw.Restart();
                vanillaNode.Model = card;
                vanillaNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                vanillaMs += sw.Elapsed.TotalMilliseconds;

                EnchantmentTabReuse.Enabled = true;
                sw.Restart();
                reuseNode.Model = card;
                reuseNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                reuseMs += sw.Elapsed.TotalMilliseconds;

                string expected = EnchantmentTabReuse.DescribeTabs(vanillaNode), actual = EnchantmentTabReuse.DescribeTabs(reuseNode);
                if (expected != actual)
                {
                    mismatches++;
                    firstMismatch ??= $"\n    {card.Id.Entry} vanilla: {expected}\n    {card.Id.Entry} reused : {actual}";
                }
            }
            long reused = EnchantmentTabReuse.Reused - reusedBefore;
            int enchanted = cards.Count(c => c.Enchantment != null);
            return ($"  tabs ({cards.Count} cards, {enchanted} enchanted, {reused} reused):vanilla {vanillaMs / cards.Count:F2} ms, reuse {reuseMs / cards.Count:F2} ms per card render — "
                + (mismatches == 0 ? $"OK: identical tabs, x{vanillaMs / Math.Max(0.001, reuseMs):F1} faster" : $"MISMATCH on {mismatches} cards!{firstMismatch}"),
                mismatches == 0);
        }
        finally
        {
            EnchantmentTabReuse.Enabled = wasEnabled;
            vanillaNode?.QueueFreeSafely();
            reuseNode?.QueueFreeSafely();
            container.QueueFree();
        }
    }
}
