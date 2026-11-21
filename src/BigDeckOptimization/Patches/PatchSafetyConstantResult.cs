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
    /// <c>PatchStamp()</c> changes) may take it, and LimitlessHookSkip through <see cref="LastConstantResult"/> (the value bounded against each saved level). <paramref name="mods"/>: those postfixes' mods (log). Asked again per Harmony epoch
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
    /// G3 for LimitlessHookSkip's drops (2026-11-21): <see cref="ConstantResultPostfixesOnly"/> holds for the int getter
    /// <paramref name="method"/>, and the last of all its postfixes in Harmony's order (<c>PatchProcessor.GetSortedPatchMethods</c>:
    /// priorities, <c>before</c>/<c>after</c>, then registration; ours and the allowed ones included) is one of those constant
    /// postfixes: the getter then answers that postfix's constant <paramref name="value"/> on every call that reaches the patched
    /// code (what ran before it, the original and the allowed postfixes, only had its result overwritten: an allowed postfix
    /// was judged by its fingerprint, Limitless's overwrites <c>__result</c> without reading it). Not cached here: asked once per
    /// chain answer. Never throws (false when it can't tell: a factory, a patch method that doesn't resolve).
    /// </summary>
    internal static bool LastConstantResult(MethodBase? method, MethodInfo[] allowed, out int value)
    {
        value = 0;
        try
        {
            if (method is not MethodInfo { ReturnType: var result } || result != typeof(int) || !ConstantResultPostfixesOnly(method, allowed, out _)
                || Harmony.GetPatchInfo(method) is not { } info)
            {
                return false;
            }
            Patch[] live = info.Postfixes.ToArray();
            MethodInfo?[] methods = live.Select(PatchMethodOf).ToArray();
            if (live.Length == 0 || methods.Any(m => m == null || IsFactory(m)))
            {
                return false;
            }
            // Sorted as copies, each with its resolved method: Harmony's own Patch objects are shared with any thread patching now
            // (this may run off the main thread) and are never written to.
            Patch[] postfixes = new Patch[live.Length];
            for (int i = 0; i < live.Length; i++)
            {
                Patch p = live[i];
                postfixes[i] = new Patch(methods[i]!, p.index, p.owner, p.priority, p.before, p.after, p.debug);
            }
            List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(method, postfixes);
            if (sorted.Count != postfixes.Length)
            {
                return false;
            }
            MethodInfo last = sorted[^1];
            int at = Array.FindIndex(methods, m => m!.MethodHandle == last.MethodHandle);
            // The last one another mod's, not an allowed one, of the constant shape (ConstantResultPostfixesOnly judged every such
            // patch, its class's initializer and its own patches included).
            if (at < 0 || PatchGuard.IsOurs(postfixes[at].owner) || allowed.Any(a => a.MethodHandle == last.MethodHandle)
                || !ConstantResultShape(last, result))
            {
                return false;
            }
            return ConstantInt(last, out value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The int a <see cref="ConstantResultShape"/> postfix stores (its one constant load), false for another kind of constant.</summary>
    private static bool ConstantInt(MethodInfo postfix, out int value)
    {
        value = 0;
        List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(postfix).Where(i => i.Op != OpCodes.Nop).ToList();
        if (code.Count != 4 || code[2].Op != OpCodes.Stind_I4)
        {
            return false;
        }
        // ldc.i4.m1 to ldc.i4.8, ldc.i4.s, ldc.i4 (an ldc.i8 or a float isn't an int's constant).
        if (IntConstant(code[1]) is not { } known)
        {
            return false;
        }
        value = known;
        return true;
    }

#if BENCH
    /// <summary>The bench's view of <see cref="ConstantInt"/> (G3's drop self-test: the constant decoded from each emitted variant).</summary>
    internal static int? ConstantIntForTest(MethodInfo postfix) => ConstantInt(postfix, out int value) ? value : null;
#endif

    /// <summary>
    /// A postfix whose whole IL is <c>nop*; ldarg __result; ldc.*|ldnull|ldstr; stind.*; ret</c> (nops anywhere; the store the
    /// result's type takes and a constant of its kind: <see cref="StoreFor"/>, <see cref="LoadFits"/>): static, void, not
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
            // The store the result's type takes, and a constant of that store's kind (audit of 2026-11-21: a mismatched pair, an
            // stind.i1 into an int, an ldc.i8 stored as a reference, is not the C# compiler's and its value isn't the one read).
            return code.Count == 4 && LoadsArgument(code[0], 0) && StoreFor(result) is { } store && code[2].Op == store
                && LoadFits(code[1].Op, store, result) && code[3].Op == OpCodes.Ret;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// The indirect store the compiler emits for a value of <paramref name="result"/> through <c>ref</c> (an enum: its underlying
    /// type's), or null for a type it stores otherwise (a struct: <c>stobj</c>).
    /// </summary>
    private static OpCode? StoreFor(Type result)
    {
        Type type = result.IsEnum ? Enum.GetUnderlyingType(result) : result;
        if (!type.IsValueType)
        {
            return OpCodes.Stind_Ref;
        }
        if (type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            return OpCodes.Stind_I;
        }
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean or TypeCode.SByte or TypeCode.Byte => OpCodes.Stind_I1,
            TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Char => OpCodes.Stind_I2,
            TypeCode.Int32 or TypeCode.UInt32 => OpCodes.Stind_I4,
            TypeCode.Int64 or TypeCode.UInt64 => OpCodes.Stind_I8,
            TypeCode.Single => OpCodes.Stind_R4,
            TypeCode.Double => OpCodes.Stind_R8,
            _ => null,
        };
    }

    /// <summary>A constant load whose value <paramref name="store"/> stores as it is: an int32 load for the 1-, 2- and 4-byte stores, ldc.i8, ldc.r4, ldc.r8, ldnull (ldstr where a string fits) for a reference.</summary>
    private static bool LoadFits(OpCode load, OpCode store, Type result)
    {
        if (!IsConstantLoad(load))
        {
            return false;
        }
        if (store == OpCodes.Stind_I1 || store == OpCodes.Stind_I2 || store == OpCodes.Stind_I4)
        {
            return load != OpCodes.Ldc_I8 && load != OpCodes.Ldc_R4 && load != OpCodes.Ldc_R8 && load != OpCodes.Ldnull && load != OpCodes.Ldstr;
        }
        if (store == OpCodes.Stind_I8)
        {
            return load == OpCodes.Ldc_I8;
        }
        if (store == OpCodes.Stind_R4)
        {
            return load == OpCodes.Ldc_R4;
        }
        if (store == OpCodes.Stind_R8)
        {
            return load == OpCodes.Ldc_R8;
        }
        if (store == OpCodes.Stind_Ref)
        {
            return load == OpCodes.Ldnull || (load == OpCodes.Ldstr && result.IsAssignableFrom(typeof(string)));
        }
        return false; // stind.i: a native int constant needs a conversion the shape doesn't have.
    }

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
}
