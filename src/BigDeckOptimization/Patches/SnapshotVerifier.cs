using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
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

    public static VerifyMode Mode { get; set; } = VerifyMode.Sample;

    public static int Snapshots { get; private set; }
    public static int Verified { get; private set; }
    public static int Mismatches { get; private set; }

    [ThreadStatic] private static bool _verifying;
    [ThreadStatic] private static PacketWriter? _optimizedWriter;
    [ThreadStatic] private static PacketWriter? _vanillaWriter;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(
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
        bool sampled = Mode == VerifyMode.All
            || (Mode == VerifyMode.Sample && (index < AlwaysVerifyFirst || index % SampleEvery == 0));
        if (!sampled)
        {
            return;
        }

        _verifying = true;
        try
        {
            NetFullCombatState vanilla = ListenerCache.RunVanilla(() => NetFullCombatState.FromRun(runState, justFinishedAction));
            Verified++;
            int diffAt = FirstDifference(__result, vanilla, out int optimizedLength, out int vanillaLength);
            if (diffAt >= 0)
            {
                Mismatches++;
                __result = vanilla;
                ListenerCache.SnapshotEnabled = false;
                HookFilter.Enabled = false;
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
