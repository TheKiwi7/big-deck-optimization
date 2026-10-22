"""Contiguous spans where a frame matching a substring is on the stack of one thread (gap-merged)."""
import json, sys

data = json.load(open(sys.argv[1], encoding="utf-8"))
thread, target = sys.argv[2], sys.argv[3]
gap = float(sys.argv[4]) if len(sys.argv) > 4 else 2.0
frames = [f["name"] for f in data["shared"]["frames"]]
prof = next(p for p in data["profiles"] if p["name"] == thread)

spans, cur, stack, last = [], None, [], None
for ev in prof["events"]:
    t = ev["at"]
    if last is not None and stack and any(target in frames[f] for f in stack):
        if cur and last - cur[1] <= gap:
            cur[1] = t
        else:
            if cur:
                spans.append(cur)
            cur = [last, t]
    last = t
    if ev["type"] == "O":
        stack.append(ev["frame"])
    elif stack and stack[-1] == ev["frame"]:
        stack.pop()
    elif ev["frame"] in stack:
        i = len(stack) - 1 - stack[::-1].index(ev["frame"])
        del stack[i:]
if cur:
    spans.append(cur)
durs = sorted((e - s for s, e in spans), reverse=True)
print(f"{target}: {len(spans)} spans, total {sum(durs):.0f} ms")
print("longest:", [round(d) for d in durs[:20]])
print("timeline (start s, dur ms):", [(round(s / 1000, 1), round(e - s)) for s, e in spans if e - s > 30][:40])
