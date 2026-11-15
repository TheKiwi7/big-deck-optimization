using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic patches of "another mod" (Harmony ids <c>bdtest.foreign.&lt;shape&gt;</c>) for the audit of
/// <see cref="PatchTolerance"/>: script command <c>foreignpatch</c> and console <c>bdperf foreign</c>. Each install logs what it
/// put where and <see cref="PatchSafety.Describe"/>'s verdict on every other mod's patch of that method, so a scenario can
/// <c>expect</c> it; the optimizations' own runtime checks then log "kept" (tolerated) or "normal game for that part".
/// <list type="bullet">
/// <item>Tolerated around the original: <c>noop-postfix</c>, <c>noop-prefix</c> (void, nothing by reference), <c>count-postfix</c> and
/// <c>count-prefix</c> (count their calls per target: the same with this mod on or off, printed as STATE lines by
/// <c>foreignpatch report</c>, which compare.py compares), <c>nested-postfix</c> (on <c>CardPile.AddInternal</c>: a card added to a
/// discard pile also moves the draw pile's last card there through RemoveInternal/AddInternal, a nested pile change).</item>
/// <item>Must back off (positive controls): <c>bool-prefix</c> (returns true), <c>ref-prefix</c> (<c>ref __result</c>, or
/// <c>ref __runOriginal</c> on a void method), <c>identity-transpiler</c>, <c>inner-postfix</c> (classified only: Harmony 2.4.2 has no
/// public way to give a patch its inner method, so one can't be installed; a synthetic <see cref="HarmonyLib.Patches"/> is classified).</item>
/// <item>Type gates on an <c>AbstractModel</c> default (TypeGated): <c>gated-postfix</c>, <c>gated-prefix</c> (bool, true) and
/// <c>gated-or-postfix</c> with gate <c>squeeze</c> (the card <c>Squeeze</c>, which overrides no hook), <c>relic</c> (RelicModel) or
/// <c>squeeze-relic</c> (or-ed, gated-or-postfix only): counting their gated calls; <c>nearmiss-postfix</c> counts every call before
/// the same test (must not be recognized).</item>
/// </list>
/// Arguments: <c>foreignpatch &lt;shape&gt; &lt;Namespace.Type:Method&gt; [gate] [priority N]</c>; <c>foreignpatch remove</c> takes every
/// one away; <c>foreignpatch report [label]</c>; <c>foreignpatch require tolerated</c> fails unless a check let a patch stand.
/// </summary>
public static class ForeignPatches
{
    public const string IdPrefix = "bdtest.foreign.";

    private static readonly List<(string Id, MethodBase Target)> _installed = new();

    /// <summary>Calls counted per (shape, target): what both sides of an A/B must agree on.</summary>
    private static readonly ConcurrentDictionary<string, long> _counts = new();

    private static readonly string[] Shapes =
    {
        "noop-postfix", "noop-prefix", "count-postfix", "count-prefix", "nested-postfix",
        "bool-prefix", "ref-prefix", "identity-transpiler", "inner-postfix",
        "gated-postfix", "gated-prefix", "gated-or-postfix", "nearmiss-postfix",
    };

    /// <summary>Runs one <c>foreignpatch</c> line; returns the lines to log.</summary>
    public static IReadOnlyList<string> Run(string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("foreignpatch <shape> <Type:Method> [gate] [priority N] | remove | report [label] | require tolerated | classify <Type:Method>; shapes: " + string.Join(", ", Shapes));
        }
        switch (parts[0])
        {
            case "remove":
                return Remove();
            case "report":
                return Report(parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "");
            case "require" when parts.Length > 1 && parts[1] == "tolerated":
#if BENCH
                if (PatchSafety.ToleratedChecks == 0)
                {
                    throw new InvalidOperationException("no check let another mod's patch stand (PatchSafety.ToleratedChecks is 0)");
                }
                return new[] { $"FOREIGN tolerated checks {PatchSafety.ToleratedChecks}" };
#else
                return Array.Empty<string>();
#endif
            case "classify" when parts.Length > 1:
                MethodBase classified = Target(parts[1]);
                return Classification(classified);
        }
        if (!Shapes.Contains(parts[0]) || parts.Length < 2)
        {
            throw new ArgumentException($"unknown foreignpatch shape '{parts[0]}' (or no target): {string.Join(", ", Shapes)}");
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
        if (shape == "inner-postfix")
        {
            return ClassifyInner(target);
        }
        string id = IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "");
        var harmony = new Harmony(id);
        HarmonyMethod Fix(string name) => new(typeof(ForeignPatches), name) { priority = priority };
        switch (shape)
        {
            case "noop-postfix":
                harmony.Patch(target, postfix: Fix(nameof(NoOpPostfix)));
                break;
            case "noop-prefix":
                harmony.Patch(target, prefix: Fix(nameof(NoOpPrefix)));
                break;
            case "count-postfix":
                harmony.Patch(target, postfix: Fix(nameof(CountPostfix)));
                break;
            case "count-prefix":
                harmony.Patch(target, prefix: Fix(nameof(CountPrefix)));
                break;
            case "nested-postfix":
                if (target.DeclaringType != typeof(CardPile) || target.Name != nameof(CardPile.AddInternal))
                {
                    throw new ArgumentException("nested-postfix goes on CardPile:AddInternal");
                }
                harmony.Patch(target, postfix: Fix(nameof(NestedPostfix)));
                break;
            case "bool-prefix":
                harmony.Patch(target, prefix: Fix(nameof(TruePrefix)));
                break;
            case "ref-prefix":
                harmony.Patch(target, prefix: Fix(RefPrefixFor(target)));
                break;
            case "identity-transpiler":
                harmony.Patch(target, transpiler: Fix(nameof(IdentityTranspiler)));
                break;
            case "gated-postfix":
                harmony.Patch(target, postfix: Fix(gate switch
                {
                    "squeeze" => nameof(SqueezeGatedPostfix),
                    "relic" => nameof(RelicGatedPostfix),
                    _ => throw new ArgumentException("gated-postfix gate: squeeze or relic"),
                }));
                break;
            case "gated-prefix":
                harmony.Patch(target, prefix: Fix(gate switch
                {
                    "squeeze" => nameof(SqueezeGatedPrefix),
                    _ => throw new ArgumentException("gated-prefix gate: squeeze"),
                }));
                break;
            case "gated-or-postfix":
                harmony.Patch(target, postfix: Fix(gate switch
                {
                    "squeeze-relic" => nameof(SqueezeOrRelicGatedPostfix),
                    _ => throw new ArgumentException("gated-or-postfix gate: squeeze-relic"),
                }));
                break;
            case "nearmiss-postfix":
                harmony.Patch(target, postfix: Fix(gate switch
                {
                    "squeeze" => nameof(NearMissPostfix),
                    _ => throw new ArgumentException("nearmiss-postfix gate: squeeze"),
                }));
                break;
        }
        lock (_installed)
        {
            _installed.Add((id, target));
        }
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {Name(target)} as {id} (priority {priority})" };
        lines.AddRange(Classification(target));
        return lines;
    }

    private static MethodBase Target(string spec) =>
        AccessTools.Method(spec) ?? throw new ArgumentException("no method " + spec + " (Namespace.Type:Method)");

    private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    /// <summary>PatchSafety's verdict on every other mod's patch of the method (one line each).</summary>
    private static IReadOnlyList<string> Classification(MethodBase target) =>
        PatchSafety.Describe(target).Select(line => $"FOREIGN classify {Name(target)}: {line}").DefaultIfEmpty($"FOREIGN classify {Name(target)}: no other mod's patch").ToList();

    /// <summary>
    /// An inner postfix can't be installed with Harmony 2.4.2 (nothing sets a patch's inner method): this classifies a synthetic
    /// one next to the method's real patches, as PatchSafety would see it.
    /// </summary>
    private static IReadOnlyList<string> ClassifyInner(MethodBase target)
    {
        MethodInfo fix = AccessTools.Method(typeof(ForeignPatches), nameof(NoOpPostfix));
        var patch = new Patch(fix, 0, IdPrefix + "inner-postfix", Priority.Normal, null, null, false);
        var info = new HarmonyLib.Patches(Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), new[] { patch });
        string surrounding = PatchSafety.Intolerable(target, info, patch, PatchSafety.PatchKind.InnerPostfix, PatchTolerance.SurroundingOnly, null) ?? "tolerated";
        string gated = PatchSafety.Intolerable(target, info, patch, PatchSafety.PatchKind.InnerPostfix, PatchTolerance.TypeGated, new List<Type>()) ?? "tolerated";
        return new[] { $"FOREIGN classify {Name(target)}: {IdPrefix}inner-postfix InnerPostfix (synthetic): SurroundingOnly {(surrounding == "tolerated" ? "tolerated" : "backs off (" + surrounding + ")")}; TypeGated {(gated == "tolerated" ? "tolerated" : "backs off (" + gated + ")")}" };
    }

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
            lines.Add($"FOREIGN removed {id} from {Name(target)}");
        }
        return lines.Count > 0 ? lines : new[] { "FOREIGN nothing to remove" };
    }

    /// <summary>
    /// The counts as STATE lines (compare.py compares them line for line between the on and off sides), then the tolerated
    /// checks and what was let stand (not compared: they differ with the mod off).
    /// </summary>
    private static IReadOnlyList<string> Report(string label)
    {
        var lines = _counts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"STATE foreign {label} {e.Key} {e.Value}").ToList();
#if BENCH
        lines.Add($"FOREIGN tolerated checks {PatchSafety.ToleratedChecks}, gate analyses {PatchSafety.GateAnalyses} ({PatchSafety.GatedMethods} gated)");
#endif
        lines.AddRange(PatchSafety.ToleratedReport().Select(t => "FOREIGN tolerated " + t));
        return lines;
    }

    private static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    // --- The patches. Their IL is what PatchSafety classifies: keep each one's shape as described. ---

    private static void NoOpPostfix()
    {
    }

    private static void NoOpPrefix()
    {
    }

    private static void CountPostfix(MethodBase __originalMethod) => Count("count-postfix " + Name(__originalMethod));

    private static void CountPrefix(MethodBase __originalMethod) => Count("count-prefix " + Name(__originalMethod));

    [ThreadStatic] private static bool _nesting;

    /// <summary>A card added to a discard pile takes the draw pile's last card with it (a nested remove and add, game methods).</summary>
    private static void NestedPostfix(CardPile __instance, CardModel card)
    {
        if (_nesting || __instance.Type != PileType.Discard || card.Owner == null)
        {
            return;
        }
        CardPile? draw = CardPile.Get(PileType.Draw, card.Owner);
        if (draw == null || draw.Cards.Count < 2)
        {
            return;
        }
        CardModel moved = draw.Cards[draw.Cards.Count - 1];
        _nesting = true;
        try
        {
            draw.RemoveInternal(moved);
            __instance.AddInternal(moved);
            Count("nested-postfix moves");
        }
        finally
        {
            _nesting = false;
        }
    }

    private static bool TruePrefix() => true;

    private static string RefPrefixFor(MethodBase target)
    {
        Type returned = target is MethodInfo m ? m.ReturnType : typeof(void);
        return returned == typeof(void) ? nameof(RunOriginalPrefix)
            : returned == typeof(bool) ? nameof(RefBoolResultPrefix)
            : returned == typeof(Task) ? nameof(RefTaskResultPrefix)
            : returned.IsValueType ? nameof(RefObjectResultPrefix)
            : throw new ArgumentException($"ref-prefix: {Name(target)} returns {returned.Name} (void, bool, Task or a value type)");
    }

    private static void RunOriginalPrefix(ref bool __runOriginal)
    {
    }

    private static void RefBoolResultPrefix(ref bool __result)
    {
    }

    private static void RefTaskResultPrefix(ref Task __result)
    {
    }

    private static void RefObjectResultPrefix(ref object __result)
    {
    }

    private static IEnumerable<CodeInstruction> IdentityTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;

    private static void SqueezeGatedPostfix(AbstractModel __instance)
    {
        if (__instance is Squeeze)
        {
            Count("gated-postfix squeeze");
        }
    }

    private static void RelicGatedPostfix(AbstractModel __instance)
    {
        if (__instance is RelicModel)
        {
            Count("gated-postfix relic");
        }
    }

    private static bool SqueezeGatedPrefix(AbstractModel __instance)
    {
        if (!(__instance is Squeeze))
        {
            return true;
        }
        Count("gated-prefix squeeze");
        return true;
    }

    private static void SqueezeOrRelicGatedPostfix(AbstractModel __instance)
    {
        if (__instance is Squeeze || __instance is RelicModel)
        {
            Count("gated-or-postfix squeeze-relic");
        }
    }

    private static long _nearMissCalls;

    /// <summary>A static write before the test: it runs for every instance, so it isn't gated.</summary>
    private static void NearMissPostfix(AbstractModel __instance)
    {
        _nearMissCalls++;
        if (__instance is Squeeze)
        {
            Count("nearmiss-postfix squeeze");
        }
    }
}
