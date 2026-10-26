using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using static BigDeckOptimization.Patches.FastListeners;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Every game hook (card played, damage received, card moved, HP lost, "should this happen"...) walks every listener:
/// with 8,400 cards, 16,000-31,000 entries per walk, dozens of walks per action, almost all cards and enchantments
/// whose type doesn't react to that hook. For those the hook calls the <see cref="AbstractModel"/> default, which does
/// nothing: a completed task, the value passed in, true for "should", false for "try", 0 to add, 1 to multiply.
///
/// <see cref="Hooks"/> lists the hooks whose loops do nothing else with a listener (verified on the game code by
/// tools/hookskip: calls with those defaults, PushModel/PopModel, ExecutionFinished, "changed" bookkeeping). In each
/// of them, and only while its code has the fingerprint checked, the walk call is redirected to
/// <see cref="FastListeners.Filtered"/>: the same walk minus the pile entries (cards, their enchantment/affliction,
/// deck cards) whose type overrides none of the methods that hook calls; listeners someone waits on (ExecutionFinished
/// subscribers) are kept where the hook signals them. Everything else in the hook is the game's own code.
///
/// Per hook: a changed hook (fingerprint), or a hook or base method another mod patches, stays vanilla; other hooks
/// keep the optimization. Per listener type: a type that overrides a hook's method is always walked for that hook, so
/// a card or enchantment that starts reacting to something (game or mod update) is handled with no change here.
/// Inside <see cref="ListenerCache"/> scopes too (same state, no full materialization). BaseLib's hand size and heal hooks call listeners
/// by interface: their walk keeps only the implementers. Verified by full combat checksums (sessions on/off match).
/// </summary>
public static partial class HookSkip
{
    public const string Name = "HookSkip";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    /// <summary>Hooks redirected, and those left vanilla (changed code), for the status line.</summary>
    public static int Active { get; private set; }
    public static int LeftVanilla { get; private set; }

    private sealed class Entry
    {
        public readonly string Hook;
        public readonly string Fingerprint;
        public readonly bool Subscribed;
        public readonly string[] Methods;
        public MethodInfo? Method;
        public PileFilter? Filter;

        public Entry(string hook, string fingerprint, bool subscribed, params string[] methods)
        {
            Hook = hook;
            Fingerprint = fingerprint;
            Subscribed = subscribed;
            Methods = methods;
        }
    }

    private static MethodInfo? _runWalk, _combatWalk, _combatHookWalk;
    private static Func<ICombatState, IEnumerable<AbstractModel>>? _vanillaCombatHookWalk;
    private static MethodInfo? _redirectRun, _redirectCombat, _redirectCombatHook;
    private static readonly Dictionary<MethodBase, int> _indexByBody = new();

    public static void Apply(Harmony harmony)
    {
        if (!FastListeners.Applied)
        {
            throw new PatchSkippedException("needs FastListeners (not applied)");
        }
        _runWalk = AccessTools.Method(typeof(IRunState), nameof(IRunState.IterateHookListeners)) ?? throw new MissingMethodException("IRunState.IterateHookListeners");
        _combatWalk = AccessTools.Method(typeof(ICombatState), nameof(ICombatState.IterateHookListeners)) ?? throw new MissingMethodException("ICombatState.IterateHookListeners");
        _combatHookWalk = AccessTools.Method(typeof(Hook), "IterateCombatHookListeners") ?? throw new MissingMethodException("Hook.IterateCombatHookListeners");
        _vanillaCombatHookWalk = AccessTools.MethodDelegate<Func<ICombatState, IEnumerable<AbstractModel>>>(_combatHookWalk);
        _redirectRun = AccessTools.Method(typeof(HookSkip), nameof(RunWalk));
        _redirectCombat = AccessTools.Method(typeof(HookSkip), nameof(CombatWalk));
        _redirectCombatHook = AccessTools.Method(typeof(HookSkip), nameof(CombatHookWalk));
        PatchSafety.RequireFingerprint(AccessTools.EnumeratorMoveNext(_combatHookWalk), CombatHookWalkFingerprint);

        for (int i = 0; i < Hooks.Length; i++)
        {
            Entry entry = Hooks[i];
            try
            {
                MethodInfo hook = AccessTools.Method(typeof(Hook), entry.Hook) ?? throw new MissingMethodException(nameof(Hook), entry.Hook);
                // Async hooks run their code (and their walks) in the state machine's MoveNext.
                MethodBase body = hook.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null
                    ? AccessTools.AsyncMoveNext(hook)
                    : hook;
                string actual = MethodFingerprint.Of(body);
                if (!PatchSafety.FingerprintMatches(actual, entry.Fingerprint))
                {
                    LeftVanilla++;
                    Log.Info($"[{ModEntry.Id}] {Name}: {entry.Hook} changed (fingerprint {actual}, expected {entry.Fingerprint}): left vanilla");
                    continue;
                }
                entry.Method = hook;
                entry.Filter = PileFilter.Overriding(entry.Subscribed, entry.Methods);
                _indexByBody[body] = i;
                harmony.Patch(body, transpiler: new HarmonyMethod(typeof(HookSkip), nameof(Transpiler)));
                Active++;
            }
            catch (Exception e)
            {
                LeftVanilla++;
                Log.Info($"[{ModEntry.Id}] {Name}: {entry.Hook} left vanilla: {e.Message}");
            }
        }
        Applied = Active > 0;
        Log.Info($"[{ModEntry.Id}] {Name}: {Active} hooks filtered, {LeftVanilla} left vanilla");
        BaseLibHooks.Init(harmony);
        if (!Applied)
        {
            throw new PatchSkippedException("no hook matched");
        }
    }

    /// <summary>Redirects the walk calls of one hook (its index is pushed as an extra argument).</summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int index = _indexByBody[__originalMethod];
        foreach (CodeInstruction code in instructions)
        {
            MethodInfo? redirect = code.Calls(_runWalk!) ? _redirectRun
                : code.Calls(_combatWalk!) ? _redirectCombat
                : code.Calls(_combatHookWalk!) ? _redirectCombatHook
                : null;
            if (redirect == null)
            {
                yield return code;
                continue;
            }
            // Same stack in, same IEnumerable out: (state[, combat]) -> (state[, combat], index) -> call redirect.
            yield return new CodeInstruction(OpCodes.Ldc_I4, index).WithLabels(code.ExtractLabels()).WithBlocks(code.ExtractBlocks());
            yield return new CodeInstruction(OpCodes.Call, redirect);
        }
    }

    /// <summary>
    /// The filter of a hook when it may run filtered right now, else null (vanilla walk). Inside
    /// <see cref="ListenerCache"/> scopes too: the walk reads the same unchanged state, without materializing every listener.
    /// </summary>
    private static PileFilter? FilterFor(int index)
    {
        Entry entry = Hooks[index];
        // RunVanilla (snapshot verifier, bench comparisons) is the vanilla reference: no filtering there.
        if (!Enabled || ListenerCache.IsSuspended || PatchSafety.IsPatchedByOthers(entry.Method))
        {
            return null;
        }
        foreach (MethodInfo baseMethod in entry.Filter!.BaseMethods!)
        {
            if (PatchSafety.IsPatchedByOthers(baseMethod))
            {
                return null;
            }
        }
        return entry.Filter;
    }

    /// <summary>Replaces <c>runState.IterateHookListeners(combatState)</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> RunWalk(IRunState runState, ICombatState? combatState, int index)
    {
        PileFilter? filter = FilterFor(index);
        return filter != null && runState is RunState run && (combatState == null || combatState is CombatState)
            ? Filtered(run, combatState, filter)
            : runState.IterateHookListeners(combatState);
    }

    /// <summary>Replaces <c>combatState.IterateHookListeners()</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> CombatWalk(ICombatState combatState, int index)
    {
        PileFilter? filter = FilterFor(index);
        return filter != null && combatState is CombatState combat
            ? Filtered(combat, filter)
            : combatState.IterateHookListeners();
    }

    /// <summary>v0.107.1 <c>Hook.IterateCombatHookListeners</c> (nothing while combat is ending), mirrored below.</summary>
    private const string CombatHookWalkFingerprint = "3BECCBC0C9F3A25A";

    /// <summary>Replaces <c>Hook.IterateCombatHookListeners(combatState)</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> CombatHookWalk(ICombatState combatState, int index)
    {
        PileFilter? filter = FilterFor(index);
        if (filter == null || combatState is not CombatState combat)
        {
            return _vanillaCombatHookWalk!(combatState);
        }
        return CombatHookFiltered(combat, filter);
    }

    private static IEnumerable<AbstractModel> CombatHookFiltered(CombatState combat, PileFilter filter)
    {
        // Lazy like the original: the "combat ending" check runs on the first MoveNext.
        if (CombatManager.Instance.IsOverOrEnding && !CombatManager.Instance.IsStarting)
        {
            yield break;
        }
        foreach (AbstractModel item in Filtered(combat, filter))
        {
            yield return item;
        }
    }

    /// <summary>
    /// BaseLib's hand size and heal hooks (BaseLib is optional and may load after this mod): they loop over every
    /// listener and use only those implementing their interface, so the walk they ask for keeps only the implementers.
    /// </summary>
    private static class BaseLibHooks
    {
        /// <summary>BaseLib 3.4.7 methods (GetMaxHandSize: the (Player, int) overload, which holds the loop).</summary>
        private static readonly (string Type, string Method, Type[]? Args, string Fingerprint, string Interface)[] Targets =
        {
            ("BaseLib.Patches.Hooks.MaxHandSizePatch", "GetMaxHandSize", new[] { typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(int) },
                "1EE490EF3F75AF02", "BaseLib.Hooks.IMaxHandSizeModifier"),
            ("BaseLib.Patches.Hooks.ModifyHealAmountPatches", "ModifyAdditive", null, "895AD94736FEED8E", "BaseLib.Hooks.IHealAmountModifier"),
            ("BaseLib.Patches.Hooks.ModifyHealAmountPatches", "ModifyMultiplicative", null, "98B29C743F42C15F", "BaseLib.Hooks.IHealAmountModifier"),
        };

        private static readonly Dictionary<MethodBase, PileFilter> _filters = new();
        private static Harmony? _harmony;

        public static void Init(Harmony harmony)
        {
            _harmony = harmony;
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BaseLib"))
            {
                PatchBaseLib();
                return;
            }
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name == "BaseLib")
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                PatchBaseLib();
            }
        }

        private static void PatchBaseLib()
        {
            foreach ((string typeName, string methodName, Type[]? args, string fingerprint, string interfaceName) in Targets)
            {
                string name = typeName + "." + methodName;
                try
                {
                    Type type = AccessTools.TypeByName(typeName) ?? throw new TypeLoadException(typeName);
                    MethodInfo method = AccessTools.Method(type, methodName, args) ?? throw new MissingMethodException(name);
                    PatchSafety.RequireFingerprint(method, fingerprint);
                    Type iface = AccessTools.TypeByName(interfaceName) ?? throw new TypeLoadException(interfaceName);
                    _filters[method] = new PileFilter(iface.IsAssignableFrom, keepSubscribed: false);
                    _harmony!.Patch(method, prefix: new HarmonyMethod(typeof(BaseLibHooks), nameof(Prefix)),
                        finalizer: new HarmonyMethod(typeof(BaseLibHooks), nameof(Finalizer)));
                    PatchGuard.Watch(method);
                    Log.Info($"[{ModEntry.Id}] {Name}: BaseLib {name} applied");
                }
                catch (Exception e)
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: BaseLib {name} left vanilla: {e.Message}");
                }
            }
        }

        /// <summary>The next RunState.IterateHookListeners call (this method's own walk) keeps only implementers.</summary>
        private static void Prefix(MethodBase __originalMethod)
        {
            if (Enabled && !ListenerCache.IsCaching && !ListenerCache.IsSuspended && !PatchSafety.IsPatchedByOthers(__originalMethod))
            {
                FastListeners.PendingRunFilter = _filters[__originalMethod];
            }
        }

        /// <summary>Never leaks to a later walk (e.g. when the run state wasn't the game's RunState).</summary>
        private static Exception? Finalizer(Exception? __exception)
        {
            FastListeners.PendingRunFilter = null;
            return __exception;
        }
    }
}
