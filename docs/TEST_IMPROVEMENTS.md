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
| Headless sessions no longer freeze | `override.cfg` in the copies (`parlib.ps1`), Sentry's logger off | 5 freezes in 192 sessions before, 0 in 400 after. A session frozen anyway starts over |
| Exact values in A/B reports | `compare.py`, state lines in sweeps | A planted difference shows `hp 999987 -> 999986` |
| Errors on one side only, this mod's problems | `compare.py`, `sweepreport.py` | |
| Effects over a sweep, most allocating items | `sweepreport.py` | |
| Live progress and time left | `status.ps1 -Watch`, `results/status.txt` | |
| Logs per game version | `results/<version>/` | The next version's run can't overwrite them |
| Before/after table | `report.py` | Warns when only one session ran under the fps cap |
| Who holds a left run | `leakroots.ps1` | Every run named; 0 chains through this mod |
| A whole run nobody scripted, to its end | `autoslay <seed>`, `autoslay-on/off`, `autoslay-2k-on/off` | 50 rooms in about 5 min, the same mod on and off: 647 card plays on the public version, 443 on the beta; with 1,900 cards, 306 and 164 |
| The same run every time | `autoslay` | 6 sessions, alone or in parallel, minutes apart: 1,517 lines identical. Mod-off logs can be cached |
| Co-op in parallel, headless, a port per pair | `tools/autotest/parcoop.ps1`, `--bdport` | 3 tests in 3.4 min, peers agree, public and beta |
| Mutations in parallel, on hard-linked folders | `tools/autotest/mutations.ps1` | 28/28 in 2.4 min instead of about 25; the copy is never written, 0 of its files changed |
| Sessions on a copy leave Steam Cloud alone | `run.ps1` without `-Steam`, all the parallel tools | Identical to sessions through Steam: `ab-2k` 54/54, turn 3,695 -> 3,691 ms, same fps; the cloud folder doesn't move |
| An outside reference for fps | `run.ps1 -PresentMon`, `presentmon.py` | 239 fps against the game's own 240, one column of pixels on screen: `fpsstat` can be trusted |
| No window on screen, no keyboard taken | `Start-HiddenGame`: sessions start hidden through WMI, shown once off-screen; hidden sessions skip the game's fullscreen | Co-op: 0 of 8,845 polls on a screen. Keyboard: 0 polls on every script; was 3 s on screen and 3 s of focus at each start |
| Settings scripts in a test slot | `settings.ps1`, `baselib-optional.ps1`, `limitless-switches.ps1 -GameDir <copy>`, `run.ps1 -KeepSlot` | No Steam, no window; your `settings.save` and cfg are no longer edited at all (checked: same hashes) |
| A session gone before its script's end starts over | `parlib.ps1`, `started over Nx` in `par.ps1`, state `CRASHED` after 3 | One whole run stopped at 15 rooms without a word: it read as a shorter session |
| Those scripts check themselves | `Test-SessionLog`, `ok` / `PROBLEM` lines | Caught a mod "disabled" that still loaded: its entry didn't exist in the test profile |
| Co-op peers compared at the same point | `--bdturnstates`, `parcoop.ps1` | Enemy states at each turn start: public and beta agree, 56 lines. The script's own `state` lines made false alarms |
| AutoSlay compares states and rooms too | `autoslay <seed> state`, `compare.py` | 1,517 lines identical mod on and off, public; 1,035 on the beta |
| What deck size costs where a player sees it | `ladder.ps1`, `ladderreport.py`: the same rendered session at several sizes, same card mix | Co-op plays 1% low 77 -> 12 fps from 945 to 32,735 cards per player, where headless sweeps saw nothing |
| Where a frame goes | `framedetail`, `FRAMES` lines | Process step, draw step, GPU, rest, main thread use, draw calls, per window |
| A player's mouse | `forcefocus`, `mousesweep`, `mouseplay`: events through the game's input path, the real cursor never moves | Hover and drag run for the first time in a test; `PLAYTIME` from click to next action |
| A co-op host alone on the GPU | `coop.ps1 -ClientHeadless` in `ladder.ps1` | Idle 393 fps at 10,295 cards; two rendering instances showed ~144 fps each |
| Which items depend on the size | `parsweep -Imports`, `scalereport.py`, `cpu` in `SWEEP` lines | Potions, cards, relics in 7 min. Finds heavy items only: headless, it can't show that something is smooth |
| A divergence ends the wait | `AutoTest` | A session was stuck for minutes waiting on a client that had left |
| A whole run by kind of step | `PHASE` lines, `phasereport.py`, `autoslay-10k-on` | Found a freeze at each room entry, 333 -> 1,650 ms over a run at 10,300 cards, no scripted test goes there |
| Traces of many card plays | `tools/profiling/bigconvert`, `coop.ps1 -TraceDelaySec` | `dotnet-trace convert` overflowed its stack on them and wrote nothing |
| Claude setup | Skills `run-tests`, `profile-game`, `version-check`, `release` (local); a hook refusing a deploy to the Steam install (`tools/hooks/no-steam-deploy.py`); `tools/version_task.ps1`, a daily check of published versions | `CLAUDE.md` 27 -> 4.6 KB |

## Lessons

- **Steam Cloud isn't isolated by `APPDATA`.** With Steam the game writes saves through Steam's API; 16 sessions of one account made cloud conflicts. Sessions on a copy now run with `--force-steam off`, mods from the copy's `mods_STEAMTEST`.
- **A session without Steam reads another profile**, with its own settings (60 fps, less anti-aliasing) and unlocks: results and timings differed until each session took the account's display settings and the test profile's progress. Checking `fpsstat` against the past logs found it.
- AutoSlay's run looked random; it had three separate causes. Its seed was lost at the character screen, so the run took one from the clock, in seconds: only sessions started together matched. It waited a fixed 300 ms after a potion, so an unfinished Snecko Oil changed the next pick on a session's speed. And after ending a turn it went straight on while the phase was still Play: with nothing playable its 100 turns were gone in a moment, and nobody played the fight ("Combat did not end"), which looked like a limit too short for slow sessions.
- A freeze with an empty managed stack needs native stacks. They named the cause in one capture (`libsentry` on the main thread); three managed dumps had shown nothing.
- Windows throttles the presents of a window entirely off-screen: PresentMon counts half the frames. One column of pixels on screen is enough (`run.ps1 -PresentMon` does it).
- Godot brings a window created beyond the screens back onto one, whatever `--position` says: only starting the process hidden keeps it off the screen.
- Godot's `no_focus` setting stops a window taking the keyboard, but the window then ignores a hidden start and shows for 3 s. What worked: a process started from outside the foreground process's descendants (WMI) isn't allowed to take the focus.
- A test can pass without testing anything: `baselib-absent` and `limitless-absent` passed while the mod they disabled still loaded. Reading a script's outcome means reading what it was meant to show, so the scripts now say it themselves.
- Two peers playing at once aren't at the same point when each logs a `state` line: compare at a point of the game itself (a turn start), not of a script.
- Speeding a test up can make it wrong: killing a boss again 0.5 s after the last kill made results depend on the session's speed. A wait must depend on the game's state, held long enough in frames and in time.
- A headless sweep can't say a thing is smooth. It said a typical card costs the same at 969 and 10,318 cards, and the user had to point out that combats do lag: no rendering, no animation time, no mouse, a 15.6 ms clock. What a player reports outranks what a test didn't see: look for what the test can't see first.
- Godot's `TimeProcess` monitor is the highest value of the last second, not this frame's: 8 ms read on frames of 1.9 ms. Timestamps at `process_frame`, `frame_pre_draw`, `frame_post_draw` give the frame's own timeline.
- A window without the focus skips part of the game's input code, and `Viewport.GetMousePosition` on the root viewport reads the real cursor whatever the events said: `forcefocus` covers both.
- A trace ending after the game closes has no method names (`?!?`): that one was first blamed on the provider list.
- PowerShell's `$s` is `$S`: a loop variable overwrote the scratch folder's path.
- Rejected, tested: time scale (mod-off sessions hang), holding waits for 3 frames instead of 10 (2-3 s per 14 items, mod on only, against a risk of races).

## Left

- One AutoSlay session stopped at room 15, no error in its log, no Windows crash record: not seen again in 192 short sessions and 8 whole runs. Cause unknown (it had just loaded a texture outside the cache, the dummy renderer isn't thread-safe: a guess). Its exit code is now recorded with the state `CRASHED`, and its first log kept as `<log>.early`.
- A polled window once at 0,0 fullscreen for a single poll (a few ms), in 2 of the last 6 scripts, around a session's start or end; origin not found.
- A session through Steam (`run.ps1` without a copy, `try.ps1`) shows its window: Steam launches the game.
- With Limitless, AutoSlay ends on "Main menu did not appear" or "Game over screen did not appear" after its 50 rooms: Limitless's last event goes on instead of ending the run. A normal end, 30 s after the last room.
