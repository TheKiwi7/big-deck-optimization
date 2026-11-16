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
    /// Only for a call site where the original always runs and this mod's code around it keeps its effect identical (no prefix of
    /// this mod skips it), or where every prefix of this mod that may skip it backs off from any other mod's postfix itself
    /// (HookSkip's <c>Hook</c> methods that HookFilter also mirrors: the mirror checks the same method with
    /// <see cref="PrefixesOnly"/>, so with a postfix there the original runs; an invariant both call sites name): patches that
    /// run around that code and can't change how it runs are let stand.
    /// <list type="bullet">
    /// <item>A postfix whose parameters are all by value, except <c>__result</c> (the caller gets what it leaves there, as without
    /// this mod) and <c>__state</c> (its own prefix's): no other <c>ref</c>/<c>out</c> parameter (this mod's finalizers read the
    /// argument slots after it, a by-reference argument or instance is that very slot), no <c>__args</c> (Harmony writes the
    /// array back into the arguments), no <c>__runOriginal</c> (it differs where a prefix of this mod skips the original), no
    /// instance field of a reference type (<c>___name</c>, Harmony's field injection: the field's object itself, a pile's card
    /// list it could change without going through the methods this mod tracks; a value-type or string field can only be read).
    /// One by-reference parameter shape stands (2026-11-16): one of the original's <c>out</c>/<c>ref</c> parameters the postfix
    /// only reads through (its IL: each use an immediate load of the value, <see cref="OnlyReadThrough"/>).</item>
    /// <item>A prefix returning void with the same parameter rules (only <c>__state</c> by reference, no <c>__args</c>, no
    /// reference-type field), except that it may read <c>__runOriginal</c> by value: Harmony sorts it before every prefix of this
    /// mod on the method (<see cref="OrderFreePrefixAttribute"/> aside), so it sees what it would see without this mod. One
    /// sorted after them runs after this mod's tracking or replacement, or not at all once it skipped the original. Since
    /// 2026-11-16 it may also take one of the original's own parameters by reference (rewrite an argument): sorted before
    /// every prefix of this mod, it writes the slot before any code of this mod reads it.</item>
    /// </list>
    /// Not let stand: transpilers, finalizers, inner prefixes and postfixes, bool prefixes, factory patch methods (returning the
    /// patch to use), patches renaming their parameters (<c>HarmonyArgument</c>: Harmony binds the real name), a patch whose
    /// method can't be found or classified.
    /// </summary>
    SurroundingOnly,

    /// <summary>
    /// For a call site where a prefix of this mod answers for the original and skips it (HookFilter's mirrors): only the void
    /// prefixes <see cref="SurroundingOnly"/> lets stand, sorted before every prefix of this mod. No postfix: after a skipping
    /// prefix Harmony still runs them, where the game would not always (a listener exception passed on after the skip: vanilla
    /// runs no postfix), and they would see <c>__runOriginal</c> false.
    /// </summary>
    PrefixesOnly,

    /// <summary>
    /// Only for a method whose calls this mod skips for instances of some types and keeps for the others: HookSkip's
    /// <c>AbstractModel</c> defaults (skipped for listener types keeping the default), LimitlessCloneMemo's card methods one
    /// level down its clone chain (skipped with the chain, per saved card type; LimitlessHookSkip's storage card too). A prefix
    /// or postfix that provably does nothing unless <c>__instance</c> is one of some types (<see cref="PatchSafety.TypeGates"/>, a strict
    /// IL shape) is let stand, and those types are reported, so the caller treats them as overriding the default (their calls
    /// stay the game's). Not when the patch method is itself patched by anyone (its replacement isn't the IL read), its class
    /// has a static constructor (its first call would run it at another time), or it renames its parameters. Anything else
    /// backs off.
    /// </summary>
    TypeGated,

    /// <summary>
    /// For a call site where a bool prefix of this mod answers for the original from its own state and skips it, with no
    /// bookkeeping of its own that must run and no code of this mod reading around the method (PileIndex's
    /// <c>CardModel.Pile</c>): what <see cref="SurroundingOnly"/> lets stand, plus prefixes of other mods returning bool or taking
    /// <c>ref __result</c>, when Harmony sorts them before every prefix of this mod on the method (each copy). Exact: one that
    /// returns false (or leaves <c>__runOriginal</c> false) makes Harmony skip this mod's prefix as it skips the original, the
    /// caller getting its answer as without this mod; one that returns true leaves the call to this mod's prefix, whose answer
    /// is the original's. Postfixes as SurroundingOnly's (by value, <c>ref __result</c>, no <c>__runOriginal</c>: false here where
    /// this mod answered). Still refused: such prefixes sorted after this mod's, a prefix taking an argument, the instance or
    /// <c>__runOriginal</c> by reference, <c>__args</c>, reference-type fields, transpilers, finalizers, factories, renamed parameters.
    /// </summary>
    BeforeReplace,
}

/// <summary>
/// On a prefix of this mod that can't change what a call does nor what another mod's prefix sorted after it sees, so it doesn't
/// count for <see cref="PatchTolerance.SurroundingOnly"/>'s order rule:
/// <list type="bullet">
/// <item>One that only reads the instance into <c>__state</c> for its finalizer to judge a call whose tracking prefix didn't run
/// (another mod's bool prefix skipped it, or a prefix before it threw): a void prefix let stand can't skip the tracking prefix,
/// so what this one read is only used when such a prefix throws, where the original doesn't run either; the finalizer's
/// judgement then is conservative (PileIndex: a pile that changed by more than the judged card, a nested move of another card
/// included, faults the index, the game's own scan from then on).</item>
/// <item>The test tooling's read-only prefixes (Debug builds only: CombatStartTimer, PerfProbe), which only read the clock or
/// a label into statics or <c>__state</c>: counted, a test build would back off where a Release build, without them, lets
/// another mod's void prefix stand, and the tests would never run the shipped path.</item>
/// </list>
/// Such a prefix never reads the original's arguments (none does: the instance, <c>__state</c>, <c>__originalMethod</c> only):
/// another mod's prefix rewriting an argument by reference may run after it (the shape rules let one stand before every
/// other prefix of this mod).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OrderFreePrefixAttribute : Attribute
{
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
        PatchTolerance.PrefixesOnly => "void prefixes",
        PatchTolerance.TypeGated => "patches gated on the listener's type",
        PatchTolerance.BeforeReplace => "postfixes and prefixes running before this mod's",
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

    /// <summary>The type-gated check with <paramref name="allowedPatches"/> also let stand (LimitlessCloneMemo's card methods).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPatchedByOthers(MethodBase? method, MethodInfo[] allowedPatches, out Type[] gates, [CallerFilePath] string caller = "") =>
        CheckTolerant(method, allowedPatches, PatchTolerance.TypeGated, caller, out gates);

    /// <summary>
    /// A cached tolerant answer. <see cref="LoggedFor"/>: the runtime call sites it was logged for (a few share one answer:
    /// PileIndex, FastListeners and CardDbScan on CardPile.AddInternal; the preflight's patch names share it too but take no
    /// slot), compared by reference on the cached path.
    /// </summary>
    private sealed class TolerantAnswer
    {
        public readonly bool Patched;
        public readonly long CheckedAt;
        public readonly string[] Mods;
        public readonly string[] ToleratedMods;
        public readonly Type[] Gates;
        public readonly string?[] LoggedFor = new string?[4];

        public TolerantAnswer(bool patched, long checkedAt, string[] mods, string[] toleratedMods, Type[] gates)
        {
            Patched = patched;
            CheckedAt = checkedAt;
            Mods = mods;
            ToleratedMods = toleratedMods;
            Gates = gates;
        }
    }

    private static readonly ConcurrentDictionary<(MethodBase Method, long Allowed, PatchTolerance Tolerance), TolerantAnswer> _tolerant = new();

    /// <summary>A check from the code that runs the optimizations (a [CallerFilePath] source path), not the preflight (a patch name).</summary>
    private static bool IsRuntimeCaller(string caller) => caller.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

#if BENCH
    /// <summary>Checks answered "not patched" because every other mod's patch there was let stand (the effects check).</summary>
    public static long ToleratedChecks { get; private set; }

    /// <summary>
    /// Per runtime call site (its source path: the preflight passes a patch name and isn't counted), method and tolerance: the
    /// checks that let other mods' patches stand, and those that backed off. What a scenario expects from the runtime alone.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Caller, MethodBase Method, PatchTolerance Tolerance), long[]> _runtimeAnswers = new(new CallSiteComparer());

    private static void CountRuntime(string caller, MethodBase method, PatchTolerance tolerance, bool patched)
    {
        if (!IsRuntimeCaller(caller))
        {
            return;
        }
        long[] counts = _runtimeAnswers.GetOrAdd((caller, method, tolerance), _ => new long[2]);
        System.Threading.Interlocked.Increment(ref counts[patched ? 1 : 0]);
    }

    /// <summary>
    /// The runtime checks of this patch name on this method (<c>Type.Method</c>), every tolerance and the strict checks' back-offs
    /// together: answers that let other mods' patches stand, and back-offs. The preflight isn't counted: what a scenario asserts
    /// to show the runtime check itself ran (the script's <c>runtimebackoff</c>).
    /// </summary>
    public static (long Kept, long BackedOff) RuntimeCounts(string patch, string typeAndMethod)
    {
        long kept = 0, backedOff = 0;
        foreach (KeyValuePair<(string Caller, MethodBase Method, PatchTolerance Tolerance), long[]> entry in _runtimeAnswers.ToArray())
        {
            if (PatchName(entry.Key.Caller) == patch && $"{entry.Key.Method.DeclaringType?.Name}.{entry.Key.Method.Name}" == typeAndMethod)
            {
                kept += entry.Value[0];
                backedOff += entry.Value[1];
            }
        }
        return (kept, backedOff);
    }

    /// <summary>One line per runtime call site, method and tolerance: "Patch Type.Method (Tolerance) kept N backed off M".</summary>
    public static IReadOnlyList<string> RuntimeReport() =>
        _runtimeAnswers.ToArray().GroupBy(e => (Patch: PatchName(e.Key.Caller), e.Key.Method, e.Key.Tolerance))
            .Select(g => $"{g.Key.Patch} {g.Key.Method.DeclaringType?.Name}.{g.Key.Method.Name} ({g.Key.Tolerance}) kept {g.Sum(e => e.Value[0])} backed off {g.Sum(e => e.Value[1])}")
            .OrderBy(line => line, StringComparer.Ordinal).ToList();
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

    /// <summary>
    /// The shape rules' switch ("Foreign patches judged by their shape", DESIGN): false makes every tolerant check the strict
    /// one (no gates either), as before those rules, for a release that leaves them out until their in-game proof
    /// (docs/RELEASE.md). The rest of their commits (the tracking prefixes' late priorities, the order-free watches) changes
    /// nothing without them. "Before those rules" is the fourth pass's state (a501424), not v1.0.3's: that pass deepened
    /// LimitlessCloneMemo's clone chain (CardModel.AddKeyword, RemoveKeyword, get_MaxUpgradeLevel among others), so with the
    /// switch off a strict patch there (CardAscension's AddKeyword prefix) turns LimitlessCloneMemo and LimitlessHookSkip off
    /// for every card, as before the rules; only the type gates keep them for other cards.
    /// </summary>
    internal static readonly bool ShapeRules = true;

    internal static bool CheckTolerant(MethodBase? method, MethodInfo[] allowedPatches, PatchTolerance tolerance, string caller, out Type[] gates)
    {
        gates = Type.EmptyTypes;
        if (tolerance == PatchTolerance.None || !ShapeRules)
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
#if BENCH
                if (IsRuntimeCaller(caller))
                {
                    ToleratedChecks++;
                }
#endif
                // Logged once per call site and answer (Tolerated dedupes too): the hot path compares the few sites it was logged for.
                if (!LoggedFor(cached, caller))
                {
                    Tolerated(caller, method, tolerance, cached.ToleratedMods, cached.Gates);
                }
            }
#if BENCH
            CountRuntime(caller, method, tolerance, cached.Patched);
#endif
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
        string[] all = WithHidden(method, ModNames(others.Select(o => o.Patch)));
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
        string[] mods = WithHidden(method, ModNames(remaining));
        string[] toleratedMods = ModNames(tolerated);
        Type[] gateTypes = mods.Length > 0 ? Type.EmptyTypes : gateList.Distinct().ToArray();
        RecordOnce(caller, method, mods, tolerance);
        var answer = new TolerantAnswer(mods.Length > 0, now, mods, toleratedMods, gateTypes);
        _tolerant[(method, key, tolerance)] = answer;
        if (mods.Length == 0 && toleratedMods.Length > 0)
        {
#if BENCH
            if (IsRuntimeCaller(caller))
            {
                ToleratedChecks++;
            }
#endif
            Tolerated(caller, method, tolerance, toleratedMods, gateTypes);
            LoggedFor(answer, caller);
        }
#if BENCH
        if (all.Length > 0)
        {
            CountRuntime(caller, method, tolerance, mods.Length > 0);
        }
#endif
        gates = gateTypes;
        return mods.Length > 0;
    }

    /// <summary>
    /// Whether this answer was already logged for this call site; if not, notes it (in a free slot, else over the first one:
    /// more sites than slots only means one more <see cref="Tolerated"/> lookup, which dedupes by itself). Only the runtime
    /// call sites take a slot: the preflight (a patch name, rare) always goes to <see cref="Tolerated"/>, so its names never
    /// push a hot site out of the slots.
    /// </summary>
    private static bool LoggedFor(TolerantAnswer answer, string caller)
    {
        // By reference first, the hot path's whole cost: only runtime call sites are ever in the slots (a preflight's patch
        // name never matches one), so the call site's kind is only looked at before taking a slot.
        string?[] sites = answer.LoggedFor;
        int free = -1;
        for (int i = 0; i < sites.Length; i++)
        {
            string? site = sites[i];
            if (ReferenceEquals(site, caller))
            {
                return true;
            }
            if (site == null && free < 0)
            {
                free = i;
            }
        }
        if (!IsRuntimeCaller(caller))
        {
            return false;
        }
        sites[free >= 0 ? free : 0] = caller;
        return false;
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
        string[] mods = WithHidden(method, ModNames(info == null ? Enumerable.Empty<Patch>() : AllPatches(info).Where(p => !PatchGuard.IsOurs(p.owner))));
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
            // What the patch method's own shape refuses can't change while it is loaded: worked out once per method, kind and
            // tolerance (attributes read as data: no other mod's attribute constructor runs). Only the order of the prefixes and
            // whether the patch method is itself patched are looked at again at each recheck.
            if (_shapes.GetOrAdd((method.MethodHandle, kind, tolerance), static (key, m) => ShapeRefusal(m, key.Kind, key.Tolerance), method) is { } refused)
            {
                return refused;
            }
            if (tolerance != PatchTolerance.TypeGated && ArgumentRefusal(original, method, kind) is { } argument)
            {
                return argument;
            }
            if (tolerance == PatchTolerance.TypeGated)
            {
                // Skipping a call to the default skips its patches: only a patch doing nothing for the skipped types stands.
                if (kind == PatchKind.Prefix && PrecedenceRefusal(original, info, method) is { } late)
                {
                    return late;
                }
                // The code that runs is the patch method's replacement when anyone patches it: not the IL read below.
                if (Harmony.GetPatchInfo(method) is { } own && AllPatches(own).Any())
                {
                    return $"{KindText(kind, method)} whose own method is patched";
                }
                Type[]? types = TypeGates(method, kind);
                if (types == null)
                {
                    return $"{KindText(kind, method)} not provably gated on the listener's type";
                }
                gates?.AddRange(types);
                return null;
            }
            return kind == PatchKind.Postfix ? null : PrecedenceRefusal(original, info, method);
        }
        catch (Exception e)
        {
            return "not classified: " + e.Message;
        }
    }

    /// <summary>Per patch method, kind and tolerance: what its own shape refuses (<see cref="ShapeRefusal"/>), null when nothing.</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Method, PatchKind Kind, PatchTolerance Tolerance), string?> _shapes = new();

    /// <summary>
    /// The part of <see cref="Intolerable"/> a loaded patch method's own code decides (factory, renamed parameters, return type,
    /// parameters), or null. Throws when it can't be read (Intolerable then refuses, and asks again at the next recheck).
    /// </summary>
    private static string? ShapeRefusal(MethodInfo method, PatchKind kind, PatchTolerance tolerance)
    {
        if (IsFactory(method))
        {
            return "factory patch method (Harmony runs the method it returns)";
        }
        // Harmony binds each parameter by its real name: a HarmonyArgument on it, its method or its class can turn a harmless
        // looking one into __args, __state, __instance or an argument by reference (InjectedParameter.CalculateRealName).
        if (RenamesParameters(method))
        {
            return "patch renaming its parameters (HarmonyArgument)";
        }
        if (tolerance == PatchTolerance.TypeGated)
        {
            return null; // The gate's IL decides (TypeGates), with the order and own-patch checks at each recheck.
        }
        if (kind == PatchKind.Postfix)
        {
            // BeforeReplace: postfixes as SurroundingOnly's (below).
            if (tolerance == PatchTolerance.PrefixesOnly)
            {
                return "postfix (a prefix of this mod skips the original here: Harmony runs postfixes where the game may not)";
            }
            ParameterInfo[] postfixParameters = method.GetParameters();
            foreach (ParameterInfo parameter in postfixParameters)
            {
                if (parameter.Name == "__args")
                {
                    return "postfix taking __args (Harmony writes it back into the arguments)";
                }
                if (parameter.Name == "__runOriginal")
                {
                    return "postfix reading __runOriginal";
                }
                // An argument by reference only read through (every use of it an immediate load of the value: never written,
                // never passed on as an address): Intolerable also requires one of the original's out/ref parameters (its value
                // after the body, read as the game's caller would). Anything else by reference stays refused.
                if ((parameter.ParameterType.IsByRef || parameter.IsOut) && parameter.Name is not ("__state" or "__result")
                    && !(IsArgumentName(parameter.Name) && OnlyReadThrough(method, parameter.Position)))
                {
                    return $"postfix taking {parameter.Name} by reference";
                }
                if (InjectsObjectField(parameter))
                {
                    return $"postfix taking the instance's field {parameter.Name!.Substring(3)} (Harmony's ___ injection: the object itself)";
                }
            }
            return null;
        }
        // BeforeReplace: a prefix may answer for the original (return bool, set __result) when it runs before this mod's
        // (PrecedenceRefusal, asked by Intolerable): it skips this mod's answer exactly as it skips the original.
        bool answers = tolerance == PatchTolerance.BeforeReplace;
        if (method.ReturnType != typeof(void) && !(answers && method.ReturnType == typeof(bool)))
        {
            return $"prefix returning {method.ReturnType.Name} (may skip the original)";
        }
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            if (parameter.Name == "__args")
            {
                return "prefix taking __args (Harmony writes it back into the arguments)";
            }
            // An argument by reference (its real name one of the original's parameters: Intolerable checks it) stands on a prefix
            // Harmony runs before every prefix of this mod (Intolerable's PrecedenceRefusal): it writes the argument slot before
            // any code of this mod reads it, so this mod's prefixes, the body (or this mod's mirror of it) and its finalizers all
            // read the value it leaves, as the original would with the caller passing it. The instance, __runOriginal, Harmony's
            // other special names and fields by reference stay refused.
            if ((parameter.ParameterType.IsByRef || parameter.IsOut) && parameter.Name != "__state" && !(answers && parameter.Name == "__result")
                && !IsArgumentName(parameter.Name))
            {
                return $"prefix taking {parameter.Name} by reference";
            }
            if (InjectsObjectField(parameter))
            {
                return $"prefix taking the instance's field {parameter.Name!.Substring(3)} (Harmony's ___ injection: the object itself)";
            }
        }
        return null;
    }

    /// <summary>
    /// A parameter Harmony binds to one of the original's arguments by name: no special name (<c>__instance</c>, <c>__result</c>,
    /// <c>__state</c>, <c>__0</c>...) nor field (<c>___name</c>). Whether the original has it is <see cref="ArgumentRefusal"/>'s.
    /// </summary>
    private static bool IsArgumentName(string? name) => !string.IsNullOrEmpty(name) && !name.StartsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// For a patch's by-reference arguments the shape rules let stand (a prefix's: any of the original's parameters; a
    /// postfix's: one of its out or ref parameters, only read through): why not, or null. Asked per original (the shape
    /// itself is cached per patch method). Harmony would refuse to patch with a name the original lacks; checked anyway.
    /// </summary>
    private static string? ArgumentRefusal(MethodBase original, MethodInfo patch, PatchKind kind)
    {
        ParameterInfo[] arguments = original.GetParameters();
        foreach (ParameterInfo parameter in patch.GetParameters())
        {
            if (!(parameter.ParameterType.IsByRef || parameter.IsOut) || !IsArgumentName(parameter.Name))
            {
                continue;
            }
            ParameterInfo? argument = Array.Find(arguments, a => a.Name == parameter.Name);
            if (argument == null)
            {
                return $"{KindText(kind, patch)} taking {parameter.Name} by reference (not one of the original's parameters)";
            }
            if (kind == PatchKind.Postfix && !argument.ParameterType.IsByRef)
            {
                // A by-value argument's own slot, which this mod's finalizers read after it (the message the shape gave before).
                return $"postfix taking {parameter.Name} by reference";
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the patch method only reads through its by-reference parameter at <paramref name="position"/>: every
    /// instruction naming that argument is <c>ldarg</c> immediately followed by a load through the address (<c>ldind.*</c>,
    /// <c>ldobj</c>), so the address is never stored through, kept, compared or passed on (no <c>starg</c>, no <c>ldarga</c>, no
    /// <c>dup</c>). A static patch method's IL only (an instance one: false). False when the IL can't be read.
    /// </summary>
    private static bool OnlyReadThrough(MethodInfo patch, int position)
    {
        try
        {
            if (!patch.IsStatic)
            {
                return false;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(patch);
            for (int i = 0; i < code.Count; i++)
            {
                OpCode op = code[i].Op;
                bool names = LoadsArgument(code[i], position)
                    || ((op == OpCodes.Starg || op == OpCodes.Starg_S || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) && code[i].Operand is int n && n == position);
                if (!names)
                {
                    continue;
                }
                if (!LoadsArgument(code[i], position) || i + 1 >= code.Count || !LoadsThrough(code[i + 1].Op))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LoadsThrough(OpCode op) =>
        op == OpCodes.Ldobj || op == OpCodes.Ldind_I || op == OpCodes.Ldind_I1 || op == OpCodes.Ldind_I2 || op == OpCodes.Ldind_I4
        || op == OpCodes.Ldind_I8 || op == OpCodes.Ldind_R4 || op == OpCodes.Ldind_R8 || op == OpCodes.Ldind_Ref || op == OpCodes.Ldind_U1
        || op == OpCodes.Ldind_U2 || op == OpCodes.Ldind_U4;

    /// <summary>
    /// A by-value parameter Harmony fills with an instance field (its name starts with three underscores) of a reference type
    /// other than string: the field's object itself, which the patch could change behind the methods this mod tracks (a pile's
    /// card list). A value-type or string field can only be read; a by-reference one is refused as any other.
    /// </summary>
    private static bool InjectsObjectField(ParameterInfo parameter) =>
        parameter.Name is { } name && name.StartsWith("___", StringComparison.Ordinal) && !parameter.ParameterType.IsByRef
        && !parameter.ParameterType.IsValueType && parameter.ParameterType != typeof(string);

    private static string KindText(PatchKind kind, MethodInfo method) =>
        kind == PatchKind.Postfix ? "postfix" : method.ReturnType == typeof(bool) ? "bool prefix" : "void prefix";

    /// <summary>A Harmony factory: a static method of one parameter returning the patch to use (<c>Patch.GetMethod</c> runs it).</summary>
    internal static bool IsFactory(MethodInfo method) =>
        (method.ReturnType == typeof(MethodInfo) || method.ReturnType == typeof(DynamicMethod)) && method.IsStatic && method.GetParameters().Length == 1;

    /// <summary>
    /// Any <c>HarmonyArgument</c> (matched by type name, as Harmony does: any copy of it) on the method, its parameters, its
    /// declaring type or any base of it (Harmony reads the class's inherited ones; every base counted, a little stricter). Read
    /// as attribute data: no attribute is built, so no other mod's attribute constructor runs.
    /// </summary>
    private static bool RenamesParameters(MethodInfo method)
    {
        static bool Any(IEnumerable<CustomAttributeData> attributes) => attributes.Any(a => a.AttributeType.Name == "HarmonyArgument");
        if (Any(method.GetCustomAttributesData()) || method.GetParameters().Any(p => Any(p.GetCustomAttributesData())))
        {
            return true;
        }
        for (Type? type = method.DeclaringType; type != null; type = type.BaseType)
        {
            if (Any(type.GetCustomAttributesData()))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Harmony's order of a method's prefixes as this mod's rule needs it: which other mods' prefixes run before every prefix
    /// of this mod (<see cref="OrderFreePrefixAttribute"/> ones aside; all of them when the method has none), or why that can't
    /// be told. Kept per method while its prefix list (owners, indexes, priorities, before/after, methods) stays the same, so
    /// Harmony's sorter runs once per change of that list, not at each 2 s recheck.
    /// </summary>
    private sealed record Precedence(string Key, bool AllPrecede, HashSet<RuntimeMethodHandle> Preceding, string? Refused);

    private static readonly ConcurrentDictionary<MethodBase, Precedence> _precedence = new();

    /// <summary>Null when Harmony runs this prefix before every prefix of this mod on the method (each of its copies), else why not.</summary>
    private static string? PrecedenceRefusal(MethodBase original, HarmonyLib.Patches info, MethodInfo prefix)
    {
        Precedence order = PrecedenceOf(original, info);
        if (order.Refused != null)
        {
            return order.Refused;
        }
        return order.AllPrecede || order.Preceding.Contains(prefix.MethodHandle) ? null
            : prefix.ReturnType == typeof(bool) ? "bool prefix running after a prefix of this mod (after its replacement, or not at all once it skipped the original)"
            : "void prefix running after a prefix of this mod (after its tracking or replacement, or not at all once it skipped the original)";
    }

    private static Precedence PrecedenceOf(MethodBase original, HarmonyLib.Patches info)
    {
        Patch[] prefixes = info.Prefixes.ToArray();
        var key = new System.Text.StringBuilder();
        foreach (Patch p in prefixes)
        {
            key.Append(p.owner).Append('|').Append(p.index).Append('|').Append(p.priority).Append('|')
                .Append(_patchToken?.GetValue(p)).Append('|').Append(_patchModule?.GetValue(p)).Append('|')
                .Append(string.Join(",", p.before ?? Array.Empty<string>())).Append('|').Append(string.Join(",", p.after ?? Array.Empty<string>())).Append(';');
        }
        string text = key.ToString();
        if (_precedence.TryGetValue(original, out Precedence? known) && known.Key == text)
        {
            return known;
        }
        Precedence computed = ComputePrecedence(original, prefixes, text);
        _precedence[original] = computed;
        return computed;
    }

    private static Precedence ComputePrecedence(MethodBase original, Patch[] prefixes, string key)
    {
        var none = new HashSet<RuntimeMethodHandle>();
        var methods = new MethodInfo[prefixes.Length];
        var ours = new List<RuntimeMethodHandle>();
        for (int i = 0; i < prefixes.Length; i++)
        {
            MethodInfo? method = PatchMethodOf(prefixes[i]);
            if (method == null)
            {
                return new Precedence(key, false, none, "a prefix's method not found (Harmony's order not known)");
            }
            // Harmony's sort calls a factory to get the patch it returns: never run another mod's code here.
            if (IsFactory(method))
            {
                return new Precedence(key, false, none, "a prefix made by a factory method (Harmony's order not known without running it)");
            }
            methods[i] = method;
            if (PatchGuard.IsOurs(prefixes[i].owner) && !method.IsDefined(typeof(OrderFreePrefixAttribute), false))
            {
                ours.Add(method.MethodHandle);
            }
        }
        if (ours.Count == 0)
        {
            return new Precedence(key, true, none, null);
        }
        // The sorter hashes each patch's method: given the resolved ones, it doesn't search every loaded module for them
        // (these Patch objects are Harmony.GetPatchInfo's own copies, made for this call).
        for (int i = 0; i < prefixes.Length; i++)
        {
            prefixes[i].PatchMethod = methods[i];
        }
        List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(original, prefixes);
        int firstOurs = int.MaxValue;
        foreach (RuntimeMethodHandle handle in ours)
        {
            int at = sorted.FindIndex(m => m.MethodHandle == handle);
            if (at < 0)
            {
                return new Precedence(key, false, none, "this mod's prefix not found in Harmony's order");
            }
            firstOurs = Math.Min(firstOurs, at);
        }
        var preceding = new HashSet<RuntimeMethodHandle>();
        for (int i = 0; i < prefixes.Length; i++)
        {
            if (PatchGuard.IsOurs(prefixes[i].owner))
            {
                continue;
            }
            RuntimeMethodHandle handle = methods[i].MethodHandle;
            // Every copy counts (PatchInfo.Add doesn't deduplicate: a method registered twice has two places): the last decides.
            int last = sorted.FindLastIndex(m => m.MethodHandle == handle);
            if (last >= 0 && last < firstOurs)
            {
                preceding.Add(handle);
            }
        }
        return new Precedence(key, false, preceding, null);
    }

    /// <summary>Patches let stand, logged once per call site, method and set of mods (the audit reads these lines).</summary>
    private static readonly ConcurrentDictionary<(string Caller, MethodBase Method, PatchTolerance Tolerance), string[]> _toleratedFrom = new(new CallSiteComparer());
    private static readonly ConcurrentDictionary<(string Patch, MethodBase Method, PatchTolerance Tolerance), string> _toleratedLogged = new();

    private static void Tolerated(string caller, MethodBase method, PatchTolerance tolerance, string[] mods, Type[] gates)
    {
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
    public static IReadOnlyList<string> Describe(MethodBase method) =>
        Harmony.GetPatchInfo(method) is { } info ? Describe(method, info) : new List<string>();

    /// <summary><see cref="Describe(MethodBase)"/> over a given patch list (also the test tooling's synthetic ones).</summary>
    internal static IReadOnlyList<string> Describe(MethodBase method, HarmonyLib.Patches info)
    {
        var lines = new List<string>();
        foreach ((Patch patch, PatchKind kind) in PatchesWithKind(info))
        {
            if (PatchGuard.IsOurs(patch.owner))
            {
                continue;
            }
            MethodInfo? patchMethod = PatchMethodOf(patch);
            var gates = new List<Type>();
            string Verdict(PatchTolerance tolerance) => Intolerable(method, info, patch, kind, tolerance, null) is { } why ? "backs off (" + why + ")" : "tolerated";
            string gated = Intolerable(method, info, patch, kind, PatchTolerance.TypeGated, gates) is { } whyGated ? "backs off (" + whyGated + ")"
                : "tolerated, gates " + string.Join("|", gates.Select(g => g.Name));
            lines.Add($"{patch.owner} {kind} {patchMethod?.DeclaringType?.Name}.{patchMethod?.Name} priority {patch.priority}: SurroundingOnly {Verdict(PatchTolerance.SurroundingOnly)}; "
                + $"PrefixesOnly {Verdict(PatchTolerance.PrefixesOnly)}; TypeGated {gated}; BeforeReplace {Verdict(PatchTolerance.BeforeReplace)}");
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
    /// no call, no field or static write, no other argument read. A postfix returning a value (passthrough) never matches, nor
    /// a method whose class has a static constructor. <c>__instance</c> is found by its declared name: <see cref="Intolerable"/>
    /// refuses a patch renaming its parameters (<c>HarmonyArgument</c>) before asking, and a patch whose own method is patched.
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
            // A static constructor runs at the class's first use (precise without beforefieldinit, at or before a static field
            // read with it): its first call by any listener without this mod, the first gate-type call (or never) with it.
            if (patch.DeclaringType == null || patch.DeclaringType.TypeInitializer != null)
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

    internal static bool LoadsArgument(MethodFingerprint.Instruction ins, int index) =>
        (index == 0 && ins.Op == OpCodes.Ldarg_0) || (index == 1 && ins.Op == OpCodes.Ldarg_1)
        || (index == 2 && ins.Op == OpCodes.Ldarg_2) || (index == 3 && ins.Op == OpCodes.Ldarg_3)
        || ((ins.Op == OpCodes.Ldarg_S || ins.Op == OpCodes.Ldarg) && ins.Operand is int n && n == index);

    internal static bool StoresLocal(MethodFingerprint.Instruction ins, out int local)
    {
        local = ins.Op == OpCodes.Stloc_0 ? 0 : ins.Op == OpCodes.Stloc_1 ? 1 : ins.Op == OpCodes.Stloc_2 ? 2 : ins.Op == OpCodes.Stloc_3 ? 3
            : (ins.Op == OpCodes.Stloc_S || ins.Op == OpCodes.Stloc) && ins.Operand is int n ? n : -1;
        return local >= 0;
    }

    internal static bool LoadsLocal(MethodFingerprint.Instruction ins, int local) =>
        (local == 0 && ins.Op == OpCodes.Ldloc_0) || (local == 1 && ins.Op == OpCodes.Ldloc_1)
        || (local == 2 && ins.Op == OpCodes.Ldloc_2) || (local == 3 && ins.Op == OpCodes.Ldloc_3)
        || ((ins.Op == OpCodes.Ldloc_S || ins.Op == OpCodes.Ldloc) && ins.Operand is int n && n == local);
}
