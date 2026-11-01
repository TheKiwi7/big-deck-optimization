"""Claude Code PreToolUse hook: refuses a build of the mod that would deploy to the Steam install.

The csproj deploys to the default game folder unless told otherwise, and a local copy there overrides the user's Workshop
version. Tests deploy to a game copy (-p:ModDeployDir=<copy>\\mods\\BigDeckOptimization), other builds use
-p:DeployToGame=false. Reads the hook's JSON on stdin; prints a deny decision, or nothing.
"""
import json
import re
import sys


def refusal(command):
    if not re.search(r'\bdotnet(\.exe)?"?\s+(build|msbuild|publish)\b', command, re.I):
        return None
    if not re.search(r'BigDeckOptimization(\.Menu)?\.csproj|src[\\/]BigDeckOptimization', command, re.I):
        return None
    if re.search(r'[-/]p:DeployToGame=false', command, re.I):
        return None
    target = re.search(r'("?)[-/]p:ModDeployDir=("?)', command, re.I)
    if target:
        rest = command[target.end():]
        value = rest.split('"')[0] if target.group(1) or target.group(2) else (rest.split() or [''])[0]
        if 'steamapps' not in value.lower():
            return None
    return ('This build would deploy to the Steam install, where a local copy overrides the user\'s Workshop version. '
            'Use -p:ModDeployDir=<game copy>\\mods\\BigDeckOptimization, or -p:DeployToGame=false to build only.')


def main():
    data = json.loads(sys.stdin.buffer.read().decode('utf-8-sig'))
    reason = refusal(str(data.get('tool_input', {}).get('command', '')))
    if reason:
        print(json.dumps({'hookSpecificOutput': {'hookEventName': 'PreToolUse', 'permissionDecision': 'deny', 'permissionDecisionReason': reason}}))
    return 0


if __name__ == '__main__':
    sys.exit(main())
