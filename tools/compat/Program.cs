// Lists every method of the mod's DLL that uses a member or type a game version doesn't have (what the load check,
// PatchSafety.RequireResolvable, would skip), all at once instead of one patch per game start.
// Usage: dotnet run --project tools/compat -- <BigDeckOptimization.dll> [game data dir, default: installed game]
// e.g. against the beta's saved assemblies: ... -- src/BigDeckOptimization/bin/Debug/BigDeckOptimization.dll refs/v0.111.0
// (game assemblies missing from that folder, e.g. GodotSharp's dependencies, are taken from the installed game).
using System.Reflection;
using System.Runtime.Loader;
using BigDeckOptimization.Patches;

string installed = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
string workshop = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840";
string[] dirs = new[] { args.Length > 1 ? Path.GetFullPath(args[1]) : installed, installed }
    .Concat(Directory.Exists(workshop) ? Directory.GetDirectories(workshop, "*", SearchOption.AllDirectories) : Array.Empty<string>())
    .ToArray();
var context = new AssemblyLoadContext("compat");
context.Resolving += (ctx, name) =>
{
    foreach (string dir in dirs)
    {
        string p = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
    }
    return null;
};
// The mod's DLL in its own context, so its game references resolve against the chosen folder.
Assembly mod = context.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
int problems = 0;
foreach (Type type in mod.GetTypes())
{
    List<MethodBase> methods;
    try
    {
        methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)).ToList();
    }
    catch (Exception e)
    {
        problems++;
        Console.WriteLine($"{type.FullName}: {e.Message}");
        continue;
    }
    foreach (MethodBase method in methods)
    {
        try
        {
            _ = MethodFingerprint.UsedMembers(method).Count();
        }
        catch (Exception e)
        {
            problems++;
            Console.WriteLine($"{type.FullName}.{method.Name}: {e.Message}");
        }
    }
}
Console.WriteLine($"{problems} problem(s)");
