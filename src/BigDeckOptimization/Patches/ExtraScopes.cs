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
        MethodInfo setup = Reflect.Method(typeof(CombatManager), "SetupPlayerTurn") ?? throw new MissingMethodException("CombatManager.SetupPlayerTurn");
        MethodInfo moveNext = Reflect.AsyncMoveNext(setup) ?? throw new MissingMethodException("SetupPlayerTurn state machine");
        PatchSafety.RequireFingerprint(moveNext, SetupPlayerTurnFingerprint);
        // The scope relies on the query reading the draw pile without changing it: its predicates are lambdas of that state
        // machine. Another mod's patch on one of them may make the pass write: no scope then (strict). On the state machine
        // itself only what runs inside the scoped ToList calls matters: see FirstTurnAllowed and FirstTurnTranspiler.
        _moveNext = moveNext;
        _lambdas = MethodFingerprint.GeneratedCode(moveNext).Where(m => m != moveNext).Distinct().ToArray();
        // The game's code, read once before this mod patches it (Harmony's reader: no transpiler runs).
        _originalFirstTurn = PatchProcessor.GetOriginalInstructions(moveNext).Select(TranspilerJudge.Ins.From).ToArray();
        foreach (MethodBase code in _lambdas)
        {
            PatchSafety.Watch(Name, code);
        }
        PatchSafety.Watch(Name, moveNext, FirstTurnAllowed);
        PatchSafety.WatchNote(Name, NoteFirstTurnCode);
        PatchGuard.Patch(harmony, moveNext, transpiler: new HarmonyMethod(typeof(ExtraScopes), nameof(FirstTurnTranspiler)) { priority = Priority.Last });
    }

    /// <summary>SetupPlayerTurn's state machine, the lambdas the scoped queries run, and the game's code of the state machine.</summary>
    private static MethodBase? _moveNext;
    private static MethodBase[] _lambdas = Array.Empty<MethodBase>();
    private static TranspilerJudge.Ins[] _originalFirstTurn = Array.Empty<TranspilerJudge.Ins>();

    /// <summary>
    /// Why the transpiler left the first turn's queries as the game's calls at the state machine's last rebuild (other mods'
    /// transpilers changed the code feeding them), else null. Display only: the decision is in the code itself.
    /// </summary>
    private static volatile string? _firstTurnRefusal;
    private static string _firstTurnLogged = "";
#if BENCH
    /// <summary>The transpiler's last verdict on other mods' transpilers ("" when there were none): the foreign tests' line.</summary>
    public static string FirstTurnVerdict { get; private set; } = "";
#endif

    /// <summary>
    /// Another mod patches the first turn's query code. The lambdas strictly. On the state machine, other mods' prefixes,
    /// postfixes and finalizers run before or after a MoveNext call, never inside the ToList calls the scope covers: they
    /// stand. So do their transpilers when Harmony runs them all before this mod's (<c>Priority.Last</c>): the code they
    /// produce is what <see cref="FirstTurnTranspiler"/> judged (it redirects the calls only when the code feeding them is the
    /// game's). Inner prefixes and postfixes, a transpiler sorted after this mod's or made by a factory: a back-off.
    /// </summary>
    private static bool FirstTurnPatched()
    {
        foreach (MethodBase code in _lambdas)
        {
            if (PatchSafety.IsPatchedByOthers(code))
            {
                return true;
            }
        }
        return PatchSafety.IsPatchedByOthers(_moveNext, FirstTurnAllowed());
    }

    /// <summary>Other mods' patches on the state machine that can't run inside the scope (above). Never throws: none then.</summary>
    private static MethodInfo[] FirstTurnAllowed()
    {
        try
        {
            return _moveNext == null || Harmony.GetPatchInfo(_moveNext) is not { } info ? Array.Empty<MethodInfo>() : FirstTurnAllowed(info);
        }
        catch
        {
            return Array.Empty<MethodInfo>();
        }
    }

    /// <summary>The allowed patches of that patch list (inner prefixes and postfixes never: they run inside the ToList calls).</summary>
    private static MethodInfo[] FirstTurnAllowed(HarmonyLib.Patches info)
    {
        var allowed = new List<MethodInfo>();
        foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
        {
            if (!PatchGuard.IsOurs(patch.owner) && PatchSafety.PatchMethodOf(patch) is { } method)
            {
                allowed.Add(method);
            }
        }
        Patch[] transpilers = info.Transpilers.ToArray();
        if (transpilers.Any(p => !PatchGuard.IsOurs(p.owner)) && OwnTranspilerLast(transpilers))
        {
            allowed.AddRange(transpilers.Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => PatchSafety.PatchMethodOf(p)!));
        }
        return allowed.ToArray();
    }

#if BENCH
    /// <summary>
    /// <c>bdperf firstturn inner</c>: Harmony 2.4.2 registers inner prefixes and postfixes but can't build them, so no foreign
    /// one can be installed in game. A synthetic patch list of the state machine (another mod's void prefix, postfix and inner
    /// postfix) goes through the allowed list and the same filter as <see cref="PatchSafety.IsPatchedByOthers(MethodBase?, MethodInfo[], string)"/>:
    /// the prefix and postfix must stand, the inner postfix must be left over (a back-off). Throws otherwise.
    /// </summary>
    internal static string BenchInnerPatch()
    {
        if (_moveNext == null)
        {
            throw new InvalidOperationException("FIRSTTURN inner: the first turn's state machine isn't resolved (part not applied)");
        }
        MethodInfo prefix = Reflect.Method(typeof(ExtraScopes), nameof(BenchForeignPrefix))!;
        MethodInfo postfix = Reflect.Method(typeof(ExtraScopes), nameof(BenchForeignPostfix))!;
        MethodInfo inner = Reflect.Method(typeof(ExtraScopes), nameof(BenchForeignInner))!;
        const string owner = "bdtest.foreign.firstturn";
        var none = Array.Empty<Patch>();
        var info = new HarmonyLib.Patches(new[] { new Patch(prefix, 0, owner, Priority.Normal, null, null, false) },
            new[] { new Patch(postfix, 1, owner, Priority.Normal, null, null, false) }, none, none, none,
            new[] { new Patch(inner, 2, owner, Priority.Normal, null, null, false) });
        MethodInfo[] allowed = FirstTurnAllowed(info);
        List<MethodInfo> left = PatchSafety.AllPatches(info)
            .Where(p => !PatchGuard.IsOurs(p.owner) && !(PatchSafety.PatchMethodOf(p) is { } m && allowed.Any(a => a.MethodHandle == m.MethodHandle)))
            .Select(p => PatchSafety.PatchMethodOf(p)!).ToList();
        string line = $"FIRSTTURN inner: allowed {string.Join(", ", allowed.Select(m => m.Name))}; backs off for {string.Join(", ", left.Select(m => m.Name))}";
        bool ok = left.Count == 1 && left[0].MethodHandle == inner.MethodHandle;
        return ok ? line : throw new InvalidOperationException(line + " (expected: the inner postfix only)");
    }

    private static void BenchForeignPrefix()
    {
    }

    private static void BenchForeignPostfix()
    {
    }

    private static void BenchForeignInner()
    {
    }
#endif

    /// <summary>This mod's transpiler is the only one of this mod there and Harmony runs it last (no factory asked to sort).</summary>
    private static bool OwnTranspilerLast(Patch[] transpilers)
    {
        MethodInfo own = Reflect.Method(typeof(ExtraScopes), nameof(FirstTurnTranspiler));
        var methods = new MethodInfo[transpilers.Length];
        for (int i = 0; i < transpilers.Length; i++)
        {
            // Harmony's sort would call a factory: never run another mod's code here.
            if (PatchSafety.PatchMethodOf(transpilers[i]) is not { } method || PatchSafety.IsFactory(method))
            {
                return false;
            }
            methods[i] = method;
            transpilers[i].PatchMethod = method;
        }
        if (methods.Count(m => m.MethodHandle == own.MethodHandle) != 1 || transpilers.Count(p => PatchGuard.IsOurs(p.owner)) != 1)
        {
            return false;
        }
        List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(_moveNext, transpilers);
        return sorted.Count > 0 && sorted[^1].MethodHandle == own.MethodHandle;
    }

    /// <summary>The preflight's note: the first turn's queries left as the game's calls by the transpiler (_firstTurnRefusal).</summary>
    private static void NoteFirstTurnCode()
    {
        if (_firstTurnRefusal is not { } why || _moveNext == null || Harmony.GetPatchInfo(_moveNext) is not { } info)
        {
            PatchSafety.ClearNote(Name, "first_turn_code");
            return;
        }
        PatchSafety.Note(Name, "first_turn_code", $"other mods' transpilers change the code feeding the first turn's card filters ({why}): they run without a scope",
            PatchSafety.ModNamesOf(info.Transpilers.Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => PatchSafety.PatchMethodOf(p)?.DeclaringType?.Assembly)));
    }

    /// <summary>
    /// Replaces the two <c>ToList</c> calls with <see cref="ToListInScope"/>, when the code feeding them is the game's. Runs last
    /// (<c>Priority.Last</c>), at each rebuild of the state machine: what it is handed holds every other mod's transpiler sorted
    /// before it (RitsuLib rewrites the hand size constants there), judged by <see cref="TranspilerJudge.FeedsUnchanged"/> (the
    /// queries, the pile and list they read, and every store of those, instruction for instruction). Otherwise the calls stay the
    /// game's (no scope). Never throws (it runs inside other mods' Patch calls).
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        try
        {
            MethodInfo toList = Reflect.Method(typeof(Enumerable), nameof(Enumerable.ToList)).MakeGenericMethod(typeof(CardModel));
            MethodInfo scoped = Reflect.Method(typeof(ExtraScopes), nameof(ToListInScope)).MakeGenericMethod(typeof(CardModel));
            List<CodeInstruction> calls = code.Where(c => c.Calls(toList)).ToList();
            if (calls.Count != 2)
            {
                Log.Error($"[{ModEntry.Id}] {Name}: expected 2 ToList calls in SetupPlayerTurn, found {calls.Count}: left vanilla");
                return code;
            }
            List<TranspilerJudge.Ins> handed = code.Select(TranspilerJudge.Ins.From).ToList();
            string? refusal = FirstTurnRefusal(_originalFirstTurn, handed, toList);
            bool foreign = !TranspilerJudge.SameInstructions(_originalFirstTurn, handed);
#if BENCH
            FirstTurnVerdict = !foreign ? "" : refusal == null ? "accepted" : "refused (" + refusal + ")";
#endif
            string logged = !foreign ? "" : refusal ?? "kept";
            if (foreign && logged != _firstTurnLogged)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: SetupPlayerTurn's first-turn card filters, other mods' transpilers judged by their IL: "
                    + (refusal == null ? "the code feeding them is the game's: kept" : $"refused ({refusal}): those filters run without a scope (normal game for that part)"));
            }
            _firstTurnLogged = logged;
            _firstTurnRefusal = refusal;
            if (refusal != null)
            {
                return code;
            }
            foreach (CodeInstruction call in calls)
            {
                call.opcode = OpCodes.Call;
                call.operand = scoped; // Same signature: IEnumerable<T> -> List<T>.
            }
            return code;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: the first-turn transpiler failed, SetupPlayerTurn left vanilla: {e.Message}");
            return code;
        }
    }

    /// <summary>
    /// Why the first turn's queries can't be scoped in <paramref name="result"/> (SetupPlayerTurn's state machine with other mods'
    /// transpilers applied), else null: the two <c>ToList</c> calls and everything feeding them as in <paramref name="original"/>.
    /// Also the test tooling's entry (the self-test).
    /// </summary>
    internal static string? FirstTurnRefusal(IReadOnlyList<TranspilerJudge.Ins> original, IReadOnlyList<TranspilerJudge.Ins> result, MethodInfo toList) =>
        original.Count == 0 ? "the game's code not read"
        : TranspilerJudge.FeedsUnchanged(original, result, ins => ins.Op == OpCodes.Call && ins.Operand is MethodInfo m && m == toList);

    /// <summary><c>source.ToList()</c>, with the lazy query evaluated inside a refresh scope.</summary>
    public static List<T> ToListInScope<T>(IEnumerable<T> source)
    {
        List<T> result;
#if BENCH
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        // The query itself is the game's: its exceptions reach the game as they would. Only the scope's are caught.
        int scope = FirstTurnOn && !FirstTurnPatched() ? Enter(display: false, Name) : NoScope;
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
        Log.Info($"[{ModEntry.Id}] {Name} first-turn filter ({(scope != NoScope ? "in a scope" : "without a scope")}): {result.Count} cards, optimized {optimizedMs:F0} ms, vanilla {vanillaMs:F0} ms, "
            + (vanilla.SequenceEqual(result) ? "OK: identical to vanilla" : $"MISMATCH: vanilla has {vanilla.Count}"));
#endif
        return result;
    }

    /// <summary>No scope opened (or its opening threw).</summary>
    private const int NoScope = int.MinValue;

    /// <summary>
    /// Opens a scope: the state <see cref="Exit"/> takes (the depth found plus one for a display scope, never 0; -1 "one
    /// level less" for a game-state one), or <see cref="NoScope"/> (this scope faulted) when its bookkeeping throws: the
    /// pass then runs without one. Both count the scope last: an exception leaves no exit owed.
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
        Type tracker = Reflect.TypeByName("BetterSpire2.DamageTracker") ?? throw new PatchSkippedException("BetterSpire2 has no DamageTracker");
        MethodInfo recalculate = Reflect.Method(tracker, "Recalculate", Type.EmptyTypes) ?? throw new PatchSkippedException("BetterSpire2 has no DamageTracker.Recalculate()");
        var methods = new List<MethodBase>();
        foreach ((string name, string fingerprint) in BetterSpireReadOnly)
        {
            MethodInfo? method;
            try
            {
                method = name == "Recalculate" ? recalculate : Reflect.Method(tracker, name);
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
        PatchSafety.Watch(Name, _betterSpireMethods); // What BetterSpirePrefix asks about (display only).
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
                // No tolerance: listener lists are replayed inside the scope, a patch could make the methods walk differently.
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

    /// <summary>
    /// Closes the scope the prefix opened. 0: the prefix never ran (another mod's prefix threw first; Harmony's default
    /// state), no scope to close: closing one would end the scopes this call runs nested in (a refresh dispatch).
    /// Void: Harmony rethrows BetterSpire2's own exception unchanged.
    /// </summary>
    private static void BetterSpireFinalizer(int __state)
    {
        if (__state != NoScope && __state != 0)
        {
            Exit(BetterSpireName, __state);
        }
    }
}
