# What the parallel sessions are doing now (they have no window): progress, time left, each session's current item.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/status.ps1 [-Watch]
param([switch]$Watch)
$file = Join-Path $PSScriptRoot "results\status.txt"
do {
    if ($Watch) { Clear-Host }
    if (Test-Path $file) { Get-Content $file } else { Write-Output "no session has run yet" }
    $age = if (Test-Path $file) { ((Get-Date) - (Get-Item $file).LastWriteTime).TotalSeconds } else { 0 }
    if ($age -gt 60) { Write-Output ("(written {0:F0} s ago: nothing running, or stuck)" -f $age) }
    if ($Watch) { Start-Sleep 3 }
} while ($Watch)
