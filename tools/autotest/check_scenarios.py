"""Every scenario line is a command the runner knows: an unknown one now stops the session, better found here.

    python -I tools/autotest/check_scenarios.py

Known commands: the labels of AutoTest.cs's dispatcher switch only (a nested switch's labels, such as a subcommand's, are
not commands), plus the top-level labels of a local LocalCommand implementation when one exists (untracked tooling). Also
checked: the kinds `waitfor` knows (WaitFor's own labels) and the numbers `wait`, `timeout`, `play` and `waitfor` parse.
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
BENCH = os.path.join(ROOT, '..', '..', 'src', 'BigDeckOptimization', 'Bench')
CASE = re.compile(r'^(\s*)case "(\w+)"(?: when [^:]+)?:')


def switch_labels(text, start):
    """The case labels of the switch that starts at `start`, at its first label's indentation (nested switches left out)."""
    labels, indent = set(), None
    for line in text[start:].splitlines()[1:]:
        if m := CASE.match(line):
            if indent is None:
                indent = m.group(1)
            if m.group(1) == indent:
                labels.add(m.group(2))
        elif indent is not None and line.strip() == '}' and len(line) - len(line.lstrip()) < len(indent):
            break  # The switch's closing brace.
    return labels


def known_commands():
    with open(os.path.join(BENCH, 'AutoTest.cs'), encoding='utf-8') as f:
        main = f.read()
    start = main.find('switch (parts[0].ToLowerInvariant())')
    if start < 0:
        raise SystemExit("AutoTest.cs: the dispatcher's 'switch (parts[0].ToLowerInvariant())' not found: update check_scenarios.py")
    known = switch_labels(main, start)
    for path in glob.glob(os.path.join(BENCH, '*.cs')):
        with open(path, encoding='utf-8') as f:
            text = f.read()
        local = re.search(r'static partial void LocalCommand\([^)]*\)\s*\{', text)
        if local and (inner := text.find('switch (', local.end())) >= 0:
            known |= switch_labels(text, inner)
    # WaitFor's kinds: its 'parts[0] == "x"' tests and the arms of its 'parts[0] switch'.
    body = main[main.find('private static Task WaitFor('):]
    body = body[:body.find('return Until(node, condition, timeout, parts[0]);')]
    kinds = set(re.findall(r'parts\[0\] == "(\w+)"', body)) | set(re.findall(r'^\s*"(\w+)" =>', body, re.M))
    if not kinds:
        raise SystemExit("AutoTest.cs: WaitFor's kinds not found: update check_scenarios.py")
    return known, kinds


def number(text, integer=False):
    try:
        int(text) if integer else float(text)
        return True
    except ValueError:
        return False


def argument_problem(command, arg, kinds):
    """What is wrong with a line's arguments the runner parses as numbers or kinds, or None."""
    args = arg.split()
    if command == 'wait' and not number(arg.strip()):
        return f'wait needs a number of seconds, not "{arg}"'
    if command == 'timeout' and not number(arg.strip()):
        return f'timeout needs a number of seconds, not "{arg}"'
    if command == 'play' and not number(arg.strip(), integer=True):
        return f'play needs a whole number, not "{arg}"'
    if command == 'waitfor':
        if not args:
            return 'waitfor needs a kind'
        kind = args[0]
        if kind not in kinds:
            return f'waitfor {kind}: unknown kind (known: {", ".join(sorted(kinds))})'
        if kind == 'round' and (len(args) < 2 or not number(args[1], integer=True) or (len(args) > 2 and not number(args[2]))):
            return 'waitfor round <n> [timeout]'
        if kind == 'room' and (len(args) < 2 or (len(args) > 2 and not number(args[2]))):
            return 'waitfor room <type> [timeout]'
        if kind not in ('round', 'room') and len(args) > 1 and not number(args[1]):
            return f'waitfor {kind} [timeout]: "{args[1]}" is not a number'
    return None


def main():
    known, kinds = known_commands()
    bad = 0
    scenarios = glob.glob(os.path.join(ROOT, 'scenarios', '*.txt'))
    for path in scenarios:
        for number_, line in enumerate(open(path, encoding='utf-8'), 1):
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            command, _, arg = line.partition(' ')
            command = command.lower()
            if command not in known:
                print(f'{os.path.basename(path)}:{number_}: unknown command {command}')
                bad += 1
            elif problem := argument_problem(command, arg, kinds):
                print(f'{os.path.basename(path)}:{number_}: {problem}')
                bad += 1
    print(f'{len(scenarios)} scenarios, {len(known)} commands, {len(kinds)} waitfor kinds, {bad} problem(s)')
    return 1 if bad else 0


if __name__ == '__main__':
    # Under python -I stdout is the ANSI code page (PYTHONIOENCODING ignored): a log's character it lacks must not crash the report.
    sys.stdout.reconfigure(errors='backslashreplace')
    sys.exit(main())
