"""Every scenario line is a command the runner knows: an unknown one now stops the session, better found here.

    python -I tools/autotest/check_scenarios.py

Known commands: the labels of AutoTest.cs's dispatcher switch only (a nested switch's labels, such as a subcommand's, are
not commands), the `[ScriptCommand("name")]` methods of the Bench part files, plus the top-level labels of a local
LocalCommand implementation when one exists (untracked tooling). Each `[ScriptCommand]` must be matched by its file's pattern
in parlib.ps1's `$BaselineBenchParts` (a file missing there counts in every baseline key, so adding it would invalidate every
mod-off baseline; matched case-insensitively, as the dispatcher and parlib.ps1 do), must not also be a switch label (the switch
would take it first) and must be the only one of its name, case aside (a duplicate makes AutoTest.PartCommand throw). Also checked: the kinds `waitfor` knows (WaitFor's own labels) and the numbers `wait`, `timeout`, `play` and `waitfor` parse;
the directive comments judge.py and par.ps1 read (`# effects-ran:`, `# effects-zero-ok:`, `# uncompared:`, `# compare-with:`:
a regex Python compiles, an `effects` line for effects-ran, a scenario that exists for compare-with), and every `expect` regex
both compile and name their target in a line of several parts: one a COUNTERS line (or any line of 'Part ..., mismatches n;'
parts) matches through another part's field than its own passes when its own part saw nothing. And the two sides of every
comparison the runners make (<x>-off/<x>-on, '# compare-with:', co-op <x>off/<x>-off/<x>van) import the same deck.
The files `$BaselineBenchParts` keys by their callers' lines (CALLER_KEYED: the foreign shapes, JoinMismatchBench): their types
named nowhere else in src/ (comments and plain string literals aside) than the call sites listed there, every line listed for a
call site matched by each file's pattern, and no way in that runs without a call (a module initializer, a Harmony attribute, a
partial type of another file, a base type the game or Godot instantiates by reflection).

    python -I tools/autotest/check_scenarios.py --against <log> [scenario]
        tests each `expect` regex of the scenario (default: the one the log ran) against that log's session lines: which match
        (how many lines) and which never do, with the closest line of the log for a miss (a drifted wording). Exit 1 when one
        never matches.
"""
import difflib
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
BENCH = os.path.join(ROOT, '..', '..', 'src', 'BigDeckOptimization', 'Bench')
CASE = re.compile(r'^(\s*)case "(\w+)"(?: when [^:]+)?:')
SCRIPT_COMMAND = re.compile(r'^\s*\[ScriptCommand\("(\w+)"\)\]', re.M)
PARLIB = os.path.join(ROOT, 'parlib.ps1')


def bench_parts():
    """parlib.ps1's $BaselineBenchParts: {file name: the pattern of the script lines that make a key count that file}."""
    with open(PARLIB, encoding='utf-8') as f:
        text = f.read()
    start = text.find('$BaselineBenchParts = [ordered]@{')
    if start < 0:
        return None
    parts = {}
    for line in text[start:].splitlines()[1:]:
        if line.strip() == '}':
            break
        if m := re.match(r"\s*'([^']+)'\s*=\s*'((?:[^']|'')*)'", line):
            parts[m.group(1)] = m.group(2).replace("''", "'")
    return parts


def part_command_problems():
    """Each [ScriptCommand] of a Bench file: matched by its file's $BaselineBenchParts pattern, and not a switch label too."""
    parts = bench_parts()
    if parts is None:
        return ["parlib.ps1: '$BaselineBenchParts = [ordered]@{' not found: update check_scenarios.py"]
    with open(os.path.join(BENCH, 'AutoTest.cs'), encoding='utf-8') as f:
        main = f.read()
    labels = switch_labels(main, main.find('switch (parts[0].ToLowerInvariant())'))
    problems = []
    # Every name once over all files, case aside (AutoTest.PartCommand's dictionary ignores case): a second one makes its Add throw
    # the first time any part command is looked up, so every unknown command of every scenario fails.
    seen = {}
    for path in sorted(glob.glob(os.path.join(BENCH, '*.cs'))):
        name = os.path.basename(path)
        with open(path, encoding='utf-8') as f:
            commands = SCRIPT_COMMAND.findall(f.read())
        for command in commands:
            if command.lower() in seen:
                problems.append(f'{name}: [ScriptCommand("{command}")] is already defined in {seen[command.lower()]} (names ignore case): '
                                'AutoTest.PartCommand would throw on every part command')
            else:
                seen[command.lower()] = name
            if command.lower() in labels:
                problems.append(f'{name}: [ScriptCommand("{command}")] is also a case of AutoTest.cs\'s switch, which takes it first')
            if name == 'AutoTest.cs':
                continue  # the engine: every key counts it anyway
            if name not in parts:
                problems.append(f'{name}: [ScriptCommand("{command}")] in a file parlib.ps1\'s $BaselineBenchParts doesn\'t list: '
                                'every mod-off baseline key counts that file; add it with a pattern of its commands')
            elif not re.search(parts[name], f'{command} 0', re.M | re.I):  # as parlib.ps1 matches them (IgnoreCase)
                problems.append(f'{name}: [ScriptCommand("{command}")] not matched by its pattern in parlib.ps1\'s '
                                f'$BaselineBenchParts: a scenario using it would reuse a baseline made without that code')
    return problems


# Bench files parlib.ps1's $BaselineBenchParts keys by the script lines that reach their callers (they have no command of their
# own), by group: the group's files, and the only places outside the group where their types may be named, each as (file, its
# enclosing 'case "x"' label or method) with script lines that reach it, which every file's pattern of the group must match. A
# reference anywhere else (an always-run file calling in, a static initializer...) would leave the mod-off baselines of scripts
# without those lines valid while that code changed: this check fails on it. 'leakcheck' reaches every Bench file
# ($BaselineBenchAll). How each call site is reached is its own file's business (AutoTest.cs's case, FaultProbe.cs's pattern...):
#   ForeignLaunch.Install runs only for a --bdforeign launch argument, which the launch scripts add for a 'launchforeign' line.
SRC = os.path.join(ROOT, '..', '..', 'src')
CALLER_KEYED = [
    {
        'files': ['ForeignPatches.cs', 'ForeignShapesGates.cs', 'ForeignShapesHookFilter.cs', 'ForeignShapesHover.cs',
                  'ForeignShapesLimitless.cs', 'ForeignShapesLimitlessTwins.cs', 'ForeignShapesParts.cs', 'ForeignShapesUnchanged.cs',
                  'IlJudgeShapes.cs'],
        'callers': {
            ('AutoTest.cs', 'case foreignpatch'): ['foreignpatch x', 'ForeignPatch x'],
            ('PerfConsoleCmd.cs', 'case foreign'): ['cmd bdperf foreign x', 'autoslayfirst bdperf foreign x', 'cmd  BdPerf  Foreign'],
            ('ForeignLaunch.cs', 'Install'): ['launchforeign x'],
            ('FaultProbe.cs', 'Arm'): ['faultprobe x'],
        },
    },
    {
        'files': ['JoinMismatchBench.cs'],
        'callers': {
            ('PerfConsoleCmd.cs', 'case joinmsg'): ['cmd bdperf joinmsg', 'autoslayfirst bdperf joinmsg backoff'],
        },
    },
]
# A base type whose subclasses the game or Godot may find and create by reflection (console commands, models, nodes).
REFLECTED_BASE = re.compile(r'(ConsoleCmd|Model|Node|Node2D|Node3D|Control|GodotObject|RefCounted|Resource)$')
METHOD = re.compile(r'^\s*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|new|extern|unsafe|partial)\s+)+'
                    r'[\w<>\[\],.?() ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\(')
CASE_LABEL = re.compile(r'^\s*case "(\w+)"')


def csharp_code(text):
    """C# source with its comments and the contents of its plain string and char literals blanked (line breaks kept, so line
    numbers hold); interpolated strings kept whole (their holes are code). None when unsure (an unclosed literal or comment)."""
    out, i, n = [], 0, len(text)

    def blank(s):
        return ''.join('\n' if c == '\n' else ' ' for c in s)

    def plain_end(j, verbatim):
        """Index past the plain string starting with its quote at j."""
        j += 1
        while j < n:
            c = text[j]
            if verbatim and c == '"':
                if j + 1 < n and text[j + 1] == '"':
                    j += 2
                    continue
                return j + 1
            if not verbatim and c == '\\':
                j += 2
                continue
            if not verbatim and c == '\n':
                return None
            if c == '"':
                return j + 1
            j += 1
        return None

    def interpolated_end(j, verbatim):
        """Index past the interpolated string whose quote is at j (holes may hold strings of their own)."""
        j += 1
        depth = 0
        while j < n:
            c = text[j]
            if depth == 0:
                if c == '"':
                    if verbatim and j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    return j + 1
                if c == '\\' and not verbatim:
                    j += 2
                    continue
                if c in '{}' and j + 1 < n and text[j + 1] == c:
                    j += 2
                    continue
                if c == '{':
                    depth = 1
                j += 1
                continue
            if c == '"':
                end = plain_end(j, False)
                if end is None:
                    return None
                j = end
                continue
            if c == '{':
                depth += 1
            elif c == '}':
                depth -= 1
            j += 1
        return None

    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if two == '//':
            end = text.find('\n', i)
            end = n if end < 0 else end
            out.append(blank(text[i:end]))
            i = end
        elif two == '/*':
            end = text.find('*/', i + 2)
            if end < 0:
                return None
            out.append(blank(text[i:end + 2]))
            i = end + 2
        elif text.startswith('"""', i):
            return None  # a raw string literal: not read here
        elif c == '$' or (c == '@' and text[i + 1:i + 2] == '$'):
            prefix = 2 if text[i:i + 2] in ('$@', '@$') else 1
            if text[i + prefix:i + prefix + 1] != '"':
                out.append(c)
                i += 1
                continue
            end = interpolated_end(i + prefix, '@' in text[i:i + prefix])
            if end is None:
                return None
            out.append(text[i:end])
            i = end
        elif c == '"' or two == '@"':
            start = i + (1 if c == '@' else 0)
            end = plain_end(start, c == '@')
            if end is None:
                return None
            out.append(text[i:start + 1] + blank(text[start + 1:end - 1]) + '"')
            i = end
        elif c == "'":
            end = i + 1
            while end < n and text[end] != "'":
                end += 2 if text[end] == '\\' else 1
            if end >= n:
                return None
            out.append("'" + blank(text[i + 1:end]) + "'")
            i = end + 1
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def top_level_types(code):
    """The names of the types a file declares at namespace level (file-scoped or block namespaces), and those it declares
    partial or with a base type: [(name, partial, bases)]."""
    file_scoped = re.search(r'^\s*namespace\s+[\w.]+\s*;', code, re.M) is not None
    found, depth, i = [], 0, 0
    declaration = re.compile(r'\b((?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|ref|unsafe|file)\s+)*)'
                             r'(class|struct|enum|record|interface)\s+(\w+)([^{;]*)')
    for m in re.finditer(r'[{}]|\b(?:class|struct|enum|record|interface)\b', code):
        if m.group() == '{':
            depth += 1
        elif m.group() == '}':
            depth -= 1
        elif depth == (0 if file_scoped else 1):
            line_start = code.rfind('\n', 0, m.start()) + 1
            d = declaration.match(code, line_start + len(code[line_start:m.start()]) - len(code[line_start:m.start()].lstrip()))
            if d and d.start(2) == m.start():
                bases = [b.strip().split('<')[0].split('.')[-1] for b in d.group(4).split(':', 1)[1].split(',')] if ':' in d.group(4) else []
                found.append((d.group(3), 'partial' in d.group(1).split(), bases))
    return found


def enclosing(lines, raw, index):
    """The 'case "x"' label or the method a line of code is in: the nearest one above it (lines: the code, its literals
    blanked; raw: the source's lines, for the label's text)."""
    for k in range(index, -1, -1):
        if re.match(r'^\s*case\s+"', lines[k]) and (m := CASE_LABEL.match(raw[k])):
            return 'case ' + m.group(1)
        if (m := METHOD.match(lines[k])) and m.group(1) not in ('if', 'while', 'for', 'foreach', 'switch', 'using', 'lock', 'catch', 'return'):
            return m.group(1)
    return None


def caller_keyed_problems():
    """CALLER_KEYED, held: the group's types named only at the listed call sites, every listed line matched by each file's
    pattern, no way into the group that runs without a call."""
    parts = bench_parts() or {}
    sources = {}
    for path in glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True):
        if os.sep + 'obj' + os.sep in path or os.sep + 'bin' + os.sep in path:
            continue
        with open(path, encoding='utf-8-sig') as f:
            sources[path] = f.read()
    by_name = {}
    for path in sources:
        by_name.setdefault(os.path.basename(path), []).append(path)
    problems = []
    declared_elsewhere = {}
    raws = dict(sources)
    for path, text in raws.items():
        code = csharp_code(text)
        if code is None:
            problems.append(f'{os.path.basename(path)}: a raw string literal or an unclosed literal or comment: check_scenarios.py '
                            'cannot read it for CALLER_KEYED (update csharp_code)')
            continue  # Its text as it is: comments and strings count as references (more found, never fewer).
        sources[path] = code
        for name, _, _ in top_level_types(code):
            declared_elsewhere.setdefault(name, set()).add(os.path.basename(path))
    for group in CALLER_KEYED:
        files = set(group['files'])
        names = set()
        for file in sorted(files):
            if file not in by_name:
                problems.append(f'CALLER_KEYED: {file} not found in src/')
                continue
            pattern = parts.get(file)
            if pattern is None:
                problems.append(f'{file}: in CALLER_KEYED but not in parlib.ps1\'s $BaselineBenchParts')
            code = sources[by_name[file][0]]
            if re.search(r'\b(ModuleInitializer|HarmonyPatch|HarmonyPatchAll|HarmonyPrefix|HarmonyPostfix|HarmonyTranspiler|HarmonyFinalizer)(Attribute)?\b', code):
                problems.append(f'{file}: a [ModuleInitializer] or [HarmonyPatch] attribute runs its code without a call: it can\'t be '
                                'keyed by its callers (take it out of CALLER_KEYED and $BaselineBenchParts)')
            for name, partial, bases in top_level_types(code):
                if partial and declared_elsewhere.get(name, set()) - files:
                    problems.append(f'{file}: partial {name}, a type of {", ".join(sorted(declared_elsewhere[name]))}: its code runs '
                                    'with that file\'s; it can\'t be keyed by its callers')
                for base in bases:
                    if REFLECTED_BASE.search(base):
                        problems.append(f'{file}: {name} derives from {base}, which the game or Godot may create by reflection: it '
                                        'can\'t be keyed by its callers')
                names.add(name)
            for (caller, site), samples in group['callers'].items():
                for sample in samples:
                    if pattern is not None and not re.search(pattern, sample, re.M | re.I):
                        problems.append(f'{file}: its $BaselineBenchParts pattern misses "{sample}", a line reaching {caller} '
                                        f'({site}), which calls into it')
        if not names:
            continue
        word = re.compile(r'\b(' + '|'.join(sorted(names)) + r')\b')
        seen = set()
        for path, code in sources.items():
            base = os.path.basename(path)
            if base in files:
                continue
            lines, raw = code.split('\n'), raws[path].split('\n')
            for number_, line in enumerate(lines):
                if not (m := word.search(line)):
                    continue
                site = enclosing(lines, raw, number_)
                if (base, site) in group['callers']:
                    seen.add((base, site))
                    continue
                problems.append(f'{base}:{number_ + 1}: names {m.group(1)} ({", ".join(sorted(files)) if len(files) < 3 else "CALLER_KEYED group of " + group["files"][0]}) '
                                f'in {site or "no case or method"}, not a call site CALLER_KEYED lists: add it there with the script lines '
                                'that reach it (and to the files\' patterns in parlib.ps1), or the mod-off baselines of scripts without '
                                'them stay valid when that code changes')
        for caller, site in group['callers']:
            if (caller, site) not in seen:
                problems.append(f'CALLER_KEYED: {caller} ({site}) no longer names a type of {group["files"][0]}\'s group: remove it')
    return problems


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
        known |= {name.lower() for name in SCRIPT_COMMAND.findall(text)}
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


# judge.py's and par.ps1's directive comments ('# name: value'); a comment that looks like one with another name is a typo.
DIRECTIVE = re.compile(r'^#\s*(effects-ran|effects-zero-ok|uncompared|compare-with|tolerated-error):\s*(.*?)\s*$')
LOOKS_LIKE_DIRECTIVE = re.compile(r'^#\s*((effects?[-_ ]\w+|uncompared\w*|compare[-_ ]?with|tolerated[-_ ]errors?)\s*:|(effects?-\w+|uncompared|compare-with|tolerated-error)\b)', re.I)


def directive_problem(kind, value, names, commands):
    """What is wrong with a directive line, or None."""
    if not value:
        return f'# {kind}: needs a value'
    if kind == 'compare-with':
        return None if value in names else f'# compare-with: no scenario {value}'
    text, version = value, None
    if kind in ('effects-zero-ok', 'uncompared') and (m := re.match(r'^(.*?)\s+on\s+(\S+)$', value)):
        text, version = m.group(1), m.group(2)
    for rx in (text, version):
        if rx is not None and not (kind == 'effects-ran' and rx == 'all'):
            try:
                re.compile(rx)
            except re.error as e:
                return f'# {kind}: "{rx}" is not a regex Python reads ({e})'
    if kind == 'effects-ran' and 'effects' not in commands:
        return '# effects-ran: the script has no "effects" line (judge.py would find no EFFECT line)'
    return None


def counters_template():
    """The COUNTERS line AutoTest's 'counters' command logs, every value a placeholder '\\0' (from its interpolated strings)."""
    with open(os.path.join(BENCH, 'AutoTest.cs'), encoding='utf-8') as f:
        main = f.read()
    start = main.find('case "counters":')
    end = main.find('break;', start)
    if start < 0 or end < 0:
        return None
    body, text, i = main[start:end], [], 0
    while (i := body.find('$"', i)) >= 0:
        i += 2
        depth, chunk, expr = 0, [], []
        while i < len(body):
            c = body[i]
            if depth == 0 and c == '"':
                break
            if c == '{':
                depth += 1
                if depth == 1:
                    expr = []
                    i += 1
                    continue
            elif c == '}':
                depth -= 1
                if depth == 0:
                    # A part's name ({X.Name}) is its type's name, the label ({arg...}) '\1', any other value '\0'.
                    e = ''.join(expr).strip()
                    chunk.append(m.group(1) if (m := re.match(r'^(\w+)\.Name$', e)) else '\1' if e.startswith('arg') else '\0')
                    i += 1
                    continue
            if depth == 0:
                chunk.append(c)
            else:
                expr.append(c)
            i += 1
        text.append(''.join(chunk))
        i += 1
    line = ''.join(text).strip()
    return line if line.startswith('COUNTERS \1') else None


def loose_expect(rx, template, labels):
    """Why an expect regex matches a line of several parts generically ('Part a n, b n; Part2 ...'), or None: it matches the line
    with every value at zero, or the line with one value set through two parts or more."""
    if not template:
        return None
    head, _, body = template.partition(': ')
    parts = body.split('; ')
    for label in labels:
        start = head.replace('\1', label) + ': '
        if rx.search(start + '; '.join(parts).replace('\0', '0')):
            return f'matches COUNTERS {label} with every value at zero'
        through = set()
        for pi, part in enumerate(parts):
            for slot in [k for k, c in enumerate(part) if c == '\0']:
                one = part[:slot] + '7' + part[slot + 1:]
                line = start + '; '.join(parts[:pi] + [one] + parts[pi + 1:]).replace('\0', '0')
                if rx.search(line):
                    through.add(part.split(' ')[0])
        if len(through) > 1:
            return f'matches COUNTERS {label} through {len(through)} parts ({", ".join(sorted(through))}): name its own part'
    return None


def deck_lines(name):
    """The lines making a scenario's deck (imports, party5), in order."""
    with open(os.path.join(ROOT, 'scenarios', name + '.txt'), encoding='utf-8') as f:
        return [line.strip() for line in f if re.search(r'bigdeck import|\bparty5\b', line) and not line.strip().startswith('#')]


def compared_pairs(names):
    """(reference, tested) of the comparisons the runners make: par.ps1's <x>-off/<x>-on and '# compare-with:' lines, parcoop.ps1's
    <x>off, <x>-off and <x>van co-op twins, peer by peer."""
    pairs = []
    for name in sorted(names):
        if name.endswith('-off') and name[:-4] + '-on' in names:
            pairs.append((name, name[:-4] + '-on'))
        with open(os.path.join(ROOT, 'scenarios', name + '.txt'), encoding='utf-8') as f:
            for line in f:
                if (m := DIRECTIVE.match(line.strip())) and m.group(1) == 'compare-with' and m.group(2) in names:
                    pairs.append((m.group(2), name))
        for role in ('-host', '-client'):
            if name.endswith(role):
                base = name[:-len(role)]
                pairs += [(base + suffix + role, name) for suffix in ('off', '-off', 'van') if base + suffix + role in names]
    return pairs


def literal_of(pattern):
    """The readable text of a regex: escapes undone, wildcards and classes turned into a space."""
    text = re.sub(r'\\([.\[\](){}*+?|^$\\/-])', r'\1', pattern)
    text = re.sub(r'\[[^\]]*\][*+?]?|\.[*+?]|\\[dswDSW][*+?]?|[()|^$?*+{}]', ' ', text)
    return re.sub(r'\s+', ' ', text).strip()


def closest_line(rx, lines, floor=0.6, cap=400):
    """(score 0..1, line) of the line of `lines` closest to the text of regex `rx` (the share of its literal text found in the
    line, in order), or None under `floor`. Candidates: the lines holding the first characters of the regex's longest literal
    chunk (the last `cap` of them)."""
    literal = literal_of(rx.pattern)
    chunks = sorted((c for c in literal.split(' ') if len(c) >= 4), key=len, reverse=True)
    if not chunks:
        return None
    key = chunks[0][:8].lower()
    best = None
    for line in [x for x in lines if key in x.lower()][-cap:]:
        blocks = difflib.SequenceMatcher(None, literal, line, autojunk=False).get_matching_blocks()
        score = sum(b.size for b in blocks) / len(literal)
        if score >= floor and (best is None or score > best[0]):
            best = (score, line)
    return best


def against(log_path, scenario):
    """Test a scenario's expect regexes against a real log (see the module's doc). Returns the exit code."""
    import judge
    with open(log_path, encoding='utf-8', errors='replace') as f:
        text = f.read().splitlines()
    if scenario is None:
        ran = judge.scenario_of(text)
        if not ran:
            print(f'{log_path}: no "script <path>" line: name the scenario')
            return 1
        scenario = re.split(r'[\\/]', ran)[-1].removesuffix('.txt')
    path = os.path.join(ROOT, 'scenarios', scenario + '.txt')
    if not os.path.exists(path):
        print(f'no scenario {scenario}')
        return 1
    session = [x for x in text if not judge.ECHO.search(x) and not judge.EXPECT.search(x) and not judge.EXPECTDECL.search(x)]
    with open(path, encoding='utf-8-sig') as f:
        expects = [line.strip().partition(' ')[2].strip() for line in f if line.strip().lower().startswith('expect ')]
    missing = 0
    for pattern in expects:
        try:
            rx = re.compile(pattern)
        except re.error as e:
            print(f'BAD   {pattern[:150]}: not a regex Python reads ({e})')
            missing += 1
            continue
        hits = sum(1 for x in session if rx.search(x))
        if hits:
            print(f'MATCH {hits}x  {pattern[:150]}')
            continue
        missing += 1
        near = closest_line(rx, session)
        print(f'NEVER      {pattern[:150]}')
        print(f'  closest ({near[0]:.0%}): {near[1].strip()[:200]}' if near else '  no close line in the log')
    print(f'{scenario}: {len(expects)} expect line(s) against {os.path.basename(log_path)}, {missing} never matching')
    return 1 if missing else 0


def main():
    known, kinds = known_commands()
    template = counters_template()
    bad = 0
    scenarios = glob.glob(os.path.join(ROOT, 'scenarios', '*.txt'))
    names = {os.path.basename(p)[:-4] for p in scenarios}
    if template is None:
        print("AutoTest.cs: the 'counters' command's COUNTERS line not found: loose expects not checked (update check_scenarios.py)")
        bad += 1
    for problem in part_command_problems() + caller_keyed_problems():
        print(problem)
        bad += 1
    for path in scenarios:
        lines = [line.strip() for line in open(path, encoding='utf-8')]
        commands = {line.partition(' ')[0].lower() for line in lines if line and not line.startswith('#')}
        labels = {line.partition(' ')[2].strip() for line in lines if line.lower().startswith('counters ')} or {'end'}
        for number_, line in enumerate(lines, 1):
            if not line:
                continue
            if line.startswith('#'):
                if m := DIRECTIVE.match(line):
                    problem = directive_problem(m.group(1), m.group(2), names, commands)
                elif LOOKS_LIKE_DIRECTIVE.match(line):
                    problem = f'"{line[:40]}": not a directive judge.py or par.ps1 knows (effects-ran, effects-zero-ok, uncompared, compare-with)'
                else:
                    problem = None
                if problem:
                    print(f'{os.path.basename(path)}:{number_}: {problem}')
                    bad += 1
                continue
            command, _, arg = line.partition(' ')
            command = command.lower()
            if command not in known:
                print(f'{os.path.basename(path)}:{number_}: unknown command {command}')
                bad += 1
            elif problem := argument_problem(command, arg, kinds):
                print(f'{os.path.basename(path)}:{number_}: {problem}')
                bad += 1
            elif command == 'expect':
                try:
                    rx = re.compile(arg.strip())
                except re.error as e:
                    print(f'{os.path.basename(path)}:{number_}: expect "{arg.strip()}" is not a regex Python reads ({e})')
                    bad += 1
                    continue
                if why := loose_expect(rx, template, labels):
                    print(f'{os.path.basename(path)}:{number_}: expect "{arg.strip()}" {why}')
                    bad += 1
    for reference, tested in compared_pairs(names):
        if (a := deck_lines(reference)) != (b := deck_lines(tested)):
            print(f'{tested}: compared with {reference}, but its deck differs ({len(b)} import line(s) against {len(a)}): '
                  'a comparison of two decks finds differences that aren\'t the mod\'s')
            bad += 1
    print(f'{len(scenarios)} scenarios, {len(known)} commands, {len(kinds)} waitfor kinds, {bad} problem(s)')
    return 1 if bad else 0


if __name__ == '__main__':
    # Under python -I stdout is the ANSI code page (PYTHONIOENCODING ignored): a log's character it lacks must not crash the report.
    sys.stdout.reconfigure(errors='backslashreplace')
    if '--against' in sys.argv:
        rest = sys.argv[sys.argv.index('--against') + 1:]
        sys.path.insert(0, ROOT)
        sys.exit(against(rest[0], rest[1] if len(rest) > 1 else None) if rest else 1)
    sys.exit(main())
