# Co-op requires the mod (v0.4.0, affects_gameplay true): the local co-op client gets this mod disabled in its own mod
# list (coop.ps1 -ClientWithout, in coop.ps1's test slot: your own settings are never touched) and tries to join the modded host.
# Expected in results/coopreq-client.log: "Mismatch in gameplay-relevant mods" / "Mod mismatch", no lobby join.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/coop-required.ps1
. (Join-Path $PSScriptRoot "common.ps1")
# An older coop-required.ps1 edited your own default\1001\settings.save: put back what one stopped from outside left.
$clientSettings = Join-Path $UserData "default\1001\settings.save"
$backup = Join-Path $Results "settings.save.client-backup"
$marker = Join-Path $Results "coop-required.inprogress"
if ((Test-Path $marker) -and (Test-Path $backup)) {
    Copy-Item $backup $clientSettings -Force
    Remove-Item $marker
    Write-Output "an older test was interrupted: client settings.save restored"
}
# The client, without this mod, has no test runner to quit, nor to hide its window: headless, closed as soon as the refusal is logged.
& (Join-Path $PSScriptRoot "coop.ps1") -Name coopreq -WithMods -TimeoutSec 150 -ClientHeadless -ClientWithout BigDeckOptimization -StopWhen "Mismatch in gameplay-relevant mods"
$clientLog = Join-Path $Results "coopreq-client.log"
if ((Test-Path $clientLog) -and (Select-String -Path $clientLog -Pattern "Mismatch in gameplay-relevant mods|Reason: ModMismatch" -Quiet)) { Write-Output "ok      coop-required: the client without this mod was refused (ModMismatch)" }
else { Write-Output "PROBLEM coop-required: the client without this mod was not refused" }
