# Unattended singleplayer session through Steam: runs scenarios/<Name>.txt with the Debug build's AutoTest runner,
# optionally records a CPU trace, and copies the game log to results/<Name>.log.
#   powershell -File tools/autotest/run.ps1 -Name solo-8k [-TimeoutSec 900] [-TraceSec 60]
# A trace must end while the game still runs (method names are written at the end): give the scenario a final wait.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 900, [int]$TraceSec = 0)
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$script = Join-Path $Scenarios "$Name.txt"
if (-not (Test-Path $script)) { throw "No scenario $script" }
$log = Join-Path $UserData "logs\godot.log"
# A direct exe launch fails Steam initialization: go through Steam, which passes the extra arguments on.
Start-Process -FilePath $SteamExe -ArgumentList "-applaunch", $AppId, "--bdtest", "`"$script`""
$t = 0
while (-not (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep 2; $t += 2 }
$trace = $null
if ($TraceSec -gt 0 -and ($game = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue)) {
    Start-Sleep 8
    $span = [TimeSpan]::FromSeconds($TraceSec).ToString("dd\:hh\:mm\:ss")
    $trace = Start-Process -FilePath "dotnet-trace" -PassThru -WindowStyle Hidden -ArgumentList "collect", "-p", $game.Id,
        "--profile", "cpu-sampling", "--duration", $span, "-o", "`"$Results\$Name.nettrace`""
}
$t = 0
while ((Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and $t -lt $TimeoutSec) { Start-Sleep 5; $t += 5 }
if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Output "TIMEOUT after $t s: closing the game"
    Stop-Process -Name SlayTheSpire2 -Force
    Start-Sleep 3
}
if ($trace) { $trace.WaitForExit(60000) | Out-Null }
Copy-Item $log (Join-Path $Results "$Name.log") -Force
Write-Output "done in $t s: $Results\$Name.log"
