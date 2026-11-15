"""Claude Code PreToolUse hook: refuses a build of the mod that would deploy to the Steam install.

The csproj deploys to the default game folder unless told otherwise, and a local copy there overrides the user's Workshop
version. Tests deploy to a game copy (-p:ModDeployDir=<copy>\\mods\\BigDeckOptimization), other builds use
-p:DeployToGame=false. tools/autotest/try.ps1, which deploys there on purpose, needs -SteamInstall. Also refused: a build naming
no project from inside the mod's folder (the hook's cwd), and a ModDeployDir given through a variable ($GameDir, %VAR%). Reads
the hook's JSON on stdin; prints a deny decision, or nothing.
"""
import json
import re
import sys

# try.ps1 run, not only named (cat, grep, git diff on it are fine): at a command's start, after a separator or the & call operator,
# or as powershell's script.
TRY_RUN = re.compile(r'(?:^|(?<!\\)[;|&(]|\b(?:powershell|pwsh)(?:\.exe)?\b[^\n;|]*?)\s*(?:&\s*)?(?:"[^"\n]*|\'[^\'\n]*|[\w:.\\/-]*)try\.ps1\b', re.I)


# A project or solution the command names: a build naming none builds the project of its working directory.
PROJECT_ARG = re.compile(r'\S+\.(csproj|sln|slnx|proj)\b', re.I)
# The mod's own projects, by path (or a cd into their folder in the same command).
MOD_PROJECT = re.compile(r'BigDeckOptimization(\.Menu)?\.csproj|src[\\/]BigDeckOptimization', re.I)
# The hook's working directory inside one of the mod's project folders (the shells keep it between commands).
MOD_FOLDER = re.compile(r'[\\/]src[\\/]BigDeckOptimization(\.Menu)?[\\/]', re.I)


def refusal(command, cwd=''):
    # tools/autotest/try.ps1 builds and deploys into the Steam install itself (a hands-on demo in the user's own game): only when
    # the user asked for that, said with -SteamInstall. -Cleanup only removes that copy.
    if TRY_RUN.search(command) and not re.search(r'[-/](SteamInstall|Cleanup)\b', command, re.I):
        return ('tools/autotest/try.ps1 deploys the Debug build into the Steam install, over the user\'s Workshop copy until '
                'try.ps1 -Cleanup. Only when the user asked for this hands-on demo, with -SteamInstall.')
    if not re.search(r'\bdotnet(\.exe)?"?\s+(build|msbuild|publish)\b', command, re.I):
        return None
    in_mod_folder = MOD_FOLDER.search(cwd.rstrip('\\/') + '\\') and not PROJECT_ARG.search(command)
    if not MOD_PROJECT.search(command) and not in_mod_folder:
        return None
    if re.search(r'[-/]p:DeployToGame=false', command, re.I):
        return None
    target = re.search(r'("?)[-/]p:ModDeployDir=("?)', command, re.I)
    if target:
        rest = command[target.end():]
        value = rest.split('"')[0] if target.group(1) or target.group(2) else (rest.split() or [''])[0]
        # A variable is expanded after this check sees it ($GameDir is the Steam install once common.ps1 is dot-sourced): only a
        # literal path is known not to be the Steam install.
        if re.search(r'[$%]', value):
            return ('-p:ModDeployDir is given through a variable, which may be the Steam install (common.ps1 sets $GameDir to it): '
                    'write the game copy\'s path, -p:ModDeployDir=<game copy>\\mods\\BigDeckOptimization.')
        if 'steamapps' not in value.lower():
            return None
    return ('This build would deploy to the Steam install, where a local copy overrides the user\'s Workshop version. '
            'Use -p:ModDeployDir=<game copy>\\mods\\BigDeckOptimization, or -p:DeployToGame=false to build only.')


def main():
    data = json.loads(sys.stdin.buffer.read().decode('utf-8-sig'))
    reason = refusal(str(data.get('tool_input', {}).get('command', '')), str(data.get('cwd') or ''))
    if reason:
        print(json.dumps({'hookSpecificOutput': {'hookEventName': 'PreToolUse', 'permissionDecision': 'deny', 'permissionDecisionReason': reason}}))
    return 0


if __name__ == '__main__':
    sys.exit(main())
