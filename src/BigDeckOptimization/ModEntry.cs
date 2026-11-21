using System;
using System.Collections.Generic;
using System.Linq;
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
        // The game's mod mismatch error over that entry explained (display only; also applied with the mod off at the start).
        (JoinMismatchNote.Name, JoinMismatchNote.Apply),
        // Next: other mods' Patch and Unpatch calls from now on are seen (caches dropped, lost detours, hidden IL hooks).
        (HarmonyWatch.Name, HarmonyWatch.Apply),
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
        (CombatCardSet.Name, CombatCardSet.Apply),
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
        (SaveBackupCheck.Name, SaveBackupCheck.Apply),
        (SaveReadDecode.Name, SaveReadDecode.Apply),
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
        // Test tooling (startup-measure branch): only with --bdstartprof.
        TryApply(Bench.StartupProfile.Name, Bench.StartupProfile.Apply);
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
    /// The longest <see cref="RecompileOverFrames"/> waits for the main menu (and HookSkip's hooks) before rebuilding anyway (a
    /// game or mod that never shows it): the rebuilds then go on at <see cref="ApplyBudgetMs"/> a frame.
    /// </summary>
    private const double MenuWaitMs = 30000;

    /// <summary>
    /// <see cref="InlineGuard.RecompileInliners"/> once the main menu shows (P2 of 2026-11-20: while the intro logo plays its
    /// steps were the logo's remaining hitches, and on a menu that showed early a share of the long frame that loaded it made a
    /// 0.3 s freeze), <see cref="ApplyBudgetMs"/> a frame (a step is one method: some take 20-40 ms), at once when a run is
    /// under way: what it fixes only matters to cards in a run (the guarantee as before: a run started before the rebuilds end
    /// gets the rest at once, in the next frame, before its first combat). The list of methods is taken now, as soon as every
    /// patch of the start is in, as before (<see cref="InlineGuard.PatchedNow"/>): HookSkip's hooks patched after it were
    /// compiled with the flags set. Which of them to rebuild is judged once HookSkip's hooks are all patched (protected), so
    /// against every protection, whenever the menu shows (before, the steps ran beside HookSkip's batches, and a method judged
    /// before a hook it reaches was patched wasn't rebuilt: a set that depended on the frames). The frames it took.
    /// </summary>
    private static async System.Threading.Tasks.Task<int> RecompileOverFrames()
    {
        Godot.SceneTree? tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
        int frames = 0;
        using IEnumerator<bool> steps = InlineGuard.RecompileInliners(new Harmony(Harmony.Id + "." + nameof(InlineGuard)), InlineGuard.PatchedNow()).GetEnumerator();
        long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        bool menu = false, started = false;
        while (true)
        {
            double budget = 0;
            if (tree == null || RunInProgress())
            {
                if (tree != null)
                {
                    Log.Info($"[{Id}] {nameof(InlineGuard)}: a run is under way, the rebuilds left run now ({System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds:F0} ms after the patches)");
                }
                budget = double.MaxValue;
            }
            else
            {
                // Once the menu showed, never waiting for it again (it is gone while a run is being set up).
                bool late = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds >= MenuWaitMs;
                menu = menu || late || CanRunStart();
                if (started || (menu && (late || !HookSkip.HooksPending)))
                {
                    if (!started)
                    {
                        started = true;
                        Log.Info($"[{Id}] {nameof(InlineGuard)}: rebuilds start, {ApplyBudgetMs} ms a frame ({System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds:F0} ms after the patches"
                            + (CanRunStart() ? ", the main menu shows)" : ", no main menu yet)"));
#if BENCH
                        // Test tooling (--bdearly, a scenario's 'launchearly' line): the test runner's script starts now, and the
                        // rebuilds are held until a run is under way, as a player starting a run within the menu's first frames
                        // would find them (a combat started right after the menu: the rest at once when the run is under way).
                        if (LaunchedWith(EarlyArg))
                        {
                            Ready = true;
                            _holdRebuilds = true;
                        }
#endif
                    }
                    budget = ApplyBudgetMs;
#if BENCH
                    if (_holdRebuilds)
                    {
                        budget = 0;
                    }
#endif
                }
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (budget > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < budget)
            {
                if (!steps.MoveNext())
                {
                    return frames;
                }
            }
            await tree!.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
            frames++;
        }
    }

    /// <summary>A run exists (true when that can't be read: everything at once, as before).</summary>
    private static bool RunInProgress()
    {
        try
        {
            return MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress;
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            return true;
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
    /// has loaded (BaseLib may come after this mod); false until then. Also true in a Debug build launched with
    /// <c>--bdvanilla</c> (the test tooling's vanilla reference sessions, never saved).
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
#if BENCH
        // Test tooling, --bdvanilla (parlib.ps1's mod-off sessions): this session starts as a player's started with the mod off
        // (nothing applied but the test tooling and the co-op check), whatever the settings say. Read from the launch arguments
        // only: never saved, and Rewritten stays OwnIntegrity's alone.
        try
        {
            if (Godot.OS.GetCmdlineArgs().Concat(Godot.OS.GetCmdlineUserArgs()).Contains(VanillaArg))
            {
                LaunchedVanilla = true;
                StartedOff = true;
                Log.Info($"[{Id}] {VanillaLine}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{Id}] {VanillaArg}: launch arguments not read: {e.Message}");
        }
#endif
        try
        {
            // Another mod may have loaded a rewritten copy of this DLL (OwnIntegrity): none of it is trusted then.
            if (OwnIntegrity.WhyRewritten(typeof(ModEntry).Assembly, OwnIntegrity.OwnFile()) is { } rewritten)
            {
                Rewritten = rewritten;
                StartedOff = true;
                Log.Warn($"[{Id}] This mod's code was not loaded as shipped ({rewritten}): another mod rewrote it. Nothing is applied this session but the co-op check (unmodified game).");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{Id}] {nameof(OwnIntegrity)}: check failed ({e.Message}), mod on");
        }
        Log.Info($"[{Id}] Initialized v{typeof(ModEntry).Assembly.GetName().Version?.ToString(3)} in {_initMs:F0} ms"
            + (Rewritten != null ? ": loaded rewritten by another mod, nothing applied but the co-op check (CoopModeTag)"
                : VanillaWhy != null ? $": {VanillaWhy}, nothing applied but the test tooling and the co-op check (CoopModeTag, JoinMismatchNote)"
                : StartedOff ? ": turned off in the settings, nothing applied but the co-op check (CoopModeTag, JoinMismatchNote; turn it on, then restart the game)" : ""));
        LogLookups();
    }

#if BENCH
    /// <summary>
    /// The test tooling's launch argument for a vanilla reference session: <see cref="StartedOff"/> true from the start, as a
    /// player's session started with the mod off, so no optimization patch is installed (only the test tooling and
    /// <see cref="CoopModeTag"/>). parlib.ps1 launches a solo mod-off scenario with it; vanillacheck.ps1 compares such a session
    /// with one where every patch is installed and <c>bdopt off</c> turns them off.
    /// </summary>
    internal const string VanillaArg = "--bdvanilla";

    /// <summary>The log line saying so (judge.py keys the mod off from the start on it, parlib.ps1 requires it).</summary>
    internal const string VanillaLine = "BDVANILLA: launched with " + VanillaArg + " (test tooling): a vanilla reference session, the mod off from its start as a player's saved off would leave it, for this session only (never saved): no optimization patch applied";

    /// <summary>Launched with <see cref="VanillaArg"/>: this session is a vanilla reference.</summary>
    internal static bool LaunchedVanilla { get; private set; }

    /// <summary>
    /// The test tooling's launch argument (a scenario's <c>launchearly</c> line) making <see cref="Ready"/> true as soon as the
    /// main menu shows and InlineGuard's rebuilds start: the script's run starts while they go on.
    /// </summary>
    internal const string EarlyArg = "--bdearly";

    /// <summary>Launched with <see cref="EarlyArg"/>: InlineGuard's rebuilds wait for a run (then all at once).</summary>
    private static bool _holdRebuilds;

    /// <summary>Whether the game was launched with <paramref name="arg"/> (false when that can't be read).</summary>
    internal static bool LaunchedWith(string arg)
    {
        try
        {
            return Godot.OS.GetCmdlineArgs().Concat(Godot.OS.GetCmdlineUserArgs()).Contains(arg);
        }
        catch (Exception)
        {
            return false;
        }
    }
#endif

    /// <summary>Why nothing is applied in a vanilla reference session (<c>--bdvanilla</c>, test tooling only); null otherwise, always in a Release build.</summary>
#if BENCH
    private static string? VanillaWhy => LaunchedVanilla ? $"started off by {VanillaArg} (test tooling)" : null;
#else
    private static string? VanillaWhy => null;
#endif

    /// <summary>
    /// Why this mod's own code isn't the shipped one (<see cref="OwnIntegrity"/>), or null: then <see cref="StartedOff"/> is
    /// true for the session, whatever the settings say.
    /// </summary>
    public static string? Rewritten { get; private set; }

    /// <summary>
    /// The game's declared version and the mods patching Harmony's reflection lookups, logged once: a cross-check for
    /// <see cref="Reflect"/> (this mod's lookups never go through Harmony's) and for the per-version tables (HookSkip).
    /// </summary>
    private static void LogLookups()
    {
        try
        {
            string[] interceptors = Reflect.LookupInterceptors();
            Log.Info($"[{Id}] Game declares {Reflect.GameVersion() ?? "no version"}"
                + (interceptors.Length > 0
                    ? $"; Harmony's reflection lookups are patched by {string.Join(", ", interceptors)}: this mod's own lookups go through the runtime's reflection, unaffected"
                    : ""));
        }
        catch (Exception e)
        {
            Log.Info($"[{Id}] Game version not read: {e.Message}");
        }
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
        // started with the mod off can't join a co-op game whose players have it on (CoopModeTag's summary). JoinMismatchNote
        // too, unless this mod's code was rewritten (nothing of it trusted then but the co-op check): display only, it explains
        // that refusal to the player whose mod is off.
        bool coopCheck = patchClass == typeof(CoopModeTag) || (patchClass == typeof(JoinMismatchNote) && Rewritten == null);
        if (StartedOff && !testTooling && !coopCheck)
        {
            _notApplied.Add(name);
            Log.Info($"[{Id}] Patch {name} not applied: " + (VanillaWhy ?? "the mod was turned off when the game started"));
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Harmony harmony = new(Harmony.Id + "." + name);
        try
        {
            PatchSafety.RequireResolvable(patchClass);
            using (HarmonyWatch.Own())
            {
                apply(harmony);
            }
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
