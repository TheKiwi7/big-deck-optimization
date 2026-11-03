# A whole-game A/B in parallel: sweep-<Kind>-2k-off and -on (scenarios/) cut into chunks with the same boundaries on both
# sides, so an item stopping its session only spoils its own chunk. An item leaving the game stuck ends that session; a new
# one goes on from the next item, up to the end of the chunk (as sweep.ps1). sweepreport.py then says what differs, down to
# the values. Live progress: results/status.txt (status.ps1). Logs and report-<Kind>.md: results/<game version>/.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/parsweep.ps1 -Kind cards -GameDir C:\Games\StS2-public-0.107.1 [-Jobs 16] [-ChunkItems 20] [-ItemTimeout 45] [-Speed instant|fast] [-UseBaselines]
# -Speed instant (default) skips animation waits: same results (checked on whole sweeps), about 4 times faster mod on. Once
# per release, -Speed fast for what only shows with animations playing.
# -Chunks 27,28: only those chunks, to rerun what a report says wasn't compared (the same -ChunkItems).
# -OnExtra "<scenario line>": a line only the mod-on sessions run, before the sweep: the positive control that a difference
# is found ("enemyhp 999998").
# -Imports 11: no A/B but what deck size costs. Mod on only, the reference run's deck imported that many times; logs in
# parts-<Kind>-i<n>. Two sizes, then scalereport.py: the items whose main thread time and allocations depend on the deck.
param([Parameter(Mandatory)][ValidateSet("cards", "relics", "potions", "encounters", "powers")][string]$Kind, [string]$GameDir = "", [int]$Jobs = [Environment]::ProcessorCount, [int]$ChunkItems = 0, [int]$ItemTimeout = 45, [ValidateSet("instant", "fast")][string]$Speed = "instant", [int]$TimeoutSec = 1800, [int]$StallSec = 300, [switch]$UseBaselines, [string]$OnExtra = "", [int[]]$Chunks = @(), [int]$Imports = 0)
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
# An encounter is a whole fight: smaller chunks. A session start costs ~10 s: bigger than this wastes cores, smaller wastes starts.
if ($ChunkItems -le 0) { $ChunkItems = if ($Kind -eq "encounters") { 6 } else { 20 } }
$work = Join-Path $out $(if ($Imports) { "parts-$Kind-i$Imports" } else { "parts-$Kind" })
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null
$started = Get-Date
$source = @{ off = Get-Content (Join-Path $Scenarios "sweep-$Kind-2k-off.txt"); on = Get-Content (Join-Path $Scenarios "sweep-$Kind-2k-on.txt") }
if ($Imports) {
    $source.on = foreach ($line in $source.on) {
        if ($line -match 'bigdeck import ref all all') { $line; if ($Imports -ge 2) { 2..$Imports | ForEach-Object { "cmd bigdeck import ref all cards" } } }
        elseif ($line -notmatch 'bigdeck import ref all cards') { $line }
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
    [pscustomobject]@{ Name = $name; Script = $script; Log = Join-Path $work "$name.log"; Side = $side; Chunk = $chunk; Part = $part; End = $first + $count; Items = $count }
}

# How many items: a session that only lists them.
$countScript = Join-Path $work "count.txt"
[IO.File]::WriteAllText($countScript, "timeout 120`nsweep $Kind 999999 0`nquit`n")
$countLog = Join-Path $work "count.log"
Invoke-Sessions -Jobs @([pscustomobject]@{ Name = "count"; Script = $countScript; Log = $countLog }) -GameDir $GameDir -MaxJobs 1 -TimeoutSec 180 -StallSec 120 | Out-Null
$match = Select-String -Path $countLog -Pattern "SWEEP $Kind`: (\d+) items" | Select-Object -First 1
if (-not $match) { throw "No item count in $countLog" }
$items = [int]$match.Matches[0].Groups[1].Value
$chunkCount = [int][Math]::Ceiling($items / $ChunkItems)
Write-Output "$Kind`: $items items, $chunkCount chunks of $ChunkItems, speed $Speed, up to $Jobs sessions at once"

$round = foreach ($chunk in 0..($chunkCount - 1)) {
    if ($Chunks.Count -and $Chunks -notcontains $chunk) { continue }
    foreach ($side in $(if ($Imports) { @("on") } else { @("off", "on") })) { New-PartJob $side $chunk 0 ($chunk * $ChunkItems) ([Math]::Min($ChunkItems, $items - $chunk * $ChunkItems)) }
}
$sessionProblems = [Collections.Generic.List[string]]::new()
$count = 0
while (@($round).Count -gt 0) {
    $outcome = Invoke-Sessions -Jobs $round -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -StallSec $StallSec -UseBaselines:$UseBaselines -Label "$Kind $(Get-GameTag $GameDir)"
    $count += @($round).Count
    $next = foreach ($job in $round) {
        $result = $outcome | Where-Object { $_.Name -eq $job.Name }
        if ($result.State -notin "done", "cached") { $sessionProblems.Add("$($job.Name): $($result.State)") }
        $abort = Select-String -Path $job.Log -Pattern "SWEEP ABORT next=(\d+)" -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($abort) {
            $from = [int]$abort.Matches[0].Groups[1].Value
            if ($from -lt $job.End) { New-PartJob $job.Side $job.Chunk ($job.Part + 1) $from ($job.End - $from) }
        }
    }
    $round = @($next)
}

Remove-TestMods $GameDir
if ($Imports) {
    $sessionProblems | ForEach-Object { Write-Output "session problem: $_" }
    Write-Output ("{0} at {1} imports: {2} sessions in {3:F1} min" -f $Kind, $Imports, $count, ((Get-Date) - $started).TotalMinutes)
    exit 0
}
$reportOutput = python -I (Join-Path $PSScriptRoot "sweepreport.py") $out $Kind
$bad = $LASTEXITCODE
$reportOutput | ForEach-Object { Write-Output $_ }
$sessionProblems | ForEach-Object { Write-Output "session problem: $_" }
Write-Output ("{0}: {1} sessions in {2:F1} min" -f $Kind, $count, ((Get-Date) - $started).TotalMinutes)
exit $(if ($bad -or $sessionProblems.Count) { 1 } else { 0 })
