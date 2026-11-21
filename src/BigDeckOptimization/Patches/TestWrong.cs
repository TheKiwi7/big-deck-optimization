#if BENCH
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Test builds only: the positive controls of the safety nets (<c>bdperf &lt;part&gt; wrong</c>). Arming a name makes the next
/// pass through that net's check see a wrong answer (a kept list missing an entry, a kept state of another card, a bit
/// flipped in a kept serialization), and run its check at once: the check must find it and take its fallback, which
/// gives the game's own result. Nothing is armed in a normal session; each armed name is taken once.
/// </summary>
internal static class TestWrong
{
    /// <summary>Every name a net takes, with what it makes wrong (bdperf lists them).</summary>
    internal static readonly (string Name, string What)[] Known =
    {
        ("segments", "FastListeners: a cached hit's entries (VerifySegment)"),
        ("segments.moves", "FastListeners: a segment kept across card moves (CheckMoveHit)"),
        ("segments.subscribers", "FastListeners: a cached hit of a filter keeping subscribers (CheckSubscribers)"),
        ("segments.shifts", "FastListeners: a snapshot after shifted moves (MatchesPiles)"),
        ("reuse.trusted", "SnapshotCardReuse: a trusted pile's kept states (its sampled full read)"),
        ("reuse.moved", "SnapshotCardReuse: a moved pile's kept card state (its sampled check)"),
        ("reuse.card", "SnapshotCardReuse: a reused card snapshot (its sampled serialization compare)"),
        ("pilebits", "SnapshotCardReuse: a pile's kept header bits (PileSerializePostfix)"),
        ("pile", "PileIndex: a fast CardModel.Pile answer (its sampled vanilla scan)"),
        ("singlescan", "PileIndex: RemoveInternal's single scan (its sampled List.IndexOf)"),
        ("combatcards", "CombatCardSet: a CombatState.ContainsCard answer (its sampled scan of the combat's cards)"),
        ("clonememo", "LimitlessCloneMemo: a kept inner card (its verify compare)"),
        ("reserialize", "LimitlessReserialize: a kept stored list (Verify)"),
        ("recalc", "LimitlessRecalcSkip: a skipped recalculation's memo (its sampled WouldKeep)"),
        ("hookskip", "LimitlessHookSkip: a dropped stacked enchantment (its sampled hook check)"),
        ("listenercache", "ListenerCache: a kept listener list (CheckedAgainstKept)"),
        ("hookfilter.walk", "HookFilter: a walked listener list (its bench walk check, needs bdperf checks on)"),
        ("hookfilter.hitting", "HookFilter: a filtered ShouldAllowHitting answer (its bench check, needs bdperf checks on)"),
        ("hookfilter.cost", "HookFilter: a filtered cost color answer (its bench check, needs bdperf checks on)"),
    };

    private static readonly HashSet<string> _armed = new();
    private static volatile int _count;

    internal static void Arm(string name)
    {
        lock (_armed)
        {
            _armed.Add(name);
            _count = _armed.Count;
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is armed (still armed after this): a net whose wrong answer can only be made in some
    /// passes (a pile of two cards or more, a reused card whose bits another one doesn't share) looks first, and takes it
    /// (<see cref="Take"/>) only in a pass where the answer really is made wrong: a take that changed nothing proves nothing.
    /// </summary>
    internal static bool IsArmed(string name)
    {
        if (_count == 0)
        {
            return false;
        }
        lock (_armed)
        {
            return _armed.Contains(name);
        }
    }

    /// <summary>Whether <paramref name="name"/> was armed (disarmed now): the caller makes its answer wrong and checks it at once.</summary>
    internal static bool Take(string name)
    {
        if (_count == 0)
        {
            return false;
        }
        lock (_armed)
        {
            if (!_armed.Remove(name))
            {
                return false;
            }
            _count = _armed.Count;
        }
        Log.Info($"[{ModEntry.Id}] TESTWRONG {name}: made wrong for its check (test)");
        return true;
    }

    /// <summary>The names armed and not taken yet.</summary>
    internal static string[] Armed()
    {
        lock (_armed)
        {
            return _armed.OrderBy(n => n).ToArray();
        }
    }
}
#endif
