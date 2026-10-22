"""List threads of a speedscope profile by time spent in game code (MegaCrit frames). The top one is the main thread."""
import json, sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
frames = [f["name"] for f in data["shared"]["frames"]]


def walk(profile):
    stack, last = [], None
    for ev in profile["events"]:
        t = ev["at"]
        if last is not None and stack:
            yield tuple(stack), t - last
        last = t
        if ev["type"] == "O":
            stack.append(ev["frame"])
        elif stack and stack[-1] == ev["frame"]:
            stack.pop()
        elif ev["frame"] in stack:
            i = len(stack) - 1 - stack[::-1].index(ev["frame"])
            del stack[i:]


rows = []
for p in data["profiles"]:
    t = sum(d for st, d in walk(p) if any("MegaCrit" in frames[f] for f in st))
    if t > 0:
        rows.append((t, p["name"]))
for t, name in sorted(rows, reverse=True):
    print(f"{t:9.0f} ms  {name}")
