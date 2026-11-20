"""A/B comparison of two AutoTest logs (same script, one option differing).

    python -I tools/autotest/compare.py results/<a>.log results/<b>.log [--no-times] [--lines-only]

Checks that the CHECKSUM, STATE and CARDPLAY lines match line for line (values only, not timings): a mismatch shows the
first line that differs, and which values in it. Then each log's problems (judge.py: a failed line, a session cut short,
an error or a difference this mod found itself, a positive control that saw nothing), and the game errors B logs alone or far
more often than A (B is the side under test, par.ps1's mod on: an exception in game code from this mod's data has no frame of
this mod), but the engine's timing noise (judge.GAME_NOISE). Then the per-card times of 'playcard' ("played X in N ms") side
by side.

A is usually a vanilla reference session (par.ps1's mod-off side, launched with --bdvanilla: no patch of this mod installed). The
lines compared don't depend on that: this mod's own lines (patches applied, preflight, EFFECT counters, bdopt's status) are
never compared. B launched vanilla (its BDVANILLA line) is a problem: the side under test would be the game itself.

A game error B logs that one of B's own `expect` lines matches (a positive control's fault, the error it provokes on purpose:
judge.py lists it as expected) is listed as expected, not counted, whichever kind of pair it is.

Verdict (last word of its line, exit code 0 only for IDENTICAL and SKIPPED):
  IDENTICAL       the same lines, at least one, and no problem in either log
  SKIPPED         B logged SKIPPED (requiregame, a game mode this version lacks: the rest of its scenario doesn't apply to this
                  game version) before A's end (or neither compared a line), its lines A's first ones, no problem in either log:
                  not applicable on this version. B skipping a last part after the same lines as A is IDENTICAL
  DIFFERENT       the lines differ (or one log stopped early)
  NOT COMPARED    no line to compare on either side: nothing was proven
  PROBLEMS        the same lines, but a log has a problem (a check of the mod failed, a session didn't end, a game error with B
                  only...)
--lines-only: the verdict is about the lines alone (mutations.ps1, which judges the errors itself).
--prefix: up to what both reached (vanillacheck.ps1 -PrefixNames, two mod-off sessions of a scenario the game itself can't
                finish in time; none by default since outside-2k stops before its v0.113 combat with 'beforegame'): the lines
                of the shorter log must be the longer's first ones,
                and a problem both logs have alike (the same text, numbers aside: the game's own failure, mod off on both
                sides) is listed, not counted.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import judge  # noqa: E402

CHECKED = re.compile(r'AutoTest\s+[\d.]+s\s+(CHECKSUM [^:]+: \w+|STATE .*$|CARDPLAY .*$)')
PLAYED = re.compile(r'AutoTest\s+[\d.]+s\s+played (\S+) in (\d+) ms')
# AutoSlay's rooms: the run went the same way, and how far.
ROOM = re.compile(r'\[AutoSlay\] (Entering .*)$')
# A problem line of this mod (judge.py's rule), for the reports that list them.
MOD_PROBLEM = re.compile(judge.MOD_ERROR.pattern + '|' + judge.MOD_PROBLEM.pattern)


def normalized_error(line):
    """A game error without what changes between sessions (numbers, paths)."""
    line = re.sub(r'[A-Za-z]:\\.*?\.(json|dll|save|log|run|pck)\b', '<path>', line)
    return re.sub(r'\d+', 'N', line)[:130].strip()


def read(path):
    """(checked lines, played cards, {normalized game error: count}, {normalized game error: its lines as logged})."""
    checked, played, errors, raw = [], [], {}, {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\n')
            if m := CHECKED.search(line) or ROOM.search(line):
                checked.append(m.group(1))
            elif m := PLAYED.search(line):
                played.append((m.group(1), int(m.group(2))))
            elif line.startswith('[ERROR]'):
                key = normalized_error(line)
                errors[key] = errors.get(key, 0) + 1
                raw.setdefault(key, []).append(line)
    return checked, played, errors, raw


def difference(x, y):
    """The words that differ between two lines of the same kind."""
    a, b = x.split(), y.split()
    if len(a) != len(b):
        return ''
    words = [f'{p} -> {q}' for p, q in zip(a, b) if p != q]
    return ', '.join(words[:6])


def describe_mismatches(a_checked, b_checked, limit=6):
    """(index, A, B, differing words) of the first mismatches, line for line."""
    found = []
    for i, (x, y) in enumerate(zip(a_checked, b_checked)):
        if x != y:
            found.append((i, x, y, difference(x, y)))
            if len(found) >= limit:
                break
    return found


def errors_only_in(a, b):
    return {k: v for k, v in a.items() if k not in b}


def errors_grown(a, b):
    """The game error kinds B logs and A doesn't, or more than twice as often (3 more at least), but the engine's timing noise
    (judge.GAME_NOISE): with B the side under test, an exception in game code from this mod's data (a stale cache read later,
    thrown outside any patched method) has no frame of this mod, and judge.py alone wouldn't see it."""
    return {k: v for k, v in b.items()
            if not judge.GAME_NOISE.search(k) and (k not in a or (v > 2 * a[k] and v - a[k] >= 3))}


def shared_problem_key(text):
    """A judge.py problem without its log's name and numbers: the same failure on both sides compares equal."""
    return re.sub(r'\d+', 'N', text.split(': ', 1)[-1])


def main(a_path, b_path, times=True, lines_only=False, prefix=False):
    for path in (a_path, b_path):
        if not os.path.exists(path):
            print(f'no log {path}')
            print('NOT COMPARED')
            return 1
    a_checked, a_played, a_errors, _ = read(a_path)
    b_checked, b_played, b_errors, b_raw = read(b_path)
    a_skipped, b_skipped = judge.is_skipped(a_path), judge.is_skipped(b_path)
    # B skipped for this game version: its lines must be A's first ones (both skipped: the same lines), nothing more is compared.
    skipped = b_skipped and len(b_checked) <= len(a_checked) and (not a_skipped or len(b_checked) == len(a_checked))
    mismatches = [(i, x, y) for i, (x, y) in enumerate(zip(a_checked, b_checked)) if x != y]
    print(f'checked lines: {len(a_checked)} vs {len(b_checked)}, mismatches: {len(mismatches)}')
    rooms = [sum(x.startswith('Entering') for x in side) for side in (a_checked, b_checked)]
    if any(rooms):
        print(f'AutoSlay rooms: {rooms[0]} vs {rooms[1]}')
    for i, x, y in mismatches[:20]:
        words = difference(x, y)
        print(f'  #{i}\n    A: {x}\n    B: {y}' + (f'\n    differs: {words}' if words else ''))
    if skipped:
        print(f'  B SKIPPED (the rest of its scenario does not apply to this game version): compared up to where it stopped, {len(b_checked)} lines'
              + (' (A skipped too)' if a_skipped else ''))
    elif len(a_checked) != len(b_checked):
        print(f'  DIFFERENT LENGTHS, compared up to what both reached: {min(len(a_checked), len(b_checked))} lines' if prefix
              else '  DIFFERENT LENGTHS (a session stopped early?)')
    for name, only in (('A', errors_only_in(a_errors, b_errors)), ('B', errors_only_in(b_errors, a_errors))):
        for text, count in list(only.items())[:8]:
            print(f'errors only in {name}: {count}x {text}')
    problems = 0
    # B is the side under test (par.ps1: mod on): a game error it logs alone, or far more often, is a problem. But one its own
    # expect lines match on every line logged (a positive control's provoked error, judge.py's expected): listed, not counted.
    b_expects = judge.expect_patterns(b_path)
    for text, count in errors_grown(a_errors, b_errors).items():
        if b_expects and all(any(rx.search(line) for rx in b_expects) for line in b_raw.get(text, [])):
            print(f'expected in B (game error, its expect lines): {count}x {text}')
            continue
        problems += 1
        if problems <= 8:
            print(f'problem in B: game error only (or {count} times vs {a_errors.get(text, 0)}) with B: {text}')
    # B is the side under test: launched vanilla it compares the game with itself.
    if judge.is_vanilla(b_path):
        problems += 1
        print('problem in B: a vanilla reference session (its BDVANILLA line, --bdvanilla): the side under test ran without the mod')
    judged = {name: judge.judge(path) for name, path in (('A', a_path), ('B', b_path))}
    # --prefix: the same failure on both sides is the game's (both mod off), listed only.
    shared = ({shared_problem_key(p) for p in judged['A'][0]} & {shared_problem_key(p) for p in judged['B'][0]}) if prefix else set()
    for name, (found, expected) in judged.items():
        for line in expected[:5]:
            print(f'expected in {name}: {line}')
        found = [p for p in found if shared_problem_key(p) not in shared]
        problems += len(found)
        for line in found[:8]:
            print(f'problem in {name}: {line}')
    for key in sorted(shared)[:8]:
        print(f'on both sides (not counted, --prefix): {key[:200]}')
    if mismatches:
        verdict = 'DIFFERENT'
    elif skipped and (len(b_checked) < len(a_checked) or not b_checked):
        verdict = 'PROBLEMS' if problems and not lines_only else 'SKIPPED'
    elif len(a_checked) != len(b_checked) and not prefix:
        verdict = 'DIFFERENT'
    elif not a_checked or (prefix and not b_checked):
        verdict = 'NOT COMPARED'
    elif problems and not lines_only:
        verdict = 'PROBLEMS'
    else:
        verdict = 'IDENTICAL'
    print(verdict)
    ok = verdict in ('IDENTICAL', 'SKIPPED')
    if not times:
        return 0 if ok else 1

    b_times = dict(b_played)
    print(f'\n{"card":<22}{"A ms":>9}{"B ms":>9}{"A/B":>7}')
    total_a = total_b = 0
    for card, ms in a_played:
        if card in b_times:
            other = b_times[card]
            total_a += ms
            total_b += other
            print(f'{card:<22}{ms:>9}{other:>9}{ms / max(other, 1):>7.1f}')
        else:
            print(f'{card:<22}{ms:>9}{"-":>9}')
    print(f'{"total (both)":<22}{total_a:>9}{total_b:>9}{total_a / max(total_b, 1):>7.1f}')
    return 0 if ok else 1


if __name__ == '__main__':
    # Under python -I stdout is the ANSI code page (PYTHONIOENCODING ignored): a log's character it lacks must not crash the report.
    sys.stdout.reconfigure(errors='backslashreplace')
    sys.exit(main(sys.argv[1], sys.argv[2], '--no-times' not in sys.argv, '--lines-only' in sys.argv, '--prefix' in sys.argv))
