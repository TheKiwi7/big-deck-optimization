using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

/// <summary>Thrown by an Apply method when the patch doesn't apply here (optional mod absent, mirrored code changed).</summary>
public class PatchSkippedException(string reason) : Exception(reason);

public static class PatchSafety
{
    private static readonly HashSet<string> _faults = new();

    /// <summary>
    /// Records that a patch turned itself off after an error: settings (bdopt, the settings menu) never turn it back on this
    /// session.
    /// </summary>
    public static void Fault(string name)
    {
        lock (_faults)
        {
            _faults.Add(name);
        }
    }

    public static bool IsFaulted(string name)
    {
        lock (_faults)
        {
            return _faults.Contains(name);
        }
    }

    /// <summary>
    /// Throws <see cref="PatchSkippedException"/> unless <paramref name="method"/> still has the code a patch mirrors.
    /// </summary>
    public static void RequireFingerprint(MethodBase method, string expected)
    {
        string actual = MethodFingerprint.Of(method);
        if (actual != expected)
        {
            throw new PatchSkippedException(
                $"{method.DeclaringType?.Name}.{method.Name} changed (fingerprint {actual}, expected {expected}): left vanilla");
        }
    }

    /// <summary>
    /// True when a mod other than us has Harmony patches on the method. Our replacements mirror vanilla bodies, so
    /// they would silently drop another mod's transpiler or double-apply its postfix: we back off instead.
    /// </summary>
    public static bool IsPatchedByOthers(MethodBase? method)
    {
        if (method == null)
        {
            return true;
        }
        // Harmony.GetPatchInfo is costly and this runs per scope (several times per frame): results are reused for
        // a short while, so a mod patching later is still seen within RecheckAfterMs.
        long now = Environment.TickCount64;
        lock (_patchedByOthers)
        {
            if (_patchedByOthers.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
            {
                return cached.Patched;
            }
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        bool patched = info != null && info.Owners.Any(owner => owner != ModEntry.Harmony.Id);
        lock (_patchedByOthers)
        {
            _patchedByOthers[method] = (patched, now);
        }
        return patched;
    }

    /// <summary>
    /// Like <see cref="IsPatchedByOthers(MethodBase?)"/>, except for these other mods' postfixes, whose effect the caller
    /// reproduces (checked by fingerprint).
    /// </summary>
    public static bool IsPatchedByOthers(MethodBase? method, params MethodInfo[] allowedPostfixes)
    {
        if (method == null)
        {
            return true;
        }
        long now = Environment.TickCount64;
        lock (_patchedBeyondAllowed)
        {
            if (_patchedBeyondAllowed.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
            {
                return cached.Patched;
            }
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        bool patched = info != null && (
            info.Prefixes.Concat(info.Transpilers).Concat(info.Finalizers).Any(p => p.owner != ModEntry.Harmony.Id)
            || info.Postfixes.Any(p => p.owner != ModEntry.Harmony.Id
                && !allowedPostfixes.Any(a => a.MethodHandle == p.PatchMethod.MethodHandle)));
        lock (_patchedBeyondAllowed)
        {
            _patchedBeyondAllowed[method] = (patched, now);
        }
        return patched;
    }

    private const long RecheckAfterMs = 2000;
    private static readonly Dictionary<MethodBase, (bool Patched, long CheckedAt)> _patchedByOthers = new();
    private static readonly Dictionary<MethodBase, (bool Patched, long CheckedAt)> _patchedBeyondAllowed = new();
}
