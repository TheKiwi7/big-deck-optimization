using System;
using System.Diagnostics;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
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

    public static void Apply(Harmony harmony)
    {
        PatchGuard.Patch(harmony, 
            AccessTools.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun)),
            postfix: new HarmonyMethod(typeof(SnapshotVerifier), nameof(FromRunPostfix)));
    }

    private static void FromRunPostfix(IRunState runState, GameAction? justFinishedAction, ref NetFullCombatState __result)
    {
        if (_verifying || !ListenerCache.IsInSnapshot)
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

        _verifying = true;
        try
        {
            NetFullCombatState vanilla = ListenerCache.RunVanilla(() => NetFullCombatState.FromRun(runState, justFinishedAction));
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
                __result = vanilla;
                ListenerCache.SnapshotEnabled = false;
                HookFilter.Enabled = false;
                PatchSafety.Fault(ListenerCache.SnapshotFault);
                PatchSafety.Fault(HookFilter.Name);
                Log.Error($"[{ModEntry.Id}] {Name}: optimized snapshot differs from vanilla at byte {diffAt} "
                    + $"(lengths {optimizedLength} vs {vanillaLength}, action {justFinishedAction}). "
                    + "Using vanilla snapshot; snapshot optimizations disabled for this session. Please report this log.");
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

    public static void ResetStats()
    {
        Snapshots = Verified = Mismatches = 0;
    }

    /// <summary>Index of the first differing byte between both serializations, or -1 when identical.</summary>
    private static int FirstDifference(NetFullCombatState optimized, NetFullCombatState vanilla, out int optimizedLength, out int vanillaLength)
    {
        _optimizedWriter ??= new PacketWriter { WarnOnGrow = false };
        _vanillaWriter ??= new PacketWriter { WarnOnGrow = false };
        ReadOnlySpan<byte> a = Serialize(_optimizedWriter, optimized);
        ReadOnlySpan<byte> b = Serialize(_vanillaWriter, vanilla);
        optimizedLength = a.Length;
        vanillaLength = b.Length;
        int common = a.CommonPrefixLength(b);
        return common == a.Length && common == b.Length ? -1 : common;
    }

    private static ReadOnlySpan<byte> Serialize(PacketWriter writer, NetFullCombatState state)
    {
        writer.Reset();
        writer.Write(state);
        writer.ZeroByteRemainder();
        return writer.Buffer.AsSpan(0, writer.BytePosition);
    }
}
