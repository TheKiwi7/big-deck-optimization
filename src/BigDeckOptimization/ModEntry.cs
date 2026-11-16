using System;
using System.Collections.Generic;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BigDeckOptimization;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string Id = "BigDeckOptimization";

    public static Harmony Harmony { get; } = new("thekiwi7." + Id);

    /// <summary>What applies at the mod's start, in this order (an Apply may rely on the ones before it).</summary>
    private static readonly Queue<(string Name, Action<Harmony> Apply)> _toApply = new(new (string, Action<Harmony>)[]
    {
        // First, and even when the mod was turned off at the start: co-op players must all have the mod on, or all off.
        (CoopModeTag.Name, CoopModeTag.Apply),
        (ListenerCache.Name, ListenerCache.Apply),
        (HookFilter.Name, HookFilter.Apply),
        (ExtraScopes.Name, ExtraScopes.Apply),
        (SnapshotVerifier.Name, SnapshotVerifier.Apply),
        (IdleButtonInput.Name, IdleButtonInput.Apply),
        (GridSortCache.Name, GridSortCache.Apply),
        (EnchantmentTabReuse.Name, EnchantmentTabReuse.Apply),
        (PoolSignalScan.Name, PoolSignalScan.Apply),
        (CardDbScan.Name, CardDbScan.Apply),
        (PileIndex.Name, PileIndex.Apply),
        (PileCounterBatch.Name, PileCounterBatch.Apply),
        (FastCombatStart.Name, FastCombatStart.Apply),
        (ModImage.Name, ModImage.Apply),
        (HoverTipReuse.Name, HoverTipReuse.Apply),
        (FastListeners.Name, FastListeners.Apply),
        (HookSkip.Name, HookSkip.Apply),
        (LimitlessReserialize.Name, LimitlessReserialize.Apply),
        (LimitlessRecalcSkip.Name, LimitlessRecalcSkip.Apply),
        (SnapshotCardReuse.Name, SnapshotCardReuse.Apply),
        (LimitlessHookSkip.Name, LimitlessHookSkip.Apply),
        (LimitlessCloneMemo.Name, LimitlessCloneMemo.Apply),
        (TextPoolReuse.Name, TextPoolReuse.Apply),
        (ThemeColorSkip.Name, ThemeColorSkip.Apply),
        (ReplayStateSkip.Name, ReplayStateSkip.Apply),
        (GcPacing.Name, GcPacing.Apply),
        (RelicFlashMerge.Name, RelicFlashMerge.Apply),
        (CardFlightLimit.Name, CardFlightLimit.Apply),
        (TypePlaqueSkip.Name, TypePlaqueSkip.Apply),
        (ControllerActionSkip.Name, ControllerActionSkip.Apply),
        (RoomCollect.Name, RoomCollect.Apply),
        (PooledCardRelease.Name, PooledCardRelease.Apply),
        (SyncedCardRelease.Name, SyncedCardRelease.Apply),
        (SavedPropertyLookup.Name, SavedPropertyLookup.Apply),
        (RelicStateReuse.Name, RelicStateReuse.Apply),
        (RichTextEnvRelease.Name, RichTextEnvRelease.Apply),
        (DeckCountMemo.Name, DeckCountMemo.Apply),
        (CardSortKeys.Name, CardSortKeys.Apply),
    });

    /// <summary>Time given to patching per frame: each patched method takes ~1.5 ms, ~400 ms in all.</summary>
    private const double ApplyBudgetMs = 4;
    private const double ApplyFrameShare = 0.25;

    public static void Init()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // First of this mod's assembly-load handlers: the ones added later (BaseLibTypes', FastListeners') see its count.
        Patches.BaseLibTypes.StartCountingLoads();
#if BENCH
        // Before the game's own start goes further: the test runner sets the window and the sound up.
        TryApply(Bench.CombatStartTimer.Name, Bench.CombatStartTimer.Apply);
        TryApply(Bench.AutoTest.Name, Bench.AutoTest.Apply);
#endif
        try
        {
            Config.SettingsMenu.Init();
        }
        catch (Exception e)
        {
            Log.Warn($"[{Id}] {Config.SettingsMenu.Name}: settings menu unavailable (bdopt still works): {e}");
        }
        _initMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ApplyOverFrames();
    }

    /// <summary>Time <see cref="Init"/> took, logged once <see cref="StartedOff"/> is known (at the first patch).</summary>
    private static double _initMs;

    /// <summary>
    /// The patches, a few per frame while the game loads, so the mod adds no pause to the game's start. Nothing they speed up
    /// matters before a run starts (menu UI patches do run while the rest is still being applied), and a run starts from the main menu: as soon as that shows, whatever is left applies at
    /// once. All of them then come after every other mod's, whatever the load order. Falls back to applying them all now
    /// when frames can't be awaited.
    /// </summary>
    private static async void ApplyOverFrames()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        double spent = 0;
        int frames = 0;
        try
        {
            Godot.SceneTree? tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            if (tree == null)
            {
                // No frames to await: all at once, then the recompile and the check below as usual.
                spent += ApplySome(double.MaxValue);
            }
            long last = started;
            while (tree != null && _toApply.Count > 0)
            {
                await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
                frames++;
                // The game's frames while it loads last hundreds of ms: a share of that adds no visible pause, and leaves
                // less for the menu's first frame.
                double frameMs = System.Diagnostics.Stopwatch.GetElapsedTime(last).TotalMilliseconds;
                spent += ApplySome(CanRunStart() ? double.MaxValue : Math.Max(ApplyBudgetMs, frameMs * ApplyFrameShare));
                last = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
        catch (Exception e)
        {
            // async void: nothing may escape. Each patch catches its own errors, so this is about the frames.
            Log.Warn($"[{Id}] Applying over frames stopped, the rest applies now: {e}");
            spent += ApplySome(double.MaxValue);
        }
        try
        {
            if (!StartedOff)
            {
                frames += await RecompileOverFrames();
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{Id}] {nameof(InlineGuard)}: patched callers not compiled again: {e}");
        }
        try
        {
            HarmonyRebuild.CheckAll("applying the patches");
        }
        catch (Exception e)
        {
            Log.Warn($"[{Id}] {nameof(HarmonyRebuild)}: {e}");
        }
        Log.Info($"[{Id}] Patches applied: {spent:F0} ms of work over {frames} frames, {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms after the start");
#if BENCH
        Log.Info($"[{Id}] Load time: {MethodFingerprint.LoadBreakdown()}");
        Ready = true;
#endif
        // After everything above and outside its timing: the back-offs other mods cause, checked over the main menu's frames
        // (a few ms per frame), so the menu and bdopt show them before any combat.
        PatchSafety.PreflightOverFrames();
    }

    /// <summary>
    /// <see cref="InlineGuard.RecompileInliners"/> a few methods per frame, at once when a run is under way: what it fixes only
    /// matters to cards in a run. The frames it took.
    /// </summary>
    private static async System.Threading.Tasks.Task<int> RecompileOverFrames()
    {
        Godot.SceneTree? tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        int frames = 0;
        using IEnumerator<bool> steps = InlineGuard.RecompileInliners(new Harmony(Harmony.Id + "." + nameof(InlineGuard))).GetEnumerator();
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            double frameMs = System.Diagnostics.Stopwatch.GetElapsedTime(last).TotalMilliseconds;
            double budget = tree == null || MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress ? double.MaxValue : Math.Max(ApplyBudgetMs, frameMs * ApplyFrameShare);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < budget)
            {
                if (!steps.MoveNext())
                {
                    return frames;
                }
            }
            last = System.Diagnostics.Stopwatch.GetTimestamp();
            await tree!.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
            frames++;
        }
    }

    /// <summary>Applies patches until the budget is spent: the time it took.</summary>
    private static double ApplySome(double budgetMs)
    {
        DecideStartedOff();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed = 0;
        while (_toApply.Count > 0 && elapsed < budgetMs)
        {
            (string name, Action<Harmony> apply) = _toApply.Dequeue();
            TryApply(name, apply);
            elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return elapsed;
    }

    /// <summary>The main menu shows, or a run already exists (a mod starting one, a game that changed): no more waiting.</summary>
    private static bool CanRunStart()
    {
        try
        {
            return MegaCrit.Sts2.Core.Nodes.NGame.Instance?.MainMenu != null || MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            return true;
        }
    }

    /// <summary>Every patch of the start has been tried.</summary>
    internal static bool AllApplied => _toApply.Count == 0;

#if BENCH
    /// <summary>Patches and their callers' recompilation are done (the test runner waits for it).</summary>
    internal static bool Ready { get; private set; }
#endif

    /// <summary>
    /// True when the saved settings had the whole mod off when the game started: no patch is applied this session, so
    /// none of this mod's code runs inside the game's (turning it back on takes effect at the next start). Only when the
    /// settings menu registered in BaseLib: it loaded that saved choice and saves the one that turns the mod back on. A
    /// saved "off" the menu can't load or change (BaseLib failed, a BaseLib update the menu can't run with) is not
    /// honoured: the mod could never be turned back on but by editing the file. Decided at the first patch, once every mod
    /// has loaded (BaseLib may come after this mod); false until then.
    /// </summary>
    public static bool StartedOff { get; private set; }

    private static bool _decided;

    private static void DecideStartedOff()
    {
        if (_decided)
        {
            return;
        }
        _decided = true;
        try
        {
            // The menu set the master switch from the saved file when it registered: the saved choice, not a session-only switch
            // (the test tooling's --bdoff, bdperf opt), which leaves every patch applied as bdopt off does.
            StartedOff = Config.SettingsMenu.Registered && !Config.Settings.MasterChoice;
        }
        catch (Exception e)
        {
            StartedOff = false;
            Log.Warn($"[{Id}] Could not read the saved on/off setting (mod on): {e.Message}");
        }
        Log.Info($"[{Id}] Initialized v{typeof(ModEntry).Assembly.GetName().Version?.ToString(3)} in {_initMs:F0} ms"
            + (StartedOff ? ": turned off in the settings, nothing applied (turn it on, then restart the game)" : ""));
    }

    /// <summary>
    /// Applies one patch in isolation: if the game changed and the target is gone, only that patch is lost, and
    /// nothing of it stays installed. Each patch gets its own Harmony id (this mod's id + "." + its name), so what it
    /// installed is just what that id owns: no need to compare every patch of the mod before and after. Once per name: a
    /// second call for a name already applied (an optional mod's late load, a test verb) does nothing, since a second Apply
    /// under the same Harmony id would install its patches twice, and a failing one would remove the first one's too.
    /// </summary>
    internal static void TryApply(string name, Action<Harmony> apply)
    {
        if (_applied.Contains(name))
        {
            Log.Info($"[{Id}] Patch {name} already applied");
            return;
        }
        Type patchClass = apply.Method.DeclaringType!;
        bool testTooling = patchClass.Namespace == typeof(ModEntry).Namespace + ".Bench";
        // CoopModeTag applies even then: it only tags this mod's entry in the gameplay mod list ("off"), so a player who
        // started with the mod off can't join a co-op game whose players have it on (CoopModeTag's summary).
        if (StartedOff && !testTooling && patchClass != typeof(CoopModeTag))
        {
            _notApplied.Add(name);
            Log.Info($"[{Id}] Patch {name} not applied: the mod was turned off when the game started");
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Harmony harmony = new(Harmony.Id + "." + name);
        try
        {
            PatchSafety.RequireResolvable(patchClass);
            apply(harmony);
            PatchGuard.Register(name, patchClass, PatchGuard.Owned(harmony));
            _applied.Add(name);
            _notApplied.Remove(name); // Skipped earlier, applied now (an optional mod that loaded after this one).
            Log.Info($"[{Id}] Patch {name} applied ({System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
        }
        catch (PatchSkippedException e)
        {
            _notApplied.Add(name);
            RemoveFailed(name, harmony);
            Log.Info($"[{Id}] Patch {name} skipped: {e.Message}");
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            // A method, field or type it needs is gone or renamed: an update, not a bug of this mod.
            _notApplied.Add(name);
            RemoveFailed(name, harmony);
            Log.Info($"[{Id}] Patch {name} skipped: {e.Message} (missing: game or mod update?), left vanilla");
        }
        catch (Exception e)
        {
            _notApplied.Add(name);
            RemoveFailed(name, harmony);
            Log.Error($"[{Id}] Patch {name} could not be applied and is disabled: {e}");
        }
    }

    /// <summary>What a failed Apply installed comes off; what can't is faulted and retried (<see cref="PatchGuard.RemoveFailedApply"/>).</summary>
    private static void RemoveFailed(string name, Harmony harmony)
    {
        try
        {
            PatchGuard.RemoveFailedApply(name, harmony);
        }
        catch (Exception e)
        {
            PatchSafety.Fault(name);
            Log.Error($"[{Id}] Patch {name}: removing what it installed failed, turned off: {e}");
        }
    }

    private static readonly HashSet<string> _notApplied = new();

    /// <summary>The names <see cref="TryApply"/> applied (each once).</summary>
    private static readonly HashSet<string> _applied = new();

    /// <summary>
    /// Whether this patch was skipped or failed when the mod loaded. RelicStateReuse's scalar part is applied inside
    /// RelicStateReuse's own Apply: listed as not applied when that one applied without it.
    /// </summary>
    internal static bool NotApplied(string name) => _notApplied.Contains(name) || (name == RelicStateReuse.ScalarName && RelicStateReuse.ScalarNotApplied);
}
