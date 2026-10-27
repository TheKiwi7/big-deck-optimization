using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
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
/// still apply): the game drops combat
/// actions that reach a player still starting the combat, so a player starting ~70 s later than the others (option off)
/// would desync or freeze the run as soon as someone plays (`coopsolo1/2` scenarios).
/// </summary>
public static class FastCombatStart
{
    public const string Name = "FastCombatStart";

    public static bool Enabled { get; set; } = true;

    /// <summary>The option, or in co-op the master switch alone (every player must start at the same pace).</summary>
    private static bool On => Enabled
        || (Config.Settings.Master && !PatchSafety.IsFaulted(Name) && RunManager.Instance is { IsSingleplayerOrFakeMultiplayer: false });

    /// <summary>Total relics across players from which the combat start is sped up in solo (setting, 0 = always).</summary>
    public static int RelicThreshold { get; set; }

    /// <summary>Option, off by default: the animation waits of whole combats are instant (a visual choice of this player).</summary>
    public static bool InstantCombats { get; set; }

    public static long SkippedWaits { get; private set; }
    public static double SkippedSeconds { get; private set; }

    private static bool _window;

    public static void Apply(Harmony harmony)
    {
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CombatRoom), nameof(CombatRoom.EnterInternal)) ?? throw new MissingMethodException("CombatRoom.EnterInternal"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(OpenWindow)));
        // The first turn starting ends the loading (by name: game v0.111 added a parameter).
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CombatManager), "StartTurn") ?? throw new MissingMethodException("CombatManager.StartTurn"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(TurnStarting)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(Cmd), nameof(Cmd.Wait), new[] { typeof(float), typeof(CancellationToken), typeof(bool) })
                ?? throw new MissingMethodException("Cmd.Wait"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(WaitPrefix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait)) ?? throw new MissingMethodException("Cmd.CustomScaledWait"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(ScaledWaitPrefix)));
    }

    private static void OpenWindow()
    {
        try
        {
            int relics = RunManager.Instance.DebugOnlyGetState()?.Players.Sum(p => p.Relics.Count) ?? 0;
            bool coop = RunManager.Instance is { IsSingleplayerOrFakeMultiplayer: false };
            _window = On && (coop || relics >= RelicThreshold);
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

    /// <summary>Whether this wait returns at once: combat loading (counted), or any combat wait with <see cref="InstantCombats"/>.</summary>
    private static bool Skip(Func<double> seconds)
    {
        if (Loading())
        {
            SkippedWaits++;
            SkippedSeconds += Math.Max(0, seconds());
            return true;
        }
        return InstantCombats && CombatManager.Instance.IsInProgress;
    }

    private static bool WaitPrefix(float seconds, ref Task __result)
    {
        try
        {
            if (!Skip(() => seconds))
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
            if (!Skip(() => SaveManager.Instance.PrefsSave.FastMode == FastModeType.Normal ? standardSeconds : fastSeconds))
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
    }
}
