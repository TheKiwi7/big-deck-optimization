using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic transpilers of "another mod" on the <c>Hook</c> methods HookSkip filters, for <see cref="TranspilerJudge"/> ("Foreign
/// transpilers judged by IL" in DESIGN), installed through <c>foreignpatch &lt;shape&gt; &lt;Hook method&gt;</c> (on an async hook's
/// state machine, where its code is):
/// <list type="bullet">
/// <item>Accepted: <c>il-gated-observer squeeze</c> (Minty Spire 2's insertion point, before each <c>InvokeExecutionFinished</c>:
/// <c>dup</c> of the current listener and a call gated on the card Squeeze, which overrides no hook, counting its calls: the same
/// with this mod on or off only if Squeeze keeps its iterations), <c>il-prewalk-observer</c> (before each walk call: a constant and a
/// call counting it, outside every iteration).</item>
/// <item>Refused (controls, each counting nothing different on or off because the hook stays vanilla): <c>il-ungated-observer</c>
/// (the same point, a call counting every listener), <c>il-store-local</c> (<c>ldloc.0; stloc.0</c> before the walk),
/// <c>il-game-call</c> (a call into the game before the walk, its result popped), <c>il-branch</c> (a jump to the walk call).</item>
/// </list>
/// <c>foreignpatch iljudge selftest</c> runs the judge on the game's own code of three hooks with each shape and its near misses
/// built as instruction lists (nothing patched): a reordered pair, a changed branch, a handler mark removed, an observer at the
/// state machine's entry, an ungated observer in the iteration, the listener read after its loop, a gated method in a class with a
/// static constructor, Minty's own shape (a <c>GetCurrentMethod</c> call next to the gated one); each verdict must be the expected
/// one. ExtraScopes' first turn, on <c>CombatManager:SetupPlayerTurn</c> (no IL judge there: its stand-ins decide at run time from
/// what they are given): <c>il-firstturn-outside</c> code inserted at the state machine's entry (the filters keep their scope);
/// <c>il-firstturn-predicate</c> the Innate filter's predicate replaced by an equivalent one of this bench (that filter without a
/// scope, the same cards); <c>il-firstturn-source</c> the bottom-of-pile filter reading an array copy of the draw pile (that filter
/// without a scope, the same cards); <c>il-firstturn-except</c> the Innate filter's excluded list as an array copy (refused except);
/// <c>il-firstturn-consumer</c> another mod's code using the bottom-of-pile <c>Where</c> result (a STATE line: the BCL's object);
/// <c>il-firstturn-undo</c> (priority 0: after this mod's transpiler) the <c>Where</c> stand-ins put back to the game's call (no
/// scope); <c>il-firstturn-lambda</c> and <c>il-firstturn-standin</c> a no-op postfix on the Innate lambda or on ExtraScopes'
/// <c>ToListInScope</c> (strict: a back-off).
/// <c>foreignpatch iljudge sweep</c>: the prewalk observer and the gated observer on every classified hook of this game (how many the
/// analysis follows).
/// The identity bridge (the owner-approved premise of 2026-11-17, Downfall's and Watcher's <c>ModifyDamageInternal</c> shape):
/// <c>il-damage-bridge squeeze</c> inserts Downfall's run right after the game's <c>stloc</c> of each listener's additive result,
/// calling a bridge gated on Squeeze that adds 1 for it (counted) and gives the game's value back otherwise (accepted: Squeeze keeps
/// its iterations, damage +1 per Squeeze listener on both sides: the checksums see a lost one); <c>il-damage-bridge-callfirst</c>
/// the same with a call before the gate (refused: the hook stays vanilla). The self-test adds the exact insertion (once, and twice
/// for two mods' bridges in a row) and its near misses: another argument given back, another local stored, the run placed before
/// the game's store, a local the game didn't read just before, a call before the gate, a static constructor, the listener twice.
/// </summary>
public static class IlJudgeShapes
{
    public static readonly string[] Shapes = { "il-gated-observer", "il-prewalk-observer", "il-ungated-observer", "il-store-local", "il-game-call", "il-branch", "il-firstturn-outside",
        "il-firstturn-predicate", "il-firstturn-source", "il-firstturn-except", "il-firstturn-consumer", "il-firstturn-undo", "il-firstturn-lambda",
        "il-firstturn-standin", "il-damage-bridge", "il-damage-bridge-callfirst" };

    private static readonly ConcurrentDictionary<string, long> _counts = new();

    internal static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    /// <summary>
    /// The counts as STATE lines (compared line for line between the on and off sides). The damage bridges' calls are compared
    /// inside the game's damage actions only (<see cref="DamageActions"/>): previews recompute damage at the display's or the clock's
    /// pace (proof run 2: the game's card, intent and poison previews, 1-2% more or fewer calls with the mod on; proof run 3: another
    /// mod's preview such as BetterSpire2's DamageTracker, 4 times a second, counted outside the game's previews), so their totals
    /// are FOREIGN display lines, not compared.
    /// </summary>
    public static IEnumerable<string> ReportLines(string label) =>
        _counts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"STATE foreign {label} {e.Key} {e.Value}")
            .Concat(Bridges.SqueezeCalls > 0 ? new[]
            {
                $"STATE foreign {label} il-damage-bridge squeeze calls in damage actions {Bridges.SqueezeInActions}",
                $"FOREIGN display {label} il-damage-bridge squeeze calls {Bridges.SqueezeCalls}",
            } : Array.Empty<string>())
            .Concat(Bridges.EveryCall > 0 ? new[]
            {
                $"STATE foreign {label} il-damage-bridge-callfirst calls in damage actions {Bridges.EveryInActions}",
                $"FOREIGN display {label} il-damage-bridge-callfirst every call {Bridges.EveryCall}",
            } : Array.Empty<string>());

    /// <summary>
    /// The game's damage actions (the only game methods calling <c>Hook.ModifyDamage</c> for real damage, on v0.107.1 and v0.113.1
    /// alike: every async <c>CreatureCmd.Damage</c> overload and Thrash's play; the others are previews): a counting scope of the bench
    /// (<c>bdtest.foreign.damage-actions</c>, a prefix and a finalizer on each state machine's MoveNext, where the synchronous
    /// <c>Hook.ModifyDamage</c> call runs; installed with the damage bridges, on both sides alike). Any preview, the game's or another
    /// mod's (BetterSpire2's DamageTracker), stays outside it.
    /// </summary>
    private static readonly (string Type, string Method)[] DamageActions =
    {
        ("MegaCrit.Sts2.Core.Commands.CreatureCmd", "Damage"),
        ("MegaCrit.Sts2.Core.Models.Cards.Thrash", "OnPlay"),
    };

    private static bool _actionsScoped;

    private static void ScopeDamageActions()
    {
        if (_actionsScoped)
        {
            return;
        }
        var harmony = new Harmony("bdtest.foreign.damage-actions");
        int scoped = 0;
        foreach ((string type, string method) in DamageActions)
        {
            Type declaring = typeof(AbstractModel).Assembly.GetType(type, throwOnError: true)!;
            foreach (MethodInfo candidate in AccessTools.GetDeclaredMethods(declaring).Where(m => m.Name == method))
            {
                if (candidate.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() == null)
                {
                    continue; // A synchronous overload forwards to an async one.
                }
                harmony.Patch(AccessTools.AsyncMoveNext(candidate), prefix: new HarmonyMethod(typeof(IlJudgeShapes), nameof(ActionEnter)),
                    finalizer: new HarmonyMethod(typeof(IlJudgeShapes), nameof(ActionExit)));
                scoped++;
            }
        }
        if (scoped < 2)
        {
            throw new MissingMethodException($"damage actions: {scoped} async state machines found (CreatureCmd.Damage, Thrash.OnPlay)");
        }
        _actionsScoped = true;
    }

    private static void ActionEnter() => Bridges.ActionDepth++;

    private static void ActionExit() => Bridges.ActionDepth--;

    /// <summary>
    /// The game's damage previews (every other method of the game calling <c>Hook.ModifyDamage</c>): a second scope
    /// (<c>bdtest.foreign.damage-previews</c>), so a preview the game refreshes synchronously inside an action (a card's label on a
    /// change) isn't counted as the action's either.
    /// </summary>
    private static readonly (string Type, string Method)[] DamagePreviews =
    {
        ("MegaCrit.Sts2.Core.Localization.DynamicVars.DamageVar", "UpdateCardPreview"),
        ("MegaCrit.Sts2.Core.Localization.DynamicVars.CalculatedDamageVar", "UpdateCardPreview"),
        ("MegaCrit.Sts2.Core.Localization.DynamicVars.OstyDamageVar", "UpdateCardPreview"),
        ("MegaCrit.Sts2.Core.Models.Powers.PoisonPower", "CalculateTotalDamageNextTurn"),
        ("MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent", "GetSingleDamage"),
    };

    private static bool _previewsScoped;

    private static void ScopeDamagePreviews()
    {
        if (_previewsScoped)
        {
            return;
        }
        var harmony = new Harmony("bdtest.foreign.damage-previews");
        foreach ((string type, string method) in DamagePreviews)
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(AbstractModel).Assembly.GetType(type, throwOnError: true), method)
                ?? throw new MissingMethodException(type, method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(IlJudgeShapes), nameof(PreviewEnter)), finalizer: new HarmonyMethod(typeof(IlJudgeShapes), nameof(PreviewExit)));
        }
        _previewsScoped = true;
    }

    private static void PreviewEnter() => Bridges.PreviewDepth++;

    private static void PreviewExit() => Bridges.PreviewDepth--;

    /// <summary>The method a shape patches: an async hook's state machine, else the hook itself.</summary>
    public static MethodBase Body(MethodBase hook) =>
        hook.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null ? AccessTools.AsyncMoveNext(hook) : hook;

    /// <summary>Installs a shape on the hook's code; returns the method patched.</summary>
    public static MethodBase Install(string shape, string gate, MethodBase hook, Harmony harmony, int priority)
    {
        if (shape.StartsWith("il-firstturn-", StringComparison.Ordinal))
        {
            if (hook.DeclaringType != typeof(CombatManager) || hook.Name != "SetupPlayerTurn")
            {
                throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Combat.CombatManager:SetupPlayerTurn");
            }
            MethodBase setup = FirstTurnBody();
            // Patches on what runs inside the scope (strict: a back-off), not on the state machine.
            if (shape is "il-firstturn-lambda" or "il-firstturn-standin")
            {
                MethodBase target = shape == "il-firstturn-lambda"
                    ? (ExtraScopes.BenchGamePredicates.Count == 2 ? ExtraScopes.BenchGamePredicates[1] : throw new InvalidOperationException(shape + ": ExtraScopes' first turn not applied"))
                    : AccessTools.Method(typeof(ExtraScopes), nameof(ExtraScopes.ToListInScope));
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(IlJudgeShapes), nameof(FirstTurnNoOpPostfix)) { priority = priority });
                return target;
            }
            string firstTurn = shape switch
            {
                "il-firstturn-outside" => nameof(FirstTurnOutsideTranspiler),
                "il-firstturn-predicate" => nameof(FirstTurnPredicateTranspiler),
                "il-firstturn-source" => nameof(FirstTurnSourceTranspiler),
                "il-firstturn-except" => nameof(FirstTurnExceptTranspiler),
                "il-firstturn-consumer" => nameof(FirstTurnConsumerTranspiler),
                "il-firstturn-undo" => nameof(FirstTurnUndoTranspiler),
                _ => throw new ArgumentException("unknown il shape " + shape),
            };
            harmony.Patch(setup, transpiler: new HarmonyMethod(typeof(IlJudgeShapes), firstTurn) { priority = priority });
            return setup;
        }
        if (hook.DeclaringType != typeof(Hook))
        {
            throw new ArgumentException(shape + " goes on a MegaCrit.Sts2.Core.Hooks.Hook method");
        }
        if (shape is "il-gated-observer" or "il-damage-bridge" or "il-damage-bridge-callfirst" && gate != "squeeze")
        {
            throw new ArgumentException(shape + " gate: squeeze");
        }
        if (shape is "il-damage-bridge" or "il-damage-bridge-callfirst" && hook.Name != "ModifyDamageInternal")
        {
            throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Hooks.Hook:ModifyDamageInternal");
        }
        _counts.TryAdd("installed " + shape, 0); // The class initialized here, not at a first gated call.
        if (shape is "il-damage-bridge" or "il-damage-bridge-callfirst")
        {
            ScopeDamageActions();
            ScopeDamagePreviews();
        }
        MethodBase body = Body(hook);
        string transpiler = shape switch
        {
            "il-gated-observer" => nameof(GatedObserverTranspiler),
            "il-prewalk-observer" => nameof(PreWalkTranspiler),
            "il-ungated-observer" => nameof(UngatedObserverTranspiler),
            "il-store-local" => nameof(StoreLocalTranspiler),
            "il-game-call" => nameof(GameCallTranspiler),
            "il-branch" => nameof(BranchTranspiler),
            "il-damage-bridge" => nameof(DamageBridgeTranspiler),
            "il-damage-bridge-callfirst" => nameof(DamageBridgeCallFirstTranspiler),
            _ => throw new ArgumentException("unknown il shape " + shape),
        };
        harmony.Patch(body, transpiler: new HarmonyMethod(typeof(IlJudgeShapes), transpiler) { priority = priority });
        return body;
    }

    /// <summary>The judge's verdict on the method (after an install).</summary>
    public static IEnumerable<string> Verdict(MethodBase body)
    {
        if (body.DeclaringType?.DeclaringType == typeof(CombatManager) || body.DeclaringType == typeof(ExtraScopes))
        {
            yield return "FOREIGN iljudge SetupPlayerTurn: not judged (ExtraScopes decides each first-turn filter at run time: bdperf firstturn status)";
            yield break;
        }
        string hook = HookName(body);
        TranspilerJudge.Verdict? verdict = TranspilerJudge.Judge(body, HookSkip.OwnTranspiler, IsWalk, hook, Array.Empty<MethodInfo>());
        yield return $"FOREIGN iljudge Hook.{hook}: " + (verdict == null ? "no other mod's transpiler"
            : verdict.Accepted ? $"accepted ({verdict.Runs} insertion(s){(verdict.Gates.Length > 0 ? ", gates " + string.Join("|", verdict.Gates.Select(g => g.Name)) : "")})"
            : $"refused ({verdict.Why})");
    }

    /// <summary>The listener walk calls: HookSkip's own test once it applied, else the walk methods by name (offline runs).</summary>
    internal static bool IsWalk(MethodInfo method) =>
        HookSkip.Applied ? HookSkip.IsWalkCall(method)
        : method.Module.Assembly == typeof(AbstractModel).Assembly && method.Name is "IterateHookListeners" or "IterateCombatHookListeners";

    private static readonly MethodInfo _executionFinished = AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.InvokeExecutionFinished));

    private static bool IsExecutionFinished(CodeInstruction code) => code.operand is MethodInfo m && m == _executionFinished;

    private static bool IsWalkCall(CodeInstruction code) => (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo m && IsWalk(m);

    // ---- The transpilers (each one throws when its insertion point is missing: the install line fails) ----

    private static IEnumerable<CodeInstruction> GatedObserverTranspiler(IEnumerable<CodeInstruction> instructions) =>
        InsertBefore(instructions, IsExecutionFinished, _ => new[] { new CodeInstruction(OpCodes.Dup), Call(typeof(Gated), nameof(Gated.Squeeze)) });

    /// <summary>
    /// Code inserted at SetupPlayerTurn's state machine's entry, outside the first turn's queries (a value loaded and popped: nothing
    /// counted, the number of MoveNext calls differing with combat start waits skipped).
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnOutsideTranspiler(IEnumerable<CodeInstruction> instructions) =>
        Insert(instructions.ToList(), 0, new CodeInstruction(OpCodes.Ldstr, "SetupPlayerTurn"), new CodeInstruction(OpCodes.Pop))!;

    /// <summary>The state machine's calls of <c>Enumerable.Where&lt;CardModel&gt;</c> (the game's, before ExtraScopes' transpiler runs).</summary>
    private static List<int> FirstTurnWheres(List<CodeInstruction> code)
    {
        List<int> wheres = Enumerable.Range(0, code.Count).Where(i => code[i].opcode == OpCodes.Call && code[i].operand is MethodInfo
        {
            Name: nameof(Enumerable.Where), IsGenericMethod: true,
        } m && m.DeclaringType == typeof(Enumerable) && m.GetGenericArguments()[0] == typeof(CardModel)).ToList();
        return wheres.Count == 2 ? wheres : throw new InvalidOperationException($"SetupPlayerTurn: {wheres.Count} Where<CardModel> calls found, 2 expected");
    }

    /// <summary>
    /// The Innate filter's predicate (the second <c>Where</c>'s) dropped and this bench's equivalent one given instead: another
    /// predicate than the game's lambda (ExtraScopes: that filter without a scope), the same cards.
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnPredicateTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int second = FirstTurnWheres(code)[1];
        // The Where call carries the label the game's cached-delegate branch jumps to: moved to the inserted run, which both paths take.
        var pop = new CodeInstruction(OpCodes.Pop);
        code[second].MoveLabelsTo(pop);
        return Insert(code, second, pop, Call(typeof(FirstTurnStandIns), nameof(FirstTurnStandIns.InnatePredicate)))!;
    }

    /// <summary>
    /// The bottom-of-pile filter (the first <c>Where</c>) reading an array copy of the draw pile (right after the last
    /// <c>CardPile.Cards</c> read before it): not the game's list (ExtraScopes: that filter without a scope), the same cards.
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnSourceTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int first = FirstTurnWheres(code)[0];
        int cards = code.FindLastIndex(first, c => c.operand is MethodInfo { Name: "get_Cards" } m && m.DeclaringType == typeof(CardPile));
        if (cards < 0)
        {
            throw new InvalidOperationException("SetupPlayerTurn: no CardPile.Cards read before the first Where");
        }
        if (code[cards + 1].labels.Count > 0)
        {
            throw new InvalidOperationException("SetupPlayerTurn: a jump target right after the CardPile.Cards read before the first Where");
        }
        return Insert(code, cards + 1, Call(typeof(FirstTurnStandIns), nameof(FirstTurnStandIns.ArrayCopy)))!;
    }

    /// <summary>
    /// The Innate filter's excluded list (the <c>Except</c> call's second argument) given as an array copy: not the game's list
    /// (ExtraScopes: that filter without a scope, refused except), the same cards.
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnExceptTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        List<int> excepts = Enumerable.Range(0, code.Count).Where(i => code[i].opcode == OpCodes.Call && code[i].operand is MethodInfo
        {
            Name: nameof(Enumerable.Except), IsGenericMethod: true,
        } m && m.DeclaringType == typeof(Enumerable) && m.GetGenericArguments()[0] == typeof(CardModel)).ToList();
        if (excepts.Count != 1)
        {
            throw new InvalidOperationException($"SetupPlayerTurn: {excepts.Count} Except<CardModel> calls found, 1 expected");
        }
        var call = Call(typeof(FirstTurnStandIns), nameof(FirstTurnStandIns.ArrayOf));
        code[excepts[0]].MoveLabelsTo(call);
        return Insert(code, excepts[0], call)!;
    }

    /// <summary>
    /// Another mod's code using the bottom-of-pile filter's <c>Where</c> result before its <c>ToList</c> (<c>dup</c>, then
    /// <see cref="FirstTurnStandIns.Consume"/>: its type, <c>Count()</c> and <c>LastOrDefault()</c> counted as a STATE line, the same
    /// with this mod on or off only if the object it gets is the BCL's own, fast paths included). Its predicate reads no hook.
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnConsumerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int first = FirstTurnWheres(code)[0];
        if (code[first + 1].labels.Count > 0)
        {
            throw new InvalidOperationException("SetupPlayerTurn: a jump target right after the first Where");
        }
        return Insert(code, first + 1, new CodeInstruction(OpCodes.Dup), Call(typeof(FirstTurnStandIns), nameof(FirstTurnStandIns.Consume)))!;
    }

    /// <summary>
    /// Sorted after ExtraScopes' transpiler (install with priority 0): its two <c>Where</c> stand-ins put back to the game's
    /// <c>Enumerable.Where</c> (another mod's transpiler dropping a stand-in): nothing marked, both filters' <c>ToList</c> stand-ins
    /// get plain queries (no scope), the same cards.
    /// </summary>
    private static IEnumerable<CodeInstruction> FirstTurnUndoTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo where = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == nameof(Enumerable.Where) && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
            .MakeGenericMethod(typeof(CardModel));
        List<CodeInstruction> standIns = code.Where(c => c.opcode == OpCodes.Call && c.operand is MethodInfo m && m.DeclaringType == typeof(ExtraScopes)
            && m.Name is nameof(ExtraScopes.WhereFirstFilter) or nameof(ExtraScopes.WhereSecondFilter)).ToList();
        if (standIns.Count != 2)
        {
            throw new InvalidOperationException($"SetupPlayerTurn: {standIns.Count} Where stand-ins of ExtraScopes found, 2 expected (install with priority 0: after its transpiler)");
        }
        foreach (CodeInstruction call in standIns)
        {
            call.operand = where;
        }
        return code;
    }

    /// <summary>A patch of another mod on what runs inside the first turn's scope (a game lambda, a stand-in): does nothing.</summary>
    private static void FirstTurnNoOpPostfix()
    {
    }

    /// <summary>What the first-turn shapes give the game's queries instead: the same answers, not the game's objects.</summary>
    public static class FirstTurnStandIns
    {
        private static readonly Func<CardModel, bool> Innate = c => c.Keywords.Contains(CardKeyword.Innate);

        public static Func<CardModel, bool> InnatePredicate() => Innate;

        public static IReadOnlyList<CardModel> ArrayCopy(IReadOnlyList<CardModel> cards) => cards.ToArray();

        public static IEnumerable<CardModel> ArrayOf(IEnumerable<CardModel> cards) => cards.ToArray();

        public static void Consume(IEnumerable<CardModel> query) =>
            Count($"firstturn consumer {query.GetType().Name} count {query.Count()} last {(query.LastOrDefault() is { } last ? last.Id.Entry : "none")}");
    }

    private static IEnumerable<CodeInstruction> UngatedObserverTranspiler(IEnumerable<CodeInstruction> instructions) =>
        InsertBefore(instructions, IsExecutionFinished, _ => new[] { new CodeInstruction(OpCodes.Dup), Call(typeof(Plain), nameof(Plain.Every)) });

    private static IEnumerable<CodeInstruction> PreWalkTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
        InsertBefore(instructions, IsWalkCall, n => new[] { new CodeInstruction(OpCodes.Ldstr, $"{HookName(__originalMethod)} walk {n}"), Call(typeof(Plain), nameof(Plain.PreWalk)) });

    /// <summary>The hook a method's code belongs to (an async hook's state machine named after it).</summary>
    internal static string HookName(MethodBase body) =>
        body.DeclaringType?.DeclaringType == typeof(Hook) && body.DeclaringType.Name.StartsWith("<", StringComparison.Ordinal)
            ? body.DeclaringType.Name.Substring(1, body.DeclaringType.Name.IndexOf('>') - 1) : body.Name;

    private static IEnumerable<CodeInstruction> StoreLocalTranspiler(IEnumerable<CodeInstruction> instructions) =>
        InsertBefore(instructions, IsWalkCall, _ => new[] { new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Stloc_0) }, first: true);

    private static IEnumerable<CodeInstruction> GameCallTranspiler(IEnumerable<CodeInstruction> instructions) =>
        InsertBefore(instructions, IsWalkCall, _ => new[]
        {
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.Instance))), new CodeInstruction(OpCodes.Pop),
        }, first: true);

    private static IEnumerable<CodeInstruction> BranchTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        List<CodeInstruction> code = instructions.ToList();
        int at = code.FindIndex(IsWalkCall);
        if (at < 0)
        {
            throw new InvalidOperationException("il-branch: no walk call");
        }
        Label label = generator.DefineLabel();
        code[at].labels.Add(label);
        code.Insert(at, new CodeInstruction(OpCodes.Br, label));
        return code;
    }

    private static IEnumerable<CodeInstruction> DamageBridgeTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
        WithBridges(instructions.ToList(), original, AccessTools.Method(typeof(Bridges), nameof(Bridges.Additive)));

    private static IEnumerable<CodeInstruction> DamageBridgeCallFirstTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
        WithBridges(instructions.ToList(), original, AccessTools.Method(typeof(Bridges), nameof(Bridges.CallFirst)));

    private static readonly MethodInfo _modifyDamageAdditive = AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageAdditive));

    /// <summary>
    /// Downfall's transpiler (ModifyDamageInternalPatch), the additive call only: after each <c>callvirt ModifyDamageAdditive;
    /// stloc.s X</c>, <c>&lt;the listener load&gt;; ldloc.s X; ldarg.2; ldloc.0; ldarg.s 5; ldarg.3; ldarg.s 6; ldarg.s 7|ldnull;
    /// call bridge; stloc.s X</c> (the listener load: the instruction before the call's <c>ldarg.2</c>).
    /// </summary>
    private static List<CodeInstruction> WithBridges(List<CodeInstruction> code, MethodBase original, MethodInfo bridge)
    {
        int found = 0;
        bool hasCardPlay = original.GetParameters().Any(p => p.ParameterType == typeof(CardPlay));
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Callvirt || code[i].operand is not MethodInfo m || m != _modifyDamageAdditive || i + 1 >= code.Count
                || code[i + 1].opcode != OpCodes.Stloc_S || BridgeRun(code, i, hasCardPlay, bridge) is not { } run)
            {
                continue;
            }
            code.InsertRange(i + 2, run);
            i += 1 + run.Count;
            found++;
        }
        if (found == 0)
        {
            throw new InvalidOperationException("il-damage-bridge: no ModifyDamageAdditive call followed by stloc.s");
        }
        return code;
    }

    /// <summary>Downfall's inserted run for the call at <paramref name="call"/> (its store right after), or null when its listener load isn't found.</summary>
    private static List<CodeInstruction>? BridgeRun(List<CodeInstruction> code, int call, bool hasCardPlay, MethodInfo bridge, OpCode? exitStore = null)
    {
        int arg2 = -1;
        for (int k = call - 1; k >= 1 && k > call - 10; k--)
        {
            if (code[k].opcode == OpCodes.Ldarg_2)
            {
                arg2 = k;
                break;
            }
        }
        if (arg2 < 1)
        {
            return null;
        }
        object stored = code[call + 1].operand;
        return new List<CodeInstruction>
        {
            code[arg2 - 1].Clone(),
            new(OpCodes.Ldloc_S, stored),
            new(OpCodes.Ldarg_2),
            new(OpCodes.Ldloc_0),
            new(OpCodes.Ldarg_S, (byte)5),
            new(OpCodes.Ldarg_3),
            new(OpCodes.Ldarg_S, (byte)6),
            hasCardPlay ? new CodeInstruction(OpCodes.Ldarg_S, (byte)7) : new CodeInstruction(OpCodes.Ldnull),
            new(OpCodes.Call, bridge),
            exitStore is { } other ? new CodeInstruction(other) : new CodeInstruction(OpCodes.Stloc_S, stored),
        };
    }

    private static CodeInstruction Call(Type type, string method) => new(OpCodes.Call, AccessTools.Method(type, method));

    private static List<CodeInstruction> InsertBefore(IEnumerable<CodeInstruction> instructions, Func<CodeInstruction, bool> at,
        Func<int, CodeInstruction[]> insert, bool first = false)
    {
        var result = new List<CodeInstruction>();
        int found = 0;
        foreach (CodeInstruction code in instructions)
        {
            if (at(code) && !(first && found > 0))
            {
                result.AddRange(insert(found));
                found++;
            }
            result.Add(code);
        }
        if (found == 0)
        {
            throw new InvalidOperationException("insertion point not found");
        }
        return result;
    }

    // ---- What the inserted code calls (classes without a static constructor: the judge refuses one with it) ----

    /// <summary>Gated on Squeeze: from the first instruction, <c>ldarg.0; isinst Squeeze; brfalse ret</c>.</summary>
    public static class Gated
    {
        public static void Squeeze(AbstractModel listener)
        {
            if (listener is Squeeze)
            {
                Count("il-gated-observer squeeze calls");
            }
        }

        /// <summary>Minty Spire 2's signature (the listener and the running method).</summary>
        public static void SqueezeWithMethod(AbstractModel listener, MethodBase method)
        {
            if (listener is Squeeze)
            {
                Count("il-gated-observer squeeze with method");
            }
        }
    }

    /// <summary>
    /// Downfall's bridge signature (DMG): gated on Squeeze (+1 to its additive result, counted), else the game's value back as it
    /// is (<c>ldarg.1; ret</c>). Its near misses for the self-test and the live control.
    /// </summary>
    public static class Bridges
    {
        /// <summary>No initializer (a static constructor or an impure one would refuse the bridges: the TypeGated rule).</summary>
        public static long SqueezeCalls, EveryCall, SqueezeInActions, EveryInActions;

        /// <summary>Inside a damage action of the game (<see cref="DamageActions"/>; main thread).</summary>
        public static int ActionDepth;

        /// <summary>Inside a damage preview of the game (<see cref="DamagePreviews"/>; main thread).</summary>
        public static int PreviewDepth;

        public static decimal Additive(AbstractModel listener, decimal vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                SqueezeCalls++;
                if (ActionDepth > 0 && PreviewDepth == 0)
                {
                    SqueezeInActions++;
                }
                return vanillaNum + 1m;
            }
            return vanillaNum;
        }

        /// <summary>Gives another argument back (the running total): not an identity.</summary>
        public static decimal OtherArgument(AbstractModel listener, decimal vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + 1m;
            }
            return amount;
        }

        /// <summary>A call before the gate (every call counted): not gated.</summary>
        public static decimal CallFirst(AbstractModel listener, decimal vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            EveryCall++;
            if (ActionDepth > 0 && PreviewDepth == 0)
            {
                EveryInActions++;
            }
            if (listener is Squeeze)
            {
                return vanillaNum + 1m;
            }
            return vanillaNum;
        }

        /// <summary>A write on the no-op path after the gate (a static counter): not an identity exit.</summary>
        public static decimal SideEffect(AbstractModel listener, decimal vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + 1m;
            }
            EveryCall++;
            return vanillaNum;
        }

        /// <summary>An identity of another type than the local stored (an int given back into a decimal local).</summary>
        public static int OtherType(AbstractModel listener, int vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + 1;
            }
            return vanillaNum;
        }

        /// <summary>The stored local by reference (its address taken).</summary>
        public static decimal ByRef(AbstractModel listener, ref decimal vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + 1m;
            }
            return vanillaNum;
        }

        /// <summary>The same identity shape on <c>Hook.ModifyBlock</c>'s additive result, in the game's argument order (the scope's control).</summary>
        public static decimal Block(AbstractModel listener, decimal vanillaNum, Creature target, decimal amount, ValueProp props, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + 1m;
            }
            return vanillaNum;
        }

        /// <summary>The listener passed twice.</summary>
        public static decimal TwoListeners(AbstractModel listener, decimal vanillaNum, AbstractModel again)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + 1m;
            }
            return vanillaNum;
        }
    }

    /// <summary>The identity bridge in a class with a static constructor (refused).</summary>
    public static class CctorBridges
    {
        private static readonly long Started = Environment.TickCount64;

        public static decimal Additive(AbstractModel listener, decimal vanillaNum, Creature target, decimal amount, ValueProp props, Creature dealer, CardModel cardSource, CardPlay? cardPlay)
        {
            if (listener is Squeeze)
            {
                return vanillaNum + Started;
            }
            return vanillaNum;
        }
    }

    public static class Plain
    {
        public static void Every(AbstractModel listener) => Count("il-ungated-observer every listener");

        public static void PreWalk(string walk) => Count("il-prewalk-observer " + walk);
    }

    /// <summary>The gated shape in a class with a static constructor (refused: its first call would run it at another time).</summary>
    public static class CctorGated
    {
        private static readonly long Started = Environment.TickCount64;

        public static void Squeeze(AbstractModel listener)
        {
            if (listener is Squeeze)
            {
                Count("il-cctor-gated " + Started);
            }
        }
    }

    // ---- Self-test: the judge on instruction lists built from the game's code ----

    /// <summary><c>foreignpatch iljudge selftest|sweep</c>.</summary>
    public static IReadOnlyList<string> Command(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "selftest" => SelfTest(),
            "sweep" => Sweep(),
            _ => throw new ArgumentException("foreignpatch iljudge selftest|sweep"),
        };
    }

    private static MethodBase HookBody(string name) => Body(AccessTools.Method(typeof(Hook), name) ?? throw new MissingMethodException("Hook." + name));

    private sealed record Case(string Hook, string Name, bool Accept, string? GateName = null);

    public static IReadOnlyList<string> SelfTest()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        int cases = 0;
        foreach (string hook in new[] { "AfterCardPlayed", "ModifyDamageInternal", "BeforeCardPlayed" })
        {
            MethodBase body = HookBody(hook);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(body, out ILGenerator generator);
            bool async = !body.IsStatic;
            foreach ((string name, bool accept, string? gate, List<CodeInstruction>? variant) in Variants(body, original, generator, async))
            {
                if (variant == null)
                {
                    continue; // Not applicable to this hook's code.
                }
                cases++;
                string? why = TranspilerJudge.JudgeCode(body, original.Select(TranspilerJudge.Ins.From).ToList(), variant.Select(TranspilerJudge.Ins.From).ToList(),
                    IsWalk, out Type[] gates, out _, out int runs);
                bool accepted = why == null;
                bool gateOk = gate == null || gates.Any(g => g.Name == gate);
                string line = $"FOREIGN iljudge {hook} {name}: {(accepted ? $"accepted ({runs} run(s){(gates.Length > 0 ? ", gates " + string.Join("|", gates.Select(g => g.Name)) : "")})" : "refused (" + why + ")")}";
                lines.Add(line);
                if (accepted != accept || (accepted && !gateOk))
                {
                    failures.Add(line + $" (expected {(accept ? "accepted" + (gate != null ? " gated on " + gate : "") : "refused")})");
                }
            }
        }
        // Re-audit of the premises (DMG-1): the identity bridge's scope, two cases whose game code differs from the loop above's.
        foreach ((string name, MethodBase scopeBody, List<CodeInstruction>? scopeOriginal, List<CodeInstruction>? scopeVariant) in ScopeVariants())
        {
            if (scopeOriginal == null || scopeVariant == null)
            {
                failures.Add($"FOREIGN iljudge {name}: its insertion point not found");
                continue;
            }
            cases++;
            string? why = TranspilerJudge.JudgeCode(scopeBody, scopeOriginal.Select(TranspilerJudge.Ins.From).ToList(), scopeVariant.Select(TranspilerJudge.Ins.From).ToList(),
                IsWalk, out _, out _, out _);
            string line = $"FOREIGN iljudge {HookName(scopeBody)} {name}: {(why == null ? "accepted" : "refused (" + why + ")")}";
            lines.Add(line);
            if (why == null)
            {
                failures.Add(line + " (expected refused)");
            }
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"IL judge self-test: {failures.Count} of {cases} case(s) unexpected: " + string.Join(" | ", failures));
        }
        lines.Add($"FOREIGN iljudge selftest: {cases} cases as expected");
        return lines;
    }

    /// <summary>
    /// The identity bridge's scope (DMG-1), each expected refused: (1) Downfall's run where the game's instruction after the store
    /// carries a label (the same game code with that label on both sides: a path joining there never ran the store); (2) the same
    /// shape on <c>Hook.ModifyBlock</c>'s additive result (another hook: outside the premise's scope).
    /// </summary>
    private static IEnumerable<(string Name, MethodBase Body, List<CodeInstruction>? Original, List<CodeInstruction>? Variant)> ScopeVariants()
    {
        MethodBase damage = HookBody("ModifyDamageInternal");
        List<CodeInstruction> o = PatchProcessor.GetOriginalInstructions(damage, out ILGenerator generator);
        int additive = o.FindIndex(c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo m && m == _modifyDamageAdditive);
        bool hasCardPlay = damage.GetParameters().Any(p => p.ParameterType == typeof(CardPlay));
        List<CodeInstruction>? labeled = null, labeledVariant = null;
        if (additive >= 0 && additive + 2 < o.Count && o[additive + 1].opcode == OpCodes.Stloc_S
            && BridgeRun(o, additive, hasCardPlay, AccessTools.Method(typeof(Bridges), nameof(Bridges.Additive))) is { } run)
        {
            labeled = new List<CodeInstruction>(o);
            CodeInstruction after = Copy(o[additive + 2]);
            after.labels.Add(generator.DefineLabel());
            labeled[additive + 2] = after;
            labeledVariant = Insert(labeled, additive + 2, run.ToArray());
        }
        yield return ("bridge-label-after-store", damage, labeled, labeledVariant);

        MethodBase block = AccessTools.Method(typeof(Hook), "ModifyBlock") ?? throw new MissingMethodException("Hook.ModifyBlock");
        List<CodeInstruction> b = PatchProcessor.GetOriginalInstructions(block, out _);
        int call = b.FindIndex(c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo m && m.Name == "ModifyBlockAdditive");
        List<CodeInstruction>? blockVariant = null;
        if (call >= 6 && call + 1 < b.Count && b[call + 1].opcode == OpCodes.Stloc_S && b[call].operand is MethodInfo additiveBlock
            && additiveBlock.GetParameters().Length == 5)
        {
            // The game's own argument loads (listener, target, num, props, cardSource, cardPlay), the stored value second.
            var blockRun = new List<CodeInstruction> { b[call - 6].Clone(), new(OpCodes.Ldloc_S, b[call + 1].operand) };
            blockRun.AddRange(b.GetRange(call - 5, 5).Select(c => c.Clone()));
            blockRun.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Bridges), nameof(Bridges.Block))));
            blockRun.Add(new CodeInstruction(OpCodes.Stloc_S, b[call + 1].operand));
            blockVariant = Insert(b, call + 2, blockRun.ToArray());
        }
        yield return ("bridge-other-hook", block, b, blockVariant);
    }

    private static IEnumerable<(string Name, bool Accept, string? Gate, List<CodeInstruction>? Code)> Variants(MethodBase body, List<CodeInstruction> o,
        ILGenerator generator, bool async)
    {
        int walk = o.FindIndex(IsWalkCall);
        int secondWalk = walk < 0 ? -1 : o.FindIndex(walk + 1, IsWalkCall);
        int finished = o.FindIndex(IsExecutionFinished);
        // Where the current listener is stored (sync code: get_Current; stloc X).
        int current = o.FindIndex(c => c.operand is MethodInfo { Name: "get_Current" });
        int listenerStore = current >= 0 && current + 1 < o.Count ? current + 1 : -1;
        CodeInstruction? listenerLoad = !async && listenerStore >= 0 ? LoadOf(o[listenerStore]) : null;

        yield return ("unchanged", true, null, new List<CodeInstruction>(o));
        yield return ("prewalk-observer", true, null, Insert(o, walk, new CodeInstruction(OpCodes.Ldstr, "selftest"), Call(typeof(Plain), nameof(Plain.PreWalk))));
        yield return ("entry-observer", !async, null, Insert(o, 0, new CodeInstruction(OpCodes.Ldstr, "selftest"), Call(typeof(Plain), nameof(Plain.PreWalk))));
        if (async)
        {
            yield return ("gated-observer", true, "Squeeze", finished < 0 ? null : Insert(o, finished, new CodeInstruction(OpCodes.Dup), Call(typeof(Gated), nameof(Gated.Squeeze))));
            yield return ("ungated-observer", false, null, finished < 0 ? null : Insert(o, finished, new CodeInstruction(OpCodes.Dup), Call(typeof(Plain), nameof(Plain.Every))));
            yield return ("cctor-gated-observer", false, null, finished < 0 ? null : Insert(o, finished, new CodeInstruction(OpCodes.Dup), Call(typeof(CctorGated), nameof(CctorGated.Squeeze))));
            yield return ("minty-shape", false, null, finished < 0 ? null : Insert(o, finished, new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MethodBase), nameof(MethodBase.GetCurrentMethod))), Call(typeof(Gated), nameof(Gated.SqueezeWithMethod))));
            yield return ("popped-listener", false, null, finished < 0 ? null : Insert(o, finished, new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ldnull)));
            // The listener field read where it doesn't hold the current listener (before the second loop's walk).
            FieldInfo? model = finished > 0 && o[finished - 1].operand is FieldInfo f ? f : null;
            yield return ("stale-listener-field", false, null, model == null || secondWalk < 0 ? null
                : Insert(o, secondWalk, new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, model), Call(typeof(Gated), nameof(Gated.Squeeze))));
        }
        else if (listenerLoad != null)
        {
            // The identity bridge (DMG) where the game has Downfall's insertion point (ModifyDamageInternal).
            int additive = o.FindIndex(c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo m && m == _modifyDamageAdditive);
            if (additive >= 0 && additive + 1 < o.Count && o[additive + 1].opcode == OpCodes.Stloc_S)
            {
                bool hasCardPlay = body.GetParameters().Any(p => p.ParameterType == typeof(CardPlay));
                MethodInfo Bridge(string name) => AccessTools.Method(typeof(Bridges), name);
                List<CodeInstruction>? Bridged(MethodInfo bridge, int at, OpCode? store = null) =>
                    BridgeRun(o, additive, hasCardPlay, bridge, store) is { } run ? Insert(o, at, run.ToArray()) : null;
                yield return ("identity-bridge", true, "Squeeze", Bridged(Bridge(nameof(Bridges.Additive)), additive + 2));
                List<CodeInstruction>? twice = BridgeRun(o, additive, hasCardPlay, Bridge(nameof(Bridges.Additive))) is { } first
                    && BridgeRun(o, additive, hasCardPlay, AccessTools.Method(typeof(Bridges), nameof(Bridges.Additive))) is { } second
                    ? Insert(o, additive + 2, first.Concat(second).ToArray()) : null;
                yield return ("identity-bridge-twice", true, "Squeeze", twice);
                yield return ("bridge-other-exit", false, null, Bridged(Bridge(nameof(Bridges.OtherArgument)), additive + 2));
                yield return ("bridge-other-local", false, null, Bridged(Bridge(nameof(Bridges.Additive)), additive + 2, OpCodes.Stloc_0));
                yield return ("bridge-before-store", false, null, Bridged(Bridge(nameof(Bridges.Additive)), additive + 1));
                yield return ("bridge-call-first", false, null, Bridged(Bridge(nameof(Bridges.CallFirst)), additive + 2));
                yield return ("bridge-cctor", false, null, Bridged(AccessTools.Method(typeof(CctorBridges), nameof(CctorBridges.Additive)), additive + 2));
                // A local the game didn't read just before (the modifiers list) in place of the running total.
                List<CodeInstruction>? unread = Bridged(Bridge(nameof(Bridges.Additive)), additive + 2);
                if (unread != null)
                {
                    unread[additive + 2 + 3] = new CodeInstruction(OpCodes.Ldloc_1);
                }
                yield return ("bridge-unread-local", false, null, unread);
                yield return ("bridge-two-listeners", false, null, BridgeRun(o, additive, hasCardPlay, Bridge(nameof(Bridges.Additive))) is { } plain
                    ? Insert(o, additive + 2, plain[0].Clone(), plain[1].Clone(), plain[0].Clone(), new CodeInstruction(OpCodes.Call, Bridge(nameof(Bridges.TwoListeners))), plain[^1].Clone())
                    : null);
                // Re-audit of the premises (DMG-1): the multiplicative and v0.113's late loops; the near misses the scope must refuse.
                int Store(string name) => o.FindIndex(c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo m && m.Name == name
                    && m.DeclaringType == typeof(AbstractModel)) is int k && k >= 0 && k + 1 < o.Count && o[k + 1].opcode == OpCodes.Stloc_S ? k : -1;
                int multiplicative = Store("ModifyDamageMultiplicative"), late = Store("ModifyDamageMultiplicativeLate");
                yield return ("identity-bridge-multiplicative", true, "Squeeze", multiplicative < 0 ? null
                    : BridgeRun(o, multiplicative, hasCardPlay, Bridge(nameof(Bridges.Additive))) is { } mul ? Insert(o, multiplicative + 2, mul.ToArray()) : null);
                yield return ("identity-bridge-late", true, "Squeeze", late < 0 ? null
                    : BridgeRun(o, late, hasCardPlay, Bridge(nameof(Bridges.Additive))) is { } lat ? Insert(o, late + 2, lat.ToArray()) : null);
                // R3-4 (re-audit round 3): an accept case per remaining store of the scope, the additive one found as the others are
                // (Store) and the cap's.
                int additiveStore = Store("ModifyDamageAdditive"), cap = Store("ModifyDamageCap");
                yield return ("identity-bridge-additive-store", true, "Squeeze", additiveStore < 0 ? null
                    : BridgeRun(o, additiveStore, hasCardPlay, Bridge(nameof(Bridges.Additive))) is { } add ? Insert(o, additiveStore + 2, add.ToArray()) : null);
                // The cap loop reads no running total before its call: the bridge's `amount` is the hook's `damage` argument there
                // (argument 4 on v0.107.1 to v0.113.1, never written by the game's code).
                List<CodeInstruction>? capRun = cap < 0 ? null : BridgeRun(o, cap, hasCardPlay, Bridge(nameof(Bridges.Additive)));
                if (capRun != null)
                {
                    capRun[3] = new CodeInstruction(OpCodes.Ldarg_S, (byte)4);
                }
                yield return ("identity-bridge-cap", true, "Squeeze", capRun == null ? null : Insert(o, cap + 2, capRun.ToArray()));
                yield return ("bridge-side-effect", false, null, Bridged(Bridge(nameof(Bridges.SideEffect)), additive + 2));
                yield return ("bridge-pair-second-not-identity", false, null, BridgeRun(o, additive, hasCardPlay, Bridge(nameof(Bridges.Additive))) is { } good
                    && BridgeRun(o, additive, hasCardPlay, Bridge(nameof(Bridges.OtherArgument))) is { } bad ? Insert(o, additive + 2, good.Concat(bad).ToArray()) : null);
                yield return ("bridge-other-type", false, null, Bridged(Bridge(nameof(Bridges.OtherType)), additive + 2));
                List<CodeInstruction>? byRef = Bridged(Bridge(nameof(Bridges.ByRef)), additive + 2);
                if (byRef != null)
                {
                    byRef[additive + 2 + 1] = new CodeInstruction(OpCodes.Ldloca_S, o[additive + 1].operand);
                }
                yield return ("bridge-address-taken", false, null, byRef);
            }
            yield return ("gated-observer", true, "Squeeze", Insert(o, listenerStore + 1, listenerLoad.Clone(), Call(typeof(Gated), nameof(Gated.Squeeze))));
            yield return ("ungated-observer", false, null, Insert(o, listenerStore + 1, listenerLoad.Clone(), Call(typeof(Plain), nameof(Plain.Every))));
            yield return ("in-loop-plain-observer", false, null, Insert(o, listenerStore + 1, new CodeInstruction(OpCodes.Ldstr, "selftest"), Call(typeof(Plain), nameof(Plain.PreWalk))));
            yield return ("stale-listener-local", false, null, secondWalk < 0 ? null : Insert(o, secondWalk, listenerLoad.Clone(), Call(typeof(Gated), nameof(Gated.Squeeze))));
        }
        yield return ("store-local", false, null, Insert(o, walk, new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Stloc_0)));
        yield return ("game-call", false, null, Insert(o, walk,
            new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(CombatManager), nameof(CombatManager.Instance))), new CodeInstruction(OpCodes.Pop)));
        // A jump to the walk call: the label on a copy of it (the original list stays as it is).
        List<CodeInstruction> branch = new(o);
        Label label = generator.DefineLabel();
        CodeInstruction target = Copy(o[walk]);
        target.labels.Add(label);
        branch[walk] = target;
        branch.Insert(walk, new CodeInstruction(OpCodes.Br, label));
        yield return ("branch", false, null, branch);
        // Two neighbours swapped (the first pair of different instructions without labels or blocks after the walk).
        int pair = Enumerable.Range(walk + 1, Math.Max(0, o.Count - walk - 2)).FirstOrDefault(i => o[i].opcode != o[i + 1].opcode
            && o[i].labels.Count + o[i + 1].labels.Count + o[i].blocks.Count + o[i + 1].blocks.Count == 0);
        if (pair > 0)
        {
            List<CodeInstruction> swapped = new(o);
            (swapped[pair], swapped[pair + 1]) = (swapped[pair + 1], swapped[pair]);
            yield return ("reorder", false, null, swapped);
        }
        // A conditional branch turned the other way.
        int cond = o.FindIndex(c => c.opcode == OpCodes.Brtrue || c.opcode == OpCodes.Brtrue_S);
        if (cond >= 0)
        {
            List<CodeInstruction> flipped = new(o);
            CodeInstruction other = Copy(o[cond]);
            other.opcode = o[cond].opcode == OpCodes.Brtrue ? OpCodes.Brfalse : OpCodes.Brfalse_S;
            flipped[cond] = other;
            yield return ("changed-branch", false, null, flipped);
        }
        // An exception block mark taken away.
        int block = o.FindIndex(c => c.blocks.Count > 0);
        if (block >= 0)
        {
            List<CodeInstruction> unblocked = new(o);
            CodeInstruction bare = Copy(o[block]);
            bare.blocks.Clear();
            unblocked[block] = bare;
            yield return ("handler-changed", false, null, unblocked);
        }
    }

    /// <summary>CombatManager.SetupPlayerTurn's state machine (ExtraScopes' first turn).</summary>
    internal static MethodBase FirstTurnBody() =>
        AccessTools.AsyncMoveNext(AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn") ?? throw new MissingMethodException("CombatManager.SetupPlayerTurn"));

    private static CodeInstruction Copy(CodeInstruction code)
    {
        CodeInstruction copy = code.Clone();
        copy.labels.AddRange(code.labels);
        copy.blocks.AddRange(code.blocks);
        return copy;
    }

    private static CodeInstruction? LoadOf(CodeInstruction store)
    {
        OpCode op = store.opcode;
        return op == OpCodes.Stloc_0 ? new CodeInstruction(OpCodes.Ldloc_0) : op == OpCodes.Stloc_1 ? new CodeInstruction(OpCodes.Ldloc_1)
            : op == OpCodes.Stloc_2 ? new CodeInstruction(OpCodes.Ldloc_2) : op == OpCodes.Stloc_3 ? new CodeInstruction(OpCodes.Ldloc_3)
            : op == OpCodes.Stloc_S ? new CodeInstruction(OpCodes.Ldloc_S, store.operand) : op == OpCodes.Stloc ? new CodeInstruction(OpCodes.Ldloc, store.operand)
            : null;
    }

    private static List<CodeInstruction>? Insert(List<CodeInstruction> o, int at, params CodeInstruction[] run)
    {
        if (at < 0)
        {
            return null;
        }
        var result = new List<CodeInstruction>(o);
        result.InsertRange(at, run);
        return result;
    }

    /// <summary>
    /// The hooks whose code the judge doesn't follow today (both versions): their walk is passed to <c>Contains</c>. The sweep
    /// fails when another hook isn't understood (a judge regression would otherwise only change the count it prints) or when
    /// one of these starts being understood (the list kept current). Verified on v0.107.1 and v0.113.1; v0.111.1 (the public
    /// version since 2026-11-17) has the same code for every hook with a walk but AfterDeath and ModifyDamageInternal, its own:
    /// on another game version a failure may be that version's hooks, not a judge bug: read the hooks named, then fix the judge
    /// or update this list.
    /// </summary>
    private static readonly string[] NotUnderstood = { "AfterPreventingBlockClear", "AfterPreventingDeath", "AfterPreventingDraw" };

    /// <summary>The prewalk observer and (async hooks) the gated observer on every classified hook of this game: how many the judge follows.</summary>
    public static IReadOnlyList<string> Sweep()
    {
        var lines = new List<string>();
        var unexpected = new List<string>();
        int hooks = 0, understood = 0;
        foreach (MethodInfo hook in typeof(Hook).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => !m.Name.Contains('<')).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            MethodBase body;
            List<CodeInstruction> original;
            try
            {
                body = Body(hook);
                if (body.GetMethodBody() == null)
                {
                    continue;
                }
                original = PatchProcessor.GetOriginalInstructions(body);
            }
            catch
            {
                continue;
            }
            int walk = original.FindIndex(IsWalkCall);
            if (walk < 0)
            {
                continue;
            }
            hooks++;
            var ins = original.Select(TranspilerJudge.Ins.From).ToList();
            string? pre = TranspilerJudge.JudgeCode(body, ins, Insert(original, walk, new CodeInstruction(OpCodes.Ldstr, "selftest"), Call(typeof(Plain), nameof(Plain.PreWalk)))!
                .Select(TranspilerJudge.Ins.From).ToList(), IsWalk, out _, out _, out _);
            int finished = original.FindIndex(IsExecutionFinished);
            string? gated = finished < 0 || body.IsStatic ? null : TranspilerJudge.JudgeCode(body, ins,
                Insert(original, finished, new CodeInstruction(OpCodes.Dup), Call(typeof(Gated), nameof(Gated.Squeeze)))!.Select(TranspilerJudge.Ins.From).ToList(),
                IsWalk, out _, out _, out _);
            bool known = NotUnderstood.Contains(hook.Name);
            if (pre == null && gated == null)
            {
                understood++;
                if (known)
                {
                    unexpected.Add(hook.Name + " now understood (update NotUnderstood)");
                }
            }
            else
            {
                lines.Add($"FOREIGN iljudge sweep {hook.Name}: prewalk {pre ?? "accepted"}; gated {gated ?? (finished < 0 || body.IsStatic ? "n/a" : "accepted")}");
                if (!known)
                {
                    unexpected.Add(hook.Name + " not understood");
                }
            }
        }
        if (unexpected.Count > 0)
        {
            throw new InvalidOperationException($"IL judge sweep: {unexpected.Count} unexpected verdict(s): " + string.Join(" | ", unexpected)
                + " (the list was verified on v0.107.1 and v0.113.1: on another game version, check that version's hooks before the judge)");
        }
        lines.Add($"FOREIGN iljudge sweep: {understood} of {hooks} hooks with a walk accept both shapes, the {NotUnderstood.Length} known exceptions aside");
        return lines;
    }
}
