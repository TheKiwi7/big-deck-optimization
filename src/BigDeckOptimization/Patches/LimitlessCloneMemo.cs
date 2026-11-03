using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Limitless 1.1.7's <c>MultiEnchantment.DeepCloneFields</c> rebuilds every saved inner card when a card is cloned:
/// <c>CardModel.FromSerializable(c).MutableClone().ToSerializable()</c>, for every card of the deck at each combat start
/// (~210 ms with 2 x 10,000 cards). That result depends only on the saved card's content, never changed once made (no code of the
/// game, Limitless or BaseLib writes to a <c>SerializableCard</c> after making it), and on BaseLib's registered saves
/// (<see cref="LimitlessReserialize.MemoStamp"/>; -1 when unknown, then always rebuilt). The deck's clones are cloned from the same
/// saved objects combat after combat: the result made for one is given again, read-only like any saved card.
///
/// One answer in <see cref="VerifyEvery"/> is rebuilt and both serialized: a byte difference turns it off for the session.
/// </summary>
public static class LimitlessCloneMemo
{
    public const string Name = "LimitlessCloneMemo";
    public const int VerifyEvery = 256;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Verified { get; private set; }

    /// <summary>Limitless 1.1.7: the method and the lambda it maps each saved inner card with.</summary>
    private const string DeepCloneFieldsFingerprint = "B0598AD4D39BD71C";
    private const string LambdaFingerprint = "70B31715ABF34F7B";

    private sealed class Memo
    {
        public long Stamp;
        public SerializableCard Result = null!;
    }

    private static readonly ConditionalWeakTable<SerializableCard, Memo> _memo = new();
    private static MethodInfo? _lambda;
    private static int _sinceVerify;
    [ThreadStatic] private static PacketWriter? _memoWriter, _freshWriter;

    public static void Apply(Harmony harmony)
    {
        if (!LimitlessReserialize.ComparisonReady)
        {
            throw new PatchSkippedException("needs LimitlessReserialize (BaseLib and Limitless)");
        }
        Type multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        MethodInfo deepClone = AccessTools.Method(multi, "DeepCloneFields")
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "DeepCloneFields");
        PatchSafety.RequireFingerprint(deepClone, DeepCloneFieldsFingerprint);
        Type closure = multi.GetNestedType("<>c", BindingFlags.NonPublic) ?? throw new PatchSkippedException("Limitless has no lambda class");
        _lambda = closure.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(m => m.Name.StartsWith("<DeepCloneFields>", StringComparison.Ordinal)
                && m.ReturnType == typeof(SerializableCard) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(SerializableCard))
            ?? throw new PatchSkippedException("DeepCloneFields maps the saved cards another way");
        PatchSafety.RequireFingerprint(_lambda, LambdaFingerprint);
        PatchGuard.Patch(harmony, _lambda, prefix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(Postfix)));
        Applied = true;
    }

    /// <summary>The stamp memos are valid for, or -1 when this can't answer (then the lambda runs and nothing is kept).</summary>
    private static long Stamp()
    {
        if (!Enabled || ListenerCache.IsSuspended || LimitlessReserialize.Faulted || LimitlessReserialize.PatchedByOthers()
            || PatchSafety.IsPatchedByOthers(_lambda))
        {
            return -1;
        }
        return LimitlessReserialize.MemoStamp();
    }

    private static bool Prefix(SerializableCard __0, ref SerializableCard __result, out long __state)
    {
        __state = -1;
        try
        {
            long stamp = Stamp();
            if (stamp < 0 || __0 == null)
            {
                return true;
            }
            __state = stamp;
            if (!_memo.TryGetValue(__0, out Memo? memo) || memo.Stamp != stamp)
            {
                return true;
            }
            if (++_sinceVerify >= VerifyEvery)
            {
                // This one runs for real and the postfix compares.
                _sinceVerify = 0;
                return true;
            }
            Reused++;
            __result = memo.Result;
            return false;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
            __state = -1;
            return true;
        }
    }

    private static void Postfix(SerializableCard __0, SerializableCard __result, long __state, bool __runOriginal)
    {
        if (!__runOriginal || __state < 0 || __0 == null || __result == null)
        {
            return;
        }
        try
        {
            if (_memo.TryGetValue(__0, out Memo? memo) && memo.Stamp == __state)
            {
                Verified++;
                if (!SameBytes(memo.Result, __result))
                {
                    Fail($"a kept inner card differs from a rebuilt one ({__0.Id})");
                }
                return;
            }
            _memo.AddOrUpdate(__0, new Memo { Stamp = __state, Result = __result });
        }
        catch (Exception e)
        {
            Fail(e.ToString());
        }
    }

    private static bool SameBytes(SerializableCard a, SerializableCard b)
    {
        _memoWriter ??= new PacketWriter { WarnOnGrow = false };
        _freshWriter ??= new PacketWriter { WarnOnGrow = false };
        _memoWriter.Reset();
        _freshWriter.Reset();
        a.Serialize(_memoWriter);
        b.Serialize(_freshWriter);
        return _memoWriter.BitPosition == _freshWriter.BitPosition
            && _memoWriter.Buffer.AsSpan(0, _memoWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition));
    }

    private static void Fail(string details)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, inner cards rebuilt at every clone as Limitless does: {details}");
    }
}
