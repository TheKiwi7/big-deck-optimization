using System;
using System.Collections;
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
///     <c>Where(...).ToList()</c>: 6 s with 8000 cards. Both <c>ToList</c> calls run the same LINQ inside a scope, decided at
///     run time from what the calls are given (<see cref="WhereInScope"/>), whatever other mods do to the state machine.
///   - BetterSpire2 (optional): <c>DamageTracker.Recalculate</c>, its damage preview recomputed 4 times a second,
///     only calls <c>Hook.ModifyDamage</c> and updates its own labels: ~100 ms per run with 8000 cards.
/// Scopes follow <see cref="ListenerCache.RefreshEnabled"/> (the "combat refresh" setting). The scope bookkeeping runs
/// inside the game's code (the first-turn one inside an async state machine, where PatchGuard never sees an exception):
/// an exception from it turns that scope off (faulted) and the pass runs without a scope, as in the game.
/// </summary>
public static class ExtraScopes
{
    public const string Name = "ExtraScopes";

    /// <summary>v0.107.1 <c>CombatManager.SetupPlayerTurn</c> state machine, whose first-turn queries are redirected.</summary>
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
    /// <summary>First-turn <c>Where</c> calls left as the game's: a source that isn't exactly a <c>List&lt;CardModel&gt;</c>.</summary>
    public static long RefusedSource { get; private set; }
    /// <summary>The same for a predicate that isn't one of the game's two lambdas (single target, of its own class or none).</summary>
    public static long RefusedPredicate { get; private set; }
    /// <summary>First-turn <c>Except</c> calls on a scoped query whose second list isn't exactly a <c>List&lt;CardModel&gt;</c>.</summary>
    public static long RefusedExcept { get; private set; }
    /// <summary>Redirected <c>ToList</c> calls given anything but a scoped query: the game's <c>ToList</c>, no scope.</summary>
    public static long PlainToLists { get; private set; }
    /// <summary>A scoped query's <c>ToList</c> run without a scope: the part off or faulted, or a back-off (<see cref="FirstTurnPatched"/>).</summary>
    public static long BackedOff { get; private set; }

    /// <summary><c>bdperf firstturn status</c>.</summary>
    internal static string BenchStatus() =>
        $"FIRSTTURN scopes {FirstTurnPasses}, refused source {RefusedSource}, predicate {RefusedPredicate}, except {RefusedExcept}, "
        + $"plain ToList {PlainToLists}, backed off {BackedOff}, on {FirstTurnOn}, faulted {PatchSafety.IsFaulted(Name)}";

    /// <summary>The game's two first-turn predicates (the bench builds delegates of them), empty before the part applied.</summary>
    internal static IReadOnlyList<MethodInfo> BenchGamePredicates => _gamePredicates;

    /// <summary>Whether <paramref name="query"/> is a scoped query of this part (the self-test's view of a decision).</summary>
    internal static bool BenchIsScoped(IEnumerable<CardModel>? query) => query is FirstTurnQuery;
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

    // ---- First turn ----

    /// <summary>The game's LINQ calls the transpiler redirects, closed over CardModel.</summary>
    private enum Linq
    {
        None,
        Where,
        Except,
        ToList,
    }

    /// <summary>
    /// Which of the redirected calls a call operand is: <c>Enumerable.Where/Except/ToList</c> closed over CardModel with the
    /// game's overload (by its closed parameter and return types: no lookup by name of a generic overload).
    /// </summary>
    private static Linq KindOf(object? operand)
    {
        if (operand is not MethodInfo { IsGenericMethod: true } method || method.DeclaringType != typeof(Enumerable))
        {
            return Linq.None;
        }
        ParameterInfo[] p = method.GetParameters();
        return method.Name switch
        {
            nameof(Enumerable.Where) when p.Length == 2 && p[0].ParameterType == typeof(IEnumerable<CardModel>)
                && p[1].ParameterType == typeof(Func<CardModel, bool>) && method.ReturnType == typeof(IEnumerable<CardModel>) => Linq.Where,
            nameof(Enumerable.Except) when p.Length == 2 && p[0].ParameterType == typeof(IEnumerable<CardModel>)
                && p[1].ParameterType == typeof(IEnumerable<CardModel>) && method.ReturnType == typeof(IEnumerable<CardModel>) => Linq.Except,
            nameof(Enumerable.ToList) when p.Length == 1 && p[0].ParameterType == typeof(IEnumerable<CardModel>)
                && method.ReturnType == typeof(List<CardModel>) => Linq.ToList,
            _ => Linq.None,
        };
    }

    /// <summary>This part's stand-ins for them, same signatures (static, non-generic).</summary>
    private static MethodInfo? _ownWhere, _ownExcept, _ownToList;

    private static void ApplyFirstTurn(Harmony harmony)
    {
        MethodInfo setup = Reflect.Method(typeof(CombatManager), "SetupPlayerTurn") ?? throw new MissingMethodException("CombatManager.SetupPlayerTurn");
        MethodInfo moveNext = Reflect.AsyncMoveNext(setup) ?? throw new MissingMethodException("SetupPlayerTurn state machine");
        PatchSafety.RequireFingerprint(moveNext, SetupPlayerTurnFingerprint);
        _ownWhere = Reflect.Method(typeof(ExtraScopes), nameof(WhereInScope)) ?? throw new MissingMethodException(nameof(WhereInScope));
        _ownExcept = Reflect.Method(typeof(ExtraScopes), nameof(ExceptInScope)) ?? throw new MissingMethodException(nameof(ExceptInScope));
        _ownToList = Reflect.Method(typeof(ExtraScopes), nameof(ToListInScope)) ?? throw new MissingMethodException(nameof(ToListInScope));
        // The game's predicates: the lambda each Where call of the game's code (read before this mod patches it, Harmony's reader:
        // no transpiler runs) is given, from the ldftn before it. A scope opens only for a query over one of them (WhereInScope).
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(moveNext);
        var predicates = new List<MethodInfo>();
        var linq = new List<MethodBase>();
        int lastFtn = -1, wheres = 0, excepts = 0, toLists = 0;
        for (int i = 0; i < original.Count; i++)
        {
            CodeInstruction ins = original[i];
            if (ins.opcode == OpCodes.Ldftn)
            {
                lastFtn = i;
                continue;
            }
            Linq kind = ins.opcode == OpCodes.Call ? KindOf(ins.operand) : Linq.None;
            if (kind == Linq.None)
            {
                continue;
            }
            linq.Add((MethodBase)ins.operand);
            if (kind == Linq.Where)
            {
                wheres++;
                if (lastFtn < 0 || original[lastFtn].operand is not MethodInfo lambda || lambda.IsStatic || lambda.DeclaringType == null)
                {
                    throw new PatchSkippedException("SetupPlayerTurn's first-turn Where call without the game's lambda before it");
                }
                predicates.Add(lambda);
                lastFtn = -1;
            }
            excepts += kind == Linq.Except ? 1 : 0;
            toLists += kind == Linq.ToList ? 1 : 0;
        }
        if (wheres != 2 || excepts != 1 || toLists != 2 || predicates.Select(p => p.MethodHandle).Distinct().Count() != 2)
        {
            throw new PatchSkippedException($"SetupPlayerTurn's first-turn queries not as expected (Where {wheres}, Except {excepts}, ToList {toLists})");
        }
        _gamePredicates = predicates.ToArray();
        // What runs inside the scope besides the BCL's List and LINQ code and the cards' Equals: the query lambdas (strict: another
        // mod's patch on one may make the pass write, hidden IL hooks included), the closed LINQ methods and this part's stand-ins.
        // Nothing on the state machine itself: what other mods do there runs outside the scope, and a query they change fails the
        // checks of the stand-ins (the game's calls then). Display only for Watch: the runtime check decides (FirstTurnPatched).
        _lambdas = MethodFingerprint.GeneratedCode(moveNext).Where(m => m != moveNext).Distinct().ToArray();
        _strict = _lambdas.Concat(linq.Distinct()).Concat(new MethodBase[] { _ownWhere, _ownExcept, _ownToList }).ToArray();
        foreach (MethodBase code in _strict)
        {
            PatchSafety.Watch(Name, code);
        }
        PatchGuard.Patch(harmony, moveNext, transpiler: new HarmonyMethod(typeof(ExtraScopes), nameof(FirstTurnTranspiler)) { priority = Priority.Last });
    }

    /// <summary>The lambdas of SetupPlayerTurn's state machine, and everything <see cref="FirstTurnPatched"/> checks.</summary>
    private static MethodBase[] _lambdas = Array.Empty<MethodBase>();
    private static MethodBase[] _strict = Array.Empty<MethodBase>();

    /// <summary>The game's two first-turn predicates (bottom-of-pile, Innate), from the unpatched code.</summary>
    private static MethodInfo[] _gamePredicates = Array.Empty<MethodInfo>();

    /// <summary>Another mod patches code that runs inside the scope: the lambdas, the closed LINQ methods, the stand-ins (strict).</summary>
    private static bool FirstTurnPatched()
    {
        foreach (MethodBase code in _strict)
        {
            if (PatchSafety.IsPatchedByOthers(code))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Replaces every <c>Enumerable.Where/Except/ToList&lt;CardModel&gt;</c> call of the state machine with this part's stand-ins
    /// (<see cref="WhereInScope"/>, <see cref="ExceptInScope"/>, <see cref="ToListInScope"/>), whatever code feeds them: each one
    /// returns the game's own result for any arguments, and a scope opens only when the query is provably the game's (decided at
    /// run time from the source, the predicate and the list). Runs last (<c>Priority.Last</c>) at each rebuild, so it sees the
    /// calls other mods' transpilers sorted before it leave; one sorted after it may drop or redirect a stand-in (the game's
    /// call, or its own code, then: no scope) or put code around it (outside the scope). Never throws (it runs inside other
    /// mods' Patch calls).
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        try
        {
            if (_ownWhere == null || _ownExcept == null || _ownToList == null)
            {
                return code;
            }
            var redirects = new List<(CodeInstruction Call, MethodInfo To)>();
            foreach (CodeInstruction ins in code)
            {
                MethodInfo? to = ins.opcode != OpCodes.Call ? null : KindOf(ins.operand) switch
                {
                    Linq.Where => _ownWhere,
                    Linq.Except => _ownExcept,
                    Linq.ToList => _ownToList,
                    _ => null,
                };
                if (to != null)
                {
                    redirects.Add((ins, to));
                }
            }
            if (redirects.Count == 0)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: no first-turn card filter call left in SetupPlayerTurn (other mods' transpilers): those filters run without a scope (normal game for that part)");
                return code;
            }
            foreach ((CodeInstruction call, MethodInfo to) in redirects)
            {
                call.operand = to; // Same signature, a static call either way.
            }
            return code;
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: the first-turn transpiler failed, SetupPlayerTurn left vanilla: {e.Message}");
            return code; // Nothing redirected: the operands are set only once every call was found.
        }
    }

    /// <summary>
    /// A first-turn query of the game's shape, made by <see cref="WhereInScope"/> or <see cref="ExceptInScope"/> only: the BCL's
    /// query it holds (<see cref="Inner"/>) is what the game's code would hold. Anything else enumerating it gets that query's
    /// enumerator: the game's laziness and results.
    /// </summary>
    private sealed class FirstTurnQuery : IEnumerable<CardModel>
    {
        internal readonly IEnumerable<CardModel> Inner;

        internal FirstTurnQuery(IEnumerable<CardModel> inner) => Inner = inner;

        public IEnumerator<CardModel> GetEnumerator() => Inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => Inner.GetEnumerator();
    }

    /// <summary>
    /// <c>Enumerable.Where(source, predicate)</c> (its argument checks and result the game's), marked as a first-turn query when
    /// all hold: <paramref name="source"/> is exactly a <c>List&lt;CardModel&gt;</c> (its enumeration is the BCL's); the predicate
    /// is exactly a <c>Func&lt;CardModel, bool&gt;</c> with a single target (no combined delegate in the game's cache field), its
    /// method one of the game's two lambdas and its target none or of that lambda's own class. Otherwise the plain result.
    /// </summary>
    public static IEnumerable<CardModel> WhereInScope(IEnumerable<CardModel> source, Func<CardModel, bool> predicate)
    {
        IEnumerable<CardModel> query = Enumerable.Where(source, predicate);
        try
        {
            if (!FirstTurnOn)
            {
                return query;
            }
            if (source.GetType() != typeof(List<CardModel>))
            {
#if BENCH
                RefusedSource++;
#endif
                Refused("first_turn_source", "a first-turn card filter reads another collection than the game's draw pile list", () => source.GetType().Assembly);
                return query;
            }
            if (!IsGamePredicate(predicate))
            {
#if BENCH
                RefusedPredicate++;
#endif
                Refused("first_turn_predicate", "a first-turn card filter is given another predicate than the game's", () => PatchSafety.DelegateAssembly(predicate));
                return query;
            }
            return new FirstTurnQuery(query);
        }
        catch (Exception e)
        {
            Disable(Name, e);
            return query;
        }
    }

    /// <summary>The game's lambda as the game's code makes it (its cached delegate): one target, the lambda's class or none.</summary>
    private static bool IsGamePredicate(Func<CardModel, bool> predicate)
    {
        if (predicate.GetType() != typeof(Func<CardModel, bool>) || !predicate.HasSingleTarget)
        {
            return false;
        }
        MethodInfo method = predicate.Method;
        foreach (MethodInfo game in _gamePredicates)
        {
            if (method.MethodHandle == game.MethodHandle)
            {
                return predicate.Target == null || predicate.Target.GetType() == game.DeclaringType;
            }
        }
        return false;
    }

    /// <summary>
    /// <c>Enumerable.Except(first, second)</c> over the BCL query a first-turn query holds (what the game's code would pass), marked
    /// as a first-turn query again when <paramref name="first"/> is one and <paramref name="second"/> is exactly a
    /// <c>List&lt;CardModel&gt;</c>; otherwise the plain result.
    /// </summary>
    public static IEnumerable<CardModel> ExceptInScope(IEnumerable<CardModel> first, IEnumerable<CardModel> second)
    {
        if (first is not FirstTurnQuery scoped)
        {
            return Enumerable.Except(first, second);
        }
        IEnumerable<CardModel> query = Enumerable.Except(scoped.Inner, second);
        try
        {
            if (second.GetType() == typeof(List<CardModel>))
            {
                return new FirstTurnQuery(query);
            }
#if BENCH
            RefusedExcept++;
#endif
            Refused("first_turn_except", "the first turn's Innate filter leaves out another collection than the game's list", () => second.GetType().Assembly);
            return query;
        }
        catch (Exception e)
        {
            Disable(Name, e);
            return query;
        }
    }

    /// <summary>
    /// A first-turn query left as the game's by another mod's code: noted (bdopt why, the menu), with the mod when its code is
    /// what was given. Never throws.
    /// </summary>
    private static void Refused(string key, string what, Func<Assembly?> from)
    {
        try
        {
            PatchSafety.Note(Name, key, what + ": that filter runs without a scope", PatchSafety.ModNamesOf(new[] { from() }));
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// <c>source.ToList()</c>; a first-turn query's evaluated inside a refresh scope, as <c>Enumerable.ToList</c> of the BCL
    /// query it holds: the game's own call on the game's own query. Inside the scope only the BCL's List and LINQ code, the
    /// game's two lambdas and the cards' Equals run.
    /// </summary>
    public static List<CardModel> ToListInScope(IEnumerable<CardModel> source)
    {
        IEnumerable<CardModel> query = source;
        int scope = NoScope;
        if (source is FirstTurnQuery scoped)
        {
            query = scoped.Inner;
            // The query itself is the game's: its exceptions reach the game as they would. Only the scope's are caught.
            bool open;
            try
            {
                open = FirstTurnOn && !FirstTurnPatched();
            }
            catch (Exception e)
            {
                Disable(Name, e);
                open = false;
            }
            scope = open ? Enter(display: false, Name) : NoScope;
#if BENCH
            if (scope != NoScope)
            {
                FirstTurnPasses++; // Only passes really run inside a scope prove the effect.
            }
            else
            {
                BackedOff++;
            }
#endif
        }
#if BENCH
        else
        {
            PlainToLists++;
        }
#endif
        List<CardModel> result;
#if BENCH
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        try
        {
            result = Enumerable.ToList(query);
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
        List<CardModel> vanilla = ListenerCache.RunVanilla(() => Enumerable.ToList(query));
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
#if BENCH
        BetterSpireScopes++;
#endif
    }

#if BENCH
    /// <summary>BetterSpire2 scopes opened (test builds: effect <c>ExtraScopes.BetterSpire2</c>).</summary>
    public static long BetterSpireScopes;
#endif

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
