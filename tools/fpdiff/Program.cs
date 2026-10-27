// Reviews what a game (or mod) update changed in the code the patches rely on: every fingerprint constant of the mod's
// sources (src/BigDeckOptimization) is looked up in an old and a new version of the game and mods.
//
//   review <old data dir> <new data dir> [--old-mods dir] [--new-mods dir] [--decompiled <old root> <new root>] [--all]
//       Per constant: SAME (the patch stays on), CHANGED (left vanilla until reviewed), SIGNATURE (only a method of the
//       same name exists), MISSING. For CHANGED/SIGNATURE: the diff of the fingerprinted code (IL, generated numbering
//       normalized, lambdas and state machines included), the C# diff when decompiled roots are given, the members the
//       new code calls that the old didn't (with their code and whether they changed), and the entry to add to
//       EquivalentFingerprints.cs once the change is reviewed as harmless for the patch.
//   list <data dir> [--mods dir]
//       Which method each constant belongs to in that version (and the constants matching nothing).
//   migrate <data dir of the version the constants were made on> [<other version data dir>]
//       One-off: rewrites constants of the legacy (raw IL) algorithm into the current one (EquivalentFingerprints values
//       from the other version).
//
// Data dirs: a game's data_sts2_windows_x86_64 folder or a refs/v<version> folder (missing assemblies come from the
// installed game). Mods: BaseLib, Limitless, BetterSpire2 from Steam's Workshop folder unless --old-mods/--new-mods
// holds them (e.g. a modified copy).
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using BigDeckOptimization.Patches;
using FpDiff;

const string Installed = @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";
const string Workshop = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840";
string[] modNames = { "BaseLib", "Limitless", "BetterSpire2" };
// Code HookSkip's hooks rely on beyond their own (tools/hookskip/classify.py assumes it): a listener keeping the defaults
// gets a choice context that only pushes/pops its model and signals ExecutionFinished. Checked at runtime per version.
string[] HookHelpers =
{
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::.ctor(AbstractModel,UInt64,ICombatState,GameActionType)",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::GetOwner(AbstractModel,ICombatState)",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::AssignTaskAndWaitForPauseOrCompletion(Task)",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::ExecuteTaskThenInvokeExecutionFinished(Task)",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.HookPlayerChoiceContext::WaitForCompletion()",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext::PushModel(AbstractModel)",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext::PopModel(AbstractModel)",
    "MegaCrit.Sts2.Core.Models.AbstractModel::InvokeExecutionFinished()",
};
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

string Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
string srcRoot = Directory.Exists("src/BigDeckOptimization") ? "src/BigDeckOptimization" : throw new Exception("run from the repository root");

List<Assembly> Load(string dataDir, string modsDir)
{
    dataDir = Path.GetFullPath(dataDir);
    string[] modDirs = (modsDir != "" ? new[] { Path.GetFullPath(modsDir) } : Array.Empty<string>())
        .Concat(Directory.Exists(Workshop) ? Directory.GetDirectories(Workshop, "*", SearchOption.AllDirectories) : Array.Empty<string>()).ToArray();
    string[] dirs = new[] { dataDir, Installed }.Concat(modDirs).ToArray();
    var context = new AssemblyLoadContext(dataDir + "|" + modsDir);
    context.Resolving += (ctx, name) =>
    {
        foreach (string dir in dirs)
        {
            string p = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
        }
        return null;
    };
    var result = new List<Assembly> { context.LoadFromAssemblyPath(Path.Combine(dataDir, "sts2.dll")) };
    foreach (string mod in modNames)
    {
        string? path = modDirs.Select(d => Path.Combine(d, mod + ".dll")).FirstOrDefault(File.Exists);
        if (path != null)
        {
            try { result.Add(context.LoadFromAssemblyPath(path)); } catch (Exception e) { Console.Error.WriteLine($"{mod}: {e.Message}"); }
        }
    }
    return result;
}

IEnumerable<MethodBase> Methods(Assembly assembly)
{
    Type[] types;
    try { types = assembly.GetTypes(); }
    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
    foreach (Type type in types)
    {
        MethodBase[] methods;
        try { methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)).ToArray(); }
        catch { continue; }
        foreach (MethodBase m in methods)
        {
            bool hasBody;
            try { hasBody = m.GetMethodBody() != null; } catch { continue; }
            if (hasBody) yield return m;
        }
    }
}

// Fingerprint -> methods, for every method with a body (computed in parallel; a few failures are skipped).
Dictionary<string, List<MethodBase>> Index(IEnumerable<Assembly> assemblies, Func<MethodBase, string> fingerprint)
{
    var bag = new ConcurrentBag<(string, MethodBase)>();
    Parallel.ForEach(assemblies.SelectMany(Methods), m =>
    {
        try { bag.Add((fingerprint(m), m)); } catch { }
    });
    return bag.GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToList());
}

string Norm(string s) => Regex.Replace(s, @"(d__|DisplayClass|b__|9__|\||AnonymousType|<)\d+(_\d+)?(?=\W|$)", "$1#");
string Key(MethodBase m) { try { return Norm(MethodFingerprint.MemberName(m)); } catch { return TypeKey(m) + "(?)"; } }
string TypeKey(MethodBase m) => Norm(MethodFingerprint.TypeName(m.DeclaringType)) + "::" + m.Name;

// Constants of the sources: (fingerprint, file:line, line text).
List<(string Fp, string Where, string Line)> Constants()
{
    var found = new List<(string, string, string)>();
    foreach (string file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("/obj/")))
    {
        string[] lines = File.ReadAllLines(file);
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in Regex.Matches(lines[i], "\"([0-9A-F]{16})\""))
            {
                found.Add((m.Groups[1].Value, $"{Path.GetRelativePath(".", file)}:{i + 1}", lines[i].Trim()));
            }
        }
    }
    return found;
}

// Line diff (LCS), printed as unified-like hunks with 3 lines of context.
void PrintDiff(string oldText, string newText, StringBuilder o)
{
    string[] a = oldText.Split('\n'), b = newText.Split('\n');
    int n = a.Length, m = b.Length;
    if ((long)n * m > 40_000_000) { o.AppendLine("    (too long to diff)"); return; }
    var lcs = new int[n + 1, m + 1];
    for (int i = n - 1; i >= 0; i--)
        for (int j = m - 1; j >= 0; j--)
            lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
    var ops = new List<(char, string)>();
    int x = 0, y = 0;
    while (x < n || y < m)
    {
        if (x < n && y < m && a[x] == b[y]) { ops.Add((' ', a[x])); x++; y++; }
        else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) { ops.Add(('+', b[y])); y++; }
        else { ops.Add(('-', a[x])); x++; }
    }
    int last = -100;
    for (int k = 0; k < ops.Count; k++)
    {
        bool near = false;
        for (int d = -3; d <= 3 && !near; d++) near = k + d >= 0 && k + d < ops.Count && ops[k + d].Item1 != ' ';
        if (!near) continue;
        if (k != last + 1) o.AppendLine("    @@");
        o.Append("    ").Append(ops[k].Item1).Append(' ').AppendLine(ops[k].Item2);
        last = k;
    }
}

// For display only: local slots, branch targets and handler offsets shift with any edit above them.
string Readable(string il) => string.Join("\n", il.Split('\n').Select(l =>
{
    l = Regex.Replace(l, @"^(ld|st)loc(a?)(\.s)? \d+$", "$1loc$2 _");
    l = Regex.Replace(l, @"^(ld|st)loc\.\d$", "$1loc _");
    l = Regex.Replace(l, @"^(br\S*|leave\S*|beq\S*|bne\S*|bge\S*|bgt\S*|ble\S*|blt\S*|switch)( .*)?$", "$1");
    l = Regex.Replace(l, @"^eh (\w+) \d+ \d+ \d+ \d+ ?", "eh $1 ");
    return l;
}));

// Reviewed equivalents already accepted at runtime: expected -> accepted fingerprints.
Dictionary<string, string[]> Reviewed()
{
    string text = File.ReadAllText(Path.Combine(srcRoot, "Patches", "EquivalentFingerprints.cs"));
    return Regex.Matches(text, @"\[""([0-9A-F]{16})""\] = new\[\] \{ ([^}]*) \}")
        .ToDictionary(m => m.Groups[1].Value, m => Regex.Matches(m.Groups[2].Value, "[0-9A-F]{16}").Select(x => x.Value).ToArray());
}

// LCS similarity of two texts' lines, 0..1.
double Similarity(string a, string b)
{
    string[] x = a.Split('\n'), y = b.Split('\n');
    if ((long)x.Length * y.Length > 20_000_000) return 0;
    var prev = new int[y.Length + 1];
    for (int i = 0; i < x.Length; i++)
    {
        var cur = new int[y.Length + 1];
        for (int j = 0; j < y.Length; j++) cur[j + 1] = x[i] == y[j] ? prev[j] + 1 : Math.Max(prev[j + 1], cur[j]);
        prev = cur;
    }
    return 2.0 * prev[y.Length] / (x.Length + y.Length);
}

// C# source of a method from an ILSpy project folder (namespace folder / outer type file), by brace matching.
string? Source(string root, MethodBase m)
{
    if (root == "") return null;
    Type t = m.DeclaringType!;
    string name = m.Name;
    if (t.Name.Contains('<'))
    {
        // State machine / lambda: the source is the user method named in <Name>.
        Match g = Regex.Match(t.Name.StartsWith("<>") ? m.Name : t.Name, @"<(\w+)>");
        if (g.Success) name = g.Groups[1].Value;
    }
    while (t.IsNested) t = t.DeclaringType!;
    string file = Path.Combine(root, t.Namespace ?? "", Regex.Replace(t.Name, @"`\d+", "") + ".cs");
    if (!File.Exists(file)) return null;
    string text = File.ReadAllText(file).Replace("\r", "");
    var sb = new StringBuilder();
    foreach (Match decl in Regex.Matches(text, $@"(?m)^\t+[^\n;=]*\b{Regex.Escape(name == ".ctor" ? t.Name : name)}\s*(<[^\n]*>)?\("))
    {
        int brace = text.IndexOf('{', decl.Index);
        int semi = text.IndexOf(';', decl.Index);
        if (brace < 0 || (semi >= 0 && semi < brace && !text[decl.Index..semi].Contains("=>"))) continue;
        int depth = 0, end = brace;
        for (; end < text.Length; end++)
        {
            if (text[end] == '{') depth++;
            else if (text[end] == '}' && --depth == 0) break;
        }
        sb.AppendLine(text[decl.Index..Math.Min(end + 1, text.Length)]);
    }
    return sb.Length == 0 ? null : sb.ToString();
}

HashSet<string> Callees(MethodBase m)
{
    var set = new HashSet<string>();
    try
    {
        foreach (string line in MethodFingerprint.Describe(m).Split('\n'))
        {
            Match c = Regex.Match(line, @"^(call|callvirt|newobj|ldftn|ldvirtftn) (.+)$");
            if (c.Success) set.Add(c.Groups[2].Value);
        }
    }
    catch { }
    return set;
}

if (args.Length == 0) { Console.WriteLine("usage: review <old> <new> [...] | list <dir> | migrate <dir> [<other>]"); return 1; }

switch (args[0])
{
    case "migrate":
    {
        var asmOld = Load(args[1], "");
        var legacy = Index(asmOld, LegacyFingerprint.Of);
        var legacyOther = args.Length > 2 && !args[2].StartsWith("--") ? Index(Load(args[2], ""), LegacyFingerprint.Of) : null;
        var map = new Dictionary<string, string>();
        var unmapped = new List<string>();
        foreach (var (fp, where, line) in Constants().DistinctBy(c => c.Fp + c.Where))
        {
            if (map.ContainsKey(fp)) continue;
            var source = legacy.TryGetValue(fp, out var ms) ? ms : legacyOther != null && legacyOther.TryGetValue(fp, out var ms2) ? ms2 : null;
            if (source == null) { unmapped.Add($"{fp} {where}: {line}"); continue; }
            var news = source.Select(MethodFingerprint.Of).Distinct().ToList();
            if (news.Count != 1) { unmapped.Add($"{fp} {where}: ambiguous ({string.Join(", ", source.Select(Key))})"); continue; }
            map[fp] = news[0];
        }
        foreach (string file in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\")))
        {
            string text = File.ReadAllText(file);
            string updated = Regex.Replace(text, "\"([0-9A-F]{16})\"", m => map.TryGetValue(m.Groups[1].Value, out string? n) ? $"\"{n}\"" : m.Value);
            if (updated != text) { File.WriteAllText(file, updated, new UTF8Encoding(false)); Console.WriteLine("updated " + file); }
        }
        Console.WriteLine($"{map.Count} constants migrated, {unmapped.Count} not:");
        unmapped.ForEach(u => Console.WriteLine("  " + u));
        return 0;
    }
    case "hookfps":
    {
        // hookfps <data dir>: input of tools/hookskip/classify.py for one game version: the code fingerprint of every Hook
        // method (async/iterator: its state machine's MoveNext, the code HookSkip transpiles), of every AbstractModel
        // method (the defaults the hooks call), and of the helpers HookSkip assumes behave as reviewed (Helper lines).
        Assembly game = Load(args[1], "")[0];
        Type hook = game.GetType("MegaCrit.Sts2.Core.Hooks.Hook", true)!;
        foreach (MethodInfo m in hook.GetMethods(All))
        {
            Type? machine = m.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType;
            MethodBase body = machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? m;
            Console.WriteLine($"Hook.{m.Name}: {MethodFingerprint.Of(body)}");
        }
        foreach (MethodInfo m in game.GetType("MegaCrit.Sts2.Core.Models.AbstractModel", true)!.GetMethods(All).Where(m => m.GetMethodBody() != null))
        {
            Console.WriteLine($"AbstractModel.{m.Name}: {MethodFingerprint.Of(m)}");
        }
        foreach (string helper in HookHelpers)
        {
            MethodBase? m = MethodFingerprint.Resolve(game, helper);
            Console.WriteLine($"Helper.{helper}: {(m == null ? "-" : MethodFingerprint.Of(m))}");
        }
        return 0;
    }
    case "deps":
    {
        // deps <mod dll> <data dir>: per patch class (Patches namespace, nested types included), the game and mod methods
        // its code uses, directly or by name through reflection (a type token followed by a string), each marked
        // "pinned" when its fingerprint is a constant of the sources. To review what a patch relies on without checking.
        var assemblies = Load(args[2], "");
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(assemblies[0])!;
        Assembly mod = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
        var external = new HashSet<Assembly>(assemblies);
        var constants = Constants().Select(c => c.Fp).ToHashSet();
        foreach (Type patch in mod.GetTypes().Where(t => t.Namespace == "BigDeckOptimization.Patches" && !t.IsNested).OrderBy(t => t.Name))
        {
            var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
            IEnumerable<Type> Nested(Type t) => new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Nested));
            foreach (MethodBase m in Nested(patch).SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All))))
            {
                List<MemberInfo> members;
                try { members = MethodFingerprint.UsedMembers(m).ToList(); } catch { continue; }
                // By-name reflection: the strings of the method, looked up on every external type it mentions.
                string[] strings = Regex.Matches(MethodFingerprint.DescribeOne(m), "^ldstr \"([A-Za-z_]\\w*)\"$", RegexOptions.Multiline).Select(x => x.Groups[1].Value).ToArray();
                foreach (Type t in members.OfType<Type>().Where(t => external.Contains(t.Assembly)).Distinct().ToList())
                {
                    foreach (string s in strings)
                    {
                        members.AddRange(t.GetMember(s, All));
                    }
                }
                foreach (MethodBase target in members.Select(x => x is PropertyInfo p ? (MethodBase?)p.GetMethod : x as MethodBase).OfType<MethodBase>())
                {
                    if (!external.Contains(target.Module.Assembly) || target.GetMethodBody() == null) continue;
                    string key = Key(target);
                    if (used.ContainsKey(key)) continue;
                    string fp;
                    try
                    {
                        Type? machine = target.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType;
                        MethodBase body = machine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) ?? target;
                        fp = MethodFingerprint.Of(body);
                    }
                    catch { fp = "?"; }
                    used[key] = constants.Contains(fp) ? "pinned" : "-";
                }
            }
            Console.WriteLine($"== {patch.Name}: {used.Count(u => u.Value == "pinned")} pinned of {used.Count}");
            foreach (var (key, state) in used) Console.WriteLine($"  {state,-6} {key}");
        }
        return 0;
    }
    case "fieldusers":
    {
        // fieldusers <data dir> <type full name> <field>: the methods of that type (nested types included) using the field,
        // as PatchSafety.RequireFieldUsers lists them.
        Type type = Load(args[1], "")[0].GetType(args[2], true)!;
        Console.WriteLine(string.Join("\n", MethodFingerprint.FieldUsers(type, args[3], args.Contains("--writers"))));
        return 0;
    }
    case "describe":
    {
        // describe <data dir> <type full name> <method|*> [--mods dir]: fingerprint and normalized code.
        foreach (Assembly a in Load(args[1], Option("--mods")))
        {
            Type? type = a.GetType(args[2]);
            if (type == null) continue;
            foreach (MethodBase m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)).Where(m => args[3] == "*" || m.Name == args[3]))
            {
                if (m.GetMethodBody() == null) { Console.WriteLine($"{Key(m)}: no body"); continue; }
                Console.WriteLine($"{Key(m)}: {MethodFingerprint.Of(m)}");
                Console.WriteLine(MethodFingerprint.Describe(m));
            }
        }
        return 0;
    }
    case "list":
    {
        var index = Index(Load(args[1], Option("--mods")), MethodFingerprint.Of);
        foreach (var group in Constants().GroupBy(c => c.Fp))
        {
            string at = string.Join(", ", group.Select(c => c.Where));
            Console.WriteLine(index.TryGetValue(group.Key, out var ms)
                ? $"{group.Key} {string.Join(" | ", ms.Select(Key))}   [{at}]"
                : $"{group.Key} NOT FOUND   [{at}]");
        }
        return 0;
    }
    case "review":
    {
        string[] dec = { "", "" };
        int d = Array.IndexOf(args, "--decompiled");
        if (d >= 0) dec = new[] { args[d + 1], args[d + 2] };
        bool showAll = args.Contains("--all");
        var asmOld = Load(args[1], Option("--old-mods"));
        var asmNew = Load(args[2], Option("--new-mods"));
        var oldIndex = Index(asmOld, MethodFingerprint.Of);
        var newMethods = asmNew.SelectMany(Methods).ToList();
        // Methods whose signature can't be resolved here (types from assemblies not in the folder) can't be compared.
        var newByKey = newMethods.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var newByName = newMethods.GroupBy(TypeKey).ToDictionary(g => g.Key, g => g.ToList());
        var oldByKey = oldIndex.Values.SelectMany(x => x).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var reviewed = Reviewed();
        var allConstants = Constants().Select(c => c.Fp).ToHashSet();
        var counts = new Dictionary<string, int>();
        var summary = new StringBuilder();
        var details = new StringBuilder();
        var equivalents = new StringBuilder();
        foreach (var group in Constants().GroupBy(c => c.Fp))
        {
            string at = string.Join(", ", group.Select(c => c.Where).Distinct());
            if (!oldIndex.TryGetValue(group.Key, out var olds))
            {
                // Values of EquivalentFingerprints (other versions), or stale constants.
                counts["not in old"] = counts.GetValueOrDefault("not in old") + 1;
                if (showAll) summary.AppendLine($"  ?          {group.Key}  not in the old version  [{at}]");
                continue;
            }
            if (olds.DistinctBy(Key).Count() > 5)
            {
                // A code shape shared by many methods (`return true;`, a no-op default): the runtime checks it on the
                // methods it names (HookSkip/HookFilter defaults), nothing to match here.
                counts["shape"] = counts.GetValueOrDefault("shape") + 1;
                if (showAll) summary.AppendLine($"  shape      {group.Key}  shared by {olds.Count} methods  [{at}]");
                continue;
            }
            foreach (MethodBase old in olds.DistinctBy(Key))
            {
                string status;
                List<MethodBase> candidates = newByKey.GetValueOrDefault(Key(old)) ?? new();
                bool bySignature = candidates.Count > 0;
                if (!bySignature) candidates = newByName.GetValueOrDefault(TypeKey(old)) ?? new();
                var fps = candidates.Select(c => { try { return MethodFingerprint.Of(c); } catch (Exception e) { return "error: " + e.Message; } }).ToList();
                bool renamed = false;
                if (candidates.Count == 0)
                {
                    // Renamed? The most similar methods of the same outer type in the new version.
                    Type outer = old.DeclaringType!;
                    while (outer.IsNested) outer = outer.DeclaringType!;
                    string prefix = Norm(MethodFingerprint.TypeName(outer));
                    string oldText = Readable(MethodFingerprint.Describe(old));
                    candidates = newMethods.Where(m => Key(m).StartsWith(prefix + "::") || Key(m).StartsWith(prefix + "+"))
                        .Where(m => old.Name == "MoveNext" ? m.Name == "MoveNext" : !m.IsConstructor)
                        .Select(m => (m, score: Similarity(oldText, Readable(MethodFingerprint.Describe(m)))))
                        .Where(x => x.score > 0.6).OrderByDescending(x => x.score).Take(2).Select(x => x.m).ToList();
                    fps = candidates.Select(MethodFingerprint.Of).ToList();
                    renamed = candidates.Count > 0;
                }
                if (fps.Contains(group.Key) && !renamed) status = "SAME";
                else if (reviewed.TryGetValue(group.Key, out string[]? accepted) && fps.Any(accepted.Contains)) status = "REVIEWED";
                // Another constant of the sources accepts the new code: a per-version table entry, a mirror per signature.
                else if (fps.Any(allConstants.Contains)) status = "COVERED";
                else if (candidates.Count == 0 || renamed) status = "MISSING";
                else status = bySignature ? "CHANGED" : "SIGNATURE";
                counts[status] = counts.GetValueOrDefault(status) + 1;
                bool fine = status is "SAME" or "REVIEWED" or "COVERED";
                if (fine && !showAll) continue;
                string newName = renamed || !bySignature ? " -> " + string.Join(" | ", candidates.Select(Key)) : "";
                summary.AppendLine($"  {status,-10} {group.Key}  {Key(old)}{newName}  [{at}]");
                if (fine || candidates.Count == 0) continue;
                details.AppendLine(renamed
                    ? $"=== MISSING: {Key(old)}  (expected {group.Key}): most similar code in the new version (renamed?):"
                    : $"=== {status}: {Key(old)}  (expected {group.Key})  [{at}]");
                for (int i = 0; i < candidates.Count; i++)
                {
                    MethodBase now = candidates[i];
                    details.AppendLine($"  new: {Key(now)}  fingerprint {fps[i]}");
                    details.AppendLine("  IL diff (normalized, generated code included):");
                    PrintDiff(Readable(MethodFingerprint.Describe(old)), Readable(MethodFingerprint.Describe(now)), details);
                    string? oldSrc = Source(dec[0], old), newSrc = Source(dec[1], now);
                    if (oldSrc != null && newSrc != null && oldSrc != newSrc)
                    {
                        details.AppendLine("  C# diff:");
                        PrintDiff(oldSrc, newSrc, details);
                    }
                    var oldCallees = Callees(old);
                    var added = Callees(now).Except(oldCallees).ToList();
                    var removed = oldCallees.Except(Callees(now)).ToList();
                    foreach (string r in removed) details.AppendLine($"  no longer calls: {r}");
                    foreach (string a in added)
                    {
                        MethodBase? callee = newByKey.GetValueOrDefault(a)?.FirstOrDefault();
                        string before = oldByKey.TryGetValue(a, out MethodBase? existed)
                            ? MethodFingerprint.Of(existed) == (callee == null ? "" : MethodFingerprint.Of(callee)) ? "existed before, unchanged" : "existed before, CHANGED"
                            : "new in this version";
                        details.AppendLine($"  newly calls: {a}  ({(callee == null ? "outside the game/mods" : before)})");
                        if (callee != null && callee.GetMethodBody() != null && callee.GetMethodBody()!.GetILAsByteArray()!.Length < 400)
                        {
                            foreach (string line in MethodFingerprint.Describe(callee).TrimEnd().Split('\n')) details.AppendLine("      | " + line);
                        }
                    }
                    if (bySignature)
                    {
                        equivalents.AppendLine($"        // {Key(old)}: <what changed, why the patch still holds>");
                        equivalents.AppendLine($"        [\"{group.Key}\"] = new[] {{ \"{fps[i]}\" }},");
                    }
                }
                details.AppendLine();
            }
        }
        Console.WriteLine("Summary: " + string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}")));
        Console.Write(summary);
        Console.WriteLine();
        Console.Write(details);
        if (equivalents.Length > 0)
        {
            Console.WriteLine("Entries for EquivalentFingerprints.cs, once each change is reviewed as harmless for the patch (else leave it vanilla):");
            Console.Write(equivalents);
        }
        return 0;
    }
}
Console.WriteLine("unknown command " + args[0]);
return 1;
