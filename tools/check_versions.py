"""Version check protocol: the game, BaseLib and Limitless versions installed (and BetterSpire2 and SpeedX, which
some patches are pinned to, when installed) vs the ones every optimization was last verified with (tools/versions.json),
and what a game log says was left vanilla or which of those mods it didn't load.

The game: versions.json's "game"/"game_assembly_hash" is the verified public version, "betas" the verified beta versions
(version -> assembly hash, tested on copies). The branch Steam has installed (the appmanifest's BetaKey) decides which
build it is compared with; a version/hash pair verified on either branch is not a change. "former_betas" keeps the pairs
of betas no longer distributed nor tested (v0.111.0 since 2026-11-16: the DLL keeps its code paths): a record only, never
read here, so a copy of one shows as changed; --accept leaves it as it is.

Also asks Steam what is PUBLISHED (not only what this machine downloaded): the build of the game's public and
public-beta branches (SteamCMD, anonymous login) and the last update of every watched mod's Workshop item (Steam Web
API). A published update players already get but this machine hasn't downloaded yet shows up here too, and --accept
refuses to record it as verified.

Usage (repo root, python -I):
  python -I tools/check_versions.py                  versions only (exit code 1 when one differs, 2 when the check
                                                     itself failed); with nothing changed and Steam reached, records
                                                     the date (LAST_CHECK)
  python -I tools/check_versions.py --local          without asking Steam
  python -I tools/check_versions.py --log <log>      also lists patches/hooks a session left vanilla (fingerprint
                                                     changed, patch skipped or failed, other mods patching a method)
                                                     and every error or warning line of the mod
  python -I tools/check_versions.py --accept         records the installed versions as verified (after the checks), with
                                                     the Workshop stamps and build of what is installed; refused (exit 1)
                                                     while a published update isn't downloaded here
      --branch public|beta                           with --accept, for a game copy outside Steam's library (no
                                                     appmanifest) whose version isn't verified yet: which branch it is
      --accept-other-branch                          with --accept, also records the build Steam publishes on the branch
                                                     not installed here (only once the protocol ran on a copy of it)

Game path: STS2_GAME_DIR, else the default Steam location (as tools/autotest/common.ps1).
"""
import datetime
import http.client
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


def steamapps() -> Path:
    """The steamapps folder holding the Workshop items and manifests: the game's own library, or, for a game copy outside
    Steam's library (STS2_GAME_DIR, e.g. C:\\Games\\StS2-beta-0.113.1), Steam's main one (as tools/autotest/common.ps1)."""
    library = GAME.parent.parent
    if (library / "workshop").exists():
        return library
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
        if (steam / "steamapps").exists():
            return steam / "steamapps"
    except (ImportError, OSError):
        pass
    return library


STEAMAPPS = steamapps()
WORKSHOP = STEAMAPPS / "workshop" / "content" / "2868840"
MODS = ("BaseLib", "Limitless")
# Other mods some patches are pinned to (fingerprints or allowed patches: ExtraScopes' BetterSpire2 scope, SpeedX's soul
# postfix): an update there leaves that part vanilla, so it is watched the same way. Unlike BaseLib and Limitless, one that
# isn't installed is not a change. Unlimited (sts2unlimited) is no longer watched (owner, 2026-11-20): no part relies on its
# code by name any more, its transpilers being judged by the code they produce (UnchangedTranspilers), like any other mod's.
EXTRA_MODS = ("BetterSpire2", "SpeedX")
WATCHED = MODS + EXTRA_MODS
APP_ID = "2868840"
# Published branches checked, and the versions.json key of their build.
BRANCH_KEYS = {"public": "game_buildid", "public-beta": "beta_buildid"}
STEAMCMD = Path(os.environ.get("STEAMCMD", r"C:\steamcmd\steamcmd.exe"))
# Last full check (Steam asked, every row checked) that found the verified versions: machine-local, so the daily task
# (tools/version_task.ps1) doesn't change a tracked file. versions.json's "verified" stays the date the patches were proven.
LAST_CHECK = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "BigDeckOptimization" / "last-check.json"

# Log lines of this mod saying a part of it runs the unmodified game (or another mod's version) instead: the phrases of the
# fallback messages in src/ (a new fallback message needs its phrase here). --log also lists every error and warning line
# of the mod, whatever its text (PROBLEM).
VANILLA = re.compile(r"\[BigDeckOptimization\].*(Patch \w+ skipped|could not be applied|changed \(fingerprint|left vanilla"
                     r"|missing or changed|getter kept|is also patched by|read through the game's own calls|read every time|not reused"
                     r"|not skipped|never skipped|left uncached|not found, it sets|stays? the game's|as the game does|(counted|written|read|serialized) by the game"
                     r"|stays? vanilla|falling back to|no longer shuffles|clone code changed|the game's \w+ changed|rebuilt at every clone"
                     r"|as Limitless does|stay registered|scope unavailable|scope not installed|postfixes changed|disabled)")
# Normal: an optional mod absent, a summary with nothing left vanilla, a scenario's `expect` line echoed by AutoTest (the
# command, then its EXPECT line).
EXPECTED = re.compile(r"(Limitless|BaseLib) not loaded|no --bdtest argument| 0 left vanilla|skipped: not needed"
                      r"|AutoTest\s+[\d.]+s\s+(?:> expect|EXPECT) ")
# Error and warning lines of this mod (a fault, a fallback at runtime): listed apart, whatever their text.
PROBLEM = re.compile(r"^\[(ERROR|WARN)\] \[BigDeckOptimization\]")

# Watched mods whose installed copy is in <game>/mods, overriding the Workshop item (whose update stamp then doesn't
# describe the installed version).
OVERRIDES = set()


def installed() -> dict:
    found = {"game": None, "game_assembly_hash": None}
    info = GAME / "release_info.json"
    if info.exists():
        data = json.loads(info.read_text(encoding="utf-8-sig"))
        found["game"] = data.get("version")
        found["game_assembly_hash"] = data.get("main_assembly_hash")
    for mod in WATCHED:
        found[mod] = None
    # Workshop items first, then <game>/mods (a local copy overrides the Workshop one in game). Sorted: with two copies the
    # answer is the same every time, and said.
    sources = {}
    for folder in (WORKSHOP, GAME / "mods"):
        if not folder.exists():
            continue
        for manifest in sorted(folder.rglob("*.json")):
            try:
                data = json.loads(manifest.read_text(encoding="utf-8-sig"))
            except (ValueError, OSError):
                continue
            if isinstance(data, dict) and data.get("id") in WATCHED:
                mod = data["id"]
                if mod in sources and found[mod] != data.get("version"):
                    print(f"note: two copies of {mod}: {sources[mod]} ({found[mod]}) and {manifest.parent} "
                          f"({data.get('version')}): the second one counts")
                found[mod] = data.get("version")
                sources[mod] = manifest.parent
                if folder != WORKSHOP:
                    OVERRIDES.add(mod)
    return found


def acf_value(text: str, key: str) -> str | None:
    m = re.search(rf'"{key}"\s+"([^"]*)"', text)
    return m.group(1) if m else None


def local_steam() -> dict:
    """Branch and build of the installed game and last update of each mod's Workshop item, as Steam downloaded them. The
    build goes under its branch's key (game_buildid for public, beta_buildid for public-beta)."""
    found = {"branch": None, "workshop_ids": {}}
    manifest = GAME.parent.parent / f"appmanifest_{APP_ID}.acf"
    if manifest.exists():
        text = manifest.read_text(encoding="utf-8", errors="replace")
        # The branch installed is MountedConfig's (UserConfig's is the one selected, which differs while Steam switches
        # branches); UserConfig only without a MountedConfig block. No BetaKey, or an empty one: the public branch.
        mounted = re.search(r'"MountedConfig"\s*\{([^}]*)\}', text)
        found["branch"] = acf_value(mounted.group(1) if mounted else text, "BetaKey") or "public"
        key = BRANCH_KEYS.get(found["branch"])
        if key:
            found[key] = acf_value(text, "buildid")
    acf = STEAMAPPS / "workshop" / f"appworkshop_{APP_ID}.acf"
    acf_text = acf.read_text(encoding="utf-8", errors="replace") if acf.exists() else ""
    for item in sorted(WORKSHOP.iterdir()) if WORKSHOP.exists() else []:
        for manifest in item.rglob("*.json"):
            try:
                data = json.loads(manifest.read_text(encoding="utf-8-sig"))
            except (ValueError, OSError):
                continue
            if isinstance(data, dict) and data.get("id") in WATCHED:
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
            for branch, key in BRANCH_KEYS.items():
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
        except (OSError, ValueError, KeyError, TypeError, http.client.HTTPException) as e:
            print(f"Steam Web API failed: {e}")
    return found


def when(stamp) -> str:
    return datetime.datetime.fromtimestamp(int(stamp)).strftime("%Y-%m-%d %H:%M") if stamp and str(stamp).isdigit() else str(stamp)


def verified_game(known: dict, version, assembly_hash) -> str | None:
    """Where this game version/hash pair was verified: 'public', 'beta', or None."""
    if version is None:
        return None
    if known.get("game") == version and known.get("game_assembly_hash") == assembly_hash:
        return "public"
    if (known.get("betas") or {}).get(version, object()) == assembly_hash:
        return "beta"
    return None


def accept(current: dict, known: dict, steam: dict, remote: dict, args: list) -> int:
    """Records what is installed as verified. Refused while a published update isn't downloaded here: its publish stamp
    would be recorded next to the older installed version, and from then on look verified. Refused too for a game copy
    outside Steam's library (no appmanifest, so no branch) whose version isn't already verified, unless --branch says
    which branch it is."""
    branch = steam.get("branch")
    if "--branch" in args:
        i = args.index("--branch")
        given = args[i + 1] if i + 1 < len(args) else None
        if given not in ("public", "beta"):
            print("NOT RECORDED: --branch takes public or beta")
            return 1
        if branch is not None and (given == "public") != (branch == "public"):
            print(f"NOT RECORDED: --branch {given}, but Steam has the {branch} branch installed")
            return 1
        branch = branch or ("public" if given == "public" else "public-beta")
    if branch is None:
        # A copy (STS2_GAME_DIR): its branch is only known when its version/hash pair is already verified on one.
        where = verified_game(known, current["game"], current["game_assembly_hash"])
        if where is None:
            print(f"NOT RECORDED: {GAME} is not in Steam's library (no appmanifest), so its branch is unknown: say which one "
                  f"{current['game']} is, --branch public or --branch beta")
            return 1
        branch = "public" if where == "public" else "public-beta"
    pending = []
    for name, key in BRANCH_KEYS.items():
        if remote.get(key) and steam.get(key) and remote[key] != steam[key]:
            pending.append(f"game ({name}): build {remote[key]} published, {steam[key]} installed here")
    for mod in WATCHED:
        key = mod + "_updated"
        if remote.get(key) and steam.get(key) and remote[key] != steam[key]:
            pending.append(f"{mod}: update of {when(remote[key])} published, {when(steam[key])} downloaded here")
    if pending:
        print("NOT RECORDED: a published update isn't downloaded here, so the installed version isn't the published one:")
        for line in pending:
            print("  " + line)
        print("Let Steam download it (or check the copy tested), run the protocol on it, then --accept again.")
        return 1
    record = dict(known)
    if branch == "public":
        record["game"], record["game_assembly_hash"] = current["game"], current["game_assembly_hash"]
    elif current["game"] is not None:
        # Steam on a beta: the public version stays the one verified; this one joins the verified betas.
        record["betas"] = dict(record.get("betas") or {}, **{current["game"]: current["game_assembly_hash"]})
        print(f"{'Steam is on ' + branch if steam.get('branch') else 'A copy on a beta'}: {current['game']} recorded as a verified "
              f"beta, the public game stays {known.get('game')}")
    for mod in WATCHED:
        # An extra mod not installed here keeps its verified version (and stamp): not installed is not a change.
        if current[mod] is not None or mod in MODS:
            record[mod] = current[mod]
    for name, key in BRANCH_KEYS.items():
        # The build of the installed branch. The other branch has no local manifest: its verified build is kept, so a new
        # build there (a beta going public) stays flagged in the published table, unless --accept-other-branch says the
        # protocol ran on a copy of the build Steam publishes there now.
        if steam.get(key):
            record[key] = steam[key]
        elif remote.get(key) and remote[key] != known.get(key):
            if "--accept-other-branch" in args:
                record[key] = remote[key]
                print(f"note: {name} build {remote[key]} recorded from Steam's published build (--accept-other-branch): the "
                      f"copy tested must be that build")
            else:
                print(f"note: {name} build {remote[key]} is published, {known.get(key)} verified: not recorded (not installed "
                      f"here). Once the protocol ran on a copy of that build, --accept --accept-other-branch.")
    for mod in WATCHED:
        key = mod + "_updated"
        if mod in OVERRIDES:
            print(f"note: {mod} is installed from <game>/mods: its Workshop stamp doesn't describe that copy")
        if current[mod] is not None or mod in MODS:
            record[key] = steam.get(key) or remote.get(key) or known.get(key)
    record["verified"] = datetime.date.today().isoformat()
    # "verified" last, as before.
    record = {k: v for k, v in record.items() if k != "verified"} | {"verified": record["verified"]}
    KNOWN.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(f"recorded as verified: {record}")
    return 0


def main() -> int:
    args = sys.argv[1:]
    log = None
    if "--log" in args:
        i = args.index("--log")
        if i + 1 >= len(args) or not Path(args[i + 1]).is_file():
            print(f"check failed: --log needs a log file ({args[i + 1] if i + 1 < len(args) else 'none given'})")
            return 2
        log = Path(args[i + 1])
    current = installed()
    known = json.loads(KNOWN.read_text(encoding="utf-8")) if KNOWN.exists() else {}
    steam = local_steam()
    remote = {} if "--local" in args else remote_steam(steam["workshop_ids"])
    if "--accept" in args:
        return accept(current, known, steam, remote, args)
    branch = steam.get("branch") or "public"
    changed, unchecked = [], []
    print(f"{'':20} {'verified':>14} {'installed':>14}")
    where = verified_game(known, current["game"], current["game_assembly_hash"])
    betas = known.get("betas") or {}
    for key in ("game", "game_assembly_hash"):
        # Steam on a beta: compared with the verified beta of that version, if any (else '-': none verified).
        if branch == "public" or where == "public":
            v = known.get(key)
        elif current["game"] in betas:
            v = current["game"] if key == "game" else betas[current["game"]]
        else:
            v = "-"
        mark = ""
        if where is None:
            mark = "   <-- CHANGED"
            changed.append(key)
        elif where == "beta" and branch == "public":
            mark = "   (verified as a beta)"
        label = f"game ({branch})"[:20] if key == "game" and branch != "public" else key
        print(f"{label:20} {str(v):>14} {str(current.get(key)):>14}{mark}")
    for key in WATCHED:
        mark = "" if known.get(key) == current.get(key) else "   <-- CHANGED"
        if key in EXTRA_MODS and current.get(key) is None:
            mark = "   (not installed)"
        elif mark:
            changed.append(key)
        print(f"{key:20} {str(known.get(key)):>14} {str(current.get(key)):>14}{mark}")
    if branch != "public":
        print(f"Steam is on the {branch} branch (verified public game: {known.get('game')}; verified betas: "
              f"{', '.join(known.get('betas') or {}) or 'none'})")
    try:
        last_check = json.loads(LAST_CHECK.read_text(encoding="utf-8")).get("checked", "never")
    except (OSError, ValueError, AttributeError):
        last_check = "never"
    print(f"last verified (patches proven): {known.get('verified', 'never')}; last checked unchanged: {last_check}")
    if "--local" not in args:
        print()
        print(f"{'published on Steam':20} {'verified':>16} {'published':>16} {'downloaded here':>16}")
        rows = [("game (public)", "game_buildid", False), ("game (public-beta)", "beta_buildid", False)]
        rows += [(m, m + "_updated", True) for m in MODS]
        rows += [(m, m + "_updated", True) for m in EXTRA_MODS if m in steam["workshop_ids"]]
        for label, key, stamp in rows:
            fmt = when if stamp else str
            # The local build is only under its own branch's key (local_steam).
            v, r, l = known.get(key), remote.get(key), steam.get(key)
            notes = []
            if r is None:
                notes.append("not checked")
                unchecked.append(key)
            elif v != r:
                notes.append("<-- PUBLISHED UPDATE NOT VERIFIED")
                changed.append(key)
            if r is not None and l is not None and l != r:
                notes.append("(not downloaded here yet)")
            if stamp and key[:-len("_updated")] in OVERRIDES:
                notes.append("(installed from <game>/mods, not this item)")
            print(f"{label:20} {fmt(v):>16} {fmt(r):>16} {fmt(l) if l else '-':>16}  {' '.join(notes)}")
    if log is not None:
        text = log.read_text(encoding="utf-8", errors="replace")
        # A patch waiting for an optional mod is skipped first, then applied when that mod loads.
        applied = set(re.findall(r"\[BigDeckOptimization\] Patch (\w+) applied", text))
        def left_vanilla(line):
            skipped = re.search(r"Patch (\w+) skipped", line)
            return VANILLA.search(line) and not EXPECTED.search(line) and not (skipped and skipped.group(1) in applied)
        lines = [line.strip() for line in text.splitlines() if left_vanilla(line)]
        print(f"\n{log.name}: {len(lines)} part(s) left vanilla")
        for line in dict.fromkeys(lines):
            print("  " + line[:220])
        problems = [line.strip() for line in text.splitlines() if PROBLEM.search(line) and line.strip() not in lines]
        if problems:
            print(f"\n{log.name}: {len(problems)} other error or warning line(s) of the mod")
            for line in dict.fromkeys(problems):
                print("  " + line[:220])
        # "Limitless/BaseLib not loaded" above is expected only when the mod isn't there: one installed here but not loaded
        # in that session (disabled, failed, a duplicate, an older copy in <game>/mods) leaves what relies on it vanilla.
        loaded = set(re.findall(r"Finished mod initialization for '.*?' \(([^)]+)\)\.", text))
        failed = set(re.findall(r"Exception thrown while loading mod (\S+?):", text))
        missing = [m for m in WATCHED if current.get(m) is not None and m not in loaded]
        if loaded and missing:
            print(f"\ninstalled here but not loaded in {log.name}: "
                  + ", ".join(m + (" (failed to load)" if m in failed else "") for m in missing)
                  + " (fine if that session left it out on purpose, e.g. a test without the extra mods)")
    if changed:
        print("\nVersions changed: follow the version check protocol (docs/RELEASE.md, 'Version check protocol') before relying on the optimizations.")
        return 1
    if "--local" not in args and not unchecked:
        LAST_CHECK.parent.mkdir(parents=True, exist_ok=True)
        stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
        LAST_CHECK.write_text(json.dumps({"checked": stamp, "versions": {k: known.get(k) for k in sorted(known)}}, indent=2) + "\n",
                              encoding="utf-8")
        print(f"recorded as last checked unchanged: {stamp}")
    elif unchecked:
        print(f"\nNOT CHECKED: {', '.join(unchecked)} (Steam not reached): a published update there would go unseen.")
    return 0


if __name__ == "__main__":
    # A console code page without a character in a name or a log line prints it escaped instead of failing the check.
    sys.stdout.reconfigure(errors="backslashreplace")
    try:
        sys.exit(main())
    except Exception as e:  # noqa: BLE001 - a crash must not read as "versions changed" (exit 1) in version_task.ps1
        print(f"check failed: {type(e).__name__}: {e}")
        sys.exit(2)
