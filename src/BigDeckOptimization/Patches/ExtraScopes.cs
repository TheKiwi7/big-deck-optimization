using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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
///     run time from what the calls are given (<see cref="WhereFirstFilter"/>), whatever other mods do to the state machine.
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
    /// <summary>First-turn filters run inside a scope where ListenerCache answers the walks (the effects check).</summary>
    public static long FirstTurnPasses { get; private set; }
    /// <summary>
    /// First-turn filters run inside a scope ListenerCache doesn't answer in (it is off or backs off): the game's walks, no effect.
    /// </summary>
    public static long NotServed { get; private set; }
    /// <summary>First-turn <c>Where</c> calls left as the game's: a source that isn't exactly a <c>List&lt;CardModel&gt;</c>.</summary>
    public static long RefusedSource { get; private set; }
    /// <summary>The same for a predicate that isn't that filter's game lambda (single target, of its own class or none).</summary>
    public static long RefusedPredicate { get; private set; }
    /// <summary>First-turn <c>Except</c> calls on a marked query whose second list isn't exactly a <c>List&lt;CardModel&gt;</c>.</summary>
    public static long RefusedExcept { get; private set; }
    /// <summary>Redirected <c>ToList</c> calls given anything but a marked query: the game's <c>ToList</c>, no scope.</summary>
    public static long PlainToLists { get; private set; }
    /// <summary>A marked query's <c>ToList</c> run without a scope: the part off or faulted, or a back-off (<see cref="FirstTurnPatched"/>).</summary>
    public static long BackedOff { get; private set; }
    /// <summary>A marked query's <c>ToList</c> whose scope failed to open (its bookkeeping threw: the part faulted).</summary>
    public static long ScopeFailed { get; private set; }

    /// <summary><c>bdperf firstturn status</c>.</summary>
    internal static string BenchStatus() =>
        $"FIRSTTURN scopes {FirstTurnPasses}, refused source {RefusedSource}, predicate {RefusedPredicate}, except {RefusedExcept}, "
        + $"plain ToList {PlainToLists}, backed off {BackedOff}, scope failed {ScopeFailed}, on {FirstTurnOn}, faulted {PatchSafety.IsFaulted(Name)}, not served {NotServed}";

    /// <summary>The game's two first-turn predicates (the bench builds delegates of them), empty before the part applied.</summary>
    internal static IReadOnlyList<MethodInfo> BenchGamePredicates => _gamePredicates;

    /// <summary>Whether <paramref name="query"/> is marked as a first-turn query of this part (the self-test's view of a decision).</summary>
    internal static bool BenchIsScoped(IEnumerable<CardModel>? query) => query != null && _marks.TryGetValue(query, out _);
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

    /// <summary>This part's stand-ins (static, non-generic, the game's signatures): one <c>Where</c> per filter, then Except, ToList.</summary>
    private static MethodInfo? _ownWhereFirst, _ownWhereSecond, _ownExcept, _ownToList;

    private static void ApplyFirstTurn(Harmony harmony)
    {
        MethodInfo setup = Reflect.Method(typeof(CombatManager), "SetupPlayerTurn") ?? throw new MissingMethodException("CombatManager.SetupPlayerTurn");
        MethodInfo moveNext = Reflect.AsyncMoveNext(setup) ?? throw new MissingMethodException("SetupPlayerTurn state machine");
        PatchSafety.RequireFingerprint(moveNext, SetupPlayerTurnFingerprint);
        _ownWhereFirst = Reflect.Method(typeof(ExtraScopes), nameof(WhereFirstFilter)) ?? throw new MissingMethodException(nameof(WhereFirstFilter));
        _ownWhereSecond = Reflect.Method(typeof(ExtraScopes), nameof(WhereSecondFilter)) ?? throw new MissingMethodException(nameof(WhereSecondFilter));
        _ownExcept = Reflect.Method(typeof(ExtraScopes), nameof(ExceptInScope)) ?? throw new MissingMethodException(nameof(ExceptInScope));
        _ownToList = Reflect.Method(typeof(ExtraScopes), nameof(ToListInScope)) ?? throw new MissingMethodException(nameof(ToListInScope));
        // The game's predicates: the lambda each Where call of the game's code (read before this mod patches it, Harmony's reader:
        // no transpiler runs) is given, from the ldftn before it, in the code's order (bottom-of-pile, then Innate). They also
        // tell the transpiler which calls are the first turn's filters.
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
        // Checked strictly (another mod's patch on one, hidden IL hooks included: no scope): the state machine's generated code
        // (the query lambdas), the closed LINQ methods as the game's IL names them, this part's stand-ins. Not the state machine
        // itself: what other mods do there runs outside the scope, and a query they change fails the stand-ins' checks. Display
        // only for Watch: the runtime check decides (FirstTurnPatched).
        _lambdas = MethodFingerprint.GeneratedCode(moveNext).Where(m => m != moveNext).Distinct().ToArray();
        _strict = _lambdas.Concat(linq.Distinct()).Concat(new MethodBase[] { _ownWhereFirst, _ownWhereSecond, _ownExcept, _ownToList }).ToArray();
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

    /// <summary>
    /// Another mod patches code that runs inside the scope: the lambdas, the closed LINQ methods, the stand-ins (strict). Generic
    /// LINQ code is shared between reference types (a patch on <c>Enumerable.ToList&lt;object&gt;</c> runs in
    /// <c>ToList&lt;CardModel&gt;</c> too, and Harmony lists it under the other instantiation): for a closed generic method, every
    /// instantiation of its definition Harmony patches counts too (<c>PatchSafety.IsPatchedByOthersWithInstances</c>, as
    /// RelicFlashMerge asks; a value-type instantiation too, which shares no code: conservative), and Harmony unable to list them
    /// counts as patched. Residual: the BCL's internals (the iterators, <c>List</c>, <c>HashSet</c>) aren't watched.
    /// </summary>
    private static bool FirstTurnPatched()
    {
        foreach (MethodBase code in _strict)
        {
            if (PatchSafety.IsPatchedByOthers(code)
                || (code is MethodInfo { IsGenericMethod: true, IsGenericMethodDefinition: false } closed
                    && PatchSafety.IsPatchedByOthersWithInstances(closed.GetGenericMethodDefinition(), Caller)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The call site this file's checks record under (its patch name, as <c>[CallerFilePath]</c> gives it).</summary>
    private const string Caller = Name + ".cs";

    /// <summary>Which first-turn filter (0 bottom-of-pile, 1 Innate) a lambda is, else -1.</summary>
    private static int FilterOf(object? lambda)
    {
        if (lambda is MethodInfo method)
        {
            for (int i = 0; i < _gamePredicates.Length; i++)
            {
                if (method.MethodHandle == _gamePredicates[i].MethodHandle)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Redirects the first turn's two filters to this part's stand-ins: each <c>Enumerable.Where&lt;CardModel&gt;</c> call whose
    /// last <c>ldftn</c> before it loads one of the game's two lambdas (<see cref="WhereFirstFilter"/>,
    /// <see cref="WhereSecondFilter"/>), then, up to the next <c>ldftn</c> or <c>Where</c>, the <c>Except&lt;CardModel&gt;</c> and
    /// the <c>ToList&lt;CardModel&gt;</c> that follow it (<see cref="ExceptInScope"/>, <see cref="ToListInScope"/>, the latter
    /// ending the filter). Other LINQ calls of the state machine (another mod's own query) stay as they are. Each stand-in returns
    /// the game's own result for any arguments, and a scope opens only when the query is provably the game's (decided at run time
    /// from the source, the predicate and the list). Runs last (<c>Priority.Last</c>) at each rebuild, whatever the switches (the
    /// stand-ins answer as the game when the part is off). Residual: a transpiler sorted after this mod's looking for those five
    /// game calls doesn't find them (as one looking for the two <c>ToList</c> calls didn't since the first version); one that
    /// drops or redirects a stand-in leaves the game's call or its own (no scope); code it puts around one runs outside the
    /// scope. Never throws (it runs inside other mods' Patch calls).
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        try
        {
            if (_ownWhereFirst == null || _ownWhereSecond == null || _ownExcept == null || _ownToList == null)
            {
                return code;
            }
            var redirects = new List<(CodeInstruction Call, MethodInfo To)>();
            int lastFtn = -1;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ins = code[i];
                if (ins.opcode == OpCodes.Ldftn)
                {
                    lastFtn = i;
                    continue;
                }
                if (ins.opcode != OpCodes.Call || KindOf(ins.operand) != Linq.Where)
                {
                    continue;
                }
                int filter = lastFtn < 0 ? -1 : FilterOf(code[lastFtn].operand);
                lastFtn = -1;
                if (filter < 0)
                {
                    continue;
                }
                redirects.Add((ins, filter == 0 ? _ownWhereFirst : _ownWhereSecond));
                for (int j = i + 1; j < code.Count; j++)
                {
                    CodeInstruction next = code[j];
                    Linq kind = next.opcode == OpCodes.Call ? KindOf(next.operand) : Linq.None;
                    if (next.opcode == OpCodes.Ldftn || kind == Linq.Where)
                    {
                        break;
                    }
                    if (kind == Linq.Except)
                    {
                        redirects.Add((next, _ownExcept));
                    }
                    else if (kind == Linq.ToList)
                    {
                        redirects.Add((next, _ownToList));
                        break;
                    }
                }
            }
            if (redirects.Count < 5)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {redirects.Count} of SetupPlayerTurn's 5 first-turn card filter calls found (other mods' transpilers): "
                    + "the others run as the game's, without a scope (normal game for that part)");
            }
            else if (redirects.Count > 5)
            {
                // Another mod's transpiler repeating the game's filters: each stand-in still returns the game's result, and a
                // scope opens only for a query proved to be the game's (from its source, predicate and list).
                Log.Info($"[{ModEntry.Id}] {Name}: {redirects.Count} first-turn card filter calls found in SetupPlayerTurn where the game has 5 "
                    + "(other mods' transpilers): each is redirected, a scope opens only for the game's own queries");
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
            // Nothing redirected: the operands are set only after the search ended (whatever it found, partial included).
            return code;
        }
    }

    /// <summary>Which first-turn filter a marked query belongs to.</summary>
    private sealed class Mark(int filter)
    {
        internal readonly int Filter = filter;
    }

    private static readonly Mark[] _filterMarks = { new(0), new(1) };

    /// <summary>
    /// The BCL query objects the stand-ins proved to be the game's (<see cref="WhereFirstFilter"/>, <see cref="ExceptInScope"/>):
    /// the objects themselves are what the game's code holds (no wrapper: another consumer keeps the BCL iterator's own paths),
    /// marked on the side; weak keys, so nothing is kept alive.
    /// </summary>
    private static readonly ConditionalWeakTable<object, Mark> _marks = new();

    /// <summary>The first turn's first filter (bottom of the draw pile): <see cref="Where"/>.</summary>
    public static IEnumerable<CardModel> WhereFirstFilter(IEnumerable<CardModel> source, Func<CardModel, bool> predicate) => Where(0, source, predicate);

    /// <summary>The first turn's second filter (Innate): <see cref="Where"/>.</summary>
    public static IEnumerable<CardModel> WhereSecondFilter(IEnumerable<CardModel> source, Func<CardModel, bool> predicate) => Where(1, source, predicate);

    /// <summary>
    /// <c>Enumerable.Where(source, predicate)</c> (its argument checks and result the game's, the same object), marked as filter
    /// <paramref name="filter"/>'s query when all hold: <paramref name="source"/> is exactly a <c>List&lt;CardModel&gt;</c> (its
    /// enumeration is the BCL's); the predicate is exactly a <c>Func&lt;CardModel, bool&gt;</c> with a single target (no combined
    /// delegate in the game's cache field), its method that filter's game lambda and its target none or of the lambda's own class.
    /// </summary>
    private static IEnumerable<CardModel> Where(int filter, IEnumerable<CardModel> source, Func<CardModel, bool> predicate)
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
                Refused(filter, "reads another collection than the game's draw pile list", () => source.GetType().Assembly);
                return query;
            }
            if (!IsGamePredicate(predicate, filter))
            {
#if BENCH
                RefusedPredicate++;
#endif
                Refused(filter, "is given another predicate than the game's", () => PatchSafety.DelegateAssembly(predicate));
                return query;
            }
            _marks.AddOrUpdate(query, _filterMarks[filter]);
        }
        catch (Exception e)
        {
            Disable(Name, e);
        }
        return query;
    }

    /// <summary>The filter's game lambda as the game's code makes it (its cached delegate): one target, the lambda's class or none.</summary>
    private static bool IsGamePredicate(Func<CardModel, bool> predicate, int filter)
    {
        if (predicate.GetType() != typeof(Func<CardModel, bool>) || !predicate.HasSingleTarget || filter >= _gamePredicates.Length)
        {
            return false;
        }
        MethodInfo game = _gamePredicates[filter];
        return predicate.Method.MethodHandle == game.MethodHandle && (predicate.Target == null || predicate.Target.GetType() == game.DeclaringType);
    }

    /// <summary>
    /// <c>Enumerable.Except(first, second)</c> (the game's call and object), marked as <paramref name="first"/>'s filter again when
    /// <paramref name="first"/> is marked and <paramref name="second"/> is exactly a <c>List&lt;CardModel&gt;</c>.
    /// </summary>
    public static IEnumerable<CardModel> ExceptInScope(IEnumerable<CardModel> first, IEnumerable<CardModel> second)
    {
        IEnumerable<CardModel> query = Enumerable.Except(first, second);
        try
        {
            if (_marks.TryGetValue(first, out Mark? mark))
            {
                if (second.GetType() == typeof(List<CardModel>))
                {
                    _marks.AddOrUpdate(query, mark);
                }
                else
                {
#if BENCH
                    RefusedExcept++;
#endif
                    Refused(mark.Filter, "leaves out another collection than the game's list", () => second.GetType().Assembly);
                }
            }
        }
        catch (Exception e)
        {
            Disable(Name, e);
        }
        return query;
    }

    /// <summary>The note key of a filter's refusal (one per filter: a later scoped pass of that filter clears it).</summary>
    private static string NoteKey(int filter) => filter == 0 ? "first_turn_bottom" : "first_turn_innate";

    /// <summary>
    /// A first-turn filter left as the game's by another mod's code: noted (bdopt why, the menu), with the mod when its code is
    /// what was given, until that filter is scoped again. Never throws.
    /// </summary>
    private static void Refused(int filter, string what, Func<Assembly?> from)
    {
        try
        {
            PatchSafety.Note(Name, NoteKey(filter), $"the first turn's {(filter == 0 ? "bottom-of-pile" : "Innate")} card filter {what}: that filter runs without a scope",
                PatchSafety.ModNamesOf(new[] { from() }));
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// <c>Enumerable.ToList(source)</c> (the game's call on the game's object); a marked query's run inside a refresh scope. What
    /// runs in the scope: the BCL's List and LINQ code, the game's two lambdas, and what they call: the cards' enchantments'
    /// <c>ShouldStartAtBottomOfDrawPile</c> (other mods' overrides: Limitless's MultiEnchantment, HonkaiFlameChasers' KevinEngrave),
    /// <c>CardModel.Keywords</c> (<c>GetKeywordsWithSources</c> and other mods' postfixes there) and <c>Hook.ModifyKeywordsInCombat</c>'s
    /// listeners (<c>TryModifyKeywordsInCombat</c>), and for the Innate filter the cards' <c>Equals</c>/<c>GetHashCode</c> (a mod's
    /// card may override them). Exactness there rests on ListenerCache's invalidators (a change of what a listener list depends on
    /// inside the scope is seen as anywhere else), as it did for this scope since its first version.
    /// </summary>
    public static List<CardModel> ToListInScope(IEnumerable<CardModel> source)
    {
        int scope = NoScope;
        Mark? mark = null;
#if BENCH
        bool served = false;
#endif
        if (source != null && _marks.TryGetValue(source, out mark))
        {
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
            // Right after entering: whether ListenerCache answers the walks in this scope (off, or its own back-off: not).
            served = scope != NoScope && ListenerCache.IsServing;
            if (served)
            {
                FirstTurnPasses++; // Only passes whose walks are really answered inside the scope prove the effect.
            }
            else if (scope != NoScope)
            {
                NotServed++;
            }
            else if (open)
            {
                ScopeFailed++;
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
            result = Enumerable.ToList(source!);
        }
        finally
        {
            if (scope != NoScope)
            {
                Exit(Name, scope);
            }
        }
        if (scope != NoScope && mark != null)
        {
            try
            {
                PatchSafety.ClearNote(Name, NoteKey(mark.Filter)); // That filter is scoped again.
            }
            catch
            {
                // Display only.
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
        List<CardModel> vanilla = ListenerCache.RunVanilla(() => Enumerable.ToList(source!));
        double vanillaMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Log.Info($"[{ModEntry.Id}] {Name} first-turn filter ({(served ? "in a scope" : scope != NoScope ? "in a scope not served" : "without a scope")}): {result.Count} cards, optimized {optimizedMs:F0} ms, vanilla {vanillaMs:F0} ms, "
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
