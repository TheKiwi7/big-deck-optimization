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
        long sorts = CardSortKeys.Sorts, answered = CardSortKeys.Answered, mismatchesBefore = CardSortKeys.Mismatches;
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
                + $"keys {keysMs / starts.Count:F1} ms per sort; answered {CardSortKeys.Answered - answered}, checks {CardSortKeys.Checks}: {verdict}");
        }
        finally
        {
            // The effect counters count the game's sorts only.
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
