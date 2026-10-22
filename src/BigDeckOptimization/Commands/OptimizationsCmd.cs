using BigDeckOptimization.Patches;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace BigDeckOptimization.Commands;

/// <summary>
/// bdopt [on|off]: turns every optimization on/off at runtime (e.g. to check whether an issue comes from this mod), or
/// shows their state. Local only, and safe in co-op: optimized and vanilla paths give identical results.
/// Part of the public build, unlike the test tooling in Bench.
/// </summary>
public class OptimizationsCmd : AbstractConsoleCmd
{
    public override string CmdName => "bdopt";

    public override string Args => "[on|off]";

    public override string Description => "[BigDeckOptimization] Turns the mod's optimizations on/off (vanilla behavior when off), or shows their state.";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string? value = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        if (value is not (null or "on" or "off"))
        {
            return new CmdResult(success: false, "Usage: bdopt " + Args);
        }
        if (value != null)
        {
            SetAll(value == "on");
        }
        return new CmdResult(success: true, Status());
    }

    public static void SetAll(bool on)
    {
        ListenerCache.SnapshotEnabled = on;
        ListenerCache.RefreshEnabled = on;
        HookFilter.Enabled = on;
        IdleButtonInput.Enabled = on;
        GridSortCache.Enabled = on;
        EnchantmentTabReuse.Enabled = on;
        PoolSignalScan.Enabled = on;
    }

    /// <summary>One line per optimization; a patch that turned itself off after an error also shows OFF here.</summary>
    public static string Status()
    {
        static string State(bool on) => on ? "on" : "OFF";
        return "BigDeckOptimization:\n"
            + $"  {ListenerCache.Name}: snapshot {State(ListenerCache.SnapshotEnabled)}, refresh {State(ListenerCache.RefreshEnabled)}\n"
            + $"  {HookFilter.Name}: {State(HookFilter.Enabled)}\n"
            + $"  {IdleButtonInput.Name}: {State(IdleButtonInput.Enabled)}\n"
            + $"  {GridSortCache.Name}: {State(GridSortCache.Enabled)}\n"
            + $"  {EnchantmentTabReuse.Name}: {(EnchantmentTabReuse.Applied ? State(EnchantmentTabReuse.Enabled) : "not applied (Limitless absent or changed)")}\n"
            + $"  {PoolSignalScan.Name}: {(PoolSignalScan.Applied ? State(PoolSignalScan.Enabled) : "not applied (game code changed)")}";
    }
}
