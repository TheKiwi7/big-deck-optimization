using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;

namespace BigDeckOptimization.Patches;

/// <summary>
/// When a pooled card node (<see cref="NCard"/>, grid holders) goes back to its pool,
/// <c>NodePool.DisconnectIncomingAndOutgoingSignals</c> walks every node of its subtree and, per node, builds the full
/// signal list (one Godot dictionary per signal, with argument info) then one connection list per signal. Every card
/// grid open or sort frees ~70 of them: 60–340 ms, while the disconnects themselves take a few ms.
///
/// The patch sits on the non-generic <c>NodePool.Free(IPoolable)</c> every free goes through (patching
/// <c>NodePool&lt;T&gt;</c> itself doesn't reach the shared code reference-type instantiations run) and mirrors the
/// pool's <c>Free</c>: same walk, same disconnects in the same order (through the pool's own <c>DisconnectSignal</c>),
/// same bookkeeping. Signal names are cached per node type (class, script) and a signal's connection list is only read
/// when <c>HasConnections</c> says it has any. Names are re-checked against the vanilla list for the first nodes of
/// each type then 1 in 64; any difference (e.g. a user signal added at runtime) turns the patch off for the session.
/// Only active while the mirrored methods' fingerprints match. UI only.
/// </summary>
public static class PoolSignalScan
{
    public const string Name = "PoolSignalScan";

    /// <summary>The v0.107.1 code mirrored below: NodePool.Free(IPoolable), NodePool&lt;T&gt;.Free(T) and its walk.</summary>
    private const string StaticFreeFingerprint = "93E5DC86F9FBFE32";
    private const string PoolFreeFingerprint = "ABC14666CCEB9854";
    private const string WalkFingerprint = "FD48203FE0D7C5E8";

    private const int VerifyFirst = 3;
    private const int VerifyEvery = 64;

    public static bool Enabled { get; set; } = true;

    public static bool Applied { get; private set; }

    /// <summary>Nodes scanned, and name lists re-checked against vanilla.</summary>
    public static long Nodes { get; private set; }

    public static long Verified { get; private set; }

    private static readonly Dictionary<(Type, string, ulong), (StringName[] Names, int Seen)> _signalNames = new();
    private static readonly ConditionalWeakTable<object, Action<Callable, Signal>> _disconnect = new();
    private static readonly Dictionary<Type, PoolAccess> _access = new();
    private static readonly Variant _nameKey = Variant.CreateFrom("name");
    private static readonly Variant _callableKey = Variant.CreateFrom("callable");
    private static readonly Variant _signalKey = Variant.CreateFrom("signal");
    private static AccessTools.FieldRef<Dictionary<Type, INodePool>>? _pools;
    private static MethodInfo? _staticFree;

    /// <summary>Reflection access to one NodePool&lt;T&gt;'s private members (T only known at runtime).</summary>
    private sealed class PoolAccess
    {
        public readonly FieldInfo Used;
        public readonly FieldInfo Free;
        public readonly MethodInfo UsedContains;
        public readonly MethodInfo UsedRemove;
        public readonly MethodInfo FreeAdd;
        public readonly MethodInfo[] Mirrored;

        public PoolAccess(Type poolType)
        {
            Used = AccessTools.Field(poolType, "_usedObjects");
            Free = AccessTools.Field(poolType, "_freeObjects");
            UsedContains = Used.FieldType.GetMethod("Contains")!;
            UsedRemove = Used.FieldType.GetMethod("Remove")!;
            FreeAdd = Free.FieldType.GetMethod("Add")!;
            Mirrored = new[]
            {
                AccessTools.Method(poolType, "DisconnectIncomingAndOutgoingSignals"),
                AccessTools.Method(poolType, "DisconnectSignal"),
                PublicFree(poolType),
            };
        }
    }

    public static void Apply(Harmony harmony)
    {
        _staticFree = AccessTools.Method(typeof(NodePool), nameof(NodePool.Free), new[] { typeof(IPoolable) });
        PatchSafety.RequireFingerprint(_staticFree, StaticFreeFingerprint);
        PatchSafety.RequireFingerprint(PublicFree(typeof(NodePool<>)), PoolFreeFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(NodePool<>), "DisconnectIncomingAndOutgoingSignals"), WalkFingerprint);
        _pools = AccessTools.StaticFieldRefAccess<Dictionary<Type, INodePool>>(AccessTools.Field(typeof(NodePool), "_pools"));
        PatchGuard.Patch(harmony, _staticFree, prefix: new HarmonyMethod(typeof(PoolSignalScan), nameof(Prefix)));
        Applied = true;
    }

    /// <summary>NodePool&lt;T&gt;.Free(T) (the explicit INodePool.Free is a separate, non-public method).</summary>
    private static MethodInfo PublicFree(Type poolType) =>
        poolType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Single(m => m.Name == "Free");

    /// <summary>Mirrors NodePool.Free → NodePool&lt;T&gt;.Free for objects the pool tracks; anything else goes vanilla.</summary>
    private static bool Prefix(IPoolable poolable)
    {
        if (!Enabled || poolable is not Node node || PatchSafety.IsPatchedByOthers(_staticFree))
        {
            return true;
        }
        object pool;
        PoolAccess? access;
        object used;
        try
        {
            if (!_pools!().TryGetValue(poolable.GetType(), out INodePool? found))
            {
                return true; // Vanilla throws.
            }
            pool = found;
            if (!_access.TryGetValue(pool.GetType(), out access))
            {
                access = new PoolAccess(pool.GetType());
                _access[pool.GetType()] = access;
            }
            used = access.Used.GetValue(pool)!;
            if (access.Mirrored.Any(method => PatchSafety.IsPatchedByOthers(method)) || !(bool)access.UsedContains.Invoke(used, new object[] { poolable })!)
            {
                return true; // Vanilla logs the double free / foreign object.
            }
            Action<Callable, Signal> disconnect = _disconnect.GetValue(pool, p =>
                AccessTools.Method(p.GetType(), "DisconnectSignal").CreateDelegate<Action<Callable, Signal>>(p));
            Walk(node, disconnect);
        }
        catch (Exception e)
        {
            // Vanilla then redoes the whole free; what was already disconnected fails its IsConnected check.
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
            return true;
        }
        access.UsedRemove.Invoke(used, new object[] { poolable });
        access.FreeAdd.Invoke(access.Free.GetValue(pool), new object[] { poolable });
        poolable.OnFreedToPool();
        return false;
    }

    private static void Walk(Node obj, Action<Callable, Signal> disconnect)
    {
        Nodes++;
        foreach (StringName signal in SignalNames(obj))
        {
            if (!obj.HasConnections(signal))
            {
                continue; // Vanilla iterates an empty connection list.
            }
            foreach (Godot.Collections.Dictionary connection in obj.GetSignalConnectionList(signal))
            {
                disconnect(connection[_callableKey].AsCallable(), connection[_signalKey].AsSignal());
            }
        }
        foreach (Godot.Collections.Dictionary connection in obj.GetIncomingConnections())
        {
            disconnect(connection[_callableKey].AsCallable(), connection[_signalKey].AsSignal());
        }
        for (int i = 0; i < obj.GetChildCount(); i++)
        {
            Walk(obj.GetChild(i), disconnect);
        }
    }

    /// <summary>The names <c>GetSignalList</c> would return, in the same order.</summary>
    private static StringName[] SignalNames(Node obj)
    {
        Variant script = obj.GetScript();
        ulong scriptId = script.VariantType == Variant.Type.Object ? script.AsGodotObject()?.GetInstanceId() ?? 0 : 0;
        (Type, string, ulong) key = (obj.GetType(), obj.GetClass(), scriptId);
        _signalNames.TryGetValue(key, out (StringName[] Names, int Seen) entry);
        int seen = entry.Seen + 1;
        if (entry.Names != null && seen > VerifyFirst && seen % VerifyEvery != 0)
        {
            _signalNames[key] = (entry.Names, seen);
            return entry.Names;
        }
        StringName[] names = obj.GetSignalList().Select(s => s[_nameKey].AsStringName()).ToArray();
        if (entry.Names != null)
        {
            Verified++;
            if (!entry.Names.SequenceEqual(names))
            {
                Enabled = false;
                PatchSafety.Fault(Name);
                Log.Error($"[{ModEntry.Id}] {Name} disabled: signals of {key.Item1.Name} ({key.Item2}) differ between nodes of the same type");
            }
        }
        _signalNames[key] = (names, seen);
        return names;
    }
}
