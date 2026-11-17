using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The delegates the game's combat-end check invokes (<c>CombatManager.IsEnding</c>, and <c>NonInteractiveMode.IsActive</c> before
/// it in <c>SfxCmd.Play</c>): the mods' combat hook subscriptions (<c>ModHelper._combatHookSubscribers</c>, invoked by
/// <c>CombatState.IterateHookListeners</c> through <c>ModHelper.IterateAllCombatStateSubscribers</c>: BaseLib's
/// "BaseLibCardModifiers" walks every card) and the test hook <c>NonInteractiveMode.AutoSlayerCheck</c> (the game's own lambda,
/// or the AutoSlayer's). The game subscribes nothing itself (v0.107.1 to v0.113.1), so any subscription is a mod's code.
/// Shared by RelicFlashMerge (whether a merged flash replays its sound) and CardFlightLimit (whether a skipped flight would
/// leave out a mod's code in that check). Allocates nothing while the answer stays the same (C2,
/// re-audit round 2 of 2026-11-17: asked per flash and per soul).
/// </summary>
internal static class GameHookDelegates
{
    private static bool _resolved;
    private static FieldInfo? _subscribers, _subscriberDelegate;
    private static Func<Func<bool>>? _autoSlayerCheck;

    /// <summary>The test hook's last answer, for the same delegate (read and replaced whole).</summary>
    private sealed record Answer(Delegate? Check, bool Value);

    private static Answer? _last;

    private static void Resolve()
    {
        if (_resolved)
        {
            return;
        }
        try
        {
            _subscribers = Reflect.Field(typeof(MegaCrit.Sts2.Core.Modding.ModHelper), "_combatHookSubscribers");
            if (Reflect.Inner(typeof(MegaCrit.Sts2.Core.Modding.ModHelper), "ModCombatHookSubscriber") is { } subscriber)
            {
                _subscriberDelegate = Reflect.Field(subscriber, "del");
            }
        }
        catch
        {
            _subscribers = null; // Unknown: counts as subscribed.
        }
        try
        {
            if (Reflect.Method(typeof(MegaCrit.Sts2.Core.Helpers.NonInteractiveMode), "get_" + nameof(MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.AutoSlayerCheck)) is { } getter)
            {
                _autoSlayerCheck = (Func<Func<bool>>)Delegate.CreateDelegate(typeof(Func<Func<bool>>), getter);
            }
        }
        catch
        {
            _autoSlayerCheck = null; // Unknown: counts as another mod's.
        }
        _resolved = true;
    }

    /// <summary>
    /// Whether those delegates may reach another mod's code: any combat hook subscription, or a test hook that isn't all the
    /// game's own code. True when it can't tell.
    /// </summary>
    public static bool AnyForeign() => Subscribed() || TestHookForeign();

    /// <summary>
    /// Whether any combat hook subscription exists (only mods subscribe: their delegates run in the combat's listener walks,
    /// <c>Hook.ShouldStopCombatFromEnding</c>'s included). True when it can't tell.
    /// </summary>
    public static bool Subscribed()
    {
        try
        {
            Resolve();
            return _subscribers?.GetValue(null) is not ICollection subscribers || subscribers.Count > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Whether the test hook <c>NonInteractiveMode.AutoSlayerCheck</c> (called by <c>NonInteractiveMode.IsActive</c> outside the
    /// game's test mode) holds anything but the game's own code. True when it can't tell.
    /// </summary>
    public static bool TestHookForeign()
    {
        try
        {
            Resolve();
            if (_autoSlayerCheck == null)
            {
                return true;
            }
            Func<bool>? check = _autoSlayerCheck();
            Answer? last = _last;
            if (last != null && ReferenceEquals(last.Check, check))
            {
                return last.Value;
            }
            Assembly game = typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly;
            bool value = check == null || check.GetInvocationList().Any(d => d.Method.Module.Assembly != game);
            _last = new Answer(check, value);
            return value;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>The assemblies those delegates come from, outside the game (display: their mods); empty when none or unknown.</summary>
    public static Assembly?[] ForeignAssemblies()
    {
        var found = new List<Assembly?>();
        try
        {
            Resolve();
            Assembly game = typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly;
            if (_subscribers?.GetValue(null) is IEnumerable subscribers && _subscriberDelegate != null)
            {
                foreach (object? subscriber in subscribers)
                {
                    if (subscriber != null && _subscriberDelegate.GetValue(subscriber) is Delegate del)
                    {
                        found.Add(PatchSafety.DelegateAssembly(del));
                    }
                }
            }
            if (_autoSlayerCheck?.Invoke() is { } check)
            {
                found.AddRange(check.GetInvocationList().Select(d => d.Method.Module.Assembly).Where(a => a != game));
            }
        }
        catch
        {
            // Display only.
        }
        return found.Where(a => a != null).Distinct().ToArray();
    }
}
