using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Script <c>leakcheck [label]</c>: walks every object reachable from this mod's static fields (the main thread's for
/// the [ThreadStatic] ones) looking for the run <c>tomenu</c> left, and logs the paths that reach it. "Still alive" alone
/// doesn't say who holds a run, the game may; a path does.
/// <c>leakcheck current</c> looks for the current run instead: the positive control, a walk finding nothing there
/// while a fight is on would mean the walk is blind.
/// Weak tables (ConditionalWeakTable) are left out by construction: their values sit behind a dependent handle. A label
/// containing <c>weak</c> walks their values too, each path naming the entry's key type: a value holds the run only while its
/// key lives, so a key the left run itself owns (a card, a pile) gives a path that keeps nothing alive, hence opt-in.
/// <c>leakcheck game...</c> paths that go through an object of this mod (a closure the game holds, a subscribed handler) are
/// marked "[through this mod: Type]": those the mod's statics can't show. Both DLLs count as the mod: a path rooted at a
/// static of BigDeckOptimization.Menu (walked by <c>leakcheck game</c>) is marked with that static's type.
/// </summary>
public static partial class AutoTest
{
    private const int LeakWalkLimit = 30_000_000;
    private const int LeakMaxPaths = 12;
    private static readonly Dictionary<Type, FieldInfo[]> _leakFields = new();

    private static void LeakCheck(string label)
    {
        bool current = label == "current";
        // "oldest" looks for the first left run still alive, not the last: the game keeps its last run until the next one.
        object? target = current ? RunManager.Instance.DebugOnlyGetState()
            : label.Contains("oldest") ? _leftRuns.Select(r => r.Target).FirstOrDefault(r => r != null)
            : _leftRuns.LastOrDefault()?.Target;
        if (target == null)
        {
            Say($"  LEAKCHECK {label}: nothing to look for ({(current ? "no run" : "left run freed")})");
            return;
        }
        Stopwatch watch = Stopwatch.StartNew();
        var parents = new Dictionary<object, (object? Parent, string Via)>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        var paths = new List<string>();
        // "game": from the statics of the game and the other mods instead, to name who holds a run when this mod doesn't.
        bool game = label.StartsWith("game");
        IEnumerable<Assembly> assemblies = game
            ? AppDomain.CurrentDomain.GetAssemblies().Where(a => a != typeof(AutoTest).Assembly && a.GetName().Name is "sts2" or "BaseLib" or "Limitless" or "BigDeckOptimization.Menu")
            : new[] { typeof(AutoTest).Assembly };
        // "bench": from the test tooling itself, in case a measure of ours is what holds the run.
        bool bench = label.StartsWith("bench");
        bool weak = label.Contains("weak");
        _leakMarkMod = game;
        _leakModRoots.Clear();
        foreach (Type type in assemblies.SelectMany(a => a.GetTypes()).Where(t => !t.ContainsGenericParameters && t.Namespace?.EndsWith(".Bench") == bench))
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && LeakWorthWalking(field.FieldType))
                {
                    object? root;
                    try
                    {
                        root = field.GetValue(null);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    string rootVia = $"{type.Name}.{field.Name}";
                    if (game && LeakIsThisMod(type))
                    {
                        _leakModRoots[rootVia] = type.FullName ?? type.Name; // A static of the Menu DLL: this mod holding the run.
                    }
                    LeakVisit(root, null, rootVia, target, parents, queue, paths);
                }
            }
        }
        while (queue.Count > 0 && parents.Count < LeakWalkLimit && paths.Count < LeakMaxPaths)
        {
            object node = queue.Dequeue();
            if (node is Array array)
            {
                if (LeakWorthWalking(array.GetType().GetElementType()!))
                {
                    foreach (object? item in array)
                    {
                        LeakVisit(item, node, "[]", target, parents, queue, paths);
                    }
                }
                continue;
            }
            if (weak && node.GetType() is { IsGenericType: true } table && table.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>))
            {
                // Entries through the table's own enumerator (.NET 9): the keys stay weak, only the values are walked.
                foreach (object? pair in (System.Collections.IEnumerable)node)
                {
                    if (pair == null)
                    {
                        continue;
                    }
                    Type pairType = pair.GetType();
                    object? key = pairType.GetProperty("Key")?.GetValue(pair);
                    LeakVisit(pairType.GetProperty("Value")?.GetValue(pair), node, $"[weak value, key {key?.GetType().Name ?? "null"}]", target, parents, queue, paths);
                }
            }
            foreach (FieldInfo field in LeakFieldsOf(node.GetType()))
            {
                object? value;
                try
                {
                    value = field.GetValue(node);
                }
                catch (Exception)
                {
                    continue;
                }
                LeakVisit(value, node, field.Name, target, parents, queue, paths);
            }
        }
        string walked = $"{parents.Count} objects walked in {watch.ElapsedMilliseconds} ms{(parents.Count >= LeakWalkLimit ? ", LIMIT REACHED" : "")}";
        // A walk stopped at its limit saw only part of what the statics hold: finding nothing then proves nothing.
        Say(paths.Count > 0 ? $"  LEAKCHECK {label}: REACHED through {paths.Count} path(s) ({walked})"
            : parents.Count >= LeakWalkLimit ? $"  LEAKCHECK {label}: INCONCLUSIVE, the walk stopped at its limit before finding {(current ? "the current run" : "the left run")} ({walked})"
            : $"  LEAKCHECK {label}: no static of {(game ? "the game" : "the mod")} reaches {(current ? "the current run" : "the left run")} ({walked})");
        paths.ForEach(p => Say($"    {p}"));
    }

    private static void LeakVisit(object? value, object? parent, string via, object target, Dictionary<object, (object? Parent, string Via)> parents,
        Queue<object> queue, List<string> paths)
    {
        if (value == null || !LeakWorthWalking(value.GetType()))
        {
            return;
        }
        if (ReferenceEquals(value, target))
        {
            // Not recorded as visited: every distinct holder gets its own path.
            if (paths.Count < LeakMaxPaths)
            {
                paths.Add(LeakPath(parent, via, parents));
            }
            return;
        }
        if (parents.TryAdd(value, (parent, via)))
        {
            queue.Enqueue(value);
        }
    }

    /// <summary>Set for a <c>leakcheck game</c> walk: its paths through an object of this mod (outside the test tooling) say so.</summary>
    private static bool _leakMarkMod;

    /// <summary>A <c>leakcheck game</c> walk's roots declared in this mod (the Menu DLL's statics), by path step, with the declaring type.</summary>
    private static readonly Dictionary<string, string> _leakModRoots = new();

    /// <summary>A type of this mod, either DLL, outside the test tooling.</summary>
    private static bool LeakIsThisMod(Type type) =>
        (type.Assembly == typeof(AutoTest).Assembly || type.Assembly.GetName().Name == "BigDeckOptimization.Menu") && type.Namespace?.EndsWith(".Bench") != true;

    private static string LeakPath(object? parent, string via, Dictionary<object, (object? Parent, string Via)> parents)
    {
        var steps = new List<string> { via };
        string? throughMod = null;
        string rootVia = via;
        for (object? node = parent; node != null; node = parents[node].Parent)
        {
            Type type = node.GetType();
            rootVia = parents[node].Via;
            steps.Add($"{rootVia} ({type.Name})");
            if (_leakMarkMod && LeakIsThisMod(type))
            {
                throughMod ??= type.FullName;
            }
        }
        // The root static itself declared in this mod (Menu DLL): the object it holds may be the game's, the holder is ours.
        if (_leakMarkMod && _leakModRoots.TryGetValue(rootVia, out string? rootType))
        {
            throughMod = rootType;
        }
        steps.Reverse();
        return string.Join(" -> ", steps) + (throughMod == null ? "" : $" [through this mod: {throughMod}]");
    }

    private static bool LeakWorthWalking(Type type) =>
        !type.IsPrimitive && !type.IsEnum && !type.IsPointer && !type.IsByRefLike && type != typeof(string) && type != typeof(decimal)
        && !typeof(MemberInfo).IsAssignableFrom(type) && !typeof(Assembly).IsAssignableFrom(type) && !typeof(Module).IsAssignableFrom(type);

    private static FieldInfo[] LeakFieldsOf(Type type)
    {
        if (!_leakFields.TryGetValue(type, out FieldInfo[]? fields))
        {
            var all = new List<FieldInfo>();
            for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                all.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => LeakWorthWalking(f.FieldType)));
            }
            fields = all.ToArray();
            _leakFields[type] = fields;
        }
        return fields;
    }
}
