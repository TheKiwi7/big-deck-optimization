using BigDeckOptimization.Config;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace BigDeckOptimization.Commands;

/// <summary>
/// bdopt [on|off] | bdopt &lt;option&gt; on|off | bdopt fast_combat_start_relics &lt;0-100&gt;: turns every optimization
/// (or one) on/off at runtime or sets the fast combat start's relic threshold, e.g. to check
/// whether an issue comes from this mod, or shows their state. Local only, and safe in co-op: optimized and vanilla
/// paths give identical results. Saved in BaseLib's mod configuration when BaseLib is installed. Part of the public
/// build, unlike the test tooling in Bench.
/// </summary>
public class OptimizationsCmd : AbstractConsoleCmd
{
    public override string CmdName => "bdopt";

    public override string Args => "[on|off] | <option> on|off | fast_combat_start_relics <0-100, step 10>";

    public override string Description => "[BigDeckOptimization] Turns the mod's optimizations on/off (vanilla behavior when off), or shows their state.";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string? first = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        if (first is null)
        {
            return new CmdResult(success: true, Settings.Status());
        }
        if (first is "on" or "off" && args.Length == 1)
        {
            Settings.SetMaster(first == "on");
            return new CmdResult(success: true, Settings.Status());
        }
        string? value = args.Length == 2 ? args[1].ToLowerInvariant() : null;
        if (Settings.Find(first) != null && value is "on" or "off")
        {
            Settings.SetOption(first, value == "on");
            return new CmdResult(success: true, Settings.Status());
        }
        if (first == Settings.FastStartRelicsKey && int.TryParse(value, out int relics) && relics is >= 0 and <= 100)
        {
            Settings.SetFastStartRelics(relics);
            return new CmdResult(success: true, Settings.Status());
        }
        return new CmdResult(success: false, "Usage: bdopt " + Args + "\n" + Settings.Status());
    }

    /// <summary>Master switch, used by the benches for A/B runs.</summary>
    public static void SetAll(bool on) => Settings.SetMaster(on);
}
