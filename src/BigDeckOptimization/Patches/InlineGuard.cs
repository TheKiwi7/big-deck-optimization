using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
}
