// For the hooks of src/BigDeckOptimization/Patches/HookSkipTable.cs, compares two game versions and prints the entries
// of PatchSafety.EquivalentFingerprints for the hooks whose code is the same: identical once the compiler's numbering
// of generated types (<Hook>d__N state machines, display classes, lambdas) is ignored, AND the AbstractModel default
// methods HookSkip relies on unchanged. Other hooks are listed with the reason (they stay vanilla until reviewed).
// Usage: dotnet run --project tools/equivalents -- <old game data dir> <new game data dir> [HookSkipTable.cs]
// e.g.   ... -- C:\Games\StS2-public-0.107.1\data_sts2_windows_x86_64 C:\Games\StS2-beta-0.111.0\data_sts2_windows_x86_64
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using BigDeckOptimization.Patches;

string tablePath = args.Length > 2 ? args[2] : "src/BigDeckOptimization/Patches/HookSkipTable.cs";
var entries = Regex.Matches(File.ReadAllText(tablePath), @"new\(""(\w+)"", ""([0-9A-F]{16})"", (?:true|false)((?:, ""\w+"")*)\)")
    .Select(m => (Hook: m.Groups[1].Value, Expected: m.Groups[2].Value,
        Methods: Regex.Matches(m.Groups[3].Value, @"""(\w+)""").Select(x => x.Groups[1].Value).ToArray()))
    .ToList();

Assembly Load(string dir)
{
    var ctx = new AssemblyLoadContext(dir, true);
    ctx.Resolving += (c, n) => { string p = Path.Combine(dir, n.Name + ".dll"); return File.Exists(p) ? c.LoadFromAssemblyPath(p) : null; };
    return ctx.LoadFromAssemblyPath(Path.Combine(dir, "sts2.dll"));
}

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

MethodBase? HookBody(Assembly game, string name)
{
    MethodInfo? hook = game.GetType("MegaCrit.Sts2.Core.Hooks.Hook")!.GetMethods(All).FirstOrDefault(m => m.Name == name);
    if (hook == null) return null;
    Type? machine = hook.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
    return machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? hook;
}

// Compiler-generated names carry a per-file counter that shifts when the game adds a member above.
string Normalized(MethodBase body) => Regex.Replace(MethodFingerprint.Describe(body), @"(d__|DisplayClass|b__)\d+(_\d+)?", "$1#");

string Defaults(Assembly game, string[] methods) => string.Join("|", methods.Select(name =>
    string.Join(",", game.GetType("MegaCrit.Sts2.Core.Models.AbstractModel")!.GetMethods(All).Where(m => m.Name == name)
        .Select(m => m.GetMethodBody() == null ? "abstract" : MethodFingerprint.Of(m)).OrderBy(x => x))));

Assembly oldGame = Load(args[0]), newGame = Load(args[1]);
int same = 0;
var equivalent = new List<string>();
var changed = new List<string>();
foreach (var (hook, expected, methods) in entries)
{
    MethodBase? oldBody = HookBody(oldGame, hook), newBody = HookBody(newGame, hook);
    if (oldBody == null || newBody == null) { changed.Add($"{hook}: missing"); continue; }
    string oldFp = MethodFingerprint.Of(oldBody), newFp = MethodFingerprint.Of(newBody);
    if (oldFp != expected) { changed.Add($"{hook}: the old version isn't the one the table expects ({oldFp})"); continue; }
    if (newFp == expected) { same++; continue; }
    if (Normalized(oldBody) != Normalized(newBody)) { changed.Add($"{hook}: code changed ({newFp})"); continue; }
    if (Defaults(oldGame, methods) != Defaults(newGame, methods)) { changed.Add($"{hook}: AbstractModel defaults changed ({string.Join(", ", methods)})"); continue; }
    equivalent.Add($"        [\"{expected}\"] = new[] {{ \"{newFp}\" }}, // Hook.{hook}");
}
Console.WriteLine($"// {entries.Count} hooks: {same} same fingerprint, {equivalent.Count} equivalent (generated-type numbering only), {changed.Count} changed");
equivalent.ForEach(Console.WriteLine);
changed.ForEach(c => Console.WriteLine("// changed: " + c));
