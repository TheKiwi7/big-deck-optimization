using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Removes this mod's Harmony patches when they could hurt the game, so that part runs the unmodified game code:
/// <list type="bullet">
/// <item>an Apply that fails halfway: the patches it already installed are removed (<see cref="Diff"/> / <see cref="Remove"/>);</item>
/// <item>code of a patch class that throws into the game at runtime (an exception leaving a patched method through
/// one of its frames): that patch is faulted (off for the session, see <see cref="PatchSafety.Fault"/>) and all its
/// Harmony patches are removed on the next frame. Patches catch their own errors; this covers what they can't, e.g.
/// a patch method the JIT can't compile, which throws before its own Enabled check and try/catch run.</item>
/// </list>
/// Each patched method gets <see cref="Guard"/> as a finalizer: it only watches (void, so Harmony rethrows the
/// exception unchanged) and costs one call per call of the method.
/// </summary>
public static class PatchGuard
{
    public const string Name = "PatchGuard";

    public readonly record struct Installed(MethodBase Original, MethodInfo Patch);

    private static readonly MethodInfo _guard = AccessTools.Method(typeof(PatchGuard), nameof(Guard));

    private static readonly object _lock = new();

    /// <summary>Patch class (outermost type) -> names of the patches applied from it (<see cref="ModEntry.TryApply"/>).</summary>
    private static readonly Dictionary<Type, HashSet<string>> _names = new();

    private static readonly HashSet<MethodBase> _guarded = new();
    private static readonly HashSet<Type> _removed = new();

    /// <summary>This mod's patches (guards excluded), to diff before and after an Apply.</summary>
    public static HashSet<Installed> Snapshot()
    {
        var installed = new HashSet<Installed>();
        foreach (MethodBase original in ModEntry.Harmony.GetPatchedMethods())
        {
            HarmonyLib.Patches? info = Harmony.GetPatchInfo(original);
            if (info == null)
            {
                continue;
            }
            foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
            {
                if (patch.owner == ModEntry.Harmony.Id && patch.PatchMethod != _guard)
                {
                    installed.Add(new Installed(original, patch.PatchMethod));
                }
            }
        }
        return installed;
    }

    public static List<Installed> Diff(HashSet<Installed> before) => Snapshot().Where(p => !before.Contains(p)).ToList();

    /// <summary>After a successful Apply: the patch class's code is watched on every method it patched.</summary>
    public static void Register(string name, Type patchClass, IEnumerable<Installed> installed)
    {
        lock (_lock)
        {
            Type type = Outermost(patchClass);
            if (!_names.TryGetValue(type, out HashSet<string>? names))
            {
                _names[type] = names = new HashSet<string>();
            }
            names.Add(name);
        }
        foreach (MethodBase original in installed.Select(p => p.Original).Distinct())
        {
            Watch(original);
        }
    }

    /// <summary>Adds the guard to a method this mod patched (for patches installed later than their Apply).</summary>
    public static void Watch(MethodBase original)
    {
        lock (_lock)
        {
            if (!_guarded.Add(original))
            {
                return;
            }
        }
        try
        {
            ModEntry.Harmony.Patch(original, finalizer: new HarmonyMethod(_guard));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: could not watch {original.DeclaringType?.Name}.{original.Name}: {e.Message}");
        }
    }

    /// <summary>Removes these patches, and the guard of each method left without patches of this mod.</summary>
    public static void Remove(IEnumerable<Installed> patches)
    {
        var originals = new HashSet<MethodBase>();
        foreach (Installed p in patches)
        {
            originals.Add(p.Original);
            try
            {
                ModEntry.Harmony.Unpatch(p.Original, p.Patch);
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.Id}] {Name}: could not remove {p.Patch.DeclaringType?.Name}.{p.Patch.Name} from {p.Original.DeclaringType?.Name}.{p.Original.Name}: {e}");
            }
        }
        HashSet<MethodBase> stillPatched = Snapshot().Select(p => p.Original).ToHashSet();
        foreach (MethodBase original in originals.Where(o => !stillPatched.Contains(o)))
        {
            lock (_lock)
            {
                if (!_guarded.Remove(original))
                {
                    continue;
                }
            }
            try
            {
                ModEntry.Harmony.Unpatch(original, _guard);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: could not remove the guard of {original.DeclaringType?.Name}.{original.Name}: {e.Message}");
            }
        }
    }

    /// <summary>Runs after every call of a patched method: kept to a null check the JIT can inline (hot methods).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Guard(Exception? __exception)
    {
        if (__exception != null)
        {
            Check(__exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Check(Exception exception)
    {
        try
        {
            FaultAndRemove(exception);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: {e}");
        }
    }

    /// <summary>Faults and removes every patch class the exception went through, from where it was thrown to here.</summary>
    private static void FaultAndRemove(Exception exception)
    {
        var hit = new List<(Type Type, string[] Names)>();
        foreach (StackFrame frame in new StackTrace(exception).GetFrames())
        {
            if (frame.GetMethod()?.DeclaringType is not { } declaring || declaring.Assembly != typeof(PatchGuard).Assembly)
            {
                continue;
            }
            Type type = Outermost(declaring);
            lock (_lock)
            {
                if (_names.TryGetValue(type, out HashSet<string>? names) && _removed.Add(type))
                {
                    hit.Add((type, names.ToArray()));
                }
            }
        }
        if (hit.Count == 0)
        {
            return;
        }
        foreach ((Type type, string[] names) in hit)
        {
            foreach (string name in names)
            {
                PatchSafety.Fault(name);
            }
            Log.Error($"[{ModEntry.Id}] {string.Join(", ", names)}: error inside the game's code, turned off and removed for this session (normal game from now on): {exception}");
        }
        Config.Settings.ApplyAll();
        // Unpatching rebuilds the method that is running right now: done once it has returned.
        Godot.Callable.From(() =>
        {
            foreach ((Type type, string[] _) in hit)
            {
                Remove(Snapshot().Where(p => p.Patch.DeclaringType != null && Outermost(p.Patch.DeclaringType) == type));
            }
        }).CallDeferred();
    }

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType != null)
        {
            type = type.DeclaringType;
        }
        return type;
    }
}
