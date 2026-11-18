using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Every <c>RelicModel.Flash()</c> spawns a particle effect over the relic bar (<c>NRelicInventoryHolder</c>), plus a
/// sound and an effect on the target creature (<c>Player.OnRelicFlashed</c>). A relic that triggers once per card
/// (Bing Bong copying each card cloned at a campfire) flashes thousands of times in the same frame: thousands of GPU
/// particle systems stacked on the same spot, which blacks out the screen (the renderer runs out of descriptors) and
/// can crash the game.
///
/// The same relic flashing again within one frame, on the same creature, now shows one flash: the extra ones would
/// draw identical effects at the same place and time. Sound and visuals only, nothing gameplay-related.
///
/// What a merged flash leaves out is computed from the game's code (<see cref="CodeSet"/>) and pinned per game version: the
/// relic bar's effect (the holder's handler, <c>DoFlash</c> and every game method they reach: the scene cache, the run's UI,
/// the relic's icon), and the creature effect (<c>NRelicFlashVfx</c>'s whole lifetime and what it reaches: the combat room's
/// creature node, its hitbox, the task helper; <c>Creature.GetVfxContainer</c>, <c>AddChildSafely</c>). The sound (rule D) is
/// replayed (<see cref="Replay"/>): exact.
///
/// The relic-flash display premise (owner-approved 2026-11-17 for the relic bar's <c>DoFlash</c>, extended in the same spirit
/// to the creature effect; DESIGN "Owner-approved premises"): other mods' patches on those two visual sets, and other mods'
/// overrides of the game virtual methods they call (a modded relic's icon path), only change what a flash shows: a merged flash
/// leaves them out as it leaves the game's own effect out. Strict still: the two handlers themselves (patches there see the
/// whole call skipped, sound included), the random number generators the sets reach (<see cref="StrictMember"/>: a patch on a
/// draw is never taken as display), the sets' review per game version, the effects' scenes carrying only the reviewed scripts
/// (<see cref="ShownNodeKnown"/>), and everything with <see cref="PatchSafety.ShapeRules"/> off (every patch or override on
/// the whole sets keeps every flash, the relic bar scene's script <c>NVfxParticleSystem</c> and what it reaches included, as
/// before the premise). Shown as a kept note (bdopt why, the menu's hover), not a back-off.
/// </summary>
public static class RelicFlashMerge
{
    public const string Name = "RelicFlashMerge";

    /// <summary>v0.107.1 handlers skipped for repeats: they only create effects and play a sound.</summary>
    private const string HolderFlashFingerprint = "266F6745573AB781";
    private const string HolderDoFlashFingerprint = "1F67DBB3C0633077";
    private const string PlayerFlashFingerprint = "EE9B58B0BB307D27";

    /// <summary>
    /// The sets' fingerprints reviewed on the decompiled game (tools/fingerprint <c>closure</c>, same roots), per version:
    /// v0.107.1, v0.111.0, v0.113.1. Sound: <c>SfxCmd.Play(string, float)</c> down to <c>NAudioManager.PlayOneShot</c> (both
    /// overloads), the combat's end check it makes (<c>CombatManager.IsEnding</c>, which walks the combat's listeners through
    /// <c>Hook.ShouldStopCombatFromEnding</c>), <c>RelicModel.FlashSfx</c> and every game override of it (roots: tools/fingerprint closure "+RelicModel::get_FlashSfx()"); its delegates: the test
    /// hook <c>NonInteractiveMode.AutoSlayerCheck</c> and the mods' combat hook subscriptions (<see cref="GameHookDelegates"/>). Its
    /// interface and abstract callees count too (A1, re-audit round 2: a mod's <c>ICombatState</c>).
    /// Visual sets: their delegates are the log's listeners and the task helper's exception callback, reached on error paths
    /// only (an exception inside the effect's own code).
    /// </summary>
    private static readonly string[] SoundSets = { "2873577A4A091527", "0AD730D8720F68E2", "299951257EABB67E" };
    private static readonly string[] PlayerVisualSets = { "B6B57C3676C5CC07", "B697AD41E405A702", "406A71DF5B8272CB" };
    /// <summary>
    /// The relic bar effect's set: the handler, <c>DoFlash</c> and the script of the scene it instantiates (relic_inventory_flash_vfx's
    /// root, <c>NVfxParticleSystem</c>: its <c>_Ready</c>, <c>TryPlayParticles</c>, <c>AfterExpired</c> and what they reach, the node
    /// pool's free path among it), roots <c>NRelicInventoryHolder::OnRelicFlashed</c>, <c>::DoFlash</c>, <c>NVfxParticleSystem::*</c>
    /// (2026-11-17; 388, 385 and 436 methods). The scene's scripts are checked at the first merge (<see cref="ShownNodeKnown"/>).
    /// </summary>
    private static readonly string[] HolderVisualSets = { "3E6CEA48350E180B", "FF7071D0C212513F", "A08B6E492A7A55A6" };

    /// <summary>
    /// The flash scene's script (<c>NVfxParticleSystem</c>'s declared methods with a body), the holder set's roots with the
    /// handler and <c>DoFlash</c>. Re-audit round 2 (A1) checked its own code strictly and pinned it on its own; since the merge
    /// with the display premise it is part of the relic bar effect's set (pinned with it): under the premise with the shape rules
    /// on, strict with them off as the whole set (what it reaches included: shared node code BaseLib and Limitless patch for
    /// their own nodes keeps every relic bar flash then).
    /// </summary>
    private static System.Reflection.MethodBase[] _holderScript = Array.Empty<System.Reflection.MethodBase>();

    /// <summary>A bound on each set (the largest is ~520 methods on v0.113.1): more means the code changed beyond review.</summary>
    private const int SetLimit = 1500;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Merged { get; private set; }

    private static System.Reflection.MethodInfo? _holderFlash;
    private static System.Reflection.MethodInfo? _playerFlash;
    private static System.Reflection.MethodInfo? _doFlash;
    private static bool _holderPatched, _playerPatched;
    private static int _mainThread;

    /// <summary>
    /// A key's last flash shown (Frame, Target) and the one its handler is showing now (Pending: A5); the effect node that shown
    /// flash created (B1, re-audit of the premises: its instance id, 0 when none was seen, <see cref="NoNode"/> when the handler
    /// provably left no node: R3-1) and whether its scripts were checked known in this frame.
    /// </summary>
    private sealed class Last
    {
        public ulong Frame = ulong.MaxValue;
        public Creature? Target;
        public ulong PendingFrame = ulong.MaxValue;
        public Creature? PendingTarget;
        public ulong ShownNode, PendingNode;
        public ulong CheckedFrame = ulong.MaxValue;
        public bool CheckedKnown;
    }

    /// <summary>
    /// R3-1 (re-audit round 3): the shown flash's handler left no node at all (out of combat <c>Creature.GetVfxContainer()</c> is
    /// null and the game's <c>?.</c> skips <c>NRelicFlashVfx.Create</c>: a rest site, Bing Bong's campfire clone). A merge with it
    /// leaves no node's code out: allowed (<see cref="ShownNodeKnown"/>).
    /// </summary>
    private const ulong NoNode = ulong.MaxValue;

    private static readonly ConditionalWeakTable<NRelicInventoryHolder, Last> _holders = new();
    private static readonly ConditionalWeakTable<RelicModel, Last> _relics = new();

    public static void Apply(Harmony harmony)
    {
        _holderFlash = Reflect.Method(typeof(NRelicInventoryHolder), "OnRelicFlashed")
            ?? throw new MissingMethodException(nameof(NRelicInventoryHolder), "OnRelicFlashed");
        _playerFlash = Reflect.Method(typeof(Player), "OnRelicFlashed")
            ?? throw new MissingMethodException(nameof(Player), "OnRelicFlashed");
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        _doFlash = Reflect.Method(typeof(NRelicInventoryHolder), "DoFlash");
        bool holder = _doFlash != null && Known(_holderFlash, HolderFlashFingerprint) && Known(_doFlash, HolderDoFlashFingerprint);
        bool player = Known(_playerFlash, PlayerFlashFingerprint);
        if (!holder && !player)
        {
            throw new PatchSkippedException("neither handler has the known code");
        }
        // The game's own call Player.OnRelicFlashed makes (v0.107.1, v0.111.0 and v0.113.1 alike, pinned by PlayerFlashFingerprint):
        // SfxCmd.Play(relic.FlashSfx) then, per target, target.GetVfxContainer()?.AddChildSafely(NRelicFlashVfx.Create(relic,
        // target)). The sound is replayed (rule D) when anything of its set could see the difference.
        _sfxPlay = Reflect.Method(typeof(MegaCrit.Sts2.Core.Commands.SfxCmd), nameof(MegaCrit.Sts2.Core.Commands.SfxCmd.Play), new[] { typeof(string), typeof(float) });
        _flashSfx = Reflect.Method(typeof(RelicModel), "get_" + nameof(RelicModel.FlashSfx));
        _play = _sfxPlay == null ? null : (Action<string, float>)Delegate.CreateDelegate(typeof(Action<string, float>), _sfxPlay);
        if (holder)
        {
            const System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            Type script = typeof(MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.NVfxParticleSystem);
            _holderScript = script.GetMethods(declared).Cast<System.Reflection.MethodBase>().Concat(script.GetConstructors(declared))
                .Where(m => m.GetMethodBody() != null).ToArray();
            // A5: the postfix records a flash shown once the handler returned (not on its exception).
            PatchGuard.Patch(harmony, _holderFlash, prefix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(HolderPrefix)),
                postfix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(Shown)), finalizer: new HarmonyMethod(typeof(RelicFlashMerge), nameof(EndCapture)));
            _holderPatched = true;
        }
        if (player)
        {
            PatchGuard.Patch(harmony, _playerFlash, prefix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(PlayerPrefix)),
                postfix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(Shown)), finalizer: new HarmonyMethod(typeof(RelicFlashMerge), nameof(EndCapture)));
            _playerPatched = true;
        }
        Applied = true;
        // What Usable asks about (display only): the handlers at once, the computed sets once built (NoteOverrides, a note step:
        // the menu's frames, never the game's loading; ~0.1-0.2 s each). With the shape rules on, only what stays strict under
        // the display premise (the handlers, the sets' random number generators); the rest is the kept note (NoteDisplay). With
        // them off, DoFlash and the flash scene's script at once too.
        PatchSafety.WatchAll(Name, () => (_holderPatched ? new System.Reflection.MethodBase?[] { _holderFlash } : Array.Empty<System.Reflection.MethodBase?>())
            .Concat(_holderPatched && !PatchSafety.ShapeRules ? new System.Reflection.MethodBase?[] { _doFlash }.Concat(_holderScript) : Array.Empty<System.Reflection.MethodBase?>())
            .Concat(_playerPatched ? new System.Reflection.MethodBase?[] { _playerFlash } : Array.Empty<System.Reflection.MethodBase?>())
            .Concat(_sets is { } sets ? (_holderPatched ? Watched(sets.Holder, sets.HolderStrict, sets.HolderGuards) : Array.Empty<System.Reflection.MethodBase>())
                .Concat(_playerPatched ? Watched(sets.Player, sets.PlayerStrict, sets.PlayerGuards) : Array.Empty<System.Reflection.MethodBase>()).Cast<System.Reflection.MethodBase?>()
                : Array.Empty<System.Reflection.MethodBase?>()));
        // The sound set: strict with the shape rules off, as before rule D; with them, replayed (no back-off to show).
        if (player && !PatchSafety.ShapeRules)
        {
            PatchSafety.Watch(Name, _sfxPlay);
        }
        PatchSafety.WatchNote(Name, NoteOverrides);
        PatchSafety.WatchNote(Name, NoteDisplay);
    }

    /// <summary>The methods of a visual set the strict checks (and the menu's back-off rows) ask about now.</summary>
    private static System.Reflection.MethodBase[] Watched(CodeSet set, System.Reflection.MethodBase[] strict,
        Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> guards) =>
        PatchSafety.ShapeRules ? (guards.Count == 0 ? strict : strict.Where(m => !Unreachable(m, guards)).ToArray()) : set.Methods;

    /// <summary>
    /// A visual set's method that stays strict under the display premise (a patch there is never taken as display): the game's
    /// random number generators (<c>MegaCrit.Sts2.Core.Random</c>: <c>Rng</c>, <c>MegaRandom</c>; an icon getter may draw from
    /// <c>Rng.Chaotic</c>: a patch there sees every draw), and, since the re-audit of the premises (A1), the game's state and save
    /// code the sets reach: everything under <c>MegaCrit.Sts2.Core.Saves</c> (the save managers, <c>ProgressState</c>, the stats and
    /// migration classes, <c>CloudSaveStore</c>: reached on lazy initialization and error paths, not on a normal flash, writes
    /// there included) and the members of <c>RunState</c>, <c>CombatState</c> and <c>Creature</c> (getters on a flash's path, the
    /// creature's <c>GetVfxContainer</c> among them). Nested classes (lambdas, display classes) count as their outer class. No
    /// Workshop mod checked patches any of them (decompiled/mods, 2026-11-17): BaseLib's and Limitless's patches in the sets
    /// (<c>ModelDb.GetEntry</c>, <c>AllCharacters</c>, <c>PackedIconPath</c>, <c>TweenHelper.AwaitFinished(Internal)</c>,
    /// <c>ConsoleLogPrinter.Print</c>, <c>NCard.OnFreedToPool</c>) stay under the premise.
    /// </summary>
    private static bool StrictMember(System.Reflection.MethodBase method)
    {
        Type? type = method.DeclaringType;
        while (type?.DeclaringType != null)
        {
            type = type.DeclaringType;
        }
        if (type == null)
        {
            return false;
        }
        string ns = type.Namespace ?? "";
        return ns == "MegaCrit.Sts2.Core.Random" || ns == "MegaCrit.Sts2.Core.Saves" || ns.StartsWith("MegaCrit.Sts2.Core.Saves.", StringComparison.Ordinal)
            || type == typeof(MegaCrit.Sts2.Core.Runs.RunState) || type == typeof(MegaCrit.Sts2.Core.Combat.CombatState) || type == typeof(Creature);
    }

    /// <summary>The two effects don't depend on each other: a changed one only leaves that one to the game.</summary>
    private static bool Known(System.Reflection.MethodInfo method, string fingerprint)
    {
        string actual = MethodFingerprint.Of(method);
        if (PatchSafety.FingerprintMatches(actual, fingerprint))
        {
            return true;
        }
        Log.Info($"[{ModEntry.Id}] {Name}: {method.DeclaringType?.Name}.{method.Name} changed (fingerprint {actual}, expected {fingerprint}): left vanilla");
        return false;
    }

    /// <summary>The sets, and each visual set's methods strict under the display premise (<see cref="StrictMember"/>).</summary>
    private sealed record ComputedSets(CodeSet Sound, CodeSet Player, CodeSet Holder,
        System.Reflection.MethodBase[] PlayerStrict, System.Reflection.MethodBase[] HolderStrict, System.Reflection.MethodInfo[] StrictVirtuals,
        Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> PlayerGuards, Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> HolderGuards);

    /// <summary>
    /// A strict member a flash can't reach now (with the shape rules on): behind a lazily built instance that already exists
    /// (<see cref="LazyGuard"/>: SaveManager's <c>ConstructDefault</c>, which only <c>SaveManager.Instance</c> calls, and only
    /// before the instance exists). Its patches can't see a merged flash's skip: not a back-off.
    /// </summary>
    private static bool Unreachable(System.Reflection.MethodBase method, Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> guards) =>
        PatchSafety.ShapeRules && guards.TryGetValue(method, out LazyGuard.Guard? guard) && LazyGuard.Shut(guard);

    private static ComputedSets? _sets;
    private static bool _setsFailed;

    /// <summary>
    /// The three sets, computed once on the main thread (the preflight's step on the menu, else the first flash). Null when they
    /// can't be computed (logged once): nothing is merged then.
    /// </summary>
    private static ComputedSets? Sets()
    {
        if (_sets != null || _setsFailed || Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            return _sets;
        }
        try
        {
            const System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            Type vfx = typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NRelicFlashVfx);
            // The effect's whole class: its instances only exist through flashes (its constructor, _Ready, StartVfx's state
            // machine, _ExitTree, the Godot-generated members the engine calls).
            IEnumerable<System.Reflection.MethodBase?> vfxMethods = vfx.GetMethods(declared).Cast<System.Reflection.MethodBase>()
                .Concat(vfx.GetConstructors(declared)).Where(m => m.GetMethodBody() != null);
            // The FlashSfx getter and every game override of it (any relic's class chain: the one a flash runs is the relic's).
            var soundRoots = new List<System.Reflection.MethodBase?> { _sfxPlay, _flashSfx };
            if (_flashSfx != null)
            {
                soundRoots.AddRange(MethodFingerprint.Implementations(typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly, _flashSfx));
            }
            var sound = CodeSet.Compute(Name + " (sound)", soundRoots, SetLimit, SoundSets, interfaces: true);
            System.Reflection.MethodBase?[] playerRoots = vfxMethods.Concat(new System.Reflection.MethodBase?[]
            {
                Reflect.Method(typeof(Creature), nameof(Creature.GetVfxContainer), Type.EmptyTypes),
                Reflect.Method(typeof(MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions), nameof(MegaCrit.Sts2.Core.Helpers.GodotTreeExtensions.AddChildSafely), new[] { typeof(Node), typeof(Node) }),
            }).ToArray();
            var player = CodeSet.Compute(Name + " (creature effect)", playerRoots, SetLimit, PlayerVisualSets);
            // The relic bar effect: the handler, DoFlash, and the whole class of its scene's script (NVfxParticleSystem: its
            // instances in that scene only exist through flashes; the same class elsewhere runs as before, only a merged flash's
            // calls are left out).
            System.Reflection.MethodBase?[] holderRoots = new System.Reflection.MethodBase?[] { _holderFlash, _doFlash }.Concat(_holderScript).ToArray();
            var holderSet = CodeSet.Compute(Name + " (relic bar effect)", holderRoots, SetLimit, HolderVisualSets);
            System.Reflection.MethodBase[] playerStrict = player.Methods.Where(StrictMember).ToArray();
            System.Reflection.MethodBase[] holderStrict = holderSet.Methods.Where(StrictMember).ToArray();
            System.Reflection.MethodInfo[] strictVirtuals = player.Virtuals.Concat(holderSet.Virtuals).Where(StrictMember).Distinct().ToArray();
            // Strict members behind a lazily built instance, per set (a method behind it in one set may be reached otherwise in
            // the other): used with the shape rules on only, asked per flash.
            Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> playerGuards = LazyGuard.Find(playerRoots, player, playerStrict, SetLimit);
            Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> holderGuards = LazyGuard.Find(holderRoots, holderSet, holderStrict, SetLimit);
            if (playerGuards.Count + holderGuards.Count > 0)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: strict members behind a lazily built instance (can't run once it exists): "
                    + string.Join(", ", playerGuards.Concat(holderGuards).GroupBy(p => p.Value.Field).Select(g =>
                        $"{g.Key.DeclaringType?.Name}.{g.Key.Name} ({g.Select(p => p.Key).Distinct().Count()} methods, {g.First().Value.Builder.Name} among them)")));
            }
            _sets = new ComputedSets(sound, player, holderSet, playerStrict, holderStrict, strictVirtuals, playerGuards, holderGuards);
        }
        catch (Exception e)
        {
            _setsFailed = true;
            Log.Warn($"[{ModEntry.Id}] {Name}: what a merged flash leaves out couldn't be computed (nothing merged): {e.Message}");
        }
        return _sets;
    }

    /// <summary>
    /// The relic bar effect: once per holder per frame. <paramref name="__state"/>: the holder's record when its handler runs for
    /// a flash that can be merged with later ones (<see cref="Shown"/> marks it then).
    /// </summary>
    private static bool HolderPrefix(NRelicInventoryHolder __instance, RelicModel __0, out Capture? __state)
    {
        __state = null;
        try
        {
            if (!Usable() || Sets() is not { } sets || !VisualUsable(sets.Holder, sets.HolderStrict, sets.StrictVirtuals, __0, _holderFlash, _doFlash, sets.HolderGuards))
            {
                return true;
            }
            Last last = _holders.GetValue(__instance, _ => new Last());
            if (!Repeat(last, null) || !ShownNodeKnown(last, HolderScene(), typeof(MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.NVfxParticleSystem)))
            {
                __state = BeginCapture(Pending(last, null), HolderScene());
                return true;
            }
            Merged++;
#if BENCH
            MergedHolder++;
            if (last.ShownNode == NoNode)
            {
                MergedNoNode++;
            }
#endif
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>
    /// Postfix of both handlers (A5, re-audit round 2 of 2026-11-17): the flash its prefix let run was shown (the handler
    /// returned), so later ones of this frame on the same target may be merged with it. Not run when the handler throws (a caller
    /// catching it and going on then shows the next flash, as without this mod); nothing to do after a merge (no record passed).
    /// </summary>
    private static void Shown(Capture? __state)
    {
        if (__state?.Record is not { } last)
        {
            return;
        }
        ulong node = last.PendingNode;
        if (node == 0 && __state.LeftNoNode())
        {
            node = NoNode;
        }
        last.Frame = last.PendingFrame;
        last.Target = last.PendingTarget;
        last.ShownNode = node;
        last.CheckedFrame = ulong.MaxValue;
    }

    /// <summary>
    /// The sound and the creature effect: once per relic and target per frame (several targets: never merged). Rule D
    /// (2026-11-17): when anything of the sound's set could see the difference (<see cref="Replay"/>), a merged flash still plays
    /// its sound through the game's own call, <c>SfxCmd.Play(relic.FlashSfx, 1f)</c> (the game's default volume, pinned by
    /// PlayerFlashFingerprint), and only its creature effect is merged. Invariant: those patches and overrides see the same
    /// calls, with the same arguments, in the same order as in the game (one per flash, each before the next flash's), on
    /// v0.107.1, v0.111.0 and v0.113.1 alike (the handler's code, fingerprinted, is the same three calls on each); only the
    /// caller on the stack differs (<see cref="PlaySound"/> instead of the handler). The call is made outside this prefix's
    /// catch, through <see cref="PlaySound"/>: a throw from it (another mod's code) reaches the game's caller as the handler's
    /// own call would have thrown it, before the effect, and PatchGuard doesn't blame this mod for it
    /// (<see cref="GameCallReplayAttribute"/>).
    /// </summary>
    private static bool PlayerPrefix(RelicModel relic, IEnumerable<Creature> targets, out Capture? __state)
    {
        __state = null;
        bool replaySound;
        try
        {
            if (!Usable() || SingleTarget(targets) is not { } target || Sets() is not { } sets
                || !VisualUsable(sets.Player, sets.PlayerStrict, sets.StrictVirtuals, relic, _playerFlash, null, sets.PlayerGuards))
            {
                return true;
            }
            if (!PatchSafety.ShapeRules || _play == null)
            {
                // As before rule D: no tolerance on the sound command (a back-off, recorded), a merged flash skips the sound. A3
                // (re-audit round 2): the sound set's overrides and delegates keep the sound too (what rule D replays otherwise).
                if (PatchSafety.IsPatchedByOthers(_sfxPlay) || sets.Sound.AnyPatched(Caller) || !sets.Sound.Reviewed || sets.Sound.ForeignOverrides()
                    || GameHookDelegates.AnyForeign())
                {
                    return true;
                }
            }
            Last last = _relics.GetValue(relic, _ => new Last());
            if (!Repeat(last, target) || !ShownNodeKnown(last, PlayerScene, typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NRelicFlashVfx)))
            {
                __state = BeginCapture(Pending(last, target), PlayerScene);
                return true;
            }
            Merged++;
#if BENCH
            MergedCreature++;
            if (last.ShownNode == NoNode)
            {
                MergedNoNode++;
            }
#endif
            replaySound = PatchSafety.ShapeRules && _play != null && Replay(sets.Sound);
#if BENCH
            if (replaySound)
            {
                SoundsReplayed++;
            }
#endif
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
        if (replaySound)
        {
            PlaySound(relic);
        }
        return false;
    }

    /// <summary>
    /// The game's own sound call of a merged flash, replayed: <c>SfxCmd.Play(relic.FlashSfx, 1f)</c>, through a delegate to the
    /// method's entry (where Harmony's detour is), never a copy the JIT could inline. Not inlined itself: PatchGuard finds this
    /// frame in an exception's stack and blames nothing outside it.
    /// </summary>
    [GameCallReplay]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void PlaySound(RelicModel relic)
    {
        // C1 (re-audit round 2): not optimized, so the call isn't made a tail call (this frame would leave the stack and PatchGuard
        // would blame this mod for another mod's throw).
        _play!(relic.FlashSfx, 1f);
    }

    /// <summary>
    /// Whether a merged flash replays its sound: the sound's set isn't the reviewed one, another mod patches any of it, overrides
    /// a game virtual method it calls (a modded relic's FlashSfx, a modded listener's end-of-combat check), or its delegates may
    /// reach another mod's code. Never a back-off (nothing recorded): the call is the game's own.
    /// </summary>
    /// <remarks>The sound set's delegates (reviewed per version): the mods' combat hook subscriptions (any: their models' hook code
    /// may run in the combat's end check) and the test hook <c>NonInteractiveMode.AutoSlayerCheck</c> (<see cref="GameHookDelegates"/>).</remarks>
    private static bool Replay(CodeSet sound) => !sound.Reviewed || sound.AnyHasPatches() || sound.ForeignOverrides() || GameHookDelegates.AnyForeign();

    private static System.Reflection.MethodInfo? _sfxPlay, _flashSfx;
    private static Action<string, float>? _play;

    /// <summary>This file's path, as the runtime checks' [CallerFilePath] gives it.</summary>
    private static readonly string Caller = CallerPath();

    private static string CallerPath([CallerFilePath] string path = "") => path;

    /// <summary>On and on the main thread.</summary>
    private static bool Usable() => Enabled && Thread.CurrentThread.ManagedThreadId == _mainThread;

    /// <summary>
    /// A visual set a merge leaves out: reviewed for this game version, no other mod's patch on the handler. With the shape rules
    /// on, the display premise: other mods' patches and overrides on the set are taken as display, but on its strict members
    /// (<paramref name="strict"/>, <paramref name="strictVirtuals"/>: the random number generators). With them off, strict: no
    /// other mod's patch on the set nor on DoFlash, no other mod's override of a game virtual method it calls (those rooted in
    /// RelicModel: only this relic's own class counts).
    /// </summary>
    private static bool VisualUsable(CodeSet set, System.Reflection.MethodBase[] strict, System.Reflection.MethodInfo[] strictVirtuals, RelicModel relic,
        System.Reflection.MethodBase? handler, System.Reflection.MethodBase? doFlash, Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> guards)
    {
        // No tolerance: merged flashes skip calls (a missing method counts as patched).
        if (!set.Reviewed || PatchSafety.IsPatchedByOthers(handler))
        {
            return false;
        }
        if (PatchSafety.ShapeRules)
        {
            bool patched = false;
            foreach (System.Reflection.MethodBase method in strict)
            {
                // Behind a lazily built instance that exists: the flash can't reach it (its patches see nothing skipped).
                if (guards.Count > 0 && Unreachable(method, guards))
                {
                    continue;
                }
                // Every one asked: each back-off recorded (bdopt why names them all).
                patched |= PatchSafety.IsPatchedByOthers(method, Caller);
            }
            return !patched && !StrictOverridden(strictVirtuals);
        }
        if ((doFlash != null && PatchSafety.IsPatchedByOthers(doFlash)) || set.AnyPatched(Caller))
        {
            return false;
        }
        return !set.ForeignOverrides(typeof(RelicModel)) && !set.InstanceOverrides(relic.GetType(), typeof(RelicModel));
    }

    /// <summary>A strict overrides answer for the loaded code (<see cref="LoadedCode.Key"/>), read and replaced whole.</summary>
    private sealed record StrictAnswer(long Key, bool Value);

    private static StrictAnswer? _strictOverridden;

    /// <summary>
    /// Whether another mod overrides a strict virtual method of the sets (none on the reviewed versions); true when it can't tell.
    /// Asked per flash: allocates nothing while the loaded code stays the same (<see cref="LoadedCode.Key"/>).
    /// </summary>
    private static bool StrictOverridden(System.Reflection.MethodInfo[] virtuals)
    {
        if (virtuals.Length == 0)
        {
            return false;
        }
        long key = LoadedCode.Key();
        if (_strictOverridden is { } known && known.Key == key)
        {
            return known.Value;
        }
        bool found = CodeSet.ForeignOverridersOf(virtuals).Length > 0;
        _strictOverridden = new StrictAnswer(key, found);
        return found;
    }

    /// <summary>
    /// The note (bdopt why): other mods' classes overriding a game virtual method a merged flash would leave out keep every
    /// flash (or that relic's): named here, since no patch shows them. Also builds the sets on the menu's frames.
    /// </summary>
    private static void NoteOverrides()
    {
        // With the shape rules on, overrides are under the display premise: named in the kept note instead (NoteDisplay).
        if (Sets() is not { } sets || PatchSafety.ShapeRules)
        {
            PatchSafety.ClearNote(Name, "overrides");
            return;
        }
        string[] types = (_holderPatched ? sets.Holder.ForeignOverriders() : Array.Empty<string>())
            .Concat(_playerPatched ? sets.Player.ForeignOverriders() : Array.Empty<string>()).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
        if (types.Length == 0)
        {
            PatchSafety.ClearNote(Name, "overrides");
            return;
        }
        PatchSafety.Note(Name, "overrides", $"other mods' classes override game code a merged relic flash would leave out ({string.Join(", ", types.Take(8))}"
            + (types.Length > 8 ? $" and {types.Length - 8} more" : "") + "): those flashes (or every flash) are kept", Array.Empty<string>());
    }
    /// <summary>
    /// The kept note of the display premise (bdopt why, the menu's hover): which other mods' patches (or hidden IL hooks) and
    /// overrides on the visual sets a merged flash leaves out. Cleared with the shape rules off (strict back-offs then), when
    /// there is none, and for an effect none of whose flashes is merged because a strict part backs off (the back-off shows
    /// instead).
    /// </summary>
    private static void NoteDisplay()
    {
        var patched = new List<System.Reflection.MethodBase>();
        var behindBuilt = new List<System.Reflection.MethodBase>();
        var overriders = new List<string>();
        // Only while flashes can be merged (G, re-audit of the premises): not once faulted (with the mod off: what would run once on).
        if (PatchSafety.ShapeRules && Applied && !PatchSafety.IsFaulted(Name) && Sets() is { } sets)
        {
            void Collect(CodeSet set, System.Reflection.MethodBase[] strict, System.Reflection.MethodBase? handler,
                Dictionary<System.Reflection.MethodBase, LazyGuard.Guard> guards)
            {
                if (!set.Reviewed || PatchSafety.HasOtherPatches(handler) || strict.Any(m => !Unreachable(m, guards) && PatchSafety.HasOtherPatches(m))
                    || StrictOverridden(sets.StrictVirtuals))
                {
                    return;
                }
                foreach (System.Reflection.MethodBase method in set.Methods)
                {
                    if (method == handler || !PatchSafety.HasOtherPatches(method))
                    {
                        continue;
                    }
                    if (!StrictMember(method))
                    {
                        patched.Add(method);
                    }
                    else if (Unreachable(method, guards))
                    {
                        behindBuilt.Add(method);
                    }
                }
                overriders.AddRange(set.ForeignOverriders());
            }
            if (_holderPatched)
            {
                Collect(sets.Holder, sets.HolderStrict, _holderFlash, sets.HolderGuards);
            }
            if (_playerPatched)
            {
                Collect(sets.Player, sets.PlayerStrict, _playerFlash, sets.PlayerGuards);
            }
        }
        NoteBehindBuilt(behindBuilt);
        if (patched.Count == 0 && overriders.Count == 0)
        {
            PatchSafety.ClearKept(Name, "flash_display");
            return;
        }
        var names = patched.Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct().ToList();
        var mods = new List<string>(PatchSafety.OtherModsPatching(patched));
        foreach (System.Reflection.MethodBase method in patched)
        {
            mods.AddRange(HarmonyWatch.HiddenOwners(method));
        }
        string[] types = overriders.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
        if (types.Length > 0)
        {
            // Other mods' classes overriding a game virtual method a merged flash leaves out (a modded relic's icon path).
            names.Add("overridden by " + string.Join(", ", types.Take(6)) + (types.Length > 6 ? $" and {types.Length - 6} more" : ""));
        }
        PatchSafety.Kept(Name, "flash_display", $"other mods' code on game code a relic flash's visuals may reach ({string.Join(", ", names)}) "
            + "doesn't run for the flashes merged where it would have: taken as display only, with no effect on the game's state (a stated "
            + "premise: what a merged flash leaves out is the relic bar's and the creature's flash effects; most of those methods only run on "
            + "a flash's first use or an error path; a mod changing game state there could desync co-op; its sound is still played)", mods.ToArray());
    }

    /// <summary>
    /// The kept note of strict members other mods patch that a flash can't reach (<see cref="Unreachable"/>): named with their
    /// mods (bdopt why, the menu's hover), not a back-off. Cleared when there is none.
    /// </summary>
    private static void NoteBehindBuilt(List<System.Reflection.MethodBase> methods)
    {
        if (methods.Count == 0)
        {
            PatchSafety.ClearKept(Name, "flash_lazy");
            return;
        }
        var mods = new List<string>(PatchSafety.OtherModsPatching(methods));
        foreach (System.Reflection.MethodBase method in methods)
        {
            mods.AddRange(HarmonyWatch.HiddenOwners(method));
        }
        PatchSafety.Kept(Name, "flash_lazy", $"other mods' code on game code that only runs while an instance isn't built yet "
            + $"({string.Join(", ", methods.Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct())}): that instance already exists, "
            + "so a relic flash can't reach it, merged or not (nothing of it is left out)", mods.ToArray());
    }
#if BENCH
    /// <summary>Merged flashes whose sound was played through the game's command (rule D).</summary>
    public static long SoundsReplayed { get; private set; }
    /// <summary>Merged flashes per effect: the relic bar's and the creature's (F2: both must grow where both merge).</summary>
    public static long MergedHolder { get; private set; }
    public static long MergedCreature { get; private set; }
    /// <summary>Merges refused because the frame's shown effect node carried other scripts, or none was seen (B1; F3's control).</summary>
    public static long SceneRefusals { get; private set; }
    /// <summary>Merges with a shown flash whose handler provably left no node (R3-1: out of combat, the creature effect).</summary>
    public static long MergedNoNode { get; private set; }
#endif

    private static Creature? SingleTarget(IEnumerable<Creature> targets)
    {
        if (targets is IReadOnlyList<Creature> { Count: 1 } list)
        {
            return list[0];
        }
        return null;
    }

    /// <summary>Whether the same key's flash was shown this frame on the same target (this one can be merged with it).</summary>
    private static bool Repeat(Last last, Creature? target) => last.Frame == Engine.GetProcessFrames() && last.Target == target;

    /// <summary>The record <see cref="Shown"/> marks once the handler shown this flash returns (A5).</summary>
    private static Last Pending(Last last, Creature? target)
    {
        last.PendingFrame = Engine.GetProcessFrames();
        last.PendingTarget = target;
        return last;
    }

    /// <summary>The creature effect's scene (NRelicFlashVfx's own constant, the same on every reviewed version).</summary>
    private const string PlayerScene = "res://scenes/vfx/relic_flash_vfx.tscn";

    private static string? _holderScenePath;

    /// <summary>The relic bar effect's scene path (<c>NRelicInventoryHolder._flashPath</c>, read once; null when it can't be).</summary>
    private static string? HolderScene()
    {
        if (_holderScenePath == null)
        {
            try
            {
                _holderScenePath = Reflect.Field(typeof(NRelicInventoryHolder), "_flashPath")?.GetValue(null) as string ?? "";
            }
            catch
            {
                _holderScenePath = "";
            }
        }
        return _holderScenePath.Length > 0 ? _holderScenePath : null;
    }

    // ---- B1 (re-audit of the premises): the scripts of the node the frame's first flash actually created ----

    /// <summary>
    /// A handler's capture of the effect node its flash adds (main thread; nested handlers: the outer one's capture waits), also the
    /// handlers' <c>__state</c> token (R3-4: the finalizer ends this capture only, once, wherever it sits in the chain). Pushed when
    /// the scene's path and the tree are known; <see cref="Added"/> counts every node entering the tree while it is in the chain,
    /// <see cref="Orphans"/> is Godot's orphan node count when it began (R3-1).
    /// </summary>
    private sealed class Capture
    {
        public Last? Record;
        public string? Path;
        public ulong Node;
        public Capture? Outer;
        public bool Pushed, Ended;
        public int Added;
        public long Orphans = -1;

        /// <summary>
        /// R3-1: the handler provably left no node (Godot's own counters, no game or mod code): no node entered the tree while it ran
        /// and the orphan node count (nodes alive outside the tree: Godot counts each node made and not added, one waiting for a
        /// deferred <c>AddChildSafely</c> among them) is the one it began with. A node made and freed inside the handler leaves
        /// nothing to show; a node made and kept for a later add raises the orphan count (not seen: no merge). The only miss is a
        /// node made while another orphan node is freed within the same handler: a visual difference at most (one flash's effect).
        /// </summary>
        public bool LeftNoNode()
        {
            if (!Pushed || Ended || Added != 0 || Node != 0 || Orphans < 0)
            {
                return false;
            }
            long now = OrphanCount();
            return now >= 0 && now == Orphans;
        }
    }

    private static Capture? _capture;
    /// <summary>The tree <see cref="_onNodeAdded"/> is connected to (R3-4: never connected twice, disconnected once nothing waits).</summary>
    private static SceneTree? _connected;
    private static readonly SceneTree.NodeAddedEventHandler _onNodeAdded = OnNodeAdded;

    /// <summary>Godot's orphan node count (<c>Performance</c>'s monitor: engine code only); -1 when it can't be read.</summary>
    private static long OrphanCount()
    {
        try
        {
            return (long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// While the handler of a flash that later ones may be merged with runs: the first node entering the tree whose scene file is
    /// the effect's scene (the effect's root, added by the game's own <c>AddChildSafely</c>; Godot's <c>node_added</c>, no game code)
    /// is that flash's node. None seen (an add deferred, another scene): no merge with that flash, unless the handler provably
    /// left no node at all (<see cref="Capture.LeftNoNode"/>). Returns the handlers' token (never null).
    /// </summary>
    private static Capture BeginCapture(Last last, string? path)
    {
        last.PendingNode = 0;
        var capture = new Capture { Record = last, Path = path };
        if (path == null || Engine.GetMainLoop() is not SceneTree tree)
        {
            return capture;
        }
        if (_connected != null && (!GodotObject.IsInstanceValid(_connected) || !ReferenceEquals(_connected, tree)))
        {
            Disconnect();
        }
        if (_connected == null)
        {
            tree.NodeAdded += _onNodeAdded;
            _connected = tree;
        }
        capture.Orphans = OrphanCount();
        capture.Outer = _capture;
        capture.Pushed = true;
        _capture = capture;
        return capture;
    }

    /// <summary>Disconnects the signal (a failure logged once and the tree kept: it stays connected, never connected again).</summary>
    private static void Disconnect()
    {
        SceneTree? tree = _connected;
        if (tree == null)
        {
            return;
        }
        if (!GodotObject.IsInstanceValid(tree))
        {
            _connected = null;
            return;
        }
        try
        {
            tree.NodeAdded -= _onNodeAdded;
            _connected = null;
        }
        catch (Exception e)
        {
            if (_sceneFindings.Add("disconnect: " + e.Message))
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: disconnecting the flash node capture failed (kept connected): {e.Message}");
            }
        }
    }

    private static void OnNodeAdded(Node node)
    {
        try
        {
            Capture? capture = _capture;
            for (Capture? c = capture; c != null; c = c.Outer)
            {
                c.Added++;
            }
            if (capture?.Record != null && capture.Node == 0 && string.Equals(node.SceneFilePath, capture.Path, StringComparison.Ordinal))
            {
                capture.Node = node.GetInstanceId();
                capture.Record.PendingNode = capture.Node;
            }
        }
        catch
        {
            // Display bookkeeping only: no node recorded, no merge with this flash.
        }
    }

    /// <summary>
    /// Finalizer of both handlers (runs on a return and on a throw, before the game's caller sees it): the capture its prefix began
    /// ends, once (R3-4: by its own token, not by its record, which a nested handler of the same key shares), taken out of the chain
    /// wherever it is; Godot's signal disconnected when no capture waits. Void, no exception parameter: Harmony rethrows the
    /// handler's.
    /// </summary>
    private static void EndCapture(Capture? __state)
    {
        try
        {
            if (__state == null || !__state.Pushed || __state.Ended)
            {
                return;
            }
            __state.Ended = true;
            if (ReferenceEquals(_capture, __state))
            {
                _capture = __state.Outer;
            }
            else
            {
                for (Capture? c = _capture; c != null; c = c.Outer)
                {
                    if (ReferenceEquals(c.Outer, __state))
                    {
                        c.Outer = __state.Outer;
                        break;
                    }
                }
            }
            if (_capture == null)
            {
                Disconnect();
            }
        }
        catch (Exception e)
        {
            _capture = null;
            Disconnect();
            Log.Warn($"[{ModEntry.Id}] {Name}: ending a flash's node capture failed: {e.Message}");
        }
    }

    /// <summary>
    /// Whether the effect node this frame's shown flash created carries only the reviewed class's script (<paramref name="script"/>'s
    /// <c>ScriptPath</c>) or none, on every node of its tree (instanced scenes included; at most 8 levels and 64 nodes): what a
    /// merged flash leaves out of the scene's code is then the computed set's. Checked on the node itself (B1: the asset cache and
    /// the scene file can be replaced, a node's script set after it was made), once per key and frame with merges; a node not
    /// seen or no longer valid, a built-in or unknown script, a node of a C# class that isn't Godot's nor the reviewed one: no
    /// merge (logged once per scene and finding). Main thread (the handlers').
    /// </summary>
    private static bool ShownNodeKnown(Last last, string? path, Type script)
    {
        ulong frame = Engine.GetProcessFrames();
        if (last.CheckedFrame == frame)
        {
            return last.CheckedKnown;
        }
        string? why = null;
        try
        {
            string? expected = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<ScriptPathAttribute>(script)?.Path;
            if (path == null || string.IsNullOrEmpty(expected))
            {
                why = "the reviewed script's path unknown";
            }
            else if (last.ShownNode == NoNode)
            {
                // R3-1: the shown flash's handler left no node: a merge leaves no scene code out.
            }
            else if (last.ShownNode == 0 || GodotObject.InstanceFromId(last.ShownNode) is not Node node || !GodotObject.IsInstanceValid(node))
            {
                why = "the shown flash's node not seen";
            }
            else
            {
                int count = 0;
                why = TreeScripts(node, expected, script, 0, ref count);
            }
        }
        catch (Exception e)
        {
            why = "unreadable: " + e.Message;
        }
        last.CheckedFrame = frame;
        last.CheckedKnown = why == null;
        if (why != null)
        {
#if BENCH
            SceneRefusals++;
#endif
            string key = (path ?? "?") + ": " + why;
            if (_sceneFindings.Add(key))
            {
                Log.Info($"[{ModEntry.Id}] {Name}: the effect of {path} isn't the code reviewed ({why}): that frame's flashes are kept");
            }
        }
        return why == null;
    }

    /// <summary>What was logged (one line per scene and finding).</summary>
    private static readonly HashSet<string> _sceneFindings = new(StringComparer.Ordinal);

    /// <summary>Null when every node's script is the expected one or none; else what was found.</summary>
    private static string? TreeScripts(Node node, string expected, Type script, int depth, ref int count)
    {
        if (depth > 8 || ++count > 64)
        {
            return "a larger tree than the scene's";
        }
        Type type = node.GetType();
        if (type.Assembly != typeof(Node).Assembly && type != script)
        {
            return "a node of class " + type.FullName;
        }
        Variant value = node.GetScript();
        if (value.VariantType != Variant.Type.Nil)
        {
            if (value.AsGodotObject() is not Script attached || string.IsNullOrEmpty(attached.ResourcePath) || attached.ResourcePath.Contains("::", StringComparison.Ordinal))
            {
                return "a built-in script";
            }
            if (!string.Equals(attached.ResourcePath, expected, StringComparison.Ordinal))
            {
                return "the script " + attached.ResourcePath;
            }
        }
        foreach (Node child in node.GetChildren(includeInternal: true))
        {
            if (TreeScripts(child, expected, script, depth + 1, ref count) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    public static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
