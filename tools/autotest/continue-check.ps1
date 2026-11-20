# Save and continue in a new process (not the same session's tomenu/continuerun, whose statics stay): continue-save builds a
# run at ~945 cards with the reference run's stacked enchantments and leaves it saved; continue-load-on continues it in a fresh
# game (every kept list, memo and note of the mod starts empty, the cards come from the save's JSON) and plays a fight with
# effect checks; then the save is put back as session 1 left it and continue-load-off continues it with the mod off from its
# launch (launchoff). The two load sessions' CHECKSUM and STATE lines must match line for line (compare.py). All in the kept
# slot, held until the end: your own saves are never touched.
#   powershell -File tools/autotest/continue-check.ps1 -GameDir <copy> [-TimeoutSec 900]
param([string]$GameDir = "", [int]$TimeoutSec = 900)
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
# An error nothing catches ends this script with exit code 1 (common.ps1's Write-RunnerError), never with its work half done.
trap { Write-RunnerError $_; exit 1 }
$GameDir = Resolve-GameCopy $copy
Assert-GameNotRunning
$runProblems = 0
$KeptSlot = Lock-KeptSlot
try {
    $slot = Sync-Slot $KeptSlot
    $data = Join-Path $slot "SlayTheSpire2"
    $aside = Join-Path $Results "continue-check.saves"
    Remove-Item $aside -Recurse -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot "run.ps1") -Name continue-save -TimeoutSec $TimeoutSec -GameDir $GameDir -KeepSlot
    if ($LASTEXITCODE) { $runProblems++ }
    # The run session 1 left (current_run*, wherever the profile keeps it), set aside with its path under the slot's user data.
    $saves = @(Get-ChildItem $data -Recurse -File -Include current_run* -ErrorAction SilentlyContinue)
    if ($saves.Count -eq 0) {
        Write-Output "PROBLEM: continue-save left no current_run save in the slot: nothing to continue"
        $runProblems++
    }
    else {
        foreach ($file in $saves) {
            $target = Join-Path $aside $file.FullName.Substring($data.Length + 1)
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $file.FullName $target -Force
        }
        Write-Output "saved run set aside: $($saves.Count) file(s)"
        & (Join-Path $PSScriptRoot "run.ps1") -Name continue-load-on -TimeoutSec $TimeoutSec -GameDir $GameDir -KeepSlot
        if ($LASTEXITCODE) { $runProblems++ }
        # The same save for the mod-off twin: what session 2 wrote since (its own autosaves) replaced by session 1's files.
        Get-ChildItem $data -Recurse -File -Include current_run* -ErrorAction SilentlyContinue | Remove-Item -Force
        foreach ($file in Get-ChildItem $aside -Recurse -File) {
            $target = Join-Path $data $file.FullName.Substring($aside.Length + 1)
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $file.FullName $target -Force
        }
        & (Join-Path $PSScriptRoot "run.ps1") -Name continue-load-off -TimeoutSec $TimeoutSec -GameDir $GameDir -KeepSlot
        if ($LASTEXITCODE) { $runProblems++ }
        Test-SessionLog "continue-load-on" "CONTINUED in \d+ ms" $true "the mod-on session continued the saved run"
        Test-SessionLog "continue-load-off" "CONTINUED in \d+ ms" $true "the mod-off session continued the same saved run"
        Test-SessionLog "continue-load-off" "mod off from the launch \(--bdoff\)" $true "the mod-off session ran with the mod off"
        $on = Join-Path $Results "continue-load-on.log"
        $off = Join-Path $Results "continue-load-off.log"
        if ((Test-Path $on) -and (Test-Path $off)) {
            $verdict = @(python -I (Join-Path $PSScriptRoot "compare.py") $off $on --no-times)
            $verdict | Out-Host
            if (-not ($verdict -contains "IDENTICAL")) { Write-Output "PROBLEM: continue-load-on and continue-load-off differ"; $runProblems++ }
        }
    }
    # Nothing of these sessions stays for the next series in this slot (Sync-Slot drops runs in progress too).
    Get-ChildItem $data -Recurse -File -Include current_run* -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    # The closing line battery.ps1 requires (parlib.ps1's Write-SessionLogSummary): printed only when every check above ran.
    Write-SessionLogSummary $runProblems
}
finally { Unlock-Slot $KeptSlot }
# Exit code 1 on any PROBLEM line above or a session run.ps1 judged with a problem.
exit $(if ($script:SessionLogProblems -or $runProblems) { 1 } else { 0 })
