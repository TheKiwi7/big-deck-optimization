# Unattended in-game tests

Drive the real game with the **Debug** build's `AutoTest` runner (`src/BigDeckOptimization/Bench/AutoTest.cs`): it only
starts when the game is launched with `--bdtest <scenario>`, so normal launches are unaffected. Results (game logs,
traces) go to `results/` (gitignored).

| Script | What |
|---|---|
| `run.ps1 -Name <scenario> [-TraceSec 60]` | Singleplayer through Steam (save profile 3, in memory only: your profiles and last-profile setting are untouched). |
| `coop.ps1 -Name coop [-WithMods]` | Local co-op: two instances without Steam (`--force-steam off --fastmp host_standard/join`), scenarios `<name>-host.txt` / `<name>-client.txt`. `-WithMods` copies your enabled Workshop mods into `<game>/mods_STEAMTEST` for the test and always deletes it afterwards. |
| `modconfig.ps1` | Enables ModConfig for two sessions (setting changed, then reloaded), then restores your `settings.save` byte for byte. |

Prerequisites: a Debug build deployed (`dotnet build -c Debug`), `dotnet-trace` on PATH for traces, a run history file
copied to `%APPDATA%\SlayTheSpire2\bigdeck\ref.run` (scenarios import its decks and relics). Before the first co-op
test, launch the game once with `--force-steam off` and accept the mods warning (each non-Steam instance has its own
data folder, `%APPDATA%\SlayTheSpire2\default\<id>`).

## Scenario lines

One command per line, `#` comments, `{hash:NAME}` replaced by the hash of `bigdeck/NAME.run`.

- `newrun <CHARACTER> [seed]`, `mplobby <CHARACTER> [players]` (co-op lobby: pick and ready up)
- `wait <s>`, `waitfor menu|run|combat|idle|nocombat [timeout s]`, `timeout <s>` (whole script)
- `cmd <console command>` (networked commands go through the action queue in co-op)
- `play <n>`, `playcard <ID>` (after `cmd card <ID> hand`), `endturn`
- `enemyhp <hp>` (next combats), `state [label]` (creatures, powers, piles), `speed normal|fast|instant` (default fast)
- `deckview [s]`, `deckscroll <steps>`, `marker <text>`, `log <text>`, `quit`

Card choices are answered with the first options, identically on every peer. The runner also fixes the `fight`
command's clock-based encounter seed (peers would desync) and skips Fur Coat when no map point exists yet.

Useful `bdperf` commands inside scenarios: `probe reset|log` (per-method times + long frames), `combatstart`,
`checks off` (no Debug-only vanilla re-checks: release-like timings), `pile [verify n]`, `verify`.
