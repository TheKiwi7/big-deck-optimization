using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
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
/// resolved at load when the menu isn't there yet, the checks run after the main menu shows, spread over frames
/// (<see cref="PreflightOverFrames"/>), and again synchronously when the menu page opens or bdopt shows its status (answers
/// younger than <see cref="RecheckAfterMs"/> come from the runtime checks' cache).
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
                _watched.RemoveAll(w => w.Patch == patch && w.Method == method);
                _watchedKeys.RemoveWhere(k => k.Item1 == patch && k.Item2 == method);
            }
            foreach ((string Patch, MethodBase Method, PatchTolerance Tolerance) key in _conflicts.Keys)
            {
                if (key.Patch == patch && key.Method == method)
                {
                    _conflicts.TryRemove(key, out _);
                }
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, string Key), (string Text, string[] Mods)> _notes = new();

    /// <summary>
    /// Records a back-off that no method check sees: another mod's types or data keep part of <paramref name="patch"/> on the
    /// normal game (<paramref name="text"/>, English, for bdopt and the log; <paramref name="mods"/>: the mods responsible,
    /// empty when they can't be named). Logged once per change, like a back-off. Shown with the patch's back-offs.
    /// </summary>
    internal static void Note(string patch, string key, string text, string[] mods)
    {
        string[] sorted = mods.Where(m => !string.IsNullOrEmpty(m)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
        if (_notes.TryGetValue((patch, key), out var known) && known.Text == text && known.Mods.SequenceEqual(sorted))
        {
            return;
        }
        _notes[(patch, key)] = (text, sorted);
        Log.Info($"[{ModEntry.Id}] {patch}: {text}" + (sorted.Length > 0 ? $" ({string.Join(", ", sorted)}): normal game for that part" : ": normal game for that part"));
    }

    /// <summary>The note no longer holds (nothing logged).</summary>
    internal static void ClearNote(string patch, string key) => _notes.TryRemove((patch, key), out _);

    /// <summary>
    /// A recorded back-off (<see cref="Note"/> texts have no method, <see cref="Method"/> null) for bdopt why and bdperf conflicts.
    /// <see cref="What"/>: <c>Type.Method</c>, with the parameter types (and generic arguments) when another recorded method
    /// would print the same (overloads, generic instantiations, same-named types).
    /// </summary>
    public readonly record struct BackOff(string Patch, string What, string[] Mods, bool IsNote, MethodBase? Method = null);

    /// <summary>Every back-off and note recorded now, sorted by patch then by what.</summary>
    public static IReadOnlyList<BackOff> BackOffs()
    {
        var conflicts = _conflicts.ToArray();
        // Short names shared by different methods get their signatures.
        var ambiguous = new HashSet<string>(conflicts.Select(e => e.Key.Method).Distinct().GroupBy(ShortName).Where(g => g.Count() > 1).Select(g => g.Key));
        return conflicts.Select(entry => new BackOff(entry.Key.Patch,
                (ambiguous.Contains(ShortName(entry.Key.Method)) ? LongName(entry.Key.Method) : ShortName(entry.Key.Method))
                + (entry.Key.Tolerance == PatchTolerance.None ? "" : $" (beyond {ToleranceText(entry.Key.Tolerance)})"), entry.Value, false, entry.Key.Method))
            .Concat(_notes.Select(entry => new BackOff(entry.Key.Patch, entry.Value.Text, entry.Value.Mods, true)))
            .OrderBy(b => b.Patch, StringComparer.Ordinal).ThenBy(b => b.IsNote).ThenBy(b => b.What, StringComparer.Ordinal).ToList();
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
            .SelectMany(b => b.Mods.Select(mod => (Mod: mod, What: b.IsNote ? (object)("note: " + b.What) : b.Method!)))
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

    /// <summary>
    /// One step per check: every watched method (its source's methods each a step), then every note check. Each step catches
    /// its own exception (logged once): one failing entry doesn't stop the others. <paramref name="checks"/> counts them.
    /// <paramref name="resolved"/>: entries already resolved at load (<see cref="PreflightOverFrames"/>), the others resolved
    /// in their step.
    /// </summary>
    private static IEnumerable<bool> PreflightSteps(Counter checks, Dictionary<Watched, (MethodBase? Method, MethodInfo[]? Allowed)[]?>? resolved = null)
    {
        Watched[] watched;
        (string Patch, Action Check)[] notes;
        lock (_watched)
        {
            watched = _watched.ToArray();
            notes = _noteChecks.ToArray();
        }
        foreach (Watched entry in watched)
        {
            (MethodBase? Method, MethodInfo[]? Allowed)[]? pairs = resolved != null && resolved.TryGetValue(entry, out var known) ? known : Resolve(entry);
            if (pairs == null)
            {
                continue;
            }
            foreach ((MethodBase? method, MethodInfo[]? listed) in pairs)
            {
                if (method == null)
                {
                    continue;
                }
                // Nulls can't be in a list a runtime check passes (it would throw there): left out.
                MethodInfo[]? allowed = listed != null && Array.IndexOf(listed, null) >= 0 ? listed.Where(a => a != null).ToArray() : listed;
                try
                {
                    _ = entry.Tolerance != PatchTolerance.None ? CheckTolerant(method, allowed ?? Array.Empty<MethodInfo>(), entry.Tolerance, entry.Patch, out _)
                        : allowed == null ? CheckPatched(method, entry.Patch) : CheckPatched(method, allowed, entry.Patch);
                    checks.Value++;
                }
                catch (Exception e)
                {
                    PreflightFailed(entry.Patch, e);
                }
                yield return true;
            }
        }
        foreach ((string patch, Action check) in notes)
        {
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
    /// Checks every registered method and note now (the settings page opening, bdopt's status). Methods checked within the
    /// last <see cref="RecheckAfterMs"/> are answered from the runtime checks' cache. Does nothing before every patch applied
    /// (allowed lists are built once every mod has loaded) or when the mod started off. Never throws.
    /// </summary>
    internal static void Preflight()
    {
        try
        {
            if (!ModEntry.AllApplied || ModEntry.StartedOff)
            {
                return;
            }
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

    /// <summary>Time given to the startup preflight per frame.</summary>
    private const double PreflightBudgetMs = 3;

    /// <summary>
    /// The startup preflight, in two parts. At load (still before the main menu shows, during the game's long loading frames):
    /// every registered entry resolved (the patches' lazy allowed lists and sources: reflection on other mods' types, e.g. a
    /// mod's patch method looked up by name), in one go. Then once the main menu shows (or a run exists), the checks themselves
    /// (the Harmony lookups, the shape rules) and the note checks (which need every mod's own setup done: never before the
    /// menu), a few per frame within <see cref="PreflightBudgetMs"/>, so they add no pause to the menu; a run starting meanwhile
    /// gets the rest at once (its loading screen). When the menu already shows by then, everything goes to the frames. Logs
    /// both parts' times and their longest step once. Never throws (async void).
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
            var checks = new Counter();
            int frames = 0;
            double spent = 0;
            // The load part: only while the menu isn't there yet (its time adds to the game's loading, not to a menu frame).
            Dictionary<Watched, (MethodBase? Method, MethodInfo[]? Allowed)[]?>? resolved = null;
            double loadMs = 0, loadLongest = 0;
            if (tree != null && !MenuOrRun())
            {
                Watched[] watched;
                lock (_watched)
                {
                    watched = _watched.ToArray();
                }
                resolved = new Dictionary<Watched, (MethodBase? Method, MethodInfo[]? Allowed)[]?>(watched.Length);
                long loadStart = Stopwatch.GetTimestamp();
                foreach (Watched entry in watched)
                {
                    long step = Stopwatch.GetTimestamp();
                    resolved[entry] = Resolve(entry);
                    loadLongest = Math.Max(loadLongest, Stopwatch.GetElapsedTime(step).TotalMilliseconds);
                }
                loadMs = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
            }
            using IEnumerator<bool> steps = PreflightSteps(checks, resolved).GetEnumerator();
            // The main menu's first frames first: a frame or two after it shows.
            while (tree != null && !MenuOrRun())
            {
                await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
            }
            for (int wait = 0; tree != null && wait < 2; wait++)
            {
                await tree.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
            }
            long started = Stopwatch.GetTimestamp();
            bool more = true;
            // A step can't be split (a note's scan of every BaseLib-dependent assembly is one): the longest is logged, the frame
            // it may have stretched beyond the budget.
            double longest = 0;
            while (more)
            {
                double budget = tree == null || RunInProgress() ? double.MaxValue : PreflightBudgetMs;
                long start = Stopwatch.GetTimestamp();
                while (true)
                {
                    long step = Stopwatch.GetTimestamp();
                    more = steps.MoveNext();
                    longest = Math.Max(longest, Stopwatch.GetElapsedTime(step).TotalMilliseconds);
                    if (!more || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= budget)
                    {
                        break;
                    }
                }
                spent += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (more)
                {
                    await tree!.ToSignal(tree, Godot.SceneTree.SignalName.ProcessFrame);
                    frames++;
                }
            }
#if BENCH
            PreflightChecks += checks.Value;
            PreflightRuns++;
#endif
            int backOffs = _conflicts.Count, notes = _notes.Count;
            string load = resolved == null ? "at load nothing (the menu already showed)"
                : $"at load {resolved.Count} entries resolved in {loadMs:F1} ms, longest {loadLongest:F1} ms";
            Log.Info($"[{ModEntry.Id}] Preflight: {checks.Value} methods and notes checked; {load}; at the main menu {spent:F1} ms over {frames + 1} frames, "
                + $"longest step {longest:F1} ms (done {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms after the main menu): {backOffs} back-offs, {notes} notes"
                + (backOffs + notes > 0 ? $" ({string.Join(", ", BackOffsPerMod().Select(m => $"{m.Mod} {m.Count}"))}; bdopt why lists them)" : ""));
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
