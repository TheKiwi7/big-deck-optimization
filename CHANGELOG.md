# Changelog

## v0.1.0

First public release, for Slay the Spire 2 v0.107.1.

- Co-op: the game checks the full combat state after every action on every player's machine. With huge decks and relic counts it took up to 1.7 s per action (2830 combat cards, 370 relics); now ~40 ms, with the exact same result.
- Combat: the refresh after every action (card costs, hand, intents, enchantment values) is 4–12x faster with huge decks.
- Deck view and other card grids: sorting no longer freezes the game (30–110 ms → under 5 ms), opening or sorting a grid stutters less (~2.5x), and with Limitless installed, scrolling enchanted cards no longer rebuilds every enchantment tab.
- Relic bar: hundreds of relics no longer slow down every mouse move.
- Client-side only: other players don't need the mod.
