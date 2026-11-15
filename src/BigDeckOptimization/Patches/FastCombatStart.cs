using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Visual concession. With hundreds of relics, entering a combat plays every relic's effect one after the other, each
/// with its own animation waits (Vulnerable, Weak, Block...): minutes before the first turn.
///
/// While a combat loads, from entering the combat room until its first turn starts (room entry and "start of combat"
/// effects), the game's animation waits (<c>Cmd.Wait</c>, <c>Cmd.CustomScaledWait</c>) return at once, exactly like
/// the game's own Instant fast mode does. The first turn (its start-of-turn effects, the draw) and everything after
/// keep the game's pace: the mod removes lag, it doesn't speed the game up. (Before v0.6.2 the window only closed on a
/// wait during the player's play phase, which in solo often never came: whole combats ran without waits.)
/// Waits never change game state (each player already picks their own animation speed), so results stay identical.
/// In solo, only when the players have at least <see cref="RelicThreshold"/> relics in total (setting, 0 = always).
///
/// <see cref="InstantCombats"/> (option, off by default): every wait of every combat instant, this player only.
///
/// Always on in co-op, whatever the relics (its own option and threshold then ignored; the master switch and faults
/// still apply: <c>bdopt off</c> then needs a confirmation, the menu's master switch is greyed out, a fault is logged as
/// such, see <see cref="Config.Settings.InCoopRun"/>): the game drops combat
/// actions that reach a player still starting the combat, so a player starting ~70 s later than the others (option off)
/// would desync or freeze the run as soon as someone plays (`coopsolo1/2` scenarios).
///
/// The window must never outlive its combat: outside one, the game has waits that nothing else paces. On v0.107.1 and v0.111
/// <c>NTransformPreview</c> (and the PunchOff event) loop on <c>Cmd.Wait</c> (only the preview's Instant branch uses <c>Task.Delay</c>), so an
/// instant wait there never gives the frame back: a frozen game, then a crash. On v0.113 they call <c>Cmd.TryWait</c>, which ends the loop
/// when the wait completed at once: the preview would stop cycling instead (no game caller opens it during a combat). A won combat is
/// entered again as pre-finished when a run is continued, and that never reaches a first turn: no window then.
/// <c>CombatManager.Reset</c> (the room left, the run left) closes one still open.
/// </summary>
public static class FastCombatStart
{
    public const string Name = "FastCombatStart";

    /// <summary>v0.107.1, v0.111.0, v0.113.0 and v0.113.1: the state machines of <c>Cmd.Wait</c> and <c>Cmd.CustomScaledWait</c>.</summary>
    private const string WaitFingerprint = "3A639D986AA66976";
    private const string ScaledWaitFingerprint = "D5717F5FE3E04BAA";

    public static bool Enabled { get; set; } = true;

    /// <summary>The option, or in co-op the master switch alone (every player must start at the same pace).</summary>
    private static bool On => Enabled
        || (Config.Settings.Master && !PatchSafety.IsFaulted(Name) && RunManager.Instance is { IsSingleplayerOrFakeMultiplayer: false });

    /// <summary>Total relics across players from which the combat start is sped up in solo (setting, 0 = always).</summary>
    public static int RelicThreshold { get; set; }

    /// <summary>Option, off by default: the animation waits of whole combats are instant (a visual choice of this player).</summary>
    public static bool InstantCombats { get; set; }

    /// <summary>Waits of the combat-start window the game would really have waited for (not at Instant speed, not in its non-interactive mode, a positive time).</summary>
    public static long SkippedWaits { get; private set; }
    public static double SkippedSeconds { get; private set; }

    private static bool _window;

    public static void Apply(Harmony harmony)
    {
        MethodInfo wait = AccessTools.Method(typeof(Cmd), nameof(Cmd.Wait), new[] { typeof(float), typeof(CancellationToken), typeof(bool) })
            ?? throw new MissingMethodException("Cmd.Wait");
        MethodInfo scaledWait = AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait)) ?? throw new MissingMethodException("Cmd.CustomScaledWait");
        // Skipping them must lose nothing but the time: their code only waits. One that changed only stays the game's. No back-off from
        // other mods' patches on them (Limitless, SpeedX): a wait holds no logic to keep, and the combat start must stay fast in co-op.
        bool waitKnown = Known(wait, WaitFingerprint);
        bool scaledKnown = Known(scaledWait, ScaledWaitFingerprint);
        if (!waitKnown && !scaledKnown)
        {
            throw new PatchSkippedException("neither wait has the known code");
        }
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CombatRoom), nameof(CombatRoom.EnterInternal)) ?? throw new MissingMethodException("CombatRoom.EnterInternal"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(OpenWindow)));
        // The first turn starting ends the loading (by name: game v0.111 added a parameter).
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CombatManager), "StartTurn") ?? throw new MissingMethodException("CombatManager.StartTurn"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(TurnStarting)));
        // Without it only leaving a combat while it loads keeps the window open until the next combat.
        if (AccessTools.Method(typeof(CombatManager), nameof(CombatManager.Reset)) is { } reset)
        {
            PatchGuard.Patch(harmony, reset, postfix: new HarmonyMethod(typeof(FastCombatStart), nameof(CombatReset)));
        }
        else
        {
            Log.Info($"[{ModEntry.Id}] {Name}: CombatManager.Reset not found");
        }
        if (waitKnown)
        {
            PatchGuard.Patch(harmony, wait, prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(WaitPrefix)));
        }
        if (scaledKnown)
        {
            PatchGuard.Patch(harmony, scaledWait, prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(ScaledWaitPrefix)));
        }
    }

    private static bool Known(MethodInfo method, string fingerprint)
    {
        if (AccessTools.AsyncMoveNext(method) is not { } body)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: Cmd.{method.Name} is not async any more: left vanilla");
            return false;
        }
        string actual = MethodFingerprint.Of(body);
        if (PatchSafety.FingerprintMatches(actual, fingerprint))
        {
            return true;
        }
        Log.Info($"[{ModEntry.Id}] {Name}: Cmd.{method.Name} changed (fingerprint {actual}, expected {fingerprint}): left vanilla");
        return false;
    }

    private static void OpenWindow(CombatRoom __instance)
    {
        try
        {
            int relics = RunManager.Instance.DebugOnlyGetState()?.Players.Sum(p => p.Relics.Count) ?? 0;
            bool coop = RunManager.Instance is { IsSingleplayerOrFakeMultiplayer: false };
            _window = On && !__instance.IsPreFinished && (coop || relics >= RelicThreshold);
            SkippedWaits = 0;
            SkippedSeconds = 0;
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>The combat's first turn starts: the loading is over (later turns find the window closed).</summary>
    private static void TurnStarting()
    {
        try
        {
            if (_window)
            {
                CloseWindow();
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>The room or the run was left, possibly before the first turn.</summary>
    private static void CombatReset() => _window = false;

    private static void CloseWindow()
    {
        _window = false;
        Log.Info($"[{ModEntry.Id}] {Name}: combat start skipped {SkippedWaits} animation waits ({SkippedSeconds:F1} s)");
    }

    /// <summary>True while a combat loads (until its first turn starts or it ends).</summary>
    private static bool Loading()
    {
        if (!_window)
        {
            return false;
        }
        if (!On)
        {
            _window = false;
            return false;
        }
        CombatManager combat = CombatManager.Instance;
        if (combat.IsInProgress && combat.IsOverOrEnding)
        {
            CloseWindow();
            return false;
        }
        return true;
    }

    /// <summary>
    /// More than any combat start asks in one frame. Past it, code is looping on a wait to pace itself (the transform preview on v0.107.1 and
    /// v0.111, a mod's on any version): an instant wait there would never give the frame back, so this one really waits, and the next frame
    /// starts over.
    /// </summary>
    private const int MaxSkipsPerFrame = 2000;
    private static ulong _skipFrame;
    private static int _skipsThisFrame;

    private static bool LoopingOnWaits()
    {
        ulong frame = Godot.Engine.GetProcessFrames();
        if (frame != _skipFrame)
        {
            _skipFrame = frame;
            _skipsThisFrame = 0;
        }
        return ++_skipsThisFrame > MaxSkipsPerFrame;
    }

    /// <summary>
    /// Whether this wait returns at once: combat loading (counted), or any combat wait with <see cref="InstantCombats"/>. The raw times, not
    /// a closure: this runs for every wait of the game.
    /// </summary>
    private static bool Skip(float fastSeconds, float standardSeconds, bool scaled)
    {
        if ((_window || InstantCombats) && LoopingOnWaits())
        {
            return false;
        }
        if (Loading())
        {
            CountSkipped(fastSeconds, standardSeconds, scaled);
            return true;
        }
        return InstantCombats && CombatManager.Instance.IsInProgress;
    }

    /// <summary>Only what the game would have waited (<c>Cmd.Wait</c>'s own early returns): a wait it skips anyway saved nothing.</summary>
    private static void CountSkipped(float fastSeconds, float standardSeconds, bool scaled)
    {
        if (NonInteractiveMode.IsActive)
        {
            return;
        }
        FastModeType mode = SaveManager.Instance.PrefsSave?.FastMode ?? FastModeType.Normal;
        float seconds = scaled && mode == FastModeType.Normal ? standardSeconds : fastSeconds;
        if (mode == FastModeType.Instant || !(seconds > 0f))
        {
            return;
        }
        SkippedWaits++;
        SkippedSeconds += seconds;
    }

    private static bool WaitPrefix(float seconds, ref Task __result)
    {
        try
        {
            if (!Skip(seconds, seconds, scaled: false))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    private static bool ScaledWaitPrefix(float fastSeconds, float standardSeconds, ref Task __result)
    {
        try
        {
            if (!Skip(fastSeconds, standardSeconds, scaled: true))
            {
                return true;
            }
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    private static void Fail(Exception e)
    {
        Enabled = false;
        InstantCombats = false;
        _window = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
        if (Config.Settings.InCoopRun)
        {
            // The other players keep their fast start: this one now starts each combat at the game's pace, far later.
            Log.Error($"[{ModEntry.Id}] {Name}: off during a co-op run. {Config.Settings.CoopOffWarning} Restarting the game turns it back on.");
        }
    }
}
