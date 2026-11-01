using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Script <c>leakcheck [label]</c>: walks every object reachable from this mod's static fields (the main thread's for
/// the [ThreadStatic] ones, weak tables excluded by construction) looking for the run <c>tomenu</c> left, and logs the
/// paths that reach it. "Still alive" alone doesn't say who holds a run, the game may; a path does.
/// <c>leakcheck current</c> looks for the current run instead: the positive control, a walk finding nothing there
/// while a fight is on would mean the walk is blind.
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
                    LeakVisit(root, null, $"{type.Name}.{field.Name}", target, parents, queue, paths);
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
        Say(paths.Count == 0
            ? $"  LEAKCHECK {label}: no static of {(game ? "the game" : "the mod")} reaches {(current ? "the current run" : "the left run")} ({walked})"
            : $"  LEAKCHECK {label}: REACHED through {paths.Count} path(s) ({walked})");
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

    private static string LeakPath(object? parent, string via, Dictionary<object, (object? Parent, string Via)> parents)
    {
        var steps = new List<string> { via };
        for (object? node = parent; node != null; node = parents[node].Parent)
        {
            steps.Add($"{parents[node].Via} ({node.GetType().Name})");
        }
        steps.Reverse();
        return string.Join(" -> ", steps);
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
