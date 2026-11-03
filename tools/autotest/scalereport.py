"""What deck size still costs, over the whole game, from two mod-on sweeps at different sizes (parsweep.ps1 -Imports):

    python -I tools/autotest/scalereport.py results/<version> 1 11 [relics cards ...] [--top 25]

For each kind and step: the typical size cost of that step (what every item pays, a common path) and the items paying far
more than the others (what that item itself does). Main thread CPU time and allocations both hold with sessions sharing
the cores; frame times don't, so they aren't used. Writes report-scale.md next to the parts.
"""
import glob
import os
import re
import statistics
import sys

STEP = re.compile(r'SWEEP (\S+) (.+?) (\S+) wall (\d+) worst (\d+) over50 (\d+) gc (\d+) mb ([\d.]+) combat (\d+) deck (\d+) cpu (\d+)')
KINDS = ['cards', 'relics', 'potions', 'encounters', 'powers']


def read(folder, kind, imports):
    """{(item, step): (cpu ms, MB, gc ms, combat cards)}"""
    steps = {}
    for path in glob.glob(os.path.join(folder, f'parts-{kind}-i{imports}', f'{kind}-on-c*-p*.log')):
        with open(path, encoding='utf-8', errors='replace') as f:
            for line in f:
                if m := STEP.search(line):
                    steps[(m.group(2), m.group(3))] = (int(m.group(11)), float(m.group(8)), int(m.group(7)), int(m.group(9)))
    return steps


def step_name(step):
    """Encounter steps carry their turn in the item: t1 play1 and t2 play1 are the same path."""
    return step


def report(folder, small, big, kind, top):
    a, b = read(folder, kind, small), read(folder, kind, big)
    common = [k for k in b if k in a]
    out = [f'## {kind}: {len(common)} steps at both sizes ({len(a)} small, {len(b)} big)']
    if not common:
        return out
    cards = statistics.median(b[k][3] for k in common)
    cards_small = statistics.median(a[k][3] for k in common)
    out.append(f'combat cards, median: {cards_small:.0f} -> {cards:.0f}. Extra per step = big - small.')
    by_step = {}
    for key in common:
        by_step.setdefault(step_name(key[1]), []).append(key)
    out.append('per step, median of all items (what any item pays):')
    out.append(f'  {"step":<14} {"items":>5} {"cpu small":>9} {"cpu big":>8} {"MB small":>8} {"MB big":>7} {"gc big":>6}')
    typical = {}
    for step, keys in sorted(by_step.items(), key=lambda kv: -statistics.median(b[k][0] for k in kv[1])):
        med = [statistics.median(side[k][i] for k in keys) for side in (a, b) for i in (0, 1)]
        typical[step] = (med[2] - med[0], med[3] - med[1])
        gc = statistics.median(b[k][2] for k in keys)
        out.append(f'  {step:<14} {len(keys):>5} {med[0]:>9.0f} {med[2]:>8.0f} {med[1]:>8.1f} {med[3]:>7.1f} {gc:>6.0f}')
    rows = []
    for key in common:
        extra_cpu = b[key][0] - a[key][0]
        extra_mb = b[key][1] - a[key][1]
        rows.append((extra_cpu - typical[step_name(key[1])][0], extra_mb - typical[step_name(key[1])][1], extra_cpu, extra_mb, key))
    header = f'  {"above typ":>9} {"extra":>7} {"small":>7} {"big":>7}   {"MB small":>8} {"MB big":>8}  item'
    out.append('items above their step, by main thread time (ms):')
    out.append(header)
    for above, _, extra, _, key in sorted(rows, key=lambda r: -r[0])[:top]:
        out.append(f'  {above:>9.0f} {extra:>7.0f} {a[key][0]:>7} {b[key][0]:>7}   {a[key][1]:>8.1f} {b[key][1]:>8.1f}  {key[0]} {key[1]}')
    out.append('items above their step, by allocations (MB):')
    out.append(f'  {"above typ":>9} {"extra":>7} {"small":>7} {"big":>7}   {"cpu small":>9} {"cpu big":>7}  item')
    for _, above, _, extra, key in sorted(rows, key=lambda r: -r[1])[:top]:
        out.append(f'  {above:>9.1f} {extra:>7.1f} {a[key][1]:>7.1f} {b[key][1]:>7.1f}   {a[key][0]:>9} {b[key][0]:>7}  {key[0]} {key[1]}')
    return out


def main():
    args = sys.argv[1:]
    top = 25
    if '--top' in args:
        i = args.index('--top')
        top = int(args[i + 1])
        del args[i:i + 2]
    folder, small, big = args[0], int(args[1]), int(args[2])
    kinds = args[3:] or KINDS
    lines = []
    for kind in kinds:
        lines += report(folder, small, big, kind, top) + ['']
    text = '\n'.join(lines)
    print(text)
    with open(os.path.join(folder, 'report-scale.md'), 'w', encoding='utf-8') as f:
        f.write(text)


if __name__ == '__main__':
    main()
