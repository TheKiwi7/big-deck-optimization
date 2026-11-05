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
///
/// The window must never outlive its combat: outside one, the game has waits that nothing else paces. <c>NTransformPreview</c> loops on
/// <c>Cmd.Wait</c> (only its Instant branch uses <c>Task.Delay</c>), so an instant wait there never gives the frame back: a frozen game,
/// then a crash. A won combat is entered again as pre-finished when a run is continued, and that never reaches a first turn: no window
/// then. <c>CombatManager.Reset</c> (the room left, the run left) closes one still open.
/// </summary>
public static class FastCombatStart
{
    public const string Name = "FastCombatStart";

    /// <summary>v0.107.1 and v0.111: the state machines of <c>Cmd.Wait</c> and <c>Cmd.CustomScaledWait</c>.</summary>
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

    public static long SkippedWaits { get; private set; }
    public static double SkippedSeconds { get; private set; }

    private static bool _window;

    public static void Apply(Harmony harmony)
    {
        MethodInfo wait = AccessTools.Method(typeof(Cmd), nameof(Cmd.Wait), new[] { typeof(float), typeof(CancellationToken), typeof(bool) })
            ?? throw new MissingMethodException("Cmd.Wait");
        MethodInfo scaledWait = AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait)) ?? throw new MissingMethodException("Cmd.CustomScaledWait");
        // Skipping them must lose nothing but the time: their code only waits.
        PatchSafety.RequireFingerprint(AccessTools.AsyncMoveNext(wait), WaitFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.AsyncMoveNext(scaledWait), ScaledWaitFingerprint);
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
        PatchGuard.Patch(harmony, wait, prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(WaitPrefix)));
        PatchGuard.Patch(harmony, scaledWait, prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(ScaledWaitPrefix)));
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
