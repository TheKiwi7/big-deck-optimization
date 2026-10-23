# Settings check (BaseLib's mod configuration): settings-menu captures the menu and turns an option off (saved in
# mod_configs), settings-menu-2 checks it is applied after a restart, turns it back on and captures the mods screen.
# Your BigDeckOptimization.cfg is restored byte for byte afterwards.
#   powershell -File tools/autotest/settings.ps1
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$config = Join-Path $UserData "mod_configs\BigDeckOptimization.cfg"
$backup = Join-Path $Results "BigDeckOptimization.cfg.backup"
$hadConfig = Test-Path $config
if ($hadConfig) { Copy-Item $config $backup -Force; $before = (Get-FileHash $config).Hash }
try {
    & (Join-Path $PSScriptRoot "run.ps1") -Name settings-menu -TimeoutSec 240
    if (Test-Path $config) { Write-Output "--- saved after settings-menu:"; Get-Content $config }
    & (Join-Path $PSScriptRoot "run.ps1") -Name settings-menu-2 -TimeoutSec 240
    if (Test-Path $config) { Write-Output "--- saved after settings-menu-2:"; Get-Content $config }
}
finally {
    if ($hadConfig) {
        Copy-Item $backup $config -Force
        Write-Output "BigDeckOptimization.cfg restored: $($before -eq (Get-FileHash $config).Hash)"
    }
    elseif (Test-Path $config) { Remove-Item $config; Write-Output "removed the test BigDeckOptimization.cfg" }
}
