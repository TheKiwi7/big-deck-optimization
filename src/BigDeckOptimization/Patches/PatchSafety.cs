using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

/// <summary>Thrown by an Apply method when the patch doesn't apply here (optional mod absent, mirrored code changed).</summary>
public class PatchSkippedException(string reason) : Exception(reason);

public static class PatchSafety
{
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
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        return info != null && info.Owners.Any(owner => owner != ModEntry.Harmony.Id);
    }
}
