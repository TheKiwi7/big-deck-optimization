using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// our mirror of the hook body would then no longer match what vanilla peers run; on the hook itself, the patches that run
/// around the mirror exactly as around the original are let stand (<see cref="PatchTolerance.BeforeReplace"/>, since
/// 2026-11-18): prefixes Harmony runs before the mirror (void, or bool and <c>ref __result</c>: one skipping the original skips
/// the mirror the same), and postfixes by value or <c>ref __result</c> (they see the mirror's answer, the original's). Still
/// refused: postfixes reading <c>__runOriginal</c> (false behind the mirror) or <c>__args</c>, by-reference writes,
/// finalizers, transpilers, inner patches, prefixes sorted after the mirror. BaseLib patches <c>Hook.ModifyDamage</c> but not
/// <c>Hook.ModifyDamageInternal</c>, which holds the listener loops.
///
/// Exceptions: one thrown by this mod's code turns HookFilter off and the call runs the game's hook. One thrown by a
/// listener's own code (the game's or a mod's, no frame of this mod between the throw and the mirror) is the one the game's
/// loop throws at the same listener after the same calls: the mirror rethrows it from its prefix (<see cref="PassOn"/>), so
/// Harmony runs no postfix (as the game runs none after a throw) and it goes on to the hook's caller as is, nothing called
/// twice, HookFilter stays on. (Returning true there instead would rerun the game's hook: every listener before the thrower
/// called again, a listener throwing only once then returning a value where the game threw.) When a frame of this mod is on the
/// way (another patch of this mod the listener reached, or a listener the JIT inlined into the mirror), it counts as this
/// mod's: HookFilter turns off and the game's hook calls the listeners before the thrower again (pure Try/Should/Modify
/// methods: same results).
/// </summary>
public static class HookFilter
{
    public const string Name = "HookFilter";

    public static bool Enabled { get; set; } = true;

    private sealed class FilteredHook
    {
        /// <summary>Null when it no longer exists as one method: this hook then stays vanilla.</summary>
        public readonly MethodInfo? BaseMethod;
        private readonly MethodInfo? _hookMethod;
        /// <summary>The AbstractModel default still has the no-op code the filtering skips (checked once).</summary>
        private readonly bool _defaultHolds;
        /// <summary>
        /// A use of this list walks <c>Hook.IterateCombatHookListeners</c> in the game (its "combat ending" check mirrored):
        /// it then also backs off when that walk changed or is patched. The others walk the state's listeners directly.
        /// </summary>
        private readonly bool _combatWalk;
        /// <summary>
        /// The damage defaults (rule R3, 2026-11-18): other mods' patches on the default that do nothing unless the listener is
        /// of some types (TypeGated, HookSkip's own gates) stand, and listeners of those types are kept in the list (their calls
        /// reach the patched default, as in the game). Not the cost, keyword, ShouldPlay or hitting defaults: SnapshotCardReuse's
        /// PureListeners takes the cost and keyword lists' listeners as overriders it knows, which a gate-kept type isn't.
        /// </summary>
        private readonly bool _gated;

        public FilteredHook(string baseMethodName, string defaultFingerprint, MethodInfo? hookMethod, bool combatWalk, bool optional = false, bool gated = false)
        {
            BaseMethod = Find(typeof(AbstractModel), baseMethodName);
            _hookMethod = hookMethod;
            _combatWalk = combatWalk;
            _gated = gated;
            if (BaseMethod == null)
            {
                if (!optional)
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: AbstractModel.{baseMethodName} not found as one method: its hook left vanilla");
                }
                return;
            }
            string actual = MethodFingerprint.Of(BaseMethod);
            _defaultHolds = actual == defaultFingerprint;
            if (!_defaultHolds)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: AbstractModel.{baseMethodName} default changed (fingerprint {actual}, expected {defaultFingerprint}): its hook left vanilla");
            }
        }

        // The hook: BeforeReplace. Prefixes Harmony runs before every prefix of this mod run before the mirror as before the
        // original (a bool one returning false skips both); postfixes see the mirror's __result and out values, the original's,
        // and none runs when a listener threw (the mirror rethrows from its prefix: PassOn), as in the game. Postfixes reading
        // __runOriginal (false behind the mirror) or __args, by-reference writes, finalizers and prefixes sorted after the mirror
        // back off. The default: no tolerance, a skipped listener skips its call to it. Callers that skip the hook call altogether
        // (SnapshotCardReuse's fast reads) check the hook strictly themselves (HookMethodOf). HookSkip's checks of these same Hook
        // methods (SurroundingOnly, TranspiledOnly) let stand shapes this check refuses only because it refuses them (the mirror
        // then runs the original, transpiled): an invariant, a change here needs one there.
        // The damage defaults: TypeGated (_gated), the gates' types kept in the list as overriders.
        public bool IsSafe(out Type[] gates)
        {
            gates = Type.EmptyTypes;
            return _defaultHolds && !(_gated ? PatchSafety.IsPatchedByOthers(BaseMethod, out gates) : PatchSafety.IsPatchedByOthers(BaseMethod))
                && !PatchSafety.IsPatchedByOthers(_hookMethod, PatchTolerance.BeforeReplace) && (!_combatWalk || !CombatHookWalkPatched());
        }

        /// <summary>The hook (or helper) method whose call this list stands for.</summary>
        public MethodInfo? HookMethod => _hookMethod;

        /// <summary>Registers what <see cref="IsSafe"/> asks about, when it gets that far (display only, never throws).</summary>
        public void Watch()
        {
            if (!_defaultHolds)
            {
                return;
            }
            if (_gated)
            {
                PatchSafety.Watch(Name, BaseMethod, PatchTolerance.TypeGated);
            }
            else
            {
                PatchSafety.Watch(Name, BaseMethod);
            }
            PatchSafety.Watch(Name, _hookMethod, PatchTolerance.BeforeReplace);
            if (_combatWalk)
            {
                PatchSafety.WatchAll(Name, CombatHookWalkParts);
            }
        }

        public bool IsOverriddenBy(Type type)
        {
            if (BaseMethod == null)
            {
                return false; // No such method here: its hook is never safe, no list is built for it.
            }
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

    private static MethodBase?[]? _combatHookWalkParts;
    private static bool? _combatHookWalkKnown;
    /// <summary>The check threw once (logged once): asked per hook walk, it must not log or redo the lookups each time.</summary>
    private static bool _combatHookWalkCheckFailed;

    /// <summary>v0.107.1 <c>Hook.IterateCombatHookListeners</c> (its state machine), equivalents in EquivalentFingerprints.</summary>
    private const string CombatHookWalkFingerprint = "B3B83A82B69E17FC";

    /// <summary>
    /// v0.113 <c>CombatManager.ShouldSkipCombatEffect</c>: <c>IsOverOrEnding ? !IsStarting : false</c>, the walk's "combat
    /// ending" check since v0.113 (mirrored as those two getters).
    /// </summary>
    private const string ShouldSkipCombatEffectFingerprint = "43E664940DA33607";

    /// <summary>
    /// The combat hooks mirrored here that go through <c>Hook.IterateCombatHookListeners</c> in the game (energy cost,
    /// ShouldPlay, ShouldAllowHitting), and HookSkip's combat walk, mirror its "combat ending" check
    /// (<see cref="CombatHookListenersEmpty"/>): true when that code isn't the
    /// one mirrored, or another mod patches it or the getters it reads (v0.113: <c>ShouldSkipCombatEffect</c>, which the
    /// mirror reads as the two getters it combines): the game's hooks then.
    /// </summary>
    internal static bool CombatHookWalkPatched([System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        try
        {
            if (_combatHookWalkParts == null)
            {
                LookUpCombatHookWalk();
            }
            if (_combatHookWalkKnown != true)
            {
                return true;
            }
            foreach (MethodBase? part in _combatHookWalkParts!)
            {
                // No tolerance: the filtered walk mirrors this walk and the getters it reads; a postfix could change the walk.
                if (PatchSafety.IsPatchedByOthers(part, caller))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception e)
        {
            if (_combatHookWalkParts == null)
            {
                // The lookups themselves failed: answered "changed" from now on (game hooks), not looked up again.
                _combatHookWalkKnown = false;
                _combatHookWalkParts = Array.Empty<MethodBase?>();
            }
            if (!_combatHookWalkCheckFailed)
            {
                _combatHookWalkCheckFailed = true;
                Log.Warn($"[{ModEntry.Id}] {Name}: combat hook walk not checked ({e.Message}): game hooks");
            }
            return true;
        }
    }

    /// <summary>
    /// The methods <see cref="CombatHookWalkPatched"/> asks about when the walk is the known code (none otherwise: it then
    /// answers "changed" without asking), for the back-off preflight of the patches calling it. A failed lookup gives none
    /// here and is left to the runtime check, which handles it.
    /// </summary>
    internal static IEnumerable<MethodBase?> CombatHookWalkParts()
    {
        if (_combatHookWalkParts == null)
        {
            try
            {
                LookUpCombatHookWalk();
            }
            catch
            {
                return Array.Empty<MethodBase?>();
            }
        }
        return _combatHookWalkKnown == true ? _combatHookWalkParts! : Array.Empty<MethodBase?>();
    }

    /// <summary>The walk's methods and whether they are the code mirrored (once; may throw: callers handle it).</summary>
    private static void LookUpCombatHookWalk()
    {
        MethodInfo? walk = Reflect.Method(typeof(Hook), "IterateCombatHookListeners");
        MethodBase? body = walk == null ? null : Reflect.EnumeratorMoveNext(walk);
        _combatHookWalkKnown = body != null && PatchSafety.FingerprintMatches(MethodFingerprint.Of(body), CombatHookWalkFingerprint);
        if (_combatHookWalkKnown != true)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: Hook.IterateCombatHookListeners changed: combat hooks left vanilla");
        }
        var parts = new List<MethodBase?> { walk, body,
            Reflect.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsOverOrEnding)),
            Reflect.PropertyGetter(typeof(CombatManager), nameof(CombatManager.IsStarting)) };
        if (Reflect.PropertyGetter(typeof(CombatManager), "ShouldSkipCombatEffect") is { } skip)
        {
            parts.Add(skip);
            if (_combatHookWalkKnown == true && MethodFingerprint.Of(skip) != ShouldSkipCombatEffectFingerprint)
            {
                _combatHookWalkKnown = false;
                Log.Info($"[{ModEntry.Id}] {Name}: CombatManager.ShouldSkipCombatEffect changed: combat hooks left vanilla");
            }
        }
        _combatHookWalkParts = parts.ToArray();
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
    /// <summary>Game v0.113 (beta) only: a third multiplicative damage loop (absent before: never safe, never used).</summary>
    internal const int DamageMultiplicativeLate = 9;

#if BENCH
    public static long HittingChecks { get; internal set; }
    public static long HittingMismatches { get; internal set; }
    public static long CostColorChecks { get; internal set; }
    public static long CostColorMismatches { get; internal set; }
    /// <summary>Hook lists built with only the listeners overriding the hook (the effects check).</summary>
    public static long ListsFiltered { get; private set; }
    /// <summary>
    /// Listeners left out of those lists (the effects check: a list can be built while nothing is left out). The pile entries
    /// FastListeners' pile filter leaves out before are counted there (FastListeners.Kinds/.Others count HookSkip's filters).
    /// </summary>
    public static long ListenersDropped { get; private set; }
    /// <summary>Filtered lists handed out (a cached one included): what bdperf checksum's filter row requires to have moved.</summary>
    public static long Served { get; private set; }
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
    /// Game v0.113 (beta) <c>Hook.ModifyDamageInternal</c>: v0.111's, with a third loop after the multiplicative one,
    /// in the same <c>Multiplicative</c> flag, over <c>ModifyDamageMultiplicativeLate</c> (default ×1, the same code as
    /// <c>ModifyDamageMultiplicative</c>'s).
    /// </summary>
    private const string ModifyDamageInternalLateFingerprint = "F7ABF27EB7D96625";

    /// <summary>
    /// Code of the <see cref="AbstractModel"/> defaults the filtering skips (the same in v0.107.1, v0.111.0, v0.113.0 and v0.113.1): "try"
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

    /// <summary>
    /// Filtered lists per source listener list (a new source list means the cache was invalidated). Weak keys: an entry goes
    /// with its source list, which ListenerCache drops (the lists of a finished combat aren't kept here until the run ends).
    /// </summary>
    [ThreadStatic] private static ConditionalWeakTable<IReadOnlyList<AbstractModel>, List<AbstractModel>?[]>? _filtered;

    /// <summary>Per-scope result of <see cref="FilteredHook.IsSafe"/> (patch checks are not free).</summary>
    [ThreadStatic] private static bool[]? _safe;

    public static void Apply(Harmony harmony)
    {
        // One gone only leaves that hook vanilla (Mirror says so).
        MethodInfo? energyHook = Find(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat));
        MethodInfo? keywordsHook = Find(typeof(Hook), nameof(Hook.ModifyKeywordsInCombat));
        MethodInfo? shouldPlayHook = Find(typeof(Hook), nameof(Hook.ShouldPlay));
        MethodInfo? damageHook = Find(typeof(Hook), "ModifyDamageInternal");
        MethodInfo? hittingHook = Find(typeof(Hook), nameof(Hook.ShouldAllowHitting));
        // Optional: a missing or changed cost color helper only leaves that helper vanilla.
        MethodInfo? energyColor = Reflect.Method(typeof(MegaCrit.Sts2.Core.Helpers.Models.CardCostHelper), "TryModifyEnergyCostWithHooks");
        MethodInfo? starColor = Reflect.Method(typeof(MegaCrit.Sts2.Core.Helpers.Models.CardCostHelper), "TryModifyStarCostWithHooks");
        // combatWalk: the game's hook walks IterateCombatHookListeners (energy cost, ShouldPlay, hitting). Keywords, the cost
        // color helpers and the damage hook walk the state's listeners directly: a mod patching that walk or the combat
        // getters it reads doesn't change them. The energy cost lists also serve the energy cost color helper (direct walk)
        // and SnapshotCardReuse's cost reads (the combat walk's), so they keep the check.
        _hooks = new[]
        {
            new FilteredHook(nameof(AbstractModel.TryModifyEnergyCostInCombat), TryOutDefault, energyHook, combatWalk: true),
            new FilteredHook(nameof(AbstractModel.TryModifyEnergyCostInCombatLate), TryOutDefault, energyHook, combatWalk: true),
            new FilteredHook(nameof(AbstractModel.TryModifyKeywordsInCombat), KeywordsDefault, keywordsHook, combatWalk: false),
            new FilteredHook(nameof(AbstractModel.ShouldPlay), TrueDefault, shouldPlayHook, combatWalk: true),
            new FilteredHook(nameof(AbstractModel.ModifyDamageAdditive), DamageAdditiveDefault, damageHook, combatWalk: false, gated: true),
            new FilteredHook(nameof(AbstractModel.ModifyDamageMultiplicative), DamageMultiplicativeDefault, damageHook, combatWalk: false, gated: true),
            new FilteredHook(nameof(AbstractModel.ModifyDamageCap), DamageCapDefault, damageHook, combatWalk: false, gated: true),
            new FilteredHook(nameof(AbstractModel.ShouldAllowHitting), TrueDefault, hittingHook, combatWalk: true),
            new FilteredHook(nameof(AbstractModel.TryModifyStarCost), TryOutDefault, starColor ?? hittingHook, combatWalk: false),
            new FilteredHook("ModifyDamageMultiplicativeLate", DamageMultiplicativeDefault, damageHook, combatWalk: false, optional: true, gated: true),
        };
        Mirror(harmony, typeof(HookFilterEnergyCost), energyHook, ModifyEnergyCostFingerprint, "ModifyEnergyCostPrefix");
        Mirror(harmony, typeof(HookFilterKeywords), keywordsHook, ModifyKeywordsFingerprint, "ModifyKeywordsPrefix");
        Mirror(harmony, typeof(HookFilterShouldPlay), shouldPlayHook, ShouldPlayFingerprint, "ShouldPlayPrefix");
        // Game v0.111 (beta) passes a CardPlay to the damage methods: a mirror per signature, calling AbstractModel's
        // methods through delegates made at load, so this class compiles and loads on both versions.
        try
        {
            if (damageHook == null)
            {
                throw new MissingMethodException(nameof(Hook), "ModifyDamageInternal");
            }
            PatchSafety.RequireResolvable(typeof(HookFilterDamage));
            PatchGuard.Register(Name, typeof(HookFilterDamage), Array.Empty<PatchGuard.Installed>());
            if (HookFilterDamage.Init())
            {
                if (HookFilterDamage.HasLate && MethodFingerprint.Of(damageHook) == ModifyDamageInternalLateFingerprint)
                {
                    HarmonyMethod late = MirrorPrefix(typeof(HookFilterDamage), nameof(HookFilterDamage.PrefixWithLate));
                    RegisterMirror(late);
                    PatchGuard.Patch(harmony, damageHook, prefix: late, finalizer: Finalizer);
                }
                else
                {
                    PatchIfUnchanged(harmony, damageHook, ModifyDamageInternalWithCardPlayFingerprint,
                        MirrorPrefix(typeof(HookFilterDamage), nameof(HookFilterDamage.PrefixWithCardPlay)));
                }
            }
            else
            {
                PatchIfUnchanged(harmony, damageHook, ModifyDamageInternalFingerprint, MirrorPrefix(typeof(HookFilterDamage), nameof(HookFilterDamage.Prefix)));
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
        // What the filtered lists' safety and the cost color mirrors ask about, checked from the main menu on (display only).
        foreach (FilteredHook hook in _hooks)
        {
            hook.Watch();
        }
        PatchSafety.Watch(Name, HookFilterCostColors.EnergyCostColor, PatchTolerance.BeforeReplace);
        PatchSafety.Watch(Name, HookFilterCostColors.StarCostColor, PatchTolerance.BeforeReplace);
        // Display only: the lists come from ListenerCache's scopes, which hand them to this class only while no other mod patches
        // the two walks (ListenerCache.IsCaching: postfixes there are let stand for the walks' own calls, not for lists used
        // directly). Any patch there keeps all of this class vanilla (the menu's red title), not only a hook.
        MethodBase?[] walks =
        {
            Reflect.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)),
            Reflect.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) }),
        };
        PatchSafety.Watch(Name, walks);
        PatchSafety.WholeMethods(Name, walks);
        // The same for the walk code ListenerCache's scopes also require unpatched (both state machines, Contains, ModHelper's
        // subscriber walks: EnterAny's _safe), resolved lazily as ListenerCache's own registration. (The walk_mirrors note needs a
        // patch on the walks themselves, which the check above already shows as a whole back-off of this class.)
        PatchSafety.WatchAll(Name, FastListeners.WalkCodeMethods);
        PatchSafety.WholeMethods(Name, FastListeners.WalkCodeMethods);
        ListenerCache.RunReleased += () =>
        {
            _filtered = null;
            _filteredWith = null;
            _filteredGates = null;
            _walked?.Clear();
        };
        ListenerCache.ScopeEnded += () =>
        {
            // Filtered lists stay valid for their source list (listener lists are immutable and reused across
            // refreshes while nothing changed); Get rebuilds them when patch safety changed. Nothing here that can throw:
            // it runs at the end of every scope, some inside the game's async code (ExtraScopes' first turn).
            _safe = null;
            _gatesNow = null;
            _walked?.Clear();
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
            return PatchIfUnchanged(harmony, target, fingerprint, MirrorPrefix(mirror, prefix));
        }
        catch (PatchSkippedException e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {target.DeclaringType?.Name}.{target.Name} left vanilla: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// A mirror prefix, last of the method's prefixes (<see cref="Priority.Last"/>): another mod's prefix at any higher priority,
    /// or at the same one registered before, runs before it as before the original without this mod, so the tolerance's order
    /// rule lets it stand (PatchSafety's PrecedenceRefusal); only one at <see cref="Priority.Last"/> registered after this mod's
    /// sorts after it. At the default priority, every prefix another mod registered later at its own default sorted after the
    /// mirror, and ties after an Unpatch and Patch depended on Harmony's sort.
    /// </summary>
    private static HarmonyMethod MirrorPrefix(Type mirror, string prefix) => new(mirror, prefix) { priority = Priority.Last };

    private static bool PatchIfUnchanged(Harmony harmony, MethodInfo hook, string fingerprint, HarmonyMethod prefix)
    {
        string actual = MethodFingerprint.Of(hook);
        if (PatchSafety.FingerprintMatches(actual, fingerprint))
        {
            RegisterMirror(prefix);
            PatchGuard.Patch(harmony, hook, prefix: prefix, finalizer: Finalizer);
            return true;
        }
        Log.Info($"[{ModEntry.Id}] {Name}: {hook.DeclaringType?.Name}.{hook.Name} changed (fingerprint {actual}): left vanilla");
        return false;
    }

    /// <summary>The only method of that name, or null: gone, or overloaded since.</summary>
    private static MethodInfo? Find(Type type, string name)
    {
        try
        {
            return Reflect.Method(type, name);
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }

    internal static bool Active => Enabled && ListenerCache.IsCaching;

    /// <summary>
    /// Whether filtering this hook is safe in this scope (what <see cref="ForCombat"/> and <see cref="ForRun"/> answer null
    /// for), without building its list: a mirror asks it before the game's own early exits, which come before any walk.
    /// </summary>
    internal static bool HookSafe(int hook) => (_safe ??= CurrentSafety())[hook];

    /// <summary>
    /// The method a hook's list stands for (null when unknown: then never safe). For a caller that uses the list to skip that
    /// call altogether rather than mirror it (SnapshotCardReuse): it must check it with no tolerance, as a skipped call skips
    /// even the void prefixes <see cref="FilteredHook.IsSafe"/> lets stand.
    /// </summary>
    internal static MethodInfo? HookMethodOf(int hook) => hook >= 0 && hook < _hooks.Length ? _hooks[hook].HookMethod : null;

    /// <summary>Filtered listeners for one hook, or null when filtering that hook isn't safe right now.</summary>
    private static List<AbstractModel>? Get(IReadOnlyList<AbstractModel> listeners, int hook)
    {
        _safe ??= CurrentSafety();
        if (_filtered == null || !SameSafety(_filteredWith, _filteredGates, _safe, _gatesNow))
        {
            // Built with another patch safety (a hook another mod patched since, or other gates on a damage default): all built again.
            _filtered = new ConditionalWeakTable<IReadOnlyList<AbstractModel>, List<AbstractModel>?[]>();
        }
        _filteredWith = _safe;
        _filteredGates = _gatesNow;
        if (!_filtered.TryGetValue(listeners, out List<AbstractModel>?[]? perHook))
        {
            perHook = FilterAll(listeners);
            _filtered.AddOrUpdate(listeners, perHook);
        }
        return perHook[hook];
    }

    /// <summary>Each hook's safety in this scope, and (as <see cref="_gatesNow"/>) the gates of the damage defaults.</summary>
    private static bool[] CurrentSafety()
    {
        bool[] safe = new bool[_hooks.Length];
        var gates = new Type[_hooks.Length][];
        for (int i = 0; i < _hooks.Length; i++)
        {
            safe[i] = _hooks[i].IsSafe(out gates[i]);
            if (!safe[i])
            {
                gates[i] = Type.EmptyTypes;
            }
        }
        _gatesNow = gates;
        return safe;
    }

    /// <summary>
    /// Per hook, this scope's gate types (set with <see cref="_safe"/>; empty but for a damage default other mods patch with
    /// type-gated patches): listeners of a type assignable to one are kept as overriders.
    /// </summary>
    [ThreadStatic] private static Type[][]? _gatesNow;

    /// <summary>The patch safety and gates the lists in <see cref="_filtered"/> were built with.</summary>
    [ThreadStatic] private static bool[]? _filteredWith;
    [ThreadStatic] private static Type[][]? _filteredGates;

    /// <summary>The same safety and the same gates per hook (the gate arrays compared by content: PatchSafety's answer is a new array at each recheck).</summary>
    private static bool SameSafety(bool[]? safeA, Type[][]? gatesA, bool[] safeB, Type[][]? gatesB)
    {
        if (safeA == null || gatesA == null || gatesB == null)
        {
            return false;
        }
        if ((!ReferenceEquals(safeA, safeB) && !safeA.SequenceEqual(safeB)) || gatesA.Length != gatesB.Length)
        {
            return false;
        }
        for (int i = 0; i < gatesA.Length; i++)
        {
            if (!ReferenceEquals(gatesA[i], gatesB[i]) && !gatesA[i].SequenceEqual(gatesB[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether this scope's gates of <paramref name="hook"/> keep listeners of <paramref name="type"/>.</summary>
    private static bool GateKeeps(int hook, Type type)
    {
        Type[] gates = _gatesNow is { } all && hook < all.Length ? all[hook] : Type.EmptyTypes;
        foreach (Type gate in gates)
        {
            if (gate.IsAssignableFrom(type))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The hooks whose gates keep <paramref name="type"/> (bit i: hook i), for <see cref="FilterAll"/>.</summary>
    private static int GateMask(Type type)
    {
        int mask = 0;
        if (_gatesNow is { } all)
        {
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Length > 0 && GateKeeps(i, type))
                {
                    mask |= 1 << i;
                }
            }
        }
        return mask;
    }

    private static List<AbstractModel>?[] FilterAll(IReadOnlyList<AbstractModel> listeners)
    {
        _safe ??= CurrentSafety();
#if BENCH
        ListsFiltered++;
#endif
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
                // Listeners come in runs of the same type (cards, enchantments...): skip most lookups. Gate types are kept as
                // overriders (the damage defaults' type-gated patches: their calls reach the patched default).
                mask = OverrideMask(type) | GateMask(type);
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
#if BENCH
        for (int i = 0; i < perHook.Length; i++)
        {
            ListenersDropped += perHook[i] == null ? 0 : listeners.Count - perHook[i]!.Count;
        }
#endif
        return perHook;
    }

    /// <summary>Per hook, the pile entries whose type overrides its method (null until FastListeners is used).</summary>
    private static FastListeners.PileFilter[]? _pileFilters;

    /// <summary>Per (state, child) the filtered lists built by <see cref="Walked"/>, for one listener/pile version, dropped at scope end.</summary>
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, long PileVersion, List<AbstractModel>?[] PerHook)>? _walked;

    /// <summary>The size of each hook's last walked list (capacity of the next one).</summary>
    [ThreadStatic] private static int[]? _lastWalked;

    /// <summary>
    /// The combat listeners of one hook in walk order, or null when filtering it isn't safe. Built from FastListeners'
    /// filtered walk (listeners outside piles + the pile entries overriding the hook, cached per pile version) instead
    /// of materializing every listener: same objects, same order, then the same override mask.
    /// </summary>
    internal static List<AbstractModel>? ForCombat(CombatState state, int hook) =>
        Count(CanWalk
            ? Cached((state, null), hook) ?? Walked((state, null), hook, filter => FastListeners.Filtered(state, filter), () => ListenerCache.GetListeners(state))
            : Get(ListenerCache.GetListeners(state), hook));

    /// <summary>The run listeners (deck + child combat) of one hook, as <see cref="ForCombat"/>.</summary>
    internal static List<AbstractModel>? ForRun(RunState run, ICombatState? child, int hook) =>
        Count(CanWalk && (child == null || child is CombatState)
            ? Cached((run, child), hook) ?? Walked((run, child), hook, filter => FastListeners.Filtered(run, child, filter), () => ListenerCache.GetRunListeners(run, child))
            : Get(ListenerCache.GetRunListeners(run, child), hook));

    /// <summary>A filtered list handed out (counted in test builds: bdperf checksum's filter row must see it used).</summary>
    private static List<AbstractModel>? Count(List<AbstractModel>? list)
    {
#if BENCH
        if (list != null)
        {
            Served++;
        }
#endif
        return list;
    }

    /// <summary>
    /// The list <see cref="Walked"/> built for this key and hook, while still current; null otherwise (then
    /// <see cref="Walked"/> decides). Checked first so a cache hit allocates nothing (the walk lambdas capture state).
    /// </summary>
    private static List<AbstractModel>? Cached((object, object?) key, int hook) =>
        _safe != null && _safe[hook] && _walked != null && _walked.TryGetValue(key, out var entry)
        && entry.Version == ListenerCache.Version && entry.PileVersion == FastListeners.EntriesVersion
            ? entry.PerHook[hook]
            : null;

    private static bool CanWalk
    {
        get
        {
            // FastListeners applies after this patch: its filters are made on first use.
            if (!FastListeners.Applied || !FastListeners.Enabled)
            {
                return false;
            }
            // No filter for a hook that isn't safe: Walked never reaches it.
            _pileFilters ??= _hooks.Select(h => h.BaseMethod == null ? null! : FastListeners.PileFilter.Overriding(false, h.BaseMethod.Name)).ToArray();
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
        if (!_walked.TryGetValue(key, out var entry) || entry.Version != ListenerCache.Version || entry.PileVersion != FastListeners.EntriesVersion)
        {
            entry = (ListenerCache.Version, FastListeners.EntriesVersion, new List<AbstractModel>?[_hooks.Length]);
            _walked[key] = entry;
        }
        List<AbstractModel>? list = entry.PerHook[hook];
        if (list == null)
        {
            // Sized as last time: thousands of entries grown by doubling were a copy each time the version changed.
            _lastWalked ??= new int[_hooks.Length];
            list = new List<AbstractModel>(_lastWalked[hook]);
            int bit = 1 << hook;
            Type? lastType = null;
            bool lastKept = false;
            // A damage default with type-gated patches: the pile filter keeps the gate types too (one filter per gate set).
            FastListeners.PileFilter pileFilter = _gatesNow is { } gatesNow && gatesNow[hook].Length > 0
                ? _pileFilters![hook].WithGates(gatesNow[hook]) : _pileFilters![hook];
            foreach (AbstractModel listener in walk(pileFilter))
            {
                // Listeners come in runs of one type (a pile's copies of a card): one lookup per run.
                Type type = listener.GetType();
                if (!ReferenceEquals(type, lastType))
                {
                    lastType = type;
                    lastKept = (OverrideMask(type) & bit) != 0 || GateKeeps(hook, type);
                }
                if (lastKept)
                {
                    list.Add(listener);
                }
#if BENCH
                else
                {
                    ListenersDropped++;
                }
#endif
            }
            entry.PerHook[hook] = list;
            _lastWalked[hook] = list.Count;
#if BENCH
            ListsFiltered++;
            bool wrongWalk = ExtraScopes.BenchChecks && list.Count > 0 && TestWrong.Take("hookfilter.walk");
            if ((ExtraScopes.BenchChecks && ++WalkChecks % 32 == 0) || wrongWalk)
            {
                List<AbstractModel>? reference = Get(materialized(), hook);
                // Test: the walked list compared with its last entry missing (the check must see it; the walk's list is kept as is).
                IEnumerable<AbstractModel> walked = wrongWalk ? list.Take(list.Count - 1) : list;
                if (reference == null || !reference.SequenceEqual(walked, ReferenceEqualityComparer.Instance))
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
    /// <summary>Answers each mirror gave from the filtered listeners (test builds: per-mirror effects, <c>HookFilter.&lt;Mirror&gt;</c>).</summary>
    internal const int MirrorDamage = 0, MirrorDamageLate = 1, MirrorEnergyCost = 2, MirrorKeywords = 3, MirrorShouldPlay = 4, MirrorHitting = 5,
        MirrorEnergyCostColor = 6, MirrorStarCostColor = 7;
    internal static readonly string[] MirrorNames = { "Damage", "DamageLate", "EnergyCost", "Keywords", "ShouldPlay", "Hitting", "EnergyCostColor", "StarCostColor" };
    internal static readonly long[] MirrorAnswers = new long[8];

    public static long WalkChecks { get; private set; }
    public static long WalkMismatches { get; private set; }

    /// <summary>Test builds: this scope's gate types of one hook (empty outside a scope or without gates).</summary>
    internal static Type[] ScopeGates(int hook) => _gatesNow is { } all && hook < all.Length ? all[hook] : Type.EmptyTypes;
#endif

    internal static bool CombatHookListenersEmpty =>
        CombatManager.Instance.IsOverOrEnding && !CombatManager.Instance.IsStarting;

    internal static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }

    /// <summary>
    /// The listener exception a mirror rethrows from its prefix, until its hook's <see cref="RethrowFinalizer"/> runs: the mark
    /// PatchGuard's guard reads (<see cref="IsPassedOn"/>), its stack trace holding the mirror's frame, which isn't this mod's
    /// fault. Between the two only the hook's finalizers run (other mods' finalizers back off: BeforeReplace), so no other
    /// mirrored hook runs there. Not cleared at a mirror's entry: a nested mirror would erase an outer pending mark.
    /// </summary>
    [ThreadStatic] private static Exception? _passOn;

    /// <summary>
    /// A mirror's catch: true when <paramref name="e"/> is a listener's own exception (marked, then rethrown by the prefix:
    /// <c>throw;</c>, so Harmony runs no postfix and the hook's finalizers pass it on); false when this mod's code threw it
    /// (HookFilter off, the prefix returns true: the game's hook runs).
    /// </summary>
    internal static bool PassOn(Exception e, Type mirror)
    {
        if (!ThrownByListener(e, mirror))
        {
            Disable(e);
            return false;
        }
        _passOn = e;
        return true;
    }

    /// <summary>
    /// The exception a mirror is passing on from its listener (PatchGuard's guard leaves it alone: the game's loop throws it at
    /// the same point, and the mirror's frame in its trace is no fault of this mod). Read only when a guard sees an exception.
    /// </summary>
    internal static bool IsPassedOn(Exception e) => ReferenceEquals(e, _passOn);

    /// <summary>
    /// Whether no code of this mod lies between where <paramref name="e"/> was thrown and the mirror that caught it: the
    /// first frame of this assembly is the mirror's class (its listener calls, decimal arithmetic in the BCL: the game's
    /// loop makes the same calls in the same order and throws the same), and not where it was thrown (a bug of the mirror
    /// itself). A listener method the JIT inlined into the mirror counts as the mirror's: game's hook then, as before.
    /// </summary>
    private static bool ThrownByListener(Exception e, Type mirror)
    {
        try
        {
            System.Diagnostics.StackFrame[] frames = new System.Diagnostics.StackTrace(e).GetFrames();
            for (int i = 0; i < frames.Length; i++)
            {
                Type? type = frames[i].GetMethod()?.DeclaringType;
                if (type != null && type.Assembly == typeof(HookFilter).Assembly)
                {
                    return i > 0 && type == mirror;
                }
            }
        }
        catch
        {
            // Can't tell: this mod's, the game's hook runs.
        }
        return false;
    }

    /// <summary>
    /// Last finalizer of each mirrored hook (<see cref="Priority.Last"/>: after PatchGuard's guard, which leaves a listener
    /// exception a mirror rethrew alone, <see cref="IsPassedOn"/>): clears that mark and returns the exception. Harmony throws a
    /// returned exception with its stack trace restarting at the hook (the mirror's frame gone, so no guard further up blames
    /// this patch either). A method with a finalizer returning an exception does that for every exception, so the game's own
    /// exceptions passing through these hooks lose their trace too, whether the mirror ran or not (outside scopes, HookFilter
    /// or the mod off: the finalizer stays for the session): both are logged here with their full trace first, the only place
    /// it is kept (one warning per exception).
    /// </summary>
    internal static Exception? RethrowFinalizer(Exception? __exception)
    {
        // Runs at every call of these hot hooks: a null test, no __originalMethod (a method lookup per call).
        if (__exception != null)
        {
            _passOn = null;
            Log.Warn($"[{ModEntry.Id}] {Name}: a mirrored hook threw (passed on to its caller as the game would): {__exception}");
        }
        return __exception;
    }

    internal static HarmonyMethod Finalizer => new(typeof(HookFilter), nameof(RethrowFinalizer)) { priority = Priority.Last };

    /// <summary>The mirror prefixes patched (and <see cref="RethrowFinalizer"/>): HookSkip's TranspiledOnly check lists them as this mod's.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeMethodHandle, byte> _mirrorPatches = new();

    private static void RegisterMirror(HarmonyMethod prefix)
    {
        if (prefix.method != null)
        {
            _mirrorPatches[prefix.method.MethodHandle] = 0;
        }
    }

    /// <summary>
    /// One of HookFilter's mirror prefixes or its rethrowing finalizer. Each mirror checks its hook with
    /// <see cref="PatchTolerance.BeforeReplace"/> once per ListenerCache scope (<see cref="FilteredHook.IsSafe"/>, cached in the
    /// thread-static <c>_safe</c> until the scope ends) and runs the game's hook when another mod has anything there that
    /// tolerance refuses. HookSkip's TranspiledOnly doesn't depend on when that check runs: a mirror skipping the hook
    /// skips the transpiled body with it (HookSkip changes nothing on that call), and a patch added mid-scope is HookFilter's
    /// own concern with or without HookSkip.
    /// </summary>
    internal static bool IsMirrorPatch(MethodInfo method) =>
        _mirrorPatches.ContainsKey(method.MethodHandle) || (method.DeclaringType == typeof(HookFilter) && method.Name == nameof(RethrowFinalizer));
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
    /// <summary>Game v0.113: <c>ModifyDamageMultiplicativeLate</c>.</summary>
    private static Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>? _multiplicativeLatePlay;

    internal static bool HasLate => _multiplicativeLatePlay != null;

    /// <summary>Makes the delegates (virtual calls, like the hook's); true when this game's damage methods take a CardPlay.</summary>
    internal static bool Init()
    {
        Type[] value = { typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel) };
        Type[] cap = { typeof(Creature), typeof(ValueProp), typeof(Creature), typeof(CardModel) };
        MethodInfo? additive = Reflect.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageAdditive), value.Append(typeof(CardPlay)).ToArray());
        if (additive != null)
        {
            _additivePlay = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(additive);
            _multiplicativePlay = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(
                Reflect.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageMultiplicative), value.Append(typeof(CardPlay)).ToArray()));
            _capPlay = Make<Func<AbstractModel, Creature?, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(
                Reflect.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageCap), cap.Append(typeof(CardPlay)).ToArray()));
            if (Reflect.Method(typeof(AbstractModel), "ModifyDamageMultiplicativeLate", value.Append(typeof(CardPlay)).ToArray()) is { } late)
            {
                _multiplicativeLatePlay = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, CardPlay?, decimal>>(late);
            }
            return true;
        }
        _additive = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(
            Reflect.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageAdditive), value));
        _multiplicative = Make<Func<AbstractModel, Creature?, decimal, ValueProp, Creature?, CardModel?, decimal>>(
            Reflect.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageMultiplicative), value));
        _cap = Make<Func<AbstractModel, Creature?, ValueProp, Creature?, CardModel?, decimal>>(
            Reflect.Method(typeof(AbstractModel), nameof(AbstractModel.ModifyDamageCap), cap));
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

    /// <summary>Game v0.113 (beta): v0.111's hook with the late multiplicative loop.</summary>
    internal static bool PrefixWithLate(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
        decimal damage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, ModifyDamageHookType modifyDamageHookType,
        ref List<AbstractModel> modifiers, ref decimal __result) =>
        Mirror(runState, combatState, target, dealer, damage, props, cardSource, cardPlay, modifyDamageHookType, ref modifiers, ref __result, late: true);

    private static decimal Additive(AbstractModel item, Creature? target, decimal num, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) =>
        _additivePlay != null ? _additivePlay(item, target, num, props, dealer, cardSource, cardPlay) : _additive!(item, target, num, props, dealer, cardSource);

    private static decimal Multiplicative(AbstractModel item, Creature? target, decimal num, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) =>
        _multiplicativePlay != null ? _multiplicativePlay(item, target, num, props, dealer, cardSource, cardPlay) : _multiplicative!(item, target, num, props, dealer, cardSource);

    private static decimal Cap(AbstractModel item, Creature? target, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay) =>
        _capPlay != null ? _capPlay(item, target, props, dealer, cardSource, cardPlay) : _cap!(item, target, props, dealer, cardSource);

    /// <summary>Mirrors <c>Hook.ModifyDamageInternal</c> over the filtered run listeners.</summary>
    private static bool Mirror(IRunState runState, ICombatState? combatState, Creature? target, Creature? dealer,
        decimal damage, ValueProp props, CardModel? cardSource, CardPlay? cardPlay, ModifyDamageHookType modifyDamageHookType,
        ref List<AbstractModel> modifiers, ref decimal __result, bool late = false)
    {
        if (!HookFilter.Active || runState is not RunState run)
        {
            return true;
        }
        List<AbstractModel>? additive, multiplicative, cap, multiplicativeLate;
        try
        {
            // This mod's code only (an exception here is never the game's, however the JIT inlines it).
            additive = HookFilter.ForRun(run, combatState, HookFilter.DamageAdditive);
            multiplicative = HookFilter.ForRun(run, combatState, HookFilter.DamageMultiplicative);
            cap = HookFilter.ForRun(run, combatState, HookFilter.DamageCap);
            multiplicativeLate = late ? HookFilter.ForRun(run, combatState, HookFilter.DamageMultiplicativeLate) : null;
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (additive == null || multiplicative == null || cap == null || (late && multiplicativeLate == null))
        {
            return true;
        }
        try
        {
            // The game's loops over the kept listeners.
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
                if (multiplicativeLate != null)
                {
                    foreach (AbstractModel item in multiplicativeLate)
                    {
                        decimal factor = _multiplicativeLatePlay!(item, target, num, props, dealer, cardSource, cardPlay);
                        num *= factor;
                        if (factor != 1m)
                        {
                            list.Add(item);
                        }
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
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorDamage]++;
            if (multiplicativeLate != null)
            {
                HookFilter.MirrorAnswers[HookFilter.MirrorDamageLate]++; // v0.113's ModifyDamageMultiplicativeLate (PrefixWithLate).
            }
#endif
            __result = num;
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterDamage)))
            {
                throw;
            }
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
        List<AbstractModel>? early, late;
        try
        {
            // This mod's code, and the two combat getters the game reads first (one throwing: HookFilter off, the game's
            // hook then reads them and throws the same).
            if (!HookFilter.HookSafe(HookFilter.EnergyCost) || !HookFilter.HookSafe(HookFilter.EnergyCostLate))
            {
                return true;
            }
            // Vanilla: negative costs return before any walk; IterateCombatHookListeners walks nothing while combat is
            // ending. No list built for them either.
            if (originalCost < 0m || HookFilter.CombatHookListenersEmpty)
            {
#if BENCH
                HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCost]++;
#endif
                __result = originalCost;
                return false;
            }
            early = HookFilter.ForCombat(state, HookFilter.EnergyCost);
            late = HookFilter.ForCombat(state, HookFilter.EnergyCostLate);
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (early == null || late == null)
        {
            return true;
        }
        try
        {
            decimal modifiedCost = originalCost;
            foreach (AbstractModel item in early)
            {
                item.TryModifyEnergyCostInCombat(card, modifiedCost, out modifiedCost);
            }
            foreach (AbstractModel item in late)
            {
                item.TryModifyEnergyCostInCombatLate(card, modifiedCost, out modifiedCost);
            }
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCost]++;
#endif
            __result = modifiedCost;
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterEnergyCost)))
            {
                throw;
            }
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
        List<AbstractModel>? listeners;
        try
        {
            // This mod's code only (an exception here is never the game's, however the JIT inlines it).
            listeners = HookFilter.ForCombat(state, HookFilter.Keywords);
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (listeners == null)
        {
            return true;
        }
        try
        {
            // Vanilla iterates combatState.IterateHookListeners() directly here (no "combat ending" check).
            foreach (AbstractModel item in listeners)
            {
                item.TryModifyKeywordsInCombat(card, keywords);
            }
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorKeywords]++;
#endif
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterKeywords)))
            {
                throw;
            }
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
        List<AbstractModel>? listeners;
        try
        {
            // This mod's code, and the combat getters the game's walk reads first (one throwing: HookFilter off, the
            // game's hook then reads them and throws the same).
            if (!HookFilter.HookSafe(HookFilter.ShouldPlay))
            {
                return true;
            }
            // Vanilla's walk (IterateCombatHookListeners) yields nothing while combat is ending: no list built for it.
            if (HookFilter.CombatHookListenersEmpty)
            {
                preventer = null;
#if BENCH
                HookFilter.MirrorAnswers[HookFilter.MirrorShouldPlay]++;
#endif
                __result = true;
                return false;
            }
            listeners = HookFilter.ForCombat(state, HookFilter.ShouldPlay);
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (listeners == null)
        {
            return true;
        }
        try
        {
            foreach (AbstractModel item in listeners)
            {
                if (!item.ShouldPlay(card, autoPlayType))
                {
                    preventer = item;
#if BENCH
                    HookFilter.MirrorAnswers[HookFilter.MirrorShouldPlay]++;
#endif
                    __result = false;
                    return false;
                }
            }
            preventer = null;
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorShouldPlay]++;
#endif
            __result = true;
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterShouldPlay)))
            {
                throw;
            }
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
        List<AbstractModel>? listeners;
        try
        {
            // This mod's code, and the combat getters the game's walk reads first (one throwing: HookFilter off, the
            // game's hook then reads them and throws the same).
            if (!HookFilter.HookSafe(HookFilter.AllowHitting))
            {
                return true;
            }
            // Vanilla goes through IterateCombatHookListeners, which yields nothing while combat is ending: no list built.
            if (HookFilter.CombatHookListenersEmpty)
            {
#if BENCH
                HookFilter.MirrorAnswers[HookFilter.MirrorHitting]++;
#endif
                __result = true;
                return false;
            }
            listeners = HookFilter.ForCombat(state, HookFilter.AllowHitting);
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (listeners == null)
        {
            return true;
        }
        try
        {
            bool allowed = true;
            foreach (AbstractModel item in listeners)
            {
                if (!item.ShouldAllowHitting(creature))
                {
                    allowed = false;
                    break;
                }
            }
#if BENCH
            bool wrongHitting = ExtraScopes.BenchChecks && TestWrong.Take("hookfilter.hitting");
            if ((ExtraScopes.BenchChecks && ++HookFilter.HittingChecks % 64 == 0) || wrongHitting)
            {
                bool vanilla = ListenerCache.RunVanilla(() => Hook.ShouldAllowHitting(combatState, creature));
                // Test: compared with the opposite answer (the check must see it; the filtered answer is returned as is).
                if (vanilla != (wrongHitting ? !allowed : allowed))
                {
                    HookFilter.HittingMismatches++;
                    Log.Error($"[{ModEntry.Id}] {HookFilter.Name}: ShouldAllowHitting({creature}) filtered {allowed}, vanilla {vanilla}");
                }
            }
#endif
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorHitting]++;
#endif
            __result = allowed;
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterHitting)))
            {
                throw;
            }
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
        // Mirror of the whole helper, skipping it: what runs around it as around the helper is let stand (BeforeReplace, as the
        // mirrored hooks).
        if (!HookFilter.Active || state is not CombatState combat || PatchSafety.IsPatchedByOthers(EnergyCostColor, PatchTolerance.BeforeReplace))
        {
            return true;
        }
        List<AbstractModel>? early, late;
        try
        {
            // This mod's code only (an exception here is never the game's, however the JIT inlines it).
            early = HookFilter.ForCombat(combat, HookFilter.EnergyCost);
            late = HookFilter.ForCombat(combat, HookFilter.EnergyCostLate);
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (early == null || late == null)
        {
            return true;
        }
        try
        {
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
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCostColor]++;
#endif
            __result = modified;
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterCostColors)))
            {
                throw;
            }
            return true;
        }
    }

    /// <summary>Mirrors <c>CardCostHelper.TryModifyStarCostWithHooks</c> (star cost color) over the filtered listeners.</summary>
    internal static bool StarCostColorPrefix(CardModel card, ICombatState state, ref decimal hookModifiedCost, ref bool __result)
    {
        // As the energy cost color helper.
        if (!HookFilter.Active || state is not CombatState combat || PatchSafety.IsPatchedByOthers(StarCostColor, PatchTolerance.BeforeReplace))
        {
            return true;
        }
        List<AbstractModel>? listeners;
        try
        {
            // This mod's code only (an exception here is never the game's, however the JIT inlines it).
            listeners = HookFilter.ForCombat(combat, HookFilter.StarCost);
        }
        catch (Exception e)
        {
            HookFilter.Disable(e);
            return true;
        }
        if (listeners == null)
        {
            return true;
        }
        try
        {
            hookModifiedCost = card.BaseStarCost;
            bool modified = false;
            foreach (AbstractModel item in listeners)
            {
                modified |= item.TryModifyStarCost(card, hookModifiedCost, out hookModifiedCost);
            }
#if BENCH
            CheckCostColor(StarCostColor!, card, state, modified, hookModifiedCost);
#endif
#if BENCH
            HookFilter.MirrorAnswers[HookFilter.MirrorStarCostColor]++;
#endif
            __result = modified;
            return false;
        }
        catch (Exception e)
        {
            // A listener's own exception goes on to the caller from here, as the game's loop throws it (Harmony then runs no
            // postfix, as the game runs none); this mod's: the game's hook.
            if (HookFilter.PassOn(e, typeof(HookFilterCostColors)))
            {
                throw;
            }
            return true;
        }
    }

#if BENCH
    /// <summary>Debug, with bench checks on: 1 call in 64 compared with the game's own helper run without caches.</summary>
    private static void CheckCostColor(MethodInfo helper, CardModel card, ICombatState state, bool modified, decimal cost)
    {
        bool wrongCost = ExtraScopes.BenchChecks && TestWrong.Take("hookfilter.cost");
        if (!wrongCost && (!ExtraScopes.BenchChecks || ++HookFilter.CostColorChecks % 64 != 0))
        {
            return;
        }
        if (wrongCost)
        {
            // Test: compared with a cost one higher (the check must see it; the filtered answer is returned as is).
            cost++;
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
