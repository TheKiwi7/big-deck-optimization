# Runs scenarios in parallel, headless, each on its own copy of the user data (parlib.ps1): for equivalence checks, not
# for timings. Logs in results/<game version>/. Every <x>-off / <x>-on pair in the list is compared.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/par.ps1 -Names ab-2k-off,ab-2k-on,shuffle-off,shuffle-on -GameDir C:\Games\StS2-public-0.107.1 [-Jobs n] [-TimeoutSec 900] [-StallSec 240] [-Rendered] [-UseBaselines] [-Instant]
# -UseBaselines: a mod-off log already made with this game, these Workshop mods, this test tooling and this scenario isn't
# run again. -Instant: no animation waits, same results, several times
# faster mod on (not for what depends on animations playing; a scenario's own speed line is replaced).
param([Parameter(Mandatory)][string[]]$Names, [string]$GameDir = "", [int]$Jobs = [Environment]::ProcessorCount, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Rendered, [switch]$UseBaselines, [switch]$Instant)
$Names = @($Names | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-Names a,b" through powershell -File is one string
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
$list = foreach ($name in $Names) {
    $script = Join-Path $Scenarios "$name.txt"
    if (-not (Test-Path $script)) { throw "No scenario $script" }
    if ($Instant) {
        $folder = Join-Path $out "par-scripts"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $text = @(Get-Content $script | Where-Object { $_ -notmatch '^speed ' })
        $script = Join-Path $folder "$name.txt"
        [IO.File]::WriteAllText($script, ((@("speed instant") + $text) -join "`n") + "`n")
    }
    [pscustomobject]@{ Name = $name; Script = $script; Log = Join-Path $out "$name.log" }
}
$started = Get-Date
$runs = Invoke-Sessions -Jobs $list -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -StallSec $StallSec -Rendered:$Rendered -UseBaselines:$UseBaselines -Label "par $(Get-GameTag $GameDir)"
Remove-TestMods $GameDir
$bad = 0
foreach ($r in $runs) {
    Write-Output ("{0,-28} {1,-8} {2,5} s{3}{4}" -f $r.Name, $r.State, $r.Seconds, $(if ($r.Restarts) { "  started over $($r.Restarts)x" } else { "" }), $(if ($r.Failed.Count) { "  " + $r.Failed[0] } else { "" }))
    if ($r.State -notin "done", "cached") { $bad++ }
}
foreach ($name in $Names | Where-Object { $_ -match '-off$' }) {
    $on = $name -replace '-off$', '-on'
    if ($Names -notcontains $on) { continue }
    $cmp = python -I (Join-Path $PSScriptRoot "compare.py") (Join-Path $out "$name.log") (Join-Path $out "$on.log") --no-times
    $verdict = $cmp | Where-Object { $_ -match "^(IDENTICAL|DIFFERENT)" }
    Write-Output ("{0} vs {1}: {2} / {3}" -f $name, $on, ($cmp | Where-Object { $_ -match "^checked lines" }), $verdict)
    if ($verdict -ne "IDENTICAL") { $bad++; $cmp | Where-Object { $_ -match "^\s+(#|A:|B:|differs:)" } | Select-Object -First 8 | ForEach-Object { Write-Output "  $_" } }
    $cmp | Where-Object { $_ -match "^(errors only|mod problem)" } | ForEach-Object { Write-Output "  $_" }
}
Write-Output ("done in {0:F0} s, {1} problem(s), logs in {2}" -f ((Get-Date) - $started).TotalSeconds, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
