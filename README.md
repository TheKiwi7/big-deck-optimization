# Big Deck Optimization

A Slay the Spire 2 mod that removes the lag of huge decks and relic piles: hundreds or thousands of cards, hundreds of relics, in singleplayer and especially in co-op.

It changes nothing about the game itself: same rules, same results, same saves. It only computes the same things faster (plus one optional visual shortcut, see below).

## What it fixes

Measured in game (Slay the Spire 2 v0.107.1), always against the unmodified game on the same state:

| Situation | Without the mod | With the mod |
|---|---|---|
| Entering a combat (8,400 cards, 368 relics) | ~31 s | ~4 s |
| Combat, doing nothing (8,400 cards, BetterSpire2 installed) | ~100–300 ms per frame | smooth |
| End of combat (8,400 cards) | ~1.2 s freeze | ~0.3 s |
| Co-op: state check after every action (2830 combat cards, 370 relics) | 1.7 s per action | 40 ms |
| Refresh after every action in combat (~950 cards, with Limitless) | 23.6 ms | 2.0 ms |
| Sorting the deck view (~950 cards) | 30–110 ms per click | under 5 ms |
| Moving the mouse with hundreds of relics | constant input processing | ~50x less |

Co-op was the worst case: after every action, the game checks that all players have the same combat state, and with huge decks that check took longer than the action itself, for every player.

## Fast combat start (visual)

With 50 relics or more, entering a combat plays every relic effect (Vulnerable, Weak, Block...) without its animation pause, like the game's own Instant speed does. The effects and results are exactly the same, only the waiting is gone. It can be turned off (`bdopt fast_combat_start off` or in ModConfig).

## Co-op

- **Client-side only.** The mod doesn't change game state, so it doesn't count as a gameplay mod: players without it can join your game. Each player gets the speed-up on their own machine only if they have it.
- **Same results as the unmodified game.** Every optimization was checked against vanilla on large states (identical state checksums), and in local co-op tests both players stayed in sync. With up to 1,000 cards in combat, the mod also re-runs the vanilla state check from time to time and compares, byte for byte; on any difference it logs an error and goes back to vanilla for the session. Above that size the re-check would freeze the game for seconds, so it is skipped.

## Install

- **Steam Workshop:** [subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3824717727).
- **Manually:** download `BigDeckOptimization-vX.Y.Z.zip` from [Releases](../../releases) and extract it into `Slay the Spire 2/mods/` (you should end up with `mods/BigDeckOptimization/BigDeckOptimization.dll`).

No dependencies. Works with or without other mods.

## Settings

Everything is on by default. With [ModConfig](https://steamcommunity.com/sharedfiles/filedetails/?id=3747557003) installed, each optimization has its own switch in its settings tab (saved by ModConfig). Without it, use the console (`` ` `` key, `*` or `^` on AZERTY):

- `bdopt` — state of each optimization.
- `bdopt off` / `bdopt on` — every optimization off (vanilla behavior) / back on, no restart needed.
- `bdopt <option> off` / `on` — one optimization, e.g. `bdopt fast_combat_start off` (the list is shown by `bdopt`).

An optimization that turned itself off after an error stays off until the game restarts, whatever the settings say.

## Compatibility

- Tested with BaseLib, Limitless, CampfireTrade, BetterSpire2, ModConfig and Unlimited (sts2unlimited).
- With Limitless installed, scrolling enchanted cards in card grids is also faster. With BetterSpire2, its damage preview no longer slows down every frame.
- If another mod modifies the same game code, the overlapping optimization steps aside and that mod's behavior wins.
- Game or mod updates: optimizations that mirror a piece of game (or Limitless) code only turn on if that exact code is unchanged. An update that touches it disables just that optimization (logged), never the game.

## Troubleshooting

If something looks wrong, check whether it still happens with `bdopt off`, then open an issue with your log from `%APPDATA%\SlayTheSpire2\logs\` (lines start with `[BigDeckOptimization]`).

## Building from source

Requirements: .NET 9 SDK, Slay the Spire 2 installed (the build references its assemblies).

```
dotnet build src/BigDeckOptimization/BigDeckOptimization.csproj -c Debug
```

- The build copies the mod into `<game>/mods/BigDeckOptimization/` (close the game first). Set a different game path in a `Directory.Build.local.props` with `<GameDir>`.
- Debug builds include test tooling (`bigdeck`, `bdperf` console commands, an unattended test runner: see [tools/autotest](tools/autotest/README.md)); Release builds don't.
- `python tools/release/package.py` builds the Release version and prepares the zip and the Workshop upload.
- Design notes, measurements and the rules every patch follows: [DESIGN.md](DESIGN.md).

## License

[MIT](LICENSE).
