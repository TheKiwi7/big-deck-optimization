# A saved "Enabled": "False" while this mod's settings menu can't register (BaseLib installed, BigDeckOptimization.Menu.dll set
# aside for the session): the mod must start on anyway (only the menu can save "on" again), its patches applied. In a test slot:
# your own settings are never touched; the menu DLL is put back whatever happens.
#   powershell -File tools/autotest/started-off-nomenu.ps1 -GameDir <copy>
param([string]$GameDir = "")
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
Assert-GameNotRunning
$menuDll = Join-Path $GameDir "mods\BigDeckOptimization\BigDeckOptimization.Menu.dll"
$asideDll = "$menuDll.bdtest-aside"
if (-not (Test-Path $menuDll)) { throw "no menu DLL at ${menuDll}: deploy the Debug build first" }
# This process's kept slot (Lock-KeptSlot: the first free one of 80-89), held until the end (other series skip it; run.ps1
# -KeepSlot below holds the same one).
$runProblems = 0
$KeptSlot = Lock-KeptSlot
# Held while the menu DLL may be aside: another series' Restore-AsideMenu leaves it alone.
$asideLock = Lock-MenuAside $GameDir
try {
    $slot = Sync-Slot $KeptSlot
    $configDir = Join-Path $slot "SlayTheSpire2\mod_configs"
    New-Item -ItemType Directory -Force $configDir | Out-Null
    [IO.File]::WriteAllText((Join-Path $configDir "BigDeckOptimization.cfg"), "{`n  `"Enabled`": `"False`"`n}`n")
    Move-Item $menuDll $asideDll -Force
    try {
        & (Join-Path $PSScriptRoot "run.ps1") -Name started-off-nomenu -TimeoutSec 300 -GameDir $GameDir -KeepSlot
        if ($LASTEXITCODE) { $runProblems++ }
    }
    finally {
        Move-Item $asideDll $menuDll -Force
    }
    Test-SessionLog "started-off-nomenu" "turned off (in the settings|when the game started)" $false "the saved off was ignored (no menu to save on again)"
    Test-SessionLog "started-off-nomenu" "Patch (ListenerCache|HookSkip|FastListeners|PileIndex) applied" $true "the patches applied"
    Test-SessionLog "started-off-nomenu" "BaseLibSettings: registered" $false "the menu did not register (the case tested)"
    Test-SessionLog "started-off-nomenu" "CHECKSUM started-off-nomenu" $true "a fight played"
}
finally { Unlock-Slot $KeptSlot; $asideLock.Dispose() }
# Exit code 1 on any PROBLEM line above or a session run.ps1 judged with a problem.
exit $(if ($script:SessionLogProblems -or $runProblems) { 1 } else { 0 })
