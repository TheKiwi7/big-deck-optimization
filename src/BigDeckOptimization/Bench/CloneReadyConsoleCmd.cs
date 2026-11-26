using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace BigDeckOptimization.Bench;

/// <summary>
/// bdclonable: from game v0.114 the rest site's clone option comes from a Clone enchantment in the deck
/// (<c>Clone.TryModifyRestSiteOptions</c>, <c>CloneRestSiteOption.IsEnabled</c>: a deck card whose <c>Enchantment is Clone</c>)
/// and no longer from Pael's Growth. Limitless 1.2.1 wraps every enchantment of a card in its <c>MultiEnchantment</c> (its
/// <c>set_Enchantment</c> prefix, even on a card without one), so with it no card ever has a bare Clone and the option never
/// shows. Test tooling only: gives the first deck card without an enchantment (else the first card) a bare Clone, written to
/// the property's field when Limitless is loaded (past its prefix), then applied as the game's Enchant does. Limitless's own
/// <c>OnSelect</c> then clones that card and every card with a stacked Clone, as the option did before v0.114. Nothing on an
/// older game. Networked: run on every peer through the action queue, deterministic.
/// </summary>
public class CloneReadyConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "bdclonable";

    public override string Args => "";

    public override string Description => "[BigDeckOptimization] Test tooling: makes the rest site's clone option available.";

    public override bool IsNetworked => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
        {
            return new CmdResult(success: false, "A run is currently not in progress!");
        }
        if (typeof(Clone).GetMethod("TryModifyRestSiteOptions")?.DeclaringType != typeof(Clone))
        {
            return new CmdResult(success: true, "CLONABLE not needed: the option is Pael's Growth's on this game version");
        }
        CardModel? card = issuingPlayer.Deck.Cards.FirstOrDefault(c => c.Enchantment == null) ?? issuingPlayer.Deck.Cards.FirstOrDefault();
        if (card == null)
        {
            return new CmdResult(success: false, "no card in the deck");
        }
        EnchantmentModel clone = ModelDb.Enchantment<Clone>().ToMutable();
        if (AccessTools.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment") != null)
        {
            // A bare enchantment first, then the game's setter: Limitless's prefix then installs the value "with vanilla replace
            // semantics" (its BARE branch), and every other patch of the setter (this mod's caches among them) sees the write.
            // The field write alone would leave this mod's listener caches stale (its own check said so: a fresh walk differs).
            AccessTools.Field(typeof(CardModel), "<Enchantment>k__BackingField").SetValue(card, clone);
            AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.Enchantment)).Invoke(card, new object[] { clone });
            clone.ApplyInternal(card, 1m);
        }
        else
        {
            card.ClearEnchantmentInternal();
            card.EnchantInternal(clone, 1m);
        }
        clone.ModifyCard();
        return new CmdResult(success: true, $"CLONABLE {card.Id.Entry}: {card.Enchantment?.GetType().Name}");
    }
}
