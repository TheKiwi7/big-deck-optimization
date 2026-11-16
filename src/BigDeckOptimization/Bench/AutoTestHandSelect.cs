using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

public static partial class AutoTest
{
    /// <summary>Longest wait for the game's own revalidation to end the held selection, once its valid cards are gone.</summary>
    private const double HandSelectEndSeconds = 15;

    /// <summary>
    /// Script <c>handselect &lt;CARD_ID&gt; [hold &lt;s&gt;] [free]</c> (solo only): plays the hand card through the game's own hand
    /// selection (<c>NPlayerHand.SelectCards</c>: the tooling's automatic card selectors are taken off for this one choice, so
    /// nothing answers it), keeps it open <c>hold</c> seconds (default 1: the CombatStateChanged dispatches of the play's own
    /// changes run meanwhile, each calling <c>NPlayerHand.RevalidateSelectionAfterStateChange</c>), then removes from the combat
    /// every hand card the selection's filter accepts (<c>CardPileCmd.RemoveFromCombat</c>, as the game's <c>remove_card</c>
    /// does). The next CombatStateChanged dispatch runs the game's revalidation, which finds no valid card and ends the selection
    /// with none (<c>TrySetResult</c>): the card's play then goes on inline, inside the dispatch, the case ListenerCache's
    /// suspension covers (<c>ListenerCache.SuspendedRevalidations</c>). Fails when the selection never opens (a selector
    /// answered, or too few valid cards: the game picks them itself), or isn't ended by the game within
    /// <see cref="HandSelectEndSeconds"/>; whatever happens, a selection still open when the line ends is ended with no card
    /// (never a session left waiting on a choice) and the selectors are put back. Use a card filtering the hand (DUAL_WIELD:
    /// attacks and powers) with at least two valid cards in hand.
    /// </summary>
    private static async Task HandSelect(SceneTree tree, string arg)
    {
        List<string> args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool free = args.Remove("free");
        double hold = 1;
        int holdAt = args.IndexOf("hold");
        if (holdAt >= 0)
        {
            hold = double.Parse(args[holdAt + 1], System.Globalization.CultureInfo.InvariantCulture);
            args.RemoveRange(holdAt, 2);
        }
        if (args.Count != 1)
        {
            throw new ArgumentException("handselect <CARD_ID> [hold <s>] [free]");
        }
        string id = args[0].ToUpperInvariant();
        if (RunManager.Instance.IsInProgress && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            throw new NotSupportedException("handselect is solo only (a co-op hand choice is synchronized with the other players)");
        }
        await Until(tree, IsIdle, 120, "idle");
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        CombatState combat = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        CardModel card = PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => c.Id.Entry == id)
            ?? throw new InvalidOperationException($"{id} not in hand: use 'cmd card {id} hand' first");
        NPlayerHand hand = NPlayerHand.Instance ?? throw new InvalidOperationException("no hand node");
        FieldInfo filterField = AccessTools.Field(typeof(NPlayerHand), "_currentSelectionFilter")
            ?? throw new MissingFieldException(nameof(NPlayerHand), "_currentSelectionFilter");
        FieldInfo sourceField = AccessTools.Field(typeof(NPlayerHand), "_selectionCompletionSource")
            ?? throw new MissingFieldException(nameof(NPlayerHand), "_selectionCompletionSource");

        // The selectors this tooling installed answer every hand choice before the game's screen opens (CardSelectCmd.FromHand):
        // off for this choice, put back in the finally.
        // Last pushed first (the combat one, from autoroom, comes after 'selector auto'): v0.113's stack resumes a suspended
        // selector when the newer one is disposed. Pushed back in the opposite order in the finally.
        bool hadSelector = _selector != null, hadCombatSelector = _combatSelector != null;
        _combatSelector?.Dispose();
        _combatSelector = null;
        _selector?.Dispose();
        _selector = null;
        long suspendedBefore = ListenerCache.SuspendedRevalidations;
        bool forced = false;
        try
        {
            object? energyCost = AccessTools.Property(typeof(CardModel), "EnergyCost")?.GetValue(card);
            bool costsX = energyCost != null && AccessTools.Property(energyCost.GetType(), "CostsX")?.GetValue(energyCost) is true;
            if (free && !costsX)
            {
                card.SetToFreeThisTurn();
            }
            Creature? enemy = combat.Enemies.FirstOrDefault(e => e.IsAlive);
            Say($"  handselect: playing {id}, its hand selection held {hold:0.##} s");
            if (!card.TryManualPlay(card.CanPlayTargeting(enemy) ? enemy : null))
            {
                throw new InvalidOperationException($"{id} cannot be played now");
            }
            // Open within 30 s; idle again for 10 frames after the first 60 means the play ended without it.
            double openBy = _clock.Elapsed.TotalSeconds + 30;
            int frames = 0, idleFrames = 0;
            while (!hand.IsInCardSelection)
            {
                idleFrames = ++frames > 60 && IsIdle() ? idleFrames + 1 : 0;
                if (_clock.Elapsed.TotalSeconds > openBy || idleFrames >= 10)
                {
                    throw new InvalidOperationException($"{id}'s hand selection never opened (a card selector answered it, or the game "
                        + "picked the cards itself: no more valid hand cards than it asks for)");
                }
                await Frames(tree, 1);
            }
            Func<CardModel, bool> filter = filterField.GetValue(hand) as Func<CardModel, bool>
                ?? throw new InvalidOperationException($"{id}'s hand selection has no filter: the game's revalidation returns at once there");
            int validAtOpen = PileType.Hand.GetPile(me).Cards.Count(filter);
            await Seconds(tree, hold);
            if (!hand.IsInCardSelection)
            {
                throw new InvalidOperationException($"{id}'s hand selection ended during the hold, nothing removed yet");
            }
            long suspendedHeld = ListenerCache.SuspendedRevalidations - suspendedBefore;
            List<CardModel> valid = PileType.Hand.GetPile(me).Cards.Where(filter).ToList();
            Say($"  handselect: open with {valid.Count} valid hand card(s) (at open {validAtOpen}), {suspendedHeld} revalidation(s) "
                + $"suspended while held; removing them from the combat");
            // All at once: the hand loses every valid holder before the next dispatch, which then ends the selection.
            await CardPileCmd.RemoveFromCombat(valid);
            double endBy = _clock.Elapsed.TotalSeconds + HandSelectEndSeconds;
            while (hand.IsInCardSelection && _clock.Elapsed.TotalSeconds < endBy)
            {
                await Frames(tree, 1);
            }
            if (hand.IsInCardSelection)
            {
                throw new InvalidOperationException($"{id}'s hand selection still open {HandSelectEndSeconds:F0} s after its last valid card left: "
                    + "the game's revalidation didn't end it");
            }
            await Until(tree, () => IsIdle() || !CombatManager.Instance.IsInProgress, 120, "idle after the held selection");
            Say($"  HANDSELECT {id} ended by the game's revalidation after {valid.Count} valid card(s) removed: "
                + $"{ListenerCache.SuspendedRevalidations - suspendedBefore} revalidation(s) suspended in all (listener optimizations "
                + $"{(Config.Settings.Master ? "on" : "off")})");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(hand) && hand.IsInCardSelection
                && sourceField.GetValue(hand) is TaskCompletionSource<IEnumerable<CardModel>> pending)
            {
                // Never leave the session waiting on a choice nobody answers: the play goes on with no card.
                forced = pending.TrySetResult(Array.Empty<CardModel>());
            }
            if (hadSelector)
            {
                _selector ??= PushSelector(new FirstCardsSelector());
            }
            if (hadCombatSelector && CombatManager.Instance.IsInProgress)
            {
                _combatSelector ??= PushSelector(new FirstCardsSelector());
            }
            if (forced)
            {
                Say("  handselect: the selection still open was ended with no card by the tooling (the line failed)");
            }
        }
    }
}
