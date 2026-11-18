using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic patches of "another mod" for the generic rules on the Limitless parts (DESIGN "Re-verification of the leads
/// (2026-11-18)", rules A, B and L), installed through <c>foreignpatch &lt;shape&gt; &lt;Namespace.Type:Method&gt; [gate] [priority N]</c>
/// (Harmony ids <c>bdtest.foreign.&lt;shape&gt;[-gate]</c>); <c>foreignpatch limitless report &lt;label&gt;</c> prints their counts as
/// STATE lines (compared line for line between the on and off sides), <c>foreignpatch limitless remove</c> removes them.
/// <list type="bullet">
/// <item>Rule A, accepted: <c>lim-result-gated-postfix squeeze</c> on <c>CardModel:ToMutable</c>, a postfix doing nothing unless
/// <c>__result</c> is a Squeeze (Acts from the Past's shape on ToMutable), counting its calls.</item>
/// <item>Rule A, controls (must back off): <c>lim-enchantment-gated-postfix</c> on <c>AbstractModel:MutableClone</c> (gated on
/// an enchantment type: MutableClone also runs on the inner card's enchantment), <c>lim-cardmodel-gated-postfix</c> (gated on
/// <c>is CardModel</c>: every inner card is one), <c>lim-write-then-gated-postfix</c> (a count before the gate's branch).</item>
/// </list>
/// </summary>
public static class ForeignShapesLimitless
{
    public static readonly string[] Shapes =
    {
        "lim-result-gated-postfix", "lim-enchantment-gated-postfix", "lim-cardmodel-gated-postfix", "lim-write-then-gated-postfix",
    };

    private static readonly List<(string Id, MethodBase Target)> _installed = new();

    /// <summary>Whether this file handles that <c>foreignpatch</c> line.</summary>
    public static bool Handles(string[] parts) =>
        parts.Length >= 2 && (parts[0] == "limitless" || Shapes.Contains(parts[0]));

    public static IReadOnlyList<string> Run(string[] parts)
    {
        if (parts[0] == "limitless")
        {
            return parts[1] switch
            {
                "report" => LimitlessShapeCounts.ReportLines(parts.Length > 2 ? parts[2] : "end"),
                "remove" => Remove(),
                _ => throw new ArgumentException("foreignpatch limitless report [label] | remove"),
            };
        }
        string shape = parts[0];
        MethodBase target = Target(parts[1]);
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string gate = parts.Length > 2 && parts[2] != "priority" ? parts[2] : "";
        string id = ForeignPatches.IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "");
        var harmony = new Harmony(id);
        HarmonyMethod Gated(string name) => new(typeof(LimitlessGates), name) { priority = priority };
        string Gate(params string[] known) => known.Contains(gate) ? gate : throw new ArgumentException($"{shape} gate: {string.Join(" or ", known)}");
        switch (shape)
        {
            case "lim-result-gated-postfix":
                Gate("squeeze");
                if (target is not MethodInfo { ReturnType: var returned } || !typeof(CardModel).IsAssignableFrom(returned))
                {
                    throw new ArgumentException(shape + " goes on a method returning a card (CardModel:ToMutable)");
                }
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.ResultGatedPostfix)));
                break;
            case "lim-enchantment-gated-postfix":
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.EnchantmentGatedPostfix)));
                break;
            case "lim-cardmodel-gated-postfix":
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.CardModelGatedPostfix)));
                break;
            case "lim-write-then-gated-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.WriteThenGatedPostfix)));
                break;
            default:
                throw new ArgumentException($"unknown foreignpatch shape '{shape}': {string.Join(", ", Shapes)}");
        }
        lock (_installed)
        {
            _installed.Add((id, target));
        }
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {LimitlessShapeCounts.Name(target)} as {id} (priority {priority})" };
        lines.AddRange(Classification(target));
        return lines;
    }

    /// <summary>A method by <c>Namespace.Type:Method</c>, or any member by its fingerprint key <c>Namespace.Type::Method(ArgTypes)</c>.</summary>
    private static MethodBase Target(string spec) =>
        (spec.Contains("::", StringComparison.Ordinal) ? MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, spec) : AccessTools.Method(spec))
        ?? throw new ArgumentException("no method " + spec + " (Namespace.Type:Method, or Namespace.Type::Method(ArgTypes))");

    private static IReadOnlyList<string> Classification(MethodBase target) =>
        PatchSafety.Describe(target).Select(line => $"FOREIGN classify {LimitlessShapeCounts.Name(target)}: {line}")
            .DefaultIfEmpty($"FOREIGN classify {LimitlessShapeCounts.Name(target)}: no other mod's patch").ToList();

    private static IReadOnlyList<string> Remove()
    {
        (string Id, MethodBase Target)[] installed;
        lock (_installed)
        {
            installed = _installed.ToArray();
            _installed.Clear();
        }
        var lines = new List<string>();
        foreach ((string id, MethodBase target) in installed)
        {
            new Harmony(id).Unpatch(target, HarmonyPatchType.All, id);
            lines.Add($"FOREIGN removed {id} from {LimitlessShapeCounts.Name(target)}");
        }
        return lines.Count > 0 ? lines : new[] { "FOREIGN nothing to remove (limitless shapes)" };
    }
}

/// <summary>
/// The counts of <see cref="LimitlessGates"/>: a class of its own whose initializer only makes the dictionary (a pure one), so
/// the gated shapes' calls into it don't make their gates refused for an initializer.
/// </summary>
internal static class LimitlessShapeCounts
{
    private static readonly ConcurrentDictionary<string, long> _counts = new();

    internal static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    internal static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    internal static IReadOnlyList<string> ReportLines(string label)
    {
        var lines = _counts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"STATE foreign {label} {e.Key} {e.Value}").ToList();
        return lines.Count > 0 ? lines : new[] { $"FOREIGN limitless shapes {label}: nothing counted" };
    }
}

/// <summary>The gated shapes and their near misses, in a class without a static initializer (one would be judged too).</summary>
internal static class LimitlessGates
{
    /// <summary>Rule A, accepted on CardModel.ToMutable: gated on the copy returned (the instance's runtime type).</summary>
    public static void ResultGatedPostfix(CardModel __result, MethodBase __originalMethod)
    {
        if (__result is Squeeze)
        {
            LimitlessShapeCounts.Count("lim-result-gated-postfix squeeze " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Control: gated on an enchantment type, on a method that also runs on the inner card's enchantment.</summary>
    public static void EnchantmentGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
    {
        if (__instance is EnchantmentModel)
        {
            LimitlessShapeCounts.Count("lim-enchantment-gated-postfix " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Control: gated on every card (the inner storage card is one).</summary>
    public static void CardModelGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
    {
        if (__instance is CardModel)
        {
            LimitlessShapeCounts.Count("lim-cardmodel-gated-postfix " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Control: a call (a count) before the gate's branch.</summary>
    public static void WriteThenGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
    {
        LimitlessShapeCounts.Count("lim-write-then-gated-postfix every call");
        if (__instance is Squeeze)
        {
            LimitlessShapeCounts.Count("lim-write-then-gated-postfix squeeze " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }
}
