using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// they would silently drop another mod's transpiler or double-apply its postfix: we back off instead. Each
    /// back-off is recorded for the calling patch (its source file name, e.g. HookFilter) with the other mods' names,
    /// so the settings menu, bdopt and the log can say which mod keeps that part of an optimization vanilla.
    /// </summary>
    public static bool IsPatchedByOthers(MethodBase? method, [CallerFilePath] string caller = "")
    {
        if (method == null)
        {
            return true;
        }
        // Harmony.GetPatchInfo is costly and this runs per hook call (hundreds of thousands of times when cloning
        // thousands of cards): results are reused for a short while, read without a lock, so a mod patching later is
        // still seen within RecheckAfterMs.
        long now = Environment.TickCount64;
        if (_patchedByOthers.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        IEnumerable<Patch> others = info == null ? Enumerable.Empty<Patch>()
            : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Where(p => p.owner != ModEntry.Harmony.Id);
        bool patched = Record(caller, method, others);
        _patchedByOthers[method] = (patched, now);
        return patched;
    }

    /// <summary>
    /// Like <see cref="IsPatchedByOthers(MethodBase?, string)"/>, except for these other mods' postfixes, whose effect
    /// the caller reproduces (checked by fingerprint).
    /// </summary>
    public static bool IsPatchedByOthers(MethodBase? method, MethodInfo[] allowedPostfixes, [CallerFilePath] string caller = "")
    {
        if (method == null)
        {
            return true;
        }
        long now = Environment.TickCount64;
        if (_patchedBeyondAllowed.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        IEnumerable<Patch> others = info == null ? Enumerable.Empty<Patch>()
            : info.Prefixes.Concat(info.Transpilers).Concat(info.Finalizers).Where(p => p.owner != ModEntry.Harmony.Id)
                .Concat(info.Postfixes.Where(p => p.owner != ModEntry.Harmony.Id
                    && !allowedPostfixes.Any(a => a.MethodHandle == p.PatchMethod.MethodHandle)));
        bool patched = Record(caller, method, others);
        _patchedBeyondAllowed[method] = (patched, now);
        return patched;
    }

    /// <summary>Other mods' patches on a method a patch backs off from, keyed by (patch name, method).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, MethodBase Method), string[]> _conflicts = new();

    private static bool Record(string caller, MethodBase method, IEnumerable<Patch> others)
    {
        string[] mods = others.Select(ModName).Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string patch = System.IO.Path.GetFileNameWithoutExtension(caller.Replace('\\', '/'));
        if (mods.Length == 0)
        {
            _conflicts.TryRemove((patch, method), out _);
            return false;
        }
        if (!_conflicts.TryGetValue((patch, method), out string[]? known) || !known.SequenceEqual(mods))
        {
            _conflicts[(patch, method)] = mods;
            MegaCrit.Sts2.Core.Logging.Log.Info($"[{ModEntry.Id}] {patch}: {method.DeclaringType?.Name}.{method.Name} is also patched by {string.Join(", ", mods)}: normal game for that part");
        }
        return true;
    }

    /// <summary>The loaded mod whose assembly holds the patch method (its display name), else the Harmony id.</summary>
    private static string ModName(Patch patch)
    {
        Assembly? assembly = patch.PatchMethod?.DeclaringType?.Assembly;
        if (assembly != null && assembly != typeof(PatchSafety).Assembly)
        {
            foreach (MegaCrit.Sts2.Core.Modding.Mod mod in MegaCrit.Sts2.Core.Modding.ModManager.Mods)
            {
                if (mod.assembly == assembly)
                {
                    return mod.manifest?.name ?? mod.manifest?.id ?? patch.owner;
                }
            }
        }
        return patch.owner;
    }

    /// <summary>The mods other patches come from, for the methods these patches currently back off from.</summary>
    public static IReadOnlyList<string> ConflictingMods(IEnumerable<string> patches)
    {
        var names = new HashSet<string>(patches);
        return _conflicts.Where(entry => names.Contains(entry.Key.Patch)).SelectMany(entry => entry.Value)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const long RecheckAfterMs = 2000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (bool Patched, long CheckedAt)> _patchedByOthers = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (bool Patched, long CheckedAt)> _patchedBeyondAllowed = new();
}
