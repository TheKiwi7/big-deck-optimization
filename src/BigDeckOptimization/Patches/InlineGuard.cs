using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Harmony replaces a method at its entry point; a caller the JIT compiles later (tier 1) can inline the method's original
/// body, and at that call site the patch no longer runs (seen in game: <c>CombatPileState.Serialize</c>, under 16 bytes of
/// IL it is nearly always inlined). For a patch that only saves time that means slower; for one that counts writes to know
/// a cache still holds (setters are the smallest methods there are), a write it doesn't see.
///
/// So each patched method gets the runtime's "no inlining" flag, what <c>[MethodImpl(NoInlining)]</c> sets when a class
/// loads (<c>mdfNotInline</c> in the runtime's MethodDesc). Harmony 2.4's MonoMod.Core sets the same flag on every method
/// it detours (<c>PlatformTripleDetourFactory.CreateDetour</c> -> <c>TryDisableInlining</c>, the same word and bit), this
/// mod's and other mods' alike; <see cref="Protect"/> sets it a moment earlier, before the patch, and records the method.
/// That word isn't a public API: <see cref="Available"/> checks at load, on methods of this class declared either way, that
/// the flag is where it is expected, and otherwise this mod writes nothing anywhere (MonoMod still does) and the patches that
/// need the check stay off. What this class adds to MonoMod is <see cref="RecompileInliners"/>: replacements compiled before
/// the flags were set, which it rebuilds whether or not <see cref="Available"/> (it only needs <see cref="HarmonyRebuild"/>).
///
/// Code that isn't a Harmony replacement is never recompiled, so this relies on no such code having inlined a protected
/// method before it was patched. True today: sts2.dll is IL only (no ReadyToRun code with cross-method inlining), tier 0
/// never inlines and tier 1 needs ~30 calls, and every patch applies before the main menu, while the methods counted
/// (card piles, card and enchantment setters) only run inside a run. A patch applied later (an Apply run again when an
/// optional mod's assembly loads, or a game or mod starting a run before the main menu) could be outrun by tier-1 code
/// already compiled, and no recompile follows it.
///
/// The other way round, this mod's own replacements: Harmony compiles them when this mod patches (at its load, over the first
/// frames), while the game compiles a method it runs at its first call, in a run. A small method they call (inlined there)
/// that another mod patches afterwards would run its original body from them, past that patch: <see cref="AfterForeignFirstPatch"/>
/// compiles them again at that patch.
/// </summary>
public static class InlineGuard
{
    /// <summary>MethodDesc: UINT16 flags and token, BYTE chunk index, BYTE flags, WORD slot number, WORD flags.</summary>
    private const int FlagsOffset = 6;
    private const short NotInline = 0x2000;

    public static bool Available { get; } = Check();
    public static long Protected { get; private set; }
    /// <summary>Patched methods whose code was compiled again by <see cref="RecompileInliners"/>.</summary>
    public static int Recompiled { get; private set; }

    private static readonly HashSet<MethodBase> _protected = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ProbeStaticNoInline() => 1;

    private static int ProbeStatic() => 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ProbeStaticAggressive() => 5;

    private sealed class Probes
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int NoInline() => 2;

        public int Plain() => 4;
    }

    private static bool Check()
    {
        try
        {
#if BENCH
            if (Environment.GetEnvironmentVariable("BDTEST_NO_INLINE_GUARD") == "1")
            {
                return false;
            }
#endif
            if (!Environment.Is64BitProcess)
            {
                return false;
            }
            // A probe's handle is valid memory whatever the layout: only reads here.
            return Flagged(typeof(InlineGuard), nameof(ProbeStaticNoInline)) && Flagged(typeof(Probes), nameof(Probes.NoInline))
                && !Flagged(typeof(InlineGuard), nameof(ProbeStatic)) && !Flagged(typeof(Probes), nameof(Probes.Plain))
                && !Flagged(typeof(InlineGuard), nameof(ProbeStaticAggressive));
        }
        catch
        {
            return false;
        }
    }

    private static bool Flagged(Type type, string probe)
    {
        MethodInfo method = type.GetMethod(probe, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMethodException(probe);
        return (Marshal.ReadInt16(method.MethodHandle.Value, FlagsOffset) & NotInline) != 0;
    }

    /// <summary>
    /// Left as they are: generic methods (shared code between instantiations) and methods of structs (with the flag set
    /// through reflection's handle, <c>CombatPileState.Serialize</c> still got inlined): no patch that counts writes is on one.
    /// Recorded for <see cref="RecompileInliners"/> even when <see cref="Available"/> is false: MonoMod flags the method anyway.
    /// </summary>
    public static void Protect(MethodBase method)
    {
        if (method.ContainsGenericParameters || method.IsGenericMethod || method.DeclaringType is not { IsGenericType: false, IsValueType: false })
        {
            return;
        }
        try
        {
            IntPtr handle = method.MethodHandle.Value;
            _protected.Add(method);
            if (!Available)
            {
                return;
            }
            short flags = Marshal.ReadInt16(handle, FlagsOffset);
            if ((flags & NotInline) == 0)
            {
                Marshal.WriteInt16(handle, FlagsOffset, (short)(flags | NotInline));
                Protected++;
            }
        }
        catch (NotSupportedException)
        {
            // No handle (a dynamic method): nothing can inline it.
        }
    }

    /// <summary>
    /// Calls of a callee the JIT may inline are followed this deep: the JIT inlines inside what it inlines (its own depth
    /// limit is far larger, its time budget stops it first). Four small wrappers between a replacement and a protected
    /// method; the per-method answers (<see cref="Reach"/>) keep the deeper search cheap.
    /// </summary>
    private const int CalleeDepth = 4;

    /// <summary>
    /// The largest IL the JIT inlines without profile data (.NET 8+ extended default policy, <c>JitExtDefaultPolicyMaxIL</c>
    /// 0x80; the older default was 100): Harmony's replacements are dynamic methods, compiled once, without profile.
    /// <c>AggressiveInlining</c> callees have no size limit.
    /// </summary>
    private const int MaxInlineIl = 128;

    /// <summary>
    /// The flag only stops what is compiled after it is set. A method another mod patched before this one's patches applied
    /// (or one of this mod's own applied earlier) already has its replacement compiled, with the original bodies of protected
    /// methods it calls possibly inlined: there a counted write isn't counted (BaseLib's postfix on <c>SetToFreeThisTurn</c>
    /// had <c>SetThisTurnOrUntilPlayed</c>'s body in it, so Bullet Time changed hand costs unseen). Once every patch is in,
    /// each patched method whose replacement calls a protected one (directly or through callees the JIT may inline) gets its
    /// replacement built again by Harmony, the same patches in the same order: now compiled with the flags set. The replacement
    /// is the code after every transpiler plus every mod's prefixes, postfixes, finalizers and inner patches, each of which the
    /// JIT may inline into it (<see cref="ReplacementReaches"/>). One step per patched method, spread over frames by
    /// <see cref="ModEntry"/> (only what a run does needs it done): ~300 ms in all for 115-144 methods measured at a callee
    /// depth of 2; at <see cref="CalleeDepth"/> 4 (and with the methods recorded without <see cref="Available"/>) the count and
    /// time are in the "compiled again" log line, still to be measured (bounded by the patched methods: each rebuilt at most once).
    ///
    /// A rebuild can fail, and a failed one used to leave the method with no patches of any mod (v1.0.0: BaseLib's postfix
    /// on <c>NGlobalUi._Ready</c> gone, see <see cref="HarmonyRebuild"/>): each goes through <see cref="HarmonyRebuild.Run{T}"/>,
    /// which refuses what can't be rebuilt and puts the previous code back after a failure. Without its Harmony internals
    /// nothing is rebuilt: inlined callees are a smaller loss than another mod's patches. <see cref="Available"/> isn't needed:
    /// MonoMod set the flags when it detoured the protected methods.
    /// </summary>
    /// <remarks>
    /// The patched methods are listed by the caller (<see cref="PatchedNow"/>) once every patch of the start is in, while the
    /// rebuilds themselves may wait for the main menu (<see cref="ModEntry"/>, P2 of 2026-11-20): what is rebuilt is decided by
    /// that list, the same whenever the steps run (HookSkip's hooks patched meanwhile, after the flags were set, aren't in it).
    /// </remarks>
    public static IEnumerable<bool> RecompileInliners(Harmony harmony, List<MethodBase> patchedMethods)
    {
        if (_protected.Count == 0)
        {
            yield break;
        }
        if (!HarmonyRebuild.Available)
        {
            Log.Warn($"[{ModEntry.Id}] {nameof(InlineGuard)}: Harmony's detour table not as expected, patched callers not compiled again");
            yield break;
        }
        double spent = 0;
        int failed = 0;
        Dictionary<MethodBase, Reach> reaches = new();
#if BENCH
        HashSet<MethodBase> protectedAtList = _protectedAtList ?? new HashSet<MethodBase>(_protected);
#endif
        foreach (MethodBase patched in patchedMethods)
        {
            if (patched.DeclaringType?.Assembly == typeof(Harmony).Assembly)
            {
                continue; // Harmony's own code (HarmonyWatch's finalizer, other mods' hooks there): no game caller inlines it.
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                bool rebuild = ReplacementReaches(patched, _protected.Contains, reaches);
#if BENCH
                // BDTEST_REBUILD_ALL=1 or a list of Type.Method: rebuilt whatever they call (a friend's game rebuilt
                // NGlobalUi._Ready, this machine's doesn't). BDTEST_REBUILD_RAW=1: straight through Harmony, as v1.0.0 did.
                string? forced = Environment.GetEnvironmentVariable("BDTEST_REBUILD_ALL");
                rebuild |= forced == "1" || (forced?.Split(',').Contains(HarmonyRebuild.Describe(patched)) ?? false);
                if (rebuild && Environment.GetEnvironmentVariable("BDTEST_REBUILD_RAW") == "1")
                {
                    new PatchProcessor(harmony, patched).Patch();
                    Recompiled++;
                    rebuild = false;
                }
#endif
                if (rebuild)
                {
#if BENCH
                    // Test builds: a method rebuilt only for methods protected after the list was taken (P2's later evaluation):
                    // named with them.
                    if (!ReplacementReaches(patched, protectedAtList.Contains, new Dictionary<MethodBase, Reach>()))
                    {
                        string late = string.Join(", ", _protected.Where(p => !protectedAtList.Contains(p)
                            && ReplacementReaches(patched, m => m == p, new Dictionary<MethodBase, Reach>())).Select(HarmonyRebuild.Describe).Take(6));
                        Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)} (test builds): {HarmonyRebuild.Describe(patched)} rebuilt for methods protected after the list: {late}");
                    }
#endif
                    HarmonyRebuild.Run(patched, "compiling it again", () => new PatchProcessor(harmony, patched).Patch());
                    Recompiled++;
#if BENCH
                    _rebuiltNames.Add(HarmonyRebuild.Describe(patched));
#endif
                }
            }
            catch (Exception e)
            {
                failed++;
                Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: {HarmonyRebuild.Describe(patched)} not compiled again, its patches as they were: {e.Message}");
            }
            spent += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            yield return true;
        }
        Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: {Recompiled} patched methods calling protected ones compiled again"
            + (failed > 0 ? $", {failed} not" : "") + $" ({spent:F0} ms)");
#if BENCH
        // Test builds: which ones, sorted (the same set whenever the steps run: P2 of 2026-11-20), out of how many listed.
        Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)} rebuilt (test builds), {patchedMethods.Count} patched methods listed: "
            + string.Join(", ", _rebuiltNames.OrderBy(n => n, StringComparer.Ordinal)));
#endif
    }

#if BENCH
    private static readonly List<string> _rebuiltNames = new();
#endif

    /// <summary>Every method Harmony has patched now, in its order: <see cref="RecompileInliners"/>'s list.</summary>
    public static List<MethodBase> PatchedNow()
    {
#if BENCH
        _protectedAtList = new HashSet<MethodBase>(_protected);
#endif
        return Harmony.GetAllPatchedMethods().ToList();
    }

#if BENCH
    /// <summary>Test builds: the methods protected when <see cref="PatchedNow"/> listed the patched ones.</summary>
    private static HashSet<MethodBase>? _protectedAtList;
#endif

    /// <summary>
    /// Methods carrying this mod's patches compiled again after another mod's first patch on a method their replacements may
    /// have inlined (<see cref="AfterForeignFirstPatch"/>): the effect counter.
    /// </summary>
    public static int Refreshed { get; private set; }

    /// <summary>Candidates found stale but not compiled again (refused, failed): surfaced (A2, re-audit of b64e896).</summary>
    public static int NotRefreshed { get; private set; }

    private static Harmony? _rebuilder;

    /// <summary>The other mods' first patches waiting for the end of the frame (any thread adds, the main thread drains).</summary>
    private static readonly List<MethodBase> _queued = new();
    private static readonly object _queueLock = new();
    private static bool _flushScheduled, _queuedOffMain;

    /// <summary>The thread this class was first used on: the main thread (PatchGuard.Protect at the mod's load). The log only.</summary>
    private static readonly int _firstThread = Environment.CurrentManagedThreadId;

    /// <summary>
    /// The helpers (by module and token) inlined by a replacement of this mod that couldn't be compiled again: their inlined body
    /// stays there, so a patch on them is never reached from it (HookSkip: <see cref="InlinedUnseen"/>).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Module Module, int Token), string> _inlinedUnseen = new();

    /// <summary>
    /// Moves each time <see cref="Surface"/> records helpers (round 4): HookSkip's per-group helper answers, cached up to a second
    /// per Harmony epoch, are asked again at once (a flush records them after the Patch call that moved the epoch).
    /// </summary>
    internal static int UnseenVersion => System.Threading.Volatile.Read(ref _unseenVersion);
    private static int _unseenVersion;

    /// <summary>
    /// Whether a replacement of this mod that may have inlined <paramref name="method"/> couldn't be compiled again after another
    /// mod patched it (A2): a patch on it isn't reached from there, whatever it is gated on.
    /// </summary>
    internal static bool InlinedUnseen(MethodBase? method)
    {
        if (method == null || _inlinedUnseen.IsEmpty)
        {
            return false;
        }
        try
        {
            return _inlinedUnseen.ContainsKey((method.Module, method.MetadataToken));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Another mod patched this method for the first time (no Harmony patch on it before, so no no-inlining flag either:
    /// MonoMod sets it when it detours a method) after replacements of this mod were compiled: one that may have inlined its
    /// body (it calls it, directly or through callees the JIT may inline) runs that body past the new patch. Seen in game:
    /// HookSkip's transpiled hooks inline <c>PlayerChoiceContext.PushModel</c> (32 bytes of IL) and, on v0.113.1, the
    /// 4-argument <c>HookPlayerChoiceContext</c> constructor (87 bytes): a postfix another mod put on PushModel mid-run saw 3
    /// calls where the game made 83,399. In the game without this mod, a hook is compiled at its first call, in a run, after
    /// the other mods' patches, and so calls them.
    ///
    /// Re-audit of b64e896 (A1, A5): never rebuilt inside the other mod's Patch call (a PatchAll batch, maybe on another thread,
    /// Harmony's lock held): the method is queued, and at the end of the frame, on the main thread, every queued method of the
    /// batch is handled at once (<see cref="Flush"/>: each candidate rebuilt once). A call made between the other mod's Patch
    /// call and the frame's end still runs the inlined body, as it did before this check existed. Nothing is queued when the
    /// method can't be inlined (too large, virtual, NoInlining) nor for a value type's method or shared generic code (A3: the
    /// rebuild changes nothing there): the common case, at no cost but this check. Never throws.
    /// </summary>
    internal static void AfterForeignFirstPatch(MethodBase patched, Func<string> ownersOf)
    {
        try
        {
            if (patched.DeclaringType is not { IsValueType: false } declaring || declaring.ContainsGenericParameters || patched.ContainsGenericParameters
                || declaring.IsGenericType || patched.IsGenericMethod || !Inlinable(patched))
            {
                return;
            }
            lock (_queueLock)
            {
                _queued.Add(patched);
                _queuedOffMain |= Environment.CurrentManagedThreadId != _firstThread;
                if (_flushScheduled)
                {
                    return;
                }
                _flushScheduled = true;
            }
            Godot.Callable.From(Flush).CallDeferred();
        }
        catch (Exception e)
        {
            lock (_queueLock)
            {
                _flushScheduled = false;
            }
            // Never in the way of the other mod's Patch call: at worst its patch misses the calls inlined before it.
            Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: after another mod patched {patched.Name}: callers not checked ({e.Message})");
        }
    }

    /// <summary>Godot's main thread (true when that can't be read: the deferred call's own thread, as before).</summary>
    private static bool OnMainThread()
    {
        try
        {
            return Godot.OS.GetThreadCallerId() == Godot.OS.GetMainThreadId();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The queued first patches of other mods (main thread, end of the frame): every candidate (a patched method carrying a patch
    /// of this mod) whose replacement reaches one of them is built again by Harmony once, the same patches in the same order
    /// (<see cref="HarmonyRebuild.Run"/>, which refuses a stateful transpiler of another mod), now with the no-inlining flag set:
    /// its calls reach the new patches. One that isn't (refused, failed) is surfaced (A2): a warning, a <c>bdopt why</c> note
    /// naming it and the other mod, and its inlined helpers recorded (<see cref="InlinedUnseen"/>). The scan time is always logged.
    /// </summary>
    private static void Flush()
    {
        MethodBase[] targets;
        bool offMain;
        if (!OnMainThread())
        {
            // Round 4: a deferred call is run by the main loop; should it ever come on another thread, it goes back there (the
            // queue kept, the flush still scheduled).
            try
            {
                Godot.Callable.From(Flush).CallDeferred();
            }
            catch (Exception e)
            {
                lock (_queueLock)
                {
                    _flushScheduled = false; // The next first patch schedules it again.
                }
                Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: flush called off the main thread, not deferred again ({e.Message})");
            }
            return;
        }
        lock (_queueLock)
        {
            targets = _queued.Distinct().ToArray();
            offMain = _queuedOffMain;
            _queued.Clear();
            _flushScheduled = false;
            _queuedOffMain = false;
        }
        if (targets.Length == 0)
        {
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        string owners = "another mod";
        try
        {
            owners = OwnersOf(targets);
            var keys = new HashSet<(Module, int)>();
            foreach (MethodBase target in targets)
            {
                try
                {
                    keys.Add((target.Module, target.MetadataToken));
                }
                catch (Exception)
                {
                    // A dynamic method: no caller inlines it.
                }
            }
            List<(MethodBase Candidate, MethodBase[] Inlined)> stale = new();
            int candidates = 0;
            foreach (MethodBase candidate in Harmony.GetAllPatchedMethods().ToList())
            {
                try
                {
                    if (candidate.DeclaringType?.Assembly == typeof(Harmony).Assembly || keys.Contains((candidate.Module, candidate.MetadataToken))
                        || Harmony.GetPatchInfo(candidate) is not { } info || !info.Owners.Any(PatchGuard.IsOurs))
                    {
                        continue;
                    }
                    candidates++;
                    MethodBase[] inlined = targets.Where(t => ReachedBy(candidate).Contains((t.Module, t.MetadataToken))).ToArray();
                    if (inlined.Length > 0)
                    {
                        stale.Add((candidate, inlined));
                    }
                }
                catch (Exception)
                {
                    // Not readable: left as it is (as before this check).
                }
            }
            double scanMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            string what = string.Join(", ", targets.Select(HarmonyRebuild.Describe).Distinct());
            if (stale.Count == 0)
            {
                Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: {owners} patched {what} for the first time: none of the {candidates} methods carrying this mod's patches may have inlined it ({scanMs:F0} ms{(offMain ? "; queued from another thread" : "")})");
                return;
            }
            int done = 0;
            List<(MethodBase Candidate, MethodBase[] Inlined, string Why)> notDone = new();
            foreach ((MethodBase method, MethodBase[] inlined) in stale)
            {
                try
                {
                    if (!HarmonyRebuild.Available)
                    {
                        throw new NotSupportedException("Harmony's detour table isn't as expected");
                    }
                    _rebuilder ??= new Harmony(ModEntry.Harmony.Id + "." + nameof(InlineGuard));
                    // Refused there (an exception, nothing touched) when another mod's transpiler on it isn't safe to run again.
                    HarmonyRebuild.Run(method, $"compiling it again after {owners} patched {string.Join(", ", inlined.Select(HarmonyRebuild.Describe))}", () => new PatchProcessor(_rebuilder, method).Patch());
                    done++;
                    Refreshed++;
                }
                catch (Exception e)
                {
                    notDone.Add((method, inlined, e.Message));
                }
            }
            Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: {owners} patched {what} for the first time, after this mod's patches were compiled: "
                + $"{done} methods carrying them that may have inlined it compiled again ({string.Join(", ", stale.Select(s => HarmonyRebuild.Describe(s.Candidate)).Distinct())})"
                + $" (scan {scanMs:F0} ms, {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms in all{(offMain ? "; queued from another thread" : "")})");
            foreach ((MethodBase method, MethodBase[] inlined, string why) in notDone)
            {
                Surface(method, inlined, owners, why);
            }
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: after {owners} patched {string.Join(", ", targets.Select(t => t.Name))}: callers not checked ({e.Message})");
        }
    }

    /// <summary>The other mods patching these methods (display names), for the log and the note.</summary>
    private static string OwnersOf(MethodBase[] targets)
    {
        string[] names = targets.Select(t => Harmony.GetPatchInfo(t)).Where(i => i != null)
            .SelectMany(i => i!.Prefixes.Concat(i.Postfixes).Concat(i.Transpilers).Concat(i.Finalizers).Concat(i.InnerPrefixes).Concat(i.InnerPostfixes))
            .Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => PatchSafety.ModNameOf(p.PatchMethod?.DeclaringType?.Assembly) ?? p.owner).Distinct().ToArray();
        return names.Length > 0 ? string.Join(", ", names) : "another mod";
    }

    /// <summary>
    /// A stale replacement not compiled again (A2): a warning, a note (bdopt why, "Other") naming it, the patched helpers and the
    /// other mods, and the helpers recorded so HookSkip keeps no tolerance for a patch there (<see cref="InlinedUnseen"/>).
    /// </summary>
    private static void Surface(MethodBase method, MethodBase[] inlined, string owners, string why)
    {
        NotRefreshed++;
        string helpers = string.Join(", ", inlined.Select(HarmonyRebuild.Describe).Distinct());
        foreach (MethodBase helper in inlined)
        {
            try
            {
                _inlinedUnseen[(helper.Module, helper.MetadataToken)] = HarmonyRebuild.Describe(method);
            }
            catch (Exception)
            {
                // A dynamic method: nothing inlines it.
            }
        }
        System.Threading.Interlocked.Increment(ref _unseenVersion);
        Log.Warn($"[{ModEntry.Id}] {nameof(InlineGuard)}: {HarmonyRebuild.Describe(method)} may have inlined {helpers}, which {owners} patched after this mod's patches were compiled, and couldn't be compiled again ({why}): its calls there don't reach that patch");
        try
        {
            string[] mods = owners.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            PatchSafety.Note(nameof(InlineGuard), HarmonyRebuild.Describe(method), $"{HarmonyRebuild.Describe(method)} may run the body of {helpers} inlined, past the patch {owners} put there after this mod's patches were compiled (compiling it again: {why})", mods);
        }
        catch (Exception)
        {
            // Display only.
        }
    }

    /// <summary>
    /// C1: per candidate, every method (by module and token) its replacement may have inlined: its code after every transpiler
    /// and each patch method, and what they call through callees the JIT may inline (<see cref="CalleeDepth"/>). Worked out again
    /// when Harmony's patches change (<see cref="HarmonyWatch.Epoch"/>).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (int Epoch, HashSet<(Module, int)> Methods)> _reached = new();

    private static HashSet<(Module, int)> ReachedBy(MethodBase candidate)
    {
        int epoch = HarmonyWatch.Epoch;
        if (_reached.TryGetValue(candidate, out var known) && known.Epoch == epoch)
        {
            return known.Methods;
        }
        var methods = new HashSet<(Module, int)>();
        var expanded = new Dictionary<MethodBase, int>();
        void Add(MethodBase method)
        {
            try
            {
                methods.Add((method.Module, method.MetadataToken));
            }
            catch (Exception)
            {
                // A dynamic method: nothing to inline.
            }
        }
        void Expand(MethodBase method, int depth)
        {
            if (depth <= 0 || (expanded.TryGetValue(method, out int seen) && seen >= depth))
            {
                return;
            }
            expanded[method] = depth;
            MethodBase[] callees;
            try
            {
                callees = OwnCallees(method);
            }
            catch (Exception)
            {
                return; // No readable body (native, dynamic).
            }
            foreach (MethodBase callee in callees)
            {
                Add(callee);
                if (Inlinable(callee))
                {
                    Expand(callee, depth - 1);
                }
            }
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(candidate);
        try
        {
            MethodBase[] code = info is { Transpilers.Count: > 0 } ? TranspiledCallees(candidate, info) : OwnCallees(candidate);
            foreach (MethodBase callee in code)
            {
                Add(callee);
                if (Inlinable(callee))
                {
                    Expand(callee, CalleeDepth);
                }
            }
        }
        catch (Exception)
        {
            // No readable body or a transpiler that throws: its patch methods are still looked at.
        }
        if (info != null)
        {
            foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.InnerPrefixes).Concat(info.InnerPostfixes))
            {
                MethodInfo? method;
                try
                {
                    method = patch.PatchMethod;
                }
                catch (Exception)
                {
                    continue;
                }
                if (method == null)
                {
                    continue;
                }
                // Called by the replacement: one level down, like a callee of its own code.
                Add(method);
                if (Inlinable(method))
                {
                    Expand(method, CalleeDepth);
                }
            }
        }
        _reached[candidate] = (epoch, methods);
        return methods;
    }

    /// <summary>The candidate's code after every transpiler, read again only when its transpilers change (<see cref="_transpiledCallees"/>).</summary>
    private static MethodBase[] TranspiledCallees(MethodBase patched, HarmonyLib.Patches info)
    {
        string transpilers = string.Join("|", info.Transpilers.Select(t => $"{t.owner}#{t.index}#{t.priority}#{t.PatchMethod?.Module.ModuleVersionId}#{t.PatchMethod?.MetadataToken}"));
        if (_transpiledCallees.TryGetValue(patched, out var cached) && cached.Transpilers == transpilers)
        {
            return cached.Callees;
        }
        // Runs the transpilers again, as any rebuild does. One Harmony can't run again safely (HarmonyRebuild.Blocker) isn't run:
        // the original's calls stand for its code (transpilers mostly keep them), so a stale one is still found, its rebuild
        // refused and surfaced (A2).
        if (HarmonyRebuild.Blocker(patched) != null)
        {
            return OwnCallees(patched);
        }
        MethodBase[] code = Callees(PatchProcessor.GetCurrentInstructions(patched).Select(c => (c.opcode, (object?)c.operand)));
        _transpiledCallees[patched] = (transpilers, code);
        return code;
    }

    /// <summary>
    /// What is known of a method: it reaches a target method within <c>FoundAt</c> calls (int.MaxValue: not
    /// known to), and doesn't within <c>NotWithin</c> calls (-1: not known). An answer only holds for the depth it was
    /// searched to: a method first met as a callee at depth 0 (its own calls only) must be searched again when it is met
    /// again at a greater depth (as a patch method, <see cref="CalleeDepth"/> - 1), or its callees' calls would never be looked at.
    /// A memo holds for one set of targets only (a new one per set).
    /// </summary>
    private record struct Reach(int FoundAt, int NotWithin);

    /// <summary>The methods a method's own IL calls or constructs (call, callvirt, newobj): its IL never changes, read once.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, MethodBase[]> _ownCallees = new();

    /// <summary>
    /// Per patched method, the methods its code after every transpiler calls, and the transpilers that code came from (in
    /// Harmony's list order): read again only when they change, so the transpilers rarely run outside a rebuild.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (string Transpilers, MethodBase[] Callees)> _transpiledCallees = new();

    private static MethodBase[] Callees(IEnumerable<(OpCode Op, object? Operand)> code) =>
        code.Where(c => c.Operand is MethodBase && (c.Op == OpCodes.Call || c.Op == OpCodes.Callvirt || c.Op == OpCodes.Newobj))
            .Select(c => (MethodBase)c.Operand!).ToArray();

    private static MethodBase[] OwnCallees(MethodBase method) =>
        _ownCallees.GetOrAdd(method, m => Callees(PatchProcessor.ReadMethodBody(m).Select(i => (i.Key, (object?)i.Value))));

    /// <summary>
    /// Whether the replacement Harmony compiled for this patched method calls a target method, directly or through a callee
    /// the JIT may inline: its code after every transpiler (the original body when there is none), and each patch method of
    /// every mod (prefixes, postfixes, finalizers, inner patches), which the replacement calls and the JIT may inline too.
    /// </summary>
    private static bool ReplacementReaches(MethodBase patched, Func<MethodBase, bool> isTarget, Dictionary<MethodBase, Reach> known)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(patched);
        try
        {
            MethodBase[] code;
            if (info is { Transpilers.Count: > 0 })
            {
                // Runs the transpilers again, as any rebuild does (they must allow it: another mod may patch later), once per
                // list of transpilers. One Harmony can't find again can't be rebuilt either (HarmonyRebuild.Run refuses it):
                // nothing to read.
                string transpilers = string.Join("|", info.Transpilers.Select(t => $"{t.owner}#{t.index}#{t.priority}#{t.PatchMethod?.Module.ModuleVersionId}#{t.PatchMethod?.MetadataToken}"));
                if (_transpiledCallees.TryGetValue(patched, out var cached) && cached.Transpilers == transpilers)
                {
                    code = cached.Callees;
                }
                else
                {
                    code = HarmonyRebuild.Blocker(patched) != null ? Array.Empty<MethodBase>()
                        : Callees(PatchProcessor.GetCurrentInstructions(patched).Select(c => (c.opcode, (object?)c.operand)));
                    _transpiledCallees[patched] = (transpilers, code);
                }
            }
            else
            {
                code = OwnCallees(patched);
            }
            if (Calls(code, CalleeDepth, isTarget, known))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // No readable body (native, dynamic) or a transpiler that throws: its patch methods are still looked at.
        }
        if (info == null)
        {
            return false;
        }
        foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.InnerPrefixes).Concat(info.InnerPostfixes))
        {
            MethodInfo? method;
            try
            {
                method = patch.PatchMethod;
            }
            catch (Exception)
            {
                continue;
            }
            // Called by the replacement: one level down, like a callee of its own code.
            if (method != null && (isTarget(method) || (Inlinable(method) && Reaches(method, CalleeDepth - 1, isTarget, known))))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether these calls reach a target method, or a callee the JIT may inline does (up to this depth).</summary>
    private static bool Calls(MethodBase[] callees, int depth, Func<MethodBase, bool> isTarget, Dictionary<MethodBase, Reach> known)
    {
        foreach (MethodBase callee in callees)
        {
            if (isTarget(callee) || (depth > 0 && Inlinable(callee) && Reaches(callee, depth - 1, isTarget, known)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the JIT may inline a call of this method: a body, not marked NoInlining, not virtual unless it can be
    /// devirtualized (final, or its type sealed), and small enough or marked AggressiveInlining.
    /// </summary>
    private static bool Inlinable(MethodBase method) => _inlinable.GetOrAdd(method, InlinableNow);

    /// <summary>C1: <see cref="Inlinable"/> per method (its flags and IL never change).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, bool> _inlinable = new();

    private static bool InlinableNow(MethodBase method)
    {
        try
        {
            MethodImplAttributes impl = method.MethodImplementationFlags;
            if (method.IsAbstract || (impl & MethodImplAttributes.NoInlining) != 0
                || (method.IsVirtual && !method.IsFinal && method.DeclaringType is not { IsSealed: true }))
            {
                return false;
            }
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            return il != null && (il.Length <= MaxInlineIl || (impl & MethodImplAttributes.AggressiveInlining) != 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether this method's own code calls a target method, or a callee the JIT may inline does (up to this depth).</summary>
    private static bool Reaches(MethodBase method, int depth, Func<MethodBase, bool> isTarget, Dictionary<MethodBase, Reach> known)
    {
        if (!known.TryGetValue(method, out Reach reach))
        {
            reach = new Reach(int.MaxValue, -1);
        }
        else if (depth >= reach.FoundAt)
        {
            return true;
        }
        else if (depth <= reach.NotWithin)
        {
            return false;
        }
        // No recursion guard: the depth goes down at each call, so a cycle of callees ends at depth 0 by itself (an
        // unfinished answer kept as final would hide what the rest of the cycle calls).
        bool found = false;
        try
        {
            found = Calls(OwnCallees(method), depth, isTarget, known);
        }
        catch (Exception)
        {
            // No readable body (native, dynamic): nothing of it to recompile.
        }
        // Read again: the callees searched meanwhile may have added to this method's entry (a cycle).
        reach = known.TryGetValue(method, out Reach now) ? now : reach;
        known[method] = found ? reach with { FoundAt = Math.Min(reach.FoundAt, depth) } : reach with { NotWithin = Math.Max(reach.NotWithin, depth) };
        return found;
    }
}
