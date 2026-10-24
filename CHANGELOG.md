# Changelog

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
