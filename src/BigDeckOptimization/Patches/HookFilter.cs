using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Inside <see cref="ListenerCache"/> scopes (checksum snapshot, UI refresh), some hooks run once per card over every
/// listener, but almost no listener overrides them: the <see cref="AbstractModel"/> defaults are no-ops (cost and
/// keywords untouched, ShouldPlay true, damage +0 / ×1 / cap MaxValue). We only call the listeners whose type
/// overrides the hook, in the same order, so results are identical.
///
/// Each mirrored hook backs off (vanilla) whenever another mod has Harmony-patched that hook or its base method, since
/// our mirror of the hook body would then no longer match what vanilla peers run. BaseLib patches
/// <c>Hook.ModifyDamage</c> but not <c>Hook.ModifyDamageInternal</c>, which holds the listener loops.
/// </summary>
public static class HookFilter
{
    public const string Name = "HookFilter";

    public static bool Enabled { get; set; } = true;

    private sealed class FilteredHook
    {
        public readonly MethodInfo BaseMethod;
        private readonly MethodInfo _hookMethod;

        public FilteredHook(string baseMethodName, MethodInfo hookMethod)
        {
            BaseMethod = AccessTools.Method(typeof(AbstractModel), baseMethodName)
                ?? throw new MissingMethodException(nameof(AbstractModel), baseMethodName);
            _hookMethod = hookMethod;
        }

        public bool IsSafe() => !PatchSafety.IsPatchedByOthers(BaseMethod) && !PatchSafety.IsPatchedByOthers(_hookMethod);

        public bool IsOverriddenBy(Type type)
        {
            try
            {
                // Reflection collapses overridden virtuals: only the most-derived implementation is returned.
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name == BaseMethod.Name && m.GetBaseDefinition() == BaseMethod)
                    {
                        return m.DeclaringType != typeof(AbstractModel);
                    }
                }
                return true;
            }
            catch
            {
                // When in doubt, keep calling it.
                return true;
            }
        }
    }

    /// <summary>Per listener type, bit i set = the type overrides hook i. One lookup per listener for all hooks.</summary>
    private static readonly ConcurrentDictionary<Type, int> _overrideMaskByType = new();

    private static int OverrideMask(Type type) => _overrideMaskByType.GetOrAdd(type, t =>
    {
        int mask = 0;
        for (int i = 0; i < _hooks.Length; i++)
        {
            if (_hooks[i].IsOverriddenBy(t))
            {
                mask |= 1 << i;
            }
        }
        return mask;
    });

    // Hook indexes. The first group filters combat listeners, the second run listeners (deck + combat).
    private const int EnergyCost = 0, EnergyCostLate = 1, Keywords = 2, ShouldPlay = 3;
    private const int DamageAdditive = 4, DamageMultiplicative = 5, DamageCap = 6;
    private const int AllowHitting = 7, StarCost = 8;

#if BENCH
    public static long HittingChecks { get; private set; }
    public static long HittingMismatches { get; private set; }
    public static long CostColorChecks { get; private set; }
    public static long CostColorMismatches { get; private set; }
#endif

    /// <summary>v0.107.1 <c>Hook.ShouldAllowHitting</c>, mirrored by <see cref="ShouldAllowHittingPrefix"/>.</summary>
    private const string ShouldAllowHittingFingerprint = "C4F317D182865D23";

    /// <summary>
    /// v0.107.1 <c>CardCostHelper.TryModifyEnergyCostWithHooks</c> / <c>TryModifyStarCostWithHooks</c>: the cost colors
    /// of every card shown in combat (each hand card after every action) ask every combat listener, with their own
    /// copy of the cost hooks' loops. Mirrored by <see cref="EnergyCostColorPrefix"/> / <see cref="StarCostColorPrefix"/>.
    /// </summary>
    private const string EnergyCostColorFingerprint = "DA4F986AFFB60B1C";
    private const string StarCostColorFingerprint = "9DF34236EE3F9BC5";

    private static MethodInfo? _energyCostColor, _starCostColor;

    private static FilteredHook[] _hooks = Array.Empty<FilteredHook>();

    /// <summary>Filtered lists per source listener list (a new source list means the cache was invalidated).</summary>
    [ThreadStatic] private static Dictionary<IReadOnlyList<AbstractModel>, List<AbstractModel>?[]>? _filtered;

    /// <summary>Per-scope result of <see cref="FilteredHook.IsSafe"/> (patch checks are not free).</summary>
    [ThreadStatic] private static bool[]? _safe;

    public static void Apply(Harmony harmony)
    {
        MethodInfo energyHook = Require(AccessTools.Method(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat)));
        MethodInfo keywordsHook = Require(AccessTools.Method(typeof(Hook), nameof(Hook.ModifyKeywordsInCombat)));
        MethodInfo shouldPlayHook = Require(AccessTools.Method(typeof(Hook), nameof(Hook.ShouldPlay)));
        MethodInfo damageHook = Require(AccessTools.Method(typeof(Hook), "ModifyDamageInternal"));
        MethodInfo hittingHook = Require(AccessTools.Method(typeof(Hook), nameof(Hook.ShouldAllowHitting)));
        // Optional: a missing or changed cost color helper only leaves that helper vanilla.
        MethodInfo? energyColor = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Helpers.Models.CardCostHelper), "TryModifyEnergyCostWithHooks");
        MethodInfo? starColor = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Helpers.Models.CardCostHelper), "TryModifyStarCostWithHooks");
        _hooks = new[]
        {
            new FilteredHook(nameof(AbstractModel.TryModifyEnergyCostInCombat), energyHook),
            new FilteredHook(nameof(AbstractModel.TryModifyEnergyCostInCombatLate), energyHook),
            new FilteredHook(nameof(AbstractModel.TryModifyKeywordsInCombat), keywordsHook),
            new FilteredHook(nameof(AbstractModel.ShouldPlay), shouldPlayHook),
            new FilteredHook(nameof(AbstractModel.ModifyDamageAdditive), damageHook),
            new FilteredHook(nameof(AbstractModel.ModifyDamageMultiplicative), damageHook),
            new FilteredHook(nameof(AbstractModel.ModifyDamageCap), damageHook),
            new FilteredHook(nameof(AbstractModel.ShouldAllowHitting), hittingHook),
            new FilteredHook(nameof(AbstractModel.TryModifyStarCost), starColor ?? hittingHook),
        };
        harmony.Patch(energyHook, prefix: Method(nameof(ModifyEnergyCostPrefix)));
        harmony.Patch(keywordsHook, prefix: Method(nameof(ModifyKeywordsPrefix)));
        harmony.Patch(shouldPlayHook, prefix: Method(nameof(ShouldPlayPrefix)));
        harmony.Patch(damageHook, prefix: Method(nameof(ModifyDamageInternalPrefix)));
        // Read by Creature.IsHittable, e.g. BetterSpire2's damage preview several times a second.
        if (MethodFingerprint.Of(hittingHook) == ShouldAllowHittingFingerprint)
        {
            harmony.Patch(hittingHook, prefix: Method(nameof(ShouldAllowHittingPrefix)));
        }
        else
        {
            Log.Info($"[{ModEntry.Id}] {Name}: Hook.ShouldAllowHitting changed (fingerprint {MethodFingerprint.Of(hittingHook)}): left vanilla");
        }
        if (energyColor != null && MethodFingerprint.Of(energyColor) == EnergyCostColorFingerprint)
        {
            _energyCostColor = energyColor;
            harmony.Patch(energyColor, prefix: Method(nameof(EnergyCostColorPrefix)));
        }
        else
        {
            Log.Info($"[{ModEntry.Id}] {Name}: CardCostHelper.TryModifyEnergyCostWithHooks missing or changed: left vanilla");
        }
        if (starColor != null && MethodFingerprint.Of(starColor) == StarCostColorFingerprint)
        {
            _starCostColor = starColor;
            harmony.Patch(starColor, prefix: Method(nameof(StarCostColorPrefix)));
        }
        else
        {
            Log.Info($"[{ModEntry.Id}] {Name}: CardCostHelper.TryModifyStarCostWithHooks missing or changed: left vanilla");
        }
        ListenerCache.ScopeEnded += () =>
        {
            // Filtered lists stay valid for their source list (listener lists are immutable and reused across
            // refreshes while nothing changed); they are rebuilt when patch safety changes.
            _safe = null;
            _walked?.Clear();
            if (_filtered != null && (_filtered.Count > 8 || _filteredWith == null || !_filteredWith.SequenceEqual(CurrentSafety())))
            {
                _filtered = null;
                _filteredWith = null;
            }
        };
    }

    private static MethodInfo Require(MethodInfo? method) =>
        method ?? throw new MissingMethodException("HookFilter target not found (game update?)");

    private static HarmonyMethod Method(string name) => new(typeof(HookFilter), name);

    private static bool Active => Enabled && ListenerCache.IsCaching;

    /// <summary>Filtered listeners for one hook, or null when filtering that hook isn't safe right now.</summary>
    private static List<AbstractModel>? Get(IReadOnlyList<AbstractModel> listeners, int hook)
    {
        _filtered ??= new Dictionary<IReadOnlyList<AbstractModel>, List<AbstractModel>?[]>(ReferenceEqualityComparer.Instance);
        if (!_filtered.TryGetValue(listeners, out List<AbstractModel>?[]? perHook))
        {
            perHook = FilterAll(listeners);
            _filtered[listeners] = perHook;
        }
        return perHook[hook];
    }

    /// <summary>One pass over the listeners builds the filtered list of every safe hook (null for unsafe ones).</summary>
    private static bool[] CurrentSafety()
    {
        bool[] safe = new bool[_hooks.Length];
        for (int i = 0; i < _hooks.Length; i++)
        {
            safe[i] = _hooks[i].IsSafe();
        }
        return safe;
    }

    /// <summary>The patch safety the lists in <see cref="_filtered"/> were built with.</summary>
    [ThreadStatic] private static bool[]? _filteredWith;

    private static List<AbstractModel>?[] FilterAll(IReadOnlyList<AbstractModel> listeners)
    {
        _safe ??= CurrentSafety();
        if (_filteredWith != null && !_filteredWith.SequenceEqual(_safe))
        {
            _filtered?.Clear();
        }
        _filteredWith = _safe;
        var perHook = new List<AbstractModel>?[_hooks.Length];
        for (int i = 0; i < _hooks.Length; i++)
        {
            perHook[i] = _safe[i] ? new List<AbstractModel>() : null;
        }
        Type? lastType = null;
        int mask = 0;
        foreach (AbstractModel listener in listeners)
        {
            Type type = listener.GetType();
            if (type != lastType)
            {
                // Listeners come in runs of the same type (cards, enchantments...): skip most lookups.
                mask = OverrideMask(type);
                lastType = type;
            }
            for (int i = 0; mask >> i != 0; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    perHook[i]?.Add(listener);
                }
            }
        }
        return perHook;
    }

    /// <summary>Per hook, the pile entries whose type overrides its method (null until FastListeners is used).</summary>
    private static FastListeners.PileFilter[]? _pileFilters;

    /// <summary>Per (state, child) the filtered lists built by <see cref="Walked"/>, for one listener/pile version, dropped at scope end.</summary>
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, long PileVersion, List<AbstractModel>?[] PerHook)>? _walked;

    /// <summary>
    /// The combat listeners of one hook in walk order, or null when filtering it isn't safe. Built from FastListeners'
    /// filtered walk (listeners outside piles + the pile entries overriding the hook, cached per pile version) instead
    /// of materializing every listener: same objects, same order, then the same override mask.
    /// </summary>
    private static List<AbstractModel>? ForCombat(CombatState state, int hook) =>
        CanWalk
            ? Walked((state, null), hook, filter => FastListeners.Filtered(state, filter), () => ListenerCache.GetListeners(state))
            : Get(ListenerCache.GetListeners(state), hook);

    /// <summary>The run listeners (deck + child combat) of one hook, as <see cref="ForCombat"/>.</summary>
    private static List<AbstractModel>? ForRun(RunState run, ICombatState? child, int hook) =>
        CanWalk && (child == null || child is CombatState)
            ? Walked((run, child), hook, filter => FastListeners.Filtered(run, child, filter), () => ListenerCache.GetRunListeners(run, child))
            : Get(ListenerCache.GetRunListeners(run, child), hook);

    private static bool CanWalk
    {
        get
        {
            // FastListeners applies after this patch: its filters are made on first use.
            if (!FastListeners.Applied || !FastListeners.Enabled)
            {
                return false;
            }
            _pileFilters ??= _hooks.Select(h => FastListeners.PileFilter.Overriding(false, h.BaseMethod.Name)).ToArray();
            return true;
        }
    }

    private static List<AbstractModel>? Walked((object, object?) key, int hook, Func<FastListeners.PileFilter, IEnumerable<AbstractModel>> walk,
        Func<IReadOnlyList<AbstractModel>> materialized)
    {
        _safe ??= CurrentSafety();
        if (!_safe[hook])
        {
            return null;
        }
        _walked ??= new Dictionary<(object, object?), (int, long, List<AbstractModel>?[])>();
        if (!_walked.TryGetValue(key, out var entry) || entry.Version != ListenerCache.Version || entry.PileVersion != FastListeners.PileVersion)
        {
            entry = (ListenerCache.Version, FastListeners.PileVersion, new List<AbstractModel>?[_hooks.Length]);
            _walked[key] = entry;
        }
        List<AbstractModel>? list = entry.PerHook[hook];
        if (list == null)
        {
            list = new List<AbstractModel>();
            int bit = 1 << hook;
            foreach (AbstractModel listener in walk(_pileFilters![hook]))
            {
                if ((OverrideMask(listener.GetType()) & bit) != 0)
                {
                    list.Add(listener);
                }
            }
            entry.PerHook[hook] = list;
#if BENCH
            if (++WalkChecks % 32 == 0)
            {
                List<AbstractModel>? reference = Get(materialized(), hook);
                if (reference == null || !reference.SequenceEqual(list, ReferenceEqualityComparer.Instance))
                {
                    WalkMismatches++;
                    Log.Error($"[{ModEntry.Id}] {Name}: walked listeners of hook {hook} differ from the materialized ones ({list.Count} vs {reference?.Count})");
                }
            }
#endif
        }
        return list;
    }

#if BENCH
    public static long WalkChecks { get; private set; }
    public static long WalkMismatches { get; private set; }
#endif

    private static bool CombatHookListenersEmpty =>
        CombatManager.Instance.IsOverOrEnding && !CombatManager.Instance.IsStarting;

    /// <summary>Mirrors <see cref="Hook.ModifyEnergyCostInCombat"/> over the filtered listeners.</summary>
    private static bool ModifyEnergyCostPrefix(ICombatState combatState, CardModel card, decimal originalCost, ref decimal __result)
    {
        if (!Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? early = ForCombat(state, EnergyCost);
            List<AbstractModel>? late = ForCombat(state, EnergyCostLate);
            if (early == null || late == null)
            {
                return true;
            }
            // Vanilla: negative costs return early; IterateCombatHookListeners yields nothing while combat is ending.
            if (originalCost < 0m || CombatHookListenersEmpty)
            {
                __result = originalCost;
                return false;
            }
            decimal modifiedCost = originalCost;
            foreach (AbstractModel item in early)
            {
                item.TryModifyEnergyCostInCombat(card, modifiedCost, out modifiedCost);
            }
            foreach (AbstractModel item in late)
            {
                item.TryModifyEnergyCostInCombatLate(card, modifiedCost, out modifiedCost);
            }
            __result = modifiedCost;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>
    /// Mirrors <c>CardCostHelper.TryModifyEnergyCostWithHooks</c> (energy cost color) over the filtered listeners: the
    /// <see cref="AbstractModel"/> defaults return false and leave the cost as is, so skipping them changes nothing.
    /// </summary>
    private static bool EnergyCostColorPrefix(CardModel card, ICombatState state, ref decimal hookModifiedCost, ref bool __result)
    {
        if (!Active || state is not CombatState combat || PatchSafety.IsPatchedByOthers(_energyCostColor))
        {
            return true;
        }
        try
        {
            List<AbstractModel>? early = ForCombat(combat, EnergyCost);
            List<AbstractModel>? late = ForCombat(combat, EnergyCostLate);
            if (early == null || late == null)
            {
                return true;
            }
            // Vanilla iterates state.IterateHookListeners() directly here (no "combat ending" check).
            hookModifiedCost = card.EnergyCost.GetWithModifiers(CostModifiers.None);
            bool modified = false;
            foreach (AbstractModel item in early)
            {
                modified |= item.TryModifyEnergyCostInCombat(card, hookModifiedCost, out hookModifiedCost);
            }
            foreach (AbstractModel item in late)
            {
                modified |= item.TryModifyEnergyCostInCombatLate(card, hookModifiedCost, out hookModifiedCost);
            }
#if BENCH
            CheckCostColor(_energyCostColor!, card, state, modified, hookModifiedCost);
#endif
            __result = modified;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>Mirrors <c>CardCostHelper.TryModifyStarCostWithHooks</c> (star cost color) over the filtered listeners.</summary>
    private static bool StarCostColorPrefix(CardModel card, ICombatState state, ref decimal hookModifiedCost, ref bool __result)
    {
        if (!Active || state is not CombatState combat || PatchSafety.IsPatchedByOthers(_starCostColor))
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = ForCombat(combat, StarCost);
            if (listeners == null)
            {
                return true;
            }
            hookModifiedCost = card.BaseStarCost;
            bool modified = false;
            foreach (AbstractModel item in listeners)
            {
                modified |= item.TryModifyStarCost(card, hookModifiedCost, out hookModifiedCost);
            }
#if BENCH
            CheckCostColor(_starCostColor!, card, state, modified, hookModifiedCost);
#endif
            __result = modified;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

#if BENCH
    /// <summary>Debug, with bench checks on: 1 call in 64 compared with the game's own helper run without caches.</summary>
    private static void CheckCostColor(MethodInfo helper, CardModel card, ICombatState state, bool modified, decimal cost)
    {
        if (!ExtraScopes.BenchChecks || ++CostColorChecks % 64 != 0)
        {
            return;
        }
        (bool Modified, decimal Cost) vanilla = ListenerCache.RunVanilla(() =>
        {
            object[] args = { card, state, 0m };
            bool result = (bool)helper.Invoke(null, args)!;
            return (result, (decimal)args[2]);
        });
        if (vanilla.Modified != modified || vanilla.Cost != cost)
        {
            CostColorMismatches++;
            Log.Error($"[{ModEntry.Id}] {Name}: {helper.Name}({card}) filtered ({modified}, {cost}), vanilla ({vanilla.Modified}, {vanilla.Cost})");
        }
    }
#endif

    /// <summary>Mirrors <see cref="Hook.ShouldAllowHitting"/> over the filtered listeners.</summary>
    private static bool ShouldAllowHittingPrefix(ICombatState combatState, Creature creature, ref bool __result)
    {
        if (!Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = ForCombat(state, AllowHitting);
            if (listeners == null)
            {
                return true;
            }
            // Vanilla goes through IterateCombatHookListeners, which yields nothing while combat is ending.
            bool allowed = true;
            if (!CombatHookListenersEmpty)
            {
                foreach (AbstractModel item in listeners)
                {
                    if (!item.ShouldAllowHitting(creature))
                    {
                        allowed = false;
                        break;
                    }
                }
            }
#if BENCH
            if (ExtraScopes.BenchChecks && ++HittingChecks % 64 == 0)
            {
                bool vanilla = ListenerCache.RunVanilla(() => Hook.ShouldAllowHitting(combatState, creature));
                if (vanilla != allowed)
                {
                    HittingMismatches++;
                    Log.Error($"[{ModEntry.Id}] {Name}: ShouldAllowHitting({creature}) filtered {allowed}, vanilla {vanilla}");
                }
            }
#endif
            __result = allowed;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>Mirrors <see cref="Hook.ModifyKeywordsInCombat"/> over the filtered listeners.</summary>
    private static bool ModifyKeywordsPrefix(ICombatState combatState, CardModel card, ISet<CardKeyword> keywords)
    {
        if (!Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = ForCombat(state, Keywords);
            if (listeners == null)
            {
                return true;
            }
            // Vanilla iterates combatState.IterateHookListeners() directly here (no "combat ending" check).
            foreach (AbstractModel item in listeners)
            {
                item.TryModifyKeywordsInCombat(card, keywords);
            }
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>Mirrors <see cref="Hook.ShouldPlay"/> over the filtered listeners.</summary>
    private static bool ShouldPlayPrefix(ICombatState combatState, CardModel card, ref AbstractModel? preventer, AutoPlayType autoPlayType, ref bool __result)
    {
        if (!Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = ForCombat(state, ShouldPlay);
            if (listeners == null)
            {
                return true;
            }
            if (!CombatHookListenersEmpty)
            {
                foreach (AbstractModel item in listeners)
                {
                    if (!item.ShouldPlay(card, autoPlayType))
                    {
                        preventer = item;
                        __result = false;
                        return false;
                    }
                }
            }
            preventer = null;
            __result = true;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>Mirrors <c>Hook.ModifyDamageInternal</c> over the filtered run listeners.</summary>
    private static bool ModifyDamageInternalPrefix(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
        decimal damage, ValueProp props, CardModel? cardSource, ModifyDamageHookType modifyDamageHookType,
        ref List<AbstractModel> modifiers, ref decimal __result)
    {
        if (!Active || runState is not RunState run)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? additive = ForRun(run, combatState, DamageAdditive);
            List<AbstractModel>? multiplicative = ForRun(run, combatState, DamageMultiplicative);
            List<AbstractModel>? cap = ForRun(run, combatState, DamageCap);
            if (additive == null || multiplicative == null || cap == null)
            {
                return true;
            }
            decimal num = damage;
            List<AbstractModel> list = new();
            if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive))
            {
                foreach (AbstractModel item in additive)
                {
                    decimal delta = item.ModifyDamageAdditive(target, num, props, dealer, cardSource);
                    num += delta;
                    if (delta != 0m)
                    {
                        list.Add(item);
                    }
                }
            }
            if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
            {
                foreach (AbstractModel item in multiplicative)
                {
                    decimal factor = item.ModifyDamageMultiplicative(target, num, props, dealer, cardSource);
                    num *= factor;
                    if (factor != 1m)
                    {
                        list.Add(item);
                    }
                }
            }
            if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Cap))
            {
                decimal lowestCap = decimal.MaxValue;
                foreach (AbstractModel item in cap)
                {
                    decimal itemCap = item.ModifyDamageCap(target, props, dealer, cardSource);
                    if (itemCap < lowestCap)
                    {
                        lowestCap = itemCap;
                        if (num > itemCap)
                        {
                            num = itemCap;
                            list.Add(item);
                        }
                    }
                }
            }
            modifiers = list;
            __result = num;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
