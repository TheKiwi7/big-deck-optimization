// Prints MethodFingerprint values for a type's methods, to fill the expected fingerprints of mirroring patches.
// Usage: dotnet run -- <assembly.dll> <type full name> <method name|*> [describe|body]
// "body": for async and iterator methods, the fingerprint of their state machine's MoveNext (the code they run).
using System.Reflection;
using System.Runtime.Loader;
using BigDeckOptimization.Patches;
// args: dll typeFullName methodName [describe]
string game = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
string workshop = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840";
// Dependencies first from the folder of the DLL given (refs/<version> holds that version's sts2, GodotSharp and 0Harmony),
// then the Steam install (whatever branch it has) and the Workshop mods.
string own = Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (string dir in new[] { own, game }.Concat(Directory.Exists(workshop) ? Directory.GetDirectories(workshop, "*", SearchOption.AllDirectories) : Array.Empty<string>()).Append(workshop))
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
    if (m.GetMethodBody() == null) continue; // Abstract or extern: no code to fingerprint ('*' stopped at the first one).
    if (args.Length > 3 && args[3] == "body")
    {
        Type? machine = m.GetCustomAttribute<System.Runtime.CompilerServices.StateMachineAttribute>()?.StateMachineType;
        MethodBase body = machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? m;
        Console.WriteLine($"{type.Name}.{m.Name}: {MethodFingerprint.Of(body)}");
        continue;
    }
    Console.WriteLine($"{type.Name}.{m.Name}: {MethodFingerprint.Of(m)}");
    if (args.Length > 3) Console.WriteLine(MethodFingerprint.Describe(m));
}
