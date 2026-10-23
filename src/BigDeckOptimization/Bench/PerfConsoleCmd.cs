using System;
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
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
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

    public override string Args => "checksum [iterations:int] | refresh [iterations:int] | verify [off|sample|all] | combatstart | grid [cards:int] | probe [reset|log|on|off|hitch <ms>] | pile [verify <n>] | checks on|off | opt on|off";

    public override string Description => "[BigDeckOptimization] Measures the multiplayer post-action checksum cost, vanilla vs optimized, or shows/sets live snapshot verification.";

    public override bool IsNetworked => false;

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
            case "checks" when args.Length >= 2 && args[1] is "on" or "off":
                ExtraScopes.BenchChecks = args[1] == "on";
                PileIndex.VerifyEvery = ExtraScopes.BenchChecks ? 1024 : int.MaxValue;
                return new CmdResult(success: true, $"Bench re-checks {args[1]} (first-turn filter, ShouldAllowHitting, PileIndex 1/1024).");
            case "opt":
                return SetOptimizations(args.Length >= 2 ? args[1] : null);
            case "refresh":
                int refreshIterations = args.Length >= 2 && int.TryParse(args[1], out int r) && r > 0 ? r : 5;
                return CompareRefresh(refreshIterations);
            default:
                return new CmdResult(success: false, "Usage: bdperf " + Args);
        }
    }

    /// <summary>
    /// Turns every optimization off/on for an A/B comparison in the same session. Safe in co-op: optimized and vanilla
    /// paths give identical results, so peers can't tell.
    /// </summary>
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

    private static CmdResult ProbeCmd(string[] args)
    {
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
            case "hitch" when args.Length > 1 && double.TryParse(args[1], out double ms) && ms > 0:
                PerfProbe.HitchMs = ms;
                return new CmdResult(success: true, $"Hitch threshold {ms:F0} ms.");
            default:
                return new CmdResult(success: false, "Usage: bdperf probe [reset|log|on|off|hitch <ms>]");
        }
    }

    private static CmdResult Verify(string? mode)
    {
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

        bool cacheWasEnabled = ListenerCache.SnapshotEnabled, filterWasEnabled = HookFilter.Enabled;
        VerifyMode verifyMode = SnapshotVerifier.Mode;
        Sample vanilla, cacheOnly, optimized;
        try
        {
            // The bench compares against vanilla itself; live verification would skew timings.
            SnapshotVerifier.Mode = VerifyMode.Off;
            ListenerCache.SnapshotEnabled = false;
            HookFilter.Enabled = false;
            vanilla = Measure(state, tracker, iterations);
            ListenerCache.SnapshotEnabled = true;
            cacheOnly = Measure(state, tracker, iterations);
            HookFilter.Enabled = true;
            optimized = Measure(state, tracker, iterations);
        }
        finally
        {
            ListenerCache.SnapshotEnabled = cacheWasEnabled;
            HookFilter.Enabled = filterWasEnabled;
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

        bool identical = vanilla.Checksum == cacheOnly.Checksum && vanilla.Checksum == optimized.Checksum;
        double speedup = (vanilla.SnapshotMs + vanilla.HashMs) / Math.Max(0.001, optimized.SnapshotMs + optimized.HashMs);
        string msg = $"checksum x{iterations} — players: {state.Players.Count}, combat cards: {cards}, relics: {relics}\n"
            + vanilla.Format("vanilla     ") + "\n"
            + cacheOnly.Format("cache       ") + "\n"
            + optimized.Format("cache+filter") + "\n"
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
