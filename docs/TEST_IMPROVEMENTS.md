# Faster, more reliable tests (November 2026)

What the v0.9.0 test series taught, what was built from it, and what's left. Numbers: 16 logical processors, 31 GB.

## Built

| What | Where | Measured |
|---|---|---|
| Parallel headless sessions without Steam, each with its own user data | `tools/autotest/par.ps1` | 16 at once; 8 sessions took 65 s each, against 61.5 s for one alone; identical results |
| Whole-game A/B in chunks | `tools/autotest/parsweep.ps1`, `sweepreport.py` | Public, all 5 kinds: 30 min. Before: 198 min for 3 kinds on the beta |
| Instant game speed for equivalence | `-Speed instant` (`parsweep`), `-Instant` (`par`) | 14 relics: mod on 50 -> 12 s, mod off 120 -> 81 s. A whole relic sweep at both speeds: 4,450 lines identical, mod on and off |
| Mod-off logs reused | `-UseBaselines`, keyed by game, Workshop mods, `Bench` sources and scenario | Skips nearly all the mod-off time until one of those changes |
| First failure ends the session | `AutoTest`, `onfail continue` when failures are expected, `check_scenarios.py` | Unknown command: 11 s instead of waiting out each later line |
| A dead turn loop fails every wait at once | `AutoTest` | Was up to 10 min per item |
| Silent log closes the session | `run.ps1 -StallSec`, `par.ps1 -StallSec` | |
| A session frozen on Godot's dummy renderer starts over | `parlib.ps1` | About 1 headless session in 40; was 5 min lost and a chunk without results |
| Exact values in A/B reports | `compare.py`, state lines in sweeps | A planted difference shows `hp 999987 -> 999986` |
| Errors on one side only, this mod's problems | `compare.py`, `sweepreport.py` | |
| Effects over a sweep, most allocating items | `sweepreport.py` | |
| Live progress and time left | `status.ps1 -Watch`, `results/status.txt` | |
| Logs per game version | `results/<version>/` | The next version's run can't overwrite them |
| Before/after table | `report.py` | Warns when only one session ran under the fps cap |
| Who holds a left run | `leakroots.ps1` | Every run named; 0 chains through this mod |
| A whole run nobody scripted | `autoslay <seed>`, `autoslay-on/off` | 453 card plays on the public version, 499 on the beta, the same mod on and off |
| Refuses a copy left modified by mutations | `parlib.ps1` | |
| Claude setup | Skills `run-tests`, `profile-game`, `version-check`, `release` (local); a hook refusing a deploy to the Steam install (`tools/hooks/no-steam-deploy.py`); `tools/version_task.ps1`, a daily check of published versions | `CLAUDE.md` 27 -> 4.6 KB |

## Lessons

- **Steam Cloud isn't isolated by `APPDATA`.** With Steam the game writes saves through Steam's API; 16 sessions of one account made cloud conflicts. Parallel sessions now run with `--force-steam off`, mods from the copy's `mods_STEAMTEST`.
- With and without Steam, the same scenario gives different results (different profile data, so different unlocks). Both sides of an A/B must use the same mode; baselines are keyed accordingly.
- Speeding a test up can make it wrong: killing a boss again 0.5 s after the last kill made results depend on the session's speed. A wait must depend on the game's state, held long enough in frames and in time.
- Rejected, tested: time scale (mod-off sessions hang), holding waits for 3 frames instead of 10 (2-3 s per 14 items, mod on only, against a risk of races).
- Stopping `mutations.ps1` from outside leaves its modified game in the copy: check `sts2.dll` against the Steam install.

## Left

- `mutations.ps1`, `run.ps1` and the settings scripts still run with Steam and a window: they write the test profile (3) to Steam Cloud. Single sessions make no conflicts, but mutations could move to headless sessions without Steam.
- PresentMon (frame times measured outside the game, GPU included) wasn't tried.
- Co-op in headless sessions wasn't tried.
