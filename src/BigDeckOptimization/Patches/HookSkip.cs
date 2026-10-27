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
        /// <summary>Index in <see cref="HelperProfiles"/> of the version this entry was classified on, -1 when it uses no helper.</summary>
        public readonly int Profile;
        public readonly string[] Methods;
        /// <summary>Fingerprint of each method's AbstractModel default in that version (the no-op the classification relied on).</summary>
        public readonly string[] Defaults;
        public MethodInfo? Method;
        /// <summary>The code transpiled: the hook itself or its state machine's MoveNext (another mod may patch either).</summary>
        public MethodBase? Body;
        public PileFilter? Filter;

        public Entry(string hook, string fingerprint, bool subscribed, int profile, params string[] methods)
        {
            Hook = hook;
            Fingerprint = fingerprint;
            Subscribed = subscribed;
            Profile = profile;
            Methods = methods.Select(m => m[..m.IndexOf('=')]).ToArray();
            Defaults = methods.Select(m => m[(m.IndexOf('=') + 1)..]).ToArray();
        }
    }

    /// <summary>
    /// Why an entry doesn't hold in this game, or null when it does: the hook's code, a default it calls, or a helper of the
    /// version it was classified on changed.
    /// </summary>
    private static string? Mismatch(Entry entry, string actual)
    {
        if (!PatchSafety.FingerprintMatches(actual, entry.Fingerprint))
        {
            return $"code changed (fingerprint {actual})";
        }
        for (int i = 0; i < entry.Methods.Length; i++)
        {
            string found = DefaultFingerprint(entry.Methods[i]);
            if (found != entry.Defaults[i])
            {
                return $"AbstractModel.{entry.Methods[i]} default changed ({found})";
            }
        }
        if (entry.Profile >= 0 && !HelperProfileHolds(entry.Profile))
        {
            return "choice context helpers changed";
        }
        return null;
    }

    private static Dictionary<string, string>? _defaults;

    /// <summary>Fingerprint of the AbstractModel method of that name (load time only, cached).</summary>
    private static string DefaultFingerprint(string name)
    {
        if (_defaults == null)
        {
            _defaults = new Dictionary<string, string>();
            foreach (IGrouping<string, MethodInfo> methods in typeof(AbstractModel)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).GroupBy(m => m.Name))
            {
                MethodInfo first = methods.First();
                _defaults[methods.Key] = methods.Count() > 1 ? "overloaded" : first.GetMethodBody() == null ? "abstract" : MethodFingerprint.Of(first);
            }
        }
        return _defaults.TryGetValue(name, out string? fingerprint) ? fingerprint : "missing";
    }

    private static bool?[]? _profiles;

    /// <summary>Whether this game's helpers (choice contexts, ExecutionFinished) have the code of that classified version.</summary>
    private static bool HelperProfileHolds(int profile)
    {
        _profiles ??= new bool?[HelperProfiles.Length];
        return _profiles[profile] ??= HelperProfiles[profile].All(helper =>
        {
            MethodBase? method = MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, helper.Key);
            string actual = method?.GetMethodBody() == null ? "-" : MethodFingerprint.Of(method);
            if (actual != helper.Value)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: helper {helper.Key} is {actual}, profile {profile} expects {helper.Value}");
            }
            return actual == helper.Value;
        });
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

        // Entries of every classified game version: per hook, the first one whose code, defaults and helpers all match.
        int absent = 0;
        foreach (IGrouping<string, int> hookEntries in Enumerable.Range(0, Hooks.Length).GroupBy(i => Hooks[i].Hook))
        {
            string hookName = hookEntries.Key;
            try
            {
                MethodInfo? hook = AccessTools.Method(typeof(Hook), hookName);
                if (hook == null)
                {
                    absent++; // A hook of another game version.
                    continue;
                }
                // Async hooks run their code (and their walks) in the state machine's MoveNext.
                MethodBase body = hook.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null
                    ? AccessTools.AsyncMoveNext(hook)
                    : hook;
                string actual = MethodFingerprint.Of(body);
                int match = -1;
                string? why = null;
                foreach (int i in hookEntries)
                {
                    string? mismatch = Mismatch(Hooks[i], actual);
                    if (mismatch == null)
                    {
                        match = i;
                        break;
                    }
                    // Report the most telling reason: a version whose hook code matches but something it relies on changed.
                    if (why == null || !mismatch.StartsWith("code changed", StringComparison.Ordinal))
                    {
                        why = mismatch;
                    }
                }
                if (match < 0)
                {
                    LeftVanilla++;
                    Log.Info($"[{ModEntry.Id}] {Name}: {hookName} {why}: left vanilla");
                    continue;
                }
                Entry entry = Hooks[match];
                entry.Method = hook;
                entry.Body = body;
                entry.Filter = PileFilter.Overriding(entry.Subscribed, entry.Methods);
                _indexByBody[body] = match;
                _toPatch.Enqueue((hookName, body));
            }
            catch (Exception e)
            {
                LeftVanilla++;
                Log.Info($"[{ModEntry.Id}] {Name}: {hookName} left vanilla: {e.Message}");
            }
        }
        if (_toPatch.Count == 0)
        {
            throw new PatchSkippedException("no hook matched");
        }
        Applied = true;
        _absent = absent;
        BaseLibHooks.Init(harmony);
        PatchOverFrames(harmony);
    }

    /// <summary>Hooks checked at load, patched by <see cref="PatchOverFrames"/>.</summary>
    private static readonly Queue<(string Hook, MethodBase Body)> _toPatch = new();
    private static int _absent;
    private const int HooksPerFrame = 8;

    /// <summary>
    /// Transpiles the checked hooks a few per frame (~1.5 ms each: ~140 of them took ~200 ms of the game's start). Hooks only
    /// run in a run, seconds later; one not patched yet just runs vanilla. Main thread, like every patch of this mod.
    /// Falls back to patching them all now when frames can't be awaited.
    /// </summary>
    private static async void PatchOverFrames(Harmony harmony)
    {
        try
        {
            if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
            {
                PatchBatch(harmony, int.MaxValue);
                return;
            }
            while (_toPatch.Count > 0)
            {
                await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
                PatchBatch(harmony, HooksPerFrame);
            }
        }
        catch (Exception e)
        {
            // async void: nothing may escape. Hooks left unpatched just run vanilla.
            Log.Error($"[{ModEntry.Id}] {Name}: patching hooks stopped ({_toPatch.Count} left vanilla): {e}");
            LeftVanilla += _toPatch.Count;
            _toPatch.Clear();
        }
    }

    private static void PatchBatch(Harmony harmony, int count)
    {
        for (int n = 0; n < count && _toPatch.Count > 0; n++)
        {
            (string hookName, MethodBase body) = _toPatch.Dequeue();
            if (PatchSafety.IsFaulted(Name))
            {
                LeftVanilla += _toPatch.Count + 1;
                _toPatch.Clear();
                break;
            }
            try
            {
                PatchGuard.Patch(harmony, body, transpiler: new HarmonyMethod(typeof(HookSkip), nameof(Transpiler)));
                Active++;
            }
            catch (Exception e)
            {
                LeftVanilla++;
                Log.Info($"[{ModEntry.Id}] {Name}: {hookName} left vanilla: {e.Message}");
            }
        }
        if (_toPatch.Count == 0)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {Active} hooks filtered, {LeftVanilla} left vanilla ({_absent} classified hooks not in this game version)");
        }
    }

    /// <summary>Redirects the walk calls of one hook (its index is pushed as an extra argument).</summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        // Harmony runs this again whenever another mod patches the hook later: it must never throw there (that mod's
        // patch would fail). Unknown body: unchanged.
        if (!_indexByBody.TryGetValue(__originalMethod, out int index))
        {
            foreach (CodeInstruction code in instructions)
            {
                yield return code;
            }
            yield break;
        }
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
        if (!Enabled || ListenerCache.IsSuspended)
        {
            return null;
        }
        // Runs inside the hooks, most of them async: an exception here would only fault the hook's task (PatchGuard
        // never sees it), so it turns HookSkip off and this walk runs vanilla.
        try
        {
            if (PatchSafety.IsPatchedByOthers(entry.Method) || (entry.Body != entry.Method && PatchSafety.IsPatchedByOthers(entry.Body, AllowedBodyPatches())))
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
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
            return null;
        }
    }

    /// <summary>
    /// Other mods' patches on a hook's state machine that leave it filtered: they must not add listener walks to the code
    /// this transpiler redirects. BaseLib 3.4.7 makes AfterEnergyReset and AfterCardPlayed first await a method of its own
    /// (its resource reset, its pre-play hooks) through <c>AsyncMethodCall.Create</c>: no walk added. Transpilers and the
    /// injector fingerprinted.
    /// </summary>
    private static MethodInfo[] AllowedBodyPatches()
    {
        if (_allowedBodyPatches != null)
        {
            return _allowedBodyPatches;
        }
        var allowed = new List<MethodInfo>();
        try
        {
            MethodInfo? injector = AccessTools.Method("BaseLib.Utils.Patching.AsyncMethodCall:Create");
            if (injector != null && PatchSafety.FingerprintMatches(MethodFingerprint.Of(injector), BaseLibAsyncInjectorFingerprint))
            {
                foreach ((string name, string fingerprint) in BaseLibAwaitTranspilers)
                {
                    MethodInfo? transpiler = AccessTools.Method(name);
                    if (transpiler != null && PatchSafety.FingerprintMatches(MethodFingerprint.Of(transpiler), fingerprint))
                    {
                        allowed.Add(transpiler);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: BaseLib's hook transpilers not checked ({e.Message}): those hooks stay vanilla with BaseLib");
        }
        // BaseLib loads before any combat, when this first runs: no need to look again.
        return _allowedBodyPatches = allowed.ToArray();
    }

    private static MethodInfo[]? _allowedBodyPatches;
    private const string BaseLibAsyncInjectorFingerprint = "DF3A98C4301D8FE8";
    private static readonly (string Method, string Fingerprint)[] BaseLibAwaitTranspilers =
    {
        ("BaseLib.Abstracts.CustomResourcePatches:AfterPlay", "A55CDC6F853C8854"),
        ("BaseLib.Patches.Hooks.AfterCardPlayedPatch:AfterPlay", "B1ED9060E4BBD70B"),
    };

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
    private const string CombatHookWalkFingerprint = "B3B83A82B69E17FC";

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
                    PatchGuard.Patch(_harmony!, method, prefix: new HarmonyMethod(typeof(BaseLibHooks), nameof(Prefix)),
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
