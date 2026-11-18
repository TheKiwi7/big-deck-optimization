"""The environment facts a brief for another agent needs, read from the machine so they are never stale.

    python -I tools/env-facts.py [--games C:\\Games]

Prints: the supported game versions (tools/versions.json: the public `game` and the verified `betas`; former betas are not
tested), each game copy under --games (version, supported or not, its mods folders, whether its deployed DLL is the last Debug
build of this tree), the Workshop mods a battery may load (BDTEST_EXTRA_MODS, BDTEST_WORKSHOP), and the tool paths. Paste the output
into a brief (.claude/skills/agent-briefs).
"""
import hashlib
import json
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLLS = ('BigDeckOptimization.dll', 'BigDeckOptimization.Menu.dll')


def short_hash(path):
    try:
        with open(path, 'rb') as f:
            return hashlib.sha256(f.read()).hexdigest()[:12]
    except OSError:
        return None


def copy_version(folder):
    try:
        with open(os.path.join(folder, 'release_info.json'), encoding='utf-8-sig') as f:
            return json.load(f).get('version', '?')
    except (OSError, ValueError):
        return '?'


def main(argv):
    games = argv[argv.index('--games') + 1] if '--games' in argv else r'C:\Games'
    with open(os.path.join(ROOT, 'tools', 'versions.json'), encoding='utf-8') as f:
        versions = json.load(f)
    supported = [versions['game']] + sorted(versions.get('betas', {}))
    print(f'repo: {ROOT}')
    print(f'supported game versions: {", ".join(supported)} (public {versions["game"]}; betas {", ".join(sorted(versions.get("betas", {}))) or "none"}; '
          f'verified {versions.get("verified", "?")}). Former betas {", ".join(sorted(versions.get("former_betas", {}))) or "none"}: not tested.')
    print('verified mods: ' + ', '.join(f'{k} {versions[k]}' for k in ('BaseLib', 'Limitless', 'BetterSpire2', 'SpeedX', 'sts2unlimited') if k in versions))
    debug = os.path.join(ROOT, 'src', 'BigDeckOptimization', 'bin', 'Debug')
    built = {d: short_hash(os.path.join(debug, d)) for d in DLLS}
    print(f'last Debug build here: {", ".join(f"{d} {h or "missing"}" for d, h in built.items())}')
    if os.path.isdir(games):
        for name in sorted(os.listdir(games)):
            folder = os.path.join(games, name)
            if not os.path.exists(os.path.join(folder, 'SlayTheSpire2.exe')):
                continue
            version = copy_version(folder)
            mods = os.path.join(folder, 'mods')
            mod_dirs = sorted(os.listdir(mods)) if os.path.isdir(mods) else []
            deployed = {d: short_hash(os.path.join(mods, 'BigDeckOptimization', d)) for d in DLLS}
            state = 'has the last Debug build' if deployed == built else 'DLLs differ from the last Debug build'
            tag = 'supported' if version in supported else 'NOT tested (do not use for results)'
            test_mods = os.path.join(folder, 'mods_STEAMTEST')
            extra = f'; mods_STEAMTEST: {", ".join(sorted(os.listdir(test_mods)))}' if os.path.isdir(test_mods) else ''
            print(f'copy {folder}: {version}, {tag}; mods: {", ".join(mod_dirs) or "none"}; {state}{extra}')
    else:
        print(f'no game copies under {games}')
    print(f'BDTEST_EXTRA_MODS={os.environ.get("BDTEST_EXTRA_MODS", "(unset)")} (release set: SpeedX,ModConfig,sts2unlimited); '
          f'BDTEST_WORKSHOP={os.environ.get("BDTEST_WORKSHOP", "(unset)")}; STS2_GAME_DIR={os.environ.get("STS2_GAME_DIR", "(unset)")}')
    for tool in ('dotnet', 'python', 'git', 'powershell'):
        print(f'{tool}: {shutil.which(tool) or "not on PATH"}')
    print('slots: parallel sessions share slots 0..Jobs-1 (give every par the same -Jobs); never launch a game from a brief unless it says so.')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    main(sys.argv[1:])
