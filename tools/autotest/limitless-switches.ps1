# Limitless-only switches (v0.4.0): two sessions with the mod list edited, on a game copy, in a test slot (your own settings
# are never touched).
#   limitless-absent:  Limitless disabled: its switches show unticked (menuvalues) and bdopt says not applied.
#   limitless-present: Limitless enabled again: they show ticked and apply (the cfg kept the wanted state).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/limitless-switches.ps1 -GameDir <copy>
param([string]$GameDir = "")
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
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

    Set-SlotModEnabled $slot "Limitless" $false
    & (Join-Path $PSScriptRoot "run.ps1") -Name limitless-absent -TimeoutSec 240 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }
    if (Test-Path $config) { Write-Output "--- saved after limitless-absent:"; Get-Content $config }

    Set-SlotModEnabled $slot "Limitless" $true
    & (Join-Path $PSScriptRoot "run.ps1") -Name limitless-present -TimeoutSec 240 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }
    if (Test-Path $config) { Write-Output "--- saved after limitless-present:"; Get-Content $config }
    Test-SessionLog "limitless-absent" "Finished mod initialization for 'Limitless'" $false "Limitless did not load"
    Test-SessionLog "limitless-absent" "MENU absent:.*LimitlessSaves=False" $true "its switches show unticked"
    Test-SessionLog "limitless-present" "Finished mod initialization for 'Limitless'" $true "Limitless loaded"
    Test-SessionLog "limitless-present" "MENU present:.*LimitlessSaves=True" $true "its switches show ticked: the cfg kept the wanted state"
}
finally { Unlock-Slot $KeptSlot }
# Exit code 1 on any PROBLEM line above or a session run.ps1 judged with a problem.
exit $(if ($script:SessionLogProblems -or $runProblems) { 1 } else { 0 })
