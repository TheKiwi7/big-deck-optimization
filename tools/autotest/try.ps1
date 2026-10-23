# Hands-on demo: deploys the Debug build, starts the game on scenarios/try-it.txt (huge deck + relics on a new run in
# save profile 3, in memory only) and leaves it to you. -Cleanup removes the local Debug copy afterwards, so the
# Workshop version loads again.
#   powershell -File tools/autotest/try.ps1
#   powershell -File tools/autotest/try.ps1 -Cleanup
param([switch]$Cleanup)
. (Join-Path $PSScriptRoot "common.ps1")
$local = Join-Path $GameDir "mods\BigDeckOptimization"
if ($Cleanup) {
    Assert-GameNotRunning
    if (Test-Path $local) { Remove-Item $local -Recurse -Force; Write-Output "removed ${local}: the Workshop version loads again" }
    else { Write-Output "no local copy" }
    return
}
Assert-GameNotRunning
$project = Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\BigDeckOptimization.csproj"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet build $project -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$script = Join-Path $Scenarios "try-it.txt"
Start-Process -FilePath $SteamExe -ArgumentList "-applaunch", $AppId, "--bdtest", "`"$script`""
Write-Output "Game starting: the run is set up in about a minute, then it's yours. Afterwards: try.ps1 -Cleanup"
