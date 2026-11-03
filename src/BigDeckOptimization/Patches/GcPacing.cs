using System;
using System.Runtime;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// With huge decks every ephemeral collection pauses the game for 50-100 ms, and the game's budget (~47 MB here) runs out
/// every few actions: a visible hitch in the middle of an animation, whatever the optimizations elsewhere. In a combat with
/// many cards, memory for <see cref="Budget"/> MB is set aside at a moment when nothing is animating (.NET's no-GC
/// region: no collection, it takes ~1 ms), so actions don't collect at all; when more than <see cref="Refill"/> of it was
/// used, more is set aside at the next such moment. Garbage then piles up: after <see cref="CollectEvery"/> MB allocated, a
/// quick collection of the recent objects runs at such a moment instead, when no card or hit is on its way; older ones
/// wait for the end of the combat, collected in the background. If an action ever uses the whole budget, .NET collects
/// as it would have anyway. No game code or state involved, memory use only (at most about <see cref="CollectEvery"/> MB
/// more, during combats).
/// </summary>
public static class GcPacing
{
    public const string Name = "GcPacing";

    public static int MinCards { get; set; } = 1000;
    private const long Budget = 240L * 1024 * 1024;
    private const long LohBudget = 64L * 1024 * 1024;
    private const double Refill = 0.35;
    private const long CollectEvery = 800L * 1024 * 1024;
    /// <summary>Older objects and big arrays only go in a full collection: past this growth in one combat, one runs at a quiet moment.</summary>
    private const long FullAfterGrowth = 1200L * 1024 * 1024;
    /// <summary>Below this much memory the game needs what this sets aside more than smooth actions.</summary>
    private const long MinMemory = 6L * 1024 * 1024 * 1024;
    private const int IdleFramesBeforeCollecting = 3;
    private const long RetryAfterMs = 5000;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Regions { get; private set; }
    public static long RegionsRefused { get; private set; }
    public static long Exhausted { get; private set; }
    public static double RegionMs { get; private set; }
    public static long Collections { get; private set; }
    public static long Fulls { get; private set; }
    public static double CollectMs { get; private set; }
    public static double WorstCollectMs { get; private set; }

    private static bool _inRegion;
    private static long _allocatedAtStart;
    private static long _allocatedAtCollect;
    private static long _heapAfterFull;
    private static long _retryAt;
    private static int _idleFrames;
    private static int _checkCards;
    private static bool _bigCombat;

    public static void Apply(HarmonyLib.Harmony harmony)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            throw new PatchSkippedException("no scene tree");
        }
        if (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < MinMemory)
        {
            throw new PatchSkippedException("less than 6 GB of memory");
        }
        tree.ProcessFrame += Tick;
        Applied = true;
    }

    private static void Tick()
    {
        try
        {
            if (!Enabled || !CombatManager.Instance.IsInProgress || !BigCombat())
            {
                bool wasBig = _bigCombat;
                _bigCombat = false;
                _checkCards = 0;
                End();
                if (wasBig)
                {
                    // What piled up and older objects: in the background, without stopping the game for a full collection.
                    GC.Collect(2, GCCollectionMode.Forced, blocking: false);
                    _heapAfterFull = 0;
                    _allocatedAtCollect = GC.GetTotalAllocatedBytes(false);
                }
                return;
            }
            if (_inRegion && GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                // The budget ran out in the middle of something: .NET collected and left the region.
                _inRegion = false;
                Exhausted++;
            }
            _idleFrames = IsQuiet() ? _idleFrames + 1 : 0;
            if (_idleFrames < IdleFramesBeforeCollecting)
            {
                return;
            }
            long allocated = GC.GetTotalAllocatedBytes(false);
            if ((!_inRegion && System.Environment.TickCount64 >= _retryAt) || (_inRegion && allocated - _allocatedAtStart > Budget * Refill))
            {
                _idleFrames = 0;
                End();
                if (allocated - _allocatedAtCollect > CollectEvery)
                {
                    Collect();
                    _allocatedAtCollect = GC.GetTotalAllocatedBytes(false);
                }
                Start();
            }
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
        }
    }

    /// <summary>Recounted about once a second: combat piles barely change size.</summary>
    private static bool BigCombat()
    {
        if (--_checkCards > 0)
        {
            return _bigCombat;
        }
        _checkCards = 240;
        int cards = 0;
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state != null)
        {
            foreach (Player player in state.Players)
            {
                if (player.PlayerCombatState is { } combat)
                {
                    foreach (var pile in combat.AllPiles)
                    {
                        cards += pile.Cards.Count;
                    }
                }
            }
        }
        _bigCombat = cards >= MinCards;
        return _bigCombat;
    }

    /// <summary>The player's turn with nothing executing: nothing moves but idle animations.</summary>
    private static bool IsQuiet()
    {
        CombatManager combat = CombatManager.Instance;
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        return state != null && LocalContext.GetMe(state)?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
            && !combat.PlayerActionsDisabled && !combat.IsEnemyTurnStarted && !combat.EndingPlayerTurnPhaseOne && !combat.EndingPlayerTurnPhaseTwo
            && !combat.IsStarting && !RunManager.Instance.ActionExecutor.IsRunning;
    }

    private static void Collect()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Collections++;
        long heap = GC.GetTotalMemory(false);
        if (_heapAfterFull == 0)
        {
            _heapAfterFull = heap;
        }
        if (heap - _heapAfterFull > FullAfterGrowth)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            _heapAfterFull = GC.GetTotalMemory(false);
            Fulls++;
        }
        else
        {
            GC.Collect(1, GCCollectionMode.Forced, blocking: true);
        }
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        CollectMs += ms;
        WorstCollectMs = Math.Max(WorstCollectMs, ms);
    }

    private static void Start()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _inRegion = GC.TryStartNoGCRegion(Budget, LohBudget, disallowFullBlockingGC: true);
        if (!_inRegion)
        {
            // Not enough room right now, and each try can itself collect: not at every quiet moment.
            _retryAt = System.Environment.TickCount64 + RetryAfterMs;
            RegionsRefused++;
        }
        RegionMs += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _allocatedAtStart = GC.GetTotalAllocatedBytes(false);
        Regions++;
    }

    private static void End()
    {
        if (_inRegion)
        {
            _inRegion = false;
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                GC.EndNoGCRegion();
            }
        }
    }
}
