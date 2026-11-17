"""Whether a session's log shows a problem, whatever it is compared with: the one judgment every runner uses (par.ps1,
parcoop.ps1, coop4.ps1, coop.ps1, run.ps1, release-incopy.ps1, compare.py, sweepreport.py).

    python -I tools/autotest/judge.py [--no-end] [--mod-off] [--mutated] <log> [<log> ...]
        one PROBLEM line per problem, then a last line 'JUDGED <n> problem(s)'; exit 1 when any. A runner that doesn't see the
        JUDGED line must count the logs as failed (this script crashed or never ran).
    --no-end: the session isn't expected to end by itself (release-incopy.ps1 closes the game once the patches are applied).
    --mod-off: a mod-off side (parcoop.ps1's -autooff twins and *off pairs): the mod found off when the game started is no
        problem there. The twins are launched off (`launchoff`, --bdoff: session only, never saved); a co-op `cmd bdopt off`
        run by hand at the menu is saved in the slot both peers share, so the other peer may start with the mod off (nothing
        applied: the game's own code, as that side wants). Nor are comparisons that compared nothing, or effects at zero.
    --mutated: a session on a game mutations.ps1 modified: the parts the mutation leaves vanilla compare nothing and count no
        effect there (mutations.ps1 checks those against its table): neither is a problem.

A problem:
- the log is missing, or the session didn't reach its end (`script finished`, `> quit`, `handoff`, or the game's auto player
  ending its run: see below);
- a failed line: `FAILED line` (a failed script line, a console command that failed included), `ABORT`, the script's
  `TIMEOUT`, `SWEEP FAILED` / `SWEEP ABORT` (not for parsweep's parts: `sweep=True`, its restarts are the design);
- anything this mod logs as an error ([ERROR] lines: its patches log errors only when they turn themselves off, when
  their result differs from the game's, or when a patch can't apply), and the lines saying a check found a difference
  (MISMATCH, `mismatches N`, `differs from`, `changed unseen`...) or that the mod was off from the start (saved settings),
  but in a vanilla reference session: launched with `--bdvanilla` (parlib.ps1's solo mod-off sessions), said by this
  mod's own `BDVANILLA: launched with --bdvanilla` line, never by the log's name;
- any warning this mod logs ([WARN] lines: an exception caught in its own code, part of its work not done), but the few
  intended ones of MOD_WARN_OK;
- an error the game logs whose stack goes through this mod's code (not the test tooling's): an exception from a patch the
  game caught itself (an async hook's task, an action) is logged without this mod's tag; so is one Godot caught in an engine
  callback (a frame tick, a deferred call, a timer): Godot's own `ERROR: System.X...` head, the exception's frames below it
  (the engine's other `ERROR:` lines, leaked RIDs or missing input actions, have no frame of this mod);
- a co-op state divergence (the host's `State divergence detected!`, the client's `State divergence message received`);
- the game's auto player (AutoSlay) ending its run other than at its end: it logs `Quitting` after a failure too, so a run
  failed at room 3 looked finished. With Limitless the last event never hands back the game over screen or the main menu,
  so a failure with those reasons after the final act's boss is the run's end;
- a leak check: `LEAKCHECK current` finding no path (the walk is blind), any other of the mod's or the tooling's finding one,
  or one that reached its limit (inconclusive), or a `leakcheck game` path through an object of this mod
  (`[through this mod: Type]`: a closure or handler the game holds, a static of the Menu DLL, holding a left run).

- a bdperf comparison that compared nothing (`... not applied ...: nothing to compare`, `left the walks vanilla`, `not
  compared: ...`): it says identical without having compared anything. Not in a vanilla session, on a mod-off side (--mod-off)
  nor on a modified game (--mutated), and not CardSortKeys' own "not needed on this game version" (v0.107, v0.111: no sort to answer);
- the effects a scenario says must have run (`# effects-ran:` below) still at zero at its last `effects` command, an open
  BALANCE, or no EFFECT line at all (a session cut short, --no-end, is not held to that last one);
- an `expect` line the script holds (AutoTest's `EXPECTDECL <regex>` at the script's start) that no line of the session
  matches: a control after an early ABORT never ran, and its EXPECT line never came;
- an ABORT after an expected failure with script lines left (`[lines left N]`, N > 0): the end of the script never ran.

A session that logs AutoTest's `SKIPPED: ...` line (`requiregame`, a game mode this version lacks: the rest of the scenario
doesn't apply to this game version, the script ends there as finished) isn't held to what the lines after it would have made:
an `expect` line of the script that never ran (its EXPECTDECL without an EXPECT line) and the `# effects-ran:` lines when no
`effects` command ran. An `expect` line that ran before the SKIPPED line is still required. compare.py says SKIPPED for it.

A scenario expecting some of these (positive controls) says `expect <regex>`: the problem lines that regex matches, anywhere
in the log, are listed as expected instead, and it must match at least one line of the session (not the script's own lines),
or that is a problem too: a control that saw nothing. The regex is checked by .NET when the script runs and by Python here:
one Python can't compile is a problem. Every `expect` line of the script must be seen so (EXPECTDECL), executed or not.

Scenario directives: comment lines of the scenario that ran (the log's `script <path> (<n> lines)` line names it; a script
not found leaves them unknown), checked by check_scenarios.py. `<regex>` is Python's; ` on <version regex>` at the end of a
line limits it to the game versions that regex matches (the log's `release=v0.111.1`).
    # effects-ran: all | <regex>          every effect of the last `effects` command (or those the regex matches, at least one)
                                          above zero, and every BALANCE at 0
    # effects-zero-ok: <regex> [on <v>]   those effects may stay at zero (the scenario can't make them run there)
    # uncompared: <regex> [on <v>]        a comparison line saying it compared nothing that the regex matches is expected
    # compare-with: <name>                par.ps1: compare this scenario's log with <name>'s (judge.py ignores it)
"""
import os
import re
import sys

CLOCK = r'AutoTest\s+[\d.]+s\s+'
FAILED = re.compile(CLOCK + r'(FAILED line .*|FAILED: .*|ABORT: .*|TIMEOUT: .*|SWEEP FAILED .*|SWEEP ABORT .*)$')
SWEEP_STOP = re.compile(r'^(SWEEP FAILED|SWEEP ABORT)')
END = re.compile(CLOCK + r'(script finished|> quit\b|handed over to the player)|\[AutoSlay\] Action: Quitting')
EXPECT = re.compile(CLOCK + r'EXPECT (.+)$')
ECHO = re.compile(CLOCK + r'> |Executing DevConsole command|DevConsole: ')
# Every error this mod logs is a problem.
MOD_ERROR = re.compile(r'^\[ERROR\].*\[BigDeckOptimization\]')
# Lines that aren't errors but say a check found a difference, a part turned itself off, or the mod was off from the start.
MOD_PROBLEM = re.compile(r'\[BigDeckOptimization\].*(MISMATCH|mismatches [1-9]|\bdisabled( after|[,:]| \()|differs? from'
                         r'|changed unseen|found wrong|turned off and removed|turned off in the settings|turned off when the game started'
                         r'|NEVER gave the frame back|PATCHES LOST)')
# Every warning this mod logs is a problem too: they say an exception was caught in its own code and part of its work was not
# done (the left run's caches not released, a switch not set, a check not run, a rebuild failed...). Except these, intended or
# already reported elsewhere: the settings menu missing (started-off-nomenu sets it aside; release-incopy checks it registers), the
# menu's co-op refusal of the master switch, a fault's retried removal (the fault is a problem line already), a mirrored hook
# or FromRun rethrowing the game's own exception as the game would, a pile snapshot read hitting the game's own exception (the game's call
# then made, as mod off), and the test tooling's own timers and probes. A new warning is a problem
# until it is listed here: never missed silently. A scenario expecting one says `expect <regex>`.
MOD_WARN = re.compile(r'^\[WARN\].*\[BigDeckOptimization\]')
MOD_WARN_OK = re.compile(r'\[BigDeckOptimization\] (SettingsMenu: (settings menu unavailable|the saved "off" can\'t be changed)'
                         r'|menu texts for \S+ unreadable|BaseLibSettings: the mod stays (on|off) during a co-op run'
                         r'|PatchGuard: faulted patches not removed yet|HookFilter: a mirrored hook threw \(passed on'
                         r'|SnapshotVerifier: the game.s FromRun threw \(passed on'
                         r'|SnapshotCardReuse: the game.s own code threw while a pile.s snapshot was read'
                         r'|PerfProbe: |CombatStartTimer: )')
# AutoTest's line at the script's start, one per `expect` line of the script (executed or not).
EXPECTDECL = re.compile(CLOCK + r'\s*EXPECTDECL (.+)$')
# AutoTest's line when the rest of the scenario doesn't apply to this game version (requiregame, a game mode it lacks).
SKIPPED = re.compile(CLOCK + r'SKIPPED: ')
# AutoTest's ABORT line ends with the number of script lines that never ran (builds since 2026-11-17; older ones: no suffix).
ABORT_LEFT = re.compile(r' \[lines left (\d+)\]\s*$')
# A bdperf comparison that compared nothing: its echo (the 'ok:' line and its continuation lines). CardSortKeys not needed on this
# game version (v0.107, v0.111: the game's sort has nothing to answer) is the game's, not a part left vanilla.
ECHO_OK = re.compile(CLOCK + r'ok: ')
UNCOMPARED = re.compile(r'nothing to compare|left the walks vanilla|\bnot compared: ')
UNCOMPARED_OK = re.compile(r'not applied \(not needed on this game version\): nothing to compare')
UNCOMPARED_HEAD = 'a comparison compared nothing: '
# The scenario that ran, and the game version (Sentry's init line, or the release_info.json the game found).
SCRIPT = re.compile(CLOCK + r'script (.+) \(\d+ lines\)\s*$')
VERSION = re.compile(r'\brelease=(v\d+(?:\.\d+)+)')
RELEASE_INFO = re.compile(r'^\[INFO\] Found release_info\.json at: (.+?)\s*$')
EFFECT = re.compile(CLOCK + r'EFFECT (\S+) (\d+)\s*$')
BALANCE = re.compile(CLOCK + r'BALANCE (\S+) (-?\d+)\s*$')
DIRECTIVE = re.compile(r'^\s*#\s*(effects-ran|effects-zero-ok|uncompared|compare-with):\s*(.*?)\s*$')
LEAK = re.compile(CLOCK + r'LEAKCHECK (.+?): (REACHED|INCONCLUSIVE|no static|nothing to look for)')
# A path of a `leakcheck game...` walk (they come right after its LEAKCHECK line) that goes through an object of this mod outside
# the test tooling: a closure or handler the game holds, or a static of the Menu DLL, holding a left run. AutoTestLeak marks it
# (a type's full name may hold brackets: a generic's arguments).
LEAK_THROUGH_MOD = re.compile(CLOCK + r'(.+ \[through this mod: .+\])$')
# The game's own errors: its first line has no mod tag, the stack trace follows on lines that don't start with '['.
GAME_ERROR = re.compile(r'^\[ERROR\]')
# Godot's own error lines (an exception in an engine callback: `ERROR: System.X: ...`, then `   at ...` frames).
GODOT_ERROR = re.compile(r'^(ERROR|SCRIPT ERROR|USER ERROR): ')
MOD_FRAME = re.compile(r'^\s*at BigDeckOptimization\.(?!Bench\.)')
DIVERGENCE = re.compile(r'^\[ERROR\].*\b[Ss]tate divergence')
# The game's errors that come and go with timing, not with this mod (compare.py and sweepreport.py count a game error logged mod on
# only as a problem, but these): the engine's leaked RIDs, task ids and input actions, and an animation asked for a creature whose
# node is already gone (sweeps: an item starts while the last one's animation is still due).
GAME_NOISE = re.compile(r'Invalid Task ID|\bRID\b|leaked|input action|Attempted to play animation on creature')
# AutoSlay (the game's auto player): how a run ends.
AUTOSLAY_START = re.compile(r'\[AutoSlay\] Starting run with seed=')
AUTOSLAY_DONE = re.compile(r'\[AutoSlay\] (Run completed successfully|Action: Victory!)')
AUTOSLAY_FAILED = re.compile(r'\[AutoSlay\] Run failed with seed=\S*: (.*)$')
AUTOSLAY_ROOM = re.compile(r'\[AutoSlay\] Entering (\w+) room')
# After the last act's boss the run goes to an event (acts before go to the map): the run's last room.
AUTOSLAY_FINAL = re.compile(r'\[AutoSlay\] Post-boss transition: room type is now Event')
AUTOSLAY_END_REASONS = ('Main menu did not appear after game over', 'Game over screen did not appear')


def problem_lines(path, sweep=False):
    """[(line number, text)] of every problem line of a log, before expectations."""
    found = []
    error_head = None  # (number, text) of the game's [ERROR] whose stack trace is being read
    leak_label = None  # label of the last LEAKCHECK line, whose paths follow it
    in_echo = False  # in a console command's echo: its 'ok:' line, then its lines without a prefix
    with open(path, encoding='utf-8', errors='replace') as f:
        for number, line in enumerate(f, 1):
            line = line.rstrip('\n').rstrip('\r')
            if not line.startswith('['):
                if GODOT_ERROR.match(line):
                    error_head = (number, line)
                    in_echo = False
                    continue
                if error_head and MOD_FRAME.search(line):
                    found.append((error_head[0], f'{error_head[1][:150]} (through this mod: {line.strip()[:120]})'))
                    error_head = None
                if in_echo and UNCOMPARED.search(line) and not UNCOMPARED_OK.search(line):
                    found.append((number, f'{UNCOMPARED_HEAD}{line.strip()[:180]}'))
                continue
            error_head = None
            in_echo = False
            if ECHO.search(line) or EXPECT.search(line) or EXPECTDECL.search(line):
                continue  # The script's own lines: '> expect ...', '> cmd ...' say what is asked, not what happened.
            # A console command's result as the script's echo shows it (once: the mod's own one-line copy of some is left out).
            if m := ECHO_OK.search(line):
                in_echo = True
                if UNCOMPARED.search(line) and not UNCOMPARED_OK.search(line):
                    found.append((number, f'{UNCOMPARED_HEAD}{line[m.end():].strip()[:180]}'))
                    continue
            if m := FAILED.search(line):
                if not (sweep and SWEEP_STOP.match(m.group(1))):
                    found.append((number, m.group(1)))
            elif MOD_ERROR.search(line) or MOD_PROBLEM.search(line) or (MOD_WARN.search(line) and not MOD_WARN_OK.search(line)):
                found.append((number, line))
            elif DIVERGENCE.search(line):
                found.append((number, line))
            elif (m := LEAK_THROUGH_MOD.search(line)) and leak_label and leak_label.startswith('game'):
                # The mod holding a left run where its own statics can't show it. A game walk always looks for a left run:
                # AutoTestLeak takes only the bare label `current` for the current run.
                found.append((number, m.group(1).strip()[-300:]))
            elif m := LEAK.search(line):
                label, verdict = m.group(1), m.group(2)
                leak_label = label
                if label.startswith('game'):
                    continue  # Who in the game holds a run: information, the game keeps its last runs.
                current = label == 'current'
                if verdict == 'INCONCLUSIVE':
                    bad = True
                elif verdict == 'nothing to look for':
                    bad = current  # No current run to find: the positive control saw nothing. A left run freed: fine.
                else:
                    bad = (verdict == 'REACHED') != current
                if bad:
                    found.append((number, line[m.start():]))
            elif GAME_ERROR.search(line):
                error_head = (number, line)
    return found


def autoslay_problem(text):
    """What is wrong with how the auto player's run ended, or None (no run, or a run that reached its end)."""
    if not any(AUTOSLAY_START.search(line) for line in text):
        return None
    if any(AUTOSLAY_DONE.search(line) for line in text):
        return None
    failed = next((m.group(1).strip() for line in text if (m := AUTOSLAY_FAILED.search(line))), None)
    if failed is None:
        return 'the auto player\'s run did not end (cut by the script\'s wait or the session\'s end)'
    final = max((i for i, line in enumerate(text) if AUTOSLAY_FINAL.search(line)), default=-1)
    later_rooms = [m.group(1) for line in text[final + 1:] if (m := AUTOSLAY_ROOM.search(line))] if final >= 0 else []
    if failed.startswith(AUTOSLAY_END_REASONS) and final >= 0 and all(room == 'Event' for room in later_rooms):
        return None  # Its last event, after the final boss: the run's end (Limitless's last event goes on).
    return f'the auto player\'s run failed before its end: {failed[:150]}'


def judge(path, sweep=False, need_end=True, mod_off=False, mutated=False):
    """(problems, expected): lists of texts. Never raises: a failure to judge is a problem. mod_off: a mod-off side (see main);
    mutated: a session on a game mutations.ps1 modified (parts it leaves vanilla compare nothing and count no effect)."""
    name = os.path.basename(path)
    try:
        return _judge(path, name, sweep, need_end, mod_off, mutated)
    except Exception as e:  # noqa: BLE001 - a judge that can't judge must say so, not pass
        return [f'{name}: judge.py could not judge this log: {type(e).__name__}: {e}'], []


def game_version(text):
    """The game version a log was made with ('v0.107.1'), or None."""
    for line in text[:400]:
        if m := VERSION.search(line):
            return m.group(1)
    for line in text[:400]:
        if m := RELEASE_INFO.search(line):
            try:
                with open(m.group(1), encoding='utf-8') as f:
                    found = re.search(r'"version"\s*:\s*"([^"]+)"', f.read())
                return found.group(1) if found else None
            except OSError:
                return None
    return None


def scenario_of(text):
    """The path of the script the session ran (its 'script <path> (<n> lines)' line), or None."""
    for line in text:
        if m := SCRIPT.search(line):
            return m.group(1).strip()
    return None


def read_directives(script):
    """[(kind, value, version regex or None)] of a scenario's directive comments; None when the script can't be read."""
    try:
        with open(script, encoding='utf-8-sig') as f:
            lines = f.read().splitlines()
    except (OSError, TypeError):
        return None
    return directives_of(lines)


def directives_of(lines):
    found = []
    for line in lines:
        if not (m := DIRECTIVE.match(line)):
            continue
        kind, value, version = m.group(1), m.group(2), None
        if kind in ('effects-zero-ok', 'uncompared') and (v := re.match(r'^(.*?)\s+on\s+(\S+)$', value)):
            value, version = v.group(1), v.group(2)
        found.append((kind, value, version))
    return found


def applies(version_rx, version):
    """Whether a directive limited to `version_rx` applies to a log of game `version` (unknown: only unlimited ones)."""
    return version_rx is None or (version is not None and re.search(version_rx, version) is not None)


def effects_problems(text, directives, version, need_end, skipped=False):
    """What the scenario's `# effects-ran:` lines find wrong in its last EFFECT values, as texts. skipped: the session logged
    SKIPPED (the rest of the scenario doesn't apply to this game version): no EFFECT line is no problem then."""
    wanted = [value for kind, value, _ in directives if kind == 'effects-ran']
    if not wanted:
        return []
    allowed = [re.compile(value) for kind, value, v in directives if kind == 'effects-zero-ok' and applies(v, version)]
    effects, balances = {}, {}
    for line in text:
        if m := EFFECT.search(line):
            effects[m.group(1)] = int(m.group(2))  # The last 'effects' command's counts: they only grow.
        elif m := BALANCE.search(line):
            balances[m.group(1)] = int(m.group(2))
    if not effects:
        # A session cut short (--no-end) never got there: the end it lacks is its problem, if any.
        return ['no EFFECT line: the session did not reach its "effects" command (# effects-ran)'] if need_end and not skipped else []
    found = [f'{key} is {count} with nothing running: something a patch opens is never closed' for key, count in balances.items() if count]
    for value in wanted:
        selected = list(effects) if value == 'all' else [key for key in effects if re.search(value, key)]
        if not selected:
            found.append(f'# effects-ran: {value}: no effect of that name in the EFFECT lines')
        for key in selected:
            if effects[key] == 0 and not any(rx.search(key) for rx in allowed):
                found.append(f'effect {key} never ran (EFFECT {key} 0; # effects-ran: {value})')
    return found


def _judge(path, name, sweep, need_end, mod_off=False, mutated=False):
    if not os.path.exists(path):
        return [f'{name}: no log'], []
    with open(path, encoding='utf-8', errors='replace') as f:
        text = f.read().splitlines()
    problems, expected = [], []
    # A vanilla reference session (--bdvanilla): the mod off from the start is what it asked for.
    vanilla = any(VANILLA.search(line) for line in text)
    # The scenario's own declarations (a script not found: none known).
    version = game_version(text)
    directives = read_directives(scenario_of(text)) or []
    uncompared_ok = [re.compile(value) for kind, value, v in directives if kind == 'uncompared' and applies(v, version)]
    expectations = []
    for line in text:
        if m := EXPECT.search(line):
            try:
                expectations.append(re.compile(m.group(1).strip()))
            except re.error as e:
                problems.append(f'{name}: expect "{m.group(1).strip()}" is not a regex Python reads ({e}): write it in the syntax both share')
    # Every 'expect' line of the script, executed or not (AutoTest's EXPECTDECL lines at the script's start).
    declared = []
    for line in text:
        if m := EXPECTDECL.search(line):
            try:
                declared.append(re.compile(m.group(1).strip()))
            except re.error as e:
                problems.append(f'{name}: expect "{m.group(1).strip()}" is not a regex Python reads ({e}): write it in the syntax both share')
    # Anywhere in the log: what the mod logs while loading (its settings read at the start) comes before the script's lines.
    previous_expected = aborted_as_expected = False
    lines_left = 0
    for number, line in problem_lines(path, sweep):
        if vanilla and MOD_OFF_START.search(line):
            continue
        uncompared = line.startswith(UNCOMPARED_HEAD)
        # Nothing of the mod to compare: the game's code on a mod-off side or in a vanilla session, a part a mutation left vanilla.
        if uncompared and (vanilla or mod_off or mutated):
            continue
        # The ABORT after an expected failed line (a positive control's last line failing on purpose) is expected with it,
        # and the session ended where the control meant it to: at its last line (no line left, or a build that doesn't say).
        previous_expected = any(rx.search(line) for rx in expectations) or (line.startswith('ABORT:') and previous_expected)
        if previous_expected and line.startswith('ABORT:'):
            left = ABORT_LEFT.search(line)
            if left and int(left.group(1)) > 0:
                lines_left = int(left.group(1))
            else:
                aborted_as_expected = True
        if previous_expected or (uncompared and any(rx.search(line[len(UNCOMPARED_HEAD):]) for rx in uncompared_ok)):
            expected.append(f'{name}: {line[:200]}')
        else:
            problems.append(f'{name}: {line[:200]}')
    if need_end and not aborted_as_expected and not any(END.search(line) for line in text):
        problems.insert(0, f'{name}: did not reach its end' + (
            f' (an expected failure aborted it with {lines_left} script line(s) left, which never ran: a control before other'
            f' lines says "onfail continue")' if lines_left else ''))
    if need_end and (slay := autoslay_problem(text)):
        problems.append(f'{name}: {slay}')
    skipped = any(SKIPPED.search(line) for line in text)
    if not (vanilla or mod_off or mutated):
        problems += [f'{name}: {p}' for p in effects_problems(text, directives, version, need_end, skipped)]
    # Seen in a line of the session, not in the script's own lines (its echo "> expect ...", the EXPECT and EXPECTDECL lines).
    session = [line for line in text if not ECHO.search(line) and not EXPECT.search(line) and not EXPECTDECL.search(line)]
    patterns = []
    executed = {rx.pattern for rx in expectations}
    for rx in expectations + declared:
        if rx.pattern not in patterns:
            patterns.append(rx.pattern)
            # After a SKIPPED line the script's remaining lines never ran: an expect among them isn't required (one that ran is).
            if skipped and rx.pattern not in executed:
                continue
            if not any(rx.search(line) for line in session):
                problems.append(f'{name}: expected "{rx.pattern}" never seen (a positive control that saw nothing)')
    return problems, expected


# Not a problem on a mod-off side (--mod-off) nor in a vanilla reference session: the mod off from the start runs the game's own code.
MOD_OFF_START = re.compile(r'turned off in the settings|turned off when the game started')
# A vanilla reference session's own line (ModEntry.VanillaLine, Debug builds launched with --bdvanilla): the only key for it.
VANILLA = re.compile(r'^\[INFO\] \[BigDeckOptimization\] BDVANILLA: launched with --bdvanilla ')


def expect_patterns(path):
    """The regexes of the `expect` lines a session ran (its EXPECT lines); those Python can't compile left out (judge() says so)."""
    found = []
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            for line in f:
                if m := EXPECT.search(line.rstrip('\n')):
                    try:
                        found.append(re.compile(m.group(1).strip()))
                    except re.error:
                        pass
    except OSError:
        pass
    return found


def is_skipped(path):
    """Whether a session logged SKIPPED (the rest of its scenario doesn't apply to this game version). False for a missing log."""
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            return any(SKIPPED.search(line) for line in f)
    except OSError:
        return False


def is_vanilla(path):
    """Whether this log is a vanilla reference session's (its BDVANILLA line). False for a missing log."""
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            return any(VANILLA.search(line) for line in f)
    except OSError:
        return False


def main(args):
    need_end = '--no-end' not in args
    mod_off = '--mod-off' in args
    mutated = '--mutated' in args
    paths = [a for a in args if a not in ('--no-end', '--mod-off', '--mutated')]
    total = 0
    for path in paths:
        problems, expected = judge(path, need_end=need_end, mod_off=mod_off, mutated=mutated)
        if mod_off:
            problems = [p for p in problems if not MOD_OFF_START.search(p)]
        for line in expected:
            print(f'expected {line}')
        for line in problems:
            print(f'PROBLEM {line}')
        total += len(problems)
    if not paths:
        print('PROBLEM judge.py: no log given')
        total += 1
    # Runners read this line: without it, the judgment never finished.
    print(f'JUDGED {total} problem(s)')
    return 1 if total else 0


if __name__ == '__main__':
    # Under python -I stdout is the ANSI code page (PYTHONIOENCODING ignored): a log's character it lacks must not crash the report.
    sys.stdout.reconfigure(errors='backslashreplace')
    sys.exit(main(sys.argv[1:]))
