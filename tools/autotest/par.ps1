# Runs scenarios in parallel, headless, each on its own copy of the user data (parlib.ps1): for equivalence checks, not
# for timings. Logs in results/<game version>/. Every <x>-off / <x>-on pair in the list is compared, and every scenario with a
# '# compare-with: <reference>' header line with that reference when both are in the list (one alone: a note, not a problem).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/par.ps1 -Names ab-2k-off,ab-2k-on,shuffle-off,shuffle-on -GameDir C:\Games\StS2-public-0.107.1 [-Jobs n] [-TimeoutSec 900] [-StallSec 240] [-Rendered] [-UseBaselines] [-Instant] [-PatchedOff]
# A mod-off scenario (its first console command 'cmd bdopt off', parlib.ps1's Test-BaselineJob) is launched with --bdvanilla:
# the mod off from the game's start, no patch of it installed. -UseBaselines: such a log already made with this game, these
# Workshop mods, this test tooling and this scenario isn't run again. -PatchedOff: mod-off scenarios the old way instead, every
# patch installed then 'bdopt off' (never cached; vanillacheck.ps1 compares both ways). -Instant: no animation waits, same
# results, several times faster mod on (not for what depends on animations playing; a scenario's own speed line is replaced):
# it finds bugs and result differences only, its fps and timings mean nothing.
param([Parameter(Mandatory)][string[]]$Names, [string]$GameDir = "", [int]$Jobs = [Environment]::ProcessorCount, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Rendered, [switch]$UseBaselines, [switch]$Instant, [switch]$PatchedOff)
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
    [pscustomobject]@{ Name = $name; Script = $script; Log = Join-Path $out "$name.log"; PatchedOff = [bool]($PatchedOff -and $name -match '-off(\d*$|-)') }
}
# Mod-off sessions first (the longest by far at these deck sizes): shorter ones fill the free slots instead of one starting last.
$list = @($list | Where-Object { $_.Name -match '-off(\d*$|-)' }) + @($list | Where-Object { $_.Name -notmatch '-off(\d*$|-)' })
$started = Get-Date
$runs = Invoke-Sessions -Jobs $list -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -StallSec $StallSec -Rendered:$Rendered -UseBaselines:$UseBaselines -Label "par $(Get-GameTag $GameDir)"
Remove-TestMods $GameDir
$bad = 0
foreach ($r in $runs) {
    Write-Output ("{0,-28} {1,-8} {2,5} s{3}{4}" -f $r.Name, $r.State, $r.Seconds, $(if ($r.Restarts) { "  started over $($r.Restarts)x" } else { "" }), $(if ($r.Failed.Count) { "  " + $r.Failed[0] } else { "" }))
    # Not ended, or a problem in its log (judge.py): a failed line (console commands included), the script's timeout, a sweep
    # stopping, an error or a difference this mod found itself, the mod off from the start (but in a vanilla session), a positive
    # control that saw nothing; or not launched as its job says (vanilla or not: parlib.ps1's Get-LaunchProblem).
    if ($r.State -notin "done", "cached" -or $r.Failed.Count) {
        $bad++
        $r.Failed | Select-Object -Skip 1 -First 7 | ForEach-Object { Write-Output "  $_" }
    }
}
# compare.py: A the reference (mod off, or the run without the fault), B the side under test.
function Compare-Pair([string]$reference, [string]$tested) {
    $cmp = python -I (Join-Path $PSScriptRoot "compare.py") (Join-Path $out "$reference.log") (Join-Path $out "$tested.log") --no-times
    $same = $LASTEXITCODE -eq 0
    $verdict = $cmp | Where-Object { $_ -match "^(IDENTICAL|SKIPPED|DIFFERENT|NOT COMPARED|PROBLEMS)$" }
    Write-Output ("{0} vs {1}: {2} / {3}" -f $reference, $tested, ($cmp | Where-Object { $_ -match "^checked lines" }), $verdict)
    if (-not $same) { $script:bad++; $cmp | Where-Object { $_ -match "^\s+(#|A:|B:|differs:)|^problem in" } | Select-Object -First 8 | ForEach-Object { Write-Output "  $_" } }
    $cmp | Where-Object { $_ -match "^(errors only|expected in)" } | ForEach-Object { Write-Output "  $_" }
}
$compared = [Collections.Generic.HashSet[string]]::new()
foreach ($name in $Names | Where-Object { $_ -match '-off$' }) {
    $on = $name -replace '-off$', '-on'
    if ($Names -notcontains $on) { continue }
    [void]$compared.Add("$name|$on")
    Compare-Pair $name $on
}
# Pairs whose names don't say it: a scenario's '# compare-with: <reference>' header lines (check_scenarios.py checks the name). Both
# in -Names: compared like an -off/-on pair. One side only: said, not a problem (the series didn't ask for that comparison).
$declared = @(foreach ($file in Get-ChildItem $Scenarios -Filter *.txt) {
    foreach ($m in (Select-String -Path $file.FullName -Pattern '^\s*#\s*compare-with:\s*(\S+)\s*$')) {
        [pscustomobject]@{ Tested = $file.BaseName; Reference = $m.Matches[0].Groups[1].Value }
    }
})
foreach ($pair in $declared) {
    $hasTested = $Names -contains $pair.Tested
    $hasReference = $Names -contains $pair.Reference
    if ($hasTested -and $hasReference) {
        if ($compared.Add("$($pair.Reference)|$($pair.Tested)")) { Compare-Pair $pair.Reference $pair.Tested }
    }
    elseif ($hasTested) { Write-Output "note: $($pair.Tested) is compared with $($pair.Reference) (its compare-with line), not in -Names: not compared" }
}
$referencesOnly = @($declared | Where-Object { $Names -contains $_.Reference -and $Names -notcontains $_.Tested } | Group-Object Reference)
foreach ($group in $referencesOnly) { Write-Output "note: $($group.Name) is also the reference of $(@($group.Group | ForEach-Object { $_.Tested }) -join ', ') (compare-with), not in -Names: not compared" }
Write-Output ("done in {0:F0} s, {1} problem(s), logs in {2}" -f ((Get-Date) - $started).TotalSeconds, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
