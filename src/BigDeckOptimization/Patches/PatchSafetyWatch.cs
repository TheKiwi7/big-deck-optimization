using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Back-offs made visible before they happen. A patch's runtime <see cref="IsPatchedByOthers(MethodBase?, string)"/> checks
/// only record a back-off when they first run (often the first combat): each patch also registers here, when it applies, the
/// methods those checks pass (<see cref="Watch(string, MethodBase?, MethodInfo[]?)"/>), and <see cref="Preflight"/> checks
/// them all with the same logic, recorded under the same patch names, so the settings menu and bdopt show them from the main
/// menu on. Back-offs that don't come from a patch on a method (another mod's types or data turning a shortcut off) are
/// recorded as notes (<see cref="Note"/>), computed by checks registered with <see cref="WatchNote"/>.
/// Registering only stores what Apply already resolved (no Harmony lookup, no fingerprint); what the entries ask about is
/// resolved and checked during the game's loading when the menu isn't there yet (the notes after it shows), what is left
/// spread over the main menu's frames (<see cref="PreflightOverFrames"/>), the rest at once when the menu page opens or bdopt
/// shows its status before that ended, then again synchronously there (answers younger than <see cref="RecheckAfterMs"/> come
/// from the runtime checks' cache).
/// </summary>
public static partial class PatchSafety
{
    private sealed class Watched
    {
        public readonly string Patch;
        public readonly MethodBase? Method;
        public readonly MethodInfo[]? Allowed;
        public readonly Func<MethodInfo[]>? LazyAllowed;
        /// <summary>Methods known only later (resolved per type or per call): asked at each preflight.</summary>
        public readonly Func<IEnumerable<MethodBase?>>? Source;
        /// <summary>Methods known only later, each with its own allowed patches.</summary>
        public readonly Func<IEnumerable<(MethodBase? Method, MethodInfo[]? Allowed)>>? Pairs;
        /// <summary>The tolerance the runtime check passes (<see cref="PatchTolerance"/>).</summary>
        public readonly PatchTolerance Tolerance;
        /// <summary>
        /// The tolerance worked out as the runtime check works it out (HookSkip's hook methods: TranspiledOnly or SurroundingOnly
        /// from this mod's own patches there), asked at each preflight instead of <see cref="Tolerance"/>: the display never
        /// answers with another tolerance than the runtime.
        /// </summary>
        public Func<PatchTolerance>? ToleranceOf;
        /// <summary>
        /// Unregistered (<see cref="Unwatch"/>) after a preflight already took it: that preflight's later steps skip it, so it
        /// doesn't record again what Unwatch forgot. Main thread only (the preflight and the late patch batches).
        /// </summary>
        public bool Removed;

        public Watched(string patch, MethodBase? method, MethodInfo[]? allowed, Func<MethodInfo[]>? lazyAllowed, Func<IEnumerable<MethodBase?>>? source,
            Func<IEnumerable<(MethodBase? Method, MethodInfo[]? Allowed)>>? pairs = null, PatchTolerance tolerance = PatchTolerance.None)
        {
            Patch = patch;
            Method = method;
            Allowed = allowed;
            LazyAllowed = lazyAllowed;
            Source = source;
            Pairs = pairs;
            Tolerance = tolerance;
        }
    }

    private static readonly List<Watched> _watched = new();
    private static readonly HashSet<(string, MethodBase, object?, PatchTolerance)> _watchedKeys = new();
    private static readonly List<(string Patch, Action Check)> _noteChecks = new();

    /// <summary>
    /// Registers a method a runtime check of <paramref name="patch"/> (its source file name, e.g. "HookSkip") passes to
    /// <see cref="IsPatchedByOthers(MethodBase?, string)"/>, or to the overload with <paramref name="allowed"/> patches. Only
    /// stores it (once per patch, method and allowed set): called at Apply time, costs nothing measurable. Null: ignored.
    /// </summary>
    /// <remarks>
    /// Runs inside the patches' Apply (ModEntry.TryApply's try): it never throws, whatever it gets (a throw would take the
    /// patch away). Display only: the runtime checks decide as before, the preflight only shares their 2 s cache (the same
    /// computation, the same answer).
    /// </remarks>
    internal static void Watch(string patch, MethodBase? method, MethodInfo[]? allowed = null)
    {
        try
        {
            if (method == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                // The allowed list by reference: the patches pass the array their runtime check passes.
                if (_watchedKeys.Add((patch, method, allowed, PatchTolerance.None)))
                {
                    _watched.Add(new Watched(patch, method, allowed, null, null));
                }
            }
        }
        catch
        {
            // Display only: a method not registered shows its back-off when its runtime check runs.
        }
    }

    /// <summary>
    /// Undoes <see cref="Watch(string, MethodBase?, MethodInfo[]?)"/> (every overload registering this method directly) for a
    /// part that ended up vanilla after registering (HookSkip's hook whose transpiler failed), and forgets what the preflight
    /// recorded for it under that patch name: its back-off would change nothing. Never throws.
    /// </summary>
    internal static void Unwatch(string patch, MethodBase? method)
    {
        try
        {
            if (method == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                _watched.RemoveAll(w =>
                {
                    bool match = w.Patch == patch && w.Method == method;
                    w.Removed |= match;
                    return match;
                });
                _watchedKeys.RemoveWhere(k => k.Item1 == patch && k.Item2 == method);
            }
            bool forgot = false;
            foreach ((string Patch, MethodBase Method, PatchTolerance Tolerance) key in _conflicts.Keys)
            {
                if (key.Patch == patch && key.Method == method)
                {
                    forgot |= _conflicts.TryRemove(key, out _);
                }
            }
            if (forgot)
            {
                Config.Settings.NotifyStatesChanged();
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// <see cref="Watch(string, MethodBase?, MethodInfo[]?)"/> with allowed patches known only once the other mods are loaded
    /// (a list the patch builds lazily: asked at each preflight, which only runs once every mod has loaded). Never throws.
    /// </summary>
    internal static void Watch(string patch, MethodBase? method, Func<MethodInfo[]>? allowed)
    {
        try
        {
            if (method == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                if (_watchedKeys.Add((patch, method, allowed, PatchTolerance.None)))
                {
                    _watched.Add(new Watched(patch, method, null, allowed, null));
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// <see cref="Watch(string, MethodBase?, MethodInfo[]?)"/> for a runtime check passing a tolerance (and possibly allowed
    /// patches, eager or lazy): the preflight asks with the same tolerance. Never throws.
    /// </summary>
    internal static void Watch(string patch, MethodBase? method, PatchTolerance tolerance, MethodInfo[]? allowed = null, Func<MethodInfo[]>? lazyAllowed = null)
    {
        try
        {
            if (method == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                if (_watchedKeys.Add((patch, method, (object?)allowed ?? lazyAllowed, tolerance)))
                {
                    _watched.Add(new Watched(patch, method, allowed, lazyAllowed, null, null, tolerance));
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// <see cref="Watch(string, MethodBase?, PatchTolerance, MethodInfo[]?, Func{MethodInfo[]}?)"/> for a runtime check whose
    /// tolerance depends on the moment (<paramref name="tolerance"/>: the same function it calls): the preflight asks it at each
    /// check. Never throws.
    /// </summary>
    internal static void Watch(string patch, MethodBase? method, Func<PatchTolerance>? tolerance, Func<MethodInfo[]>? lazyAllowed)
    {
        try
        {
            if (method == null || patch == null || tolerance == null)
            {
                return;
            }
            lock (_watched)
            {
                if (_watchedKeys.Add((patch, method, (tolerance, lazyAllowed), PatchTolerance.None)))
                {
                    _watched.Add(new Watched(patch, method, null, lazyAllowed, null, null, PatchTolerance.None) { ToleranceOf = tolerance });
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>Several methods with one tolerance (each as <see cref="Watch(string, MethodBase?, PatchTolerance, MethodInfo[]?, Func{MethodInfo[]}?)"/>).</summary>
    internal static void Watch(string patch, IEnumerable<MethodBase?>? methods, PatchTolerance tolerance)
    {
        try
        {
            if (methods == null)
            {
                return;
            }
            foreach (MethodBase? method in methods)
            {
                Watch(patch, method, tolerance);
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>Several methods at once (each as <see cref="Watch(string, MethodBase?, MethodInfo[]?)"/>). Never throws.</summary>
    internal static void Watch(string patch, IEnumerable<MethodBase?>? methods, MethodInfo[]? allowed = null)
    {
        try
        {
            if (methods == null)
            {
                return;
            }
            foreach (MethodBase? method in methods)
            {
                Watch(patch, method, allowed);
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// Methods a runtime check passes that are known only later or change (per listener type, per subscriber): the source is
    /// asked at each preflight, inside its try/catch. It must be cheap and only read what the patch already resolved. Never
    /// throws (registered once per patch and source method and target).
    /// </summary>
    internal static void WatchAll(string patch, Func<IEnumerable<MethodBase?>>? methods, Func<MethodInfo[]>? allowed = null)
    {
        try
        {
            if (methods == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                if (_watchedKeys.Add((patch, methods.Method, methods.Target, PatchTolerance.None)))
                {
                    _watched.Add(new Watched(patch, null, null, allowed, methods));
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// <see cref="WatchAll"/> for methods each with its own allowed patches (null: none allowed), e.g. a chain whose
    /// methods allow different mods' patches, checked with <paramref name="tolerance"/>. Never throws.
    /// </summary>
    internal static void WatchPairs(string patch, Func<IEnumerable<(MethodBase? Method, MethodInfo[]? Allowed)>>? pairs, PatchTolerance tolerance = PatchTolerance.None)
    {
        try
        {
            if (pairs == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                if (_watchedKeys.Add((patch, pairs.Method, pairs.Target, tolerance)))
                {
                    _watched.Add(new Watched(patch, null, null, null, null, pairs, tolerance));
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// A check that records or clears a note (<see cref="Note"/>, <see cref="ClearNote"/>) of <paramref name="patch"/>: run at
    /// each preflight, never at startup before the main menu. Registered once per patch and check method. Never throws.
    /// </summary>
    internal static void WatchNote(string patch, Action? check)
    {
        try
        {
            if (check == null || patch == null)
            {
                return;
            }
            lock (_watched)
            {
                if (_watchedKeys.Add((patch, check.Method, check.Target, PatchTolerance.None)))
                {
                    _noteChecks.Add((patch, check));
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, string Key), (string Text, string[] Mods, bool Whole)> _notes = new();

    /// <summary>
    /// Records a back-off that no method check sees: another mod's types or data keep part of <paramref name="patch"/> on the
    /// normal game (<paramref name="text"/>, English, for bdopt and the log; <paramref name="mods"/>: the mods responsible,
    /// empty when they can't be named). Logged once per change, like a back-off. Shown with the patch's back-offs.
    /// <paramref name="whole"/>: what it says keeps all of that patch on the normal game (the menu's red title).
    /// </summary>
    internal static void Note(string patch, string key, string text, string[] mods, bool whole = false)
    {
        string[] sorted = mods.Where(m => !string.IsNullOrEmpty(m)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
        if (_notes.TryGetValue((patch, key), out var known) && known.Text == text && known.Mods.SequenceEqual(sorted) && known.Whole == whole)
        {
            return;
        }
        _notes[(patch, key)] = (text, sorted, whole);
        Log.Info($"[{ModEntry.Id}] {patch}: {text}" + (sorted.Length > 0 ? $" ({string.Join(", ", sorted)})" : "") + (whole ? ": normal game for all of it" : ": normal game for that part"));
        Config.Settings.NotifyStatesChanged();
    }

    /// <summary>The note no longer holds (nothing logged).</summary>
    internal static void ClearNote(string patch, string key)
    {
        if (_notes.TryRemove((patch, key), out _))
        {
            Config.Settings.NotifyStatesChanged();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, string Key), (string Text, string[] Mods)> _kept = new();

    /// <summary>
    /// Records what <paramref name="patch"/> keeps running next to other mods on a premise it states, rather than backing off
    /// (CardFlightLimit: other mods' code on a flying card's own visuals, taken as display only): <paramref name="text"/>,
    /// English, for bdopt why and the log; <paramref name="mods"/> the mods concerned. Not a back-off: the menu's colours don't
    /// change, its hover names the mods (<see cref="Config.Settings.Kept"/>). Logged once per change. Never throws.
    /// </summary>
    internal static void Kept(string patch, string key, string text, string[] mods)
    {
        try
        {
            string[] sorted = mods.Where(m => !string.IsNullOrEmpty(m)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
            if (_kept.TryGetValue((patch, key), out var known) && known.Text == text && known.Mods.SequenceEqual(sorted))
            {
                return;
            }
            _kept[(patch, key)] = (text, sorted);
            Log.Info($"[{ModEntry.Id}] {patch}: {text}" + (sorted.Length > 0 ? $" ({string.Join(", ", sorted)})" : "") + ": kept");
            Config.Settings.NotifyStatesChanged();
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>The kept note no longer holds (nothing logged).</summary>
    internal static void ClearKept(string patch, string key)
    {
        if (_kept.TryRemove((patch, key), out _))
        {
            Config.Settings.NotifyStatesChanged();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, string Key), (string Text, string[] Mods)> _exact = new();

    /// <summary>
    /// Records how <paramref name="patch"/> runs next to other mods' code where it needs none of its stated premises there, only
    /// its exact tier (ListenerCache.Walks: other mods' CombatStateChanged handlers get the game's walks at the scope's exact
    /// tier, never a reused output): <paramref name="text"/>,
    /// English, for bdopt why and the log; <paramref name="mods"/> the mods concerned. Neither a back-off nor a premise: no
    /// colour, nothing in the menu. Logged once per change. Never throws.
    /// </summary>
    internal static void Exact(string patch, string key, string text, string[] mods)
    {
        try
        {
            string[] sorted = mods.Where(m => !string.IsNullOrEmpty(m)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
            if (_exact.TryGetValue((patch, key), out var known) && known.Text == text && known.Mods.SequenceEqual(sorted))
            {
                return;
            }
            _exact[(patch, key)] = (text, sorted);
            Log.Info($"[{ModEntry.Id}] {patch}: {text}" + (sorted.Length > 0 ? $" ({string.Join(", ", sorted)})" : "") + ": exact, the normal game for that code");
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>The exact note no longer holds (nothing logged). Never throws.</summary>
    internal static void ClearExact(string patch, string key)
    {
        try
        {
            _exact.TryRemove((patch, key), out _);
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>Every exact note recorded now (<see cref="Exact"/>), as kept notes are, sorted by patch then text.</summary>
    public static IReadOnlyList<KeptNote> ExactNotes() =>
        _exact.Select(entry => new KeptNote(entry.Key.Patch, entry.Key.Key, entry.Value.Text, entry.Value.Mods))
            .OrderBy(k => k.Patch, StringComparer.Ordinal).ThenBy(k => k.Text, StringComparer.Ordinal).ToList();

    /// <summary>A kept note (<see cref="Kept"/>): the patch, its text and the mods it names.</summary>
    public readonly record struct KeptNote(string Patch, string Key, string Text, string[] Mods);

    /// <summary>Every kept note recorded now, sorted by patch then text.</summary>
    public static IReadOnlyList<KeptNote> KeptNotes() =>
        _kept.Select(entry => new KeptNote(entry.Key.Patch, entry.Key.Key, entry.Value.Text, entry.Value.Mods))
            .OrderBy(k => k.Patch, StringComparer.Ordinal).ThenBy(k => k.Text, StringComparer.Ordinal).ToList();

    /// <summary>The mods (display names, as back-off lines give them) with any patch on these methods.</summary>
    internal static string[] OtherModsPatching(IEnumerable<MethodBase?> methods) =>
        ModNames(methods.Where(m => m != null).Select(m => Harmony.GetPatchInfo(m!)).Where(info => info != null)
            .SelectMany(info => AllPatches(info!)).Where(p => !PatchGuard.IsOurs(p.owner)));

    /// <summary>Patches all of whose method back-offs keep the whole patch vanilla (<see cref="WholePatch"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _wholePatches = new(StringComparer.Ordinal);

    /// <summary>Methods whose back-off keeps the whole patch vanilla (<see cref="WholeMethods"/>), by patch.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, MethodBase Method), byte> _wholeMethods = new();

    /// <summary>
    /// Display only (the menu's red title, bdopt): a back-off from any method this patch checks keeps all of it on the normal
    /// game (one gate in front of everything it does), not just the part that method concerns. Never throws.
    /// </summary>
    internal static void WholePatch(string patch)
    {
        try
        {
            _wholePatches[patch] = 0;
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>As <see cref="WholePatch"/>, for some of the patch's checked methods only (the others keep only their part vanilla). Never throws.</summary>
    internal static void WholeMethods(string patch, IEnumerable<MethodBase?>? methods)
    {
        try
        {
            foreach (MethodBase? method in methods ?? Enumerable.Empty<MethodBase?>())
            {
                if (method != null)
                {
                    _wholeMethods[(patch, method)] = 0;
                }
            }
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>Methods known only later (resolved lazily, like <see cref="WatchAll"/>'s), registered whole per patch.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<IEnumerable<MethodBase?>>> _wholeLazy = new(StringComparer.Ordinal);

    /// <summary>As <see cref="WholeMethods(string, IEnumerable{MethodBase?})"/>, with the methods resolved when a back-off is recorded. Never throws.</summary>
    internal static void WholeMethods(string patch, Func<IEnumerable<MethodBase?>>? methods)
    {
        if (patch != null && methods != null)
        {
            _wholeLazy[patch] = methods;
        }
    }

    private static bool InWholeLazy(string patch, MethodBase method)
    {
        try
        {
            return _wholeLazy.TryGetValue(patch, out var methods) && methods().Contains(method);
        }
        catch
        {
            return false; // Display only.
        }
    }

#if BENCH
    /// <summary>
    /// The patches registered whole, and those with some methods registered whole ("Name (N methods)", "+ lazy" when it also has
    /// methods registered with a source, "Name (lazy methods)" with those only): bdperf states.
    /// </summary>
    internal static IEnumerable<string> WholeNames() => _wholePatches.Keys
        .Concat(_wholeMethods.Keys.Select(k => k.Patch).Concat(_wholeLazy.Keys).Distinct(StringComparer.Ordinal).Where(p => !_wholePatches.ContainsKey(p))
            .Select(p => (Patch: p, Fixed: _wholeMethods.Keys.Count(k => k.Patch == p), Lazy: _wholeLazy.ContainsKey(p)))
            .Select(w => w.Fixed == 0 ? $"{w.Patch} (lazy methods)" : $"{w.Patch} ({w.Fixed} methods{(w.Lazy ? " + lazy" : "")})"));
#endif

    private static bool IsWhole(string patch, MethodBase method) =>
        _wholePatches.ContainsKey(patch) || _wholeMethods.ContainsKey((patch, method)) || InWholeLazy(patch, method);

    /// <summary>
    /// A recorded back-off (<see cref="Note"/> texts have no method, <see cref="Method"/> null) for bdopt why and bdperf conflicts.
    /// <see cref="What"/>: <c>Type.Method</c>, with the parameter types (and generic arguments) when another recorded method
    /// would print the same (overloads, generic instantiations, same-named types). <see cref="Whole"/>: it keeps all of its
    /// patch on the normal game (<see cref="WholePatch"/>, <see cref="WholeMethods"/>, a whole note).
    /// </summary>
    public readonly record struct BackOff(string Patch, string What, string[] Mods, bool IsNote, MethodBase? Method = null, bool Whole = false);

    /// <summary>Every back-off and note recorded now, sorted by patch then by what.</summary>
    public static IReadOnlyList<BackOff> BackOffs()
    {
        var conflicts = _conflicts.ToArray();
        // Short names shared by different methods get their signatures (one method reached through two reflected types counts once).
        var ambiguous = new HashSet<string>(conflicts.Select(e => e.Key.Method).DistinctBy(MethodKey).GroupBy(ShortName).Where(g => g.Count() > 1).Select(g => g.Key));
        return conflicts.Select(entry => new BackOff(entry.Key.Patch,
                (ambiguous.Contains(ShortName(entry.Key.Method)) ? LongName(entry.Key.Method) : ShortName(entry.Key.Method))
                + (entry.Key.Tolerance == PatchTolerance.None ? "" : $" (beyond {ToleranceText(entry.Key.Tolerance)})"), entry.Value, false, entry.Key.Method,
                IsWhole(entry.Key.Patch, entry.Key.Method)))
            .Concat(_notes.Select(entry => new BackOff(entry.Key.Patch, entry.Value.Text, entry.Value.Mods, true, null, entry.Value.Whole)))
            .OrderBy(b => b.Patch, StringComparer.Ordinal).ThenBy(b => b.IsNote).ThenBy(b => b.What, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// One method whatever type it was reflected from (two <see cref="MethodBase"/> objects then, not equal): its handle and its
    /// declaring type's (generic instantiations stay apart). The object itself when it has no handle (a dynamic method).
    /// </summary>
    private static object MethodKey(MethodBase method)
    {
        try
        {
            return (method.MethodHandle.Value, method.DeclaringType?.TypeHandle.Value ?? IntPtr.Zero);
        }
        catch
        {
            return method;
        }
    }

    private static string ShortName(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    private static string LongName(MethodBase method)
    {
        try
        {
            string generic = method.IsGenericMethod ? "<" + string.Join(", ", method.GetGenericArguments().Select(t => t.Name)) + ">" : "";
            string type = method.DeclaringType == null ? "" : (method.DeclaringType.Namespace is { Length: > 0 } ns ? ns + "." : "") + method.DeclaringType.Name;
            return $"{type}.{method.Name}{generic}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
        }
        catch
        {
            return ShortName(method);
        }
    }

    /// <summary>
    /// Per mod, how many methods it keeps vanilla and notes name it, most first: a method several patches watch (the combat
    /// hook walk under HookFilter, HookSkip and CardSortKeys), or one checked with and without a tolerance, counts once; two
    /// methods with the same name (overloads, generic instantiations) count twice.
    /// <paramref name="includePatch"/>: only the back-offs of these patch names (those that run now).
    /// </summary>
    public static IReadOnlyList<(string Mod, int Count)> BackOffsPerMod(Func<string, bool>? includePatch = null) =>
        BackOffs().Where(b => includePatch == null || includePatch(b.Patch))
            .SelectMany(b => b.Mods.Select(mod => (Mod: mod, What: b.IsNote ? (object)("note: " + b.What) : MethodKey(b.Method!))))
            .Distinct().GroupBy(e => e.Mod, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count())).OrderByDescending(e => e.Item2).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Notes recorded for these patches without a mod name (shown as "other mods").</summary>
    public static bool HasUnnamedNotes(IEnumerable<string> patches)
    {
        var names = new HashSet<string>(patches);
        return _notes.Any(entry => names.Contains(entry.Key.Patch) && entry.Value.Mods.Length == 0);
    }

#if BENCH
    /// <summary>Method checks and note checks preflights ran (the effects check).</summary>
    public static long PreflightChecks { get; private set; }
    public static int PreflightRuns { get; private set; }
    /// <summary>Back-offs and notes recorded now.</summary>
    public static int RecordedBackOffs => _conflicts.Count + _notes.Count;
#endif

    private static bool _preflightFailedLogged;

    /// <summary>
    /// What one registered entry asks about: its methods with their allowed patches (a source's methods, a lazy allowed list
    /// built by the patch: reflection only, no Harmony lookup). Null when it failed (logged once).
    /// </summary>
    private static (MethodBase? Method, MethodInfo[]? Allowed)[]? Resolve(Watched entry)
    {
        try
        {
            if (entry.Pairs != null)
            {
                return entry.Pairs().ToArray();
            }
            MethodInfo[]? shared = entry.Allowed ?? entry.LazyAllowed?.Invoke();
            return (entry.Source != null ? entry.Source().ToArray() : new[] { entry.Method }).Select(m => (m, shared)).ToArray();
        }
        catch (Exception e)
        {
            PreflightFailed(entry.Patch, e);
            return null;
        }
    }

    /// <summary>A bound on the rounds of <see cref="PreflightRounds"/> (entries registered without end: the rest waits for the runtime checks).</summary>
    private const int PreflightMaxRounds = 64;

    /// <summary>The watched entries not in <paramref name="seen"/>, in rounds until a round finds none new (bounded).</summary>
    private static IEnumerable<bool> PreflightRounds(Counter checks, HashSet<Watched> seen)
    {
        for (int round = 0; round < PreflightMaxRounds; round++)
        {
            Watched[] watched;
            lock (_watched)
            {
                watched = _watched.Where(w => !seen.Contains(w)).ToArray();
            }
            if (watched.Length == 0)
            {
                yield break;
            }
            foreach (Watched entry in watched)
            {
                seen.Add(entry);
                if (entry.Removed)
                {
                    continue;
                }
                checks.Current = entry.Patch;
                // Its own step: a lazy allowed list or a source may take a few ms (reflection, fingerprints).
                (MethodBase? Method, MethodInfo[]? Allowed)[]? pairs = Resolve(entry);
                yield return true;
                if (pairs == null)
                {
                    continue;
                }
                foreach ((MethodBase? method, MethodInfo[]? listed) in pairs)
                {
                    // Unwatched since this preflight took it (HookSkip's hook left vanilla in a late batch): what Unwatch forgot isn't recorded again.
                    if (method == null || entry.Removed)
                    {
                        continue;
                    }
                    checks.Current = entry.Patch;
                    // Nulls can't be in a list a runtime check passes (it would throw there): left out.
                    MethodInfo[]? allowed = listed != null && Array.IndexOf(listed, null) >= 0 ? listed.Where(a => a != null).ToArray() : listed;
                    try
                    {
                        PatchTolerance tolerance = entry.ToleranceOf?.Invoke() ?? entry.Tolerance;
                        // Strict: a generic definition's member on each closed instantiation Harmony patches too, as the runtime's set
                        // checks ask it (full audit A-L2; nothing more for any other member).
                        _ = tolerance != PatchTolerance.None ? CheckTolerant(method, allowed ?? Array.Empty<MethodInfo>(), tolerance, entry.Patch, out _)
                            : allowed == null ? IsPatchedByOthersWithInstances(method, entry.Patch) : IsPatchedByOthersWithInstances(method, _ => allowed, entry.Patch);
                        checks.Value++;
                    }
                    catch (Exception e)
                    {
                        PreflightFailed(entry.Patch, e);
                    }
                    yield return true;
                }
            }
        }
    }

    /// <summary>
    /// One step per check: every watched entry's resolution, every watched method (its source's methods each a step), then
    /// every note check. Each step catches its own exception (logged once): one failing entry doesn't stop the others.
    /// <paramref name="checks"/> counts them; <paramref name="checks"/>' <c>Current</c> names the patch of the step under way
    /// (the startup log names the longest). Nothing is carried over between preflights: the startup's part (1) runs these same
    /// steps, and each later preflight (the menu, bdopt) resolves every entry again.
    /// </summary>
    private static IEnumerable<bool> PreflightSteps(Counter checks)
    {
        // Entries are read in rounds until none is new: the startup preflight's first steps run during the game's loading, and
        // the late batches (a mod's assembly loading: LimitlessReserialize, TypePlaqueSkip, ExtraScopes' BetterSpire2 part)
        // register theirs afterwards; the notes are read only once the method checks are done (below), for the same reason.
        var seen = new HashSet<Watched>(ReferenceEqualityComparer.Instance);
        foreach (bool step in PreflightRounds(checks, seen))
        {
            yield return step;
        }
        // A step doing nothing, so a caller can stop before the notes (the startup preflight's loading part: notes need every
        // mod's own setup done, never before the main menu).
        checks.InNotes = true;
        yield return true;
        // The startup's part (1) stops at the step above, during the loading, and part (2) resumes on the main menu's frames:
        // entries a late batch registered in between (a mod's assembly loading meanwhile) are read again here, before the notes.
        foreach (bool step in PreflightRounds(checks, seen))
        {
            yield return step;
        }
        (string Patch, Action Check)[] notes;
        lock (_watched)
        {
            notes = _noteChecks.ToArray();
        }
        foreach ((string patch, Action check) in notes)
        {
            checks.Current = patch + " (note)";
            try
            {
                check();
                checks.Value++;
            }
            catch (Exception e)
            {
                PreflightFailed(patch, e);
            }
            yield return true;
        }
    }

    private sealed class Counter
    {
        public int Value;
        /// <summary>The patch name of the step under way.</summary>
        public string? Current;
        /// <summary>The method checks are done, the note checks come next.</summary>
        public bool InNotes;
    }

    private static void PreflightFailed(string patch, Exception e)
    {
        if (!_preflightFailedLogged)
        {
            _preflightFailedLogged = true;
            Log.Warn($"[{ModEntry.Id}] Back-off preflight: a check of {patch} failed (that part shows its back-offs when it runs): {e.Message}");
        }
    }

    /// <summary>
    /// Checks every registered method and note now (the settings page opening, bdopt's status). The startup preflight's steps
    /// still left are run first, at once (<see cref="FinishStartup"/>: the page and bdopt never show a half-checked state, a
    /// one-off pause of tens of ms at most). Methods checked within the last <see cref="RecheckAfterMs"/> are answered from
    /// the runtime checks' cache. Does nothing before every patch applied (allowed lists are built once every mod has loaded)
    /// or when the mod started off. Never throws.
    /// </summary>
    internal static void Preflight()
    {
        try
        {
            if (!ModEntry.AllApplied || ModEntry.StartedOff)
            {
                return;
            }
            HarmonyWatch.Revalidate();
            FinishStartup();
            var checks = new Counter();
            foreach (bool _ in PreflightSteps(checks))
            {
            }
#if BENCH
            PreflightChecks += checks.Value;
            PreflightRuns++;
#endif
        }
        catch (Exception e)
        {
            PreflightFailed("all patches", e);
        }
    }

    /// <summary>Time given to the startup preflight per main menu frame (what is left after the loading part).</summary>
    private const double PreflightBudgetMs = 3;

    /// <summary>The startup preflight under way (main thread only): its steps, and the time the rest took when finished on demand.</summary>
    private sealed class Startup
    {
        public readonly Counter Checks = new();
        public IEnumerator<bool>? Steps;
        public double OnDemandMs;
        public int OnDemandSteps;
        public string? OnDemandBy;
    }

    private static Startup? _startup;

    /// <summary>
    /// Runs the startup preflight's remaining steps now (the settings page or bdopt opened before it finished over the menu's
    /// frames). Main thread (the menu, the console, the startup preflight's frames). Never throws.
    /// </summary>
    private static void FinishStartup([System.Runtime.CompilerServices.CallerMemberName] string by = "")
    {
        Startup? startup = _startup;
        if (startup?.Steps == null)
        {
            return;
        }
        long start = Stopwatch.GetTimestamp();
        try
        {
            while (startup.Steps.MoveNext())
            {
                startup.OnDemandSteps++;
            }
        }
        catch (Exception e)
        {
            PreflightFailed("startup (finished on demand)", e);
        }
        startup.Steps.Dispose();
        startup.Steps = null;
        startup.OnDemandMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        startup.OnDemandBy ??= by;
    }

    /// <summary>
    /// The startup preflight, in up to three parts, so the menu and bdopt show the back-offs before any combat and never a
    /// half-checked state (the owner's choice: a little more loading time rather than a settings page opened early showing
    /// less than it should). (1) While the game is still loading (the patches finished before the main menu showed): every
    /// registered entry resolved (the patches' lazy allowed lists and sources) and checked (the Harmony lookups, the shape
    /// rules), in one go, its time adding to the loading; the note checks wait for the menu (they need every mod's own setup
    /// done). (2) Once the main menu shows (or a run exists), whatever is left, a few steps per frame within
    /// <see cref="PreflightBudgetMs"/>, so they add no pause to a menu nobody uses yet; a run starting meanwhile gets the rest
    /// at once (its loading screen). (3) The settings page or bdopt opened before (2) finished: the rest at once
    /// (<see cref="FinishStartup"/>). Usually the menu shows first (the remaining patches then apply at once on its frame,
    /// ModEntry): part (1) is then empty. Logs each part's time and longest step (with its patch) once. Never throws (async void).
    /// </summary>
    internal static async void PreflightOverFrames()
    {
        try
        {
            if (ModEntry.StartedOff)
            {
                return;
            }
            Godot.SceneTree? tree = Godot.Engine.GetMainLoop() as Godot.SceneTree;
            var startup = new Startup();
            Counter checks = startup.Checks;
            startup.Steps = PreflightSteps(checks).GetEnumerator();
            _startup = startup;
            int frames = 0;
            double spent = 0;
            // Part (1): only while the menu isn't there yet (its time adds to the game's loading, not to a menu frame).
            double loadMs = 0, loadLongest = 0;
            int loadSteps = 0;
            string? loadLongestPatch = null;
            if (tree != null && !MenuOrRun())
            {
                long loadStart = Stopwatch.GetTimestamp();
                while (!checks.InNotes && startup.Steps != null)
                {
                    long step = Stopwatch.GetTimestamp();
                    if (!startup.Steps.MoveNext())
                    {
                        break;
                    }
                    loadSteps++;
                    double ms = Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                    if (ms > loadLongest)
                    {
                        loadLongest = ms;
                        loadLongestPatch = checks.Current;
                    }
                }
                loadMs = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
            }
            // The main menu's first frames first: a frame or two after it shows.
            while (tree != null && !MenuOrRun() && startup.Steps != null)
            {
                await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
            }
            for (int wait = 0; tree != null && wait < 2 && startup.Steps != null; wait++)
            {
                await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
            }
            long started = Stopwatch.GetTimestamp();
            // A step can't be split (a note's scan of every BaseLib-dependent assembly is one): the longest is logged, the frame
            // it may have stretched beyond the budget.
            double longest = 0;
            string? longestPatch = null;
            int menuSteps = 0;
            // Part (2); FinishStartup (part 3) may run the rest between two frames: Steps is then null.
            while (startup.Steps != null)
            {
                double budget = tree == null || RunInProgress() ? double.MaxValue : PreflightBudgetMs;
                long start = Stopwatch.GetTimestamp();
                bool more = true;
                while (true)
                {
                    long step = Stopwatch.GetTimestamp();
                    more = startup.Steps.MoveNext();
                    double ms = Stopwatch.GetElapsedTime(step).TotalMilliseconds;
                    if (more)
                    {
                        menuSteps++;
                    }
                    if (ms > longest)
                    {
                        longest = ms;
                        longestPatch = checks.Current;
                    }
                    if (!more || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= budget)
                    {
                        break;
                    }
                }
                spent += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (!more)
                {
                    startup.Steps.Dispose();
                    startup.Steps = null;
                    break;
                }
                await tree!.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
                frames++;
            }
#if BENCH
            PreflightChecks += checks.Value;
            PreflightRuns++;
#endif
            int backOffs = _conflicts.Count, notes = _notes.Count;
            string load = loadSteps == 0 ? "during loading nothing (the menu showed first)"
                : $"during loading {loadSteps} steps in {loadMs:F1} ms, longest {loadLongest:F1} ms ({loadLongestPatch})";
            string menu = menuSteps == 0 ? "at the main menu nothing"
                : $"at the main menu {menuSteps} steps, {spent:F1} ms over {frames + 1} frames, longest step {longest:F1} ms ({longestPatch})";
            string onDemand = startup.OnDemandSteps == 0 ? "" : $"; finished on demand ({startup.OnDemandBy}): {startup.OnDemandSteps} steps in {startup.OnDemandMs:F1} ms";
            Log.Info($"[{ModEntry.Id}] Preflight: {checks.Value} methods and notes checked; {load}; {menu}{onDemand} "
                + $"(done {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms after the main menu): {backOffs} back-offs, {notes} notes"
                + (backOffs + notes > 0 ? $" ({string.Join(", ", BackOffsPerMod().Select(m => $"{m.Mod} {m.Count}"))}; bdopt why lists them)" : ""));
            // The settings page may be open: its colours and texts follow what the preflight found.
            Config.Settings.NotifyStatesChanged();
        }
        catch (Exception e)
        {
            PreflightFailed("startup", e);
        }
    }

    private static bool MenuOrRun()
    {
        try
        {
            return MegaCrit.Sts2.Core.Nodes.NGame.Instance?.MainMenu != null || RunInProgress();
        }
        catch
        {
            return true;
        }
    }

    private static bool RunInProgress()
    {
        try
        {
            return MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The names of the mods defining these assemblies (unknown ones left out), distinct.</summary>
    internal static string[] ModNamesOf(IEnumerable<Assembly?> assemblies) =>
        assemblies.Distinct().Select(ModNameOf).Where(name => name != null).Distinct().ToArray()!;

    /// <summary>
    /// The assembly a delegate's code comes from: the method itself, or, for a wrapper of a library (BaseLib's closure around
    /// a mod's lambda), the first delegate the wrapper holds that comes from another assembly.
    /// </summary>
    internal static Assembly? DelegateAssembly(Delegate? del, Assembly? wrapperAssembly = null)
    {
        if (del == null)
        {
            return null;
        }
        Assembly? own = del.Method.DeclaringType?.Assembly;
        if (own != wrapperAssembly || del.Target == null)
        {
            return own;
        }
        foreach (FieldInfo field in del.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(del.Target) is Delegate inner && inner.Method.DeclaringType?.Assembly is { } assembly && assembly != wrapperAssembly)
            {
                return assembly;
            }
        }
        return own;
    }
}
