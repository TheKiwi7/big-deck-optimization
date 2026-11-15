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
/// Scopes follow <see cref="ListenerCache.RefreshEnabled"/> (the "combat refresh" setting). The scope bookkeeping runs
/// inside the game's code (the first-turn one inside an async state machine, where PatchGuard never sees an exception):
/// an exception from it turns that scope off (faulted) and the pass runs without a scope, as in the game.
/// </summary>
public static class ExtraScopes
{
    public const string Name = "ExtraScopes";

    /// <summary>v0.107.1 <c>CombatManager.SetupPlayerTurn</c> state machine, whose two ToList calls are replaced.</summary>
    private const string SetupPlayerTurnFingerprint = "98A103A1D8204ED3";

    private const string BetterSpireAssembly = "BetterSpire2";
    private const string BetterSpireName = Name + ".BetterSpire2";

    /// <summary>
    /// BetterSpire2 1.83.14's <c>DamageTracker.Recalculate</c> and the methods it calls that read the game (the others only
    /// show or hide its labels): the scope relies on them reading the combat without changing it, synchronously, on the
    /// main thread (what a refresh scope's cached listener lists need). A changed one leaves the scope off.
    /// </summary>
    private static readonly (string Method, string Fingerprint)[] BetterSpireReadOnly =
    {
        ("Recalculate", "30626A8DDCE04FBA"),
        ("InvokeModifyDamage", "02EAC66D4A82BF51"),
        ("CountInfernoTriggers", "2E63BF3D056FBAB6"),
        ("GetHibernateSharedFrostBlock", "A95DC5113D2DF1C6"),
        ("IsMonsterInteractable", "F45A56F32F1E18A4"),
    };

    /// <summary>The methods above in this game: another mod patching one may make the pass write state, the scope then stays off.</summary>
    private static MethodBase[] _betterSpireMethods = Array.Empty<MethodBase>();

    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Each scope also stays off once faulted (its own error below, or PatchGuard's: Harmony may have failed to remove the
    /// patch, whose code would otherwise keep running).
    /// </summary>
    private static bool FirstTurnOn => Enabled && !PatchSafety.IsFaulted(Name);
    private static bool BetterSpireOn => Enabled && !PatchSafety.IsFaulted(BetterSpireName);

#if BENCH
    /// <summary>Test builds: re-run checked passes the vanilla way and compare (costly: off to measure real timings).</summary>
    public static bool BenchChecks { get; set; } = true;
    /// <summary>First-turn filters run inside a scope (the effects check).</summary>
    public static long FirstTurnPasses { get; private set; }
#endif

    public static void Apply(Harmony harmony)
    {
        // BetterSpire2's scope is a part of its own (own name, own Harmony id): set up first, whatever the first turn does.
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == BetterSpireAssembly))
        {
            ModEntry.TryApply(BetterSpireName, ApplyBetterSpire);
        }
        else
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
        // Not caught: a first turn left vanilla (changed game code) makes ModEntry record this part as not applied, so
        // bdopt and the menu say so like for any other part.
        ApplyFirstTurn(harmony);
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == BetterSpireAssembly)
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            ModEntry.TryApply(BetterSpireName, ApplyBetterSpire);
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
        // The query itself is the game's: its exceptions reach the game as they would. Only the scope's are caught.
        int scope = FirstTurnOn ? Enter(display: false, Name) : NoScope;
#if BENCH
        if (scope != NoScope)
        {
            FirstTurnPasses++; // Only passes really run inside a scope prove the effect.
        }
#endif
        try
        {
            result = source.ToList();
        }
        finally
        {
            if (scope != NoScope)
            {
                Exit(Name, scope);
            }
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

    /// <summary>No scope opened (or its opening threw).</summary>
    private const int NoScope = int.MinValue;

    /// <summary>
    /// Opens a scope: the state <see cref="Exit"/> takes (the depth found for a display scope, -1 "one level less" for a
    /// game-state one), or <see cref="NoScope"/> (this scope faulted) when its bookkeeping throws: the pass then runs
    /// without one. Both count the scope last: an exception leaves no exit owed.
    /// </summary>
    private static int Enter(bool display, string name)
    {
        try
        {
            if (display)
            {
                ListenerCache.EnterDisplayRefresh(out int depth);
                return depth;
            }
            ListenerCache.EnterRefresh();
            return -1;
        }
        catch (Exception e)
        {
            Disable(name, e);
            return NoScope;
        }
    }

    private static void Exit(string name, int scope)
    {
        try
        {
            ListenerCache.ExitRefresh(null, scope);
        }
        catch (Exception e)
        {
            Disable(name, e);
        }
    }

    private static void Disable(string name, Exception e)
    {
        PatchSafety.Fault(name);
        Log.Error($"[{ModEntry.Id}] {name} disabled after exception, the pass runs without a scope (vanilla): {e}");
    }

    private static void ApplyBetterSpire(Harmony harmony)
    {
        Type tracker = AccessTools.TypeByName("BetterSpire2.DamageTracker") ?? throw new PatchSkippedException("BetterSpire2 has no DamageTracker");
        MethodInfo recalculate = AccessTools.Method(tracker, "Recalculate", Type.EmptyTypes) ?? throw new PatchSkippedException("BetterSpire2 has no DamageTracker.Recalculate()");
        var methods = new List<MethodBase>();
        foreach ((string name, string fingerprint) in BetterSpireReadOnly)
        {
            MethodInfo? method;
            try
            {
                method = name == "Recalculate" ? recalculate : AccessTools.Method(tracker, name);
            }
            catch (AmbiguousMatchException)
            {
                method = null; // Overloaded since: not the code checked.
            }
            if (method == null)
            {
                throw new PatchSkippedException($"BetterSpire2's DamageTracker.{name} not found as one method");
            }
            PatchSafety.RequireFingerprint(method, fingerprint);
            methods.Add(method);
        }
        _betterSpireMethods = methods.ToArray();
        PatchGuard.Patch(harmony, recalculate,
            prefix: new HarmonyMethod(typeof(ExtraScopes), nameof(BetterSpirePrefix)),
            finalizer: new HarmonyMethod(typeof(ExtraScopes), nameof(BetterSpireFinalizer)));
    }

    /// <summary>A display refresh scope around BetterSpire2's preview, while what it relies on holds.</summary>
    private static void BetterSpirePrefix(out int __state)
    {
        __state = NoScope;
        if (!BetterSpireOn)
        {
            return;
        }
        try
        {
            foreach (MethodBase method in _betterSpireMethods)
            {
                if (PatchSafety.IsPatchedByOthers(method))
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Disable(BetterSpireName, e);
            return;
        }
        __state = Enter(display: true, BetterSpireName);
    }

    /// <summary>Closes the scope the prefix opened. Void: Harmony rethrows BetterSpire2's own exception unchanged.</summary>
    private static void BetterSpireFinalizer(int __state)
    {
        if (__state != NoScope)
        {
            Exit(BetterSpireName, __state);
        }
    }
}
