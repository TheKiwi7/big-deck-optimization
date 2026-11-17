using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Script <c>eventtrace on</c>: logs, with the frame number, what an event option does (a remote player's choice arriving, an
/// option chosen, the page set, the max HP, damage, heal and waits it awaits, each with when its task completed). To see which
/// step a peer is on when the next choice arrives (the game applies a remote choice to that player's current options).
/// </summary>
public static partial class AutoTest
{
    private static bool _eventTrace;

    private static void EventTrace()
    {
        if (_eventTrace)
        {
            return;
        }
        _eventTrace = true;
        Type sync = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer");
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(sync, "HandleEventOptionChosenMessage"),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(TraceReceived)));
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(sync, "ChooseOptionForEvent"),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(TraceChosen)));
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(EventModel), "SetEventState"),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(TracePage)));
        var task = new HarmonyMethod(typeof(AutoTest), nameof(TraceTask));
        var start = new HarmonyMethod(typeof(AutoTest), nameof(TraceStart));
        foreach (MethodInfo m in typeof(CreatureCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name is "GainMaxHp" or "Damage" or "Heal" && typeof(Task).IsAssignableFrom(m.ReturnType)))
        {
            PatchGuard.Patch(ModEntry.Harmony, m, prefix: start, postfix: task);
        }
        foreach (MethodInfo m in typeof(Cmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name is "Wait" or "CustomScaledWait" && typeof(Task).IsAssignableFrom(m.ReturnType)))
        {
            PatchGuard.Patch(ModEntry.Harmony, m, prefix: start, postfix: task);
        }
        Say("  event trace on");
    }

    private static string Frame => $"f{Engine.GetProcessFrames()} {_clock.Elapsed.TotalMilliseconds:F0}ms";

    private static void TraceReceived(object __0, ulong __1) => Say($"  TRACE {Frame} received choice from {__1}: {Traverse.Create(__0).Field("optionIndex").GetValue()}");

    private static void TraceChosen(Player player, int optionIndex) => Say($"  TRACE {Frame} choose for {player.NetId} index {optionIndex}");

    private static void TracePage(EventModel __instance) => Say($"  TRACE {Frame} page set for {__instance.Owner?.NetId}");

    private static void TraceStart(MethodBase __originalMethod) => Say($"  TRACE {Frame} {__originalMethod.Name} starts");

    private static void TraceTask(MethodBase __originalMethod, Task __result)
    {
        string name = __originalMethod.Name;
        if (__result == null)
        {
            return;
        }
        if (__result.IsCompleted)
        {
            Say($"  TRACE {Frame} {name} done at once");
            return;
        }
        Say($"  TRACE {Frame} {name} pending");
        __result.ContinueWith(_ => Say($"  TRACE {Frame} {name} done later"), TaskContinuationOptions.ExecuteSynchronously);
    }
}
