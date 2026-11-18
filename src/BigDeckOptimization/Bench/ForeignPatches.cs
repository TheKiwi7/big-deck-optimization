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
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;
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
/// <item>HookSkip's choice-context helpers and CardFlightLimit's soul (2026-11-16, RitsuLib's shapes; "Needless back-offs made safe"):
/// a helper target may be given by its fingerprint key (<c>Namespace.Type::Method(ArgTypes)</c>, the constructor
/// <c>MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::.ctor(AbstractModel,UInt64,ICombatState,GameActionType)</c>).
/// <c>count-postfix</c> there is the ungated counting postfix (only the hooks constructing contexts back off). <c>ctor-source-gated</c>
/// with gate <c>squeeze</c> (RitsuLib's constructor postfix: <c>if (source is Squeeze)</c> in a <c>beforefieldinit</c> class with a
/// static field, the hits counted as STATE lines) and its controls, which must back off: <c>ctor-combatstate-gated</c> (the test on
/// <c>combatState</c>), <c>ctor-instance-gated</c> (on <c>__instance</c>, the context), <c>ctor-cctor-gated</c> (a class with an explicit
/// static constructor), <c>ctor-read-then-gated</c> (<c>combatState</c> read and stored before the test). On
/// <c>NCardFlyShuffleVfx.Create</c>: <c>create-null-gated-postfix</c> (RitsuLib's start-position postfix: nothing for a null result;
/// it counts the souls it sees, compared by <c>foreignpatch require souls</c> with the souls the scene tree got) and its control
/// <c>create-nearmiss-postfix</c> (a static read before the null test). On <c>NCardTrailVfx.Create</c>: <c>trail-replacing-prefix</c>
/// (RitsuLib's trail override: a bool prefix building the trail itself and returning false, for every trail; install it at
/// priority 800). Their counts are FOREIGN lines (a skipped soul makes none of them: they differ with the mod off).</item>
/// <item>Per hook, HookSkip's walks: <c>foreignpatch walks mark</c>, then <c>foreignpatch require filtered|vanilla Hook1,Hook2</c>
/// (each hook this game has: some walk filtered since the mark, or walks made since the mark and none filtered; names of another
/// game version are listed and skipped, at least one must be there).</item>
/// </list>
/// Arguments: <c>foreignpatch &lt;shape&gt; &lt;Namespace.Type:Method&gt; [gate] [priority N]</c>; <c>foreignpatch remove</c> takes every
/// one away; <c>foreignpatch report [label]</c>; <c>foreignpatch require tolerated</c> fails unless a check let a patch stand;
/// <c>foreignpatch modname &lt;assembly&gt;</c> prints the mod name back-off lines give that assembly. Shapes needed before this mod
/// applies (Harmony's own lookups intercepted): <see cref="ForeignLaunch"/>.
/// </summary>
public static class ForeignPatches
{
#if BENCH
    /// <summary>
    /// <c>foreignpatch namegate selftest</c> (offline harness too): a name gate's types (PatchSafety.TypesNamed) refuse a closed
    /// generic's name and a type still being built in a dynamic assembly, and find a type emitted later into a dynamic assembly
    /// already loaded once the loaded-code key moves (sampled every 2 s: this waits 2.1 s, on the main thread in game).
    /// </summary>
    /// <summary>The first loaded assembly whose types can't all be read, or null.</summary>
    private static string? Unreadable()
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                a.GetTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                // Its loadable types are listed (TypesNamed takes them since proof run 2): readable for name gates.
            }
            catch
            {
                return a.GetName().Name ?? "?";
            }
        }
        return null;
    }

    public static IReadOnlyList<string> NameGateSelfTest()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        void Expect(bool ok, string what)
        {
            lines.Add($"FOREIGN namegate selftest {what}: {(ok ? "ok" : "FAILED")}");
            if (!ok)
            {
                failures.Add(what);
            }
        }
        // The IL shape itself (round 4: ldstr's literal was never decoded, so no name gate was ever found in game).
        foreach (string gated in new[] { nameof(Gates.SqueezeNameGatedPostfix), nameof(Gates.SqueezeFullNameGatedPostfix) })
        {
            MethodInfo patch = typeof(Gates).GetMethod(gated, BindingFlags.Public | BindingFlags.Static)!;
            bool gate = PatchSafety.TypeGates(patch, PatchSafety.PatchKind.Postfix) is { Length: > 0 };
            // Offline, an assembly whose types can't all be read refuses every name gate (by design: see below).
            Expect(gate || Unreadable() != null, $"{gated}'s name test read as a gate" + (gate ? "" : $" (refused: {PatchSafety.TypesNamedFailure ?? "the IL shape"})"));
        }
        MethodInfo nearMiss = typeof(Gates).GetMethod(nameof(Gates.SqueezeNameNearMissPostfix), BindingFlags.Public | BindingFlags.Static)!;
        Expect(PatchSafety.TypeGates(nearMiss, PatchSafety.PatchKind.Postfix) == null, "a call before the name test: no gate");
        Expect(PatchSafety.TypesNamedForTest(true, "System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib]]") == null, "a closed generic's FullName refused");
        Expect(PatchSafety.TypesNamedForTest(false, "List`1") == null, "a generic arity's name refused");
        Type[]? none = PatchSafety.TypesNamedForTest(false, "BdoNoSuchTypeName");
        if (Unreadable() is { } unreadableNow)
        {
            Expect(none == null, $"a name no type has: refused while {unreadableNow}'s types can't be read (by design)");
        }
        else
        {
            Expect(none is { Length: 0 }, "a name no type has: no type (gates on nothing)"
                + (none == null ? $" (refused: {PatchSafety.TypesNamedFailure ?? "no failure recorded"})" : ""));
        }
        string unique = "BdoNameGateSelfTest" + Environment.TickCount64.ToString("X");
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        System.Reflection.Emit.ModuleBuilder module = assembly.DefineDynamicModule(unique);
        module.DefineType(unique + "First", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        System.Threading.Thread.Sleep(2100);
        System.Reflection.Emit.TypeBuilder building = module.DefineType(unique + "Later", TypeAttributes.Public | TypeAttributes.Class);
        Expect(PatchSafety.TypesNamedForTest(false, unique + "Later") == null || PatchSafety.TypesNamedForTest(false, unique + "Later") is { Length: 0 },
            "a type being built: refused, or not listed yet (no gate made from it)");
        Type? created = building.CreateType();
        System.Threading.Thread.Sleep(2100);
        Type[]? found = PatchSafety.TypesNamedForTest(false, unique + "Later");
        if (Unreadable() is { } unreadable)
        {
            // Offline (the harness): an assembly whose types can't all be read here (Steamworks.NET without its native library)
            // makes every name gate refused, by design: this case can't be told apart then.
            Expect(found == null, $"a type emitted later: refused while {unreadable}'s types can't be read (by design; not testable here)");
        }
        else
        {
            Expect(found is { Length: 1 } && found[0] == created, "a type emitted later into a loaded dynamic assembly found once the key moves"
                + (found is { Length: 1 } && found[0] == created ? "" : $" (found {(found == null ? "null" : found.Length.ToString())})"));
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("name gate self-test: " + string.Join(" | ", failures));
        }
        lines.Add($"FOREIGN namegate selftest: {lines.Count} cases as expected");
        return lines;
    }
#endif

    public const string IdPrefix = "bdtest.foreign.";

    private static readonly List<(string Id, MethodBase Target)> _installed = new();

    /// <summary>Calls counted (or values summed) per key: what both sides of an A/B must agree on.</summary>
    private static readonly ConcurrentDictionary<string, long> _counts = new();

    private static readonly string[] Shapes =
    {
        "noop-postfix", "noop-prefix", "count-postfix", "count-prefix", "nested-postfix", "keyword-prefix", "cost-postfix",
        "probe-prefix", "probe-postfix", "state-prefix",
        "bool-prefix", "ref-prefix", "identity-transpiler", "inner-postfix", "runoriginal-postfix", "byref-postfix", "field-postfix",
        "argsrename-prefix", "factory-postfix", "dup-prefix", "own-postfix",
        "gated-postfix", "gated-prefix", "gated-or-postfix", "nearmiss-postfix", "renamed-gated-postfix", "gated-patched-postfix",
        "cctor-gated-postfix",
        "walk-expand-postfix", "walk-filter-repeat", "pile-prefix-narrow", "piles-postfix", "byref-arg-prefix", "byref-out-postfix",
        "byref-write-postfix",
        "ctor-source-gated", "ctor-combatstate-gated", "ctor-instance-gated", "ctor-cctor-gated", "ctor-read-then-gated",
        "ctor-impure-cctor-gated", "ctor-throwing-cctor-gated", "ctor-reach-gated",
        "ctor-lambda-gated", "ctor-accesstools-gated", "ctor-capacity-cctor-gated", "ctor-helper-reach-gated", "model-gated-postfix", "model-gated-prefix",
        "hook-replacing-prefix", "hook-out-write-postfix", "hook-args-prefix", "noop-finalizer", "sfx-observer-prefix",
        "create-null-gated-postfix", "create-nearmiss-postfix", "create-null-gated-reach-postfix", "trail-replacing-prefix",
        "skip-prefix", "skip-and-add-prefix", "display-count-prefix",
        "name-gated-postfix", "fullname-gated-postfix", "name-nearmiss-postfix", "name-othervar-postfix", "walk-unstable-postfix",
        "flash-script-postfix", "energy-cost-postfix", "energy-cost-unstable-postfix", "walk-snapflip-postfix",
        "walk-snapflip-cost-postfix", "walk-snaprng-postfix",
    };

    /// <summary>
    /// Calls counted by the display shapes (<c>display-count-prefix</c>): other mods' code an owner-approved display premise
    /// leaves out for the calls this mod merges or reuses. FOREIGN lines, never STATE: they differ with the mod off by design.
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> _displayCounts = new();

    /// <summary>
    /// The shapes whose patch changes game state on the peer that installs it (Squeeze's keywords or cost, cards moved between
    /// piles): in co-op every peer must install them, before any combat (AutoTest refuses them later; bdperf foreign refuses them).
    /// </summary>
    public static bool ChangesState(string arg) =>
        arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is "keyword-prefix" or "cost-postfix" or "nested-postfix"
            or "byref-arg-prefix" or "pilecard" or "skip-prefix" or "skip-and-add-prefix" or "hook-replacing-prefix" or "hook-out-write-postfix"
            or "il-damage-bridge" or "il-damage-bridge-callfirst";

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
            case "statechanged" when parts.Length > 1 && parts[1] == "subscribe":
                // Another mod's CombatStateChanged handler (the rule of 2026-11-17: it runs with the walk reuse suspended, the
                // game's own handlers keep it). Default: walking the real state (RandomForeseer's prediction refresh), each walk
                // compared with the game's own call (foreignpatch require freshhandler); "noop": doing nothing but count its calls
                // (the game's handlers around it must still reuse); "mutating": flipping walk-expand-postfix's output (state no
                // version bump sees: the outputs kept before it must not be reused after it, foreignpatch require dropped). "afterhand": put right after the hand's handler (NPlayerHand,
                // whose card updates walk the most: the outputs it kept are dropped after this one, the game's handlers after it
                // keep and reuse again), else in the middle of the handlers; default: after every handler, as a mod's +=. Counted
                // (calls, listeners seen: STATE lines, the same on and off); removed by "foreignpatch remove". One at a time.
                MegaCrit.Sts2.Core.Combat.CombatStateTracker tracker = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.StateTracker;
                bool noop = parts.Skip(2).Contains("noop"), afterHand = parts.Skip(2).Contains("afterhand");
                if (_stateTracker != null)
                {
                    return new[] { "FOREIGN statechanged subscriber already added" };
                }
                bool mutating = parts.Skip(2).Contains("mutating");
                Action<MegaCrit.Sts2.Core.Combat.CombatState> handler = mutating ? OnStateChangedMutating : noop ? OnStateChangedNoop : OnStateChangedForTest;
                int position = SubscribeForTest(tracker, handler, afterHand);
                _stateTracker = tracker;
                _stateHandler = handler;
                return new[] { $"FOREIGN statechanged subscriber added ({(mutating ? "mutating" : noop ? "noop" : "walking")}, handler {position} of the dispatch)" };
            case "require" when parts.Length > 1 && parts[1] == "freshhandler":
                return RequireFreshHandler();
            case "require" when parts.Length > 2 && parts[1] == "snapwalks" && parts[2] == "sealed":
                // walk-snaprng-postfix: the net's seal found RNGs drawn during the snapshot and put every one back.
#if BENCH
                return new[] { ListenerCache.SnapshotWalksControl("require sealed"), $"FOREIGN walk-snaprng-postfix draws {_snapRngDraws}" };
#else
                return Array.Empty<string>();
#endif
            case "require" when parts.Length > 2 && parts[1] == "snapwalks":
                // The checksum snapshot's walk reuse (DESIGN "Snapshot walk reuse"): "on" (reused, compared by its net, no detection,
                // no live re-check difference, the part on) or "detected" (the net found snapshots wrong and they were built again,
                // the part still on, no live re-check difference). A "require" line: parcoop's mod-off twins drop it.
#if BENCH
                return new[] { ListenerCache.SnapshotWalksControl("require " + parts[2]) };
#else
                return Array.Empty<string>();
#endif
            case "require" when parts.Length > 1 && parts[1] == "dropped":
                return RequireDropped();
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
            case "require" when parts.Length > 1 && parts[1] == "pushpop":
                return RequirePushPop();
            case "require" when parts.Length > 1 && parts[1] == "nodouble":
                // A walk postfix let stand runs once per call on the unpatched stream: never on its own output (a list kept with it).
                if (_counts.TryGetValue("walk-expand-postfix applied twice", out long twice) && twice > 0)
                {
                    throw new InvalidOperationException($"walk-expand-postfix ran on its own output {twice} time(s): a cached list held another mod's postfix output");
                }
                return new[] { "FOREIGN walk-expand-postfix never ran on its own output" };
            case "require" when parts.Length > 2 && parts[1] is "filtered" or "vanilla":
                return RequireWalks(parts[1] == "filtered", parts[2]);
            case "require" when parts.Length > 1 && parts[1] == "souls":
                return RequireSouls();
            case "walks" when parts.Length > 1 && parts[1] == "mark":
                return MarkWalks();
            case "narrow":
                return Narrow(parts.Length > 1 ? parts[1] : "");
            case "pilecard":
                return PileCard(parts.Length > 1 ? parts[1] : "");
            case "classify" when parts.Length > 1:
                MethodBase classified = Target(parts[1]);
                return Classification(classified);
            case "spiretarget" when parts.Length > 1:
                // Rule E: whether an interface key of a SavedSpireField counts as on cards (a loaded card or enchantment type
                // implements it). irunstate: CardAscension's key (no card is a run state); icomparable: IComparable<AbstractModel>,
                // which every card implements (the control).
                Type spireKey = parts[1] switch
                {
                    "irunstate" => typeof(IRunState),
                    "icomparable" => typeof(IComparable<AbstractModel>),
                    _ => throw new ArgumentException("foreignpatch spiretarget irunstate|icomparable"),
                };
#if BENCH
                return new[] { $"FOREIGN spiretarget {parts[1]}: on cards {BaseLibTypes.MayBeOnCardsForTest(spireKey)}" };
#else
                return Array.Empty<string>();
#endif
            case "modname" when parts.Length > 1:
                // The name back-off lines give a loaded assembly's mod, and whether the game's own mod entry gave it.
                Assembly? named = PatchSafety.LoadedAssembly(parts[1]);
                return new[] { $"FOREIGN modname {parts[1]}: " + (named == null ? "not loaded"
                    : $"{PatchSafety.ModNameOf(named) ?? "none"} ({(PatchSafety.ModEntryNameOf(named) != null ? "mod entry" : "no mod entry")})") };
        }
#if BENCH
        // The augmented walks' kept outputs (ListenerCache): lazy pulls, replays, throws, checks, scope (no game state used).
        if (parts[0] == "walkreuse" && parts.Length > 1 && parts[1] == "selftest")
        {
            return ListenerCache.WalkOutputSelfTest();
        }
        // KeptOnly's memo over the kept outputs: its controls (an error, a memo made different), counters and state checks.
        if (parts[0] == "keptmemo" && parts.Length > 1)
        {
            return new[] { ListenerCache.MemoControl(string.Join(" ", parts.Skip(1))) };
        }
        // The checksum snapshot's walk reuse: its counters (foreignpatch require snapwalks on|detected for the checks).
        if (parts[0] == "snapwalks" && parts.Length > 1 && parts[1] == "status")
        {
            return new[] { ListenerCache.SnapshotWalksControl("status"), $"FOREIGN walk-snapflip shapes changed walks {_snapFlips}, RNG draws {_snapRngDraws}" };
        }
        // LazyGuard's control: every lazily built instance read as not built yet (open), or as it is (shut).
        if (parts[0] == "lazyguard" && parts.Length > 1 && parts[1] is "open" or "shut")
        {
            Patches.LazyGuard.ForceOpen = parts[1] == "open";
            return new[] { $"FOREIGN lazyguard {(Patches.LazyGuard.ForceOpen ? "open: every guard read as not built yet" : "shut: guards read from their fields")}" };
        }
        // The name gates' resolution (re-audit of the premises, P-1): closed generic names, types being built, types emitted later.
        if (parts[0] == "namegate" && parts.Length > 1 && parts[1] == "selftest")
        {
            return NameGateSelfTest();
        }
#endif
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
        // own-postfix: a no-op postfix under this mod's own Harmony id (a sub-id PatchGuard.IsOurs counts), not marked as a
        // read-only probe: HookSkip's rule A then takes SurroundingOnly on that hook (OwnPatchesOnly), its fallback control.
        string id = shape == "own-postfix" ? ModEntry.Harmony.Id + ".bdtest." + shape : IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "");
        var harmony = new Harmony(id);
        HarmonyMethod Fix(string name) => new(typeof(ForeignPatches), name) { priority = priority };
        HarmonyMethod Gated(string name) => new(typeof(Gates), name) { priority = priority };
        var extra = new List<(string Id, MethodBase Target)>();
        string Gate(params string[] known) => known.Contains(gate) ? gate : throw new ArgumentException($"{shape} gate: {string.Join(" or ", known)}");
        switch (shape)
        {
            case "noop-postfix":
            case "own-postfix":
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
            case "display-count-prefix":
                // A void prefix counting calls an owner-approved premise lets run fewer times (RelicFlashMerge's visual sets,
                // HoverTipReuse's panel methods, CardSortKeys' cost chain): its counts are FOREIGN lines (they differ with the mod
                // off by design).
                harmony.Patch(target, prefix: Fix(nameof(DisplayCountPrefix)));
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
            case "skip-prefix" or "skip-and-add-prefix":
                // A bool prefix returning false for Squeeze on CardPile.AddInternal / RemoveInternal (gate squeeze): the original
                // skipped, the pile left as it was ("skip-prefix") or changed by the prefix itself, its list written directly
                // ("skip-and-add-prefix": the card added, or removed, without the pile's events), as a mod replacing the method
                // does. PileIndex's and CardDbScan's !__runOriginal branches judge both. Changes the game's state: same with this
                // mod on or off (an off twin installs it too).
                Gate("squeeze");
                if (target.DeclaringType != typeof(CardPile) || target.Name is not (nameof(CardPile.AddInternal) or nameof(CardPile.RemoveInternal)))
                {
                    throw new ArgumentException($"{shape} goes on CardPile:AddInternal or CardPile:RemoveInternal");
                }
                bool adds = target.Name == nameof(CardPile.AddInternal);
                harmony.Patch(target, prefix: Fix(shape == "skip-prefix" ? nameof(SkipSqueezePrefix)
                    : adds ? nameof(SkipAndAddSqueezePrefix) : nameof(SkipAndRemoveSqueezePrefix)));
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
                // The same patch method twice (PatchInfo.Add doesn't deduplicate): before this mod's prefixes, then after them
                // (Priority.Last registered after this mod's: after HookFilter's mirrors and PileIndex's tracking prefixes alike).
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(DupPrefix)) { priority = Priority.First });
                var second = new Harmony(id + ".again");
                second.Patch(target, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(DupPrefix)) { priority = Priority.Last });
                extra.Add((id + ".again", target));
                break;
            case "gated-postfix":
                harmony.Patch(target, postfix: Gated(Gate("squeeze", "relic") == "squeeze" ? nameof(Gates.SqueezeGatedPostfix) : nameof(Gates.RelicGatedPostfix)));
                break;
            case "gated-prefix":
                Gate("squeeze");
                harmony.Patch(target, prefix: Gated(nameof(Gates.SqueezeGatedPrefix)));
                break;
            case "name-gated-postfix":
            case "fullname-gated-postfix":
            case "name-nearmiss-postfix":
            case "name-othervar-postfix":
                // The reflection-reads premise (2026-11-17, Skin Changer's shape): a name test on the instance's runtime type
                // (GetType().Name / FullName against a literal), and its controls: a call before the test (every call counted), a
                // name test on another argument (the drawn card of AfterCardDrawn: counted when it is a Squeeze).
                Gate("squeeze");
                if (typeof(Squeeze).FullName != Gates.SqueezeFullName)
                {
                    throw new InvalidOperationException($"Squeeze's full name is {typeof(Squeeze).FullName}, the shape tests {Gates.SqueezeFullName}");
                }
                if (shape == "name-othervar-postfix" && target.Name != nameof(AbstractModel.AfterCardDrawn))
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Models.AbstractModel:AfterCardDrawn");
                }
                harmony.Patch(target, postfix: Gated(shape switch
                {
                    "name-gated-postfix" => nameof(Gates.SqueezeNameGatedPostfix),
                    "fullname-gated-postfix" => nameof(Gates.SqueezeFullNameGatedPostfix),
                    "name-nearmiss-postfix" => nameof(Gates.SqueezeNameNearMissPostfix),
                    _ => nameof(Gates.SqueezeNameOtherArgumentPostfix),
                }));
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
            case "energy-cost-postfix" or "energy-cost-unstable-postfix":
                // CardSortKeys' sort-cost premise (re-audit of the premises, F4/F5): RitsuLib's by-ref postfix on
                // CardEnergyCost.GetWithModifiers, here +1 for Squeeze (the same at every read: the premise holds; counted, FOREIGN
                // display lines), and its violating twin, +1 on every other read of any card's cost (the positive control: the
                // sort's check must find an answer that differs from the game's and turn the part off).
                if (target.DeclaringType != typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost) || target.Name != "GetWithModifiers")
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost:GetWithModifiers");
                }
                _energyCostCard ??= AccessTools.FieldRefAccess<MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost, CardModel>("_card");
                harmony.Patch(target, postfix: Fix(shape == "energy-cost-postfix" ? nameof(EnergyCostPostfix) : nameof(EnergyCostUnstablePostfix)));
                break;
            case "flash-script-postfix":
                // RelicFlashMerge's scene check control (re-audit of the premises, B1/F3): a postfix of "another mod" on the creature
                // effect's factory giving the node it returns a child carrying another script (an in-memory GDScript: no resource
                // path). The check on the node the frame's first flash created must find it: no creature-effect merge at all.
                if (target.DeclaringType != typeof(NRelicFlashVfx) || target.Name != nameof(NRelicFlashVfx.Create) || target.GetParameters().Length != 2)
                {
                    throw new ArgumentException("flash-script-postfix goes on MegaCrit.Sts2.Core.Nodes.Vfx.NRelicFlashVfx::Create(RelicModel,Creature)");
                }
                harmony.Patch(target, postfix: Fix(nameof(FlashScriptPostfix)));
                break;
            case "walk-unstable-postfix":
                // The augmented-walk premise's control (2026-11-17): a postfix whose output changes from call to call at one state
                // (every third call without its last listener): a kept output reused must be found to differ (the 1-in-16 check).
                RequireWalk(target);
                harmony.Patch(target, postfix: Fix(nameof(WalkUnstablePostfix)));
                break;
            case "walk-filter-repeat":
                RequireWalk(target);
                harmony.Patch(target, postfix: Fix(nameof(WalkFilterRepeatPostfix)));
                break;
            case "walk-snapflip-postfix":
            case "walk-snapflip-cost-postfix":
            case "walk-snaprng-postfix":
                // The snapshot walk reuse's controls (DESIGN "Snapshot walk reuse"), postfixes on the combat walk whose output or side
                // effect changes in the middle of each checksum snapshot, counted per FromRun call (its own frame: a rebuild or the
                // vanilla re-check nested in a snapshot starts at its beginning again, as the game's own FromRun):
                //  - walk-snapflip-postfix: from the frame's third combat walk on, every Squeeze yielded twice (Squeeze reacts to no
                //    hook: no game value changes). The first reuse's check (the second walk) sees the same output: only the net can.
                //  - walk-snapflip-cost-postfix: from the frame's second pile on (CombatPileState.From counted: the same point however
                //    many walks this mod's other parts make), every Spiked Gauntlets relic yielded twice: a power card's cost there is
                //    +2 instead of +1 in the game's own FromRun, so a reused snapshot would differ from vanilla (the net must find it
                //    and the rebuilt snapshot must be the one used: bdperf verify all compares it with a vanilla one).
                //  - walk-snaprng-postfix: each enumeration of the walk's output inside a FromRun draws the run's Niche RNG once (a lazy
                //    side effect a reuse runs fewer times): the net's seal must find it and put the RNG back before the rebuild.
                if (target.DeclaringType != typeof(MegaCrit.Sts2.Core.Combat.CombatState) || target.Name != "IterateHookListeners")
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Combat.CombatState:IterateHookListeners");
                }
                InstallFromRunFrames(id, extra, piles: shape == "walk-snapflip-cost-postfix");
                harmony.Patch(target, postfix: Fix(shape switch
                {
                    "walk-snapflip-postfix" => nameof(WalkSnapFlipPostfix),
                    "walk-snapflip-cost-postfix" => nameof(WalkSnapCostPostfix),
                    _ => nameof(WalkSnapRngPostfix),
                }));
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
            case "ctor-source-gated":
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SourceGate), nameof(SourceGate.Postfix)) { priority = priority });
                break;
            case "ctor-combatstate-gated":
            case "ctor-instance-gated":
            case "ctor-read-then-gated":
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SourceGate), shape switch
                {
                    "ctor-combatstate-gated" => nameof(SourceGate.CombatStateGatedPostfix),
                    "ctor-instance-gated" => nameof(SourceGate.InstanceGatedPostfix),
                    _ => nameof(SourceGate.ReadThenGatedPostfix),
                }) { priority = priority });
                break;
            case "hook-replacing-prefix":
                // EnchantLib's shape on Hook.ModifyBlock (rule A): a bool prefix answering for the hook itself (ref __result, the out
                // modifiers written) and skipping it, for an even block; true otherwise. Changes the game's state: same with this
                // mod on or off (an off twin installs it too).
                if (target.Name != "ModifyBlock")
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Hooks.Hook:ModifyBlock");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HookShapes), nameof(HookShapes.ReplaceBlockPrefix)) { priority = priority });
                break;
            case "hook-out-write-postfix":
                // Zone's / EnchantLib's shapes (rule A): a postfix writing an out parameter and __result. On Hook.ModifyHpLost a
                // monster loses 1 HP more and the modifiers are written back (a copy); on Hook.ShouldAllowTargeting the preventer is
                // written back (the same value) and counted.
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(HookShapes), target.Name switch
                {
                    "ModifyHpLost" => nameof(HookShapes.HpLostWritePostfix),
                    "ShouldAllowTargeting" => nameof(HookShapes.TargetingWritePostfix),
                    _ => throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Hooks.Hook:ModifyHpLost or :ShouldAllowTargeting"),
                }) { priority = priority });
                break;
            case "hook-args-prefix":
                // A void prefix taking __args (Harmony writes the array back into the arguments): only reads it, counted per target.
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HookShapes), nameof(HookShapes.ArgsPrefix)) { priority = priority });
                break;
            case "sfx-observer-prefix":
                // ARAM's / RegentFX's shape on SfxCmd.Play(string, float) (rule D): a bool prefix counting the relic sounds per sfx
                // and suppressing the general relic flash sound (returns false for it). Sound only: no game state.
                if (target.DeclaringType?.Name != "SfxCmd" || target.Name != "Play" || target.GetParameters().Length != 2)
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Commands.SfxCmd::Play(String,Single)");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HookShapes), nameof(HookShapes.SfxObserverPrefix)) { priority = priority });
                break;
            case "noop-finalizer":
                // A void finalizer (the control of rule A: finalizers on a hook still back off).
                harmony.Patch(target, finalizer: new HarmonyMethod(typeof(HookShapes), nameof(HookShapes.NoOpFinalizer)) { priority = priority });
                break;
            case "ctor-impure-cctor-gated":
                // The source-gated shape in a beforefieldinit class whose initializer reads the clock: when it runs matters.
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SourceImpureGate), nameof(SourceImpureGate.Postfix)) { priority = priority });
                break;
            case "ctor-throwing-cctor-gated":
                // P1 (2026-11-17): the source-gated shape in a beforefieldinit class whose initializer looks a method up by name
                // (AccessTools.Method: an ambiguous name throws, at whatever time the runtime runs it).
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SourceThrowingGate), nameof(SourceThrowingGate.Postfix)) { priority = priority });
                break;
            case "ctor-reach-gated":
                // P2 (2026-11-17): the source-gated shape in a class with no initializer, reaching (on its gated path) a static of a
                // beforefieldinit class whose initializer reads the clock: compiling the postfix may run it.
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SourceReachGate), nameof(SourceReachGate.Postfix)) { priority = priority });
                break;
            case "ctor-lambda-gated":
            case "ctor-accesstools-gated":
            case "ctor-capacity-cctor-gated":
            case "ctor-helper-reach-gated":
                // Re-audit round 2 of 2026-11-17: the source-gated shape with (B2) a non-capturing lambda (Roslyn's <>c class, its
                // initializer `newobj; stsfld`): stands; (B3) a call to HarmonyLib's AccessTools.Field on its gated path: stands;
                // (A3) its class's initializer creating a ConcurrentDictionary with concurrency level 0 (throws): backs off; (A2) an
                // own-class helper it calls (the JIT may inline it) reading ImpureClock: backs off.
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(shape switch
                {
                    "ctor-lambda-gated" => typeof(SourceLambdaGate),
                    "ctor-accesstools-gated" => typeof(SourceAccessToolsGate),
                    "ctor-capacity-cctor-gated" => typeof(SourceCapacityGate),
                    _ => typeof(SourceHelperReachGate),
                }, "Postfix") { priority = priority });
                break;
            case "model-gated-postfix":
            case "model-gated-prefix":
                // Gated on the listener argument `model` of PlayerChoiceContext.PushModel / PopModel (HookSkip's helpers): the
                // postfix stands on both, the prefix only on PopModel (PushModel creates the stack a prefix could read).
                if (target.DeclaringType != typeof(PlayerChoiceContext) || target.Name is not ("PushModel" or "PopModel"))
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext:PushModel or :PopModel");
                }
                Gate("squeeze");
                void PatchModel() => harmony.Patch(target, prefix: shape == "model-gated-prefix" ? new HarmonyMethod(typeof(ModelGate), nameof(ModelGate.Prefix)) { priority = priority } : null,
                    postfix: shape == "model-gated-postfix" ? new HarmonyMethod(typeof(ModelGate), nameof(ModelGate.Postfix)) { priority = priority } : null);
                if (parts.Contains("background"))
                {
                    // A mod patching from another thread (re-audit of b64e896, A5): InlineGuard queues its rebuild to the main thread.
                    System.Threading.Tasks.Task.Run(PatchModel).GetAwaiter().GetResult();
                }
                else
                {
                    PatchModel();
                }
                break;
            case "ctor-cctor-gated":
                RequireChoiceConstructor(target);
                Gate("squeeze");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SourceCctorGate), nameof(SourceCctorGate.Postfix)) { priority = priority });
                break;
            case "create-null-gated-postfix":
            case "create-nearmiss-postfix":
            case "create-null-gated-reach-postfix":
                if (target.DeclaringType != typeof(NCardFlyShuffleVfx) || target.Name != nameof(NCardFlyShuffleVfx.Create))
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyShuffleVfx:Create");
                }
                // create-null-gated-reach-postfix (B1, re-audit round 2 of 2026-11-17): RitsuLib's null-gated shape in a class whose
                // beforefieldinit initializer reads the clock, reaching another such class on its non-null path (RitsuLib's
                // ModCardPileRegistry): NullSkipped judges no initializer (the postfix runs at every call, skipped or not), so it stands.
                harmony.Patch(target, postfix: shape == "create-null-gated-reach-postfix"
                    ? new HarmonyMethod(typeof(SoulReachShape), nameof(SoulReachShape.Postfix)) { priority = priority }
                    : new HarmonyMethod(typeof(SoulShapes), shape == "create-null-gated-postfix"
                        ? nameof(SoulShapes.NullGatedPostfix) : nameof(SoulShapes.NearMissPostfix)) { priority = priority });
                if (shape == "create-null-gated-postfix")
                {
                    SoulShapes.WatchTree();
                }
                break;
            case "trail-replacing-prefix":
                if (target.DeclaringType != typeof(NCardTrailVfx) || target.Name != nameof(NCardTrailVfx.Create))
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Nodes.Vfx.NCardTrailVfx:Create");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(SoulShapes), nameof(SoulShapes.TrailPrefix)) { priority = priority });
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

    /// <summary>A method by <c>Namespace.Type:Method</c>, or any member (a constructor too) by its fingerprint key <c>Namespace.Type::Method(ArgTypes)</c>.</summary>
    private static MethodBase Target(string spec) =>
        (spec.Contains("::", StringComparison.Ordinal) ? MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, spec) : AccessTools.Method(spec))
        ?? throw new ArgumentException("no method " + spec + " (Namespace.Type:Method, or Namespace.Type::Method(ArgTypes))");

    private static void RequireChoiceConstructor(MethodBase target)
    {
        ParameterInfo[] parameters = target.GetParameters();
        if (target.DeclaringType != typeof(HookPlayerChoiceContext) || !target.IsConstructor || parameters.Length != 4 || parameters[0].Name != "source")
        {
            throw new ArgumentException("this shape goes on MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::.ctor(AbstractModel,UInt64,ICombatState,GameActionType)");
        }
    }

    /// <summary>Per hook name, HookSkip's walk calls and filtered walks at the last <c>foreignpatch walks mark</c>.</summary>
    private static readonly Dictionary<string, (long Calls, long Filtered)> _walkMark = new();

    private static IReadOnlyList<string> MarkWalks()
    {
#if BENCH
        _walkMark.Clear();
        foreach (string hook in typeof(MegaCrit.Sts2.Core.Hooks.Hook).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(m => m.Name).Distinct())
        {
            _walkMark[hook] = (HookSkip.WalkCallsOf(hook), HookSkip.FilteredWalksOf(hook));
        }
        return new[] { $"FOREIGN walks marked ({_walkMark.Count} hook names)" };
#else
        return Array.Empty<string>();
#endif
    }

    /// <summary>
    /// <c>foreignpatch require filtered|vanilla Hook1,Hook2</c>: since the mark, each hook this game has ran some walk filtered
    /// (filtered), or made walks and none filtered (vanilla: it ran, the game's whole walk each time). Names of another game
    /// version are listed and skipped; at least one name must be this game's.
    /// </summary>
    private static IReadOnlyList<string> RequireWalks(bool filtered, string hooks)
    {
#if BENCH
        var lines = new List<string>();
        int present = 0;
        foreach (string hook in hooks.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!HookSkip.HasHook(hook))
            {
                lines.Add($"FOREIGN walks Hook.{hook}: not a hook of this game version, skipped");
                continue;
            }
            present++;
            (long calls0, long filtered0) = _walkMark.TryGetValue(hook, out var mark) ? mark : (0, 0);
            long calls = HookSkip.WalkCallsOf(hook) - calls0, ran = HookSkip.FilteredWalksOf(hook) - filtered0;
            lines.Add($"FOREIGN walks Hook.{hook}: {calls} since the mark, {ran} filtered");
            if (filtered ? ran == 0 : calls == 0 || ran > 0)
            {
                throw new InvalidOperationException($"Hook.{hook}: {calls} walks since the mark, {ran} filtered (expected {(filtered ? "some filtered" : "some, none filtered")})");
            }
        }
        if (present == 0)
        {
            throw new InvalidOperationException($"none of {hooks} is a hook of this game version");
        }
        return lines;
#else
        return Array.Empty<string>();
#endif
    }

    /// <summary>
    /// <c>foreignpatch require souls</c> (with create-null-gated-postfix installed): the souls that postfix saw (non-null results)
    /// equal the souls the scene tree got since the install, and at least one; it never saw a skipped one (null) as a soul.
    /// </summary>
    private static IReadOnlyList<string> RequireSouls()
    {
        long seen = SoulShapes.SoulsSeen, added = SoulShapes.SoulsAdded;
        if (seen == 0 || seen != added)
        {
            throw new InvalidOperationException($"create-null-gated-postfix saw {seen} souls, the scene tree got {added}");
        }
        return new[] { $"FOREIGN souls: create-null-gated-postfix saw {seen}, the scene tree got {added}, null results {SoulShapes.NullResults}, trails built by trail-replacing-prefix {SoulShapes.TrailsBuilt}" };
    }

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
        Patches.LazyGuard.ForceOpen = false;
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
        SoulShapes.UnwatchTree();
        _framesInstalled = false;
        _pileFramesInstalled = false;
        if (_stateTracker != null)
        {
            if (_stateHandler != null)
            {
                _stateTracker.CombatStateChanged -= _stateHandler;
            }
            _stateTracker = null;
            _stateHandler = null;
            lines.Add("FOREIGN removed the statechanged subscriber");
        }
        return lines.Count > 0 ? lines : new[] { "FOREIGN nothing to remove" };
    }

    /// <summary>
    /// The counts as STATE lines (compare.py compares them line for line between the on and off sides), then the tolerated
    /// checks, what was let stand, and what only the runtime produces (not compared: they differ with the mod off).
    /// </summary>
    private static IReadOnlyList<string> Report(string label)
    {
        // The kept notes are worked out at each preflight (WatchNote): run one first, as bdopt runs it, so the notes name the
        // patches installed since the last one (proof run 2: the kept-note expectations of relicflash-display, foreign-walkreuse,
        // hover-tips-display only matched in bdopt why's later output). Display bookkeeping only.
        try
        {
            Config.Settings.RefreshConflicts();
        }
        catch
        {
            // The report goes on with the notes as they are.
        }
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
        lines.Add($"FOREIGN walk reuse kept {ListenerCache.WalksMaterialized}, reused {ListenerCache.WalksReused}, checked {ListenerCache.WalksVerified}, "
            + $"mismatches {ListenerCache.WalkMismatches}, foreign dispatches {ListenerCache.ForeignDispatches} (reused in the game's handlers "
            + $"{ListenerCache.GameHandlerReuses}, other mods' handlers run suspended {ListenerCache.ForeignHandlerCalls}, kept outputs dropped after them "
            + $"{ListenerCache.KeptDroppedAfterForeign}, first walks after them kept anew {ListenerCache.KeptAfterForeign}, reused {ListenerCache.ReusedAfterForeign}), "
            + $"out of scope {ListenerCache.ReusesOutOfScope}, "
            + $"suspended {ListenerCache.SuspendedRevalidations}, on {ListenerCache.WalkReuseEnabled}");
        lines.Add(ListenerCache.MemoControl("status"));
        lines.Add(ListenerCache.SnapshotWalksControl("status"));
        lines.AddRange(PatchSafety.RuntimeReport().Select(t => "FOREIGN runtime " + t));
        lines.AddRange(HookSkip.GatedWalkReport().Select(t => "FOREIGN gated walks " + t));
        lines.AddRange(HookSkip.FilteredWalkReport().Select(t => "FOREIGN filtered walks " + t));
        lines.AddRange(HookSkip.JudgedWalkReport().Select(t => "FOREIGN judged walks " + t));
        lines.AddRange(TranspilerJudge.Report().Select(t => "FOREIGN iljudge " + t));
#endif
        lines.AddRange(PatchSafety.ToleratedReport().Select(t => "FOREIGN tolerated " + t));
        lines.AddRange(PatchSafety.KeptNotes().Select(k => $"FOREIGN kept {k.Patch}: {k.Text} <- {string.Join(", ", k.Mods)}"));
        lines.AddRange(PatchSafety.ExactNotes().Select(k => $"FOREIGN exact {k.Patch}: {k.Text} <- {string.Join(", ", k.Mods)}"));
        // Whether combat hook subscriptions exist (BaseLib's, say): every merged relic flash replays its sound then, and the
        // combat-end walk counts as another mod's code for CardFlightLimit (re-audit round 2).
        lines.Add($"FOREIGN combat hook subscriptions {(GameHookDelegates.Subscribed() ? "present" : "none")}");
        if (SoulReachShape.Seen > 0)
        {
            lines.Add($"FOREIGN souls seen by create-null-gated-reach-postfix {SoulReachShape.Seen}");
        }
        lines.AddRange(_displayCounts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"FOREIGN display {label} {e.Key} {e.Value}"));
        if (SoulShapes.SoulsSeen + SoulShapes.NullResults + SoulShapes.TrailsBuilt > 0)
        {
            lines.Add($"FOREIGN souls seen {SoulShapes.SoulsSeen}, added to the tree {SoulShapes.SoulsAdded}, null results {SoulShapes.NullResults}, trails built {SoulShapes.TrailsBuilt}");
        }
        return lines;
    }

    private static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    private static MegaCrit.Sts2.Core.Combat.CombatStateTracker? _stateTracker;
    private static Action<MegaCrit.Sts2.Core.Combat.CombatState>? _stateHandler;

    /// <summary>
    /// Subscribes <paramref name="handler"/> to the tracker's CombatStateChanged: after every handler (+=, as a mod subscribing),
    /// or right after the hand's (NPlayerHand) handler, else in the middle of them (the event's field set to the same handlers
    /// with it inserted there: their order kept). The same on and off. Returns its index in the invocation list.
    /// </summary>
    private static int SubscribeForTest(MegaCrit.Sts2.Core.Combat.CombatStateTracker tracker, Action<MegaCrit.Sts2.Core.Combat.CombatState> handler, bool afterHand)
    {
        FieldInfo field = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Combat.CombatStateTracker), "CombatStateChanged")
            ?? throw new InvalidOperationException("CombatStateTracker.CombatStateChanged's field not found");
        if (!afterHand)
        {
            tracker.CombatStateChanged += handler;
            return field.GetValue(tracker) is Delegate all ? all.GetInvocationList().Length - 1 : 0;
        }
        Delegate[] current = (field.GetValue(tracker) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
        int hand = Array.FindIndex(current, d => d.Method.DeclaringType == typeof(MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand));
        int at = hand >= 0 ? hand + 1 : current.Length / 2;
        field.SetValue(tracker, Delegate.Combine(current.Take(at).Append(handler).Concat(current.Skip(at)).ToArray()));
        return at;
    }

    /// <summary>
    /// The statechanged subscriber: a walk of the combat's listeners and of the run's (with the combat as child) in each dispatch,
    /// as RandomForeseer's refresh makes, each compared with the game's own call made right after it (ListenerCache.RunVanilla:
    /// every listener optimization off). The rule of 2026-11-17: what it sees is the game's walk, and no kept output was made,
    /// checked or reused while it ran (test builds; <c>foreignpatch require freshhandler</c>).
    /// </summary>
    private static void OnStateChangedForTest(MegaCrit.Sts2.Core.Combat.CombatState state)
    {
        Count("statechanged-subscriber calls");
#if BENCH
        long touchedBefore = ListenerCache.WalksReused + ListenerCache.WalksVerified + ListenerCache.WalksMaterialized
            + ListenerCache.MemoHits + ListenerCache.MemoBuilt + ListenerCache.MemoVerified;
#endif
        List<AbstractModel> combat = state.IterateHookListeners().ToList();
        RunState? runState = state.RunState as RunState;
        List<AbstractModel>? run = runState?.IterateHookListeners(state).ToList();
#if BENCH
        _freshHandlerTouched += ListenerCache.WalksReused + ListenerCache.WalksVerified + ListenerCache.WalksMaterialized
            + ListenerCache.MemoHits + ListenerCache.MemoBuilt + ListenerCache.MemoVerified - touchedBefore;
#endif
        List<AbstractModel> freshCombat = ListenerCache.RunVanilla(() => state.IterateHookListeners().ToList());
        List<AbstractModel>? freshRun = runState == null ? null : ListenerCache.RunVanilla(() => runState.IterateHookListeners(state).ToList());
        _freshHandlerWalks += run == null ? 1 : 2;
        if (!combat.SequenceEqual(freshCombat, ReferenceEqualityComparer.Instance)
            || (run != null && !run.SequenceEqual(freshRun!, ReferenceEqualityComparer.Instance)))
        {
            _freshHandlerMismatches++;
        }
        _counts.AddOrUpdate("statechanged-subscriber listeners", combat.Count, (_, n) => n + combat.Count);
        if (run != null)
        {
            _counts.AddOrUpdate("statechanged-subscriber run listeners", run.Count, (_, n) => n + run.Count);
        }
    }

    /// <summary>
    /// The mutating statechanged subscriber: counts its calls (a STATE line) and flips what walk-expand-postfix returns from the next
    /// walk on (<see cref="_expandFlipped"/>): another mod's handler changing state a version bump doesn't see. The game's handlers
    /// after it must keep new outputs, never reuse those kept before it (<c>foreignpatch require dropped</c>).
    /// </summary>
    private static void OnStateChangedMutating(MegaCrit.Sts2.Core.Combat.CombatState state)
    {
        Count("statechanged-mutating calls");
        _expandFlipped = !_expandFlipped;
    }

    /// <summary>
    /// <c>foreignpatch require dropped</c> (test builds): keys kept when another mod's handler returned were kept anew at their next
    /// walk, never reused or checked against an output kept before it, no kept output differed from a fresh one, the part on.
    /// </summary>
    private static IReadOnlyList<string> RequireDropped()
    {
#if BENCH
        string line = $"FOREIGN after other mods' handlers: kept anew {ListenerCache.KeptAfterForeign}, reused {ListenerCache.ReusedAfterForeign}, "
            + $"walk mismatches {ListenerCache.WalkMismatches}, walk reuse on {ListenerCache.WalkReuseEnabled}";
        if (ListenerCache.KeptAfterForeign == 0 || ListenerCache.ReusedAfterForeign > 0 || ListenerCache.WalkMismatches > 0 || !ListenerCache.WalkReuseEnabled)
        {
            throw new InvalidOperationException(line);
        }
        return new[] { line };
#else
        return Array.Empty<string>();
#endif
    }

    /// <summary>The noop statechanged subscriber: counts its calls (a STATE line), nothing else.</summary>
    private static void OnStateChangedNoop(MegaCrit.Sts2.Core.Combat.CombatState state) => Count("statechanged-noop calls");

    /// <summary>The walking subscriber's walks compared, those differing from the game's call, and kept outputs made or reused while it ran.</summary>
    private static long _freshHandlerWalks, _freshHandlerMismatches, _freshHandlerTouched;

    /// <summary>
    /// <c>foreignpatch require freshhandler</c>: the walking subscriber compared walks, none differed from the game's own call, and
    /// (test builds) no kept walk output was made, checked or reused while it ran.
    /// </summary>
    private static IReadOnlyList<string> RequireFreshHandler()
    {
        string line = $"FOREIGN statechanged subscriber walks compared {_freshHandlerWalks}, mismatches {_freshHandlerMismatches}, "
            + $"kept outputs made or reused inside {_freshHandlerTouched}";
        if (_freshHandlerWalks == 0 || _freshHandlerMismatches > 0 || _freshHandlerTouched > 0)
        {
            throw new InvalidOperationException(line);
        }
        return new[] { line };
    }

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
    /// <summary>
    /// The tables of game objects the shapes keep, in their own class: ForeignPatches' own initializer stays one PatchSafety
    /// accepts (P2, 2026-11-17: the gated shapes call Count, so compiling them may run it), these two being collections of game types.
    /// </summary>
    private static class Tables
    {
        public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, CardPile> ExtraPiles = new();
        public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AbstractModel, object> ExpandedInCombat = new();
    }

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
        CardPile extra = Tables.ExtraPiles.GetValue(me, _ => new CardPile(PileType.None));
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

    private static void DisplayCountPrefix(MethodBase __originalMethod) => _displayCounts.AddOrUpdate(Name(__originalMethod), 1, (_, n) => n + 1);

    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost, CardModel>? _energyCostCard;
    private static long _unstableReads;

    /// <summary>RitsuLib's shape: a by-ref postfix on the cost, the same at every read (+1 for Squeeze).</summary>
    private static void EnergyCostPostfix(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost __instance, ref int __result)
    {
        _displayCounts.AddOrUpdate("energy-cost-postfix reads", 1, (_, n) => n + 1);
        if (_energyCostCard!(__instance) is Squeeze)
        {
            __result += 1;
        }
    }

    /// <summary>The premise violated: every other read of any card's cost is one more (inconsistent within one sort).</summary>
    private static void EnergyCostUnstablePostfix(ref int __result)
    {
        _displayCounts.AddOrUpdate("energy-cost-unstable-postfix reads", 1, (_, n) => n + 1);
        if ((++_unstableReads & 1) == 0)
        {
            __result += 1;
        }
    }

    /// <summary>The script flash-script-postfix attaches (made once: <c>extends Node</c>, no resource path).</summary>
    private static Godot.Script? _flashScript;

    private static void FlashScriptPostfix(NRelicFlashVfx? __result)
    {
        if (__result == null)
        {
            return;
        }
        try
        {
            if (_flashScript == null)
            {
                var script = new Godot.GDScript { SourceCode = "extends Node\n" };
                script.Reload();
                _flashScript = script;
            }
            var child = new Godot.Node { Name = "BdTestScripted" };
            child.SetScript(_flashScript);
            __result.AddChild(child);
            _displayCounts.AddOrUpdate("flash-script-postfix added", 1, (_, n) => n + 1);
        }
        catch (Exception e)
        {
            _displayCounts.AddOrUpdate("flash-script-postfix failed", 1, (_, n) => n + 1);
            MegaCrit.Sts2.Core.Logging.Log.Warn($"bdtest flash-script-postfix: {e.Message}");
        }
    }

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

    /// <summary>skip-prefix: the original skipped for Squeeze, nothing done instead (counted per target).</summary>
    private static bool SkipSqueezePrefix(CardModel card, MethodBase __originalMethod)
    {
        if (card is not Squeeze)
        {
            return true;
        }
        Count("skip-prefix " + __originalMethod.Name);
        return false;
    }

    /// <summary>skip-and-add-prefix on AddInternal: Squeeze put at the end of the pile's list directly (no event), the original skipped.</summary>
    private static bool SkipAndAddSqueezePrefix(CardModel card, List<CardModel> ____cards)
    {
        if (card is not Squeeze || ____cards.Contains(card))
        {
            return true;
        }
        ____cards.Add(card);
        Count("skip-and-add-prefix AddInternal");
        return false;
    }

    /// <summary>skip-and-add-prefix on RemoveInternal: Squeeze taken out of the pile's list directly (no event), the original skipped.</summary>
    private static bool SkipAndRemoveSqueezePrefix(CardModel card, List<CardModel> ____cards)
    {
        if (card is not Squeeze || !____cards.Remove(card))
        {
            return true;
        }
        Count("skip-and-add-prefix RemoveInternal");
        return false;
    }

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
    private static void WalkExpandPostfix(ref IEnumerable<AbstractModel> __result) => __result = ExpandWalk(__result, nested: false, expand: !_expandFlipped);

    /// <summary><see cref="WalkExpandPostfix"/> on the run walk, whose input ends with the child combat walk's output.</summary>
    private static void WalkExpandRunPostfix(ref IEnumerable<AbstractModel> __result) => __result = ExpandWalk(__result, nested: true, expand: !_expandFlipped);

    /// <summary>
    /// Flipped by the mutating statechanged subscriber at each of its calls: the expansion's output then changes (Squeezes no longer
    /// doubled, or doubled again) from the next walk on, read when the postfix runs (state a version bump doesn't see).
    /// </summary>
    private static bool _expandFlipped;

    /// <summary>The Squeezes the combat walk's expansion yielded twice: the run walk's input holds them as pairs (its child combat walk).</summary>

    /// <summary>
    /// Each Squeeze yielded twice (it reacts to no hook). A second application on its own output is counted. The combat walk's
    /// input must never hold the same model twice in a row. The run walk's may, as the game builds it: <c>RunState.IterateHookListeners</c>
    /// ends with <c>childCombatState.IterateHookListeners()</c>, the combat walk's patched call, whose postfixes (this one included)
    /// have run, so a combat Squeeze comes as a pair there, with or without this mod. There a pair of a Squeeze the combat walk's
    /// expansion yielded is the game's; a pair of any other model (the deck's cards are other objects than the combat's) or three
    /// in a row is a second application.
    /// </summary>
    private static IEnumerable<AbstractModel> ExpandWalk(IEnumerable<AbstractModel> walk, bool nested, bool expand)
    {
        AbstractModel? previous = null;
        int repeated = 0;
        foreach (AbstractModel model in walk)
        {
            repeated = ReferenceEquals(previous, model) ? repeated + 1 : 1;
            if (repeated > 1 && model is Squeeze
                && (!nested || repeated > 2 || !Tables.ExpandedInCombat.TryGetValue(model, out _)))
            {
                Count("walk-expand-postfix applied twice");
            }
            previous = model;
            yield return model;
            if (model is Squeeze && expand)
            {
                if (!nested && !Tables.ExpandedInCombat.TryGetValue(model, out _))
                {
                    Tables.ExpandedInCombat.AddOrUpdate(model, model);
                }
                yield return model;
            }
        }
    }

    /// <summary>EnchantLib's walk shape: the stream snapshotted at once, Squeezes moved to its end (not per-item, not lazy).</summary>
    private static long _unstableCalls;

    private static void WalkUnstablePostfix(ref IEnumerable<AbstractModel> __result)
    {
        if (++_unstableCalls % 3 == 0)
        {
            IEnumerable<AbstractModel> walk = __result;
            __result = DropLast(walk);
        }
    }

    private static IEnumerable<AbstractModel> DropLast(IEnumerable<AbstractModel> walk)
    {
        AbstractModel? held = null;
        bool any = false;
        foreach (AbstractModel model in walk)
        {
            if (any)
            {
                yield return held!;
            }
            held = model;
            any = true;
        }
    }

    /// <summary>Per FromRun call on this thread (innermost last): its combat walks and the piles it started (the snapflip shapes).</summary>
    [ThreadStatic] private static List<(int Walks, int Piles)>? _fromRunFrames;
    private static bool _framesInstalled, _pileFramesInstalled;
    /// <summary>Walks the snapflip shapes changed, and the RNG draws of walk-snaprng-postfix (FOREIGN lines: not compared).</summary>
    private static long _snapFlips, _snapRngDraws;

    /// <summary>
    /// A prefix and a finalizer of "another mod" on FromRun keeping <see cref="_fromRunFrames"/> (and, for the cost shape, a prefix on
    /// CombatPileState.From counting the frame's piles), once for the shapes installed (removed with them).
    /// </summary>
    private static void InstallFromRunFrames(string id, List<(string Id, MethodBase Target)> extra, bool piles)
    {
        MethodBase fromRun = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState), "FromRun")
            ?? throw new ArgumentException("NetFullCombatState.FromRun not found");
        if (!_framesInstalled)
        {
            new Harmony(id + ".frames").Patch(fromRun, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(FramePrefix)),
                finalizer: new HarmonyMethod(typeof(ForeignPatches), nameof(FrameFinalizer)));
            extra.Add((id + ".frames", fromRun));
            _framesInstalled = true;
        }
        if (piles && !_pileFramesInstalled)
        {
            MethodBase pileFrom = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState.CombatPileState), "From")
                ?? throw new ArgumentException("CombatPileState.From not found");
            new Harmony(id + ".piles").Patch(pileFrom, prefix: new HarmonyMethod(typeof(ForeignPatches), nameof(PileFramePrefix)));
            extra.Add((id + ".piles", pileFrom));
            _pileFramesInstalled = true;
        }
    }

    private static void FramePrefix(out int __state)
    {
        _fromRunFrames ??= new List<(int, int)>();
        __state = _fromRunFrames.Count;
        _fromRunFrames.Add((0, 0));
    }

    /// <summary>Back to the depth the prefix found (Harmony may run a finalizer twice: the same depth again).</summary>
    private static void FrameFinalizer(int __state)
    {
        if (_fromRunFrames != null && _fromRunFrames.Count > __state && __state >= 0)
        {
            _fromRunFrames.RemoveRange(__state, _fromRunFrames.Count - __state);
        }
    }

    private static void PileFramePrefix()
    {
        if (_fromRunFrames is { Count: > 0 })
        {
            (int walks, int piles) = _fromRunFrames[^1];
            _fromRunFrames[^1] = (walks, piles + 1);
        }
    }

    /// <summary>This walk's place in the innermost FromRun call (walks so far included), or null outside one.</summary>
    private static (int Walks, int Piles)? NextFrameWalk()
    {
        if (_fromRunFrames is not { Count: > 0 })
        {
            return null;
        }
        (int walks, int piles) = _fromRunFrames[^1];
        _fromRunFrames[^1] = (walks + 1, piles);
        return (walks + 1, piles);
    }

    private static void WalkSnapFlipPostfix(ref IEnumerable<AbstractModel> __result)
    {
        if (NextFrameWalk() is { Walks: >= 3 })
        {
            _snapFlips++;
            __result = DoubleSqueezes(__result);
        }
    }

    private static void WalkSnapCostPostfix(ref IEnumerable<AbstractModel> __result)
    {
        if (NextFrameWalk() is { Piles: >= 2 })
        {
            _snapFlips++;
            __result = DoubleGauntlets(__result);
        }
    }

    private static IEnumerable<AbstractModel> DoubleGauntlets(IEnumerable<AbstractModel> walk)
    {
        foreach (AbstractModel model in walk)
        {
            yield return model;
            if (model.GetType().Name == "SpikedGauntlets")
            {
                yield return model;
            }
        }
    }

    private static void WalkSnapRngPostfix(ref IEnumerable<AbstractModel> __result)
    {
        if (NextFrameWalk() != null)
        {
            __result = DrawWhenEnumerated(__result);
        }
    }

    /// <summary>The walk's output, drawing the run's Niche RNG once when it starts being enumerated (a lazy side effect).</summary>
    private static IEnumerable<AbstractModel> DrawWhenEnumerated(IEnumerable<AbstractModel> walk)
    {
        if (RunManager.Instance.DebugOnlyGetState() is { } run)
        {
            run.Rng.Niche.NextInt();
            _snapRngDraws++;
        }
        foreach (AbstractModel model in walk)
        {
            yield return model;
        }
    }

    private static IEnumerable<AbstractModel> DoubleSqueezes(IEnumerable<AbstractModel> walk)
    {
        foreach (AbstractModel model in walk)
        {
            yield return model;
            if (model is Squeeze)
            {
                yield return model;
            }
        }
    }

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
        if (Tables.ExtraPiles.TryGetValue(__instance, out CardPile? extra))
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

        /// <summary>Squeeze's full name, as the full-name shape tests it (checked against the type at installation).</summary>
        public const string SqueezeFullName = "MegaCrit.Sts2.Core.Models.Cards.Squeeze";

        public static void SqueezeNameGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
        {
            if (__instance.GetType().Name != "Squeeze")
            {
                return;
            }
            Count("name-gated-postfix squeeze " + Name(__originalMethod));
        }

        public static void SqueezeFullNameGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
        {
            if (__instance.GetType().FullName == SqueezeFullName)
            {
                Count("fullname-gated-postfix squeeze " + Name(__originalMethod));
            }
        }

        /// <summary>A call before the name test: it runs for every instance, so it isn't gated (every call counted, a STATE line).</summary>
        public static void SqueezeNameNearMissPostfix(AbstractModel __instance, MethodBase __originalMethod)
        {
            Count("name-nearmiss-postfix every call " + Name(__originalMethod));
            if (__instance.GetType().Name != "Squeeze")
            {
                return;
            }
            Count("name-nearmiss-postfix squeeze " + Name(__originalMethod));
        }

        /// <summary>The name test on another argument (the drawn card), not the listener: not gated on the listener.</summary>
        public static void SqueezeNameOtherArgumentPostfix(AbstractModel __instance, CardModel card, MethodBase __originalMethod)
        {
            if (card.GetType().Name != "Squeeze")
            {
                return;
            }
            Count("name-othervar-postfix squeeze card " + Name(__originalMethod));
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
    /// <summary>
    /// RitsuLib's choice-context constructor postfix and its near misses (HookSkip's helpers): the accepted one in a class with a
    /// static field initializer and no static constructor, which C# marks <c>beforefieldinit</c> (ECMA-335 II.10.5.3.2).
    /// </summary>
    private static class SourceGate
    {
        /// <summary>A field initializer: a type initializer, run at or before the first read of a static field (beforefieldinit).</summary>
        private static readonly string Key = "ctor-source-gated squeeze";

        /// <summary>The control ctor-read-then-gated's store (no initializer).</summary>
        public static bool LastHadCombat;

        /// <summary>RitsuLib's shape: nothing unless the listener (source) is of the gate type.</summary>
        public static void Postfix(HookPlayerChoiceContext __instance, AbstractModel source, MegaCrit.Sts2.Core.Combat.ICombatState? combatState)
        {
            if (source is Squeeze)
            {
                Count(Key);
            }
        }

        /// <summary>The test on combatState, not the listener: not a listener gate.</summary>
        public static void CombatStateGatedPostfix(AbstractModel source, MegaCrit.Sts2.Core.Combat.ICombatState? combatState)
        {
            if (combatState is MegaCrit.Sts2.Core.Combat.CombatState)
            {
                Count("ctor-combatstate-gated combat");
            }
        }

        /// <summary>The test on __instance (the context), the listener taken too: a helper's instance isn't the listener.</summary>
        public static void InstanceGatedPostfix(object __instance, AbstractModel source)
        {
            if (__instance is Squeeze)
            {
                Count("ctor-instance-gated squeeze");
            }
        }

        /// <summary>combatState read and stored before the test: it runs for every listener.</summary>
        public static void ReadThenGatedPostfix(AbstractModel source, MegaCrit.Sts2.Core.Combat.ICombatState? combatState)
        {
            LastHadCombat = combatState != null;
            if (source is Squeeze)
            {
                Count("ctor-read-then-gated squeeze");
            }
        }
    }

    /// <summary>The source-gated shape in a class with an explicit static constructor (not beforefieldinit): its first call runs it.</summary>
    private static class SourceCctorGate
    {
        private static readonly long Started;

        static SourceCctorGate()
        {
            Started = Environment.TickCount64;
        }

        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Count("ctor-cctor-gated squeeze " + (Started != 0));
            }
        }
    }

    /// <summary>
    /// Rule A's shapes (2026-11-17) on HookSkip's Hook methods, which this mod only transpiles: every prefix and postfix stands
    /// there. Counted per target (STATE lines: the same with this mod on or off); no initializer.
    /// </summary>
    private static class HookShapes
    {
        public static bool ReplaceBlockPrefix(decimal block, ref IEnumerable<AbstractModel> modifiers, ref decimal __result)
        {
            if (decimal.Truncate(block) % 2 != 0 || block <= 0)
            {
                return true;
            }
            __result = block + 1;
            modifiers = Array.Empty<AbstractModel>();
            Count("hook-replacing-prefix ModifyBlock replaced");
            return false;
        }

        public static void HpLostWritePostfix(Creature target, ref IEnumerable<AbstractModel> modifiers, ref decimal __result)
        {
            modifiers = modifiers?.ToList() ?? new List<AbstractModel>();
            if (target.IsMonster && __result > 0)
            {
                __result += 1;
                Count("hook-out-write-postfix ModifyHpLost monster hp losses");
            }
        }

        public static void TargetingWritePostfix(ref AbstractModel? preventer, bool __result)
        {
            AbstractModel? same = preventer;
            preventer = same;
            Count("hook-out-write-postfix ShouldAllowTargeting " + __result);
        }

        public static void ArgsPrefix(object[] __args, MethodBase __originalMethod) => Count($"hook-args-prefix {Name(__originalMethod)} {__args.Length}");

        public static void NoOpFinalizer(Exception? __exception)
        {
        }

        /// <summary>Counts each relic sound (sfx naming a relic) with its volume; suppresses the general relic flash sound.</summary>
        public static bool SfxObserverPrefix(string sfx, float volume)
        {
            if (sfx == null || !sfx.Contains("relic", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            Count($"sfx-observer-prefix {sfx} volume {volume}");
            if (sfx == "event:/sfx/ui/relic_activate_general")
            {
                Count("sfx-observer-prefix suppressed");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The source-gated shape in a beforefieldinit class (no explicit static constructor) whose field initializer reads the clock:
    /// a time-dependent initializer the runtime may run at another time with this mod. Must back off (fix 2, 2026-11-17).
    /// </summary>
    private static class SourceImpureGate
    {
        private static readonly long Started = Environment.TickCount64;

        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Count("ctor-impure-cctor-gated squeeze " + (Started != 0));
            }
        }
    }

    /// <summary>
    /// The source-gated shape in a beforefieldinit class whose initializer looks a method up by name (P1: AccessTools.Method can
    /// throw on an ambiguous name, at whatever time the runtime runs the initializer). Must back off.
    /// </summary>
    private static class SourceThrowingGate
    {
        private static readonly MethodInfo? Lookup = AccessTools.Method(typeof(Squeeze), "OnUpgrade");

        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Count("ctor-throwing-cctor-gated squeeze " + (Lookup != null));
            }
        }
    }

    /// <summary>B2: the source-gated shape using a non-capturing lambda on its gated path (Roslyn's cached <c>&lt;&gt;c</c> delegate). Stands.</summary>
    private static class SourceLambdaGate
    {
        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Func<string, string> name = text => "ctor-lambda-gated " + text;
                Count(name("squeeze"));
            }
        }
    }

    /// <summary>B3: the source-gated shape calling HarmonyLib's AccessTools on its gated path (initialized by Harmony itself). Stands.</summary>
    private static class SourceAccessToolsGate
    {
        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Count("ctor-accesstools-gated squeeze " + (AccessTools.Field(typeof(Squeeze), "_nonexistent") == null));
            }
        }
    }

    /// <summary>
    /// A3: the source-gated shape in a beforefieldinit class whose initializer throws (concurrency level 0). Backs off. Its gated path
    /// catches the TypeInitializationException (proof run 3: uncaught, it threw out of HookPlayerChoiceContext's constructor, as it
    /// would without this mod, and killed the combat's turn loop at the next end of turn): the initializer is judged the same.
    /// </summary>
    private static class SourceCapacityGate
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Table = new(0, 4);

        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                string count;
                try
                {
                    count = Table.Count.ToString();
                }
                catch (TypeInitializationException)
                {
                    count = "initializer threw";
                }
                Count("ctor-capacity-cctor-gated squeeze " + count);
            }
        }
    }

    /// <summary>A2: the source-gated shape calling an own-class helper that reads <see cref="ImpureClock"/> (inlining). Backs off.</summary>
    private static class SourceHelperReachGate
    {
        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Count("ctor-helper-reach-gated squeeze " + Started());
            }
        }

        private static bool Started() => ImpureClock.Started != 0;
    }

    /// <summary>The clock read by a beforefieldinit initializer, reached by <see cref="SourceReachGate"/>'s gated path.</summary>
    private static class ImpureClock
    {
        public static readonly long Started = Environment.TickCount64;
    }

    /// <summary>
    /// B1 (re-audit round 2 of 2026-11-17): the null-gated postfix shape on NCardFlyShuffleVfx.Create in a beforefieldinit class
    /// whose initializer reads the clock, its non-null path reading <see cref="ImpureClock"/> (RitsuLib's start-position postfix
    /// reaching ModCardPileRegistry, whose initializer creates a Lock and dictionaries of mod types). Stands as NullSkipped:
    /// counted per real soul (FOREIGN lines: a skipped soul makes none).
    /// </summary>
    private static class SoulReachShape
    {
        private static readonly long Started = Environment.TickCount64;

        public static long Seen;

        public static void Postfix(ref NCardFlyShuffleVfx? __result)
        {
            if (__result != null && Started + ImpureClock.Started != 0)
            {
                Seen++;
            }
        }
    }

    /// <summary>
    /// The source-gated shape in a class with no initializer, reading another class's static whose beforefieldinit initializer
    /// reads the clock (P2: compiling the postfix may run that initializer). Must back off.
    /// </summary>
    private static class SourceReachGate
    {
        public static void Postfix(AbstractModel source)
        {
            if (source is Squeeze)
            {
                Count("ctor-reach-gated squeeze " + (ImpureClock.Started != 0));
            }
        }
    }

    /// <summary>
    /// Gates on PushModel's / PopModel's listener argument (HookSkip's helpers), no initializer. Counted per method: the same
    /// with this mod on or off where they stand. Proof run 2: PushModel (32 bytes of IL) patched mid-session, after the game's
    /// JIT compiled callers with its body inlined, is missed by those callers in the game itself (83,399 of 119,397 calls with
    /// the mod off) while this mod's replacements see every call (InlineGuard); so a late PushModel postfix's count is a FOREIGN
    /// display line (not compared), checked on the mod's side against PopModel's (<c>require pushpop</c>), and the same patches
    /// installed at the launch (<c>launchforeign model-gated-pushpop</c>, before any caller is compiled) are STATE lines.
    /// Proof run 3: the game itself doesn't pop every push. A new context's own first push (HookPlayerChoiceContext's constructor
    /// pushes its source, BranchingPlayerChoiceContext's creator its card or potion, v0.113) is never popped: the context is
    /// dropped (mod off at the launch: 348,912 pushes, 324,560 pops). Those pushes are counted apart (<see cref="OpensContext"/>).
    /// </summary>
    private static class ModelGate
    {
        public static void Postfix(object __instance, AbstractModel model, MethodBase __originalMethod)
        {
            if (model is Squeeze)
            {
                string key = "model-gated-postfix squeeze " + Name(__originalMethod) + (OpensContext(__instance, __originalMethod) ? " opening a context" : "");
                if (__originalMethod.Name == "PushModel" && !AtLaunch)
                {
                    _displayCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
                }
                else
                {
                    Count(key);
                }
            }
        }

        public static void Prefix(AbstractModel model, MethodBase __originalMethod)
        {
            if (model is Squeeze)
            {
                Count("model-gated-prefix squeeze " + Name(__originalMethod));
            }
        }

        /// <summary>Installed at the launch (<see cref="InstallAtLaunch"/>): every count a STATE line.</summary>
        public static bool AtLaunch;
    }

    /// <summary>
    /// <c>launchforeign model-gated-pushpop</c> (ForeignLaunch): the PushModel postfix and the PopModel prefix gated on their
    /// <c>model</c>, in before this mod applies and before any caller of theirs is compiled: on and off see the same calls.
    /// </summary>
    internal static void InstallModelGatesAtLaunch(Harmony harmony)
    {
        ModelGate.AtLaunch = true;
        MethodInfo push = AccessTools.DeclaredMethod(typeof(PlayerChoiceContext), "PushModel") ?? throw new MissingMethodException(nameof(PlayerChoiceContext), "PushModel");
        MethodInfo pop = AccessTools.DeclaredMethod(typeof(PlayerChoiceContext), "PopModel") ?? throw new MissingMethodException(nameof(PlayerChoiceContext), "PopModel");
        harmony.Patch(push, postfix: new HarmonyMethod(typeof(ModelGate), nameof(ModelGate.Postfix)));
        harmony.Patch(pop, prefix: new HarmonyMethod(typeof(ModelGate), nameof(ModelGate.Prefix)));
    }

    /// <summary>
    /// <c>require pushpop</c>: the PushModel postfix saw as many Squeeze pushes as the PopModel prefix saw pops (every call seen;
    /// both installed at one script line or both at the launch, between game actions), a new context's own first push aside (the
    /// game never pops it: <see cref="ModelGate"/>).
    /// </summary>
    private static string[] RequirePushPop()
    {
        const string pushKey = "model-gated-postfix squeeze PlayerChoiceContext.PushModel", popKey = "model-gated-prefix squeeze PlayerChoiceContext.PopModel";
        const string openKey = pushKey + " opening a context";
        long Get(string key) => _counts.TryGetValue(key, out long c) ? c : _displayCounts.TryGetValue(key, out long d) ? d : 0;
        long push = Get(pushKey), opened = Get(openKey);
        long pop = _counts.TryGetValue(popKey, out long q) ? q : 0;
        if (push == 0 || push != pop)
        {
            throw new InvalidOperationException($"PushModel's gated postfix saw {push} Squeeze pushes (and {opened} opening a context, never popped), PopModel's gated prefix {pop} pops: not every call seen");
        }
        return new[] { $"FOREIGN pushpop PushModel {push} = PopModel {pop}: every call seen ({opened} pushes opening a context aside)" };
    }

    /// <summary>The context's model stack field (read by <see cref="OpensContext"/>; set at its first use: no initializer).</summary>
    private static FieldInfo? _modelStackField;

    /// <summary>
    /// A push that is a new context's own first one (never popped by the game): PushModel on a HookPlayerChoiceContext or a
    /// BranchingPlayerChoiceContext whose stack holds only that model after it. Later pushes on such a context find its first
    /// model below (the game never pops it), and other contexts' first pushes are popped (a card's play, a potion's use).
    /// </summary>
    private static bool OpensContext(object instance, MethodBase original)
    {
        if (original.Name != "PushModel" || instance is not PlayerChoiceContext context
            || context.GetType().Name is not ("HookPlayerChoiceContext" or "BranchingPlayerChoiceContext"))
        {
            return false;
        }
        _modelStackField ??= AccessTools.Field(typeof(PlayerChoiceContext), "_modelStack");
        return _modelStackField?.GetValue(context) is System.Collections.ICollection { Count: 1 };
    }

    /// <summary>
    /// The trail field reference trail-replacing-prefix writes, in its own class: a FieldRefAccess initializer isn't one
    /// PatchSafety.InitializerFree accepts (it can throw, at whatever time the runtime runs it), and SoulShapes holds the
    /// accepted null-gated postfix (its class's initializer must stay a plain constant store).
    /// </summary>
    private static class SoulTrail
    {
        public static readonly AccessTools.FieldRef<NCardTrailVfx, Godot.Control> NodeToFollow =
            AccessTools.FieldRefAccess<NCardTrailVfx, Godot.Control>("_nodeToFollow");
    }

    /// <summary>
    /// CardFlightLimit's soul shapes (RitsuLib's start-position postfix and trail override) and the counts a scenario checks.
    /// FOREIGN lines only: a skipped soul makes none of them, so they differ with the mod off.
    /// </summary>
    private static class SoulShapes
    {
        public static long SoulsSeen, SoulsAdded, NullResults, TrailsBuilt;

        /// <summary>A static read before the null test (the control): on a null result it still runs that read.</summary>
        public static bool Enabled = true;

        private static Godot.SceneTree? _tree;


        /// <summary>RitsuLib's shape: nothing for a null result (a skipped soul, as in the game's test mode).</summary>
        public static void NullGatedPostfix(ref NCardFlyShuffleVfx? __result)
        {
            if (__result != null)
            {
                SoulsSeen++;
            }
        }

        /// <summary>The control: a static field read before the null test.</summary>
        public static void NearMissPostfix(ref NCardFlyShuffleVfx? __result)
        {
            if (Enabled && __result == null)
            {
                NullResults++;
            }
        }

        /// <summary>RitsuLib's trail override: the trail built here from the same scene for every card, the original skipped.</summary>
        public static bool TrailPrefix(Godot.Control card, string characterTrailPath, ref NCardTrailVfx? __result)
        {
            NCardTrailVfx trail = MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetScene(characterTrailPath)
                .Instantiate<NCardTrailVfx>(Godot.PackedScene.GenEditState.Disabled);
            SoulTrail.NodeToFollow(trail) = card;
            __result = trail;
            TrailsBuilt++;
            return false;
        }

        /// <summary>Counts the souls entering the scene tree from now on (a signal, not a patch: no other mod's patch to judge).</summary>
        public static void WatchTree()
        {
            if (_tree == null && Godot.Engine.GetMainLoop() is Godot.SceneTree tree)
            {
                _tree = tree;
                tree.NodeAdded += OnNodeAdded;
            }
        }

        public static void UnwatchTree()
        {
            if (_tree != null)
            {
                _tree.NodeAdded -= OnNodeAdded;
                _tree = null;
            }
        }

        private static void OnNodeAdded(Godot.Node node)
        {
            if (node is NCardFlyShuffleVfx)
            {
                SoulsAdded++;
            }
        }
    }
}
