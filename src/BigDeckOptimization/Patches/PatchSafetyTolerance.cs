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
    /// this mod skips it), or where every prefix of this mod that may skip it answers exactly as the original, throws from the
    /// prefix what the original's own code would throw (Harmony then runs no postfix, as the game runs none) and backs off
    /// itself from the postfixes that could see the difference (HookSkip's <c>Hook</c> methods that HookFilter also mirrors: the
    /// mirror checks the same method with <see cref="BeforeReplace"/>, which refuses postfixes reading <c>__runOriginal</c> or
    /// <c>__args</c> or writing by reference, so with one of those there the original runs; an invariant both call sites name):
    /// patches that run around that code and can't change how it runs are let stand.
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
    /// <c>CardModel.Pile</c>), or answers exactly as the original's code would and throws from the prefix the exception that code
    /// would throw at the same point (HookFilter's mirrors, since 2026-11-18: a listener's own exception rethrown, so Harmony runs
    /// no postfix then, as the game runs none): what <see cref="SurroundingOnly"/> lets stand, plus prefixes of other mods returning bool or taking
    /// <c>ref __result</c>, when Harmony sorts them before every prefix of this mod on the method (each copy). Exact: one that
    /// returns false (or leaves <c>__runOriginal</c> false) makes Harmony skip this mod's prefix as it skips the original, the
    /// caller getting its answer as without this mod; one that returns true leaves the call to this mod's prefix, whose answer
    /// is the original's. Postfixes as SurroundingOnly's (by value, <c>ref __result</c>, no <c>__runOriginal</c>: false here where
    /// this mod answered). Still refused: such prefixes sorted after this mod's, a prefix taking the instance or
    /// <c>__runOriginal</c> by reference, <c>__args</c>, reference-type fields, transpilers, finalizers, factories, renamed parameters.
    /// A prefix taking one of the original's own arguments by reference stands as under SurroundingOnly (sorted before every
    /// prefix of this mod: it writes the slot before any code of this mod reads it; CardModel.Pile has no argument anyway).
    /// </summary>
    BeforeReplace,

    /// <summary>
    /// For a call site where a bool prefix of this mod skips the original and leaves a null <c>__result</c>, the answer the
    /// original itself gives in a mode its callers already handle (CardFlightLimit's skipped soul: <c>NCardFlyShuffleVfx.Create</c>
    /// returns null in the game's test mode): only postfixes that provably do nothing for a null <c>__result</c> stand
    /// (<see cref="PatchSafety.NullResultGated"/>, a strict IL shape), so on a skipped call they run as no-ops. What such a
    /// postfix does with a real result is not judged here: the caller states why leaving that out is safe (CardFlightLimit: the
    /// flight's own display, DESIGN "Needless back-offs made safe"). Not when the postfix method is itself patched (asked at each
    /// recheck) or renames its parameters; no type initializer is judged (the postfix runs at every call, the skipped ones
    /// included, and the first call is never a skip: B1, re-audit round 2 of 2026-11-17). Every prefix,
    /// transpiler, finalizer, inner patch and factory backs off.
    /// </summary>
    NullSkipped,

    /// <summary>
    /// For a method whose only code of this mod is a transpiler that keeps what the body does (HookSkip's <c>Hook</c> methods:
    /// its walk redirect drops only listeners whose calls are no-ops), plus at most HookFilter's mirror (which checks the same
    /// method with <see cref="BeforeReplace"/> itself, once per ListenerCache scope, and runs the original whenever anything that
    /// tolerance refuses is there: a postfix reading <c>__runOriginal</c> or <c>__args</c> or writing by reference, a bool prefix
    /// sorted after the mirror, a finalizer), this mod's read-only guard finalizer and order-free prefixes
    /// (<see cref="OrderFreePrefixAttribute"/>: LimitlessHookSkip's sampling prefixes, the test builds' probes): prefixes and
    /// postfixes of every shape stand (bool,
    /// <c>ref __result</c>, arguments written by reference, <c>__args</c>, <c>__runOriginal</c>, factories, renamed parameters).
    /// A prefix decides whether and with which arguments the body runs, as without this mod; a postfix sees the same
    /// <c>__result</c>, out values and <c>__runOriginal</c>, the dropped iterations being no-ops. Finalizers, transpilers (the
    /// caller's IL judge aside) and inner patches back off. The caller checks that this mod's own patches there are only those
    /// (HookSkip.OwnPatchesOnly), else it asks <see cref="SurroundingOnly"/>.
    /// </summary>
    TranspiledOnly,

    /// <summary>
    /// For a call site where a bool prefix of this mod skips the original without answering for it, leaving out its effect by
    /// design (ReplayStateSkip's <c>CombatReplayWriter.RecordChecksum</c>: the state left out of the debug recording, 2026-11-18):
    /// the prefixes <see cref="BeforeReplace"/> lets stand, Harmony running them before every prefix of this mod (a void one runs as
    /// before the original; a bool one returning false skips this mod's prefix as it skips the original, one returning true
    /// leaves the call to it). No postfix: after this mod's skip it would run without the original's effect (a recording without
    /// the entry), where the game runs it after one. Transpilers, finalizers, inner patches, factories, renamed parameters and
    /// prefixes sorted after this mod's back off.
    /// </summary>
    BeforeSkip,

    /// <summary>
    /// For a call site where a postfix of this mod rewrites the method's result and nothing else of this mod is there
    /// (JoinMismatchNote's <c>NetErrorInfo.GetErrorString</c>, display only, 2026-11-18): other mods' postfixes Harmony runs before
    /// every postfix of this mod (this mod's at <see cref="Priority.Last"/>) see the game's result as without this mod, and this
    /// mod's postfix reads what they leave. Every prefix, transpiler, finalizer, inner patch, factory, patch renaming its
    /// parameters and postfix sorted after this mod's backs off.
    /// </summary>
    PostfixesBefore,
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
/// <item>The listener walks' pending-filter prefixes (FastListeners' <c>TakeRunFilter</c>, HookSkip's <c>DropPendingFilter</c>,
/// both <c>Priority.First</c>, no argument, never skipping the original): they only move HookSkip's filter for this walk from
/// the pending slot. Another mod's void prefix sorted after them can only make a nested walk take or drop it, which leaves this
/// walk unfiltered (every listener: the game's own list), never filtered for another walk.</item>
/// <item>LimitlessHookSkip's sampling prefixes (<c>Sample0</c>-<c>Sample3</c> on four async <c>Hook</c> methods, Release builds
/// included): no argument, void, never skipping the hook; they only read the combat's piles and Limitless's stacked
/// enchantments to compare what a dropped forwarder would have done, and a difference only turns LimitlessHookSkip off (its
/// own state). Harmony runs them whatever another mod's prefix decided (they don't affect the original), and nothing they do
/// is seen by another mod's prefix or by the body.</item>
/// </list>
/// Such a prefix never reads the original's arguments (none does: the instance, <c>__state</c>, <c>__originalMethod</c> only):
/// another mod's prefix rewriting an argument by reference may run after it (the shape rules let one stand before every
/// other prefix of this mod).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OrderFreePrefixAttribute : Attribute
{
}

/// <summary>
/// On a postfix of the test tooling (Debug builds only: CombatStartTimer, PerfProbe) that writes nothing another patch or the
/// game sees: no <c>ref __result</c>, no argument by reference, only the clock and a label into its own statics, at most a
/// continuation on the returned <c>Task</c> (taken by value) that records the same. HookSkip.OwnPatchesOnly doesn't count it:
/// counted, a test build would take SurroundingOnly on <c>Hook.AfterRoomEntered</c> and <c>Hook.BeforeCombatStart</c> where a
/// Release build, without these postfixes, takes TranspiledOnly, so the tests would report back-offs players never have
/// (another mod's bool prefix or <c>__args</c> postfix there) and never run the shipped path.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ReadOnlyProbePostfixAttribute : Attribute
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
        PatchTolerance.TypeGated => "patches gated on the listener's type",
        PatchTolerance.BeforeReplace => "postfixes and prefixes running before this mod's",
        PatchTolerance.BeforeSkip => "prefixes running before this mod's",
        PatchTolerance.PostfixesBefore => "postfixes running before this mod's",
        PatchTolerance.NullSkipped => "postfixes doing nothing for a null result",
        PatchTolerance.TranspiledOnly => "prefixes and postfixes around the transpiled body",
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
            // TranspiledOnly: no prefix or finalizer of this mod skips or reads around the body there (the caller checks this
            // mod's own patches; HookFilter's mirror checks BeforeReplace itself and runs the original when one of these is
            // there), so any prefix or postfix runs exactly as without this mod: no shape to judge, factories and renamed
            // parameters included (Harmony binds and builds them the same with or without this mod).
            if (tolerance == PatchTolerance.TranspiledOnly)
            {
                return null;
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
            if (tolerance == PatchTolerance.NullSkipped)
            {
                // ShapeRefusal let only a null-gated postfix through. The code that runs is its replacement when anyone patches
                // it, not the IL read: asked at each recheck.
                return Harmony.GetPatchInfo(method) is { } patchedNull && AllPatches(patchedNull).Any()
                    ? $"{KindText(kind, method)} whose own method is patched (judged a no-op for a null result from its IL)" : null;
            }
            // A postfix let stand because its IL only reads through a by-reference argument (OnlyReadThrough): the code that runs
            // is its replacement when anyone patches it, not the IL read. Asked at each recheck, as TypeGated's gates are.
            if (kind == PatchKind.Postfix && tolerance != PatchTolerance.TypeGated && ReadsThroughByReference(method)
                && Harmony.GetPatchInfo(method) is { } patchedPostfix && AllPatches(patchedPostfix).Any())
            {
                return $"{KindText(kind, method)} whose own method is patched (its by-reference argument judged read-only from its IL)";
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
                Type[]? types = TypeGates(method, kind, original);
                if (types == null)
                {
                    ListenerSlot? slot = ListenerOf(original);
                    string listener = slot?.Parameter ?? (ModelInstance(original) ? "__instance" : "");
                    if (kind == PatchKind.Prefix && slot is { PrefixesGated: false })
                    {
                        return $"{KindText(kind, method)} (no prefix gated on the listener stands on this helper: it may read state the skipped calls create)";
                    }
                    return GateRefusal(method, kind, original, listener != "__instance" ? $" (its argument {(listener.Length > 0 ? listener : "none")})" : "");
                }
                gates?.AddRange(types);
                return null;
            }
            if (kind == PatchKind.Postfix)
            {
                // PostfixesBefore: only one Harmony runs before every postfix of this mod (it sees the original's result).
                return tolerance == PatchTolerance.PostfixesBefore ? PrecedenceRefusal(original, info, method, PatchKind.Postfix) : null;
            }
            return PrecedenceRefusal(original, info, method);
        }
        catch (Exception e)
        {
            return "not classified: " + e.Message;
        }
    }

    /// <summary>A postfix taking one of the original's arguments by reference (what ShapeRefusal lets stand only through <see cref="OnlyReadThrough"/>).</summary>
    private static bool ReadsThroughByReference(MethodInfo method) => method.GetParameters()
        .Any(p => (p.ParameterType.IsByRef || p.IsOut) && p.Name is not ("__state" or "__result") && IsArgumentName(p.Name));

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
        if (tolerance == PatchTolerance.PostfixesBefore)
        {
            // Any postfix shape: one sorted before this mod's runs as without this mod (the order, asked at each recheck).
            return kind == PatchKind.Postfix ? null : $"{KindText(kind, method)} (a postfix of this mod rewrites the result here: only postfixes running before it stand)";
        }
        if (tolerance == PatchTolerance.NullSkipped)
        {
            // A prefix would run before (or instead of) this mod's skip, with no result to gate on; a postfix stands only when
            // its IL does nothing for a null __result (the own-patch check at each recheck, in Intolerable).
            return kind != PatchKind.Postfix ? $"{KindText(kind, method)} (a prefix of this mod skips the original here)"
                // No initializer check here (B1, re-audit round 2 of 2026-11-17): on a skipped call Harmony still runs the
                // postfix (on the null __result the skip leaves), so it runs as many times as in the game, and its first call is
                // never a skip (the burst): it is compiled and first called when it is without this mod, so its own class's
                // initializer and those its compilation may run run then too. What its non-null path reaches (a class first used
                // there) is what it does with a real soul: the premise's (CardFlightLimit's kept note).
                : NullResultGated(method) ? null : "postfix not provably a no-op for a null result";
        }
        if (kind == PatchKind.Postfix)
        {
            if (tolerance == PatchTolerance.BeforeSkip)
            {
                return "postfix (a prefix of this mod skips the original here: it would run without the original's effect)";
            }
            // BeforeReplace: postfixes as SurroundingOnly's (below).
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
        bool answers = tolerance is PatchTolerance.BeforeReplace or PatchTolerance.BeforeSkip;
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

    private static readonly ConcurrentDictionary<(MethodBase Method, PatchKind Kind), Precedence> _precedence = new();

    /// <summary>
    /// Null when Harmony runs this prefix before every prefix of this mod on the method (each of its copies), else why not; with
    /// <paramref name="kind"/> Postfix, the same for a postfix and this mod's postfixes (PostfixesBefore).
    /// </summary>
    private static string? PrecedenceRefusal(MethodBase original, HarmonyLib.Patches info, MethodInfo prefix, PatchKind kind = PatchKind.Prefix)
    {
        Precedence order = PrecedenceOf(original, info, kind);
        if (order.Refused != null)
        {
            return order.Refused;
        }
        return order.AllPrecede || order.Preceding.Contains(prefix.MethodHandle) ? null
            : kind == PatchKind.Postfix ? "postfix running after a postfix of this mod (it would see what this mod's postfix changed)"
            : prefix.ReturnType == typeof(bool) ? "bool prefix running after a prefix of this mod (after its replacement, or not at all once it skipped the original)"
            : "void prefix running after a prefix of this mod (after its tracking or replacement, or not at all once it skipped the original)";
    }

    private static Precedence PrecedenceOf(MethodBase original, HarmonyLib.Patches info, PatchKind kind = PatchKind.Prefix)
    {
        Patch[] prefixes = (kind == PatchKind.Postfix ? info.Postfixes : info.Prefixes).ToArray();
        var key = new System.Text.StringBuilder();
        foreach (Patch p in prefixes)
        {
            key.Append(p.owner).Append('|').Append(p.index).Append('|').Append(p.priority).Append('|')
                .Append(_patchToken?.GetValue(p)).Append('|').Append(_patchModule?.GetValue(p)).Append('|')
                .Append(string.Join(",", p.before ?? Array.Empty<string>())).Append('|').Append(string.Join(",", p.after ?? Array.Empty<string>())).Append(';');
        }
        string text = key.ToString();
        if (_precedence.TryGetValue((original, kind), out Precedence? known) && known.Key == text)
        {
            return known;
        }
        Precedence computed = ComputePrecedence(original, prefixes, text, kind);
        _precedence[(original, kind)] = computed;
        return computed;
    }

    /// <summary>
    /// <paramref name="prefixes"/>: the prefixes (or with <paramref name="kind"/> Postfix the postfixes; Harmony sorts both the same
    /// way: priority, then before/after, then registration). This mod's order-free prefixes and read-only probe postfixes don't count.
    /// </summary>
    private static Precedence ComputePrecedence(MethodBase original, Patch[] prefixes, string key, PatchKind kind)
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
            if (PatchGuard.IsOurs(prefixes[i].owner)
                && !method.IsDefined(kind == PatchKind.Postfix ? typeof(ReadOnlyProbePostfixAttribute) : typeof(OrderFreePrefixAttribute), false))
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
        if (gates.Length > 0)
        {
            NoteNameGates(patch, method, mods);
        }
        Log.Info($"[{ModEntry.Id}] {patch}: {method.DeclaringType?.Name}.{method.Name} is also patched by {string.Join(", ", mods)} ({ToleranceText(tolerance)}"
            + (gates.Length > 0 ? $": {string.Join(", ", gates.Select(g => g.Name))} treated as overriding it" + GateDetail(method)
                : tolerance == PatchTolerance.NullSkipped ? ": no-ops on the calls this mod skips"
                : ": run around this mod's code as without it") + "): kept");
    }

    /// <summary>For the log: the argument a type gate was read on.</summary>
    private static string GateDetail(MethodBase method)
    {
        string listener = ListenerOf(method)?.Parameter ?? "__instance";
        return (listener == "__instance" ? ", gated on the instance" : $", gated on its argument {listener}")
            + (NameGated(method) ? "; a name test on the instance's runtime type taken as a gate on every loaded type of that name (a stated premise)" : "")
            + InitializerDetail(method);
    }

    /// <summary>Whether another mod's patch on the method was analysed with a name gate (<see cref="NameTest"/>).</summary>
    private static bool NameGated(MethodBase method)
    {
        try
        {
            if (_gateNames.IsEmpty || Harmony.GetPatchInfo(method) is not { } info)
            {
                return false;
            }
            var handles = AllPatches(info).Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => PatchMethodOf(p)?.MethodHandle).ToList();
            return _gateNames.Keys.Any(k => handles.Contains(k.Method));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Per patch, the methods let stand on a name gate (the kept note's text), and their mods.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string[]>> _nameGated = new(StringComparer.Ordinal);

    /// <summary>
    /// The reflection-reads premise's kept note (bdopt why, the menu's hover): which other mods' patches are taken as gated by a
    /// name test on the listener's runtime type, for <paramref name="patch"/>. Kept with the tolerated log lines (never cleared).
    /// </summary>
    private static void NoteNameGates(string patch, MethodBase method, string[] mods)
    {
        if (!NameGated(method))
        {
            return;
        }
        ConcurrentDictionary<string, string[]> methods = _nameGated.GetOrAdd(patch, _ => new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal));
        methods[$"{method.DeclaringType?.Name}.{method.Name}"] = mods;
        string names = string.Join(", ", methods.Keys.OrderBy(n => n, StringComparer.Ordinal));
        Kept(patch, "name_gates", $"other mods' patches that only act for a listener of a given type name ({names}) are taken as doing nothing "
            + "for the others: those listeners keep their calls (a stated premise: a name test on the listener's type, GetType().Name or "
            + "FullName against a constant, only reads it, and every loaded type of that name is kept)", methods.Values.SelectMany(m => m).ToArray());
    }

    /// <summary>For the log: whether a patch let stand there has a beforefieldinit initializer judged harmless whenever it runs.</summary>
    private static string InitializerDetail(MethodBase method)
    {
        try
        {
            bool any = Harmony.GetPatchInfo(method) is { } info && AllPatches(info).Where(p => !PatchGuard.IsOurs(p.owner))
                .Any(p => PatchMethodOf(p)?.DeclaringType is { } type && type.TypeInitializer != null && InitializerFree(type));
            return any ? "; its class's static initializer (beforefieldinit) only stores constants, empty runtime collections or field lookups that can't throw: the same values whenever the runtime runs it" : "";
        }
        catch
        {
            return "";
        }
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
                + $"TypeGated {gated}; BeforeReplace {Verdict(PatchTolerance.BeforeReplace)}; "
                + $"NullSkipped {Verdict(PatchTolerance.NullSkipped)}; TranspiledOnly {Verdict(PatchTolerance.TranspiledOnly)}; "
                + $"BeforeSkip {Verdict(PatchTolerance.BeforeSkip)}; PostfixesBefore {Verdict(PatchTolerance.PostfixesBefore)}");
        }
        return lines;
    }

    // --- Type-gated patches (TypeGated) ---

    /// <summary>
    /// Per patch method, kind and the parameter the gate must test (and the original's position of it, -1 for the instance):
    /// its gate types, or null when it isn't one (analysed once: a loaded method's IL doesn't change).
    /// </summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Method, PatchKind Kind, string Listener, int Position), Type[]?> _gates = new();

    /// <summary>
    /// Where a TypeGated original's skipped calls carry the listener (<see cref="SetListenerParameter"/>): <see cref="Parameter"/>
    /// its declared name ("__instance" for the instance, "": none, no gate), <see cref="PrefixesGated"/> false where a prefix
    /// can't stand even when gated (it runs before state the skipped calls would have created: PushModel's lazy stack).
    /// </summary>
    internal sealed record ListenerSlot(string Parameter, bool PrefixesGated);

    /// <summary>
    /// The registrations of <see cref="SetListenerParameter"/>, keyed by the method's module and token: the same method reached
    /// through another reflected type (another <c>MethodBase</c> object, not equal to the first) finds the same entry.
    /// </summary>
    private static readonly ConcurrentDictionary<(Module Module, int Token), ListenerSlot> _listenerParameters = new();

    /// <summary>
    /// Says that every call of <paramref name="original"/> this mod skips for a listener passes that listener as
    /// <paramref name="parameter"/> (its declared name, as Harmony binds it; "__instance" for the instance, "" for none): a
    /// <see cref="PatchTolerance.TypeGated"/> check of it then accepts gates on that parameter only, never on <c>__instance</c>
    /// unless it is that. <paramref name="prefixesGated"/> false: no prefix stands there, gated or not. Registered before the
    /// first check of the method (HookSkip resolves its helpers at load, in its Watch, then checks them); cached answers about
    /// it are dropped when it is registered. Two different answers for one method leave it none (""): every patch there backs
    /// off. A TypeGated original that isn't a model's method (its instance isn't the listener) and isn't registered gets no
    /// gate at all (<see cref="TypeGates(MethodInfo, PatchKind, MethodBase?)"/>). Never throws.
    /// </summary>
    internal static void SetListenerParameter(MethodBase? original, string parameter, bool prefixesGated = true)
    {
        try
        {
            if (original != null)
            {
                var slot = new ListenerSlot(parameter, prefixesGated);
                _listenerParameters.AddOrUpdate((original.Module, original.MetadataToken), slot,
                    (_, known) => known.Parameter == parameter ? known with { PrefixesGated = known.PrefixesGated && prefixesGated } : new ListenerSlot("", false));
                Invalidate(original);
            }
        }
        catch
        {
            // Not registered: a helper isn't a model's method, so TypeGates finds no gate on it (every patch there backs off).
        }
    }

    /// <summary>The listener registered for this original, or null (none registered).</summary>
    internal static ListenerSlot? ListenerOf(MethodBase original)
    {
        try
        {
            return _listenerParameters.TryGetValue((original.Module, original.MetadataToken), out ListenerSlot? slot) ? slot : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The original is an instance method of a model type (an AbstractModel default, a card method): its instance is the
    /// listener or card the caller skips calls for, the default place of a gate when nothing is registered.
    /// </summary>
    private static bool ModelInstance(MethodBase original) =>
        !original.IsStatic && !original.IsConstructor && original.DeclaringType != null
        && typeof(MegaCrit.Sts2.Core.Models.AbstractModel).IsAssignableFrom(original.DeclaringType);

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
    /// a method whose class has a static constructor run at its first use (a <c>beforefieldinit</c> one doing only lookups and constant stores stands, see
    /// <see cref="AnalyseGates"/>). <c>__instance</c> is found by its declared name: <see cref="Intolerable"/> refuses a patch
    /// renaming its parameters (<c>HarmonyArgument</c>) before asking, and a patch whose own method is patched.
    /// </summary>
    public static Type[]? TypeGates(MethodInfo patch, PatchKind kind) => TypeGates(patch, kind, null);

    /// <summary>
    /// <see cref="TypeGates(MethodInfo, PatchKind)"/> on the parameter holding the listener in the calls this mod skips of
    /// <paramref name="original"/> (<see cref="SetListenerParameter"/>): the tests load that parameter, by its declared name or
    /// Harmony's positional <c>__N</c>. A gate on <c>__instance</c> doesn't count where the listener is another parameter (a
    /// helper's instance is the choice context, not the listener). Nothing registered: <c>__instance</c> only for a model's own
    /// method (<see cref="ModelInstance"/>), else no gate. Registered "" (none), or a prefix where prefixes can't stand: no gate.
    /// </summary>
    internal static Type[]? TypeGates(MethodInfo patch, PatchKind kind, MethodBase? original)
    {
        if (kind is not (PatchKind.Prefix or PatchKind.Postfix))
        {
            return null;
        }
        ListenerSlot? slot = original != null ? ListenerOf(original) : null;
        if (slot != null && kind == PatchKind.Prefix && !slot.PrefixesGated)
        {
            return null;
        }
        string listener = slot?.Parameter ?? (original == null || ModelInstance(original) ? "__instance" : "");
        if (listener.Length == 0)
        {
            return null;
        }
        int position = listener == "__instance" ? -1 : Array.FindIndex(original!.GetParameters(), p => p.Name == listener);
        if (listener != "__instance" && position < 0)
        {
            return null;
        }
        var gateKey = (patch.MethodHandle, kind, listener, position);
        Type[]? shape = _gates.GetOrAdd(gateKey, key => AnalyseGates(patch, key.Kind, key.Listener, key.Position));
        // Name gates (the reflection-reads premise) resolved to the loaded types of that name at each answer: null when they can't be.
        if (shape != null && Array.IndexOf(shape, typeof(NameGateMarker)) >= 0)
        {
            shape = _gateNames.TryGetValue(gateKey, out (bool Full, string Name)[]? names) ? WithNamedTypes(shape, names) : null;
        }
        // The initializers after the shape (a refusal then says which of the two failed: GateRefusal).
        return shape != null && InitializersFree(patch) ? shape : null;
    }

    /// <summary>Stands for the name gates of one analysed patch in <see cref="_gates"/> (never a listener's type).</summary>
    private sealed class NameGateMarker
    {
    }

    /// <summary>A name gate no loaded type has (the patch then does nothing for every listener): assignable from no model type.</summary>
    internal sealed class NoTypeOfThatName
    {
    }

    /// <summary>The name gates per analysed patch (<see cref="AnalyseGates"/>): the runtime type's Name (or FullName) tested.</summary>
    private static readonly ConcurrentDictionary<(RuntimeMethodHandle Method, PatchKind Kind, string Listener, int Position), (bool Full, string Name)[]> _gateNames = new();

    /// <summary>
    /// The gates with each name gate replaced by every loaded type of that exact Name (or FullName): a listener whose runtime type
    /// has that name is one of them (types assignable to them are kept too: more listeners kept, never fewer). Null (no gate)
    /// when a type of that name is generic (its closed instances aren't loaded types) or an assembly's types can't be read.
    /// Worked out again when an assembly loads or a dynamic assembly gains a type (<see cref="LoadedCode.Key"/>, sampled every 2 s;
    /// PatchSafety's answers are asked again within 2 s).
    /// </summary>
    private static Type[]? WithNamedTypes(Type[] shape, (bool Full, string Name)[] names)
    {
        var gates = shape.Where(t => t != typeof(NameGateMarker)).ToList();
        foreach ((bool full, string name) in names)
        {
            Type[]? named = TypesNamed(full, name);
            if (named == null)
            {
                return null;
            }
            gates.AddRange(named.Length > 0 ? named : new[] { typeof(NoTypeOfThatName) });
        }
        return gates.Distinct().ToArray();
    }

    private static readonly ConcurrentDictionary<(bool Full, string Name), (long Code, Type[]? Types)> _typesNamed = new();

    /// <summary>Why the last name gate's type scan failed (display and the self-test's message), or null.</summary>
    internal static string? TypesNamedFailure { get; private set; }

    /// <summary>
    /// The loaded types of that exact Name (or FullName), or null (no gate). Keyed on <see cref="LoadedCode.Key"/> (re-audit of
    /// the premises, P-1: keyed on the assembly count before, a type emitted later into a dynamic assembly already loaded, such as
    /// RitsuLib's placeholder models or BaseLib's generated badges, was missed for good). Null for a name a closed generic type's
    /// would have (<c>[</c>, or a generic arity's <c>`</c>: no loaded type is a closed generic, so none would match while its
    /// instances' runtime names do), a generic type of that name, a type of that name still being built in a dynamic assembly
    /// (a <c>TypeBuilder</c>: it becomes a type of that name once created), and an assembly whose types can't all be read.
    /// </summary>
    private static Type[]? TypesNamed(bool full, string name)
    {
        if (name.IndexOf('[') >= 0 || name.IndexOf('`') >= 0)
        {
            return null;
        }
        long code = LoadedCode.Key();
        if (_typesNamed.TryGetValue((full, name), out var known) && known.Code == code)
        {
            return known.Types;
        }
        Type[]? found;
        try
        {
            var list = new List<Type>();
            bool building = false;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e) when (!assembly.IsDynamic)
                {
                    // Proof run 2: one loaded assembly with a type that can't load refused every name gate. A type that couldn't load
                    // has no instance, so no listener of that name (as LoadedCode.Types takes them). Not for a dynamic assembly
                    // (round 4): an unreadable one adds a constant to LoadedCode.Key, so a type emitted into it later wouldn't move
                    // the key and a partial list cached now would miss it for good: refused (the outer catch), as before proof run 2.
                    types = e.Types.Where(t => t != null).ToArray()!;
                }
                foreach (Type type in types)
                {
                    if ((full ? type.FullName : type.Name) == name)
                    {
                        building |= type is System.Reflection.Emit.TypeBuilder or System.Reflection.Emit.EnumBuilder
                            or System.Reflection.Emit.GenericTypeParameterBuilder;
                        list.Add(type);
                    }
                }
            }
            found = building || list.Any(t => t.ContainsGenericParameters || t.IsGenericType) ? null : list.ToArray();
        }
        catch (Exception e)
        {
            found = null; // An assembly whose types can't be read: a type of that name may be there.
            TypesNamedFailure = e.GetType().Name + ": " + e.Message;
        }
        _typesNamed[(full, name)] = (code, found);
        return found;
    }

#if BENCH
    /// <summary>Bench and offline self-test: what a name gate on that name resolves to now (null: refused).</summary>
    internal static Type[]? TypesNamedForTest(bool full, string name) => TypesNamed(full, name);
#endif

    /// <summary>
    /// The reflection-reads premise's name test (owner-approved 2026-11-17, DESIGN "Owner-approved premises"; Skin Changer's
    /// <c>__instance.GetType().Name != "MultiplayerScalingModel"</c>): at <paramref name="index"/>,
    /// <c>ldarg &lt;the instance&gt;; callvirt object.GetType(); callvirt get_Name()|get_FullName() (MemberInfo or Type);
    /// ldstr S; call string.op_Equality|op_Inequality(string, string); [stloc n; ldloc n;] brtrue|brfalse L</c>. On a non-null
    /// instance none of these can throw or do anything but read. <paramref name="next"/>: where a listener whose runtime type
    /// isn't named S goes (the branch target or the next instruction).
    /// </summary>
    private static bool NameTest(List<MethodFingerprint.Instruction> code, int index, int[] bound, Dictionary<int, int> at,
        out bool full, out string name, out int next)
    {
        full = false;
        name = "";
        next = -1;
        if (index + 5 >= code.Count || !bound.Any(b => LoadsArgument(code[index], b)))
        {
            return false;
        }
        static bool Calls(MethodFingerprint.Instruction ins, out MethodInfo? method)
        {
            method = (ins.Op == OpCodes.Call || ins.Op == OpCodes.Callvirt) ? ins.Operand as MethodInfo : null;
            return method != null;
        }
        if (!Calls(code[index + 1], out MethodInfo? getType) || getType!.DeclaringType != typeof(object) || getType.Name != "GetType"
            || getType.GetParameters().Length != 0)
        {
            return false;
        }
        if (!Calls(code[index + 2], out MethodInfo? getName) || (getName!.DeclaringType != typeof(MemberInfo) && getName.DeclaringType != typeof(Type))
            || getName.Name is not ("get_Name" or "get_FullName") || getName.GetParameters().Length != 0)
        {
            return false;
        }
        if (code[index + 3].Op != OpCodes.Ldstr || code[index + 3].Operand is not string literal)
        {
            return false;
        }
        if (!Calls(code[index + 4], out MethodInfo? compare) || compare!.DeclaringType != typeof(string) || !compare.IsStatic
            || compare.Name is not ("op_Equality" or "op_Inequality") || compare.GetParameters() is not [{ ParameterType: var a }, { ParameterType: var b2 }]
            || a != typeof(string) || b2 != typeof(string))
        {
            return false;
        }
        int branch = index + 5;
        if (StoresLocal(code[branch], out int local) && branch + 1 < code.Count && LoadsLocal(code[branch + 1], local))
        {
            branch += 2;
        }
        if (branch >= code.Count)
        {
            return false;
        }
        MethodFingerprint.Instruction test = code[branch];
        bool onTrue = test.Op == OpCodes.Brtrue || test.Op == OpCodes.Brtrue_S;
        bool onFalse = test.Op == OpCodes.Brfalse || test.Op == OpCodes.Brfalse_S;
        if ((!onTrue && !onFalse) || test.Operand is not int to || !at.TryGetValue(to, out int taken))
        {
            return false;
        }
        // A listener not named S: op_Inequality true, op_Equality false.
        bool value = compare.Name == "op_Inequality";
        next = value == onTrue ? taken : branch + 1;
        full = getName.Name == "get_FullName";
        name = literal;
        return true;
    }

    /// <summary>
    /// Why <see cref="TypeGates(MethodInfo, PatchKind, MethodBase?)"/> gives no gate, for the log: the IL shape first (a patch not
    /// gated is told so, whatever its class), then the type initializers it may run (<see cref="InitializersFree"/>).
    /// </summary>
    private static string GateRefusal(MethodInfo patch, PatchKind kind, MethodBase original, string listener)
    {
        ListenerSlot? slot = ListenerOf(original);
        string name = slot?.Parameter ?? (ModelInstance(original) ? "__instance" : "");
        int position = name == "__instance" || name.Length == 0 ? -1 : Array.FindIndex(original.GetParameters(), p => p.Name == name);
        bool shaped = name.Length > 0 && (name == "__instance" || position >= 0)
            && _gates.GetOrAdd((patch.MethodHandle, kind, name, position), key => AnalyseGates(patch, key.Kind, key.Listener, key.Position)) != null;
        if (shaped && InitializerRefusal(patch) is { } why)
        {
            return $"{KindText(kind, patch)} gated, but {why}";
        }
        return $"{KindText(kind, patch)} not provably gated on the listener's type" + listener;
    }

    /// <summary>The gates found (null: none), the name gates recorded for <see cref="TypeGates(MethodInfo, PatchKind, MethodBase?)"/>.</summary>
    private static Type[]? Gated(List<Type> gates, List<(bool Full, string Name)> names, MethodInfo patch, PatchKind kind, string listener, int position)
    {
        if (gates.Count == 0)
        {
            return null;
        }
        if (names.Count > 0)
        {
            _gateNames[(patch.MethodHandle, kind, listener, position)] = names.Distinct().ToArray();
        }
        return gates.ToArray();
    }

    private static Type[]? AnalyseGates(MethodInfo patch, PatchKind kind, string listener, int position)
    {
        try
        {
            if (!patch.IsStatic || patch.ContainsGenericParameters)
            {
                return null;
            }
            // The IL shape only: the type initializers the patch may run are judged after (TypeGates: InitializersFree).
            if (patch.DeclaringType == null)
            {
                return null;
            }
            bool boolPrefix = kind == PatchKind.Prefix && patch.ReturnType == typeof(bool);
            if (patch.ReturnType != typeof(void) && !boolPrefix)
            {
                return null;
            }
            ParameterInfo[] parameters = patch.GetParameters();
            // The patch parameters Harmony binds to the listener's argument: its name, or "__N" for the original's N-th argument.
            int[] bound = Enumerable.Range(0, parameters.Length)
                .Where(i => parameters[i].Name == listener || (position >= 0 && parameters[i].Name == "__" + position)).ToArray();
            if (bound.Length == 0 || bound.Any(i => parameters[i].ParameterType.IsByRef || parameters[i].ParameterType.IsValueType))
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
            var names = new List<(bool Full, string Name)>();
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
                    return boolPrefix ? null : Gated(gates, names, patch, kind, listener, position);
                }
                if (boolPrefix && ins.Op == OpCodes.Ldc_I4_1 && index + 1 < code.Count && code[index + 1].Op == OpCodes.Ret)
                {
                    return Gated(gates, names, patch, kind, listener, position);
                }
                // The reflection-reads premise (shape rules on): a name test on the instance's runtime type (NameTest).
                if (PatchSafety.ShapeRules && position < 0 && NameTest(code, index, bound, at, out bool full, out string name, out int afterName))
                {
                    names.Add((full, name));
                    if (!gates.Contains(typeof(NameGateMarker)))
                    {
                        gates.Add(typeof(NameGateMarker));
                    }
                    index = afterName;
                    continue;
                }
                // A test: ldarg <the listener's parameter>; isinst T; [stloc n; ldloc n;] brfalse/brtrue L.
                if (!bound.Any(b => LoadsArgument(ins, b)) || index + 2 >= code.Count || code[index + 1].Op != OpCodes.Isinst
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

    /// <summary>
    /// When the class's type initializer runs changes nothing: it has none, or it is marked <c>beforefieldinit</c> (the C#
    /// compiler's mark for static field initializers without a static constructor; an explicit <c>static T() {...}</c> clears
    /// it) and its code is <see cref="PureInitializer"/>. Not "never runs at a call": CoreCLR may run a beforefieldinit
    /// initializer when the JIT compiles a method reading the class's statics (ECMA-335 II.10.5.3.2 lets it run at any time
    /// before the first static field access), and this mod can delay or remove the first call of a patch method. A class
    /// without beforefieldinit runs its initializer at its first use: the patch's own class at the patch's first call (any
    /// call), so it must have none.
    /// </summary>
    internal static bool InitializerFree(Type type)
    {
        try
        {
            return type.TypeInitializer == null
                || ((type.Attributes & TypeAttributes.BeforeFieldInit) != 0 && _pureInitializers.GetOrAdd(type, PureInitializer));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// P2 (the verification audit of 2026-11-17): <see cref="InitializerFree"/> for the patch's own class, and for every other
    /// class outside the runtime's own assemblies whose static field the patch method (its generated code included) reads or
    /// writes, whose static method it calls or whose object it creates: compiling the patch method (at its first call, which
    /// this mod can delay or remove) may run any of their beforefieldinit initializers. A class without beforefieldinit there
    /// runs its initializer at its first access, on the code path that makes it (the gated path, which runs as without this
    /// mod): it stands. Cached per patch method.
    /// </summary>
    internal static bool InitializersFree(MethodInfo patch) => InitializerRefusal(patch) == null;

    /// <summary>Why <see cref="InitializersFree"/> fails (for the log), or null when it holds.</summary>
    internal static string? InitializerRefusal(MethodInfo patch) => _initializerRefusals.GetOrAdd(patch.MethodHandle, _ =>
    {
        try
        {
            if (patch.DeclaringType is not { } own)
            {
                return "its class can't be read";
            }
            if (!InitializerFree(own))
            {
                return $"its class {own.Name} has a static initializer that could run at another time with this mod (when the runtime compiles or first calls the patch, which this mod can delay or remove) and could do something different then";
            }
            foreach (MethodBase code in CompiledWith(patch))
            {
                foreach (MemberInfo member in MethodFingerprint.UsedMembers(code))
                {
                    Type? reached = member switch
                    {
                        FieldInfo { IsStatic: true } field => field.DeclaringType,
                        MethodBase { IsStatic: true } method => method.DeclaringType,
                        ConstructorInfo constructor => constructor.DeclaringType,
                        _ => null,
                    };
                    if (reached == null || reached == own || IsRuntimeAssembly(reached) || InitializedByHarmony(reached))
                    {
                        continue;
                    }
                    Type definition = reached.IsGenericType ? reached.GetGenericTypeDefinition() : reached;
                    if (definition.TypeInitializer != null && (definition.Attributes & TypeAttributes.BeforeFieldInit) != 0 && !InitializerFree(definition))
                    {
                        return $"it reaches {definition.Name}, whose static initializer (beforefieldinit) could run at another time with this mod (when the runtime compiles the patch, which this mod can delay or remove) and could do something different then";
                    }
                }
            }
            return null;
        }
        catch (Exception e)
        {
            return "its code can't be read (" + e.GetType().Name + ")";
        }
    });

    private static readonly ConcurrentDictionary<RuntimeMethodHandle, string?> _initializerRefusals = new();

    /// <summary>
    /// The code the runtime may compile with the patch method (the re-audit round 2 of 2026-11-17): the method, and (A2) the methods
    /// of its own assembly it calls directly, transitively to depth 3 (the JIT may inline them into it, and their static accesses
    /// with them), plus the state machine of any of those (an async or iterator body, started through the runtime's generic
    /// builder). Lambdas only referenced by a delegate (ldftn) are compiled on their own when first invoked, on the path that
    /// invokes them (B4: no longer scanned unless called directly). Throws when the IL can't be read (the caller refuses).
    /// </summary>
    private static List<MethodBase> CompiledWith(MethodInfo patch)
    {
        var found = new List<MethodBase> { patch };
        var seen = new HashSet<MethodBase> { patch };
        Assembly assembly = patch.Module.Assembly;
        var frontier = new List<MethodBase> { patch };
        for (int depth = 0; depth < 3 && frontier.Count > 0; depth++)
        {
            var next = new List<MethodBase>();
            foreach (MethodBase method in frontier)
            {
                foreach (MethodFingerprint.Instruction ins in MethodFingerprint.Instructions(method, strict: true))
                {
                    if ((ins.Op == OpCodes.Call || ins.Op == OpCodes.Callvirt || ins.Op == OpCodes.Newobj) && ins.Operand is MethodBase callee
                        && callee.Module.Assembly == assembly && callee.GetMethodBody() != null && seen.Add(callee))
                    {
                        found.Add(callee);
                        next.Add(callee);
                    }
                }
            }
            frontier = next;
        }
        foreach (MethodBase method in found.ToArray())
        {
            if (method.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType?.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } moveNext && seen.Add(moveNext))
            {
                found.Add(moveNext);
            }
        }
        return found;
    }

    /// <summary>
    /// B3 (re-audit round 2): HarmonyLib's <c>AccessTools</c> from the 0Harmony this mod and the other mods' patches use: Harmony runs
    /// it while patching (any patch, this mod's own at its load included), so its initializer has run before any other mod's patch
    /// can be judged, whenever that patch is compiled. By assembly identity, not by name (C6). Traverse isn't taken: whether
    /// patching uses it isn't established (its initializer is judged as any other's).
    /// </summary>
    private static bool InitializedByHarmony(Type type) =>
        type.Assembly == typeof(Harmony).Assembly && type.FullName == "HarmonyLib.AccessTools";

    /// <summary>A type of the runtime's own assemblies (System.*, mscorlib): their initializers don't depend on this mod's calls.</summary>
    private static bool IsRuntimeType(Type type)
    {
        if (type.HasElementType)
        {
            return IsRuntimeType(type.GetElementType()!);
        }
        // A constructed generic type (a tuple, a collection) is the runtime's only with every argument the runtime's (P4: their
        // Equals, GetHashCode, CompareTo run in a collection's methods).
        return IsRuntimeAssembly(type) && (!type.IsGenericType || type.IsGenericTypeDefinition || type.GetGenericArguments().All(IsRuntimeType));
    }

    /// <summary>
    /// Declared in the runtime's own assemblies (a generic type's arguments aside): the core library, or (C6, re-audit round 2) an
    /// assembly named as the runtime's (System.*, System, mscorlib, netstandard) loaded from the core library's own folder, not
    /// any assembly with such a name (a mod could ship one).
    /// </summary>
    private static bool IsRuntimeAssembly(Type type)
    {
        Assembly assembly = type.Assembly;
        if (assembly == typeof(object).Assembly)
        {
            return true;
        }
        return _runtimeAssemblies.GetOrAdd(assembly, static a =>
        {
            try
            {
                return !a.IsDynamic && a.GetName().Name is { } name
                    && (name.StartsWith("System.", StringComparison.Ordinal) || name == "System" || name == "mscorlib" || name == "netstandard")
                    && a.Location.Length > 0 && RuntimeFolder is { } folder
                    && string.Equals(System.IO.Path.GetDirectoryName(a.Location), folder, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }

    private static readonly ConcurrentDictionary<Assembly, bool> _runtimeAssemblies = new();

    /// <summary>The core library's folder (the runtime's own assemblies are loaded from there), or null when unknown.</summary>
    private static readonly string? RuntimeFolder = CoreFolder();

    private static string? CoreFolder()
    {
        try
        {
            string location = typeof(object).Assembly.Location;
            return location.Length > 0 ? System.IO.Path.GetDirectoryName(location) : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly ConcurrentDictionary<Type, bool> _pureInitializers = new();

    /// <summary>
    /// The initializer's IL only computes values that are the same whenever it runs and can't throw, stored in the class's own
    /// static fields (P1, the verification audit of 2026-11-17: a throw at another time would be a behaviour difference):
    /// constants (<c>ldstr</c>, <c>ldc.*</c>, <c>ldnull</c>), <c>ldtoken</c> and <c>typeof</c> (<c>Type.GetTypeFromHandle</c>,
    /// <c>MethodBase.GetMethodFromHandle</c>), arrays of runtime types (<c>newarr</c>, <c>stelem</c>,
    /// <c>RuntimeHelpers.InitializeArray</c>), <c>new object()</c>, empty runtime collections of runtime element types
    /// (<c>System.Collections.*</c> constructors taking nothing, a constant non-negative capacity, or a comparer from a runtime
    /// static getter: <c>StringComparer.*</c>, <c>EqualityComparer&lt;T&gt;.Default</c>, <c>Comparer&lt;T&gt;.Default</c>),
    /// runtime static readonly fields (<c>Type.EmptyTypes</c>, <c>string.Empty</c>), <c>Array.Empty&lt;T&gt;()</c>, field
    /// lookups (HarmonyLib's <c>AccessTools.Field/DeclaredField(Type, string)</c>, which return null for a missing field (they throw only
    /// for two fields of one name in one class, which emitted IL alone can make), and
    /// <c>Type.GetField</c> on a <c>typeof</c>: RitsuLib's <c>OwnerField = AccessTools.Field(...)</c>), locals (<c>stloc</c>,
    /// <c>ldloc</c>; <c>ldloca</c> only feeds instructions this list allows), <c>dup</c>/<c>pop</c>/<c>box</c>, its own static
    /// fields (<c>stsfld</c>, <c>ldsfld</c>), and <c>ret</c>. Refused: branches, any other call, <c>AccessTools.TypeByName</c>
    /// (assembly-resolve handlers would run at another time), method and property lookups (<c>AccessTools.Method</c>,
    /// <c>Type.GetMethod</c>, <c>GetProperty</c>: an ambiguous name throws), any call on a value that may be null, collection
    /// methods with arguments (<c>Add</c> throws on a duplicate key, <c>Sort</c> runs comparers), collections of other types
    /// (their <c>Equals</c>/<c>GetHashCode</c>/<c>CompareTo</c> are another mod's code). False when the IL can't be read.
    /// </summary>
    private static bool PureInitializer(Type type)
    {
        ConstructorInfo? initializer = type.TypeInitializer;
        if (initializer == null)
        {
            return true;
        }
        List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(initializer, strict: true);
        // A3 (re-audit round 2): the array being filled (from its newarr): its element type and length, for the stores into it.
        Type? arrayElement = null;
        int arrayLength = 0;
        for (int i = 0; i < code.Count; i++)
        {
            MethodFingerprint.Instruction ins = code[i];
            OpCode op = ins.Op;
            if (op.Name!.StartsWith("stelem", StringComparison.Ordinal))
            {
                if (!ArrayStore(code, i, arrayElement, arrayLength))
                {
                    return false;
                }
                continue;
            }
            if (op == OpCodes.Nop || op == OpCodes.Ret || op == OpCodes.Dup || op == OpCodes.Pop || op == OpCodes.Ldnull || op == OpCodes.Ldstr
                || op == OpCodes.Ldtoken || op.Name.StartsWith("ldc.", StringComparison.Ordinal)
                || op.Name.StartsWith("stloc", StringComparison.Ordinal) || op.Name.StartsWith("ldloc", StringComparison.Ordinal) || op == OpCodes.Box)
            {
                continue;
            }
            if (op == OpCodes.Newarr && ins.Operand is Type element && IsRuntimeType(element))
            {
                // A constant size in [0, 1 << 20] (a computed or huge one could throw: OverflowException, OutOfMemoryException).
                int? size = i == 0 ? null : IntConstant(code[i - 1]);
                if (size is not (>= 0 and <= 1 << 20))
                {
                    return false;
                }
                arrayElement = element;
                arrayLength = size.Value;
                continue;
            }
            // B2 (re-audit round 2): Roslyn's closure class for non-capturing lambdas (<>c): `newobj <>c::.ctor(); stsfld <>9`, its
            // constructor only calling object's (the <>9__N_M caches are assigned lazily by the methods using them).
            if (op == OpCodes.Newobj && ins.Operand is ConstructorInfo own && own.DeclaringType == type && own.GetParameters().Length == 0
                && TrivialConstructor(own))
            {
                continue;
            }
            if ((op == OpCodes.Stsfld || op == OpCodes.Ldsfld) && ins.Operand is FieldInfo field && field.DeclaringType == type)
            {
                continue;
            }
            if (op == OpCodes.Ldsfld && ins.Operand is FieldInfo { IsInitOnly: true } runtimeField && runtimeField.DeclaringType is { } holder
                && holder.Assembly == typeof(object).Assembly && !holder.IsGenericType)
            {
                continue; // Type.EmptyTypes, string.Empty: the runtime's own, already initialized.
            }
            if ((op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj) && ins.Operand is MethodBase called && PureCall(code, i, called))
            {
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// A3 (re-audit round 2): a store into the array being filled, in the compiler's pattern only: <c>dup; ldc index; value;
    /// stelem</c> right after its <c>newarr</c> or the previous store, the index a constant inside the array, the value a constant,
    /// <c>ldnull</c>, a string or a <c>typeof</c> (one that the element type accepts: no ArrayTypeMismatchException, no
    /// IndexOutOfRangeException at whatever time the initializer runs).
    /// </summary>
    private static bool ArrayStore(List<MethodFingerprint.Instruction> code, int at, Type? element, int length)
    {
        if (element == null)
        {
            return false;
        }
        int value = at - 1;
        Type? stored;
        if (value >= 1 && code[value].Op == OpCodes.Call && code[value].Operand is MethodBase { Name: "GetTypeFromHandle" } h && h.DeclaringType == typeof(Type)
            && code[value - 1].Op == OpCodes.Ldtoken)
        {
            value--;
            stored = typeof(Type);
        }
        else if (value >= 1 && code[value].Op == OpCodes.Box && code[value].Operand is Type boxed && IntConstant(code[value - 1]) != null)
        {
            value--;
            stored = boxed;
        }
        else if (value >= 0 && code[value].Op == OpCodes.Ldstr)
        {
            stored = typeof(string);
        }
        else if (value >= 0 && code[value].Op == OpCodes.Ldnull)
        {
            stored = null;
        }
        else if (value >= 0 && (IntConstant(code[value]) != null || code[value].Op.Name!.StartsWith("ldc.", StringComparison.Ordinal)))
        {
            stored = element; // A numeric constant into a numeric array (the store's own width).
            if (!element.IsPrimitive || code[at].Op == OpCodes.Stelem_Ref)
            {
                return false;
            }
        }
        else
        {
            return false;
        }
        if (code[at].Op == OpCodes.Stelem_Ref && (element.IsValueType || (stored != null && !element.IsAssignableFrom(stored))))
        {
            return false;
        }
        int index = value - 1, dup = value - 2;
        if (dup < 1 || IntConstant(code[index]) is not { } i || i < 0 || i >= length || code[dup].Op != OpCodes.Dup)
        {
            return false;
        }
        OpCode before = code[dup - 1].Op;
        return before == OpCodes.Newarr || before.Name!.StartsWith("stelem", StringComparison.Ordinal);
    }

    /// <summary>A constructor whose code only calls object's constructor (nops aside): <c>ldarg.0; call object::.ctor(); ret</c>.</summary>
    private static bool TrivialConstructor(ConstructorInfo constructor)
    {
        try
        {
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(constructor, strict: true).Where(c => c.Op != OpCodes.Nop).ToList();
            return code.Count == 3 && code[0].Op == OpCodes.Ldarg_0 && code[1].Op == OpCodes.Call && code[1].Operand is ConstructorInfo baseCtor
                && baseCtor.DeclaringType == typeof(object) && code[2].Op == OpCodes.Ret;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A call <see cref="PureInitializer"/> allows at <paramref name="at"/> (its arguments one instruction each).</summary>
    private static bool PureCall(List<MethodFingerprint.Instruction> code, int at, MethodBase called)
    {
        Type? owner = called.DeclaringType;
        if (owner == null)
        {
            return false;
        }
        ParameterInfo[] parameters = called.GetParameters();
        // typeof and the token lookups: a valid token from ldtoken never fails, but (A3, re-audit round 2) the one-argument
        // GetMethodFromHandle throws for a method of a generic type: only with its type's handle then.
        if ((owner == typeof(Type) && called.Name == "GetTypeFromHandle") || (owner == typeof(RuntimeHelpers) && called.Name == "InitializeArray"))
        {
            return true;
        }
        if (owner == typeof(MethodBase) && called.Name == "GetMethodFromHandle")
        {
            return parameters.Length == 2 || (at >= 1 && code[at - 1].Op == OpCodes.Ldtoken && code[at - 1].Operand is MethodBase token
                && token.DeclaringType is { IsGenericType: false });
        }
        // Type.GetField(name[, flags]) on a typeof: never null, and GetField doesn't throw on a name (the most derived field wins).
        if ((owner == typeof(Type) || owner.FullName == "System.RuntimeType" || owner == typeof(System.Reflection.TypeInfo)) && called.Name == "GetField" && !called.IsStatic)
        {
            // A3: no IgnoreCase (two fields differing by case: an AmbiguousMatchException).
            return ArgumentsAreConstants(code, at, parameters.Length) && at - parameters.Length - 1 >= 0
                && (parameters.Length < 2 || IntConstant(code[at - 1]) is { } flags && (flags & (int)BindingFlags.IgnoreCase) == 0)
                && code[at - parameters.Length - 1] is { Op: var op } receiver && (op == OpCodes.Call || op == OpCodes.Callvirt)
                && receiver.Operand is MethodBase { Name: "GetTypeFromHandle" } r && r.DeclaringType == typeof(Type);
        }
        // HarmonyLib's field lookups: (Type, string), null on a null type or a missing field (Harmony 2.4.2; AccessTools.Field can
        // still throw for two fields of one name in a class's hierarchy, which C# can't declare: DESIGN). The loaded 0Harmony's
        // AccessTools only (C6, re-audit round 2: by assembly, not by name).
        if (owner == typeof(AccessTools) && called.Name is "Field" or "DeclaredField" && called.IsStatic
            && parameters.Length == 2 && parameters[0].ParameterType == typeof(Type) && parameters[1].ParameterType == typeof(string))
        {
            return true;
        }
        if (!IsRuntimeAssembly(owner))
        {
            return false;
        }
        if (called is ConstructorInfo constructor)
        {
            if (owner == typeof(object))
            {
                return parameters.Length == 0;
            }
            // An empty collection: no argument or a constant capacity (any element types, B4 of the re-audit round 2: constructing
            // it runs no code of theirs; a ConditionalWeakTable too), or a runtime comparer (runtime element types only: the comparer
            // is theirs). No other argument: a collection initializer's Add runs the keys' Equals and GetHashCode.
            bool weakTable = owner.IsGenericType && owner.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>);
            if ((!weakTable && owner.Namespace is not ("System.Collections.Generic" or "System.Collections.Concurrent" or "System.Collections.ObjectModel" or "System.Collections"))
                || at - parameters.Length < 0)
            {
                return false;
            }
            for (int p = 0; p < parameters.Length; p++)
            {
                MethodFingerprint.Instruction argument = code[at - parameters.Length + p];
                Type type = parameters[p].ParameterType;
                // A3: sizes in [0, 1 << 20] (a huge one: OutOfMemoryException); a concurrency level of -1 (the default) or 1 and more;
                // a bounded capacity of 1 and more; no other integer argument.
                bool ok = type == typeof(int) ? parameters[p].Name switch
                    {
                        "capacity" => IntConstant(argument) is >= 0 and <= 1 << 20,
                        "concurrencyLevel" => IntConstant(argument) is -1 or (>= 1 and <= 1 << 20),
                        "boundedCapacity" => IntConstant(argument) is >= 1 and <= 1 << 20,
                        _ => false,
                    }
                    : type.IsGenericType && type.GetGenericTypeDefinition() is var d && (d == typeof(IEqualityComparer<>) || d == typeof(IComparer<>))
                        ? argument.Operand is MethodBase getter && RuntimeComparer(getter) && owner.GetGenericArguments().All(IsRuntimeType)
                    : false;
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }
        // The runtime's own comparers and Array.Empty<T>(), static and taking nothing.
        return called.IsStatic && parameters.Length == 0 && (RuntimeComparer(called)
            || (owner == typeof(Array) && called.Name == "Empty" && called.GetGenericArguments().All(IsRuntimeType)));
    }

    private static bool RuntimeComparer(MethodBase getter) =>
        getter.IsStatic && getter.GetParameters().Length == 0 && getter.Name.StartsWith("get_", StringComparison.Ordinal) && getter.DeclaringType is { } t
        && (t == typeof(StringComparer)
            || (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(EqualityComparer<>) || t.GetGenericTypeDefinition() == typeof(Comparer<>))
                && getter.Name == "get_Default" && t.GetGenericArguments().All(IsRuntimeType)));

    private static bool ArgumentsAreConstants(List<MethodFingerprint.Instruction> code, int at, int count)
    {
        if (at - count < 0)
        {
            return false;
        }
        for (int i = at - count; i < at; i++)
        {
            if (code[i].Op != OpCodes.Ldstr && IntConstant(code[i]) == null)
            {
                return false;
            }
        }
        return true;
    }

    private static int? IntConstant(MethodFingerprint.Instruction ins)
    {
        OpCode op = ins.Op;
        if (op == OpCodes.Ldc_I4_M1) return -1;
        if (op == OpCodes.Ldc_I4_0) return 0;
        if (op == OpCodes.Ldc_I4_1) return 1;
        if (op == OpCodes.Ldc_I4_2) return 2;
        if (op == OpCodes.Ldc_I4_3) return 3;
        if (op == OpCodes.Ldc_I4_4) return 4;
        if (op == OpCodes.Ldc_I4_5) return 5;
        if (op == OpCodes.Ldc_I4_6) return 6;
        if (op == OpCodes.Ldc_I4_7) return 7;
        if (op == OpCodes.Ldc_I4_8) return 8;
        if ((op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) && ins.Operand is int n) return n;
        if (op == OpCodes.Ldc_I4_S && ins.Operand is sbyte b) return b;
        return null;
    }

    /// <summary>Per postfix method: whether <see cref="NullResultGated"/> holds (analysed once: a loaded method's IL doesn't change).</summary>
    private static readonly ConcurrentDictionary<RuntimeMethodHandle, bool> _nullGated = new();

    /// <summary>
    /// A postfix that provably does nothing when <c>__result</c> is null (<see cref="PatchTolerance.NullSkipped"/>): static,
    /// returns void, not generic, a <c>__result</c> parameter of a reference type (by value, or by reference: <c>ref __result</c>), and from its first instruction (nops
    /// aside) only <c>ldarg __result; [ldind.ref when by reference]; brfalse L</c> (taken on null) or <c>... brtrue L</c> (not
    /// taken on null), unconditional branches, then <c>ret</c>: on that path nothing but that one load (no call, no field, no
    /// store, no other argument). Harmony runs postfixes after a prefix that skipped the original, on the <c>__result</c> it
    /// left: null there. Renamed parameters (<c>HarmonyArgument</c>) and factories are refused before (ShapeRefusal); a patch
    /// whose own method is patched at each recheck (Intolerable). No initializer is judged (B1, re-audit round 2 of
    /// 2026-11-17): the postfix runs at every call, a skipped one included, and the first call is never a skip, so it is compiled
    /// and first called when it is without this mod (unlike TypeGated, where this mod removes the calls).
    /// </summary>
    public static bool NullResultGated(MethodInfo postfix) => _nullGated.GetOrAdd(postfix.MethodHandle, _ => AnalyseNullGate(postfix));

    private static bool AnalyseNullGate(MethodInfo patch)
    {
        try
        {
            if (!patch.IsStatic || patch.ContainsGenericParameters || patch.ReturnType != typeof(void) || patch.DeclaringType == null)
            {
                return false;
            }
            ParameterInfo[] parameters = patch.GetParameters();
            int result = Array.FindIndex(parameters, p => p.Name == "__result");
            if (result < 0)
            {
                return false;
            }
            Type type = parameters[result].ParameterType;
            bool byRef = type.IsByRef;
            Type value = byRef ? type.GetElementType()! : type;
            if (value.IsValueType || value.ContainsGenericParameters)
            {
                return false;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(patch);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            var visited = new HashSet<int>();
            bool tested = false;
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
                    return tested;
                }
                // The test, once: ldarg __result; [ldind.ref]; brfalse|brtrue L.
                if (tested || !LoadsArgument(ins, result))
                {
                    return false;
                }
                int branch = index + 1;
                if (byRef)
                {
                    if (branch >= code.Count || code[branch].Op != OpCodes.Ldind_Ref)
                    {
                        return false;
                    }
                    branch++;
                }
                if (branch >= code.Count)
                {
                    return false;
                }
                MethodFingerprint.Instruction test = code[branch];
                bool onFalse = test.Op == OpCodes.Brfalse || test.Op == OpCodes.Brfalse_S;
                bool onTrue = test.Op == OpCodes.Brtrue || test.Op == OpCodes.Brtrue_S;
                if (!onFalse && !onTrue || test.Operand is not int to || !at.TryGetValue(to, out int taken))
                {
                    return false;
                }
                tested = true;
                // Null: brfalse is taken, brtrue falls through.
                index = onFalse ? taken : branch + 1;
            }
            return false;
        }
        catch
        {
            return false;
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
