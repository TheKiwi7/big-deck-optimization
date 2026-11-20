# A whole-game A/B in parallel: sweep-<Kind>-2k-off and -on (scenarios/) cut into chunks with the same boundaries on both
# sides, so an item stopping its session only spoils its own chunk. An item leaving the game stuck ends that session; a new
# one goes on from the next item, up to the end of the chunk (as sweep.ps1), queued as soon as that session ends. sweepreport.py
# then says what differs, down to the values. Live progress: results/status-<Kind>-<version>.txt (status.ps1). Logs and
# report-<Kind>.md: results/<game version>/. The five kinds at once, on one copy or both: parsweep-all.ps1 (the solo slot pool is
# shared, parlib.ps1's $SlotPool: the longest waiting session of any series takes a free slot; -Jobs, by default the whole pool,
# caps this series only). Parts start longest first (results/<version>/durations.tsv, filled from the last run's part logs too).
#   powershell -ExecutionPolicy Bypass -File tools/autotest/parsweep.ps1 -Kind cards -GameDir C:\Games\StS2-public-0.111.1 [-Jobs 14] [-ChunkItems 20] [-ItemTimeout 45] [-Speed instant|fast] [-UseBaselines]
# -Speed instant (default) skips animation waits: same results (checked on whole sweeps), about 4 times faster mod on. Once
# per release, -Speed fast for what only shows with animations playing.
# -Chunks 27,28: only those chunks, to rerun what a report says wasn't compared (the same -ChunkItems).
# -OnExtra "<scenario line>": a line only the mod-on sessions run, before the sweep: the positive control that a difference
# is found ("enemyhp 999998").
# -Imports 11: no A/B but what deck size costs. Mod on only, the reference run's deck imported that many times; logs in
# parts-<Kind>-i<n>. Two sizes, then scalereport.py: the items whose main thread time and allocations depend on the deck.
# The mod-off parts are launched with --bdvanilla (parlib.ps1): the mod off from the game's start, none of its patches installed.
# -PatchedOff (vanillacheck.ps1): no mod-on side, but the mod-off scenario a second time on the "on" side, the old way: every patch
# installed then 'bdopt off' (never cached). Both must be identical: 'bdopt off' is the unmodified game. Parts and report in
# results/<version>/vanillacheck/.
param([Parameter(Mandatory)][ValidateSet("cards", "relics", "potions", "encounters", "powers")][string]$Kind, [string]$GameDir = "", [int]$Jobs = 0, [int]$ChunkItems = 0, [int]$ItemTimeout = 45, [ValidateSet("instant", "fast")][string]$Speed = "instant", [int]$TimeoutSec = 1800, [int]$StallSec = 300, [switch]$UseBaselines, [string]$OnExtra = "", [int[]]$Chunks = @(), [int]$Imports = 0, [switch]$PatchedOff)
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
# An error nothing catches ends this script with exit code 1 (common.ps1's Write-RunnerError), never with its work half done.
trap { Write-RunnerError $_; exit 1 }
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
if ($Jobs -le 0) { $Jobs = $SlotPool }
$statusFile = "status-$Kind-$(Get-GameTag $GameDir).txt"
if ($PatchedOff -and $Imports) { throw "-PatchedOff compares mod off both ways: not with -Imports" }
# Where the parts and the report go: the A/B's own, or vanillacheck's (a real sweep's parts are never overwritten by it).
$reportDir = if ($PatchedOff) { Join-Path $out "vanillacheck" } else { $out }
# An encounter is a whole fight: smaller chunks. A session start costs ~10 s: bigger than this wastes cores, smaller wastes starts.
if ($ChunkItems -le 0) { $ChunkItems = if ($Kind -eq "encounters") { 6 } else { 20 } }
$work = Join-Path $reportDir $(if ($Imports) { "parts-$Kind-i$Imports" } else { "parts-$Kind" })
$started = Get-Date
$source = @{ off = Get-Content (Join-Path $Scenarios "sweep-$Kind-2k-off.txt"); on = Get-Content (Join-Path $Scenarios "sweep-$Kind-2k-$(if ($PatchedOff) { 'off' } else { 'on' }).txt") }
# What the parts were made with: -Chunks reruns some chunks of the last whole run, whose other chunks stay in the report, only
# when they were cut and played the same way (sweepreport.py also checks every item is in a chunk on both sides).
$plan = "chunk items $ChunkItems, speed $Speed, item timeout $ItemTimeout, on extra '$OnExtra'$(if ($PatchedOff) { ', patched off' }), game $(Get-GameTag $GameDir), scenarios " +
    (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes(($source.off + $source.on) -join "`n")))).Hash.Substring(0, 16)
$planFile = Join-Path $work "plan.txt"
# The last run's part logs give the next run's order (longest first), for the parts durations.tsv doesn't know yet: read before
# the folder is emptied.
if (Test-Path $work) {
    Save-LogDurations $GameDir @(Get-ChildItem $work -Filter "$Kind-*-c*-p*.log" -File | ForEach-Object { [pscustomobject]@{ Name = $_.BaseName; Script = [IO.Path]::ChangeExtension($_.FullName, ".txt"); Log = $_.FullName } })
}
if ($Chunks.Count) {
    if (-not (Test-Path $planFile) -or [IO.File]::ReadAllText($planFile).Trim() -ne $plan) {
        throw "-Chunks reruns chunks of the last whole $Kind run in $work, made the same way: $(if (Test-Path $planFile) { "it was '$([IO.File]::ReadAllText($planFile).Trim())', now '$plan'" } else { 'there is none' }). Run the whole sweep."
    }
    # Only these chunks' parts go: the others stay, and the report covers the whole game again.
    foreach ($chunk in $Chunks) { Get-ChildItem $work -File -Filter "$Kind-*-c$chunk-p*" | Remove-Item -Force }
}
else {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $work | Out-Null
    [IO.File]::WriteAllText($planFile, $plan)
}
if ($Imports) {
    $source.on = foreach ($line in $source.on) {
        if ($line -match 'bigdeck import ref all all') { $line; if ($Imports -ge 2) { 2..$Imports | ForEach-Object { "cmd bigdeck import ref all cards" } } }
        elseif ($line -notmatch 'bigdeck import ref (all|1) cards') { $line }
    }
}

# Scenario text decides the baseline key: stable names and content, whatever the folder.
function New-PartJob([string]$side, [int]$chunk, [int]$part, [int]$first, [int]$count) {
    $name = "$Kind-$side-c$chunk-p$part"
    $after = $false
    $lines = foreach ($line in $source[$side]) {
        if ($after) { if ($line -notmatch '^(counters|wait) ') { $line } }
        elseif ($line -match '^speed ') { "speed $Speed" }
        elseif ($line -match '^sweep ') {
            if ($side -eq "on" -and $OnExtra) { $OnExtra }
            "sweeptimeout $ItemTimeout"; "sweep $Kind $first $count$(if (-not $Imports) { ' checksum state' })"; "effects end"
            $after = $true
        }
        else { $line }
    }
    $script = Join-Path $work "$name.txt"
    [IO.File]::WriteAllText($script, (($lines -join "`n") + "`n"))
    [pscustomobject]@{ Name = $name; Script = $script; Log = Join-Path $work "$name.log"; Side = $side; Chunk = $chunk; Part = $part; End = $first + $count; Items = $count; PatchedOff = [bool]($PatchedOff -and $side -eq "on") }
}

# How many items: a session that only lists them, or the count a session made with the same game, the same test mods (their
# files), the same build of this mod in the copy, the same test tooling and the same sweep scenarios found (results/<version>/
# sweep-counts/). Unsure (anything of those changed, a key that can't be made): counted again. A stale count would leave items in
# no chunk: each part also logs the count it sees, checked below and by sweepreport.py (items in no chunk: not compared).
$countScript = Join-Path $work "count.txt"
[IO.File]::WriteAllText($countScript, "timeout 120`nsweep $Kind 999999 0`nquit`n")
$countLog = Join-Path $work "count.log"
$countDir = Join-Path $out "sweep-counts"
$countFile = $null
try {
    $modDir = Join-Path $GameDir "mods\BigDeckOptimization"
    $build = (@(Get-ChildItem $modDir -Recurse -File -ErrorAction Stop | Sort-Object FullName | ForEach-Object { "$($_.Name)=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join "")
    if (-not $build) { throw "no build of this mod in $modDir" }
    $sha = [Security.Cryptography.SHA256]::Create()
    $countKey = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("count1|$Kind|$(Get-GameTag $GameDir)|$(Get-BaselineKey $GameDir $countScript)|$build|$plan"))) -replace "-", "").Substring(0, 16)
    $countFile = Join-Path $countDir "$Kind-$countKey.txt"
}
catch { Write-Output "note: item count not cached ($($_.Exception.Message)): counted" }
$items = 0
if ($countFile -and (Test-Path $countFile) -and ([IO.File]::ReadAllText($countFile).Trim() -match '^\d+$')) {
    $items = [int][IO.File]::ReadAllText($countFile).Trim()
    Write-Output "$Kind`: $items items (counted before with this game, these mods, this build and these scenarios: $(Split-Path $countFile -Leaf))"
}
else {
    Invoke-Sessions -Jobs @([pscustomobject]@{ Name = "count"; Script = $countScript; Log = $countLog }) -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec 180 -StallSec 120 -Label "$Kind count" -StatusFile $statusFile | Out-Null
    $match = if (Test-Path $countLog) { Select-String -Path $countLog -Pattern "SWEEP $Kind`: (\d+) items" | Select-Object -First 1 }
    if (-not $match) { throw "No item count in $countLog" }
    $items = [int]$match.Matches[0].Groups[1].Value
    if ($countFile) {
        New-Item -ItemType Directory -Force $countDir | Out-Null
        [IO.File]::WriteAllText($countFile, "$items")
    }
}
$chunkCount = [int][Math]::Ceiling($items / $ChunkItems)
Write-Output "$Kind`: $items items, $chunkCount chunks of $ChunkItems, speed $Speed, up to $Jobs sessions at once"

$parts = foreach ($chunk in 0..($chunkCount - 1)) {
    if ($Chunks.Count -and $Chunks -notcontains $chunk) { continue }
    foreach ($side in $(if ($Imports) { @("on") } else { @("off", "on") })) { New-PartJob $side $chunk 0 ($chunk * $ChunkItems) ([Math]::Min($ChunkItems, $items - $chunk * $ChunkItems)) }
}
# The mod-off parts first: 5 to 20 times longer each, they set the sweep's length; the mod-on ones then fill the free slots
# instead of a long mod-off part starting last. Invoke-Sessions then puts the parts it knows the length of longest first (those it
# doesn't, in this order, before them).
$parts = @($parts | Where-Object { $_.Side -eq "off" }) + @($parts | Where-Object { $_.Side -ne "off" })
$sessionProblems = [Collections.Generic.List[string]]::new()
# Each part's end, as soon as its session ends (Invoke-Sessions -OnFinish): its problems, and the chunk's next part when an item
# stopped it (SWEEP ABORT next=N), queued at once ahead of the parts not started yet, not after the slowest session of a round.
$onPartEnd = {
    param($part, $result)
    if ($result.State -notin "done", "cached") { $sessionProblems.Add("$($part.Name): $($result.State)") }
    # What sweepreport.py doesn't see in the parts' logs: an earlier session of this part that ended early or froze in this mod's code (not the known
    # renderer crash), a test mod the game didn't find, a session not launched as its side says (Get-LaunchProblem).
    $result.Failed | Where-Object { $_ -match '^(ended early|froze|test mod |launched with|its log has the BDVANILLA|a patched-off session)' } | ForEach-Object { $sessionProblems.Add("$($part.Name): $_") }
    # A part without its log (it never wrote one) has no continuation: its result says why. (Select-String throws on a missing
    # file whatever -ErrorAction says: inside Invoke-Sessions, it would end the whole sweep.)
    $abort = if (Test-Path $part.Log) { Select-String -Path $part.Log -Pattern "SWEEP ABORT next=(\d+)" -ErrorAction SilentlyContinue | Select-Object -Last 1 }
    if ($abort) {
        $from = [int]$abort.Matches[0].Groups[1].Value
        if ($from -lt $part.End) { New-PartJob $part.Side $part.Chunk ($part.Part + 1) $from ($part.End - $from) }
    }
}
$outcome = @(Invoke-Sessions -Jobs $parts -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -StallSec $StallSec -UseBaselines:$UseBaselines -Label "$Kind $(Get-GameTag $GameDir)" -OnFinish $onPartEnd -StatusFile $statusFile)
$count = $outcome.Count
# Every part has its result: one that never ran has no log of this run (Invoke-Sessions deletes a queued job's old log).
Get-MissingResults $parts $outcome | ForEach-Object { $sessionProblems.Add(($_ -replace '^PROBLEM ', '')) }
# The count every part saw: a cached count that no longer holds is dropped (the next run counts again) and said here.
$seen = @(Get-ChildItem $work -Filter "$Kind-*-c*-p*.log" -File | ForEach-Object { Select-String -Path $_.FullName -Pattern "SWEEP $Kind`: (\d+) items" | Select-Object -First 1 } | Where-Object { $_ } | ForEach-Object { [int]$_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
if (@($seen | Where-Object { $_ -ne $items }).Count) {
    $sessionProblems.Add("item count: the sweep was cut for $items items, its sessions saw $($seen -join ', '): items left out$(if ($countFile -and (Test-Path $countFile)) { ' (cached count dropped: run it again)' })")
    if ($countFile) { Remove-Item $countFile -Force -ErrorAction SilentlyContinue }
}

Remove-TestMods $GameDir
if ($Imports) {
    $sessionProblems | ForEach-Object { Write-Output "session problem: $_" }
    Write-Output ("{0} at {1} imports: {2} sessions in {3:F1} min" -f $Kind, $Imports, $count, ((Get-Date) - $started).TotalMinutes)
    exit $(if ($sessionProblems.Count) { 1 } else { 0 })
}
$reportOutput = python -I (Join-Path $PSScriptRoot "sweepreport.py") $reportDir $Kind
$bad = $LASTEXITCODE
$reportOutput | ForEach-Object { Write-Output $_ }
$sessionProblems | ForEach-Object { Write-Output "session problem: $_" }
Write-Output ("{0}: {1} sessions in {2:F1} min" -f $Kind, $count, ((Get-Date) - $started).TotalMinutes)
exit $(if ($bad -or $sessionProblems.Count) { 1 } else { 0 })
