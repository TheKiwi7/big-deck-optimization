using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game records each combat as a replay (<c>replays/latest.mcr</c>, written at combat end, for debugging): its actions
/// and, for every checksum, the full combat state. With huge decks that keeps every state of the combat alive: the heap
/// grew by ~100 MB per turn at 20,000 combat cards, every collection got slower, and combat end serialized all of it.
///
/// Above <see cref="MaxCards"/> combat cards, checksums are left out of the recording, each with its state (the id and
/// context alone would be useless: playing a replay back checks each recorded checksum by hashing its state, and logs
/// "Replay state diverged" for an id it doesn't find); the actions, choices and hooks still are recorded. The replay is a
/// local debugging file no game logic reads (checksums compare the last 20 states, kept apart by <c>ChecksumTracker</c>,
/// which also dumps them on a divergence), so results don't change.
/// </summary>
public static class ReplayStateSkip
{
    public const string Name = "ReplayStateSkip";

    /// <summary>
    /// v0.107.1, v0.111, v0.113.x <c>CombatReplayWriter.RecordChecksum</c>: in a combat, while enabled, adds to the recording, or throws
    /// when nothing is being recorded (<c>RecordInitialState</c> not called); nothing else.
    /// </summary>
    private const string RecordChecksumFingerprint = "EB218CAA224E4EC5";

    private const int MaxCards = 1000;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Skipped { get; private set; }

    private static MethodInfo? _record;

    public static void Apply(Harmony harmony)
    {
        _record = AccessTools.Method(typeof(CombatReplayWriter), "RecordChecksum") ?? throw new MissingMethodException(nameof(CombatReplayWriter), "RecordChecksum");
        PatchSafety.RequireFingerprint(_record, RecordChecksumFingerprint);
        PatchGuard.Patch(harmony, _record, prefix: new HarmonyMethod(typeof(ReplayStateSkip), nameof(Prefix)));
        Applied = true;
        PatchSafety.Watch(Name, _record); // What Prefix asks about (display only).
    }

    private static bool Prefix(CombatReplayWriter __instance, NetFullCombatState fullCombatState)
    {
        // Only where the game would add to the recording: disabled or out of combat it does nothing (nothing to count); not
        // recording, its own error stays.
        // No tolerance: the entry is left out, its patches wouldn't run.
        if (!Enabled || !__instance.IsEnabled || !__instance.IsRecordingReplay || !CombatManager.Instance.IsInProgress || PatchSafety.IsPatchedByOthers(_record))
        {
            return true;
        }
        try
        {
            int cards = 0;
            foreach (NetFullCombatState.PlayerState player in fullCombatState.Players)
            {
                foreach (NetFullCombatState.CombatPileState pile in player.piles)
                {
                    cards += pile.cards.Count;
                }
            }
            if (cards <= MaxCards)
            {
                return true;
            }
            Skipped++;
            return false;
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, states are recorded as usual: {e}");
            return true;
        }
    }
}
