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
/// put where and <see cref="PatchSafety.Describe(MethodBase)"/>'s verdict on every other mod's patch of that method, so a scenario
/// can <c>expect</c> it; the optimizations' own runtime checks then log "kept" (tolerated) or "normal game for that part", and
/// <c>foreignpatch report</c> prints what only the runtime produces (<c>FOREIGN runtime</c>: the checks per call site, the preflight
/// not counted; <c>FOREIGN gated walks</c>: HookSkip's walks filtered with gates).
/// <list type="bullet">
/// <item>Tolerated around the original: <c>noop-postfix</c>, <c>noop-prefix</c> (void, nothing by reference), <c>count-postfix</c> and
/// <c>count-prefix</c> (count their calls per target: the same with this mod on or off, printed as STATE lines by
/// <c>foreignpatch report</c>, which compare.py compares), <c>nested-postfix</c> (on <c>CardPile.AddInternal</c>: a card added to a
/// discard pile also moves the draw pile's last card there through RemoveInternal/AddInternal, a nested pile change).
/// With an effect the checksums see: <c>keyword-prefix</c> (void, on <c>Hook.ModifyKeywordsInCombat</c>: Squeeze gets Retain in the
/// set the hook fills; HookFilter's mirror runs after it, SnapshotCardReuse's local keywords would miss it), <c>cost-postfix</c>
/// (<c>ref __result</c> on <c>Hook.ModifyEnergyCostInCombat</c>: Squeeze costs 1 more; HookSkip lets it stand, HookFilter and
/// SnapshotCardReuse back off), <c>probe-prefix</c>/<c>probe-postfix</c> (on <c>CardPile.AddInternal</c>: the added card's cost read
/// before and after the add, a hook walk over the cached piles mid-move, summed as STATE lines), <c>state-prefix</c> (on
/// <c>CardPile.AddInternal</c>: a void prefix taking <c>ref __state</c> and its postfix, SynergyMod's shape, the pile's growth summed).</item>
/// <item>Must back off (positive controls): <c>bool-prefix</c> (returns true), <c>ref-prefix</c> (<c>ref __result</c>, or
/// <c>ref __runOriginal</c> on a void method), <c>identity-transpiler</c>, <c>inner-postfix</c> (classified only: Harmony 2.4.2 can
/// register one through PatchProcessor.AddInnerPostfix but nothing gives it its inner method, so Harmony can't build it; a
/// synthetic <see cref="HarmonyLib.Patches"/> goes through <see cref="PatchSafety.Describe(MethodBase, HarmonyLib.Patches)"/>),
/// <c>runoriginal-postfix</c> (reads <c>__runOriginal</c>), <c>byref-postfix</c> (<c>ref CardModel card</c>, on a CardPile method),
/// <c>argsrename-prefix</c> (<c>[HarmonyArgument("__args")] object[]</c>), <c>factory-postfix</c> (a factory method returning the
/// postfix), <c>dup-prefix</c> (one void prefix registered twice: priority 800, then the default priority).</item>
/// <item>Type gates on an <c>AbstractModel</c> default (TypeGated): <c>gated-postfix</c>, <c>gated-prefix</c> (bool, true) and
/// <c>gated-or-postfix</c> with gate <c>squeeze</c> (the card <c>Squeeze</c>, which overrides no hook), <c>relic</c> (RelicModel) or
/// <c>squeeze-relic</c> (or-ed, gated-or-postfix only): counting their gated calls per target. Near misses (must not be
/// recognized): <c>nearmiss-postfix</c> (a static write before the test; every call counted, a STATE line),
/// <c>renamed-gated-postfix</c> (the gate read from <c>[HarmonyArgument("card")] __instance</c>, on a default taking a card),
/// <c>gated-patched-postfix</c> (a gated postfix whose own method gets a no-op postfix), <c>cctor-gated-postfix</c> (a gated postfix
/// in a class with a static constructor).</item>
/// </list>
/// Arguments: <c>foreignpatch &lt;shape&gt; &lt;Namespace.Type:Method&gt; [gate] [priority N]</c>; <c>foreignpatch remove</c> takes every
/// one away; <c>foreignpatch report [label]</c>; <c>foreignpatch require tolerated</c> fails unless a check let a patch stand.
/// </summary>
public static class ForeignPatches
{
    public const string IdPrefix = "bdtest.foreign.";

    private static readonly List<(string Id, MethodBase Target)> _installed = new();

    /// <summary>Calls counted (or values summed) per key: what both sides of an A/B must agree on.</summary>
    private static readonly ConcurrentDictionary<string, long> _counts = new();

    private static readonly string[] Shapes =
    {
        "noop-postfix", "noop-prefix", "count-postfix", "count-prefix", "nested-postfix", "keyword-prefix", "cost-postfix",
        "probe-prefix", "probe-postfix", "state-prefix",
        "bool-prefix", "ref-prefix", "identity-transpiler", "inner-postfix", "runoriginal-postfix", "byref-postfix",
        "argsrename-prefix", "factory-postfix", "dup-prefix",
        "gated-postfix", "gated-prefix", "gated-or-postfix", "nearmiss-postfix", "renamed-gated-postfix", "gated-patched-postfix",
        "cctor-gated-postfix",
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
        HarmonyMethod Gated(string name) => new(typeof(Gates), name) { priority = priority };
        var extra = new List<(string Id, MethodBase Target)>();
        string Gate(params string[] known) => known.Contains(gate) ? gate : throw new ArgumentException($"{shape} gate: {string.Join(" or ", known)}");
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
                RequirePileMethod(target, nameof(CardPile.AddInternal));
                harmony.Patch(target, postfix: Fix(nameof(NestedPostfix)));
                break;
            case "keyword-prefix":
                Gate("squeeze");
                harmony.Patch(target, prefix: Fix(nameof(SqueezeKeywordPrefix)));
                break;
            case "cost-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: Fix(nameof(SqueezeCostPostfix)));
                break;
            case "probe-prefix":
                RequirePileMethod(target, nameof(CardPile.AddInternal));
                harmony.Patch(target, prefix: Fix(nameof(ProbePrefix)));
                break;
            case "state-prefix":
                // SynergyMod's shape: a void prefix taking __state by reference, read by its own postfix.
                RequirePileMethod(target, nameof(CardPile.AddInternal));
                harmony.Patch(target, prefix: Fix(nameof(StatePrefix)), postfix: Fix(nameof(StatePostfix)));
                break;
            case "probe-postfix":
                RequirePileMethod(target, nameof(CardPile.AddInternal));
                harmony.Patch(target, postfix: Fix(nameof(ProbePostfix)));
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
            case "runoriginal-postfix":
                harmony.Patch(target, postfix: Fix(nameof(RunOriginalPostfix)));
                break;
            case "byref-postfix":
                if (target.DeclaringType != typeof(CardPile))
                {
                    throw new ArgumentException("byref-postfix goes on a CardPile method taking a card");
                }
                harmony.Patch(target, postfix: Fix(nameof(ByRefCardPostfix)));
                break;
            case "argsrename-prefix":
                harmony.Patch(target, prefix: Fix(nameof(RenamedArgsPrefix)));
                break;
            case "factory-postfix":
                harmony.Patch(target, postfix: Fix(nameof(FactoryPostfix)));
                break;
            case "dup-prefix":
                // The same patch method twice (PatchInfo.Add doesn't deduplicate): before this mod's prefixes, then after them.
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(DupPrefix)) { priority = Priority.First });
                var second = new Harmony(id + ".again");
                second.Patch(target, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(DupPrefix)) { priority = Priority.Normal });
                extra.Add((id + ".again", target));
                break;
            case "gated-postfix":
                harmony.Patch(target, postfix: Gated(Gate("squeeze", "relic") == "squeeze" ? nameof(Gates.SqueezeGatedPostfix) : nameof(Gates.RelicGatedPostfix)));
                break;
            case "gated-prefix":
                Gate("squeeze");
                harmony.Patch(target, prefix: Gated(nameof(Gates.SqueezeGatedPrefix)));
                break;
            case "gated-or-postfix":
                Gate("squeeze-relic");
                harmony.Patch(target, postfix: Gated(nameof(Gates.SqueezeOrRelicGatedPostfix)));
                break;
            case "nearmiss-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: Gated(nameof(Gates.NearMissPostfix)));
                break;
            case "renamed-gated-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: Gated(nameof(Gates.RenamedGatedPostfix)));
                break;
            case "gated-patched-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: Gated(nameof(Gates.PatchedGatePostfix)));
                // Another "mod" patches that gated postfix itself: the code that runs is its replacement, not the IL read.
                MethodInfo gated = AccessTools.Method(typeof(Gates), nameof(Gates.PatchedGatePostfix));
                var onGate = new Harmony(id + ".ongate");
                onGate.Patch(gated, postfix: new HarmonyMethod(typeof(ForeignPatches), nameof(NoOpPostfix)));
                extra.Add((id + ".ongate", gated));
                break;
            case "cctor-gated-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CctorGate), nameof(CctorGate.Postfix)) { priority = priority });
                break;
        }
        lock (_installed)
        {
            _installed.Add((id, target));
            _installed.AddRange(extra);
        }
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {Name(target)} as {id} (priority {priority})" };
        lines.AddRange(Classification(target));
        return lines;
    }

    private static void RequirePileMethod(MethodBase target, string method)
    {
        if (target.DeclaringType != typeof(CardPile) || target.Name != method)
        {
            throw new ArgumentException($"this shape goes on CardPile:{method}");
        }
    }

    private static MethodBase Target(string spec) =>
        AccessTools.Method(spec) ?? throw new ArgumentException("no method " + spec + " (Namespace.Type:Method)");

    private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    /// <summary>PatchSafety's verdict on every other mod's patch of the method (one line each).</summary>
    private static IReadOnlyList<string> Classification(MethodBase target) =>
        PatchSafety.Describe(target).Select(line => $"FOREIGN classify {Name(target)}: {line}").DefaultIfEmpty($"FOREIGN classify {Name(target)}: no other mod's patch").ToList();

    /// <summary>
    /// An inner postfix can be registered (PatchProcessor.AddInnerPostfix) but not built by Harmony 2.4.2: nothing gives it its inner
    /// method. This classifies a synthetic one through PatchSafety's own listing of a patch list (every kind's list, as for a real
    /// method), next to nothing else.
    /// </summary>
    private static IReadOnlyList<string> ClassifyInner(MethodBase target)
    {
        MethodInfo fix = AccessTools.Method(typeof(ForeignPatches), nameof(NoOpPostfix));
        var patch = new Patch(fix, 0, IdPrefix + "inner-postfix", Priority.Normal, null, null, false);
        var info = new HarmonyLib.Patches(Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), Array.Empty<Patch>(), new[] { patch });
        IReadOnlyList<string> lines = PatchSafety.Describe(target, info);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("PatchSafety.Describe listed no patch for a synthetic inner postfix: the inner postfixes aren't read");
        }
        return lines.Select(line => $"FOREIGN classify {Name(target)} (synthetic): {line}").ToList();
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
    /// checks, what was let stand, and what only the runtime produces (not compared: they differ with the mod off).
    /// </summary>
    private static IReadOnlyList<string> Report(string label)
    {
        var lines = _counts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"STATE foreign {label} {e.Key} {e.Value}").ToList();
        if (Gates.NearMissCalls > 0)
        {
            lines.Add($"STATE foreign {label} nearmiss-postfix every call {Gates.NearMissCalls}");
        }
#if BENCH
        lines.Add($"FOREIGN tolerated checks {PatchSafety.ToleratedChecks}, gate analyses {PatchSafety.GateAnalyses} ({PatchSafety.GatedMethods} gated)");
        lines.AddRange(PatchSafety.RuntimeReport().Select(t => "FOREIGN runtime " + t));
        lines.AddRange(HookSkip.GatedWalkReport().Select(t => "FOREIGN gated walks " + t));
#endif
        lines.AddRange(PatchSafety.ToleratedReport().Select(t => "FOREIGN tolerated " + t));
        return lines;
    }

    private static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    private static void Add(string key, long value) => _counts.AddOrUpdate(key, value, (_, n) => n + value);

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

    /// <summary>Void, by value: Squeeze gets Retain in the set the hook fills (before HookFilter's mirror, as before the original).</summary>
    private static void SqueezeKeywordPrefix(CardModel card, ISet<CardKeyword> keywords)
    {
        if (card is Squeeze)
        {
            keywords.Add(CardKeyword.Retain);
        }
    }

    /// <summary>A postfix changing the result (<c>ref __result</c>): Squeeze costs 1 more in combat.</summary>
    private static void SqueezeCostPostfix(CardModel card, ref decimal __result)
    {
        if (card is Squeeze)
        {
            __result += 1;
        }
    }

    /// <summary>The pile's count before the add, for this patch's own postfix (<c>ref __state</c>, as SynergyMod's AddInternal prefix).</summary>
    private static void StatePrefix(CardPile __instance, ref int __state) => __state = __instance.Cards.Count;

    /// <summary>How much the pile grew over the call (nested moves included), summed: the same with this mod on or off.</summary>
    private static void StatePostfix(CardPile __instance, int __state) => Add("state-prefix growth", __instance.Cards.Count - __state);

    /// <summary>The added card's cost, read before the add: a hook walk over the piles from a prefix of another mod.</summary>
    private static void ProbePrefix(CardModel card) => Probe("probe-prefix", card);

    /// <summary>The same read after the add (and after this mod's tracking, before its finalizers).</summary>
    private static void ProbePostfix(CardModel card) => Probe("probe-postfix", card);

    private static void Probe(string key, CardModel card)
    {
        if (card.CombatState == null)
        {
            return;
        }
        try
        {
            Add(key + " cost", card.EnergyCost.GetWithModifiers(CostModifiers.All));
            Count(key + " reads");
        }
        catch (Exception e)
        {
            Count($"{key} errors {e.GetType().Name}");
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

    /// <summary>The Harmony idiom "only when the original ran": false behind a prefix of this mod that skipped it.</summary>
    private static void RunOriginalPostfix(bool __runOriginal)
    {
        if (__runOriginal)
        {
            Count("runoriginal-postfix ran");
        }
    }

    /// <summary>A card argument taken by reference (the replacement's own argument slot, read by this mod's finalizers).</summary>
    private static void ByRefCardPostfix(ref CardModel card)
    {
    }

    /// <summary>Looks harmless under its C# name; Harmony binds it as __args and writes the array back.</summary>
    private static void RenamedArgsPrefix([HarmonyArgument("__args")] object[] values)
    {
    }

    /// <summary>A Harmony factory: Harmony calls it with the original and uses the postfix it returns.</summary>
    private static MethodInfo FactoryPostfix(MethodBase original) => AccessTools.Method(typeof(ForeignPatches), nameof(NoOpPostfix));

    private static void DupPrefix()
    {
    }

    /// <summary>
    /// The type-gated shapes and their near misses, in a class without a static constructor (one with it is refused: see
    /// <see cref="CctorGate"/>), so each near miss is refused for its own reason only. Counting goes through the outer class.
    /// </summary>
    private static class Gates
    {
        /// <summary>No initializer (none would make a static constructor).</summary>
        public static long NearMissCalls;

        public static void SqueezeGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
        {
            if (__instance is Squeeze)
            {
                Count("gated-postfix squeeze " + Name(__originalMethod));
            }
        }

        public static void RelicGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
        {
            if (__instance is RelicModel)
            {
                Count("gated-postfix relic " + Name(__originalMethod));
            }
        }

        public static bool SqueezeGatedPrefix(AbstractModel __instance, MethodBase __originalMethod)
        {
            if (!(__instance is Squeeze))
            {
                return true;
            }
            Count("gated-prefix squeeze " + Name(__originalMethod));
            return true;
        }

        public static void SqueezeOrRelicGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
        {
            if (__instance is Squeeze || __instance is RelicModel)
            {
                Count("gated-or-postfix squeeze-relic " + Name(__originalMethod));
            }
        }

        /// <summary>A static write before the test: it runs for every instance, so it isn't gated (every call counted, a STATE line).</summary>
        public static void NearMissPostfix(AbstractModel __instance)
        {
            NearMissCalls++;
            if (__instance is Squeeze)
            {
                Count("nearmiss-postfix squeeze");
            }
        }

        /// <summary>The gated shape, but its "__instance" is bound to the original's card argument: the gate tests the card.</summary>
        public static void RenamedGatedPostfix([HarmonyArgument("card")] AbstractModel __instance)
        {
            if (__instance is Squeeze)
            {
                Count("renamed-gated-postfix squeeze");
            }
        }

        /// <summary>The gated shape; another "mod" patches this very method (gated-patched-postfix).</summary>
        public static void PatchedGatePostfix(AbstractModel __instance)
        {
            if (__instance is Squeeze)
            {
                Count("gated-patched-postfix squeeze");
            }
        }
    }

    /// <summary>The gated shape in a class with a static constructor: its first call runs it.</summary>
    private static class CctorGate
    {
        private static readonly long Started;

        static CctorGate()
        {
            Started = Environment.TickCount64;
            Count("cctor-gated-postfix class initialized");
        }

        public static void Postfix(AbstractModel __instance)
        {
            if (__instance is Squeeze)
            {
                Count("cctor-gated-postfix squeeze " + (Started != 0));
            }
        }
    }
}
