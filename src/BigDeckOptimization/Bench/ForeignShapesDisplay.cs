using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic display callers of DeckCountMemo's preview (2026-11-21, evening): "another mod" calculating a Perfected Strike in
/// hand outside the card redraw, through <c>foreignpatch</c> as ForeignPatches' own shapes. The memo answers a display caller
/// (a Godot frame or deferred callback reaching the preview through other mods' code only) and leaves every other caller to
/// the game's count (DeckCountMemo.DisplayCaller).
/// <list type="bullet">
/// <item><c>display-call deferred</c>: a <c>Callable.From(...).CallDeferred()</c> calling <c>UpdateDynamicVarPreview</c> (BetterSpire2's
/// panel refresh, SpeedX's per-frame overlay): answered.</item>
/// <item><c>display-call invoke</c>: the same through <c>MethodInfo.Invoke</c> (BetterSpire2's own call): answered.</item>
/// <item><c>display-call async</c>: from an async method's continuation after a Godot frame (an async flow, as the game's actions):
/// refused, the game counts.</item>
/// <item><c>display-call continuewith</c>: a plain closure (no async state machine) run by <c>Task.ContinueWith</c> on the main
/// thread's synchronization context (Godot's): resumed by the runtime's continuation machinery, refused.</item>
/// <item><c>display-hook-postfix</c> (a shape, removed by <c>foreignpatch remove</c>): a postfix on <c>PerfectedStrike.OnPlay</c>
/// previewing a Perfected Strike left in hand, called by the game's play (game code above it): refused.</item>
/// </list>
/// <c>foreignpatch display-check &lt;label&gt; answered|refused fresh|stale</c>: the last such call ran, was answered (or refused) by
/// the memo, and the count its calculation got equals a fresh count of the piles with the game's expression (<c>fresh</c>) or the
/// memo's held count, which differs from it (<c>stale</c>: the positive control after <c>bdperf deckcount inplace</c>); else the
/// line fails.
/// </summary>
public static class ForeignShapesDisplay
{
    public const string HookShape = "display-hook-postfix";

    public static bool Handles(string[] parts) => parts.Length >= 1 && parts[0] is "display-call" or "display-check" or HookShape;

    private static string _how = "";
    private static bool _ran;
    private static long _answered, _refused;
    private static decimal? _got;
    private static int _fresh;
    private static int? _held;

    public static IReadOnlyList<string> Run(string[] parts, Action<string, MethodBase> installed)
    {
        switch (parts[0])
        {
            case HookShape:
            {
                MethodBase target = AccessTools.Method(typeof(PerfectedStrike), "OnPlay") ?? throw new MissingMethodException(nameof(PerfectedStrike), "OnPlay");
                string id = ForeignPatches.IdPrefix + HookShape;
                new Harmony(id).Patch(target, postfix: new HarmonyMethod(typeof(ForeignShapesDisplay), nameof(OnPlayPostfix)));
                installed(id, target);
                Reset("hook");
                return new[] { $"FOREIGN {HookShape} on PerfectedStrike.OnPlay: previews a Perfected Strike left in hand at each play" };
            }
            case "display-call" when parts.Length >= 2 && parts[1] is "deferred" or "invoke":
            {
                string how = parts[1];
                Reset(how);
                Callable.From(() => Preview(how == "invoke")).CallDeferred();
                return new[] { $"FOREIGN display-call {how}: queued for the end of this frame" };
            }
            case "display-call" when parts.Length >= 2 && parts[1] == "async":
                Reset("async");
                _ = AfterAFrame();
                return new[] { "FOREIGN display-call async: an async method previews after the next frame" };
            case "display-call" when parts.Length >= 2 && parts[1] == "continuewith":
                Reset("continuewith");
                _ = Task.Delay(1).ContinueWith(_ => Preview(viaInvoke: false), TaskScheduler.FromCurrentSynchronizationContext());
                return new[] { "FOREIGN display-call continuewith: a closure previews as a continuation posted to this thread's context" };
            case "display-check" when parts.Length >= 4 && parts[2] is "answered" or "refused" && parts[3] is "fresh" or "stale":
                return Check(parts[1], parts[2] == "answered", parts[3] == "stale");
        }
        throw new ArgumentException("foreignpatch display-call deferred|invoke|async|continuewith | display-hook-postfix | display-check <label> answered|refused fresh|stale");
    }

    private static void Reset(string how)
    {
        _how = how;
        _ran = false;
        _got = null;
    }

    private static async Task AfterAFrame()
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Preview(viaInvoke: false);
    }

    private static void OnPlayPostfix(CardModel __instance)
    {
        if (_how == "hook")
        {
            Preview(viaInvoke: false, except: __instance);
        }
    }

    /// <summary>A Perfected Strike in the local hand previewed as a mod does; what the memo did and what the count gave, kept for the check.</summary>
    private static void Preview(bool viaInvoke, CardModel? except = null)
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { PlayerCombatState: { } state } me
            || PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => c is PerfectedStrike && c != except) is not { } card)
        {
            return;
        }
        long answered = DeckCountMemo.AnsweredDisplay, refused = DeckCountMemo.DisplayRefused;
        DeckCountMemo.LastOutside[DeckCountMemo.StrikeKind] = null;
        DeckCountMemo.LastDisplay[DeckCountMemo.StrikeKind] = null;
        if (viaInvoke)
        {
            typeof(CardModel).GetMethod(nameof(CardModel.UpdateDynamicVarPreview))!.Invoke(card, new object?[] { CardPreviewMode.Normal, null, card.DynamicVars });
        }
        else
        {
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, card.DynamicVars);
        }
        _answered = DeckCountMemo.AnsweredDisplay - answered;
        _refused = DeckCountMemo.DisplayRefused - refused;
        // The memo's answer (a display caller) or the game's count: never both for one call.
        _got = DeckCountMemo.LastDisplay[DeckCountMemo.StrikeKind] ?? DeckCountMemo.LastOutside[DeckCountMemo.StrikeKind];
        _fresh = state.AllCards.Count(c => c.Tags.Contains(CardTag.Strike));
        _held = DeckCountMemo.HeldCount(state, DeckCountMemo.StrikeKind);
        _ran = true;
    }

    private static IReadOnlyList<string> Check(string label, bool answered, bool stale)
    {
        string line = $"FOREIGN display-check {label} ({_how}): ran {_ran}, answered {_answered}, refused {_refused}, count got {_got?.ToString() ?? "none"}, "
            + $"the piles now {_fresh}, the memo holds {_held?.ToString() ?? "nothing"}";
        if (!_ran || _got == null)
        {
            throw new InvalidOperationException(line + " (MISMATCH: the caller never calculated)");
        }
        if (answered ? _answered < 1 || _refused != 0 : _answered != 0 || _refused < 1)
        {
            throw new InvalidOperationException(line + $" (MISMATCH: expected {(answered ? "answered by the memo" : "refused, the game's count")})");
        }
        if (stale ? _got != _held || _got == _fresh : _got != _fresh)
        {
            throw new InvalidOperationException(line + $" (MISMATCH: expected {(stale ? "the memo's stale count" : "the piles' count")})");
        }
        return new[] { line + ": ok" };
    }
}
