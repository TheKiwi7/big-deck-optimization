# What the parallel sessions are doing now (they have no window): progress, time left, each session's current item. One file per
# series: results/status.txt (par.ps1, parcoop.ps1, mutations.ps1...), status-<kind>.txt (parsweep.ps1, parsweep-all.ps1's five).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/status.ps1 [-Watch]
param([switch]$Watch)
$folder = Join-Path $PSScriptRoot "results"
do {
    if ($Watch) { Clear-Host }
    $files = @(Get-ChildItem $folder -Filter "status*.txt" -File -ErrorAction SilentlyContinue | Sort-Object Name)
    if (-not $files.Count) { Write-Output "no session has run yet" }
    foreach ($file in $files) {
        $age = ((Get-Date) - $file.LastWriteTime).TotalSeconds
        # A series ended long ago: one line, not its last screen.
        if ($files.Count -gt 1 -and $age -gt 600) { Write-Output ("{0}: written {1:F0} min ago (ended)" -f $file.Name, ($age / 60)); continue }
        if ($files.Count -gt 1) { Write-Output "== $($file.Name)" }
        Get-Content $file.FullName
        if ($age -gt 60) { Write-Output ("(written {0:F0} s ago: nothing running, or stuck)" -f $age) }
    }
    if ($Watch) { Start-Sleep 3 }
} while ($Watch)
