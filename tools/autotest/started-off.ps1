# The mod turned off when the game starts (saved "Enabled": "False", BaseLib installed): no patch applied, bdopt and the menu say so,
# a fight plays, and turning it back on only says "at the next start". In a test slot: your own settings are never touched.
#   powershell -File tools/autotest/started-off.ps1 -GameDir <copy>
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
    $configDir = Join-Path $slot "SlayTheSpire2\mod_configs"
    New-Item -ItemType Directory -Force $configDir | Out-Null
    [IO.File]::WriteAllText((Join-Path $configDir "BigDeckOptimization.cfg"), "{`n  `"Enabled`": `"False`"`n}`n")
    & (Join-Path $PSScriptRoot "run.ps1") -Name started-off -TimeoutSec 300 -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }
    Test-SessionLog "started-off" "Initialized v[\d.]+ in \d+ ms: turned off in the settings" $true "the mod said it was off at the start"
    Test-SessionLog "started-off" "Patch (ListenerCache|HookSkip|FastListeners|PileIndex) applied" $false "no patch of the mod applied"
    Test-SessionLog "started-off" "Patch JoinMismatchNote applied" $true "the join refusal's explanation applied all the same (as CoopModeTag)"
    Test-SessionLog "started-off" "JOINMSG all \d+ cases ok" $true "the join refusal's explanation works with the mod off at the start"
    Test-SessionLog "started-off" "inactive this session" $true "bdopt said inactive"
    Test-SessionLog "started-off" "CHECKSUM started-off" $true "a fight played"
    Test-SessionLog "started-off" "takes effect when you restart" $true "turning it on said: at the next start"
}
finally { Unlock-Slot $KeptSlot }
# Exit code 1 on any PROBLEM line above or a session run.ps1 judged with a problem.
exit $(if ($script:SessionLogProblems -or $runProblems) { 1 } else { 0 })
