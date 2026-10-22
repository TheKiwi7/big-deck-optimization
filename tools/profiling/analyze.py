"""Aggregate a speedscope (evented) profile: per-thread totals, top inclusive and self frames on the busiest thread."""
import json, sys, collections

data = json.load(open(sys.argv[1], encoding="utf-8"))
frames = [f["name"] for f in data["shared"]["frames"]]
filter_sub = sys.argv[2] if len(sys.argv) > 2 else None
top_n = int(sys.argv[3]) if len(sys.argv) > 3 else 40

def walk(profile):
    """Yield (stack tuple, duration) for each interval between events."""
    stack, last = [], None
    for ev in profile["events"]:
        t = ev["at"]
        if last is not None and stack:
            yield tuple(stack), t - last
        last = t
        if ev["type"] == "O":
            stack.append(ev["frame"])
        else:
            if stack and stack[-1] == ev["frame"]:
                stack.pop()
            elif ev["frame"] in stack:
                idx = len(stack) - 1 - stack[::-1].index(ev["frame"])
                del stack[idx:]

threads = []
for p in data["profiles"]:
    total = 0.0
    for _, d in walk(p):
        total += d
    threads.append((total, p))
threads.sort(key=lambda x: -x[0])
print("unit:", data["profiles"][0].get("unit"))
for total, p in threads[:6]:
    print(f"thread {p['name']}: {total:.0f}")

main_name = sys.argv[4] if len(sys.argv) > 4 else None
sel = [x for x in threads if x[1]["name"] == main_name]
total, main = sel[0] if sel else threads[0]
incl = collections.Counter()
self_ = collections.Counter()
for stack, d in walk(main):
    if filter_sub and not any(filter_sub in frames[f] for f in stack):
        continue
    self_[frames[stack[-1]]] += d
    for f in set(stack):
        incl[frames[f]] += d

def short(n):
    return n if len(n) < 170 else n[:170] + "..."

print(f"\n== top inclusive on busiest thread (filter={filter_sub}) ==")
for name, d in incl.most_common(top_n):
    print(f"{d:9.0f} {100*d/total:5.1f}%  {short(name)}")
print("\n== top self ==")
for name, d in self_.most_common(25):
    print(f"{d:9.0f} {100*d/total:5.1f}%  {short(name)}")
