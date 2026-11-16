# CoopModeTag: co-op players must all have the mod on, or all off (DESIGN "Co-op with a player without the mod", the CoopModeTag
# row). Two local co-op sessions through coop.ps1 (its test slot 70: your own settings are never touched), the mod turned off
# from the launch (a scenario's 'launchoff' line: --bdoff, before the main menu's --fastmp lobby builds the join lists):
#   1. coopmixed: the host on, the client off: the game's join check refuses the client, naming this mod's "-off" entry.
#   2. coopbothoff: both off: they join, and the join list each game compares says "-off" (coopmodetag lines).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/coop-modetag.ps1
# Exit code 1 when the mixed pair was not refused, the off pair did not join, or coop.ps1 found a problem in a log.
. (Join-Path $PSScriptRoot "common.ps1")
$bad = 0
& (Join-Path $PSScriptRoot "coop.ps1") -Name coopmixed -TimeoutSec 150 -StopWhen "Mismatch in gameplay-relevant mods"
if ($LASTEXITCODE) { $bad++ }
$clientLog = Join-Path $Results "coopmixed-client.log"
$refused = (Test-Path $clientLog) -and (Select-String -Path $clientLog -Pattern "Mismatch in gameplay-relevant mods" -Quiet) `
    -and (Select-String -Path $clientLog -Pattern "BigDeckOptimization-v[0-9.]+-off" -Quiet)
if ($refused) { Write-Output "ok      coop-modetag: the client with the mod off was refused by the host with it on (its '-off' entry named)" }
else { Write-Output "PROBLEM coop-modetag: the client with the mod off was not refused, or the refusal doesn't name the '-off' entry"; $bad++ }
& (Join-Path $PSScriptRoot "coop.ps1") -Name coopbothoff -TimeoutSec 300
if ($LASTEXITCODE) { $bad++ }
$joined = $true
foreach ($role in "host", "client") {
    $log = Join-Path $Results "coopbothoff-$role.log"
    $tags = @(if (Test-Path $log) { Select-String -Path $log -Pattern "COOPMODETAG BigDeckOptimization-v[0-9.]+-off " })
    $run = (Test-Path $log) -and (Select-String -Path $log -Pattern "> waitfor run" -Quiet) -and -not (Select-String -Path $log -Pattern "FAILED line" -Quiet)
    if (-not $tags.Count -or -not $run) { $joined = $false; Write-Output "PROBLEM coop-modetag: $role of the off pair: $(if (-not $tags.Count) { 'no -off join list' } else { 'no run (a failed line)' })" }
}
if ($joined) { Write-Output "ok      coop-modetag: two players with the mod off joined, their join lists saying '-off'" } else { $bad++ }
exit $(if ($bad) { 1 } else { 0 })
