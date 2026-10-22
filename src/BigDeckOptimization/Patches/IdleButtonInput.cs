using System;
using System.Collections.Concurrent;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Every <see cref="NButton"/> receives every input event (each mouse motion) through <c>_Input</c>, which only
/// cancels a press when the mouse is dragged while the button is held. With hundreds of relic holders, that is
/// hundreds of Godot→C# calls per mouse move for nothing.
///
/// For buttons whose <c>_Input</c> is exactly NButton's, input processing is kept off while the button isn't pressed
/// and turned on when it is: same behavior, no idle calls. UI only, nothing gameplay-related.
/// </summary>
public static class IdleButtonInput
{
    public const string Name = "IdleButtonInput";

    public static bool Enabled { get; set; } = true;

    private static readonly ConcurrentDictionary<Type, bool> _eligibleByType = new();
    private static AccessTools.FieldRef<NClickableControl, bool>? _isPressed;
    private static AccessTools.FieldRef<NClickableControl, bool>? _isEnabled;

    public static void Apply(Harmony harmony)
    {
        _isPressed = AccessTools.FieldRefAccess<NClickableControl, bool>("_isPressed");
        _isEnabled = AccessTools.FieldRefAccess<NClickableControl, bool>("_isEnabled");
        harmony.Patch(AccessTools.Method(typeof(Node), nameof(Node.SetProcessInput)),
            prefix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SetProcessInputPrefix)));
        // Subclasses never call base._Ready() but always reach NClickableControl.ConnectSignals() from it.
        harmony.Patch(AccessTools.DeclaredMethod(typeof(NClickableControl), "ConnectSignals"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NClickableControl), "OnPressHandler"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NClickableControl), "OnReleaseHandler"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NClickableControl), "CheckMouseDragThreshold"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
    }

    private static bool IsEligible(Node node) => Enabled && IsEligibleType(node);

    private static bool IsEligibleType(Node node)
    {
        return node is NButton && _eligibleByType.GetOrAdd(node.GetType(), type =>
        {
            MethodInfo? input = type.GetMethod("_Input", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                new[] { typeof(InputEvent) });
            return input?.DeclaringType == typeof(NButton);
        });
    }

    /// <summary>Blocks turning input on for an idle eligible button; the press handler turns it on when needed.</summary>
    private static void SetProcessInputPrefix(Node __instance, ref bool enable)
    {
        try
        {
            if (enable && IsEligible(__instance) && !_isPressed!((NClickableControl)__instance))
            {
                enable = false;
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    /// <summary>Input on exactly while pressed.</summary>
    private static void SyncPostfix(NClickableControl __instance)
    {
        try
        {
            if (!IsEligibleType(__instance))
            {
                return;
            }
            if (!Enabled)
            {
                // Turned off at runtime: give buttons we muted their vanilla state back (input on while enabled).
                if (!__instance.IsProcessingInput() && _isEnabled!(__instance))
                {
                    __instance.SetProcessInput(true);
                }
                return;
            }
            bool pressed = _isPressed!(__instance);
            if (__instance.IsProcessingInput() != pressed)
            {
                __instance.SetProcessInput(pressed);
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
    }
}
