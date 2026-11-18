"""Cases for shell-traps.py: python -I tools/hooks/test_shell_traps.py (exits 1 on a failed case)."""
import importlib.util
import os
import sys

spec = importlib.util.spec_from_file_location("shell_traps", os.path.join(os.path.dirname(os.path.abspath(__file__)), "shell-traps.py"))
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

BS = chr(92)
CASES = [
    # (tool, command, denied?)
    ("Bash", "python -I - <<'EOF'\nprint('a" + BS + "d')\nEOF", True),
    ("Bash", "python3 - << EOF\nx = r'C:" + BS + "Games'\nEOF", True),
    ("Bash", "cd x; python -I - <<'EOF'\nprint('fine, no backslash here')\nEOF", False),
    ("Bash", "git commit -q -F - <<'EOF'\nThe owner's decision, C:" + BS + "path in prose\nEOF", False),
    ("Bash", "cat > f.txt <<'EOF'\na" + BS + "b\nEOF", False),
    ("Bash", "echo done", False),
    ("PowerShell", "Set-Content tools" + BS + "autotest" + BS + "scenarios" + BS + "x.txt 'a'", True),
    ("PowerShell", "'x' | Out-File \"C:" + BS + "Users" + BS + "A" + BS + "Documents" + BS + "Github" + BS + "Big Deck Optimization" + BS + "CHANGELOG.md\"", True),
    ("PowerShell", "Set-Content \"$sp" + BS + "proof.ps1\" $s -Encoding utf8  # scratchpad", False),
    ("PowerShell", "Add-Content C:" + BS + "Users" + BS + "A" + BS + "AppData" + BS + "Local" + BS + "Temp" + BS + "claude" + BS + "x" + BS + "log.txt 'a'", False),
    ("PowerShell", "Get-Content src" + BS + "a.cs", False),
    ("PowerShell", "Add-Content DESIGN.md 'x'", True),
]

failed = 0
for tool, command, denied in CASES:
    got = mod.verdict(tool, command) is not None
    if got != denied:
        failed += 1
        print(f"FAIL {tool}: {command[:70]!r}: denied {got}, expected {denied}")
print(f"{len(CASES) - failed}/{len(CASES)} cases pass")
sys.exit(1 if failed else 0)
