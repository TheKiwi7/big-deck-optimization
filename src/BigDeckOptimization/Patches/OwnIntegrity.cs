using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Whether this mod's code (or its menu DLL) is the one it was built as. Another mod can load a rewritten copy of a mod's
/// assembly in place of the file (a cross-version compatibility mod does, from its own cache, its references pointing at
/// stand-in assemblies): the load checks (<see cref="PatchSafety.RequireResolvable"/>) and fingerprints would then run on
/// code this mod never shipped. Two generic signs, no list of mods: a reference to an assembly this mod isn't built against
/// (only the runtime's, the game's folder's, and the named ones), or a file loaded from outside the mod's folder whose bytes
/// differ from the mod folder's copy. Either one: nothing applied this session but the co-op check (<see cref="ModEntry.StartedOff"/>).
/// Costs a list of references and two path compares (reading the files only when the paths differ).
/// </summary>
public static class OwnIntegrity
{
    /// <summary>Why <paramref name="assembly"/> isn't this mod's own build, or null when nothing says so.</summary>
    /// <param name="expectedFile">The file the game (or the main DLL, for the menu) loads it from.</param>
    /// <param name="alsoReferenced">Assemblies it is built against besides the runtime's, the game's and Harmony.</param>
    public static string? WhyRewritten(Assembly assembly, string? expectedFile, params string[] alsoReferenced)
    {
        string[] foreign = assembly.GetReferencedAssemblies().Select(name => name.Name ?? "?")
            .Where(name => !Expected(name, alsoReferenced)).ToArray();
        if (foreign.Length > 0)
        {
            return $"{assembly.GetName().Name} references {string.Join(", ", foreign)}, which this mod isn't built against";
        }
        string location;
        try
        {
            location = assembly.Location;
        }
        catch (Exception)
        {
            location = "";
        }
        if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(expectedFile))
        {
            return null; // Loaded from bytes, or no mod folder known: the references above are all there is to check.
        }
        try
        {
            string actual = Path.GetFullPath(location), expected = Path.GetFullPath(expectedFile);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(actual, expected, comparison) || !File.Exists(expected))
            {
                return null;
            }
            // Another file: a plain copy is fine, a different one is a rewritten copy.
            return File.ReadAllBytes(actual).AsSpan().SequenceEqual(File.ReadAllBytes(expected))
                ? null
                : $"{assembly.GetName().Name} was loaded from {actual}, a file that differs from the mod's own {expected}";
        }
        catch (Exception)
        {
            return null; // A file the runtime can't read: no evidence either way.
        }
    }

    /// <summary>The runtime's assemblies, the game's (sts2, GodotSharp, what ships in its folder), Harmony, and the named ones.</summary>
    private static bool Expected(string name, string[] alsoReferenced)
    {
        if (name is "sts2" or "GodotSharp" or "0Harmony" or "mscorlib" or "netstandard" or "System" or ModEntry.Id
            || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || alsoReferenced.Contains(name))
        {
            return true;
        }
        // An assembly shipped with the game or the runtime (a future build may use one more of them): its folder says so.
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded == null || loaded.IsDynamic || string.IsNullOrEmpty(loaded.Location))
        {
            return false;
        }
        string? folder = Path.GetDirectoryName(loaded.Location);
        return folder != null && (folder == Path.GetDirectoryName(typeof(MegaCrit.Sts2.Core.Models.AbstractModel).Assembly.Location)
            || folder == Path.GetDirectoryName(typeof(object).Assembly.Location));
    }

    /// <summary>This mod's DLL file as the game loads it: <c>&lt;its mod folder&gt;/BigDeckOptimization.dll</c>, or null when no mod entry says.</summary>
    public static string? OwnFile()
    {
        try
        {
            return MegaCrit.Sts2.Core.Modding.ModManager.Mods.FirstOrDefault(mod => mod.manifest?.id == ModEntry.Id)?.path is { Length: > 0 } path
                ? Path.Combine(path, ModEntry.Id + ".dll")
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
