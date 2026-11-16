using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

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
/// static constructor, Minty's own shape (a <c>GetCurrentMethod</c> call next to the gated one); each verdict must be the expected one.
/// <c>foreignpatch iljudge sweep</c>: the prewalk observer and the gated observer on every classified hook of this game (how many the
/// analysis follows).
/// </summary>
public static class IlJudgeShapes
{
    public static readonly string[] Shapes = { "il-gated-observer", "il-prewalk-observer", "il-ungated-observer", "il-store-local", "il-game-call", "il-branch" };

    private static readonly ConcurrentDictionary<string, long> _counts = new();

    internal static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    /// <summary>The counts as STATE lines (compared line for line between the on and off sides).</summary>
    public static IEnumerable<string> ReportLines(string label) =>
        _counts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"STATE foreign {label} {e.Key} {e.Value}");

    /// <summary>The method a shape patches: an async hook's state machine, else the hook itself.</summary>
    public static MethodBase Body(MethodBase hook) =>
        hook.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null ? AccessTools.AsyncMoveNext(hook) : hook;

    /// <summary>Installs a shape on the hook's code; returns the method patched.</summary>
    public static MethodBase Install(string shape, string gate, MethodBase hook, Harmony harmony, int priority)
    {
        if (hook.DeclaringType != typeof(Hook))
        {
            throw new ArgumentException(shape + " goes on a MegaCrit.Sts2.Core.Hooks.Hook method");
        }
        if (shape == "il-gated-observer" && gate != "squeeze")
        {
            throw new ArgumentException("il-gated-observer gate: squeeze");
        }
        _counts.TryAdd("installed " + shape, 0); // The class initialized here, not at a first gated call.
        MethodBase body = Body(hook);
        string transpiler = shape switch
        {
            "il-gated-observer" => nameof(GatedObserverTranspiler),
            "il-prewalk-observer" => nameof(PreWalkTranspiler),
            "il-ungated-observer" => nameof(UngatedObserverTranspiler),
            "il-store-local" => nameof(StoreLocalTranspiler),
            "il-game-call" => nameof(GameCallTranspiler),
            "il-branch" => nameof(BranchTranspiler),
            _ => throw new ArgumentException("unknown il shape " + shape),
        };
        harmony.Patch(body, transpiler: new HarmonyMethod(typeof(IlJudgeShapes), transpiler) { priority = priority });
        return body;
    }

    /// <summary>The judge's verdict on the method (after an install).</summary>
    public static IEnumerable<string> Verdict(MethodBase body)
    {
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
        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"IL judge self-test: {failures.Count} of {cases} case(s) unexpected: " + string.Join(" | ", failures));
        }
        lines.Add($"FOREIGN iljudge selftest: {cases} cases as expected");
        return lines;
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

    /// <summary>The prewalk observer and (async hooks) the gated observer on every classified hook of this game: how many the judge follows.</summary>
    public static IReadOnlyList<string> Sweep()
    {
        var lines = new List<string>();
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
            if (pre == null && gated == null)
            {
                understood++;
            }
            else
            {
                lines.Add($"FOREIGN iljudge sweep {hook.Name}: prewalk {pre ?? "accepted"}; gated {gated ?? (finished < 0 || body.IsStatic ? "n/a" : "accepted")}");
            }
        }
        lines.Add($"FOREIGN iljudge sweep: {understood} of {hooks} hooks with a walk accept both shapes");
        return lines;
    }
}
