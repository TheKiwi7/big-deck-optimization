# Release procedure

## 1. Before releasing

- [ ] Version check protocol (CLAUDE.md): `python -I tools/check_versions.py` says the installed game, BaseLib and Limitless are the verified versions; otherwise follow the protocol (`--log` on a fresh session, adapt or leave vanilla, A/B checks, `--accept`) before going on.
- [ ] Manifest `dependencies` empty (BaseLib is optional: only `BigDeckOptimization.Menu.dll` references it).
- [ ] Version bumped in both `src/BigDeckOptimization/BigDeckOptimization.json` (`"version"`) and `BigDeckOptimization.csproj` (`<Version>`), `min_game_version` still right.
- [ ] `CHANGELOG.md` has a `## vX.Y.Z` section (the package script uses it as the Workshop change note).
- [ ] Debug build, singleplayer with a huge deck (`bigdeck import <ref> all`): `bdperf checksum 5`, `bdperf refresh 10`, `bdperf grid`, `bdperf pile` all `OK: identical` (keep `checksum`/`refresh` under ~3,000 cards: they run vanilla first).
- [ ] Unattended checks (`tools/autotest`): `run.ps1 -Name solo-8k`, `run.ps1 -Name fast-start-check` (identical `STATE` lines), `coop.ps1 -Name coop -WithMods` (same `STATE` lines on both peers, no `divergence`), `settings.ps1` (BaseLib menu shows every option, setting saved and reloaded, image on the mods screen), `baselib-optional.ps1` (menu still appears when this mod loads before BaseLib; without BaseLib every patch applies and bdopt works), `run.ps1 -Name hookskip-off` then `hookskip-on` (identical CHECKSUM and STATE lines), `memory-off` / `memory-on` (live heap flat over 15 turns). Since v0.4.0, also (compare with `python -I tools/autotest/compare.py a.log b.log`: `IDENTICAL`): `release-checks` (the `bdperf` comparisons above, unattended), `pile-cards-off` / `pile-cards-on` (59 cards, every play logged; `counters end`: 0 mismatches), `turns-off` vs `hookskip-on` (everything off vs on, 5 turns), `shuffle-off` / `shuffle-on` (reshuffles), `coop.ps1 -Name coopfast -WithMods` (both modded, the client's fast combat start switched off: it must still skip the waits in co-op, no divergence), `run.ps1 -Name limitless-menu` via `limitless-switches.ps1` (Limitless switches unticked without Limitless, ticked again with it). `coop-required.ps1` (the client without this mod is refused when joining: "Mods mismatch… BigDeckOptimization-vX.Y.Z"). `coopvanilla` / `coopsolo1` / `coopsolo2` reproduce the mixed-setup failures (expected to diverge or freeze).
- [ ] After a game update: re-decompile, `dotnet run --project tools/fingerprint -- <sts2.dll> MegaCrit.Sts2.Core.Hooks.Hook * body > hook_fps.txt` and `python -I tools/hookskip/classify.py decompiled/sts2 hook_fps.txt` to regenerate `HookSkipTable.cs` (hooks whose code changed are left vanilla until then).
- [ ] Release build in game (`python tools/release/package.py`, then copy `dist/BigDeckOptimization-*.zip` content into `<game>/mods/` in place of the Debug install):
  - the log shows `[BigDeckOptimization] Initialized vX.Y.Z` and every patch `applied` (`EnchantmentTabReuse` only with Limitless);
  - `bdopt` works, `bdperf` / `bigdeck` are unknown commands.
- [ ] Manifest `affects_gameplay: true` (since v0.4.0): a player without the mod, or with another version, gets the game's mod mismatch error when joining. Co-op with mixed setups desyncs or freezes with huge decks (DESIGN "Co-op with a player without the mod").
- [ ] Co-op with a player with the mod: `bdopt` on both, play a few combats, no desync, no `MISMATCH` / `disabled` lines in either log.
- [ ] Optional mods most players use (BetterSpire2, SpeedX, ModConfig, Limitless) enabled at least once: no `disabled after exception` in the log.

## 2. Package

```
python -I tools/release/package.py
```

Produces in `dist/` (gitignored): `BigDeckOptimization-vX.Y.Z.zip` and `workshop/` (content folder + `workshop_item.vdf`).

## 3. Steam Workshop

The game has no uploader: use SteamCMD (https://developer.valvesoftware.com/wiki/SteamCMD).

1. Put a preview image (under 1 MB, square: Workshop lists show ~135 px thumbnails, so a big readable text beats a full screenshot) at `docs/workshop_preview.jpg` before packaging. Current one: a pile of 119 different real cards cut from in-game deck view captures (`tools/autotest`, scenario `card-art`, command `deckpages`), rotated at full size then scaled down (clean edges), with the card count in Kreon (SIL Open Font License). The same image, 512 px, is `src/BigDeckOptimization/mod_image.jpg` (mods screen; JPEG quality 97 with 4:4:4 color, e.g. Pillow `save(..., quality=97, subsampling=0, optimize=True)`: 242 KB, PSNR 42.5 dB vs the PNG). To change only the image on an existing item, run SteamCMD with a VDF holding just `appid`, `publishedfileid` and `previewfile`: subscribers and collections are kept (only deleting and recreating the item loses them).
2. Upload: `steamcmd +login <steam account> +workshop_build_item "<repo>\dist\workshop\workshop_item.vdf" +quit`
   - First upload (`docs/workshop_id.txt` absent): creates the item **hidden**, with title and description from `docs/workshop_description.bbcode`. SteamCMD prints the new item ID: save it in `docs/workshop_id.txt` so later uploads update that item (title/description are then edited on the page, not by the script).
3. On the item page: check the description and preview, add the game version tag if offered, then set visibility to Public. **Required items** (Owner Controls → Add/Remove Required Items) must stay empty: BaseLib is optional (the Workshop has no "optional item": it is linked from the description instead).
4. Subscribe on a second account or check that a subscriber gets the update. A local copy in `<game>/mods/` takes precedence over the Workshop one (the game disables the Workshop copy), so remove the local copy to test the Workshop version.

## 4. GitHub

1. Rename the CHANGELOG `## Unreleased` section to `## vX.Y.Z`.
2. Commit, tag `vX.Y.Z`, push the tag.
3. Create a release from the tag: attach `dist/BigDeckOptimization-vX.Y.Z.zip`, paste the CHANGELOG section.

## 5. Nexus Mods

On demand only: same zip, same description.
