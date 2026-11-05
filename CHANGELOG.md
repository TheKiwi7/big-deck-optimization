# Changelog

## v1.0.0

- Version 1.0: the goal set for this mod is reached. In my tests with two players at 11,000 cards each and 189 relics, waiting, hovering, playing cards with the mouse and ending the turn all stay above 120 fps almost all the time. Starting a combat is a single pause of about half a second (your numbers will differ on another PC).
- The mod's version now shows at the top right of its settings page: every co-op player needs the same one.
- A game or mod update now turns off only the part of an optimization it really touches. If part of one stops working, its settings tooltip and `bdopt` say so.
- Better compatibility: the mod steps aside for more of the game's code when another mod changes it.
- 'Instant combat animations' can no longer freeze the game on a screen that waits for its own animations (the same kind of freeze v0.12.1 fixed).

## v0.12.1

- Fixed a crash: after quitting once a fight was won and continuing the run, a screen previewing a card transformation before the next fight (Aroma of Chaos, for example) froze the game, then crashed it.
- Fixed: after continuing a run that way, waits outside combat (rest sites, events) were skipped until the next fight.
- Turning off "Effect listener lists" now turns all of it off, and turning off "Memory cleanups at quiet moments" during a fight fully restores the normal memory settings.
- Better safety nets: if the co-op state check ever finds a difference, every part of it now turns off at once, and the Squeeze, Perfected Strike and Crescent Spear counts go back to the game's own after an error.

## v0.12.0

- Co-op runs no longer get heavier with every combat: the game kept a full copy of every other player's deck in memory at each combat, for the rest of the run, and this mod kept a finished combat's cards until the next one. In my tests with two players at 10,300 cards each, memory after 8 combats is about 530 MB instead of 1.3 GB, and the longest freeze when starting the 8th combat is about 0.6 s instead of 2 s. The more players, the bigger the difference.
- Combat start with Limitless is much shorter, from the first combat of a session and for every player's deck: in my tests with two players at 10,300 cards each, the longest freeze is about 0.5 s instead of 0.9 s. Solo with 10,300 cards, no room took longer than about 0.3 s to enter over a whole run.
- Shorter memory cleanups at every deck size: the game's animated texts create much less temporary data. In my tests on the map, they take about 0.01 s instead of 0.02 to 0.03 s, and a third of the time overall.
- Squeeze, Perfected Strike and Crescent Spear no longer count your whole deck each time their value is shown.
- Now tested with four players: 10,300 cards each (41,000 in one combat) with no desync, and about 320 fps while nothing happens on the PC used for the tests.
- Better protection against game updates: two more optimizations now turn themselves off if the game code they rely on changes.

## v0.11.0

- Shorter freezes everywhere, even with a small deck: the game's animated texts now free their temporary memory right away. In my tests on the map, the memory cleanups take about 0.02 to 0.03 s instead of 0.07 to 0.09 s, and the slowest frame went from about 85 ms to 29 ms (your numbers will differ on another PC).
- Co-op with huge decks: the state check after each action is much shorter when a card changes cards in your draw pile, or while a cost effect is active (Curious, Corruption, Spiked Gauntlets...). In my tests with 11,000 cards per player, it re-read the whole draw pile in about 5 to 9 ms after such actions, and now takes about 0.6 ms.
- Co-op: after Apotheosis on a huge deck, the state check takes about 8 ms instead of about 20 with 10,300 cards. With 369 relics per player, checking the relics takes about 0.2 ms per player instead of 0.5.
- Fixed: with BaseLib, the co-op state check could miss cost changes made by some cards (Bullet Time making your hand free, for example). Players who all have the mod got the same result on every side, so games didn't desync.

## v0.10.0

- Co-op with huge decks: playing cards is much smoother. In my tests with two players at about 20,000 cards each, the slowest moments while playing cards quickly are now at about 90 to 130 fps instead of 55, and about 120 to 155 fps instead of 105 when playing with the mouse (your numbers will differ on another PC).
- Long runs no longer get slower and slower: with BaseLib, every finished combat stayed in memory until the end of the run. In my tests with 10,300 cards, memory at the end of a run is about 260 MB instead of 1.2 GB, and starting an Act 3 combat takes about 0.5 s instead of up to 1.6 s.
- Shorter freezes when entering a room: the game's memory cleanup after loading a room now runs in the background (about 0.1 to 0.25 s instead of 0.3 to 0.65 s in my tests with 10,300 cards).
- With Limitless: combats start faster after the first one of a session (in my tests with 10,300 cards, preparing the deck takes about 0.14 s instead of 0.27 s).
- No more error written to the game's log at every input when your settings still hold old controller actions that no longer exist (hundreds per card played).
- Fixed: after a relic that changes card costs was removed (Spiked Gauntlets, for example), the co-op state check could differ from the game's own. Players who all have the mod got the same result on every side, so games didn't desync.

## v0.9.0

- Much smoother combat with huge decks. In my tests with about 32,000 cards, playing cards now runs at about 170 fps instead of 65, and the slowest moments of an end of turn at about 120 fps instead of 17 (your numbers will differ on another PC).
- Fewer short freezes while cards are played: the game's memory cleanups now wait for a moment when nothing is animating. New option, on by default: it uses a few hundred MB more memory in combats with 1,000 cards or more.
- Co-op with huge decks and Limitless: in my tests with 10,000 cards, the longest freezes when ending a turn are now about 0.1 s instead of 0.35 s, and most turns no longer have any freeze of 0.1 s while cards are played.
- Reshuffling thousands of cards no longer freezes the game for minutes: a steady stream of cards flies to the pile instead of one per card. Small piles look the same. New option, on by default.
- No more freeze when Dampen weakens a whole deck (the Knights elite): in my tests with 8,400 cards, the longest freeze at the end of that turn is under 0.1 s instead of 1.3 s.
- Apotheosis on a huge deck: about 0.3 s instead of 1 s with 32,000 cards in my tests.
- Fixed: since v0.7.0, some optimizations weren't really running, the fast deck view sorting among them.
- Fixed: with Limitless, the mod kept your last run in memory after you went back to the main menu.
- The game starts more smoothly: the mod installs its optimizations over a few frames instead of all at once.
- Better at surviving game updates: when an update changes the game, even fewer optimizations switch back to the normal game, and only the part relying on the changed code.

## v0.8.0

- Much shorter freeze when an enemy afflicts your whole deck (the Spectral Knight's Hex in the Knights elite) with Limitless stacked enchantments: in my tests with a 4,100-card deck, about 2 seconds instead of 10 (the unmodified game freezes for about 5 minutes there; your numbers will differ on another PC).
- Co-op with huge decks: the state check after every action produces far less garbage, so the short freezes while everyone plays cards are mostly gone. In my tests with a 4,100-card deck, the slowest moments while playing cards went from about 10 fps to about 25 fps (your numbers will differ on another PC).
- Card texts (descriptions, and Limitless's stacked enchantment lines) are built with much less memory churn, so the hand refreshes more smoothly after each action.

## v0.7.0

- Game beta branch: every optimization now works there too, including the faster damage calculation, the remaining effects and the first-turn speedup that were still off.
- Better at surviving game and mod updates: when an update changes the game, only the optimizations that depend on the changed code switch back to the normal game. Before, an update that only shuffled some internal names could switch off far more than needed.
- More robust: if an error ever happened inside the optimizations while the game resolves an effect, that effect now finishes normally and the optimization turns itself off for the session.
- Faster game start: in my tests the mod now loads in about 0.3 s instead of almost 1 s (your numbers will differ on another PC).

## v0.6.4

- Game beta branch: most optimizations that were off there work again, including the most important ones for huge decks (skipping the cards that don't react to each effect, finding which pile a card is in, and with Limitless, saving stacked enchantments). Still off on the beta for now: the faster damage calculation, a handful of effects, and a small first-turn speedup.

## v0.6.3

- Clearer description of the instant combat animations option in the settings menu: outside of fights nothing changes, and in co-op it only affects your own screen.

## v0.6.2

- Fixed: the fast combat start kept skipping animations for the whole fight in solo, so attacks and enemy turns played almost instantly. It now only speeds up the combat loading (entering the fight and the relics that trigger at the start of combat); your turns and the enemies' turns play at the game's normal pace again.
- New option, off by default: instant combat animations. Every fight plays without animation waits. Outside of fights, nothing changes. In co-op it only affects your own screen: the other players still see the normal animations.

## v0.6.1

- Fixed: on the game's beta branch, combat broke from the first fight (cards not loading, shown as error cards), even with the mod's switches turned off. A part of the mod that doesn't fit your game version is now skipped when the game starts, and that part keeps the normal game behavior.
- If a part of the mod ever causes an error during the game, it turns itself off for the session and the normal game takes over.
- With BaseLib: if the mod is switched off when you start the game, none of it is loaded at all. Switching it off still takes effect right away; switching it back on then applies the next time you start the game.

## v0.6.0

Much smoother combat and card lists with very large decks, and a translated settings menu.

- Smoother combat with very large decks: in my tests at about 32,000 cards, playing cards ran at about 52 frames per second instead of 29, and the end of the turn at about 30 instead of 17, with shorter memory pauses (your numbers will differ on another PC).
- With BaseLib: scrolling the draw pile, discard pile or deck view with a huge deck is smooth again, even when dragging the scrollbar, and the pile view opens faster. New switch: BaseLib card type line.
- The faster combat start now applies from the first relic instead of from 50 relics. In solo, a new slider in the settings sets how many relics it starts from; in co-op it's always on, so every player starts at the same pace.
- The settings menu is translated into every language the game offers and follows the game's language (English otherwise). The top of the page now says what co-op requires.
- If another mod changes the same part of the game as one of the optimizations, that optimization's tooltip names the mod (that part keeps the normal game behavior, as before).

## v0.5.0

Copying thousands of cards no longer freezes or crashes the game, and turns are a bit faster.

- Campfire clone with a huge deck: much faster. In my test (Limitless' stacked Clone enchantment, 4 Bing Bongs copying every new card), the deck went from 16,840 to 85,240 cards: over a minute and a half before, often ending in a black screen or a crash; now about 14 seconds.
- Reflections (the event whose Shatter choice copies your whole deck): about 6 times faster at Instant speed, and much smoother at the other speeds.
- Relics that trigger on every new card (like Bing Bong) no longer fill the screen with thousands of flashes at once, which blacked out the screen and could crash the game: a relic flashing several times at the same moment now shows one flash. New switch: Merge repeated relic flashes.
- Turns are about 10% faster with huge decks.
- Settings menu (with BaseLib): a switch that can't work in your setup (Limitless not installed, a game update, an error) is greyed out, and hovering it tells you why.

## v0.4.0

With huge decks (8,000+ cards), a turn now takes about half as long as with v0.3.0, and much of Limitless' and BaseLib's work optimized.

- Co-op: every player now needs the mod, with the same version (the game checks it when someone joins). With huge decks, a player without the mod starts every combat far behind the others, and the game drops the actions played in the meantime: desyncs or a frozen run. This was already the case with earlier versions.
- The faster combat start is always on in co-op (its switch now only applies to solo), so every player starts the combat at the same pace.
- Turns are faster at 8,000+ cards: a turn of 4 plays and its end takes about half the time of v0.3.0, with far fewer memory pauses. The 59 cards that look through your piles, played one by one, also take about half the time.
- With Limitless: stacked enchantments no longer rebuild their saved copy after every action when nothing changed (same content kept). This was the biggest cost of playing cards with thousands of enchanted cards, together with the save data BaseLib attached to every rebuilt copy.
- New switch in the settings: Limitless enchantment copies. The Limitless-only switches now appear unticked when Limitless isn't installed, and ticked once it is.
- Safer: card reshuffles are handled even when another effect reorders the draw pile, and the co-op safety check compares against a fully unoptimized game.
- Smaller download (about 450 KB instead of 640 KB): the mods screen image is now a high-quality JPEG (242 KB instead of 433 KB, no visible difference), and the debug symbols are inside the DLLs (error logs keep their line numbers).

## v0.3.0

Faster turns with huge decks.

- With 8,000+ cards, playing cards and ending your turn is about 20% faster than in v0.2, and the longest freezes during a turn are about halved.
- Cards that look through your draw pile, like Hidden Gem, no longer freeze the game for seconds.
- Less memory churn during combat.
- Hovering an enemy no longer rebuilds its tooltip after every action when nothing changed (it still updates when something does).
- Three new switches in the settings: effect listener lists, skip non-reacting cards, creature hover tips.

## v0.2.3

- BaseLib is now optional. Some players have reported problems with BaseLib: this mod now works without it. BaseLib only adds the settings menu (Mod Configuration → Big Deck Optimization); without it, the bdopt console command still turns improvements on or off until you quit the game.

## v0.2.2

- New, cleaner image on the mods screen and the Workshop page.

## v0.2.1

- The settings are now in the game's settings menu, under Mod Configuration → Big Deck Optimization (from BaseLib's mod configuration). BaseLib is now required; on the Workshop it is installed with this mod.
- The mod has its own image on the mods screen.
- Switches turned off in ModConfig with v0.2.0 are not carried over: everything starts on again.

## v0.2.0

Huge decks are now playable well past 10,000 cards.

- With 8,000+ cards and hundreds of relics, entering a combat takes about 4 seconds instead of half a minute.
- Playing cards and waiting in combat are smooth again, even with BetterSpire2 installed.
- The freeze at the end of a combat is mostly gone.
- With 50+ relics, the relic effects at the start of a combat skip their animation pauses (same effects, just faster). Can be turned off.
- Co-op: no more multi-second freezes from the mod's own safety checks with very big decks.
- New settings: each improvement has its own switch (`bdopt <name> off` in the console).

## v0.1.0

First public release, for Slay the Spire 2 v0.107.1.

- Co-op: the game checks the full combat state after every action on every player's machine. With huge decks and relic counts it took up to 1.7 s per action (2830 combat cards, 370 relics); now ~40 ms, with the exact same result.
- Combat: the refresh after every action (card costs, hand, intents, enchantment values) is 4–12x faster with huge decks.
- Deck view and other card grids: sorting no longer freezes the game (30–110 ms → under 5 ms), opening or sorting a grid stutters less (~2.5x), and with Limitless installed, scrolling enchanted cards no longer rebuilds every enchantment tab.
- Relic bar: hundreds of relics no longer slow down every mouse move.
- Client-side only: other players don't need the mod.
