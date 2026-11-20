# Settings check (BaseLib's mod configuration): settings-menu captures the menu and turns an option off (saved in
# mod_configs), settings-menu-2 checks it is applied after a restart, turns it back on and captures the mods screen.
# On a game copy, in a test slot: your own settings are never touched.
#   powershell -File tools/autotest/settings.ps1 -GameDir <copy>
param([string]$GameDir = "")
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
# An error nothing catches ends this script with exit code 1 (common.ps1's Write-RunnerError), never with its work half done.
trap { Write-RunnerError $_; exit 1 }
$GameDir = Resolve-GameCopy $copy
# Other games may run (par, the sweeps, the same script on the other copy): this one's sessions have their own kept slot and
# results\<version>\ logs, and time nothing.
# This process's kept slot (Lock-KeptSlot: the first free one of 80-89), held until the end (other series skip it; run.ps1
# -KeepSlot below holds the same one).
$runProblems = 0
$KeptSlot = Lock-KeptSlot
try {
    $slot = Sync-Slot $KeptSlot
    $config = Join-Path $slot "SlayTheSpire2\mod_configs\BigDeckOptimization.cfg"
    & (Join-Path $PSScriptRoot "run.ps1") -Name settings-menu -TimeoutSec 240 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }
    if (Test-Path $config) { Write-Output "--- saved after settings-menu:"; Get-Content $config }
    & (Join-Path $PSScriptRoot "run.ps1") -Name settings-menu-2 -TimeoutSec 240 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }
    if (Test-Path $config) { Write-Output "--- saved after settings-menu-2:"; Get-Content $config }
    Test-SessionLog "settings-menu" "screenshot .*shot-settings" $true "the menu was captured"
    Test-SessionLog "settings-menu-2" "Fast combat start \[fast_combat_start\]: OFF" $true "the option turned off stayed off after a restart"
    Test-SessionLog "settings-menu-2" "screenshot .*shot-mods" $true "the mods screen was captured"
    # The closing line battery.ps1 requires (parlib.ps1's Write-SessionLogSummary): printed only when every check above ran.
    Write-SessionLogSummary $runProblems
}
finally { Unlock-Slot $KeptSlot }
# Exit code 1 on any PROBLEM line above or a session run.ps1 judged with a problem.
exit $(if ($script:SessionLogProblems -or $runProblems) { 1 } else { 0 })
