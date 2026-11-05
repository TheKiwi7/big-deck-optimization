"""A timeline for etwreport.py from the trace itself, when none was recorded: windows of rapid plays = samples of the main thread under
PlayCardAction, in clusters. The game's log has no wall clock and reaches the disk late.
    python -I tools/profiling/etwtimeline.py <etwdump's folder>   (writes timeline.tsv there)
"""
import collections, datetime, os, sys
d = sys.argv[1]
ids = set()
for l in open(os.path.join(d, "addrs.tsv"), encoding="utf-8"):
    f = l.rstrip("\n").split("\t")
    if len(f) >= 4 and "PlayCardAction" in f[3]:
        ids.add(f[0])
print("addresses with PlayCardAction", len(ids))
stacks = set()
parents = {}
for l in open(os.path.join(d, "stacks.tsv"), encoding="utf-8"):
    i, par, ad = l.rstrip(chr(10)).split(chr(9))
    parents[i] = (par, ad)
known = {}
for i in parents:
    chain = []
    x = i
    while x in parents and x not in known:
        chain.append(x)
        x = parents[x][0]
    r = known.get(x, False)
    for c in reversed(chain):
        r = r or parents[c][1] in ids
        known[c] = r
stacks = {i for i, v in known.items() if v}
print("stacks", len(stacks))
t0 = None
times = collections.defaultdict(list)
for l in open(os.path.join(d, "samples.tsv"), encoding="utf-8"):
    f = l.rstrip("\n").split("\t")
    if f[0] == "start":
        t0 = datetime.datetime.fromisoformat(f[1][:26].rstrip("Z"))
    elif f[0] == "S" and f[5] in stacks:
        times[(f[2], f[3])].append(float(f[1]))
host = max(times, key=lambda k: len(times[k]))
ts = sorted(times[host])
print("thread", host, "samples", len(ts), "from %.1f to %.1f s" % (ts[0] / 1000, ts[-1] / 1000), "others", {k: len(v) for k, v in times.items() if k != host})
clusters = []
for t in ts:
    if clusters and t - clusters[-1][1] < 1500:
        clusters[-1][1] = t
    else:
        clusters.append([t, t])
out = open(os.path.join(d, "timeline.tsv"), "w", encoding="utf-8")
for a, b in clusters:
    print("window %.2f-%.2f s (%.0f ms)" % (a / 1000, b / 1000, b - a))
    for ms, cmd in ((a - 30, "play 10"), (b + 300, "fpsstat plays")):
        out.write((t0 + datetime.timedelta(milliseconds=ms)).isoformat() + "\t0\t" + cmd + "\n")
out.close()
