using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Wall-clock breakdown of entering a combat: phase timestamps, GC pauses and the longest frames, to find what causes
/// the black screen at combat start (sampling profiles miss GC and native stalls). Read-only: only records times.
/// Shown by <c>bdperf combatstart</c> and logged at the end of each combat start.
/// </summary>
public static class CombatStartTimer
{
    public const string Name = "CombatStartTimer";

    private static readonly Stopwatch _clock = new();
    private static readonly List<(double Ms, string Label)> _marks = new();
    private static readonly List<(double Ms, double FrameMs, string Phase)> _frames = new();
    private static string _phase = "";
    private static double _lastFrameMs;
    private static TimeSpan _gcPauseStart;
    private static int _gen2Start;
    private static bool _running;
    private static bool _firstTurnSeen;
    private static SceneTree? _tree;

    public static string LastReport { get; private set; } = "No combat start recorded yet.";

    public static void Apply(Harmony harmony)
    {
        Patch(harmony, AccessTools.Method(typeof(CombatRoom), nameof(CombatRoom.EnterInternal)), nameof(StartPrefix), null);
        PatchAsync(harmony, AccessTools.Method(typeof(PreloadManager), nameof(PreloadManager.LoadRoomCombatAssets)), "assets");
        PatchSync(harmony, AccessTools.Method(typeof(NRun), nameof(NRun.SetCurrentRoom)), "room scene");
        PatchSync(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetUpCombat)), "SetUpCombat");
        PatchAsync(harmony, AccessTools.Method(typeof(Hook), nameof(Hook.AfterRoomEntered)), "AfterRoomEntered hooks");
        PatchAsync(harmony, AccessTools.Method(typeof(Hook), nameof(Hook.BeforeCombatStart)), "BeforeCombatStart hooks");
        PatchAsync(harmony, AccessTools.Method(typeof(CombatManager), "StartTurn"), "first turn", end: true);
    }

    private static void Patch(Harmony harmony, System.Reflection.MethodBase? target, string? prefix, string? postfix)
    {
        if (target == null)
        {
            throw new MissingMethodException(Name + " target not found");
        }
        harmony.Patch(target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(CombatStartTimer), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(CombatStartTimer), postfix));
    }

    private static readonly Dictionary<System.Reflection.MethodBase, (string Label, bool End)> _labels = new();

    private static void PatchSync(Harmony harmony, System.Reflection.MethodBase? target, string label)
    {
        _labels[target ?? throw new MissingMethodException(Name + " target not found: " + label)] = (label, false);
        Patch(harmony, target, nameof(SyncPrefix), nameof(SyncPostfix));
    }

    private static void PatchAsync(Harmony harmony, System.Reflection.MethodBase? target, string label, bool end = false)
    {
        _labels[target ?? throw new MissingMethodException(Name + " target not found: " + label)] = (label, end);
        Patch(harmony, target, nameof(SyncPrefix), nameof(AsyncPostfix));
    }

    private static void StartPrefix()
    {
        try
        {
            _clock.Restart();
            _marks.Clear();
            _frames.Clear();
            _lastFrameMs = 0;
            _gcPauseStart = GC.GetTotalPauseDuration();
            _gen2Start = GC.CollectionCount(2);
            _firstTurnSeen = false;
            _phase = "enter";
            if (!_running && Engine.GetMainLoop() is SceneTree tree)
            {
                _tree = tree;
                _tree.ProcessFrame += OnFrame;
            }
            _running = true;
            Mark("enter combat room");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: {e.Message}");
        }
    }

    private static void SyncPrefix(System.Reflection.MethodBase __originalMethod)
    {
        if (_running && _labels.TryGetValue(__originalMethod, out var info))
        {
            _phase = info.Label;
            Mark(info.Label + " start");
        }
    }

    private static void SyncPostfix(System.Reflection.MethodBase __originalMethod)
    {
        if (_running && _labels.TryGetValue(__originalMethod, out var info))
        {
            Mark(info.Label + " end");
        }
    }

    private static void AsyncPostfix(System.Reflection.MethodBase __originalMethod, Task __result)
    {
        if (!_running || !_labels.TryGetValue(__originalMethod, out var info))
        {
            return;
        }
        if (info.End)
        {
            if (_firstTurnSeen)
            {
                return;
            }
            _firstTurnSeen = true;
        }
        __result.ContinueWith(_ =>
        {
            Mark(info.Label + " done");
            if (info.End)
            {
                Finish();
            }
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private static void Mark(string label)
    {
        _marks.Add((_clock.Elapsed.TotalMilliseconds, label));
    }

    private static void OnFrame()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _frames.Add((now, now - _lastFrameMs, _phase));
        _lastFrameMs = now;
    }

    private static void Finish()
    {
        _running = false;
        Callable.From(() =>
        {
            if (_tree != null)
            {
                _tree.ProcessFrame -= OnFrame;
            }
        }).CallDeferred();

        StringBuilder sb = new();
        double total = _clock.Elapsed.TotalMilliseconds;
        TimeSpan gcPause = GC.GetTotalPauseDuration() - _gcPauseStart;
        sb.Append($"combat start: {total:F0} ms total, GC pauses {gcPause.TotalMilliseconds:F0} ms ({GC.CollectionCount(2) - _gen2Start} gen2)\n");
        foreach ((double ms, string label) in _marks)
        {
            sb.Append($"  {ms,7:F0} ms  {label}\n");
        }
        sb.Append("  longest frames:");
        foreach (var f in _frames.OrderByDescending(f => f.FrameMs).Take(5))
        {
            sb.Append($" {f.FrameMs:F0} ms @{f.Ms:F0} ({f.Phase});");
        }
        LastReport = sb.ToString();
        Log.Info($"[{ModEntry.Id}] " + LastReport.Replace("\n", " |"));
    }
}
