using System;
using BigDeckOptimization.Patches;
using Godot;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Script <c>startsets</c> (P1 of 2026-11-20): RelicFlashMerge's and CardFlightLimit's game-code sets as the session uses them
/// (built in the background while the game loaded, or on the main thread when that build wasn't done) against a build made now on
/// the main thread: every method, its order, the virtual methods, delegates, fingerprints, the strict members and the lazily
/// built instances' guards. A difference fails the line. Logs where the sets in use came from (<c>STARTSETS ... from the
/// background</c>: the background path really ran). A part not applied is said so, not failed.
/// </summary>
public static partial class AutoTest
{
    [ScriptCommand("startsets")]
    private static void StartSets(SceneTree tree, string arg)
    {
        Say("  STARTSETS " + (RelicFlashMerge.Applied ? RelicFlashMerge.BenchCompareSets() : $"{RelicFlashMerge.Name} not applied"));
        Say("  STARTSETS " + (CardFlightLimit.Applied ? CardFlightLimit.BenchCompareSets() : $"{CardFlightLimit.Name} not applied"));
    }

    /// <summary>
    /// Script <c>launchearly</c> (P2 of 2026-11-20): the session was launched with <c>--bdearly</c> (the launchers add it for this
    /// line: parlib.ps1's Get-ScenarioLaunchArgs), so the script started as soon as the main menu showed, InlineGuard's rebuilds
    /// held until a run is under way (ModEntry). Fails when the argument is missing (the session would test nothing early). Logs the step reached.
    /// </summary>
    [ScriptCommand("launchearly")]
    private static void LaunchEarly(SceneTree tree, string arg)
    {
        if (!ModEntry.LaunchedWith(ModEntry.EarlyArg))
        {
            throw new InvalidOperationException($"launched without {ModEntry.EarlyArg}: the script didn't start early");
        }
        Say($"  LAUNCHEARLY script started at the menu, InlineGuard recompiled {InlineGuard.Recompiled} so far");
    }
}
