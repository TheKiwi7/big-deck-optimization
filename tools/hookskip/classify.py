"""Finds the game hooks HookSkip may filter and writes src/BigDeckOptimization/Patches/HookSkipTable.cs.

A hook qualifies when every loop over the listeners only does things that are no-ops for a listener keeping the
AbstractModel default of the method(s) the hook calls (default checked in AbstractModel.cs): awaiting a default task,
returning true/false for "should" checks, passing a value through, adding 0 / multiplying by 1, "try" methods that
return false, PushModel/PopModel, ExecutionFinished (listeners with subscribers are then kept), a per-listener
HookPlayerChoiceContext (no game action unless the listener asks for a choice), "changed value" bookkeeping. Anything
else leaves the hook out. Each entry carries the fingerprint of the hook's code (async: its state machine).

Usage (repo root, after decompiling the game into decompiled/sts2):
  dotnet run --project tools/fingerprint -- <game>/data_sts2_windows_x86_64/sts2.dll MegaCrit.Sts2.Core.Hooks.Hook * body > hook_fps.txt
  python -I tools/hookskip/classify.py decompiled/sts2 hook_fps.txt
"""
import re
import sys

ROOT, FPS = sys.argv[1], sys.argv[2]
TABLE = 'src/BigDeckOptimization/Patches/HookSkipTable.cs'
abstract = open(ROOT + '/MegaCrit.Sts2.Core.Models/AbstractModel.cs', encoding='utf-8').read()
fps = {}
for line in open(FPS, encoding='utf-8-sig'):
    m = re.match(r'Hook\.(\w+): ([0-9A-F]{16})', line.strip())
    if m:
        fps.setdefault(m.group(1), []).append(m.group(2))


def extract_hooks():
    """Hook methods (name -> source) that walk the listeners."""
    src = open(ROOT + '/MegaCrit.Sts2.Core.Hooks/Hook.cs', encoding='utf-8').read().split('\n')
    found, i = {}, 0
    while i < len(src):
        m = re.match(r'\t(public|private|internal) static (async )?([\w<>\[\], ?]+) (\w+)\(', src[i])
        if m:
            depth, body, j, started = 0, [], i, False
            while j < len(src):
                body.append(src[j])
                depth += src[j].count('{') - src[j].count('}')
                started = started or '{' in src[j]
                if started and depth == 0:
                    break
                j += 1
            text = '\n'.join(body)
            if ('IterateHookListeners' in text or 'IterateCombatHookListeners(' in text) and m.group(4) != 'IterateCombatHookListeners':
                found[m.group(4)] = text
            i = j + 1
            continue
        i += 1
    return found


hooks = extract_hooks()


def block(text, start):
    """Text inside the braces opening at or after start, and the index after the closing brace."""
    i = text.index('{', start)
    depth = 0
    for j in range(i, len(text)):
        if text[j] == '{':
            depth += 1
        elif text[j] == '}':
            depth -= 1
            if depth == 0:
                return text[i + 1:j], j + 1
    raise ValueError('unbalanced')


def split_args(s):
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '([<':
            depth += 1
        elif ch in ')]>':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


# AbstractModel virtual defaults: name -> (params, kind)
defaults = {}
for m in re.finditer(r'public virtual ([\w<>?\[\], ]+?) (\w+)\(([^)]*)\)\s*\{', abstract):
    name = m.group(2)
    body, _ = block(abstract, m.end() - 1)
    params = [p.split()[-1] for p in split_args(m.group(3))] if m.group(3).strip() else []
    b = ' '.join(body.split())
    if b == 'return Task.CompletedTask;':
        kind = ('task',)
    elif b == 'return true;':
        kind = ('true',)
    elif b == 'return false;':
        kind = ('false',)
    elif b == '':
        kind = ('void',)
    elif b in ('return 0m;', 'return 1m;', 'return decimal.MaxValue;', 'return 0;', 'return 1;'):
        kind = ('const', b[len('return '):-1])
    elif re.fullmatch(r'(\w+) = (\w+); return false;', b) and re.fullmatch(r'(\w+) = (\w+); return false;', b).group(2) in params:
        mm = re.fullmatch(r'(\w+) = (\w+); return false;', b)
        kind = ('tryout', params.index(mm.group(2)), mm.group(1))
    elif re.fullmatch(r'((\w+) = (null|false|0m|0); )+return false;', b) and all(
            a in params for a in re.findall(r'(\w+) = (?:null|false|0m|0);', b)):
        kind = ('falsy',)
    elif re.fullmatch(r'return (\w+);', b) and re.fullmatch(r'return (\w+);', b).group(1) in params:
        kind = ('identity', params.index(re.fullmatch(r'return (\w+);', b).group(1)))
    else:
        kind = ('other', b[:80])
    if name in defaults:
        defaults[name] = (params, ('overloaded',))
    else:
        defaults[name] = (params, kind)


def call(stmt, var):
    """Parses 'VAR.M(args)' -> (M, args) or None."""
    m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)', stmt.strip())
    return (m.group(1), split_args(m.group(2))) if m else None


def statements(body):
    """Top-level statements of a block (if-blocks kept whole)."""
    out, i, s = [], 0, body.strip()
    while i < len(s):
        s2 = s[i:].lstrip()
        i = len(s) - len(s2)
        if not s2:
            break
        if s2.startswith('if ('):
            depth, j = 0, 0
            for j, ch in enumerate(s2):
                if ch == '(':
                    depth += 1
                elif ch == ')':
                    depth -= 1
                    if depth == 0:
                        break
            cond = s2[4:j]
            inner, end = block(s2, j)
            out.append(('if', cond.strip(), inner))
            i += end
        else:
            j = s2.index(';')
            out.append(('stmt', s2[:j].strip()))
            i += j + 1
    return out


class Unsafe(Exception):
    pass


NOOP_KINDS = ('task', 'true', 'false', 'void', 'const', 'falsy')
FALSE_KINDS = ('false', 'tryout', 'falsy')


def kind_of(meth):
    return defaults.get(meth, (None, ('missing',)))[1]


def check_loop(var, body, used, neutral=None):
    """Raises Unsafe unless every statement is a no-op for a listener keeping the defaults of the methods in `used`."""
    neutral = dict(neutral or {})  # local var -> default constant it holds for a default listener
    sts = statements(body)
    flat = [' '.join(x[1].split()) if x[0] == 'stmt' else None for x in sts]
    # Value chain: [T prev = num;] num = VAR.M(...num...); [if (cmp(prev, num)) { list.Add(VAR); }]
    k = 0
    while k < len(sts):
        st = sts[k]
        if st[0] == 'stmt':
            s = flat[k]
            if re.fullmatch(rf'\w+\.(PushModel|PopModel)\({var}\)', s) or s == f'{var}.InvokeExecutionFinished()':
                if s.endswith('InvokeExecutionFinished()'):
                    used['subscribed'] = True
                k += 1
                continue
            # A choice context per listener: for a default listener the task is already complete, so it only
            # signals ExecutionFinished (kept listeners: those with subscribers) - no game action unless a choice.
            m = re.fullmatch(rf'HookPlayerChoiceContext (\w+) = new HookPlayerChoiceContext\({var}, netId\.Value, [\w.]+, GameActionType\.Combat\)', s)
            if m:
                neutral['ctx:' + m.group(1)] = True
                used['subscribed'] = True
                k += 1
                continue
            m = re.fullmatch(rf'Task (\w+) = {var}\.(\w+)\((\w+)(, .*)?\)', s)
            if m and neutral.get('ctx:' + m.group(3)) and kind_of(m.group(2))[0] == 'task':
                neutral['task:' + m.group(1)] = True
                used['methods'].add(m.group(2))
                k += 1
                continue
            m = re.fullmatch(r'await (\w+)\.AssignTaskAndWaitForPauseOrCompletion\((\w+)\)', s)
            if m and neutral.get('ctx:' + m.group(1)) and neutral.get('task:' + m.group(2)):
                k += 1
                continue
            m = re.fullmatch(r'\w+\.Add\((\w+)\.WaitForCompletion\(\)\)', s)
            if m and neutral.get('ctx:' + m.group(1)):
                k += 1
                continue
            # bool f = VAR.M(...) with M's default returning false
            m = re.fullmatch(rf'bool (\w+) = {var}\.(\w+)\((.*)\)', s)
            if m and kind_of(m.group(2))[0] in FALSE_KINDS and 'out ' not in m.group(3):
                neutral[m.group(1)] = 'false'
                used['methods'].add(m.group(2))
                k += 1
                continue
            m = re.fullmatch(r'(\w+) = \1 \|\| (\w+)', s)
            if m and neutral.get(m.group(2)) == 'false':
                k += 1
                continue
            m = re.fullmatch(rf'(\w+) = \1 \|\| {var}\.(\w+)\((.*)\)', s)
            if m and kind_of(m.group(2))[0] in FALSE_KINDS and 'out ' not in m.group(3):
                used['methods'].add(m.group(2))
                k += 1
                continue
            # T d = VAR.M(...) where M's default returns a constant (0m, 1m, MaxValue)
            m = re.fullmatch(rf'(?:decimal|int) (\w+) = {var}\.(\w+)\((.*)\)', s)
            if m and defaults.get(m.group(2), (None, ('missing',)))[1][0] == 'const':
                neutral[m.group(1)] = defaults[m.group(2)][1][1]
                used['methods'].add(m.group(2))
                k += 1
                continue
            m = re.fullmatch(r'(\w+) (\+|\*)= (\w+)', s)
            if m and m.group(3) in neutral and ((m.group(2) == '+' and neutral[m.group(3)] in ('0m', '0')) or (m.group(2) == '*' and neutral[m.group(3)] in ('1m', '1'))):
                k += 1
                continue
            # VAR.TryX(..., IN, ..., out ACC) whose default copies IN to the out parameter: identity when IN is ACC
            m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)', s)
            if m:
                kind = defaults.get(m.group(1), (None, ('missing',)))[1]
                args = split_args(m.group(2))
                if kind[0] == 'tryout' and kind[1] < len(args) and args[-1] == 'out ' + args[kind[1]]:
                    used['methods'].add(m.group(1))
                    k += 1
                    continue
                if kind[0] in NOOP_KINDS:
                    used['methods'].add(m.group(1))  # result ignored, default has no side effect
                    k += 1
                    continue
            m = re.fullmatch(rf'await {var}\.(\w+)\((.*)\)', s)
            if m:
                need(m.group(1), ('task',), used)
                k += 1
                continue
            c = call(s, var)
            if c:
                need(c[0], ('void',), used)
                k += 1
                continue
            m = re.fullmatch(rf'(?:\w+ )?(\w+) = {var}\.(\w+)\((.*)\)', s)
            if m:
                acc, meth, args = m.group(1), m.group(2), split_args(m.group(3))
                params, kind = defaults.get(meth, (None, ('missing',)))
                if kind[0] != 'identity' or kind[1] >= len(args) or args[kind[1]] != acc:
                    raise Unsafe(f'{meth}: not an identity on {acc} ({kind})')
                used['methods'].add(meth)
                k += 1
                continue
            m = re.fullmatch(r'(?:decimal|int|bool|float) (\w+) = (\w+)', s)
            if m and not re.search(rf'\b{var}\b', s):
                k += 1  # previous value of the accumulator
                continue
            raise Unsafe('statement: ' + s)
        else:
            _, cond, inner = st
            c = ' '.join(cond.split())
            inner_s = ' '.join(inner.split())
            m = re.fullmatch(rf'!{var}\.(\w+)\((.*)\)', c)
            if m and re.fullmatch(rf'(\w+ = {var}; )?return false;', inner_s):
                need(m.group(1), ('true',), used)
                k += 1
                continue
            m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)', c)
            if m and inner_s == 'return true;':
                need(m.group(1), ('false',), used)
                k += 1
                continue
            if re.fullmatch(rf'\w+\.Contains\({var}\)', c):
                check_loop(var, inner, used)  # only members get the call: skipping a member keeping defaults is a no-op
                k += 1
                continue
            m = re.fullmatch(r'(?:decimal\.Truncate\(|\(int\))?(\w+)\)? != (?:decimal\.Truncate\(|\(int\))?(\w+)\)?', c)
            if m and re.fullmatch(rf'\w+\.Add\({var}\);', inner_s):
                k += 1  # value changed -> listed: a default keeps it unchanged
                continue
            m = re.fullmatch(r'(\w+) != (\w+)', c)
            if m and m.group(1) in neutral and m.group(2) == neutral[m.group(1)] and re.fullmatch(rf'\w+\.Add\({var}\);', inner_s):
                k += 1  # listed only when the returned value isn't the default constant
                continue
            # cap: if (d < lowest) { lowest = d; if (acc > d) { acc = d; list.Add(VAR); } } with d = MaxValue for defaults
            m = re.fullmatch(r'(\w+) < (\w+)', c)
            if m and neutral.get(m.group(1)) == 'decimal.MaxValue' and re.fullmatch(rf'{m.group(2)} = {m.group(1)}; if \(\w+ > {m.group(1)}\) {{ \w+ = {m.group(1)}; \w+\.Add\({var}\); }}', inner_s):
                k += 1  # MaxValue < lowest never holds (lowest starts at MaxValue and only decreases)
                continue
            # if (f) { list.Add(VAR); } with f holding a default false
            if c in neutral and neutral[c] == 'false' and re.fullmatch(rf'\w+\.Add\({var}\);', inner_s):
                k += 1
                continue
            # if (VAR.TryX(..., [out [var|T] m]) [&& m != null]) { ... } whose default returns false: never runs
            m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)( && \w+ != null)?', c)
            if m and kind_of(m.group(1))[0] in FALSE_KINDS:
                outs = re.findall(r'out (?:var |[\w<>?]+ )(\w+)', m.group(2))
                if all(not re.search(rf'{o}', ' '.join(x[1] if x[0] == 'stmt' else x[1] + x[2] for x in sts if x is not st)) for o in outs):
                    used['methods'].add(m.group(1))
                    k += 1
                    continue
            raise Unsafe('if: ' + c + ' { ' + inner_s[:60] + ' }')
    return used


def need(meth, kinds, used):
    params, kind = defaults.get(meth, (None, ('missing',)))
    if kind[0] not in [k[0] for k in [kinds]]:
        raise Unsafe(f'{meth}: default {kind}, need {kinds}')
    used['methods'].add(meth)


safe, unsafe = [], []
for name, text in hooks.items():
    try:
        if len(fps.get(name, [])) != 1:
            raise Unsafe('overloaded or no fingerprint')
        sig, rest = text.split('\n', 1)
        used = {'methods': set(), 'subscribed': False}
        loops = 0
        for m in re.finditer(r'foreach \(AbstractModel (\w+) in ([^\n]*)\)\s*\n', text):
            header = m.group(2)
            if 'IterateHookListeners' not in header and 'IterateCombatHookListeners' not in header:
                continue
            if not re.fullmatch(r'(runState\.IterateHookListeners\((combatState|null)\)|IterateCombatHookListeners\(combatState\)|[\w.]+\.IterateHookListeners\(\))', header.strip()):
                raise Unsafe('walk: ' + header)
            body, _ = block(text, m.end() - 1)
            var = m.group(1)
            check_loop(var, body, used)
            # the loop variable must not escape the loop
            loops += 1
        m = re.search(r'if \((runState\.IterateHookListeners\(combatState\)|IterateCombatHookListeners\(combatState\))\.Contains\((\w+)\)\)\s*\n', text)
        if m and loops == 0:
            inner, _ = block(text, m.end() - 1)
            p_ = m.group(2)
            inner_s = ' '.join(inner.split())
            mm = re.fullmatch(rf'await {p_}\.(\w+)\((.*)\); {p_}\.InvokeExecutionFinished\(\);', inner_s)
            if not mm or kind_of(mm.group(1))[0] != 'task':
                raise Unsafe('contains: ' + inner_s[:80])
            # Only P's membership matters: P is kept if it overrides M or has ExecutionFinished subscribers;
            # otherwise vanilla would call the no-op default and signal nobody.
            used['methods'].add(mm.group(1))
            used['subscribed'] = True
            loops += 1
        walks = len(re.findall(r'IterateHookListeners|IterateCombatHookListeners\(', text))
        if walks != loops:
            raise Unsafe(f'{walks} walks, {loops} checked loops')
        if not used['methods']:
            raise Unsafe('no listener method')
        kind = 'iterator' if 'yield' in text else ('async' if ' async ' in sig else 'plain')
        safe.append((name, fps[name][0], kind, sorted(used['methods']), used['subscribed']))
    except Unsafe as e:
        unsafe.append((name, str(e)))
    except Exception as e:  # parse problems: unsafe
        unsafe.append((name, 'parse: ' + repr(e)))

print(f'SAFE {len(safe)}  UNSAFE {len(unsafe)}')
for n, why in unsafe:
    print('  -', n, ':', why[:110])
rows = []
for name, fp, kind, methods, subscribed in safe:
    args = ', '.join(f'"{m}"' for m in methods)
    rows.append(f'        new("{name}", "{fp}", {"true" if subscribed else "false"}, {args}),')
open(TABLE, 'w', encoding='utf-8', newline='\n').write(
    '// Generated by tools/hookskip/classify.py from the decompiled game (v0.107.1): do not edit by hand.\n'
    'namespace BigDeckOptimization.Patches;\n\n'
    'public static partial class HookSkip\n{\n'
    '    /// <summary>Hooks whose listener loops only do no-ops for a listener keeping the defaults of these methods.</summary>\n'
    '    private static readonly Entry[] Hooks =\n    {\n' + '\n'.join(rows) + '\n    };\n}\n')
print('wrote', TABLE)
