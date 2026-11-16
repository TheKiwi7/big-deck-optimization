# CoopModeTag: co-op players must all have the mod on, or all off (DESIGN "Co-op with a player without the mod", the CoopModeTag
# row). Two local co-op sessions through coop.ps1 (its test slot 70: your own settings are never touched), the mod turned off
# from the launch (a scenario's 'launchoff' line: --bdoff, before the main menu's --fastmp lobby builds the join lists):
#   1. coopmixed: the host on, the client off: the game's join check refuses the client, its detail lines naming this mod's
#      "+off" entry on the client's side and the host's untagged entry on the other.
#   2. coopbothoff: both off: they join, and the join list each game compares says "+off" (coopmodetag lines).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/coop-modetag.ps1
# Exit code 1 when the mixed pair was not refused, the off pair did not join, or coop.ps1 found a problem in a log.
. (Join-Path $PSScriptRoot "common.ps1")
$bad = 0
& (Join-Path $PSScriptRoot "coop.ps1") -Name coopmixed -TimeoutSec 150 -StopWhen "Mismatch in gameplay-relevant mods"
if ($LASTEXITCODE) { $bad++ }
$clientLog = Join-Path $Results "coopmixed-client.log"
# The game's own detail lines of the refusal (v0.107.1 JoinFlow: "Mods that we have that host doesn't: ...", "Mods that host has
# that we don't: ..."; v0.111+ HandshakeManager: "Mods that local has that remote does not: ...", "Mods that remote has that local
# does not: ..."), not any line: the client's own COOPMODETAG line names the '+off' entry too. The '+off' entry on the client's
# side, the host's untagged one on the other: the mismatch is this mod's tag, not another difference between the copies.
$refused = (Test-Path $clientLog) -and (Select-String -Path $clientLog -Pattern "Mismatch in gameplay-relevant mods" -Quiet) `
    -and (Select-String -Path $clientLog -Pattern "Mods that (we have that host doesn't|local has that remote does not): .*BigDeckOptimization-v[0-9.]+\+off" -Quiet) `
    -and (Select-String -Path $clientLog -Pattern "Mods that (host has that we don't|remote has that local does not): .*BigDeckOptimization-v[0-9.]+(,|$)" -Quiet)
if ($refused) { Write-Output "ok      coop-modetag: the client with the mod off was refused by the host with it on (the game's refusal names its '+off' entry and the host's)" }
else { Write-Output "PROBLEM coop-modetag: the client with the mod off was not refused, or the game's refusal doesn't name its '+off' entry against the host's"; $bad++ }
# The mixed pair's games were just closed: coop.ps1 refuses to start while one is still exiting.
$waitUntil = (Get-Date).AddSeconds(60)
while ((Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $waitUntil) { Start-Sleep -Milliseconds 500 }
& (Join-Path $PSScriptRoot "coop.ps1") -Name coopbothoff -TimeoutSec 300
if ($LASTEXITCODE) { $bad++ }
$joined = $true
foreach ($role in "host", "client") {
    $log = Join-Path $Results "coopbothoff-$role.log"
    $tags = @(if (Test-Path $log) { Select-String -Path $log -Pattern "COOPMODETAG BigDeckOptimization-v[0-9.]+\+off " })
    $run = (Test-Path $log) -and (Select-String -Path $log -Pattern "> waitfor run" -Quiet) -and -not (Select-String -Path $log -Pattern "FAILED line" -Quiet)
    if (-not $tags.Count -or -not $run) { $joined = $false; Write-Output "PROBLEM coop-modetag: $role of the off pair: $(if (-not $tags.Count) { 'no +off join list' } else { 'no run (a failed line)' })" }
}
if ($joined) { Write-Output "ok      coop-modetag: two players with the mod off joined, their join lists saying '+off'" } else { $bad++ }
exit $(if ($bad) { 1 } else { 0 })
