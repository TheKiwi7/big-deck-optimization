"""Compares two sweep sessions (AutoTest 'sweep'): the items whose time depends the most on the deck size.
   python -I sweepdiff.py <big.log> <small.log> [top n]"""
import re, sys

LINE = re.compile(r"SWEEP (\S+) (.+?) (\S+) wall (\d+) worst (\d+) over50 (\d+) gc (\d+) mb ([\d.]+) combat (\d+) deck (\d+)")


def read(path):
    items = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        m = LINE.search(line)
        if m:
            kind, item, step, wall, worst, over, gc, mb, combat, deck = m.groups()
            items[(kind, item, step)] = (int(wall), int(worst), int(over), int(gc), float(mb), int(combat), int(deck))
    return items


big, small = read(sys.argv[1]), read(sys.argv[2])
top = int(sys.argv[3]) if len(sys.argv) > 3 else 40
rows = []
for key, b in big.items():
    s = small.get(key, (0, 0, 0, 0, 0.0, 0, 0))
    rows.append((b[0] - s[0], b[1], key, b, s))
print(f"{len(big)} items vs {len(small)}; sum of wall {sum(b[0] for b in big.values()) / 1000:.0f} s vs {sum(s[0] for s in small.values()) / 1000:.0f} s")
print("by extra time:")
print(f"{'extra':>7} {'wall':>7} {'small':>6} {'worst':>6} {'gc':>5} {'MB':>7} {'cards':>6}  item")
for extra, _, key, b, s in sorted(rows, key=lambda r: -r[0])[:top]:
    print(f"{extra:7d} {b[0]:7d} {s[0]:6d} {b[1]:6d} {b[3]:5d} {b[4]:7.1f} {b[5]:6d}  {' '.join(key)}")
print("by worst frame:")
for extra, _, key, b, s in sorted(rows, key=lambda r: -r[1])[:top // 2]:
    print(f"{extra:7d} {b[0]:7d} {s[0]:6d} {b[1]:6d} {b[3]:5d} {b[4]:7.1f} {b[5]:6d}  {' '.join(key)}")
print("by allocations:")
for extra, _, key, b, s in sorted(rows, key=lambda r: -r[3][4])[:top // 2]:
    print(f"{extra:7d} {b[0]:7d} {s[0]:6d} {b[1]:6d} {b[3]:5d} {b[4]:7.1f} {b[5]:6d}  {' '.join(key)}")
