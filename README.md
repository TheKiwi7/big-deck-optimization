# Big Deck Optimization

<img src="docs/workshop_preview.jpg" alt="Big Deck Optimization: smooth at 10,000 cards" width="320" align="right">

Removes the lag of huge decks in Slay the Spire 2: thousands of cards, hundreds of relics, in singleplayer and in co-op.

**Playing with Limitless or BaseLib?** The mod also speeds up a large part of their work during combat, with the same results.

**In co-op, every player needs this mod.** The game won't let a player without it join.
Why: with a huge deck, the mod speeds the game up so much that a player without it falls far behind. For example, with about 8,000 cards and a few hundred relics, starting a combat takes about 4 seconds with the mod and over 30 seconds without it. The game can't handle players that far apart: the cards played in the meantime get lost, and the run desyncs or freezes.

**BaseLib is not required.** The mod works on its own; with [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) installed, you also get a settings menu to turn each improvement on or off.

> **Early development.** I haven't found any bug, but some might still exist. The mod gets regular updates to fix problems and speed things up further.

## What it changes

With a huge deck, the game slows down more and more until every action freezes it. With the mod:

- **Entering a combat:** with 8,000+ cards and hundreds of relics, about 4 seconds instead of half a minute.
- **Playing cards:** smooth again, even past 10,000 cards. A turn takes about half the time it did with v0.3.
- **Limitless and BaseLib:** the mod also speeds up a good part of their work during combat. Limitless' stacked enchantments are no longer re-saved after every action when nothing changed (nor the save data BaseLib attaches to each copy), and BaseLib's card modifier checks are skipped when no mod uses them. Same results.
- **Cards that search your piles:** cards like Hidden Gem no longer freeze the game.
- **Waiting in combat:** no more constant stutter.
- **End of combat:** no more long freeze before the rewards.
- **Co-op:** the long pause after every action is gone. Players with big decks no longer slow down everyone else.
- **Deck view and card grids:** sorting, opening and scrolling are fast again.
- **Lots of relics:** moving the mouse no longer slows the game down.
- **Optional:** with 50+ relics, the relic effects at the start of a combat skip their animation pauses. It looks faster, and the effects stay exactly the same (always on in co-op).

## What it doesn't change

- **The game itself:** same rules, same results, same saves. The mod only makes the game do the same work faster.
- **Your other mods:** it needs no other mod and works alongside Limitless, BetterSpire2, CampfireTrade, ModConfig and Unlimited. If another mod changes the same part of the game, this mod steps aside there.
- **Its safety:** after a game or mod update, any part of the mod that no longer fits turns itself off instead of guessing.

Co-op with very big decks is much better, but not instant: going from one turn to the next can still take a few seconds.

## Install

- **Steam Workshop:** [subscribe](https://steamcommunity.com/sharedfiles/filedetails/?id=3824717727).
- **Manually:** download `BigDeckOptimization-vX.Y.Z.zip` from [Releases](../../releases) and extract it into `Slay the Spire 2/mods/`.

## Settings

Everything is on by default. With [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) (optional), each improvement has its own switch under **Mod Configuration → Big Deck Optimization**. Some players have reported problems with BaseLib: this mod works without it, you just won't have that settings menu. You can also use the console (`` ` `` key, `*` or `^` on AZERTY; without BaseLib, these changes last until you quit the game):

- `bdopt` shows what is on.
- `bdopt off` / `bdopt on` turns the whole mod off or back on, without restarting.
- `bdopt fast_combat_start off` turns off just the faster combat start. In co-op it stays on for everyone, so all players start each combat at the same pace (`bdopt` lists the other names).

The Limitless-only switches appear unticked without Limitless, and ticked once it is installed.

## Something looks wrong?

Check whether the bug still happens with the mod turned off, then switch it back on:

- With BaseLib: in **Mod Configuration → Big Deck Optimization**, untick **Optimizations enabled**.
- With or without BaseLib: open the console and type `bdopt off` (`bdopt on` to switch it back on).

In co-op, every player turns it off (and back on) together, for the same reason: a player with the mod off falls far behind the others.

If the problem is still there with the mod off, it doesn't come from this mod. Otherwise, please open an issue saying whether it happens with the mod on, off, or both. Feedback is very welcome!

## Building from source

.NET 9 SDK, Slay the Spire 2 and [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) installed (for the settings menu DLL; found in the Workshop folder, elsewhere set `<BaseLibDll>` in `Directory.Build.local.props`), then `dotnet build src/BigDeckOptimization/BigDeckOptimization.csproj -c Debug` (copies the mod into the game's `mods` folder). Debug builds also include test tools ([tools/autotest](tools/autotest/README.md)). `python tools/release/package.py` prepares a release. Design notes and measurements: [DESIGN.md](DESIGN.md).

## License

[MIT](LICENSE).
