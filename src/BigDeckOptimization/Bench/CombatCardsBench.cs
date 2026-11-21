using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BigDeckOptimization.Patches;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf combatcards [passes]</c>, in combat: what a reshuffle asks of the combat's cards, the game's way and this mod's.
/// <list type="bullet">
/// <item><see cref="CombatCardSet"/>: <c>CombatState.ContainsCard</c> for every card of the local player's draw, discard and hand
/// piles and for every card of its deck (not combat cards: false answers too), the game's (the whole mod off) and the set's: the
/// same answer for each, and the times of one pass over them both ways.</item>
/// <item>PileIndex's single scan in <c>CardPile.RemoveInternal</c>: the local draw and discard cards put in a scratch list (no
/// pile, nothing of the game changes) and removed one by one in a shuffled order, as a reshuffle does, with the game's two
/// searches (<c>Enumerable.Contains</c>, then <c>List.Remove</c>) and with the single scan: the same list after each remove,
/// and the times both ways.</item>
/// </list>
/// The effect counters are put back (<see cref="EffectCounters"/>): only the game's calls count.
/// </summary>
public static class CombatCardsBench
{
    public static CmdResult Run(int passes)
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me
            || me.Creature?.CombatState is not CombatState combat)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        List<CardModel> piles = new[] { PileType.Draw, PileType.Discard, PileType.Hand }.SelectMany(p => p.GetPile(me).Cards).ToList();
        List<CardModel> asked = piles.Concat(me.Deck.Cards).ToList();
        string contains = Contains(combat, asked, passes, out bool containsOk);
        string removes = Removes(me, passes, out bool removesOk);
        string self = SelfTests(piles, out bool selfOk);
        return new CmdResult(containsOk && removesOk && selfOk, $"COMBATCARDS {piles.Count} combat cards, {asked.Count - piles.Count} deck cards: {contains}; {removes}; {self}");
    }

    /// <summary><c>this == obj</c>, expression-bodied and as a block (the Debug compiler's two shapes), and a real override.</summary>
    private sealed class SameReference
    {
        public override bool Equals(object? obj) => this == obj;
        public override int GetHashCode() => 0;
    }

    private sealed class SameReferenceBlock
    {
        public override bool Equals(object? obj)
        {
            return this == obj;
        }

        public override int GetHashCode() => 0;
    }

    private sealed class ByValue
    {
        public int Value = 1;
        public override bool Equals(object? obj) => obj is ByValue other && other.Value == Value;
        public override int GetHashCode() => Value;
    }

    /// <summary>
    /// CombatCardSet's writers driven as other mods' patches could make them run (<see cref="CombatCardSet.SelfTest"/>: a skipped
    /// add inserting at the front, a nested add, a skipped remove, a swapped list), and the equality rule: <c>object.Equals</c> and
    /// a <c>this == obj</c> override (both shapes) by reference, a real override not.
    /// </summary>
    private static string SelfTests(List<CardModel> piles, out bool ok)
    {
        var failures = new List<string>();
        if (CombatCardSet.Applied && CombatCardSet.Enabled)
        {
            failures.AddRange(CombatCardSet.SelfTest(piles));
        }
        (Type Type, CardEquality.Kind Expected)[] kinds =
        {
            (typeof(CardModel), CardEquality.Kind.Reference),
            (typeof(object), CardEquality.Kind.Reference),
            (typeof(SameReference), CardEquality.Kind.ReferenceOverride),
            (typeof(SameReferenceBlock), CardEquality.Kind.ReferenceOverride),
            (typeof(ByValue), CardEquality.Kind.Other),
            (typeof(string), CardEquality.Kind.Other),
        };
        foreach ((Type type, CardEquality.Kind expected) in kinds)
        {
            CardEquality.Kind kind = CardEquality.Classify(type, out _);
            if (kind != expected)
            {
                failures.Add($"{type.Name}'s Equals classed {kind}, expected {expected}");
            }
        }
        // The IL shapes themselves: this build compiles both classes above to the short one; the Debug compiler's (Guzhenren's
        // cards) and near misses.
        (string Name, byte[] Il, bool Expected)[] shapes =
        {
            ("short", new byte[] { 0x02, 0x03, 0xFE, 0x01, 0x2A }, true),
            ("Debug compiler's", new byte[] { 0x00, 0x02, 0x03, 0xFE, 0x01, 0x0A, 0x2B, 0x00, 0x06, 0x2A }, true),
            ("negated", new byte[] { 0x02, 0x03, 0xFE, 0x01, 0x16, 0xFE, 0x01, 0x2A }, false),
            ("arguments swapped for a field", new byte[] { 0x02, 0x7B, 0x01, 0x00, 0x00, 0x04, 0x03, 0xFE, 0x01, 0x2A }, false),
            ("branch elsewhere", new byte[] { 0x02, 0x03, 0xFE, 0x01, 0x0A, 0x2B, 0x02, 0x00, 0x16, 0x06, 0x2A }, false),
            ("always true", new byte[] { 0x17, 0x2A }, false),
        };
        foreach ((string name, byte[] il, bool expected) in shapes)
        {
            if (CardEquality.IsReferenceComparison(il) != expected)
            {
                failures.Add($"IL shape '{name}' read as {(expected ? "not " : "")}a reference comparison");
            }
        }
        foreach (Type type in piles.Select(c => c.GetType()).Distinct())
        {
            if (CardEquality.Classify(type, out _) != CardEquality.Kind.Reference)
            {
                failures.Add($"{type.Name} (a card of this combat) has its own Equals: the set and the single scan leave it to the game");
                break;
            }
        }
        ok = failures.Count == 0 || failures.All(f => f.Contains("has its own Equals"));
        return failures.Count == 0 ? "self-tests OK" : "SELF-TEST " + string.Join("; ", failures);
    }

    private static string Contains(CombatState combat, List<CardModel> asked, int passes, out bool ok)
    {
        ok = true;
        if (!CombatCardSet.Applied)
        {
            return $"{CombatCardSet.Name} not applied (see the log): nothing to compare";
        }
        if (!CombatCardSet.Enabled)
        {
            ok = false;
            return $"{CombatCardSet.Name} is off (setting or fault): it would be compared with itself";
        }
        bool[] game = new bool[asked.Count];
        double gameMs = PerfConsoleCmd.Vanilla(() =>
        {
            long start = Stopwatch.GetTimestamp();
            for (int pass = 0; pass < passes; pass++)
            {
                for (int i = 0; i < asked.Count; i++)
                {
                    game[i] = combat.ContainsCard(asked[i]);
                }
            }
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds / passes;
        });
        bool?[] mine = new bool?[asked.Count];
        long t0 = Stopwatch.GetTimestamp();
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < asked.Count; i++)
            {
                mine[i] = CombatCardSet.AnswerForTest(combat, asked[i]);
            }
        }
        double setMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds / passes;
        int unanswered = mine.Count(m => m == null);
        int different = Enumerable.Range(0, asked.Count).Count(i => mine[i] != null && mine[i] != game[i]);
        int inCombat = game.Count(g => g);
        if (different > 0 || !CombatCardSet.Enabled)
        {
            ok = false;
            return $"ContainsCard MISMATCH: {different} of {asked.Count} answers differ{(CombatCardSet.Enabled ? "" : " (part turned off)")}";
        }
        if (unanswered == asked.Count)
        {
            return $"ContainsCard: the set answered none of {asked.Count} cards (back-off or a card type overriding Equals): nothing to compare";
        }
        return $"ContainsCard game {gameMs:F2} ms, set {setMs:F2} ms per pass ({inCombat} in the combat, {unanswered} left to the game): OK: identical answers";
    }

    private static string Removes(MegaCrit.Sts2.Core.Entities.Players.Player me, int passes, out bool ok)
    {
        ok = true;
        if (!PileIndex.Applied)
        {
            return $"{PileIndex.Name} not applied (see the log): single scan not compared: nothing to compare";
        }
        List<CardModel> cards = new[] { PileType.Draw, PileType.Discard }.SelectMany(p => p.GetPile(me).Cards).ToList();
        var random = new Random(1234);
        List<CardModel> order = cards.ToList();
        for (int j = order.Count - 1; j > 0; j--)
        {
            int k = random.Next(j + 1);
            (order[j], order[k]) = (order[k], order[j]);
        }
        // The last few cards stay: both lists end holding them (in the same order), so the final comparison reads something.
        int kept = Math.Min(3, order.Count / 2);
        order.RemoveRange(order.Count - kept, kept);
        double gameMs = 0, scanMs = 0;
        long scans = 0;
        int different = 0;
        for (int pass = 0; pass < passes && different == 0; pass++)
        {
            List<CardModel> game = cards.ToList();
            long t0 = Stopwatch.GetTimestamp();
            foreach (CardModel card in order)
            {
                // RemoveInternal's two searches, as the game runs them.
                if (!((IEnumerable<CardModel>)game).Contains(card))
                {
                    throw new InvalidOperationException("bench: card not in the list");
                }
                game.Remove(card);
            }
            gameMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            List<CardModel> mine = cards.ToList();
            List<CardModel> check = cards.ToList();
            long scansBefore = PileIndex.SingleScans;
            long t1 = Stopwatch.GetTimestamp();
            foreach (CardModel card in order)
            {
                PileIndex.RemoveForTest(mine, card);
            }
            scanMs += Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
            scans += PileIndex.SingleScans - scansBefore;
            // The same list after each remove: replayed with a check after each step on the first pass only (it costs a copy).
            if (pass == 0)
            {
                List<CardModel> stepped = cards.ToList();
                foreach (CardModel card in order)
                {
                    PileIndex.RemoveForTest(stepped, card);
                    check.Remove(card);
                    if (!stepped.SequenceEqual(check, ReferenceEqualityComparer.Instance))
                    {
                        different++;
                        break;
                    }
                }
            }
            different += game.Count == cards.Count - order.Count && game.SequenceEqual(mine, ReferenceEqualityComparer.Instance) ? 0 : 1;
        }
        if (different > 0 || !PileIndex.SingleScanEnabled)
        {
            ok = different == 0;
            return different > 0 ? $"RemoveInternal MISMATCH: the lists differ after a remove" : "RemoveInternal single scan switched off: not compared: the game's searches only";
        }
        if (scans == 0 && cards.Count > 0)
        {
            return "RemoveInternal: the single scan ran for none of the removes (switched off, faulted, or a card type overriding Equals): nothing to compare";
        }
        return $"RemoveInternal of {order.Count} of {cards.Count} cards in a shuffled order: game {gameMs / passes:F2} ms, single scan {scanMs / passes:F2} ms per pass: OK: identical lists";
    }
}
