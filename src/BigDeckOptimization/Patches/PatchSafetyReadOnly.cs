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
    /// <summary>The read-only postfixes on a method, for the Harmony epoch and 2 s they were worked out at.</summary>
    private sealed record ReadOnlyAnswer(int Epoch, long CheckedAt, MethodInfo[] Postfixes);

    private static readonly ConcurrentDictionary<MethodBase, ReadOnlyAnswer> _readOnly = new();

    /// <summary>Per (postfix, original): the methods its code calls (game getters, its own static methods), or null (not read-only).</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Postfix, RuntimeMethodHandle Original), MethodBase[]?> _readOnlyShapes = new();

    /// <summary>What the analysis knows of a value: the original's instance (never null), the <c>__result</c> address, or anything else.</summary>
    private enum RoKind : byte { Value, Instance, Result }

    /// <summary>
    /// Rule RO (2026-11-18, YutaMod's <c>IndependentCurseLeaderPatch</c> on <c>Creature.get_IsSecondaryEnemy</c>): the other mods'
    /// postfixes on <paramref name="method"/> (an instance method returning a value) whose every path only reads and sets
    /// <c>__result</c>: static, void, not generic, no factory, no renamed parameters, no type initializer, parameters only
    /// <c>__instance</c> (by value) and <c>ref __result</c>, no exception clause; on every path only nops, constants, arguments and
    /// locals, <c>dup</c>/<c>pop</c>, <c>isinst</c>, comparisons, <c>and</c>/<c>or</c>/<c>xor</c>/<c>not</c>, branches and
    /// <c>switch</c>, reads and stores through <c>__result</c> only, calls to compiler-generated get-only non-virtual auto-property
    /// getters of the game's assembly made on the instance itself (never on a returned value), and calls to static methods of
    /// its own class (no recursion, the same constraints, nothing passed but values and the instance). No path can throw, write,
    /// or reach code of its own beyond these; each method it calls and the postfix itself unpatched (Harmony, hidden owners) at
    /// each recheck. Such a postfix only changes what <paramref name="method"/> returns: a call left out (CardFlightLimit's
    /// skipped soul, whose end check is that call's only user) leaves out nothing else. Empty when none. Asked again per Harmony
    /// epoch and at most every 2 s; never throws.
    /// </summary>
    internal static MethodInfo[] ReadOnlyPostfixes(MethodBase? method)
    {
        if (method == null)
        {
            return Array.Empty<MethodInfo>();
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (_readOnly.TryGetValue(method, out ReadOnlyAnswer? known) && known.Epoch == epoch && now - known.CheckedAt < RecheckAfterMs)
        {
            return known.Postfixes;
        }
        MethodInfo[] postfixes;
        try
        {
            postfixes = Harmony.GetPatchInfo(method) is { } info
                ? info.Postfixes.Where(p => !PatchGuard.IsOurs(p.owner)).Select(PatchMethodOf).OfType<MethodInfo>()
                    .Where(p => ReadOnlyShape(p, method) is { } relied && !HasOtherPatches(p) && relied.All(r => !HasOtherPatches(r))).ToArray()
                : Array.Empty<MethodInfo>();
        }
        catch
        {
            postfixes = Array.Empty<MethodInfo>();
        }
        // The same array while the answer is the same (callers key caches on its content).
        if (known != null && known.Postfixes.SequenceEqual(postfixes))
        {
            postfixes = known.Postfixes;
        }
        _readOnly[method] = new ReadOnlyAnswer(epoch, now, postfixes);
        return postfixes;
    }

    private static MethodBase[]? ReadOnlyShape(MethodInfo postfix, MethodBase method) => _readOnlyShapes.GetOrAdd((postfix.MethodHandle, method.MethodHandle), _ =>
    {
        try
        {
            if (method is not MethodInfo { IsStatic: false, ReturnType: var returned } || returned == typeof(void) || returned.IsByRef || method.DeclaringType is not { } instanceType
                || !postfix.IsStatic || postfix.ContainsGenericParameters || postfix.ReturnType != typeof(void) || IsFactory(postfix) || RenamesParameters(postfix)
                || postfix.DeclaringType is not { TypeInitializer: null } own)
            {
                return null;
            }
            ParameterInfo[] parameters = postfix.GetParameters();
            var kinds = new RoKind[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (parameters[i].Name == "__instance" && !type.IsByRef && !type.IsValueType && type.IsAssignableFrom(instanceType))
                {
                    kinds[i] = RoKind.Instance;
                }
                else if (parameters[i].Name == "__result" && type.IsByRef && type.GetElementType() == returned)
                {
                    kinds[i] = RoKind.Result;
                }
                else
                {
                    return null;
                }
            }
            var relied = new HashSet<MethodBase>();
            return ReadOnlyBody(postfix, kinds, instanceType, own, relied, new HashSet<MethodBase> { postfix }) ? relied.ToArray() : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>
    /// Every reachable instruction of <paramref name="method"/> run over abstract stacks and locals (merged per instruction until
    /// nothing changes): true when each is one rule RO allows with what it gets. Methods it calls go into <paramref name="relied"/>.
    /// </summary>
    private static bool ReadOnlyBody(MethodInfo method, RoKind[] arguments, Type instanceType, Type own, HashSet<MethodBase> relied, HashSet<MethodBase> active)
    {
        if (method.GetMethodBody() is not { } body || body.ExceptionHandlingClauses.Count > 0 || method.ReturnType.IsByRef || active.Count > 4)
        {
            return false;
        }
        List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(method, strict: true);
        var at = new Dictionary<int, int>();
        for (int i = 0; i < code.Count; i++)
        {
            at[code[i].Offset] = i;
        }
        var states = new (RoKind[] Stack, RoKind[] Locals)?[code.Count];
        var work = new Stack<int>();
        bool Flow(int index, RoKind[] stack, RoKind[] locals)
        {
            if (index < 0 || index >= code.Count)
            {
                return false;
            }
            if (states[index] is not { } seen)
            {
                states[index] = (stack, locals);
                work.Push(index);
                return true;
            }
            if (seen.Stack.Length != stack.Length)
            {
                return false;
            }
            RoKind[] s = seen.Stack.Zip(stack, (a, b) => a == b ? a : RoKind.Value).ToArray();
            RoKind[] l = seen.Locals.Zip(locals, (a, b) => a == b ? a : RoKind.Value).ToArray();
            if (!s.SequenceEqual(seen.Stack) || !l.SequenceEqual(seen.Locals))
            {
                states[index] = (s, l);
                work.Push(index);
            }
            return true;
        }
        int Target(object? operand) => operand is int offset && at.TryGetValue(offset, out int index) ? index : -1;
        if (!Flow(0, Array.Empty<RoKind>(), new RoKind[body.LocalVariables.Count]))
        {
            return false;
        }
        while (work.Count > 0)
        {
            int index = work.Pop();
            (RoKind[] start, RoKind[] startLocals) = states[index]!.Value;
            var stack = new List<RoKind>(start);
            RoKind[] locals = (RoKind[])startLocals.Clone();
            MethodFingerprint.Instruction ins = code[index];
            OpCode op = ins.Op;
            RoKind Pop()
            {
                RoKind top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return top;
            }
            string name = op.Name ?? "";
            // ldarg forms only (ldarga refused below: an address could be written through).
            int argument = Enumerable.Range(0, arguments.Length).FirstOrDefault(a => LoadsArgument(ins, a), -1);
            int[] next = { index + 1 };
            if (op == OpCodes.Nop)
            {
            }
            else if (IntConstant(ins) != null || op == OpCodes.Ldnull)
            {
                stack.Add(RoKind.Value);
            }
            else if (argument >= 0)
            {
                stack.Add(arguments[argument]);
            }
            else if (Enumerable.Range(0, locals.Length).FirstOrDefault(l => LoadsLocal(ins, l), -1) is var loaded and >= 0)
            {
                stack.Add(locals[loaded]);
            }
            else if (StoresLocal(ins, out int stored) && stored < locals.Length)
            {
                locals[stored] = Pop();
            }
            else if (op == OpCodes.Dup)
            {
                stack.Add(stack[^1]);
            }
            else if (op == OpCodes.Pop)
            {
                Pop();
            }
            else if (op == OpCodes.Isinst || op == OpCodes.Not)
            {
                Pop();
                stack.Add(RoKind.Value);
            }
            else if (op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Cgt_Un || op == OpCodes.Clt || op == OpCodes.Clt_Un
                || op == OpCodes.And || op == OpCodes.Or || op == OpCodes.Xor)
            {
                Pop();
                Pop();
                stack.Add(RoKind.Value);
            }
            else if (name.StartsWith("ldind.", StringComparison.Ordinal))
            {
                if (Pop() != RoKind.Result)
                {
                    return false;
                }
                stack.Add(RoKind.Value);
            }
            else if (name.StartsWith("stind.", StringComparison.Ordinal))
            {
                if (Pop() == RoKind.Result || Pop() != RoKind.Result)
                {
                    return false;
                }
            }
            else if (op == OpCodes.Br || op == OpCodes.Br_S)
            {
                next = new[] { Target(ins.Operand) };
            }
            else if (op.FlowControl == FlowControl.Cond_Branch)
            {
                // brtrue/brfalse and switch take one value, beq/bne.un/bge/bgt/ble/blt (and .un) two.
                Pop();
                if (op != OpCodes.Brtrue && op != OpCodes.Brtrue_S && op != OpCodes.Brfalse && op != OpCodes.Brfalse_S && op != OpCodes.Switch)
                {
                    Pop();
                }
                next = op == OpCodes.Switch && ins.Operand is int[] targets
                    ? targets.Select(t => at.TryGetValue(t, out int j) ? j : -1).Append(index + 1).ToArray()
                    : new[] { Target(ins.Operand), index + 1 };
            }
            else if (op == OpCodes.Ret)
            {
                if (method.ReturnType != typeof(void) && Pop() == RoKind.Result)
                {
                    return false;
                }
                if (stack.Count != 0)
                {
                    return false;
                }
                next = Array.Empty<int>();
            }
            else if ((op == OpCodes.Call || op == OpCodes.Callvirt) && ins.Operand is MethodInfo callee)
            {
                if (GameAutoGetter(callee, instanceType))
                {
                    if (Pop() != RoKind.Instance)
                    {
                        return false; // On a returned value (may be null) or anything else: refused.
                    }
                }
                else if (op == OpCodes.Call && callee.IsStatic && callee.DeclaringType == own && !callee.ContainsGenericParameters && !active.Contains(callee))
                {
                    var passed = new RoKind[callee.GetParameters().Length];
                    for (int i = passed.Length - 1; i >= 0; i--)
                    {
                        passed[i] = Pop();
                    }
                    if (passed.Contains(RoKind.Result) || !ReadOnlyBody(callee, passed, instanceType, own, relied, new HashSet<MethodBase>(active) { callee }))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
                relied.Add(callee);
                if (callee.ReturnType != typeof(void))
                {
                    stack.Add(RoKind.Value);
                }
            }
            else
            {
                return false;
            }
            foreach (int successor in next)
            {
                // No backward branch: a loop of pure code that never ends would hang the game's call, and a skipped call wouldn't.
                if (successor <= index || !Flow(successor, stack.ToArray(), locals))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// A compiler-generated, get-only, non-virtual auto-property getter of the game's assembly that an instance of
    /// <paramref name="instanceType"/> has: <c>ldarg.0; ldfld &lt;P&gt;k__BackingField; ret</c>, nothing that can throw on a non-null instance.
    /// </summary>
    private static bool GameAutoGetter(MethodInfo getter, Type instanceType)
    {
        if (getter.IsStatic || getter.IsVirtual || getter.ContainsGenericParameters || !getter.IsSpecialName || getter.GetParameters().Length != 0
            || !getter.Name.StartsWith("get_", StringComparison.Ordinal) || getter.DeclaringType is not { } declaring
            || declaring.Assembly != typeof(MegaCrit.Sts2.Core.Models.AbstractModel).Assembly || !declaring.IsAssignableFrom(instanceType)
            || !getter.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
        {
            return false;
        }
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string property = getter.Name.Substring(4);
        if (declaring.GetProperty(property, declared) is not { } info || info.GetSetMethod(nonPublic: true) != null || info.GetGetMethod(nonPublic: true)?.MethodHandle != getter.MethodHandle)
        {
            return false;
        }
        List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(getter, strict: true);
        return code.Count == 3 && code[0].Op == OpCodes.Ldarg_0 && code[1].Op == OpCodes.Ldfld && code[2].Op == OpCodes.Ret
            && code[1].Operand is FieldInfo { IsStatic: false } field && field.DeclaringType == declaring && field.Name == $"<{property}>k__BackingField";
    }
}
