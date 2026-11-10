using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Each Harmony Patch or Unpatch builds the method's replacement again, from all its patches, every mod's. Two things made
/// that go wrong in game (v1.0.0, <see cref="InlineGuard.RecompileInliners"/>):
/// <list type="bullet">
/// <item>Harmony keeps patches serialized and finds a patch method again by module and token: for one declared in a generic
/// type (BaseLib's <c>IAddedNodes&lt;T&gt;.UnconditionalAdd</c>, its postfix on <c>NGlobalUi._Ready</c> and
/// <c>NRewardButton._Ready</c>) that gives the open generic method, which the JIT refuses ("invalid program"). Only the
/// first build, with the method as the patching mod passed it, works.</item>
/// <item>Harmony 2.4's <c>PatchTools.DetourMethod</c> undoes the method's detour before it makes the new one: a replacement
/// that fails to compile leaves the method with no detour, every mod's patches on it gone. Without BaseLib's postfix on
/// <c>NGlobalUi._Ready</c>, Limitless's relic bar never got its scroll container, and in co-op the back button of a
/// player's profile threw (Limitless's AnimShow/AnimHide postfixes): no way back to the map.</item>
/// </list>
/// So this mod never rebuilds a method one of whose patches can't be found again (<see cref="Blocker"/>), and a rebuild that
/// fails anyway gets the previous replacement back (<see cref="Run{T}"/>). The Harmony internals read here are checked at
/// load (<see cref="Available"/>); restoring checks its own result.
/// </summary>
public static class HarmonyRebuild
{
    private static readonly IDictionary? _detours;
    private static readonly PropertyInfo? _target;
    private static readonly PropertyInfo? _isApplied;
    private static readonly MethodInfo? _detourMethod;

    /// <summary>Harmony's detour table and the detours' target and state were found as expected: restoring is possible.</summary>
    public static bool Available { get; }

    /// <summary>Rebuilds refused before they started (<see cref="Blocker"/>).</summary>
    public static int Refused { get; private set; }
    /// <summary>Failed rebuilds after which the method had lost its detour, and got its previous replacement back.</summary>
    public static int Restored { get; private set; }
    /// <summary>Failed rebuilds whose method couldn't get its previous replacement back.</summary>
    public static int Lost { get; private set; }

    static HarmonyRebuild()
    {
        try
        {
            Assembly harmony = typeof(Harmony).Assembly;
            Type? patchTools = harmony.GetType("HarmonyLib.PatchTools");
            Type? coreDetour = harmony.GetType("MonoMod.Core.ICoreDetour");
            Type? coreDetourBase = harmony.GetType("MonoMod.Core.ICoreDetourBase");
            if (patchTools == null || coreDetour == null || coreDetourBase == null)
            {
                return;
            }
            FieldInfo? detours = patchTools.GetField("detours", BindingFlags.Static | BindingFlags.NonPublic);
            _target = coreDetour.GetProperty("Target");
            _isApplied = coreDetourBase.GetProperty("IsApplied");
            _detourMethod = patchTools.GetMethod("DetourMethod", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(MethodBase), typeof(MethodBase) }, null);
            if (detours?.FieldType != typeof(Dictionary<,>).MakeGenericType(typeof(MethodBase), coreDetour)
                || _target?.PropertyType != typeof(MethodBase) || _isApplied?.PropertyType != typeof(bool) || _detourMethod == null)
            {
                return;
            }
            _detours = detours.GetValue(null) as IDictionary;
            Available = _detours != null;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {nameof(HarmonyRebuild)}: Harmony's detour table not as expected: {e.Message}");
        }
    }

    /// <summary>
    /// Why Harmony can't build this method's replacement again, or null. Only the first build of a patch method declared in a
    /// generic type works (see the class summary); one Harmony can't find at all (its assembly gone) fails too.
    /// </summary>
    public static string? Blocker(MethodBase original)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(original);
        if (info == null)
        {
            return null;
        }
        foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
            .Concat(info.InnerPrefixes).Concat(info.InnerPostfixes))
        {
            MethodInfo? method;
            try
            {
                method = patch.PatchMethod;
            }
            catch (Exception)
            {
                method = null;
            }
            if (method == null)
            {
                return $"a patch of {patch.owner} can't be found again";
            }
            if (method.ContainsGenericParameters || method.DeclaringType?.ContainsGenericParameters == true)
            {
                return $"{patch.owner}'s {MethodFingerprint.TypeName(method.DeclaringType)}.{method.Name} is declared in a generic type";
            }
        }
        return null;
    }

    /// <summary>
    /// Runs a Harmony call that rebuilds this method (Patch, Unpatch). Refused (an exception, nothing touched) when a patch
    /// already on it couldn't be rebuilt; when it fails anyway, the replacement the method had is put back before the
    /// exception goes on. <paramref name="what"/> names the call in the log.
    /// </summary>
    public static T Run<T>(MethodBase original, string what, Func<T> rebuild)
    {
        if (Blocker(original) is { } blocker)
        {
            Refused++;
            throw new NotSupportedException($"{Describe(original)} can't be built again by Harmony ({blocker}): {what} not done, its patches kept");
        }
        object? key = null;
        MethodBase? before = Available ? Current(original, out key) : null;
        try
        {
            return rebuild();
        }
        catch (Exception e)
        {
            if (before != null)
            {
                Restore(original, key!, before, what, e);
            }
            throw;
        }
    }

    public static void Run(MethodBase original, string what, Action rebuild) =>
        Run<object?>(original, what, () =>
        {
            rebuild();
            return null;
        });

    /// <summary>Patched methods without an applied detour: every mod's patches on them gone. Empty when <see cref="Available"/> is false.</summary>
    public static List<MethodBase> Unapplied()
    {
        var unapplied = new List<MethodBase>();
        if (!Available)
        {
            return unapplied;
        }
        foreach (MethodBase original in Harmony.GetAllPatchedMethods())
        {
            if (Current(original, out _) == null)
            {
                unapplied.Add(original);
            }
        }
        return unapplied;
    }

    /// <summary>
    /// Logs every patched method left without its detour (an error: some mod's patches don't run there), once all of this
    /// mod's rebuilds are done. Read by the tests (<c>HarmonyRebuild: all ... patched methods detoured</c>).
    /// </summary>
    public static void CheckAll(string after)
    {
        if (!Available)
        {
            return;
        }
        List<MethodBase> unapplied = Unapplied();
        if (unapplied.Count == 0)
        {
            Log.Info($"[{ModEntry.Id}] {nameof(HarmonyRebuild)}: all {Harmony.GetAllPatchedMethods().Count()} patched methods detoured after {after}"
                + (Refused + Restored + Lost > 0 ? $" ({Refused} rebuilds refused, {Restored} restored, {Lost} lost)" : ""));
            return;
        }
        Log.Error($"[{ModEntry.Id}] {nameof(HarmonyRebuild)}: after {after}, {unapplied.Count} patched methods run without their patches: "
            + string.Join(", ", unapplied.Select(Describe)));
    }

    /// <summary>The replacement this method runs now, or null (no detour, or the internals unavailable).</summary>
    public static MethodBase? Replacement(MethodBase original) => Available ? Current(original, out _) : null;

    /// <summary>The replacement this method is detoured to now, if its detour is applied; the key Harmony keeps it under.</summary>
    private static MethodBase? Current(MethodBase original, out object? key)
    {
        object? detour = Find(original, out key);
        if (detour == null || _isApplied!.GetValue(detour) is not true)
        {
            return null;
        }
        return _target!.GetValue(detour) as MethodBase;
    }

    private static object? Find(MethodBase original, out object? key)
    {
        lock (_detours!)
        {
            if (_detours.Contains(original))
            {
                key = original;
                return _detours[original];
            }
            // The same method reached through another type (ReflectedType) is another MethodBase object.
            RuntimeMethodHandle handle;
            try
            {
                handle = original.MethodHandle;
            }
            catch (Exception)
            {
                key = null;
                return null;
            }
            foreach (DictionaryEntry entry in _detours)
            {
                if (entry.Key is MethodBase method && method.MethodHandle.Equals(handle))
                {
                    key = method;
                    return entry.Value;
                }
            }
        }
        key = null;
        return null;
    }

    private static void Restore(MethodBase original, object key, MethodBase before, string what, Exception failure)
    {
        try
        {
            if (Current(original, out _) == before)
            {
                // Failed before the detour was touched (building the replacement): nothing lost.
                return;
            }
            try
            {
                _detourMethod!.Invoke(null, new[] { key, before });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
            if (Current(original, out _) != before)
            {
                throw new InvalidOperationException("the detour isn't on the previous replacement");
            }
            Restored++;
            Log.Warn($"[{ModEntry.Id}] {nameof(HarmonyRebuild)}: {what} on {Describe(original)} failed ({failure.Message}): its previous code is back, every mod's patches on it with it");
        }
        catch (Exception e)
        {
            Lost++;
            Log.Error($"[{ModEntry.Id}] {nameof(HarmonyRebuild)}: {what} on {Describe(original)} failed ({failure.Message}), and its previous code couldn't be put back ({e.Message}): the game's own code runs there, without any mod's patches");
        }
    }

    public static string Describe(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";
}
