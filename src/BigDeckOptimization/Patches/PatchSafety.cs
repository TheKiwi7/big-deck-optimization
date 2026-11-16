using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

/// <summary>Thrown by an Apply method when the patch doesn't apply here (optional mod absent, mirrored code changed).</summary>
public class PatchSkippedException(string reason) : Exception(reason);

/// <summary>
/// A method of a patch class only one part of the patch runs: its load check is that part's
/// (<see cref="PatchSafety.RequirePartResolvable"/>), so a game member it needs going away only takes that part away.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PatchPartAttribute(string part) : Attribute
{
    public string Part { get; } = part;
}

public static partial class PatchSafety
{
    private static readonly HashSet<string> _faults = new();

    /// <summary>
    /// Records that a patch turned itself off after an error: settings (bdopt, the settings menu) never turn it back on this
    /// session.
    /// </summary>
    public static void Fault(string name)
    {
        lock (_faults)
        {
            _faults.Add(name);
        }
    }

    public static bool IsFaulted(string name)
    {
        lock (_faults)
        {
            return _faults.Contains(name);
        }
    }

    /// <summary>
    /// Whether code with fingerprint <paramref name="actual"/> is what a patch expecting <paramref name="expected"/>
    /// relies on: the same code, or another game version's code checked to behave the same
    /// (<c>EquivalentFingerprints.cs</c>), so one DLL keeps the patch on both versions.
    /// </summary>
    public static bool FingerprintMatches(string actual, string expected) =>
        actual == expected || (EquivalentFingerprints.TryGetValue(expected, out string[]? equivalent) && equivalent.Contains(actual));

    /// <summary>
    /// Throws <see cref="PatchSkippedException"/> unless <paramref name="method"/> still has the code a patch mirrors
    /// (see <see cref="FingerprintMatches"/>).
    /// </summary>
    public static void RequireFingerprint(MethodBase method, string expected)
    {
        string actual = MethodFingerprint.Of(method);
        if (!FingerprintMatches(actual, expected))
        {
            throw new PatchSkippedException(
                $"{method.DeclaringType?.Name}.{method.Name} changed (fingerprint {actual}, expected {expected}): left vanilla");
        }
    }

    /// <summary>
    /// Throws <see cref="PatchSkippedException"/> when code other than <paramref name="known"/> may change a private field a
    /// patch tracks through the methods it watches (e.g. a new game method changing <c>CardPile._cards</c>, which no
    /// fingerprint would catch). Writers per <see cref="MethodFingerprint.FieldUsers"/>: assignments, mutating calls, the
    /// collection passed to a method taking a mutable collection; new readers don't matter.
    /// </summary>
    public static void RequireFieldWriters(Type type, string field, params string[] known)
    {
#if BENCH
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        string[] unknown = MethodFingerprint.FieldUsers(type, field, writersOnly: true).Where(user => !known.Contains(user)).ToArray();
#if BENCH
        MethodFingerprint.LoadTicks[1] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
#endif
        if (unknown.Length > 0)
        {
            throw new PatchSkippedException($"{type.Name}.{field} may now also be changed by {string.Join(", ", unknown)} (game update?): left vanilla");
        }
    }

    /// <summary>The code that changes <c>CardPile._cards</c> (game v0.107.1 to v0.113.1): the constructor and its fingerprinted list methods.</summary>
    public static readonly string[] CardListWriters =
        { ".ctor", "AddInternal", "Clear", "MoveToBottomInternal", "MoveToTopInternal", "RandomizeOrderInternal", "RemoveInternal" };

    /// <summary>The code that changes <c>Player._relics</c> / <c>Player._potionSlots</c> (ListenerCache invalidates on these or their callers).</summary>
    public static readonly string[] RelicListWriters = { ".ctor", "AddRelicInternal", "RemoveRelicInternal" };
    public static readonly string[] PotionSlotWriters = { ".ctor", "AddPotionInternal", "RemovePotionInternal", "SetMaxPotionCountInternal" };

    /// <summary>
    /// Throws <see cref="PatchSkippedException"/> when the patch class (with its nested types: lambdas, state machines)
    /// uses a game member or type this game version doesn't have, e.g. a method whose signature an update changed, or
    /// doesn't compile against it. Else the patch would apply and the JIT would only fail on a prefix's first call,
    /// before its Enabled check and its try/catch run: the patched game method would then throw on every call, even with
    /// the patch turned off. Calls into other classes of this mod fail at the call instead, inside the caller's
    /// try/catch (and <see cref="PatchGuard"/> removes a patch whose code throws into the game).
    /// </summary>
    public static void RequireResolvable(Type patchClass)
    {
#if BENCH
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            CheckResolvable(patchClass);
        }
        finally
        {
            MethodFingerprint.LoadTicks[2] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
#else
        CheckResolvable(patchClass);
#endif
    }

    private static void CheckResolvable(Type patchClass)
    {
        while (patchClass.DeclaringType != null)
        {
            patchClass = patchClass.DeclaringType;
        }
        MethodBase? current = null;
        try
        {
            Dictionary<MethodBase, string> partCode = PartCode(patchClass);
            foreach (MethodBase method in DeclaredMethods(patchClass))
            {
                if (method.IsDefined(typeof(PatchPartAttribute), false) || partCode.ContainsKey(method))
                {
                    continue;
                }
                current = method;
                // Resolving each member throws when it is missing; the JIT would also refuse a member that became
                // inaccessible. (Compiling the methods here instead, RuntimeHelpers.PrepareMethod, made turns ~2% slower:
                // methods compiled that way skip tiered compilation.)
                foreach (MemberInfo member in MethodFingerprint.UsedMembers(method))
                {
                    if (!Accessible(member, patchClass.Assembly, method.DeclaringType))
                    {
                        throw new MemberAccessException($"{member.DeclaringType?.Name}.{member.Name} is not accessible");
                    }
                }
            }
        }
        catch (Exception e)
        {
            string where = current == null ? patchClass.Name : $"{current.DeclaringType?.Name}.{current.Name}";
            throw new PatchSkippedException($"{where} uses something this game version doesn't have (game update?), left vanilla: {e.Message}");
        }
    }

    /// <summary>
    /// <see cref="RequireResolvable"/> for the methods marked as this part of a patch class, before that part is patched.
    /// </summary>
    public static void RequirePartResolvable(Type patchClass, string part)
    {
        while (patchClass.DeclaringType != null)
        {
            patchClass = patchClass.DeclaringType;
        }
        Dictionary<MethodBase, string> partCode;
        try
        {
            partCode = PartCode(patchClass);
        }
        catch (Exception e)
        {
            throw new PatchSkippedException($"{patchClass.Name}'s part {part} couldn't be checked, left vanilla: {e.Message}");
        }
        foreach (MethodBase method in DeclaredMethods(patchClass))
        {
            if ((method.GetCustomAttribute<PatchPartAttribute>()?.Part ?? partCode.GetValueOrDefault(method)) != part)
            {
                continue;
            }
            try
            {
                foreach (MemberInfo member in MethodFingerprint.UsedMembers(method))
                {
                    if (!Accessible(member, patchClass.Assembly, method.DeclaringType))
                    {
                        throw new MemberAccessException($"{member.DeclaringType?.Name}.{member.Name} is not accessible");
                    }
                }
            }
            catch (Exception e)
            {
                throw new PatchSkippedException($"{method.DeclaringType?.Name}.{method.Name} uses something this game version doesn't have (game update?), left vanilla: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The compiler-generated code of each <see cref="PatchPartAttribute"/> method (its lambdas, local functions, state
    /// machines: nested members that can't carry the attribute) -> that part. They are that part's code, checked with it:
    /// checked with the whole class, another game version's member they use would skip the whole patch instead of the part.
    /// Generated code also used by a method outside the part (or by two parts) stays with the whole class.
    /// </summary>
    private static Dictionary<MethodBase, string> PartCode(Type outermost)
    {
        lock (_partCode)
        {
            if (!_partCode.TryGetValue(outermost, out Dictionary<MethodBase, string>? known))
            {
                _partCode[outermost] = known = FindPartCode(outermost);
            }
            return known;
        }
    }

    /// <summary>Per patch class: the class and each of its parts are checked once each, at load.</summary>
    private static readonly Dictionary<Type, Dictionary<MethodBase, string>> _partCode = new();

    private static Dictionary<MethodBase, string> FindPartCode(Type outermost) =>
        MethodFingerprint.PartCode(outermost, method => method.GetCustomAttribute<PatchPartAttribute>()?.Part);

    /// <summary>See <see cref="MethodFingerprint.Accessible"/> (shared with tools/compat).</summary>
    private static bool Accessible(MemberInfo member, Assembly own, Type? user) => MethodFingerprint.Accessible(member, own, user);

    private static IEnumerable<MethodBase> DeclaredMethods(Type type) => MethodFingerprint.DeclaredMethods(type);

    /// <summary>
    /// True when a mod other than us has Harmony patches on the method. Our replacements mirror vanilla bodies, so
    /// they would silently drop another mod's transpiler or double-apply its postfix: we back off instead. Each
    /// back-off is recorded for the calling patch (its source file name, e.g. HookFilter) with the other mods' names,
    /// so the settings menu, bdopt and the log can say which mod keeps that part of an optimization vanilla.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPatchedByOthers(MethodBase? method, [CallerFilePath] string caller = "") => CheckPatched(method, caller);

    /// <summary>
    /// The check behind <see cref="IsPatchedByOthers(MethodBase?, string)"/>: <paramref name="caller"/> is a call site's source
    /// path (its file name is the patch name) or a patch name itself (<see cref="Preflight"/>), under which a back-off is recorded.
    /// </summary>
    private static bool CheckPatched(MethodBase? method, string caller)
    {
        if (method == null)
        {
            return true;
        }
        // Harmony.GetPatchInfo is costly and this runs per hook call (hundreds of thousands of times when cloning
        // thousands of cards): results are reused for a short while, read without a lock, so a mod patching later is
        // still seen within RecheckAfterMs.
        long now = Environment.TickCount64;
        if (_patchedByOthers.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            if (cached.Patched || _anyConflict)
            {
                // Another patch may have filled the cache: this one's back-off (or its end) is recorded too (menu, bdopt).
                RecordOnce(caller, method, cached.Mods);
            }
#if BENCH
            if (cached.Patched)
            {
                CountRuntime(caller, method, PatchTolerance.None, true);
            }
#endif
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        string[] mods = ModNames(info == null ? Enumerable.Empty<Patch>()
            : AllPatches(info).Where(p => !PatchGuard.IsOurs(p.owner)));
        if (mods.Length == 0)
        {
            // No other mod's patch at all: no patch backs off from it any more, whichever one filled the cache.
            Forget(method);
        }
        RecordOnce(caller, method, mods);
        _patchedByOthers[method] = (mods.Length > 0, now, mods);
#if BENCH
        // The runtime's own back-offs (the preflight's aren't counted): what the positive controls assert (runtimebackoff).
        if (mods.Length > 0)
        {
            CountRuntime(caller, method, PatchTolerance.None, true);
        }
#endif
        return mods.Length > 0;
    }

    /// <summary>
    /// Like <see cref="IsPatchedByOthers(MethodBase?, string)"/>, except for these patch methods of other mods (postfixes
    /// whose effect the caller reproduces, transpilers known not to touch what it changes; checked by fingerprint).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPatchedByOthers(MethodBase? method, MethodInfo[] allowedPatches, [CallerFilePath] string caller = "") =>
        CheckPatched(method, allowedPatches, caller);

    private static bool CheckPatched(MethodBase? method, MethodInfo[] allowedPatches, string caller)
    {
        if (method == null)
        {
            return true;
        }
        long now = Environment.TickCount64;
        // Keyed by the allowed set too: two patches may allow different patches on the same method.
        long allowedKey = 0;
        foreach (MethodInfo allowed in allowedPatches)
        {
            allowedKey += allowed.MethodHandle.Value.ToInt64() * 31 + 7;
        }
        if (_patchedBeyondAllowed.TryGetValue((method, allowedKey), out var cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            if (cached.Patched || _anyConflict)
            {
                RecordOnce(caller, method, cached.Mods);
            }
#if BENCH
            if (cached.Patched)
            {
                CountRuntime(caller, method, PatchTolerance.None, true);
            }
#endif
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        string[] mods = ModNames(info == null ? Enumerable.Empty<Patch>()
            : AllPatches(info)
                .Where(p => !PatchGuard.IsOurs(p.owner) && !(PatchMethodOf(p) is { } patchMethod && allowedPatches.Any(a => a.MethodHandle == patchMethod.MethodHandle))));
        RecordOnce(caller, method, mods);
        _patchedBeyondAllowed[(method, allowedKey)] = (mods.Length > 0, now, mods);
#if BENCH
        if (mods.Length > 0)
        {
            CountRuntime(caller, method, PatchTolerance.None, true);
        }
#endif
        return mods.Length > 0;
    }

    /// <summary>
    /// Every patch on the method, Harmony 2.4's inner prefixes and postfixes included (they patch the calls the method makes:
    /// code a mirror of the method would skip as surely as a prefix).
    /// </summary>
    private static IEnumerable<Patch> AllPatches(HarmonyLib.Patches info) =>
        info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.InnerPrefixes).Concat(info.InnerPostfixes);

    /// <summary>Other mods' patches on a method a patch backs off from, keyed by (patch name, method).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, MethodBase Method, PatchTolerance Tolerance), string[]> _conflicts = new();

    /// <summary>
    /// The mods last recorded per call site (its source path) and method. Several patches can check the same method within
    /// <see cref="RecheckAfterMs"/>: each records its back-off from the shared cached answer, once per answer (a new array at
    /// each check), not at every hook call.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Caller, MethodBase Method, PatchTolerance Tolerance), string[]> _recordedFrom =
        new(new CallSiteComparer());

    /// <summary>Some back-off was recorded this session: only then can a cached "not patched" end one (hot path otherwise untouched).</summary>
    private static bool _anyConflict;

    /// <summary>
    /// The caller path is a [CallerFilePath] literal, the same interned string at every call from a file: compared by
    /// reference (cheap per hook call). Another string with the same text only means one more <see cref="Record"/>.
    /// </summary>
    private sealed class CallSiteComparer : IEqualityComparer<(string Caller, MethodBase Method, PatchTolerance Tolerance)>
    {
        public bool Equals((string Caller, MethodBase Method, PatchTolerance Tolerance) a, (string Caller, MethodBase Method, PatchTolerance Tolerance) b) =>
            ReferenceEquals(a.Caller, b.Caller) && a.Method == b.Method && a.Tolerance == b.Tolerance;

        public int GetHashCode((string Caller, MethodBase Method, PatchTolerance Tolerance) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Caller), key.Method, key.Tolerance);
    }

    private static string[] ModNames(IEnumerable<Patch> others) =>
        others.Select(ModName).Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Records a check's answer under its call site. Keyed by the tolerance too: one patch may check a method strictly for one
    /// part and with a tolerance for another, two answers that must not undo each other at every call.
    /// </summary>
    private static void RecordOnce(string caller, MethodBase method, string[] mods, PatchTolerance tolerance = PatchTolerance.None)
    {
        if (_recordedFrom.TryGetValue((caller, method, tolerance), out string[]? recorded) && ReferenceEquals(recorded, mods))
        {
            return;
        }
        _recordedFrom[(caller, method, tolerance)] = mods;
        Record(caller, method, mods, tolerance);
    }

    /// <summary>
    /// Drops every patch's recorded back-off from this method (no other mod patches it any more). <see cref="_recordedFrom"/>
    /// is kept: a cached answer from before isn't recorded again, the next one (a new array) is.
    /// </summary>
    private static void Forget(MethodBase method)
    {
        if (_conflicts.IsEmpty)
        {
            return;
        }
        foreach ((string Patch, MethodBase Method, PatchTolerance Tolerance) key in _conflicts.Keys)
        {
            if (key.Method == method)
            {
                _conflicts.TryRemove(key, out _);
            }
        }
    }

    private static void Record(string caller, MethodBase method, string[] mods, PatchTolerance tolerance = PatchTolerance.None)
    {
        string patch = PatchName(caller);
        if (mods.Length == 0)
        {
            _conflicts.TryRemove((patch, method, tolerance), out _);
            return;
        }
        _anyConflict = true;
        if (!_conflicts.TryGetValue((patch, method, tolerance), out string[]? known) || !known.SequenceEqual(mods))
        {
            _conflicts[(patch, method, tolerance)] = mods;
            MegaCrit.Sts2.Core.Logging.Log.Info($"[{ModEntry.Id}] {patch}: {method.DeclaringType?.Name}.{method.Name} is also patched by {string.Join(", ", mods)}"
                + (tolerance == PatchTolerance.None ? "" : $" ({ToleranceText(tolerance)} not enough)") + ": normal game for that part");
        }
    }

    /// <summary>
    /// The patch name a call site records under: a [CallerFilePath] source path gives its file name (e.g. HookFilter), a patch
    /// name (<see cref="Preflight"/>, <see cref="Watch(string, MethodBase?, MethodInfo[]?)"/>) is used as is.
    /// </summary>
    private static string PatchName(string caller) =>
        caller.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileNameWithoutExtension(caller.Replace('\\', '/')) : caller;

    /// <summary>The loaded mod whose assembly holds the patch method (its display name), else the Harmony id.</summary>
    private static string ModName(Patch patch)
    {
        Assembly? assembly = PatchMethodOf(patch)?.DeclaringType?.Assembly;
        return assembly != null && assembly != typeof(PatchSafety).Assembly && ModNameOf(assembly) is { } name ? name : patch.owner;
    }

    /// <summary>
    /// The display name of the loaded mod holding this assembly (manifest name, else id), or null when no mod holds it (the
    /// game, the runtime, dynamic assemblies) or it is this mod's own.
    /// </summary>
    internal static string? ModNameOf(Assembly? assembly)
    {
        if (assembly == null || assembly == typeof(PatchSafety).Assembly || assembly.IsDynamic)
        {
            return null;
        }
        if (_modNames.TryGetValue(assembly, out string? known))
        {
            return known;
        }
        foreach (MegaCrit.Sts2.Core.Modding.Mod mod in MegaCrit.Sts2.Core.Modding.ModManager.Mods)
        {
            if (ModAssemblies(mod).Contains(assembly))
            {
                if ((mod.manifest?.name ?? mod.manifest?.id) is { } name)
                {
                    // A mod's assemblies and manifest don't change once loaded. Not kept: an assembly no mod holds yet.
                    _modNames[assembly] = name;
                    return name;
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>Mod display name per assembly holding a patch method (<see cref="ModName"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, string> _modNames = new();

    // Harmony 2.4's Patch keeps its method as module GUID and token, and Harmony.GetPatchInfo gives new Patch objects at each
    // call: reading PatchMethod there searches every loaded assembly's modules again (AccessTools.GetMethodByModuleAndToken,
    // a Parallel.ForEach over all of them), at each recheck of each method another mod patches. Read by reflection: without
    // these fields (another Harmony), PatchMethod as before.
    private static readonly FieldInfo? _patchToken = AccessTools.Field(typeof(Patch), "methodToken");
    private static readonly FieldInfo? _patchModule = AccessTools.Field(typeof(Patch), "moduleGUID");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Module, int Token), MethodInfo> _patchMethods = new();

    /// <summary>
    /// A patch's method, resolved once per module and token (a module's methods don't change while it is loaded); the same
    /// as <c>patch.PatchMethod</c>, which is also what is done when Harmony gave none (not kept: its assembly may load later).
    /// </summary>
    private static MethodInfo? PatchMethodOf(Patch patch)
    {
        if (_patchToken?.GetValue(patch) is not int token || _patchModule?.GetValue(patch) is not string module)
        {
            return patch.PatchMethod;
        }
        if (_patchMethods.TryGetValue((module, token), out MethodInfo? known))
        {
            return known;
        }
        MethodInfo? method = patch.PatchMethod;
        if (method != null)
        {
            _patchMethods[(module, token)] = method;
        }
        return method;
    }

    // Game v0.107: Mod.assembly; v0.111 (beta): Mod.assemblies. Read by reflection so both work.
    private static readonly FieldInfo? _modAssembly = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Modding.Mod), "assembly");
    private static readonly FieldInfo? _modAssemblies = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Modding.Mod), "assemblies");

    private static IEnumerable<Assembly> ModAssemblies(MegaCrit.Sts2.Core.Modding.Mod mod) =>
        _modAssembly?.GetValue(mod) is Assembly single ? new[] { single }
        : _modAssemblies?.GetValue(mod) as IEnumerable<Assembly> ?? Enumerable.Empty<Assembly>();

    /// <summary>
    /// The mods other patches come from, for the methods these patches currently back off from, and the mods their notes
    /// name (<see cref="Note"/>).
    /// </summary>
    public static IReadOnlyList<string> ConflictingMods(IEnumerable<string> patches)
    {
        var names = new HashSet<string>(patches);
        return _conflicts.Where(entry => names.Contains(entry.Key.Patch)).SelectMany(entry => entry.Value)
            .Concat(_notes.Where(entry => names.Contains(entry.Key.Patch)).SelectMany(entry => entry.Value.Mods))
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const long RecheckAfterMs = 2000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (bool Patched, long CheckedAt, string[] Mods)> _patchedByOthers = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(MethodBase, long), (bool Patched, long CheckedAt, string[] Mods)> _patchedBeyondAllowed = new();
}
