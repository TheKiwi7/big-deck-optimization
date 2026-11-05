# WPR needs an elevated shell and the test sessions don't run in one. Run this once in an administrator PowerShell, leave it open: it starts and
# stops a CPU trace each time a test session creates a flag file, and ends by itself. It runs nothing else: a flag file only says start, stop or quit,
# its content is never read, and the trace name is fixed. A trace stops by itself after $MaxTraceMin, the script after $MaxWaitMin.
#   powershell -ExecutionPolicy Bypass -File tools/profiling/wpr-watch.ps1
# From the test side: create results/etw/start.flag, wait for started.flag, play the session, create stop.flag, wait for stopped.flag; quit.flag ends this.
# The game's log reaches the disk late: start the trace before the session, not on a line of its log (the session was over when that line showed).
# A trace holds stacks of every process on this PC for that time: it stays in tools/autotest/results (gitignored).
param([int]$MaxWaitMin = 240, [int]$MaxTraceMin = 12)
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Open PowerShell as administrator first." }
$dir = Join-Path $PSScriptRoot "..\autotest\results\etw"
New-Item -ItemType Directory -Force $dir | Out-Null
$dir = (Resolve-Path $dir).Path
Remove-Item (Join-Path $dir "*.flag") -ErrorAction SilentlyContinue
$trace = Join-Path $dir "trace.etl"
$end = (Get-Date).AddMinutes($MaxWaitMin)
Write-Output "Waiting for the test session (at most $MaxWaitMin minutes). Leave this window open."
while ((Get-Date) -lt $end -and -not (Test-Path (Join-Path $dir "quit.flag"))) {
    if (-not (Test-Path (Join-Path $dir "start.flag"))) { Start-Sleep 1; continue }
    Remove-Item (Join-Path $dir "start.flag"), (Join-Path $dir "stopped.flag") -ErrorAction SilentlyContinue
    wpr -start CPU -filemode
    New-Item -ItemType File (Join-Path $dir "started.flag") -Force | Out-Null
    Write-Output "Tracing since $(Get-Date -Format HH:mm:ss) (at most $MaxTraceMin minutes)."
    $stopAt = (Get-Date).AddMinutes($MaxTraceMin)
    while ((Get-Date) -lt $stopAt -and -not (Test-Path (Join-Path $dir "stop.flag"))) { Start-Sleep 1 }
    wpr -stop $trace
    Remove-Item (Join-Path $dir "stop.flag"), (Join-Path $dir "started.flag") -ErrorAction SilentlyContinue
    New-Item -ItemType File (Join-Path $dir "stopped.flag") -Force | Out-Null
    Write-Output "Trace written at $(Get-Date -Format HH:mm:ss). Still waiting: more may follow."
}
Write-Output "Done, you can close this window."
