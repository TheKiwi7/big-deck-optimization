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
/// From entering a combat room until the local player can play cards, the game's animation waits
/// (<c>Cmd.Wait</c>, <c>Cmd.CustomScaledWait</c>) return at once, exactly like the game's own Instant fast mode does.
/// Waits never change game state (each player already picks their own animation speed), so results stay identical.
/// In solo, only when the players have at least <see cref="RelicThreshold"/> relics in total (setting, 0 = always).
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

    public static long SkippedWaits { get; private set; }
    public static double SkippedSeconds { get; private set; }

    private static bool _window;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CombatRoom), nameof(CombatRoom.EnterInternal)) ?? throw new MissingMethodException("CombatRoom.EnterInternal"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(OpenWindow)));
        harmony.Patch(AccessTools.Method(typeof(Cmd), nameof(Cmd.Wait), new[] { typeof(float), typeof(CancellationToken), typeof(bool) })
                ?? throw new MissingMethodException("Cmd.Wait"),
            prefix: new HarmonyMethod(typeof(FastCombatStart), nameof(WaitPrefix)));
        harmony.Patch(AccessTools.Method(typeof(Cmd), nameof(Cmd.CustomScaledWait)) ?? throw new MissingMethodException("Cmd.CustomScaledWait"),
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

    /// <summary>True while entering a combat, until the local player's first play phase (or the combat ends).</summary>
    private static bool Active()
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
        if (combat.IsInProgress)
        {
            Player? me = RunManager.Instance.DebugOnlyGetState() is { } state ? LocalContext.GetMe(state) : null;
            if (combat.IsOverOrEnding || me?.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                _window = false;
                Log.Info($"[{ModEntry.Id}] {Name}: combat start skipped {SkippedWaits} animation waits ({SkippedSeconds:F1} s)");
                return false;
            }
        }
        return true;
    }

    private static bool WaitPrefix(float seconds, ref Task __result)
    {
        try
        {
            if (!Active())
            {
                return true;
            }
            SkippedWaits++;
            SkippedSeconds += Math.Max(0, seconds);
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
            if (!Active())
            {
                return true;
            }
            SkippedWaits++;
            SkippedSeconds += SaveManager.Instance.PrefsSave.FastMode == FastModeType.Normal ? standardSeconds : fastSeconds;
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
        _window = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
    }
}
