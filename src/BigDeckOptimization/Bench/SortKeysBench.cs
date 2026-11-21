using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf sortkeys [wrong]</c>, in combat: the local player's draw, discard and hand cards sorted as <c>StableShuffle</c>
/// sorts them, with the game's code (the whole mod off) and with <see cref="CardSortKeys"/>, from the piles' order and two
/// shuffled ones; then <c>StableShuffle</c> itself both ways with the same seed. Same order element by element, and the times.
/// <c>wrong</c>: answers reversed from then on (positive control: the comparison and the part's own check must see it).
/// Then, where the game has it (v0.113+), the same sort with a Veilpiercer on the local player (its cost hook reads
/// <c>card.Keywords</c>, which runs the keyword hook): the part must still answer (not refuse) and give the game's order. The
/// power goes on and comes off with the game's own internal calls, silently, before the bench returns: the combat's state is as
/// it was (its icon shows and goes, and the game's "power removed" effect plays once).
/// </summary>
public static class SortKeysBench
{
    public static CmdResult Run(bool wrong)
    {
        if (wrong)
        {
            CardSortKeys.WrongForTest = true;
            return new CmdResult(success: true, $"{CardSortKeys.Name}: answers reversed from now on (test).");
        }
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!CardSortKeys.Applied)
        {
            // Not applied (the cheap comparison of v0.107/v0.111, or changed code): a legitimate state, no "OK".
            return new CmdResult(success: true, $"{CardSortKeys.Name} not applied ({(CardSortKeys.NotNeeded ? "not needed on this game version" : "see the log")}): nothing to compare.");
        }
        if (!CardSortKeys.Enabled)
        {
            return new CmdResult(success: false, $"{CardSortKeys.Name} is off (setting or fault): it would be compared with itself.");
        }
        List<CardModel> cards = new[] { PileType.Draw, PileType.Discard, PileType.Hand }.SelectMany(p => p.GetPile(me).Cards).ToList();
        long sorts = CardSortKeys.Sorts, answered = CardSortKeys.Answered, mismatchesBefore = CardSortKeys.Mismatches, sharedBefore = CardSortKeys.SharedKeyCompares;
        try
        {
            var random = new Random(1234);
            List<List<CardModel>> starts = new() { cards };
            for (int i = 0; i < 2; i++)
            {
                List<CardModel> order = cards.ToList();
                for (int j = order.Count - 1; j > 0; j--)
                {
                    int k = random.Next(j + 1);
                    (order[j], order[k]) = (order[k], order[j]);
                }
                starts.Add(order);
            }
            double gameMs = 0, keysMs = 0;
            int different = 0;
            foreach (List<CardModel> start in starts)
            {
                List<CardModel> game = start.ToList();
                // Timed inside: Vanilla's two switch passes aren't the game's sort.
                gameMs += PerfConsoleCmd.Vanilla(() =>
                {
                    long inside = Stopwatch.GetTimestamp();
                    game.Sort();
                    return Stopwatch.GetElapsedTime(inside).TotalMilliseconds;
                });
                List<CardModel> mine = start.ToList();
                long answeredBefore = CardSortKeys.Answered;
                long t0 = Stopwatch.GetTimestamp();
                bool opened = CardSortKeys.SortInScope(mine);
                keysMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                different += Different(game, mine) ? 1 : 0;
                if (!CardSortKeys.Enabled)
                {
                    break; // Its own check saw a wrong answer (or it faulted): a mismatch below.
                }
                if (!opened || (mine.Count > 1 && CardSortKeys.Answered == answeredBefore))
                {
                    return new CmdResult(success: true, $"{CardSortKeys.Name} left this sort to the game ({CardSortKeys.LastRefusal ?? "nothing answered"}): nothing to compare.");
                }
            }
            // StableShuffle itself, the game's then through the transpiled callers' path, from the same seed.
            bool shuffleDifferent = false;
            if (CardSortKeys.Enabled)
            {
                List<CardModel> gameShuffled = cards.ToList(), mineShuffled = cards.ToList();
                PerfConsoleCmd.Vanilla(() => gameShuffled.StableShuffle(NewRng(77)));
                CardSortKeys.StableShuffleInScope(mineShuffled, NewRng(77));
                shuffleDifferent = Different(gameShuffled, mineShuffled);
            }
            (bool veilOk, string veilText) = CardSortKeys.Enabled ? Veilpiercer(me, starts[1]) : (true, "");
            long mismatches = CardSortKeys.Mismatches - mismatchesBefore;
            bool ok = different == 0 && !shuffleDifferent && mismatches == 0 && CardSortKeys.Enabled && veilOk;
            string verdict = ok ? "OK: identical orders" + veilText
                : $"MISMATCH: {different} of {starts.Count} sorts{(shuffleDifferent ? " and the shuffle" : "")} differ, own check mismatches {mismatches}{(CardSortKeys.Enabled ? "" : " (part turned off)")}{veilText}";
            // One line: scenarios match the verdict on it (expect SORTKEYS .*MISMATCH).
            return new CmdResult(ok, $"SORTKEYS {cards.Count} cards, {cards.Select(c => c.Id).Distinct().Count()} kinds: game {gameMs / starts.Count:F1} ms, "
                + $"keys {keysMs / starts.Count:F1} ms per sort; answered {CardSortKeys.Answered - answered} ({CardSortKeys.SharedKeyCompares - sharedBefore} by shared keys), checks {CardSortKeys.Checks}: {verdict}");
        }
        finally
        {
            // The effect counters count the game's sorts only.
            CardSortKeys.RestoreCounters(sorts, answered);
        }
    }

    /// <summary>
    /// <c>bdperf sortkeys deck</c> (R3-2, re-audit round 3): the local player's deck sorted as <c>StableShuffle</c> sorts it (WarHammer's
    /// post-combat shuffle of the deck's upgradable cards), the game's and this part's, from the deck's order and two shuffled ones.
    /// After a combat the owner's <c>Creature.CombatState</c> is still the ended combat, whose listeners hold any impure cost listener
    /// the player has (Brilliant Scarf): deck cards never reach the cost hook (<c>card.CombatState</c> is null), so the sort must be
    /// answered, with the game's order. The verdict names the ended combat's first impure cost listener (the test's premise).
    /// </summary>
    public static CmdResult RunDeck()
        => RunDeckCore();

    /// <summary>
    /// <c>bdperf sortkeys partonly [cards] [passes]</c>, in combat: the part's own sort alone, timed (no game sort: at ~10,000 cards
    /// it takes minutes), with the shared-key comparison and with the game's variable loop in every comparison, alternating, from
    /// one shuffled order of the local draw, discard and hand cards (the first <c>cards</c> of it). Both orders must be the same
    /// (and the part's own check compares its answers with the game's, 1 in 16 in test builds); the cost reads per sort are
    /// printed (once per card at most). The effect counters are put back.
    /// </summary>
    public static CmdResult RunPartOnly(int count, int passes)
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!CardSortKeys.Applied || !CardSortKeys.Enabled)
        {
            return new CmdResult(success: !CardSortKeys.Applied, $"{CardSortKeys.Name} {(CardSortKeys.Applied ? "is off" : "not applied")}: nothing to time.");
        }
        List<CardModel> cards = new[] { PileType.Draw, PileType.Discard, PileType.Hand }.SelectMany(p => p.GetPile(me).Cards).ToList();
        var random = new Random(1234);
        for (int j = cards.Count - 1; j > 0; j--)
        {
            int k = random.Next(j + 1);
            (cards[j], cards[k]) = (cards[k], cards[j]);
        }
        if (count > 0 && count < cards.Count)
        {
            cards = cards.Take(count).ToList();
        }
        long sorts = CardSortKeys.Sorts, answered = CardSortKeys.Answered, mismatches = CardSortKeys.Mismatches;
        var sharedMs = new List<double>();
        var loopMs = new List<double>();
        var lines = new List<string>();
        long sharedReads = 0, loopReads = 0, sharedCompares = 0;
        bool different = false;
        List<CardModel>? last = null;
        try
        {
            // One sort of each first, not counted (JIT tiers, first-use caches), then the two alternating, the order swapped at each
            // pass; each sort's collections (gen 0/1/2) printed, so an outlier can be told from a real difference.
            for (int pass = -1; pass < passes; pass++)
            {
                string line = pass < 0 ? "  warm-up:" : $"  pass {pass + 1}:";
                foreach (bool shared in pass % 2 == 0 ? new[] { true, false } : new[] { false, true })
                {
                    List<CardModel> mine = cards.ToList();
                    CardSortKeys.NoSharedKeysForTest = !shared;
                    long reads = CardSortKeys.CostReads, compares = CardSortKeys.SharedKeyCompares, before = CardSortKeys.Answered;
                    int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                    long t0 = Stopwatch.GetTimestamp();
                    bool opened = CardSortKeys.SortInScope(mine);
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    string gcs = $"{GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}";
                    if (!opened || (mine.Count > 1 && CardSortKeys.Answered == before))
                    {
                        return new CmdResult(success: true, $"{CardSortKeys.Name} left this sort to the game ({CardSortKeys.LastRefusal ?? "nothing answered"}): nothing to time.");
                    }
                    line += $" {(shared ? "shared" : "loop")} {ms:F1} ms (gc {gcs})";
                    if (pass >= 0)
                    {
                        if (shared)
                        {
                            sharedMs.Add(ms);
                            sharedReads += CardSortKeys.CostReads - reads;
                            sharedCompares += CardSortKeys.SharedKeyCompares - compares;
                        }
                        else
                        {
                            loopMs.Add(ms);
                            loopReads += CardSortKeys.CostReads - reads;
                        }
                    }
                    last ??= mine;
                    different |= Different(last, mine);
                    last = mine;
                }
                lines.Add(line);
            }
        }
        finally
        {
            CardSortKeys.NoSharedKeysForTest = false;
            CardSortKeys.RestoreCounters(sorts, answered);
        }
        long ownMismatches = CardSortKeys.Mismatches - mismatches;
        bool ok = !different && ownMismatches == 0 && CardSortKeys.Enabled;
        return new CmdResult(ok, $"SORTKEYS partonly {cards.Count} cards, {cards.Select(c => c.Id).Distinct().Count()} kinds: median shared keys {Median(sharedMs):F1} ms, "
            + $"variable loop {Median(loopMs):F1} ms per sort ({passes} each); cost reads per sort {sharedReads / passes} / {loopReads / passes}, shared-key comparisons "
            + $"{sharedCompares / passes} per sort, checks {CardSortKeys.Checks}: {(ok ? "OK: identical orders" : $"MISMATCH: orders {(different ? "differ" : "same")}, own check mismatches {ownMismatches}")}"
            + "\n" + string.Join("\n", lines));
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        List<double> sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static CmdResult RunDeckCore()
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "No run.");
        }
        if (!CardSortKeys.Applied || !CardSortKeys.Enabled)
        {
            return new CmdResult(success: !CardSortKeys.Applied, $"{CardSortKeys.Name} {(CardSortKeys.Applied ? "is off" : "not applied")}: nothing to compare.");
        }
        List<CardModel> cards = me.Deck.Cards.ToList();
        ICombatState? combat = me.Creature?.CombatState;
        string where = combat == null ? "no combat" : CombatManager.Instance.IsInProgress ? "combat in progress" : "ended combat";
        string impure = combat == null ? "none" : CardSortKeys.ImpureCostListenerForTest(combat) ?? "none";
        // R3-2's narrow judgment off (A3's superset: deck cards judged with their owner's combat): named in the verdict. The owner's
        // decision (2026-11-17): strict when another mod patches PlayerCombatState.get_AllPiles (BaseLib, RitsuLib), so with an
        // impure cost listener in the owner's combat every sort must then be refused, the game's order kept.
        string? narrowOff = CardSortKeys.NarrowOffForTest();
        string narrow = narrowOff != null ? $", narrow judgment off ({narrowOff})" : "";
        bool expectRefusal = narrowOff != null && impure != "none";
        long sorts = CardSortKeys.Sorts, answered = CardSortKeys.Answered, refused = CardSortKeys.Refused, mismatches = CardSortKeys.Mismatches;
        try
        {
            var random = new Random(4321);
            int different = 0, notAnswered = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                List<CardModel> start = cards.ToList();
                for (int j = pass == 0 ? 0 : start.Count - 1; j > 0; j--)
                {
                    int k = random.Next(j + 1);
                    (start[j], start[k]) = (start[k], start[j]);
                }
                List<CardModel> game = start.ToList();
                PerfConsoleCmd.Vanilla(() => { game.Sort(); return 0; });
                List<CardModel> mine = start.ToList();
                long before = CardSortKeys.Answered;
                bool opened = CardSortKeys.SortInScope(mine);
                different += Different(game, mine) ? 1 : 0;
                notAnswered += !opened || (mine.Count > 1 && CardSortKeys.Answered == before) ? 1 : 0;
            }
            long refusedNow = CardSortKeys.Refused - refused, mismatchesNow = CardSortKeys.Mismatches - mismatches;
            bool ok = different == 0 && mismatchesNow == 0 && CardSortKeys.Enabled && (expectRefusal ? notAnswered == 3 : notAnswered == 0);
            string verdict = ok ? (expectRefusal ? $"OK: left to the game as expected ({CardSortKeys.LastRefusal ?? "refused"}), identical orders" : "OK: answered, identical orders")
                : expectRefusal && notAnswered < 3 ? $"ANSWERED: {3 - notAnswered} of 3 sorts answered while the narrow judgment is off"
                : notAnswered > 0 ? $"REFUSED: {notAnswered} of 3 sorts left to the game ({CardSortKeys.LastRefusal ?? "nothing answered"})"
                : $"MISMATCH: {different} of 3 sorts differ, own check mismatches {mismatchesNow}";
            return new CmdResult(ok, $"SORTKEYS deck {cards.Count} cards, {where}, impure cost listener {impure}{narrow}: answered {CardSortKeys.Answered - answered}, "
                + $"refused {refusedNow}, checks {CardSortKeys.Checks}: {verdict}");
        }
        finally
        {
            CardSortKeys.RestoreCounters(sorts, answered);
        }
    }

    private const string VeilpiercerType = "MegaCrit.Sts2.Core.Models.Powers.VeilpiercerPower";

    /// <summary>
    /// The sort of <paramref name="start"/> with a Veilpiercer on the local player, the game's and this part's: whether the part
    /// answered without refusing and gave the game's order, and the text appended to the verdict. The power is taken off again
    /// in any case (one the player already had stays).
    /// </summary>
    private static (bool Ok, string Text) Veilpiercer(Player me, List<CardModel> start)
    {
        PowerModel? canonical = ModelDb.AllPowers.FirstOrDefault(p => p.GetType().FullName == VeilpiercerType);
        if (canonical == null)
        {
            return (true, "; Veilpiercer: not in this game version");
        }
        Creature creature = me.Creature;
        PowerModel? added = null;
        if (!creature.Powers.Any(p => p.GetType() == canonical.GetType()))
        {
            added = canonical.ToMutable();
            added.ApplyInternal(creature, 1, silent: true);
        }
        try
        {
            List<CardModel> game = start.ToList();
            PerfConsoleCmd.Vanilla(() => { game.Sort(); return 0; });
            List<CardModel> mine = start.ToList();
            long answered = CardSortKeys.Answered, refused = CardSortKeys.Refused;
            bool opened = CardSortKeys.SortInScope(mine);
            if (CardSortKeys.Refused != refused || !opened)
            {
                string why = CardSortKeys.LastRefusal ?? "nothing answered";
                // A keyword listener in this combat not known to only add keywords (a mod's, a card's; or a known adder another
                // mod patches) refuses by design: nothing to compare here. Tested by its kind, not its wording.
                return CardSortKeys.LastRefusalKeywordListener
                    ? (true, $"; Veilpiercer: left to the game ({why}), not compared")
                    : (false, $"; Veilpiercer: REFUSED ({why})");
            }
            bool answeredSome = mine.Count <= 1 || CardSortKeys.Answered > answered;
            bool same = !Different(game, mine);
            return (answeredSome && same && CardSortKeys.Enabled,
                $"; Veilpiercer{(added == null ? " (already there)" : "")}: answered {CardSortKeys.Answered - answered}, "
                + (same ? "same order" : "ORDER DIFFERS") + (answeredSome ? "" : ", NOTHING ANSWERED"));
        }
        finally
        {
            added?.RemoveInternal();
        }
    }

    private static bool Different(List<CardModel> a, List<CardModel> b) =>
        a.Count != b.Count || a.Where((card, i) => !ReferenceEquals(card, b[i])).Any();

    /// <summary>A seeded Rng whatever the game version's constructor (v0.113: <c>Rng(ulong)</c>; earlier: <c>Rng(uint, int)</c>).</summary>
    private static Rng NewRng(uint seed)
    {
        foreach (ConstructorInfo constructor in typeof(Rng).GetConstructors())
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (parameters.Length == 0 || (parameters[0].ParameterType != typeof(ulong) && parameters[0].ParameterType != typeof(uint))
                || parameters.Skip(1).Any(p => !p.HasDefaultValue))
            {
                continue;
            }
            object?[] args = parameters.Select((p, i) => i == 0 ? Convert.ChangeType(seed, p.ParameterType) : p.DefaultValue).ToArray();
            return (Rng)constructor.Invoke(args);
        }
        throw new MissingMethodException(nameof(Rng), "a constructor taking a seed");
    }
}
