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

    /// <summary>
    /// What the analysis knows of a value: the original's instance (never null), the <c>__result</c> address, the int constants 0
    /// and 1 (a bool prefix's answers: <see cref="ReadOnlyPrefixes"/>), or anything else.
    /// </summary>
    private enum RoKind : byte { Value, Instance, Result, Zero, One }

    /// <summary>The read-only bool prefixes on a method, for the Harmony epoch and 2 s they were worked out at.</summary>
    private static readonly ConcurrentDictionary<MethodBase, ReadOnlyAnswer> _readOnlyPrefixes = new();

    /// <summary>Per (prefix, original): the methods its code calls, or null (not a read-only bool prefix).</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Prefix, RuntimeMethodHandle Original), MethodBase[]?> _readOnlyPrefixShapes = new();

    /// <summary>
    /// RO for bool prefixes (2026-11-21, GensokyoSpire's <c>CreatureIsPlayerPatch</c> on <c>Creature.get_IsPlayer</c>: <c>if
    /// (__instance.Monster != null) { __result = false; return false; } return true;</c>): the other mods' prefixes on
    /// <paramref name="method"/> (an instance method returning a value) that return bool and take only <c>__instance</c> (by value)
    /// and <c>ref __result</c> (no <c>__runOriginal</c>, no other argument, nothing renamed, no factory, no type initializer, no
    /// exception clause), whose every path either returns true having stored nothing, or stores through <c>__result</c> and then
    /// returns false (a constant answer on each path, followed path by path: Debug builds join both answers in one <c>ret</c>), under
    /// <see cref="ReadOnlyPostfixes"/>' read rules otherwise (no static read, no call but the instance's own game auto-property getters
    /// and pure static helpers of its own class, no write but through <c>__result</c>, no backward branch); the prefix and what it
    /// calls unpatched at each recheck. Such a prefix either lets the original run untouched or answers in its place, reading only
    /// the instance's own fields: it changes what the call returns and nothing else, so leaving a call out leaves out nothing of it
    /// where that call's answer only fed skipped code (HookSkip's choice-context helpers, RelicFlashMerge's merged flashes). Not a
    /// tolerance: those callers pass them as allowed patches. Empty when none. Asked again per Harmony epoch and at most every 2 s;
    /// never throws.
    /// </summary>
    internal static MethodInfo[] ReadOnlyPrefixes(MethodBase? method)
    {
        if (method == null)
        {
            return Array.Empty<MethodInfo>();
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (_readOnlyPrefixes.TryGetValue(method, out ReadOnlyAnswer? known) && known.Epoch == epoch && now - known.CheckedAt < RecheckAfterMs)
        {
            return known.Postfixes;
        }
        MethodInfo[] prefixes;
        try
        {
            prefixes = Harmony.GetPatchInfo(method) is { } info
                ? info.Prefixes.Where(p => !PatchGuard.IsOurs(p.owner)).Select(PatchMethodOf).OfType<MethodInfo>()
                    .Where(p => ReadOnlyPrefixShape(p, method) is { } relied && !HasOtherPatches(p) && relied.All(r => !HasOtherPatches(r))).ToArray()
                : Array.Empty<MethodInfo>();
        }
        catch
        {
            prefixes = Array.Empty<MethodInfo>();
        }
        if (known != null && known.Postfixes.SequenceEqual(prefixes))
        {
            prefixes = known.Postfixes;
        }
        _readOnlyPrefixes[method] = new ReadOnlyAnswer(epoch, now, prefixes);
        return prefixes;
    }

#if BENCH
    /// <summary>The bench's view of <see cref="ReadOnlyPrefixShape"/> (its self-test, offline too): whether the prefix has the shape on that method.</summary>
    internal static bool ReadOnlyPrefixShapeForTest(MethodInfo prefix, MethodBase method) => ReadOnlyPrefixShape(prefix, method) != null;
#endif

    private static MethodBase[]? ReadOnlyPrefixShape(MethodInfo prefix, MethodBase method) => _readOnlyPrefixShapes.GetOrAdd((prefix.MethodHandle, method.MethodHandle), _ =>
    {
        try
        {
            if (prefix.ReturnType != typeof(bool) || ReadOnlyArguments(prefix, method) is not { } kinds || prefix.DeclaringType is not { } own
                || method.DeclaringType is not { } instanceType)
            {
                return null;
            }
            var relied = new HashSet<MethodBase>();
            return ReadOnlyPrefixPaths(prefix, kinds, instanceType, own, relied) ? relied.ToArray() : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>
    /// The shape both RO rules ask of the patch method (<paramref name="patch"/>) and the original (<paramref name="method"/>): an
    /// instance method returning a value; the patch static, not generic, no factory, nothing renamed, its class without a type
    /// initializer, its parameters only <c>__instance</c> (by value, the instance's type) and <c>ref __result</c> (the return type).
    /// Their kinds, or null.
    /// </summary>
    private static RoKind[]? ReadOnlyArguments(MethodInfo patch, MethodBase method)
    {
        if (method is not MethodInfo { IsStatic: false, ReturnType: var returned } || returned == typeof(void) || returned.IsByRef || method.DeclaringType is not { } instanceType
            || !patch.IsStatic || patch.ContainsGenericParameters || IsFactory(patch) || RenamesParameters(patch)
            || patch.DeclaringType is not { TypeInitializer: null })
        {
            return null;
        }
        ParameterInfo[] parameters = patch.GetParameters();
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
        return kinds;
    }

    /// <summary>Paths a prefix's analysis follows at most (no backward branch: a finite tree; past this, refused).</summary>
    private const int MaxPrefixSteps = 4096;

    /// <summary>
    /// Every path of the bool prefix <paramref name="prefix"/> followed on its own (no merge: an answer's constant and whether
    /// <c>__result</c> was stored belong to the path): true when each instruction is one RO allows (<see cref="RoStep"/>) and each
    /// <c>ret</c> returns the constant 1 with nothing stored on its path, or the constant 0 after a store through <c>__result</c>.
    /// </summary>
    private static bool ReadOnlyPrefixPaths(MethodInfo prefix, RoKind[] arguments, Type instanceType, Type own, HashSet<MethodBase> relied)
    {
        if (prefix.GetMethodBody() is not { } body || body.ExceptionHandlingClauses.Count > 0)
        {
            return false;
        }
        List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(prefix, strict: true);
        var at = new Dictionary<int, int>();
        for (int i = 0; i < code.Count; i++)
        {
            at[code[i].Offset] = i;
        }
        var active = new HashSet<MethodBase> { prefix };
        var work = new Stack<(int Index, RoKind[] Stack, RoKind[] Locals, bool Stored)>();
        work.Push((0, Array.Empty<RoKind>(), new RoKind[body.LocalVariables.Count], false));
        int steps = 0;
        while (work.Count > 0)
        {
            if (++steps > MaxPrefixSteps)
            {
                return false;
            }
            (int index, RoKind[] start, RoKind[] startLocals, bool stored) = work.Pop();
            var stack = new List<RoKind>(start);
            RoKind[] locals = (RoKind[])startLocals.Clone();
            if (!RoStep(prefix, code, at, index, arguments, instanceType, own, relied, active, stack, locals, out int[] next, out bool stores, out RoKind? returned))
            {
                return false;
            }
            stored |= stores;
            if (returned is { } answer && !((answer == RoKind.One && !stored) || (answer == RoKind.Zero && stored)))
            {
                return false;
            }
            foreach (int successor in next)
            {
                // No backward branch (as RoStep's callers: a loop of pure code that never ends would hang the game's call).
                if (successor <= index || successor >= code.Count)
                {
                    return false;
                }
                work.Push((successor, stack.ToArray(), locals, stored));
            }
        }
        return true;
    }

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
            if (postfix.ReturnType != typeof(void) || ReadOnlyArguments(postfix, method) is not { } kinds || postfix.DeclaringType is not { } own
                || method.DeclaringType is not { } instanceType)
            {
                return null;
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
            if (!RoStep(method, code, at, index, arguments, instanceType, own, relied, active, stack, locals, out int[] next, out _, out _))
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
    /// One instruction of <paramref name="method"/> under rule RO's rules, over the abstract <paramref name="stack"/> and
    /// <paramref name="locals"/> (changed in place): false when refused. <paramref name="next"/>: its successors (none for
    /// <c>ret</c>); <paramref name="stores"/>: it stored through <c>__result</c>; <paramref name="returned"/>: what a <c>ret</c> of a
    /// method returning a value returned (null otherwise). Methods it calls go into <paramref name="relied"/> (its own static helpers
    /// analysed by <see cref="ReadOnlyBody"/>).
    /// </summary>
    private static bool RoStep(MethodInfo method, List<MethodFingerprint.Instruction> code, Dictionary<int, int> at, int index, RoKind[] arguments,
        Type instanceType, Type own, HashSet<MethodBase> relied, HashSet<MethodBase> active, List<RoKind> stack, RoKind[] locals,
        out int[] next, out bool stores, out RoKind? returned)
    {
        stores = false;
        returned = null;
        next = new[] { index + 1 };
        int Target(object? operand) => operand is int offset && at.TryGetValue(offset, out int found) ? found : -1;
        MethodFingerprint.Instruction ins = code[index];
        OpCode op = ins.Op;
        RoKind Pop()
        {
            // An empty stack is invalid IL: refused by the throw (the callers' catch).
            RoKind top = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return top;
        }
        string name = op.Name ?? "";
        // ldarg forms only (ldarga refused below: an address could be written through).
        int argument = Enumerable.Range(0, arguments.Length).FirstOrDefault(a => LoadsArgument(ins, a), -1);
        if (op == OpCodes.Nop)
        {
        }
        else if (IntConstant(ins) is { } constant)
        {
            stack.Add(constant == 0 ? RoKind.Zero : constant == 1 ? RoKind.One : RoKind.Value);
        }
        else if (op == OpCodes.Ldnull)
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
            stores = true;
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
            if (method.ReturnType != typeof(void))
            {
                returned = Pop();
                if (returned == RoKind.Result)
                {
                    return false;
                }
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
