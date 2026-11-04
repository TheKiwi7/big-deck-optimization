using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf shufflebench [n]</c>, in combat: what deck-wide reads cost over the local player's cards. The game's sort of
/// <c>StableShuffle</c> against a sort on integer keys (same order required, ties included, from shuffled starts), and the
/// counts Squeeze, Perfected Strike and Crescent Spear make for each calculation.
/// </summary>
public static class ShuffleBench
{
    public static CmdResult Run(int iterations)
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me
            || me.PlayerCombatState is not { } combat)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        List<CardModel> cards = combat.AllCards.ToList();

        double Time(Action pass)
        {
            pass();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                pass();
            }
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        double vanilla = Time(() => cards.ToList().Sort());
        double keyed = Time(() => KeyedSort(cards));

        int different = 0;
        var random = new Random(1234);
        List<CardModel> order = cards.ToList();
        for (int i = 0; i < 40; i++)
        {
            for (int j = order.Count - 1; j > 0; j--)
            {
                int k = random.Next(j + 1);
                (order[j], order[k]) = (order[k], order[j]);
            }
            // Short lists too: the sort takes other paths below 16 items and in small partitions.
            List<CardModel> start = i % 4 == 3 ? order.Take(random.Next(2, 40)).ToList() : order;
            List<CardModel> game = start.ToList();
            game.Sort();
            CardModel[] mine = KeyedSort(start);
            for (int j = 0; j < game.Count; j++)
            {
                if (!ReferenceEquals(game[j], mine[j]))
                {
                    different++;
                    break;
                }
            }
        }

        int sink = 0;
        double squeeze = Time(() => sink += combat.AllCards.Count(c => c.Tags.Contains(CardTag.OstyAttack)));
        double strike = Time(() => sink += combat.AllCards.Count(c => c.Tags.Contains(CardTag.Strike)));
        double stars = Time(() => sink += combat.AllCards.Count(c => c.CanonicalStarCost >= 0 || c.HasStarCostX));
        return new CmdResult(success: different == 0, $"SHUFFLEBENCH {cards.Count} cards, {cards.Select(c => c.Id).Distinct().Count()} kinds: sort {vanilla:F2} ms, keyed {keyed:F2} ms, "
            + $"orders different {different} of 40; counts: Squeeze {squeeze:F3} ms, Perfected Strike {strike:F3} ms, Crescent Spear {stars:F3} ms ({sink & 1})");
    }

    private static CardModel[] KeyedSort(List<CardModel> list)
    {
        var ranks = new Dictionary<ModelId, int>();
        foreach (CardModel card in list)
        {
            ranks[card.Id] = 0;
        }
        List<ModelId> ids = ranks.Keys.ToList();
        ids.Sort((a, b) => a.CompareTo(b));
        for (int i = 0; i < ids.Count; i++)
        {
            ranks[ids[i]] = i;
        }
        var keys = new long[list.Count];
        CardModel[] items = list.ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            keys[i] = ((long)ranks[items[i].Id] << 32) | (uint)(items[i].CurrentUpgradeLevel ^ int.MinValue);
        }
        Array.Sort(keys, items);
        return items;
    }
}
