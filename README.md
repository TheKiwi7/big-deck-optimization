# Big Deck Optimization

A Slay the Spire 2 mod that removes the lag of huge decks and relic piles: hundreds or thousands of cards, hundreds of relics, in singleplayer and especially in co-op.

It changes nothing about the game itself: same rules, same results, same saves. It only computes the same things faster.

## What it fixes

Measured in game (Slay the Spire 2 v0.107.1), always against the unmodified game on the same state:

| Situation | Without the mod | With the mod |
|---|---|---|
| Co-op: state check after every action (2830 combat cards, 370 relics) | 1.7 s per action | 40 ms |
| Refresh after every action in combat (~950 cards, with Limitless) | 23.6 ms | 2.0 ms |
| Sorting the deck view (~950 cards) | 30–110 ms per click | under 5 ms |
| Opening or sorting a card grid | ~120 ms stutter | ~50 ms |
| Moving the mouse with hundreds of relics | constant input processing | ~50x less |

Co-op was the worst case: after every action, the game checks that all players have the same combat state, and with huge decks that check took longer than the action itself, for every player.

## Co-op

- **Client-side only.** The mod doesn't change game state, so it doesn't count as a gameplay mod: players without it can join your game. Each player gets the speed-up on their own machine only if they have it.
- **Same results as the unmodified game.** Every optimization was checked against vanilla on large states (identical state checksums). In co-op the mod also re-runs the vanilla state check from time to time and compares, byte for byte; on any difference it logs an error and goes back to vanilla for the session.

## Install

- **Steam Workshop:** subscribe (link coming with the first upload).
- **Manually:** download `BigDeckOptimization-vX.Y.Z.zip` from [Releases](../../releases) and extract it into `Slay the Spire 2/mods/` (you should end up with `mods/BigDeckOptimization/BigDeckOptimization.dll`).

No dependencies. Works with or without other mods.

## Compatibility

- Tested with BaseLib, Limitless and CampfireTrade.
- With Limitless installed, scrolling enchanted cards in card grids is also faster.
- If another mod modifies the same game code, the overlapping optimization steps aside and that mod's behavior wins.
- Game or mod updates: optimizations that mirror a piece of game (or Limitless) code only turn on if that exact code is unchanged. An update that touches it disables just that optimization (logged), never the game.

## Troubleshooting

Open the console (`` ` `` key, `*` or `^` on AZERTY) and type:

- `bdopt` — state of each optimization.
- `bdopt off` — turns every optimization off (vanilla behavior) without restarting; `bdopt on` turns them back on.

If something looks wrong, check whether it still happens with `bdopt off`, then open an issue with your log from `%APPDATA%\SlayTheSpire2\logs\` (lines start with `[BigDeckOptimization]`).

## Building from source

Requirements: .NET 9 SDK, Slay the Spire 2 installed (the build references its assemblies).

```
dotnet build src/BigDeckOptimization/BigDeckOptimization.csproj -c Debug
```

- The build copies the mod into `<game>/mods/BigDeckOptimization/` (close the game first). Set a different game path in a `Directory.Build.local.props` with `<GameDir>`.
- Debug builds include test console commands (`bigdeck`, `bdperf`) used to create huge decks and compare against vanilla; Release builds don't.
- `python tools/release/package.py` builds the Release version and prepares the zip and the Workshop upload.
- Design notes, measurements and the rules every patch follows: [DESIGN.md](DESIGN.md).

## License

[MIT](LICENSE).
