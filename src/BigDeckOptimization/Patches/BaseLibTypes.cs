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
                foreach (string name in _none.Keys)
                {
                    if (_none.TryGetValue(name, out bool none) && none && Defines(args.LoadedAssembly, name))
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

    public static bool MayDefine(Assembly assembly)
    {
        try
        {
            return !assembly.IsDynamic && (assembly.GetName().Name == "BaseLib" || assembly.GetReferencedAssemblies().Any(r => r.Name == "BaseLib"));
        }
        catch
        {
            return !assembly.IsDynamic; // Can't tell: it may.
        }
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
