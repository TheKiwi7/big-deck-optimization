using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

public static partial class PatchSafety
{
    /// <summary>A value-gate answer per (method, getter), for the Harmony epoch and 2 s it was worked out at.</summary>
    private sealed record ValueGateAnswer(int Epoch, long CheckedAt, (MethodInfo Patch, int Value)[]? Gates);

    private static readonly ConcurrentDictionary<(MethodBase Method, MethodInfo Getter), ValueGateAnswer> _valueGates = new();

    /// <summary>Per postfix and getter: the constant its first test compares the getter's value with, or null (not the shape).</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Patch, RuntimeMethodHandle Getter, RuntimeTypeHandle Instance), int?> _valueGateShapes = new();

    /// <summary>
    /// Rule P (2026-11-18, Downfall's Hermit <c>HandChangedPatches</c> on <c>CardPile.InvokeCardAddFinished</c>): when every other
    /// mod's patch on <paramref name="method"/> is a postfix gated on a value of its instance (<see cref="ValueGateShape"/>: its
    /// code starts <c>ldarg __instance; call|callvirt getter; ldc.i4 K; bne.un -&gt; ret</c>, or <c>beq</c> with <c>ret</c> next,
    /// nothing before the test), the postfixes with their K; null when any patch there isn't one (a transpiler, a prefix, a
    /// finalizer, an inner patch, another shape), when the getter is patched by another mod (or hooked unseen), or when it can't
    /// tell. Each postfix unpatched itself. Empty: no other mod's patch. What a caller may then take as never acting: a gate
    /// whose K differs from the getter's value on every instance it calls the method on (CardFlightLimit's merged pile event:
    /// the pile's Type, written by its constructor only). The getter's own code is the caller's to pin (fingerprint, field
    /// writers). Asked again per Harmony epoch and at most every 2 s; never throws.
    /// </summary>
    internal static (MethodInfo Patch, int Value)[]? ValueGatedPostfixes(MethodBase? method, MethodInfo? getter)
    {
        if (method == null || getter == null)
        {
            return null;
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (_valueGates.TryGetValue((method, getter), out ValueGateAnswer? known) && known.Epoch == epoch && now - known.CheckedAt < RecheckAfterMs)
        {
            return known.Gates;
        }
        (MethodInfo Patch, int Value)[]? gates;
        try
        {
            gates = ComputeValueGates(method, getter);
        }
        catch
        {
            gates = null;
        }
        _valueGates[(method, getter)] = new ValueGateAnswer(epoch, now, gates);
        return gates;
    }

    private static (MethodInfo Patch, int Value)[]? ComputeValueGates(MethodBase method, MethodInfo getter)
    {
        if (method.IsStatic || method.DeclaringType is not { } instanceType || HarmonyWatch.HiddenOwners(method).Length > 0
            || getter.IsStatic || getter.GetParameters().Length != 0 || getter.DeclaringType is not { } getterType || !getterType.IsAssignableFrom(instanceType)
            || !(getter.ReturnType == typeof(int) || (getter.ReturnType.IsEnum && Enum.GetUnderlyingType(getter.ReturnType) == typeof(int))))
        {
            return null;
        }
        if (Harmony.GetPatchInfo(method) is not { } info)
        {
            return Array.Empty<(MethodInfo, int)>();
        }
        var gates = new List<(MethodInfo, int)>();
        foreach (Patch patch in AllPatches(info))
        {
            if (PatchGuard.IsOurs(patch.owner))
            {
                continue; // This mod's own patches are its callers' to judge.
            }
            if (!info.Postfixes.Contains(patch) || PatchMethodOf(patch) is not { } postfix || ValueGateShape(postfix, getter, instanceType) is not { } value
                || (Harmony.GetPatchInfo(postfix) is { } own && AllPatches(own).Any()) || HarmonyWatch.HiddenOwners(postfix).Length > 0)
            {
                return null;
            }
            gates.Add((postfix, value));
        }
        // The getter the gates call: unpatched (nobody's code but the game's runs in it), else no gate is judged.
        if (gates.Count > 0 && HasOtherPatches(getter))
        {
            return null;
        }
        return gates.ToArray();
    }

    /// <summary>
    /// A postfix doing nothing unless its instance's <paramref name="getter"/> value is some K: static, void, not generic, no
    /// factory, no renamed parameters, its only parameter <c>__instance</c> (by value, a class the getter is declared on or
    /// derives from, the patched type assignable to it), and from its first instruction (nops aside) <c>ldarg __instance;
    /// call|callvirt getter; ldc.i4 K</c> then <c>bne.un L</c> with only nops, unconditional branches and <c>ret</c> from L, or
    /// <c>beq L</c> with only those next. Its type initializer isn't judged: the caller still calls the method (one event for
    /// several cards), so the postfix still runs at least once, at the same first moment as before. Returns K, or null.
    /// </summary>
    private static int? ValueGateShape(MethodInfo postfix, MethodInfo getter, Type instanceType) =>
        _valueGateShapes.GetOrAdd((postfix.MethodHandle, getter.MethodHandle, instanceType.TypeHandle), _ =>
    {
        try
        {
            if (!postfix.IsStatic || postfix.ContainsGenericParameters || postfix.ReturnType != typeof(void) || IsFactory(postfix) || RenamesParameters(postfix))
            {
                return null;
            }
            ParameterInfo[] parameters = postfix.GetParameters();
            if (parameters is not [{ Name: "__instance", ParameterType: var parameterType }] || parameterType.IsByRef || parameterType.IsValueType
                || !getter.DeclaringType!.IsAssignableFrom(parameterType) || !parameterType.IsAssignableFrom(instanceType))
            {
                return null;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(postfix);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            int index = SkipNops(code, 0);
            if (index + 3 >= code.Count || !LoadsArgument(code[index], 0)
                || (code[index + 1].Op != OpCodes.Call && code[index + 1].Op != OpCodes.Callvirt)
                || code[index + 1].Operand is not MethodInfo called || called.MethodHandle != getter.MethodHandle
                || IntConstant(code[index + 2]) is not { } value || code[index + 3].Operand is not int to || !at.TryGetValue(to, out int taken))
            {
                return null;
            }
            OpCode test = code[index + 3].Op;
            int idle;
            if (test == OpCodes.Bne_Un || test == OpCodes.Bne_Un_S)
            {
                idle = taken; // Taken when the value isn't K.
            }
            else if (test == OpCodes.Beq || test == OpCodes.Beq_S)
            {
                idle = index + 4; // Falls through when the value isn't K.
            }
            else
            {
                return null;
            }
            return ReturnsOnly(code, at, idle) ? value : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>Whether the path from <paramref name="next"/> is only nops and unconditional branches to a <c>ret</c>.</summary>
    private static bool ReturnsOnly(List<MethodFingerprint.Instruction> code, Dictionary<int, int> at, int next)
    {
        var visited = new HashSet<int>();
        while (next >= 0 && next < code.Count && visited.Add(next))
        {
            OpCode op = code[next].Op;
            if (op == OpCodes.Nop)
            {
                next++;
            }
            else if (op == OpCodes.Br || op == OpCodes.Br_S)
            {
                next = code[next].Operand is int target && at.TryGetValue(target, out int jumped) ? jumped : -1;
            }
            else
            {
                return op == OpCodes.Ret;
            }
        }
        return false;
    }
}
