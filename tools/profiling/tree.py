"""For frames matching a substring on one thread: inclusive time, top callers and top callees."""
import json, sys, collections

data = json.load(open(sys.argv[1], encoding="utf-8"))
thread = sys.argv[2]
targets = sys.argv[3:]
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
            idx = len(stack) - 1 - stack[::-1].index(ev["frame"])
            del stack[idx:]

prof = next(p for p in data["profiles"] if p["name"] == thread)
samples = list(walk(prof))

def short(n):
    n = n.replace("sts2!MegaCrit.Sts2.Core.", "").replace("System.Private.CoreLib.il!", "")
    return n if len(n) < 150 else n[:150] + "..."

for target in targets:
    incl = 0.0
    callers = collections.Counter()
    callees = collections.Counter()
    for stack, d in samples:
        idx = next((i for i, f in enumerate(stack) if target in frames[f]), None)
        if idx is None:
            continue
        incl += d
        # nearest non-generic caller
        for j in range(idx - 1, -1, -1):
            n = frames[stack[j]]
            if "MegaCrit" in n or "Harmony" in n or "Limitless" in n or "BaseLib" in n:
                callers[n] += d
                break
        if idx + 1 < len(stack):
            callees[frames[stack[idx + 1]]] += d
    print(f"\n######## {target}: {incl:.0f} ms")
    print("  callers:")
    for n, d in callers.most_common(8):
        print(f"    {d:7.0f}  {short(n)}")
    print("  callees:")
    for n, d in callees.most_common(12):
        print(f"    {d:7.0f}  {short(n)}")
