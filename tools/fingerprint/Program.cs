// Prints MethodFingerprint values for a type's methods, to fill the expected fingerprints of mirroring patches.
// Usage: dotnet run -- <assembly.dll> <type full name> <method name|*> [describe]
using System.Reflection;
using System.Runtime.Loader;
using BigDeckOptimization.Patches;
// args: dll typeFullName methodName [describe]
string game = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
string workshop = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840";
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (string dir in new[] { game }.Concat(Directory.GetDirectories(workshop, "*", SearchOption.AllDirectories)).Append(workshop))
    {
        string p = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
    }
    return null;
};
Assembly asm = Assembly.LoadFrom(args[0]);
Type type = asm.GetType(args[1], true)!;
foreach (MethodBase m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
{
    if (args[2] != "*" && m.Name != args[2]) continue;
    Console.WriteLine($"{type.Name}.{m.Name}: {MethodFingerprint.Of(m)}");
    if (args.Length > 3) Console.WriteLine(MethodFingerprint.Describe(m));
}
