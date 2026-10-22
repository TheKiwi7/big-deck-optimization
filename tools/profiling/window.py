"""Top inclusive game frames on one thread within a time window [start_s, end_s] of the trace."""
import json, sys, collections

data = json.load(open(sys.argv[1], encoding="utf-8"))
thread = sys.argv[2]
start, end = float(sys.argv[3]) * 1000, float(sys.argv[4]) * 1000
top_n = int(sys.argv[5]) if len(sys.argv) > 5 else 40
frames = [f["name"] for f in data["shared"]["frames"]]
prof = next(p for p in data["profiles"] if p["name"] == thread)

incl = collections.Counter()
busy = 0.0
stack, last = [], None
for ev in prof["events"]:
    t = ev["at"]
    if last is not None and stack and start <= last < end:
        d = t - last
        names = {frames[f] for f in stack}
        if any("MegaCrit" in n for n in names):
            busy += d
            for n in names:
                if "MegaCrit" in n or "Limitless" in n or "BaseLib" in n or "BigDeck" in n or "Harmony" in n:
                    incl[n] += d
    last = t
    if ev["type"] == "O":
        stack.append(ev["frame"])
    elif stack and stack[-1] == ev["frame"]:
        stack.pop()
    elif ev["frame"] in stack:
        i = len(stack) - 1 - stack[::-1].index(ev["frame"])
        del stack[i:]


def short(n):
    n = n.replace("sts2!MegaCrit.Sts2.Core.", "").replace("0Harmony!dynamicClass.MegaCrit.Sts2.Core.", "[patched] ")
    return n if len(n) < 160 else n[:160] + "..."


print(f"window {start / 1000:.1f}-{end / 1000:.1f}s: {busy:.0f} ms in game code")
for n, d in incl.most_common(top_n):
    print(f"{d:7.0f}  {short(n)}")
