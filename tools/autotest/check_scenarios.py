"""Every scenario line is a command the runner knows: an unknown one now stops the session, better found here.

    python -I tools/autotest/check_scenarios.py
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
BENCH = os.path.join(ROOT, '..', '..', 'src', 'BigDeckOptimization', 'Bench')


def main():
    known = set()
    for path in glob.glob(os.path.join(BENCH, 'AutoTest*.cs')):
        known |= set(re.findall(r'case "(\w+)":', open(path, encoding='utf-8').read()))
    bad = 0
    scenarios = glob.glob(os.path.join(ROOT, 'scenarios', '*.txt'))
    for path in scenarios:
        for number, line in enumerate(open(path, encoding='utf-8'), 1):
            line = line.strip()
            if not line or line.startswith('#'):
                continue
            command = line.split(' ', 1)[0].lower()
            if command not in known:
                print(f'{os.path.basename(path)}:{number}: unknown command {command}')
                bad += 1
    print(f'{len(scenarios)} scenarios, {len(known)} commands, {bad} unknown')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
