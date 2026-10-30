using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game formats its texts (card descriptions, tooltips, Limitless's stacked enchantment lines) with SmartFormat,
/// parsing the template each time. SmartFormat 3.3's <c>Placeholder.FormatterOptions</c> creates a new
/// <c>ArrayPool&lt;char&gt;</c> (<c>ArrayPool.Create(1024, 1024)</c>, ~56 KB of bucket arrays) for each new placeholder,
/// rents one buffer from it and drops it: a quarter of everything allocated while cards are played with 4,100
/// Limitless-enchanted cards (the hand's texts are formatted again after every action).
///
/// That call returns the shared pool instead. The buffer only receives the unescaped options (written from its start,
/// then copied into the cached string), so the text is the same; nothing else in the getter changes.
/// </summary>
public static class TextPoolReuse
{
    public const string Name = "TextPoolReuse";

    private const string PlaceholderType = "SmartFormat.Core.Parsing.Placeholder";
    /// <summary>SmartFormat 3.3.0 (game v0.107.1 and v0.111) <c>Placeholder.FormatterOptions</c> getter.</summary>
    private const string FormatterOptionsFingerprint = "8B7803D2AE1BD745";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    private static readonly MethodInfo _create = AccessTools.Method(typeof(ArrayPool<char>), nameof(ArrayPool<char>.Create), new[] { typeof(int), typeof(int) });
    private static bool _replaced;

    public static void Apply(Harmony harmony)
    {
        Type placeholder = AccessTools.TypeByName(PlaceholderType) ?? throw new PatchSkippedException("no " + PlaceholderType);
        MethodInfo getter = AccessTools.PropertyGetter(placeholder, "FormatterOptions") ?? throw new MissingMethodException(PlaceholderType, "get_FormatterOptions");
        PatchSafety.RequireFingerprint(getter, FormatterOptionsFingerprint);
        PatchGuard.Patch(harmony, getter, transpiler: new HarmonyMethod(typeof(TextPoolReuse), nameof(Transpiler)));
        if (!_replaced)
        {
            throw new PatchSkippedException("FormatterOptions: ArrayPool.Create call not found");
        }
        Applied = true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        bool replaced = false; // Harmony runs transpilers again each time the method is re-patched.
        foreach (CodeInstruction instruction in instructions)
        {
            if (!replaced && instruction.Calls(_create))
            {
                replaced = true;
                _replaced = true;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TextPoolReuse), nameof(Pool)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    /// <summary>In place of <c>ArrayPool&lt;char&gt;.Create(maxArrayLength, maxArraysPerBucket)</c>.</summary>
    private static ArrayPool<char> Pool(int maxArrayLength, int maxArraysPerBucket) =>
        Enabled ? ArrayPool<char>.Shared : ArrayPool<char>.Create(maxArrayLength, maxArraysPerBucket);
}
