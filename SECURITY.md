# Security policy

## Supported versions

Only the latest release of Big Deck Optimization (the version on the Steam Workshop and in this repository's
[releases](https://github.com/TheKiwi7/big-deck-optimization/releases)) gets fixes. Please update before reporting.

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Report it privately instead:

1. Go to this repository's **Security** tab.
2. Click **Report a vulnerability** and describe the problem: what an attacker could do, the mod and game versions,
   and the steps to reproduce it.

You'll get an answer as soon as possible. This is a hobby project maintained in free time, so please allow a few days.
Once a fix is released, the report can be made public, with credit to you if you want it.

## Scope

Big Deck Optimization is a client-side mod for Slay the Spire 2. It patches the game's code in memory to remove lag
with huge decks and doesn't send or receive anything on its own. Relevant reports include, for example:

- a way for another player in a co-op game to make the mod run unintended code or crash your game;
- a problem in how the mod reads its settings file or game data that could be abused.

Bugs without a security impact (crashes, desyncs, slowdowns, wrong results) are welcome as regular
[issues](https://github.com/TheKiwi7/big-deck-optimization/issues) or comments on the Steam Workshop page.
