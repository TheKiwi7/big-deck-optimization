using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

// Nullable annotations off: the answers are as oblivious as Harmony's AccessTools ones they replace (no new warnings at
// the hundreds of call sites written for those).
#nullable disable

namespace BigDeckOptimization.Patches;

/// <summary>
/// The reflection lookups of this mod's patches: the same answers as Harmony's <see cref="AccessTools"/> lookups (same
/// binding flags, base types searched the same way, the same ambiguity fallback), found through the runtime's own reflection
/// and checked, never through <see cref="AccessTools"/> itself.
/// <para>
/// Why: another mod can patch Harmony's lookups for every caller (a cross-version compatibility mod does: a lookup that
/// fails is answered with another member, a hook of the same family, a field of another name, a stand-in type under the
/// game's name). A patch looking a game method up by name would then mirror, fingerprint or patch that other member. The
/// runtime's reflection is the one Harmony itself ends in, and nothing patches it. Each answer is also checked: the member
/// has the name asked, it is the runtime's own (not a stand-in object of another assembly), it is declared by the type asked
/// or one of its base types, its parameters are the ones asked; a type under the game's namespace comes from the game's
/// assembly. An answer failing a check is treated as absent (logged once), so the patch relying on it is left vanilla.
/// </para>
/// No type scan: a name is looked up in the game's assembly, then in each loaded assembly by full name, as Harmony's first
/// two steps do (its last step, a scan of every type of every assembly for a short name, isn't used: this mod gives full names).
/// </summary>
public static class Reflect
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.GetField | BindingFlags.SetField | BindingFlags.GetProperty | BindingFlags.SetProperty;
    private const BindingFlags AllDeclared = All | BindingFlags.DeclaredOnly;

    /// <summary>The game's assembly (sts2).</summary>
    public static readonly Assembly Game = typeof(MegaCrit.Sts2.Core.Models.AbstractModel).Assembly;

    /// <summary>The runtime's reflection objects (RuntimeMethodInfo, RtFieldInfo...) come from the core library.</summary>
    private static readonly Assembly CoreLibrary = typeof(object).Assembly;

    /// <summary>The game's root namespace ("MegaCrit."): a type under it found anywhere but the game's assembly is a stand-in.</summary>
    private static readonly string GameNamespace = typeof(MegaCrit.Sts2.Core.Models.AbstractModel).Namespace!.Split('.')[0] + ".";

    /// <summary>Lookups whose answer failed a check, logged once each.</summary>
    private static readonly ConcurrentDictionary<string, byte> _refused = new(StringComparer.Ordinal);

    /// <summary>Answers refused this session (another mod answering the runtime's reflection: never seen so far).</summary>
    public static int Refused => _refused.Count;

    public static MethodInfo Method(Type type, string name, Type[] parameters = null, Type[] generics = null)
    {
        if (type == null || string.IsNullOrEmpty(name))
        {
            return null;
        }
        MethodInfo method;
        if (parameters == null)
        {
            try
            {
                method = BaseTypes(type, t => t.GetMethod(name, All));
            }
            catch (AmbiguousMatchException inner)
            {
                method = BaseTypes(type, t => t.GetMethod(name, All, null, Type.EmptyTypes, Array.Empty<ParameterModifier>()));
                if (method == null)
                {
                    throw new AmbiguousMatchException($"Ambiguous match for {type}:{name}", inner);
                }
            }
        }
        else
        {
            method = BaseTypes(type, t => t.GetMethod(name, All, null, parameters, Array.Empty<ParameterModifier>()));
        }
        method = Checked(method, type, name, parameters);
        return method != null && generics != null ? method.MakeGenericMethod(generics) : method;
    }

    /// <summary><c>Namespace.Type:Method</c> (nested types with <c>+</c>), the type found as <see cref="TypeByName"/> does.</summary>
    public static MethodInfo Method(string typeColonName, Type[] parameters = null, Type[] generics = null)
    {
        int colon = typeColonName.LastIndexOf(':');
        return colon <= 0 ? null : Method(TypeByName(typeColonName.Substring(0, colon)), typeColonName.Substring(colon + 1), parameters, generics);
    }

    public static MethodInfo DeclaredMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
    {
        if (type == null || string.IsNullOrEmpty(name))
        {
            return null;
        }
        MethodInfo method = parameters != null
            ? type.GetMethod(name, AllDeclared, null, parameters, Array.Empty<ParameterModifier>())
            : type.GetMethod(name, AllDeclared);
        method = Checked(method, type, name, parameters);
        return method != null && generics != null ? method.MakeGenericMethod(generics) : method;
    }

    public static FieldInfo Field(Type type, string name) =>
        type == null || string.IsNullOrEmpty(name) ? null : Checked(BaseTypes(type, t => t.GetField(name, All)), type, name, null);

    public static PropertyInfo Property(Type type, string name) =>
        type == null || string.IsNullOrEmpty(name) ? null : Checked(BaseTypes(type, t => t.GetProperty(name, All)), type, name, null);

    public static MethodInfo PropertyGetter(Type type, string name) => Accessor(Property(type, name)?.GetGetMethod(nonPublic: true), type);

    public static MethodInfo PropertySetter(Type type, string name) => Accessor(Property(type, name)?.GetSetMethod(nonPublic: true), type);

    /// <summary>A nested type, searched in the base types too.</summary>
    public static Type Inner(Type type, string name) =>
        type == null || string.IsNullOrEmpty(name) ? null : CheckedType(BaseTypes(type, t => t.GetNestedType(name, All)), name, type);

    /// <summary>
    /// A type by full name (nested types with <c>+</c>): the game's assembly first, then each loaded assembly. A name under the
    /// game's namespace is only taken from the game's assembly (a stand-in type of another assembly under a game name, made
    /// for a member missing from this game version, would otherwise be mirrored or patched).
    /// </summary>
    public static Type TypeByName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        Type type = Game.GetType(name, throwOnError: false);
        if (type == null && !name.StartsWith(GameNamespace, StringComparison.Ordinal))
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly != Game && (type = assembly.GetType(name, throwOnError: false)) != null)
                {
                    break;
                }
            }
        }
        return CheckedType(type, name, null);
    }

    /// <summary>The MoveNext of an async method's state machine (as <see cref="AccessTools.AsyncMoveNext"/>).</summary>
    public static MethodInfo AsyncMoveNext(MethodBase method) =>
        method?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType is { } machine ? DeclaredMethod(machine, "MoveNext") : null;

    /// <summary>The MoveNext of an iterator's enumerator: the one type the method creates (as <see cref="AccessTools.EnumeratorMoveNext"/>).</summary>
    public static MethodInfo EnumeratorMoveNext(MethodBase method)
    {
        if (method == null)
        {
            return null;
        }
        ConstructorInfo[] created = PatchProcessor.ReadMethodBody(method).Where(pair => pair.Key == OpCodes.Newobj)
            .Select(pair => pair.Value as ConstructorInfo).ToArray()!;
        return created.Length == 1 && created[0]?.DeclaringType is { } enumerator ? Method(enumerator, "MoveNext") : null;
    }

    /// <summary>A by-reference accessor of an instance field (as <see cref="AccessTools.FieldRefAccess{T, F}(string)"/>).</summary>
    public static AccessTools.FieldRef<T, F> FieldRefAccess<T, F>(string fieldName)
    {
        FieldInfo field = Field(typeof(T), fieldName);
        if (field == null || field.IsStatic)
        {
            throw new ArgumentException($"FieldRefAccess<{typeof(T)}, {typeof(F)}> for {fieldName} caused an exception",
                field == null ? new MissingFieldException(typeof(T).Name, fieldName) : new ArgumentException("Field must not be static"));
        }
        return AccessTools.FieldRefAccess<T, F>(field);
    }

    /// <summary>A by-reference accessor of a field already found (the runtime's own <see cref="FieldInfo"/> only).</summary>
    public static AccessTools.FieldRef<T, F> FieldRefAccess<T, F>(FieldInfo field) =>
        field.GetType().Assembly == CoreLibrary ? AccessTools.FieldRefAccess<T, F>(field)
        : throw new ArgumentException($"FieldRefAccess<{typeof(T)}, {typeof(F)}>: {field.Name} is not the runtime's own field ({field.GetType().FullName})");

    /// <summary>A by-reference accessor of a field found by <see cref="Field"/> (as <see cref="AccessTools.FieldRefAccess{F}(Type, string)"/>).</summary>
    public static AccessTools.FieldRef<object, F> FieldRefAccess<F>(Type type, string fieldName) =>
        AccessTools.FieldRefAccess<object, F>(Field(type, fieldName)
            ?? throw new ArgumentException($"FieldRefAccess<{typeof(F)}> for {type}, {fieldName} caused an exception", new MissingFieldException(type.Name, fieldName)));

    private static T BaseTypes<T>(Type type, Func<Type, T> find) where T : class
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            if (find(t) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The member, or null when it isn't what was asked (refusal logged once).</summary>
    private static T Checked<T>(T member, Type type, string name, Type[] parameters) where T : MemberInfo
    {
        if (member == null)
        {
            return null;
        }
        string why = member.Name != name ? "another name"
            : member.GetType().Assembly != CoreLibrary ? "not the runtime's own member (" + member.GetType().FullName + ")"
            : !DeclaredAlong(member.DeclaringType, type) ? "declared by " + member.DeclaringType?.FullName
            : parameters != null && member is MethodBase method && !SameParameters(method, parameters) ? "other parameters"
            : null;
        return why == null ? member : Refuse<T>(type.FullName + ":" + name, member, why);
    }

    private static Type CheckedType(Type type, string name, Type outer)
    {
        if (type == null)
        {
            return null;
        }
        string why = type.GetType().Assembly != CoreLibrary ? "not the runtime's own type"
            : outer != null ? (type.Name != name ? "another name" : !DeclaredAlong(type.DeclaringType, outer) ? "nested in " + type.DeclaringType?.FullName : null)
            : type.FullName != name && type.FullName?.Replace('+', '.') != name.Replace('+', '.') ? "another name"
            : type.FullName!.StartsWith(GameNamespace, StringComparison.Ordinal) && type.Assembly != Game ? "a game name outside the game's assembly (" + type.Assembly.GetName().Name + ")"
            : null;
        return why == null ? type : Refuse<Type>((outer == null ? "" : outer.FullName + "+") + name, type, why);
    }

    /// <summary>A property accessor, checked like the property (the runtime's own, declared along the type asked).</summary>
    private static MethodInfo Accessor(MethodInfo accessor, Type type) =>
        accessor == null || type == null ? null
        : accessor.GetType().Assembly == CoreLibrary && DeclaredAlong(accessor.DeclaringType, type) ? accessor
        : Refuse<MethodInfo>(type.FullName + ":" + accessor.Name, accessor, "accessor not the runtime's own");

    /// <summary>Whether <paramref name="declaring"/> is <paramref name="type"/> or one of its base types.</summary>
    private static bool DeclaredAlong(Type declaring, Type type)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            if (t == declaring)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The parameters asked, each the method's own or one the runtime's binder accepts in its place (a derived type).</summary>
    private static bool SameParameters(MethodBase method, Type[] parameters)
    {
        ParameterInfo[] actual = method.GetParameters();
        if (actual.Length != parameters.Length)
        {
            return false;
        }
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i].ParameterType != parameters[i] && !actual[i].ParameterType.IsAssignableFrom(parameters[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static T Refuse<T>(string asked, MemberInfo answer, string why) where T : class
    {
        if (_refused.TryAdd(asked, 0))
        {
            Log.Warn($"[{ModEntry.Id}] reflection: {asked} answered by {answer.DeclaringType?.FullName}.{answer.Name} ({why}): treated as absent, what relies on it stays vanilla");
        }
        return null;
    }

    /// <summary>
    /// What Harmony's own lookup answers for a game method the runtime says doesn't exist, or answers differently: the member
    /// another mod substitutes (a mod patching Harmony's lookups), else null. Asked where a missing member is expected (a hook
    /// of another game version), to tell "answered by another member" apart from "absent" or "code changed" in the logs.
    /// </summary>
    public static MethodInfo SubstitutedMethod(Type type, string name, MethodInfo own)
    {
        try
        {
            MethodInfo harmony = AccessTools.Method(type, name);
            return harmony != null && (own == null || harmony.MetadataToken != own.MetadataToken || harmony.Module != own.Module) ? harmony : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The mods patching Harmony's reflection lookups (<see cref="AccessTools"/>' Method, Field, TypeByName...): logged once at the
    /// mod's start, a cross-check for the answers above. Reading the patch info of those few methods costs nothing.
    /// </summary>
    public static string[] LookupInterceptors()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "Method", "DeclaredMethod", "Field", "DeclaredField", "TypeByName", "Property", "DeclaredProperty", "Inner" };
        var owners = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MethodInfo lookup in typeof(AccessTools).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (names.Contains(lookup.Name) && Harmony.GetPatchInfo(lookup) is { } info)
            {
                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    if (!PatchGuard.IsOurs(patch.owner))
                    {
                        owners.Add(PatchSafety.ModNameOf(patch.PatchMethod?.DeclaringType?.Assembly) ?? patch.owner);
                    }
                }
            }
        }
        return owners.ToArray();
    }

    /// <summary>The game's version as the game declares it (<c>ModManager._gameVersion</c>, read through the runtime: "v0.113.1"), or null.</summary>
    public static string GameVersion()
    {
        try
        {
            FieldInfo field = typeof(MegaCrit.Sts2.Core.Modding.ModManager).GetField("_gameVersion", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            string version = field != null && field.GetType().Assembly == CoreLibrary ? field.GetValue(null)?.ToString() : null;
            return string.IsNullOrEmpty(version) || version.StartsWith("v", StringComparison.Ordinal) ? version : "v" + version;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
