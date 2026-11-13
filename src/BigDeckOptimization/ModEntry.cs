using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        ApplyOverFrames();
        Log.Info($"[{Id}] Initialized v{typeof(ModEntry).Assembly.GetName().Version?.ToString(3)} in {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms"
            + (StartedOff ? ": turned off in the settings, nothing applied (turn it on, then restart the game)" : ""));
    }

    /// <summary>
    /// The patches, a few per frame while the game loads, so the mod adds no pause to the game's start. Nothing they touch
    /// runs before a run starts, and a run starts from the main menu: as soon as that shows, whatever is left applies at
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
            if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
            {
                ApplySome(double.MaxValue);
                return;
            }
            long last = started;
            while (_toApply.Count > 0)
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
    /// none of this mod's code runs inside the game's (turning it back on takes effect at the next start). Only with
    /// BaseLib, which saves the settings and can turn the mod back on.
    /// </summary>
    public static bool StartedOff { get; } = SavedOff();

    private static bool SavedOff()
    {
        try
        {
            if (!ModManager.Mods.Any(m => m.manifest?.id == Config.SettingsMenu.BaseLibId && m.state is ModLoadState.None or ModLoadState.Loaded))
            {
                return false;
            }
            string path = Path.Combine(Godot.OS.GetUserDataDir(), "mod_configs", Id + ".cfg");
            if (!File.Exists(path))
            {
                return false;
            }
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            return saved.RootElement.TryGetProperty("Enabled", out JsonElement enabled)
                && string.Equals(enabled.ToString(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Log.Warn($"[{Id}] Could not read the saved on/off setting (mod on): {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Applies one patch in isolation: if the game changed and the target is gone, only that patch is lost, and
    /// nothing of it stays installed. Each patch gets its own Harmony id (this mod's id + "." + its name), so what it
    /// installed is just what that id owns: no need to compare every patch of the mod before and after.
    /// </summary>
    internal static void TryApply(string name, Action<Harmony> apply)
    {
        Type patchClass = apply.Method.DeclaringType!;
        bool testTooling = patchClass.Namespace == typeof(ModEntry).Namespace + ".Bench";
        if (StartedOff && !testTooling)
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
            _notApplied.Remove(name); // Skipped earlier, applied now (an optional mod that loaded after this one).
            Log.Info($"[{Id}] Patch {name} applied ({System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
        }
        catch (PatchSkippedException e)
        {
            _notApplied.Add(name);
            PatchGuard.Remove(PatchGuard.Owned(harmony));
            Log.Info($"[{Id}] Patch {name} skipped: {e.Message}");
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException)
        {
            // A method, field or type it needs is gone or renamed: an update, not a bug of this mod.
            _notApplied.Add(name);
            PatchGuard.Remove(PatchGuard.Owned(harmony));
            Log.Info($"[{Id}] Patch {name} skipped: {e.Message} (missing: game or mod update?), left vanilla");
        }
        catch (Exception e)
        {
            _notApplied.Add(name);
            PatchGuard.Remove(PatchGuard.Owned(harmony));
            Log.Error($"[{Id}] Patch {name} could not be applied and is disabled: {e}");
        }
    }

    private static readonly HashSet<string> _notApplied = new();

    /// <summary>Whether this patch was skipped or failed when the mod loaded.</summary>
    internal static bool NotApplied(string name) => _notApplied.Contains(name);
}
