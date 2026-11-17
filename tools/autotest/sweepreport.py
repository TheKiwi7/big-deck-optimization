"""What a parallel whole-game A/B found (parsweep.ps1), from its session logs in results/<version>/parts-<kind>/:

    python -I tools/autotest/sweepreport.py results/<version> cards [relics ...]

For each kind: chunks compared one by one, with the item and the values that differ; items that stopped a session; game
errors logged on one side only; this mod's problems; what each optimization did over all sessions and those that never
ran; the items allocating the most with the mod on, and where it saves the least. Allocations are measured per session,
so they hold under load; frame times don't, they aren't reported here. Writes report-<kind>.md and merged logs
sweep-<kind>-2k-off/on.log next to the parts. Exit code 1 when anything differs, when an item stopped a session on one side
only, when a game error is logged mod on only or far more often (but the engine's timing noise, judge.GAME_NOISE), or when
a session has a problem (judge.py: an error or a difference this mod found itself, a failed line, a session neither ended nor
stopped on an item). An item stopping a session on both sides (the game's own error in a situation it
never meets, mod off too) is listed, not counted: the next session goes on from the next item; it is compared only up to the
step that failed, and one failing at its first step (headless: an image the dummy renderer never loaded, as FLANKING_POWER's
on v0.113.1) is listed apart as never compared. Every item of the game must
be in a chunk on both sides (the parts' scripts, against the item count): a chunk without any log, or a sweep cut short
(parsweep.ps1 -Chunks over another run), is "not compared".

The mod-off parts are vanilla reference sessions (launched with --bdvanilla, parlib.ps1): their "mod off from the start" lines
are no problem (judge.py), and a mod-on part launched vanilla is one (the side under test without the mod).
parsweep.ps1 -PatchedOff (vanillacheck.ps1) puts the mod-off scenario, every patch installed then bdopt off, on the "on" side.
"""
import glob
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import compare  # noqa: E402
import judge  # noqa: E402

PART = re.compile(r'(\w+)-(off|on)-c(\d+)-p(\d+)\.log$')
# Lazy item: an encounter's turn steps are 'X t1 play1' (the item's id and its turn).
STEP = re.compile(r'SWEEP (\S+) (.+?) (\S+) wall (\d+) worst (\d+) over50 (\d+) gc (\d+) mb ([\d.]+)')
FAILED = re.compile(r'SWEEP FAILED \S+ (\S+): (.{0,110})')
EFFECT = re.compile(r'EFFECT (\S+) (\d+)')
CLOCK = re.compile(r'AutoTest\s+([\d.]+)s')
ITEMS = re.compile(r'SWEEP \w+: (\d+) items')
# A part's script: 'sweep <kind> <first> <count> ...' (parsweep.ps1's chunks, p0 = the chunk's whole range).
PLANNED = re.compile(r'^sweep \S+ (\d+) (\d+)', re.M)
SCRIPT = re.compile(r'(\w+)-(off|on)-c(\d+)-p0\.txt$')


def planned_of(folder, kind):
    """{(side, chunk): (first, count)} from the chunks' first scripts."""
    found = {}
    for path in glob.glob(os.path.join(folder, f'parts-{kind}', f'{kind}-*-c*-p0.txt')):
        m = SCRIPT.search(os.path.basename(path))
        with open(path, encoding='utf-8', errors='replace') as f:
            planned = PLANNED.search(f.read())
        if m and planned:
            found[(m.group(2), int(m.group(3)))] = (int(planned.group(1)), int(planned.group(2)))
    return found


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
    planned = planned_of(folder, kind)
    # A chunk planned but without any log on either side is listed too ("not compared").
    chunks = sorted({chunk for _, chunk in parts} | {chunk for _, chunk in planned})
    out, different, cut, missing = [f'## {kind}'], [], [], []
    checked = 0
    effects, zero_runs = {}, 0
    failed = {'off': {}, 'on': {}}
    errors = {'off': {}, 'on': {}}
    problems = []
    session_problems = []
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
            vanilla = False
            for path in paths:
                found, _ = judge.judge(path, sweep=True, need_end=False)
                session_problems += [f'mod {side}: {p}' for p in found]
                if judge.is_vanilla(path):
                    vanilla = True
                    if side == 'on':
                        session_problems.append(f'mod on: {os.path.basename(path)}: a vanilla reference session (BDVANILLA line, --bdvanilla): the side under test ran without the mod')
                with open(path, encoding='utf-8', errors='replace') as f:
                    tail = f.read()
                if not judge.END.search(tail) and 'SWEEP ABORT next=' not in tail:
                    session_problems.append(f'mod {side}: {os.path.basename(path)}: neither ended nor stopped on an item')
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
                if compare.MOD_PROBLEM.search(line) and not (vanilla and judge.MOD_OFF_START.search(line)):
                    problems.append(f'mod {side}: ' + line.strip()[:170])
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
    # Every item of the game in a chunk, on both sides: chunks with no script nor log (a sweep cut short, -Chunks over another run)
    # would otherwise just be absent from this report.
    for side in ('off', 'on'):
        if items is None:
            missing.append(f'mod {side}: no item count (SWEEP <kind>: N items): what the chunks cover is unknown')
            break
        covered = set()
        for (planned_side, _), (first, count) in planned.items():
            if planned_side == side:
                covered.update(range(first, first + count))
        absent = sorted(set(range(items)) - covered)
        if absent:
            missing.append(f'mod {side}: {len(absent)} of {items} items in no chunk (from item {absent[0]}): the sweep did not cover the whole game')
    out.append(f'{items or "?"} items, {len(chunks)} chunks, {sessions["off"] + sessions["on"]} sessions, {checked} checked lines (mod off), session time off {seconds["off"] / 60:.1f} min, on {seconds["on"] / 60:.1f} min')
    verdict = 'IDENTICAL' if not different and not cut and not missing else f'{len([d for d in different if d.startswith("chunk")])} DIFFERENT chunk(s), {len(cut)} cut differently, {len(missing)} not compared'
    out.append(verdict)
    out += different + cut + missing
    both = sorted(set(failed['off']) & set(failed['on']))
    if both:
        # Compared up to the step that failed (the steps both sides logged before it), never after: a new session goes on from the
        # next item. One failing at its first step (headless: an asset the dummy renderer never loaded) is never compared at all.
        compared = {item: sum(1 for key in steps['off'] if key[0] == item and key in steps['on']) for item in both}
        never = [item for item in both if compared[item] == 0]
        partly = [f'{item} ({compared[item]} step(s))' for item in both if compared[item]]
        out.append(f"items stopping a session on both sides (the game's own failure, mod off too; not counted): {len(both)}, compared"
                   " only up to the failing step")
        if partly:
            out.append('  compared before their failing step: ' + ', '.join(partly))
        if never:
            out.append(f'  NEVER COMPARED (failed at their first step on both sides): {len(never)}: ' + ', '.join(never))
    one_side = []
    # The same item stopping both sides is the game's own error only when it is the same error: another one mod on (an exception
    # from this mod's code where the game also fails) counts as a difference.
    for item in both:
        if compare.normalized_error(failed['off'][item]) != compare.normalized_error(failed['on'][item]):
            one_side.append(f'item stopping both sides with different errors: {item}: off "{failed["off"][item]}", on "{failed["on"][item]}"')
    for side, other in (('off', 'on'), ('on', 'off')):
        for item in sorted(set(failed[side]) - set(failed[other])):
            one_side.append(f'item stopping a session only mod {side}: {item}: {failed[side][item]}')
    out += one_side
    for side, other in (('on', 'off'), ('off', 'on')):
        for text, count in list(compare.errors_only_in(errors[side], errors[other]).items())[:10]:
            out.append(f'game errors only mod {side}: {count}x {text}')
    # Mod on alone, or far more often, but the engine's timing noise: a problem (an exception in game code from this mod's data has
    # no frame of this mod). Mod off only stays information.
    grown = compare.errors_grown(errors['off'], errors['on'])
    for text, count in list(grown.items())[:10]:
        out.append(f'game error mod on only or far more often ({count} vs {errors["off"].get(text, 0)}): {text}')
    for line in sorted(set(problems))[:10]:
        out.append(f'mod problem: {line}')
    for line in list(dict.fromkeys(session_problems))[:20]:
        out.append(f'session problem: {line}')
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
    return out, bool(different or cut or missing or one_side or session_problems or grown)


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
    # Under python -I stdout is the ANSI code page (PYTHONIOENCODING ignored): a log's character it lacks must not crash the report.
    sys.stdout.reconfigure(errors='backslashreplace')
    sys.exit(main())
