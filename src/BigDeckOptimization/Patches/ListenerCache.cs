using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <see cref="CombatState.IterateHookListeners"/> and <see cref="RunState.IterateHookListeners"/> rebuild a list of
/// every listener (all combat cards, deck cards, enchantments, relics, powers, mod subscribers...) on each call.
/// Read-only passes call them once or more per card, which is O(cards²):
///   - Snapshot scope: the multiplayer post-action checksum (<see cref="NetFullCombatState.FromRun"/>).
///   - Refresh scope: UI refreshes after every action (hand cards, end turn button, enemy intents, creature state,
///     energy, orbs) and enchantment value recalculation.
///
/// Inside a scope nothing changes the listener set, so we materialize it once and replay it (game logic that resumes inline
/// inside the dispatch scope, a hand selection ended by a state change, runs vanilla: <see cref="SuspendPrefix"/>). As defense in depth, the
/// cache is dropped on every change that can alter the listener set: combat state changes reported by the game, any
/// card pile mutation (deck included), enchantment/affliction changes, relic (incl. melting), potion and orb changes,
/// players becoming active/inactive for hooks, creatures added, removed or reordered and their powers added or removed.
/// Lists are reused across display scopes only while BaseLib card modifiers (changed with nothing watched) can't exist.
/// </summary>
public static class ListenerCache
{
    public const string Name = "ListenerCache";

    /// <summary>Snapshot-scope caching. Toggled by the bench, or off for the session by the fail-safes.</summary>
    public static bool SnapshotEnabled { get; set; } = true;

    /// <summary>Fault recorded when the snapshot verifier saw an optimized snapshot differ from vanilla.</summary>
    public const string SnapshotFault = Name + ".Snapshot";

    /// <summary>Refresh-scope caching. Toggled by the bench, or off for the session by the fail-safes.</summary>
    public static bool RefreshEnabled { get; set; } = true;

    private static MethodInfo? _combatIterate;
    private static MethodInfo? _runIterate;
    private static AccessTools.FieldRef<CardModel, Player?>? _cardOwner;

    private sealed class RefComparer : IEqualityComparer<(object, object?)>
    {
        public bool Equals((object, object?) a, (object, object?) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((object, object?) k) => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Item1), k.Item2 == null ? 0 : RuntimeHelpers.GetHashCode(k.Item2));

        /// <summary>Same models, by reference.</summary>
        public static readonly IEqualityComparer<AbstractModel> Models = ReferenceEqualityComparer.Instance;
    }

    // Main thread in practice; thread-static state keeps any other caller fully vanilla.
    [ThreadStatic] private static int _snapshotDepth;
    [ThreadStatic] private static int _refreshDepth;
    [ThreadStatic] private static bool _materializingCombat;
    [ThreadStatic] private static bool _materializingRun;
    [ThreadStatic] private static bool _suspended;
    [ThreadStatic] private static bool _safe;
    /// <summary>
    /// Other mods' patches on the combat (or run) walk, all let stand (postfixes run on each replay, see <see cref="EnterAny"/>):
    /// the lists are built from FastListeners' unpatched mirrors, and nothing but the game's own call uses them.
    /// </summary>
    [ThreadStatic] private static bool _combatAugmented, _runAugmented;
    /// <summary>
    /// FastListeners' unpatched mirror declined in this augmented scope (<see cref="UnpatchedWalkMissing"/>): the rest of the
    /// scope's augmented walks take the game's call at once, with no exception thrown per walk. Cleared when the next outermost
    /// scope opens (<see cref="EnterAny"/>).
    /// </summary>
    [ThreadStatic] private static bool _mirrorDeclined;
    [ThreadStatic] private static int _version;
    /// <summary>Key: (CombatState, null) or (RunState, child combat state or null).</summary>
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, ReadOnlyCollection<AbstractModel> Listeners)>? _cache;

    /// <summary>True while inside a checksum snapshot where the snapshot optimizations may run.</summary>
    public static bool IsInSnapshot => SnapshotEnabled && _snapshotDepth > 0 && _safe && !_suspended;

    /// <summary>Inside a snapshot scope, whether or not its listener lists are cached (the snapshot reuse patches run either way).</summary>
    public static bool InSnapshotScope => _snapshotDepth > 0 && !_suspended;

    /// <summary>
    /// True inside any scope (snapshot or refresh) where listener lists are cached and may be used directly (HookFilter's
    /// per-hook lists). False while other mods' postfixes on the walks are let stand: a list built here is the unpatched
    /// walk's, only the game's own call (where those postfixes run on it) may use it then.
    /// </summary>
    public static bool IsCaching => Serving && !_combatAugmented && !_runAugmented;

    /// <summary>Inside a scope where the walks' own calls are answered from the cached lists (<see cref="CombatIteratePrefix"/>).</summary>
    private static bool Serving => _safe && !_suspended
        && ((SnapshotEnabled && _snapshotDepth > 0) || (RefreshEnabled && _refreshDepth > 0));

    /// <summary>Raised when the outermost scope ends, so dependent caches can drop their state.</summary>
    public static event Action? ScopeEnded;

    /// <summary><c>OrbQueue.TryEnqueue</c> (v0.107.1, v0.111): adds the orb before its first await.</summary>
    private const string TryEnqueueFingerprint = "DC22B902D9BCF8AA";

    public static void Apply(Harmony harmony)
    {
        // Order matters: if any of these throws, nothing below is patched and the cache never turns on.
        // 1. Invalidation.
        MethodBase?[] invalidators =
        {
            Watched(Reflect.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged"), typeof(CombatStateTracker), "NotifyCombatStateChanged"),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.AddInternal)), typeof(CardPile), nameof(CardPile.AddInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)), typeof(CardPile), nameof(CardPile.RemoveInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.MoveToBottomInternal)), typeof(CardPile), nameof(CardPile.MoveToBottomInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.MoveToTopInternal)), typeof(CardPile), nameof(CardPile.MoveToTopInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)), typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.Clear)), typeof(CardPile), nameof(CardPile.Clear)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.AddRelicInternal)), typeof(Player), nameof(Player.AddRelicInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.RemoveRelicInternal)), typeof(Player), nameof(Player.RemoveRelicInternal)),
            // Both filter what IterateHookListeners yields (melted relics, dead players' models).
            Watched(Reflect.PropertySetter(typeof(RelicModel), nameof(RelicModel.IsMelted)), typeof(RelicModel), nameof(RelicModel.IsMelted)),
            Watched(Reflect.PropertySetter(typeof(Player), nameof(Player.IsActiveForHooks)), typeof(Player), nameof(Player.IsActiveForHooks)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.AddPotionInternal)), typeof(Player), nameof(Player.AddPotionInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.DiscardPotionInternal)), typeof(Player), nameof(Player.DiscardPotionInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)), typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.SubtractFromMaxPotionCount)), typeof(Player), nameof(Player.SubtractFromMaxPotionCount)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)), typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.Remove)), typeof(OrbQueue), nameof(OrbQueue.Remove)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.Insert)), typeof(OrbQueue), nameof(OrbQueue.Insert)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.Clear)), typeof(OrbQueue), nameof(OrbQueue.Clear)),
        };
        // The methods above (or the ones they call) are the only code changing the piles, relics and potion slots: a new
        // one would leave cached lists stale.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        PatchSafety.RequireFieldWriters(typeof(Player), "_relics", PatchSafety.RelicListWriters);
        PatchSafety.RequireFieldWriters(typeof(Player), "_potionSlots", PatchSafety.PotionSlotWriters);
        PatchSafety.RequireFieldWriters(typeof(OrbQueue), "_orbs", ".ctor", "<TryEnqueue>d__#+MoveNext", "Clear", "Insert", "Remove");
        // TryEnqueue is async: its finalizer runs at its first await, which comes after the orb is added (pinned: same in v0.111).
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)), TryEnqueueFingerprint);
        // Finalizers, not postfixes: a method throwing after its change (an event handler of AddInternal, v0.113's arrow
        // slot) must still invalidate; a postfix doesn't run then.
        foreach (MethodBase? target in invalidators)
        {
            PatchGuard.Patch(harmony, Require(target), finalizer: Method(nameof(Invalidate)));
        }
        // Game v0.113: the walks also yield the run's dominion. Every write goes through its setter (the field's only
        // writer: checked), which invalidates. Absent before v0.113.
        foreach (Type owner in new[] { typeof(RunState), typeof(CombatState) })
        {
            if (Reflect.PropertySetter(owner, "Dominion") is { } setDominion)
            {
                PatchSafety.RequireFieldWriters(owner, "<Dominion>k__BackingField", "set_Dominion");
                PatchGuard.Patch(harmony, setDominion, finalizer: Method(nameof(Invalidate)));
            }
            // Game v0.113: the run models the walks yield sit behind a private setter too. The game sets them once (the run's
            // creation, the combat's construction), a mod may set them again later (a framework adding its run models):
            // invalidated the same way. Absent before v0.113 (the walks read them elsewhere, unchanged).
            if (Reflect.PropertySetter(owner, "RunModels") is { } setRunModels)
            {
                PatchSafety.RequireFieldWriters(owner, "<RunModels>k__BackingField", "set_RunModels");
                PatchGuard.Patch(harmony, setRunModels, finalizer: Method(nameof(Invalidate)));
            }
        }
        // The combat's creatures (the walks start from its ally and enemy lists, in that order) and their powers. Every change
        // of them comes today with a watched one (CombatManager.CreaturesChanged, the tracker's power events,
        // History.MonsterPerformedMove right before a monster killed during its move leaves), but a killed creature is
        // unsubscribed from the tracker while its powers are still listed until it leaves: watched here so the cache doesn't
        // rest on that order. Optional: one missing or not patched only loses that watch (the order above still holds).
        foreach ((Type owner, string name) in new[]
        {
            (typeof(CombatState), nameof(CombatState.AddCreature)), (typeof(CombatState), nameof(CombatState.RemoveCreature)),
            (typeof(CombatState), nameof(CombatState.SortEnemiesBySlotName)), (typeof(CombatState), nameof(CombatState.SetEnemyIndex)),
            (typeof(Creature), nameof(Creature.ApplyPowerInternal)), (typeof(Creature), nameof(Creature.RemovePowerInternal)),
        })
        {
            try
            {
                if (Reflect.Method(owner, name) is { } creatures)
                {
                    PatchGuard.Patch(harmony, creatures, finalizer: Method(nameof(InvalidateCreatures)));
                }
                else
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: {owner.Name}.{name} not found: not watched");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: {owner.Name}.{name} not watched: {e.Message}");
            }
        }
        // A card's enchantment/affliction is only listed through a pile, and only owned cards sit in piles. Ownerless
        // cards are detached models, e.g. Limitless' enchantment storage cards rebuilt while rendering every card.
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Enchantment>k__BackingField", "set_Enchantment");
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Affliction>k__BackingField", "set_Affliction");
        _cardOwner = Reflect.FieldRefAccess<CardModel, Player?>("_owner");
        PatchGuard.Patch(harmony, Require(Reflect.PropertySetter(typeof(CardModel), nameof(CardModel.Enchantment))), finalizer: Method(nameof(InvalidateIfOwned)));
        PatchGuard.Patch(harmony, Require(Reflect.PropertySetter(typeof(CardModel), nameof(CardModel.Affliction))), finalizer: Method(nameof(InvalidateIfOwned)));

        // 2. The caches themselves.
        _combatIterate = Require(Reflect.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)));
        _runIterate = Require(Reflect.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) }));
        PatchGuard.Patch(harmony, _combatIterate, prefix: Method(nameof(CombatIteratePrefix)));
        PatchGuard.Patch(harmony, _runIterate, prefix: Method(nameof(RunIteratePrefix)));
        // A left run: the lists kept for the next display scope (and HookFilter's filtered ones) would hold it until then.
        if (Reflect.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), nameof(MegaCrit.Sts2.Core.Runs.RunManager.CleanUp)) is { } cleanUp)
        {
            PatchGuard.Patch(harmony, cleanUp, postfix: Method(nameof(RunCleanedUp)));
        }

        // 3. Scopes. Each one is independent: a missing target only loses that scope.
        TryScope(harmony, Reflect.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun)), nameof(EnterSnapshot), nameof(ExitSnapshot));
        // Read-only refresh passes: they only read game state and update visuals/display values.
        TryScope(harmony, Reflect.Method(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues)), nameof(EnterRefreshPatch), nameof(ExitRefresh));
        // One scope around the whole CombatStateChanged dispatch (hand, end turn button, intents, creature state,
        // energy, orbs...): the listener lists are built and filtered once per refresh instead of once per handler. Only
        // while the game code that resumes game logic inside it is the one SuspendPrefix covers (DispatchResumesKnown).
        if (DispatchResumesKnown())
        {
            TryDispatchScope(harmony);
        }
        TryScope(harmony, Reflect.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard)), nameof(EnterDisplayRefresh), nameof(ExitRefresh));
        TryScope(harmony, Reflect.Method(typeof(NCard), nameof(NCard.UpdateVisuals)), nameof(EnterDisplayRefresh), nameof(ExitRefresh));

        // 4. Game logic resuming inside the dispatch scope: run vanilla there (see SuspendPrefix). Required: without it a
        // hand selection ended by a state change would run the card's play on the scope's lists.
        PatchGuard.Patch(harmony, Require(Reflect.Method(typeof(NPlayerHand), "RevalidateSelectionAfterStateChange")),
            prefix: Method(nameof(SuspendPrefix)), finalizer: Method(nameof(ResumeFinalizer)));

        // BaseLib loading later (it is optional): whether card modifiers may exist is looked at again (CardModifiersMayChange).
        if (System.Threading.Interlocked.Exchange(ref _watchingBaseLib, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
        // Answered once at load rather than at the first kept-list hit in a combat (with BaseLib, BaseLibTypes scans the
        // assemblies referencing it); it never throws.
        CardModifiersMayChange();
        // Last: a throw above leaves the scopes vanilla. ModEntry then removes the invalidators, but the scopes ExtraScopes
        // and HookFilter open through this class would still cache (on a version nothing bumps any more) without this.
        _applied = true;
        // What the scope check asks (display only): both walks, and the walk code FastListeners knows (resolved lazily).
        PatchSafety.Watch(Name, new MethodBase?[] { _combatIterate, _runIterate }, PatchTolerance.SurroundingOnly);
        PatchSafety.WatchAll(Name, FastListeners.WalkCodeMethods);
        // Display only: these gate every scope (EnterAny's _safe), any back-off keeps all of it vanilla (the menu's red title).
        PatchSafety.WholePatch(Name);
        // With other mods' postfixes on the walks let stand, the lists come from FastListeners' unpatched mirrors: with that
        // option off (or its mirrors not applied) no scope caches then. Display only.
        PatchSafety.WatchNote(Name, NoteWalkMirrors);
        // Kept lists aren't reused across display scopes while card modifiers may change (CardModifiersMayChange).
        PatchSafety.WatchNote(ReuseFault, () => BaseLibTypes.NoteIfDefined(ReuseFault, "card_modifiers", CardModifierType,
            "listener lists are rebuilt in each refresh instead of reused: other mods define BaseLib card modifiers", whole: true));
    }

    /// <summary>The note (back-off preflight) for scopes that can't cache while other mods patch the walks: see <see cref="EnterAny"/>.</summary>
    private static void NoteWalkMirrors()
    {
        var mods = new List<Assembly?>();
        foreach (MethodInfo? walk in new[] { _combatIterate, _runIterate })
        {
            if (walk != null && PatchSafety.HasOtherPatches(walk) && Harmony.GetPatchInfo(walk) is { } info)
            {
                mods.AddRange(info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                    .Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => p.PatchMethod?.DeclaringType?.Assembly));
            }
        }
        if (mods.Count == 0 || FastListeners.MirrorsUnpatched)
        {
            PatchSafety.ClearNote(Name, "walk_mirrors");
            return;
        }
        PatchSafety.Note(Name, "walk_mirrors", "other mods patch the listener walks: their lists are built from the listener-walk optimization's "
            + "copies of the walks, which is off here, so no list is kept", PatchSafety.ModNamesOf(mods), whole: true);
    }

    /// <summary>Set once <see cref="Apply"/> got through: until then (or when it threw) no scope caches.</summary>
    private static bool _applied;

    /// <summary>
    /// <c>NPlayerHand.RevalidateSelectionAfterStateChange</c> runs inside the CombatStateChanged dispatch scope (while the
    /// player chooses cards in hand) and ends the selection when no hand card is valid any more: <c>TrySetResult</c> on a
    /// task made without <c>RunContinuationsAsynchronously</c>, awaited on the same context, so the card's play goes on
    /// inline, inside the scope (on host or solo, the action queue with it). The game's walks there must be its lazy
    /// ones (<c>Contains</c> at each yield: a listener leaving the state during an async hook's walk is skipped), not a
    /// list built for the scope: everything this mod does with listeners runs vanilla until the method returns.
    /// </summary>
    private static void SuspendPrefix(out int __state)
    {
        __state = _suspended ? 2 : 1;
        _suspended = true;
#if BENCH
        SuspendedRevalidations++;
#endif
    }

    /// <summary>
    /// Restores what the prefix found; 0: the prefix never ran (an earlier prefix threw: Harmony runs the finalizers with the
    /// default state). A prefix returning false doesn't stop this one: it returns void and takes only <c>__state</c>.
    /// </summary>
    private static void ResumeFinalizer(int __state)
    {
        if (__state != 0)
        {
            _suspended = __state == 2;
        }
    }

    /// <summary>
    /// The code the dispatch scope's suspension relies on (SuspendPrefix): the hand's CombatStateChanged handler, which calls
    /// <c>RevalidateSelectionAfterStateChange</c>, that method, which ends a selection no hand card fits any more
    /// (<c>TrySetResult</c>), and <c>SelectCards</c>, which makes that task without <c>RunContinuationsAsynchronously</c> (the
    /// card's play then goes on inline). Same on v0.107.1, v0.111.0, v0.113.0 and v0.113.1 but SelectCards' body (v0.107.1 vs
    /// v0.111+: the task made the same way in both).
    /// </summary>
    private static readonly (string Method, bool Body, string[] Fingerprints)[] DispatchResumeCode =
    {
        ("OnCombatStateChanged", false, new[] { "B1E8150FD5A853F6" }),
        ("RevalidateSelectionAfterStateChange", false, new[] { "92DB756795D6C546" }),
        ("SelectCards", true, new[] { "ED039D9684E57FC5", "D189372D06A00A80" }),
    };

    /// <summary>
    /// Whether the game code that can resume game logic inside the CombatStateChanged dispatch is still the one SuspendPrefix
    /// covers. Changed, another place may end a selection (or a new handler resume a play) inside the scope, unsuspended: the
    /// whole dispatch scope stays vanilla then (logged), not only the suspension.
    /// </summary>
    private static bool DispatchResumesKnown()
    {
        foreach ((string name, bool body, string[] fingerprints) in DispatchResumeCode)
        {
            MethodInfo? method = Reflect.Method(typeof(NPlayerHand), name);
            MethodBase? code = method == null ? null : body ? Reflect.AsyncMoveNext(method) : method;
            string actual = code == null ? "missing" : MethodFingerprint.Of(code);
            if (!fingerprints.Any(f => PatchSafety.FingerprintMatches(actual, f)))
            {
                Log.Info($"[{ModEntry.Id}] {Name}: NPlayerHand.{name} changed (fingerprint {actual}, expected {string.Join(" or ", fingerprints)}): "
                    + "the CombatStateChanged dispatch scope stays vanilla (game logic may resume inside it)");
                return false;
            }
        }
        return true;
    }

    /// <summary>A method the cache must watch to stay up to date: missing, the whole patch is skipped (named in the log).</summary>
    private static MethodBase Watched(MethodBase? method, Type type, string name) =>
        method ?? throw new PatchSkippedException($"{type.Name}.{name}, which it watches to keep its cache up to date, is missing (game update?): left vanilla");

    private static MethodBase Require(MethodBase? method) =>
        method ?? throw new MissingMethodException("ListenerCache target not found (game update?)");

    private static MethodInfo Require(MethodInfo? method) =>
        method ?? throw new MissingMethodException("ListenerCache target not found (game update?)");

    private static void TryScope(Harmony harmony, MethodBase? target, string enter, string exit)
    {
        try
        {
            PatchGuard.Patch(harmony, Require(target), prefix: Method(enter), finalizer: Method(exit));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: scope {target?.DeclaringType?.Name}.{target?.Name} unavailable: {e.Message}");
        }
    }

    private static HarmonyMethod Method(string name) => new(typeof(ListenerCache), name);

    private static MethodInfo? _invokeInScope;

    private static void TryDispatchScope(Harmony harmony)
    {
        try
        {
            MethodInfo dispatch = Require(Reflect.Method(typeof(CombatStateTracker), "CallCombatStateChangedDeferred"));
            MethodInfo moveNext = Require(Reflect.AsyncMoveNext(dispatch));
            _invokeInScope = Require(Reflect.Method(typeof(ListenerCache), nameof(InvokeStateChangedInScope)));
            PatchGuard.Patch(harmony, moveNext, transpiler: Method(nameof(DispatchTranspiler)));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: CombatStateChanged dispatch scope unavailable: {e.Message}");
        }
    }

    /// <summary>
    /// Replaces <c>this.CombatStateChanged?.Invoke(_state)</c> with <see cref="InvokeStateChangedInScope"/>. Leaves the
    /// method untouched unless exactly one such call is found.
    /// </summary>
    private static IEnumerable<CodeInstruction> DispatchTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo invoke = Reflect.Method(typeof(Action<CombatState>), nameof(Action<CombatState>.Invoke));
        List<CodeInstruction> calls = code.Where(c => c.Calls(invoke)).ToList();
        if (calls.Count != 1 || _invokeInScope == null)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: unexpected CombatStateChanged dispatch ({calls.Count} invoke calls), scope not installed.");
            return code;
        }
        // Same stack shape: (Action<CombatState>, CombatState) -> void.
        calls[0].opcode = System.Reflection.Emit.OpCodes.Call;
        calls[0].operand = _invokeInScope;
        return code;
    }

    /// <summary>Invokes the CombatStateChanged handlers inside one refresh scope.</summary>
    public static void InvokeStateChangedInScope(Action<CombatState> handlers, CombatState state)
    {
        EnterRefresh();
        try
        {
            handlers(state);
        }
        finally
        {
            ExitRefresh(null);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with every listener optimization off, producing a pure vanilla result.
    /// </summary>
    public static T RunVanilla<T>(Func<T> action)
    {
        bool wasSuspended = _suspended;
        _suspended = true;
        try
        {
            return action();
        }
        finally
        {
            _suspended = wasSuspended;
        }
    }

    /// <summary>Bumped by every invalidation (this thread).</summary>
    public static int Version => _version;

    /// <summary>Inside <see cref="RunVanilla"/>: listener optimizations must stay off (the result is the reference).</summary>
    public static bool IsSuspended => _suspended;

    /// <summary>
    /// The combat listener sequence, materialized once per scope and state version.
    /// </summary>
    public static ReadOnlyCollection<AbstractModel> GetListeners(CombatState combatState)
    {
        _cache ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
        if (_cache.TryGetValue((combatState, null), out var entry) && entry.Version == _version)
        {
            return entry.Listeners;
        }
        _materializingCombat = true;
        try
        {
            ReadOnlyCollection<AbstractModel>? kept = Kept((combatState, null));
            ReadOnlyCollection<AbstractModel> listeners = kept != null && !VerifyThisReuse()
                ? kept
                : CheckedAgainstKept(kept, Materialize(CombatSource(combatState), entry.Listeners?.Count ?? kept?.Count ?? 0));
            _cache[(combatState, null)] = (_version, listeners);
            return listeners;
        }
        finally
        {
            _materializingCombat = false;
        }
    }

    /// <summary>
    /// The run listener sequence (deck + run subscribers + combat), materialized once per scope and state version.
    /// </summary>
    public static ReadOnlyCollection<AbstractModel> GetRunListeners(RunState runState, ICombatState? child)
    {
        _cache ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
        if (_cache.TryGetValue((runState, child), out var entry) && entry.Version == _version)
        {
            return entry.Listeners;
        }
        _materializingRun = true;
        try
        {
            ReadOnlyCollection<AbstractModel>? kept = Kept((runState, child));
            ReadOnlyCollection<AbstractModel> listeners = kept != null && !VerifyThisReuse()
                ? kept
                : CheckedAgainstKept(kept, Materialize(RunSource(runState, child), entry.Listeners?.Count ?? kept?.Count ?? 0));
            _cache[(runState, child)] = (_version, listeners);
            return listeners;
        }
        finally
        {
            _materializingRun = false;
        }
    }

    /// <summary>
    /// What a combat list is built from: the game's call (no other mod's patch there: <see cref="EnterAny"/>), or, while other
    /// mods' postfixes on it are let stand, the unpatched walk (FastListeners' mirror, called directly): built through the
    /// game's call, the list would hold their output and they would run on it again at each replay.
    /// </summary>
    private static IEnumerable<AbstractModel> CombatSource(CombatState combatState) =>
        !_combatAugmented ? combatState.IterateHookListeners()
            : FastListeners.UnpatchedCombat(combatState) ?? throw new UnpatchedWalkMissing();

    /// <summary>
    /// <see cref="CombatSource"/> for the run walk. Its child combat walk goes through the game's call either way (as the
    /// original's): never served with a child while the combat walk carries other mods' patches (<see cref="RunIteratePrefix"/>),
    /// whose output would then be kept.
    /// </summary>
    private static IEnumerable<AbstractModel> RunSource(RunState runState, ICombatState? child) =>
        !_runAugmented ? runState.IterateHookListeners(child)
            : FastListeners.UnpatchedRun(runState, child) ?? throw new UnpatchedWalkMissing();

    /// <summary>
    /// An augmented scope's list can't be built: FastListeners' unpatched mirror declined (another mod patched the walk's state
    /// machine or Contains since the scope opened: <see cref="HarmonyWatch"/> makes that visible mid-scope). Not an error of this
    /// mod: the prefixes then let the game's call run (as outside a scope) for the rest of the scope (<see cref="_mirrorDeclined"/>:
    /// thrown once per scope, not once per walk), ListenerCache stays on.
    /// </summary>
    private sealed class UnpatchedWalkMissing : Exception
    {
        public UnpatchedWalkMissing() : base("the unpatched listener walk is unavailable (its code is patched by another mod now)")
        {
        }
    }

    /// <summary>
    /// <c>listeners.ToList().AsReadOnly()</c>, sized from the previous count: thousands of entries grown by doubling
    /// would allocate twice that, the last arrays on the large object heap.
    /// </summary>
    private static ReadOnlyCollection<AbstractModel> Materialize(IEnumerable<AbstractModel> listeners, int sizeHint)
    {
        var list = new List<AbstractModel>(sizeHint);
        foreach (AbstractModel listener in listeners)
        {
            list.Add(listener);
        }
        return list.AsReadOnly();
    }

    /// <summary>
    /// Harmony finalizer of every watched method: runs whether it returned or threw (a second run only bumps again). Void:
    /// with every finalizer of a method void, Harmony rethrows the game's exception with its own stack trace.
    /// </summary>
    private static void Invalidate()
    {
        Bump();
    }

    /// <summary><see cref="Invalidate"/> for the creature lists and powers (counted apart in test builds).</summary>
    private static void InvalidateCreatures()
    {
        Bump();
#if BENCH
        CreatureInvalidations++;
#endif
    }

    private static void InvalidateIfOwned(CardModel __instance)
    {
        // Never throws into the setter: unsure, invalidate.
        try
        {
            // A card in no pile is in no walk: its enchantment or affliction changing changes no listener list. Owned clones
            // outside every pile (a mod's previews, predictions) no longer drop the kept lists then. Only when the pile index
            // knows it exactly (PileIndex.InNoPile: false when unsure, off or mid-move); else invalidate as before.
            if (_cardOwner == null || (_cardOwner(__instance) != null && !PileIndex.InNoPile(__instance)))
            {
                Bump();
            }
        }
        catch
        {
            Bump();
        }
    }

    /// <summary>
    /// A new version: the kept lists can never be used again (<see cref="Kept"/> needs the same version) and go now, so they
    /// don't hold a finished combat (its piles cleared at its end) until the next display scope.
    /// </summary>
    private static void Bump()
    {
        _version++;
        if (_kept is { Count: > 0 })
        {
            _kept.Clear();
        }
    }

    private static void EnterAny()
    {
        if (_snapshotDepth + _refreshDepth == 0)
        {
            try
            {
                // Checked per outermost scope: another mod may patch after we load. A replayed list stands for the whole
                // walk: the iterators' state machines, Contains and the mod subscriber walks too (FastListeners knows them):
                // no tolerance there. On IterateHookListeners itself, postfixes (and void prefixes sorted before every prefix
                // of this mod) are let stand (SurroundingOnly): the lists are then built from the unpatched walk
                // (FastListeners' mirror, not the game's call: CombatSource), and Harmony runs those postfixes after the
                // serving prefix on every call, on the replayed list, once, as on the game's stream without this mod. Only
                // the unpatched part is kept (what the scopes already assume stable); what the postfixes add, drop or reorder
                // is computed live at each call. HookFilter, which uses the lists directly, stays off then (IsCaching).
                bool combatOthers = PatchSafety.HasOtherPatches(_combatIterate), runOthers = PatchSafety.HasOtherPatches(_runIterate);
                _safe = _applied && !PatchSafety.IsPatchedByOthers(_combatIterate, PatchTolerance.SurroundingOnly)
                    && !PatchSafety.IsPatchedByOthers(_runIterate, PatchTolerance.SurroundingOnly)
                    && !FastListeners.WalkCodePatchedByOthers() && (!(combatOthers || runOthers) || FastListeners.MirrorsUnpatched);
                _combatAugmented = combatOthers;
                _runAugmented = runOthers;
                _mirrorDeclined = false;
#if BENCH
                if (_safe && (combatOthers || runOthers))
                {
                    AugmentedScopes++;
                }
#endif
            }
            catch (Exception e)
            {
                // Runs in the game's methods (scope prefixes): never throw there; this scope stays vanilla.
                _safe = false;
                _combatAugmented = _runAugmented = false;
                _mirrorDeclined = false;
                if (!_safeCheckFailed)
                {
                    _safeCheckFailed = true;
                    Log.Warn($"[{ModEntry.Id}] {Name}: patch check failed, scopes left vanilla while it does: {e.Message}");
                }
            }
        }
    }

    private static bool _safeCheckFailed;

    [ThreadStatic] private static bool _releaseAtExit;

    /// <summary>Raised after the game cleans a run up: caches holding its models drop them.</summary>
    public static event Action? RunReleased;

    private static void RunCleanedUp()
    {
        try
        {
            if (_snapshotDepth + _refreshDepth == 0)
            {
                _kept?.Clear();
                _fromKept?.Clear();
                _cache?.Clear();
            }
            else
            {
                _releaseAtExit = true; // Inside a scope: its exit hands nothing on.
            }
            RunReleased?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: releasing the left run's lists failed: {e.Message}");
        }
    }

    /// <summary>
    /// Runs in the scopes' finalizers: never throws (Harmony would run every finalizer of the method again, then throw into
    /// the game). Safe to run twice: the second run finds its cache empty and leaves what the first handed on.
    /// </summary>
    private static void ExitAny()
    {
        if (_snapshotDepth + _refreshDepth != 0)
        {
            return;
        }
        try
        {
            // Refresh-only scopes hand their lists to the next refresh scope (valid while the version is unchanged). A scope
            // that built nothing hands nothing new: the lists kept before stay, each checked against the version on use.
            bool release = _releaseAtExit;
            _releaseAtExit = false;
            if (release || _reuseBlocked || !ReuseEnabled)
            {
                _kept?.Clear();
            }
            if (release)
            {
                RunReleased?.Invoke(); // Lists filtered later in that scope for the left run go too.
            }
            else if (!_reuseBlocked && ReuseEnabled && _cache is { Count: > 0 })
            {
                _kept ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
                _kept.Clear();
                foreach (var pair in _cache)
                {
                    _kept[pair.Key] = pair.Value;
                }
            }
            _reuseBlocked = false;
            _fromKept?.Clear();
            _cache?.Clear();
            ScopeEnded?.Invoke();
        }
        catch (Exception e)
        {
            // A dependent cache (ScopeEnded) may not have dropped its lists: no scope caches any more, so none is used.
            _reuseBlocked = false;
            _fromKept?.Clear();
            Fail(e);
        }
    }

    /// <summary>
    /// Reuse of listener lists across display refresh scopes (UI refreshing several times a second, e.g. BetterSpire2's
    /// damage preview): never in or after a snapshot or game-state refresh scope, only while no invalidation happened.
    /// </summary>
    public static bool ReuseEnabled { get; set; } = true;
    public const string ReuseFault = Name + ".Reuse";
    private const int ReuseVerifyEvery = 16;
    public static long Reused { get; private set; }
    public static long ReuseVerified { get; private set; }
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, ReadOnlyCollection<AbstractModel> Listeners)>? _kept;
    /// <summary>A snapshot or a game-state refresh scope is (or was) open in the current outermost scope.</summary>
    [ThreadStatic] private static bool _reuseBlocked;
    /// <summary>Cache keys whose list came from a kept one, dropped if a game-state scope opens inside.</summary>
    [ThreadStatic] private static HashSet<(object, object?)>? _fromKept;

    private static ReadOnlyCollection<AbstractModel>? Kept((object, object?) key)
    {
        if (!ReuseEnabled || _reuseBlocked || _kept == null
            || !_kept.TryGetValue(key, out var entry) || entry.Version != _version || CardModifiersMayChange())
        {
            return null;
        }
        Reused++;
        (_fromKept ??= new HashSet<(object, object?)>(new RefComparer())).Add(key);
        return entry.Listeners;
    }

    private static bool VerifyThisReuse() => Reused % ReuseVerifyEvery == 0;

    /// <summary>BaseLib's card modifier base type.</summary>
    private const string CardModifierType = "BaseLib.Abstracts.CardModifier";
    private const string BaseLibAssembly = "BaseLib";
    /// <summary>0: not looked up yet (or BaseLib loaded since), 1: no such type (no BaseLib), 2: BaseLib has it.</summary>
    private static int _cardModifierBase;
    private static int _watchingBaseLib;

    /// <summary>
    /// Whether a mod may apply BaseLib card modifiers. BaseLib's "BaseLibCardModifiers" subscriber lists each pile card's
    /// modifiers, which BaseLib changes with no method this cache watches: a kept list may then miss one applied or removed
    /// since, so lists aren't reused across display scopes (each scope still builds its own once). No BaseLib, or no mod
    /// defining a concrete CardModifier (every supported setup today): reused as before. Never throws: can't tell, no reuse.
    /// </summary>
    private static bool CardModifiersMayChange()
    {
        try
        {
            int known = System.Threading.Volatile.Read(ref _cardModifierBase);
            if (known == 0)
            {
                // Looked up in BaseLib's assembly only: AccessTools.TypeByName on a missing type (no BaseLib, the common
                // setup) falls back to GetTypes() on every loaded assembly, a hitch in the middle of a combat.
                known = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name == BaseLibAssembly && a.GetType(CardModifierType, false) != null) ? 2 : 1;
                System.Threading.Interlocked.CompareExchange(ref _cardModifierBase, known, 0);
            }
            return known == 2 && !BaseLibTypes.NoneDefined(CardModifierType);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Runs inside another assembly's load: never throws.</summary>
    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            if (args.LoadedAssembly.GetName().Name == BaseLibAssembly)
            {
                System.Threading.Volatile.Write(ref _cardModifierBase, 0);
            }
        }
        catch
        {
            System.Threading.Volatile.Write(ref _cardModifierBase, 0);
        }
    }

    /// <summary>Fresh list built while a kept one was available: they must match, else reuse is unsafe.</summary>
    private static ReadOnlyCollection<AbstractModel> CheckedAgainstKept(ReadOnlyCollection<AbstractModel>? kept, ReadOnlyCollection<AbstractModel> fresh)
    {
        if (kept != null)
        {
            ReuseVerified++;
            if (!kept.SequenceEqual(fresh, RefComparer.Models))
            {
                ReuseEnabled = false;
                _kept?.Clear();
                PatchSafety.Fault(ReuseFault);
                Log.Error($"[{ModEntry.Id}] {Name}: a kept listener list differs from a fresh one ({kept.Count} vs {fresh.Count}): "
                    + "reuse across refreshes disabled for this session. Please report this log.");
            }
        }
        return fresh;
    }

    // Scope prefixes keep the depth they found plus one in __state and their finalizers restore it instead of decrementing:
    // Harmony 2.4 runs every finalizer of a method a second time when one of them throws (another mod's included), and a
    // second decrement would leave this thread's depth negative (no scope ever caching again, or one never closing).
    // A state of 0 is the default Harmony gives a finalizer whose prefix never ran (an earlier prefix threw): no scope was
    // opened, nothing to restore (restoring 0 would close the scopes this call runs nested in).

    private static void EnterSnapshot(out int __state)
    {
        __state = _snapshotDepth + 1;
        EnterAny();
        BlockReuse();
        _snapshotDepth++;
    }

    private static void ExitSnapshot(Exception? __exception, int __state)
    {
        if (__state <= 0)
        {
            return;
        }
        _snapshotDepth = __state - 1;
        ExitAny();
    }

    /// <summary>
    /// Refresh scope around a pass whose results can feed game state (enchantment values, first-turn filters): lists
    /// are always built fresh here, never reused from an earlier scope. For callers pairing it with <c>ExitRefresh(null)</c>
    /// in a try/finally; patches use <see cref="EnterRefreshPatch"/>.
    /// </summary>
    internal static void EnterRefresh()
    {
        EnterAny();
        BlockReuse();
        _refreshDepth++;
    }

    /// <summary><see cref="EnterRefresh"/> as a Harmony prefix (finalizer <see cref="ExitRefresh"/>).</summary>
    private static void EnterRefreshPatch(out int __state)
    {
        __state = _refreshDepth + 1;
        EnterRefresh();
    }

    /// <summary>
    /// Refresh scope around a display-only pass (card visuals, other mods' previews): may reuse kept lists. Harmony prefix
    /// (finalizer <see cref="ExitRefresh"/>); its state (the depth found plus one, never 0) is what <see cref="ExitRefresh"/> takes.
    /// </summary>
    internal static void EnterDisplayRefresh(out int __state)
    {
        __state = _refreshDepth + 1;
        EnterAny();
        _refreshDepth++;
    }

    /// <summary>From here to the end of the outermost scope, no kept list is used, and lists already taken from one are dropped.</summary>
    private static void BlockReuse()
    {
        _reuseBlocked = true;
        if (_fromKept is { Count: > 0 })
        {
            foreach (var key in _fromKept)
            {
                _cache?.Remove(key);
            }
            _fromKept.Clear();
        }
    }

    /// <summary>
    /// Harmony finalizer of <see cref="EnterRefreshPatch"/> and <see cref="EnterDisplayRefresh"/> (restores the depth they
    /// found: state - 1; 0, their prefix never ran: nothing to close), or called directly after <see cref="EnterRefresh"/>
    /// in a finally (no state, -1: one level less).
    /// </summary>
    internal static void ExitRefresh(Exception? __exception, int __state = -1)
    {
        if (__state == 0)
        {
            return;
        }
        _refreshDepth = __state > 0 ? __state - 1 : Math.Max(0, _refreshDepth - 1);
        ExitAny();
    }

#if BENCH
    /// <summary>Walks answered from a materialized list (the effects check).</summary>
    public static long Served { get; private set; }
    /// <summary>Invalidations by the creature lists and powers watched in <see cref="Apply"/> (the effects check: they run).</summary>
    public static long CreatureInvalidations { get; private set; }
    /// <summary>Hand selections revalidated with the listener optimizations suspended (<see cref="SuspendPrefix"/>).</summary>
    public static long SuspendedRevalidations { get; private set; }
    /// <summary>Scopes open on this thread: 0 when nothing runs (the effects check; a lost finalizer leaves them open).</summary>
    public static int OpenScopes => _snapshotDepth + _refreshDepth;
#endif

    private static bool CombatIteratePrefix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
    {
        // Other mods' postfixes let stand: only while the unpatched mirror can build the list (else the game's call).
        if (!Serving || _materializingCombat || (_combatAugmented && (_mirrorDeclined || !FastListeners.MirrorsUnpatched)))
        {
            return true;
        }
        try
        {
            // Another mod's postfix let stand gets the list behind a plain enumerable, as the game's iterator is (no list
            // interface it could test, cast or change: the list is shared by every call of the scope).
            __result = _combatAugmented ? new Replay(GetListeners(__instance)) : GetListeners(__instance);
#if BENCH
            Served++;
#endif
            return false;
        }
        catch (UnpatchedWalkMissing)
        {
            // The game's call: what an augmented scope relies on no longer holds (see UnpatchedWalkMissing), for the rest of it.
            _mirrorDeclined = true;
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    private static bool RunIteratePrefix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result)
    {
        // HookSkip asked for a filtered walk (BaseLib's hand size): no need for every listener. A child combat walk with other
        // mods' patches: what their postfixes return would be kept inside the run's list, so the game's call then (it calls the
        // combat walk, served from the combat's own list, where they run live).
        if (!Serving || _materializingRun || FastListeners.RunFilterPending || (childCombatState != null && _combatAugmented)
            || (_runAugmented && (_mirrorDeclined || !FastListeners.MirrorsUnpatched)))
        {
            return true;
        }
        try
        {
            __result = _runAugmented ? new Replay(GetRunListeners(__instance, childCombatState)) : GetRunListeners(__instance, childCombatState);
#if BENCH
            Served++;
#endif
            return false;
        }
        catch (UnpatchedWalkMissing)
        {
            // The game's call: what an augmented scope relies on no longer holds (see UnpatchedWalkMissing), for the rest of it.
            _mirrorDeclined = true;
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    /// <summary>
    /// A replayed list behind an enumerable only (no <c>IList</c>/<c>ICollection</c> to test, cast or change), handed to the game's
    /// call while other mods' postfixes run on it: they see what the game's iterator offers, never the shared list itself.
    /// </summary>
    private sealed class Replay : IEnumerable<AbstractModel>
    {
        private readonly ReadOnlyCollection<AbstractModel> _listeners;

        public Replay(ReadOnlyCollection<AbstractModel> listeners) => _listeners = listeners;

        public IEnumerator<AbstractModel> GetEnumerator() => _listeners.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

#if BENCH
    /// <summary>Outermost scopes caching with other mods' patches on the walks let stand (the effects check of the tolerance).</summary>
    public static long AugmentedScopes { get; private set; }
#endif

    private static void Fail(Exception e)
    {
        SnapshotEnabled = false;
        RefreshEnabled = false;
        PatchSafety.Fault(Name);
        _cache?.Clear();
        _kept?.Clear();
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
