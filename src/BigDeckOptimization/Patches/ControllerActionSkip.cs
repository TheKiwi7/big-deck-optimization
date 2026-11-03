using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace BigDeckOptimization.Patches;

/// <summary>
/// A <c>settings.save</c> written by an older game version keeps controller actions that no longer exist
/// (<c>controller_d_pad_up</c>, <c>controller_r_stick_*</c>...). <c>NInputManager._UnhandledInput</c> asks about every mapped action
/// at every unhandled event, and each question about a missing one makes Godot log an error with a C# backtrace: ~240 errors per card
/// played with the mouse, what filled a 64 MB game log in a co-op run.
///
/// In that method, an action the InputMap doesn't have answers false at once: what Godot returns after logging its error.
/// </summary>
public static class ControllerActionSkip
{
    public const string Name = "ControllerActionSkip";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Skipped { get; private set; }

    private static readonly MethodInfo _pressed = AccessTools.Method(typeof(InputEvent), nameof(InputEvent.IsActionPressed));
    private static readonly MethodInfo _released = AccessTools.Method(typeof(InputEvent), nameof(InputEvent.IsActionReleased));
    private static readonly HashSet<StringName> _known = new();

    public static void Apply(Harmony harmony)
    {
        MethodInfo original = AccessTools.Method(typeof(NInputManager), "_UnhandledInput")
            ?? throw new MissingMethodException(nameof(NInputManager), "_UnhandledInput");
        PatchGuard.Patch(harmony, original, transpiler: new HarmonyMethod(typeof(ControllerActionSkip), nameof(Transpiler)));
        Applied = true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_pressed) || instruction.Calls(_released))
            {
                // Same stack: (event, action, flags).
                string helper = instruction.Calls(_pressed) ? nameof(Pressed) : nameof(Released);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ControllerActionSkip), helper))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static bool Pressed(InputEvent inputEvent, StringName action, bool allowEcho, bool exactMatch) =>
        Exists(action) && inputEvent.IsActionPressed(action, allowEcho, exactMatch);

    private static bool Released(InputEvent inputEvent, StringName action, bool exactMatch) =>
        Exists(action) && inputEvent.IsActionReleased(action, exactMatch);

    private static bool Exists(StringName action)
    {
        if (!Enabled)
        {
            return true;
        }
        try
        {
            // Only those seen: a mod may add a missing action later, and asking about one costs far less than the error.
            if (_known.Contains(action))
            {
                return true;
            }
            if (InputMap.HasAction(action))
            {
                _known.Add(action);
                return true;
            }
            Skipped++;
            return false;
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, every action is asked about: {e}");
            return true;
        }
    }
}
