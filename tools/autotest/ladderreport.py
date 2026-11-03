"""The same session at several deck sizes (ladder.ps1): how each moment of a combat changes with the size.

    python -I tools/autotest/ladderreport.py <log> <log> ... [--markdown]

One block per moment (idle, plays, end of turn, combat start), one row per log: seconds, fps, percentiles, where a frame's time
goes (ms per frame: process step, draw step on the main thread, GPU beside them, the rest), main thread use, GC, allocations.
Then the time of a play from the click to when the player can act again.
"""
import re
import statistics
import sys

FPS = re.compile(r'FPSSTAT ([^:]+): (\d+) frames in ([\d.]+) s, avg (\d+) fps, 1% low (\d+) fps, worst (\d+) ms, over 33 ms (\d+), over 50 ms (\d+), over 100 ms (\d+)')
FRAMES = re.compile(r'FRAMES ([^:]+): p50 ([\d.]+) p90 ([\d.]+) p99 ([\d.]+) p999 ([\d.]+) over8 (\d+) over16 (\d+) process avg ([\d.]+) p99 ([\d.]+) max ([\d.]+) '
                    r'draw avg ([\d.]+) p99 ([\d.]+) max ([\d.]+) rendercpu avg ([\d.]+) gpu avg ([\d.]+) p99 ([\d.]+) rest avg (-?[\d.]+) maincpu (\d+)% draws (\d+) objects (\d+) nodes (\d+) mb ([\d.]+) gc (\d+)')
PLAY = re.compile(r'PLAYTIME (\S+) (\d+) ms')
DECK = re.compile(r'Deck: (\d+) cards')
# Co-op imports go through the action queue: their result isn't in the log.
IMPORT = re.compile(r'> cmd bigdeck import ref')
COMBAT = re.compile(r'hand (\d+), draw (\d+), discard (\d+), exhaust (\d+)')
MOMENTS = ['idle', 'hover', 'mouse plays', 'plays', 'end of turn', 'combat start']


def moment(label):
    return re.sub(r' t\d+$', '', label)


def read(path):
    windows, plays, deck, imports = {}, [], 0, 0
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            if m := FPS.search(line):
                windows.setdefault(moment(m[1]), []).append(dict(frames=int(m[2]), wall=float(m[3]), low=int(m[5]), worst=int(m[6]), over33=int(m[7]), over100=int(m[9])))
            elif m := FRAMES.search(line):
                w = windows.get(moment(m[1]))
                if w:
                    w[-1].update(p50=float(m[2]), p99=float(m[4]), p999=float(m[5]), over8=int(m[6]), over16=int(m[7]), process=float(m[8]), processp99=float(m[9]), processmax=float(m[10]),
                                 draw=float(m[11]), drawp99=float(m[12]), rcpu=float(m[14]), gpu=float(m[15]), rest=float(m[17]), main=int(m[18]), draws=int(m[19]), objects=int(m[20]), nodes=int(m[21]), mb=float(m[22]), gc=int(m[23]))
            elif m := PLAY.search(line):
                plays.append(int(m[2]))
            if m := DECK.search(line):
                deck = int(m[1])
            elif m := IMPORT.search(line):
                imports += 1
    deck = deck or imports * 935 + 10
    return deck, windows, plays


def weighted(items, key):
    frames = sum(i['frames'] for i in items if key in i) or 1
    return sum(i[key] * i['frames'] for i in items if key in i) / frames


def row(name, deck, items):
    frames = sum(i['frames'] for i in items)
    wall = sum(i['wall'] for i in items)
    cells = [name, f'{deck}', f'{wall:.1f}', f'{frames / max(wall, 0.001):.0f}', f'{statistics.median(i["low"] for i in items):.0f}',
             f'{max(i.get("p999", 0) for i in items):.1f}', f'{max(i["worst"] for i in items)}', f'{sum(i.get("over8", 0) for i in items) * 100 / max(frames, 1):.1f}',
             f'{sum(i["over33"] for i in items)}', f'{sum(i["over100"] for i in items)}']
    cells += [f'{weighted(items, k):.2f}' for k in ('p50', 'process', 'processp99', 'draw', 'drawp99', 'gpu', 'rest')]
    cells += [f'{weighted(items, "main"):.0f}', f'{weighted(items, "draws"):.0f}', f'{sum(i.get("gc", 0) for i in items)}', f'{sum(i.get("mb", 0) for i in items):.0f}']
    return cells


HEAD = ['log', 'cards', 'time s', 'fps', '1%low', 'p99.9', 'worst', '%>8ms', '>33', '>100', 'p50', 'proc', 'proc99', 'draw', 'draw99', 'gpu', 'rest', 'main%', 'draws', 'gc ms', 'MB']


def print_table(rows, markdown):
    if markdown:
        print('| ' + ' | '.join(rows[0]) + ' |')
        print('|' + '---|' * len(rows[0]))
        for r in rows[1:]:
            print('| ' + ' | '.join(r) + ' |')
        return
    widths = [max(len(r[i]) for r in rows) for i in range(len(rows[0]))]
    for r in rows:
        print('  '.join(c.rjust(w) if i else c.ljust(w) for i, (c, w) in enumerate(zip(r, widths))))


def main():
    markdown = '--markdown' in sys.argv
    logs = [(p, *read(p)) for p in sys.argv[1:] if p != '--markdown']
    for name in MOMENTS:
        rows = [HEAD]
        for path, deck, windows, _ in logs:
            if name in windows:
                rows.append(row(path.replace('\\', '/').split('/')[-1].removesuffix('.log'), deck, windows[name]))
        if len(rows) > 1:
            print(f'## {name}')
            print_table(rows, markdown)
    rows = [['log', 'cards', 'plays', 'median ms', 'p90 ms', 'max ms', 'total s']]
    for path, deck, _, plays in logs:
        if plays:
            ordered = sorted(plays)
            rows.append([path.replace('\\', '/').split('/')[-1].removesuffix('.log'), f'{deck}', f'{len(plays)}', f'{statistics.median(plays):.0f}', f'{ordered[int(len(ordered) * 0.9)]}', f'{ordered[-1]}', f'{sum(plays) / 1000:.1f}'])
    if len(rows) > 1:
        print('## one play, click to next action')
        print_table(rows, markdown)


if __name__ == '__main__':
    main()
