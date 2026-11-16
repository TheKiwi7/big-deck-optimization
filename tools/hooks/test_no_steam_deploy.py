"""Cases for tools/hooks/no-steam-deploy.py: python -I tools/hooks/test_no_steam_deploy.py (exit 1 on a wrong answer)."""
import importlib.util
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("hook", Path(__file__).with_name("no-steam-deploy.py"))
hook = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hook)

P = r'"src\BigDeckOptimization\BigDeckOptimization.csproj"'
D = r'"C:\Program Files\dotnet\dotnet.exe"'
cases = [
    # (expected deny, command, cwd)
    (True, f'dotnet build {P} -c Release -p:DeployToGame=false; dotnet build {P} -c Debug', ''),
    (True, rf'dotnet build {P} -p:ModDeployDir=C:\Games\x -c Debug; dotnet build {P} -c Release', ''),
    (True, f'dotnet pack {P}', ''),
    (True, f'dotnet test {P}', ''),
    (True, f'dotnet build {P} -p:DeployToGame=falsey', ''),
    (True, r'MSBuild.exe src\BigDeckOptimization\BigDeckOptimization.csproj', ''),
    (True, f'dotnet build {P}', ''),
    (True, f'$env:PATH = "C:\\Program Files\\dotnet;$env:PATH"; dotnet build {P} -c Debug', ''),
    (True, f'$out = dotnet build {P} -c Debug 2>&1', ''),
    (True, f'cmd /c "dotnet build {P[1:-1]} -c Debug"', ''),
    (True, f'& {D} build {P}', ''),
    (True, 'dotnet build', r'C:\repo\src\BigDeckOptimization'),
    (True, 'cd src/BigDeckOptimization && dotnet build -c Debug', r'C:\repo'),
    (True, f'dotnet build {P} -p:ModDeployDir=$GameDir\\mods\\BigDeckOptimization', ''),
    (True, f'dotnet build {P} -p:GameDir=$GameDir', ''),
    (True, rf'dotnet build {P} "-p:ModDeployDir=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\BigDeckOptimization"', ''),
    (True, rf'dotnet build {P} -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"', ''),
    (True, r'powershell -File tools/autotest/try.ps1', ''),
    (True, r'.\tools\autotest\try.ps1', ''),
    (True, r'& "tools\autotest\try.ps1"', ''),
    (True, 'dotnet build -p Foo=Bar', r'C:\repo\src\BigDeckOptimization'),
    # A project through a variable, a cd inside a nested command, an escaped quote, try.ps1 inside ( ) or { }.
    (True, f'$p = {P}; dotnet build $p -c Debug', ''),
    (True, 'dotnet build @buildArgs', ''),
    (True, r'cmd /c "cd src\BigDeckOptimization && dotnet build -c Debug"', ''),
    (True, 'bash -c "cd src/BigDeckOptimization; dotnet build"', ''),
    (True, r'cmd /c "cd /d C:\repo\src\BigDeckOptimization && dotnet build"', ''),
    (True, 'git pull; if ($?) { cd src\\BigDeckOptimization; dotnet build -c Debug }', ''),
    (True, 'python -c "import subprocess; subprocess.run(\\"dotnet build src/BigDeckOptimization/BigDeckOptimization.csproj\\")"', ''),
    (True, r'(& tools\autotest\try.ps1)', ''),
    (True, r'git pull; if ($?) { .\tools\autotest\try.ps1 }', ''),
    (False, f'$p = {P}; dotnet build $p -c Debug -p:DeployToGame=false', ''),
    (False, rf'dotnet build {P} -c $cfg -p:ModDeployDir=C:\Games\x\mods\BigDeckOptimization', ''),
    (False, r'dotnet build tools\fpdiff\fpdiff.csproj -c $cfg', ''),
    (False, 'cmd /c "cd tools/fpdiff && dotnet build -c Release"', ''),
    (False, r'git pull; if ($?) { .\tools\autotest\try.ps1 -SteamInstall }', ''),
    (False, 'git commit -m "(fix try.ps1 docs)"', ''),
    # MSBuild takes the last value of a repeated property; AllowSteamDeploy=true is try.ps1 -SteamInstall's alone.
    (True, rf'dotnet build {P} -p:ModDeployDir=C:\Games\x "-p:ModDeployDir=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\BigDeckOptimization"', ''),
    (True, f'dotnet build {P} -p:DeployToGame=false -p:DeployToGame=true', ''),
    (True, rf'dotnet build {P} -p:ModDeployDir=C:\Games\x -p:AllowSteamDeploy=true', ''),
    (True, f'dotnet build {P} -p:DeployToGame=false -p:AllowSteamDeploy=true', ''),
    (False, f'dotnet build {P} -p:DeployToGame=true -p:DeployToGame=false', ''),
    (False, rf'dotnet build {P} "-p:ModDeployDir=C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\BigDeckOptimization" -p:ModDeployDir=C:\Games\x', ''),
    (False, f'dotnet build {P} -c Release -p:DeployToGame=false', ''),
    (False, f'dotnet build {P} -c Release -property:DeployToGame=false', ''),
    (False, f'dotnet build {P} "-p:Foo=1;DeployToGame=false"', ''),
    (False, rf'dotnet build {P} -c Debug -p:ModDeployDir=C:\Games\StS2-public-0.107.1\mods\BigDeckOptimization', ''),
    (False, rf'dotnet build {P} -c Debug "-p:ModDeployDir=C:\Games\StS2 beta\mods\BigDeckOptimization"', ''),
    (False, rf'dotnet build {P} -p:GameDir=C:\Games\StS2-public-0.107.1', ''),
    (False, f'$env:PATH = "C:\\Program Files\\dotnet;$env:PATH"; dotnet build {P} -c Debug -p:DeployToGame=false', ''),
    (False, f'dotnet build {P} -p:DeployToGame=false 2>&1 | Select-String error', ''),
    (False, 'dotnet run --project tools/compat -- src/BigDeckOptimization/bin/Debug/BigDeckOptimization.dll refs/v0.107.1', ''),
    (False, 'dotnet run --project tools/fpdiff -c Release -- review a b', r'C:\repo'),
    (False, r'dotnet build tools\fpdiff\fpdiff.csproj -c Release', ''),
    (False, "sed -n 1p 'x powershell tools/autotest/try.ps1'", ''),
    (False, 'python -I -c "print(\'powershell try.ps1\')"', ''),
    (False, 'cat tools/autotest/try.ps1', ''),
    (False, 'git diff tools/autotest/try.ps1', ''),
    (False, r'powershell -File tools/autotest/try.ps1 -SteamInstall', ''),
    (False, r'powershell -File tools/autotest/try.ps1 -Cleanup', ''),
    (False, 'dotnet-trace collect -p 123', ''),
    (False, 'dotnet --info', r'C:\repo\src\BigDeckOptimization'),
    (False, 'git status', r'C:\repo\src\BigDeckOptimization'),
    (False, 'dotnet build -p:DeployToGame=false', r'C:\repo\src\BigDeckOptimization'),
]
bad = 0
for deny, command, cwd in cases:
    got = hook.refusal(command, cwd) is not None
    if got != deny:
        bad += 1
        print(f"WRONG (expected {'deny' if deny else 'allow'}): {command}  [cwd {cwd!r}]")
print(f"{len(cases) - bad}/{len(cases)} as expected")
sys.exit(1 if bad else 0)
