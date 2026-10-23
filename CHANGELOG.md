# Changelog

## v0.2.0

Huge decks are now playable well past 10,000 cards.

- With 8,000+ cards and hundreds of relics, entering a combat takes about 4 seconds instead of half a minute.
- Playing cards and waiting in combat are smooth again, even with BetterSpire2 installed.
- The freeze at the end of a combat is mostly gone.
- With 50+ relics, the relic effects at the start of a combat skip their animation pauses (same effects, just faster). Can be turned off.
- Co-op: no more multi-second freezes from the mod's own safety checks with very big decks.
- New settings: with ModConfig, each improvement has its own switch. Without it, `bdopt <name> off` in the console.

## v0.1.0

First public release, for Slay the Spire 2 v0.107.1.

- Co-op: the game checks the full combat state after every action on every player's machine. With huge decks and relic counts it took up to 1.7 s per action (2830 combat cards, 370 relics); now ~40 ms, with the exact same result.
- Combat: the refresh after every action (card costs, hand, intents, enchantment values) is 4–12x faster with huge decks.
- Deck view and other card grids: sorting no longer freezes the game (30–110 ms → under 5 ms), opening or sorting a grid stutters less (~2.5x), and with Limitless installed, scrolling enchanted cards no longer rebuilds every enchantment tab.
- Relic bar: hundreds of relics no longer slow down every mouse move.
- Client-side only: other players don't need the mod.
