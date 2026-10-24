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

    /// <summary>
    /// Whether no loaded assembly defines one (cached; an assembly loading later that defines one turns it false).
    /// </summary>
    public static bool NoneDefined(string baseTypeName)
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                if (!MayDefine(args.LoadedAssembly))
                {
                    return;
                }
                foreach (string name in _none.Keys)
                {
                    if (_none.TryGetValue(name, out bool none) && none && Defines(args.LoadedAssembly, name))
                    {
                        _none[name] = false;
                    }
                }
            };
        }
        return _none.GetOrAdd(baseTypeName, name => !AnyDefined(name));
    }

    /// <summary>True when some loaded assembly defines one, or the BaseLib type is missing (can't tell).</summary>
    public static bool AnyDefined(string baseTypeName) =>
        AccessTools.TypeByName(baseTypeName) == null
        || AppDomain.CurrentDomain.GetAssemblies().Any(a => MayDefine(a) && Defines(a, baseTypeName));

    public static bool MayDefine(Assembly assembly) =>
        !assembly.IsDynamic && (assembly.GetName().Name == "BaseLib" || assembly.GetReferencedAssemblies().Any(r => r.Name == "BaseLib"));

    public static bool Defines(Assembly assembly, string baseTypeName)
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
        catch
        {
            return true; // Can't tell: don't skip.
        }
        return types.Any(t => !t.IsAbstract && !t.IsInterface && baseType.IsAssignableFrom(t));
    }
}
