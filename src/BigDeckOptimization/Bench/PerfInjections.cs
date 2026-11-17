using System;
using System.Collections.Generic;
using System.Linq;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// bdperf subcommands that prove a safety net reacts (positive controls: a wrong answer injected where the net must see it) or
/// lower a limit so a small deck reaches the path past it. Each one changes nothing the game computes unless its net misses
/// what it was given: a scenario expects the detection line and compares its checksums with a mod-off twin.
/// </summary>
public partial class PerfConsoleCmd
{
    /// <summary>Whether a scenario set <c>pendingcap</c>: its later reports then need an overflow.</summary>
    private static bool _pendingCapSet;

    /// <summary>The injection or limit <paramref name="sub"/> names, or null for the other subcommands.</summary>
    private static CmdResult? Injection(string sub, string[] args, bool coop)
    {
        string what = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        if (what == "wrong" && WrongName(sub, args.Length >= 3 ? args[2].ToLowerInvariant() : null) is { } wrongName)
        {
            // bdperf <part> wrong [variant]: that net's next check sees a wrong answer (TestWrong): it must find it and fall back.
            if (coop)
            {
                return new CmdResult(success: false, "bdperf " + sub + " wrong is singleplayer only (a fallback on one peer only would change its checksums' path).");
            }
            TestWrong.Arm(wrongName);
            return new CmdResult(success: true, $"WRONG armed {wrongName}: {TestWrong.Known.First(k => k.Name == wrongName).What}");
        }
        switch (sub)
        {
            case "badges":
                return Badges(what, args.Length >= 3 ? args[2].ToLowerInvariant() : "", coop);
            case "baselibtypes" when what is "assume" or "clear":
                // bdperf baselibtypes assume <BaseLib type> | clear: BaseLibTypes.NoneDefined answers false for it (the branch a mod
                // defining one takes), or as the loaded assemblies say again.
                if (what == "clear")
                {
                    BaseLibTypes.AssumedForTest.Clear();
                }
                else if (args.Length >= 3)
                {
                    BaseLibTypes.AssumedForTest.Add(args[2]);
                }
                return new CmdResult(success: true, "BASELIBTYPES assumed defined: " + (BaseLibTypes.AssumedForTest.Count == 0 ? "none" : string.Join(", ", BaseLibTypes.AssumedForTest)));
            case "wrong":
                // bdperf wrong [check]: what is armed and not taken yet; "check" fails while one is (its net never saw it).
                string[] armed = TestWrong.Armed();
                return new CmdResult(success: what != "check" || armed.Length == 0, "WRONG armed: " + (armed.Length == 0 ? "none (every injection taken)" : string.Join(", ", armed))
                    + "\n  " + string.Join("\n  ", TestWrong.Known.Select(k => $"{k.Name}: {k.What}")));
            case "cardids" when what == "blind":
                // CardDbScan's sampled check (a skipped walk's whole-pile check): one add left unrecorded, every skip checked after it.
                CardDbScan.BlindForTest = true;
                return new CmdResult(success: true, $"{CardDbScan.Name}: the next add of a card without an ID is not recorded (test); every skipped walk is checked from then on.");
            case "cardids" when what == "failadd":
                return CardIdsFailedAdd();
            case "cardids":
                return new CmdResult(success: true, CardIdsReport());
            case "pendingcap":
                // CardDbScan's per-pile pending adds, lowered so a small deck overflows them (256 otherwise: reshuffles of big piles).
                if (args.Length >= 2)
                {
                    if (!int.TryParse(args[1], out int cap) || cap < 1)
                    {
                        return new CmdResult(success: false, "bdperf pendingcap <limit, 1 or more>");
                    }
                    CardDbScan.MaxPending = cap;
                    _pendingCapSet = true;
                    return new CmdResult(success: true, $"PENDINGCAP limit {CardDbScan.MaxPending}, overflows {CardDbScan.Overflows}");
                }
                // A report after the limit was set must have seen an overflow: 0 would pass a path that never ran.
                bool none = _pendingCapSet && CardDbScan.Overflows == 0 && !ModEntry.NotApplied(CardDbScan.Name);
                return new CmdResult(success: !none, $"PENDINGCAP limit {CardDbScan.MaxPending}, overflows {CardDbScan.Overflows}, " + CardIdsReport()
                    + (none ? " (limit set but no overflow: the path wasn't reached)" : ""));
        }
        return null;
    }

    /// <summary>The <see cref="TestWrong"/> name of <c>bdperf &lt;part&gt; wrong [variant]</c>, or null when that part has none.</summary>
    private static string? WrongName(string part, string? variant)
    {
        string name = part switch
        {
            "segments" or "reuse" or "hookfilter" when variant != null => part + "." + variant,
            "reuse" => "reuse.trusted",
            "hookfilter" => "hookfilter.walk",
            _ => part,
        };
        return TestWrong.Known.Any(k => k.Name == name) ? name : null;
    }

    private static string CardIdsReport() =>
        $"CARDIDS enabled {CardDbScan.Enabled}, walks {CardDbScan.Walks}, skipped {CardDbScan.Skipped}, verified {CardDbScan.Verified}, "
        + $"overflows {CardDbScan.Overflows}, failed adds dropped {CardDbScan.DroppedFailedAdds}, skipped originals {CardDbScan.SkippedAdds} (pile changed {CardDbScan.SkippedAddsChanged})";

    /// <summary>
    /// <c>bdperf cardids failadd</c>: an add the game refuses (a canonical card: AssertMutable throws before the list changes)
    /// into the hand. CardDbScan recorded it before the original ran: it must be dropped again, or every later walk of the hand
    /// would stay vanilla until combat end. Changes nothing of the game's state (the add threw, as the game's would).
    /// </summary>
    private static CmdResult CardIdsFailedAdd()
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me
            || PileType.Hand.GetPile(me) is not { } hand || hand.Cards.FirstOrDefault() is not { } inHand)
        {
            return new CmdResult(success: false, "Start a combat with a card in hand first.");
        }
        if (ModEntry.NotApplied(CardDbScan.Name) || !CardDbScan.Enabled)
        {
            return new CmdResult(success: true, $"{CardDbScan.Name} not applied or off: nothing to compare");
        }
        CardModel canonical = inHand.CanonicalInstance;
        long dropped = CardDbScan.DroppedFailedAdds;
        string threw = "nothing";
        try
        {
            hand.AddInternal(canonical);
        }
        catch (Exception e)
        {
            threw = e.GetType().Name;
        }
        bool held = CardDbScan.PendingHolds(hand, canonical);
        bool ok = threw != "nothing" && !held && CardDbScan.DroppedFailedAdds == dropped + 1 && !hand.Cards.Contains(canonical);
        return new CmdResult(success: ok, $"FAILADD {canonical.Id.Entry}: the add threw {threw}, pending holds it {held}, dropped {dropped} -> {CardDbScan.DroppedFailedAdds}: "
            + (ok ? "OK: dropped" : "MISMATCH"));
    }

    /// <summary>
    /// bdperf badges [clear | guard off|on]: HookSkip's badge guard (A4 in DESIGN). <c>clear</c> empties the run's badges and the
    /// current combat's (a state the game never makes: every run gets them), so the guard sends the filtered walks to the game's
    /// walk (<c>HookSkip.NoBadge</c> grows). <c>guard off</c> lets such a state walk filtered anyway, the positive control of
    /// <c>HookSkip.Emptied</c> (walks kept empty where the game's walk isn't). Changes the run's listeners (no badge hook runs any
    /// more): singleplayer only, and a scenario using it compares nothing with another session.
    /// </summary>
    private static CmdResult Badges(string what, string arg, bool coop)
    {
        string Report() => $"BADGES run {RunManager.Instance.DebugOnlyGetState()?.BadgeModels.Count.ToString() ?? "-"}, combat "
            + $"{CombatManager.Instance.DebugOnlyGetState()?.BadgeModels.Count.ToString() ?? "-"}, guard {(HookSkip.BadgeGuardOff ? "off" : "on")}, "
            + $"no-badge walks {HookSkip.NoBadgeWalks}, emptied walks {HookSkip.EmptiedWalks}, first kept {FastListeners.FirstKept}";
        switch (what)
        {
            case "clear":
                if (coop)
                {
                    return new CmdResult(success: false, "bdperf badges clear is singleplayer only: it changes this peer's listeners.");
                }
                if (RunManager.Instance.DebugOnlyGetState() is not { } run)
                {
                    return new CmdResult(success: false, "Start a run first.");
                }
                // RunState.BadgeModels: private setter; CombatState.BadgeModels: get-only (its backing field, set in the constructor).
                AccessTools.PropertySetter(typeof(RunState), nameof(RunState.BadgeModels)).Invoke(run, new object[] { new List<BadgeModel>() });
                if (CombatManager.Instance.DebugOnlyGetState() is { } combat)
                {
                    AccessTools.Field(typeof(CombatState), "<BadgeModels>k__BackingField").SetValue(combat, Array.Empty<BadgeModel>());
                }
                return new CmdResult(success: true, Report());
            case "guard" when arg is "off" or "on":
                HookSkip.BadgeGuardOff = arg == "off";
                return new CmdResult(success: true, Report());
            case "":
                return new CmdResult(success: true, Report());
            default:
                return new CmdResult(success: false, "bdperf badges [clear | guard off|on]");
        }
    }
}
