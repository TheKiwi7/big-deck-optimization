using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Survey profiler for normal play: times a list of game methods suspected to scale with deck size (calls, total, max;
/// async ones also from call to completion), and logs every long frame with the probes that ran during it, so one play
/// session with a huge deck shows where hitches come from. Read-only: only records times. Shown by <c>bdperf probe</c>.
/// Nested probes are counted in each level (inclusive times).
/// </summary>
public static class PerfProbe
{
    public const string Name = "PerfProbe";

    private sealed class Stat
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
        public long Completed;
        public double WallTotalMs;
        public double WallMaxMs;
    }

    private static readonly Dictionary<MethodBase, string> _labels = new();
    private static readonly Dictionary<string, Stat> _stats = new();
    private static readonly Dictionary<string, double> _frameMs = new();
    private static readonly List<string> _hitches = new();
    private static readonly object _lock = new();
    private static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;
    private static long _lastFrameTicks;
    private static TimeSpan _lastGcPause;
    private static bool _subscribed;

    public static bool Enabled { get; set; } = true;

    /// <summary>Frames longer than this are logged with the probes that ran during them.</summary>
    public static double HitchMs { get; set; } = 100;

    public static void Apply(Harmony harmony)
    {
        // Sync: CPU time on the calling thread.
        Probe(harmony, AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)), "CardModel.Pile");
        Probe(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)), "CardPile.AddInternal");
        Probe(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)), "CardPile.RemoveInternal");
        Probe(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.Clear)), "CardPile.Clear");
        Probe(harmony, AccessTools.Method(typeof(NetCombatCardDb), "OnPileContentsChanged"), "NetCombatCardDb.OnPileContentsChanged");
        Probe(harmony, AccessTools.Method(typeof(NetCombatCardDb), nameof(NetCombatCardDb.StartCombat)), "NetCombatCardDb.StartCombat");
        Probe(harmony, AccessTools.Method(typeof(NCombatCardPile), "AddCard"), "NCombatCardPile.AddCard");
        Probe(harmony, AccessTools.Method(typeof(NCombatCardPile), "RemoveCard"), "NCombatCardPile.RemoveCard");
        Probe(harmony, AccessTools.Method(typeof(Player), nameof(Player.AfterCombatEnd)), "Player.AfterCombatEnd");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetUpCombat)), "CombatManager.SetUpCombat");
        Probe(harmony, AccessTools.Method(typeof(NCardGrid), nameof(NCardGrid.SetCards)), "NCardGrid.SetCards");
        Probe(harmony, AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable)), "NCard.FindOnTable");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState), "FromRun"), "NetFullCombatState.FromRun (co-op checksum)");

        // Async: first slice (until the first await) + wall time until the task completes (includes animations).
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.EndCombatInternal)), "CombatManager.EndCombatInternal");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.HandlePlayerDeath)), "CombatManager.HandlePlayerDeath");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.EndPlayerTurnPhaseOneInternal)), "CombatManager.EndPlayerTurnPhaseOne");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.EndPlayerTurnPhaseTwoInternal)), "CombatManager.EndPlayerTurnPhaseTwo");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), "FlushPlayerHand"), "CombatManager.FlushPlayerHand");
        Probe(harmony, AccessTools.Method(typeof(Hook), nameof(Hook.AfterCombatEnd)), "Hook.AfterCombatEnd");
        Probe(harmony, AccessTools.Method(typeof(Hook), nameof(Hook.AfterCombatVictory)), "Hook.AfterCombatVictory");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Shuffle)), "CardPileCmd.Shuffle");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Draw),
            new[] { typeof(PlayerChoiceContext), typeof(decimal), typeof(Player), typeof(bool) }), "CardPileCmd.Draw");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromCombat),
            new[] { typeof(IEnumerable<CardModel>), typeof(bool) }), "CardPileCmd.RemoveFromCombat");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
            new[] { typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) }), "CardPileCmd.Add(many)");
        Probe(harmony, AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"), "PlayCardAction.ExecuteAction");
        Probe(harmony, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.SaveRun)), "SaveManager.SaveRun");
        Probe(harmony, AccessTools.Method(typeof(RewardsSet), nameof(RewardsSet.GenerateWithoutOffering)), "RewardsSet.GenerateWithoutOffering");
        Subscribe();
    }

    private static void Probe(Harmony harmony, MethodBase? target, string label)
    {
        if (target == null)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: {label} not found, not probed");
            return;
        }
        _labels[target] = label;
        Type returnType = target is MethodInfo info ? info.ReturnType : typeof(void);
        Patches.PatchGuard.Patch(harmony, target,
            prefix: new HarmonyMethod(typeof(PerfProbe), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(PerfProbe), typeof(Task).IsAssignableFrom(returnType) ? nameof(AsyncPostfix) : nameof(SyncPostfix)));
    }

    private static void Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    /// <summary>Probes whose callers are sampled (one call in <see cref="CallerSampleEvery"/>), to find who calls them.</summary>
    private static readonly HashSet<string> _sampledCallers = new() { "CardModel.Pile" };
    private const int CallerSampleEvery = 500;
    private static readonly Dictionary<string, int> _callers = new();
    private static long _sampleCounter;

    private static void SampleCaller(string label)
    {
        if (++_sampleCounter % CallerSampleEvery != 0)
        {
            return;
        }
        // Skip the probe and Harmony's wrapper: the probed method then up to 4 game frames above it.
        StackTrace trace = new(2, false);
        string key = label + " <- " + string.Join(" <- ", trace.GetFrames().Take(5)
            .Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
        lock (_lock)
        {
            _callers[key] = _callers.GetValueOrDefault(key) + 1;
        }
    }

    private static void SyncPostfix(MethodBase __originalMethod, long __state)
    {
        if (__state != 0)
        {
            if (_labels.TryGetValue(__originalMethod, out string? sampled) && _sampledCallers.Contains(sampled))
            {
                SampleCaller(sampled);
            }
            Record(__originalMethod, (Stopwatch.GetTimestamp() - __state) * _msPerTick);
        }
    }

    private static void AsyncPostfix(MethodBase __originalMethod, object? __result, long __state)
    {
        if (__state == 0)
        {
            return;
        }
        Record(__originalMethod, (Stopwatch.GetTimestamp() - __state) * _msPerTick);
        if (__result is Task task)
        {
            task.ContinueWith(_ => RecordWall(__originalMethod, (Stopwatch.GetTimestamp() - __state) * _msPerTick),
                TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private static void Record(MethodBase method, double ms)
    {
        if (!_labels.TryGetValue(method, out string? label))
        {
            return;
        }
        lock (_lock)
        {
            Stat stat = GetStat(label);
            stat.Calls++;
            stat.TotalMs += ms;
            stat.MaxMs = Math.Max(stat.MaxMs, ms);
            _frameMs[label] = _frameMs.GetValueOrDefault(label) + ms;
        }
        if (!_subscribed)
        {
            Subscribe();
        }
    }

    private static void RecordWall(MethodBase method, double ms)
    {
        if (!_labels.TryGetValue(method, out string? label))
        {
            return;
        }
        lock (_lock)
        {
            Stat stat = GetStat(label);
            stat.Completed++;
            stat.WallTotalMs += ms;
            stat.WallMaxMs = Math.Max(stat.WallMaxMs, ms);
        }
    }

    private static Stat GetStat(string label)
    {
        if (!_stats.TryGetValue(label, out Stat? stat))
        {
            _stats[label] = stat = new Stat();
        }
        return stat;
    }

    private static void Subscribe()
    {
        if (_subscribed || Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }
        _subscribed = true;
        _lastFrameTicks = Stopwatch.GetTimestamp();
        _lastGcPause = GC.GetTotalPauseDuration();
        tree.ProcessFrame += OnFrame;
    }

    private static void OnFrame()
    {
        long now = Stopwatch.GetTimestamp();
        double frameMs = (now - _lastFrameTicks) * _msPerTick;
        _lastFrameTicks = now;
        TimeSpan gcPause = GC.GetTotalPauseDuration();
        double gcMs = (gcPause - _lastGcPause).TotalMilliseconds;
        _lastGcPause = gcPause;
        lock (_lock)
        {
            if (Enabled && frameMs >= HitchMs)
            {
                string top = string.Join(", ", _frameMs.OrderByDescending(p => p.Value).Take(5).Select(p => $"{p.Key} {p.Value:F0}"));
                string line = $"hitch {frameMs:F0} ms (GC {gcMs:F0} ms, {CardCounts()}): {(top.Length > 0 ? top : "no probe")}";
                _hitches.Add($"{DateTime.Now:HH:mm:ss} {line}");
                if (_hitches.Count > 200)
                {
                    _hitches.RemoveAt(0);
                }
                Log.Info($"[{ModEntry.Id}] {Name} {line}");
            }
            _frameMs.Clear();
        }
    }

    private static string CardCounts()
    {
        try
        {
            RunState? state = RunManager.Instance.DebugOnlyGetState();
            if (state == null)
            {
                return "no run";
            }
            int deck = state.Players.Sum(p => p.Deck.Cards.Count);
            int combat = state.Players.Sum(p => p.PlayerCombatState?.AllPiles.Sum(pile => pile.Cards.Count) ?? 0);
            return $"deck {deck}, combat {combat}";
        }
        catch (Exception)
        {
            return "counts n/a";
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _stats.Clear();
            _callers.Clear();
            _hitches.Clear();
            _frameMs.Clear();
        }
    }

    public static string Report(int hitches = 10)
    {
        StringBuilder sb = new();
        lock (_lock)
        {
            sb.Append($"probe {(Enabled ? "on" : "OFF")}, hitch threshold {HitchMs:F0} ms, {CardCounts()}\n");
            sb.Append("  total ms | calls | max ms | avg ms | [async: done, wall avg/max ms] | probe\n");
            foreach ((string label, Stat s) in _stats.OrderByDescending(p => p.Value.TotalMs))
            {
                string wall = s.Completed > 0 ? $" [{s.Completed}, {s.WallTotalMs / s.Completed:F0}/{s.WallMaxMs:F0}]" : "";
                sb.Append($"  {s.TotalMs,8:F0} | {s.Calls,7} | {s.MaxMs,6:F1} | {s.TotalMs / s.Calls,6:F3}{wall} | {label}\n");
            }
            foreach ((string key, int count) in _callers.OrderByDescending(p => p.Value).Take(12))
            {
                sb.Append($"  caller ~x{count * CallerSampleEvery}: {key}\n");
            }
            sb.Append($"  last {Math.Min(hitches, _hitches.Count)} of {_hitches.Count} hitches:\n");
            foreach (string hitch in _hitches.Skip(Math.Max(0, _hitches.Count - hitches)))
            {
                sb.Append("    ").Append(hitch).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Writes the full report to the game log, one line per row, for later analysis.</summary>
    public static void LogReport()
    {
        foreach (string line in Report(200).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Log.Info($"[{ModEntry.Id}] {Name} | {line}");
        }
    }
}
