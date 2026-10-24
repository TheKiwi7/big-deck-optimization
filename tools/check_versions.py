"""Version check protocol: the game, BaseLib and Limitless versions installed vs the ones every optimization was last
verified with (tools/versions.json), and what a game log says was left vanilla.

Usage (repo root, python -I):
  python -I tools/check_versions.py                  versions only (exit code 1 when one differs)
  python -I tools/check_versions.py --log <log>      also lists patches/hooks a session left vanilla (fingerprint
                                                     changed, patch skipped or failed, other mods patching a method)
  python -I tools/check_versions.py --accept         records the installed versions as verified (after the checks)

Game path: STS2_GAME_DIR, else the default Steam location (as tools/autotest/common.ps1).
"""
import datetime
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KNOWN = ROOT / "tools" / "versions.json"
GAME = Path(os.environ.get("STS2_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"))
WORKSHOP = GAME.parent.parent / "workshop" / "content" / "2868840"
MODS = ("BaseLib", "Limitless")

# Log lines of this mod saying a part of it runs the unmodified game (or another mod's version) instead.
VANILLA = re.compile(r"\[BigDeckOptimization\].*(Patch \w+ skipped|could not be applied|changed \(fingerprint|left vanilla"
                     r"|missing or changed|getter kept|is also patched by)")
# Normal: an optional mod absent, a summary with nothing left vanilla.
EXPECTED = re.compile(r"(Limitless|BaseLib) not loaded|no --bdtest argument| 0 left vanilla")


def installed() -> dict:
    found = {"game": None, "game_assembly_hash": None}
    info = GAME / "release_info.json"
    if info.exists():
        data = json.loads(info.read_text(encoding="utf-8-sig"))
        found["game"] = data.get("version")
        found["game_assembly_hash"] = data.get("main_assembly_hash")
    for mod in MODS:
        found[mod] = None
    # Workshop items first, then <game>/mods (a local copy overrides the Workshop one in game).
    for folder in (WORKSHOP, GAME / "mods"):
        if not folder.exists():
            continue
        for manifest in folder.rglob("*.json"):
            try:
                data = json.loads(manifest.read_text(encoding="utf-8-sig"))
            except (ValueError, OSError):
                continue
            if isinstance(data, dict) and data.get("id") in MODS:
                found[data["id"]] = data.get("version")
    return found


def main() -> int:
    args = sys.argv[1:]
    current = installed()
    known = json.loads(KNOWN.read_text(encoding="utf-8")) if KNOWN.exists() else {}
    if "--accept" in args:
        current["verified"] = datetime.date.today().isoformat()
        KNOWN.write_text(json.dumps(current, indent=2) + "\n", encoding="utf-8")
        print(f"recorded as verified: {current}")
        return 0
    changed = []
    print(f"{'':20} {'verified':>14} {'installed':>14}")
    for key in ("game", "game_assembly_hash") + MODS:
        mark = "" if known.get(key) == current.get(key) else "   <-- CHANGED"
        if mark:
            changed.append(key)
        print(f"{key:20} {str(known.get(key)):>14} {str(current.get(key)):>14}{mark}")
    print(f"last verified: {known.get('verified', 'never')}")
    if "--log" in args:
        log = Path(args[args.index("--log") + 1])
        lines = [line.strip() for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                 if VANILLA.search(line) and not EXPECTED.search(line)]
        print(f"\n{log.name}: {len(lines)} part(s) left vanilla")
        for line in dict.fromkeys(lines):
            print("  " + line[:220])
    if changed:
        print("\nVersions changed: follow the version check protocol (CLAUDE.md) before relying on the optimizations.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
