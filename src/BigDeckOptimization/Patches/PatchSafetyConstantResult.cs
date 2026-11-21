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
    /// <summary>A constant-result answer per getter and allowed set, for the Harmony epoch and 2 s it was worked out at.</summary>
    private sealed record ConstantAnswer(int Epoch, long CheckedAt, bool Only, string[] Mods);

    private static readonly ConcurrentDictionary<(MethodBase Method, long Allowed), ConstantAnswer> _constantResults = new();

    /// <summary>Per postfix method and result type: whether its own code is <see cref="ConstantResultShape"/>'s (a loaded method's IL doesn't change).</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Method, Type Result), bool> _constantShapes = new();

    /// <summary>
    /// G3 (2026-11-21): whether every other mod's patch on the getter <paramref name="method"/> but <paramref name="allowed"/> is a
    /// postfix setting its result to one constant (<see cref="ConstantResultShape"/>), at least one, none of them patched itself,
    /// no hidden IL hook on the getter or on them (HarmonyWatch), their classes' initializers free (<see cref="InitializerFree"/>:
    /// a class with a precise initializer would run it at the postfix's first call). Such a getter answers that constant whatever
    /// the state, and the postfix reads, writes and calls nothing else: code that calls the getter computes what it computes without
    /// the postfix's own effects, so running it fewer times changes nothing but that code's own calls. Not a tolerance of the strict
    /// checks: only a caller that keeps whole results of that code per patch set (LimitlessCloneMemo, cleared when
    /// <c>PatchStamp()</c> changes) may take it. <paramref name="mods"/>: those postfixes' mods (log). Asked again per Harmony epoch
    /// and at most every 2 s. Never throws (false when it can't tell).
    /// </summary>
    internal static bool ConstantResultPostfixesOnly(MethodBase? method, MethodInfo[] allowed, out string[] mods)
    {
        mods = Array.Empty<string>();
        if (method == null)
        {
            return false;
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64, key = AllowedKey(allowed);
        if (_constantResults.TryGetValue((method, key), out ConstantAnswer? known) && known.Epoch == epoch && now - known.CheckedAt < RecheckAfterMs)
        {
            mods = known.Mods;
            return known.Only;
        }
        bool only = false;
        try
        {
            if (method is MethodInfo { ReturnType: var result } getter && result != typeof(void) && getter.Name.StartsWith("get_", StringComparison.Ordinal)
                && getter.GetParameters().Length == 0 && !getter.ContainsGenericParameters && HarmonyWatch.HiddenOwners(method).Length == 0
                && Harmony.GetPatchInfo(method) is { } info)
            {
                var constant = new List<Patch>();
                only = true;
                foreach (Patch patch in AllPatches(info))
                {
                    if (PatchGuard.IsOurs(patch.owner))
                    {
                        continue;
                    }
                    MethodInfo? postfix = PatchMethodOf(patch);
                    if (postfix != null && allowed.Any(a => a.MethodHandle == postfix.MethodHandle))
                    {
                        continue;
                    }
                    if (postfix == null || !info.Postfixes.Contains(patch) || !ConstantResultShape(postfix, result)
                        || (Harmony.GetPatchInfo(postfix) is { } own && AllPatches(own).Any()) || HarmonyWatch.HiddenOwners(postfix).Length > 0
                        || postfix.DeclaringType is not { } declaring || !InitializerFree(declaring))
                    {
                        only = false;
                        break;
                    }
                    constant.Add(patch);
                }
                only = only && constant.Count > 0;
                if (only)
                {
                    mods = ModNames(constant);
                }
            }
        }
        catch
        {
            only = false;
            mods = Array.Empty<string>();
        }
        _constantResults[(method, key)] = new ConstantAnswer(epoch, now, only, mods);
        return only;
    }

    /// <summary>
    /// A postfix whose whole IL is <c>nop*; ldarg __result; ldc.*|ldnull|ldstr; stind.*; ret</c> (nops anywhere): static, void, not
    /// generic, no factory, no renamed parameters, its only parameter <c>ref __result</c> of the getter's type <paramref name="result"/>.
    /// It sets the result to one constant and does nothing else (Infinite Upgrades' <c>__result = int.MaxValue</c>). Refused: a
    /// static read (<c>__result = Flag ? 1 : 0</c>), the result read (<c>Math.Max(__result, x)</c>), any other parameter or call.
    /// </summary>
    private static bool ConstantResultShape(MethodInfo postfix, Type result) => _constantShapes.GetOrAdd((postfix.MethodHandle, result), _ =>
    {
        try
        {
            if (!postfix.IsStatic || postfix.ContainsGenericParameters || postfix.ReturnType != typeof(void) || IsFactory(postfix) || RenamesParameters(postfix)
                || postfix.GetParameters() is not [{ Name: "__result", ParameterType: { IsByRef: true } byRef }] || byRef.GetElementType() != result)
            {
                return false;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(postfix).Where(i => i.Op != OpCodes.Nop).ToList();
            return code.Count == 4 && LoadsArgument(code[0], 0) && IsConstantLoad(code[1].Op) && IsIndirectStore(code[2].Op) && code[3].Op == OpCodes.Ret;
        }
        catch
        {
            return false;
        }
    });

#if BENCH
    /// <summary>The bench's view of <see cref="ConstantResultShape"/> and the class initializer rule (G3's self-test, offline too).</summary>
    internal static bool ConstantResultShapeForTest(MethodInfo postfix, Type result) =>
        ConstantResultShape(postfix, result) && postfix.DeclaringType is { } declaring && InitializerFree(declaring);
#endif

    private static bool IsConstantLoad(OpCode op) =>
        op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4_M1 || op == OpCodes.Ldc_I4_0 || op == OpCodes.Ldc_I4_1
        || op == OpCodes.Ldc_I4_2 || op == OpCodes.Ldc_I4_3 || op == OpCodes.Ldc_I4_4 || op == OpCodes.Ldc_I4_5 || op == OpCodes.Ldc_I4_6
        || op == OpCodes.Ldc_I4_7 || op == OpCodes.Ldc_I4_8 || op == OpCodes.Ldc_I8 || op == OpCodes.Ldc_R4 || op == OpCodes.Ldc_R8
        || op == OpCodes.Ldnull || op == OpCodes.Ldstr;

    private static bool IsIndirectStore(OpCode op) =>
        op == OpCodes.Stind_I || op == OpCodes.Stind_I1 || op == OpCodes.Stind_I2 || op == OpCodes.Stind_I4 || op == OpCodes.Stind_I8
        || op == OpCodes.Stind_R4 || op == OpCodes.Stind_R8 || op == OpCodes.Stind_Ref;
}
