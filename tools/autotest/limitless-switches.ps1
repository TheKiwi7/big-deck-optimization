# Limitless-only switches (v0.4.0): two sessions with your mod list edited in settings.save, then restored byte for byte
# (your BigDeckOptimization.cfg too).
#   limitless-absent:  Limitless disabled: its switches show unticked (menuvalues) and bdopt says not applied.
#   limitless-present: Limitless enabled again: they show ticked and apply (the cfg kept the wanted state).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/limitless-switches.ps1
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$settings = Join-Path (Get-SteamAccountDir) "settings.save"
$backup = Join-Path $Results "settings.save.limitless-backup"
$config = Join-Path $UserData "mod_configs\BigDeckOptimization.cfg"
$configBackup = Join-Path $Results "BigDeckOptimization.cfg.limitless-backup"
Copy-Item $settings $backup -Force
$before = (Get-FileHash $settings).Hash
$hadConfig = Test-Path $config
if ($hadConfig) { Copy-Item $config $configBackup -Force; $configBefore = (Get-FileHash $config).Hash }

try {
    $data = Get-Content $settings -Raw | ConvertFrom-Json
    $data.mod_settings.mod_list | Where-Object { $_.id -eq "Limitless" } | ForEach-Object { $_.is_enabled = $false }
    # No BOM: the game's JSON reader expects plain UTF-8.
    [IO.File]::WriteAllText($settings, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
    & (Join-Path $PSScriptRoot "run.ps1") -Name limitless-absent -TimeoutSec 240 -KeepConfig
    if (Test-Path $config) { Write-Output "--- saved after limitless-absent:"; Get-Content $config }
    Copy-Item $backup $settings -Force
    & (Join-Path $PSScriptRoot "run.ps1") -Name limitless-present -TimeoutSec 240 -KeepConfig
    if (Test-Path $config) { Write-Output "--- saved after limitless-present:"; Get-Content $config }
}
finally {
    Copy-Item $backup $settings -Force
    Write-Output "settings.save restored: $($before -eq (Get-FileHash $settings).Hash)"
    if ($hadConfig) { Copy-Item $configBackup $config -Force; Write-Output "BigDeckOptimization.cfg restored: $($configBefore -eq (Get-FileHash $config).Hash)" }
    elseif (Test-Path $config) { Remove-Item $config }
}