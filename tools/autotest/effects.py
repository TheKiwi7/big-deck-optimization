"""What each optimization really did in a session (AutoTest's "effects" command): applied is not enough.

    python -I tools/autotest/effects.py results/<a>.log                          lists them, exit 1 when one stayed at zero
    python -I tools/autotest/effects.py results/<a>.log --reference <b>.log --lost   the effects at zero in <a> but not in <b>
"""
import re
import sys

EFFECT = re.compile(r'EFFECT (\S+) (\d+)')
BALANCE = re.compile(r'BALANCE (\S+) (-?\d+)')


def read(path):
    with open(path, encoding='utf-8', errors='replace') as f:
        return {m.group(1): int(m.group(2)) for m in map(EFFECT.search, f) if m}


def main(args):
    effects = read(args[0])
    if '--reference' in args:
        reference = read(args[args.index('--reference') + 1])
        for name, count in reference.items():
            if count > 0 and effects.get(name, 0) == 0:
                print(name)
        return 0
    if not effects:
        print('no EFFECT line: the session did not reach the effects command')
        return 1
    zero = [name for name, count in effects.items() if count == 0]
    with open(args[0], encoding='utf-8', errors='replace') as f:
        unbalanced = [(m.group(1), m.group(2)) for m in map(BALANCE.search, f) if m and m.group(2) != '0']
    for name, count in unbalanced:
        print(f'{name} is {count} with nothing running: something a patch opens is never closed')
    for name, count in effects.items():
        print(f'{name:<34}{count:>14}{"   <-- NEVER RAN" if count == 0 else ""}')
    print(f'{len(effects) - len(zero)}/{len(effects)} effects ran')
    return 1 if zero or unbalanced else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
