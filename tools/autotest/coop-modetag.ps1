# CoopModeTag: co-op players must all have the mod on, or all off (DESIGN "Co-op with a player without the mod", the CoopModeTag
# row). Two local co-op sessions through coop.ps1 (its test slot 70: your own settings are never touched), the mod turned off
# from the launch (a scenario's 'launchoff' line: --bdoff, before the main menu's --fastmp lobby builds the join lists):
#   1. coopmixed: the host on, the client off: the game's join check refuses the client, its detail lines naming this mod's
#      "+off" entry on the client's side and the host's untagged entry on the other. The client's error text explained
#      (JoinMismatchNote "off-you": its mod off, the host's on); the host's failed-join message keeps the game's text.
#   2. coopbothoff: both off: they join, and the join list each game compares says "+off" (coopmodetag lines).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/coop-modetag.ps1
# Exit code 1 when the mixed pair was not refused, the off pair did not join, or coop.ps1 found a problem in a log.
. (Join-Path $PSScriptRoot "common.ps1")
$bad = 0
# The games running before (none, or coop.ps1 refuses): the wait below is for the mixed pair's own games only.
$before = @(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
# Closed once the client's error text is built (JoinMismatchNote's line, or its fault line), a moment after the game's refusal.
& (Join-Path $PSScriptRoot "coop.ps1") -Name coopmixed -TimeoutSec 150 -StopWhen "JoinMismatchNote: (join refused|mod mismatch text left)"
if ($LASTEXITCODE) { $bad++ }
$clientLog = Join-Path $Results "coopmixed-client.log"
$hostLog = Join-Path $Results "coopmixed-host.log"
# The game's own detail lines of the refusal (v0.107.1 JoinFlow: "Mods that we have that host doesn't: ...", "Mods that host has
# that we don't: ..."; v0.111+ HandshakeManager: "Mods that local has that remote does not: ...", "Mods that remote has that local
# does not: ..."), not any line: the client's own COOPMODETAG line names the '+off' entry too. The '+off' entry on the client's
# side, the host's untagged one on the other: the mismatch is this mod's tag, not another difference between the copies.
$refused = (Test-Path $clientLog) -and (Select-String -Path $clientLog -Pattern "Mismatch in gameplay-relevant mods" -Quiet) `
    -and (Select-String -Path $clientLog -Pattern "Mods that (we have that host doesn't|local has that remote does not): .*BigDeckOptimization-v[0-9.]+\+off" -Quiet) `
    -and (Select-String -Path $clientLog -Pattern "Mods that (host has that we don't|remote has that local does not): .*BigDeckOptimization-v[0-9.]+(,|$)" -Quiet)
if ($refused) { Write-Output "ok      coop-modetag: the client with the mod off was refused by the host with it on (the game's refusal names its '+off' entry and the host's)" }
else { Write-Output "PROBLEM coop-modetag: the client with the mod off was not refused, or the game's refusal doesn't name its '+off' entry against the host's"; $bad++ }
# The explanation added to the game's error text (JoinMismatchNote's log line, with the text it added): the client's mod off
# against the host's on. The joining player only: the host's failed-join message keeps the game's text (no such line there).
$explained = (Test-Path $clientLog) -and (Select-String -Path $clientLog -Pattern "JoinMismatchNote: join refused over this mod's entry \(off-you, client: BigDeckOptimization-v[0-9.]+\+off vs BigDeckOptimization-v[0-9.]+\)" -Quiet)
if ($explained) { Write-Output "ok      coop-modetag: the client's error text explains it (its mod off, the host's on)" }
else { Write-Output "PROBLEM coop-modetag: no JoinMismatchNote off-you explanation in the client's error text"; $bad++ }
if ((Test-Path $hostLog) -and (Select-String -Path $hostLog -Pattern "JoinMismatchNote: join refused" -Quiet)) {
    Write-Output "PROBLEM coop-modetag: JoinMismatchNote explained the host's failed-join message (the joining player only)"; $bad++
}
else { Write-Output "ok      coop-modetag: the host's failed-join message keeps the game's text" }
# The mixed pair's games were just closed: coop.ps1 refuses to start while one is still exiting. Only the games that run
# started are waited for (not a game the player opened meanwhile), 60 s at most; one still there is said, and the off pair isn't
# run (coop.ps1 would refuse it, and an earlier session's logs would be judged instead).
# Asked again each time by name, as coop.ps1's own check does (a process object can say it has exited while Windows still lists it).
function Get-StartedGames { @(Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | ForEach-Object { $_.Id }) }
$waitUntil = (Get-Date).AddSeconds(60)
while ((Get-StartedGames).Count -and (Get-Date) -lt $waitUntil) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 1
$left = Get-StartedGames
if ($left.Count) {
    Write-Output "PROBLEM coop-modetag: the mixed pair's game(s) $($left -join ', ') still running 60 s after its session ended: the off pair not run"
    exit 1
}
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
