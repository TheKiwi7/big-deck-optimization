using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Test tooling (startup-measure branch): where this mod's startup time goes, frame by frame. Only with the launch argument
/// <c>--bdstartprof</c>; otherwise it installs nothing. Times a fixed list of this mod's own methods (prefix/postfix, outermost
/// call per method and thread: nested calls of the same method are counted once), records every process frame from the mod's
/// initializer on, and logs once, a fixed time after the main menu shows: the frames (duration, this mod's measured time in
/// each by entry point), the totals per method (before and after the menu, main thread or not) and the startup preflight's
/// steps per patch. Read-only: records times, changes nothing the game or the mod computes. Its own patches add a few ms at
/// the start (logged as its TryApply time) and a fraction of a microsecond per timed call.
/// </summary>
public static class StartupProfile
{
    public const string Name = "StartupProfile";

    internal const string Arg = "--bdstartprof";

    /// <summary>Time after the main menu first shows at which the profile is logged.</summary>
    private const double DumpAfterMenuMs = 12000;

    private static long _t0;
    private static int _mainThread;
    private static readonly List<(long Stamp, long GcPauseTicks, int Gen2)> _frames = new();
    private static long _menuStamp;
    private static bool _menuSeen;
    private static ulong _menuTicks;
    private static ulong _initTicks;
    private static bool _dumped;

    private sealed class Stat
    {
        public int Calls;
        public long Total, Max, Pre, Post, Background;
        public long First = long.MaxValue, Last = long.MinValue;
    }

    public struct Call
    {
        public long Start;
        public bool Outermost;
        public bool Root;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Stat> _stats = new();
    private static readonly Dictionary<MethodBase, string> _keys = new();
    private static readonly HashSet<MethodBase> _roots = new();
    private static readonly Dictionary<string, Stat> _preflight = new();
    private static readonly Dictionary<string, Stat> _applies = new();
    /// <summary>Main-thread calls of entry points not inside another one: start, ticks, key.</summary>
    private static readonly List<(long Start, long Ticks, string Key)> _rootCalls = new();

    [ThreadStatic] private static Dictionary<MethodBase, int>? _depth;
    [ThreadStatic] private static int _rootDepth;

    private static FieldInfo? _checksField;
    private static FieldInfo? _currentField;

    /// <summary>The timed methods: (type, method name, entry point); every overload; an iterator's MoveNext for "&lt;Name&gt;".</summary>
    private static readonly (Type Type, string Method, bool Root)[] Targets =
    {
        (typeof(ModEntry), "ApplySome", true),
        (typeof(ModEntry), "TryApply", true),
        (typeof(ModEntry), "DecideStartedOff", false),
        (typeof(Config.SettingsMenu), "Register", true),
        (typeof(Patches.OwnIntegrity), "WhyRewritten", false),
        (typeof(Patches.InlineGuard), "<RecompileInliners>", true),
        (typeof(Patches.InlineGuard), "Flush", true),
        (typeof(Patches.InlineGuard), "AfterForeignFirstPatch", false),
        (typeof(Patches.HarmonyRebuild), "CheckAll", true),
        (typeof(Patches.HookSkip), "PatchBatch", true),
        (typeof(Patches.HookSkip), "Finished", true),
        (typeof(Patches.PatchSafety), "<PreflightSteps>", true),
        (typeof(Patches.PatchSafety), "Preflight", true),
        (typeof(Patches.PatchSafety), "FinishStartup", true),
        (typeof(Patches.PatchSafety), "RequireResolvable", false),
        (typeof(Patches.MethodFingerprint), "Of", false),
        (typeof(Patches.MethodFingerprint), "Describe", false),
        (typeof(Patches.MethodFingerprint), "GameClosure", false),
        (typeof(Patches.MethodFingerprint), "GameCallees", false),
        (typeof(Patches.MethodFingerprint), "SetFingerprint", false),
        (typeof(Patches.MethodFingerprint), "FieldUsers", false),
        (typeof(Patches.MethodFingerprint), "Implementations", false),
        (typeof(Patches.CodeSet), "Compute", false),
        (typeof(Patches.CodeSet), "ForeignOverridersOf", false),
        (typeof(Patches.LoadedCode), "Types", false),
        (typeof(Patches.TranspilerJudge), "Capture", false),
        (typeof(Patches.TranspilerJudge), "RebuildEnded", false),
        (typeof(Patches.TranspilerJudge), "Judge", false),
        (typeof(Patches.UnchangedTranspilers), "Register", false),
        (typeof(Patches.UnchangedTranspilers), "Decide", false),
        (typeof(Patches.UnchangedTranspilers), "InstallIfNeeded", true),
        (typeof(Patches.UnchangedTranspilers), "AfterForeignRebuild", false),
        (typeof(Patches.HarmonyWatch), "ScanAtLoad", false),
        (typeof(Patches.HarmonyWatch), "Changed", false),
        (typeof(Patches.HarmonyWatch), "CheckPending", true),
        (typeof(Patches.HarmonyWatch), "Revalidate", false),
        (typeof(Config.Settings), "NotifyStatesChanged", true),
    };

    public static void Apply(Harmony harmony)
    {
        if (!OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).Contains(Arg))
        {
            return;
        }
        _t0 = Stopwatch.GetTimestamp();
        _initTicks = Time.GetTicksMsec();
        _mainThread = System.Environment.CurrentManagedThreadId;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += OnFrame;
        }
        var prefix = new HarmonyMethod(typeof(StartupProfile), nameof(Prefix)) { priority = Priority.First };
        var postfix = new HarmonyMethod(typeof(StartupProfile), nameof(Postfix)) { priority = Priority.Last };
        var stepPostfix = new HarmonyMethod(typeof(StartupProfile), nameof(StepPostfix)) { priority = Priority.Last };
        int patched = 0;
        var missing = new List<string>();
        foreach ((Type type, string name, bool root) in Targets)
        {
            var methods = new List<MethodBase>();
            if (name.StartsWith('<'))
            {
                Type? machine = type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(t => t.Name.StartsWith(name, StringComparison.Ordinal));
                if (machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } moveNext)
                {
                    methods.Add(moveNext);
                }
            }
            else
            {
                methods.AddRange(type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name && !m.IsGenericMethodDefinition && !m.IsAbstract && m.GetMethodBody() != null));
            }
            if (methods.Count == 0)
            {
                missing.Add($"{type.Name}.{name}");
                continue;
            }
            foreach (MethodBase method in methods)
            {
                // Harmony can't rebuild a body with an exception filter (catch ... when): left untimed.
                if (method.GetMethodBody()!.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Filter))
                {
                    missing.Add($"{type.Name}.{name} untimed (exception filter)");
                    continue;
                }
                string key = $"{type.Name}.{name.Trim('<', '>')}" + (methods.Count > 1 ? $"({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})" : "");
                _keys[method] = key;
                if (root)
                {
                    _roots.Add(method);
                }
                HarmonyMethod after = name == "<PreflightSteps>" ? stepPostfix : postfix;
                try
                {
                    harmony.Patch(method, prefix: prefix, finalizer: after);
                }
                catch (Exception e)
                {
                    // A finalizer can't wrap every body (Harmony's exception block): a postfix then (a call that throws isn't counted).
                    try
                    {
                        harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                        harmony.Patch(method, prefix: prefix, postfix: after);
                        missing.Add($"{key} as a postfix ({e.GetType().Name})");
                    }
                    catch (Exception again)
                    {
                        missing.Add($"{key} untimed ({again.GetType().Name})");
                        continue;
                    }
                }
                patched++;
            }
        }
        Log.Info($"[{ModEntry.Id}] STARTPROF init: Godot ticks {_initTicks} ms, {patched} methods timed"
            + (missing.Count > 0 ? $"; notes: {string.Join(", ", missing)}" : ""));
    }

    private static void OnFrame()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _frames.Add((now, GC.GetTotalPauseDuration().Ticks, GC.CollectionCount(2)));
        }
        if (!_menuSeen)
        {
            try
            {
                if (NGame.Instance?.MainMenu != null)
                {
                    _menuSeen = true;
                    _menuStamp = now;
                    _menuTicks = Time.GetTicksMsec();
                }
            }
            catch
            {
            }
        }
        else if (!_dumped && Stopwatch.GetElapsedTime(_menuStamp, now).TotalMilliseconds >= DumpAfterMenuMs)
        {
            _dumped = true;
            try
            {
                Dump();
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] STARTPROF dump failed: {e}");
            }
        }
    }

    public static void Prefix(MethodBase __originalMethod, out Call __state)
    {
        _depth ??= new Dictionary<MethodBase, int>();
        _depth.TryGetValue(__originalMethod, out int depth);
        _depth[__originalMethod] = depth + 1;
        bool root = _roots.Contains(__originalMethod) && System.Environment.CurrentManagedThreadId == _mainThread;
        __state = new Call { Start = Stopwatch.GetTimestamp(), Outermost = depth == 0, Root = root && _rootDepth == 0 };
        if (root)
        {
            _rootDepth++;
        }
    }

    public static void Postfix(MethodBase __originalMethod, object[] __args, Call __state)
    {
        string key = Leave(__originalMethod, __state, out long elapsed);
        if (key.Length == 0)
        {
            return;
        }
        if (key == "ModEntry.TryApply" && __args.Length > 0 && __args[0] is string name)
        {
            Add(_applies, name, __state.Start, elapsed);
        }
    }

    public static void StepPostfix(MethodBase __originalMethod, object __instance, Call __state)
    {
        string key = Leave(__originalMethod, __state, out long elapsed);
        if (key.Length == 0)
        {
            return;
        }
        string patch = "?";
        try
        {
            _checksField ??= __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).First(f => f.Name == "checks");
            object? checks = _checksField.GetValue(__instance);
            if (checks != null)
            {
                _currentField ??= checks.GetType().GetField("Current");
                patch = _currentField?.GetValue(checks) as string ?? "?";
            }
        }
        catch
        {
        }
        Add(_preflight, patch, __state.Start, elapsed);
    }

    /// <summary>Records the call: its key, or "" for a nested call of the same method (not counted).</summary>
    private static string Leave(MethodBase method, Call state, out long elapsed)
    {
        long now = Stopwatch.GetTimestamp();
        elapsed = now - state.Start;
        if (_depth != null && _depth.TryGetValue(method, out int depth))
        {
            _depth[method] = depth - 1;
        }
        if (_roots.Contains(method) && System.Environment.CurrentManagedThreadId == _mainThread)
        {
            _rootDepth--;
        }
        if (!state.Outermost)
        {
            return "";
        }
        string key = _keys.TryGetValue(method, out string? k) ? k : method.Name;
        Add(_stats, key, state.Start, elapsed);
        if (state.Root)
        {
            lock (_lock)
            {
                _rootCalls.Add((state.Start, elapsed, key));
            }
        }
        return key;
    }

    private static void Add(Dictionary<string, Stat> table, string key, long start, long elapsed)
    {
        bool main = System.Environment.CurrentManagedThreadId == _mainThread;
        lock (_lock)
        {
            if (!table.TryGetValue(key, out Stat? stat))
            {
                table[key] = stat = new Stat();
            }
            stat.Calls++;
            stat.Total += elapsed;
            stat.Max = Math.Max(stat.Max, elapsed);
            if (!main)
            {
                stat.Background += elapsed;
            }
            else if (!_menuSeen)
            {
                stat.Pre += elapsed;
            }
            else
            {
                stat.Post += elapsed;
            }
            stat.First = Math.Min(stat.First, start);
            stat.Last = Math.Max(stat.Last, start + elapsed);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A time relative to the menu's first frame, in ms (negative: before it).</summary>
    private static string Rel(long stamp) => stamp == long.MaxValue || stamp == long.MinValue ? "-" : $"{Ms(stamp - _menuStamp):+0;-0}";

    private static void Dump()
    {
        string P = $"[{ModEntry.Id}] STARTPROF";
        lock (_lock)
        {
            Log.Info($"{P} menu: Godot ticks {_menuTicks} ms, {Ms(_menuStamp - _t0):F0} ms after this mod's init (Godot ticks {_initTicks} ms); times below in ms from the menu's first frame");
            // Frame i: from process_frame i to process_frame i+1; the entry points' main-thread work that started in it.
            var calls = _rootCalls.OrderBy(c => c.Start).ToList();
            int c0 = 0;
            var frames = new List<(int Index, long Start, double Ms, double Ours, double Gc, int Gen2, List<(long Start, long Ticks, string Key)> Work)>();
            for (int i = 0; i + 1 < _frames.Count; i++)
            {
                long a = _frames[i].Stamp, b = _frames[i + 1].Stamp;
                var work = new List<(long, long, string)>();
                while (c0 < calls.Count && calls[c0].Start < a)
                {
                    c0++;
                }
                for (int c = c0; c < calls.Count && calls[c].Start < b; c++)
                {
                    work.Add(calls[c]);
                }
                frames.Add((i, a, Ms(b - a), Ms(work.Sum(w => w.Item2)), TimeSpan.FromTicks(_frames[i + 1].GcPauseTicks - _frames[i].GcPauseTicks).TotalMilliseconds, _frames[i + 1].Gen2 - _frames[i].Gen2, work));
            }
            double before = Ms(calls.Where(c => c.Start < (_frames.Count > 0 ? _frames[0].Stamp : long.MaxValue)).Sum(c => c.Ticks));
            Log.Info($"{P} entry points before the first frame: {before:F1} ms");
            string Summary(IList<(int Index, long Start, double Ms, double Ours, double Gc, int Gen2, List<(long Start, long Ticks, string Key)> Work)> list) => list.Count == 0 ? "none"
                : $"{list.Count} frames, {list.Sum(d => d.Ms):F0} ms, longest {list.Max(d => d.Ms):F1} ms, over 16.7 ms {list.Count(d => d.Ms > 16.7)}, over 33 ms {list.Count(d => d.Ms > 33.3)}, "
                  + $"over 50 ms {list.Count(d => d.Ms > 50)}, over 100 ms {list.Count(d => d.Ms > 100)}; this mod's timed work {list.Sum(d => d.Ours):F1} ms in {list.Count(d => d.Ours > 0.05)} frames, "
                  + $"longest {list.Max(d => d.Ours):F1} ms; GC pauses {list.Sum(d => d.Gc):F1} ms, gen2 {list.Sum(d => d.Gen2)}";
            Log.Info($"{P} frames before the menu: {Summary(frames.Where(f => f.Start < _menuStamp).ToList())}");
            foreach (int seconds in new[] { 1, 2, 5, 10 })
            {
                var window = frames.Where(f => f.Start >= _menuStamp && Ms(f.Start - _menuStamp) < seconds * 1000).ToList();
                Log.Info($"{P} frames in the menu's first {seconds} s: {Summary(window)}");
            }
            foreach (var f in frames.Where(f => f.Start < _menuStamp).OrderByDescending(f => f.Ms).Take(12).OrderBy(f => f.Index))
            {
                string top = string.Join("; ", f.Work.GroupBy(w => w.Key).Select(g => (g.Key, Ms: Ms(g.Sum(w => w.Ticks)), N: g.Count()))
                    .OrderByDescending(g => g.Ms).Take(4).Select(g => $"{g.Key} {g.Ms:F1}" + (g.N > 1 ? $" x{g.N}" : "")));
                Log.Info($"{P} loading frame {f.Index} at {Rel(f.Start)} (Godot ticks ~{_initTicks + (ulong)Math.Max(0, Ms(f.Start - _t0)):F0}): {f.Ms:F1} ms, ours {f.Ours:F1} ms{(f.Gc > 0 ? $", GC {f.Gc:F1} ms" : "")}{(top.Length > 0 ? ": " + top : "")}");
            }
            foreach (var f in frames.Where(f => Ms(f.Start - _menuStamp) >= -3000 && (f.Ours > 0.05 || f.Ms > 12 || f.Gc > 0)))
            {
                string top = string.Join("; ", f.Work.GroupBy(w => w.Key).Select(g => (g.Key, Ms: Ms(g.Sum(w => w.Ticks)), N: g.Count()))
                    .OrderByDescending(g => g.Ms).Take(6).Select(g => $"{g.Key} {g.Ms:F1}" + (g.N > 1 ? $" x{g.N}" : "")));
                Log.Info($"{P} frame {f.Index} at {Rel(f.Start)}: {f.Ms:F1} ms, ours {f.Ours:F1} ms{(f.Gc > 0 ? $", GC {f.Gc:F1} ms" : "")}{(f.Gen2 > 0 ? $", gen2 {f.Gen2}" : "")}{(top.Length > 0 ? ": " + top : "")}");
            }
            foreach ((string key, Stat s) in _stats.OrderByDescending(e => e.Value.Total))
            {
                Log.Info($"{P} method {key}: {s.Calls} calls, {Ms(s.Total):F1} ms (before the menu {Ms(s.Pre):F1}, after {Ms(s.Post):F1}, other threads {Ms(s.Background):F1}), longest {Ms(s.Max):F1} ms, {Rel(s.First)}..{Rel(s.Last)}");
            }
            foreach ((string key, Stat s) in _preflight.OrderByDescending(e => e.Value.Total))
            {
                Log.Info($"{P} preflight {key}: {s.Calls} steps, {Ms(s.Total):F1} ms (before the menu {Ms(s.Pre):F1}, after {Ms(s.Post):F1}), longest {Ms(s.Max):F1} ms, {Rel(s.First)}..{Rel(s.Last)}");
            }
            foreach ((string key, Stat s) in _applies.OrderBy(e => e.Value.First))
            {
                Log.Info($"{P} apply {key}: {Ms(s.Total):F1} ms at {Rel(s.First)}");
            }
            Log.Info($"{P} done");
        }
    }
}
