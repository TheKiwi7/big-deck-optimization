using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// (<see cref="LimitlessReserialize.MemoStamp"/>; -1 when unknown, then always rebuilt). The result made for a content is given for the
/// same content again, read-only like any saved card: a big deck holds few different inner cards, a first combat gains as much as the
/// next ones, and so do other players' decks, rebuilt as new objects at each co-op combat. A saved card with real BaseLib data isn't kept.
///
/// One answer in <see cref="VerifyEvery"/> is rebuilt and both serialized: a byte difference turns it off for the session.
/// </summary>
public static class LimitlessCloneMemo
{
    public const string Name = "LimitlessCloneMemo";
    internal static int VerifyEvery { get; set; } = 256;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Verified { get; private set; }

    /// <summary>Limitless 1.1.7: the method and the lambda it maps each saved inner card with.</summary>
    private const string DeepCloneFieldsFingerprint = "B0598AD4D39BD71C";
    private const string LambdaFingerprint = "70B31715ABF34F7B";

    /// <summary>A saved card by content, and what BaseLib holds for it (<see cref="LimitlessReserialize.StoredDataState"/>).</summary>
    private readonly record struct Key(SerializableCard Saved, int Data);

    private sealed class ContentComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key a, Key b) => a.Data == b.Data && (ReferenceEquals(a.Saved, b.Saved) || LimitlessReserialize.SameContent(a.Saved, b.Saved));

        public int GetHashCode(Key key) =>
            HashCode.Combine(key.Saved.Id, key.Saved.CurrentUpgradeLevel, key.Saved.FloorAddedToDeck, key.Saved.Enchantment?.Id, key.Saved.Enchantment?.Amount, key.Data);
    }

    /// <summary>Holds saved cards only, no model. Main thread.</summary>
    private static readonly Dictionary<Key, SerializableCard> _memo = new(new ContentComparer());
    private const int MaxContents = 16384;
    private static long _memoStamp = -1;
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
        if (!Enabled || ListenerCache.IsSuspended || LimitlessReserialize.Faulted || !LimitlessReserialize.CanCompareHere || LimitlessReserialize.PatchedByOthers()
            || PatchSafety.IsPatchedByOthers(_lambda))
        {
            return -1;
        }
        return LimitlessReserialize.MemoStamp();
    }

    private static bool Prefix(SerializableCard __0, ref SerializableCard __result, out int __state)
    {
        __state = -1;
        try
        {
            long stamp = Stamp();
            if (stamp < 0 || __0 == null)
            {
                return true;
            }
            if (stamp != _memoStamp)
            {
                _memo.Clear();
                _memoStamp = stamp;
            }
            __state = LimitlessReserialize.StoredDataState(__0);
            if (__state < 0 || !_memo.TryGetValue(new Key(__0, __state), out SerializableCard? memo))
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
            __result = memo;
            return false;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
            __state = -1;
            return true;
        }
    }

    private static void Postfix(SerializableCard __0, SerializableCard __result, int __state, bool __runOriginal)
    {
        if (!__runOriginal || __state < 0 || __0 == null || __result == null)
        {
            return;
        }
        try
        {
            Key key = new(__0, __state);
            if (_memo.TryGetValue(key, out SerializableCard? memo))
            {
                Verified++;
                if (!SameBytes(memo, __result))
                {
                    Fail($"a kept inner card differs from a rebuilt one ({__0.Id})");
                }
                return;
            }
            if (_memo.Count >= MaxContents)
            {
                _memo.Clear();
            }
            _memo[key] = __result;
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
        _memo.Clear();
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, inner cards rebuilt at every clone as Limitless does: {details}");
    }
}
