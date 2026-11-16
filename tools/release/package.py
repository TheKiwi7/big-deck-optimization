"""Builds the public (Release) mod and lays out what each distribution channel needs.

Usage (repo root):  python -I tools/release/package.py [--draft]

The package is built from the release commit: src/ must have no uncommitted change, since the DLL's embedded Source Link
names HEAD (stack traces from players resolve to that commit on GitHub). --draft builds anyway, to test the package on a
copy before the commit; package again once committed. The build is not incremental, compiles against
refs/v<min_game_version> (the manifest's oldest supported game, whatever Steam has installed), and the csproj <Version>
must match the manifest's version (the game logs the first, co-op compares the second).

Produces, under dist/ (gitignored):
  BigDeckOptimization-vX.Y.Z.zip     GitHub release / Nexus: a BigDeckOptimization/ folder to drop into <game>/mods/
  workshop/content/                  Steam Workshop item content (files at the root, like other STS2 mods)
  workshop/workshop_item.vdf         SteamCMD script: steamcmd +login <account> +workshop_build_item <this file> +quit

Release builds exclude the test tooling (bigdeck/bdperf console commands, AutoTest, PerfProbe); the script checks the DLL has none.
The build does not deploy to the game folder, so a Debug install used for testing is left alone.
"""
import json
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "BigDeckOptimization"
MANIFEST = PROJECT / "BigDeckOptimization.json"
OUTPUT = PROJECT / "bin" / "Release"
DIST = ROOT / "dist"
DOCS = ROOT / "docs"
APP_ID = "2868840"
# The .Menu DLL holds the settings menu (BaseLib, optional): built by the main project's BuildMenu target. PDBs are embedded in the DLLs.
FILES = ["BigDeckOptimization.dll", "BigDeckOptimization.json", "mod_image.jpg", "BigDeckOptimization.Menu.dll"]


def dotnet() -> str:
    default = Path(r"C:\Program Files\dotnet\dotnet.exe")
    return str(default) if default.exists() else "dotnet"


def check_sources(manifest: dict, draft: bool) -> Path:
    """Version in step, sources committed, reference assemblies there. Returns the reference assemblies' folder."""
    version = manifest["version"]
    csproj = (PROJECT / "BigDeckOptimization.csproj").read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", csproj)
    if not match or "v" + match.group(1).strip() != version:
        sys.exit(f"BigDeckOptimization.csproj <Version> {match.group(1) if match else '(none)'} doesn't match the manifest's {version}")
    refs = ROOT / "refs" / f"v{manifest.get('min_game_version', '')}"
    missing = [name for name in ("sts2.dll", "GodotSharp.dll", "0Harmony.dll") if not (refs / name).exists()]
    if missing:
        sys.exit(f"{refs} lacks {', '.join(missing)}: the release compiles against the oldest supported game "
                 f"(min_game_version), copy its assemblies there")
    status = subprocess.run(["git", "status", "--porcelain", "--", "src"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    if status.strip():
        head = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
        text = f"src/ has uncommitted changes: the DLL's Source Link would name {head}, whose sources differ:\n{status.rstrip()}"
        if not draft:
            sys.exit(text + "\nCommit first (on the user's word), or --draft to test the package on a copy.")
        print("DRAFT, not for publishing: " + text)
    return refs


def build(refs: Path) -> None:
    subprocess.run([dotnet(), "build", str(PROJECT / "BigDeckOptimization.csproj"), "-c", "Release", "--no-incremental",
                    "-p:DeployToGame=false", f"-p:GameDataDir={refs}", "-nologo", "-v", "q"], check=True)
    dll = (OUTPUT / "BigDeckOptimization.dll").read_bytes()
    # Test tooling (console commands, unattended test runner, profiler) must not ship.
    for marker in ("bdperf", "bigdeck", "--bdtest", "PerfProbe"):
        if marker.encode("utf-16-le") in dll or marker.encode() in dll:
            sys.exit(f"Release DLL still contains test tooling '{marker}': is Bench excluded from Release?")
    check_translations(dll)


def menu_keys() -> set:
    """Every key the settings menu asks Translations for: '<option>.title/.desc' of each option of Settings.Options and of
    the fast-start relics line, and every literal key of a Translations.Get call."""
    settings = (PROJECT / "Config" / "Settings.cs").read_text(encoding="utf-8")
    options = re.findall(r'\bnew\("([a-z_]+)",\s*"', settings)
    fast = re.search(r'FastStartRelicsKey\s*=\s*"([a-z_]+)"', settings)
    if len(options) < 10 or not fast:
        sys.exit("Settings.cs: options or FastStartRelicsKey not found (tools/release/package.py's menu_keys needs updating)")
    keys = {key + suffix for key in options + [fast.group(1)] for suffix in (".title", ".desc")}
    for file in (ROOT / "src").rglob("*.cs"):
        if "obj" in file.relative_to(ROOT).parts or "bin" in file.relative_to(ROOT).parts:
            continue
        keys |= set(re.findall(r'Translations\.Get\([^,()]*(?:\([^()]*\))?[^,()]*,\s*"([^"]+)"', file.read_text(encoding="utf-8")))
    return keys


def check_translations(dll: bytes) -> None:
    """Settings menu translations (Localization/<game language>.json): every key the menu asks for, the same keys in every
    file, no empty text, embedded in the DLL. A missing key would show English in that language without any error."""
    files = sorted((PROJECT / "Localization").glob("*.json"))
    if not files:
        sys.exit("No settings menu translations in src/BigDeckOptimization/Localization")
    needed = menu_keys()
    reference = None
    for file in files:
        texts = json.loads(file.read_text(encoding="utf-8"))
        if needed - set(texts):
            sys.exit(f"{file.name} lacks the menu's keys {sorted(needed - set(texts))}")
        reference = reference or (file.name, set(texts))
        if set(texts) != reference[1]:
            sys.exit(f"{file.name} and {reference[0]} have different keys: {sorted(set(texts) ^ reference[1])}")
        if any(not str(text).strip() for text in texts.values()):
            sys.exit(f"{file.name} has an empty text")
        if f"BigDeckOptimization.Localization.{file.stem}.json".encode() not in dll:
            sys.exit(f"{file.name} is not embedded in the Release DLL")


def changenote(version: str) -> str:
    """The CHANGELOG section of this version, without its heading. The Workshop change note is plain text (an HTML comment
    shows as is) and players read it: a section still holding a comment or a pending mark is refused, not cleaned."""
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(rf"^## {re.escape(version)}\b.*?$(.*?)(?=^## |\Z)", text, re.M | re.S)
    if not match:
        sys.exit(f"CHANGELOG.md has no '## {version}' section")
    section = match.group(1).strip()
    if "<!--" in section or re.search(r"\(Pending\b", section, re.I):
        sys.exit(f"CHANGELOG.md's '## {version}' section still has an HTML comment or a pending mark: remove it (docs/RELEASE.md)")
    return section


def vdf_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def workshop(version: str) -> None:
    folder = DIST / "workshop"
    content = folder / "content"
    content.mkdir(parents=True)
    for name in FILES:
        shutil.copy2(OUTPUT / name, content / name)

    id_file = DOCS / "workshop_id.txt"
    published_id = id_file.read_text(encoding="utf-8").strip() if id_file.exists() else "0"
    preview = next((p for p in (DOCS / "workshop_preview.png", DOCS / "workshop_preview.jpg") if p.exists()), None)
    fields = {
        "appid": APP_ID,
        "publishedfileid": published_id,
        "contentfolder": str(content),
        # SteamCMD ends a value at a double quote, escaped or not: v0.12.1's note was cut there.
        "changenote": f"{version}\n{changenote(version)}".replace('"', "'"),
    }
    if published_id == "0":
        # First upload: hidden until the page is checked; title/description are then edited on the page itself.
        fields["visibility"] = "2"
        fields["title"] = "Big Deck Optimization"
        fields["description"] = (DOCS / "workshop_description.bbcode").read_text(encoding="utf-8").strip()
    if preview:
        fields["previewfile"] = str(preview)
    else:
        print("warning: no docs/workshop_preview.png|jpg (under 1 MB): the item will have no preview image")
    lines = ['"workshopitem"', "{"] + [f"\t{vdf_string(k)}\t{vdf_string(v)}" for k, v in fields.items()] + ["}"]
    (folder / "workshop_item.vdf").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    version = manifest["version"]
    refs = check_sources(manifest, "--draft" in sys.argv[1:])
    if DIST.exists():
        shutil.rmtree(DIST)
    DIST.mkdir()
    build(refs)

    archive = DIST / f"BigDeckOptimization-{version}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
        for name in FILES:
            zf.write(OUTPUT / name, f"BigDeckOptimization/{name}")
    workshop(version)
    print(f"{archive.relative_to(ROOT)}\n{(DIST / 'workshop').relative_to(ROOT)}{Path('/')}")


if __name__ == "__main__":
    main()
