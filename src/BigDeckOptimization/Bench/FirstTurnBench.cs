using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf firstturn status|selftest|imbue &lt;n&gt;</c>: ExtraScopes' first-turn filters, decided at run time by its stand-ins for
/// <c>Where</c>, <c>Except</c> and <c>ToList</c> (DESIGN "ExtraScopes' first turn decided at run time").
/// <list type="bullet">
/// <item><c>status</c>: scopes opened and the stand-ins' refusals by reason (<see cref="ExtraScopes.BenchStatus"/>).</item>
/// <item><c>selftest</c> (in combat): the stand-ins called directly over the local player's combat cards with the game's two
/// lambdas and with near misses: each case must give the game's own LINQ result, and a scope exactly when the query is the game's
/// shape (a <c>List&lt;CardModel&gt;</c> source, that filter's game lambda as made by the game, a <c>List&lt;CardModel&gt;</c> to
/// leave out). The effect counters are put back by the caller (only the game's calls count), the bench's vanilla comparison is
/// paused (its log lines are the real first turn's), and the notes the refused cases record are cleared at the end.</item>
/// <item><c>imbue &lt;n&gt;</c> (outside combat): the first <c>n</c> deck cards Imbued can enchant (not enchanted yet) enchanted with
/// it, through the game's <c>CardCmd.Enchant</c>: a bottom-of-pile enchantment for the first turn's filter. Deterministic (deck
/// order), this peer only.</item>
/// </list>
/// </summary>
public static class FirstTurnBench
{
    public static CmdResult Run(string[] args)
    {
        string what = args.Length > 1 ? args[1] : "";
        try
        {
            return what switch
            {
                "status" => new CmdResult(success: true, ExtraScopes.BenchStatus()),
                "selftest" => SelfTest(),
                "imbue" when args.Length > 2 && int.TryParse(args[2], out int count) && count > 0 => Imbue(count),
                _ => new CmdResult(success: false, "Usage: bdperf firstturn status | selftest | imbue <n>"),
            };
        }
        catch (Exception e)
        {
            return new CmdResult(success: false, $"FIRSTTURN {what} failed: {e}");
        }
    }

    private static CmdResult Imbue(int count)
    {
        if (CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "bdperf firstturn imbue: outside combat only (deck cards have combat copies there).");
        }
        if (RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "Start a run first.");
        }
        int done = 0;
        foreach (CardModel card in me.Deck.Cards.ToList())
        {
            if (done == count)
            {
                break;
            }
            EnchantmentModel imbued = ModelDb.Enchantment<Imbued>().ToMutable();
            if (card.Enchantment != null || !imbued.CanEnchant(card))
            {
                continue;
            }
            CardCmd.Enchant(imbued, card, 1);
            done++;
        }
        return new CmdResult(done == count, $"FIRSTTURN imbued {done} of {count} deck cards");
    }

    /// <summary>A list type of its own: not exactly a <c>List&lt;CardModel&gt;</c>, the same cards.</summary>
    private sealed class SubList(IEnumerable<CardModel> cards) : List<CardModel>(cards);

    private static readonly Func<CardModel, bool> OwnInnate = c => c.Keywords.Contains(CardKeyword.Innate);

    private static CmdResult SelfTest()
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        IReadOnlyList<MethodInfo> games = ExtraScopes.BenchGamePredicates;
        if (games.Count != 2)
        {
            return new CmdResult(success: false, "FIRSTTURN selftest: the game's predicates aren't known (ExtraScopes' first turn not applied)");
        }
        Func<CardModel, bool> Game(MethodInfo method, bool nullTarget = false) => (Func<CardModel, bool>)Delegate.CreateDelegate(typeof(Func<CardModel, bool>),
            nullTarget ? null : Reflect.Field(method.DeclaringType!, "<>9")!.GetValue(null), method);
        Func<CardModel, bool> bottom = Game(games[0]), innate = Game(games[1]);
        List<CardModel> cards = new[] { PileType.Draw, PileType.Hand, PileType.Discard }.SelectMany(p => p.GetPile(me).Cards).ToList();
        List<CardModel> bottoms = cards.Where(bottom).ToList();
        int innates = cards.Count(innate);

        var failures = new List<string>();
        int cases = 0;
        // Filter 0 (bottom of the draw pile) goes through WhereFirstFilter, filter 1 (Innate) through WhereSecondFilter.
        IEnumerable<CardModel> Where(int filter, IEnumerable<CardModel> source, Func<CardModel, bool> predicate) =>
            filter == 0 ? ExtraScopes.WhereFirstFilter(source, predicate) : ExtraScopes.WhereSecondFilter(source, predicate);
        void Case(string name, bool expectScope, int filter, IEnumerable<CardModel> source, Func<CardModel, bool> predicate, IEnumerable<CardModel>? second = null)
        {
            cases++;
            IEnumerable<CardModel> query = Where(filter, source, predicate);
            if (second != null)
            {
                query = ExtraScopes.ExceptInScope(query, second);
            }
            long before = ExtraScopes.FirstTurnPasses;
            List<CardModel> got = ExtraScopes.ToListInScope(query);
            bool scoped = ExtraScopes.FirstTurnPasses > before;
            IEnumerable<CardModel> plain = Enumerable.Where(source, predicate);
            List<CardModel> game = (second == null ? plain : Enumerable.Except(plain, second)).ToList();
            if (scoped != expectScope || !got.SequenceEqual(game))
            {
                failures.Add($"{name}: {(scoped ? "a scope" : "no scope")}, {got.Count} cards (expected {(expectScope ? "a scope" : "no scope")}, the game's {game.Count})");
            }
        }

        bool checks = ExtraScopes.BenchChecks;
        bool enabled = ExtraScopes.Enabled;
        ExtraScopes.BenchChecks = false;
        try
        {
            // The game's shape: both filters as SetupPlayerTurn runs them.
            Case("game-bottom", true, 0, cards, bottom);
            Case("game-innate-except", true, 1, cards, innate, bottoms);
            // The game's lambda closed over null (the rule: no target or one of the lambda's own class).
            Case("game-lambda-null-target", true, 1, cards, Game(games[1], nullTarget: true));
            // Near misses: another predicate (the same answers), the other filter's lambda, a combined delegate, another source
            // type, another list left out.
            Case("predicate-other", false, 1, cards, OwnInnate);
            Case("predicate-other-filter", false, 1, cards, bottom);
            Case("predicate-combined", false, 1, cards, (Func<CardModel, bool>)Delegate.Combine(innate, innate));
            Case("source-array", false, 1, cards.ToArray(), innate);
            Case("source-subclass", false, 1, new SubList(cards), innate);
            Case("except-array", false, 1, cards, innate, bottoms.ToArray());
            // Except over a query the stand-in didn't make: no scope.
            cases++;
            List<CardModel> plainFirst = ExtraScopes.ToListInScope(ExtraScopes.ExceptInScope(Enumerable.Where(cards, innate), bottoms));
            if (ExtraScopes.BenchIsScoped(ExtraScopes.ExceptInScope(Enumerable.Where(cards, innate), bottoms))
                || !plainFirst.SequenceEqual(cards.Where(innate).Except(bottoms)))
            {
                failures.Add("except-plain-first: scoped or not the game's cards");
            }
            // The part off: the game's calls.
            ExtraScopes.Enabled = false;
            Case("part-off", false, 1, cards, innate);
            ExtraScopes.Enabled = enabled;
            // Another code using a marked query gets the BCL's own object (its type, its fast paths: Count, Last, ElementAt).
            cases++;
            IEnumerable<CardModel> marked = ExtraScopes.WhereSecondFilter(cards, innate);
            IEnumerable<CardModel> bcl = Enumerable.Where(cards, innate);
            if (!ExtraScopes.BenchIsScoped(marked) || marked.GetType() != bcl.GetType() || !marked.SequenceEqual(bcl) || marked.Count() != innates
                || !Equals(marked.LastOrDefault(), bcl.LastOrDefault()) || (innates > 0 && !Equals(marked.ElementAt(innates - 1), bcl.ElementAt(innates - 1))))
            {
                failures.Add($"foreign-consumer: marked {ExtraScopes.BenchIsScoped(marked)}, {marked.GetType().Name} (the BCL's {bcl.GetType().Name}), or not the game's cards");
            }
            // Null arguments throw as the game's calls do (same parameter names).
            cases++;
            string? Thrown(Action action)
            {
                try
                {
                    action();
                    return null;
                }
                catch (ArgumentNullException e)
                {
                    return e.ParamName;
                }
            }
            (string Name, string? Own, string? Bcl)[] nulls =
            {
                ("Where source", Thrown(() => ExtraScopes.WhereSecondFilter(null!, innate)), Thrown(() => Enumerable.Where(null!, innate))),
                ("Where predicate", Thrown(() => ExtraScopes.WhereFirstFilter(cards, null!)), Thrown(() => Enumerable.Where(cards, (Func<CardModel, bool>)null!))),
                ("Except first", Thrown(() => ExtraScopes.ExceptInScope(null!, bottoms)), Thrown(() => Enumerable.Except(null!, bottoms))),
                ("Except second", Thrown(() => ExtraScopes.ExceptInScope(ExtraScopes.WhereSecondFilter(cards, innate), null!)), Thrown(() => Enumerable.Except(cards.Where(innate), null!))),
                ("ToList source", Thrown(() => ExtraScopes.ToListInScope(null!)), Thrown(() => Enumerable.ToList((IEnumerable<CardModel>)null!))),
            };
            foreach ((string name, string? own, string? bcl2) in nulls)
            {
                if (own == null || own != bcl2)
                {
                    failures.Add($"null {name}: {own ?? "no exception"} (the game's: {bcl2 ?? "none"})");
                }
            }
        }
        finally
        {
            ExtraScopes.Enabled = enabled;
            ExtraScopes.BenchChecks = checks;
            // The refused cases' notes are this test's own.
            foreach (string key in new[] { "first_turn_bottom", "first_turn_innate" })
            {
                PatchSafety.ClearNote(ExtraScopes.Name, key);
            }
        }
        string summary = $"{cards.Count} combat cards, {bottoms.Count} bottom-of-pile, {innates} Innate";
        return failures.Count == 0
            ? new CmdResult(success: true, $"FIRSTTURN selftest: {cases} cases as expected ({summary})")
            : new CmdResult(success: false, $"FIRSTTURN selftest: {failures.Count} of {cases} case(s) unexpected ({summary}): " + string.Join(" | ", failures));
    }
}
