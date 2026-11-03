# What deck size costs where a player sees it: the same rendered session at several sizes, uncapped, nothing else running, the
# frames split by where they go (FRAMES lines) and each play's time (PLAYTIME lines). Each size has the same card mix and the
# reference run's relics: every size is that run's deck imported N times (935 cards each). ladderreport.py makes the table.
#   powershell -File tools/autotest/ladder.ps1 -GameDir <copy> -Sizes "1,2,5,11,22,44" [-Coop] [-Plain] [-Turns 5] [-Tag x]
# -Coop: two peers, both playing; the host renders, the client is headless, as a second instance on this GPU would take half of
# the host's frames (real co-op: one game per PC). Logs: results/<version>/lad-<tag>c<N>-host.log, solo lad-<tag>s<N>.log.
# Headless rendering would hide what this is for: never run it through par.ps1.
# -TraceSec: a trace of the host, as coop.ps1 and run.ps1 record it. The game's card ids are 16 bits on the network: two peers above
# 32,768 cards each make it play the wrong cards and diverge (it can't go above 35 imports in co-op, 70 solo).
param([Parameter(Mandatory)][string]$GameDir, [string]$Sizes = "1,2,5,11,22,35", [switch]$Coop, [switch]$Plain, [int]$Turns = 5, [string]$Tag = "", [int]$TimeoutSec = 900, [int]$TraceSec = 0, [int]$TraceDelaySec = 0, [string]$TraceProfile = "cpu-sampling", [string]$TraceProviders = "", [switch]$Party5, [string]$Avoid = "", [switch]$Probe, [switch]$Verify, [string[]]$Extra = @())
$scenarioDir = Join-Path $PSScriptRoot "scenarios"

# -Avoid APOTHEOSIS,...: cards neither play picks (Apotheosis upgrades the whole deck: one play of it isn't a typical play).
# -Probe: every frame over 8 ms logged with the probed methods that ran in it (PerfProbe hitch lines), and the probe report at the end.
function Get-Body([string]$label) {
    if ($Avoid) { "avoid $Avoid" }
    if ($Probe) { "cmd bdperf probe on"; "cmd bdperf probe hitch 8"; "cmd bdperf probe reset" }
    # -Verify: every co-op snapshot compared with the game's own (SnapshotVerifier), the counts logged at the end. Slower: no timings.
    if ($Verify) { "cmd bdperf verify all" }
    # -Extra: scenario lines both peers run before the turns (e.g. a bdperf switch).
    $Extra
    "fpsstat"
    "gcstat"
    foreach ($t in 1..$Turns) {
        "cmd energy 50"
        "wait 0.5"
        "fpsstat"
        "mousesweep 4"
        "fpsstat hover t$t"
        "fpsstat"
        "gcstat"
        "mouseplay 4"
        "waitfor idle 300"
        "fpsstat mouse plays t$t"
        "gcstat mouse plays t$t"
        "fpsstat"
        "gcstat"
        "play 4"
        "waitfor idle 300"
        "fpsstat plays t$t"
        "gcstat plays t$t"
        "fpsstat"
        "gcstat"
        "endturn"
        "waitfor idle 900"
        "fpsstat end of turn t$t"
        "gcstat end of turn t$t"
    }
}

# Size 0: the starting deck and relics, nothing imported (the game's own cost per play, with or without this mod).
function Get-Imports([int]$n, [string]$suffix) {
    if ($n -le 0) { return @() }
    "cmd bigdeck import ref all all$suffix"
    if ($n -ge 2) { 2..$n | ForEach-Object { "cmd bigdeck import ref all cards$suffix" } }
}

# -Party5: the user's 5-player run instead of the reference run: on the host its big Necrobinder (4,112 cards with stacked Limitless
# enchantments, upgraded +10) imported N times, on the client the other players' 400 relics. Soul Nexus elite, as party5coop.
function Get-CoopImports([int]$n, [bool]$isHost) {
    if (-not $Party5 -or $n -le 0) { return Get-Imports $n " {hash:ref}" }
    if (-not $isHost) { return "cmd bigdeck import party5 0,1,3,4 relics {hash:party5}" }
    "cmd bigdeck import party5 2 all {hash:party5}"
    if ($n -ge 2) { 2..$n | ForEach-Object { "cmd bigdeck import party5 2 cards {hash:party5}" } }
}

$setup = @("cmd bdperf checks off", "cmd bdperf probe off", "cmd bdperf segments verify off")
$fight = if ($Party5) { "SOUL_NEXUS_ELITE" } else { "CULTISTS_NORMAL" }
foreach ($n in ($Sizes -split ',' | ForEach-Object { [int]$_ })) {
    $wait = 10 + 2 * $n
    if ($Coop) {
        $name = "lad-$($Tag)c$n"
        # Each peer ends by itself when its script does, whatever the other does: the rule of immortal until is tied to the round.
        $hostLines = @("timeout $($TimeoutSec - 60)", "mplobby NECROBINDER 2", "waitfor run 240", "uncap", "framedetail", "forcefocus", "immortal until 99") + (Get-CoopImports $n $true) + $setup +
            @("wait $wait", "enemyhp 999999", "wait 25", "fpsstat", "gcstat", "cmd fight $fight", "waitfor idle 900", "fpsstat combat start", "gcstat combat start", "wait 8", "fpsstat idle") + (Get-Body) + $(if ($Probe) { @("cmd bdperf probe log") } else { @() }) + $(if ($Verify) { @("cmd bdperf verify", "counters verified") } else { @() }) + @("effects end", "wait 15", "quit")
        $clientLines = @("timeout $($TimeoutSec - 60)", "mplobby IRONCLAD 2", "waitfor run 240", "immortal until 99", "forcefocus") + (Get-CoopImports $n $false) + $setup +
            @("wait $($wait + 6)", "enemyhp 999999", "waitfor combat 900", "waitfor idle 900", "wait 8") + (Get-Body) + $(if ($Verify) { @("wait 1500") } else { @() }) + @("wait 15", "quit")
        [IO.File]::WriteAllLines((Join-Path $scenarioDir "$name-host.txt"), [string[]]$hostLines)
        [IO.File]::WriteAllLines((Join-Path $scenarioDir "$name-client.txt"), [string[]]$clientLines)
        Write-Output "START $name $(Get-Date -Format HH:mm:ss)"
        $env:STS2_GAME_DIR = $GameDir
        $mods = if ($Plain) { @{} } else { @{ WithMods = $true } }
        & (Join-Path $PSScriptRoot "coop.ps1") -Name $name -ClientHeadless -TimeoutSec $TimeoutSec -TraceSec $TraceSec -TraceDelaySec $TraceDelaySec -TraceProfile $TraceProfile -TraceProviders $TraceProviders @mods
        Remove-Item (Join-Path $scenarioDir "$name-host.txt"), (Join-Path $scenarioDir "$name-client.txt")
    }
    else {
        $name = "lad-$($Tag)s$n"
        $lines = @("timeout $($TimeoutSec - 60)", "newrun NECROBINDER 1234", "waitfor run 120", "wait 2", "uncap", "framedetail", "forcefocus", "immortal") + (Get-Imports $n "") + $setup +
            @("enemyhp 999999", "wait 3", "fpsstat", "gcstat", "cmd fight CULTISTS_NORMAL", "waitfor idle 900", "fpsstat combat start", "gcstat combat start", "wait 8", "fpsstat idle") + (Get-Body) + @("effects end", "wait 2", "quit")
        [IO.File]::WriteAllLines((Join-Path $scenarioDir "$name.txt"), [string[]]$lines)
        Write-Output "START $name $(Get-Date -Format HH:mm:ss)"
        & (Join-Path $PSScriptRoot "run.ps1") -Name $name -GameDir $GameDir -TimeoutSec $TimeoutSec -TraceSec $TraceSec -TraceProfile $TraceProfile
        Remove-Item (Join-Path $scenarioDir "$name.txt")
    }
    Write-Output "END $name $(Get-Date -Format HH:mm:ss)"
}
Write-Output "ALL DONE"
