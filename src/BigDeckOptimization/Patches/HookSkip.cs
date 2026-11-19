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
/// (<see cref="FastListeners.KindSkipEnabled"/>, under the same hook_skip option), badges aside: every filtered walk keeps
/// them, and a state without one walks vanilla (<see cref="HasBadge(CombatState)"/>), so a filtered walk never ends with
/// nothing kept where the game's walk yields something (the choice context's model stack, A4 in DESIGN). BaseLib's hand size, heal and
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

    /// <summary>Per hook: walks that ran filtered (the foreign tests: which hooks a patch on a helper left filtered).</summary>
    private static long[] _filteredWalks = Array.Empty<long>();

    /// <summary>"Hook.Name N" per hook that ran filtered at least once.</summary>
    public static IReadOnlyList<string> FilteredWalkReport() =>
        Hooks.Select((entry, i) => (entry.Hook, Count: i < _filteredWalks.Length ? _filteredWalks[i] : 0)).Where(e => e.Count > 0)
            .GroupBy(e => e.Hook).Select(g => $"Hook.{g.Key} {g.Sum(e => e.Count)}").ToList();

    /// <summary>Filtered walks of the hook of that name so far (its rows of every version together).</summary>
    public static long FilteredWalksOf(string hook) =>
        Enumerable.Range(0, Math.Min(Hooks.Length, _filteredWalks.Length)).Where(i => Hooks[i].Hook == hook).Sum(i => _filteredWalks[i]);

    /// <summary>Per hook: its redirected walk calls, filtered or not (the foreign tests: a hook left vanilla still walks).</summary>
    private static long[] _walkCalls = Array.Empty<long>();

    /// <summary>Redirected walk calls of the hook of that name so far, filtered or not.</summary>
    public static long WalkCallsOf(string hook) =>
        Enumerable.Range(0, Math.Min(Hooks.Length, _walkCalls.Length)).Where(i => Hooks[i].Hook == hook).Sum(i => _walkCalls[i]);

    /// <summary>Whether this game has a hook of that name with a row (a name of another version: false).</summary>
    public static bool HasHook(string hook) => Hooks.Any(e => e.Hook == hook && e.Method != null);

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
        /// <summary>Index in <see cref="HelperSets"/> of the helpers a dropped listener's iteration calls, -1 when none.</summary>
        public readonly int HelperSet;
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

        /// <summary>Whether this mod's own patches on <see cref="Method"/> are only those TranspiledOnly allows, as of that epoch and time.</summary>
        public sealed record OwnAnswer(int Epoch, long CheckedAt, bool Only);

        public OwnAnswer? LastOwn;

        public Entry(string hook, string fingerprint, bool subscribed, int profile, int helperSet, params string[] methods)
        {
            Hook = hook;
            Fingerprint = fingerprint;
            Subscribed = subscribed;
            Profile = profile;
            HelperSet = helperSet;
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
            string actual = ProfileValue(helper.Key);
            if (actual != helper.Value)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: helper {helper.Key} is {actual}, profile {profile} expects {helper.Value}");
            }
            return actual == helper.Value;
        });
    }

    /// <summary>
    /// What a helper profile pins under <paramref name="key"/> in this game: a method's code fingerprint ("-": absent); since the
    /// verification audit of 2026-11-17 (A2), "virtual:K" its virtual/final flag ("virtual": another class's override can run
    /// in its place, "-" not), and "dispatch:K|C" the game's implementations a virtual or interface call C in K's code reaches
    /// (<see cref="MethodFingerprint.DispatchTargets"/>: ICombatState.Players -> CombatState's and NullCombatState's; a new
    /// implementer would run code no set lists). Anything that can't be read answers "?" (never equal: the profile fails).
    /// </summary>
    private static string ProfileValue(string key)
    {
        try
        {
            Assembly game = typeof(AbstractModel).Assembly;
            if (key.StartsWith("virtual:", StringComparison.Ordinal))
            {
                MethodBase? member = MethodFingerprint.Resolve(game, key["virtual:".Length..]);
                return member == null ? "?" : member is MethodInfo { IsVirtual: true, IsFinal: false } ? "virtual" : "-";
            }
            if (key.StartsWith("dispatch:", StringComparison.Ordinal))
            {
                string rest = key["dispatch:".Length..];
                int bar = rest.IndexOf(")|", StringComparison.Ordinal);
                MethodBase? caller = bar < 0 ? null : MethodFingerprint.Resolve(game, rest[..(bar + 1)]);
                return caller == null ? "?" : MethodFingerprint.DispatchTargets(game, caller, rest[(bar + 2)..]) ?? "?";
            }
            MethodBase? method = MethodFingerprint.Resolve(game, key);
            return method?.GetMethodBody() == null ? "-" : MethodFingerprint.Of(method);
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: helper profile entry {key} not read ({e.GetType().Name})");
            return "?";
        }
    }

    /// <summary>
    /// The helpers a dropped listener's iteration calls, per helper group: an entry's own set (<see cref="HelperSets"/>,
    /// classify.py: the helpers its loops call, closed over their own calls), or with the shape rules off
    /// (<see cref="PatchSafety.ShapeRules"/>) every helper of its version's profile, as before the sets. Each with the parameter
    /// holding the listener in all those calls (null: none, or the state machine of an async helper: any other mod's patch backs
    /// off). Resolved once per group (also for the back-off preflight).
    /// </summary>
    private static (MethodBase Method, string? Listener)[]?[]? _helperMethods;

    /// <summary>
    /// A helper group's answer: patched (an ungated patch on one of its helpers), and the listener types the gated patches test
    /// (the same array while they stay the same: <see cref="FilterFor"/>'s combined gates compare by reference), with the Harmony
    /// epoch and time it was worked out at. Read and replaced whole (a hook may run off the main thread).
    /// </summary>
    /// <summary>Unseen: <see cref="InlineGuard.UnseenVersion"/> when answered (a helper recorded since asks again at once).</summary>
    private sealed record HelperAnswer(bool Patched, Type[] Gates, long CheckedAt, int Epoch, int Unseen);

    private static HelperAnswer?[]? _helperAnswers;

    /// <summary>The entry's helper group (<see cref="_helperMethods"/>), or -1 when its loops call no helper.</summary>
    private static int HelperGroup(Entry entry) =>
        PatchSafety.ShapeRules ? entry.HelperSet : entry.Profile >= 0 ? HelperSets.Length + entry.Profile : -1;

    /// <summary>
    /// Asked per walk: whether another mod's patch on a helper of this group could miss the calls a dropped listener skips, and
    /// the listener types its gated patches test (those keep their iterations, as the defaults' gates do). Answered from a
    /// per-group result rechecked each second, and at once after any Patch or Unpatch (HarmonyWatch.Epoch).
    /// </summary>
    private static bool HelpersPatched(int group, out Type[] gates)
    {
        HelperAnswer?[] answers = _helperAnswers ??= new HelperAnswer?[HelperSets.Length + HelperProfiles.Length];
        HelperAnswer? known = answers[group];
        long now = Environment.TickCount64;
        int epoch = HarmonyWatch.Epoch;
        int unseen = InlineGuard.UnseenVersion;
        if (known == null || now - known.CheckedAt >= 1000 || known.Epoch != epoch || known.Unseen != unseen)
        {
            bool patched = AnyHelperPatched(group, out Type[] found);
            if (known != null && found.Length > 0 && known.Gates.Length == found.Length && !known.Gates.Except(found).Any())
            {
                found = known.Gates;
            }
            known = new HelperAnswer(patched, patched ? Type.EmptyTypes : found, now, epoch, unseen);
            answers[group] = known;
        }
        gates = known.Gates;
        return known.Patched;
    }

    /// <summary>The compiler-generated callees' keys not found (the "unresolved" note, C3).</summary>
    private static readonly SortedSet<string> _unresolvedKeys = new(StringComparer.Ordinal);

    /// <summary>Groups with a compiler-generated callee whose key no longer resolves (renumbered): always "patched".</summary>
    private static bool[]? _unresolvedGroups;

    private static bool AnyHelperPatched(int group, out Type[] gates)
    {
        gates = Type.EmptyTypes;
        List<Type>? found = null;
        (MethodBase Method, string? Listener)[] helpers = HelperMethods(group);
        if ((_unresolvedGroups != null && _unresolvedGroups[group]) || ForeignImplementers(group))
        {
            return true;
        }
        foreach ((MethodBase helper, string? listener) in helpers)
        {
            // A2 (re-audit of b64e896): a replacement of this mod that may have inlined this helper couldn't be compiled again after
            // another mod patched it: from there the patch isn't reached at all, so no gate on it holds (strict, the group vanilla).
            if (InlineGuard.InlinedUnseen(helper))
            {
                return true;
            }
            if (listener == null)
            {
                // No tolerance: a dropped listener skips its calls to this helper, which has no listener argument to gate on,
                // so a patch here would miss those calls. The same for every game method the helpers may call and for the
                // reviewed chains' getters (in the sets since 2026-11-17, from fpdiff's call graph: CardModel.Owner,
                // Creature.Player, CombatState.Players... the v0.107.1 constructor inlines the owner lookup).
                if (PatchSafety.IsPatchedByOthers(helper))
                {
                    return true;
                }
                continue;
            }
            // Every call a dropped listener skips passes that listener as `listener` (classify.py: the loop patterns and the
            // helpers' own calls): a patch doing nothing unless that argument is of some types (TypeGated on that parameter,
            // registered by HelperMethods) misses nothing once those types keep their iterations. Anything else backs off.
            if (PatchSafety.IsPatchedByOthers(helper, out Type[] typeGates))
            {
                return true;
            }
            if (typeGates.Length > 0)
            {
                (found ??= new List<Type>()).AddRange(typeGates);
            }
        }
        if (found != null)
        {
            gates = found.Distinct().ToArray();
        }
        return false;
    }

    /// <summary>
    /// Helpers on which no prefix stands even gated on the listener: <c>PlayerChoiceContext.PushModel</c> creates the context's
    /// model stack on its first call (v0.107.1 and v0.113.1 PlayerChoiceContext.cs, <c>if (_modelStack == null) _modelStack =
    /// new Stack</c>), so at the first kept listener's call of a context whose earlier listeners were all dropped a prefix would
    /// find the stack null (<c>ModelStack</c>, <c>____modelStack</c>) where the game has an empty one. A postfix runs once the
    /// stack exists on both sides, with the same contents (each listener pops what it pushed); PopModel's and the other helpers'
    /// prefixes see no state a dropped call creates. After the walk the stack is the game's (A4: the badges kept and
    /// <see cref="HasBadge(CombatState)"/>); during a mirrored walk it may still be created later than the game's, at the first
    /// kept listener rather than the first listener, which is what this back-off covers (the walks over the game's own walk keep
    /// its first listener: created at the same moment there).
    /// </summary>
    private static readonly HashSet<string> PrefixesNeverGated = new(StringComparer.Ordinal)
    {
        "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext::PushModel(AbstractModel)",
    };

    /// <summary>The helpers of a group present in this game, resolved once (also for the back-off preflight).</summary>
    private static (MethodBase Method, string? Listener)[] HelperMethods(int group)
    {
        (MethodBase Method, string? Listener)[]?[] methods = _helperMethods ??= new (MethodBase, string?)[]?[HelperSets.Length + HelperProfiles.Length];
        if (methods[group] is { } known)
        {
            return known;
        }
        bool set = group < HelperSets.Length;
        // With the shape rules off a group is a profile (every helper of a version, as before the sets): its pinned methods (the
        // "virtual:" and "dispatch:" entries aside), plus the compiler-generated callees of the sets its hooks use, which no
        // profile pins (A3, verification audit of 2026-11-17: since a30358c they were neither checked nor able to back off there).
        IEnumerable<string> keys = set ? HelperSets[group]
            : HelperProfiles[group - HelperSets.Length].Keys
                .Where(k => !k.StartsWith("virtual:", StringComparison.Ordinal) && !k.StartsWith("dispatch:", StringComparison.Ordinal))
                .Concat(Hooks.Where(e => e.Profile == group - HelperSets.Length && e.HelperSet >= 0).SelectMany(e => HelperSets[e.HelperSet])
                    .Where(k => k.Contains('<'))).Distinct();
        var resolved = new List<(MethodBase Method, string? Listener)>();
        var resolvedKeys = new List<string>();
        foreach (string key in keys)
        {
            MethodBase? method = MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, key);
            if (method == null)
            {
                // A helper of another game version. A compiler-generated callee of this one (a lambda the helpers' getters use,
                // not pinned on its own: its code is in its caller's fingerprint) gone means it was renumbered: its patches
                // can't be checked under its new name, so the group backs off (only the hooks constructing contexts). B1: said
                // in the log and shown in bdopt why (a note: no patch shows it).
                if (key.Contains('<'))
                {
                    (_unresolvedGroups ??= new bool[HelperSets.Length + HelperProfiles.Length])[group] = true;
                    Log.Info($"[{ModEntry.Id}] {Name}: {key} not found in this game (renumbered?): the hooks of its helper group {group} run the game's code");
                    // C3 (re-audit round 2): one note for every group, naming every method not found.
                    lock (_unresolvedKeys)
                    {
                        _unresolvedKeys.Add(key);
                        PatchSafety.Note(Name, "unresolved", $"compiler-generated game methods the choice-context helpers call ({string.Join(", ", _unresolvedKeys)}) aren't found in this game: "
                            + "the hooks using those helpers run the normal game", Array.Empty<string>());
                    }
                }
                continue;
            }
            resolvedKeys.Add(key);
            string? listener = set && HelperListeners.TryGetValue(key, out string? parameter) && parameter.Length > 0 ? parameter : null;
            if (listener != null)
            {
                // Before any check of it: the TypeGated check reads the gate on this parameter.
                PatchSafety.SetListenerParameter(method, listener, prefixesGated: !PrefixesNeverGated.Contains(key));
            }
            resolved.Add((method, listener));
            // An async helper (AssignTaskAndWaitForPauseOrCompletion, WhenAny...) runs its code in its state machine's MoveNext,
            // where a MethodType.Async patch goes: both are checked, like a hook's Method and Body; the state machine strictly.
            if (StateMachineBody(method) is { } body)
            {
                resolved.Add((body, null));
            }
        }
        // A2: the game's virtual methods the group's code dispatches to (pinned per version as "dispatch:" entries, their game
        // implementers checked with the profile) and its own virtual members: another assembly's override or implementation runs
        // code no set lists (a mod's ICombatState), so the group backs off while one is loaded (AnyHelperPatched).
        var virtuals = new List<MethodInfo>();
        foreach (Dictionary<string, string> profile in HelperProfiles)
        {
            foreach (string entry in profile.Keys)
            {
                if (!entry.StartsWith("dispatch:", StringComparison.Ordinal))
                {
                    continue;
                }
                string rest = entry["dispatch:".Length..];
                int bar = rest.IndexOf(")|", StringComparison.Ordinal);
                if (bar < 0 || !resolvedKeys.Contains(rest[..(bar + 1)]))
                {
                    continue;
                }
                try
                {
                    if (MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, rest[..(bar + 1)]) is { } caller
                        && MethodFingerprint.DispatchCallee(caller, rest[(bar + 2)..]) is { } callee)
                    {
                        virtuals.Add(callee);
                    }
                }
                catch
                {
                    // The profile check fails on the same entry (ProfileValue answers "?"): its hooks are vanilla already.
                }
            }
        }
        virtuals.AddRange(resolved.Select(r => r.Method).OfType<MethodInfo>().Where(m => m.IsVirtual && !m.IsFinal));
        (_groupVirtuals ??= new MethodInfo[]?[HelperSets.Length + HelperProfiles.Length])[group] = virtuals.Distinct().ToArray();
        return methods[group] = resolved.ToArray();
    }

    /// <summary>Per helper group, the virtual methods whose overrides in other assemblies would run code no set lists (A2).</summary>
    private static MethodInfo[]?[]? _groupVirtuals;

    /// <summary>Per helper group, the loaded code's key and the foreign overriders found then (A2, rechecked when it changes).</summary>
    private sealed record GroupOverriders(long Key, string[] Types);

    /// <summary>Per helper group (A6, re-audit round 2: records replaced whole, never a torn struct read).</summary>
    private static GroupOverriders?[]? _groupOverriders;

    /// <summary>
    /// A2: a loaded class outside the game (another mod's) overriding or implementing one of the group's dispatched virtual
    /// methods: its code would run in the calls a dropped listener skips, which no set lists. Logged and noted (bdopt why) once
    /// per change; cached while the loaded code stays the same (<see cref="LoadedCode.Key"/>: an assembly loads or a dynamic one
    /// gains types, A4 of the re-audit round 2).
    /// </summary>
    private static bool ForeignImplementers(int group)
    {
        MethodInfo[] virtuals = _groupVirtuals?[group] ?? Array.Empty<MethodInfo>();
        if (virtuals.Length == 0)
        {
            return false;
        }
        long loaded = LoadedCode.Key();
        GroupOverriders?[] known = _groupOverriders ??= new GroupOverriders?[HelperSets.Length + HelperProfiles.Length];
        if (known[group] is { } last && last.Key == loaded)
        {
            return last.Types.Length > 0;
        }
        string[] types = CodeSet.ForeignOverridersOf(virtuals);
        known[group] = new GroupOverriders(loaded, types);
        if (types.Length > 0)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {string.Join(", ", types)} override code the choice-context helpers of group {group} call: those hooks run the game's code");
        }
        // C3 (re-audit round 2): one note for every group (the same classes were listed once per group before).
        string[] all = known.Where(k => k != null).SelectMany(k => k!.Types).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
        if (all.Length > 0)
        {
            PatchSafety.Note(Name, "overriders", $"other mods' classes ({string.Join(", ", all.Take(6))}{(all.Length > 6 ? $" and {all.Length - 6} more" : "")}) override game code the choice-context helpers call: "
                + "the hooks using those helpers run the normal game", Array.Empty<string>());
        }
        else
        {
            PatchSafety.ClearNote(Name, "overriders");
        }
        return types.Length > 0;
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
        LookUpBadges();
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
                // Badges kept: with HasBadge's guard, a filtered walk keeps one listener at least whenever the game's walk yields one (A4).
                entry.Filter = PileFilter.Overriding(entry.Subscribed, entry.Methods).KeepingBadges();
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
        // What the badge guard reads at each filtered walk (HasBadge).
        PatchSafety.Watch(Name, new[] { _combatBadges, _runBadges });
        // Other mods' patches let stand on the choice-context helpers (gated on the listener): shown in bdopt why (kept section).
        PatchSafety.WatchNote(Name, NoteHelperGates);
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
        // With the tolerance FilterFor asks (MethodTolerance, the same function: TranspiledOnly while this mod's own patches there
        // are only those rule A allows, else SurroundingOnly), worked out again at each preflight: display and runtime agree.
        PatchSafety.Watch(Name, entry.Method, () => MethodTolerance(entry), entry.Body == entry.Method ? judged : AllowedForPreflight);
        if (entry.Body != entry.Method)
        {
            PatchSafety.Watch(Name, entry.Body, judged);
        }
        int group = HelperGroup(entry);
        if (group >= 0 && _watchedGroups.Add(group))
        {
            try
            {
                // Resolved now (Mismatch already resolved every helper of the profile): the listener parameters are registered
                // before anything asks PatchSafety about these helpers (the runtime check resolves them first anyway).
                HelperMethods(group);
            }
            catch
            {
                // Display only here: the runtime check resolves them again and falls back to vanilla on a throw.
            }
            // As AnyHelperPatched asks: the helpers without a listener argument (and the async ones' state machines) strictly,
            // the others gated on the listener's parameter.
            PatchSafety.WatchAll(Name, () => HelperMethods(group).Where(h => h.Listener == null).Select(h => (MethodBase?)h.Method));
            PatchSafety.WatchPairs(Name, () => HelperMethods(group).Where(h => h.Listener != null).Select(h => ((MethodBase?)h.Method, (MethodInfo[]?)null)),
                PatchTolerance.TypeGated);
        }
        if (entry.Filter?.BaseMethods is { } bases)
        {
            PatchSafety.Watch(Name, bases, PatchTolerance.TypeGated);
        }
    }

    private static readonly HashSet<int> _watchedGroups = new();

    /// <summary>
    /// C1/C2 (re-audit round 2): the group's answer as the preflight sees it (back-offs recorded under this patch's name, not
    /// counted as the runtime's): a renumbered callee, another mod's class overriding a dispatched virtual method (worked out
    /// here, so its note shows before any combat), an ungated patch on a helper, or a gated one refused.
    /// </summary>
    private static bool PreflightPatched(int group)
    {
        (MethodBase Method, string? Listener)[] helpers = HelperMethods(group);
        if ((_unresolvedGroups != null && _unresolvedGroups[group]) || ForeignImplementers(group))
        {
            return true;
        }
        foreach ((MethodBase helper, string? listener) in helpers)
        {
            if (listener == null ? PatchSafety.IsPatchedByOthers(helper, Name) : PatchSafety.IsPatchedByOthers(helper, out _, Name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The kept note (display only, bdopt why): other mods' patches on the helpers a dropped listener's iteration calls that
    /// stand because they are gated on the listener argument, each helper with the types its own patches test (those listeners
    /// keep their calls). Judged from the patches' IL (TypeGated), not a premise. Only helpers of a group that runs filtered
    /// (B2, verification audit of 2026-11-17: a group sent to the game by another patch keeps every listener anyway). Cleared
    /// when there is none, or with the shape rules off (no gate then).
    /// </summary>
    private static void NoteHelperGates()
    {
        var methods = new List<string>();
        var patched = new List<MethodBase?>();
        if (PatchSafety.ShapeRules)
        {
            var gatesOf = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            foreach (int group in _watchedGroups.ToArray())
            {
                // The runtime's last answer for the group (not asked again here: a check from the preflight would count as the
                // runtime's in the test tooling): its hooks run the game's code, no listener is dropped there. Before the first walk
                // (C2, re-audit round 2: no runtime answer yet) the preflight's own answer, recorded under this patch's name.
                if (_helperAnswers?[group] is { Patched: true } || (_helperAnswers?[group] == null && PreflightPatched(group)))
                {
                    continue;
                }
                foreach ((MethodBase helper, string? listener) in HelperMethods(group))
                {
                    if (listener == null || !PatchSafety.HasOtherPatches(helper) || PatchSafety.IsPatchedByOthers(helper, out Type[] found, Name))
                    {
                        continue;
                    }
                    string name = $"{helper.DeclaringType?.Name}.{helper.Name} ({listener})";
                    if (!gatesOf.TryGetValue(name, out List<Type>? types))
                    {
                        gatesOf[name] = types = new List<Type>();
                        patched.Add(helper);
                    }
                    types.AddRange(found);
                }
            }
            methods.AddRange(gatesOf.Select(g => $"{g.Key}: {string.Join(" or ", g.Value.Distinct().Select(t => t.Name))}"));
        }
        if (methods.Count == 0)
        {
            PatchSafety.ClearKept(Name, "helper_gates");
            return;
        }
        PatchSafety.Kept(Name, "helper_gates", $"other mods' patches on the hooks' choice-context helpers do nothing unless the listener is of these types, "
            + $"judged from the patches' code ({string.Join("; ", methods)}): those listeners keep their calls", PatchSafety.OtherModsPatching(patched));
    }

    /// <summary>
    /// The tolerance on the hook method itself: TranspiledOnly (rule A) while this mod's own patches there are only HookSkip's
    /// transpiler, HookFilter's mirror prefix and rethrowing finalizer, the read-only guard and order-free prefixes
    /// (LimitlessHookSkip's sampling prefixes; the test builds' probes); else SurroundingOnly, the rule before. Worked out again
    /// after any Patch or Unpatch (HarmonyWatch's epoch) and each second. The preflight asks this same
    /// function (Watch), so bdopt why and the menu show the tolerance in force; a flip forgets the other tolerance's record.
    /// </summary>
    private static PatchTolerance MethodTolerance(Entry entry)
    {
        if (!PatchSafety.ShapeRules)
        {
            if (entry.LastOwn is not null)
            {
                // Shape rules turned off: SurroundingOnly from now on, a TranspiledOnly record forgotten (asked again once on).
                entry.LastOwn = null;
                PatchSafety.ForgetOtherTolerances(Name, entry.Method, PatchTolerance.SurroundingOnly, PatchTolerance.TranspiledOnly, PatchTolerance.SurroundingOnly);
            }
            return PatchTolerance.SurroundingOnly;
        }
        Entry.OwnAnswer? last = entry.LastOwn;
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        // Each second even with HarmonyWatch (C5, re-audit round 2: the reason given before was wrong). Its epoch moves inside
        // UpdateWrapper's finalizer, before Harmony stores the new patch list, but Harmony 2.4.2's PatchProcessor holds its lock
        // across UpdateWrapper and UpdatePatchInfo and GetPatchInfo takes the same lock: a check reading the new epoch then
        // reads the new list. The timed recheck stays for what moves no epoch (HarmonyWatch's finalizer removed by another mod's
        // UnpatchAll, until HarmonyWatch.Revalidate puts it back).
        if (last == null || last.Epoch != epoch || now - last.CheckedAt >= 1000)
        {
            Entry.OwnAnswer? before = last;
            last = new Entry.OwnAnswer(epoch, now, OwnPatchesOnly(entry.Method));
            entry.LastOwn = last;
            if (before == null || before.Only != last.Only)
            {
                // The answer flipped (or is the first): a back-off recorded under the other tolerance no longer stands; the next
                // check under this one records what it finds.
                PatchSafety.ForgetOtherTolerances(Name, entry.Method, last.Only ? PatchTolerance.TranspiledOnly : PatchTolerance.SurroundingOnly,
                    PatchTolerance.TranspiledOnly, PatchTolerance.SurroundingOnly);
            }
        }
        return last.Only ? PatchTolerance.TranspiledOnly : PatchTolerance.SurroundingOnly;
    }

    /// <summary>This mod's patches on the hook method are only those TranspiledOnly allows (false when it can't tell).</summary>
    private static bool OwnPatchesOnly(MethodBase? method)
    {
        try
        {
            if (method == null)
            {
                return false;
            }
            if (Harmony.GetPatchInfo(method) is not { } info)
            {
                return true;
            }
            static bool Ours(Patch p) => PatchGuard.IsOurs(p.owner);
            foreach (Patch p in info.Prefixes.Where(Ours))
            {
                if (PatchSafety.PatchMethodOf(p) is not { } m || !(HookFilter.IsMirrorPatch(m) || m.IsDefined(typeof(OrderFreePrefixAttribute), false)))
                {
                    return false;
                }
            }
            foreach (Patch p in info.Transpilers.Where(Ours))
            {
                if (PatchSafety.PatchMethodOf(p) is not { } m || m.MethodHandle != OwnTranspiler.MethodHandle)
                {
                    return false;
                }
            }
            foreach (Patch p in info.Finalizers.Where(Ours))
            {
                if (PatchSafety.PatchMethodOf(p) is not { } m || !(PatchGuard.IsGuard(m) || HookFilter.IsMirrorPatch(m)))
                {
                    return false;
                }
            }
            // The test tooling's read-only probe postfixes aside (ReadOnlyProbePostfixAttribute: Debug builds only), so a test
            // build takes the tolerance a Release build takes.
            static bool Probe(Patch p) => PatchSafety.PatchMethodOf(p) is { } m && m.IsDefined(typeof(ReadOnlyProbePostfixAttribute), false);
            return !info.Postfixes.Any(p => Ours(p) && !Probe(p)) && !info.InnerPrefixes.Any(Ours) && !info.InnerPostfixes.Any(Ours);
        }
        catch
        {
            return false;
        }
    }

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
            bool Holds(int profile) => _profiles?[profile] ?? HelperProfiles[profile].All(helper => ProfileValue(helper.Key) == helper.Value);
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
            // The hook itself (rule A, 2026-11-17: TranspiledOnly): every prefix and postfix of another mod stands, whatever its
            // shape (bool prefixes, ref __result, arguments written by reference, __args, __runOriginal, factories). This mod's
            // only code there is the transpiled body (and, on some, HookFilter's mirror and the read-only guard): a prefix decides
            // whether and with which arguments the body runs, as without this mod, and the body runs the same listeners with the
            // same effects (only no-op calls are left out), so a postfix sees the same __result, out values and __runOriginal
            // (RitsuLib's lifecycle postfixes wrapping the returned task; EnchantLib's ModifyBlock bool prefix and ShouldDie
            // postfix; Zone's ModifyHpLost modifiers). Holds on v0.107.1, v0.111.0 and v0.113.1 alike: it relies on no hook's
            // code, only on the transpiler keeping each hook's effects (each row's fingerprint and classification) and on
            // MethodTolerance (this mod's own patches there are only those; another of this mod's: SurroundingOnly, as before).
            // Finalizers that don't provably pass the exception on unchanged (F1: void, or returning __exception as it came stand),
            // transpilers (the IL judge aside) and inner patches still back off. Not on an async hook's state
            // machine: a patch there can read the machine's fields, the walk's enumerator among them, which the redirect makes
            // another object (strict, below). A postfix of an async hook subscribing to ExecutionFinished: Entry.Subscribed.
            // Invariant with HookFilter: on the hooks it mirrors (a bool prefix of this mod skipping the original), what
            // BeforeReplace refuses (postfixes reading __runOriginal or __args or writing by reference, prefixes sorted after the
            // mirror) stands here only because FilteredHook.IsSafe checks the same method with BeforeReplace (once per
            // ListenerCache scope), turning the mirror off (the original, transpiled, runs) whenever another mod has one there:
            // a change to either check needs the other.
            // Other mods' transpilers on the transpiled code stand only when TranspilerJudge accepts what they produce (inserted
            // observers outside the listener iterations, or calls gated on the current listener's type: those types keep their
            // iterations, as gated defaults' do below). Everything else there still backs off.
            // The helpers a dropped listener's iteration calls (choice contexts, PushModel/PopModel, ExecutionFinished): only
            // that hook's own set (classify.py), and on those only patches gated on the listener's argument stand, their types
            // keeping their iterations (HelpersPatched).
            Entry.Judged judged = BodyAllowed(entry);
            Type[] helperGates = Type.EmptyTypes;
            int group = HelperGroup(entry);
            if (PatchSafety.IsPatchedByOthers(entry.Method, entry.Body == entry.Method ? judged.Allowed : AllowedBodyPatches(), MethodTolerance(entry))
                || (entry.Body != entry.Method && PatchSafety.IsPatchedByOthers(entry.Body, judged.Allowed))
                || (group >= 0 && HelpersPatched(group, out helperGates)))
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
            // The judged transpilers' gates first (the same array while the judgement stands), then the helpers' (the same
            // array while their answer stands).
            if (judged.Gates.Length > 0)
            {
                same = same && known!.Inputs.Length > 0 && ReferenceEquals(known.Inputs[0], judged.Gates);
                single = judged.Gates;
                gated++;
            }
            if (helperGates.Length > 0)
            {
                same = same && gated < known!.Inputs.Length && ReferenceEquals(known.Inputs[gated], helperGates);
                single = helperGates;
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
            if (helperGates.Length > 0)
            {
                inputs.Add(helperGates);
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
        CountWalk(index);
        PileFilter? filter = FilterFor(index);
        if (filter != null && runState is RunState run && (combatState == null || combatState is CombatState)
            && (combatState is CombatState child ? HasBadge(child) : HasBadge(run)))
        {
            CountFiltered(index);
#if BENCH
            return EndCheck(Filtered(run, combatState, filter), () => runState.IterateHookListeners(combatState), index);
#else
            return Filtered(run, combatState, filter);
#endif
        }
        return runState.IterateHookListeners(combatState);
    }

    /// <summary>Replaces <c>combatState.IterateHookListeners()</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> CombatWalk(ICombatState combatState, int index)
    {
        CountWalk(index);
        PileFilter? filter = FilterFor(index);
        if (filter != null && combatState is CombatState combat && HasBadge(combat))
        {
            CountFiltered(index);
#if BENCH
            return EndCheck(Filtered(combat, filter), combatState.IterateHookListeners, index);
#else
            return Filtered(combat, filter);
#endif
        }
        return combatState.IterateHookListeners();
    }

    /// <summary>Replaces <c>Hook.IterateCombatHookListeners(combatState)</c> in a hook.</summary>
    public static IEnumerable<AbstractModel> CombatHookWalk(ICombatState combatState, int index)
    {
        // The filtered walk mirrors this method (nothing while combat is ending): its code changed (fingerprint, checked
        // there), or another mod's patch on it or on what it reads, keeps the game's (never throws).
        CountWalk(index);
        PileFilter? filter = FilterFor(index);
        if (filter == null || HookFilter.CombatHookWalkPatched() || combatState is not CombatState combat || !HasBadge(combat))
        {
            return _vanillaCombatHookWalk!(combatState);
        }
        CountFiltered(index);
#if BENCH
        return EndCheck(CombatHookFiltered(combat, filter), () => _vanillaCombatHookWalk!(combatState), index);
#else
        return CombatHookFiltered(combat, filter);
#endif
    }

    /// <summary>
    /// The getters the badge guard reads (<see cref="HasBadge(CombatState)"/>), and whether both have the code it relies on:
    /// auto-properties returning their backing field, <c>CombatState.BadgeModels</c> set in its constructor (v0.107.1 and
    /// v0.113.1 CombatState.cs: <c>badgeModels ?? Array.Empty</c>), <c>RunState.BadgeModels</c> in <c>CreateShared</c> (a
    /// clone of <c>ModelDb.BadgeModels</c>, two badge types on v0.107.1, v0.111.0 and v0.113.1). The same fingerprints on the
    /// three versions.
    /// </summary>
    private static MethodInfo? _combatBadges, _runBadges;
    private static bool _badgesKnown;
    private const string CombatBadgesFingerprint = "9B31700B809E179C";
    private const string RunBadgesFingerprint = "9179602CD0ECA943";

    /// <summary>
    /// Finds the badge getters and checks their code: a change (or a lookup failure) skips HookSkip as a whole (not applied:
    /// the menu and bdopt show it, as any other patch whose code changed), since the guard could then answer no walk.
    /// </summary>
    private static void LookUpBadges()
    {
        string combat, run;
        try
        {
            _combatBadges = Reflect.PropertyGetter(typeof(CombatState), nameof(CombatState.BadgeModels));
            _runBadges = Reflect.PropertyGetter(typeof(RunState), nameof(RunState.BadgeModels));
            combat = _combatBadges == null ? "missing" : MethodFingerprint.Of(_combatBadges);
            run = _runBadges == null ? "missing" : MethodFingerprint.Of(_runBadges);
        }
        catch (Exception e)
        {
            _badgesKnown = false;
            throw new PatchSkippedException($"the badge getters not checked ({e.Message}): the walk guard (A4) can't be relied on");
        }
        _badgesKnown = PatchSafety.FingerprintMatches(combat, CombatBadgesFingerprint) && PatchSafety.FingerprintMatches(run, RunBadgesFingerprint);
        if (!_badgesKnown)
        {
            throw new PatchSkippedException($"the badge getters changed (CombatState.get_BadgeModels {combat}, expected {CombatBadgesFingerprint}; "
                + $"RunState.get_BadgeModels {run}, expected {RunBadgesFingerprint}): the walk guard (A4) can't be relied on");
        }
    }

    /// <summary>
    /// The badge guard (A4, verification audit of 2026-11-17, made exact): a hook's loop pushes each listener on its passed-in
    /// choice context first thing (every <c>PushModel</c> in Hook.cs, 24 on v0.107.1 and v0.113.1), and <c>PushModel</c> creates
    /// the context's model stack on its first call. A filtered walk that kept nothing where the game's walk yields something would
    /// leave the stack null where the game leaves it empty. The filters drop no badge (<see cref="PileFilter.KeepsBadges"/>; the
    /// game's walks yield every badge of the state they read, its <c>Contains</c> true for a BadgeModel): a filtered walk of a
    /// state holding one keeps one at least. A state without one (none in the game's runs: <c>RunState.CreateShared</c> gives
    /// every run the badges, and every CombatState of a run gets the run's) walks vanilla. The run walk with a child combat
    /// yields the child's badges, not the run's (<c>childCombatState == null</c> adds the run's). Read through the getters, as
    /// the walks read them; another mod's patch there (the guard would run it once more) or a changed getter: vanilla.
    /// </summary>
    private static bool HasBadge(CombatState combat) =>
        (_badgesKnown && !PatchSafety.IsPatchedByOthers(_combatBadges) && combat.BadgeModels.Count > 0) || NoBadge();

    /// <summary><see cref="HasBadge(CombatState)"/> for the run walk without a child combat.</summary>
    private static bool HasBadge(RunState run) =>
        (_badgesKnown && !PatchSafety.IsPatchedByOthers(_runBadges) && run.BadgeModels.Count > 0) || NoBadge();

    /// <summary>The guard's answer when the state has no badge (or the getters can't be relied on): the game's walk.</summary>
    private static bool NoBadge()
    {
#if BENCH
        NoBadgeWalks++;
        // Test only (bdperf badges guard off): the filtered walk anyway, the positive control of EmptiedWalks.
        return BadgeGuardOff;
#else
        return false;
#endif
    }

#if BENCH
    /// <summary>Filtered walks the badge guard sent to the game's walk (the effect <c>HookSkip.NoBadge</c>; 0 in the game's runs).</summary>
    public static long NoBadgeWalks;

    /// <summary>
    /// Filtered walks that ended with nothing kept while the game's walk of the same state yields something (the effect
    /// <c>HookSkip.Emptied</c>: 0 expected; above 0 only with the guard off on a state without badges, its positive control).
    /// </summary>
    public static long EmptiedWalks;

    /// <summary>The hooks (row indexes) whose emptied walk was logged.</summary>
    private static readonly HashSet<int> _emptiedLogged = new();

    /// <summary>Test: the badge guard lets a state without badges walk filtered (bdperf badges guard off).</summary>
    internal static bool BadgeGuardOff;

    /// <summary>
    /// A filtered walk, checked once it ends: nothing kept, the game's walk (<paramref name="vanilla"/>, run as the vanilla
    /// reference: no optimization of this mod in it) asked for one listener. Only a walk run to its end is checked (a hook
    /// leaving its loop early kept one at least), so the reference runs only after an empty filtered walk.
    /// </summary>
    private static IEnumerable<AbstractModel> EndCheck(IEnumerable<AbstractModel> walk, Func<IEnumerable<AbstractModel>> vanilla, int index)
    {
        bool any = false;
        foreach (AbstractModel model in walk)
        {
            any = true;
            yield return model;
        }
        if (!any)
        {
            // A BaseLib walk filter pending on this thread stays for its own walk.
            PileFilter? pending = PendingRunFilter;
            PendingRunFilter = null;
            try
            {
                if (ListenerCache.RunVanilla(() => vanilla().Any()))
                {
                    EmptiedWalks++;
                    // Once per hook (the scenario's control names the hooks: badges reacting to three hooks only).
                    lock (_emptiedLogged)
                    {
                        if (_emptiedLogged.Add(index))
                        {
                            Log.Info($"[{ModEntry.Id}] {Name}: a filtered walk of Hook.{Hooks[index].Hook} kept no listener where the game's walk yields some (the choice context's model stack: A4)");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: the emptied walk check failed ({e.Message})");
            }
            finally
            {
                PendingRunFilter = pending;
            }
        }
    }
#endif

    /// <summary>A redirected walk call, filtered or not.</summary>
    [System.Diagnostics.Conditional("BENCH")]
    private static void CountWalk(int index)
    {
#if BENCH
        long[] counts = _walkCalls;
        if (counts.Length != Hooks.Length)
        {
            counts = _walkCalls = new long[Hooks.Length];
        }
        System.Threading.Interlocked.Increment(ref counts[index]);
#endif
    }

    /// <summary>A walk is about to run filtered (counted only where the filtered walk is really returned).</summary>
    [System.Diagnostics.Conditional("BENCH")]
    private static void CountFiltered(int index)
    {
#if BENCH
        FilteredWalks++;
        long[] counts = _filteredWalks;
        if (counts.Length != Hooks.Length)
        {
            counts = _filteredWalks = new long[Hooks.Length];
        }
        System.Threading.Interlocked.Increment(ref counts[index]);
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
#if BENCH
    /// <summary>BaseLib interface hooks' walks given an implementer filter (test builds: effect <c>HookSkip.BaseLib</c>).</summary>
    public static long BaseLibWalks;
#endif

    private static class BaseLibHooks
    {
        /// <summary>
        /// BaseLib 3.4.7 fingerprints: GetMaxHandSize (the (Player, int) overload, which holds the loop) and DowngradeHook are the
        /// same in 3.4.8, the two heal methods changed there (equivalents in EquivalentFingerprints).
        /// </summary>
        private static readonly (string Type, string Method, Type[]? Args, string Fingerprint, string Interface, WalkOf Walk)[] Targets =
        {
            ("BaseLib.Patches.Hooks.MaxHandSizePatch", "GetMaxHandSize", new[] { typeof(MegaCrit.Sts2.Core.Entities.Players.Player), typeof(int) },
                "1EE490EF3F75AF02", "BaseLib.Hooks.IMaxHandSizeModifier", WalkOf.PlayerRunState),
            ("BaseLib.Patches.Hooks.ModifyHealAmountPatches", "ModifyAdditive", null, "895AD94736FEED8E", "BaseLib.Hooks.IHealAmountModifier", WalkOf.RunStateArgument),
            ("BaseLib.Patches.Hooks.ModifyHealAmountPatches", "ModifyMultiplicative", null, "98B29C743F42C15F", "BaseLib.Hooks.IHealAmountModifier", WalkOf.RunStateArgument),
            // A postfix of CardModel.DowngradeInternal: one walk per downgraded card (Dampen downgrades a whole deck).
            ("BaseLib.Hooks.IAfterCardDowngraded+DowngradeHook", "Patch", null, "D268A4B256D8F60C", "BaseLib.Hooks.IAfterCardDowngraded", WalkOf.Unknown),
        };

        /// <summary>
        /// How the prefix knows the method's own walk is the game's <c>RunState</c> one (rule H of 2026-11-18): read from the
        /// fingerprinted bodies (BaseLib 3.4.8), where every call with that run state calls <c>IterateHookListeners</c> on it
        /// before any return.
        /// </summary>
        private enum WalkOf
        {
            /// <summary>Not told (DowngradeHook: its run state comes through the card's getters): no other mod's patch stands.</summary>
            Unknown,

            /// <summary>GetMaxHandSize(Player player, int): <c>player.RunState ?? NullRunState.Instance</c>, walked first.</summary>
            PlayerRunState,

            /// <summary>The heal modifiers (IRunState runState, ...): the argument, walked first.</summary>
            RunStateArgument,
        }

        private static readonly Dictionary<MethodBase, PileFilter> _filters = new();
        private static readonly Dictionary<MethodBase, WalkOf> _walks = new();

        /// <summary>
        /// The game's <c>Player.RunState</c> getter (<c>return _runState;</c>, fingerprinted per game version): read by the prefix
        /// only while nobody patches it, so the value is the one BaseLib's body reads, at no cost of its own.
        /// </summary>
        private static MethodInfo? _playerRunState;

        /// <summary>Player.get_RunState: the same IL in v0.107.1, v0.111.1 and v0.113.1.</summary>
        private const string PlayerRunStateFingerprint = "ABF676830ACF9774";
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
            try
            {
                MethodInfo getter = Reflect.Method(typeof(MegaCrit.Sts2.Core.Entities.Players.Player), "get_RunState")
                    ?? throw new MissingMethodException("Player.get_RunState");
                PatchSafety.RequireFingerprint(getter, PlayerRunStateFingerprint);
                _playerRunState = getter;
            }
            catch (Exception e)
            {
                // GetMaxHandSize's walk is then never told certain: its filter stays off (the game's walk).
                Log.Info($"[{ModEntry.Id}] {Name}: BaseLib GetMaxHandSize's run state can't be read ({e.Message}): its walk stays the game's");
            }
            foreach ((string typeName, string methodName, Type[]? args, string fingerprint, string interfaceName, WalkOf walk) in Targets)
            {
                string name = typeName + "." + methodName;
                try
                {
                    Type type = Reflect.TypeByName(typeName) ?? throw new TypeLoadException(typeName);
                    MethodInfo method = Reflect.Method(type, methodName, args) ?? throw new MissingMethodException(name);
                    PatchSafety.RequireFingerprint(method, fingerprint);
                    Type iface = Reflect.TypeByName(interfaceName) ?? throw new TypeLoadException(interfaceName);
                    _filters[method] = new PileFilter(iface.IsAssignableFrom, keepSubscribed: false);
                    _walks[method] = walk;
                    // Rule H: last among the prefixes, so every other mod's void prefix let stand runs before the filter is set.
                    PatchGuard.Patch(_harmony!, method, prefix: new HarmonyMethod(typeof(BaseLibHooks), nameof(Prefix)) { priority = Priority.Last },
                        finalizer: new HarmonyMethod(typeof(BaseLibHooks), nameof(Finalizer)));
                    PatchGuard.Watch(method);
                    // What Prefix asks about before taking the filter (display only).
                    if (walk == WalkOf.Unknown)
                    {
                        PatchSafety.Watch(Name, method);
                    }
                    else
                    {
                        PatchSafety.Watch(Name, method, PatchTolerance.SurroundingOnly);
                    }
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

        [OrderFreePrefix]
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

        /// <summary>
        /// The next RunState.IterateHookListeners call (this method's own walk) keeps only implementers. Rule H (2026-11-18): set
        /// only when that walk is certain, the game's <c>RunState</c> one (<see cref="RunStateWalk"/>): its call, the body's first
        /// (BaseLib 3.4.8, fingerprinted), takes the filter, so nothing after it (another mod's postfix starting a walk of its own)
        /// can find it pending, and on a <c>NullRunState</c> (which walks the combat, never taking it) none is set. This prefix runs
        /// last (<c>Priority.Last</c>), after every other mod's void prefix, so none of those can take it either, and with
        /// <c>__runOriginal</c> true (the body runs). Then other mods' patches stand as <see cref="PatchTolerance.SurroundingOnly"/>
        /// lets them (postfixes; void prefixes sorted before this one: one sorted after it, at <c>Priority.Last</c> patched later,
        /// still backs off): the walk yields the same listeners for the body's loop (the dropped ones aren't implementers: their
        /// iteration does nothing), so they see what they see without this mod. DowngradeHook (its run state read through the
        /// card's getters) keeps no tolerance.
        /// </summary>
        private static void Prefix(MethodBase __originalMethod, object? __0, bool __runOriginal)
        {
            if (!__runOriginal || !Enabled || !FastListeners.RunMirrored || ListenerCache.IsSuspended)
            {
                return;
            }
            WalkOf walk = _walks[__originalMethod];
            if (walk == WalkOf.Unknown ? PatchSafety.IsPatchedByOthers(__originalMethod)
                : PatchSafety.IsPatchedByOthers(__originalMethod, PatchTolerance.SurroundingOnly) || !RunStateWalk(walk, __0))
            {
                return;
            }
            FastListeners.PendingRunFilter = _filters[__originalMethod];
#if BENCH
            BaseLibWalks++;
#endif
        }

        /// <summary>
        /// Whether the method's walk is the game's <c>RunState.IterateHookListeners</c>: its run state (the player's, read through
        /// the game's getter only while nobody patches it, or the argument) is exactly a <c>RunState</c> (a subclass could walk
        /// otherwise; <c>NullRunState</c> walks the combat).
        /// </summary>
        private static bool RunStateWalk(WalkOf walk, object? first)
        {
            object? runState = walk switch
            {
                WalkOf.PlayerRunState => first is MegaCrit.Sts2.Core.Entities.Players.Player player && _playerRunState != null
                    && !PatchSafety.IsPatchedByOthers(_playerRunState) ? player.RunState : null,
                WalkOf.RunStateArgument => first,
                _ => null,
            };
            return runState != null && runState.GetType() == typeof(RunState);
        }

        /// <summary>Never leaks to a later walk (e.g. when the run state wasn't the game's RunState).</summary>
        private static void Finalizer(Exception? __exception)
        {
            FastListeners.PendingRunFilter = null;
        }
    }
}
