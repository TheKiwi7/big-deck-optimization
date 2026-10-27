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
        string runtime = await CheckThrowingPrefix(nextFrames);
        return $"failing Apply: {apply} | throwing prefix: {runtime}";
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
    private static int OurPatches()
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(_target);
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
