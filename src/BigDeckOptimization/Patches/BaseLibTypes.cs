using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Whether any loaded mod defines a concrete type deriving from (or implementing) a BaseLib type, without referencing
/// BaseLib: only BaseLib and assemblies referencing it can.
/// </summary>
internal static class BaseLibTypes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _none = new();
    private static int _watching;
    private static long _changes;

    /// <summary>Bumped each time a <see cref="NoneDefined"/> answer turns false (an assembly defining such a type loaded).</summary>
    public static long Changes => System.Threading.Interlocked.Read(ref _changes);

    /// <summary>
    /// Whether no loaded assembly defines one (cached; an assembly loading later that defines one turns it false).
    /// </summary>
    public static bool NoneDefined(string baseTypeName)
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                // Runs inside another assembly's load: never throw there. Can't tell = a type may exist.
                bool may;
                try
                {
                    may = MayDefine(args.LoadedAssembly);
                }
                catch
                {
                    may = true;
                }
                if (!may)
                {
                    return;
                }
                // An assembly depending on BaseLib loaded: assemblies loaded before it that reference it may now derive from
                // its types (they were judged without it, an answer not kept), so every loaded one is looked at again.
                foreach (string name in _none.Keys)
                {
                    if (_none.TryGetValue(name, out bool none) && none && (Defines(args.LoadedAssembly, name) || AnyDefined(name)))
                    {
                        _none[name] = false;
                        System.Threading.Interlocked.Increment(ref _changes);
                    }
                }
            };
        }
        return _none.GetOrAdd(baseTypeName, name => !AnyDefined(name));
    }

    /// <summary>True when some loaded assembly defines one, or the BaseLib type is missing (can't tell).</summary>
    public static bool AnyDefined(string baseTypeName)
    {
        try
        {
            return AccessTools.TypeByName(baseTypeName) == null
                || AppDomain.CurrentDomain.GetAssemblies().Any(a => MayDefine(a) && Defines(a, baseTypeName));
        }
        catch
        {
            return true; // Can't tell: don't skip.
        }
    }

    private static Func<System.Collections.IList>? _spireFields;
    private static bool _spireFieldsUnreadable;
    private static int _lookForBaseLib = 1, _watchingLoads;
    private static int _spireFieldsCounted = -1;
    private static Type[] _spireFieldTargets = Array.Empty<Type>();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _spireTargeted = new();
    private static readonly object _spireLock = new();

    /// <summary>
    /// Whether a BaseLib SavedSpireField targets models of this type: BaseLib's postfix on <c>SavedProperties.FromInternal</c>
    /// then exports its value into the model's saved properties, which the game's own property list for the type doesn't
    /// show. Fields are only ever added (registered at BaseLib's post-init): targets are read again when their count
    /// changes. No BaseLib: false. BaseLib whose registry can't be read: true (can't tell).
    /// </summary>
    public static bool SpireFieldTargets(Type type)
    {
        System.Collections.IList? fields = SpireFields();
        if (fields == null)
        {
            return _spireFieldsUnreadable;
        }
        try
        {
            lock (_spireLock)
            {
                if (fields.Count != _spireFieldsCounted)
                {
                    _spireFieldTargets = fields.Cast<object>().Select(f => (Type)f.GetType().GetProperty("TargetType")!.GetValue(f)!).ToArray();
                    _spireFieldsCounted = fields.Count;
                    _spireTargeted.Clear();
                }
            }
            return _spireTargeted.GetOrAdd(type, t => _spireFieldTargets.Any(target => target.IsAssignableFrom(t)));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>How many SavedSpireFields BaseLib has registered (-1: no BaseLib or unreadable): caches built on
    /// <see cref="SpireFieldTargets"/> are rebuilt when it changes.</summary>
    public static int SpireFieldCount()
    {
        try
        {
            return SpireFields()?.Count ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static System.Collections.IList? SpireFields()
    {
        if (_spireFields != null)
        {
            return _spireFields();
        }
        if (_spireFieldsUnreadable)
        {
            return null;
        }
        // No BaseLib yet: looked for again only after an assembly loads (this runs in hot paths).
        if (System.Threading.Interlocked.Exchange(ref _lookForBaseLib, 0) == 0)
        {
            return null;
        }
        if (System.Threading.Interlocked.Exchange(ref _watchingLoads, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, _) => System.Threading.Interlocked.Exchange(ref _lookForBaseLib, 1);
        }
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BaseLib"))
        {
            return null;
        }
        try
        {
            FieldInfo field = AccessTools.Field(AccessTools.TypeByName("BaseLib.Patches.Utils.SavedSpireFieldPatch"), "RegisteredFields")
                ?? throw new MissingFieldException("SavedSpireFieldPatch", "RegisteredFields");
            _spireFields = System.Linq.Expressions.Expression.Lambda<Func<System.Collections.IList>>(
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(null, field), typeof(System.Collections.IList))).Compile();
            return _spireFields();
        }
        catch
        {
            _spireFieldsUnreadable = true;
            return null;
        }
    }

    /// <summary>
    /// Whether the assembly may define a type deriving from a BaseLib type: BaseLib itself, or an assembly referencing
    /// BaseLib directly or through another loaded assembly (a type deriving from a mod's own BaseLib subclass needs no
    /// reference to BaseLib in its metadata).
    /// </summary>
    public static bool MayDefine(Assembly assembly)
    {
        try
        {
            return !assembly.IsDynamic && DependsOnBaseLib(assembly.GetName().Name!, assembly, 0, out _);
        }
        catch
        {
            return !assembly.IsDynamic; // Can't tell: it may.
        }
    }

    /// <summary>Per assembly name: whether it depends on BaseLib (only answers about loaded assemblies are kept).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _dependsOnBaseLib = new();

    private static System.Collections.Generic.Dictionary<string, Assembly>? _loadedByName;
    private static int _loads, _loadsTabled = -1, _countingLoads;
    private static readonly object _loadedLock = new();

    /// <summary>Assemblies loaded since this started counting: tables and incomplete answers are valid while it stays.</summary>
    private static int Loads
    {
        get
        {
            if (System.Threading.Interlocked.Exchange(ref _countingLoads, 1) == 0)
            {
                AppDomain.CurrentDomain.AssemblyLoad += (_, _) => System.Threading.Interlocked.Increment(ref _loads);
            }
            return System.Threading.Volatile.Read(ref _loads);
        }
    }

    /// <summary>Subscribes the load counter (idempotent; never throws).</summary>
    public static void StartCountingLoads()
    {
        try
        {
            _ = Loads;
        }
        catch
        {
            // Counting starts at the first use instead.
        }
    }

    /// <summary>The loaded (non-dynamic) assembly of that name, from a table rebuilt after assemblies load.</summary>
    private static Assembly? Loaded(string name)
    {
        lock (_loadedLock)
        {
            int loads = Loads;
            if (_loadedByName == null || loads != _loadsTabled)
            {
                _loadedByName = new System.Collections.Generic.Dictionary<string, Assembly>();
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!assembly.IsDynamic && assembly.GetName().Name is { } loadedName)
                    {
                        _loadedByName.TryAdd(loadedName, assembly);
                    }
                }
                _loadsTabled = loads;
            }
            return _loadedByName.TryGetValue(name, out Assembly? found) ? found : null;
        }
    }

    /// <summary>Incomplete "doesn't depend" answers, valid until the next assembly load.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _incompleteAt = new();

    /// <summary><paramref name="complete"/>: false when an unloaded reference was taken as not depending (answer not kept).</summary>
    private static bool DependsOnBaseLib(string name, Assembly? loaded, int depth, out bool complete)
    {
        complete = true;
        if (name == "BaseLib")
        {
            return true;
        }
        if (_dependsOnBaseLib.TryGetValue(name, out bool known))
        {
            return known;
        }
        if (_incompleteAt.TryGetValue(name, out int at) && at == Loads)
        {
            complete = false;
            return false;
        }
        loaded ??= Loaded(name);
        if (loaded == null || depth >= 16)
        {
            // Not loaded: none of its types exist yet. When one depending on BaseLib loads, the others are judged again.
            complete = false;
            return false;
        }
        bool depends = false;
        foreach (AssemblyName reference in loaded.GetReferencedAssemblies())
        {
            if (reference.Name == null)
            {
                continue;
            }
            if (DependsOnBaseLib(reference.Name, null, depth + 1, out bool referenceComplete))
            {
                depends = true;
                complete = true;
                break;
            }
            complete &= referenceComplete;
        }
        if (complete)
        {
            _dependsOnBaseLib[name] = depends;
        }
        else
        {
            _incompleteAt[name] = Loads;
        }
        return depends;
    }

    public static bool Defines(Assembly assembly, string baseTypeName)
    {
        try
        {
            Type? baseType = AccessTools.TypeByName(baseTypeName);
            if (baseType == null)
            {
                return true;
            }
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray()!;
            }
            return types.Any(t => !t.IsAbstract && !t.IsInterface && baseType.IsAssignableFrom(t));
        }
        catch
        {
            return true; // Can't tell: don't skip.
        }
    }
}
