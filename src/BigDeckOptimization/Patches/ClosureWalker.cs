using System;
using System.Collections.Generic;
using System.Reflection;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game methods a set of roots reaches, as <see cref="MethodFingerprint.GameClosure"/> computes them (no type initializers,
/// dispatch through the game's own virtual methods), walked by one thread (LimitlessCloneMemo's worker; tools/fingerprint
/// <c>closures</c> measures it) with two savings that leave the answer exact: the callees of each method are read once for every
/// walk sharing a <see cref="CalleeGraph"/> (the overrides' closures overlap by most of their methods), and a walk stops at the
/// first method its caller says is patched by another mod (<see cref="Advance"/>: the answer is "patched" while that patch stays),
/// to be resumed from there if that patch goes away. Not thread-safe: one thread walks; others read what it publishes.
/// </summary>
internal sealed class CalleeGraph
{
    private readonly Assembly _game;
    private readonly Dictionary<MethodBase, MethodBase[]> _callees = new();

    public CalleeGraph(Assembly game) => _game = game;

    /// <summary>Methods whose callees were read (each once).</summary>
    public int Read => _callees.Count;

    public MethodBase[] Callees(MethodBase method)
    {
        if (!_callees.TryGetValue(method, out MethodBase[]? callees))
        {
            // Delegates' Invoke are listed rather than thrown on, as CodeSet's sets do (their targets aren't followed there either).
            _callees[method] = callees = MethodFingerprint.GameCallees(_game, method, new List<MethodInfo>(), initializers: false, dispatch: false).ToArray();
        }
        return callees;
    }
}

/// <summary>One root's walk over a <see cref="CalleeGraph"/>, resumable (see <see cref="CalleeGraph"/>).</summary>
internal sealed class ClosureWalk
{
    private readonly CalleeGraph _graph;
    private readonly int _limit;
    private readonly HashSet<MethodBase> _seen = new();
    private readonly List<MethodBase> _found = new();
    private readonly Queue<MethodBase> _queue = new();
    /// <summary>The method the walk stopped at (patched), expanded first when it resumes.</summary>
    public MethodBase? Blocker { get; private set; }

    public ClosureWalk(CalleeGraph graph, MethodBase root, int limit)
    {
        _graph = graph;
        _limit = limit;
        _seen.Add(root);
        _found.Add(root);
        _queue.Enqueue(root);
    }

    /// <summary>Every method reached, the root first (the whole closure once <see cref="Complete"/>).</summary>
    public IReadOnlyList<MethodBase> Found => _found;

    public bool Complete => Blocker == null && _queue.Count == 0;

    /// <summary>
    /// Walks on until the closure is complete (true) or a method <paramref name="patched"/> says yes to is reached (false: it is
    /// <see cref="Blocker"/>, its callees not read yet). A blocker no longer patched is expanded first. More than the limit throws,
    /// as GameClosure does (the caller then can't bound the set).
    /// </summary>
    public bool Advance(Func<MethodBase, bool> patched)
    {
        if (Blocker is { } blocker)
        {
            if (patched(blocker))
            {
                return false;
            }
            Blocker = null;
            Expand(blocker);
        }
        while (_queue.Count > 0)
        {
            MethodBase caller = _queue.Dequeue();
            if (patched(caller))
            {
                Blocker = caller;
                return false;
            }
            Expand(caller);
        }
        return true;
    }

    private void Expand(MethodBase caller)
    {
        foreach (MethodBase callee in _graph.Callees(caller))
        {
            if (_seen.Add(callee))
            {
                if (_found.Count >= _limit)
                {
                    throw new InvalidOperationException($"more than {_limit} game methods reached (at {MethodFingerprint.MemberName(callee)})");
                }
                _found.Add(callee);
                _queue.Enqueue(callee);
            }
        }
    }
}
