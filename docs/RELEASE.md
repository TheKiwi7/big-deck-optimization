# Release procedure

## 1. Before releasing

- [ ] Version bumped in both `src/BigDeckOptimization/BigDeckOptimization.json` (`"version"`) and `BigDeckOptimization.csproj` (`<Version>`), `min_game_version` still right.
- [ ] `CHANGELOG.md` has a `## vX.Y.Z` section (the package script uses it as the Workshop change note).
- [ ] Debug build, singleplayer with a huge deck (`bigdeck import <ref> all`): `bdperf checksum 5`, `bdperf refresh 10`, `bdperf grid`, `bdperf pile` all `OK: identical` (keep `checksum`/`refresh` under ~3,000 cards: they run vanilla first).
- [ ] Unattended checks (`tools/autotest`): `run.ps1 -Name solo-8k`, `run.ps1 -Name fast-start-check` (identical `STATE` lines), `coop.ps1 -Name coop -WithMods` (same `STATE` lines on both peers, no `divergence`), `modconfig.ps1` (setting saved and reloaded, settings restored).
- [ ] Release build in game (`python tools/release/package.py`, then copy `dist/BigDeckOptimization-*.zip` content into `<game>/mods/` in place of the Debug install):
  - the log shows `[BigDeckOptimization] Initialized vX.Y.Z` and every patch `applied` (`EnchantmentTabReuse` only with Limitless);
  - `bdopt` works, `bdperf` / `bigdeck` are unknown commands.
- [ ] Co-op with a player **without** the mod: they can join, play a few combats, no desync (backs the "client-side only" claim; last verified with v0.1.0).
- [ ] Co-op with a player with the mod: `bdopt` on both, play a few combats, no desync, no `MISMATCH` / `disabled` lines in either log.
- [ ] Optional mods most players use (BetterSpire2, SpeedX, ModConfig) enabled at least once: no `disabled after exception` in the log.

## 2. Package

```
python -I tools/release/package.py
```

Produces in `dist/` (gitignored): `BigDeckOptimization-vX.Y.Z.zip` and `workshop/` (content folder + `workshop_item.vdf`).

## 3. Steam Workshop

The game has no uploader: use SteamCMD (https://developer.valvesoftware.com/wiki/SteamCMD).

1. Put a preview image (under 1 MB) at `docs/workshop_preview.png` (or `.jpg`) before packaging. Current one: crop of [Cards -Deck Playing.jpg](https://commons.wikimedia.org/wiki/File:Cards_-Deck_Playing.jpg) (tookapic on Pixabay, CC0: no attribution required).
2. Upload: `steamcmd +login <steam account> +workshop_build_item "<repo>\dist\workshop\workshop_item.vdf" +quit`
   - First upload (`docs/workshop_id.txt` absent): creates the item **hidden**, with title and description from `docs/workshop_description.bbcode`. SteamCMD prints the new item ID: save it in `docs/workshop_id.txt` so later uploads update that item (title/description are then edited on the page, not by the script).
3. On the item page: check the description and preview, add the game version tag if offered, then set visibility to Public.
4. Subscribe on a second account or check that a subscriber gets the update. A local copy in `<game>/mods/` takes precedence over the Workshop one (the game disables the Workshop copy), so remove the local copy to test the Workshop version.

## 4. GitHub

1. Rename the CHANGELOG `## Unreleased` section to `## vX.Y.Z`.
2. Commit, tag `vX.Y.Z`, push the tag.
3. Create a release from the tag: attach `dist/BigDeckOptimization-vX.Y.Z.zip`, paste the CHANGELOG section.

## 5. Nexus Mods

On demand only: same zip, same description.
