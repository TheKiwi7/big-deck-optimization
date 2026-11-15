using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>Which other mods' patches on a method a check lets stand (see "Foreign patches judged by their shape" in DESIGN).</summary>
public enum PatchTolerance
{
    /// <summary>Any other mod's patch backs off (<see cref="PatchSafety.IsPatchedByOthers(MethodBase?, string)"/>).</summary>
    None,

    /// <summary>
    /// Only for a call site where this mod's code keeps the method's own effect identical and only runs it (the original runs,
    /// or a mirror whose result is the original's): patches that run around that code and can't change how it runs are let
    /// stand. A postfix (any signature: Harmony runs it after the original or after a skipping prefix, on the same result), or
    /// a prefix returning void whose parameters are all by value (no <c>ref</c>/<c>out</c> argument, no <c>ref __result</c>,
    /// no <c>ref __runOriginal</c>; <c>__state</c> by reference is allowed, Harmony only hands it to that patch's own postfix)
    /// that doesn't take <c>__args</c> (Harmony writes that array back into the arguments) and that Harmony runs before every
    /// prefix of this mod returning bool (one sorted after such a prefix would run after this mod's replacement, or not at all
    /// once it skipped the original). Transpilers, finalizers, inner prefixes and postfixes, bool prefixes: not let stand.
    /// </summary>
    SurroundingOnly,

    /// <summary>
    /// Only for an <c>AbstractModel</c> default whose calls this mod skips for listener types keeping it: a prefix or postfix
    /// that provably does nothing unless <c>__instance</c> is one of some types (<see cref="PatchSafety.TypeGates"/>, a strict
    /// IL shape) is let stand, and those types are reported, so the caller treats them as overriding the default (their calls
    /// stay the game's). Anything else backs off.
    /// </summary>
    TypeGated,
}

public static partial class PatchSafety
{
    /// <summary>The kind of a Harmony patch (the list of <see cref="HarmonyLib.Patches"/> it is in).</summary>
    public enum PatchKind
    {
        Prefix,
        Postfix,
        Transpiler,
        Finalizer,
        InnerPrefix,
        InnerPostfix,
    }

    internal static string ToleranceText(PatchTolerance tolerance) => tolerance switch
    {
        PatchTolerance.SurroundingOnly => "postfixes and void prefixes",
        PatchTolerance.TypeGated => "patches gated on the listener's type",
        _ => "nothing",
    };

    /// <summary>
    /// <see cref="IsPatchedByOthers(MethodBase?, string)"/>, except for other mods' patches the tolerance lets stand (see
    /// <see cref="PatchTolerance"/>). Same cost as the plain check while no other mod patches the method (its 2 s answer is
    /// read first). A back-off is recorded as the plain check does; patches let stand are logged once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPatchedByOthers(MethodBase? method, PatchTolerance tolerance, [CallerFilePath] string caller = "") =>
        CheckTolerant(method, Array.Empty<MethodInfo>(), tolerance, caller, out _);

    /// <summary>The tolerant check with <paramref name="allowedPatches"/> also let stand (see the allowed overload).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPatchedByOthers(MethodBase? method, MethodInfo[] allowedPatches, PatchTolerance tolerance, [CallerFilePath] string caller = "") =>
        CheckTolerant(method, allowedPatches, tolerance, caller, out _);

    /// <summary>
    /// <see cref="PatchTolerance.TypeGated"/>: false when no other mod patches the default or every patch is gated on the
    /// listener's type (<paramref name="gates"/>: the types the patches test, empty when nothing is gated); the caller then
    /// treats types assignable to a gate as overriders. Gates come from the same cached answer as the verdict.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPatchedByOthers(MethodBase? method, out Type[] gates, [CallerFilePath] string caller = "") =>
        CheckTolerant(method, Array.Empty<MethodInfo>(), PatchTolerance.TypeGated, caller, out gates);

    private sealed record TolerantAnswer(bool Patched, long CheckedAt, string[] Mods, string[] ToleratedMods, Type[] Gates);

    private static readonly ConcurrentDictionary<(MethodBase Method, long Allowed, PatchTolerance Tolerance), TolerantAnswer> _tolerant = new();

#if BENCH
    /// <summary>Checks answered "not patched" because every other mod's patch there was let stand (the effects check).</summary>
    public static long ToleratedChecks { get; private set; }
#endif

    private static long AllowedKey(MethodInfo[] allowedPatches)
    {
        long allowedKey = 0;
        foreach (MethodInfo allowed in allowedPatches)
        {
            allowedKey += allowed.MethodHandle.Value.ToInt64() * 31 + 7;
        }
        return allowedKey;
    }

    internal static bool CheckTolerant(MethodBase? method, MethodInfo[] allowedPatches, PatchTolerance tolerance, string caller, out Type[] gates)
    {
        gates = Type.EmptyTypes;
        if (tolerance == PatchTolerance.None)
        {
            return allowedPatches.Length == 0 ? CheckPatched(method, caller) : CheckPatched(method, allowedPatches, caller);
        }
        if (method == null)
        {
            return true;
        }
        long now = Environment.TickCount64;
        // The plain check's answer first: no other mod's patch at all, nothing to tolerate (the usual case, its usual cost).
        if (_patchedByOthers.TryGetValue(method, out var plain) && now - plain.CheckedAt < RecheckAfterMs && !plain.Patched)
        {
            if (_anyConflict)
            {
                RecordOnce(caller, method, plain.Mods, tolerance);
            }
            return false;
        }
        long key = AllowedKey(allowedPatches);
        if (_tolerant.TryGetValue((method, key, tolerance), out TolerantAnswer? cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            if (cached.Patched || _anyConflict)
            {
                RecordOnce(caller, method, cached.Mods, tolerance);
            }
            if (!cached.Patched && cached.ToleratedMods.Length > 0)
            {
                Tolerated(caller, method, tolerance, cached.ToleratedMods, cached.Gates);
            }
            gates = cached.Gates;
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        var others = new List<(Patch Patch, PatchKind Kind)>();
        if (info != null)
        {
            foreach ((Patch patch, PatchKind kind) in PatchesWithKind(info))
            {
                if (!PatchGuard.IsOurs(patch.owner))
                {
                    others.Add((patch, kind));
                }
            }
        }
        string[] all = ModNames(others.Select(o => o.Patch));
        if (all.Length == 0)
        {
            Forget(method);
        }
        // The plain check's cache gets the raw answer too: the fast path above, and the plain checks of the same method.
        _patchedByOthers[method] = (all.Length > 0, now, all);
        var remaining = new List<Patch>();
        var tolerated = new List<Patch>();
        var gateList = new List<Type>();
        foreach ((Patch patch, PatchKind kind) in others)
        {
            if (PatchMethodOf(patch) is { } patchMethod && allowedPatches.Any(a => a.MethodHandle == patchMethod.MethodHandle))
            {
                continue;
            }
            if (Intolerable(method, info!, patch, kind, tolerance, gateList) == null)
            {
                tolerated.Add(patch);
            }
            else
            {
                remaining.Add(patch);
            }
        }
        string[] mods = ModNames(remaining);
        string[] toleratedMods = ModNames(tolerated);
        Type[] gateTypes = mods.Length > 0 ? Type.EmptyTypes : gateList.Distinct().ToArray();
        RecordOnce(caller, method, mods, tolerance);
        _tolerant[(method, key, tolerance)] = new TolerantAnswer(mods.Length > 0, now, mods, toleratedMods, gateTypes);
        if (mods.Length == 0 && toleratedMods.Length > 0)
        {
            Tolerated(caller, method, tolerance, toleratedMods, gateTypes);
        }
        gates = gateTypes;
        return mods.Length > 0;
    }

    /// <summary>
    /// Whether another mod has any patch on the method, without recording a back-off: for a check that only makes an answer
    /// more careful while the optimization keeps running (PileIndex's cards mid-move). Shares the plain check's 2 s cache.
    /// </summary>
    internal static bool HasOtherPatches(MethodBase? method)
    {
        if (method == null)
        {
            return true;
        }
        long now = Environment.TickCount64;
        if (_patchedByOthers.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        string[] mods = ModNames(info == null ? Enumerable.Empty<Patch>() : AllPatches(info).Where(p => !PatchGuard.IsOurs(p.owner)));
        if (mods.Length == 0)
        {
            Forget(method);
        }
        _patchedByOthers[method] = (mods.Length > 0, now, mods);
        return mods.Length > 0;
    }

    /// <summary>Every patch of the method with its kind, inner prefixes and postfixes included (as <see cref="AllPatches"/>).</summary>
    private static IEnumerable<(Patch Patch, PatchKind Kind)> PatchesWithKind(HarmonyLib.Patches info) =>
        info.Prefixes.Select(p => (p, PatchKind.Prefix))
            .Concat(info.Postfixes.Select(p => (p, PatchKind.Postfix)))
            .Concat(info.Transpilers.Select(p => (p, PatchKind.Transpiler)))
            .Concat(info.Finalizers.Select(p => (p, PatchKind.Finalizer)))
            .Concat(info.InnerPrefixes.Select(p => (p, PatchKind.InnerPrefix)))
            .Concat(info.InnerPostfixes.Select(p => (p, PatchKind.InnerPostfix)));

    /// <summary>
    /// Why this patch of another mod can change what <paramref name="original"/> does under that tolerance, or null when it
    /// can't (<see cref="PatchTolerance"/>). <paramref name="gates"/> gets a gated patch's types (TypeGated). Never throws: a
    /// question it can't answer is a reason.
    /// </summary>
    internal static string? Intolerable(MethodBase original, HarmonyLib.Patches info, Patch patch, PatchKind kind, PatchTolerance tolerance, List<Type>? gates)
    {
        try
        {
            MethodInfo? method = PatchMethodOf(patch);
            if (method == null)
            {
                return "patch method not found";
            }
            if (tolerance == PatchTolerance.None)
            {
                return "no tolerance at this call site";
            }
            if (kind is not (PatchKind.Prefix or PatchKind.Postfix))
            {
                return kind switch
                {
                    PatchKind.Transpiler => "transpiler (changes the method's code)",
                    PatchKind.Finalizer => "finalizer (may change or swallow the method's exception)",
                    PatchKind.InnerPrefix => "inner prefix (patches a call inside the method)",
                    _ => "inner postfix (patches a call inside the method)",
                };
            }
            if (tolerance == PatchTolerance.TypeGated)
            {
                // Skipping a call to the default skips its patches: only a patch doing nothing for the skipped types stands.
                if (kind == PatchKind.Prefix && !PrecedesOurSkippingPrefixes(original, info, method))
                {
                    return "prefix running after a prefix of this mod that may skip the original";
                }
                Type[]? types = TypeGates(method, kind);
                if (types == null)
                {
                    return $"{KindText(kind, method)} not provably gated on the listener's type";
                }
                gates?.AddRange(types);
                return null;
            }
            if (kind == PatchKind.Postfix)
            {
                return null;
            }
            if (method.ReturnType != typeof(void))
            {
                return $"prefix returning {method.ReturnType.Name} (may skip the original)";
            }
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (parameter.Name == "__args")
                {
                    return "prefix taking __args (Harmony writes it back into the arguments)";
                }
                if ((parameter.ParameterType.IsByRef || parameter.IsOut) && parameter.Name != "__state")
                {
                    return $"prefix taking {parameter.Name} by reference";
                }
            }
            if (!PrecedesOurSkippingPrefixes(original, info, method))
            {
                return "void prefix running after a prefix of this mod that may skip the original";
            }
            return null;
        }
        catch (Exception e)
        {
            return "not classified: " + e.Message;
        }
    }

    private static string KindText(PatchKind kind, MethodInfo method) =>
        kind == PatchKind.Postfix ? "postfix" : method.ReturnType == typeof(bool) ? "bool prefix" : "void prefix";

    /// <summary>
    /// Whether Harmony runs this prefix before every prefix of this mod on the method that returns bool (may skip the original):
    /// Harmony's own order (priority, before/after, registration). With none of ours, true.
    /// </summary>
    private static bool PrecedesOurSkippingPrefixes(MethodBase original, HarmonyLib.Patches info, MethodInfo prefix)
    {
        Patch[] prefixes = info.Prefixes.ToArray();
        MethodInfo[] ours = prefixes.Where(p => PatchGuard.IsOurs(p.owner)).Select(PatchMethodOf)
            .Where(m => m != null && (m.ReturnType == typeof(bool) || m.GetParameters().Any(a => a.Name == "__runOriginal" && a.ParameterType.IsByRef)))
            .ToArray()!;
        if (ours.Length == 0)
        {
            return true;
        }
        List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(original, prefixes);
        int at = sorted.FindIndex(m => m.MethodHandle == prefix.MethodHandle);
        return at >= 0 && ours.All(o => sorted.FindIndex(m => m.MethodHandle == o.MethodHandle) > at);
    }

    /// <summary>Patches let stand, logged once per call site, method and set of mods (the audit reads these lines).</summary>
    private static readonly ConcurrentDictionary<(string Caller, MethodBase Method, PatchTolerance Tolerance), string[]> _toleratedFrom = new(new CallSiteComparer());
    private static readonly ConcurrentDictionary<(string Patch, MethodBase Method, PatchTolerance Tolerance), string> _toleratedLogged = new();

    private static void Tolerated(string caller, MethodBase method, PatchTolerance tolerance, string[] mods, Type[] gates)
    {
#if BENCH
        ToleratedChecks++;
#endif
        if (_toleratedFrom.TryGetValue((caller, method, tolerance), out string[]? known) && ReferenceEquals(known, mods))
        {
            return;
        }
        _toleratedFrom[(caller, method, tolerance)] = mods;
        string patch = PatchName(caller);
        string text = string.Join(", ", mods) + (gates.Length > 0 ? " gated on " + string.Join(", ", gates.Select(g => g.Name)) : "");
        if (_toleratedLogged.TryGetValue((patch, method, tolerance), out string? logged) && logged == text)
        {
            return;
        }
        _toleratedLogged[(patch, method, tolerance)] = text;
        Log.Info($"[{ModEntry.Id}] {patch}: {method.DeclaringType?.Name}.{method.Name} is also patched by {string.Join(", ", mods)} ({ToleranceText(tolerance)}"
            + (gates.Length > 0 ? $": {string.Join(", ", gates.Select(g => g.Name))} treated as overriding it" : ": run around this mod's code as without it") + "): kept");
    }

    /// <summary>The patches let stand so far: patch, method, mods (and gates). For bdperf conflicts and the test commands.</summary>
    public static IReadOnlyList<string> ToleratedReport() =>
        _toleratedLogged.Select(e => $"{e.Key.Patch}: {e.Key.Method.DeclaringType?.Name}.{e.Key.Method.Name} ({e.Key.Tolerance}) <- {e.Value}")
            .OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>
    /// One line per other mod's patch on the method: owner, kind, patch method, and its verdict under each tolerance (for the
    /// test commands and the audit: what a scenario expects).
    /// </summary>
    public static IReadOnlyList<string> Describe(MethodBase method)
    {
        var lines = new List<string>();
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        if (info == null)
        {
            return lines;
        }
        foreach ((Patch patch, PatchKind kind) in PatchesWithKind(info))
        {
            if (PatchGuard.IsOurs(patch.owner))
            {
                continue;
            }
            MethodInfo? patchMethod = PatchMethodOf(patch);
            var gates = new List<Type>();
            string surrounding = Intolerable(method, info, patch, kind, PatchTolerance.SurroundingOnly, null) is { } why ? "backs off (" + why + ")" : "tolerated";
            string gated = Intolerable(method, info, patch, kind, PatchTolerance.TypeGated, gates) is { } whyGated ? "backs off (" + whyGated + ")"
                : "tolerated, gates " + string.Join("|", gates.Select(g => g.Name));
            lines.Add($"{patch.owner} {kind} {patchMethod?.DeclaringType?.Name}.{patchMethod?.Name} priority {patch.priority}: SurroundingOnly {surrounding}; TypeGated {gated}");
        }
        return lines;
    }

    // --- Type-gated patches (TypeGated) ---

    /// <summary>Per patch method and kind: its gate types, or null when it isn't one (analysed once: a loaded method's IL doesn't change).</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Method, PatchKind Kind), Type[]?> _gates = new();

#if BENCH
    /// <summary>Patch methods analysed for a type gate, and those found gated (the effects check).</summary>
    public static int GateAnalyses => _gates.Count;
    public static int GatedMethods => _gates.Values.Count(g => g != null);
#endif

    /// <summary>
    /// The types a prefix or postfix of another mod is gated on: it provably does nothing (returns at once, a bool prefix
    /// returning true) unless <c>__instance</c> is an instance of one of them. Null when the code isn't exactly that shape:
    /// from its first instruction (nops aside), only tests <c>ldarg __instance; isinst T; [stloc n; ldloc n;] brfalse/brtrue</c>
    /// along the path a non-matching instance takes (isinst gives null there: brfalse taken, brtrue not), unconditional
    /// branches, and then the no-op exit: <c>ret</c> (void), or <c>ldc.i4.1; ret</c> (bool prefix). Nothing else on that path:
    /// no call, no field or static write, no other argument read. A postfix returning a value (passthrough) never matches.
    /// </summary>
    public static Type[]? TypeGates(MethodInfo patch, PatchKind kind)
    {
        if (kind is not (PatchKind.Prefix or PatchKind.Postfix))
        {
            return null;
        }
        return _gates.GetOrAdd((patch.MethodHandle, kind), _ => AnalyseGates(patch, kind));
    }

    private static Type[]? AnalyseGates(MethodInfo patch, PatchKind kind)
    {
        try
        {
            if (!patch.IsStatic || patch.ContainsGenericParameters)
            {
                return null;
            }
            bool boolPrefix = kind == PatchKind.Prefix && patch.ReturnType == typeof(bool);
            if (patch.ReturnType != typeof(void) && !boolPrefix)
            {
                return null;
            }
            ParameterInfo[] parameters = patch.GetParameters();
            int instance = Array.FindIndex(parameters, p => p.Name == "__instance");
            if (instance < 0 || parameters[instance].ParameterType.IsByRef || parameters[instance].ParameterType.IsValueType)
            {
                return null;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(patch);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            var gates = new List<Type>();
            var visited = new HashSet<int>();
            int index = 0;
            while (index >= 0 && index < code.Count && visited.Add(index))
            {
                MethodFingerprint.Instruction ins = code[index];
                if (ins.Op == OpCodes.Nop)
                {
                    index++;
                    continue;
                }
                if (ins.Op == OpCodes.Br || ins.Op == OpCodes.Br_S)
                {
                    index = ins.Operand is int target && at.TryGetValue(target, out int next) ? next : -1;
                    continue;
                }
                if (ins.Op == OpCodes.Ret)
                {
                    // Void exit: nothing on the stack. A bool prefix must push true first (handled below).
                    return boolPrefix ? null : gates.Count > 0 ? gates.ToArray() : null;
                }
                if (boolPrefix && ins.Op == OpCodes.Ldc_I4_1 && index + 1 < code.Count && code[index + 1].Op == OpCodes.Ret)
                {
                    return gates.Count > 0 ? gates.ToArray() : null;
                }
                // A test: ldarg __instance; isinst T; [stloc n; ldloc n;] brfalse/brtrue L.
                if (!LoadsArgument(ins, instance) || index + 2 >= code.Count || code[index + 1].Op != OpCodes.Isinst
                    || code[index + 1].Operand is not Type gate || gate.IsValueType || gate.ContainsGenericParameters)
                {
                    return null;
                }
                int branch = index + 2;
                if (StoresLocal(code[branch], out int local) && branch + 1 < code.Count && LoadsLocal(code[branch + 1], local))
                {
                    branch += 2;
                }
                if (branch >= code.Count)
                {
                    return null;
                }
                MethodFingerprint.Instruction test = code[branch];
                bool onFalse = test.Op == OpCodes.Brfalse || test.Op == OpCodes.Brfalse_S;
                bool onTrue = test.Op == OpCodes.Brtrue || test.Op == OpCodes.Brtrue_S;
                if (!onFalse && !onTrue || test.Operand is not int to || !at.TryGetValue(to, out int taken))
                {
                    return null;
                }
                gates.Add(gate);
                // Not an instance of the gate: null, so brfalse is taken and brtrue falls through.
                index = onFalse ? taken : branch + 1;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LoadsArgument(MethodFingerprint.Instruction ins, int index) =>
        (index == 0 && ins.Op == OpCodes.Ldarg_0) || (index == 1 && ins.Op == OpCodes.Ldarg_1)
        || (index == 2 && ins.Op == OpCodes.Ldarg_2) || (index == 3 && ins.Op == OpCodes.Ldarg_3)
        || ((ins.Op == OpCodes.Ldarg_S || ins.Op == OpCodes.Ldarg) && ins.Operand is int n && n == index);

    private static bool StoresLocal(MethodFingerprint.Instruction ins, out int local)
    {
        local = ins.Op == OpCodes.Stloc_0 ? 0 : ins.Op == OpCodes.Stloc_1 ? 1 : ins.Op == OpCodes.Stloc_2 ? 2 : ins.Op == OpCodes.Stloc_3 ? 3
            : (ins.Op == OpCodes.Stloc_S || ins.Op == OpCodes.Stloc) && ins.Operand is int n ? n : -1;
        return local >= 0;
    }

    private static bool LoadsLocal(MethodFingerprint.Instruction ins, int local) =>
        (local == 0 && ins.Op == OpCodes.Ldloc_0) || (local == 1 && ins.Op == OpCodes.Ldloc_1)
        || (local == 2 && ins.Op == OpCodes.Ldloc_2) || (local == 3 && ins.Op == OpCodes.Ldloc_3)
        || ((ins.Op == OpCodes.Ldloc_S || ins.Op == OpCodes.Ldloc) && ins.Operand is int n && n == local);
}
