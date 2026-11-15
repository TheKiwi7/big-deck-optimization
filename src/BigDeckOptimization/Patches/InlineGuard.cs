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
    /// JIT may inline into it (<see cref="ReplacementReaches"/>). One step per patched method: ~300 ms
    /// in all, spread over frames by <see cref="ModEntry"/> (only what a run does needs it done).
    ///
    /// A rebuild can fail, and a failed one used to leave the method with no patches of any mod (v1.0.0: BaseLib's postfix
    /// on <c>NGlobalUi._Ready</c> gone, see <see cref="HarmonyRebuild"/>): each goes through <see cref="HarmonyRebuild.Run{T}"/>,
    /// which refuses what can't be rebuilt and puts the previous code back after a failure. Without its Harmony internals
    /// nothing is rebuilt: inlined callees are a smaller loss than another mod's patches. <see cref="Available"/> isn't needed:
    /// MonoMod set the flags when it detoured the protected methods.
    /// </summary>
    public static IEnumerable<bool> RecompileInliners(Harmony harmony)
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
        foreach (MethodBase patched in Harmony.GetAllPatchedMethods().ToList())
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                bool rebuild = ReplacementReaches(patched, reaches);
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
                    HarmonyRebuild.Run(patched, "compiling it again", () => new PatchProcessor(harmony, patched).Patch());
                    Recompiled++;
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
    }

    /// <summary>
    /// What is known of a method: it reaches a protected method within <c>FoundAt</c> calls (int.MaxValue: not
    /// known to), and doesn't within <c>NotWithin</c> calls (-1: not known). An answer only holds for the depth it was
    /// searched to: a method first met as a callee at depth 0 (its own calls only) must be searched again when it is met
    /// again at a greater depth (as a patch method, <see cref="CalleeDepth"/> - 1), or its callees' calls would never be looked at.
    /// </summary>
    private record struct Reach(int FoundAt, int NotWithin);

    /// <summary>
    /// Whether the replacement Harmony compiled for this patched method calls a protected method, directly or through a callee
    /// the JIT may inline: its code after every transpiler (the original body when there is none), and each patch method of
    /// every mod (prefixes, postfixes, finalizers, inner patches), which the replacement calls and the JIT may inline too.
    /// </summary>
    private static bool ReplacementReaches(MethodBase patched, Dictionary<MethodBase, Reach> known)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(patched);
        try
        {
            IEnumerable<(OpCode, object?)> code;
            if (info is { Transpilers.Count: > 0 })
            {
                // Runs the transpilers again, as any rebuild does (they must allow it: another mod may patch later). One
                // Harmony can't find again can't be rebuilt either (HarmonyRebuild.Run refuses it): nothing to read.
                code = HarmonyRebuild.Blocker(patched) != null ? Enumerable.Empty<(OpCode, object?)>()
                    : PatchProcessor.GetCurrentInstructions(patched).Select(c => (c.opcode, (object?)c.operand)).ToList();
            }
            else
            {
                code = PatchProcessor.ReadMethodBody(patched).Select(i => (i.Key, (object?)i.Value)).ToList();
            }
            if (Calls(code, CalleeDepth, known))
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
            if (method != null && (_protected.Contains(method) || (Inlinable(method) && Reaches(method, CalleeDepth - 1, known))))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether this code calls a protected method, or a callee the JIT may inline does (up to this depth).</summary>
    private static bool Calls(IEnumerable<(OpCode Op, object? Operand)> code, int depth, Dictionary<MethodBase, Reach> known)
    {
        foreach ((OpCode op, object? operand) in code)
        {
            if (operand is not MethodBase callee || (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj))
            {
                continue;
            }
            if (_protected.Contains(callee) || (depth > 0 && Inlinable(callee) && Reaches(callee, depth - 1, known)))
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
    private static bool Inlinable(MethodBase method)
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

    /// <summary>Whether this method's own code calls a protected method, or a callee the JIT may inline does (up to this depth).</summary>
    private static bool Reaches(MethodBase method, int depth, Dictionary<MethodBase, Reach> known)
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
            found = Calls(PatchProcessor.ReadMethodBody(method).Select(i => (i.Key, (object?)i.Value)), depth, known);
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
