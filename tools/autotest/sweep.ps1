# Runs a sweep scenario (scenarios/<Name>.txt, with a 'sweep <kind> ...' line) to its end, however many sessions it takes:
# an item leaving the game stuck ends its session at once (AutoTest logs 'SWEEP ABORT next=<index>'), and a new session
# starts at the next item. All the sessions' logs end up in results/<Name>.log, in order.
#   powershell -File tools/autotest/sweep.ps1 -Name sweep-cards-2k-on [-GameDir <copy>] [-Start <index>] [-SessionTimeoutSec 1500] [-MaxSessions 30]
# The scenario's line: 'sweep <kind> [count] [name] [checksum] [state]', this script supplying the first index (-Start, then each
# restart's). A line with two numbers ('sweep relics 75 75 ...': first and count) keeps its range, -Start replacing its first. A
# restart sweeps what is left of the range, not a whole count more. A named item can't take a first index (the list is that item).
# Each session's problems are shown (run.ps1's judgment, but the stuck item that ended it); exit code 1 on any, or when the sweep
# didn't finish.
param([Parameter(Mandatory)][string]$Name, [string]$GameDir = "", [int]$Start = 0, [int]$SessionTimeoutSec = 1500, [int]$MaxSessions = 30)
$scenarios = Join-Path $PSScriptRoot "scenarios"
$results = Join-Path $PSScriptRoot "results"
$source = Get-Content (Join-Path $scenarios "$Name.txt")
$part = "$Name-part"
$partScript = Join-Path $scenarios "$part.txt"
$all = Join-Path $results "$Name.log"
# The copy to run on: -GameDir, else STS2_GAME_DIR; neither: through Steam (run.ps1 without -GameDir). common.ps1 sets
# $GameDir to the Steam install in that case, which run.ps1 must not get as a copy.
$copyDir = if ($GameDir) { $GameDir } else { "$env:STS2_GAME_DIR" }
$startGiven = $PSBoundParameters.ContainsKey("Start")
. (Join-Path $PSScriptRoot "common.ps1")
$tagDir = if ($copyDir) { $copyDir } else { $GameDir }
$GameDir = $copyDir

# The sweep line: kind, numbers (AutoTestSweep reads the first two tokens starting with a digit as first and count), the rest.
$sweepLines = @($source | Where-Object { $_ -match '^sweep \S+' })
if ($sweepLines.Count -ne 1) { throw "$Name.txt: $($sweepLines.Count) 'sweep' lines, one expected" }
$tokens = @($sweepLines[0].Trim() -split '\s+' | Select-Object -Skip 1)
$kind = $tokens[0]
$numbers = @($tokens | Select-Object -Skip 1 | Where-Object { $_ -match '^\d' })
$others = @($tokens | Select-Object -Skip 1 | Where-Object { $_ -notmatch '^\d' })
if ($numbers.Count -gt 2) { throw "$Name.txt: 'sweep' takes at most two numbers (first and count): $($sweepLines[0])" }
$next = $Start
$end = [int]::MaxValue
if ($numbers.Count -eq 2) {
    if (-not $startGiven) { $next = [int]$numbers[0] }
    $end = [int]$numbers[0] + [int]$numbers[1]
}
elseif ($numbers.Count -eq 1) { $end = $next + [int]$numbers[0] }
if (@($others | Where-Object { $_ -notin "checksum", "state" }).Count -and $next -gt 0) { throw "$Name.txt names an item: no first index for it (-Start $next)" }

Set-Content $all "" -Encoding utf8
$bad = 0
$finished = $false
try {
    for ($session = 1; $session -le $MaxSessions -and $next -lt $end; $session++) {
        $line = "sweep $kind $next" + $(if ($end -ne [int]::MaxValue) { " $($end - $next)" } else { "" }) + $(if ($others.Count) { " " + ($others -join " ") } else { "" })
        $lines = $source | ForEach-Object { if ($_ -match '^sweep \S+') { $line } else { $_ } }
        [IO.File]::WriteAllText($partScript, (($lines -join "`n") + "`n"))
        $run = @{ Name = $part; TimeoutSec = $SessionTimeoutSec }
        if ($GameDir) { $run.GameDir = $GameDir }
        $output = @(& (Join-Path $PSScriptRoot "run.ps1") @run)
        $log = Join-Path $results "$part.log"
        # As written (UTF-8): read as ANSI, every non-ASCII character would come out twice encoded.
        Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue | Add-Content $all -Encoding utf8
        $abort = Select-String -Path $log -Pattern "SWEEP ABORT next=(\d+)" -ErrorAction SilentlyContinue | Select-Object -Last 1
        # The session's problems (run.ps1's judgment), but the stuck item that ended it and the end it then never reached (AutoTest
        # quits after SWEEP ABORT: no 'script finished'): this script's restart is the design. 'FAILED: sweep ... exercised nothing' stays.
        $problems = @($output | Where-Object { $_ -match '^\s*PROBLEM ' -and -not ($abort -and $_ -match 'SWEEP (FAILED|ABORT) |ABORT: |: did not reach its end$') } | ForEach-Object { $_.Trim() })
        $problems | ForEach-Object { Write-Output "  session $session`: $_" }
        $bad += $problems.Count
        if (-not $abort) {
            $finished = [bool](Select-String -Path $log -Pattern "SWEEP \S+ done" -Quiet -ErrorAction SilentlyContinue)
            Write-Output "$Name session $session from $next`: $(if ($finished) { 'done' } else { 'ended without finishing (game closed or timeout)' })"
            break
        }
        $next = [int]$abort.Matches[0].Groups[1].Value
        Write-Output "$Name session $session stopped on a stuck item, next starts at $next"
        if ($next -ge $end) { $finished = $true }
    }
}
finally {
    Remove-Item $partScript -ErrorAction SilentlyContinue
    Save-VersionedLog $all $tagDir
}
if (-not $finished) { Write-Output "PROBLEM $Name`: the sweep did not finish (stopped at item $next)" }
exit $(if ($bad -or -not $finished) { 1 } else { 0 })
