# Changelog

## v0.2.0

For Slay the Spire 2 v0.107.1. Measured with 8,400 cards and 368 relics.

- Entering a combat: ~31 s → ~4 s. The first turn no longer freezes for seconds (Innate cards check), and with 50+ relics the combat-start relic effects play without their animation pauses (visual only, same results; option `fast_combat_start`).
- Combat: finding which pile a card is in no longer scans every card (it is used by costs, keywords, playability...), so card plays and refreshes stay fast with thousands of cards.
- With BetterSpire2: its damage preview, recomputed several times a second, no longer slows down every frame.
- End of combat: ~1.2 s → ~0.3 s (combat card IDs and pile counters no longer re-scan whole piles for each card).
- Co-op: the safety re-check of the state against vanilla is skipped above 1,000 cards in combat, where it froze the game for seconds.
- Settings: one switch per optimization in ModConfig (optional), or `bdopt <option> on|off` in the console. An optimization that turned itself off after an error stays off.

## v0.1.0

First public release, for Slay the Spire 2 v0.107.1.

- Co-op: the game checks the full combat state after every action on every player's machine. With huge decks and relic counts it took up to 1.7 s per action (2830 combat cards, 370 relics); now ~40 ms, with the exact same result.
- Combat: the refresh after every action (card costs, hand, intents, enchantment values) is 4–12x faster with huge decks.
- Deck view and other card grids: sorting no longer freezes the game (30–110 ms → under 5 ms), opening or sorting a grid stutters less (~2.5x), and with Limitless installed, scrolling enchanted cards no longer rebuilds every enchantment tab.
- Relic bar: hundreds of relics no longer slow down every mouse move.
- Client-side only: other players don't need the mod.
