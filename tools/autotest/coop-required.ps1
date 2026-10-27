# Co-op requires the mod (v0.4.0, affects_gameplay true): the local co-op client gets this mod disabled in its own mod
# list (user://default/1001/settings.save, restored byte for byte after) and tries to join the modded host.
# Expected in results/coopreq-client.log: "Mismatch in gameplay-relevant mods" / "Mod mismatch", no lobby join.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/coop-required.ps1
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$clientSettings = Join-Path $UserData "default\1001\settings.save"
if (-not (Test-Path $clientSettings)) { throw "No ${clientSettings} (run coop.ps1 once first)" }
$backup = Join-Path $Results "settings.save.client-backup"
# Present while the test runs: a test stopped from outside (its restore never ran) is restored by the next one.
$marker = Join-Path $Results "coop-required.inprogress"
if ((Test-Path $marker) -and (Test-Path $backup)) {
    Copy-Item $backup $clientSettings -Force
    Write-Output "the previous test was interrupted: client settings.save restored"
}
Copy-Item $clientSettings $backup -Force
$before = (Get-FileHash $clientSettings).Hash
Set-Content $marker "client settings.save backed up"
try {
    $data = Get-Content $clientSettings -Raw | ConvertFrom-Json
    $data.mod_settings.mod_list | Where-Object { $_.id -eq "BigDeckOptimization" } | ForEach-Object { $_.is_enabled = $false }
    # No BOM: the game's JSON reader expects plain UTF-8.
    [IO.File]::WriteAllText($clientSettings, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
    # The client, without this mod, can't hide its window: closed as soon as the refusal is logged.
    & (Join-Path $PSScriptRoot "coop.ps1") -Name coopreq -WithMods -TimeoutSec 150 -StopWhen "Mismatch in gameplay-relevant mods"
}
finally {
    Copy-Item $backup $clientSettings -Force
    Write-Output "client settings.save restored: $($before -eq (Get-FileHash $clientSettings).Hash)"
    Remove-Item $marker -ErrorAction SilentlyContinue
}