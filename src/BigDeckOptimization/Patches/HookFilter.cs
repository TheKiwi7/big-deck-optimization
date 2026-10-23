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
    private const int AllowHitting = 7;

#if BENCH
    public static long HittingChecks { get; private set; }
    public static long HittingMismatches { get; private set; }
#endif

    /// <summary>v0.107.1 <c>Hook.ShouldAllowHitting</c>, mirrored by <see cref="ShouldAllowHittingPrefix"/>.</summary>
    private const string ShouldAllowHittingFingerprint = "C4F317D182865D23";

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
        ListenerCache.ScopeEnded += () =>
        {
            // Filtered lists stay valid for their source list (listener lists are immutable and reused across
            // refreshes while nothing changed); they are rebuilt when patch safety changes.
            _safe = null;
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
            IReadOnlyList<AbstractModel> listeners = ListenerCache.GetListeners(state);
            List<AbstractModel>? early = Get(listeners, EnergyCost);
            List<AbstractModel>? late = Get(listeners, EnergyCostLate);
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

    /// <summary>Mirrors <see cref="Hook.ShouldAllowHitting"/> over the filtered listeners.</summary>
    private static bool ShouldAllowHittingPrefix(ICombatState combatState, Creature creature, ref bool __result)
    {
        if (!Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = Get(ListenerCache.GetListeners(state), AllowHitting);
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
            List<AbstractModel>? listeners = Get(ListenerCache.GetListeners(state), Keywords);
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
            List<AbstractModel>? listeners = Get(ListenerCache.GetListeners(state), ShouldPlay);
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
            IReadOnlyList<AbstractModel> listeners = ListenerCache.GetRunListeners(run, combatState);
            List<AbstractModel>? additive = Get(listeners, DamageAdditive);
            List<AbstractModel>? multiplicative = Get(listeners, DamageMultiplicative);
            List<AbstractModel>? cap = Get(listeners, DamageCap);
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
