# Runs a sweep scenario (scenarios/<Name>.txt, with a 'sweep <kind> ...' line) to its end, however many sessions it takes:
# an item leaving the game stuck ends its session at once (AutoTest logs 'SWEEP ABORT next=<index>'), and a new session
# starts at the next item. All the sessions' logs end up in results/<Name>.log, in order.
#   powershell -File tools/autotest/sweep.ps1 -Name sweep-cards-2k-on [-GameDir <copy>] [-Start <index>] [-SessionTimeoutSec 1500] [-MaxSessions 30]
param([Parameter(Mandatory)][string]$Name, [string]$GameDir = "", [int]$Start = 0, [int]$SessionTimeoutSec = 1500, [int]$MaxSessions = 30)
$scenarios = Join-Path $PSScriptRoot "scenarios"
$results = Join-Path $PSScriptRoot "results"
$source = Get-Content (Join-Path $scenarios "$Name.txt")
$part = "$Name-part"
$partScript = Join-Path $scenarios "$part.txt"
$all = Join-Path $results "$Name.log"
$tagDir = if ($GameDir) { $GameDir } else { "$env:STS2_GAME_DIR" }
. (Join-Path $PSScriptRoot "common.ps1")
if (-not $tagDir) { $tagDir = $GameDir }
if ($tagDir -ne $GameDir -or $PSBoundParameters.ContainsKey("GameDir")) { $GameDir = $PSBoundParameters["GameDir"] }
Set-Content $all "" -Encoding utf8
$next = $Start
try {
    for ($session = 1; $session -le $MaxSessions; $session++) {
        $lines = $source | ForEach-Object { if ($_ -match '^sweep (\S+)(.*)$') { "sweep $($Matches[1]) $next$($Matches[2])" } else { $_ } }
        [IO.File]::WriteAllText($partScript, (($lines -join "`n") + "`n"))
        $run = @{ Name = $part; TimeoutSec = $SessionTimeoutSec }
        if ($GameDir) { $run.GameDir = $GameDir }
        & (Join-Path $PSScriptRoot "run.ps1") @run | Out-Null
        $log = Join-Path $results "$part.log"
        Get-Content $log | Add-Content $all -Encoding utf8
        $abort = Select-String -Path $log -Pattern "SWEEP ABORT next=(\d+)" | Select-Object -Last 1
        if (-not $abort) {
            $done = Select-String -Path $log -Pattern "SWEEP \S+ done" -Quiet
            Write-Output "$Name session $session from $next`: $(if ($done) { 'done' } else { 'ended without finishing (game closed or timeout)' })"
            break
        }
        $next = [int]$abort.Matches[0].Groups[1].Value
        Write-Output "$Name session $session stopped on a stuck item, next starts at $next"
    }
}
finally {
    Remove-Item $partScript -ErrorAction SilentlyContinue
    Save-VersionedLog $all $tagDir
}
