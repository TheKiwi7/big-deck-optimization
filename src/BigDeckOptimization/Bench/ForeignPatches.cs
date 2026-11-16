using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

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
/// <c>field-postfix</c> (<c>List&lt;CardModel&gt; ____cards</c>, Harmony's field injection: the pile's own list, on a CardPile method),
/// <c>argsrename-prefix</c> (<c>[HarmonyArgument("__args")] object[]</c>), <c>factory-postfix</c> (a factory method returning the
/// postfix), <c>dup-prefix</c> (one void prefix registered twice: priority 800, then the default priority).</item>
/// <item>Needless back-offs made safe (2026-11-16, "Needless back-offs made safe" in DESIGN): <c>walk-expand-postfix</c> (on
/// <c>CombatState</c>/<c>RunState.IterateHookListeners</c>, RitsuLib's shape: a lazy wrapper inserting a second copy of each Squeeze
/// after it, Squeeze reacting to no hook; a second application on its own output is counted, <c>foreignpatch require nodouble</c>),
/// <c>walk-filter-repeat</c> (the same walks, EnchantLib's shape: the stream snapshotted, Squeezes moved to its end),
/// <c>pile-prefix-narrow</c> (on <c>CardModel.Pile</c>, RitsuLib's extra hand: a bool prefix answering Hand for one card while
/// <c>foreignpatch narrow</c> sets it, true otherwise; priority 800 sorts it before this mod's prefix as a mod patching at its
/// load), <c>piles-postfix</c> (on <c>Player.Piles</c>, RitsuLib's mod piles: a pile of this tooling appended, filled by
/// <c>foreignpatch pilecard</c>), <c>byref-arg-prefix</c> (a void prefix taking an argument by reference: on
/// <c>Hook.ModifyHpLost</c> a monster loses 1 HP more, on <c>CardPile.AddInternal</c> the card passed through unchanged),
/// <c>byref-out-postfix</c> (on <c>Hook.ModifyHpLost</c>: <c>ref</c> to the out parameter <c>modifiers</c>, only read, their count
/// summed), and their controls <c>byref-write-postfix</c> (the same, writing it back: must back off).</item>
/// <item>Type gates on an <c>AbstractModel</c> default (TypeGated): <c>gated-postfix</c>, <c>gated-prefix</c> (bool, true) and
/// <c>gated-or-postfix</c> with gate <c>squeeze</c> (the card <c>Squeeze</c>, which overrides no hook), <c>relic</c> (RelicModel) or
/// <c>squeeze-relic</c> (or-ed, gated-or-postfix only): counting their gated calls per target. Near misses (must not be
/// recognized): <c>nearmiss-postfix</c> (a static write before the test; every call counted, a STATE line),
/// <c>renamed-gated-postfix</c> (the gate read from <c>[HarmonyArgument("card")] __instance</c>, on a default taking a card),
/// <c>gated-patched-postfix</c> (a gated postfix whose own method gets a no-op postfix), <c>cctor-gated-postfix</c> (a gated postfix
/// in a class with a static constructor).</item>
/// </list>
/// Arguments: <c>foreignpatch &lt;shape&gt; &lt;Namespace.Type:Method&gt; [gate] [priority N]</c>; <c>foreignpatch remove</c> takes every
/// one away; <c>foreignpatch report [label]</c>; <c>foreignpatch require tolerated</c> fails unless a check let a patch stand;
/// <c>foreignpatch modname &lt;assembly&gt;</c> prints the mod name back-off lines give that assembly. Shapes needed before this mod
/// applies (Harmony's own lookups intercepted): <see cref="ForeignLaunch"/>.
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
        "bool-prefix", "ref-prefix", "identity-transpiler", "inner-postfix", "runoriginal-postfix", "byref-postfix", "field-postfix",
        "argsrename-prefix", "factory-postfix", "dup-prefix",
        "gated-postfix", "gated-prefix", "gated-or-postfix", "nearmiss-postfix", "renamed-gated-postfix", "gated-patched-postfix",
        "cctor-gated-postfix",
        "walk-expand-postfix", "walk-filter-repeat", "pile-prefix-narrow", "piles-postfix", "byref-arg-prefix", "byref-out-postfix",
        "byref-write-postfix",
    };

    /// <summary>
    /// The shapes whose patch changes game state on the peer that installs it (Squeeze's keywords or cost, cards moved between
    /// piles): in co-op every peer must install them, before any combat (AutoTest refuses them later; bdperf foreign refuses them).
    /// </summary>
    public static bool ChangesState(string arg) =>
        arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is "keyword-prefix" or "cost-postfix" or "nested-postfix"
            or "byref-arg-prefix" or "pilecard";

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
            case "require" when parts.Length > 1 && parts[1] == "nodouble":
                // A walk postfix let stand runs once per call on the unpatched stream: never on its own output (a list kept with it).
                if (_counts.TryGetValue("walk-expand-postfix applied twice", out long twice) && twice > 0)
                {
                    throw new InvalidOperationException($"walk-expand-postfix ran on its own output {twice} time(s): a cached list held another mod's postfix output");
                }
                return new[] { "FOREIGN walk-expand-postfix never ran on its own output" };
            case "narrow":
                return Narrow(parts.Length > 1 ? parts[1] : "");
            case "pilecard":
                return PileCard(parts.Length > 1 ? parts[1] : "");
            case "classify" when parts.Length > 1:
                MethodBase classified = Target(parts[1]);
                return Classification(classified);
            case "modname" when parts.Length > 1:
                // The name back-off lines give a loaded assembly's mod, and whether the game's own mod entry gave it.
                Assembly? named = PatchSafety.LoadedAssembly(parts[1]);
                return new[] { $"FOREIGN modname {parts[1]}: " + (named == null ? "not loaded"
                    : $"{PatchSafety.ModNameOf(named) ?? "none"} ({(PatchSafety.ModEntryNameOf(named) != null ? "mod entry" : "no mod entry")})") };
        }
        // Transpilers on the Hook methods HookSkip filters, judged by their IL (IlJudgeShapes.cs).
        if (parts[0] == "iljudge")
        {
            return IlJudgeShapes.Command(parts.Skip(1).ToArray());
        }
        if (IlJudgeShapes.Shapes.Contains(parts[0]) && parts.Length >= 2)
        {
            return InstallIl(parts);
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
            case "field-postfix":
                if (target.DeclaringType != typeof(CardPile))
                {
                    throw new ArgumentException("field-postfix goes on a CardPile method");
                }
                harmony.Patch(target, postfix: Fix(nameof(CardsFieldPostfix)));
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
            case "walk-expand-postfix":
                RequireWalk(target);
                harmony.Patch(target, postfix: Fix(target.DeclaringType == typeof(MegaCrit.Sts2.Core.Runs.RunState) ? nameof(WalkExpandRunPostfix) : nameof(WalkExpandPostfix)));
                break;
            case "walk-filter-repeat":
                RequireWalk(target);
                harmony.Patch(target, postfix: Fix(nameof(WalkFilterRepeatPostfix)));
                break;
            case "pile-prefix-narrow":
                if (target.DeclaringType != typeof(CardModel) || target.Name != "get_Pile")
                {
                    throw new ArgumentException("pile-prefix-narrow goes on MegaCrit.Sts2.Core.Models.CardModel:get_Pile");
                }
                harmony.Patch(target, prefix: Fix(nameof(NarrowPilePrefix)));
                break;
            case "piles-postfix":
                if (target.DeclaringType != typeof(Player) || target.Name != "get_Piles")
                {
                    throw new ArgumentException("piles-postfix goes on MegaCrit.Sts2.Core.Entities.Players.Player:get_Piles");
                }
                harmony.Patch(target, postfix: Fix(nameof(PilesPostfix)));
                break;
            case "byref-arg-prefix":
                harmony.Patch(target, prefix: Fix(target.Name switch
                {
                    "ModifyHpLost" => nameof(HpLostByRefPrefix),
                    nameof(CardPile.AddInternal) or nameof(CardPile.RemoveInternal) => nameof(CardByRefPrefix),
                    _ => throw new ArgumentException("byref-arg-prefix goes on Hook:ModifyHpLost or CardPile:AddInternal/RemoveInternal"),
                }));
                break;
            case "byref-out-postfix":
            case "byref-write-postfix":
                if (target.Name != "ModifyHpLost")
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Hooks.Hook:ModifyHpLost");
                }
                harmony.Patch(target, postfix: Fix(shape == "byref-out-postfix" ? nameof(ModifiersReadPostfix) : nameof(ModifiersWritePostfix)));
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

    /// <summary>An <see cref="IlJudgeShapes"/> transpiler on a hook's code (its state machine for an async hook), and the judge's verdict.</summary>
    private static IReadOnlyList<string> InstallIl(string[] parts)
    {
        string shape = parts[0];
        MethodBase hook = Target(parts[1]);
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string gate = parts.Length > 2 && parts[2] != "priority" ? parts[2] : "";
        string id = IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "");
        MethodBase body = IlJudgeShapes.Install(shape, gate, hook, new Harmony(id), priority);
        lock (_installed)
        {
            _installed.Add((id, body));
        }
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {Name(hook)} ({Name(body)}) as {id} (priority {priority})" };
        lines.AddRange(Classification(body));
        lines.AddRange(IlJudgeShapes.Verdict(body));
        return lines;
    }

    private static void RequireWalk(MethodBase target)
    {
        if (target.Name != "IterateHookListeners" || (target.DeclaringType != typeof(MegaCrit.Sts2.Core.Combat.CombatState) && target.DeclaringType != typeof(RunState)))
        {
            throw new ArgumentException("this shape goes on MegaCrit.Sts2.Core.Combat.CombatState:IterateHookListeners or MegaCrit.Sts2.Core.Runs.RunState:IterateHookListeners");
        }
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
        lines.AddRange(IlJudgeShapes.ReportLines(label));
        if (Gates.NearMissCalls > 0)
        {
            lines.Add($"STATE foreign {label} nearmiss-postfix every call {Gates.NearMissCalls}");
        }
#if BENCH
        lines.Add($"FOREIGN tolerated checks {PatchSafety.ToleratedChecks}, gate analyses {PatchSafety.GateAnalyses} ({PatchSafety.GatedMethods} gated)");
        lines.Add($"FOREIGN listener scopes with walk patches {ListenerCache.AugmentedScopes}, walks served {ListenerCache.Served}, "
            + $"mirrored combat {FastListeners.CombatWalks} run {FastListeners.RunWalks}, pile lookups fast {PileIndex.Fast} verified {PileIndex.Verified}");
        lines.AddRange(PatchSafety.RuntimeReport().Select(t => "FOREIGN runtime " + t));
        lines.AddRange(HookSkip.GatedWalkReport().Select(t => "FOREIGN gated walks " + t));
        lines.AddRange(HookSkip.JudgedWalkReport().Select(t => "FOREIGN judged walks " + t));
        lines.AddRange(TranspilerJudge.Report().Select(t => "FOREIGN iljudge " + t));
#endif
        lines.AddRange(PatchSafety.ToleratedReport().Select(t => "FOREIGN tolerated " + t));
        return lines;
    }

    private static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    /// <summary>The local player in a combat (the narrow and pilecard lines), or why not.</summary>
    private static Player LocalPlayerInCombat()
    {
        RunState run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        Player me = LocalContext.GetMe(run) ?? throw new InvalidOperationException("no local player");
        if (me.PlayerCombatState == null)
        {
            throw new InvalidOperationException("not in a combat");
        }
        return me;
    }

    /// <summary>
    /// <c>foreignpatch narrow [label]</c> (with pile-prefix-narrow installed): the draw pile's first card is narrowed to the hand,
    /// its pile read (the prefix answers: Hand), the narrowing lifted, its pile read again (the prefix lets the call through:
    /// Draw, answered by this mod's index when it stands). STATE: the two answers (the same with this mod off); FOREIGN: whether
    /// the index answered each read.
    /// </summary>
    private static IReadOnlyList<string> Narrow(string label)
    {
        Player me = LocalPlayerInCombat();
        CardPile draw = CardPile.Get(PileType.Draw, me) ?? throw new InvalidOperationException("no draw pile");
        if (draw.Cards.Count == 0)
        {
            throw new InvalidOperationException("the draw pile is empty");
        }
        CardModel card = draw.Cards[0];
        _narrowPile = CardPile.Get(PileType.Hand, me);
        _narrowCard = card;
        long fast0 = PileIndex.Fast;
        CardPile? narrowed;
        try
        {
            narrowed = card.Pile;
        }
        finally
        {
            _narrowCard = null;
            _narrowPile = null;
        }
        long fast1 = PileIndex.Fast;
        CardPile? through = card.Pile;
        long fast2 = PileIndex.Fast;
        return new[]
        {
            $"STATE foreign {label} pile-prefix-narrow narrowed {narrowed?.Type.ToString() ?? "none"} through {through?.Type.ToString() ?? "none"}",
            $"FOREIGN narrow: index answered while narrowed {fast1 > fast0}, through {fast2 > fast1}",
        };
    }

    [ThreadStatic] private static CardModel? _narrowCard;
    [ThreadStatic] private static CardPile? _narrowPile;

    /// <summary>The extra pile <see cref="PilesPostfix"/> appends to each player's piles (made by <see cref="PileCard"/>).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, CardPile> _extraPiles = new();

    /// <summary>
    /// <c>foreignpatch pilecard [label]</c> (with piles-postfix installed): the draw pile's first card moves (silently, the game's
    /// own pile methods) into the extra pile Player.Piles now lists, its pile is read (the extra pile, as the game's scan finds
    /// it), then it goes back to its place. STATE: the answer; FOREIGN: whether the index answered.
    /// </summary>
    private static IReadOnlyList<string> PileCard(string label)
    {
        Player me = LocalPlayerInCombat();
        CardPile draw = CardPile.Get(PileType.Draw, me) ?? throw new InvalidOperationException("no draw pile");
        if (draw.Cards.Count == 0)
        {
            throw new InvalidOperationException("the draw pile is empty");
        }
        CardPile extra = _extraPiles.GetValue(me, _ => new CardPile(PileType.None));
        CardModel card = draw.Cards[0];
        draw.RemoveInternal(card, silent: true);
        extra.AddInternal(card, -1, silent: true);
        long fast0 = PileIndex.Fast;
        CardPile? answer;
        try
        {
            answer = card.Pile;
        }
        finally
        {
            extra.RemoveInternal(card, silent: true);
            draw.AddInternal(card, 0, silent: true);
        }
        long fast1 = PileIndex.Fast;
        string text = ReferenceEquals(answer, extra) ? "extra" : answer?.Type.ToString() ?? "none";
        return new[]
        {
            $"STATE foreign {label} piles-postfix pile {text}, back in {card.Pile?.Type.ToString() ?? "none"}",
            $"FOREIGN pilecard: index answered {fast1 > fast0}",
        };
    }

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

    /// <summary>
    /// A cost read before the add: a hook walk over the piles from a prefix of another mod while the card is in no pile. Of the
    /// target pile's first card (in combat), else of the added card: the added card is between piles here (removed from its
    /// old one first), so its CombatState is null and its own cost read never walks (the shape counted 0 reads before).
    /// </summary>
    private static void ProbePrefix(CardPile __instance, CardModel card) => Probe("probe-prefix", __instance.Cards.Count > 0 ? __instance.Cards[0] : card);

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

    /// <summary>The pile's own card list through Harmony's field injection (never changed here: what the shape allows is the point).</summary>
    private static void CardsFieldPostfix(List<CardModel> ____cards)
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

    /// <summary>RitsuLib's walk shape: the walk wrapped lazily, by value with <c>ref __result</c> only (on the combat walk).</summary>
    private static void WalkExpandPostfix(ref IEnumerable<AbstractModel> __result) => __result = ExpandWalk(__result, nested: false);

    /// <summary><see cref="WalkExpandPostfix"/> on the run walk, whose input ends with the child combat walk's output.</summary>
    private static void WalkExpandRunPostfix(ref IEnumerable<AbstractModel> __result) => __result = ExpandWalk(__result, nested: true);

    /// <summary>The Squeezes the combat walk's expansion yielded twice: the run walk's input holds them as pairs (its child combat walk).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AbstractModel, object> _expandedInCombat = new();

    /// <summary>
    /// Each Squeeze yielded twice (it reacts to no hook). A second application on its own output is counted. The combat walk's
    /// input must never hold the same model twice in a row. The run walk's may, as the game builds it: <c>RunState.IterateHookListeners</c>
    /// ends with <c>childCombatState.IterateHookListeners()</c>, the combat walk's patched call, whose postfixes (this one included)
    /// have run, so a combat Squeeze comes as a pair there, with or without this mod. There a pair of a Squeeze the combat walk's
    /// expansion yielded is the game's; a pair of any other model (the deck's cards are other objects than the combat's) or three
    /// in a row is a second application.
    /// </summary>
    private static IEnumerable<AbstractModel> ExpandWalk(IEnumerable<AbstractModel> walk, bool nested)
    {
        AbstractModel? previous = null;
        int repeated = 0;
        foreach (AbstractModel model in walk)
        {
            repeated = ReferenceEquals(previous, model) ? repeated + 1 : 1;
            if (repeated > 1 && model is Squeeze
                && (!nested || repeated > 2 || !_expandedInCombat.TryGetValue(model, out _)))
            {
                Count("walk-expand-postfix applied twice");
            }
            previous = model;
            yield return model;
            if (model is Squeeze)
            {
                if (!nested && !_expandedInCombat.TryGetValue(model, out _))
                {
                    _expandedInCombat.AddOrUpdate(model, model);
                }
                yield return model;
            }
        }
    }

    /// <summary>EnchantLib's walk shape: the stream snapshotted at once, Squeezes moved to its end (not per-item, not lazy).</summary>
    private static void WalkFilterRepeatPostfix(ref IEnumerable<AbstractModel> __result)
    {
        List<AbstractModel> all = __result.ToList();
        __result = all.Where(m => m is not Squeeze).Concat(all.Where(m => m is Squeeze)).ToList();
    }

    /// <summary>RitsuLib's extra-hand shape: a bool prefix answering for one card in its own context, true otherwise.</summary>
    private static bool NarrowPilePrefix(CardModel __instance, ref CardPile? __result)
    {
        if (_narrowCard == null || !ReferenceEquals(__instance, _narrowCard))
        {
            return true;
        }
        __result = _narrowPile;
        return false;
    }

    /// <summary>RitsuLib's mod piles shape: an extra pile appended to the player's piles.</summary>
    private static void PilesPostfix(Player __instance, ref IEnumerable<CardPile> __result)
    {
        if (_extraPiles.TryGetValue(__instance, out CardPile? extra))
        {
            __result = __result.Concat(new[] { extra });
        }
    }

    /// <summary>ARAM's shape (an argument rewritten by reference before the original): a monster loses 1 HP more.</summary>
    private static void HpLostByRefPrefix(Creature target, ref decimal amount)
    {
        if (target.IsMonster && amount > 0)
        {
            amount += 1;
            Count("byref-arg-prefix monster hp losses");
        }
    }

    /// <summary>An argument by reference passed through unchanged (each call counted).</summary>
    private static void CardByRefPrefix(ref CardModel card, MethodBase __originalMethod) => Count("byref-arg-prefix " + Name(__originalMethod) + " " + (card != null));

    /// <summary>Who Carried?'s shape: the out parameter taken by reference, only read (the modifiers counted).</summary>
    private static void ModifiersReadPostfix(ref IEnumerable<AbstractModel> modifiers)
    {
        // Counted without enumerating (a list in both versions): a lazy sequence would run again here, perturbing what the
        // control measures. -1 marks one that isn't a collection (the same on both sides of the A/B).
        Add("byref-out-postfix modifiers", modifiers is IReadOnlyCollection<AbstractModel> collection ? collection.Count : modifiers == null ? 0 : -1);
    }

    /// <summary>The control: the same parameter written back (the same value): a write through it, refused.</summary>
    private static void ModifiersWritePostfix(ref IEnumerable<AbstractModel> modifiers)
    {
        modifiers = modifiers?.ToList() ?? new List<AbstractModel>();
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
