"""First look at a batch's logs: which ones have problems, what came before the first failure, and whether it is a known false alarm.

    python -I tools/autotest/triage.py <results dir or logs...> [--since HH:MM] [--pairs] [--catalogue] [--lines N]

For every log judge.py finds problems in: the problems (deduplicated), the lines before the first failure (stack frames, debug
lines and repeats left out), and a label when every problem is a known false alarm (CATALOGUE below; `--catalogue` prints how to
confirm each). Output is at most --lines lines (default 40), real problems first (severity), labelled ones after.
--since HH:MM: only logs modified today at or after that time. --pairs: compare.py's verdict of each on/off pair present
(<x>-off/<x>-on, <x>-autooff-<peer>/<x>-<peer>), non-IDENTICAL ones listed. Exit code 1 when a problem has no label.
A label says "looks like": it is a lead to confirm with the catalogue's recipe, not a verdict.
A results directory is read flat: results/<version>/ holds the sessions, the mutation logs and run.ps1's logs on a copy; the progress files (status-par-<v>.txt ...) are not logs and are left out.
"""
import contextlib
import datetime
import difflib
import io
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import judge  # noqa: E402
import compare  # noqa: E402
from check_scenarios import closest_line  # noqa: E402

CATALOGUE = {
    'pace-checkpoint': 'A checksum or state taken right after a fight/obtain/play while effects still run: the two sides differ by pace. '
                       'Confirm: the scenario waits (`waitfor idle`) before that line? rerun the pair with par.ps1 -Instant or the '
                       '*-instant variant; identical there = pace.',
    'limitless-rng': 'Limitless draws per-player RNG in an order that depends on timing. Confirm: the mismatch is an RNG/shuffle '
                     'value, the log lists Limitless, and the same scenario without Limitless (or mod off on both sides) differs too.',
    'modoff-slow': 'A mod-off side at 8,400+ cards: the game itself takes minutes per action and the scenario timeout fires. Confirm: '
                   'the log is a mod-off side, the deck is huge, the failing line is a wait/timeout, no mod line near it.',
    'expect-drift': 'A positive control\'s expected text no longer matches: the game or the mod changed the wording. Confirm: the '
                    'near-identical line shown is the same event; fix the expect regex (check_scenarios.py --against <log>).',
    'vanilla-baseline': 'A --bdvanilla session (BDVANILLA line): no patch installed, so a control of the mod\'s own patches never fires. '
                        'Confirm: the log has the BDVANILLA line; compare.py on the pair ignores it. Fix: the scenario should not be a baseline.',
    'modoff-control': 'A control of the mod\'s own work (LEAKCHECK current, an effect counter) on a mod-off side: nothing of the mod runs '
                      'there, so it finds nothing. Confirm: the log is the mod-off twin of a pair whose mod-on side passes it.',
    'game-both-sides':'The same failure on the mod-on and mod-off side (numbers aside): the game\'s own. Confirm: compare.py --prefix on '
                       'the pair says "on both sides"; the failing stack has no BigDeckOptimization frame.',
}
# Problems that follow from an earlier failure: they take its label.
CONSEQUENCE = re.compile(r'^(did not reach its end|ABORT:|FAILED line)')
MISMATCH = re.compile(r'MISMATCH|differs? from|mismatches [1-9]|changed unseen|found wrong|CHECKSUM|state divergence', re.I)
NEVER_SEEN = re.compile(r'^expected "(.*)" never seen')
BIG_DECK = re.compile(r'8k|8400|8,400|10k|16k|21k|32k|5k|big')
NOISE = re.compile(r'^\s+at |^\s*$|^\[(DEBUG|VERBOSE)\]|^(ERROR|WARNING): |PerfProbe|Asset not cached|VSync|Invalid Task ID|\bRID\b|leaked')
PREFIX = re.compile(r'^\[(INFO|WARN|ERROR)\] (\[BigDeckOptimization\] )?')
ECHO_CMD = re.compile(r'AutoTest\s+[\d.]+s\s+> (fight|obtain|play|endturn|relic|card|potion)\b')
WAITED = re.compile(r'AutoTest\s+[\d.]+s\s+> (wait|waitfor)\b|waitfor idle')


def read_lines(path):
    with open(path, encoding='utf-8', errors='replace') as f:
        return f.read().splitlines()


def is_mod_off_name(name):
    """A mod-off side by its name (par.ps1 <x>-off, parcoop.ps1 -autooff twins, <x>off, <x>van)."""
    return bool(re.search(r'(^|[-_])(autooff|off|van|vanilla)([-_]|$)|(coop\w*?)off|van-', name[:-4] if name.endswith('.log') else name))


def partner_name(name):
    """The log of the other side of a comparison (reference, tested) pair this log belongs to: (A, B) as compare.py takes them."""
    stem = name[:-4]
    if '-autooff-' in stem:
        return stem.replace('-autooff-', '-') + '.log', name, 'B'
    if stem.endswith('-off'):
        return name, stem[:-4] + '-on.log', 'A'
    if stem.endswith('-on'):
        return stem[:-3] + '-off.log', name, 'B'
    if re.search(r'-(host|client)\d*$', stem):
        twin = re.sub(r'-(host|client\d*)$', r'-autooff-\1', stem)
        return name, twin + '.log', 'A'
    return None


def context_before(lines, number, count=12):
    """The `count` lines before line `number` (1-based; None: the log's end), without noise, repeats or the mod's prefix."""
    end = len(lines) if number is None else number - 1
    out = []
    for line in reversed(lines[:end]):
        if NOISE.search(line):
            continue
        text = PREFIX.sub('', line).strip()
        if out and out[-1] == text:
            continue
        out.append(text)
        if len(out) >= count:
            break
    return list(reversed(out))


def normalize(text):
    return re.sub(r'\d+', 'N', text)


class Log:
    def __init__(self, path):
        self.path, self.name = path, os.path.basename(path)
        self.lines = read_lines(path)
        self.vanilla = any(judge.VANILLA.search(x) for x in self.lines)
        self.mod_off = is_mod_off_name(self.name)
        self.problems, _ = judge.judge(path, mod_off=self.mod_off)
        if self.mod_off:  # as judge.py's main(): the mod found off at the start is no problem on a mod-off side
            self.problems = [p for p in self.problems if not judge.MOD_OFF_START.search(p)]
        self.problems = [p.split(': ', 1)[-1] if p.startswith(self.name + ': ') else p for p in self.problems]
        self.numbers = {}
        for number, text in judge.problem_lines(path):
            self.numbers.setdefault(text[:200], number)


def partner_problem_keys(log, cache):
    found = partner_name(log.name)
    if not found:
        return set()
    a, b, _ = found
    other = b if a == log.name else a
    other_path = os.path.join(os.path.dirname(log.path), other)
    if not os.path.exists(other_path):
        return set()
    if other_path not in cache:
        cache[other_path] = Log(other_path)
    return {normalize(p) for p in cache[other_path].problems}


def label_problem(log, problem, number, partners):
    """(label, detail) of one problem, or (None, None)."""
    if normalize(problem) in partners:
        return 'game-both-sides', None
    if log.mod_off and re.search(r'LEAKCHECK current|effect \S+ never ran|nothing to look for', problem):
        return 'modoff-control', None
    if m := NEVER_SEEN.match(problem):
        if log.vanilla:
            return 'vanilla-baseline', None
        session = [x for x in log.lines if not judge.ECHO.search(x) and not judge.EXPECT.search(x) and not judge.EXPECTDECL.search(x)]
        try:
            near = closest_line(re.compile(m.group(1)), session)
        except re.error:
            near = None
        if near:
            return 'expect-drift', f'near: {near[1][:140].strip()}'
        return None, None
    if log.mod_off and BIG_DECK.search(log.name) and re.search(r'TIMEOUT|timed out|did not reach its end', problem):
        return 'modoff-slow', None
    if MISMATCH.search(problem):
        before = context_before(log.lines, number, 12)
        text = ' '.join(before) + ' ' + problem
        if 'Limitless' in ' '.join(log.lines[:600]) and re.search(r'\bRng\b|\bRNG\b|random|shuffle', text):
            return 'limitless-rng', None
        cmd = max((i for i, x in enumerate(before) if ECHO_CMD.search(x)), default=-1)
        if cmd >= 0 and not any(WAITED.search(x) for x in before[cmd + 1:]):
            return 'pace-checkpoint', None
    return None, None


def triage_log(log, cache):
    """(label or None, [(problem, count)], first problem line number or None, detail)."""
    partners = partner_problem_keys(log, cache)
    counts = {}
    for p in log.problems:
        counts[p] = counts.get(p, 0) + 1
    labels, detail, first_label = [], None, None
    for p in counts:
        number = log.numbers.get(p[:200])
        label, extra = label_problem(log, p, number, partners)
        if CONSEQUENCE.match(p) and label is None:
            label = first_label  # inherits (None while nothing before it was labelled)
        elif first_label is None:
            first_label = label
        labels.append(label)
        detail = detail or extra
    label = labels[0] if labels and all(x == labels[0] for x in labels) and labels[0] else None
    problems = list(counts.items())
    first_number = next((log.numbers[p[:200]] for p, _ in problems if p[:200] in log.numbers), None)
    return label, problems, first_number, detail


def severity(problems):
    text = ' '.join(p for p, _ in problems)
    if re.search(r'divergence|\[ERROR\]|MISMATCH|differs? from|PATCHES LOST', text):
        return 0
    if re.search(r'LEAKCHECK|compared nothing|\[WARN\]', text):
        return 1
    if re.search(r'FAILED|ABORT|TIMEOUT', text):
        return 2
    return 3


def collect(paths, since):
    logs = []
    for p in paths:
        if os.path.isdir(p):
            logs += [os.path.join(p, f) for f in sorted(os.listdir(p)) if f.endswith('.log')]
        elif os.path.exists(p):
            logs.append(p)
    if since:
        limit = datetime.datetime.combine(datetime.date.today(), since).timestamp()
        logs = [p for p in logs if os.path.getmtime(p) >= limit]
    return logs


def pair_verdicts(logs):
    """[(a, b, verdict)] for each pair with both logs present."""
    names = {os.path.basename(p): p for p in logs}
    done, out = set(), []
    for name, path in names.items():
        found = partner_name(name)
        if not found or found[0] not in names or found[1] not in names or (found[0], found[1]) in done:
            continue
        done.add((found[0], found[1]))
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            try:
                compare.main(names[found[0]], names[found[1]], times=False)
            except Exception as e:  # noqa: BLE001
                print(f'compare crashed: {e}\nNOT COMPARED')
        verdict = next((x.strip() for x in reversed(buf.getvalue().splitlines()) if x.strip()
                        in ('IDENTICAL', 'SKIPPED', 'DIFFERENT', 'NOT COMPARED', 'PROBLEMS')), '?')
        out.append((found[0][:-4], found[1][:-4], verdict))
    return out


def main(argv):
    args, paths, since, limit = list(argv), [], None, 40
    show_pairs = '--pairs' in args
    if '--catalogue' in args:
        for label, how in CATALOGUE.items():
            print(f'{label}: {how}')
        return 0
    i = 0
    while i < len(args):
        a = args[i]
        if a == '--since':
            since = datetime.datetime.strptime(args[i + 1], '%H:%M').time()
            i += 1
        elif a == '--lines':
            limit = int(args[i + 1])
            i += 1
        elif not a.startswith('--'):
            paths.append(a)
        i += 1
    logs = collect(paths, since)
    cache, entries = {}, []
    for path in logs:
        log = Log(path)
        if not log.problems:
            continue
        label, problems, number, detail = triage_log(log, cache)
        entries.append((label, log, problems, number, detail))
    real = sorted((e for e in entries if not e[0]), key=lambda e: (severity(e[2]), -len(e[2]), e[1].name))
    alarms = sorted((e for e in entries if e[0]), key=lambda e: (e[0], e[1].name))
    out = [f'TRIAGE {len(logs)} logs, {len(entries)} with problems: {len(real)} to look at, {len(alarms)} known false alarms']
    tail = []
    by_label = {}
    for label, log, problems, number, detail in alarms:
        by_label.setdefault(label, []).append((log.name[:-4], detail))
    for label, items in by_label.items():
        tail.append(f'ALARM[{label}] x{len(items)}: {", ".join(n for n, _ in items)}'[:190])
        if items[0][1]:
            tail.append(f'    {items[0][1]}')
    if show_pairs:
        verdicts = pair_verdicts(logs)
        bad = [v for v in verdicts if v[2] not in ('IDENTICAL', 'SKIPPED')]
        tail.append(f'PAIRS {len(verdicts)} compared, {len(verdicts) - len(bad)} identical/skipped' + (':' if bad else ''))
        tail += [f'  {a} vs {b}: {v}' for a, b, v in bad[:5]]
        if len(bad) > 5:
            tail.append(f'  ... and {len(bad) - 5} more')
    # The first few real ones one by one with the lines before their first failure; the others grouped by their first problem.
    room = limit - len(out) - len(tail)
    single, rest = real[:3], real[3:]
    groups = {}
    for entry in rest:
        groups.setdefault(normalize(entry[2][0][0])[:70], []).append(entry[1].name[:-4])
    room -= min(len(groups), 6) + len(single)
    for label, log, problems, number, detail in single:
        extra = f' (+{len(problems) - 1} more)' if len(problems) > 1 else ''
        out.append(f'LOOK  {log.name}: {problems[0][0][:170]}{extra}')
        take = min(8, room // max(1, len(single)))
        if take >= 3:
            ctx = context_before(log.lines, number, take)
            out += ['    | ' + c[:150] for c in ctx]
            room -= len(ctx)
    for key, names in sorted(groups.items(), key=lambda kv: -len(kv[1]))[:6]:
        out.append(f'LOOK  x{len(names)} {key}: {", ".join(names)}'[:190])
    out += tail
    if len(out) > limit:
        out = out[:limit - 1] + [f'... output cut at {limit} lines']
    print('\n'.join(out))
    return 1 if real else 0


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    sys.exit(main(sys.argv[1:]))
