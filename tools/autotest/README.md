# Unattended in-game tests

Drive the real game with the **Debug** build's `AutoTest` runner (`src/BigDeckOptimization/Bench/AutoTest.cs`): it only
starts when the game is launched with `--bdtest <scenario>`, so normal launches are unaffected. Results (game logs,
traces) go to `results/` (gitignored).

| Script | What |
|---|---|
| `run.ps1 -Name <scenario> [-TraceSec 60] [-KeepConfig] [-Visible] [-Sound] [-Screen 1]` | Singleplayer through Steam (save profile 3, in memory only: your profiles and last-profile setting are untouched). The game window is moved off-screen (windowed 1920x1080, still rendering: same screenshots and fps) and gives the focus back to the window you were using; it first shows for a moment on screen `-Screen` (default 1, the second screen; Godot's `--screen`, 0 = first) while the mod loads; `-Visible` keeps it on screen. The game is muted (every master volume it sets is 0, your volume settings are untouched); `-Sound` keeps the sound. Only the window and the live volume change, not your settings. Your `mod_configs/BigDeckOptimization.cfg` (where `bdopt` saves with BaseLib) is restored byte for byte afterwards, unless `-KeepConfig`; if interrupted, copy back `results/BigDeckOptimization.cfg.run-backup`. `-GameDir <folder>`: a full copy of another game version (e.g. the beta branch copied to `C:\Games\StS2-beta-0.111.0`, with a `steam_appid.txt` containing `2868840` next to the exe) launched directly instead of through Steam, so tests can target it while Steam keeps another branch installed; deploy into it with `dotnet build ... -p:ModDeployDir=<folder>\mods\BigDeckOptimization`. |
| `coop.ps1 -Name coop [-WithMods] [-Visible] [-Sound]` | Local co-op: two instances without Steam (`--force-steam off --fastmp host_standard/join`), scenarios `<name>-host.txt` / `<name>-client.txt`. `-WithMods` copies your enabled Workshop mods into `<game>/mods_STEAMTEST` for the test and always deletes it afterwards. |
| `record.ps1 -Name <scenario> [-Screen 1] [-Out <file.mp4>] [-NoAudio]` | Real-time screen recording of a scenario (lag shows as it is): the game fullscreen and on top on screen `-Screen` (`run.ps1 -Show`), ffmpeg's desktop duplication capture of that screen plus `tools/audiocapture` (the game process's sound only, Windows process loopback) between the scenario's `marker recording start` and `marker recording stop` lines, muxed into an MP4 (default: your Videos folder). Checks the recording shows the game (32x18 comparison with the scenario's `screenshot video-check`, deleted otherwise) and warns on a silent track. Needs ffmpeg (`winget install Gyan.FFmpeg`). Workshop video: `video-cascade-30k-on` / `-off` (same seed and cards, only `bdopt` differs). |
| `try.ps1 [-Cleanup]` | Hands-on demo: a new run with ~16,800 cards and 736 relics (scenario `try-it`), then you play (`bdopt off` / `bdopt on` to compare). `-Cleanup` removes the local Debug copy afterwards. |
| `baselib-optional.ps1` | Two sessions with your mod list edited (restored byte for byte): this mod loaded before BaseLib (the settings menu must still appear), then BaseLib disabled (every patch applies, bdopt works; Limitless and CampfireTrade fail to load, they need BaseLib). |
| `settings.ps1` | Two sessions: BaseLib's settings menu captured and an option turned off, then reloaded after a restart and turned back on (mods screen captured); restores your `mod_configs/BigDeckOptimization.cfg` byte for byte. |
| `python -I compare.py <a.log> <b.log>` | A/B check of two sessions: CHECKSUM, STATE and CARDPLAY lines must match line for line (prints IDENTICAL/DIFFERENT), then per-card `playcard` times side by side. |

Prerequisites: a Debug build deployed (`dotnet build -c Debug`), `dotnet-trace` on PATH for traces, a run history file
copied to `%APPDATA%\SlayTheSpire2\bigdeck\ref.run` (scenarios import its decks and relics). Before the first co-op
test, launch the game once with `--force-steam off` and accept the mods warning (each non-Steam instance has its own
data folder, `%APPDATA%\SlayTheSpire2\default\<id>`).

## Scenario lines

One command per line, `#` comments, `{hash:NAME}` replaced by the hash of `bigdeck/NAME.run`.

- `newrun <CHARACTER> [seed]`, `mplobby <CHARACTER> [players]` (co-op lobby: pick and ready up)
- `wait <s>`, `waitfor menu|run|combat|idle|nocombat [timeout s]`, `timeout <s>` (whole script)
- `cmd <console command>` (networked commands go through the action queue in co-op)
- `play <n>`, `playcard <ID> [enemy index] [free]` (after `cmd card <ID> hand`; `free`: energy and star costs 0 first, not X costs; every card play, auto-plays included, is logged as `CARDPLAY`), `endturn`
- `enemyhp <hp>` (next combats), `state [label]` (creatures, powers, piles), `speed normal|fast|instant` (default fast)
- `deckview [s] [shot]`, `deckscroll <steps>`, `screenshot <name>`, `modsscreen <shot>`, `baselibmenu <shot>` (this mod's page in BaseLib's settings)
- `pilescroll draw|discard|exhaust <frames> [px]` (opens a combat pile view and moves its scroll target every frame like a held mouse wheel: open time, ms per frame, median/95th/worst; 3000 px steps redraw the whole grid like dragging the scrollbar)
- `language <code>` (game language for the session only, e.g. `fra`, `zhs`, `zht`; logs this mod's menu texts as `LANG` lines), `menuback` (closes the current main-menu submenu), `menutext <SLUG>` (logs an option's tooltip text, e.g. `HOOK_FILTER`, as `MENUTEXT`)
- `deckfilter [ID,ID...] [stacked]` (on the map: removes cards by id and, with `stacked`, those holding several stacked Limitless enchantments)
- `fakeconflict <Type:Method>` (a no-op postfix under another Harmony id, as another mod would patch that method: the optimization using it backs off and names it; `conflict-check`)
- `gcstat [label]` (wall time, GC pause time and collections since the previous gcstat), `gcfull [label]` (live heap after a full collection)
- `fpsstat [label]` (frames since the previous fpsstat: average fps, 1% low, worst frame, frames over 33/50/100 ms)
- `checksum [label] [vanilla]` (full combat state checksum, as co-op peers compare), `immortal [off]` (enemies never drop below 1 HP)
- `setenergy <n>`, `emptyhand`, `hover <enemy>` / `checktips [label]` (creature hover tips match the current tips)
- `counters [label]`: LimitlessReserialize kept/rebuilt lists and Debug verification mismatches, FastListeners cache counters
- Test sessions ignore the game's 30 fps cap in the background, so timings don't depend on window focus.
- `marker <text>`, `log <text>`, `quit`, `handoff` (stop answering card choices and leave the game to the player)

Card choices are answered with the first options, identically on every peer. The runner also fixes the `fight`
command's clock-based encounter seed (peers would desync) and skips Fur Coat when no map point exists yet.

Useful `bdperf` commands inside scenarios: `probe reset|log` (per-method times + long frames), `combatstart`,
`checks off` (no Debug-only vanilla re-checks: release-like timings), `pile [verify n]`, `verify`, `plaque [n]` (BaseLib's card type line, TypePlaqueSkip off vs on: same text object).

Version check protocol (CLAUDE.md): `python -I tools/check_versions.py [--log <log>] [--accept]` compares the installed game/BaseLib/Limitless with `tools/versions.json` and lists what a session left vanilla.
