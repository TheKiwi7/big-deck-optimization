using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

/// <summary>Thrown by an Apply method when the patch doesn't apply here (optional mod absent, mirrored code changed).</summary>
public class PatchSkippedException(string reason) : Exception(reason);

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
        string[] unknown = MethodFingerprint.FieldUsers(type, field, writersOnly: true).Where(user => !known.Contains(user)).ToArray();
        if (unknown.Length > 0)
        {
            throw new PatchSkippedException($"{type.Name}.{field} may now also be changed by {string.Join(", ", unknown)} (game update?): left vanilla");
        }
    }

    /// <summary>The code that changes <c>CardPile._cards</c> (game v0.107.1 and v0.111): the constructor and its fingerprinted list methods.</summary>
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
        while (patchClass.DeclaringType != null)
        {
            patchClass = patchClass.DeclaringType;
        }
        MethodBase? current = null;
        try
        {
            foreach (MethodBase method in DeclaredMethods(patchClass))
            {
                current = method;
                // Resolving each member throws when it is missing; the JIT would also refuse a member that became
                // inaccessible. (Compiling the methods here instead, RuntimeHelpers.PrepareMethod, made turns ~2% slower:
                // methods compiled that way skip tiered compilation.)
                foreach (MemberInfo member in MethodFingerprint.UsedMembers(method))
                {
                    if (!Accessible(member, patchClass.Assembly))
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
    /// Whether code of this mod may use the member directly: its own members, or public ones (protected for overrides
    /// and base calls) of public types. This mod reaches non-public game members through reflection only.
    /// </summary>
    private static bool Accessible(MemberInfo member, Assembly own)
    {
        Type? owner = member as Type ?? member.DeclaringType;
        if (owner == null || owner.Assembly == own || owner.IsGenericParameter)
        {
            return true;
        }
        bool visible = member is Type type ? IsVisible(type, own) : IsVisible(owner, own);
        return visible && member switch
        {
            MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
            FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
            _ => true,
        };
    }

    /// <summary>Public all the way out (nested types included); array, pointer and generic arguments checked too.</summary>
    private static bool IsVisible(Type type, Assembly own)
    {
        if (type.HasElementType)
        {
            return IsVisible(type.GetElementType()!, own);
        }
        if (type.IsGenericParameter || type.Assembly == own)
        {
            return true;
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition && !type.GetGenericArguments().All(t => IsVisible(t, own)))
        {
            return false;
        }
        Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.IsNested ? (definition.IsNestedPublic || definition.IsNestedFamily || definition.IsNestedFamORAssem) && IsVisible(definition.DeclaringType!, own) : definition.IsPublic;
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in type.GetMethods(all))
        {
            yield return method;
        }
        foreach (ConstructorInfo constructor in type.GetConstructors(all))
        {
            yield return constructor;
        }
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (MethodBase method in DeclaredMethods(nested))
            {
                yield return method;
            }
        }
    }

    /// <summary>
    /// True when a mod other than us has Harmony patches on the method. Our replacements mirror vanilla bodies, so
    /// they would silently drop another mod's transpiler or double-apply its postfix: we back off instead. Each
    /// back-off is recorded for the calling patch (its source file name, e.g. HookFilter) with the other mods' names,
    /// so the settings menu, bdopt and the log can say which mod keeps that part of an optimization vanilla.
    /// </summary>
    public static bool IsPatchedByOthers(MethodBase? method, [CallerFilePath] string caller = "")
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
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        IEnumerable<Patch> others = info == null ? Enumerable.Empty<Patch>()
            : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Where(p => !PatchGuard.IsOurs(p.owner));
        bool patched = Record(caller, method, others);
        _patchedByOthers[method] = (patched, now);
        return patched;
    }

    /// <summary>
    /// Like <see cref="IsPatchedByOthers(MethodBase?, string)"/>, except for these patch methods of other mods (postfixes
    /// whose effect the caller reproduces, transpilers known not to touch what it changes; checked by fingerprint).
    /// </summary>
    public static bool IsPatchedByOthers(MethodBase? method, MethodInfo[] allowedPatches, [CallerFilePath] string caller = "")
    {
        if (method == null)
        {
            return true;
        }
        long now = Environment.TickCount64;
        if (_patchedBeyondAllowed.TryGetValue(method, out var cached) && now - cached.CheckedAt < RecheckAfterMs)
        {
            return cached.Patched;
        }
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        IEnumerable<Patch> others = info == null ? Enumerable.Empty<Patch>()
            : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Where(p => !PatchGuard.IsOurs(p.owner) && !allowedPatches.Any(a => a.MethodHandle == p.PatchMethod.MethodHandle));
        bool patched = Record(caller, method, others);
        _patchedBeyondAllowed[method] = (patched, now);
        return patched;
    }

    /// <summary>Other mods' patches on a method a patch backs off from, keyed by (patch name, method).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Patch, MethodBase Method), string[]> _conflicts = new();

    private static bool Record(string caller, MethodBase method, IEnumerable<Patch> others)
    {
        string[] mods = others.Select(ModName).Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string patch = System.IO.Path.GetFileNameWithoutExtension(caller.Replace('\\', '/'));
        if (mods.Length == 0)
        {
            _conflicts.TryRemove((patch, method), out _);
            return false;
        }
        if (!_conflicts.TryGetValue((patch, method), out string[]? known) || !known.SequenceEqual(mods))
        {
            _conflicts[(patch, method)] = mods;
            MegaCrit.Sts2.Core.Logging.Log.Info($"[{ModEntry.Id}] {patch}: {method.DeclaringType?.Name}.{method.Name} is also patched by {string.Join(", ", mods)}: normal game for that part");
        }
        return true;
    }

    /// <summary>The loaded mod whose assembly holds the patch method (its display name), else the Harmony id.</summary>
    private static string ModName(Patch patch)
    {
        Assembly? assembly = patch.PatchMethod?.DeclaringType?.Assembly;
        if (assembly != null && assembly != typeof(PatchSafety).Assembly)
        {
            foreach (MegaCrit.Sts2.Core.Modding.Mod mod in MegaCrit.Sts2.Core.Modding.ModManager.Mods)
            {
                if (ModAssemblies(mod).Contains(assembly))
                {
                    return mod.manifest?.name ?? mod.manifest?.id ?? patch.owner;
                }
            }
        }
        return patch.owner;
    }

    // Game v0.107: Mod.assembly; v0.111 (beta): Mod.assemblies. Read by reflection so both work.
    private static readonly FieldInfo? _modAssembly = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Modding.Mod), "assembly");
    private static readonly FieldInfo? _modAssemblies = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Modding.Mod), "assemblies");

    private static IEnumerable<Assembly> ModAssemblies(MegaCrit.Sts2.Core.Modding.Mod mod) =>
        _modAssembly?.GetValue(mod) is Assembly single ? new[] { single }
        : _modAssemblies?.GetValue(mod) as IEnumerable<Assembly> ?? Enumerable.Empty<Assembly>();

    /// <summary>The mods other patches come from, for the methods these patches currently back off from.</summary>
    public static IReadOnlyList<string> ConflictingMods(IEnumerable<string> patches)
    {
        var names = new HashSet<string>(patches);
        return _conflicts.Where(entry => names.Contains(entry.Key.Patch)).SelectMany(entry => entry.Value)
            .Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const long RecheckAfterMs = 2000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (bool Patched, long CheckedAt)> _patchedByOthers = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, (bool Patched, long CheckedAt)> _patchedBeyondAllowed = new();
}
