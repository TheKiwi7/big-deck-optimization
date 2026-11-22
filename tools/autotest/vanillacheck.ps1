# The proof that 'bdopt off' is the unmodified game, which no other test checks directly: the same mod-off scenarios both ways,
# vanilla (launched with --bdvanilla: the mod off from the game's start, no patch of it installed) and patched-off (every patch
# installed, then 'bdopt off': how mod-off sessions ran before --bdvanilla), must log IDENTICAL compared lines (compare.py:
# CHECKSUM, STATE, CARDPLAY, AutoSlay's rooms) with no problem in either log. It is what lets every A/B and sweep run its
# mod-off side vanilla and reuse it from the baselines while their key is unchanged (parlib.ps1's Get-BaselineKey leaves this
# mod's patches out). A disabled patch that still changed the game would show on both sides of an A/B; here it differs.
# Once per release on every supported game version. Exit code 1 on any difference or problem.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/vanillacheck.ps1 -GameDir <copy> [-Jobs n] [-Names ab-2k-off,outside-2k-off] [-SweepKind potions|none] [-Fresh]
# What runs each time and what comes from the baselines: the vanilla side of every scenario and of the sweep's "off" parts is a
# vanilla log that depends only on the game, the data, the Workshop mods, the scenario and the test runner code it uses, never on
# this mod's patches (parlib.ps1's Get-BaselineKey): it is reused from results/baselines/<version>/ when the same key holds (the
# solo steps and parsweep-all keep them as they run) and made, then kept, when it does not. The patched-off side (every patch
# installed, then 'bdopt off') always runs: it is the side under test. -Fresh: every session runs, vanilla ones included (once per
# release and version, the battery's -Fresh passes it).
# -Names: solo mod-off scenarios (their first console command 'cmd bdopt off'). -PrefixNames: those the game itself may not finish
# mod off: compared up to what both sessions reached, a failure both share listed, not counted (compare.py --prefix), a session
# that timed out or stalled accepted. None by default: outside-2k-off, which needed it on v0.113 (its rest site's clone makes
# act 2's combat start last minutes), now stops before that combat there itself ('beforegame v0.113'), so it must end.
# -SweepKind: a whole sweep both ways (parsweep.ps1 -PatchedOff; parts and report in results/<version>/vanillacheck/), none to skip.
param([string]$GameDir = "", [int]$Jobs = 0, [string[]]$Names = @("ab-2k-off", "outside-2k-off"),
    [string[]]$PrefixNames = @(), [ValidateSet("cards", "relics", "potions", "encounters", "powers", "none")][string]$SweepKind = "potions",
    [int]$TimeoutSec = 3600, [int]$StallSec = 600, [switch]$Fresh)
$Names = @($Names | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-Names a,b" through powershell -File is one string
$PrefixNames = @($PrefixNames | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
# An error nothing catches ends this script with exit code 1 (common.ps1's Write-RunnerError), never with its work half done.
trap { Write-RunnerError $_; exit 1 }
$GameDir = Resolve-GameCopy $copy
if ($Jobs -le 0) { $Jobs = $SlotPool }
$out = Join-Path (Get-VersionDir $GameDir) "vanillacheck"
New-Item -ItemType Directory -Force $out | Out-Null
$started = Get-Date
$bad = 0
$list = foreach ($name in $Names) {
    $script = Join-Path $Scenarios "$name.txt"
    if (-not (Test-Path $script)) { throw "No scenario $script" }
    $vanilla = [pscustomobject]@{ Name = $name; Script = $script; Log = Join-Path $out "$name-vanilla.log" }
    # Only a scenario that turns the whole mod off before its run is launched vanilla: anything else would compare nothing.
    if (-not (Test-BaselineJob $vanilla)) { throw "$name is not a solo mod-off scenario (its name says off, its first console command is 'cmd bdopt off')" }
    $vanilla
    [pscustomobject]@{ Name = "$name-patched"; Script = $script; Log = Join-Path $out "$name-patched.log"; PatchedOff = $true }
}
# A vanilla log is read from the baselines unless -Fresh, and a fresh one is kept as a baseline as any other (Invoke-Sessions);
# the patched-off jobs are never baseline jobs (Test-BaselineJob), so they always run.
$runs = Invoke-Sessions -Jobs $list -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -StallSec $StallSec -UseBaselines:(-not $Fresh) -Label "vanillacheck $(Get-GameTag $GameDir)"
Remove-TestMods $GameDir
# Every job has its result: one that never ran left no log to compare (Invoke-Sessions deletes a queued job's old log).
$missing = @(Get-MissingResults $list $runs)
$missing | ForEach-Object { Write-Output $_ }
$bad += $missing.Count
foreach ($r in $runs) {
    $prefix = $PrefixNames -contains ($r.Name -replace '-patched$', '')
    # How it ran: not ended, not launched as its side says (Get-LaunchProblem), a test mod missing, an earlier session that ended
    # early or froze in this mod's code. A --prefix scenario's other problems (judge.py's) are compare.py's to weigh, both sides.
    $failed = @($r.Failed | Where-Object { -not $prefix -or $_ -match '^(ended early|froze|test mod |launched with|its log has the BDVANILLA|a patched-off session)' })
    $ok = $r.State -in "done", "cached" -or ($prefix -and $r.State -in "TIMEOUT", "STALLED")
    Write-Output ("{0,-28} {1,-8} {2,5} s{3}" -f $r.Name, $r.State, $r.Seconds, $(if ($failed.Count) { "  " + $failed[0] } else { "" }))
    if (-not $ok -or $failed.Count) {
        $bad++
        $failed | Select-Object -Skip 1 -First 7 | ForEach-Object { Write-Output "  $_" }
    }
}
foreach ($name in $Names) {
    $arguments = @("-I", (Join-Path $PSScriptRoot "compare.py"), (Join-Path $out "$name-vanilla.log"), (Join-Path $out "$name-patched.log"), "--no-times")
    if ($PrefixNames -contains $name) { $arguments += "--prefix" }
    $cmp = @(& python @arguments)
    $same = $LASTEXITCODE -eq 0
    $verdict = $cmp | Where-Object { $_ -match "^(IDENTICAL|SKIPPED|DIFFERENT|NOT COMPARED|PROBLEMS)$" }
    Write-Output ("{0} vanilla vs patched-off: {1} / {2}" -f $name, ($cmp | Where-Object { $_ -match "^checked lines" }), $verdict)
    if (-not $same) { $bad++; $cmp | Where-Object { $_ -match "^\s+(#|A:|B:|differs:)|^problem in" } | Select-Object -First 8 | ForEach-Object { Write-Output "  $_" } }
    $cmp | Where-Object { $_ -match "^(errors only|expected in|on both sides|\s+DIFFERENT LENGTHS)" } | ForEach-Object { Write-Output "  $_" }
}
if ($SweepKind -ne "none") {
    # The mod-off parts vanilla ("off" side), the same parts patched-off ("on" side): sweepreport.py's verdict, its exit code.
    # -UseBaselines: the "off" parts are the same vanilla parts as the real sweep's (same script text): from the baselines.
    $sweepArgs = @("-Kind", $SweepKind, "-GameDir", $GameDir, "-Jobs", $Jobs, "-PatchedOff")
    if (-not $Fresh) { $sweepArgs += "-UseBaselines" }
    & (Join-Path $PSScriptRoot "parsweep.ps1") @sweepArgs
    if ($LASTEXITCODE) { $bad++; Write-Output "sweep $SweepKind vanilla vs patched-off: DIFFERENT or a problem (report above, results/$(Get-GameTag $GameDir)/vanillacheck/report-$SweepKind.md)" }
    else { Write-Output "sweep $SweepKind vanilla vs patched-off: IDENTICAL" }
}
Write-Output ("vanillacheck done in {0:F0} s, {1} problem(s), logs in {2}" -f ((Get-Date) - $started).TotalSeconds, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
