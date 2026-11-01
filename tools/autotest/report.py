"""Before/after table of two AutoTest logs, from their FPSSTAT, GCSTAT, SWEEP and "played" lines.

    python -I tools/autotest/report.py <before.log> <after.log> [--markdown]

For a same scenario on two builds. Warns when one session ran under the game's fps cap (averages and 1% lows aren't
comparable then, worst frames are) and when a scenario command was unknown or failed in one of them.
"""
import re
import sys

FPS = re.compile(r'FPSSTAT ([^:]+): \d+ frames in ([\d.]+) s, avg (\d+) fps, 1% low (\d+) fps, worst (\d+) ms, over 33 ms (\d+), over 50 ms (\d+), over 100 ms (\d+)')
GC = re.compile(r'GCSTAT ([^:]+): (\d+) ms, GC pauses (\d+) ms')
SWEEP = re.compile(r'SWEEP (\w+ \S+ \S+(?: \S+)?) wall (\d+) worst (\d+) over50 (\d+) gc (\d+)')
PLAYED = re.compile(r'played (\S+) in (\d+) ms')
HEAP = re.compile(r'GCFULL ([^:]+): live heap ([\d.]+) MB')
CAP = re.compile(r'display: max fps (\d+)')
UNCAPPED = re.compile(r'display: max fps 0\b')
FAILED = re.compile(r'(FAILED line .*|unknown script command.*|SWEEP FAILED .*)')


def read(path):
    data = {'fps': {}, 'gc': {}, 'sweep': {}, 'played': {}, 'heap': {}, 'capped': False, 'failed': []}
    cap = None
    uncapped = False
    plays = {}
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if m := FPS.search(line):
                data['fps'][m.group(1)] = dict(avg=int(m[3]), low=int(m[4]), worst=int(m[5]), over100=int(m[8]))
            elif m := GC.search(line):
                data['gc'][m.group(1)] = dict(wall=int(m[2]), pause=int(m[3]))
            elif m := SWEEP.search(line):
                data['sweep'][m.group(1)] = dict(wall=int(m[2]), worst=int(m[3]), over50=int(m[4]), gc=int(m[5]))
            elif m := PLAYED.search(line):
                plays[m.group(1)] = plays.get(m.group(1), 0) + 1
                data['played'][f'{m.group(1)} #{plays[m.group(1)]}'] = int(m[2])
            elif m := HEAP.search(line):
                data['heap'][m.group(1)] = float(m[2])
            elif m := CAP.search(line):
                cap = int(m[1])
                uncapped |= bool(UNCAPPED.search(line))
            if m := FAILED.search(line):
                data['failed'].append(m.group(1)[:110])
    data['capped'] = bool(cap) and not uncapped
    return data


def rows_for(title, a, b, columns):
    keys = [k for k in a if k in b]
    if not keys:
        return []
    return [(title, k, [(c, a[k][c], b[k][c]) for c in columns]) for k in keys]


def main(a_path, b_path, markdown):
    a, b = read(a_path), read(b_path)
    print(f'A: {a_path}\nB: {b_path}')
    if a['capped'] != b['capped']:
        print(f"WARNING: only {'A' if a['capped'] else 'B'} ran under the game's fps cap: averages and 1% lows aren't comparable, worst frames are")
    elif a['capped']:
        print("both ran under the game's fps cap: an average near it means at least that much")
    for name, data in (('A', a), ('B', b)):
        for failed in data['failed'][:5]:
            print(f'WARNING: {name}: {failed}')
    sections = rows_for('fps', a['fps'], b['fps'], ['avg', 'low', 'worst', 'over100'])
    sections += rows_for('gc', a['gc'], b['gc'], ['wall', 'pause'])
    sections += rows_for('sweep', a['sweep'], b['sweep'], ['wall', 'worst', 'over50', 'gc'])
    heap = {k: {'MB': v} for k, v in a['heap'].items()}
    other = {k: {'MB': v} for k, v in b['heap'].items()}
    sections += rows_for('heap', heap, other, ['MB'])
    played = {k: {'ms': v} for k, v in a['played'].items()}
    other = {k: {'ms': v} for k, v in b['played'].items()}
    sections += rows_for('played', played, other, ['ms'])
    if not sections:
        print('nothing in common between these logs')
        return 1
    for title, key, cells in sections:
        text = ', '.join(f'{c} {x} -> {y}' for c, x, y in cells)
        print(f'| {title} | {key} | {text} |' if markdown else f'{title:<7}{key:<44}{text}')
    only_a = [k for k in a['fps'] if k not in b['fps']]
    only_b = [k for k in b['fps'] if k not in a['fps']]
    if only_a or only_b:
        print(f'only in A: {only_a}, only in B: {only_b}')
    return 0


if __name__ == '__main__':
    args = [x for x in sys.argv[1:] if x != '--markdown']
    sys.exit(main(args[0], args[1], '--markdown' in sys.argv))
