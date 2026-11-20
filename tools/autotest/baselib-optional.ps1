# BaseLib is optional (v0.2.3): two sessions with the mod list edited, on a game copy, in a test slot (your own settings are
# never touched).
#   baselib-late:   this mod moved before BaseLib in the load order (the settings menu registers when BaseLib loads).
#   baselib-absent: BaseLib disabled (every patch applies, bdopt works, no settings menu).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/baselib-optional.ps1 -GameDir <copy>
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
    Update-SlotModList $slot { param($mods) @($mods | Where-Object { $_.id -eq "BigDeckOptimization" }) + @($mods | Where-Object { $_.id -ne "BigDeckOptimization" }) }
    & (Join-Path $PSScriptRoot "run.ps1") -Name baselib-late -TimeoutSec 240 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }

    $slot = Sync-Slot $KeptSlot
    Set-SlotModEnabled $slot "BaseLib" $false
    & (Join-Path $PSScriptRoot "run.ps1") -Name baselib-absent -TimeoutSec 240 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }

    Test-SessionLog "baselib-late" "BaseLib not loaded \(yet\)" $true "this mod loaded before BaseLib"
    Test-SessionLog "baselib-late" "BaseLibSettings: registered" $true "the settings menu registered all the same"
    Test-SessionLog "baselib-late" "screenshot .*shot-settings-late" $true "the menu was captured"
    Test-SessionLog "baselib-absent" "Finished mod initialization for 'BaseLib'" $false "BaseLib did not load"
    Test-SessionLog "baselib-absent" "BaseLibSettings: registered" $false "no settings menu"
    Test-SessionLog "baselib-absent" "FAILED line" $false "bdopt worked"
    # HookSkip filters every hook of each supported version ("N hooks filtered, 0 left vanilla"): a count above 0 or a hook whose
    # code changed is a problem here, as any other part left vanilla or not applied.
    Test-SessionLog "baselib-absent" "[1-9]\d* left vanilla|Patch \w+ could not|HookSkip: \w+ code changed" $false "every patch that needs no other mod applied"
    # The closing line battery.ps1 requires (parlib.ps1's Write-SessionLogSummary): printed only when every check above ran.
    Write-SessionLogSummary $runProblems
}
finally { Unlock-Slot $KeptSlot }
# Exit code 1 on any PROBLEM line above or a session run.ps1 judged with a problem.
exit $(if ($script:SessionLogProblems -or $runProblems) { 1 } else { 0 })
