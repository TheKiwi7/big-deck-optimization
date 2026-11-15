# Hands-on demo in YOUR game: deploys the Debug build into the Steam install (it then loads instead of your Workshop copy, until
# -Cleanup), starts the game through Steam on scenarios/try-it.txt (huge deck + relics on a new run in save profile 3, in memory
# only; the profile's saves go to Steam Cloud as usual) and leaves it to you. Every other test runs on a game copy: this one
# touches the Steam install on purpose, so it asks for -SteamInstall (tools/hooks/no-steam-deploy.py refuses it without).
#   powershell -File tools/autotest/try.ps1 -SteamInstall [-Scenario try-32k]
#   powershell -File tools/autotest/try.ps1 -Cleanup
param([switch]$Cleanup, [switch]$SteamInstall, [string]$Scenario = "try-it")
. (Join-Path $PSScriptRoot "common.ps1")
# common.ps1's $GameDir is STS2_GAME_DIR when set: this demo is about the Steam install, always.
$steamGame = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
$local = Join-Path $steamGame "mods\BigDeckOptimization"
if ($Cleanup) {
    Assert-GameNotRunning
    if (Test-Path $local) { Remove-Item $local -Recurse -Force; Write-Output "removed ${local}: the Workshop version loads again" }
    else { Write-Output "no local copy" }
    return
}
if (-not $SteamInstall) { throw "try.ps1 deploys the Debug build into the Steam install ($local), over your Workshop copy until -Cleanup: pass -SteamInstall to do it" }
Assert-GameNotRunning
$project = Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\BigDeckOptimization.csproj"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
# AllowSteamDeploy: the csproj refuses a deploy into the Steam install otherwise.
dotnet build $project -c Debug -nologo -v q "-p:ModDeployDir=$local" "-p:AllowSteamDeploy=true"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$script = Join-Path $Scenarios "$Scenario.txt"
Start-Process -FilePath $SteamExe -ArgumentList "-applaunch", $AppId, "--bdtest", "`"$script`""
Write-Output "Game starting: the run is set up in about a minute, then it's yours. Afterwards: try.ps1 -Cleanup"
