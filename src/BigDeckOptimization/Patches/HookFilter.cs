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
        /// <summary>The AbstractModel default still has the no-op code the filtering skips (checked once).</summary>
        private readonly bool _defaultHolds;

        public FilteredHook(string baseMethodName, string defaultFingerprint, MethodInfo hookMethod)
        {
            BaseMethod = AccessTools.Method(typeof(AbstractModel), baseMethodName)
                ?? throw new MissingMethodException(nameof(AbstractModel), baseMethodName);
            _hookMethod = hookMethod;
            string actual = MethodFingerprint.Of(BaseMethod);
            _defaultHolds = actual == defaultFingerprint;
            if (!_defaultHolds)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: AbstractModel.{baseMethodName} default changed (fingerprint {actual}, expected {defaultFingerprint}): its hook left vanilla");
            }
        }

        public bool IsSafe() => _defaultHolds && !PatchSafety.IsPatchedByOthers(BaseMethod) && !PatchSafety.IsPatchedByOthers(_hookMethod);

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
    internal const int EnergyCost = 0, EnergyCostLate = 1, Keywords = 2, ShouldPlay = 3;
    internal const int DamageAdditive = 4, DamageMultiplicative = 5, DamageCap = 6;
    internal const int AllowHitting = 7, StarCost = 8;

#if BENCH
    public static long HittingChecks { get; internal set; }
    public static long HittingMismatches { get; internal set; }
    public static long CostColorChecks { get; internal set; }
    public static long CostColorMismatches { get; internal set; }
#endif

    /// <summary>v0.107.1 hooks mirrored by the prefixes below; a changed hook is left vanilla.</summary>
    private const string ModifyEnergyCostFingerprint = "081395225799F6BD";
    private const string ModifyKeywordsFingerprint = "ABD6C412D3CBF743";
    private const string ShouldPlayFingerprint = "AB809B7EE9837D5F";
    private const string ModifyDamageInternalFingerprint = "A4F307EAF0B5E7B1";
    private const string ShouldAllowHittingFingerprint = "C4F317D182865D23";

    /// <summary>Game v0.111 (beta) <c>Hook.ModifyDamageInternal</c>: the same loops passing a new <c>CardPlay</c> to each listener.</summary>
    private const string ModifyDamageInternalWithCardPlayFingerprint = "7E4311B76F856D7F";

    /// <summary>
    /// Code of the <see cref="AbstractModel"/> defaults the filtering skips (the same in v0.107.1 and v0.111): "try"
    /// methods copying the cost to the out parameter and returning false, ShouldX returning true, keywords returning false,
    /// damage +0, ×1, cap MaxValue. A changed default leaves its hook vanilla.
    /// </summary>
    private const string TryOutDefault = "DB289946DCE7F74D", TrueDefault = "E11E49A74833605F", KeywordsDefault = "B9D92F458020A730",
        DamageAdditiveDefault = "CFD452ED10FEAAFA", DamageMultiplicativeDefault = "4AE82AACF5E60A0C", DamageCapDefault = "26EBA2F72C9799DF";

    /// <summary>
    /// v0.107.1 <c>CardCostHelper.TryModifyEnergyCostWithHooks</c> / <c>TryModifyStarCostWithHooks</c>: the cost colors
    /// of every card shown in combat (each hand card after every action) ask every combat listener, with their own
    /// copy of the cost hooks' loops. Mirrored by <see cref="EnergyCostColorPrefix"/> / <see cref="StarCostColorPrefix"/>.
    /// </summary>
    private const string EnergyCostColorFingerprint = "DA4F986AFFB60B1C";
    private const string StarCostColorFingerprint = "9DF34236EE3F9BC5";

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
            new FilteredHook(nameof(AbstractModel.TryModifyEnergyCostInCombat), TryOutDefault, energyHook),
            new FilteredHook(nameof(AbstractModel.TryModifyEnergyCostInCombatLate), TryOutDefault, energyHook),
            new FilteredHook(nameof(AbstractModel.TryModifyKeywordsInCombat), KeywordsDefault, keywordsHook),
            new FilteredHook(nameof(AbstractModel.ShouldPlay), TrueDefault, shouldPlayHook),
            new FilteredHook(nameof(AbstractModel.ModifyDamageAdditive), DamageAdditiveDefault, damageHook),
            new FilteredHook(nameof(AbstractModel.ModifyDamageMultiplicative), DamageMultiplicativeDefault, damageHook),
            new FilteredHook(nameof(AbstractModel.ModifyDamageCap), DamageCapDefault, damageHook),
            new FilteredHook(nameof(AbstractModel.ShouldAllowHitting), TrueDefault, hittingHook),
            new FilteredHook(nameof(AbstractModel.TryModifyStarCost), TryOutDefault, starColor ?? hittingHook),
        };
        Mirror(harmony, typeof(HookFilterEnergyCost), energyHook, ModifyEnergyCostFingerprint, "ModifyEnergyCostPrefix");
        Mirror(harmony, typeof(HookFilterKeywords), keywordsHook, ModifyKeywordsFingerprint, "ModifyKeywordsPrefix");
        Mirror(harmony, typeof(HookFilterShouldPlay), shouldPlayHook, ShouldPlayFingerprint, "ShouldPlayPrefix");
        // Game v0.111 (beta) passes a CardPlay to the damage methods: a mirror per signature, calling AbstractModel's
        // methods through delegates made at load, so this class compiles and loads on both versions.
        try
        {
            PatchSafety.RequireResolvable(typeof(HookFilterDamage));
            PatchGuard.Register(Name, typeof(HookFilterDamage), Array.Empty<PatchGuard.Installed>());
            if (HookFilterDamage.Init())
            {
                PatchIfUnchanged(harmony, damageHook, ModifyDamageInternalWithCardPlayFingerprint,
                    new HarmonyMethod(typeof(HookFilterDamage), nameof(HookFilterDamage.PrefixWithCardPlay)));
            }
            else
            {
                PatchIfUnchanged(harmony, damageHook, ModifyDamageInternalFingerprint, new HarmonyMethod(typeof(HookFilterDamage), nameof(HookFilterDamage.Prefix)));
            }
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: Hook.ModifyDamageInternal left vanilla: {e.Message}");
        }
        // Read by Creature.IsHittable, e.g. BetterSpire2's damage preview several times a second.
        Mirror(harmony, typeof(HookFilterHitting), hittingHook, ShouldAllowHittingFingerprint, "ShouldAllowHittingPrefix");
        // Optional: a missing or changed cost color helper only leaves that helper vanilla.
        if (Mirror(harmony, typeof(HookFilterCostColors), energyColor, EnergyCostColorFingerprint, "EnergyCostColorPrefix"))
        {
            HookFilterCostColors.EnergyCostColor = energyColor;
        }
        if (Mirror(harmony, typeof(HookFilterCostColors), starColor, StarCostColorFingerprint, "StarCostColorPrefix"))
        {
            HookFilterCostColors.StarCostColor = starColor;
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

    /// <summary>
    /// Patches one mirror (its own class) onto the method it mirrors when that method's code is the one mirrored and the
    /// class only uses what this game has; otherwise only that method stays vanilla. True when patched.
    /// </summary>
    private static bool Mirror(Harmony harmony, Type mirror, MethodInfo? target, string fingerprint, string prefix)
    {
        if (target == null)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: target of {mirror.Name} missing: left vanilla");
            return false;
        }
        try
        {
            PatchSafety.RequireResolvable(mirror);
            PatchGuard.Register(Name, mirror, Array.Empty<PatchGuard.Installed>());
            return PatchIfUnchanged(harmony, target, fingerprint, new HarmonyMethod(mirror, prefix));
        }
        catch (PatchSkippedException e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {target.DeclaringType?.Name}.{target.Name} left vanilla: {e.Message}");
            return false;
        }
    }

    private static bool PatchIfUnchanged(Harmony harmony, MethodInfo hook, string fingerprint, HarmonyMethod prefix)
    {
        string actual = MethodFingerprint.Of(hook);
        if (PatchSafety.FingerprintMatches(actual, fingerprint))
        {
            PatchGuard.Patch(harmony, hook, prefix: prefix);
            return true;
        }
        Log.Info($"[{ModEntry.Id}] {Name}: {hook.DeclaringType?.Name}.{hook.Name} changed (fingerprint {actual}): left vanilla");
        return false;
    }

    private static MethodInfo Require(MethodInfo? method) =>
        method ?? throw new MissingMethodException("HookFilter target not found (game update?)");

    internal static bool Active => Enabled && ListenerCache.IsCaching;

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
    internal static List<AbstractModel>? ForCombat(CombatState state, int hook) =>
        CanWalk
            ? Walked((state, null), hook, filter => FastListeners.Filtered(state, filter), () => ListenerCache.GetListeners(state))
            : Get(ListenerCache.GetListeners(state), hook);

    /// <summary>The run listeners (deck + child combat) of one hook, as <see cref="ForCombat"/>.</summary>
    internal static List<AbstractModel>? ForRun(RunState run, ICombatState? child, int hook) =>
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

    internal static bool CombatHookListenersEmpty =>
        CombatManager.Instance.IsOverOrEnding && !CombatManager.Instance.IsStarting;

    internal static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}

/// <summary>
/// <see cref="HookFilter"/>'s mirror of <c>Hook.ModifyDamageInternal</c>, in its own class (its own load check). The
/// <see cref="AbstractModel"/> damage methods take a <c>CardPlay</c> since game v0.111 (beta): they are called through
/// delegates made by <see cref="Init"/> for the signature this game has, so the same code runs on both versions. Same
/// file, so conflicts with other mods are still recorded under HookFilter.
/// </summary>
internal static class HookFilterDamage
{
    private static Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>? _additive, _multiplicative;
    private static Func<AbstractModel, Creature?, ValueProp, Creature?, CardModel?, decimal>? _cap;
    private static Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>? _additivePlay, _multiplicativePlay;
    private static Func<AbstractModel, Creature?, ValueProp, Creature?, CardModel?, CardPlay?, decimal>? _capPlay;

    /// <summary>Makes the delegates (virtual calls, like the hook's); true when this game's damage methods take a CardPlay.</summary>
    internal static bool Init()
    {
        Type[] value = { typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel) };
        Type[] cap = { typeof(Creature), typeof(ValueProp), typeof(Creature), typeof(CardModel) };
        MethodInfo? additive = AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageAdditive), value.Append(typeof(CardPlay)).ToArray());
        if (additive != null)
        {
            _additivePlay = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(additive);
            _multiplicativePlay = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(
                AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageMultiplicative), value.Append(typeof(CardPlay)).ToArray()));
            _capPlay = Make<Func<AbstractModel, Creature?, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(
                AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageCap), cap.Append(typeof(CardPlay)).ToArray()));
            return true;
        }
        _additive = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(
            AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageAdditive), value));
        _multiplicative = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(
            AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageMultiplicative), value));
        _cap = Make<Func<AbstractModel, Creature?, ValueProp, Creature?, CardModel?, decimal>>(
            AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageCap), cap));
        return false;
    }

    private static T Make<T>(MethodInfo? method) where T : Delegate =>
        AccessTools.MethodDelegate<T>(method ?? throw new MissingMethodException("AbstractModel damage method with the expected signature"), virtualCall: true);

    /// <summary>Game v0.107: mirrors <c>Hook.ModifyDamageInternal</c> over the filtered run listeners.</summary>
    internal static bool Prefix(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
        decimal damage, ValueProp props, CardModel? cardSource, ModifyDamageHookType modifyDamageHookType,
        ref List<AbstractModel> modifiers, ref decimal __result) =>
        Mirror(runState, combatState, target, dealer, damage, props, cardSource, null, modifyDamageHookType, ref modifiers, ref __result);

    /// <summary>Game v0.111 (beta): the same hook with the card play passed to every listener.</summary>
    internal static bool PrefixWithCardPlay(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
        decimal damage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, ModifyDamageHookType modifyDamageHookType,
        ref List<AbstractModel> modifiers, ref decimal __result) =>
        Mirror(runState, combatState, target, dealer, damage, props, cardSource, cardPlay, modifyDamageHookType, ref modifiers, ref __result);

    private static decimal Additive(AbstractModel item, Creature? target, decimal num, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) =>
        _additivePlay != null ? _additivePlay(item, target, num, props, dealer, cardSource, cardPlay) : _additive!(item, target, num, props, dealer, cardSource);

    private static decimal Multiplicative(AbstractModel item, Creature? target, decimal num, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) =>
        _multiplicativePlay != null ? _multiplicativePlay(item, target, num, props, dealer, cardSource, cardPlay) : _multiplicative!(item, target, num, props, dealer, cardSource);

    private static decimal Cap(AbstractModel item, Creature? target, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) =>
        _capPlay != null ? _capPlay(item, target, props, dealer, cardSource, cardPlay) : _cap!(item, target, props, dealer, cardSource);

    /// <summary>Mirrors <c>Hook.ModifyDamageInternal</c> over the filtered run listeners.</summary>
    private static bool Mirror(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
        decimal damage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, ModifyDamageHookType modifyDamageHookType,
        ref List<AbstractModel> modifiers, ref decimal __result)
    {
        if (!HookFilter.Active || runState is not RunState run)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? additive = HookFilter.ForRun(run, combatState, HookFilter.DamageAdditive);
            List<AbstractModel>? multiplicative = HookFilter.ForRun(run, combatState, HookFilter.DamageMultiplicative);
            List<AbstractModel>? cap = HookFilter.ForRun(run, combatState, HookFilter.DamageCap);
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
                    decimal delta = Additive(item, target, num, props, dealer, cardSource, cardPlay);
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
                    decimal factor = Multiplicative(item, target, num, props, dealer, cardSource, cardPlay);
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
                    decimal itemCap = Cap(item, target, props, dealer, cardSource, cardPlay);
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
            HookFilter.Disable(e);
            return true;
        }
    }
}

/// <summary><see cref="HookFilter"/>'s mirror of <c>Hook.ModifyEnergyCostInCombat</c>. In its own class (own load check, <see cref="PatchSafety.RequireResolvable"/>): a game member it
/// uses that changes leaves only this hook vanilla. Same file, so conflicts are recorded under HookFilter.</summary>
internal static class HookFilterEnergyCost
{
    /// <summary>Mirrors <see cref="Hook.ModifyEnergyCostInCombat"/> over the filtered listeners.</summary>
    internal static bool ModifyEnergyCostPrefix(ICombatState combatState, CardModel card, decimal originalCost, ref decimal __result)
    {
        if (!HookFilter.Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? early = HookFilter.ForCombat(state, HookFilter.EnergyCost);
            List<AbstractModel>? late = HookFilter.ForCombat(state, HookFilter.EnergyCostLate);
            if (early == null || late == null)
            {
                return true;
            }
            // Vanilla: negative costs return early; IterateCombatHookListeners yields nothing while combat is ending.
            if (originalCost < 0m || HookFilter.CombatHookListenersEmpty)
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
            HookFilter.Disable(e);
            return true;
        }
    }
}

/// <summary><see cref="HookFilter"/>'s mirror of <c>Hook.ModifyKeywordsInCombat</c>. In its own class (own load check, <see cref="PatchSafety.RequireResolvable"/>): a game member it
/// uses that changes leaves only this hook vanilla. Same file, so conflicts are recorded under HookFilter.</summary>
internal static class HookFilterKeywords
{
    /// <summary>Mirrors <see cref="Hook.ModifyKeywordsInCombat"/> over the filtered listeners.</summary>
    internal static bool ModifyKeywordsPrefix(ICombatState combatState, CardModel card, ISet<CardKeyword> keywords)
    {
        if (!HookFilter.Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = HookFilter.ForCombat(state, HookFilter.Keywords);
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
            HookFilter.Disable(e);
            return true;
        }
    }
}

/// <summary><see cref="HookFilter"/>'s mirror of <c>Hook.ShouldPlay</c>. In its own class (own load check, <see cref="PatchSafety.RequireResolvable"/>): a game member it
/// uses that changes leaves only this hook vanilla. Same file, so conflicts are recorded under HookFilter.</summary>
internal static class HookFilterShouldPlay
{
    /// <summary>Mirrors <see cref="Hook.ShouldPlay"/> over the filtered listeners.</summary>
    internal static bool ShouldPlayPrefix(ICombatState combatState, CardModel card, ref AbstractModel? preventer, AutoPlayType autoPlayType, ref bool __result)
    {
        if (!HookFilter.Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = HookFilter.ForCombat(state, HookFilter.ShouldPlay);
            if (listeners == null)
            {
                return true;
            }
            if (!HookFilter.CombatHookListenersEmpty)
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
            HookFilter.Disable(e);
            return true;
        }
    }
}

/// <summary><see cref="HookFilter"/>'s mirror of <c>Hook.ShouldAllowHitting</c>. In its own class (own load check, <see cref="PatchSafety.RequireResolvable"/>): a game member it
/// uses that changes leaves only this hook vanilla. Same file, so conflicts are recorded under HookFilter.</summary>
internal static class HookFilterHitting
{
    /// <summary>Mirrors <see cref="Hook.ShouldAllowHitting"/> over the filtered listeners.</summary>
    internal static bool ShouldAllowHittingPrefix(ICombatState combatState, Creature creature, ref bool __result)
    {
        if (!HookFilter.Active || combatState is not CombatState state)
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = HookFilter.ForCombat(state, HookFilter.AllowHitting);
            if (listeners == null)
            {
                return true;
            }
            // Vanilla goes through IterateCombatHookListeners, which yields nothing while combat is ending.
            bool allowed = true;
            if (!HookFilter.CombatHookListenersEmpty)
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
            if (ExtraScopes.BenchChecks && ++HookFilter.HittingChecks % 64 == 0)
            {
                bool vanilla = ListenerCache.RunVanilla(() => Hook.ShouldAllowHitting(combatState, creature));
                if (vanilla != allowed)
                {
                    HookFilter.HittingMismatches++;
                    Log.Error($"[{ModEntry.Id}] {HookFilter.Name}: ShouldAllowHitting({creature}) filtered {allowed}, vanilla {vanilla}");
                }
            }
#endif
            __result = allowed;
            return false;
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
    }
}

/// <summary><see cref="HookFilter"/>'s mirrors of the cost color helpers (<c>CardCostHelper</c>). In its own class (own load check, <see cref="PatchSafety.RequireResolvable"/>): a game member it
/// uses that changes leaves only this hook vanilla. Same file, so conflicts are recorded under HookFilter.</summary>
internal static class HookFilterCostColors
{
    internal static MethodInfo? EnergyCostColor, StarCostColor;

    /// <summary>
    /// Mirrors <c>CardCostHelper.TryModifyEnergyCostWithHooks</c> (energy cost color) over the filtered listeners: the
    /// <see cref="AbstractModel"/> defaults return false and leave the cost as is, so skipping them changes nothing.
    /// </summary>
    internal static bool EnergyCostColorPrefix(CardModel card, ICombatState state, ref decimal hookModifiedCost, ref bool __result)
    {
        if (!HookFilter.Active || state is not CombatState combat || PatchSafety.IsPatchedByOthers(EnergyCostColor))
        {
            return true;
        }
        try
        {
            List<AbstractModel>? early = HookFilter.ForCombat(combat, HookFilter.EnergyCost);
            List<AbstractModel>? late = HookFilter.ForCombat(combat, HookFilter.EnergyCostLate);
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
            CheckCostColor(EnergyCostColor!, card, state, modified, hookModifiedCost);
#endif
            __result = modified;
            return false;
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
    }

    /// <summary>Mirrors <c>CardCostHelper.TryModifyStarCostWithHooks</c> (star cost color) over the filtered listeners.</summary>
    internal static bool StarCostColorPrefix(CardModel card, ICombatState state, ref decimal hookModifiedCost, ref bool __result)
    {
        if (!HookFilter.Active || state is not CombatState combat || PatchSafety.IsPatchedByOthers(StarCostColor))
        {
            return true;
        }
        try
        {
            List<AbstractModel>? listeners = HookFilter.ForCombat(combat, HookFilter.StarCost);
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
            CheckCostColor(StarCostColor!, card, state, modified, hookModifiedCost);
#endif
            __result = modified;
            return false;
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
    }

#if BENCH
    /// <summary>Debug, with bench checks on: 1 call in 64 compared with the game's own helper run without caches.</summary>
    private static void CheckCostColor(MethodInfo helper, CardModel card, ICombatState state, bool modified, decimal cost)
    {
        if (!ExtraScopes.BenchChecks || ++HookFilter.CostColorChecks % 64 != 0)
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
            HookFilter.CostColorMismatches++;
            Log.Error($"[{ModEntry.Id}] {HookFilter.Name}: {helper.Name}({card}) filtered ({modified}, {cost}), vanilla ({vanilla.Modified}, {vanilla.Cost})");
        }
    }
#endif
}
