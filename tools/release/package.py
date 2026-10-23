"""Builds the public (Release) mod and lays out what each distribution channel needs.

Usage (repo root):  python -I tools/release/package.py

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
FILES = ["BigDeckOptimization.dll", "BigDeckOptimization.pdb", "BigDeckOptimization.json"]


def dotnet() -> str:
    default = Path(r"C:\Program Files\dotnet\dotnet.exe")
    return str(default) if default.exists() else "dotnet"


def build() -> None:
    subprocess.run([dotnet(), "build", str(PROJECT / "BigDeckOptimization.csproj"), "-c", "Release",
                    "-p:DeployToGame=false", "-nologo", "-v", "q"], check=True)
    dll = (OUTPUT / "BigDeckOptimization.dll").read_bytes()
    # Test tooling (console commands, unattended test runner, profiler) must not ship.
    for marker in ("bdperf", "bigdeck", "--bdtest", "PerfProbe"):
        if marker.encode("utf-16-le") in dll or marker.encode() in dll:
            sys.exit(f"Release DLL still contains test tooling '{marker}': is Bench excluded from Release?")


def changenote(version: str) -> str:
    """The CHANGELOG section of this version, without its heading."""
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(rf"^## {re.escape(version)}\b.*?$(.*?)(?=^## |\Z)", text, re.M | re.S)
    if not match:
        sys.exit(f"CHANGELOG.md has no '## {version}' section")
    return match.group(1).strip()


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
        "changenote": f"{version}\n{changenote(version)}",
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
    version = json.loads(MANIFEST.read_text(encoding="utf-8"))["version"]
    if DIST.exists():
        shutil.rmtree(DIST)
    DIST.mkdir()
    build()

    archive = DIST / f"BigDeckOptimization-{version}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zf:
        for name in FILES:
            zf.write(OUTPUT / name, f"BigDeckOptimization/{name}")
    workshop(version)
    print(f"{archive.relative_to(ROOT)}\n{(DIST / 'workshop').relative_to(ROOT)}{Path('/')}")


if __name__ == "__main__":
    main()
