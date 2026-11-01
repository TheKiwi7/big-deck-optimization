using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace BigDeckOptimization.Patches;

/// <summary>
/// After every action the hand's cards, health bars and counters set their labels' colors again
/// (<c>AddThemeColorOverride</c>, six times per card), almost always to the color they already have. Each call makes Godot
/// tell the label its theme changed, which it answers by shaping its text again on the next draw: ~85 µs per call plus
/// that shaping, the largest part of a hand refresh.
///
/// In the methods that do it after every action, the call is skipped when the label already has that override with that
/// color, read from the label itself (nothing cached). Same colors on screen, and any change still goes through.
/// </summary>
public static class ThemeColorSkip
{
    public const string Name = "ThemeColorSkip";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Skipped { get; private set; }
    public static long Set { get; private set; }

    private static readonly MethodInfo _add = AccessTools.Method(typeof(Control), nameof(Control.AddThemeColorOverride));

    public static void Apply(Harmony harmony)
    {
        if (_add == null)
        {
            throw new MissingMethodException(nameof(Control), nameof(Control.AddThemeColorOverride));
        }
        var transpiler = new HarmonyMethod(typeof(ThemeColorSkip), nameof(Transpiler));
        int patched = 0;
        // By name: looking through all their methods took 20 ms at each launch. One the game renames just keeps setting its colors.
        foreach ((Type type, string method) in new[]
        {
            (typeof(NCard), "UpdateEnergyCostVisuals"), (typeof(NCard), "UpdateEnergyCostColor"), (typeof(NCard), "UpdateStarCostVisuals"),
            (typeof(NCard), "UpdateStarCostColor"), (typeof(NCard), "UpdateTitleLabel"), (typeof(NHealthBar), "RefreshText"),
            (typeof(NEnergyCounter), "RefreshLabel"), (typeof(NStarCounter), "SetStarCountText"), (typeof(NPower), "RefreshAmount"),
        })
        {
            if (AccessTools.DeclaredMethod(type, method) is { } original)
            {
                PatchGuard.Patch(harmony, original, transpiler: transpiler);
                patched++;
            }
            else
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {type.Name}.{method} not found, it sets its colors as the game does");
            }
        }
        if (patched == 0)
        {
            throw new PatchSkippedException("none of its methods exists any more");
        }
        Applied = true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_add))
            {
                // Same stack: (control, name, color).
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ThemeColorSkip), nameof(AddIfChanged)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static void AddIfChanged(Control control, StringName name, Color color)
    {
        if (Enabled)
        {
            try
            {
                if (control.HasThemeColorOverride(name) && control.GetThemeColor(name) == color)
                {
                    Skipped++;
                    return;
                }
            }
            catch (Exception e)
            {
                Enabled = false;
                PatchSafety.Fault(Name);
                Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, colors are set every time: {e}");
            }
        }
        Set++;
        control.AddThemeColorOverride(name, color);
    }
}
