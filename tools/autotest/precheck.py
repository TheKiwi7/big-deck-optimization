"""What a battery should look at before it starts: scenarios new or changed on a game version, and expects that drifted.

    python -I tools/autotest/precheck.py --version v0.113.1 [--results <results dir>] name[,name...] [name ...]

For each scenario of `scenarios/` named (a co-op one is looked up as <name>-host and <name>-client):
  NEW       no log in results/<version>/ and no line in results/<version>/durations.tsv: it never ran on this version.
  CHANGED   its file is newer than its last log there (a checkout or a merge touching the file counts too): the log is stale.
  EXPECT    an `expect` line of the scenario that its last log (when the scenario did not change since) never matched:
            check_scenarios.py --against's NEVER lines, with the closest line of the log. A lead, not a failure: the code may
            have changed on purpose, a log may be a cut-short session's.
Output, one line each (battery.ps1 prints them with a PRECHECK prefix):
    NEW <n>: <name>, <name>, ...
    CHANGED <n>: <name>, <name>, ...
    EXPECT <name>: <pattern> | closest (<n>%): <line>
    SUMMARY <version>: <n> scenario(s), <a> new, <b> changed, <c> expect never matched in <d> log(s)
Always exit code 0: nothing here fails a battery. `--list-new` prints only the names of the new and changed ones, comma-joined.
"""
import contextlib
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SLACK_SEC = 2.0  # a copy of a log (a baseline's) keeps its time; a scenario saved in the same second is not newer


def scenario_files(name):
    """[(scenario name, path)] of a scenario: itself, or its co-op host and client files."""
    out = []
    for n in (name, name + '-host', name + '-client'):
        p = os.path.join(ROOT, 'scenarios', n + '.txt')
        if os.path.exists(p):
            out.append((n, p))
    return out


def duration_names(results, version):
    path = os.path.join(results, version, 'durations.tsv')
    names = set()
    if os.path.exists(path):
        with open(path, encoding='utf-8', errors='replace') as f:
            for line in f:
                key = line.split('\t')[0]
                names.add(key.split('|')[0])
    return names


def expect_misses(log, scenario):
    """[(pattern, closest text)] the scenario's expects the log never matched (check_scenarios.against, its output read)."""
    sys.path.insert(0, ROOT)
    import check_scenarios
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        try:
            check_scenarios.against(log, scenario)
        except Exception as e:  # a log judge.py cannot read: no lead rather than a crash
            return [('(could not check: %s)' % e, '')]
    misses = []
    lines = buf.getvalue().splitlines()
    for i, line in enumerate(lines):
        if line.startswith('NEVER') or line.startswith('BAD'):
            pattern = line.split(None, 1)[1].strip() if ' ' in line else ''
            near = ''
            if i + 1 < len(lines) and lines[i + 1].lstrip().startswith('closest'):
                near = lines[i + 1].strip()
            misses.append((pattern, near))
    return misses


def check(results, version, names):
    new, changed, expects, logs = [], [], [], 0
    known = duration_names(results, version)
    seen = set()
    for name in names:
        for n, path in scenario_files(name):
            if n in seen:
                continue
            seen.add(n)
            log = os.path.join(results, version, n + '.log')
            if not os.path.exists(log):
                if n not in known:
                    new.append(n)
                continue
            if os.path.getmtime(path) > os.path.getmtime(log) + SLACK_SEC:
                changed.append(n)
                continue
            sys.path.insert(0, ROOT)
            import judge
            if judge.is_skipped(log):  # SKIPPED: the rest of the scenario does not apply to this game version: no drift to find
                continue
            logs += 1
            for pattern, near in expect_misses(log, n):
                expects.append((n, pattern, near))
    return len(seen), new, changed, expects, logs


def main(argv):
    version, results, names, list_only = None, os.path.join(ROOT, 'results'), [], False
    it = iter(argv)
    for a in it:
        if a == '--version':
            version = next(it)
        elif a == '--results':
            results = next(it)
        elif a == '--list-new':
            list_only = True
        else:
            names += [x for x in a.split(',') if x]
    if not version:
        print(__doc__)
        return 0
    count, new, changed, expects, logs = check(results, version, names)
    if list_only:
        print(','.join(new + changed))
        return 0
    if new:
        print(f'NEW {len(new)}: ' + ', '.join(new))
    if changed:
        print(f'CHANGED {len(changed)}: ' + ', '.join(changed))
    for n, pattern, near in expects:
        print(f'EXPECT {n}: {pattern}' + (f' | {near}' if near else ''))
    print(f'SUMMARY {version}: {count} scenario(s), {len(new)} new, {len(changed)} changed, {len(expects)} expect never matched in {logs} log(s)')
    return 0


if __name__ == '__main__':
    sys.stdout.reconfigure(errors='backslashreplace')
    sys.exit(main(sys.argv[1:]))
