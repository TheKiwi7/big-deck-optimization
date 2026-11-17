using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

public enum VerifyMode
{
    Off,
    /// <summary>The first snapshots of the session, then one in <see cref="SnapshotVerifier.SampleEvery"/>.</summary>
    Sample,
    All,
}

/// <summary>
/// Safety net for the snapshot optimizations: on sampled snapshots, rebuilds the snapshot the vanilla way and compares
/// both serializations byte for byte. On any difference the vanilla snapshot is used (so peers never see a divergent
/// checksum from us) and every snapshot optimization is turned off for the session.
///
/// Also, whatever the sampling: when <see cref="SnapshotCardReuse"/> or <see cref="RelicStateReuse"/> finds one of its reuses wrong
/// (or turns itself off) while a snapshot is built or its checksum computed, the part found wrong is off from then on, but that snapshot was partly made the same
/// way. It is built again (<see cref="Rebuilt"/>), or its checksum computed again (<see cref="Reserialized"/>), before anything uses it.
/// </summary>
public static class SnapshotVerifier
{
    public const string Name = "SnapshotVerifier";

    public const int AlwaysVerifyFirst = 3;
    public const int SampleEvery = 25;

    /// <summary>
    /// The vanilla rebuild is O(cards²): ~50–200 ms at 1,000 cards, ~10 s at 7,500. Verifications are spaced so they take
    /// at most this share of play time, and the "first snapshots" rule only applies while a verification is cheap.
    /// </summary>
    private const double MaxTimeShare = 0.01;
    private const double CheapMs = 250;
    private static long _nextAllowedTicks;
    private static double _lastVerifyMs;

    /// <summary>
    /// Above this many cards in combat (all players) a verification freezes the game noticeably (~0.2 s at 1,000 cards,
    /// ~1.7 s at 2,800, ~10 s at 7,500): skipped (user's choice). A verification over <see cref="GiveUpMs"/> also stops
    /// them for the session.
    /// </summary>
    public const int MaxCombatCards = 1000;
    private const double GiveUpMs = 1000;
    private static bool _gaveUp;
    private static bool _loggedSkip;

    public static int SkippedTooLarge { get; private set; }

    public static VerifyMode Mode { get; set; } = VerifyMode.Sample;

    public static int Snapshots { get; private set; }
    public static int Verified { get; private set; }
    public static int Mismatches { get; private set; }

    [ThreadStatic] private static bool _verifying;
    [ThreadStatic] private static PacketWriter? _optimizedWriter;
    [ThreadStatic] private static PacketWriter? _vanillaWriter;

    /// <summary>Snapshots built again, and checksums computed again, after a reuse was found wrong during them.</summary>
    public static int Rebuilt { get; private set; }
    public static int Reserialized { get; private set; }

    /// <summary>
    /// Reuses found wrong (or a reuse turned off) by the card and the relic reuse, and snapshots whose walk reuse the listener
    /// cache's net found wrong (<see cref="ListenerCache.EndSnapshotWalks"/>): what a snapshot or checksum is redone on.
    /// </summary>
    private static long Detections => SnapshotCardReuse.Detections + RelicStateReuse.Detections + ListenerCache.SnapshotDetections;

    /// <summary>FromRun's prefix and postfix are in place: the snapshot walk reuse's net can run (<see cref="NetApplied"/>).</summary>
    private static bool _fromRunPatched;

    /// <summary>The net of the snapshot walk reuse runs in this class's FromRun postfix: patched and not faulted.</summary>
    public static bool NetApplied => _fromRunPatched && !Faulted;

    /// <summary>
    /// Whether the checksum snapshot of <paramref name="runState"/> may reuse walk outputs (ListenerCache): its net in place, and
    /// no sampled re-check of this snapshot possible. The Sample mode's schedule depends on timing (a time budget, the cost of the
    /// last re-check), so one peer may re-check a snapshot another doesn't, and a re-check finding a difference the net missed
    /// would make only that peer use the game's snapshot (a desync) and turn its optimizations off (its later checksums differing
    /// at the same points). Above <see cref="MaxCombatCards"/> combat cards (every peer counts the same state) Sample never
    /// re-checks; Off never does; All (test tooling) re-checks every snapshot, an independent reference. Never throws.
    /// </summary>
    internal static bool WalkNetFor(IRunState runState)
    {
        try
        {
            return NetApplied && (Mode != VerifyMode.Sample || CombatCards(runState) > MaxCombatCards);
        }
        catch
        {
            return false;
        }
    }

    /// <summary><see cref="Detections"/> when the current snapshot, and the current checksum, started.</summary>
    [ThreadStatic] private static long _detectionsAtSnapshot;
    [ThreadStatic] private static long _detectionsAtChecksum;
    [ThreadStatic] private static bool _reserializing;

    /// <summary>
    /// A fault recorded under this name (PatchGuard, when one of these patches threw into the game) means these patches should be
    /// gone: if Harmony couldn't remove one, it does nothing.
    /// </summary>
    private static bool Faulted => PatchSafety.IsFaulted(Name);

    public static void Apply(Harmony harmony)
    {
        PatchGuard.Patch(harmony,
            Reflect.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun)),
            prefix: new HarmonyMethod(typeof(SnapshotVerifier), nameof(FromRunPrefix)),
            // First of FromRun's postfixes: another mod's postfix never sees a snapshot the walk reuse's net hasn't checked (or
            // rebuilt), and a walk it makes runs exact (the net ends the reuse).
            postfix: new HarmonyMethod(typeof(SnapshotVerifier), nameof(FromRunPostfix)) { priority = Priority.First },
            // After ListenerCache's scope finalizer (which says whether the snapshot threw with reused walk outputs).
            finalizer: new HarmonyMethod(typeof(SnapshotVerifier), nameof(FromRunFinalizer)) { priority = Priority.Last });
        _fromRunPatched = true;
        try
        {
            PatchGuard.Patch(harmony,
                Reflect.Method(typeof(ChecksumTracker), nameof(ChecksumTracker.GenerateChecksum), new[] { typeof(NetFullCombatState) }),
                prefix: new HarmonyMethod(typeof(SnapshotVerifier), nameof(ChecksumPrefix)),
                postfix: new HarmonyMethod(typeof(SnapshotVerifier), nameof(ChecksumPostfix)));
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: a checksum computed while card bits were found wrong isn't computed again: {e.Message}");
        }
    }

    private static void FromRunPrefix(IRunState runState)
    {
        if (!_verifying && !Faulted)
        {
            _detectionsAtSnapshot = Detections;
            // The state FromRun only reads, as it is before this snapshot, where a re-check may come (see ReCheckFromStart).
            _preSeal = null;
            try
            {
                if (Mode == VerifyMode.All || (Mode == VerifyMode.Sample && !_gaveUp && CombatCards(runState) <= MaxCombatCards))
                {
                    _preSeal = SnapshotSeal.Take(runState);
                }
            }
            catch
            {
                _preSeal = null;
            }
        }
    }

    /// <summary>The seal FromRunPrefix took for the snapshot being built (null: no re-check possible, or it couldn't be taken).</summary>
    [ThreadStatic] private static SnapshotSeal? _preSeal;

    /// <summary>Re-checks discarded: another mod's code changed what FromRun only reads, in a way an RNG put-back can't undo.</summary>
    public static int ReChecksDiscarded { get; private set; }
    private static bool _loggedDiscard;

    private static void Discard(string why)
    {
        ReChecksDiscarded++;
        if (!_loggedDiscard)
        {
            _loggedDiscard = true;
            Log.Info($"[{ModEntry.Id}] {Name}: a vanilla re-check was discarded ({why}): other mods' code changes what the state check reads "
                + "while it runs, so a second build can't be compared with the first; nothing turned off");
        }
    }

    private static void FromRunPostfix(IRunState runState, GameAction? justFinishedAction, ref NetFullCombatState __result)
    {
        if (_verifying)
        {
            return;
        }
        // First, before anything else reads the snapshot: the walk reuse's net (ListenerCache.EndSnapshotWalks, the outermost
        // snapshot only). A difference counts a detection and the snapshot is built again below, here, before FromRun returns it
        // to the game's checksum (ChecksumTracker.ObtainAndTrackChecksum: FromRun, then GenerateChecksum on what it returned).
        // Also when this class is faulted meanwhile: the walk reuse is gated on NetApplied, this is its only rebuild.
        bool walksWrong = ListenerCache.EndSnapshotWalks();
        if (!walksWrong && (ListenerCache.IsSuspended || Faulted))
        {
            return;
        }
        RebuildIfDetected(runState, justFinishedAction, ref __result, walksWrong);
        if (ListenerCache.IsSuspended || Faulted)
        {
            return;
        }
        // Also when the listener cache backs off or faulted: the card and relic reuse in snapshots still run then.
        // A snapshot (this is FromRun's postfix), not the vanilla reference (RunVanilla), with a snapshot optimization on
        // (also when the listener cache backs off, faulted or never applied: the card and relic reuse still run then).
        if (!(ListenerCache.IsInSnapshot || (SnapshotCardReuse.Applied && SnapshotCardReuse.Enabled) || (RelicStateReuse.Applied && RelicStateReuse.Enabled)))
        {
            return;
        }
        int index = Snapshots++;
        long now = Stopwatch.GetTimestamp();
        bool sampled = Mode == VerifyMode.All
            || (Mode == VerifyMode.Sample && (index == 0
                || (index < AlwaysVerifyFirst && _lastVerifyMs < CheapMs)
                || (index % SampleEvery == 0 && now >= _nextAllowedTicks)));
        if (!sampled)
        {
            return;
        }

        _verifying = true;
        try
        {
            // Inside the try: an exception counting the piles (a content mod's pile) would otherwise leave through this postfix into the
            // game's FromRun caller, and PatchGuard would fault the verifier.
            if (Mode == VerifyMode.Sample && (_gaveUp || CombatCards(runState) > MaxCombatCards))
            {
                SkippedTooLarge++;
                if (!_loggedSkip)
                {
                    _loggedSkip = true;
                    Log.Info($"[{ModEntry.Id}] {Name}: state too large for a vanilla re-check without freezing the game "
                        + $"(over {MaxCombatCards} combat cards or a re-check over {GiveUpMs / 1000:F0} s): verification skipped.");
                }
                return;
            }
            // The re-check runs other mods' walk postfixes again, unsealed: one drawing an RNG while its output is enumerated would
            // start it from RNGs the snapshot already advanced (a difference on this peer alone, its snapshot optimizations then
            // off: a desync recurring at every rejoin, index 0 being always re-checked). It starts from the state the snapshot
            // started from (the RNGs put back), and the RNGs are put back afterwards to what the snapshot left (the game's state
            // after FromRun). A change the RNGs don't explain can't be undone: the re-check is discarded, nothing turned off.
            SnapshotSeal? after = null;
            SnapshotSeal? before = _preSeal;
            _preSeal = null;
            if (before != null)
            {
                (bool rngs, string? other) = before.Diff(runState);
                if (other != null || (rngs && !SnapshotSeal.CanRestore))
                {
                    Discard(other ?? "an RNG drawn while the snapshot was built, which this game version can't put back");
                    return;
                }
                if (rngs)
                {
                    after = SnapshotSeal.Take(runState);
                    if (after == null || !before.RestoreRngs())
                    {
                        after?.RestoreRngs();
                        Discard("the RNGs couldn't be put back");
                        return;
                    }
                }
                else
                {
                    after = before;
                }
            }
            NetFullCombatState vanilla;
            try
            {
                vanilla = ListenerCache.RunVanilla(() => NetFullCombatState.FromRun(runState, justFinishedAction));
            }
            finally
            {
                after?.RestoreRngs();
            }
            if (after != null && after.Diff(runState).Other is { } changedByReCheck)
            {
                Discard(changedByReCheck);
                return;
            }
            Verified++;
            _lastVerifyMs = Stopwatch.GetElapsedTime(now).TotalMilliseconds;
            _nextAllowedTicks = Stopwatch.GetTimestamp() + (long)(_lastVerifyMs / MaxTimeShare / 1000 * Stopwatch.Frequency);
            if (_lastVerifyMs > GiveUpMs && !_gaveUp)
            {
                _gaveUp = true;
                Log.Info($"[{ModEntry.Id}] {Name}: a vanilla verification took {_lastVerifyMs / 1000:F1} s: no more verifications this session.");
            }
            int diffAt = FirstDifference(__result, vanilla, out int optimizedLength, out int vanillaLength);
            if (diffAt >= 0)
            {
                Mismatches++;
#if BENCH
                NetFullCombatState optimizedState = __result;
#endif
                __result = vanilla;
                ListenerCache.SnapshotEnabled = false;
                HookFilter.Enabled = false;
                SnapshotCardReuse.Enabled = false;
                PatchSafety.Fault(ListenerCache.SnapshotFault);
                PatchSafety.Fault(HookFilter.Name);
                PatchSafety.Fault(SnapshotCardReuse.Name);
                // The option's other parts (RelicStateReuse...) go with it, now.
                Config.Settings.ApplyAll();
                Log.Error($"[{ModEntry.Id}] {Name}: optimized snapshot differs from vanilla at byte {diffAt} "
                    + $"(lengths {optimizedLength} vs {vanillaLength}, action {justFinishedAction}). "
                    + "Using vanilla snapshot; snapshot optimizations disabled for this session. Please report this log.");
#if BENCH
                // Which state lines differ (the game's own dump format, as on a co-op divergence).
                string[] mine = optimizedState.ToString().Split('\n');
                string[] theirs = vanilla.ToString().Split('\n');
                int shown = 0;
                for (int i = 0; i < Math.Max(mine.Length, theirs.Length) && shown < 20; i++)
                {
                    string a = i < mine.Length ? mine[i] : "<none>";
                    string b = i < theirs.Length ? theirs[i] : "<none>";
                    if (a != b)
                    {
                        Log.Error($"[{ModEntry.Id}] {Name}: line {i}: optimized '{a.Trim()}' vanilla '{b.Trim()}' (previous line '{(i > 0 && i - 1 < theirs.Length ? theirs[i - 1].Trim() : "")}')");
                        shown++;
                    }
                }
#endif
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name} failed, keeping the game's snapshot: {e}");
        }
        finally
        {
            _verifying = false;
        }
    }

    /// <summary>
    /// A reuse found wrong while this snapshot was built: what found it is off now (the whole card reuse, trusted piles, the relic reuse
    /// or its scalar part), but the
    /// snapshot's other states may have been taken the same way. Built again at the optimized speed (a vanilla rebuild freezes for
    /// seconds at large decks), with what found it off; at most three times, each pass turning more off.
    /// <paramref name="walks"/>: the walk reuse's net found this snapshot wrong (ListenerCache.EndSnapshotWalks). The rebuild is a
    /// nested FromRun, never the outermost snapshot, so its walks are the exact tier's (the game's call at each walk, every postfix
    /// run): the snapshot the game builds without that reuse. Nothing is turned off for the session (the next snapshot reuses and
    /// is checked again: the same decision on every peer for the same state). If that rebuild throws, the exception (the game's
    /// or another mod's, met where the game's own FromRun meets it) goes on to the game's caller, as without the reuse: the reused
    /// snapshot is never kept.
    /// </summary>
    private static void RebuildIfDetected(IRunState runState, GameAction? justFinishedAction, ref NetFullCombatState __result, bool walks = false)
    {
        for (int pass = 0; pass < 3 && ((walks && pass == 0) || Detections != _detectionsAtSnapshot); pass++)
        {
            _detectionsAtSnapshot = Detections;
            _verifying = true;
            try
            {
                __result = BuildAgain(runState, justFinishedAction);
                Rebuilt++;
                if (!walks)
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: a snapshot reuse was found wrong while building a snapshot: built again");
                }
            }
            catch (Exception e) when (!walks)
            {
                Log.Error($"[{ModEntry.Id}] {Name}: building a snapshot again failed, keeping the first one: {e}");
                return;
            }
            finally
            {
                _verifying = false;
            }
        }
    }

    /// <summary>Snapshots that threw with reused walk outputs before their net ran, built again exactly by <see cref="FromRunFinalizer"/>.</summary>
    public static int RebuiltAfterThrow { get; private set; }

    /// <summary>
    /// FromRun's last finalizer. When the snapshot threw with reused walk outputs before the walk net ran
    /// (<see cref="ListenerCache.TakeThrewAfterReuse"/>), the game's own FromRun might not have: it is built again exactly
    /// (<see cref="ListenerCache.WithoutSnapshotReuse"/>) and that snapshot returned, or that build's exception passed on (the
    /// game's, met where its FromRun meets it). Any other exception is passed on as it is. A finalizer returning an exception makes
    /// Harmony throw it again from FromRun for every exception through it, its stack trace restarting there (as HookFilter's
    /// RethrowFinalizer). Returning null instead would swallow it (Harmony stores what a finalizer returns as the exception), so an
    /// exception this finalizer doesn't handle is returned as it is, its trace so far kept in front of the new one
    /// (<see cref="PassOn"/>, as ExceptionDispatchInfo does): the log and the game's error report show it whole, reuse or not.
    /// PatchGuard's guard (first finalizer) has seen the original.
    /// </summary>
    private static Exception? FromRunFinalizer(Exception? __exception, IRunState runState, GameAction? justFinishedAction, ref NetFullCombatState __result)
    {
        if (__exception == null)
        {
            return null;
        }
        if (!ListenerCache.TakeThrewAfterReuse())
        {
            return PassOn(__exception);
        }
        bool wasVerifying = _verifying;
        _verifying = true;
        try
        {
            __result = ListenerCache.WithoutSnapshotReuse(() => BuildAgain(runState, justFinishedAction));
            Rebuilt++;
            RebuiltAfterThrow++;
            Log.Info($"[{ModEntry.Id}] {Name}: a state check that reused listener walk outputs threw ({__exception.GetType().Name}): built again the game's way");
            return null;
        }
        catch (Exception e)
        {
            return PassOn(e);
        }
        finally
        {
            _verifying = wasVerifying;
        }
    }

    /// <summary>The runtime's "remote" stack trace of an exception: what ExceptionDispatchInfo keeps in front of a rethrow's trace.</summary>
    private static readonly FieldInfo? RemoteStackTrace = typeof(Exception).GetField("_remoteStackTraceString", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// <paramref name="exception"/>, to be thrown again from FromRun by Harmony, with its trace so far kept in front of the new one
    /// (as ExceptionDispatchInfo.Throw keeps it). Where the runtime has no such field, logged with its full trace instead. Never throws.
    /// </summary>
    private static Exception PassOn(Exception exception)
    {
        try
        {
            if (RemoteStackTrace != null && exception.StackTrace is { } trace)
            {
                RemoteStackTrace.SetValue(exception, trace + Environment.NewLine + "--- End of stack trace from previous location ---" + Environment.NewLine);
                return exception;
            }
        }
        catch
        {
            // Logged below.
        }
        Log.Warn($"[{ModEntry.Id}] {Name}: the game's FromRun threw (passed on to its caller as the game would): {exception}");
        return exception;
    }

    /// <summary>The game's own FromRun, called again as the game called it: its exceptions are the game's (or another mod's).</summary>
    [GameCallReplay]
    private static NetFullCombatState BuildAgain(IRunState runState, GameAction? justFinishedAction) =>
        NetFullCombatState.FromRun(runState, justFinishedAction);

    private static void ChecksumPrefix()
    {
        if (!_reserializing && !Faulted)
        {
            _detectionsAtChecksum = Detections;
        }
    }

    /// <summary>
    /// Kept bits found wrong while this checksum's state was serialized: the bits written before that came from the same kept bits.
    /// Card and pile bits are off now (SnapshotCardReuse turned itself off): the game's serialization again, once.
    /// </summary>
    private static void ChecksumPostfix(ChecksumTracker __instance, NetFullCombatState state, ref uint __result)
    {
        if (_reserializing || Faulted || Detections == _detectionsAtChecksum)
        {
            return;
        }
        _reserializing = true;
        try
        {
            __result = __instance.GenerateChecksum(state);
            Reserialized++;
            Log.Info($"[{ModEntry.Id}] {Name}: kept bits were found wrong while computing a checksum: computed again");
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: computing a checksum again failed, keeping the first one: {e}");
        }
        finally
        {
            _reserializing = false;
        }
    }

    private static int CombatCards(IRunState runState)
    {
        int cards = 0;
        foreach (Player player in runState.Players)
        {
            if (player.PlayerCombatState != null)
            {
                foreach (CardPile pile in player.PlayerCombatState.AllPiles)
                {
                    cards += pile.Cards.Count;
                }
            }
        }
        return cards;
    }

    /// <summary>
    /// Index of the first differing byte between both serializations, or -1 when identical (bit lengths too: the last byte's unused
    /// bits are zeros on both sides). The optimized state is serialized as a checksum would (kept card and pile bits); the vanilla one
    /// inside <see cref="ListenerCache.RunVanilla"/>, where <see cref="SnapshotCardReuse"/>'s serialization shortcuts give way to the
    /// game's own writers (its pile list header mirror included), so a mistake there shows as a difference.
    /// </summary>
    private static int FirstDifference(NetFullCombatState optimized, NetFullCombatState vanilla, out int optimizedLength, out int vanillaLength)
    {
        PacketWriter optimizedWriter = _optimizedWriter ??= new PacketWriter { WarnOnGrow = false };
        PacketWriter vanillaWriter = _vanillaWriter ??= new PacketWriter { WarnOnGrow = false };
        Serialize(optimizedWriter, optimized);
        ListenerCache.RunVanilla(() => Serialize(vanillaWriter, vanilla));
        ReadOnlySpan<byte> a = optimizedWriter.Buffer.AsSpan(0, optimizedWriter.BytePosition);
        ReadOnlySpan<byte> b = vanillaWriter.Buffer.AsSpan(0, vanillaWriter.BytePosition);
        optimizedLength = a.Length;
        vanillaLength = b.Length;
        int common = a.CommonPrefixLength(b);
        if (common == a.Length && common == b.Length)
        {
            return optimizedWriter.BitPosition == vanillaWriter.BitPosition ? -1 : common;
        }
        return common;
    }

    private static bool Serialize(PacketWriter writer, NetFullCombatState state)
    {
        writer.Reset();
        writer.Write(state);
        writer.ZeroByteRemainder();
        return true;
    }
}
