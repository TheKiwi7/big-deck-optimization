"""What a parallel whole-game A/B found (parsweep.ps1), from its session logs in results/<version>/parts-<kind>/:

    python -I tools/autotest/sweepreport.py results/<version> cards [relics ...]

For each kind: chunks compared one by one, with the item and the values that differ; items that stopped a session; game
errors logged on one side only; this mod's problems; what each optimization did over all sessions and those that never
ran; the items allocating the most with the mod on, and where it saves the least. Allocations are measured per session,
so they hold under load; frame times don't, they aren't reported here. Writes report-<kind>.md and merged logs
sweep-<kind>-2k-off/on.log next to the parts. Exit code 1 when anything differs.
"""
import glob
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import compare  # noqa: E402

PART = re.compile(r'(\w+)-(off|on)-c(\d+)-p(\d+)\.log$')
STEP = re.compile(r'SWEEP (\S+) (\S+) (\S+) wall (\d+) worst (\d+) over50 (\d+) gc (\d+) mb ([\d.]+)')
FAILED = re.compile(r'SWEEP FAILED \S+ (\S+): (.{0,110})')
EFFECT = re.compile(r'EFFECT (\S+) (\d+)')
CLOCK = re.compile(r'AutoTest\s+([\d.]+)s')
ITEMS = re.compile(r'SWEEP \w+: (\d+) items')


def parts_of(folder, kind):
    """{(side, chunk): [paths in order]}"""
    found = {}
    for path in glob.glob(os.path.join(folder, f'parts-{kind}', f'{kind}-*-c*-p*.log')):
        m = PART.search(os.path.basename(path))
        if m:
            found.setdefault((m.group(2), int(m.group(3))), []).append((int(m.group(4)), path))
    return {key: [p for _, p in sorted(value)] for key, value in found.items()}


def read_parts(paths):
    """Concatenated log text of a chunk's sessions, and how long they took."""
    text, seconds = [], 0.0
    for path in paths:
        last = 0.0
        with open(path, encoding='utf-8', errors='replace') as f:
            for line in f:
                text.append(line)
                if m := CLOCK.search(line):
                    last = float(m.group(1))
        seconds += last
    return text, seconds


def report(folder, kind):
    parts = parts_of(folder, kind)
    if not parts:
        return [f'## {kind}', 'no session logs'], True
    chunks = sorted({chunk for _, chunk in parts})
    out, different, cut, missing = [f'## {kind}'], [], [], []
    checked = 0
    effects, zero_runs = {}, 0
    failed = {'off': {}, 'on': {}}
    errors = {'off': {}, 'on': {}}
    problems = []
    steps = {'off': {}, 'on': {}}
    seconds = {'off': 0.0, 'on': 0.0}
    sessions = {'off': 0, 'on': 0}
    items = None
    merged = {side: open(os.path.join(folder, f'sweep-{kind}-2k-{side}.log'), 'w', encoding='utf-8') for side in ('off', 'on')}
    for chunk in chunks:
        logs = {}
        for side in ('off', 'on'):
            paths = parts.get((side, chunk), [])
            text, spent = read_parts(paths)
            seconds[side] += spent
            sessions[side] += len(paths)
            merged[side].writelines(text)
            logs[side] = dict(lines=[], parts=len(paths))
            for line in text:
                if m := compare.CHECKED.search(line):
                    logs[side]['lines'].append(m.group(1))
                elif line.startswith('[ERROR]'):
                    key = compare.normalized_error(line.rstrip('\n'))
                    errors[side][key] = errors[side].get(key, 0) + 1
                if m := STEP.search(line):
                    steps[side][(m.group(2), m.group(3))] = float(m.group(8))
                elif m := FAILED.search(line):
                    failed[side][m.group(1)] = m.group(2)
                elif side == 'on' and (m := EFFECT.search(line)):
                    effects[m.group(1)] = effects.get(m.group(1), 0) + int(m.group(2))
                elif items is None and (m := ITEMS.search(line)):
                    items = int(m.group(1))
                if side == 'on' and compare.MOD_PROBLEM.search(line):
                    problems.append(line.strip()[:170])
        a, b = logs['off']['lines'], logs['on']['lines']
        checked += len(a)
        same = a == b
        if not a or not b:
            missing.append(f"chunk {chunk}: not compared, no result mod {'off' if not a else 'on'} (session failed)")
        elif logs['off']['parts'] != logs['on']['parts']:
            cut.append(f"chunk {chunk}: off {logs['off']['parts']} session(s), on {logs['on']['parts']}: the state differs from the cut on")
        elif not same:
            different.append(f'chunk {chunk}: {len(a)} vs {len(b)} lines')
            for i, x, y, words in compare.describe_mismatches(a, b, 3):
                different.append(f'  line {i}\n    off: {x[:150]}\n    on:  {y[:150]}' + (f'\n    differs: {words}' if words else ''))
    for handle in merged.values():
        handle.close()
    out.append(f'{items or "?"} items, {len(chunks)} chunks, {sessions["off"] + sessions["on"]} sessions, {checked} checked lines (mod off), session time off {seconds["off"] / 60:.1f} min, on {seconds["on"] / 60:.1f} min')
    verdict = 'IDENTICAL' if not different and not cut and not missing else f'{len([d for d in different if d.startswith("chunk")])} DIFFERENT chunk(s), {len(cut)} cut differently, {len(missing)} not compared'
    out.append(verdict)
    out += different + cut + missing
    both = sorted(set(failed['off']) & set(failed['on']))
    if both:
        out.append('items stopping a session on both sides: ' + ', '.join(both))
    for side, other in (('off', 'on'), ('on', 'off')):
        for item in sorted(set(failed[side]) - set(failed[other])):
            out.append(f'item stopping a session only mod {side}: {item}: {failed[side][item]}')
    for side, other in (('on', 'off'), ('off', 'on')):
        for text, count in list(compare.errors_only_in(errors[side], errors[other]).items())[:10]:
            out.append(f'game errors only mod {side}: {count}x {text}')
    for line in sorted(set(problems))[:10]:
        out.append(f'mod problem: {line}')
    if effects:
        ran = {k: v for k, v in effects.items() if v}
        never = sorted(k for k, v in effects.items() if not v)
        out.append(f'effects: {len(ran)}/{len(effects)} ran' + (f'; never: {", ".join(never)}' if never else ''))
    else:
        out.append('effects: no EFFECT line')
    common = [k for k in steps['on'] if k in steps['off']]
    heavy = sorted(common, key=lambda k: -steps['on'][k])[:8]
    if heavy:
        out.append('most allocated with the mod on (MB, mod off in parentheses):')
        out += [f'  {steps["on"][k]:8.1f} ({steps["off"][k]:8.1f})  {k[0]} {k[1]}' for k in heavy]
    return out, bool(different or cut or missing)


def overall_effects(folder, kinds):
    """What each optimization did over the given kinds together: one that never ran wasn't tested by any of them."""
    totals = {}
    for kind in kinds:
        for path in glob.glob(os.path.join(folder, f'parts-{kind}', f'{kind}-on-c*-p*.log')):
            with open(path, encoding='utf-8', errors='replace') as f:
                for line in f:
                    if m := EFFECT.search(line):
                        totals[m.group(1)] = totals.get(m.group(1), 0) + int(m.group(2))
    return totals


def main():
    folder = sys.argv[1]
    bad = False
    kinds = sys.argv[2:]
    for kind in kinds:
        lines, wrong = report(folder, kind)
        bad |= wrong
        text = '\n'.join(lines)
        print(text)
        with open(os.path.join(folder, f'report-{kind}.md'), 'w', encoding='utf-8') as f:
            f.write(text + '\n')
    if len(kinds) > 1:
        totals = overall_effects(folder, kinds)
        print(f'## all of {", ".join(kinds)}')
        print(f'effects: {sum(1 for v in totals.values() if v)}/{len(totals)} ran; never: {", ".join(sorted(k for k, v in totals.items() if not v)) or "none"}')
        for name, count in sorted(totals.items(), key=lambda kv: -kv[1]):
            print(f'  {count:>12}  {name}')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
