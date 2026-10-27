using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Patches;

/// <summary>
/// More read-only passes run inside <see cref="ListenerCache"/> refresh scopes, found by profiling an 8000-card combat.
/// Each reads keywords/damage through hooks once per card or per enemy, and every hook call rebuilt the listener list
/// (all cards): O(cards²).
///   - First turn: <c>SetupPlayerTurn</c> filters the draw pile for Innate cards (and bottom-of-pile enchantments) with
///     <c>Where(...).ToList()</c>: 6 s with 8000 cards. Both <c>ToList</c> calls run the same LINQ inside a scope.
///   - BetterSpire2 (optional): <c>DamageTracker.Recalculate</c>, its damage preview recomputed 4 times a second,
///     only calls <c>Hook.ModifyDamage</c> and updates its own labels: ~100 ms per run with 8000 cards.
/// Scopes follow <see cref="ListenerCache.RefreshEnabled"/> (the "combat refresh" setting).
/// </summary>
public static class ExtraScopes
{
    public const string Name = "ExtraScopes";

    /// <summary>v0.107.1 <c>CombatManager.SetupPlayerTurn</c> state machine, whose two ToList calls are replaced.</summary>
    private const string SetupPlayerTurnFingerprint = "98A103A1D8204ED3";

    private const string BetterSpireAssembly = "BetterSpire2";

#if BENCH
    /// <summary>Test builds: re-run checked passes the vanilla way and compare (costly: off to measure real timings).</summary>
    public static bool BenchChecks { get; set; } = true;
#endif

    public static void Apply(Harmony harmony)
    {
        ApplyFirstTurn(harmony);
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == BetterSpireAssembly))
        {
            ModEntry.TryApply(Name + ".BetterSpire2", ApplyBetterSpire);
        }
        else
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == BetterSpireAssembly)
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            ModEntry.TryApply(Name + ".BetterSpire2", ApplyBetterSpire);
        }
    }

    private static void ApplyFirstTurn(Harmony harmony)
    {
        MethodInfo setup = AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn") ?? throw new MissingMethodException("CombatManager.SetupPlayerTurn");
        MethodInfo moveNext = AccessTools.AsyncMoveNext(setup) ?? throw new MissingMethodException("SetupPlayerTurn state machine");
        PatchSafety.RequireFingerprint(moveNext, SetupPlayerTurnFingerprint);
        PatchGuard.Patch(harmony, moveNext, transpiler: new HarmonyMethod(typeof(ExtraScopes), nameof(FirstTurnTranspiler)));
    }

    private static IEnumerable<CodeInstruction> FirstTurnTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo toList = AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToList)).MakeGenericMethod(typeof(CardModel));
        MethodInfo scoped = AccessTools.Method(typeof(ExtraScopes), nameof(ToListInScope)).MakeGenericMethod(typeof(CardModel));
        List<CodeInstruction> calls = code.Where(c => c.Calls(toList)).ToList();
        if (calls.Count != 2)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: expected 2 ToList calls in SetupPlayerTurn, found {calls.Count}: left vanilla");
            return code;
        }
        foreach (CodeInstruction call in calls)
        {
            call.opcode = OpCodes.Call;
            call.operand = scoped; // Same signature: IEnumerable<T> -> List<T>.
        }
        return code;
    }

    /// <summary><c>source.ToList()</c>, with the lazy query evaluated inside a refresh scope.</summary>
    public static List<T> ToListInScope<T>(IEnumerable<T> source)
    {
        List<T> result;
#if BENCH
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        ListenerCache.EnterRefresh();
        try
        {
            result = source.ToList();
        }
        finally
        {
            ListenerCache.ExitRefresh(null);
        }
#if BENCH
        if (!BenchChecks)
        {
            return result;
        }
        // Test builds: the same query the vanilla way must give the same cards in the same order.
        double optimizedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        List<T> vanilla = ListenerCache.RunVanilla(() => source.ToList());
        double vanillaMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Log.Info($"[{ModEntry.Id}] {Name} first-turn filter: {result.Count} cards, optimized {optimizedMs:F0} ms, vanilla {vanillaMs:F0} ms, "
            + (vanilla.SequenceEqual(result) ? "OK: identical to vanilla" : $"MISMATCH: vanilla has {vanilla.Count}"));
#endif
        return result;
    }

    private static void ApplyBetterSpire(Harmony harmony)
    {
        Type tracker = AccessTools.TypeByName("BetterSpire2.DamageTracker") ?? throw new PatchSkippedException("BetterSpire2 has no DamageTracker");
        MethodInfo recalculate = AccessTools.Method(tracker, "Recalculate", Type.EmptyTypes) ?? throw new PatchSkippedException("BetterSpire2 has no DamageTracker.Recalculate()");
        PatchGuard.Patch(harmony, recalculate,
            prefix: new HarmonyMethod(typeof(ListenerCache), nameof(ListenerCache.EnterDisplayRefresh)),
            finalizer: new HarmonyMethod(typeof(ListenerCache), nameof(ListenerCache.ExitRefresh)));
    }
}
