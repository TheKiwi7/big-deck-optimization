# The whole-game A/B of several kinds at once on one game copy: one parsweep.ps1 per kind, side by side. They share the test
# slots (each session takes a free one, Lock-Slot), the copy's test mods (held by each, made once) and the baselines; each has its
# own parts folder, report and live progress (results/status-<kind>.txt: status.ps1 shows them all). The long mod-off parts of
# one kind no longer wait for the end of another kind: the sweeps end about when the longest kind would alone.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/parsweep-all.ps1 -GameDir C:\Games\StS2-beta-0.113.1 [-Kinds cards,relics,potions,encounters,powers] [-Jobs 16] [-Speed instant|fast] [-ItemTimeout 45] [-TimeoutSec 2700] [-UseBaselines]
# Each kind's whole output: results/<game version>/parsweep-<kind>.out (its report: report-<kind>.md). Exit code 1 when any kind
# found a problem (its parsweep.ps1's exit code), or didn't end.
# -TimeoutSec: per session, longer than parsweep.ps1's own default (1800): five series at once load the PC, and a mod-off part
# runs slower beside the others' sessions.
# Stopped from outside: the parsweep.ps1 already started go on to their end (headless, nothing on screen, each with its own
# timeouts), their games closed by their own cleanup; this script only waits for them.
param([string]$GameDir = "", [string[]]$Kinds = @("encounters", "powers", "relics", "cards", "potions"), [int]$Jobs = [Environment]::ProcessorCount, [ValidateSet("instant", "fast")][string]$Speed = "instant", [int]$ItemTimeout = 45, [int]$TimeoutSec = 2700, [int]$StallSec = 300, [switch]$UseBaselines)
$Kinds = @($Kinds | ForEach-Object { $_ -split "," } | Where-Object { $_ } | Select-Object -Unique)  # "-Kinds a,b" through powershell -File is one string
$known = @("cards", "relics", "potions", "encounters", "powers")
$unknown = @($Kinds | Where-Object { $known -notcontains $_ })
if ($unknown.Count) { throw "-Kinds: no such kind $($unknown -join ', ') (known: $($known -join ', '))" }
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
$started = Get-Date
$children = foreach ($kind in $Kinds) {
    $log = Join-Path $out "parsweep-$kind.out"
    $err = Join-Path $out "parsweep-$kind.err"
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$(Join-Path $PSScriptRoot 'parsweep.ps1')`"", "-Kind", $kind, "-GameDir", "`"$GameDir`"",
        "-Jobs", $Jobs, "-Speed", $Speed, "-ItemTimeout", $ItemTimeout, "-TimeoutSec", $TimeoutSec, "-StallSec", $StallSec)
    if ($UseBaselines) { $arguments += "-UseBaselines" }
    $p = Start-Process -FilePath "powershell" -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError $err
    [void]$p.Handle # or its exit code is lost
    Write-Host "started $kind (process $($p.Id)): $log"
    [pscustomobject]@{ Kind = $kind; Process = $p; Out = $log; Err = $err }
    # Each starts by making or checking the copy's test mods under one lock, then its item count: a few seconds apart.
    Start-Sleep 5
}
while (@($children | Where-Object { -not $_.Process.HasExited }).Count) { Start-Sleep 10 }
$bad = 0
foreach ($child in $children) {
    $code = $child.Process.ExitCode
    if ($code -ne 0) { $bad++ }
    Write-Output ("== {0}: exit code {1}" -f $child.Kind, $code)
    # Its summary: the report's head (verdict, items not compared or stopping a session) and its last lines.
    $text = @(Get-Content $child.Out -ErrorAction SilentlyContinue)
    $text | Where-Object { $_ -match '^(\d+ items|IDENTICAL|\d+ DIFFERENT|items stopping|  (NEVER COMPARED|compared before)|chunk \d+|mod (off|on):|session problem|game error|mod problem|\w+: \d+ sessions in)' } | ForEach-Object { Write-Output "  $($_.Substring(0, [Math]::Min(220, $_.Length)))" }
    $errors = @(Get-Content $child.Err -ErrorAction SilentlyContinue | Where-Object { $_.Trim() })
    if ($errors.Count) { Write-Output "  stderr: $($errors[0])"; if ($code -eq 0) { $bad++ } }
}
Write-Output ("{0} kind(s) in {1:F1} min, {2} with a problem; outputs in {3}" -f $children.Count, ((Get-Date) - $started).TotalMinutes, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
