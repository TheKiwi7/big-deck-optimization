"""A/B comparison of two AutoTest logs (same script, one option differing).

    python -I tools/autotest/compare.py results/<a>.log results/<b>.log

Checks that the CHECKSUM, STATE and CARDPLAY lines match line for line (values only, not timings), then lists the
per-card times of 'playcard' ("played X in N ms") side by side.
"""
import re
import sys

CHECKED = re.compile(r'AutoTest\s+[\d.]+s\s+(CHECKSUM [^:]+: \w+|STATE .*$|CARDPLAY .*$)')
PLAYED = re.compile(r'AutoTest\s+[\d.]+s\s+played (\S+) in (\d+) ms')


def read(path):
    checked, played = [], []
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.rstrip('\n')
            if m := CHECKED.search(line):
                checked.append(m.group(1))
            elif m := PLAYED.search(line):
                played.append((m.group(1), int(m.group(2))))
    return checked, played


def main(a_path, b_path):
    a_checked, a_played = read(a_path)
    b_checked, b_played = read(b_path)
    mismatches = [(i, x, y) for i, (x, y) in enumerate(zip(a_checked, b_checked)) if x != y]
    print(f'checked lines: {len(a_checked)} vs {len(b_checked)}, mismatches: {len(mismatches)}')
    for i, x, y in mismatches[:20]:
        print(f'  #{i}\n    A: {x}\n    B: {y}')
    if len(a_checked) != len(b_checked):
        print('  DIFFERENT LENGTHS (a session stopped early?)')
    ok = not mismatches and len(a_checked) == len(b_checked)
    print('IDENTICAL' if ok else 'DIFFERENT')

    b_times = dict(b_played)
    print(f'\n{"card":<22}{"A ms":>9}{"B ms":>9}{"A/B":>7}')
    total_a = total_b = 0
    for card, ms in a_played:
        if card in b_times:
            other = b_times[card]
            total_a += ms
            total_b += other
            print(f'{card:<22}{ms:>9}{other:>9}{ms / max(other, 1):>7.1f}')
        else:
            print(f'{card:<22}{ms:>9}{"-":>9}')
    print(f'{"total (both)":<22}{total_a:>9}{total_b:>9}{total_a / max(total_b, 1):>7.1f}')
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], sys.argv[2]))
