using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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
/// Inside a scope nothing changes the listener set, so we materialize it once and replay it. As defense in depth, the
/// cache is dropped on every change that can alter the listener set: combat state changes reported by the game, any
/// card pile mutation (deck included), enchantment/affliction changes, relic (incl. melting), potion and orb changes,
/// players becoming active/inactive for hooks.
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
    [ThreadStatic] private static int _version;
    /// <summary>Key: (CombatState, null) or (RunState, child combat state or null).</summary>
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, ReadOnlyCollection<AbstractModel> Listeners)>? _cache;

    /// <summary>True while inside a checksum snapshot where the snapshot optimizations may run.</summary>
    public static bool IsInSnapshot => SnapshotEnabled && _snapshotDepth > 0 && _safe && !_suspended;

    /// <summary>True inside any scope (snapshot or refresh) where listener lists are cached.</summary>
    public static bool IsCaching => _safe && !_suspended
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
            Watched(AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged"), typeof(CombatStateTracker), "NotifyCombatStateChanged"),
            Watched(AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)), typeof(CardPile), nameof(CardPile.AddInternal)),
            Watched(AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)), typeof(CardPile), nameof(CardPile.RemoveInternal)),
            Watched(AccessTools.Method(typeof(CardPile), nameof(CardPile.MoveToBottomInternal)), typeof(CardPile), nameof(CardPile.MoveToBottomInternal)),
            Watched(AccessTools.Method(typeof(CardPile), nameof(CardPile.MoveToTopInternal)), typeof(CardPile), nameof(CardPile.MoveToTopInternal)),
            Watched(AccessTools.Method(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)), typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)),
            Watched(AccessTools.Method(typeof(CardPile), nameof(CardPile.Clear)), typeof(CardPile), nameof(CardPile.Clear)),
            Watched(AccessTools.Method(typeof(Player), nameof(Player.AddRelicInternal)), typeof(Player), nameof(Player.AddRelicInternal)),
            Watched(AccessTools.Method(typeof(Player), nameof(Player.RemoveRelicInternal)), typeof(Player), nameof(Player.RemoveRelicInternal)),
            // Both filter what IterateHookListeners yields (melted relics, dead players' models).
            Watched(AccessTools.PropertySetter(typeof(RelicModel), nameof(RelicModel.IsMelted)), typeof(RelicModel), nameof(RelicModel.IsMelted)),
            Watched(AccessTools.PropertySetter(typeof(Player), nameof(Player.IsActiveForHooks)), typeof(Player), nameof(Player.IsActiveForHooks)),
            Watched(AccessTools.Method(typeof(Player), nameof(Player.AddPotionInternal)), typeof(Player), nameof(Player.AddPotionInternal)),
            Watched(AccessTools.Method(typeof(Player), nameof(Player.DiscardPotionInternal)), typeof(Player), nameof(Player.DiscardPotionInternal)),
            Watched(AccessTools.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)), typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
            Watched(AccessTools.Method(typeof(Player), nameof(Player.SubtractFromMaxPotionCount)), typeof(Player), nameof(Player.SubtractFromMaxPotionCount)),
            Watched(AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)), typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)),
            Watched(AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.Remove)), typeof(OrbQueue), nameof(OrbQueue.Remove)),
            Watched(AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.Insert)), typeof(OrbQueue), nameof(OrbQueue.Insert)),
            Watched(AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.Clear)), typeof(OrbQueue), nameof(OrbQueue.Clear)),
        };
        // The methods above (or the ones they call) are the only code changing the piles, relics and potion slots: a new
        // one would leave cached lists stale.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        PatchSafety.RequireFieldWriters(typeof(Player), "_relics", PatchSafety.RelicListWriters);
        PatchSafety.RequireFieldWriters(typeof(Player), "_potionSlots", PatchSafety.PotionSlotWriters);
        PatchSafety.RequireFieldWriters(typeof(OrbQueue), "_orbs", ".ctor", "<TryEnqueue>d__#+MoveNext", "Clear", "Insert", "Remove");
        // TryEnqueue is async: its postfix runs at its first await, which comes after the orb is added (pinned: same in v0.111).
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)), TryEnqueueFingerprint);
        foreach (MethodBase? target in invalidators)
        {
            PatchGuard.Patch(harmony, Require(target), postfix: Method(nameof(Invalidate)));
        }
        // A card's enchantment/affliction is only listed through a pile, and only owned cards sit in piles. Ownerless
        // cards are detached models, e.g. Limitless' enchantment storage cards rebuilt while rendering every card.
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Enchantment>k__BackingField", "set_Enchantment");
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Affliction>k__BackingField", "set_Affliction");
        _cardOwner = AccessTools.FieldRefAccess<CardModel, Player?>("_owner");
        PatchGuard.Patch(harmony, Require(AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.Enchantment))), postfix: Method(nameof(InvalidateIfOwned)));
        PatchGuard.Patch(harmony, Require(AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.Affliction))), postfix: Method(nameof(InvalidateIfOwned)));

        // 2. The caches themselves.
        _combatIterate = Require(AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)));
        _runIterate = Require(AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) }));
        PatchGuard.Patch(harmony, _combatIterate, prefix: Method(nameof(CombatIteratePrefix)));
        PatchGuard.Patch(harmony, _runIterate, prefix: Method(nameof(RunIteratePrefix)));

        // 3. Scopes. Each one is independent: a missing target only loses that scope.
        TryScope(harmony, AccessTools.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun)), nameof(EnterSnapshot), nameof(ExitSnapshot));
        // Read-only refresh passes: they only read game state and update visuals/display values.
        TryScope(harmony, AccessTools.Method(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues)), nameof(EnterRefresh), nameof(ExitRefresh));
        // One scope around the whole CombatStateChanged dispatch (hand, end turn button, intents, creature state,
        // energy, orbs...): the listener lists are built and filtered once per refresh instead of once per handler.
        TryDispatchScope(harmony);
        TryScope(harmony, AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard)), nameof(EnterDisplayRefresh), nameof(ExitRefresh));
        TryScope(harmony, AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals)), nameof(EnterDisplayRefresh), nameof(ExitRefresh));
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
            MethodInfo dispatch = Require(AccessTools.Method(typeof(CombatStateTracker), "CallCombatStateChangedDeferred"));
            MethodInfo moveNext = Require(AccessTools.AsyncMoveNext(dispatch));
            _invokeInScope = Require(AccessTools.Method(typeof(ListenerCache), nameof(InvokeStateChangedInScope)));
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
        MethodInfo invoke = AccessTools.Method(typeof(Action<CombatState>), nameof(Action<CombatState>.Invoke));
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
                : CheckedAgainstKept(kept, Materialize(combatState.IterateHookListeners(), entry.Listeners?.Count ?? kept?.Count ?? 0));
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
                : CheckedAgainstKept(kept, Materialize(runState.IterateHookListeners(child), entry.Listeners?.Count ?? kept?.Count ?? 0));
            _cache[(runState, child)] = (_version, listeners);
            return listeners;
        }
        finally
        {
            _materializingRun = false;
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

    private static void Invalidate()
    {
        _version++;
    }

    private static void InvalidateIfOwned(CardModel __instance)
    {
        if (_cardOwner == null || _cardOwner(__instance) != null)
        {
            _version++;
        }
    }

    private static void EnterAny()
    {
        if (_snapshotDepth + _refreshDepth == 0)
        {
            // Checked per outermost scope: another mod may patch after we load.
            _safe = !PatchSafety.IsPatchedByOthers(_combatIterate) && !PatchSafety.IsPatchedByOthers(_runIterate);
        }
    }

    private static void ExitAny()
    {
        if (_snapshotDepth + _refreshDepth == 0)
        {
            // Refresh-only scopes hand their lists to the next refresh scope (valid while the version is unchanged).
            _kept?.Clear();
            if (!_reuseBlocked && ReuseEnabled && _cache != null)
            {
                _kept ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
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
            || !_kept.TryGetValue(key, out var entry) || entry.Version != _version)
        {
            return null;
        }
        Reused++;
        (_fromKept ??= new HashSet<(object, object?)>(new RefComparer())).Add(key);
        return entry.Listeners;
    }

    private static bool VerifyThisReuse() => Reused % ReuseVerifyEvery == 0;

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

    private static void EnterSnapshot()
    {
        EnterAny();
        BlockReuse();
        _snapshotDepth++;
    }

    private static Exception? ExitSnapshot(Exception? __exception)
    {
        _snapshotDepth--;
        ExitAny();
        return __exception;
    }

    /// <summary>
    /// Refresh scope around a pass whose results can feed game state (enchantment values, first-turn filters): lists
    /// are always built fresh here, never reused from an earlier scope.
    /// </summary>
    internal static void EnterRefresh()
    {
        EnterAny();
        BlockReuse();
        _refreshDepth++;
    }

    /// <summary>Refresh scope around a display-only pass (card visuals, other mods' previews): may reuse kept lists.</summary>
    internal static void EnterDisplayRefresh()
    {
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

    internal static Exception? ExitRefresh(Exception? __exception)
    {
        _refreshDepth--;
        ExitAny();
        return __exception;
    }

    private static bool CombatIteratePrefix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
    {
        if (!IsCaching || _materializingCombat)
        {
            return true;
        }
        try
        {
            __result = GetListeners(__instance);
            return false;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    private static bool RunIteratePrefix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result)
    {
        if (!IsCaching || _materializingRun)
        {
            return true;
        }
        try
        {
            __result = GetRunListeners(__instance, childCombatState);
            return false;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

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
