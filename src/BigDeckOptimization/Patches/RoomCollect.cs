using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game ends each room's, act's and run's asset loading with <c>GC.Collect()</c>: a full, blocking collection on the main thread. Its
/// length follows the heap, so with a huge deck the screen freezes 0.3 to 1.6 s at each room entry, longer as the run goes on. The same
/// call is the worst frame of a combat start.
///
/// There, it asks for the same full collection in the background: what it frees (assets just unloaded) is freed as well, a moment later,
/// while the game goes on. And .NET is asked to keep its own full collections in the background too (<see cref="GCLatencyMode.SustainedLowLatency"/>:
/// blocking ones only when memory runs short), with 6 GB or more of memory. Memory only, no game code or state involved.
/// </summary>
public static class RoomCollect
{
    public const string Name = "RoomCollect";

    private const long MinMemory = 6L * 1024 * 1024 * 1024;

    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            SyncLatency();
        }
    }

    public static bool Applied { get; private set; }
    public static long Background { get; private set; }

    private static readonly MethodInfo _collect = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
    private static bool _bigMemory;

    public static void Apply(Harmony harmony)
    {
        var transpiler = new HarmonyMethod(typeof(RoomCollect), nameof(Transpiler));
        int patched = 0;
        foreach (string name in new[] { "LoadRoomAssets", "LoadActAssets", "LoadRunAssets" })
        {
            if (AccessTools.Method(typeof(PreloadManager), name) is { } method)
            {
                PatchGuard.Patch(harmony, AccessTools.AsyncMoveNext(method), transpiler: transpiler);
                patched++;
            }
            else
            {
                Log.Info($"[{ModEntry.Id}] {Name}: PreloadManager.{name} not found, it collects as the game does");
            }
        }
        if (patched == 0)
        {
            throw new PatchSkippedException("none of its methods exists any more");
        }
        _bigMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >= MinMemory;
        Applied = true;
        SyncLatency();
    }

    /// <summary>Only between Interactive (.NET's default here) and SustainedLowLatency: a no-GC region (<see cref="GcPacing"/>) restores what it found.</summary>
    internal static void SyncLatency()
    {
        if (!Applied || !_bigMemory)
        {
            return;
        }
        try
        {
            GCLatencyMode wanted = _enabled ? GCLatencyMode.SustainedLowLatency : GCLatencyMode.Interactive;
            if (GCSettings.LatencyMode is GCLatencyMode.Interactive or GCLatencyMode.SustainedLowLatency && GCSettings.LatencyMode != wanted)
            {
                GCSettings.LatencyMode = wanted;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: latency mode not changed: {e.Message}");
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_collect))
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RoomCollect), nameof(Collect)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static void Collect()
    {
        if (_enabled)
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
                Background++;
                return;
            }
            catch (Exception e)
            {
                _enabled = false;
                PatchSafety.Fault(Name);
                Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, rooms collect as the game does: {e}");
            }
        }
        GC.Collect();
    }
}
