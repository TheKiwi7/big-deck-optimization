using System;
using System.Collections.Generic;
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

    /// <summary>v0.107.1 and v0.111: <c>_Input</c> only calls this check, which only acts on a pressed button.</summary>
    private const string InputFingerprint = "78557E27E72174AF";
    private const string DragCheckFingerprint = "60208EA15DC82512";

    public static bool Enabled { get; set; } = true;

    private static readonly ConcurrentDictionary<Type, bool> _eligibleByType = new();
    private static AccessTools.FieldRef<NClickableControl, bool>? _isPressed;
    private static AccessTools.FieldRef<NClickableControl, bool>? _isEnabled;
#if BENCH
    /// <summary>Buttons kept from processing input (the effects check).</summary>
    public static long Muted { get; private set; }
#endif

    public static void Apply(Harmony harmony)
    {
        _input = AccessTools.DeclaredMethod(typeof(NButton), "_Input", new[] { typeof(InputEvent) })
            ?? throw new MissingMethodException(nameof(NButton), "_Input");
        _dragCheck = AccessTools.Method(typeof(NClickableControl), "CheckMouseDragThreshold")
            ?? throw new MissingMethodException(nameof(NClickableControl), "CheckMouseDragThreshold");
        PatchSafety.RequireFingerprint(_input, InputFingerprint);
        PatchSafety.RequireFingerprint(_dragCheck, DragCheckFingerprint);
        _isPressed = AccessTools.FieldRefAccess<NClickableControl, bool>("_isPressed");
        _isEnabled = AccessTools.FieldRefAccess<NClickableControl, bool>("_isEnabled");
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(Node), nameof(Node.SetProcessInput)),
            prefix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SetProcessInputPrefix)));
        // Subclasses never call base._Ready() but always reach NClickableControl.ConnectSignals() from it.
        PatchGuard.Patch(harmony, AccessTools.DeclaredMethod(typeof(NClickableControl), "ConnectSignals"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(NClickableControl), "OnPressHandler"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(NClickableControl), "OnReleaseHandler"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(NClickableControl), "CheckMouseDragThreshold"),
            postfix: new HarmonyMethod(typeof(IdleButtonInput), nameof(SyncPostfix)));
    }

    private static System.Reflection.MethodInfo? _input, _dragCheck;

    /// <summary>On, and nobody else patches what a muted button no longer runs.</summary>
    private static bool Active => Enabled && !PatchSafety.IsPatchedByOthers(_input) && !PatchSafety.IsPatchedByOthers(_dragCheck);

    private static bool IsEligible(Node node) => IsEligibleType(node) && Active;

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
                Track((NClickableControl)__instance);
#if BENCH
                Muted++;
#endif
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
            if (!Active)
            {
                // Turned off (or another mod patches _Input) at runtime: give buttons we muted their vanilla state back (input on while enabled).
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

    /// <summary>Buttons kept from input, given it back as soon as this turns off (bdopt, a fault, another mod's patch).</summary>
    private static readonly List<WeakReference<NClickableControl>> _muted = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NClickableControl, object> _mutedSet = new();
    private static bool _watching;
    private static int _frames;

    private static void Track(NClickableControl button)
    {
        if (_mutedSet.TryGetValue(button, out _))
        {
            return;
        }
        _mutedSet.Add(button, _muted);
        _muted.Add(new WeakReference<NClickableControl>(button));
        if (_muted.Count > 1024)
        {
            _muted.RemoveAll(w => !w.TryGetTarget(out NClickableControl? b) || !GodotObject.IsInstanceValid(b));
        }
        if (!_watching && Engine.GetMainLoop() is SceneTree tree)
        {
            _watching = true;
            tree.ProcessFrame += Tick;
        }
    }

    /// <summary>Twice a second: once off, the muted buttons get the input vanilla gives an enabled button.</summary>
    private static void Tick()
    {
        try
        {
            if (++_frames < 30 || _muted.Count == 0)
            {
                return;
            }
            _frames = 0;
            if (Active)
            {
                return;
            }
            // A copy: giving input back runs our prefix again, which may track a button.
            WeakReference<NClickableControl>[] muted = _muted.ToArray();
            _muted.Clear();
            foreach (WeakReference<NClickableControl> reference in muted)
            {
                if (reference.TryGetTarget(out NClickableControl? button) && GodotObject.IsInstanceValid(button)
                    && !button.IsProcessingInput() && _isEnabled!(button))
                {
                    button.SetProcessInput(true);
                }
                if (button != null)
                {
                    _mutedSet.Remove(button);
                }
            }
        }
        catch (Exception e)
        {
            _muted.Clear();
            _mutedSet.Clear();
            Log.Error($"[{ModEntry.Id}] {Name}: giving muted buttons their input back failed: {e}");
        }
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
    }
}
