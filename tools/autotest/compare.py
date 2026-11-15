"""A/B comparison of two AutoTest logs (same script, one option differing).

    python -I tools/autotest/compare.py results/<a>.log results/<b>.log [--no-times] [--lines-only]

Checks that the CHECKSUM, STATE and CARDPLAY lines match line for line (values only, not timings): a mismatch shows the
first line that differs, and which values in it. Then each log's problems (judge.py: a failed line, a session cut short,
an error or a difference this mod found itself, a positive control that saw nothing). Then the per-card times of
'playcard' ("played X in N ms") side by side.

Verdict (last word of its line, exit code 0 only for IDENTICAL):
  IDENTICAL       the same lines, at least one, and no problem in either log
  DIFFERENT       the lines differ (or one log stopped early)
  NOT COMPARED    no line to compare on either side: nothing was proven
  PROBLEMS        the same lines, but a log has a problem (a check of the mod failed, a session didn't end...)
--lines-only: the verdict is about the lines alone (mutations.ps1, which judges the errors itself).
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
    checked, played, errors = [], [], {}
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
    return checked, played, errors


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


def main(a_path, b_path, times=True, lines_only=False):
    for path in (a_path, b_path):
        if not os.path.exists(path):
            print(f'no log {path}')
            print('NOT COMPARED')
            return 1
    a_checked, a_played, a_errors = read(a_path)
    b_checked, b_played, b_errors = read(b_path)
    mismatches = [(i, x, y) for i, (x, y) in enumerate(zip(a_checked, b_checked)) if x != y]
    print(f'checked lines: {len(a_checked)} vs {len(b_checked)}, mismatches: {len(mismatches)}')
    rooms = [sum(x.startswith('Entering') for x in side) for side in (a_checked, b_checked)]
    if any(rooms):
        print(f'AutoSlay rooms: {rooms[0]} vs {rooms[1]}')
    for i, x, y in mismatches[:20]:
        words = difference(x, y)
        print(f'  #{i}\n    A: {x}\n    B: {y}' + (f'\n    differs: {words}' if words else ''))
    if len(a_checked) != len(b_checked):
        print('  DIFFERENT LENGTHS (a session stopped early?)')
    for name, only in (('A', errors_only_in(a_errors, b_errors)), ('B', errors_only_in(b_errors, a_errors))):
        for text, count in list(only.items())[:8]:
            print(f'errors only in {name}: {count}x {text}')
    problems = 0
    for name, path in (('A', a_path), ('B', b_path)):
        found, expected = judge.judge(path)
        problems += len(found)
        for line in expected[:5]:
            print(f'expected in {name}: {line}')
        for line in found[:8]:
            print(f'problem in {name}: {line}')
    if mismatches or len(a_checked) != len(b_checked):
        verdict = 'DIFFERENT'
    elif not a_checked:
        verdict = 'NOT COMPARED'
    elif problems and not lines_only:
        verdict = 'PROBLEMS'
    else:
        verdict = 'IDENTICAL'
    print(verdict)
    ok = verdict == 'IDENTICAL'
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
    sys.exit(main(sys.argv[1], sys.argv[2], '--no-times' not in sys.argv, '--lines-only' in sys.argv))
