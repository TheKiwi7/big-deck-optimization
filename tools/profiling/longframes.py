"""What runs in long bursts of game code on one thread: the hitches, not the average.

A burst is a run of samples inside game code with gaps under <gap> ms (the engine's own work between callbacks shows as a
gap). Prints each burst over <min_ms> and, over all of them, the inclusive time per frame.
   python -I longframes.py <speedscope.json> "<thread>" <from s> <to s> [min_ms=16] [gap_ms=1.5] [top=45]
"""
import collections
import json
import sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
thread = sys.argv[2]
start, end = float(sys.argv[3]) * 1000, float(sys.argv[4]) * 1000
min_ms = float(sys.argv[5]) if len(sys.argv) > 5 else 16
gap = float(sys.argv[6]) if len(sys.argv) > 6 else 1.5
top = int(sys.argv[7]) if len(sys.argv) > 7 else 45
names = [f["name"] for f in data["shared"]["frames"]]
prof = next(p for p in data["profiles"] if p["name"] == thread)
GAME = ("MegaCrit", "Limitless", "BaseLib", "BigDeck", "Harmony", "RichText")
is_game = [not n.startswith(("Process", "Thread", "(Non", "Threads", "Activity")) and any(g in n for g in GAME) for n in names]
NOISE = ("AsyncTaskMethodBuilder", "AsyncMethodBuilderCore", "TaskHelper", "ExceptionHandling", "AwaitProcessFrame", "YieldOneFrame", "FinishAfterOneFrame")


def short(n):
    n = n.replace("sts2!MegaCrit.Sts2.Core.", "").replace("0Harmony!dynamicClass.MegaCrit.Sts2.Core.", "[p] ")
    return n[:110]


bursts = []
cur = None
stack, last = [], None
for ev in prof["events"]:
    t = ev["at"]
    if last is not None and stack and start <= last < end and t > last:
        game = {f for f in stack if is_game[f]}
        if game:
            if cur is None or last - cur["end"] > gap:
                if cur:
                    bursts.append(cur)
                cur = {"start": last, "end": t, "incl": collections.Counter(), "busy": 0.0}
            cur["end"] = t
            cur["busy"] += t - last
            for f in game:
                cur["incl"][f] += t - last
    last = t
    if ev["type"] == "O":
        stack.append(ev["frame"])
    elif stack and stack[-1] == ev["frame"]:
        stack.pop()
    elif ev["frame"] in stack:
        i = len(stack) - 1 - stack[::-1].index(ev["frame"])
        del stack[i:]
if cur:
    bursts.append(cur)

long_ones = [b for b in bursts if b["busy"] >= min_ms]
all_busy = sum(b["busy"] for b in bursts)
print(f"{len(bursts)} bursts, {all_busy:.0f} ms in game code; over {min_ms:.0f} ms: {len(long_ones)}, {sum(b['busy'] for b in long_ones):.0f} ms")
hist = collections.Counter()
for b in bursts:
    hist["<8" if b["busy"] < 8 else "8-16" if b["busy"] < 16 else "16-33" if b["busy"] < 33 else "33-66" if b["busy"] < 66 else "66-100" if b["busy"] < 100 else ">100"] += 1
print("bursts by ms:", dict(hist))
total = collections.Counter()
for b in long_ones:
    total.update(b["incl"])
print(f"\ninclusive over the bursts >= {min_ms:.0f} ms:")
for f, d in total.most_common(top * 3):
    if not any(x in names[f] for x in NOISE):
        print(f"{d:8.0f}  {short(names[f])}")
        top -= 1
        if top == 0:
            break
print("\nlongest bursts:")
for b in sorted(long_ones, key=lambda b: -b["busy"])[:12]:
    tops = [short(names[f]) for f, _ in b["incl"].most_common(40) if not any(x in names[f] for x in NOISE)][:4]
    print(f"{b['start'] / 1000:8.2f}s {b['busy']:6.0f} ms  " + " | ".join(t[:60] for t in tops))
