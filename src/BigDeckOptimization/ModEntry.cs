using System;
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
        Log.Info($"[{Id}] Initialized v{typeof(ModEntry).Assembly.GetName().Version?.ToString(3)}");
    }

    /// <summary>
    /// Applies one patch in isolation: if the game changed and the target is gone, only that patch is lost.
    /// </summary>
    internal static void TryApply(string name, Action<Harmony> apply)
    {
        try
        {
            apply(Harmony);
            Log.Info($"[{Id}] Patch {name} applied");
        }
        catch (PatchSkippedException e)
        {
            Log.Info($"[{Id}] Patch {name} skipped: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Error($"[{Id}] Patch {name} could not be applied and is disabled: {e}");
        }
    }
}
