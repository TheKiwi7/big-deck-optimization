"""Finds the game hooks HookSkip may filter and writes src/BigDeckOptimization/Patches/HookSkipTable.cs.

A hook qualifies when every loop over the listeners only does things that are no-ops for a listener keeping the
AbstractModel default of the method(s) the hook calls (default checked in AbstractModel.cs): awaiting a default task,
returning true/false for "should" checks, passing a value through, adding 0 / multiplying by 1, "try" methods that
return false, PushModel/PopModel, ExecutionFinished (listeners with subscribers are then kept), a per-listener
HookPlayerChoiceContext (no game action unless the listener asks for a choice), "changed value" bookkeeping. Anything
else leaves the hook out. Each entry carries the fingerprint of the hook's code (async: its state machine).

What "a no-op for a default listener" needs, checked here and not assumed:
- the arguments of every call to the listener are plain locals, parameters, literals or a parameter's chain of
  non-virtual auto-properties reviewed in REVIEWED_CHAINS: vanilla evaluates them once per listener, and a getter
  running code could throw or initialize something, which skipping the listener would not do (a chain's null root
  throws at the first listener in vanilla, at the first kept one filtered: each chain is reviewed for that);
- a listener call compared with a hook parameter (`if (VAR == source) { ... }`, v0.113's AfterTakingExtraTurn) is a
  reference comparison (AbstractModel declares no operator ==; the IL's `bne.un` is in the pinned fingerprint) with a
  parameter the hook never assigns: only that listener gets the block, checked like a loop body;
- a per-listener `List<T> x = new List<T>()` (v0.113's TryReplaceDefaultRoomRewards) is only an allocation: `x` may only
  be an argument of an `if (VAR.TryX(...))` whose default returns false, and be read inside that block (never run);
- the walk `combatState?.IterateHookListeners() ?? Array.Empty<AbstractModel>()` (v0.113's damage target hooks): HookSkip
  redirects the call on the non-null branch only, the null branch stays the game's empty array;
- no `out`/`ref` argument writes a variable the hook already has, except the "try" identity `M(..., x, out x)`;
  `out` declarations are allowed where the default's result means their value is never read;
- a "changed value" comparison compares the listener's own before/after values (`T prev = acc; acc = M(acc);
  if (prev != acc)`), not the running value with the hook's input (that one would list default listeners too);
- PushModel/PopModel come in pairs on the same context within one listener's turn;
- the helpers in REQUIRED_HELPERS are pinned (their code decides that a completed default task adds no await);
- a per-listener choice context reads `netId.Value`: the hook must first return when `!netId.HasValue` (otherwise vanilla
  would throw at the first listener, the filtered walk only at the first kept one);
- a list collecting each listener's `WaitForCompletion()` task holds fewer (completed) tasks when listeners are dropped: it
  may only be read by `await Task.WhenAll(list)` (same completion either way) and emptied by `Clear()`.
Known and accepted: PlayerChoiceContext creates its model stack on the first PushModel, so a passed-in context whose
listeners were all dropped keeps a null stack where vanilla leaves an empty one. Only `ModelStack` (public since v0.111)
tells them apart and nothing reads it (game, decompiled mods); a new reader would change no pinned fingerprint.

Every game version the DLL has code paths for is classified on its own code (the table is rewritten from the versions
given: one left out loses its rows; v0.111.0, the former beta, is no longer tested since 2026-11-16 but keeps its rows, so
it stays in the command below): an entry is kept for a version only when that version's hook passes, and carries what it relies on in that version: the hook's code fingerprint, the fingerprint of each
AbstractModel default it calls ("Name=FP"), and, for hooks using choice contexts / PushModel / ExecutionFinished, the
helpers' fingerprints of that version (HelperProfiles, reviewed by hand: tools/fpdiff review flags them when they change).
HookSkip turns a hook on only when all of these match the running game.

Usage (repo root), per version: decompile the game (ilspycmd -p) and dump the fingerprints, then classify all at once
(from Git Bash; in Windows PowerShell 5.1 `>` writes UTF-16, which load() also reads, or pipe to `Out-File -Encoding utf8`):
  dotnet run --project tools/fpdiff -c Release -- hookfps <game>/data_sts2_windows_x86_64 > fps-<version>.txt
  python -I tools/hookskip/classify.py v0.107.1 decompiled/sts2 fps-v0.107.1.txt v0.111.0 decompiled/sts2-beta fps-v0.111.0.txt \
      v0.113.1 decompiled/sts2-beta-0.113.1 fps-v0.113.1.txt
"""
import glob
import re
import sys

TABLE = 'src/BigDeckOptimization/Patches/HookSkipTable.cs'
VERSIONS = [sys.argv[i:i + 3] for i in range(1, len(sys.argv), 3)]
# Helpers every profile must pin (tools/fpdiff hookfps prints them, "Helper." lines): besides the choice-context helpers,
# TaskHelper.WhenAny, which AssignTaskAndWaitForPauseOrCompletion awaits: completed synchronously for a default listener's
# completed task. Were it to yield, skipping that listener would change timing (co-op ordering).
REQUIRED_HELPERS = ['MegaCrit.Sts2.Core.Helpers.TaskHelper::WhenAny(Task[])']
# A call argument vanilla evaluates per listener with no effect: a local, a parameter or a literal.
SIMPLE_ARG = re.compile(r'(\w+|-?\d+(\.\d+)?[mMfFdDuUlL]?|null|true|false)')
OUT_DECLARATION = re.compile(r'out (?:var |[\w<>?]+ )(\w+)')
# Auto-property chains passed to listeners, reviewed by hand: (hook, chain) -> why its root is never null (a null root
# throws in vanilla at the first listener, filtered at the first kept one or never). Any other chain: unsafe.
REVIEWED_CHAINS = {
    # v0.107.1: CombatManager's end of turn passes each player of the combat (never null; Player.Creature is set by its
    # constructor). A null player would make vanilla throw at every flush (a combat always has listeners).
    ('BeforeFlush', 'player.Creature.CombatState'): 'CombatManager passes a combat player',
    # v0.107.1: CreatureCmd.Kill passes the creature it reads CombatState, HP and MaxHp from before (a null one throws there).
    ('AfterDeath', 'creature.CombatState'): 'CreatureCmd.Kill passes the creature it already dereferenced',
}
CURRENT_HOOK = ''
ROOT = abstract = None
fps, default_fps, helpers, defaults = {}, {}, {}, {}


def load(root, fps_path):
    global ROOT, abstract, fps, default_fps, helpers
    ROOT = root
    abstract = open(ROOT + '/MegaCrit.Sts2.Core.Models/AbstractModel.cs', encoding='utf-8').read()
    fps, default_fps, helpers = {}, {}, {}
    raw = open(fps_path, 'rb').read()
    # Windows PowerShell 5.1's `>` writes UTF-16 LE with a BOM.
    text = raw.decode('utf-16') if raw[:2] in (b'\xff\xfe', b'\xfe\xff') else raw.decode('utf-8-sig')
    for line in text.splitlines():
        m = re.match(r'(Hook|AbstractModel|Helper)\.(.+): ([0-9A-F]{16}|-)$', line.strip())
        if not m:
            continue
        if m.group(1) == 'Hook':
            fps.setdefault(m.group(2), []).append(m.group(3))
        elif m.group(1) == 'AbstractModel':
            default_fps.setdefault(m.group(2), []).append(m.group(3))
        else:
            helpers[m.group(2)] = m.group(3)


def extract_hooks():
    """Hook methods (name -> source) that walk the listeners."""
    src = open(ROOT + '/MegaCrit.Sts2.Core.Hooks/Hook.cs', encoding='utf-8').read().split('\n')
    found, i = {}, 0
    while i < len(src):
        m = re.match(r'\t(public|private|internal) static (async )?([\w<>\[\], ?()]+) (\w+)\(', src[i])
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


def parse_defaults():
    """AbstractModel virtual defaults: name -> (params, kind)."""
    defaults = {}
    for m in re.finditer(r'public virtual ([\w<>?\[\], ]+?|\([\w<>?\[\], ]+\)) (\w+)\(([^)]*)\)\s*\{', abstract):
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
        elif re.fullmatch(r'return \((\w+), (\w+)\);', b) and all(x in params for x in re.fullmatch(r'return \((\w+), (\w+)\);', b).groups()):
            # A tuple of two parameters given back: passes both through.
            kind = ('tupleid', tuple(params.index(x) for x in re.fullmatch(r'return \((\w+), (\w+)\);', b).groups()))
        elif re.fullmatch(r'return (\w+);', b) and re.fullmatch(r'return (\w+);', b).group(1) in params:
            kind = ('identity', params.index(re.fullmatch(r'return (\w+);', b).group(1)))
        else:
            kind = ('other', b[:80])
        if name in defaults:
            defaults[name] = (params, ('overloaded',))
        else:
            defaults[name] = (params, kind)
    return defaults


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


HOOK_PARAMS = {}  # parameter name -> type name of the hook being classified
_auto_properties = {}


def auto_property(type_name, prop):
    """Type of `prop` when it is an auto-property of `type_name` (`{ get; }`, `{ get; set; }`...) in the decompiled
    game, else None: reading it only loads a field."""
    key = (ROOT, type_name, prop)
    if key not in _auto_properties:
        found = None
        for path in glob.glob(f'{ROOT}/**/{type_name}.cs', recursive=True):
            m = re.search(rf'\bpublic ([\w<>?\[\], ]+?) {prop} {{ get; (?:(?:private |internal |protected )?(?:set|init); )?}}',
                          open(path, encoding='utf-8').read())
            # Not virtual or abstract: an override could compute it.
            if m and not {'virtual', 'override', 'abstract', 'static', 'new'} & set(m.group(1).split()):
                found = m.group(1).split()[-1].rstrip('?')
        _auto_properties[key] = found
    return _auto_properties[key]


def auto_chain(expr):
    """`param.A.B`: a hook parameter followed by auto-property reads only (no code runs, nothing can change)."""
    parts = expr.split('.')
    if len(parts) < 2 or parts[0] not in HOOK_PARAMS or (CURRENT_HOOK, expr) not in REVIEWED_CHAINS:
        return False
    type_name = HOOK_PARAMS[parts[0]]
    for prop in parts[1:]:
        type_name = auto_property(type_name, prop)
        if type_name is None:
            return False
    return True


def plain(args, declarations=False):
    """Every argument is a local, a parameter, a literal or a parameter's auto-property chain (with declarations: or
    `out var x` / `out T x`)."""
    return all(SIMPLE_ARG.fullmatch(a) or auto_chain(a) or (declarations and OUT_DECLARATION.fullmatch(a)) for a in args)


def need_plain(args, declarations=False):
    if not plain(args, declarations):
        raise Unsafe('argument: ' + ', '.join(args))


def check_loop(var, body, used, neutral=None):
    """Raises Unsafe unless every statement is a no-op for a listener keeping the defaults of the methods in `used`."""
    neutral = dict(neutral or {})  # local var -> default constant it holds for a default listener
    sts = statements(body)
    flat = [' '.join(x[1].split()) if x[0] == 'stmt' else None for x in sts]
    # Value chain: [T prev = num;] num = VAR.M(...num...); [if (cmp(prev, num)) { list.Add(VAR); }]
    # copies: prev -> [the accumulator it copied, whether a listener call assigned that accumulator since].
    copies = {}
    # Contexts PushModel(VAR) was called on and not popped yet, innermost last.
    pushed = []
    # Per-listener `List<T> x = new List<T>()` locals -> index of their declaration; indexes of the `if (VAR.TryX(...))`
    # statements accepted as never running for a default listener (the only other place such a local may appear).
    fresh = {}
    false_ifs = set()

    def assigned(*accs):
        for copy in copies.values():
            if copy[0] in accs:
                copy[1] = True

    def own_change(a, b):
        """a and b are this listener's before/after values: a default listener keeps them equal."""
        return any(x in copies and copies[x] == [y, True] for x, y in ((a, b), (b, a)))

    k = 0
    while k < len(sts):
        st = sts[k]
        if st[0] == 'stmt':
            s = flat[k]
            m = re.fullmatch(rf'(\w+)\.PushModel\({var}\)', s)
            if m:
                pushed.append(m.group(1))
                used['contexts'] = True
                k += 1
                continue
            m = re.fullmatch(rf'(\w+)\.PopModel\({var}\)', s)
            if m:
                # Only a pair leaves the context's model stack as it was.
                if not pushed or pushed[-1] != m.group(1):
                    raise Unsafe('PopModel without its PushModel: ' + s)
                pushed.pop()
                used['contexts'] = True
                k += 1
                continue
            if s == f'{var}.InvokeExecutionFinished()':
                used['subscribed'] = True
                used['contexts'] = True
                k += 1
                continue
            # A choice context per listener: for a default listener the task is already complete, so it only
            # signals ExecutionFinished (kept listeners: those with subscribers) - no game action unless a choice.
            m = re.fullmatch(rf'HookPlayerChoiceContext (\w+) = new HookPlayerChoiceContext\({var}, netId\.Value, ([\w.]+), (?:GameActionType\.Combat|\(combatState != null\) \? GameActionType\.Combat : GameActionType\.NonCombat)\)', s)
            if m and plain([m.group(2)]):
                neutral['ctx:' + m.group(1)] = True
                used['subscribed'] = True
                used['contexts'] = True
                used['netid'] = True  # the hook's netId guard is checked in classify()
                k += 1
                continue
            m = re.fullmatch(rf'Task (\w+) = {var}\.(\w+)\((\w+)(?:, (.*))?\)', s)
            if m and neutral.get('ctx:' + m.group(3)) and kind_of(m.group(2))[0] == 'task':
                need_plain(split_args(m.group(4) or ''))
                neutral['task:' + m.group(1)] = True
                used['methods'].add(m.group(2))
                k += 1
                continue
            m = re.fullmatch(r'await (\w+)\.AssignTaskAndWaitForPauseOrCompletion\((\w+)\)', s)
            if m and neutral.get('ctx:' + m.group(1)) and neutral.get('task:' + m.group(2)):
                k += 1
                continue
            m = re.fullmatch(r'(\w+)\.Add\((\w+)\.WaitForCompletion\(\)\)', s)
            if m and neutral.get('ctx:' + m.group(2)):
                used['tasklists'].add(m.group(1))  # how the hook reads that list is checked in classify()
                k += 1
                continue
            # bool f = VAR.M(...) with M's default returning false
            m = re.fullmatch(rf'bool (\w+) = {var}\.(\w+)\((.*)\)', s)
            if m and kind_of(m.group(2))[0] in FALSE_KINDS:
                need_plain(split_args(m.group(3)))
                neutral[m.group(1)] = 'false'
                used['methods'].add(m.group(2))
                k += 1
                continue
            m = re.fullmatch(r'(\w+) = \1 \|\| (\w+)', s)
            if m and neutral.get(m.group(2)) == 'false':
                k += 1
                continue
            m = re.fullmatch(rf'(\w+) = \1 \|\| {var}\.(\w+)\((.*)\)', s)
            if m and kind_of(m.group(2))[0] in FALSE_KINDS:
                need_plain(split_args(m.group(3)))
                used['methods'].add(m.group(2))
                k += 1
                continue
            # T d = VAR.M(...) where M's default returns a constant (0m, 1m, MaxValue)
            m = re.fullmatch(rf'(?:decimal|int) (\w+) = {var}\.(\w+)\((.*)\)', s)
            if m and defaults.get(m.group(2), (None, ('missing',)))[1][0] == 'const':
                need_plain(split_args(m.group(3)))
                neutral[m.group(1)] = defaults[m.group(2)][1][1]
                used['methods'].add(m.group(2))
                k += 1
                continue
            m = re.fullmatch(r'(\w+) (\+|\*)= (\w+)', s)
            if m and m.group(3) in neutral and ((m.group(2) == '+' and neutral[m.group(3)] in ('0m', '0')) or (m.group(2) == '*' and neutral[m.group(3)] in ('1m', '1'))):
                k += 1
                continue
            m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)', s)
            if m:
                kind = defaults.get(m.group(1), (None, ('missing',)))[1]
                args = split_args(m.group(2))
                # VAR.TryX(..., IN, ..., out IN) whose default copies IN to the out parameter: identity. An out
                # argument naming another variable would be written by the default: not this rule.
                if kind[0] == 'tryout' and kind[1] < len(args) - 1 and args[-1] == 'out ' + args[kind[1]]:
                    need_plain(args[:-1])
                    used['methods'].add(m.group(1))
                    k += 1
                    continue
                # Result ignored, default without side effect; no out/ref argument (a "falsy" default writes those).
                if kind[0] in NOOP_KINDS:
                    need_plain(args)
                    used['methods'].add(m.group(1))
                    k += 1
                    continue
            m = re.fullmatch(rf'await {var}\.(\w+)\((.*)\)', s)
            if m:
                need(m.group(1), ('task',), used)
                need_plain(split_args(m.group(2)))
                k += 1
                continue
            c = call(s, var)
            if c:
                need(c[0], ('void',), used)
                need_plain(c[1])
                k += 1
                continue
            m = re.fullmatch(rf'(?:\w+ )?(\w+) = {var}\.(\w+)\((.*)\)', s)
            if m:
                acc, meth, args = m.group(1), m.group(2), split_args(m.group(3))
                params, kind = defaults.get(meth, (None, ('missing',)))
                if kind[0] != 'identity' or kind[1] >= len(args) or args[kind[1]] != acc:
                    raise Unsafe(f'{meth}: not an identity on {acc} ({kind})')
                need_plain(args)
                assigned(acc)
                used['methods'].add(meth)
                k += 1
                continue
            # (a, b) = VAR.M(..., a, b) whose default gives both back
            m = re.fullmatch(rf'\((\w+), (\w+)\) = {var}\.(\w+)\((.*)\)', s)
            if m:
                acc, meth, args = (m.group(1), m.group(2)), m.group(3), split_args(m.group(4))
                kind = defaults.get(meth, (None, ('missing',)))[1]
                if kind[0] != 'tupleid' or max(kind[1]) >= len(args) or (args[kind[1][0]], args[kind[1][1]]) != acc:
                    raise Unsafe(f'{meth}: not an identity on {acc} ({kind})')
                need_plain(args)
                assigned(*acc)
                used['methods'].add(meth)
                k += 1
                continue
            m = re.fullmatch(r'(?:decimal|int|bool|float|PileType|CardPilePosition|CardLocation) (\w+) = (\w+)', s)
            if m and not re.search(rf'\b{var}\b', s):
                copies[m.group(1)] = [m.group(2), False]  # previous value of the accumulator
                k += 1
                continue
            # A new empty list per listener (an allocation, nothing else): where it may be used is checked at the end.
            m = re.fullmatch(r'List<([\w.<>]+)> (\w+) = new List<\1>\(\)', s)
            if m and m.group(2) != var and m.group(2) not in fresh:
                fresh[m.group(2)] = k
                k += 1
                continue
            raise Unsafe('statement: ' + s)
        else:
            _, cond, inner = st
            c = ' '.join(cond.split())
            inner_s = ' '.join(inner.split())
            m = re.fullmatch(rf'!{var}\.(\w+)\((.*)\)', c)
            if m and re.fullmatch(rf'(\w+ = {var}; )?return false;', inner_s):
                need(m.group(1), ('true',), used)
                need_plain(split_args(m.group(2)))
                k += 1
                continue
            m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)', c)
            if m and inner_s == 'return true;':
                need(m.group(1), ('false',), used)
                need_plain(split_args(m.group(2)))
                k += 1
                continue
            if re.fullmatch(rf'\w+\.Contains\({var}\)', c):
                check_loop(var, inner, used)  # only members get the call: skipping a member keeping defaults is a no-op
                k += 1
                continue
            # if (VAR == p) { ... }: only the listener that is the hook's AbstractModel parameter p gets the block. A
            # reference comparison (no operator ==/!= in AbstractModel.cs; the IL's bne.un pinned by the fingerprint), p
            # never assigned in the hook (checked in check_hook_level): skipping p keeping defaults is a no-op.
            m = re.fullmatch(rf'(?:{var} == (\w+)|(\w+) == {var})', c)
            if m and HOOK_PARAMS.get(m.group(1) or m.group(2)) == 'AbstractModel' and not model_equality_overloaded():
                used['eqparams'].add(m.group(1) or m.group(2))
                check_loop(var, inner, used)
                k += 1
                continue
            # Value changed -> listed: only this listener's before/after values (a default keeps them equal); a
            # comparison with the hook's input would list default listeners after the first change.
            m = re.fullmatch(r'(?:decimal\.Truncate\(|\(int\))?(\w+)\)? != (?:decimal\.Truncate\(|\(int\))?(\w+)\)?', c)
            if m and re.fullmatch(rf'\w+\.Add\({var}\);', inner_s) and own_change(m.group(1), m.group(2)):
                k += 1
                continue
            m = re.fullmatch(r'\(?(\w+) != (\w+) \|\| (\w+) != (\w+)\)?', c)
            if m and re.fullmatch(rf'\w+\.Add\({var}\);', inner_s) and own_change(m.group(1), m.group(2)) and own_change(m.group(3), m.group(4)):
                k += 1  # either value changed -> listed: a default keeps both unchanged
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
            # if (VAR.TryX(..., [out [var|T] m]) [&& m != null]) { ... } whose default returns false: never runs. Out
            # arguments must be new variables (the default writes them), read nowhere else in the loop.
            m = re.fullmatch(rf'{var}\.(\w+)\((.*)\)( && \w+ != null)?', c)
            if m and kind_of(m.group(1))[0] in FALSE_KINDS:
                need_plain(split_args(m.group(2)), declarations=True)
                outs = OUT_DECLARATION.findall(m.group(2))
                if all(not re.search(rf'\b{o}\b', ' '.join(x[1] if x[0] == 'stmt' else x[1] + x[2] for x in sts if x is not st)) for o in outs):
                    used['methods'].add(m.group(1))
                    false_ifs.add(k)
                    k += 1
                    continue
            raise Unsafe('if: ' + c + ' { ' + inner_s[:60] + ' }')
    if pushed:
        raise Unsafe('PushModel without its PopModel')
    for name, declared in fresh.items():
        # Only as an argument of (or inside) an if whose default call returns false: a default listener never makes
        # anything read it. Declared in the loop body, so not visible after the loop.
        for i, x in enumerate(sts):
            text = x[1] if x[0] == 'stmt' else x[1] + ' ' + x[2]
            if i != declared and i not in false_ifs and re.search(rf'\b{name}\b', text):
                raise Unsafe(f'{name}: per-listener list used outside a never-run block')
    return used


def model_equality_overloaded():
    """AbstractModel (or this version's source of it) declares == or != itself: `VAR == p` would run code."""
    return re.search(r'\boperator\s*(==|!=)', abstract) is not None


def need(meth, kinds, used):
    params, kind = defaults.get(meth, (None, ('missing',)))
    if kind[0] not in kinds:
        raise Unsafe(f'{meth}: default {kind}, need {kinds}')
    used['methods'].add(meth)


def check_hook_level(body, used):
    """What the loops rely on in the rest of the hook: the netId guard, how the per-listener task lists are read."""
    flat = ' '.join(body.split())
    if used['netid']:
        # First statements of the hook: `ulong? netId = LocalContext.NetId; if (!netId.HasValue) { return; }`, netId never
        # assigned again.
        if not re.match(r'\{ ulong\? netId = LocalContext\.NetId; if \(!netId\.HasValue\) \{ return; \}', flat) \
                or len(re.findall(r'\bnetId\s*=[^=]', flat)) != 1:
            raise Unsafe('netId.Value without the !netId.HasValue early return first')
    for name in used['eqparams']:
        # The parameter a listener is compared with keeps the value it came with (assigned, `out`/`ref`: unsafe).
        if re.search(rf'\b{name}\s*(?:\?\?)?=(?!=)|\b(?:out|ref) {name}\b', flat):
            raise Unsafe(f'{name}: compared with listeners but assigned in the hook')
    for name in used['tasklists']:
        declared = len(re.findall(rf'List<Task> {name} = new List<Task>\(\);', flat))
        added = len(re.findall(rf'\b{name}\.Add\(\w+\.WaitForCompletion\(\)\);', flat))
        awaited = len(re.findall(rf'await Task\.WhenAll\({name}\);', flat))
        cleared = len(re.findall(rf'\b{name}\.Clear\(\);', flat))  # emptied for the next loop: size never read
        if declared != 1 or len(re.findall(rf'\b{name}\b', flat)) != declared + added + awaited + cleared:
            raise Unsafe(f'{name}: read otherwise than by await Task.WhenAll({name})')


def classify():
    global CURRENT_HOOK
    hooks = extract_hooks()
    safe, unsafe = [], []
    for name, text in hooks.items():
        try:
            if len(fps.get(name, [])) != 1:
                raise Unsafe('overloaded or no fingerprint')
            sig, rest = text.split('\n', 1)
            CURRENT_HOOK = name
            HOOK_PARAMS.clear()
            for p_type, p_name in re.findall(r'([\w.<>\[\]]+)\?? (\w+)(?=,|\))', sig[sig.index('('):]):
                HOOK_PARAMS[p_name] = p_type.split('.')[-1]
            used = {'methods': set(), 'subscribed': False, 'contexts': False, 'netid': False, 'tasklists': set(), 'eqparams': set()}
            loops = 0
            for m in re.finditer(r'foreach \(AbstractModel (\w+) in ([^\n]*)\)\s*\n', text):
                header = m.group(2)
                if 'IterateHookListeners' not in header and 'IterateCombatHookListeners' not in header:
                    continue
                if not re.fullmatch(r'(runState\.IterateHookListeners\((combatState|null)\)|IterateCombatHookListeners\(combatState\)|[\w.]+\.IterateHookListeners\(\)'
                                    # Null branch: the game's empty array; the non-null call is the one redirected (CombatWalk).
                                    r'|combatState\?\.IterateHookListeners\(\) \?\? Array\.Empty<AbstractModel>\(\))', header.strip()):
                    raise Unsafe('walk: ' + header)
                body, _ = block(text, m.end() - 1)
                var = m.group(1)
                # The loop variable escapes the loop only where a default listener never gets: C# scopes it to the loop,
                # and check_loop's closed list of statements (anything else raises Unsafe) stores it outside only in
                # blocks that don't run for a default (the "changed" list.Add(VAR)). No separate check.
                check_loop(var, body, used)
                loops += 1
            m = re.search(r'if \((runState\.IterateHookListeners\(combatState\)|IterateCombatHookListeners\(combatState\))\.Contains\((\w+)\)\)\s*\n', text)
            if m and loops == 0:
                inner, _ = block(text, m.end() - 1)
                p_ = m.group(2)
                inner_s = ' '.join(inner.split())
                mm = re.fullmatch(rf'await {p_}\.(\w+)\((.*)\); {p_}\.InvokeExecutionFinished\(\);', inner_s)
                if not mm or kind_of(mm.group(1))[0] != 'task':
                    raise Unsafe('contains: ' + inner_s[:80])
                need_plain(split_args(mm.group(2)))
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
            check_hook_level(rest, used)
            safe.append((name, fps[name][0], sorted(used['methods']), used['subscribed'], used['contexts'] or used['subscribed']))
        except Unsafe as e:
            unsafe.append((name, str(e)))
        except Exception as e:  # parse problems: unsafe
            unsafe.append((name, 'parse: ' + repr(e)))
    return safe, unsafe


rows, profiles, labels = [], [], []
for label, root, fps_path in VERSIONS:
    load(root, fps_path)
    missing = [h for h in REQUIRED_HELPERS if h not in helpers]
    if missing:
        raise SystemExit(f'{label}: {fps_path} has no Helper line for {missing}: add them to HookHelpers in tools/fpdiff/Program.cs')
    defaults = parse_defaults()
    safe, unsafe = classify()
    labels.append(label)
    profiles.append(dict(helpers))
    print(f'{label}: SAFE {len(safe)}  UNSAFE {len(unsafe)}')
    for n, why in unsafe:
        print('  -', n, ':', why[:110])
    for name, fp, methods, subscribed, contexts in safe:
        pins = []
        for meth in methods:
            if len(default_fps.get(meth, [])) != 1:
                raise SystemExit(f'{label}: {meth} has no single AbstractModel default fingerprint')
            pins.append(f'"{meth}={default_fps[meth][0]}"')
        profile = len(profiles) - 1 if contexts else -1
        row = f'        new("{name}", "{fp}", {"true" if subscribed else "false"}, {profile}, {", ".join(pins)}),'
        if row not in rows:
            rows.append(row)
keys = sorted(set().union(*profiles))
profile_rows = []
for label, profile in zip(labels, profiles):
    items = ', '.join(f'["{k}"] = "{profile.get(k, "-")}"' for k in keys)
    profile_rows.append(f'        // {label}\n        new() {{ {items} }},')
open(TABLE, 'w', encoding='utf-8', newline='\n').write(
    f'// Generated by tools/hookskip/classify.py from the decompiled game ({", ".join(labels)}): do not edit by hand.\n'
    'using System.Collections.Generic;\n\n'
    'namespace BigDeckOptimization.Patches;\n\n'
    'public static partial class HookSkip\n{\n'
    '    /// <summary>Per classified game version: fingerprints of the helpers the hooks using choice contexts rely on ("-": absent).</summary>\n'
    '    private static readonly Dictionary<string, string>[] HelperProfiles =\n    {\n' + '\n'.join(profile_rows) + '\n    };\n\n'
    '    /// <summary>\n'
    '    /// Hooks whose listener loops only do no-ops for a listener keeping the defaults of these methods, per classified\n'
    '    /// version: hook code fingerprint, ExecutionFinished kept, helper profile (-1: none used), "Method=default fingerprint".\n'
    '    /// </summary>\n'
    '    private static readonly Entry[] Hooks =\n    {\n' + '\n'.join(rows) + '\n    };\n}\n')
print('wrote', TABLE, len(rows), 'entries')
