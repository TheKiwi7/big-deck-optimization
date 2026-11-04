# Where a WPR trace's time goes, per phase of a scenario: etwdump's files, dwarfsyms' function table, a timeline of scenario lines.
#   python -I tools/profiling/etwreport.py <dump dir> <funcs.tsv> <timeline.tsv> [--phase plays|combat|end|idle] [--top 40] [--tree <substring>] [--frames <min ms>]
# CPU samples are 1 ms of one core; a wait is the time the thread was off the CPU, with the stack it waited on.
import argparse, bisect, collections, datetime, os, re

ap = argparse.ArgumentParser()
ap.add_argument("dump")
ap.add_argument("funcs")
ap.add_argument("timeline")
ap.add_argument("--phase", default="plays")
ap.add_argument("--top", type=int, default=40)
ap.add_argument("--tree", default="")
ap.add_argument("--thread", type=int, default=0)
ap.add_argument("--from", dest="start", default="", help="with --to: windows from each scenario line matching this regex")
ap.add_argument("--to", default="", help="to the next line matching this regex")
ap.add_argument("--frames", type=float, default=0, help="only frames of at least this many ms (a frame ends when drawing gives way to anything else)")
a = ap.parse_args()

starts, ends, names = [], [], []
for l in open(a.funcs, encoding="utf-8"):
    s, e, n = l.rstrip("\n").split("\t", 2)
    starts.append(int(s, 16)); ends.append(int(e, 16)); names.append(n)


def engine(rva):
    i = bisect.bisect_right(starts, rva) - 1
    return names[i] if i >= 0 and rva < ends[i] else "engine?%x" % rva


addr = {}
for l in open(os.path.join(a.dump, "addrs.tsv"), encoding="utf-8"):
    i, mod, rva, method = l.rstrip("\n").split("\t", 3)
    mod = os.path.basename(mod.replace("\\", "/")).lower()
    if mod.startswith("slaythespire2"):
        addr[int(i)] = (mod, engine(int(rva, 16)))
    elif method:
        addr[int(i)] = (mod or "jit", "M:" + method)
    else:
        addr[int(i)] = (mod if mod != "?" else "jit", "[" + (mod if mod != "?" else "jit") + "]")

parent, frame = {}, {}
for l in open(os.path.join(a.dump, "stacks.tsv")):
    i, p, ad = l.split("\t")
    parent[int(i)] = int(p); frame[int(i)] = int(ad)
memo = {}


def stack(sid):
    # Frames root first, as (module, name).
    if sid < 0:
        return ()
    r = memo.get(sid)
    if r is None:
        out, s = [], sid
        while s >= 0:
            out.append(addr[frame[s]]); s = parent[s]
        out.reverse()
        r = memo[sid] = tuple(out)
    return r


samples, waits, t0 = [], [], None
for l in open(os.path.join(a.dump, "samples.tsv")):
    f = l.rstrip("\n").split("\t")
    if f[0] == "start":
        t0 = datetime.datetime.fromisoformat(f[1][:26].rstrip("Z")).replace(tzinfo=datetime.timezone.utc)
    elif f[0] == "S":
        samples.append((float(f[1]), int(f[2]), int(f[3]), int(f[5])))
    else:
        waits.append((float(f[1]), int(f[2]), int(f[3]), float(f[4]), int(f[5])))

tl = []
for l in open(a.timeline):
    f = l.rstrip("\n").split("\t")
    if len(f) == 3:
        t = datetime.datetime.fromisoformat(f[0][:26].rstrip("Z")).replace(tzinfo=datetime.timezone.utc)
        tl.append(((t - t0).total_seconds() * 1000, f[2]))

wins = []
for i, (t, cmd) in enumerate(tl):
    nxt = lambda rx: next((x for x, c in tl[i + 1:] if re.match(rx, c)), None)
    e = None
    if a.start:
        if re.match(a.start, cmd):
            e = nxt(a.to)
    elif a.phase == "plays" and re.match(r"play \d+", cmd):
        e = nxt(r"fpsstat plays")
    elif a.phase == "combat" and cmd.startswith("cmd fight"):
        e = nxt(r"fpsstat combat start")
    elif a.phase == "end" and cmd == "endturn":
        e = nxt(r"cmd energy|effects end")
    elif a.phase == "idle" and cmd == "wait 8":
        e = nxt(r"fpsstat idle")
    if e is not None:
        wins.append((t, e))
print("windows (s):", ", ".join("%.1f-%.1f" % (x / 1000, y / 1000) for x, y in wins))
inside = lambda t: any(x <= t < y for x, y in wins)

# The host renders: the thread with most samples in the viewport drawing is its main thread.
draws = collections.Counter()
for t, pid, tid, s in samples:
    if any(n == "RendererViewport::draw_viewports" for _, n in stack(s)):
        draws[(pid, tid)] += 1
if not draws:
    raise SystemExit("no rendering thread found")
(hostpid, main), _ = draws.most_common(1)[0]
print("host pid", hostpid, "main thread", main)
tid = a.thread or main

cpu = [(t, s) for t, pid, th, s in samples if th == tid and inside(t)]
off = [(t, d, s) for t, pid, th, d, s in waits if th == tid and inside(t)]
wall = sum(y - x for x, y in wins)
waited = sum(d for _, d, _ in off)
print("wall %.0f ms, cpu samples %d, waits %.0f ms" % (wall, len(cpu), waited))

others = collections.Counter()
for t, pid, th, s in samples:
    if pid == hostpid and inside(t):
        others[th] += 1
print("host threads by cpu samples:", others.most_common(8))


def under(st):
    # The frame right below the engine's frame loop: what part of an iteration this is.
    for i, (_, n) in enumerate(st):
        if n == "Main::iteration":
            return st[i + 1][1] if i + 1 < len(st) else "Main::iteration"
    return st[-1][1] if st else "?"


def table(title, c, total, n=None):
    print("\n--", title)
    for k, v in c.most_common(n or a.top):
        print("%8.0f %5.1f%%  %s" % (v, 100 * v / max(total, 1), k[:150]))


if a.frames:
    # A frame ends when the samples leave the drawing part of an iteration: 1 ms samples find long frames, not 3 ms ones.
    frames, cur, drawing, last = [], [], False, 0
    for t, s in sorted(cpu):
        d = under(stack(s)) == "RenderingServerDefault::_draw"
        if (drawing and not d) or t - last > 200:
            frames.append(cur); cur = []
        cur.append((t, s)); drawing = d; last = t
    frames.append(cur)
    longf = [f for f in frames if f and f[-1][0] - f[0][0] >= a.frames]
    print("frames found %d, of at least %.0f ms: %d, their samples %d of %d" % (len(frames), a.frames, len(longf), sum(map(len, longf)), len(cpu)))
    print("longest:", sorted((round(f[-1][0] - f[0][0]) for f in longf), reverse=True)[:20])
    cpu = [x for f in longf for x in f]

if a.tree:
    inc = 0
    callers, callees = collections.Counter(), collections.Counter()
    for t, s in cpu:
        st = stack(s)
        idx = [i for i, (_, n) in enumerate(st) if a.tree in n]
        if idx:
            inc += 1
            i, j = idx[0], idx[-1]
            callers[st[i - 1][1] if i else "(root)"] += 1
            callees[st[j + 1][1] if j + 1 < len(st) else "(self)"] += 1
    print("\n%s: %d samples, %.1f%% of cpu" % (a.tree, inc, 100 * inc / max(len(cpu), 1)))
    table("callers", callers, inc, 15)
    table("callees", callees, inc, a.top)
    wc, wn, wl = collections.Counter(), 0, []
    for t, d, s in off:
        st = stack(s)
        idx = [i for i, (_, n) in enumerate(st) if a.tree in n]
        if idx:
            wc[st[idx[0] - 1][1] if idx[0] else "(root)"] += d
            wn += 1
            wl.append(round(d, 2))
    print("waits inside it: %d, %.1f ms, longest %s" % (wn, sum(wc.values()), sorted(wl, reverse=True)[:10]))
    table("wait callers (ms)", wc, sum(wc.values()), 8)
    raise SystemExit

top, mods, selfs, incl = collections.Counter(), collections.Counter(), collections.Counter(), collections.Counter()
for t, s in cpu:
    st = stack(s)
    top[under(st)] += 1
    if st:
        mods[st[-1][0]] += 1
        selfs[st[-1][1]] += 1
    for n in {n for _, n in st}:
        incl[n] += 1
table("parts of an iteration (cpu)", top, len(cpu), 25)
table("self by module", mods, len(cpu), 15)
table("self by function", selfs, len(cpu))
table("inclusive by function", incl, len(cpu), a.top * 2)

wt = collections.Counter()
for t, d, s in off:
    st = stack(s)
    names_ = [n for _, n in st if not n.startswith("[")]
    wt[(names_[-1] if names_ else "?")] += d
table("waits by last named frame (ms)", wt, waited, 15)
