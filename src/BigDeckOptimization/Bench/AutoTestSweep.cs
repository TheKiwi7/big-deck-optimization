using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Script <c>sweep cards|relics|potions|encounters|powers [first] [count] [checksum]</c>: tries every model of the game
/// one by one and logs a <c>SWEEP</c> line per item (wall time, worst frame, frames over 50 ms, GC pause, bytes allocated,
/// combat cards). Two sessions (huge deck, small deck) compared with <c>tools/autotest/sweepdiff.py</c> show what still
/// depends on the deck size; with <c>checksum</c> two sessions differing by an option are an A/B over the whole game.
/// </summary>
public static partial class AutoTest
{
    private static double SweepItemTimeout = 30;
    private static bool _sweepChecksum, _sweepState;

    private static async Task Sweep(SceneTree tree, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> numbers = parts.Skip(1).Where(p => char.IsDigit(p[0])).ToList();
        int first = numbers.Count > 0 ? int.Parse(numbers[0]) : 0;
        int count = numbers.Count > 1 ? int.Parse(numbers[1]) : int.MaxValue;
        _sweepChecksum = parts.Contains("checksum");
        _sweepState = parts.Contains("state");
        IEnumerable<string> ids = parts[0] switch
        {
            "cards" => ModelDb.AllCards.Select(c => c.Id.Entry),
            "relics" => ModelDb.AllRelics.Select(r => r.Id.Entry),
            "potions" => ModelDb.AllPotions.Select(p => p.Id.Entry),
            "encounters" => ModelDb.AllEncounters.Select(e => e.Id.Entry),
            "powers" => ModelDb.AllPowers.Select(p => p.Id.Entry),
            _ => throw new ArgumentException("sweep " + parts[0]),
        };
        List<string> list = ids.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
        Say($"  SWEEP {parts[0]}: {list.Count} items, from {first}");
        // An item by name: its index changes between game versions.
        if (parts.Skip(1).FirstOrDefault(list.Contains) is { } named)
        {
            list = new List<string> { named };
        }
        int index = first;
        foreach (string id in list.Skip(first).Take(count))
        {
            index++;
            Task item = parts[0] switch
            {
                "cards" => SweepCard(tree, id),
                "relics" => SweepRelic(tree, id),
                "potions" => SweepPotion(tree, id),
                "encounters" => SweepEncounter(tree, id),
                _ => SweepPower(tree, id),
            };
            try
            {
                await item;
            }
            catch (Exception e)
            {
                // Often the game's own error in a situation it never meets (its action queue then stays stuck): nothing
                // after it would mean anything. tools/autotest/sweep.ps1 goes on from the next item in a new session.
                Say($"  SWEEP FAILED {parts[0]} {id}: {e.GetType().Name}: {e.Message}");
                Say($"  SWEEP ABORT next={index}");
                Quit();
                return;
            }
        }
        Say($"  SWEEP {parts[0]} done");
    }

    /// <summary>Runs one step and logs its SWEEP line.</summary>
    private static async Task SweepMeasure(SceneTree tree, string kind, string id, string step, Func<Task> body)
    {
        _frameMs.Clear();
        TimeSpan pause = GC.GetTotalPauseDuration();
        long allocated = GC.GetTotalAllocatedBytes(false);
        long start = Stopwatch.GetTimestamp();
        await body();
        await SweepSettle(tree);
        double wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        double worst = _frameMs.Count > 0 ? _frameMs.Max() : 0;
        int over50 = _frameMs.Count(ms => ms > 50);
        double gc = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(false) - allocated;
        int cards = Me()?.PlayerCombatState?.AllPiles.Sum(p => p.Cards.Count) ?? 0;
        int deck = Me()?.Deck.Cards.Count ?? 0;
        Say($"  SWEEP {kind} {id} {step} wall {wall:F0} worst {worst:F0} over50 {over50} gc {gc:F0} mb {bytes / (1024.0 * 1024):F1} combat {cards} deck {deck}");
        if (_sweepChecksum)
        {
            LogChecksum($"{kind} {id} {step}");
        }
        if (_sweepState)
        {
            // What a different checksum means: which pile count, HP or power moved.
            LogState($"{kind} {id} {step}");
        }
    }

    /// <summary>Until the player can act again, the combat is over, or the item timeout.</summary>
    private static async Task SweepSettle(SceneTree tree)
    {
        await Frames(tree, 2);
        await Until(tree, () => IsIdle() || !CombatManager.Instance.IsInProgress, SweepItemTimeout, "settled");
    }

    /// <summary>A combat with an empty hand and energy to spare, whatever the last item did.</summary>
    private static async Task SweepReady(SceneTree tree)
    {
        if (!CombatManager.Instance.IsInProgress)
        {
            await Command("fight CULTISTS_NORMAL");
            await Frames(tree, 5);
            await Until(tree, IsIdle, 60, "idle");
        }
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        await Until(tree, () => IsIdle() || !CombatManager.Instance.IsInProgress, SweepItemTimeout, "idle");
        if (!CombatManager.Instance.IsInProgress)
        {
            await SweepReady(tree);
            return;
        }
        if (PileType.Hand.GetPile(me).Cards.Count > 0)
        {
            await EmptyHand(tree);
        }
        me.PlayerCombatState!.Energy = 10;
    }

    private static async Task SweepCard(SceneTree tree, string id)
    {
        await SweepReady(tree);
        await Command($"card {id} hand");
        await Frames(tree, 2);
        await SweepMeasure(tree, "card", id, "play", () => SweepPlay(tree, id));
    }

    /// <summary><c>card.TryManualPlay</c> as in <see cref="PlayCard"/>, without its logs and waits.</summary>
    private static async Task SweepPlay(SceneTree tree, string? id)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        CombatState combat = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        Creature? enemy = combat.Enemies.FirstOrDefault(e => e.IsAlive);
        CardModel? card = PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => id == null ? c.CanPlayTargeting(enemy) || c.CanPlayTargeting(null) : c.Id.Entry == id);
        if (card == null)
        {
            return;
        }
        card.SetToFreeThisTurn();
        if (!card.TryManualPlay(card.CanPlayTargeting(enemy) ? enemy : null))
        {
            Say($"  {card.Id.Entry} cannot be played now");
        }
        await Frames(tree, 2);
    }

    private static async Task SweepTurn(SceneTree tree, string kind, string id)
    {
        await SweepMeasure(tree, kind, id, "play1", () => SweepPlay(tree, null));
        await SweepMeasure(tree, kind, id, "endturn", async () =>
        {
            if (CombatManager.Instance.IsInProgress)
            {
                EndTurn();
                await Frames(tree, 5);
            }
        });
    }

    private static async Task SweepRelic(SceneTree tree, string id)
    {
        await SweepReady(tree);
        await SweepMeasure(tree, "relic", id, "obtain", () => Command($"relic add {id}"));
        await SweepTurn(tree, "relic", id);
        // Back to the same relics for the next one (a relic its own effect removed stays removed).
        await SweepReady(tree);
        await Command($"relic remove {id}");
    }

    private static async Task SweepPotion(SceneTree tree, string id)
    {
        await SweepReady(tree);
        await Command($"potion {id}");
        await Frames(tree, 2);
        await SweepMeasure(tree, "potion", id, "use", async () =>
        {
            Player me = Me() ?? throw new InvalidOperationException("no local player");
            PotionModel? potion = me.Potions.LastOrDefault(p => p.Id.Entry == id);
            if (potion == null)
            {
                Say($"  {id} not procured");
                return;
            }
            Creature? enemy = CombatManager.Instance.DebugOnlyGetState()?.Enemies.FirstOrDefault(e => e.IsAlive);
            potion.EnqueueManualUse(potion.TargetType.ToString().Contains("Enemy") ? enemy : me.Creature);
            await Frames(tree, 2);
        });
    }

    private static async Task SweepEncounter(SceneTree tree, string id)
    {
        await SweepKill(tree);
        // The game rolls the rewards' cards 0.5 s after a win (Fast mode, longer after a boss): wait for that rather than
        // the same 0.5 s, or the run's RNG depends on the timing and A/B sessions differ from there on.
        if (RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is CombatRoom { RoomType: not RoomType.Boss, IsPreFinished: true, Encounter.ShouldGiveRewards: true })
        {
            await Until(tree, () => NOverlayStack.Instance?.Peek() is NRewardsScreen, SweepItemTimeout, "rewards");
        }
        await Seconds(tree, 0.5);
        await SweepMeasure(tree, "encounter", id, "start", async () =>
        {
            await Command($"fight {id}");
            await Frames(tree, 5);
            await Until(tree, () => CombatManager.Instance.IsInProgress, SweepItemTimeout, "combat");
        });
        for (int turn = 1; turn <= 3 && CombatManager.Instance.IsInProgress; turn++)
        {
            await SweepTurn(tree, "encounter", id + " t" + turn);
        }
        await SweepMeasure(tree, "encounter", id, "end", () => SweepKill(tree));
    }

    /// <summary>Ends the combat: the enemies can die for this, then the next ones are immortal again.</summary>
    private static async Task SweepKill(SceneTree tree)
    {
        bool immortal = _immortal;
        _immortal = false;
        try
        {
            for (int i = 0; i < 5 && CombatManager.Instance.IsInProgress; i++)
            {
                await Command("kill all");
                double start = _clock.Elapsed.TotalSeconds;
                double idleSince = start;
                int idleFrames = 0;
                // Until the deaths are over: the combat ends, or the player has been able to act for a long while (a boss's next
                // phase, enemies summoned while dying). A second kill during the first one changes the end state, and A/B sessions
                // would differ by their speed: frames and time both, slow frames count for little. Limit: the slowest death with
                // the mod off (Knights ~20 s at 1,900 cards).
                while (CombatManager.Instance.IsInProgress && _clock.Elapsed.TotalSeconds - start < 60 && (idleFrames < 60 || _clock.Elapsed.TotalSeconds - idleSince < 1))
                {
                    await Frames(tree, 1);
                    if (IsIdle())
                    {
                        idleFrames++;
                    }
                    else
                    {
                        idleFrames = 0;
                        idleSince = _clock.Elapsed.TotalSeconds;
                    }
                }
            }
        }
        finally
        {
            _immortal = immortal;
        }
    }

    private static async Task SweepPower(SceneTree tree, string id)
    {
        await SweepReady(tree);
        foreach ((string side, int index) in new[] { ("player", 0), ("enemy", 1) })
        {
            await SweepMeasure(tree, "power", id, "apply-" + side, () => Command($"power {id} 3 {index}"));
        }
        await SweepTurn(tree, "power", id);
    }
}
