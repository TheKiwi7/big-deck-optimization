using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using BigDeckOptimization.Patches;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Test of <see cref="PatchGuard"/> (script command <c>faultprobe</c>), on a game helper nothing else of this mod patches
/// (<c>StringHelper.Slugify</c>): an Apply failing after patching leaves nothing installed; a prefix throwing into the
/// game gets its patch faulted and removed, after which the game method runs unmodified. Each check logs OK or FAILED.
/// </summary>
public static class FaultProbe
{
    public const string Name = "FaultProbe";

    private static readonly MethodInfo _target = AccessTools.Method(typeof(StringHelper), nameof(StringHelper.Slugify));

    private static bool _armed;

    /// <summary>Runs both checks; <paramref name="nextFrames"/> lets deferred removals happen.</summary>
    public static async Task<string> Run(Func<Task> nextFrames)
    {
        string apply = CheckFailingApply();
        // Installed before the first await, like the others (the tooling's patches, not the optimizations').
        Harmony foreign = FaultProbeSwallow.Arm();
        string runtime = await CheckThrowingPrefix(nextFrames);
        string swallowed = await FaultProbeSwallow.Check(foreign, nextFrames);
        return $"failing Apply: {apply} | throwing prefix: {runtime} | swallowing finalizer: {swallowed}";
    }

    private static string CheckFailingApply()
    {
        ModEntry.TryApply(Name + ".Apply", harmony =>
        {
            PatchGuard.Patch(harmony, _target, prefix: new HarmonyMethod(typeof(FaultProbe), nameof(Prefix)));
            throw new InvalidOperationException("simulated failure after patching");
        });
        return OurPatches() == 0 && ModEntry.NotApplied(Name + ".Apply") ? "OK: nothing left installed" : $"FAILED: {OurPatches()} patch(es) left";
    }

    private static async Task<string> CheckThrowingPrefix(Func<Task> nextFrames)
    {
        ModEntry.TryApply(Name, harmony => PatchGuard.Patch(harmony, _target, prefix: new HarmonyMethod(typeof(FaultProbe), nameof(Prefix))));
        if (OurPatches() != 2)
        {
            return $"FAILED: expected prefix + guard, found {OurPatches()} patch(es)";
        }
        string first;
        _armed = true;
        try
        {
            StringHelper.Slugify("Fault Probe");
            first = "no exception";
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith(Name))
        {
            first = "threw";
        }
        finally
        {
            _armed = false;
        }
        if (first != "threw")
        {
            return "FAILED: the armed prefix didn't throw";
        }
        await nextFrames();
        _armed = true;
        try
        {
            string slug = StringHelper.Slugify("Fault Probe");
            return OurPatches() == 0 && PatchSafety.IsFaulted(Name)
                ? $"OK: faulted, removed, then unmodified game code ran ({slug})"
                : $"FAILED: {OurPatches()} patch(es) left, faulted {PatchSafety.IsFaulted(Name)}";
        }
        catch (InvalidOperationException)
        {
            return "FAILED: the prefix still ran after the fault";
        }
        finally
        {
            _armed = false;
        }
    }

    /// <summary>This mod's patches on the target, guard included.</summary>
    private static int OurPatches() => OurPatches(_target);

    internal static int OurPatches(MethodBase target)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(target);
        return info == null ? 0 : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
            .Count(p => PatchGuard.IsOurs(p.owner));
    }

    private static void Prefix()
    {
        if (_armed)
        {
            throw new InvalidOperationException(Name + ": simulated error inside a patch");
        }
    }
}

/// <summary>
/// <see cref="FaultProbe"/>'s third check in a class of its own: PatchGuard faults every patch name registered from the same
/// outermost class at once, so with both probes in FaultProbe the throwing prefix's fault also took this probe's patch away
/// before it was checked ("found 0 patch(es)").
/// </summary>
internal static class FaultProbeSwallow
{
    private static readonly MethodInfo _swallowTarget = AccessTools.Method(typeof(StringHelper), nameof(StringHelper.Unslugify));
    private const string SwallowName = FaultProbe.Name + ".Swallowed";
    private static bool _armedSwallow;

    /// <summary>
    /// Another mod's finalizer returning null (swallowing every exception), registered before this mod's patch on the same
    /// method (so Harmony would run it first at the same priority): the guard runs first of the finalizers (Priority.First) and
    /// still sees the prefix's throw, so the patch is faulted although the game's caller saw no exception.
    /// </summary>
    internal static Harmony Arm()
    {
        var foreign = new Harmony(ForeignPatches.IdPrefix + "swallow-finalizer");
        foreign.Patch(_swallowTarget, finalizer: new HarmonyMethod(typeof(FaultProbeSwallow), nameof(Swallow)));
        ModEntry.TryApply(SwallowName, harmony => PatchGuard.Patch(harmony, _swallowTarget, prefix: new HarmonyMethod(typeof(FaultProbeSwallow), nameof(SwallowedPrefix))));
        return foreign;
    }

    internal static async Task<string> Check(Harmony foreign, Func<Task> nextFrames)
    {
        try
        {
            if (FaultProbe.OurPatches(_swallowTarget) != 2)
            {
                return $"FAILED: expected prefix + guard, found {FaultProbe.OurPatches(_swallowTarget)} patch(es)";
            }
            _armedSwallow = true;
            try
            {
                StringHelper.Unslugify("fault-probe");
            }
            catch (InvalidOperationException)
            {
                return "FAILED: the foreign finalizer didn't swallow the exception (nothing tested)";
            }
            finally
            {
                _armedSwallow = false;
            }
            await nextFrames();
            return PatchSafety.IsFaulted(SwallowName) && FaultProbe.OurPatches(_swallowTarget) == 0
                ? "OK: faulted and removed though another mod's finalizer swallowed the exception"
                : $"FAILED: faulted {PatchSafety.IsFaulted(SwallowName)}, {FaultProbe.OurPatches(_swallowTarget)} patch(es) of this mod left";
        }
        finally
        {
            foreign.UnpatchAll(foreign.Id);
        }
    }

    private static Exception? Swallow(Exception? __exception) => null;

    private static void SwallowedPrefix()
    {
        if (_armedSwallow)
        {
            throw new InvalidOperationException(SwallowName + ": simulated error inside a patch");
        }
    }
}
