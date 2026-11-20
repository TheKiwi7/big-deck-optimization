using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic transpilers of "another mod" on the serializers RelicStateReuse and SnapshotCardReuse rely on, for
/// <see cref="UnchangedTranspilers"/> (DESIGN "Transpilers leaving the game's code unchanged"): <c>foreignpatch &lt;shape&gt;
/// &lt;Type:Method&gt; [priority N]</c>, removed by <c>foreignpatch remove</c>. Every shape keeps the game's behaviour (the
/// same values written), so an A/B stays identical; only the parts' verdict differs.
/// <list type="bullet">
/// <item><c>unchanged-transpiler</c>: Unlimited's <c>PacketSizePatch</c> shape (a list width 3 right before <c>WriteList</c> or
/// <c>ReadList</c> replaced by a call): no such constant in these serializers, the result is the game's code. Kept.</item>
/// <item><c>getter-transpiler</c>: the first <c>ldc.i4 8</c> or <c>32</c> replaced by a call of a pure static getter returning
/// the same value. Refused (the rule takes no difference at all).</item>
/// <item><c>field-transpiler</c>: the same constant replaced by a mutable static field holding it. Refused.</item>
/// <item><c>nop-transpiler</c>: a <c>nop</c> inserted first. Refused.</item>
/// <item><c>branch-transpiler</c>: a <c>br</c> to the next instruction inserted first. Refused.</item>
/// </list>
/// ForeignPatches' <c>identity-transpiler</c> completes them: kept at the default priority, refused at priority 0 once the
/// capture is there (sorted after it). <c>foreignpatch unchanged selftest</c>: the judge itself on the game's code of
/// <c>SerializableRelic.Serialize</c> built as instruction lists (copies, a retargeted branch, an exception block).
/// </summary>
public static class ForeignShapesUnchanged
{
    public static readonly string[] Shapes = { "unchanged-transpiler", "getter-transpiler", "field-transpiler", "nop-transpiler", "branch-transpiler" };

    public static bool Handles(string[] parts) => parts.Length >= 2 && (Shapes.Contains(parts[0]) || parts[0] == "unchanged");

    public static IReadOnlyList<string> Run(string[] parts, Action<string, MethodBase> installed)
    {
        if (parts[0] == "unchanged")
        {
            return parts[1] switch
            {
                "selftest" => SelfTest(),
                "report" => Report(),
                _ => throw new ArgumentException("foreignpatch unchanged selftest|report"),
            };
        }
        string shape = parts[0];
        MethodBase target = AccessTools.Method(parts[1]) ?? throw new ArgumentException("no method " + parts[1] + " (Namespace.Type:Method)");
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string id = ForeignPatches.IdPrefix + shape;
        // Fails here, not later: a shape that finds nothing to change on its target would be a silent kept.
        if (shape is "getter-transpiler" or "field-transpiler" && PatchProcessor.GetOriginalInstructions(target).All(c => ConstantOf(c) is not (8 or 32)))
        {
            throw new ArgumentException($"{shape}: no ldc.i4 8 or 32 in {Name(target)}");
        }
        new Harmony(id).Patch(target, transpiler: new HarmonyMethod(typeof(ForeignShapesUnchanged), shape switch
        {
            "unchanged-transpiler" => nameof(UnlimitedShape),
            "getter-transpiler" => nameof(GetterShape),
            "field-transpiler" => nameof(FieldShape),
            "nop-transpiler" => nameof(NopShape),
            "branch-transpiler" => nameof(BranchShape),
            _ => throw new ArgumentException(shape),
        }) { priority = priority });
        installed(id, target);
        return new[] { $"FOREIGN installed {shape} on {Name(target)} as {id} (priority {priority})" };
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    private static IReadOnlyList<string> Report()
    {
#if BENCH
        return new[] { $"FOREIGN unchanged verdicts kept {UnchangedTranspilers.Kept} refused {UnchangedTranspilers.Refused}" };
#else
        return new[] { "FOREIGN unchanged verdicts: test builds only" };
#endif
    }

    // ---- The shapes ----

    private static readonly MethodInfo _wireBits = AccessTools.Method(typeof(ForeignShapesUnchanged), nameof(WireBits));

    /// <summary>Unlimited's <c>GetRequiredBits</c> (16 players: 5 bits).</summary>
    public static int WireBits() => 5;

    /// <summary>Unlimited 2.0.2's <c>Transpile_SerializeDeserialize</c>, line for line (its <c>IsVanillaListBitsConstant</c> inlined).</summary>
    private static IEnumerable<CodeInstruction> UnlimitedShape(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);
        for (int i = 1; i < list.Count; i++)
        {
            if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && list[i].operand is MethodInfo method
                && method.DeclaringType?.Name is nameof(PacketWriter) or nameof(PacketReader) && method.Name is "WriteList" or "ReadList"
                && ConstantOf(list[i - 1]) == 3)
            {
                list[i - 1] = new CodeInstruction(OpCodes.Call, _wireBits);
            }
        }
        return list;
    }

    public static int Eight() => 8;

    public static int ThirtyTwo() => 32;

    /// <summary>Mutable: <see cref="FieldShape"/> reads it where the constant was (set to that constant before the method runs).</summary>
    public static int MutableEight = 8, MutableThirtyTwo = 32;

    private static IEnumerable<CodeInstruction> GetterShape(IEnumerable<CodeInstruction> instructions) =>
        ReplaceFirstConstant(instructions, value => new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ForeignShapesUnchanged), value == 8 ? nameof(Eight) : nameof(ThirtyTwo))));

    private static IEnumerable<CodeInstruction> FieldShape(IEnumerable<CodeInstruction> instructions) =>
        ReplaceFirstConstant(instructions, value => new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(ForeignShapesUnchanged), value == 8 ? nameof(MutableEight) : nameof(MutableThirtyTwo))));

    private static IEnumerable<CodeInstruction> ReplaceFirstConstant(IEnumerable<CodeInstruction> instructions, Func<int, CodeInstruction> with)
    {
        var list = new List<CodeInstruction>(instructions);
        int at = list.FindIndex(c => ConstantOf(c) is 8 or 32);
        if (at >= 0)
        {
            CodeInstruction replacement = with(ConstantOf(list[at])!.Value);
            replacement.labels.AddRange(list[at].labels);
            replacement.blocks.AddRange(list[at].blocks);
            list[at] = replacement;
        }
        return list;
    }

    private static IEnumerable<CodeInstruction> NopShape(IEnumerable<CodeInstruction> instructions) =>
        new[] { new CodeInstruction(OpCodes.Nop) }.Concat(instructions);

    private static IEnumerable<CodeInstruction> BranchShape(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var list = new List<CodeInstruction>(instructions);
        Label next = generator.DefineLabel();
        list[0].labels.Add(next);
        list.Insert(0, new CodeInstruction(OpCodes.Br, next));
        return list;
    }

    private static int? ConstantOf(CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldc_I4_3) return 3;
        if (code.opcode == OpCodes.Ldc_I4_8) return 8;
        if ((code.opcode == OpCodes.Ldc_I4_S || code.opcode == OpCodes.Ldc_I4) && code.operand != null) return Convert.ToInt32(code.operand);
        return null;
    }

    // ---- The judge itself, on instruction lists ----

    /// <summary>
    /// <see cref="TranspilerJudge.Unchanged"/> on <c>SerializableRelic.Serialize</c>'s code read twice (two readers, other label
    /// ids: kept), and on edited copies: each must give the expected verdict. Fails the line otherwise.
    /// </summary>
    private static IReadOnlyList<string> SelfTest()
    {
        MethodBase body = AccessTools.Method(typeof(SerializableRelic), nameof(SerializableRelic.Serialize));
        List<TranspilerJudge.Ins> Read() => PatchProcessor.GetOriginalInstructions(body).Select(TranspilerJudge.Ins.From).ToList();
        List<TranspilerJudge.Ins> original = Read();
        var lines = new List<string>();
        int failed = 0;
        void Check(string label, IReadOnlyList<TranspilerJudge.Ins> variant, bool kept)
        {
            string? why = TranspilerJudge.Unchanged(original, variant);
            bool ok = (why == null) == kept;
            failed += ok ? 0 : 1;
            lines.Add($"FOREIGN unchanged selftest {label}: {(why == null ? "kept" : "refused (" + why + ")")} {(ok ? "OK" : "WRONG")}");
        }
        Check("read again", Read(), true);
        // Every branch's target moved to another branch's: same count, same opcodes, a target changed.
        int[] branches = Enumerable.Range(0, original.Count).Where(i => original[i].IsBranch).ToArray();
        if (branches.Length < 2 || original.Select((ins, i) => (ins, i)).Where(p => p.ins.Labels.Length > 0).Select(p => p.i).Distinct().Count() < 2)
        {
            throw new InvalidOperationException("unchanged selftest: SerializableRelic.Serialize has fewer than two branches or targets");
        }
        int first = branches[0];
        int otherTarget = original.Select((ins, i) => (ins, i)).First(p => p.ins.Labels.Length > 0 && !p.ins.Labels.Contains((int)original[first].Operand!)).ins.Labels[0];
        Check("branch retargeted", original.Select((ins, i) => i == first ? new TranspilerJudge.Ins(ins.Op, otherTarget, ins.Labels, ins.Blocks) : ins).ToList(), false);
        // A label moved off its instruction onto the next one (where a branch lands changes).
        int labelled = original.FindIndex(ins => ins.Labels.Length > 0);
        Check("label moved", original.Select((ins, i) => i == labelled ? new TranspilerJudge.Ins(ins.Op, ins.Operand, Array.Empty<int>(), ins.Blocks)
            : i == labelled + 1 ? new TranspilerJudge.Ins(ins.Op, ins.Operand, ins.Labels.Concat(original[labelled].Labels).ToArray(), ins.Blocks) : ins).ToList(), false);
        // An exception block opened on the first instruction.
        Check("exception block", original.Select((ins, i) => i == 0 ? new TranspilerJudge.Ins(ins.Op, ins.Operand, ins.Labels,
            new[] { new TranspilerJudge.Block(ExceptionBlockType.BeginExceptionBlock, null) }) : ins).ToList(), false);
        // A constant changed in place, an operand replaced, an instruction dropped.
        int constant = original.FindIndex(ins => ins.Op == OpCodes.Ldc_I4_8);
        if (constant < 0)
        {
            throw new InvalidOperationException("unchanged selftest: no ldc.i4.8 in SerializableRelic.Serialize");
        }
        Check("constant changed", original.Select((ins, i) => i == constant ? new TranspilerJudge.Ins(OpCodes.Ldc_I4_7, null, ins.Labels, ins.Blocks) : ins).ToList(), false);
        Check("getter for the constant", original.Select((ins, i) => i == constant ? new TranspilerJudge.Ins(OpCodes.Call, AccessTools.Method(typeof(ForeignShapesUnchanged), nameof(Eight)), ins.Labels, ins.Blocks) : ins).ToList(), false);
        Check("instruction dropped", original.Where((_, i) => i != constant).ToList(), false);
        lines.Add($"FOREIGN unchanged selftest {(failed == 0 ? "passed" : failed + " WRONG")}");
        if (failed > 0)
        {
            throw new InvalidOperationException(string.Join("; ", lines.Where(l => l.EndsWith("WRONG", StringComparison.Ordinal))));
        }
        return lines;
    }
}
