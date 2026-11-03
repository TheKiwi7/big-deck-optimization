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
/// loads (<c>mdfNotInline</c> in the runtime's MethodDesc; the method MonoMod's RuntimeDetour used). That word isn't a public
/// API: <see cref="Available"/> checks at load, on methods of this class declared either way, that the flag is where it is
/// expected, and otherwise nothing is written anywhere and the patches that need this stay off.
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
    /// </summary>
    public static void Protect(MethodBase method)
    {
        if (!Available || method.ContainsGenericParameters || method.IsGenericMethod || method.DeclaringType is not { IsGenericType: false, IsValueType: false })
        {
            return;
        }
        try
        {
            IntPtr handle = method.MethodHandle.Value;
            _protected.Add(method);
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

    /// <summary>Calls of a small non-virtual callee are followed this deep: the JIT inlines inside what it inlines.</summary>
    private const int CalleeDepth = 2;
    private const int SmallCalleeIl = 96;

    /// <summary>
    /// The flag only stops what is compiled after it is set. A method another mod patched before this one's patches applied
    /// (or one of this mod's own applied earlier) already has its replacement compiled, with the original bodies of protected
    /// methods it calls possibly inlined: there a counted write isn't counted (BaseLib's postfix on <c>SetToFreeThisTurn</c>
    /// had <c>SetThisTurnOrUntilPlayed</c>'s body in it, so Bullet Time changed hand costs unseen). Once every patch is in,
    /// each patched method whose code calls a protected one (directly or through small callees) gets its replacement built
    /// again by Harmony, the same patches in the same order: now compiled with the flags set.
    /// </summary>
    public static void RecompileInliners(Harmony harmony)
    {
        if (!Available || _protected.Count == 0)
        {
            return;
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int failed = 0;
        Dictionary<MethodBase, bool> reaches = new();
        foreach (MethodBase patched in Harmony.GetAllPatchedMethods().ToList())
        {
            try
            {
                if (!Reaches(patched, CalleeDepth, reaches))
                {
                    continue;
                }
                new PatchProcessor(harmony, patched).Patch();
                Recompiled++;
            }
            catch (Exception e)
            {
                failed++;
                Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: {patched.DeclaringType?.Name}.{patched.Name} left as compiled: {e.Message}");
            }
        }
        Log.Info($"[{ModEntry.Id}] {nameof(InlineGuard)}: {Recompiled} patched methods calling protected ones compiled again"
            + (failed > 0 ? $", {failed} failed" : "") + $" ({System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
    }

    /// <summary>Whether this method's own code calls a protected method, or a small non-virtual callee does (up to this depth).</summary>
    private static bool Reaches(MethodBase method, int depth, Dictionary<MethodBase, bool> known)
    {
        if (known.TryGetValue(method, out bool answer))
        {
            return answer;
        }
        known[method] = false; // Recursion guard.
        bool found = false;
        try
        {
            foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
            {
                if (instruction.Value is not MethodBase callee || (instruction.Key != OpCodes.Call && instruction.Key != OpCodes.Callvirt && instruction.Key != OpCodes.Newobj))
                {
                    continue;
                }
                if (_protected.Contains(callee))
                {
                    found = true;
                    break;
                }
                if (depth > 0 && !callee.IsVirtual && !callee.IsAbstract && callee.GetMethodBody() is { } body && body.GetILAsByteArray()?.Length <= SmallCalleeIl
                    && Reaches(callee, depth - 1, known))
                {
                    found = true;
                    break;
                }
            }
        }
        catch (Exception)
        {
            // No readable body (native, dynamic): nothing of it to recompile.
        }
        return known[method] = found;
    }
}
