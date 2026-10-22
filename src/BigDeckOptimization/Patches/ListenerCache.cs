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

    /// <summary>Refresh-scope caching. Toggled by the bench, or off for the session by the fail-safes.</summary>
    public static bool RefreshEnabled { get; set; } = true;

    private static MethodInfo? _combatIterate;
    private static MethodInfo? _runIterate;
    private static AccessTools.FieldRef<CardModel, Player?>? _cardOwner;

    private sealed class RefComparer : IEqualityComparer<(object, object?)>
    {
        public bool Equals((object, object?) a, (object, object?) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((object, object?) k) => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Item1), k.Item2 == null ? 0 : RuntimeHelpers.GetHashCode(k.Item2));
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

    public static void Apply(Harmony harmony)
    {
        // Order matters: if any of these throws, nothing below is patched and the cache never turns on.
        // 1. Invalidation.
        MethodBase?[] invalidators =
        {
            AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged"),
            AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)),
            AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)),
            AccessTools.Method(typeof(CardPile), nameof(CardPile.MoveToBottomInternal)),
            AccessTools.Method(typeof(CardPile), nameof(CardPile.MoveToTopInternal)),
            AccessTools.Method(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)),
            AccessTools.Method(typeof(CardPile), nameof(CardPile.Clear)),
            AccessTools.Method(typeof(Player), nameof(Player.AddRelicInternal)),
            AccessTools.Method(typeof(Player), nameof(Player.RemoveRelicInternal)),
            // Both filter what IterateHookListeners yields (melted relics, dead players' models).
            AccessTools.PropertySetter(typeof(RelicModel), nameof(RelicModel.IsMelted)),
            AccessTools.PropertySetter(typeof(Player), nameof(Player.IsActiveForHooks)),
            AccessTools.Method(typeof(Player), nameof(Player.AddPotionInternal)),
            AccessTools.Method(typeof(Player), nameof(Player.DiscardPotionInternal)),
            AccessTools.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
            AccessTools.Method(typeof(Player), nameof(Player.SubtractFromMaxPotionCount)),
            AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)),
            AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.Remove)),
            AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.Insert)),
            AccessTools.Method(typeof(OrbQueue), nameof(OrbQueue.Clear)),
        };
        foreach (MethodBase? target in invalidators)
        {
            harmony.Patch(Require(target), postfix: Method(nameof(Invalidate)));
        }
        // A card's enchantment/affliction is only listed through a pile, and only owned cards sit in piles. Ownerless
        // cards are detached models, e.g. Limitless' enchantment storage cards rebuilt while rendering every card.
        _cardOwner = AccessTools.FieldRefAccess<CardModel, Player?>("_owner");
        harmony.Patch(Require(AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.Enchantment))), postfix: Method(nameof(InvalidateIfOwned)));
        harmony.Patch(Require(AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.Affliction))), postfix: Method(nameof(InvalidateIfOwned)));

        // 2. The caches themselves.
        _combatIterate = Require(AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)));
        _runIterate = Require(AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) }));
        harmony.Patch(_combatIterate, prefix: Method(nameof(CombatIteratePrefix)));
        harmony.Patch(_runIterate, prefix: Method(nameof(RunIteratePrefix)));

        // 3. Scopes. Each one is independent: a missing target only loses that scope.
        TryScope(harmony, AccessTools.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun)), nameof(EnterSnapshot), nameof(ExitSnapshot));
        // Read-only refresh passes: they only read game state and update visuals/display values.
        TryScope(harmony, AccessTools.Method(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues)), nameof(EnterRefresh), nameof(ExitRefresh));
        // One scope around the whole CombatStateChanged dispatch (hand, end turn button, intents, creature state,
        // energy, orbs...): the listener lists are built and filtered once per refresh instead of once per handler.
        TryDispatchScope(harmony);
        TryScope(harmony, AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard)), nameof(EnterRefresh), nameof(ExitRefresh));
        TryScope(harmony, AccessTools.Method(typeof(NCard), nameof(NCard.UpdateVisuals)), nameof(EnterRefresh), nameof(ExitRefresh));
    }

    private static MethodBase Require(MethodBase? method) =>
        method ?? throw new MissingMethodException("ListenerCache target not found (game update?)");

    private static MethodInfo Require(MethodInfo? method) =>
        method ?? throw new MissingMethodException("ListenerCache target not found (game update?)");

    private static void TryScope(Harmony harmony, MethodBase? target, string enter, string exit)
    {
        try
        {
            harmony.Patch(Require(target), prefix: Method(enter), finalizer: Method(exit));
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
            harmony.Patch(moveNext, transpiler: Method(nameof(DispatchTranspiler)));
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
            ReadOnlyCollection<AbstractModel> listeners = combatState.IterateHookListeners().ToList().AsReadOnly();
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
            ReadOnlyCollection<AbstractModel> listeners = runState.IterateHookListeners(child).ToList().AsReadOnly();
            _cache[(runState, child)] = (_version, listeners);
            return listeners;
        }
        finally
        {
            _materializingRun = false;
        }
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
            _cache?.Clear();
            ScopeEnded?.Invoke();
        }
    }

    private static void EnterSnapshot()
    {
        EnterAny();
        _snapshotDepth++;
    }

    private static Exception? ExitSnapshot(Exception? __exception)
    {
        _snapshotDepth--;
        ExitAny();
        return __exception;
    }

    private static void EnterRefresh()
    {
        EnterAny();
        _refreshDepth++;
    }

    private static Exception? ExitRefresh(Exception? __exception)
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
        _cache?.Clear();
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
