"""Claude Code PreToolUse hook: refuses two shell traps that cost many turns in this project's history.

1. Bash: a script for an interpreter (python, py, node, powershell, pwsh) fed through a heredoc (<< 'EOF' or << EOF) whose
   body holds a backslash: the Bash tool's quoting lost or doubled backslashes there (regex escapes, Windows paths, '\\'
   in replace calls), and a heredoc body with quotes broke the tool's own parsing. Write the script with the Write tool into
   the scratchpad, then run it (`python -I <file>`).
2. PowerShell: Set-Content, Add-Content or Out-File writing a file of this repository: Windows PowerShell 5.1 writes ANSI by
   default and a BOM with -Encoding utf8, which broke scenarios, the CHANGELOG and commit messages. Write repo files with the
   Write or Edit tool (or Python with encoding="utf-8").

Reads the hook's JSON on stdin; prints a deny decision, or nothing. Cases: python -I tools/hooks/test_shell_traps.py.
"""
import json
import re
import sys

INTERPRETER_HEREDOC = re.compile(r"\b(python3?|py|node|powershell|pwsh)(\.exe)?\b[^\n]*<<-?\s*(['\"]?)(\w+)\3", re.I)
WRITE_CMDLET = re.compile(r"\b(Set-Content|Add-Content|Out-File)\b", re.I)
# A target inside this repository: its folder name, or a repo-relative path to one of its tracked trees.
REPO_TARGET = re.compile(r"Big Deck Optimization[\\/]|(^|[\s'\"(=])\.?[\\/]?(src|tools|docs)[\\/][^\s'\"]+|"
                         r"(^|[\s'\"(=])(CHANGELOG|DESIGN|README|CLAUDE)\.md", re.I)
SCRATCH = re.compile(r"[\\/]Temp[\\/]claude[\\/]|scratchpad", re.I)


def heredoc_bodies(command):
    """Yields (interpreter heredoc start, body) for each interpreter heredoc in the command."""
    for m in INTERPRETER_HEREDOC.finditer(command):
        tag = m.group(4)
        rest = command[m.end():]
        nl = rest.find("\n")
        if nl < 0:
            continue
        body_and_after = rest[nl + 1:]
        end = re.search(r"^\s*" + re.escape(tag) + r"\s*$", body_and_after, re.M)
        body = body_and_after[:end.start()] if end else body_and_after
        yield m.group(0), body


def verdict(tool, command):
    if tool == "Bash":
        for start, body in heredoc_bodies(command):
            if "\\" in body:
                return ("A heredoc script with a backslash loses or doubles it through the Bash tool (" + start.strip()[:40] +
                        "...): write the script with the Write tool into the scratchpad, then run it (python -I <file>).")
    if tool == "PowerShell" and WRITE_CMDLET.search(command):
        for seg in re.split(r"[;|\n]", command):
            if WRITE_CMDLET.search(seg) and REPO_TARGET.search(seg) and not SCRATCH.search(seg):
                return ("Set-Content/Add-Content/Out-File on a repository file: PowerShell 5.1 writes ANSI or a BOM. "
                        "Use the Write or Edit tool (or Python with encoding='utf-8').")
    return None


def main():
    try:
        data = json.load(sys.stdin)
    except Exception:
        return
    tool = data.get("tool_name", "")
    command = (data.get("tool_input") or {}).get("command", "") or ""
    reason = verdict(tool, command)
    if reason:
        print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
                                                 "permissionDecisionReason": reason}}))


if __name__ == "__main__":
    main()
