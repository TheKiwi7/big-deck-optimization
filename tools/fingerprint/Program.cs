// Prints MethodFingerprint values for a type's methods, to fill the expected fingerprints of mirroring patches.
// Usage: dotnet run -- <assembly.dll> <type full name> <method name|*> [describe|body|own|own-describe]
// "body": for async and iterator methods, the fingerprint of their state machine's MoveNext (the code they run).
// Also: <dll> closure <limit> <root>... (a set CodeSet pins) and <dll> callees <root>... (direct game callees); FP_GAME=<sts2.dll>
// when the roots are another mod's methods.
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
if (args[1] == "closures")
{
    // closures <dll> closures <limit> [!key...] <root>...: LimitlessCloneMemo's worker offline (ClosureWalker: one callee graph
    // for every root, each walk stopping at a "!key" method, as at a method another mod patches); FP_SEPARATE=1: one
    // MethodFingerprint.GameClosure per root instead (the worker before the shared graph). Wall and process CPU time.
    var blocked = new HashSet<MethodBase>(args.Skip(3).Where(a => a.StartsWith('!')).Select(a => MethodFingerprint.Resolve(asm, a[1..]) ?? throw new Exception("not found: " + a)));
    MethodBase[] closureRoots = args.Skip(3).Where(a => !a.StartsWith('!')).Select(a => MethodFingerprint.Resolve(asm, a) ?? throw new Exception("not found: " + a)).ToArray();
    int closureLimit = int.Parse(args[2]);
    var process = System.Diagnostics.Process.GetCurrentProcess();
    TimeSpan cpu0 = process.TotalProcessorTime;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    if (Environment.GetEnvironmentVariable("FP_SEPARATE") == "1")
    {
        foreach (MethodBase root in closureRoots)
        {
            Console.WriteLine($"{MethodFingerprint.MemberName(root)}: {MethodFingerprint.GameClosure(asm, new[] { root }, closureLimit, new List<MethodInfo>(), initializers: false, dispatch: false).Count} methods");
        }
    }
    else
    {
        var graph = new CalleeGraph(asm);
        foreach (MethodBase root in closureRoots)
        {
            var walk = new ClosureWalk(graph, root, closureLimit);
            bool done = walk.Advance(blocked.Contains);
            Console.WriteLine($"{MethodFingerprint.MemberName(root)}: {(done ? walk.Found.Count + " methods" : "stopped at " + MethodFingerprint.MemberName(walk.Blocker) + " after " + walk.Found.Count)}");
        }
        Console.WriteLine($"{graph.Read} methods read");
    }
    process.Refresh();
    Console.WriteLine($"{clock.ElapsedMilliseconds} ms wall, {(process.TotalProcessorTime - cpu0).TotalMilliseconds:0} ms CPU");
    return;
}
if (args[1] == "callees")
{
    // callees <dll> callees <Namespace.Type::*|key>...: each root's direct game callees (MethodFingerprint.GameCallees, as
    // LimitlessCloneMemo's allowed patches are judged: no type initializers, the game's implementations of a game virtual).
    // FP_GAME=<sts2.dll>: the game assembly when the roots are another mod's methods.
    Assembly gameAsm = Environment.GetEnvironmentVariable("FP_GAME") is { Length: > 0 } calleeGame ? Assembly.LoadFrom(calleeGame) : asm;
    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    foreach (string key in args.Skip(2))
    {
        IEnumerable<MethodBase> rootMethods = key.EndsWith("::*")
            ? asm.GetType(key[..^3], true)!.GetMethods(all).Cast<MethodBase>().Where(m => m.GetMethodBody() != null)
            : new[] { MethodFingerprint.Resolve(asm, key) ?? throw new Exception("not found: " + key) };
        foreach (MethodBase root in rootMethods)
        {
            Console.WriteLine($"{MethodFingerprint.MemberName(root)} {MethodFingerprint.Of(root)}");
            var calleeDelegates = new List<MethodInfo>();
            foreach (MethodBase callee in MethodFingerprint.GameCallees(gameAsm, root, calleeDelegates, initializers: false, dispatch: false)
                .Where(c => c.Module.Assembly == gameAsm).Select(MethodFingerprint.Definition).Distinct().OrderBy(c => MethodFingerprint.MemberName(c), StringComparer.Ordinal))
            {
                Console.WriteLine($"  {MethodFingerprint.MemberName(callee)}{(callee is MethodInfo { IsVirtual: true, IsFinal: false } ? " virtual" : "")}");
            }
            foreach (string d in calleeDelegates.Select(d => MethodFingerprint.MemberName(d)).Distinct())
            {
                Console.WriteLine($"  delegate {d}");
            }
        }
    }
    return;
}
if (args[1] == "closure")
{
    // closure <dll> closure <limit> <root>...: the game methods reached from the roots (MethodFingerprint.GameClosure), each
    // with its fingerprint, the delegates invoked, and the set's fingerprint (MethodFingerprint.SetFingerprint). A root is a
    // Resolve key (Namespace.Type::Name(ParamType,...)), or Namespace.Type::* for every method and constructor the type declares.
    // "!Key": a method the closure neither adds nor follows (CodeSet's stopAt).
    const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    var roots = new List<MethodBase>();
    var stops = new HashSet<MethodBase>();
    foreach (string key in args.Skip(3))
    {
        if (key.StartsWith('!'))
        {
            stops.Add(MethodFingerprint.Resolve(asm, key[1..]) ?? throw new Exception("not found: " + key));
            continue;
        }
        if (key.EndsWith("::*"))
        {
            Type t = asm.GetType(key[..^3], true)!;
            roots.AddRange(t.GetMethods(declared).Cast<MethodBase>().Concat(t.GetConstructors(declared)).Where(m => m.GetMethodBody() != null));
            continue;
        }
        // "+Key": the method and every implementation the game has of it (a virtual getter a call may reach on any subclass).
        MethodBase root = MethodFingerprint.Resolve(asm, key.TrimStart('+')) ?? throw new Exception("not found: " + key);
        roots.Add(root);
        if (key.StartsWith('+') && root is MethodInfo virtualRoot)
        {
            roots.AddRange(MethodFingerprint.Implementations(asm, virtualRoot));
        }
    }
    var delegates = new List<MethodInfo>();
    var parents = new Dictionary<MethodBase, MethodBase>();
    var dispatches = new List<(MethodInfo Callee, List<MethodBase> Implementations)>();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    List<MethodBase> closure;
    try
    {
        // FP_GAME=<sts2.dll>: the game assembly when the roots are another mod's methods (LimitlessCloneMemo's allowed patches).
        // FP_ROOTSONLY=1: the roots alone, not followed (RelicFlashMerge's scene scripts: their own code, pinned).
        closure = Environment.GetEnvironmentVariable("FP_ROOTSONLY") == "1" ? roots.Distinct().ToList() : MethodFingerprint.GameClosure(Environment.GetEnvironmentVariable("FP_GAME") is { Length: > 0 } gamePath ? Assembly.LoadFrom(gamePath) : asm, roots, int.Parse(args[2]), delegates, parents,
            // As CodeSet computes it: no type initializers, dispatch only through the game's own virtual methods.
            Environment.GetEnvironmentVariable("FP_INIT") == "1", Environment.GetEnvironmentVariable("FP_DISPATCH") == "1", dispatches, stops.Count > 0 ? stops : null);
        watch.Stop();
    }
    catch (InvalidOperationException e) when (e.Message.StartsWith("more than"))
    {
        // The chain that led there, for the review.
        Console.WriteLine(e.Message);
        MethodBase? at = parents.Keys.LastOrDefault();
        for (int i = 0; at != null && i < 40; i++, at = parents.GetValueOrDefault(at))
        {
            Console.WriteLine("  from " + MethodFingerprint.MemberName(at));
        }
        return;
    }
    foreach (MethodBase m in closure.OrderBy(m => MethodFingerprint.MemberName(m), StringComparer.Ordinal))
    {
        // "virtual": another assembly's override runs in its place (CodeSet.ForeignOverrides).
        Console.WriteLine($"{MethodFingerprint.MemberName(m)} {MethodFingerprint.Of(m)}{(m is MethodInfo { IsVirtual: true, IsFinal: false } ? " virtual" : "")}");
    }
    string[] delegateNames = delegates.Select(d => MethodFingerprint.MemberName(d)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
    foreach (string d in delegateNames)
    {
        Console.WriteLine($"delegate {d}");
    }
    // The game's virtual and interface methods called (abstract ones included): what CodeSet.Virtuals checks for other
    // assemblies' overrides and implementations (not part of the set's fingerprint).
    foreach (string d in dispatches.Where(d => d.Callee.Module.Assembly == asm).Select(d => MethodFingerprint.MemberName(MethodFingerprint.Definition(d.Callee)))
        .Distinct().OrderBy(n => n, StringComparer.Ordinal))
    {
        Console.WriteLine($"dispatch {d}");
    }
    Console.WriteLine($"{closure.Count} methods, set {MethodFingerprint.SetFingerprint(closure, delegateNames)} ({watch.ElapsedMilliseconds} ms)");
    return;
}
Type type = asm.GetType(args[1], true)!;
foreach (MethodBase m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
    .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
{
    if (args[2] != "*" && m.Name != args[2]) continue;
    if (m.GetMethodBody() == null) continue; // Abstract or extern: no code to fingerprint ('*' stopped at the first one).
    // "own" / "own-describe": OwnNormalizedIl (the method's own assembly's types named without namespace: rule B's twins).
    if (args.Length > 3 && args[3] is "own" or "own-describe")
    {
        Console.WriteLine($"{type.Name}.{m.Name}: {OwnNormalizedIl.Of(m)}");
        if (args[3] == "own-describe") Console.WriteLine(OwnNormalizedIl.Describe(m, null));
        continue;
    }
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
