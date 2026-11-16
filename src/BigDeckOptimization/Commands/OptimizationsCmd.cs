using BigDeckOptimization.Config;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace BigDeckOptimization.Commands;

/// <summary>
/// bdopt [on|off] | bdopt &lt;option&gt; on|off | bdopt fast_combat_start_relics &lt;0-100&gt; | bdopt why: turns every optimization
/// (or one) on/off at runtime or sets the fast combat start's relic threshold, e.g. to check
/// whether an issue comes from this mod, or shows their state; bdopt why lists the parts other mods keep on the normal game
/// (<see cref="Settings.WhyReport"/>). Local only. Each option gives the same results on or off,
/// so peers may differ, with one exception: the whole mod, which every co-op player must have in the same state (all on or
/// all off, <see cref="Patches.CoopModeTag"/>). Switched in a co-op game (<see cref="Settings.InCoopGame"/>: a run, a start
/// lobby, a co-op save's load lobby, or the run manager's net session connected as host or client), one player's combats
/// would start at another pace than the others' (the fast combat
/// start), so <c>bdopt off</c> and <c>bdopt on</c> then warn and need <c>bdopt off yes</c> / <c>bdopt on yes</c>. Not when the
/// mod was turned off at the start (<see cref="ModEntry.StartedOff"/>: nothing of it runs this session either way).
/// Saved in BaseLib's mod configuration when BaseLib is installed. Part of the public build, unlike the test tooling in Bench.
/// </summary>
public class OptimizationsCmd : AbstractConsoleCmd
{
    public override string CmdName => "bdopt";

    public override string Args => "[on|off] | off yes / on yes (in a co-op run or lobby) | <option> on|off | fast_combat_start_relics <0-100, step 10> | why";

    public override string Description => "[BigDeckOptimization] Turns the mod's optimizations on/off (vanilla behavior when off), or shows their state; why: which other mods keep part of them on the normal game.";

    public override bool IsNetworked => false;

    /// <summary>
    /// Not a debug tool: it only changes this mod's local switches. A debug-only command (the default) marks the run as
    /// having used one in its save since game v0.113 (<c>ExtraFields.MarkDebugToolUsed</c>), even to show the state.
    /// </summary>
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string? first = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        // What other mods keep vanilla, checked now (answers younger than 2 s reused): the status and why show it.
        Settings.RefreshConflicts();
        if (first is null)
        {
            return new CmdResult(success: true, Settings.Status());
        }
        if (first == "why" && args.Length == 1)
        {
            return new CmdResult(success: true, Settings.WhyReport());
        }
        bool confirmed = args.Length == 2 && first is "on" or "off" && args[1].ToLowerInvariant() == "yes";
        if (first is "on" or "off" && (args.Length == 1 || confirmed))
        {
            bool on = first == "on";
            // A switch of the whole mod that changes what runs (not after a start with the mod off: nothing of it runs then).
            if (!confirmed && on != Settings.Master && !ModEntry.StartedOff && Settings.InCoopGame)
            {
                return new CmdResult(success: false, (on ? Settings.CoopOnWarning : Settings.CoopOffWarning) + " " + Settings.CoopRejoinNow
                    + $"\nTo turn it {first} anyway, type: bdopt {first} yes");
            }
            Settings.SetMaster(on);
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
}
