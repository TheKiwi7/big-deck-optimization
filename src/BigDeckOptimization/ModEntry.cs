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

    public static void Init()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        TryApply(ListenerCache.Name, ListenerCache.Apply);
        TryApply(HookFilter.Name, HookFilter.Apply);
        TryApply(ExtraScopes.Name, ExtraScopes.Apply);
        TryApply(SnapshotVerifier.Name, SnapshotVerifier.Apply);
        TryApply(IdleButtonInput.Name, IdleButtonInput.Apply);
        TryApply(GridSortCache.Name, GridSortCache.Apply);
        TryApply(EnchantmentTabReuse.Name, EnchantmentTabReuse.Apply);
        TryApply(PoolSignalScan.Name, PoolSignalScan.Apply);
        TryApply(CardDbScan.Name, CardDbScan.Apply);
        TryApply(PileIndex.Name, PileIndex.Apply);
        TryApply(PileCounterBatch.Name, PileCounterBatch.Apply);
        TryApply(FastCombatStart.Name, FastCombatStart.Apply);
        TryApply(ModImage.Name, ModImage.Apply);
        TryApply(HoverTipReuse.Name, HoverTipReuse.Apply);
        TryApply(FastListeners.Name, FastListeners.Apply);
        TryApply(HookSkip.Name, HookSkip.Apply);
        TryApply(LimitlessReserialize.Name, LimitlessReserialize.Apply);
        TryApply(LimitlessRecalcSkip.Name, LimitlessRecalcSkip.Apply);
        TryApply(SnapshotCardReuse.Name, SnapshotCardReuse.Apply);
        TryApply(TextPoolReuse.Name, TextPoolReuse.Apply);
        TryApply(RelicFlashMerge.Name, RelicFlashMerge.Apply);
        TryApply(TypePlaqueSkip.Name, TypePlaqueSkip.Apply);
#if BENCH
        TryApply(Bench.CombatStartTimer.Name, Bench.CombatStartTimer.Apply);
        TryApply(Bench.PerfProbe.Name, Bench.PerfProbe.Apply);
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
        Log.Info($"[{Id}] Initialized v{typeof(ModEntry).Assembly.GetName().Version?.ToString(3)} in {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms"
            + (StartedOff ? ": turned off in the settings, nothing applied (turn it on, then restart the game)" : ""));
    }

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
