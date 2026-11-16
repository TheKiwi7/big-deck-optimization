"""Claude Code PreToolUse hook: refuses a build of the mod that would deploy to the Steam install.

The csproj deploys to the default game folder unless told otherwise, and a local copy there overrides the user's Workshop
version. Tests deploy to a game copy (-p:ModDeployDir=<copy>\\mods\\BigDeckOptimization, or -p:GameDir=<copy>), other builds
use -p:DeployToGame=false. A property given twice counts by its last value, as MSBuild takes it; -p:AllowSteamDeploy=true (which
lifts the csproj's own refusal) is refused. tools/autotest/try.ps1, which deploys there on purpose, needs -SteamInstall. Also refused: a build
naming no project from inside the mod's folder (the hook's cwd, or a cd earlier in the command), and a ModDeployDir or GameDir
given through a variable ($GameDir, %VAR%), and a project given through one ($p, %P%, @args): it may be the mod's.

Each command of a compound line (; && || | & and newlines, outside quotes) is judged on its own arguments: one build's
-p:DeployToGame=false does not cover the next one. Every dotnet verb that runs the Build target (build, msbuild, publish, pack,
test, run) and MSBuild(.exe) itself are judged, also inside another command ($x = dotnet build ..., cmd /c "...",
if ($?) { ... }), with a cd into the mod's folder anywhere before it in that command (cmd /c "cd ... && dotnet build").
try.ps1 is judged at the start of every command, also inside ( ) or { }. Reads the hook's JSON on stdin; prints a deny decision, or nothing. Cases: python -I tools/hooks/test_no_steam_deploy.py.
"""
import json
import re
import sys

# A project or solution the command names: a build naming none builds the project of its working directory.
PROJECT_ARG = re.compile(r'\.(csproj|sln|slnx|proj)"?$', re.I)
# The mod's own projects or their folders.
MOD_PROJECT = re.compile(r'BigDeckOptimization(\.Menu)?\.csproj"?$|src[\\/]BigDeckOptimization(\.Menu)?[\\/]?"?$', re.I)
# A working directory inside one of the mod's project folders (the shells keep it between commands).
MOD_FOLDER = re.compile(r'[\\/]src[\\/]BigDeckOptimization(\.Menu)?[\\/]', re.I)
DOTNET_BUILDS = re.compile(r'^(build|msbuild|publish|pack|test|run)$', re.I)
# Shells and launchers whose arguments are a command: try.ps1 named anywhere in them is run, not only read.
LAUNCHERS = re.compile(r'^(powershell|pwsh|cmd|bash|sh|start|start-process|invoke-expression|iex|invoke-command)(\.exe)?$', re.I)
CD = re.compile(r'^(cd|chdir|pushd|set-location|sl|push-location)$', re.I)
# -p:DeployToGame=false, -property:..., or one item of a ;-separated property list.
NO_DEPLOY = re.compile(r'(?:[-/]p(?:roperty)?:|;)\s*DeployToGame=false(?![\w])', re.I)
# A cd anywhere in a command, also inside a quoted or bracketed one (cmd /c "cd ... && ...", { cd ...; ... }): its path.
CD_ANY = re.compile(r'(?:^|[\s{(;&|"\'])(?:cd|chdir|pushd|set-location|sl|push-location)(?:\s+/d|\s+-(?:Literal)?Path)?\s+'
                    r'["\']?([^\s"\';|&(){}]+)', re.I)
# try.ps1 run at the start of a command inside ( ) or { } (if ($?) { .\tools\autotest\try.ps1 }), quoted text blanked first.
TRY_IN_BLOCK = re.compile(r'[{(]\s*(?:[&.]\s*)?[\w:.\\/-]*try\.ps1\b', re.I)


def segments(command):
    """The commands of a line, split on ; & | and newlines outside quotes (2>&1 and >& are redirections, not separators)."""
    parts, current, quote = [], [], None
    for i, ch in enumerate(command):
        if quote:
            current.append(ch)
            if ch == quote:
                quote = None
            continue
        if ch in '"\'':
            quote = ch
            current.append(ch)
            continue
        if ch == '&' and ((i > 0 and command[i - 1] == '>') or command[i + 1:i + 2] == '>'):
            current.append(ch)
            continue
        if ch in ';|&\n\r':
            parts.append(''.join(current))
            current = []
            continue
        current.append(ch)
    parts.append(''.join(current))
    return [p.strip() for p in parts if p.strip()]


def tokens(segment):
    """Words of one command, quotes kept on each word (a quoted path stays one word)."""
    return re.findall(r'(?:"[^"]*"|\'[^\']*\'|[^\s"\'])+', segment)


def bare(word):
    return word.strip('"\'')


def path_word(word):
    """A word as a path: quotes, escaped quotes and the brackets around it dropped (subprocess.run(\\"x.csproj\\"))."""
    return word.strip('"\'\\(){}`')


def program(words):
    """The command word, past PowerShell's call/dot-source operators and the brackets around it ('(', '{', ')', '}')."""
    i = 0
    while i < len(words) and bare(words[i]) in ('&', '.', '(', '{'):
        i += 1
    if i >= len(words):
        return '', []
    name = re.split(r'[\\/]', bare(words[i].lstrip('({')).rstrip(')}'))[-1]
    return name, words[i + 1:]


def try_refusal(segment, name, args):
    # tools/autotest/try.ps1 builds and deploys into the Steam install itself (a hands-on demo in the user's own game): only
    # when the user asked for that, said with -SteamInstall. -Cleanup only removes that copy. Run means: the command itself,
    # or named by a shell or launcher (cat, grep, sed, git on it are fine, even with 'powershell' in their quoted text).
    # Inside ( ) or { } too: (& tools\autotest\try.ps1), if ($?) { .\tools\autotest\try.ps1 }.
    unquoted = re.sub(r'"[^"]*"|\'[^\']*\'', '""', segment)
    runs = (re.fullmatch(r'try\.ps1', name, re.I) or (LAUNCHERS.match(name) and re.search(r'try\.ps1\b', segment, re.I))
            or TRY_IN_BLOCK.search(unquoted))
    if runs and not re.search(r'[-/](SteamInstall|Cleanup)\b', segment, re.I):
        return ('tools/autotest/try.ps1 deploys the Debug build into the Steam install, over the user\'s Workshop copy until '
                'try.ps1 -Cleanup. Only when the user asked for this hands-on demo, with -SteamInstall.')
    return None


def property_value(segment, prop):
    """The literal value of -p:<prop>=... / -property:<prop>=... in one command, or None: the last one given, as MSBuild takes."""
    found = list(re.finditer(rf'("?)[-/]p(?:roperty)?:(?:[^\s"\';]*;)*{prop}=("?)', segment, re.I))
    if not found:
        return None
    target = found[-1]
    rest = segment[target.end():]
    if target.group(1) or target.group(2):
        return rest.split('"')[0]
    return (re.split(r'[\s;]', rest) or [''])[0]


def build_refusal(segment, name, args, in_mod_folder):
    name = name.lower()
    if name in ('dotnet', 'dotnet.exe'):
        verb = next((bare(a) for a in args if not bare(a).startswith('-')), '')
        if not DOTNET_BUILDS.match(verb):
            return None
        # dotnet run: what follows -- goes to the program, not to MSBuild.
        build_args = args[:[bare(a) for a in args].index('--')] if '--' in [bare(a) for a in args] else args
    elif name in ('msbuild', 'msbuild.exe'):
        verb, build_args = '', args
    else:
        return None
    words = [path_word(a) for a in build_args]
    projects = [w for w in words if not w.startswith('-') and (PROJECT_ARG.search(w) or MOD_PROJECT.search(w))]
    # A project given through a variable ($p, %P%, @args splatting) may be the mod's: unknown counts as the mod. A word
    # right after a bare option (-c $cfg) is that option's value.
    unknown = any(re.search(r'[$%]|^@', w) and not w.startswith('-') and not (i and re.fullmatch(r'-[\w-]+', words[i - 1]))
                  for i, w in enumerate(words))
    # dotnet run/test --project <path> (dotnet run's -p <path> too; MSBuild's -p:x=y has a colon, not a space).
    for flag in ('--project', '-p') if verb.lower() == 'run' else ('--project',):
        if flag in words and words.index(flag) + 1 < len(words):
            projects.append(words[words.index(flag) + 1])
    projects += [w.split('=', 1)[1] for w in words if w.lower().startswith('--project=')]
    is_mod = unknown or (any(MOD_PROJECT.search(p) for p in projects) if projects else in_mod_folder)
    if not is_mod:
        return None
    # The csproj's own guard against a deploy into the Steam install is lifted by this: only try.ps1 -SteamInstall passes it.
    if (property_value(segment, 'AllowSteamDeploy') or '').lower() == 'true':
        return ('-p:AllowSteamDeploy=true lifts the csproj\'s refusal to deploy into the Steam install: only tools/autotest/try.ps1 '
                '-SteamInstall passes it (when the user asked for that demo).')
    # Off only when its last value is false (MSBuild takes the last of a repeated property).
    if NO_DEPLOY.search(segment) and (property_value(segment, 'DeployToGame') or '').lower() == 'false':
        return None
    for prop in ('ModDeployDir', 'GameDir'):
        value = property_value(segment, prop)
        if value is None:
            continue
        # A variable is expanded after this check sees it ($GameDir is the Steam install once common.ps1 is dot-sourced): only a
        # literal path is known not to be the Steam install.
        if re.search(r'[$%]', value):
            return (f'-p:{prop} is given through a variable, which may be the Steam install (common.ps1 sets $GameDir to it): '
                    'write the game copy\'s path, -p:ModDeployDir=<game copy>\\mods\\BigDeckOptimization.')
        if 'steamapps' in value.lower():
            break
        if prop == 'ModDeployDir' or property_value(segment, 'ModDeployDir') is None:
            return None
    return ('This build would deploy to the Steam install, where a local copy overrides the user\'s Workshop version. '
            'Use -p:ModDeployDir=<game copy>\\mods\\BigDeckOptimization, or -p:DeployToGame=false to build only.')


# A dotnet or MSBuild command inside another one ($x = dotnet build ..., cmd /c "...", Measure-Command { ... }).
EMBEDDED = re.compile(r'(?<![\w.-])(?:dotnet|msbuild)(?:\.exe)?(?=["\'\s)}]|$)', re.I)


def judge(command, in_mod_folder):
    for segment in segments(command):
        words = tokens(segment)
        name, args = program(words)
        # A cd into the mod's folder anywhere in this command (a nested one too) makes its builds naming no project build the
        # mod: refusing more is the safe side.
        in_mod_folder = in_mod_folder or any(in_folder(path_word(m.group(1))) or MOD_PROJECT.search(path_word(m.group(1)))
                                             for m in CD_ANY.finditer(segment))
        reason = try_refusal(segment, name, args)
        if reason:
            return reason, in_mod_folder
        if CD.match(name):
            # A cd into the mod's folder makes the next builds naming no project build the mod (kept once set: refusing
            # more is the safe side).
            in_mod_folder = in_mod_folder or any(in_folder(bare(a)) or MOD_PROJECT.search(bare(a))
                                                 for a in args)
            continue
        if name.lower() in ('dotnet', 'dotnet.exe', 'msbuild', 'msbuild.exe'):
            reason = build_refusal(segment, name, args, in_mod_folder)
        else:
            # Each embedded build judged from its own start (always a shorter text: the recursion ends).
            reason = next((r for m in EMBEDDED.finditer(segment) if m.start() > 0
                           for r in [judge(segment[m.start():], in_mod_folder)[0]] if r), None)
        if reason:
            return reason, in_mod_folder
    return None, in_mod_folder


def in_folder(path):
    return bool(MOD_FOLDER.search(path.rstrip('\\/') + '\\'))


def refusal(command, cwd=''):
    return judge(command, in_folder(cwd))[0]


def main():
    data = json.loads(sys.stdin.buffer.read().decode('utf-8-sig'))
    command = str(data.get('tool_input', {}).get('command', ''))
    try:
        reason = refusal(command, str(data.get('cwd') or ''))
    except Exception as e:  # noqa: BLE001 - a crashed hook lets the command through: refuse what could deploy instead
        reason = (f'tools/hooks/no-steam-deploy.py failed on this command ({type(e).__name__}: {e}): split it into simpler '
                  'commands') if re.search(r'dotnet|msbuild|try\.ps1', command, re.I) else None
    if reason:
        print(json.dumps({'hookSpecificOutput': {'hookEventName': 'PreToolUse', 'permissionDecision': 'deny', 'permissionDecisionReason': reason}}))
    return 0


if __name__ == '__main__':
    sys.exit(main())
