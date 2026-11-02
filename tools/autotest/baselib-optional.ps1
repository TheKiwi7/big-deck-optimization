# BaseLib is optional (v0.2.3): two sessions with the mod list edited, on a game copy, in a test slot (your own settings are
# never touched).
#   baselib-late:   this mod moved before BaseLib in the load order (the settings menu registers when BaseLib loads).
#   baselib-absent: BaseLib disabled (every patch applies, bdopt works, no settings menu).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/baselib-optional.ps1 -GameDir <copy>
param([string]$GameDir = "")
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
Assert-GameNotRunning

$slot = Sync-Slot $KeptSlot
Update-SlotModList $slot { param($mods) @($mods | Where-Object { $_.id -eq "BigDeckOptimization" }) + @($mods | Where-Object { $_.id -ne "BigDeckOptimization" }) }
& (Join-Path $PSScriptRoot "run.ps1") -Name baselib-late -TimeoutSec 240 -GameDir $GameDir -KeepSlot

$slot = Sync-Slot $KeptSlot
Set-SlotModEnabled $slot "BaseLib" $false
& (Join-Path $PSScriptRoot "run.ps1") -Name baselib-absent -TimeoutSec 240 -GameDir $GameDir -KeepSlot

Test-SessionLog "baselib-late" "BaseLib not loaded \(yet\)" $true "this mod loaded before BaseLib"
Test-SessionLog "baselib-late" "BaseLibSettings: registered" $true "the settings menu registered all the same"
Test-SessionLog "baselib-late" "screenshot .*shot-settings-late" $true "the menu was captured"
Test-SessionLog "baselib-absent" "Finished mod initialization for 'BaseLib'" $false "BaseLib did not load"
Test-SessionLog "baselib-absent" "BaseLibSettings: registered" $false "no settings menu"
Test-SessionLog "baselib-absent" "FAILED line" $false "bdopt worked"
Test-SessionLog "baselib-absent" "[1-9]\d* left vanilla|Patch \w+ could not" $false "every patch that needs no other mod applied"
