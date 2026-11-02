"""Frame times measured outside the game (PresentMon, GPU included): what run.ps1 -PresentMon recorded.

    python -I tools/autotest/presentmon.py results/<name>.presentmon.csv [skip seconds]

An independent reference for the game's own FPSSTAT lines. "skip": leaves out the start of the capture (loading).
"""
import csv
import sys


def main(path, skip=0.0):
    with open(path, encoding='utf-8-sig', errors='replace', newline='') as f:
        rows = list(csv.DictReader(f))
    if not rows:
        print('no frame recorded')
        return 1
    # The last line can be cut short when the game closes.
    rows = [r for r in rows if r.get('TimeInMs') and r.get('MsBetweenPresents') not in (None, '', 'NA')]
    start = float(rows[0]['TimeInMs'])
    frames = [float(r['MsBetweenPresents']) for r in rows if float(r['TimeInMs']) - start >= skip * 1000]
    if not frames:
        print('no frame after the skipped part')
        return 1
    seconds = sum(frames) / 1000
    ordered = sorted(frames, reverse=True)
    slow = ordered[:max(1, len(ordered) // 100)]
    print(f'{len(frames)} frames in {seconds:.1f} s, avg {len(frames) / seconds:.0f} fps, 1% low {1000 / (sum(slow) / len(slow)):.0f} fps, '
          f'worst {ordered[0]:.0f} ms, over 33 ms {sum(1 for x in frames if x > 33)}, over 50 ms {sum(1 for x in frames if x > 50)}, '
          f'over 100 ms {sum(1 for x in frames if x > 100)}; {rows[0]["PresentMode"]}, sync interval {rows[0]["SyncInterval"]}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else 0.0))
