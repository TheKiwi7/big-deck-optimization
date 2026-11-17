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
Made exact, no longer a premise (verification audit of 2026-11-17, A4; DESIGN "Hardening of 2026-11-17"): PlayerChoiceContext
creates its model stack (`_modelStack`, a Stack<AbstractModel>) on the first PushModel, and in Hook.cs every PushModel is the
first statement of its foreach over a walk (24 on v0.107.1, v0.111.1 and v0.113.1), so only a walk keeping no listener where vanilla's
yields some would leave a passed-in context's stack null where vanilla leaves it empty. HookSkip's filters keep every badge
and walk vanilla on a state without one (every run has them: RunState.CreateShared), and the filtered walks over the game's
own walk (FastListeners.KeptOnly) keep its first listener. What this script relies on is unchanged: PushModel/PopModel in
pairs within one listener's turn (the stack's contents are the same after each turn, kept or dropped). During a mirrored
walk the stack may still be created at the first kept listener rather than the first one: HookSkip lets no prefix gated on
the listener stand on PushModel (postfixes run once the stack exists on both sides).

Every game version the DLL has code paths for is classified on its own code (the table is rewritten from the versions
given: one left out loses its own rows; only the public version and the latest beta are given: v0.111.1 and v0.113.1
since 2026-11-17, when v0.111.1 became the public version and v0.107.1 was no longer distributed). HookSkip matches a hook
against every row of its name whatever the version, so a version left out keeps the filtering for each hook whose code,
defaults and helpers equal a kept version's row (v0.111.0: all 148, its hooks, defaults and helpers being v0.111.1's;
v0.107.1: the 62 hooks using no helper whose code and defaults equal a kept row, its helpers matching no kept profile;
both untested) and runs the game's code for the others: an entry is kept for a version only when that version's hook passes, and carries what it relies on in that version: the hook's code fingerprint, the fingerprint of each
AbstractModel default it calls ("Name=FP"), and, for hooks using choice contexts / PushModel / ExecutionFinished, the
helpers' fingerprints of that version (HelperProfiles, reviewed by hand: tools/fpdiff review flags them when they change).
HookSkip turns a hook on only when all of these match the running game.

Each entry also names its helper set (HelperSets): the helpers a dropped listener's iteration calls, those check_loop matched
in its loops closed over the helpers' own calls (CLOSURE, checked against each version's helper bodies), so another mod's
patch on a helper only sends the hooks calling it to the game; and per helper the parameter holding the listener in all those
calls (HelperListeners: "source", "model", "__instance", or "" for none), on which HookSkip lets patches gated on the
listener's type stand (DESIGN "Needless back-offs made safe", class 4). Since 2026-11-17 a set also holds every method of the
game a helper may call (fpdiff hookfps' Calls lines: its call graph inside the game's assembly, virtual and interface calls
to every implementation, up to the other helpers, minus the reviewed NEVER_TAKEN edges) and the getters of the reviewed
chains a hook passes (Getter lines): a dropped listener skips those calls too, so they are checked strictly (no listener
argument) and pinned in the version's profile.

Usage (repo root), per version: decompile the game (ilspycmd -p) and dump the fingerprints, then classify all at once
(from Git Bash; in Windows PowerShell 5.1 `>` writes UTF-16, which load() also reads, or pipe to `Out-File -Encoding utf8`):
  dotnet run --project tools/fpdiff -c Release -- hookfps <game>/data_sts2_windows_x86_64 > fps-<version>.txt
  python -I tools/hookskip/classify.py v0.111.1 decompiled/sts2-public-0.111.1 fps-v0.111.1.txt \
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
# Per entry, the helpers a dropped listener's iteration calls (HookSkip checks other mods' patches on those only): the ones
# check_loop matched in the loop bodies (used['helpers']), closed over the helpers' own calls to other helpers listed here and
# checked against each version's code (helper_closure: any other call to a helper name in a helper's body stops the script).
# Each item: callee, the exact call text in the caller's body, and where the callee gets the listener (0: its first argument,
# 'this': its instance, None: no listener). `Source` is the context's get-only auto-property, set from the constructor's
# `source` (checked): in the hooks' iterations every context comes from `new HookPlayerChoiceContext(VAR, ...)`.
CLOSURE = {
    '.ctor': [('PushModel', r'\bPushModel\(Source\);', 0), ('GetOwner', r'\bOwner = GetOwner\(source, combatState\);', 0)],
    'AssignTaskAndWaitForPauseOrCompletion': [
        ('ExecuteTaskThenInvokeExecutionFinished', r'\bTask = ExecuteTaskThenInvokeExecutionFinished\(task\);', None),
        ('WhenAny', r'\bawait TaskHelper\.WhenAny\(task, _pausedCompletionSource\.Task\);', None)],
    'ExecuteTaskThenInvokeExecutionFinished': [('InvokeExecutionFinished', r'\bSource\?\.InvokeExecutionFinished\(\);', 'this')],
}
# Where the loop patterns of check_loop give each helper the listener (the same meaning as in CLOSURE).
LOOP_LISTENER = {'.ctor': 0, 'PushModel': 0, 'PopModel': 0, 'InvokeExecutionFinished': 'this',
                 'AssignTaskAndWaitForPauseOrCompletion': None, 'WaitForCompletion': None}
# Calls in a helper's body that only look like a helper's name: TaskHelper.WhenAny awaits System.Threading.Tasks.Task's.
NOT_HELPER_CALLS = [r'\bTask\.WhenAny\(tasks\)']
CSHARP_TYPES = {'ulong': 'UInt64', 'long': 'Int64', 'int': 'Int32', 'uint': 'UInt32', 'bool': 'Boolean', 'string': 'String',
                'object': 'Object', 'decimal': 'Decimal', 'float': 'Single', 'double': 'Double'}
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
calls_graph, skipped_edges, getters = {}, set(), {}
# The getters of the reviewed chains the hook being classified passes to its listeners (vanilla calls them once per listener).
CHAIN_USED = set()
# Edges of the helpers' call graph a dropped listener's call never takes: (caller, callee) -> a regex that must match the
# caller's whole decompiled body, proving the call sits in that branch. fpdiff's NeverTaken lists the same pairs: it leaves
# them out of the Calls lines and prints a Skipped line; a Skipped edge without a proof here, or a proof that no longer
# matches, stops the script.
NEVER_TAKEN = {
    # PopModel's error branch: the listener's PopModel(VAR) follows its own PushModel(VAR) on the same context with only a
    # default's no-op between (check_loop's pair rule), so the stack holds that model on top: the branch never runs.
    ('MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext::PopModel(AbstractModel)', 'MegaCrit.Sts2.Core.Logging.Log::Error(String,Int32)'):
        r'^\s*AbstractModel result = null;\s*if \(_modelStack == null \|\| !_modelStack\.TryPeek\(out result\) \|\| result != model\)\s*'
        r'\{\s*Log\.Error\([^;]*\);\s*\}\s*else\s*\{\s*_modelStack\.Pop\(\);\s*\}\s*$',
}
# The same branch's first use of Log runs its type initializer (fpdiff lists the initializers a call may run, 2026-11-17 C).
NEVER_TAKEN[('MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext::PopModel(AbstractModel)', 'MegaCrit.Sts2.Core.Logging.Log::.cctor()')] = \
    NEVER_TAKEN[('MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext::PopModel(AbstractModel)', 'MegaCrit.Sts2.Core.Logging.Log::Error(String,Int32)')]
# Delegates the helpers' call graph invokes (fpdiff's Invokes lines: the code behind them isn't in the graph), each with why a
# dropped listener's call reaches no code there; any other stops the script (verification audit of 2026-11-17, C).
INVOKED = {
    # The listener's ExecutionFinished event: HookSkip keeps every listener with a subscriber in the hooks that signal it
    # (Entry.Subscribed, the filters keeping ExecutionFinished subscribers), so a dropped listener's event has none.
    ('MegaCrit.Sts2.Core.Models.AbstractModel::InvokeExecutionFinished()', 'System.Action`1[MegaCrit.Sts2.Core.Models.AbstractModel]'),
}


def load(root, fps_path):
    global ROOT, abstract, fps, default_fps, helpers, calls_graph, skipped_edges, getters, invoked, dispatch, flags
    ROOT = root
    abstract = open(ROOT + '/MegaCrit.Sts2.Core.Models/AbstractModel.cs', encoding='utf-8').read()
    fps, default_fps, helpers, calls_graph, skipped_edges, getters, invoked, dispatch, flags = {}, {}, {}, {}, set(), {}, set(), {}, {}
    raw = open(fps_path, 'rb').read()
    # Windows PowerShell 5.1's `>` writes UTF-16 LE with a BOM.
    text = raw.decode('utf-16') if raw[:2] in (b'\xff\xfe', b'\xfe\xff') else raw.decode('utf-8-sig')
    for line in text.splitlines():
        # The helpers' call graph inside the game (fpdiff hookfps' Calls lines), the edges it left out (Skipped), and the
        # chain getters' fingerprints (Getter).
        m = re.match(r'Calls\.(.+?) -> (.+): ([0-9A-F]{16}|-)$', line.strip())
        if m:
            calls_graph.setdefault(m.group(1), []).append((m.group(2), m.group(3)))
            continue
        m = re.match(r'Skipped\.(.+?) -> (.+)$', line.strip())
        if m:
            skipped_edges.add((m.group(1), m.group(2)))
            continue
        m = re.match(r'Invokes\.(.+?) -> (.+)$', line.strip())
        if m:
            invoked.add((m.group(1), m.group(2)))
            continue
        # A2: each virtual or interface call's implementers in the game, and each method's virtual/final flag (pinned).
        m = re.match(r'Dispatch\.(.+?) -> (.+): (.*)$', line.strip())
        if m:
            dispatch[(m.group(1), m.group(2))] = m.group(3)
            continue
        m = re.match(r'Flags\.(.+): (virtual|-)$', line.strip())
        if m:
            flags[m.group(1)] = m.group(2)
            continue
        m = re.match(r'Getter\.(.+): ([0-9A-F]{16}|-)$', line.strip())
        if m:
            getters[m.group(1)] = m.group(2)
            continue
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
    """(Type of `prop`, its getter's key) when it is an auto-property of `type_name` (`{ get; }`, `{ get; set; }`...) in
    the decompiled game, else None: reading it only loads a field in the game's code. Another mod's patch on that getter
    does run: the getter goes into the hook's helper set (checked strictly) and its fingerprint is pinned (fpdiff's Getter
    lines, ChainGetters there)."""
    key = (ROOT, type_name, prop)
    if key not in _auto_properties:
        found = None
        for path in glob.glob(f'{ROOT}/**/{type_name}.cs', recursive=True):
            m = re.search(rf'\bpublic ([\w<>?\[\], ]+?) {prop} {{ get; (?:(?:private |internal |protected )?(?:set|init); )?}}',
                          open(path, encoding='utf-8').read())
            # Not virtual or abstract: an override could compute it.
            if m and not {'virtual', 'override', 'abstract', 'static', 'new'} & set(m.group(1).split()):
                namespace = path.replace('\\', '/').split('/')[-2]
                found = (m.group(1).split()[-1].rstrip('?'), f'{namespace}.{type_name}::get_{prop}()')
        _auto_properties[key] = found
    return _auto_properties[key]


def auto_chain(expr):
    """`param.A.B`: a hook parameter followed by auto-property reads only (no code runs, nothing can change)."""
    parts = expr.split('.')
    if len(parts) < 2 or parts[0] not in HOOK_PARAMS or (CURRENT_HOOK, expr) not in REVIEWED_CHAINS:
        return False
    type_name = HOOK_PARAMS[parts[0]]
    keys = []
    for prop in parts[1:]:
        found = auto_property(type_name, prop)
        if found is None:
            return False
        type_name = found[0]
        keys.append(found[1])
    CHAIN_USED.update(keys)
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
                used['helpers'].add('PushModel')  # the listener is its argument
                k += 1
                continue
            m = re.fullmatch(rf'(\w+)\.PopModel\({var}\)', s)
            if m:
                # Only a pair leaves the context's model stack as it was.
                if not pushed or pushed[-1] != m.group(1):
                    raise Unsafe('PopModel without its PushModel: ' + s)
                pushed.pop()
                used['contexts'] = True
                used['helpers'].add('PopModel')  # the listener is its argument
                k += 1
                continue
            if s == f'{var}.InvokeExecutionFinished()':
                used['subscribed'] = True
                used['contexts'] = True
                used['helpers'].add('InvokeExecutionFinished')  # the listener is the instance
                k += 1
                continue
            # A choice context per listener: for a default listener the task is already complete, so it only
            # signals ExecutionFinished (kept listeners: those with subscribers) - no game action unless a choice.
            m = re.fullmatch(rf'HookPlayerChoiceContext (\w+) = new HookPlayerChoiceContext\({var}, netId\.Value, ([\w.]+), (?:GameActionType\.Combat|\(combatState != null\) \? GameActionType\.Combat : GameActionType\.NonCombat)\)', s)
            if m and plain([m.group(2)]):
                neutral['ctx:' + m.group(1)] = True
                used['subscribed'] = True
                used['contexts'] = True
                used['helpers'].add('.ctor')  # the listener is its first argument (source)
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
                used['helpers'].add('AssignTaskAndWaitForPauseOrCompletion')  # on this listener's own context: no listener argument
                k += 1
                continue
            m = re.fullmatch(r'(\w+)\.Add\((\w+)\.WaitForCompletion\(\)\)', s)
            if m and neutral.get('ctx:' + m.group(2)):
                used['helpers'].add('WaitForCompletion')  # on this listener's own context: no listener argument
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
            used = {'methods': set(), 'subscribed': False, 'contexts': False, 'netid': False, 'tasklists': set(), 'eqparams': set(),
                    'helpers': set()}
            CHAIN_USED.clear()
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
                used['helpers'].add('InvokeExecutionFinished')  # P, the walk's member, is the instance
                loops += 1
            walks = len(re.findall(r'IterateHookListeners|IterateCombatHookListeners\(', text))
            if walks != loops:
                raise Unsafe(f'{walks} walks, {loops} checked loops')
            if not used['methods']:
                raise Unsafe('no listener method')
            check_hook_level(rest, used)
            safe.append((name, fps[name][0], sorted(used['methods']), used['subscribed'], used['contexts'] or used['subscribed'],
                         sorted(used['helpers']), sorted(CHAIN_USED)))
        except Unsafe as e:
            unsafe.append((name, str(e)))
        except Exception as e:  # parse problems: unsafe
            unsafe.append((name, 'parse: ' + repr(e)))
    return safe, unsafe


def short_name(key):
    """'Ns.Type::Method(Args)' -> 'Method' ('.ctor' for a constructor): unique among the helper profiles' keys."""
    return key.split('::', 1)[1].split('(', 1)[0]


def declaration(key):
    """(parameter names, body, file text) of a helper in this version's decompiled code, matched by its key's argument types."""
    owner, member = key.split('::', 1)
    namespace, type_name = owner.rsplit('.', 1)
    method, args = member[:-1].split('(', 1)
    want = [a for a in args.split(',') if a]
    text = open(f'{ROOT}/{namespace}/{type_name}.cs', encoding='utf-8').read()
    name = type_name if method == '.ctor' else method
    found = []
    for m in re.finditer(rf'\n\t(?:public|private|internal|protected)[\w <>?\[\],]* {re.escape(name)}\(([^)]*)\)\s*\n\t\{{', text):
        params = [p.strip() for p in split_args(m.group(1))] if m.group(1).strip() else []
        declared = [p.replace('params ', '').rsplit(' ', 1)[0].rstrip('?') for p in params]
        if [CSHARP_TYPES.get(t, t) for t in declared] == want:
            body, _ = block(text, m.end() - 1)
            found.append(([p.rsplit(' ', 1)[1] for p in params], body, text))
    if len(found) != 1:
        raise SystemExit(f'{key}: {len(found)} declarations found in the decompiled code (one expected): update declaration()')
    return found[0]


def listener_parameter(key, where):
    """The name Harmony binds the listener's argument by: the declared parameter, or __instance ('': no listener)."""
    if where is None:
        return ''
    if where == 'this':
        return '__instance'
    return declaration(key)[0][where]


def helper_closure():
    """For this version: per helper present (short name) -> (key, its calls to other helpers: [(short name, listener place)]).
    Every call to a helper's name in a helper's body must be one listed in CLOSURE (or NOT_HELPER_CALLS): anything else
    stops the script, and the closure is reviewed by hand."""
    present = {short_name(k): k for k, fp in helpers.items() if fp != '-'}
    names = '|'.join(re.escape(n) for n in present if n != '.ctor')
    calls = {}
    for short, key in present.items():
        _, body, text = declaration(key)
        listed, covered = [], []
        for callee, pattern, where in CLOSURE.get(short, []):
            for m in re.finditer(pattern, body):
                if callee not in present:
                    raise SystemExit(f"{key} calls {callee}, which this version's helper profile lacks")
                covered.append((m.start(), m.end()))
                listed.append((callee, where))
        for pattern in NOT_HELPER_CALLS:
            covered += [(m.start(), m.end()) for m in re.finditer(pattern, body)]
        for m in re.finditer(rf'\b({names})\(|\bnew HookPlayerChoiceContext\(', body):
            if not any(a <= m.start() < b for a, b in covered):
                context = body[max(0, m.start() - 40):m.end() + 40].strip()
                raise SystemExit(f'{key}: a call to a helper not in CLOSURE ({context!r}): review the closure')
        if short in ('.ctor', 'ExecuteTaskThenInvokeExecutionFinished') and listed:
            # The calls taking Source as the listener: Source must be a get-only auto-property (set by constructors only),
            # set from the constructor's source before its PushModel(Source).
            if not re.search(r'\bpublic AbstractModel\? Source \{ get; \}', text):
                raise SystemExit(f'{key}: HookPlayerChoiceContext.Source is no longer a get-only auto-property: review the closure')
            if short == '.ctor' and not re.search(r'\bSource = source;[\s\S]*\bPushModel\(Source\);', body):
                raise SystemExit(f'{key}: Source not set from source before PushModel(Source): review the closure')
        calls[short] = (key, listed)
    return calls


def check_helper_edges():
    """fpdiff's helper-to-helper Calls edges are exactly CLOSURE's items (A1, verification audit of 2026-11-17): an edge it
    lists that CLOSURE lacks means a helper calls another in a way classify.py never reviewed (the listener argument, a pair
    rule); and every delegate the graph invokes is a reviewed one (INVOKED)."""
    for caller, callees in calls_graph.items():
        if caller not in helpers:
            continue
        allowed = {c for c, _, _ in CLOSURE.get(short_name(caller), [])}
        for callee, _ in callees:
            if callee in helpers and short_name(callee) not in allowed:
                raise SystemExit(f'{caller} -> {callee}: fpdiff lists a helper-to-helper call CLOSURE lacks: review the closure')
    for caller, delegate in sorted(invoked):
        if (caller, delegate) not in INVOKED:
            raise SystemExit(f'{caller} invokes {delegate}: its targets are not in the call graph; review it and add it to INVOKED')


def check_never_taken():
    """Every edge fpdiff left out of the helpers' call graph has its proof here, and the proof matches the caller's body."""
    for caller, callee in sorted(skipped_edges):
        proof = NEVER_TAKEN.get((caller, callee))
        if proof is None:
            raise SystemExit(f'{caller} -> {callee}: fpdiff skipped this edge but NEVER_TAKEN has no proof for it')
        _, body, _ = declaration(caller)
        if not re.fullmatch(proof, body):
            raise SystemExit(f'{caller} -> {callee}: the proof that a dropped listener never takes this call no longer matches its body')


def game_callees(keys):
    """{callee key: fingerprint} of every game method the helpers in `keys` may call, transitively (fpdiff's Calls lines: up
    to the other helpers, minus the never-taken edges). A dropped listener skips those calls: checked strictly and pinned."""
    found, todo = {}, list(keys)
    while todo:
        caller = todo.pop()
        for callee, fp in calls_graph.get(caller, []):
            if callee in helpers:
                # A1: only a helper calls a helper, and only as CLOSURE lists (a game method calling PushModel or constructing a
                # context would give it a listener argument classify.py never matched): anything else stops the script.
                if caller not in helpers or short_name(callee) not in {c for c, _, _ in CLOSURE.get(short_name(caller), [])}:
                    raise SystemExit(f'{caller} -> {callee}: a call to a helper from outside CLOSURE: review the closure')
                continue
            if callee in found:
                continue
            found[callee] = fp
            todo.append(callee)
    return found


def helper_set(used_helpers, calls, listeners):
    """The keys of the helpers a dropped listener's iteration calls (its loop's, then theirs, transitively), recording where
    each gets the listener in `listeners` (key -> set of parameter names Harmony binds). The game methods they call are added
    by the caller (game_callees), with no listener (strict)."""
    todo = [(h, LOOP_LISTENER[h]) for h in used_helpers]
    keys, seen = set(), set()
    while todo:
        short, where = todo.pop()
        if (short, where) in seen:
            continue
        seen.add((short, where))
        if short not in calls:
            raise SystemExit(f'{short}: used by a hook but absent from this version\'s helper profile')
        key, callees = calls[short]
        keys.add(key)
        listeners.setdefault(key, set()).add(listener_parameter(key, where))
        todo += callees
    return sorted(keys)


rows, profiles, labels, sets, version_helpers = [], [], [], [], []
listeners = {}
for label, root, fps_path in VERSIONS:
    load(root, fps_path)
    missing = [h for h in REQUIRED_HELPERS if h not in helpers]
    if missing:
        raise SystemExit(f'{label}: {fps_path} has no Helper line for {missing}: add them to HookHelpers in tools/fpdiff/Program.cs')
    defaults = parse_defaults()
    safe, unsafe = classify()
    labels.append(label)
    check_never_taken()
    check_helper_edges()
    # The profile pins the helpers, every game method they may call (Calls lines) and the chain getters (Getter lines).
    version_helpers.append(set(helpers))
    profile = dict(helpers)
    # Compiler-generated callees (lambdas, '<' in the key) aren't pinned on their own: their code is in their caller's
    # fingerprint (MethodFingerprint includes the generated code a method uses, numbering normalized), and their names change
    # with any renumbering, which would leave every hook of the profile vanilla for nothing. They stay in the sets (patches
    # checked; HookSkip backs off when such a key no longer resolves).
    profile.update({k: v for k, v in game_callees([k for k, fp in helpers.items() if fp != '-']).items() if '<' not in k})
    # The chain getters only where this version has a hook passing that chain (C: a getter pinned for a version that never
    # relies on it would leave its hooks vanilla for nothing when it changes).
    profile.update({g: fp for g, fp in getters.items() if any(g in chains for *_, chains in safe)})
    # A2 (verification audit of 2026-11-17): the implementers each virtual or interface call of a pinned method reaches, and the
    # virtual/final flag of each pinned method (HookSkip recomputes both at load: a new implementer, or a method turning
    # virtual, leaves the profile's hooks vanilla). Generated callers aren't pinned (their calls are in their caller's code).
    pinned = [k for k in profile if k in flags and '<' not in k]
    profile.update({f'virtual:{k}': flags[k] for k in pinned})
    profile.update({f'dispatch:{caller}|{callee}': targets for (caller, callee), targets in dispatch.items() if caller in pinned})
    profiles.append(profile)
    calls = helper_closure()
    print(f'{label}: SAFE {len(safe)}  UNSAFE {len(unsafe)}')
    for n, why in unsafe:
        print('  -', n, ':', why[:110])
    for name, fp, methods, subscribed, contexts, used_helpers, chains in safe:
        pins = []
        for meth in methods:
            if len(default_fps.get(meth, [])) != 1:
                raise SystemExit(f'{label}: {meth} has no single AbstractModel default fingerprint')
            pins.append(f'"{meth}={default_fps[meth][0]}"')
        for chain in chains:
            if chain not in getters or getters[chain] == '-':
                raise SystemExit(f'{label}: {name} passes {chain} to its listeners: add it to ChainGetters in tools/fpdiff/Program.cs')
        # A hook passing a reviewed chain relies on its getters too: the profile pins them (one is needed even without contexts).
        profile = len(profiles) - 1 if contexts or chains else -1
        keys = helper_set(used_helpers, calls, listeners) if contexts else []
        if contexts and not keys:
            raise SystemExit(f'{label}: {name} uses choice contexts but no helper was recorded')
        # What a dropped listener's iteration also calls: the helpers' game callees and the chain getters, strictly (no listener).
        keys = sorted(set(keys) | set(game_callees(keys)) | set(chains))
        if keys and keys not in sets:
            sets.append(keys)
        index = sets.index(keys) if keys else -1
        row = f'        new("{name}", "{fp}", {"true" if subscribed else "false"}, {profile}, {index}, {", ".join(pins)}),'
        if row not in rows:
            rows.append(row)
# The helpers of every version, in each profile ("-" where a version lacks one: checked absent, as its code was classified);
# the game methods they call and the chain getters only in the profiles of the versions that call them (a version not
# calling one doesn't rely on it, and may have it: a "-" there would leave its hooks vanilla for nothing).
helper_keys = set().union(*(set(k for k in profile if k in profile_helpers) for profile, profile_helpers in zip(profiles, version_helpers)))
profile_rows = []
for label, profile in zip(labels, profiles):
    keys = sorted(helper_keys | set(profile))
    items = ', '.join(f'["{k}"] = "{profile.get(k, "-")}"' for k in keys)
    profile_rows.append(f'        // {label}\n        new() {{ {items} }},')
# One place per helper: a helper getting the listener in two places (or in one call and not in another) has none ("").
listener_rows = ', '.join(f'["{k}"] = "{next(iter(v)) if len(v) == 1 else ""}"' for k, v in sorted(listeners.items()))
set_rows = '\n'.join('        new[] { ' + ', '.join(f'"{k}"' for k in s) + ' },' for s in sets)
open(TABLE, 'w', encoding='utf-8', newline='\n').write(
    f'// Generated by tools/hookskip/classify.py from the decompiled game ({", ".join(labels)}): do not edit by hand.\n'
    'using System.Collections.Generic;\n\n'
    'namespace BigDeckOptimization.Patches;\n\n'
    'public static partial class HookSkip\n{\n'
    '    /// <summary>\n'
    '    /// Per classified game version: fingerprints of the helpers the hooks using choice contexts rely on, of every game method\n'
    "    /// those may call (fpdiff's call graph) and of the reviewed chains' getters (\"-\": absent).\n"
    '    /// </summary>\n'
    '    private static readonly Dictionary<string, string>[] HelperProfiles =\n    {\n' + '\n'.join(profile_rows) + '\n    };\n\n'
    '    /// <summary>The game version each helper profile was classified on (checked against the version the game declares).</summary>\n'
    '    private static readonly string[] HelperProfileVersions = { ' + ', '.join(f'"{label}"' for label in labels) + ' };\n\n'
    '    /// <summary>\n'
    "    /// The distinct sets of helpers a dropped listener's iteration calls (an entry's helper set): those its loops call, then\n"
    "    /// the ones those call (classify.py's CLOSURE, checked against each version's code), then every game method those may\n"
    "    /// call (fpdiff's call graph) and the reviewed chains' getters: those two have no listener argument (checked strictly).\n"
    "    /// A helper a game lacks isn't listed.\n"
    '    /// </summary>\n'
    '    private static readonly string[][] HelperSets =\n    {\n' + set_rows + '\n    };\n\n'
    '    /// <summary>\n'
    "    /// Per helper, the parameter holding the listener in every call a dropped listener's iteration makes to it, by the name\n"
    '    /// Harmony binds it ("__instance": the instance; "": none, so no patch there can be gated on the listener).\n'
    '    /// </summary>\n'
    '    private static readonly Dictionary<string, string> HelperListeners = new() { ' + listener_rows + ' };\n\n'
    '    /// <summary>\n'
    '    /// Hooks whose listener loops only do no-ops for a listener keeping the defaults of these methods, per classified\n'
    '    /// version: hook code fingerprint, ExecutionFinished kept, helper profile (-1: none used), helper set (-1: none),\n'
    '    /// "Method=default fingerprint".\n'
    '    /// </summary>\n'
    '    private static readonly Entry[] Hooks =\n    {\n' + '\n'.join(rows) + '\n    };\n}\n')
print('wrote', TABLE, len(rows), 'entries,', len(sets), 'helper sets')
