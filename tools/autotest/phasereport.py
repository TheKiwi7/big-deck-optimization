"""Where a whole AutoSlay run spends its frames, by kind of step (PHASE lines of the autoslay command):

    python -I tools/autotest/phasereport.py <log> [<other log>] [--top 15]

For each kind: how many steps, time, frames, frames over 8.3 / 33 / 100 ms, worst frame, GC pauses, allocations, main thread
time; then the steps with the worst frames. Only a rendered session's frame times mean anything (run.ps1, alone).
"""
import re
import sys

PHASE = re.compile(r'PHASE (.+?) \| wall (\d+) frames (\d+) worst (\d+) gc (\d+) mb ([\d.]+) cpu (\d+) long (\S*)')
NORMAL = [
    (re.compile(r'^Combat turn.*'), 'Combat turn'),
    (re.compile(r'^Entering (\w+) room.*'), r'Enter \1'),
    (re.compile(r'^Handling screen: (\w+).*'), r'\1'),
]


def kind_of(name):
    for pattern, replacement in NORMAL:
        if pattern.match(name):
            return pattern.sub(replacement, name)
    return name


def read(path):
    steps = []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if m := PHASE.search(line):
                long_frames = [int(x) for x in m.group(8).split(',') if x]
                steps.append(dict(name=m.group(1), kind=kind_of(m.group(1)), wall=int(m.group(2)), frames=int(m.group(3)), worst=int(m.group(4)),
                                  gc=int(m.group(5)), mb=float(m.group(6)), cpu=int(m.group(7)), long=long_frames))
    return steps


def table(steps):
    kinds = {}
    for step in steps:
        kinds.setdefault(step['kind'], []).append(step)
    out = [f'{"kind":<30} {"steps":>5} {"wall s":>7} {"frames":>7} {"fps":>5} {">8ms":>6} {">33ms":>5} {">100":>5} {"worst":>6} {"gc ms":>6} {"MB":>7} {"cpu s":>6}']
    rows = sorted(kinds.items(), key=lambda kv: -sum(sum(ms for ms in s['long'] if ms > 33) for s in kv[1]))
    for kind, items in rows:
        wall = sum(s['wall'] for s in items)
        frames = sum(s['frames'] for s in items)
        long_frames = [ms for s in items for ms in s['long']]
        out.append(f'{kind:<30} {len(items):>5} {wall / 1000:>7.1f} {frames:>7} {frames * 1000 / max(wall, 1):>5.0f} {len(long_frames):>6} '
                   f'{sum(1 for ms in long_frames if ms > 33):>5} {sum(1 for ms in long_frames if ms > 100):>5} {max((s["worst"] for s in items), default=0):>6} '
                   f'{sum(s["gc"] for s in items):>6} {sum(s["mb"] for s in items):>7.0f} {sum(s["cpu"] for s in items) / 1000:>6.1f}')
    return out


def main():
    args = sys.argv[1:]
    top = 15
    if '--top' in args:
        i = args.index('--top')
        top = int(args[i + 1])
        del args[i:i + 2]
    for path in args:
        steps = read(path)
        print(f'## {path}: {len(steps)} steps')
        print('\n'.join(table(steps)))
        print('worst frames:')
        for step in sorted(steps, key=lambda s: -s['worst'])[:top]:
            print(f'  {step["worst"]:>6} ms  gc {step["gc"]:>5}  {step["mb"]:>7.1f} MB  {step["name"]}')
        print()


if __name__ == '__main__':
    main()
