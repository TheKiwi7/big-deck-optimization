using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// bdperf console cmd. Measures game costs that only appear in multiplayer, from a singleplayer run.
///   bdperf checksum [iterations] — times the post-action checksum (full state snapshot + serialize + hash),
///   vanilla vs optimized, and checks both produce the same checksum.
/// Read-only: builds snapshots but never sends, tracks or compares them with peers.
/// </summary>
public class PerfConsoleCmd : AbstractConsoleCmd
{
    private readonly struct Sample
    {
        public double SnapshotMs { get; init; }
        public double HashMs { get; init; }
        public double WorstMs { get; init; }
        public double AllocatedKb { get; init; }
        public uint Checksum { get; init; }

        public string Format(string label) =>
            $"  {label}: snapshot {SnapshotMs:F2} ms + hash {HashMs:F2} ms = {SnapshotMs + HashMs:F2} ms (worst {WorstMs:F2}), {AllocatedKb:F0} KB, checksum {Checksum}";
    }

    public override string CmdName => "bdperf";

    public override string Args => "checksum [iterations:int] | refresh [iterations:int] | verify [off|sample|all] | combatstart | grid [cards:int] | probe [reset|log|on|off|hitch <ms>] | pile [verify <n>] | plaque [cards:int] | checks on|off | opt on|off | listeners [iterations:int]";

    public override string Description => "[BigDeckOptimization] Measures the multiplayer post-action checksum cost, vanilla vs optimized, or shows/sets live snapshot verification.";

    public override bool IsNetworked => false;

    private static int _spins;

    /// <summary>A loop pacing itself on Cmd.Wait, as the transform preview: with waits skipped it must still give the frame back.</summary>
    private static CmdResult WaitSpin()
    {
        _spins = 0;
        ulong frame = Godot.Engine.GetProcessFrames();
        System.Threading.Tasks.Task spin = Spin(frame);
        return new CmdResult(success: !spin.IsCompleted, $"WAITSPIN {_spins} waits returned at once, then {(spin.IsCompleted ? "NEVER gave the frame back" : "the frame was given back")}");
    }

    private static async System.Threading.Tasks.Task Spin(ulong frame)
    {
        using var cancel = new System.Threading.CancellationTokenSource();
        while (_spins < 200000 && Godot.Engine.GetProcessFrames() == frame)
        {
            _spins++;
            await MegaCrit.Sts2.Core.Commands.Cmd.Wait(0.2f, cancel.Token, ignoreCombatEnd: true);
        }
    }

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        // The benches freeze the game for seconds (vanilla runs) and replay game code: keep them out of real co-op.
        if (sub is "checksum" or "refresh" && RunManager.Instance.IsInProgress && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            return new CmdResult(success: false, "bdperf " + sub + " is singleplayer only. In co-op use 'bdperf verify'.");
        }
        switch (sub)
        {
            case "checksum":
                int iterations = args.Length >= 2 && int.TryParse(args[1], out int n) && n > 0 ? n : 5;
                return CompareChecksum(iterations);
            case "verify":
                return Verify(args.Length >= 2 ? args[1] : null);
            case "blocks" when args.Length >= 2:
                SnapshotCardReuse.BlockCopies = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: unchanged runs of a pile's cards {(SnapshotCardReuse.BlockCopies ? "copied as blocks" : "composed card by card")}; {SnapshotCardReuse.BlockCopiedCards} cards copied in blocks so far.");
            case "upgradebits" when args.Length >= 2 && args[1] is "on" or "off" or "wrong":
                SnapshotCardReuse.UpgradeBits = args[1] != "off";
                SnapshotCardReuse.UpgradeBitsWrongForTest = args[1] == "wrong";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: an upgraded card's bits {(SnapshotCardReuse.UpgradeBits ? "patched" : "written in full")}; patched {SnapshotCardReuse.UpgradeBitsPatched}, mismatches {SnapshotCardReuse.Mismatches}; not patched: "
                    + string.Join(", ", SnapshotCardReuse.WhyNoUpgradeBits.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")) + $"; cards upgraded (light snapshot objects) {SnapshotCardReuse.LightBuilt}");
            case "envrelease" when args.Length >= 2 && args[1] is "on" or "off":
                RichTextEnvRelease.Enabled = args[1] == "on";
                return new CmdResult(success: true, $"{RichTextEnvRelease.Name}: text effects' dictionaries {(RichTextEnvRelease.Enabled ? "disposed after use" : "left to their finalizers")}; released {RichTextEnvRelease.Released}.");
            case "gcmin" when args.Length >= 2 && int.TryParse(args[1], out int minCards) && minCards >= 0:
                GcPacing.MinCards = minCards;
                return new CmdResult(success: true, $"{GcPacing.Name}: combats from {minCards} cards; regions {GcPacing.Regions}, collections {GcPacing.Collections}.");
            case "release" when args.Length >= 2:
                // What a finished combat or a co-op sync leaves behind, kept as before: comparison sessions.
                SyncedCardRelease.Enabled = args[1] != "off";
                SnapshotCardReuse.ReleaseCombats = args[1] != "off";
                return new CmdResult(success: true, $"releases {args[1]}");
            case "clonememo":
                if (args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int cloneEvery) && cloneEvery > 0)
                {
                    LimitlessCloneMemo.VerifyEvery = cloneEvery;
                }
                return new CmdResult(success: LimitlessCloneMemo.Enabled, $"CLONEMEMO enabled {LimitlessCloneMemo.Enabled}, reused {LimitlessCloneMemo.Reused}, verified {LimitlessCloneMemo.Verified}");
            case "deckcount":
                if (args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int countEvery) && countEvery > 0)
                {
                    DeckCountMemo.VerifyEvery = countEvery;
                }
                else if (args.Length >= 2 && args[1] == "wrong")
                {
                    DeckCountMemo.WrongForTest = 1;
                }
                return new CmdResult(success: DeckCountMemo.Mismatches == 0, $"DECKCOUNT enabled {DeckCountMemo.Enabled}, kept {DeckCountMemo.Kept}, updated {DeckCountMemo.Updated}, walks {DeckCountMemo.Walks}, "
                    + $"unsure {DeckCountMemo.Unsure}, checks {DeckCountMemo.Checks}, mismatches {DeckCountMemo.Mismatches}");
            case "shufflebench":
                return ShuffleBench.Run(args.Length >= 2 && int.TryParse(args[1], out int shufflePasses) && shufflePasses > 0 ? shufflePasses : 5);
            case "upgradebench":
                return UpgradeBench.Run(args.Length >= 2 && int.TryParse(args[1], out int upgradePasses) && upgradePasses > 0 ? upgradePasses : 20);
            case "relics":
                return RelicBench.Run(args.Length >= 2 && int.TryParse(args[1], out int passes) && passes > 0 ? passes : 20);
            case "waitspin":
                return WaitSpin();
            case "relicreuse" when args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int relicEvery) && relicEvery > 0:
                RelicStateReuse.ScalarVerifyEvery = relicEvery;
                return new CmdResult(success: true, $"{RelicStateReuse.ScalarName}: one answer in {relicEvery} compared with the game's.");
            case "relicreuse" when args.Length >= 3 && args[1] == "scalarblind":
                RelicStateReuse.ScalarBlindForTest = args[2] == "on";
                return new CmdResult(success: true, $"{RelicStateReuse.ScalarName}: relics {(RelicStateReuse.ScalarBlindForTest ? "taken as unchanged without reading them (test)" : "read")}; reused {RelicStateReuse.ScalarReused}, verified {RelicStateReuse.ScalarVerified}.");
            case "relicreuse" when args.Length >= 3 && args[1] == "blind":
                RelicStateReuse.BlindForTest = args[2] == "on";
                return new CmdResult(success: true, $"{RelicStateReuse.Name}: saved properties {(RelicStateReuse.BlindForTest ? "taken as unchanged (test)" : "compared")}; reused {RelicStateReuse.Reused}, built {RelicStateReuse.Built}.");
            case "grid":
                int tabCards = args.Length >= 2 && int.TryParse(args[1], out int g) && g > 0 ? g : 100;
                return GridBench.Run(tabCards);
            case "combatstart":
                return new CmdResult(success: true, CombatStartTimer.LastReport);
            case "probe":
                return ProbeCmd(args.Skip(1).ToArray());
            case "pile" when args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int every) && every > 0:
                PileIndex.VerifyEvery = every;
                return new CmdResult(success: true, $"PileIndex verifies 1 answer in {every}.");
            case "pile":
                return ComparePile();
            case "text":
                return CompareText();
            case "scan":
                return CompareScan();
            case "costhooks":
                // Who keeps SnapshotCardReuse from trusting piles: the listeners of the keyword and energy cost hooks in this combat.
                if (CombatManager.Instance.DebugOnlyGetState() is not { } costCombat)
                {
                    return new CmdResult(success: false, "Start a combat first.");
                }
                string Types(int hook)
                {
                    ListenerCache.InvokeStateChangedInScope(_ => _costList = HookFilter.ForCombat(costCombat, hook), costCombat);
                    return _costList is { } list
                    ? $"{list.Count}: " + string.Join(", ", list.GroupBy(m => m.GetType().Name).Select(g => $"{g.Key} x{g.Count()}"))
                    : "not filtered";
                }
                return new CmdResult(success: true, $"keywords {Types(HookFilter.Keywords)} | cost {Types(HookFilter.EnergyCost)} | cost late {Types(HookFilter.EnergyCostLate)}");
            case "piletimes":
                lock (SnapshotCardReuse.PileTimes)
                {
                    string times = string.Join(", ", SnapshotCardReuse.PileTimes.OrderByDescending(p => p.Value.Ms).Select(p => $"{p.Key}: {p.Value.Calls} calls {p.Value.Ms:F1} ms {p.Value.Cards / Math.Max(1, p.Value.Calls)} cards avg"));
                    if (args.Length >= 2 && args[1] == "reset")
                    {
                        SnapshotCardReuse.PileTimes.Clear();
                    }
                    return new CmdResult(success: true, "PILE TIMES " + times);
                }
            case "costwrites":
                {
                    // Whether a cost change reaches SnapshotCardReuse's write prefix, called directly and through the card.
                    if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } costRun || LocalContext.GetMe(costRun) is not { } costMe
                        || PileType.Hand.GetPile(costMe).Cards.FirstOrDefault() is not { } costCard)
                    {
                        return new CmdResult(success: false, "Start a combat with a card in hand first.");
                    }
                    long before = SnapshotCardReuse.CostWriteCalls;
                    costCard.EnergyCost.SetThisTurnOrUntilPlayed(costCard.EnergyCost.GetWithModifiers(CostModifiers.Local));
                    long direct = SnapshotCardReuse.CostWriteCalls - before;
                    before = SnapshotCardReuse.CostWriteCalls;
                    costCard.SetToFreeThisTurn();
                    long viaCard = SnapshotCardReuse.CostWriteCalls - before;
                    return new CmdResult(success: true, $"COSTWRITES {costCard.Id}: SetThisTurnOrUntilPlayed called directly {direct}, through SetToFreeThisTurn {viaCard} (total {SnapshotCardReuse.CostWriteCalls})");
                }
            case "purehooks" when args.Length >= 2 && args[1] == "blind":
                SnapshotCardReuse.PureHooksBlindForTest = true;
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: listeners compared without their amounts (test).");
            case "purehooks" when args.Length >= 2 && args[1] is "on" or "off":
                SnapshotCardReuse.PureHookReads = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: piles {(SnapshotCardReuse.PureHookReads ? "trusted" : "read in full")} with pure cost or keyword listeners; {SnapshotCardReuse.PilesWithHooks} so far.");
            case "dirtyreads" when args.Length >= 2 && args[1] is "on" or "off":
                SnapshotCardReuse.DirtyReads = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: piles with written cards {(SnapshotCardReuse.DirtyReads ? "read those only" : "read in full")}; {SnapshotCardReuse.DirtyPiles} so far.");
            case "snapmoved" when args.Length >= 2 && args[1] is "on" or "off":
                SnapshotCardReuse.MovedPiles = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name} moved piles {args[1]}.");
            case "plaque":
                int plaqueCards = args.Length >= 2 && int.TryParse(args[1], out int pc) && pc > 0 ? pc : 200;
                return CompareTypePlaque(plaqueCards);
            case "segments" when args.Length >= 3 && args[1] == "verify" && args[2] is "all" or "sample" or "off":
                FastListeners.VerifyAll = args[2] == "all";
                FastListeners.CacheVerifyEvery = args[2] == "off" ? int.MaxValue : 256;
                return new CmdResult(success: true, $"{FastListeners.Name} compares {(FastListeners.VerifyAll ? "every" : "1 in " + FastListeners.CacheVerifyEvery)} cached pile walk with a fresh walk.");
            case "checks" when args.Length >= 2 && args[1] is "on" or "off":
                ExtraScopes.BenchChecks = args[1] == "on";
                PileIndex.VerifyEvery = ExtraScopes.BenchChecks ? 1024 : int.MaxValue;
                return new CmdResult(success: true, $"Bench re-checks {args[1]} (first-turn filter, ShouldAllowHitting, PileIndex 1/1024).");
            case "fault" when args.Length >= 2 && args[1] == "piles":
                FastListeners.FailNextPileWalk = true;
                return new CmdResult(success: true, "The next filtered pile walk throws inside FastListeners' cache (test of its fallback).");
            case "gcprobe":
                return GcProbe();
            case "opt":
                return SetOptimizations(args.Length >= 2 ? args[1] : null);
            case "listeners":
                int listenerIterations = args.Length >= 2 && int.TryParse(args[1], out int li) && li > 0 ? li : 50;
                return CompareListeners(listenerIterations);
            case "refresh":
                int refreshIterations = args.Length >= 2 && int.TryParse(args[1], out int r) && r > 0 ? r : 5;
                return CompareRefresh(refreshIterations);
            default:
                return new CmdResult(success: false, "Usage: bdperf " + Args);
        }
    }

    /// <summary>
    /// bdperf gcprobe: what an ephemeral collection costs right now, and how much of it each 100,000 extra dependent
    /// handles (ConditionalWeakTable entries, scanned by every collection) add.
    /// </summary>
    private static CmdResult GcProbe()
    {
        static double Pause(int generation)
        {
            double total = 0;
            for (int i = 0; i < 8; i++)
            {
                TimeSpan before = GC.GetTotalPauseDuration();
                GC.Collect(generation, GCCollectionMode.Forced, blocking: true);
                total += (GC.GetTotalPauseDuration() - before).TotalMilliseconds;
            }
            return total / 8;
        }
        double gen0 = Pause(0), gen1 = Pause(1);
        var keys = new object[100_000];
        var values = new object[keys.Length];
        var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new object();
            values[i] = new object();
            table.Add(keys[i], values[i]);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        double gen0Extra = Pause(0), gen1Extra = Pause(1);
        GC.KeepAlive(keys);
        GC.KeepAlive(values);
        return new CmdResult(success: true, $"GCPROBE gen0 {gen0:F1} ms, gen1 {gen1:F1} ms; with 100,000 more dependent handles gen0 {gen0Extra:F1} ms, gen1 {gen1Extra:F1} ms; latency {System.Runtime.GCSettings.LatencyMode}, server {System.Runtime.GCSettings.IsServerGC}");
    }

    /// <summary>
    /// Turns every optimization off/on for an A/B comparison in the same session. Safe in co-op: optimized and vanilla
    /// paths give identical results, so peers can't tell.
    /// </summary>
    /// <summary>
    /// bdperf listeners [n]: the combat and run (with the combat as child) listener sequences, FastListeners off then
    /// on: the same objects in the same order, and the time of n full walks each.
    /// </summary>
    /// <summary>
    /// <c>bdperf scan</c>: reading <c>Keywords</c> of every card of the draw pile, as Catastrophe, Uproar or Hidden Gem do outside any
    /// scope: vanilla walk, filtered walk, filtered walk leaving out kinds of listener. Same keywords for every card, or it says so.
    /// </summary>
    private static List<AbstractModel>? _costList;

    private static CmdResult CompareScan()
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        List<CardModel> cards = PileType.Draw.GetPile(me).Cards.ToList();
        (double Ms, List<string> Sets) Scan()
        {
            Stopwatch sw = Stopwatch.StartNew();
            foreach (CardModel card in cards)
            {
                _ = card.Keywords.Contains(CardKeyword.Unplayable);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            return (ms, cards.Select(c => string.Join(",", c.Keywords.OrderBy(k => k))).ToList());
        }
        bool kinds = FastListeners.KindSkipEnabled;
        try
        {
            (double Ms, List<string> Sets) vanilla = ListenerCache.RunVanilla(Scan);
            FastListeners.KindSkipEnabled = false;
            Scan();
            (double Ms, List<string> Sets) filtered = Scan();
            FastListeners.KindSkipEnabled = true;
            Scan();
            (double Ms, List<string> Sets) skipped = Scan();
            bool same = vanilla.Sets.SequenceEqual(filtered.Sets) && vanilla.Sets.SequenceEqual(skipped.Sets);
            return new CmdResult(success: true, $"keyword scan of {cards.Count} cards: vanilla {vanilla.Ms:F1} ms, filtered {filtered.Ms:F1} ms, kinds left out {skipped.Ms:F1} ms; keywords {(same ? "identical" : "DIFFERENT")}");
        }
        finally
        {
            FastListeners.KindSkipEnabled = kinds;
        }
    }

    private static CmdResult CompareListeners(int iterations)
    {
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        if (run == null || combat == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!FastListeners.Applied)
        {
            return new CmdResult(success: false, "FastListeners is not applied (see the log).");
        }

        (double CombatMs, double RunMs, List<AbstractModel> Combat, List<AbstractModel> Run) Walk()
        {
            var combatList = combat.IterateHookListeners().ToList();
            var runList = run.IterateHookListeners(combat).ToList();
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                foreach (AbstractModel _ in combat.IterateHookListeners())
                {
                }
            }
            double combatMs = sw.Elapsed.TotalMilliseconds / iterations;
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                foreach (AbstractModel _ in run.IterateHookListeners(combat))
                {
                }
            }
            return (combatMs, sw.Elapsed.TotalMilliseconds / iterations, combatList, runList);
        }

        bool wasEnabled = FastListeners.Enabled;
        (double CombatMs, double RunMs, List<AbstractModel> Combat, List<AbstractModel> Run) vanilla, fast;
        int gen2 = GC.CollectionCount(2);
        try
        {
            FastListeners.Enabled = false;
            Walk();
            gen2 = GC.CollectionCount(2);
            vanilla = Walk();
            int vanillaGen2 = GC.CollectionCount(2) - gen2;
            FastListeners.Enabled = true;
            Walk();
            gen2 = GC.CollectionCount(2);
            fast = Walk();
            int fastGen2 = GC.CollectionCount(2) - gen2;
            bool same = vanilla.Combat.SequenceEqual(fast.Combat, ReferenceEqualityComparer.Instance)
                && vanilla.Run.SequenceEqual(fast.Run, ReferenceEqualityComparer.Instance);
            string text = $"{(same ? "OK: identical" : "MISMATCH")} listeners: combat {vanilla.Combat.Count}/{fast.Combat.Count}, " +
                $"run {vanilla.Run.Count}/{fast.Run.Count}. Walk: combat {vanilla.CombatMs:F2} -> {fast.CombatMs:F2} ms, " +
                $"run {vanilla.RunMs:F2} -> {fast.RunMs:F2} ms; gen2 collections {vanillaGen2} -> {fastGen2} over {iterations * 2} walks.";
            Log.Info($"[{ModEntry.Id}] bdperf listeners: {text}");
            return new CmdResult(same, text);
        }
        finally
        {
            FastListeners.Enabled = wasEnabled;
        }
    }

    private static CmdResult SetOptimizations(string? value)
    {
        if (value is not ("on" or "off"))
        {
            return new CmdResult(success: false, "Usage: bdperf opt on|off");
        }
        bool on = value == "on";
        Commands.OptimizationsCmd.SetAll(on);
        return new CmdResult(success: true, $"BigDeckOptimization optimizations {(on ? "ON" : "OFF (vanilla)")}.");
    }

    /// <summary>CardModel.Pile for every card of every pile: indexed vs vanilla answers and timings.</summary>
    /// <summary>
    /// bdperf plaque [n]: BaseLib's card type line for the first n cards of the local player's piles, TypePlaqueSkip
    /// off (BaseLib's walk of every hook listener) then on: the same LocString object for every card, and the times.
    /// </summary>
    private static CmdResult CompareTypePlaque(int count)
    {
        Player? me = RunManager.Instance.DebugOnlyGetState() is { } state ? LocalContext.GetMe(state) : null;
        if (me == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!TypePlaqueSkip.Applied)
        {
            return new CmdResult(success: false, "TypePlaqueSkip is not applied (BaseLib absent or changed, see the log).");
        }
        List<CardModel> cards = me.Piles.SelectMany(p => p.Cards).Take(count).ToList();
        var originals = cards.Select(c => c.Type.ToLocString()).ToList();
        bool wasEnabled = TypePlaqueSkip.Enabled;
        long skippedBefore = TypePlaqueSkip.Skipped;
        List<LocString> expected, actual;
        Stopwatch vanilla = Stopwatch.StartNew();
        TypePlaqueSkip.Enabled = false;
        try
        {
            expected = cards.Select((c, i) => TypePlaqueSkip.Invoke(originals[i], c)).ToList();
        }
        finally
        {
            TypePlaqueSkip.Enabled = wasEnabled;
        }
        vanilla.Stop();
        Stopwatch fast = Stopwatch.StartNew();
        TypePlaqueSkip.Enabled = true;
        try
        {
            actual = cards.Select((c, i) => TypePlaqueSkip.Invoke(originals[i], c)).ToList();
        }
        finally
        {
            TypePlaqueSkip.Enabled = wasEnabled;
        }
        fast.Stop();
        int mismatches = Enumerable.Range(0, cards.Count)
            .Count(i => !ReferenceEquals(expected[i], actual[i]) || expected[i].GetFormattedText() != actual[i].GetFormattedText());
        string verdict = mismatches == 0 ? "OK: identical type lines" : $"MISMATCH: {mismatches} cards differ";
        return new CmdResult(mismatches == 0,
            $"type line — {cards.Count} cards ({me.Piles.Sum(p => p.Cards.Count)} in piles), skipped {TypePlaqueSkip.Skipped - skippedBefore}\n"
            + $"  BaseLib: {vanilla.Elapsed.TotalMilliseconds:F1} ms ({vanilla.Elapsed.TotalMilliseconds / Math.Max(1, cards.Count):F3} ms/card)\n"
            + $"  skip   : {fast.Elapsed.TotalMilliseconds:F1} ms ({fast.Elapsed.TotalMilliseconds / Math.Max(1, cards.Count):F3} ms/card)\n  {verdict}");
    }

    private static CmdResult ComparePile()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return new CmdResult(success: false, "Start a run first.");
        }
        System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel> cards = state.Players
            .SelectMany(p => p.Piles).SelectMany(p => p.Cards).ToList();
        int mismatches = 0;
        Stopwatch vanilla = Stopwatch.StartNew();
        var expected = cards.Select(PileIndex.Vanilla).ToList();
        vanilla.Stop();
        Stopwatch fast = Stopwatch.StartNew();
        var actual = cards.Select(c => c.Pile).ToList();
        fast.Stop();
        for (int i = 0; i < cards.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                mismatches++;
            }
        }
        string verdict = mismatches == 0 ? "OK: identical piles" : $"MISMATCH: {mismatches} cards differ";
        return new CmdResult(mismatches == 0,
            $"pile lookup — {cards.Count} cards, index {(PileIndex.Enabled ? "on" : "OFF")}\n"
            + $"  vanilla: {vanilla.Elapsed.TotalMilliseconds:F1} ms ({vanilla.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, cards.Count):F2} us/card)\n"
            + $"  index  : {fast.Elapsed.TotalMilliseconds:F1} ms ({fast.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, cards.Count):F2} us/card)\n"
            + $"  fast {PileIndex.Fast}, fallbacks {PileIndex.Fallbacks}, verified {PileIndex.Verified}\n  {verdict}");
    }

    /// <summary>
    /// Every card's description (with its enchantment and affliction texts) formatted with TextPoolReuse on and off:
    /// identical texts, time and bytes allocated each way.
    /// </summary>
    private static CmdResult CompareText()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return new CmdResult(success: false, "Start a run first.");
        }
        System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel> cards = state.Players
            .SelectMany(p => p.Piles).SelectMany(p => p.Cards).ToList();
        bool was = TextPoolReuse.Enabled;
        try
        {
            (System.Collections.Generic.List<string> texts, double ms, long bytes) Format(bool reuse)
            {
                TextPoolReuse.Enabled = reuse;
                long before = GC.GetAllocatedBytesForCurrentThread();
                Stopwatch watch = Stopwatch.StartNew();
                var texts = cards.Select(c => c.GetDescriptionForPile(c.Pile?.Type ?? PileType.Deck)).ToList();
                return (texts, watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            var vanilla = Format(false);
            var reused = Format(true);
            int mismatches = vanilla.texts.Zip(reused.texts).Count(pair => !string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
            string verdict = mismatches == 0 ? "OK: identical texts" : $"MISMATCH: {mismatches} texts differ";
            return new CmdResult(mismatches == 0,
                $"card texts — {cards.Count} cards, {TextPoolReuse.Name} {(TextPoolReuse.Applied ? "applied" : "NOT applied")}\n"
                + $"  vanilla: {vanilla.ms:F1} ms, {vanilla.bytes / 1048576.0:F1} MB allocated\n"
                + $"  shared : {reused.ms:F1} ms, {reused.bytes / 1048576.0:F1} MB allocated\n  {verdict}");
        }
        finally
        {
            TextPoolReuse.Enabled = was;
        }
    }

    private static bool _probeApplied;

    private static CmdResult ProbeCmd(string[] args)
    {
        // Only when asked: a probe's stub allocates on every call of very hot methods (CardModel.Pile), which falsifies measures.
        if (!_probeApplied && !(args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            _probeApplied = true;
            ModEntry.TryApply(PerfProbe.Name, PerfProbe.Apply);
        }
        switch (args.Length > 0 ? args[0].ToLowerInvariant() : "")
        {
            case "":
                return new CmdResult(success: true, PerfProbe.Report());
            case "reset":
                PerfProbe.Reset();
                return new CmdResult(success: true, "Probe stats cleared.");
            case "log":
                PerfProbe.LogReport();
                return new CmdResult(success: true, "Probe report written to the game log.");
            case "on" or "off":
                PerfProbe.Enabled = args[0].ToLowerInvariant() == "on";
                return new CmdResult(success: true, $"Probe {(PerfProbe.Enabled ? "on" : "off")}.");
            case "nodes":
                ModEntry.TryApply(PerfProbe.Name + ".Nodes", PerfProbe.ApplyNodes);
                return new CmdResult(success: true, "Node callbacks probed.");
            case "hitch" when args.Length > 1 && double.TryParse(args[1], out double ms) && ms > 0:
                PerfProbe.HitchMs = ms;
                return new CmdResult(success: true, $"Hitch threshold {ms:F0} ms.");
            default:
                return new CmdResult(success: false, "Usage: bdperf probe [reset|log|on|off|hitch <ms>]");
        }
    }

    private static CmdResult Verify(string? mode)
    {
        // "check": fails when a snapshot differed, so a scenario's cmd line ends the session there.
        if (mode == "check")
        {
            return new CmdResult(success: SnapshotVerifier.Mismatches == 0 && SnapshotVerifier.Verified > 0,
                $"verified {SnapshotVerifier.Verified}, mismatches {SnapshotVerifier.Mismatches}");
        }
        if (mode != null)
        {
            if (!TryParseEnum(mode, out VerifyMode parsed))
            {
                return new CmdResult(success: false, "Mode must be off, sample or all.");
            }
            SnapshotVerifier.Mode = parsed;
        }
        return new CmdResult(success: true,
            $"verify mode: {SnapshotVerifier.Mode} — snapshots {SnapshotVerifier.Snapshots}, verified {SnapshotVerifier.Verified}, mismatches {SnapshotVerifier.Mismatches}, skipped (too large) {SnapshotVerifier.SkippedTooLarge}\n"
            + $"  {ListenerCache.Name}: snapshot {(ListenerCache.SnapshotEnabled ? "on" : "OFF")}, refresh {(ListenerCache.RefreshEnabled ? "on" : "OFF")}, {HookFilter.Name}: {(HookFilter.Enabled ? "on" : "OFF")}, reuse {(ListenerCache.ReuseEnabled ? "on" : "OFF")} (reused {ListenerCache.Reused}, verified {ListenerCache.ReuseVerified}), hitting checks {HookFilter.HittingChecks} mismatches {HookFilter.HittingMismatches}");
    }

    /// <summary>
    /// Replays what the game does on every combat state change (enchantment recalculation + hand refresh), vanilla vs
    /// cached, then checks both leave the exact same game state (vanilla snapshot checksum).
    /// </summary>
    private static CmdResult CompareRefresh(int iterations)
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        NPlayerHand? hand = NPlayerHand.Instance;
        Player? me = state == null ? null : LocalContext.GetMe(state);
        if (state == null || combat == null || hand == null || me?.PlayerCombatState == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        MethodInfo handRefresh = AccessTools.Method(typeof(NPlayerHand), "OnCombatStateChanged");
        ChecksumTracker tracker = RunManager.Instance.ChecksumTracker;

        (double Ms, uint Checksum) Run()
        {
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                me.PlayerCombatState.RecalculateCardValues();
                // Same wrapping as the game's CombatStateChanged dispatch (one scope for all handlers).
                ListenerCache.InvokeStateChangedInScope(s => handRefresh.Invoke(hand, new object[] { s }), combat);
            }
            double ms = sw.Elapsed.TotalMilliseconds / iterations;
            uint checksum = ListenerCache.RunVanilla(() => tracker.GenerateChecksum(NetFullCombatState.FromRun(state, null)));
            return (ms, checksum);
        }

        bool wasEnabled = ListenerCache.RefreshEnabled;
        VerifyMode verifyMode = SnapshotVerifier.Mode;
        (double Ms, uint Checksum) vanilla, cached;
        try
        {
            SnapshotVerifier.Mode = VerifyMode.Off;
            ListenerCache.RefreshEnabled = false;
            Run(); // warm-up
            vanilla = Run();
            ListenerCache.RefreshEnabled = true;
            Run();
            cached = Run();
        }
        finally
        {
            ListenerCache.RefreshEnabled = wasEnabled;
            SnapshotVerifier.Mode = verifyMode;
        }

        bool identical = vanilla.Checksum == cached.Checksum;
        string msg = $"refresh x{iterations} — hand: {me.PlayerCombatState.Hand.Cards.Count} cards, combat cards: {me.PlayerCombatState.AllCards.Count()}\n"
            + $"  vanilla: {vanilla.Ms:F2} ms per refresh, state checksum {vanilla.Checksum}\n"
            + $"  cached : {cached.Ms:F2} ms per refresh, state checksum {cached.Checksum}\n"
            + (identical ? $"  OK: identical state, x{vanilla.Ms / Math.Max(0.001, cached.Ms):F1} faster" : "  MISMATCH: refresh left a different game state!");
        if (identical)
        {
            Log.Info("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        else
        {
            Log.Error("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        return new CmdResult(success: identical, msg);
    }

    private static CmdResult CompareChecksum(int iterations)
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first: the checksum covers combat piles.");
        }
        ChecksumTracker tracker = RunManager.Instance.ChecksumTracker;

        bool cacheWasEnabled = ListenerCache.SnapshotEnabled, filterWasEnabled = HookFilter.Enabled, reuseWasEnabled = SnapshotCardReuse.Enabled;
        VerifyMode verifyMode = SnapshotVerifier.Mode;
        Sample vanilla, cacheOnly, filtered, optimized;
        try
        {
            // The bench compares against vanilla itself; live verification would skew timings.
            SnapshotVerifier.Mode = VerifyMode.Off;
            ListenerCache.SnapshotEnabled = false;
            HookFilter.Enabled = false;
            SnapshotCardReuse.Enabled = false;
            vanilla = Measure(state, tracker, iterations);
            ListenerCache.SnapshotEnabled = true;
            cacheOnly = Measure(state, tracker, iterations);
            HookFilter.Enabled = true;
            filtered = Measure(state, tracker, iterations);
            SnapshotCardReuse.Enabled = reuseWasEnabled;
            optimized = Measure(state, tracker, iterations);
        }
        finally
        {
            ListenerCache.SnapshotEnabled = cacheWasEnabled;
            HookFilter.Enabled = filterWasEnabled;
            SnapshotCardReuse.Enabled = reuseWasEnabled;
            SnapshotVerifier.Mode = verifyMode;
        }

        int cards = 0, relics = 0;
        foreach (Player p in state.Players)
        {
            relics += p.Relics.Count;
            if (p.PlayerCombatState != null)
            {
                foreach (var _ in p.PlayerCombatState.AllCards)
                {
                    cards++;
                }
            }
        }

        bool identical = vanilla.Checksum == cacheOnly.Checksum && vanilla.Checksum == filtered.Checksum && vanilla.Checksum == optimized.Checksum;
        double speedup = (vanilla.SnapshotMs + vanilla.HashMs) / Math.Max(0.001, optimized.SnapshotMs + optimized.HashMs);
        string msg = $"checksum x{iterations} — players: {state.Players.Count}, combat cards: {cards}, relics: {relics}\n"
            + vanilla.Format("vanilla     ") + "\n"
            + cacheOnly.Format("cache       ") + "\n"
            + filtered.Format("cache+filter") + "\n"
            + optimized.Format("+card reuse ") + "\n"
            + (identical ? $"  OK: identical checksums, x{speedup:F1} faster" : "  MISMATCH: optimized checksum differs from vanilla!");
        if (identical)
        {
            Log.Info("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        else
        {
            Log.Error("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        return new CmdResult(success: identical, msg);
    }

    private static Sample Measure(RunState state, ChecksumTracker tracker, int iterations)
    {
        // Warm-up so JIT doesn't pollute the first sample.
        tracker.GenerateChecksum(NetFullCombatState.FromRun(state, null));

        double snapshotMs = 0, hashMs = 0, worstMs = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        uint checksum = 0;
        Stopwatch sw = new();
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            NetFullCombatState snapshot = NetFullCombatState.FromRun(state, null);
            double snap = sw.Elapsed.TotalMilliseconds;
            checksum = tracker.GenerateChecksum(snapshot);
            double total = sw.Elapsed.TotalMilliseconds;
            snapshotMs += snap;
            hashMs += total - snap;
            worstMs = Math.Max(worstMs, total);
        }
        return new Sample
        {
            SnapshotMs = snapshotMs / iterations,
            HashMs = hashMs / iterations,
            WorstMs = worstMs,
            AllocatedKb = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024.0 / iterations,
            Checksum = checksum,
        };
    }
}
