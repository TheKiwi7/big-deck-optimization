"""Version check protocol: the game, BaseLib and Limitless versions installed vs the ones every optimization was last
verified with (tools/versions.json), and what a game log says was left vanilla.

Also asks Steam what is PUBLISHED (not only what this machine downloaded): the build of the game's public and
public-beta branches (SteamCMD, anonymous login) and the last update of the BaseLib and Limitless Workshop items (Steam
Web API). A published update players already get but this machine hasn't downloaded yet shows up here too.

Usage (repo root, python -I):
  python -I tools/check_versions.py                  versions only (exit code 1 when one differs)
  python -I tools/check_versions.py --local          without asking Steam
  python -I tools/check_versions.py --log <log>      also lists patches/hooks a session left vanilla (fingerprint
                                                     changed, patch skipped or failed, other mods patching a method)
  python -I tools/check_versions.py --accept         records the installed versions as verified (after the checks)

Game path: STS2_GAME_DIR, else the default Steam location (as tools/autotest/common.ps1).
"""
import datetime
import json
import os
import re
import subprocess
import sys
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KNOWN = ROOT / "tools" / "versions.json"
GAME = Path(os.environ.get("STS2_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"))
WORKSHOP = GAME.parent.parent / "workshop" / "content" / "2868840"
MODS = ("BaseLib", "Limitless")
APP_ID = "2868840"
STEAMCMD = Path(os.environ.get("STEAMCMD", r"C:\steamcmd\steamcmd.exe"))

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


def acf_value(text: str, key: str) -> str | None:
    m = re.search(rf'"{key}"\s+"([^"]*)"', text)
    return m.group(1) if m else None


def local_steam() -> dict:
    """Build of the installed game and last update of each mod's Workshop item, as Steam downloaded them."""
    found = {"game_buildid": None, "workshop_ids": {}}
    manifest = GAME.parent.parent / f"appmanifest_{APP_ID}.acf"
    if manifest.exists():
        found["game_buildid"] = acf_value(manifest.read_text(encoding="utf-8", errors="replace"), "buildid")
    acf = GAME.parent.parent / "workshop" / f"appworkshop_{APP_ID}.acf"
    acf_text = acf.read_text(encoding="utf-8", errors="replace") if acf.exists() else ""
    for item in WORKSHOP.iterdir() if WORKSHOP.exists() else []:
        for manifest in item.rglob("*.json"):
            try:
                data = json.loads(manifest.read_text(encoding="utf-8-sig"))
            except (ValueError, OSError):
                continue
            if isinstance(data, dict) and data.get("id") in MODS:
                found["workshop_ids"][data["id"]] = item.name
                block = re.search(rf'"{item.name}"\s*\{{([^}}]*)\}}', acf_text)
                found[data["id"] + "_updated"] = acf_value(block.group(1), "timeupdated") if block else None
    return found


def remote_steam(workshop_ids: dict) -> dict:
    """What Steam publishes now: public / public-beta builds and Workshop item update times. Missing on failure."""
    found = {}
    if STEAMCMD.exists():
        try:
            out = subprocess.run([str(STEAMCMD), "+login", "anonymous", "+app_info_update", "1", "+app_info_print", APP_ID, "+quit"],
                                 capture_output=True, text=True, timeout=180, errors="replace").stdout
            branches = out[out.find('"branches"'):]
            for branch, key in (("public", "game_buildid"), ("public-beta", "beta_buildid")):
                m = re.search(rf'"{branch}"\s*\{{[^}}]*?"buildid"\s+"(\d+)"', branches)
                if m:
                    found[key] = m.group(1)
        except (OSError, subprocess.SubprocessError) as e:
            print(f"SteamCMD failed: {e}")
    else:
        print(f"SteamCMD not found ({STEAMCMD}, or set STEAMCMD): game branches not checked")
    if workshop_ids:
        body = {"itemcount": str(len(workshop_ids))}
        for i, item in enumerate(workshop_ids.values()):
            body[f"publishedfileids[{i}]"] = item
        try:
            request = urllib.request.Request("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                                             data=urllib.parse.urlencode(body).encode())
            details = json.load(urllib.request.urlopen(request, timeout=30))["response"]["publishedfiledetails"]
            for mod, item in workshop_ids.items():
                for d in details:
                    if d.get("publishedfileid") == item and "time_updated" in d:
                        found[mod + "_updated"] = str(d["time_updated"])
        except (OSError, ValueError, KeyError) as e:
            print(f"Steam Web API failed: {e}")
    return found


def when(stamp) -> str:
    return datetime.datetime.fromtimestamp(int(stamp)).strftime("%Y-%m-%d %H:%M") if stamp and str(stamp).isdigit() else str(stamp)


def main() -> int:
    args = sys.argv[1:]
    current = installed()
    known = json.loads(KNOWN.read_text(encoding="utf-8")) if KNOWN.exists() else {}
    steam = local_steam()
    remote = {} if "--local" in args else remote_steam(steam["workshop_ids"])
    if "--accept" in args:
        # The verified builds: what Steam publishes now (the protocol verified both branches and the mods), else local.
        for key in ("game_buildid", "beta_buildid") + tuple(m + "_updated" for m in MODS):
            current[key] = remote.get(key) or steam.get(key) or known.get(key)
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
    if remote:
        print()
        print(f"{'published on Steam':20} {'verified':>16} {'published':>16} {'downloaded here':>16}")
        rows = [("game (public)", "game_buildid", False), ("game (public-beta)", "beta_buildid", False)]
        rows += [(m, m + "_updated", True) for m in MODS]
        for label, key, stamp in rows:
            fmt = when if stamp else str
            v, r, l = known.get(key), remote.get(key), steam.get(key) if key != "beta_buildid" else None
            notes = []
            if r is None:
                notes.append("not checked")
            elif v != r:
                notes.append("<-- PUBLISHED UPDATE NOT VERIFIED")
                changed.append(key)
            if r is not None and l is not None and l != r:
                notes.append("(not downloaded here yet)")
            print(f"{label:20} {fmt(v):>16} {fmt(r):>16} {fmt(l) if l else '-':>16}  {' '.join(notes)}")
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
