using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

public static partial class PatchSafety
{
    /// <summary>A null guard's answer per method, for the Harmony epoch and 2 s it was worked out at.</summary>
    private sealed record GuardAnswer(int Epoch, long CheckedAt, bool? Answer, string[] Mods);

    private static readonly ConcurrentDictionary<MethodBase, GuardAnswer> _nullGuards = new();

    /// <summary>Per patch method: whether its IL is the null guard's (<see cref="NullGuardShape"/>), and K.</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Method, string Argument), bool?> _guardShapes = new();

    /// <summary>
    /// Rule NG (2026-11-18, HonkaiLib's <c>CombatStateContainsNullGuardPatch</c>): when every other mod's patch on
    /// <paramref name="method"/> (a game method taking one model argument and returning bool: <c>CombatState.Contains</c>,
    /// <c>RunState.Contains</c>) is a pass-through null guard, the answer the method then gives for a null model (K: false only),
    /// else null. A guard: a static bool prefix, not generic, no factory, no renamed parameters, whose parameters are the
    /// original's model argument (by its name or <c>__0</c>, by value) and <c>ref bool __result</c>, and whose IL (nops aside) is
    /// <c>ldarg model; brfalse|brtrue</c>, then on the non-null path only <c>ldc.i4.1; ret</c> (the original runs, unchanged) and on
    /// the null path only <c>ldarg __result; ldc.i4.0; stind.i1; ldc.i4.0; ret</c> (the original skipped, false returned). Its own
    /// method unpatched, no hidden IL hook on the original (HarmonyWatch), its class's initializer none or harmless whenever it runs
    /// (<see cref="InitializerFree"/>: a mirror never calls the guard; the pure part cached with the shape, what it relies on asked
    /// with the rest). Asked again per Harmony epoch and at most every 2 s; never
    /// throws (a question it can't answer is null). For an unfiltered mirror of the method only: such a mirror answers K for a
    /// null model while this answers K, and runs the original's logic for any other (what the guard lets through).
    /// </summary>
    internal static bool? NullGuardAnswer(MethodBase? method)
    {
        if (method == null)
        {
            return null;
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (_nullGuards.TryGetValue(method, out GuardAnswer? known) && known.Epoch == epoch && now - known.CheckedAt < RecheckAfterMs)
        {
            return known.Answer;
        }
        bool? answer = null;
        string[] mods = Array.Empty<string>();
        try
        {
            answer = ComputeNullGuard(method, out mods);
        }
        catch
        {
            answer = null;
        }
        bool changed = known == null || known.Answer != answer || !known.Mods.SequenceEqual(mods);
        _nullGuards[method] = new GuardAnswer(epoch, now, answer, mods);
        if (answer != null && changed)
        {
            Log.Info($"[{ModEntry.Id}] PatchSafety: {method.DeclaringType?.Name}.{method.Name} is patched by {string.Join(", ", mods)} with a null guard "
                + "(a null model answered false, every other one left to the game's code): the walks' unfiltered mirrors keep it");
        }
        return answer;
    }

    /// <summary>The mods of the null guards <see cref="NullGuardAnswer"/> last accepted on the method (display), or none.</summary>
    internal static string[] NullGuardMods(MethodBase? method) =>
        method != null && _nullGuards.TryGetValue(method, out GuardAnswer? known) && known.Answer != null ? known.Mods : Array.Empty<string>();

    private static bool? ComputeNullGuard(MethodBase method, out string[] mods)
    {
        mods = Array.Empty<string>();
        if (method is not MethodInfo original || original.ReturnType != typeof(bool) || original.GetParameters() is not [{ ParameterType: var argType } argument]
            || !typeof(MegaCrit.Sts2.Core.Models.AbstractModel).IsAssignableFrom(argType) || HarmonyWatch.HiddenOwners(method).Length > 0)
        {
            return null;
        }
        if (Harmony.GetPatchInfo(method) is not { } info)
        {
            return null;
        }
        var guards = new List<Patch>();
        foreach (Patch patch in AllPatches(info))
        {
            if (PatchGuard.IsOurs(patch.owner))
            {
                return null; // Nothing of this mod there today: one would change what the guard's skip skips.
            }
            if (!info.Prefixes.Contains(patch) || PatchMethodOf(patch) is not { } guard || NullGuardShape(guard, argument.Name ?? "") != false)
            {
                return null;
            }
            if ((Harmony.GetPatchInfo(guard) is { } own && AllPatches(own).Any()) || (guard.DeclaringType is { } declaring && PatchedInitializerReliance(declaring) != null))
            {
                return null;
            }
            guards.Add(patch);
        }
        if (guards.Count == 0)
        {
            return null;
        }
        mods = ModNames(guards);
        return false;
    }

    /// <summary>The guard's K (the answer for a null model) when <paramref name="guard"/> has the null guard's shape, else null.</summary>
    private static bool? NullGuardShape(MethodInfo guard, string argumentName) => _guardShapes.GetOrAdd((guard.MethodHandle, argumentName), _ =>
    {
        try
        {
            if (!guard.IsStatic || guard.ContainsGenericParameters || guard.ReturnType != typeof(bool) || IsFactory(guard) || RenamesParameters(guard)
                || guard.DeclaringType is not { } declaring || (declaring.TypeInitializer != null && !InitializerPure(declaring)))
            {
                return null;
            }
            ParameterInfo[] parameters = guard.GetParameters();
            if (parameters.Length != 2)
            {
                return null;
            }
            int model = Array.FindIndex(parameters, p => (p.Name == argumentName || p.Name == "__0") && !p.ParameterType.IsByRef && !p.ParameterType.IsValueType);
            int result = Array.FindIndex(parameters, p => p.Name == "__result" && p.ParameterType == typeof(bool).MakeByRefType());
            if (model < 0 || result < 0 || model == result)
            {
                return null;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(guard);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            int index = SkipNops(code, 0);
            if (index + 1 >= code.Count || !LoadsArgument(code[index], model))
            {
                return null;
            }
            MethodFingerprint.Instruction test = code[index + 1];
            bool onFalse = test.Op == OpCodes.Brfalse || test.Op == OpCodes.Brfalse_S;
            bool onTrue = test.Op == OpCodes.Brtrue || test.Op == OpCodes.Brtrue_S;
            if ((!onFalse && !onTrue) || test.Operand is not int to || !at.TryGetValue(to, out int taken))
            {
                return null;
            }
            int nullPath = onFalse ? taken : index + 2;
            int otherPath = onFalse ? index + 2 : taken;
            // The non-null path: true returned (the original runs), nothing else.
            int a = SkipNops(code, otherPath);
            if (a + 1 >= code.Count || code[a].Op != OpCodes.Ldc_I4_1 || code[a + 1].Op != OpCodes.Ret)
            {
                return null;
            }
            // The null path: __result = K; return false.
            int b = SkipNops(code, nullPath);
            if (b + 4 >= code.Count || !LoadsArgument(code[b], result) || code[b + 2].Op != OpCodes.Stind_I1
                || code[b + 3].Op != OpCodes.Ldc_I4_0 || code[b + 4].Op != OpCodes.Ret)
            {
                return null;
            }
            OpCode k = code[b + 1].Op;
            return k == OpCodes.Ldc_I4_0 ? false : k == OpCodes.Ldc_I4_1 ? true : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>A null-fill answer per getter, for the Harmony epoch and 2 s it was worked out at.</summary>
    private sealed record FillAnswer(int Epoch, long CheckedAt, bool Only, string[] Mods);

    private static readonly ConcurrentDictionary<MethodBase, FillAnswer> _nullFills = new();
    private static readonly ConcurrentDictionary<RuntimeMethodHandle, bool> _fillShapes = new();

    /// <summary>
    /// Rule null-fill (2026-11-18, made for CardFusion's <c>FusedCard_CombatState_Patch</c>, which it refuses since the review of
    /// 2026-11-19, C4: that fill calls <c>FusedCard.ChildToParentMap.TryGetValue</c> and another card's getter): whether every patch on the getter
    /// <paramref name="method"/> is another mod's postfix that does nothing when the getter's result isn't null (it may only
    /// replace a null result: <see cref="NullFillShape"/>) and whose every path, the fill's included, only reads and sets its
    /// result (rule RO's analysis, <see cref="ReadOnlyShape"/>: the methods it calls unpatched too), its own method unpatched, no
    /// hidden IL hook on the getter, nothing of this mod there. A caller mirroring the getter's non-null answers from fields
    /// (CardSortKeys' OwnerCombat) then reads the real getter for what a null-filling postfix may give instead, for cards the game
    /// may never read it for: the fill must do nothing else (a lazy registration, a random draw would run a different number of
    /// times than in the game). Asked again per Harmony epoch and at most every 2 s. Never throws (false when it can't tell).
    /// </summary>
    internal static bool NullFillOnly(MethodBase? method) => NullFillAnswer(method).Only;

    /// <summary>The mods of the null-fill postfixes <see cref="NullFillOnly"/> last accepted (display).</summary>
    internal static string[] NullFillMods(MethodBase? method) => NullFillAnswer(method) is { Only: true } answer ? answer.Mods : Array.Empty<string>();

    private static FillAnswer NullFillAnswer(MethodBase? method)
    {
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (method == null)
        {
            return new FillAnswer(epoch, now, false, Array.Empty<string>());
        }
        if (_nullFills.TryGetValue(method, out FillAnswer? known) && known.Epoch == epoch && now - known.CheckedAt < RecheckAfterMs)
        {
            return known;
        }
        bool only = false;
        string[] mods = Array.Empty<string>();
        try
        {
            if (method is MethodInfo { ReturnType: { IsValueType: false } } && HarmonyWatch.HiddenOwners(method).Length == 0 && Harmony.GetPatchInfo(method) is { } info)
            {
                Patch[] all = AllPatches(info).ToArray();
                only = all.Length > 0 && all.All(p => !PatchGuard.IsOurs(p.owner) && info.Postfixes.Contains(p) && PatchMethodOf(p) is { } postfix
                    && NullFillShape(postfix) && !(Harmony.GetPatchInfo(postfix) is { } own && AllPatches(own).Any())
                    && ReadOnlyShape(postfix, method) is { } relied && relied.All(r => !HasOtherPatches(r)));
                if (only)
                {
                    mods = ModNames(all);
                }
            }
        }
        catch
        {
            only = false;
        }
        var answer = new FillAnswer(epoch, now, only, mods);
        _nullFills[method] = answer;
        return answer;
    }

    /// <summary>
    /// A postfix doing nothing for a non-null result: static, void, not generic, no factory, no renamed parameters, a
    /// <c>ref __result</c> of a reference type, and from its first instruction (nops aside) <c>ldarg __result; ldind.ref; brtrue L</c>
    /// with only nops, unconditional branches and <c>ret</c> from L (<see cref="NullResultGated"/> the other way round: what it
    /// does with a null result is the caller's to judge).
    /// </summary>
    private static bool NullFillShape(MethodInfo postfix) => _fillShapes.GetOrAdd(postfix.MethodHandle, _ =>
    {
        try
        {
            if (!postfix.IsStatic || postfix.ContainsGenericParameters || postfix.ReturnType != typeof(void) || IsFactory(postfix) || RenamesParameters(postfix))
            {
                return false;
            }
            ParameterInfo[] parameters = postfix.GetParameters();
            int result = Array.FindIndex(parameters, p => p.Name == "__result");
            if (result < 0 || !parameters[result].ParameterType.IsByRef || parameters[result].ParameterType.GetElementType() is not { IsValueType: false })
            {
                return false;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(postfix);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            int index = SkipNops(code, 0);
            if (index + 2 >= code.Count || !LoadsArgument(code[index], result) || code[index + 1].Op != OpCodes.Ldind_Ref
                || (code[index + 2].Op != OpCodes.Brtrue && code[index + 2].Op != OpCodes.Brtrue_S) || code[index + 2].Operand is not int to
                || !at.TryGetValue(to, out int next))
            {
                return false;
            }
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
        catch
        {
            return false;
        }
    });

    /// <summary>The first instruction at or after <paramref name="index"/> that isn't a nop (unconditional branches not followed: none in the shape).</summary>
    private static int SkipNops(List<MethodFingerprint.Instruction> code, int index)
    {
        while (index < code.Count && code[index].Op == OpCodes.Nop)
        {
            index++;
        }
        return index;
    }
}
