# Settings check (BaseLib's mod configuration): settings-menu captures the menu and turns an option off (saved in
# mod_configs), settings-menu-2 checks it is applied after a restart, turns it back on and captures the mods screen.
# On a game copy, in a test slot: your own settings are never touched.
#   powershell -File tools/autotest/settings.ps1 -GameDir <copy>
param([string]$GameDir = "")
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
Assert-GameNotRunning
$slot = Sync-Slot $KeptSlot
$config = Join-Path $slot "SlayTheSpire2\mod_configs\BigDeckOptimization.cfg"
& (Join-Path $PSScriptRoot "run.ps1") -Name settings-menu -TimeoutSec 240 -GameDir $GameDir -KeepSlot
if (Test-Path $config) { Write-Output "--- saved after settings-menu:"; Get-Content $config }
& (Join-Path $PSScriptRoot "run.ps1") -Name settings-menu-2 -TimeoutSec 240 -GameDir $GameDir -KeepSlot
if (Test-Path $config) { Write-Output "--- saved after settings-menu-2:"; Get-Content $config }
Test-SessionLog "settings-menu" "screenshot .*shot-settings" $true "the menu was captured"
Test-SessionLog "settings-menu-2" "Fast combat start \[fast_combat_start\]: OFF" $true "the option turned off stayed off after a restart"
Test-SessionLog "settings-menu-2" "screenshot .*shot-mods" $true "the mods screen was captured"
