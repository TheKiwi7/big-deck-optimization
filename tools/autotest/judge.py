"""Whether a session's log shows a problem, whatever it is compared with: the one judgment every runner uses (par.ps1,
parcoop.ps1, coop4.ps1, coop.ps1, run.ps1, release-incopy.ps1, compare.py, sweepreport.py).

    python -I tools/autotest/judge.py [--no-end] [--mod-off] <log> [<log> ...]
        one PROBLEM line per problem, then a last line 'JUDGED <n> problem(s)'; exit 1 when any. A runner that doesn't see the
        JUDGED line must count the logs as failed (this script crashed or never ran).
    --no-end: the session isn't expected to end by itself (release-incopy.ps1 closes the game once the patches are applied).
    --mod-off: a mod-off side (parcoop.ps1's -autooff twins and *off pairs): the mod found off when the game started is no
        problem there. A co-op host's `cmd bdopt off` at the menu is saved in the slot both peers share, so the client often
        starts with the mod off (nothing applied: the game's own code, as that side wants).

A problem:
- the log is missing, or the session didn't reach its end (`script finished`, `> quit`, `handoff`, or the game's auto player
  ending its run: see below);
- a failed line: `FAILED line` (a failed script line, a console command that failed included), `ABORT`, the script's
  `TIMEOUT`, `SWEEP FAILED` / `SWEEP ABORT` (not for parsweep's parts: `sweep=True`, its restarts are the design);
- anything this mod logs as an error ([ERROR] lines: its patches log errors only when they turn themselves off, when
  their result differs from the game's, or when a patch can't apply), and the lines saying a check found a difference
  (MISMATCH, `mismatches N`, `differs from`, `changed unseen`...) or that the mod was off from the start (saved settings);
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

A scenario expecting some of these (positive controls) says `expect <regex>`: the problem lines that regex matches, anywhere
in the log, are listed as expected instead, and it must match at least one line of the session (not the script's own lines),
or that is a problem too: a control that saw nothing. The regex is checked by .NET when the script runs and by Python here:
one Python can't compile is a problem.
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
# rethrowing the game's own exception as the game would, a pile snapshot read hitting the game's own exception (the game's call
# then made, as mod off), and the test tooling's own timers and probes. A new warning is a problem
# until it is listed here: never missed silently. A scenario expecting one says `expect <regex>`.
MOD_WARN = re.compile(r'^\[WARN\].*\[BigDeckOptimization\]')
MOD_WARN_OK = re.compile(r'\[BigDeckOptimization\] (SettingsMenu: (settings menu unavailable|the saved "off" can\'t be changed)'
                         r'|menu texts for \S+ unreadable|BaseLibSettings: the mod stays on during a co-op run'
                         r'|PatchGuard: faulted patches not removed yet|HookFilter: a mirrored hook threw \(passed on'
                         r'|SnapshotCardReuse: the game.s own code threw while a pile.s snapshot was read'
                         r'|PerfProbe: |CombatStartTimer: )')
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
    with open(path, encoding='utf-8', errors='replace') as f:
        for number, line in enumerate(f, 1):
            line = line.rstrip('\n').rstrip('\r')
            if not line.startswith('['):
                if GODOT_ERROR.match(line):
                    error_head = (number, line)
                    continue
                if error_head and MOD_FRAME.search(line):
                    found.append((error_head[0], f'{error_head[1][:150]} (through this mod: {line.strip()[:120]})'))
                    error_head = None
                continue
            error_head = None
            if ECHO.search(line) or EXPECT.search(line):
                continue  # The script's own lines: '> expect ...', '> cmd ...' say what is asked, not what happened.
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


def judge(path, sweep=False, need_end=True):
    """(problems, expected): lists of texts. Never raises: a failure to judge is a problem."""
    name = os.path.basename(path)
    try:
        return _judge(path, name, sweep, need_end)
    except Exception as e:  # noqa: BLE001 - a judge that can't judge must say so, not pass
        return [f'{name}: judge.py could not judge this log: {type(e).__name__}: {e}'], []


def _judge(path, name, sweep, need_end):
    if not os.path.exists(path):
        return [f'{name}: no log'], []
    with open(path, encoding='utf-8', errors='replace') as f:
        text = f.read().splitlines()
    problems, expected = [], []
    expectations = []
    for line in text:
        if m := EXPECT.search(line):
            try:
                expectations.append(re.compile(m.group(1).strip()))
            except re.error as e:
                problems.append(f'{name}: expect "{m.group(1).strip()}" is not a regex Python reads ({e}): write it in the syntax both share')
    # Anywhere in the log: what the mod logs while loading (its settings read at the start) comes before the script's lines.
    previous_expected = aborted_as_expected = False
    for number, line in problem_lines(path, sweep):
        # The ABORT after an expected failed line (a positive control's last line failing on purpose) is expected with it,
        # and the session ended where the control meant it to.
        previous_expected = any(rx.search(line) for rx in expectations) or (line.startswith('ABORT:') and previous_expected)
        aborted_as_expected |= previous_expected and line.startswith('ABORT:')
        if previous_expected:
            expected.append(f'{name}: {line[:200]}')
        else:
            problems.append(f'{name}: {line[:200]}')
    if need_end and not aborted_as_expected and not any(END.search(line) for line in text):
        problems.insert(0, f'{name}: did not reach its end')
    if need_end and (slay := autoslay_problem(text)):
        problems.append(f'{name}: {slay}')
    # Seen in a line of the session, not in the script's own lines (its echo "> expect ...", the EXPECT line).
    session = [line for line in text if not ECHO.search(line) and not EXPECT.search(line)]
    for rx in expectations:
        if not any(rx.search(line) for line in session):
            problems.append(f'{name}: expected "{rx.pattern}" never seen (a positive control that saw nothing)')
    return problems, expected


# Not a problem on a mod-off side (--mod-off): the mod off from the start runs the game's own code.
MOD_OFF_START = re.compile(r'turned off in the settings|turned off when the game started')


def main(args):
    need_end = '--no-end' not in args
    mod_off = '--mod-off' in args
    paths = [a for a in args if a not in ('--no-end', '--mod-off')]
    total = 0
    for path in paths:
        problems, expected = judge(path, need_end=need_end)
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
