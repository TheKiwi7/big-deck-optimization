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
/// Inside <see cref="ListenerCache"/> scopes too (same state, no full materialization). For hooks keeping no subscriber, the
/// non-pile listeners of every kind whose type overrides none of the hook's methods are skipped too
/// (<see cref="FastListeners.KindSkipEnabled"/>, under the same hook_skip option). BaseLib's hand size, heal and
/// downgrade hooks (<c>IAfterCardDowngraded.DowngradeHook</c>) call listeners by interface: their walk keeps only the
/// implementers. Verified by full combat checksums (sessions on/off match).
/// </summary>
public static partial class HookSkip
{
    public const string Name = "HookSkip";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    /// <summary>Hooks redirected, and those left vanilla (changed code), for the status line.</summary>
    public static int Active { get; private set; }
    public static int LeftVanilla { get; private set; }

    /// <summary>Hook lookups Harmony's AccessTools answered with another member than the runtime's (another mod intercepting them): ignored.</summary>
    public static int AnsweredByOthers { get; private set; }
#if BENCH
    /// <summary>Hook walks that ran filtered (the effects check).</summary>
    public static long FilteredWalks { get; private set; }

    /// <summary>Per hook: filters handed out with gates (another mod's type-gated patches on its defaults), for the foreign tests.</summary>
    private static long[] _gatedWalks = Array.Empty<long>();

    private static void CountGated(int index)
    {
        long[] counts = _gatedWalks;
        if (counts.Length != Hooks.Length)
        {
            counts = _gatedWalks = new long[Hooks.Length];
        }
        System.Threading.Interlocked.Increment(ref counts[index]);
    }

    /// <summary>Per hook: walks past the transpiled code's check while other mods' transpilers judged by their IL stood there (the foreign tests).</summary>
    private static long[] _judgedWalks = Array.Empty<long>();

    private static void CountJudged(int index)
    {
        long[] counts = _judgedWalks;
        if (counts.Length != Hooks.Length)
        {
            counts = _judgedWalks = new long[Hooks.Length];
        }
        System.Threading.Interlocked.Increment(ref counts[index]);
    }

    /// <summary>"Hook.Name N" per hook filtered with other mods' transpilers judged by their IL standing there.</summary>
    public static IReadOnlyList<string> JudgedWalkReport() =>
        Hooks.Select((entry, i) => (entry.Hook, Count: i < _judgedWalks.Length ? _judgedWalks[i] : 0)).Where(e => e.Count > 0)
            .Select(e => $"Hook.{e.Hook} {e.Count}").ToList();

    /// <summary>"Hook.Name N" per hook that ran with gates (test tooling: what only a runtime walk produces).</summary>
    public static IReadOnlyList<string> GatedWalkReport() =>
        Hooks.Select((entry, i) => (entry.Hook, Count: i < _gatedWalks.Length ? _gatedWalks[i] : 0)).Where(e => e.Count > 0)
            .Select(e => $"Hook.{e.Hook} {e.Count}").ToList();
    /// <summary>BaseLib walk filters a combat walk dropped before a RunState walk took them (BaseLib walking NullRunState): 0 in runs.</summary>
    public static long PendingFiltersDropped { get; private set; }
#endif

    private sealed class Entry
    {
        public readonly string Hook;
        public readonly string Fingerprint;
        /// <summary>
        /// The hook signals listeners' ExecutionFinished: pile entries with subscribers are kept. Judged when the walk's list
        /// is built (first MoveNext), where vanilla signals each listener at its turn: an entry keeping the defaults that
        /// gets its first subscriber while the walk runs, before its turn, isn't signalled by this walk. Accepted: the only
        /// subscriber (NPlayerHand's card selection) moves hand nodes back, UI only, and is consumed by the source's own
        /// later walks first in the flows read. Another mod's code subscribing during the walk joins that gap: its listeners'
        /// code (as before), and a postfix let stand on an async hook, which runs once the hook first awaits, the walk suspended
        /// (no mod read subscribes: RitsuLib and BaseLib only invoke ExecutionFinished).
        /// </summary>
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

        /// <summary>
        /// The gates of several of this hook's defaults together (<see cref="Inputs"/>: PatchSafety's arrays for them, in base
        /// order), kept while those answers (the same arrays) stay: a walk compares references, no allocation, and the filter
        /// gets the same array (its gated filter kept by reference). Read and replaced whole (a hook may run off the main thread).
        /// </summary>
        public sealed record Combined(Type[][] Inputs, Type[] Gates);

        public Combined? LastCombined;

        /// <summary>
        /// The patches let stand on the hook's transpiled code (BaseLib's allowed ones, plus other mods' transpilers the IL judge
        /// accepted) and the judged gates, with the Harmony epoch and capture they were worked out at (<see cref="BodyAllowed"/>).
        /// Read and replaced whole.
        /// </summary>
        public sealed record Judged(int Epoch, int Capture, long CheckedAt, MethodInfo[] Allowed, Type[] Gates, bool Accepted);

        public Judged? LastJudged;

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
    /// version it was classified on changed. Exact comparisons only, never PatchSafety's EquivalentFingerprints: those are
    /// reviewed for other patches (HookFilter, CardSortKeys pin some of the same hooks), while a changed hook is only safe
    /// once classify.py has classified its new code and given it a row of its own.
    /// </summary>
    private static string? Mismatch(Entry entry, string actual)
    {
        if (actual != entry.Fingerprint)
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

    /// <summary>Per profile, the helper methods present in this game: a dropped listener skips its calls to them.</summary>
    private static MethodBase[]?[]? _helperMethods;

    private static bool[]? _helpersPatched;
    private static long[]? _helpersCheckedAt;
    private static int[]? _helpersEpoch;

    /// <summary>Asked per walk: answered from a per-profile result rechecked each second.</summary>
    private static bool HelpersPatched(int profile)
    {
        _helpersPatched ??= new bool[HelperProfiles.Length];
        _helpersCheckedAt ??= Enumerable.Repeat(long.MinValue / 2, HelperProfiles.Length).ToArray();
        long now = Environment.TickCount64;
        // Also at once after any Patch or Unpatch (HarmonyWatch.Epoch).
        _helpersEpoch ??= new int[HelperProfiles.Length];
        if (now - _helpersCheckedAt[profile] >= 1000 || _helpersEpoch[profile] != HarmonyWatch.Epoch)
        {
            _helpersEpoch[profile] = HarmonyWatch.Epoch;
            _helpersPatched[profile] = AnyHelperPatched(profile);
            _helpersCheckedAt[profile] = now;
        }
        return _helpersPatched[profile];
    }

    private static bool AnyHelperPatched(int profile)
    {
        foreach (MethodBase helper in HelperMethods(profile))
        {
            // No tolerance: a dropped listener skips its calls to these helpers, so a patch on one would miss those calls.
            if (PatchSafety.IsPatchedByOthers(helper))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The helpers of a profile present in this game, resolved once (also for the back-off preflight).</summary>
    private static MethodBase[] HelperMethods(int profile)
    {
        _helperMethods ??= new MethodBase[]?[HelperProfiles.Length];
        // An async helper (AssignTaskAndWaitForPauseOrCompletion, WhenAny...) runs its code in its state machine's MoveNext,
        // where a MethodType.Async patch goes: both are checked, like a hook's Method and Body.
        return _helperMethods[profile] ??= HelperProfiles[profile].Keys
            .Select(key => MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, key)).Where(m => m != null)
            .SelectMany(m => StateMachineBody(m!) is { } body ? new[] { m!, body } : new[] { m! }).ToArray();
    }

    /// <summary>The MoveNext of an async or iterator method's state machine, else null.</summary>
    private static MethodBase? StateMachineBody(MethodBase method) =>
        method.GetCustomAttribute<System.Runtime.CompilerServices.StateMachineAttribute>()?.StateMachineType
            ?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    private static MethodInfo? _runWalk, _combatWalk, _combatHookWalk;
    private static Func<ICombatState, IEnumerable<AbstractModel>>? _vanillaCombatHookWalk;
    private static MethodInfo? _redirectRun, _redirectCombat, _redirectCombatHook;
    private static readonly Dictionary<MethodBase, int> _indexByBody = new();
    /// <summary>Hooks whose transpiler failed in a batch (left vanilla after registering for the preflight).</summary>
    private static readonly HashSet<int> _failedIndexes = new();

    public static void Apply(Harmony harmony)
    {
        if (!FastListeners.Applied)
        {
            throw new PatchSkippedException("needs FastListeners (not applied)");
        }
        _runWalk = Reflect.Method(typeof(IRunState), nameof(IRunState.IterateHookListeners)) ?? throw new MissingMethodException("IRunState.IterateHookListeners");
        _combatWalk = Reflect.Method(typeof(ICombatState), nameof(ICombatState.IterateHookListeners)) ?? throw new MissingMethodException("ICombatState.IterateHookListeners");
        _combatHookWalk = Reflect.Method(typeof(Hook), "IterateCombatHookListeners") ?? throw new MissingMethodException("Hook.IterateCombatHookListeners");
        _vanillaCombatHookWalk = AccessTools.MethodDelegate<Func<ICombatState, IEnumerable<AbstractModel>>>(_combatHookWalk);
        _redirectRun = Reflect.Method(typeof(HookSkip), nameof(RunWalk));
        _redirectCombat = Reflect.Method(typeof(HookSkip), nameof(CombatWalk));
        _redirectCombatHook = Reflect.Method(typeof(HookSkip), nameof(CombatHookWalk));
        // No fingerprint required on Hook.IterateCombatHookListeners here: only the hooks walking through it rely on its
        // code (CombatHookFiltered mirrors its "combat ending" check), and CombatHookWalk asks HookFilter.CombatHookWalkPatched
        // at each walk, which answers "patched" when that code changed. The hooks walking runState/combatState directly
        // stay filtered then.

        // Entries of every classified game version: per hook, the first one whose code, defaults and helpers all match.
        int absent = 0;
        foreach (IGrouping<string, int> hookEntries in Enumerable.Range(0, Hooks.Length).GroupBy(i => Hooks[i].Hook))
        {
            string hookName = hookEntries.Key;
            try
            {
                // The runtime's own lookup (Reflect): a mod patching Harmony's lookups may answer a hook of another game
                // version with a hook of another name (BeforeTurnEnd for BeforeSideTurnEnd), whose code would then fail this
                // row's fingerprint and read as "changed". Told apart in the log; that other hook is checked under its own row.
                MethodInfo? hook = Reflect.Method(typeof(Hook), hookName);
                if (Reflect.SubstitutedMethod(typeof(Hook), hookName, hook) is { } substitute)
                {
                    AnsweredByOthers++;
                    Log.Info($"[{ModEntry.Id}] {Name}: {hookName}: Harmony's lookup answered by another member ({substitute.DeclaringType?.Name}.{substitute.Name}: another mod intercepts reflection lookups), ignored: the game's own {(hook == null ? "has no such hook" : "hook is used")}");
                }
                if (hook == null)
                {
                    absent++; // A hook of another game version.
                    continue;
                }
                // Async hooks run their code (and their walks) in the state machine's MoveNext.
                MethodBase body = hook.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() != null
                    ? Reflect.AsyncMoveNext(hook)
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
                Watch(entry);
            }
            catch (Exception e)
            {
                LeftVanilla++;
                Log.Info($"[{ModEntry.Id}] {Name}: {hookName} left vanilla: {e.Message}");
            }
        }
        CountUnclassified();
        if (_toPatch.Count == 0)
        {
            throw new PatchSkippedException("no hook matched");
        }
        Applied = true;
        _absent = absent;
        // A hook walking Hook.IterateCombatHookListeners asks HookFilter about that walk too (CombatHookWalk).
        PatchSafety.WatchAll(Name, () => _redirectsCombatHookWalk ? HookFilter.CombatHookWalkParts() : Array.Empty<MethodBase?>());
        BaseLibHooks.Init(harmony);
        PatchOverFrames(harmony);
    }

    /// <summary>
    /// The game's Hook methods that walk listeners but have no table row (a hook new in this game version, or one
    /// classify.py found unsafe in every classified version): they run vanilla like any hook left out, and are counted and
    /// logged as left vanilla so the status line, release checks and check_versions see a hook walking every listener
    /// unfiltered. Only the methods outside the table are read (none walks on the supported versions besides
    /// IterateCombatHookListeners itself), so this costs nothing at load today.
    /// </summary>
    private static void CountUnclassified()
    {
        var classified = new HashSet<string>(Hooks.Select(e => e.Hook), StringComparer.Ordinal);
        foreach (MethodInfo method in typeof(Hook).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            // A generated method (local function, '<' in its name) is part of the code of the hook that declares it.
            if (classified.Contains(method.Name) || method.Name.Contains('<') || IsWalk(method) || method.GetMethodBody() == null)
            {
                continue;
            }
            try
            {
                // Its own code and its generated code (state machine, lambdas), like its fingerprint.
                if (new MethodBase[] { method }.Concat(MethodFingerprint.GeneratedCode(method))
                    .Any(code => MethodFingerprint.UsedMembers(code).Any(member => member is MethodInfo called && IsWalk(called))))
                {
                    LeftVanilla++;
                    Log.Info($"[{ModEntry.Id}] {Name}: {method.Name} walks the listeners but isn't classified for this game version: left vanilla");
                }
            }
            catch (Exception e)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {method.Name} not checked for listener walks ({e.Message})");
            }
        }
    }

    /// <summary>
    /// Registers what <see cref="FilterFor"/> asks about for this hook (display only, never throws): the hook and its state
    /// machine with BaseLib's allowed patches (built once BaseLib is there: <see cref="AllowedForPreflight"/>), its version's
    /// helpers (resolved lazily, inside the preflight), and the AbstractModel defaults its filter keeps the overriders of.
    /// </summary>
    private static void Watch(Entry entry)
    {
        // The transpiled code (the hook itself, or its state machine) with other mods' transpilers the IL judge accepts: judged
        // here, in the preflight's load part, as at the first walk.
        Func<MethodInfo[]> judged = () => BodyAllowed(entry, preflight: true).Allowed;
        PatchSafety.Watch(Name, entry.Method, PatchTolerance.SurroundingOnly, lazyAllowed: entry.Body == entry.Method ? judged : AllowedForPreflight);
        if (entry.Body != entry.Method)
        {
            PatchSafety.Watch(Name, entry.Body, judged);
        }
        if (entry.Profile >= 0)
        {
            int profile = entry.Profile;
            if (_watchedProfiles.Add(profile))
            {
                PatchSafety.WatchAll(Name, () => HelperMethods(profile));
            }
        }
        if (entry.Filter?.BaseMethods is { } bases)
        {
            PatchSafety.Watch(Name, bases, PatchTolerance.TypeGated);
        }
    }

    private static readonly HashSet<int> _watchedProfiles = new();

    /// <summary>
    /// <see cref="AllowedBodyPatches"/> for the preflight: it keeps its first answer, so it is only built once BaseLib is
    /// loaded (as <see cref="Finished"/> does); without BaseLib, no BaseLib patch to allow, nothing kept. Whether BaseLib is
    /// loaded is asked once: the preflight runs after every patch applied, every mod loaded (the game loads them all before its
    /// first frame), and it asks per watched hook, a few hundred times.
    /// </summary>
    private static MethodInfo[] AllowedForPreflight() =>
        _allowedBodyPatches ?? ((_baseLibLoaded ??= AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BaseLib"))
            ? AllowedBodyPatches() : Array.Empty<MethodInfo>());

    private static bool? _baseLibLoaded;

    /// <summary>Some hook's walk call to Hook.IterateCombatHookListeners was redirected (<see cref="Transpiler"/>).</summary>
    private static bool _redirectsCombatHookWalk;

    /// <summary>One of the walk calls <see cref="Transpiler"/> redirects.</summary>
    private static bool IsWalk(MethodInfo method) => Same(method, _runWalk) || Same(method, _combatWalk) || Same(method, _combatHookWalk);

    /// <summary><see cref="IsWalk"/> for the IL judge and its tests.</summary>
    internal static readonly Func<MethodInfo, bool> IsWalkCall = IsWalk;

    /// <summary>This patch's transpiler (the IL judge checks Harmony runs it last).</summary>
    internal static MethodInfo OwnTranspiler => _ownTranspiler ??= Reflect.Method(typeof(HookSkip), nameof(Transpiler))!;

    private static MethodInfo? _ownTranspiler;

    /// <summary>
    /// The patches let stand on the hook's transpiled code: BaseLib's allowed ones, plus every other mod's transpiler there when
    /// <see cref="TranspilerJudge"/> accepts their result (with the types their gated insertions test). Worked out again when
    /// Harmony's epoch or the judge's capture moved (each second as well without HarmonyWatch); the hot path compares two ints.
    /// <paramref name="preflight"/>: BaseLib's list as the preflight builds it (not kept for the runtime).
    /// </summary>
    private static Entry.Judged BodyAllowed(Entry entry, bool preflight = false)
    {
        Entry.Judged? last = entry.LastJudged;
        int epoch = HarmonyWatch.Epoch, capture = TranspilerJudge.CaptureVersion;
        long now = Environment.TickCount64;
        if (!preflight && last != null && last.Epoch == epoch && last.Capture == capture && (HarmonyWatch.Applied || now - last.CheckedAt < 1000))
        {
            return last;
        }
        MethodInfo[] allowed = preflight ? AllowedForPreflight() : AllowedBodyPatches();
        Entry.Judged judged;
        if (!PatchSafety.ShapeRules || entry.Body == null
            || TranspilerJudge.Judge(entry.Body, OwnTranspiler, IsWalkCall, entry.Hook, allowed) is not { Accepted: true } verdict)
        {
            judged = new Entry.Judged(epoch, capture, now, allowed, Type.EmptyTypes, false);
        }
        else
        {
            judged = new Entry.Judged(epoch, capture, now, allowed.Concat(verdict.Transpilers).Distinct().ToArray(), verdict.Gates, true);
        }
        if (!preflight)
        {
            entry.LastJudged = judged;
        }
        return judged;
    }

    private static bool Same(MethodInfo method, MethodInfo? other) =>
        other != null && method.MetadataToken == other.MetadataToken && method.Module == other.Module;

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
            Finished();
        }
    }

    private static bool _finished;

    /// <summary>
    /// The game's declared version against the classified versions whose helpers this game's code matches: a cross-check,
    /// never a lock (each hook is judged by its own code). A declared version that is classified but whose helpers don't
    /// match is logged: game files changed, or reflection answered by another mod.
    /// </summary>
    private static void CrossCheckVersion()
    {
        try
        {
            string declared = Reflect.GameVersion() ?? "no version";
            // Silent (HelperProfileHolds logs each helper differing from a profile the hooks rely on; another version's differs).
            bool Holds(int profile) => _profiles?[profile] ?? HelperProfiles[profile].All(helper =>
                MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, helper.Key) is var method
                && (method?.GetMethodBody() == null ? "-" : MethodFingerprint.Of(method)) == helper.Value);
            string[] holding = Enumerable.Range(0, HelperProfiles.Length).Where(Holds).Select(i => HelperProfileVersions[i]).ToArray();
            int classified = Array.FindIndex(HelperProfileVersions, label => declared.StartsWith(label, StringComparison.Ordinal));
            Log.Info($"[{ModEntry.Id}] {Name}: the game declares {declared}; its code matches the tables of {(holding.Length == 0 ? "no classified version" : string.Join(", ", holding))}"
                + (classified >= 0 && !holding.Contains(HelperProfileVersions[classified])
                    ? $" (not {HelperProfileVersions[classified]}'s: game files changed, or another mod answers reflection lookups or rewrote the game's code)"
                    : ""));
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: version cross-check skipped: {e.Message}");
        }
    }

    /// <summary>
    /// The last hook is patched: logs the totals and checks every patched method has its detour again (ModEntry's check
    /// may have run while hooks were still queued). InlineGuard needs nothing here: no hook is inlined by its callers
    /// (each has a loop over an IEnumerable, i.e. exception handling, which the JIT doesn't inline).
    /// </summary>
    private static void Finished()
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        Log.Info($"[{ModEntry.Id}] {Name}: {Active} hooks filtered, {LeftVanilla} left vanilla ({_absent} classified hooks not in this game version"
            + (AnsweredByOthers > 0 ? $", {AnsweredByOthers} lookups answered by another member, ignored" : "") + ")");
        CrossCheckVersion();
        try
        {
            // ModEntry's own check ran when its patches were in, possibly before these hooks: ~150 rebuilds since.
            HarmonyRebuild.CheckAll("HookSkip patched its hooks");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {nameof(HarmonyRebuild)}: {e}");
        }
        // BaseLib's patches FilterFor allows are fingerprinted here, at the menu, rather than in the first hook of a run.
        // Only once BaseLib is loaded (it is by now unless this ran synchronously at load): otherwise the first walk
        // does it, as before.
        try
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BaseLib"))
            {
                AllowedBodyPatches();
            }
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: BaseLib's hook patches checked at the first walk instead ({e.Message})");
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
                // Last of the transpilers: Harmony runs them by priority (highest first), each on the previous one's output,
                // so another mod's transpiler on the hook still sees the game's walk calls it may look for (this one
                // replaces them, and the redirect stays in place even when this patch backs off or is switched off).
                // Equal priorities run in registration order: a transpiler registered at load (every mod loads before
                // these batches) sees the game's calls; one at priority 0 or less registered after these batches sees
                // the redirect calls instead (FilterFor then backs off from that hook, but its matcher may miss). Keeping
                // the game's call and wrapping its result would change the walk's laziness: accepted, none does that.
                using (HarmonyWatch.Own())
                {
                    PatchGuard.Patch(harmony, body, transpiler: new HarmonyMethod(typeof(HookSkip), nameof(Transpiler)) { priority = Priority.Last });
                }
                Active++;
            }
            catch (Exception e)
            {
                LeftVanilla++;
                Log.Info($"[{ModEntry.Id}] {Name}: {hookName} left vanilla: {e.Message}");
                // Registered at Apply (the preflight may run before the last batch): a hook left vanilla here shows no
                // back-off from its own methods, which would change nothing: the hook, its state machine, and the
                // AbstractModel defaults no other hook still filtered keeps (the version's helpers are shared: kept).
                if (_indexByBody.TryGetValue(body, out int failed))
                {
                    _failedIndexes.Add(failed);
                    PatchSafety.Unwatch(Name, Hooks[failed].Method);
                    PatchSafety.Unwatch(Name, body);
                    foreach (MethodInfo baseMethod in Hooks[failed].Filter?.BaseMethods ?? Array.Empty<MethodInfo>())
                    {
                        bool shared = _indexByBody.Values.Any(i => !_failedIndexes.Contains(i)
                            && Hooks[i].Filter?.BaseMethods is { } others && Array.IndexOf(others, baseMethod) >= 0);
                        if (!shared)
                        {
                            PatchSafety.Unwatch(Name, baseMethod);
                        }
                    }
                }
            }
        }
        if (_toPatch.Count == 0)
        {
            Finished();
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
        // What every other mod's transpiler made of the hook (Harmony runs this one last): kept for the IL judge, confirmed once
        // this rebuild ends well (HarmonyWatch). Never throws.
        List<CodeInstruction> input = instructions as List<CodeInstruction> ?? instructions.ToList();
        TranspilerJudge.Capture(__originalMethod, input);
        foreach (CodeInstruction code in input)
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
            if (redirect == _redirectCombatHook)
            {
                _redirectsCombatHookWalk = true;
            }
            // Same stack in, same IEnumerable out: (state[, combat]) -> (state[, combat], index) -> call redirect. In
            // `combatState?.IterateHookListeners() ?? Array.Empty<AbstractModel>()` (v0.113) the call sits on the non-null
            // branch only: the null branch keeps the game's empty array.
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
            // The hook itself: postfixes (arguments by value, __result and __state aside; no __args, no __runOriginal) and void
            // prefixes Harmony runs before every prefix of this mod there (HookFilter's mirrors on some) are let stand. The
            // transpiled body runs the same listeners with the same effects (only no-op calls are left out), so a patch around it
            // sees what it sees without this mod (RitsuLib's lifecycle postfixes wrapping the returned task). Not on an async
            // hook's state machine: a patch there can read the machine's fields, the walk's enumerator among them, which the
            // redirect makes another object. A postfix of an async hook subscribing to ExecutionFinished: Entry.Subscribed.
            // Invariant with HookFilter: on the hooks it mirrors (a bool prefix of this mod skipping the original), a postfix
            // is let stand here only because FilteredHook.IsSafe checks the same method with PrefixesOnly, turning the mirror
            // off (the original runs) whenever another mod has a postfix there: a change to either check needs the other.
            // Other mods' transpilers on the transpiled code stand only when TranspilerJudge accepts what they produce (inserted
            // observers outside the listener iterations, or calls gated on the current listener's type: those types keep their
            // iterations, as gated defaults' do below). Everything else there still backs off.
            Entry.Judged judged = BodyAllowed(entry);
            if (PatchSafety.IsPatchedByOthers(entry.Method, entry.Body == entry.Method ? judged.Allowed : AllowedBodyPatches(), PatchTolerance.SurroundingOnly)
                || (entry.Body != entry.Method && PatchSafety.IsPatchedByOthers(entry.Body, judged.Allowed))
                || (entry.Profile >= 0 && HelpersPatched(entry.Profile)))
            {
                return null;
            }
#if BENCH
            if (judged.Accepted)
            {
                CountJudged(index);
            }
#endif
            // The defaults a dropped listener's call would run: a postfix or prefix there would miss the skipped calls, so only
            // patches doing nothing unless the listener is of some types stand (TypeGated), and those types keep their calls
            // (the filter treats them as overriders); any other patch keeps the hook vanilla.
            Type[]? single = null;
            int gated = 0;
            Entry.Combined? known = entry.LastCombined;
            bool same = known != null;
            MethodInfo[] bases = entry.Filter!.BaseMethods!;
            // The judged transpilers' gates first (the same array while the judgement stands).
            if (judged.Gates.Length > 0)
            {
                same = same && known!.Inputs.Length > 0 && ReferenceEquals(known.Inputs[0], judged.Gates);
                single = judged.Gates;
                gated++;
            }
            for (int i = 0; i < bases.Length; i++)
            {
                if (PatchSafety.IsPatchedByOthers(bases[i], out Type[] found))
                {
                    return null;
                }
                if (found.Length > 0)
                {
                    if (same)
                    {
                        same = gated < known!.Inputs.Length && ReferenceEquals(known.Inputs[gated], found);
                    }
                    single = found;
                    gated++;
                }
            }
            if (gated == 0)
            {
                return entry.Filter;
            }
#if BENCH
            CountGated(index);
#endif
            if (gated == 1)
            {
                return entry.Filter.WithGates(single!);
            }
            if (same && gated == known!.Inputs.Length)
            {
                return entry.Filter.WithGates(known.Gates);
            }
            // Several gated defaults with a new answer: their arrays taken again (cached answers, the same unless one expired
            // meanwhile: then a patched one keeps the hook vanilla) and combined once for the following walks.
            var inputs = new List<Type[]>(gated);
            if (judged.Gates.Length > 0)
            {
                inputs.Add(judged.Gates);
            }
            foreach (MethodInfo baseMethod in bases)
            {
                if (PatchSafety.IsPatchedByOthers(baseMethod, out Type[] found))
                {
                    return null;
                }
                if (found.Length > 0)
                {
                    inputs.Add(found);
                }
            }
            var combined = new Entry.Combined(inputs.ToArray(), inputs.SelectMany(g => g).Distinct().ToArray());
            entry.LastCombined = combined;
            return entry.Filter.WithGates(combined.Gates);
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
    /// Other mods' patches on a hook (or its state machine) that leave it filtered: they must not add listener walks to
    /// the code this transpiler redirects, nor depend on which listeners it walks. BaseLib 3.4.7 makes AfterEnergyReset
    /// and AfterCardPlayed first await a method of its own (its resource reset, its pre-play hooks) through
    /// <c>AsyncMethodCall.Create</c>: no walk added. BaseLib 3.4.8's ModifyBlock patches apply its card modifiers to the
    /// block before the listener walks (a prefix on <c>block</c>, a transpiler inserting one call before the first walk):
    /// no walk added, the result independent of the listeners walked. Patches, the injector and the methods the inserted
    /// code calls fingerprinted.
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
            // Looked up in BaseLib's assembly only: without BaseLib (the first hook of a run, or a preflight), no scan of every
            // loaded type (Harmony's TypeByName fallback).
            MethodInfo? injector = PatchSafety.MethodIn("BaseLib", "BaseLib.Utils.Patching.AsyncMethodCall:Create");
            if (injector != null && PatchSafety.FingerprintMatches(MethodFingerprint.Of(injector), BaseLibAsyncInjectorFingerprint))
            {
                AddMatching(allowed, BaseLibAwaitTranspilers);
            }
            if (BaseLibBlockCallees.All(c => PatchSafety.MethodIn("BaseLib", c.Method) is { } callee && PatchSafety.FingerprintMatches(MethodFingerprint.Of(callee), c.Fingerprint)))
            {
                AddMatching(allowed, BaseLibBlockPatches);
            }
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: BaseLib's hook transpilers not checked ({e.Message}): those hooks stay vanilla with BaseLib");
        }
        // BaseLib loads before any combat, when this first runs: no need to look again.
        return _allowedBodyPatches = allowed.ToArray();
    }

    private static void AddMatching(List<MethodInfo> allowed, (string Method, string Fingerprint)[] patches)
    {
        foreach ((string name, string fingerprint) in patches)
        {
            MethodInfo? patch = PatchSafety.MethodIn("BaseLib", name);
            if (patch != null && PatchSafety.FingerprintMatches(MethodFingerprint.Of(patch), fingerprint))
            {
                allowed.Add(patch);
            }
        }
    }

    private static MethodInfo[]? _allowedBodyPatches;
    private const string BaseLibAsyncInjectorFingerprint = "DF3A98C4301D8FE8";
    private static readonly (string Method, string Fingerprint)[] BaseLibAwaitTranspilers =
    {
        ("BaseLib.Abstracts.CustomResourcePatches:AfterPlay", "A55CDC6F853C8854"),
        ("BaseLib.Patches.Hooks.AfterCardPlayedPatch:AfterPlay", "B1ED9060E4BBD70B"),
    };

    /// <summary>BaseLib 3.4.8's patches on Hook.ModifyBlock (prefix, transpiler).</summary>
    private static readonly (string Method, string Fingerprint)[] BaseLibBlockPatches =
    {
        ("BaseLib.Patches.Hooks.ModifyBaseBlockPatches+ModifyBlockCalc:AdjustBaseAdditive", "2477EB2DF4CD711C"),
        ("BaseLib.Patches.Hooks.ModifyBaseBlockPatches+ModifyBlockCalc:AdjustBaseMultiplicative", "FFCF6B94FC51BC72"),
    };

    /// <summary>What they call: a loop over the card's own modifiers, no listener walk.</summary>
    private static readonly (string Method, string Fingerprint)[] BaseLibBlockCallees =
    {
        ("BaseLib.Patches.Hooks.ModifyBaseBlockPatches:ModifyBaseBlockAdditive", "16CDB53A92498CB8"),
        ("BaseLib.Patches.Hooks.ModifyBaseBlockPatches:ModifyBaseBlockMultiplicative", "D41C34C33D28709E"),
    };

    /// <summary>Replaces <c>runState.IterateHookListeners(combatState)</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> RunWalk(IRunState runState, ICombatState? combatState, int index)
    {
        PileFilter? filter = FilterFor(index);
        if (filter != null && runState is RunState run && (combatState == null || combatState is CombatState))
        {
            CountFiltered();
            return Filtered(run, combatState, filter);
        }
        return runState.IterateHookListeners(combatState);
    }

    /// <summary>Replaces <c>combatState.IterateHookListeners()</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> CombatWalk(ICombatState combatState, int index)
    {
        PileFilter? filter = FilterFor(index);
        if (filter != null && combatState is CombatState combat)
        {
            CountFiltered();
            return Filtered(combat, filter);
        }
        return combatState.IterateHookListeners();
    }

    /// <summary>Replaces <c>Hook.IterateCombatHookListeners(combatState)</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> CombatHookWalk(ICombatState combatState, int index)
    {
        // The filtered walk mirrors this method (nothing while combat is ending): its code changed (fingerprint, checked
        // there), or another mod's patch on it or on what it reads, keeps the game's (never throws).
        PileFilter? filter = FilterFor(index);
        if (filter == null || HookFilter.CombatHookWalkPatched() || combatState is not CombatState combat)
        {
            return _vanillaCombatHookWalk!(combatState);
        }
        CountFiltered();
        return CombatHookFiltered(combat, filter);
    }

    /// <summary>A walk is about to run filtered (counted only where the filtered walk is really returned).</summary>
    [System.Diagnostics.Conditional("BENCH")]
    private static void CountFiltered()
    {
#if BENCH
        FilteredWalks++;
#endif
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
    /// BaseLib's hand size, heal and downgrade hooks (BaseLib is optional and may load after this mod): they loop over every
    /// listener and use only those implementing their interface, so the walk they ask for keeps only the implementers.
    /// </summary>
    private static class BaseLibHooks
    {
        /// <summary>
        /// BaseLib 3.4.7 fingerprints: GetMaxHandSize (the (Player, int) overload, which holds the loop) and DowngradeHook are the
        /// same in 3.4.8, the two heal methods changed there (equivalents in EquivalentFingerprints).
        /// </summary>
        private static readonly (string Type, string Method, Type[]? Args, string Fingerprint, string Interface)[] Targets =
        {
            ("BaseLib.Patches.Hooks.MaxHandSizePatch", "GetMaxHandSize", new[] { typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(int) },
                "1EE490EF3F75AF02", "BaseLib.Hooks.IMaxHandSizeModifier"),
            ("BaseLib.Patches.Hooks.ModifyHealAmountPatches", "ModifyAdditive", null, "895AD94736FEED8E", "BaseLib.Hooks.IHealAmountModifier"),
            ("BaseLib.Patches.Hooks.ModifyHealAmountPatches", "ModifyMultiplicative", null, "98B29C743F42C15F", "BaseLib.Hooks.IHealAmountModifier"),
            // A postfix of CardModel.DowngradeInternal: one walk per downgraded card (Dampen downgrades a whole deck).
            ("BaseLib.Hooks.IAfterCardDowngraded+DowngradeHook", "Patch", null, "D268A4B256D8F60C", "BaseLib.Hooks.IAfterCardDowngraded"),
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
                    Type type = Reflect.TypeByName(typeName) ?? throw new TypeLoadException(typeName);
                    MethodInfo method = Reflect.Method(type, methodName, args) ?? throw new MissingMethodException(name);
                    PatchSafety.RequireFingerprint(method, fingerprint);
                    Type iface = Reflect.TypeByName(interfaceName) ?? throw new TypeLoadException(interfaceName);
                    _filters[method] = new PileFilter(iface.IsAssignableFrom, keepSubscribed: false);
                    PatchGuard.Patch(_harmony!, method, prefix: new HarmonyMethod(typeof(BaseLibHooks), nameof(Prefix)),
                        finalizer: new HarmonyMethod(typeof(BaseLibHooks), nameof(Finalizer)));
                    PatchGuard.Watch(method);
                    // What Prefix asks about before taking the filter (display only).
                    PatchSafety.Watch(Name, method);
                    Log.Info($"[{ModEntry.Id}] {Name}: BaseLib {name} applied");
                }
                catch (Exception e)
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: BaseLib {name} left vanilla: {e.Message}");
                }
            }
            if (_filters.Count == 0)
            {
                return;
            }
            try
            {
                // A filter set for a BaseLib walk of NullRunState (no RunState walk takes it: NullRunState walks the
                // combat directly) would stay pending through BaseLib's whole loop, where a listener's code could start
                // another hook's RunState walk. Any combat walk drops it: BaseLib's own RunState walk takes it in its first
                // prefix, before it walks the combat. A prefix Harmony always runs (no bool return), first.
                PatchGuard.Patch(_harmony!, Reflect.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)),
                    prefix: new HarmonyMethod(typeof(BaseLibHooks), nameof(DropPendingFilter)) { priority = Priority.First });
            }
            catch (Exception e)
            {
                // Each BaseLib method's finalizer still drops its filter when it returns.
                Log.Info($"[{ModEntry.Id}] {Name}: BaseLib pending filter guard on combat walks not applied: {e.Message}");
            }
        }

        private static void DropPendingFilter()
        {
            if (FastListeners.PendingRunFilter != null)
            {
                FastListeners.PendingRunFilter = null;
#if BENCH
                PendingFiltersDropped++;
#endif
            }
        }

        /// <summary>The next RunState.IterateHookListeners call (this method's own walk) keeps only implementers.</summary>
        private static void Prefix(MethodBase __originalMethod)
        {
            // No tolerance: a void prefix of another mod running after this one would take the pending filter for its own walk.
            if (Enabled && FastListeners.RunMirrored && !ListenerCache.IsSuspended && !PatchSafety.IsPatchedByOthers(__originalMethod))
            {
                FastListeners.PendingRunFilter = _filters[__originalMethod];
            }
        }

        /// <summary>Never leaks to a later walk (e.g. when the run state wasn't the game's RunState).</summary>
        private static void Finalizer(Exception? __exception)
        {
            FastListeners.PendingRunFilter = null;
        }
    }
}
