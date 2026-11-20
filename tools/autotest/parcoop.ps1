# Co-op tests in parallel: each scenario pair (scenarios/<name>-host.txt, -client.txt) headless, without Steam, on its own
# local port and its own copy of the user data, so nothing shows on screen and your settings aren't touched. Equivalence only:
# the peers must agree (no divergence, same enemy states), timings aren't meaningful; coop.ps1 for fps or hands-on.
# Logs: results/<game version>/<name>-host.log, -client.log.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/parcoop.ps1 -Names coopfast,coopclone -GameDir C:\Games\StS2-public-0.111.1 [-Plain] [-Jobs 6] [-OffJobs 1] [-TimeoutSec 900] [-Instant] [-OnOff [-UseBaselines] [-PatchedOff]] [-FrameLimitMs 10000]
# -Plain: only this mod, no other Workshop mod (coop.ps1 without -WithMods).
# Mod on vs mod off: peers agreeing says nothing of what the game would have done (both run the same code). Each scenario
# <x> whose mod-off twin <x>off or <x>-off is in -Names too is compared with it, peer by peer (host with host, client with
# client): the states at each turn start (--bdturnstates), the runstate lines and the checksums taken where both peers are
# settled (a 'checksum' line right after a 'barrier' or 'waitfor nocombat', 'wait' lines between allowed) and the synthetic foreign
# patches' counts ('STATE foreign', foreignpatch report), over what both ran (a shorter control compares its own length). -OnOff:
# every scenario also runs as its own mod-off twin (<x>-autooff: launched with the mod off, 'launchoff', so the co-op join lists
# and the run's creation are the game's own too, without its bdperf, bdopt, counters, effects and expect lines), compared the same
# way. No twin for a scenario that is a mod-off side itself (its host launched off, 'launchoff'; a name ending off or van), nor for
# one whose own mod-off pair (<x>off, <x>-off, <x>van) is in -Names: it is compared with that one. The pairs start longest first
# (results/<version>/durations.tsv, else the host's last log of that script; unknown ones first, the mod-off sides first among
# them: the longest by far at these deck sizes).
# The -autooff twins are vanilla pairs: both peers launched with --bdvanilla too (no patch of this mod installed but the test
# tooling and CoopModeTag, whose join entry then says "+off", as a player's who started with the mod off: the twin's 'coopmodetag
# off' line checks it, and --bdoff from its 'launchoff' line still turns the switch off before the lobby), as solo mod-off sides
# are; each log must say so (BDVANILLA) and no other may. Kept when the pair passed (results/baselines/<version>/coop/, keyed by
# Get-CoopBaselineKey), reused with -UseBaselines. -PatchedOff: the twins the old way, every patch installed then off (--bdoff),
# never kept. The explicit mod-off scenarios (coopoff, coopleakoff, outside-coopoff...) still run patched-off, never cached.
# -OffJobs: at most that many mod-off pairs at once (default -Jobs), mod-on pairs filling the other slots: '-OffJobs 1 -Jobs 3'
# runs a 1,900-card mod-off pair, which must not be slowed past the network timeout, beside two mod-on pairs.
# -FrameLimitMs: each peer's longest frame (CombatStartTimer's 'longest frames' at each combat start, where a mod-off co-op pair
# freezes) and the host's highest ping (NetQualityTracker, every 20 s) are printed per pair, and one at or above it is a problem
# (NETWORK MARGIN): the game drops a peer silent for 20 s (ENet's timeout), half of it by default. A pair cut short by a
# disconnect fails anyway; this says when one came near, or which pair may not run beside others.
param([Parameter(Mandatory)][string[]]$Names, [string]$GameDir = "", [int]$Jobs = 6, [int]$OffJobs = 0, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Plain, [switch]$Instant, [switch]$OnOff, [switch]$UseBaselines, [switch]$PatchedOff, [int]$FrameLimitMs = 10000)
if ($OffJobs -le 0) { $OffJobs = $Jobs }
$Names = @($Names | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
$exe = Join-Path $GameDir "SlayTheSpire2.exe"
if ($Plain) {
    Remove-TestMods $GameDir
    # A mods_STEAMTEST that isn't this tooling's, or that another series on this copy uses, would still load: not "only this mod".
    if (Test-Path (Join-Path $GameDir "mods_STEAMTEST")) { throw "$(Join-Path $GameDir 'mods_STEAMTEST') exists (another series on this copy uses it, or it isn't this tooling's): -Plain would load its mods." }
}
else { Add-TestMods $GameDir }
$folder = Join-Path $out "coop-scripts"
New-Item -ItemType Directory -Force $folder | Out-Null
$waiting = [Collections.Generic.List[object]]::new()
# A scenario's mod-off twin: each peer launched with the mod off ('launchoff': Start-Peer adds --bdoff, which turns it off for
# the session before the main menu's --fastmp lobby exists; a script line comes too late there: the lobby is already open, so
# 'bdopt off' would ask for 'bdopt off yes', and v0.111+ would have built its join list with the mod on). Then 'coopmodetag
# off' after the lobby: the join list the game compares says "+off" on both peers, which joined. The run's creation is the
# game's own too; without the lines that only make sense with the mod on (bdperf comparisons, option switches, counters, effects and their
# checks, positive controls, the mod's own probes: button input checks, tip rebuilds, a fake conflicting patch, a fault probe, a
# rebuild check). The synthetic foreign patches stay (they stand for another mod every side has), only 'foreignpatch require'
# (a check of this mod's tolerance) goes.
$ModOnlyLine = '^\s*(cmd bdperf |cmd bdopt\b|counters\b|effects\b|effectcheck\b|expect\b|buttoninput\s+expect|tipsrebuilt\b|checktips\b|fakeconflict\b|foreignpatch\s+require\b|faultprobe\b|rebuildcheck\b|runtimebackoff\b|coopmodetag\b|launchoff\b)'
function New-OffTwin([string]$source, [string]$target) {
    $lines = [Collections.Generic.List[string]]::new()
    $placed = $false
    foreach ($line in Get-Content $source) {
        if ($line -match $ModOnlyLine) { continue }
        if (-not $placed -and $line -match '^\s*(mplobby|newrun)\b') {
            $lines.Add("launchoff")
            $lines.Add($line)
            if ($line -match '^\s*mplobby\b') { $lines.Add("coopmodetag off") }
            $placed = $true
            continue
        }
        $lines.Add($line)
    }
    if (-not $placed) { throw "$source has no 'mplobby' line: no place for its mod-off twin's 'launchoff'" }
    [IO.File]::WriteAllText($target, ($lines -join "`n") + "`n")
}
# Whether -OnOff makes this scenario's -autooff twin: not for a mod-off side, nor when its explicit mod-off pair runs too.
function Test-NeedsTwin([string]$name) {
    if ($name -match '(off|van|vanilla)$') { return $false }
    $hostScript = Join-Path $Scenarios "$name-host.txt"
    if ((Test-Path $hostScript) -and (Select-String -Path $hostScript -Pattern '^\s*launchoff\s*$' -Quiet)) { return $false }
    foreach ($suffix in "off", "-off", "van") { if ($Names -contains "$name$suffix") { return $false } }
    $true
}
$groups = @(foreach ($name in $Names) {
    $makeTwin = $OnOff -and (Test-NeedsTwin $name)
    if ($OnOff -and -not $makeTwin) { Write-Host "-OnOff: no $name-autooff twin ($name is a mod-off side, or its own mod-off pair is in -Names)" }
    foreach ($twin in $(if ($makeTwin) { @($false, $true) } else { @($false) })) {
        $scripts = @{}
        $groupName = if ($twin) { "$name-autooff" } else { $name }
        foreach ($role in "host", "client") {
            $source = Join-Path $Scenarios "$name-$role.txt"
            if (-not (Test-Path $source)) { throw "No scenario $source" }
            $scripts[$role] = $source
            if ($Instant) {
                $scripts[$role] = Join-Path $folder "$groupName-$role.txt"
                [IO.File]::WriteAllText($scripts[$role], ((@("speed instant") + @(Get-Content $source | Where-Object { $_ -notmatch '^speed ' })) -join "`n") + "`n")
            }
            if ($twin) {
                $twinScript = Join-Path $folder "$groupName-$role.txt"
                New-OffTwin $scripts[$role] $twinScript
                $scripts[$role] = $twinScript
            }
        }
        # Port: set from the slot the group runs in (Start-Peer's caller), unique while that slot is locked. Vanilla: an -autooff twin
        # launched with --bdvanilla on both peers (not with -PatchedOff).
        [pscustomobject]@{ Name = $groupName; Scripts = $scripts; Port = 0; HostLog = Join-Path $out "$groupName-host.log"; ClientLog = Join-Path $out "$groupName-client.log"; Vanilla = ($twin -and -not $PatchedOff) }
    }
})
$modOffSide = '(-autooff|off|van|vanilla)$'
$done = [Collections.Generic.List[object]]::new()
# A vanilla twin kept from an earlier series with the same key (-UseBaselines): its logs copied, not run.
$coopBaselines = Join-Path $BaselineRoot "$(Get-GameTag $GameDir)\coop"
foreach ($group in $groups | Where-Object { $_.Vanilla }) {
    $key = Get-CoopBaselineKey $GameDir $group.Scripts["host"] $group.Scripts["client"] $Plain.IsPresent
    $group | Add-Member -NotePropertyName CacheHost -NotePropertyValue (Join-Path $coopBaselines "$($group.Name)-$key-host.log")
    $group | Add-Member -NotePropertyName CacheClient -NotePropertyValue (Join-Path $coopBaselines "$($group.Name)-$key-client.log")
}
# Longest first (durations.tsv, else the host's last log of that script), those nothing is known of first: the mod-off sides
# first among them (several times longer than the mod-on ones: a long one starting last would set the series' length).
$durationFile = Join-Path $out $DurationsFileName
$durations = Read-Durations $durationFile
$index = 0
$ranked = foreach ($group in @($groups | Where-Object { $_.Name -match $modOffSide }) + @($groups | Where-Object { $_.Name -notmatch $modOffSide })) {
    $key = Get-DurationKey ([pscustomobject]@{ Name = "coop:$($group.Name)"; Script = $group.Scripts["host"] }) $false
    $seconds = if ($durations.ContainsKey($key)) { $durations[$key] } else { Get-LogDuration $group.HostLog $group.Scripts["host"] }
    $group | Add-Member -NotePropertyName DurationKey -NotePropertyValue $key
    [pscustomobject]@{ Group = $group; Length = $(if ($null -ne $seconds) { [double]$seconds } else { $UnknownLength }); Index = $index++ }
}
foreach ($entry in @($ranked | Sort-Object -Property @{ Expression = { $_.Length }; Descending = $true }, @{ Expression = { $_.Index }; Descending = $false })) {
    $group = $entry.Group
    if ($UseBaselines -and $group.Vanilla -and (Test-Path $group.CacheHost) -and (Test-Path $group.CacheClient)) {
        Copy-Item $group.CacheHost $group.HostLog -Force
        Copy-Item $group.CacheClient $group.ClientLog -Force
        $done.Add([pscustomobject]@{ Group = $group; State = "cached"; Seconds = 0 })
        continue
    }
    $waiting.Add($group)
}
# Mod on / mod off pairs: <x> with <x>off, <x>-off, <x>van (scenarios) or <x>-autooff (-OnOff).
$pairs = @(foreach ($group in $groups) {
    foreach ($suffix in "off", "-off", "van", "-autooff") {
        $twin = $groups | Where-Object { $_.Name -eq "$($group.Name)$suffix" } | Select-Object -First 1
        if ($twin) { [pscustomobject]@{ On = $group; Off = $twin } }
    }
})
function Start-Peer($group, [string]$role, [string[]]$mode) {
    $log = if ($role -eq "host") { $group.HostLog } else { $group.ClientLog }
    Remove-Item $log -ErrorAction SilentlyContinue
    # --bdoff for a script with a 'launchoff' line (the -autooff twins, the mod-off controls); --bdvanilla for a vanilla twin.
    $arguments = @("--headless", "--force-steam", "off", "--bdport", $group.Port, "--bdturnstates", "--bdmute", "--log-file", "`"$log`"", "--bdtest", "`"$($group.Scripts[$role])`"") + $mode + @(Get-ScenarioLaunchArgs $group.Scripts[$role])
    if ($group.Vanilla) { $arguments += $VanillaArg }
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden -ArgumentList $arguments
}
$started = Get-Date
$running = @{}
$realAppData = $env:APPDATA
# Slots 40 and up, each held for the whole series (Lock-Slot): a slot another series holds is skipped.
$locked = [Collections.Generic.HashSet[int]]::new()
try {
    while ($waiting.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($slot in 0..($Jobs - 1)) {
            if ($running.ContainsKey($slot) -or $waiting.Count -eq 0) { continue }
            # The first waiting pair this slot may take: a mod-off one only while fewer than -OffJobs run.
            $offRunning = @($running.Values | Where-Object { $_.Group.Name -match $modOffSide }).Count
            $group = $waiting | Where-Object { $_.Name -notmatch $modOffSide -or $offRunning -lt $OffJobs } | Select-Object -First 1
            if (-not $group) { break }
            if (-not $locked.Contains(40 + $slot)) {
                if (-not (Lock-Slot (40 + $slot) -Try)) { continue }
                [void]$locked.Add(40 + $slot)
            }
            [void]$waiting.Remove($group)
            # Each test slot has its own port: two series side by side (other slots) never share one, as a port per group of this
            # series would (both from 33800: a host failing to listen, a client joining the other series' lobby).
            $group.Port = Get-SlotPort (40 + $slot)
            $env:APPDATA = Sync-Slot (40 + $slot) -Coop
            $h = Start-Peer $group "host" @("--fastmp", "host_standard")
            $env:APPDATA = $realAppData
            $running[$slot] = [pscustomobject]@{ Group = $group; Host = $h; Client = $null; Slot = $slot; Started = Get-Date }
        }
        Start-Sleep 2
        foreach ($slot in @($running.Keys)) {
            $run = $running[$slot]
            $age = ((Get-Date) - $run.Started).TotalSeconds
            # The client joins once the host had time to listen.
            if (-not $run.Client -and $age -gt 12) {
                $env:APPDATA = Join-Path $SlotRoot "slot$(40 + $run.Slot)"
                $run.Client = Start-Peer $run.Group "client" @("--fastmp", "join", "--clientId", "1001")
                $env:APPDATA = $realAppData
                continue
            }
            if (-not $run.Client) { continue }
            $state = $null
            $silent = 0
            foreach ($log in $run.Group.HostLog, $run.Group.ClientLog) {
                if (Test-Path $log) { $silent = [Math]::Max($silent, ((Get-Date) - (Get-Item $log).LastWriteTime).TotalSeconds) }
            }
            # The test runner never started (no Debug build in the copy, or it failed to load): nothing would ever run.
            $runner = if ($age -gt 90 -and (Test-Path $run.Group.HostLog)) { [bool](Select-String -Path $run.Group.HostLog -Pattern "\] AutoTest " -Quiet) } else { $true }
            if (-not $runner) { $state = "AUTOTEST NOT LOADED" }
            elseif ($run.Host.HasExited -and $run.Client.HasExited) { $state = "done" }
            elseif ($age -gt $TimeoutSec) { $state = "TIMEOUT" }
            elseif ($silent -gt $StallSec) { $state = "STALLED" }
            elseif (($run.Host.HasExited -or $run.Client.HasExited) -and $silent -gt 30) { $state = "ONE PEER LEFT" }
            if (-not $state) { continue }
            foreach ($p in $run.Host, $run.Client) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
            $done.Add([pscustomobject]@{ Group = $run.Group; State = $state; Seconds = [int]$age })
            # Its length, for the next series' order.
            if ($state -in "done", "TIMEOUT") { Save-Durations $durationFile @{ $run.Group.DurationKey = [int]$age } }
            $running.Remove($slot)
        }
        $lines = @("{0}  co-op {1}: {2}/{3} done, {4} running, {5:F0} min so far" -f (Get-Date -Format "HH:mm:ss"), (Get-GameTag $GameDir), $done.Count, $groups.Count, $running.Count, ((Get-Date) - $started).TotalMinutes)
        Set-Content (Join-Path $Results "status-coop-$(Get-GameTag $GameDir).txt") $lines -Encoding utf8 -ErrorAction SilentlyContinue
    }
}
finally {
    $env:APPDATA = $realAppData
    foreach ($run in $running.Values) { foreach ($p in $run.Host, $run.Client) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } } }
    foreach ($slot in $locked) { Unlock-Slot $slot }
    Remove-TestMods $GameDir
}
$bad = 0
foreach ($entry in $done) {
    $problems = @()
    if ($entry.State -notin "done", "cached") { $problems += $entry.State }
    foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) {
        $role = if ($log -eq $entry.Group.HostLog) { "host" } else { "client" }
        # judge.py: not ended, a failed line, an error or a difference this mod found itself, a positive control that saw nothing...
        # A mod-off side (-autooff twin, *off / *van pair): launched off ('launchoff', never saved), or a hand-run 'bdopt off' saved
        # in the slot both peers share, so a peer may start with the mod off, the game's own code: no problem there.
        $modOff = $entry.Group.Name -match $modOffSide
        Invoke-Judge @($log) -ModOff:$modOff | Select-Object -First 3 | ForEach-Object { $problems += "$role`: " + ($_ -replace '^PROBLEM [^:]+: ', '') }
        Select-String -Path $log -CaseSensitive -Pattern "State divergence" -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $problems += "$role`: state divergence" }
        # The Workshop mods its game must have found (none with -Plain): a mods_STEAMTEST emptied by another series would go unseen.
        if (-not $Plain) { Get-MissingTestMods $log $GameDir | Select-Object -First 2 | ForEach-Object { $problems += "$role`: $_" } }
        # Launched as its group says: a vanilla twin's peers both vanilla (BDVANILLA), no other peer (a mod-on side launched vanilla
        # would compare the game with itself).
        $vanilla = (Test-Path $log) -and [bool](Select-String -Path $log -Pattern $VanillaLinePattern -Quiet)
        if ($entry.Group.Vanilla -and -not $vanilla -and (Test-Path $log)) { $problems += "$role`: launched with $VanillaArg, but its log has no BDVANILLA line: not a vanilla peer" }
        elseif (-not $entry.Group.Vanilla -and $vanilla) { $problems += "$role`: its log has the BDVANILLA line, but this pair is not a vanilla twin: it ran without the mod's patches" }
    }
    # How near the network timeout each peer came: its longest frame at a combat start (CombatStartTimer's list, longest first) and
    # the host's highest ping. Not every frame of the session: those of a combat start, where the long freezes are.
    $margins = foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) {
        $role = if ($log -eq $entry.Group.HostLog) { "host" } else { "client" }
        $frame = (@(Select-String -Path $log -Pattern "combat start: \d+ ms total.*longest frames: (\d+) ms" -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Matches[0].Groups[1].Value }) | Measure-Object -Maximum).Maximum
        $ping = (@(Select-String -Path $log -Pattern "\[NetQualityTracker\]\s+\d+ - Ping: (\d+(?:\.\d+)?)" -ErrorAction SilentlyContinue | ForEach-Object { [double]$_.Matches[0].Groups[1].Value }) | Measure-Object -Maximum).Maximum
        [pscustomobject]@{ Role = $role; Frame = $frame; Ping = $ping }
        if ($frame -ge $FrameLimitMs) { $problems += "NETWORK MARGIN: the $role's longest frame $frame ms at a combat start, at or above -FrameLimitMs $FrameLimitMs (the game drops a peer silent for 20 s): too near a disconnect; run it with fewer pairs beside it (-OffJobs, -Jobs) or not at all" }
        if ($ping -ge $FrameLimitMs) { $problems += "NETWORK MARGIN: the $role saw a ping of $([int]$ping) ms, at or above -FrameLimitMs $FrameLimitMs" }
    }
    $marginText = ($margins | ForEach-Object { "{0} {1} ms{2}" -f $_.Role, $(if ($null -ne $_.Frame) { $_.Frame } else { "?" }), $(if ($null -ne $_.Ping) { ", ping {0:F0}" -f $_.Ping } else { "" }) }) -join "; "
    # Each peer logs its own player and plays; the enemies are the same for both. At each turn start only (--bdturnstates): a
    # script's state line comes when that peer gets there, maybe after the other one's plays of the turn. A player-side turn
    # start only where both peers took it before any action of the turn ran (parlib.ps1's Get-TurnStates: the other player's
    # first card may already have run on the slower peer, the game's checksums equal).
    $peerTurns = @((Get-TurnStates $entry.Group.HostLog), (Get-TurnStates $entry.Group.ClientLog))
    $enemyStates = Compare-PeerTurnStates $peerTurns @("host", "client") { $_ -cmatch '^[^:]+: enemy ' }
    $problems += @($enemyStates.Problems)
    # runstate lines (outside combat, every player's gold, HP, potions, relics, deck): each peer logs them for all players.
    $runStates = foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) { ,@(Select-String -Path $log -Pattern "STATE (?![^:]*\(local\))[^:]+: (floor \d+, act |\S+ gold )" -ErrorAction SilentlyContinue | ForEach-Object { $_.Line -replace '^.*STATE ', '' }) }
    if (($runStates[0] -join "`n") -ne ($runStates[1] -join "`n")) { $problems += "run states differ ($($runStates[0].Count) vs $($runStates[1].Count) lines)" }
    if ($enemyStates.Lines -eq 0 -and $runStates[0].Count -eq 0) { $problems += "no enemy or run state line" }
    $skippedText = if ($enemyStates.Skipped) { " ($($enemyStates.Skipped) player turn starts after the other player's action not compared)" } else { "" }
    Write-Output ("{0,-16} {1,-8} {2,4} s  {3} enemy, {4} run state lines{5}, longest frame {6}{7}" -f $entry.Group.Name, $entry.State, $entry.Seconds, $enemyStates.Lines, $runStates[0].Count, $skippedText, $marginText, $(if ($problems.Count) { "  PROBLEMS: " + ($problems -join "; ") } else { "  agree" }))
    if ($problems.Count) { $bad++ }
    # A vanilla twin that ran to its end with nothing to say: kept for -UseBaselines (both peers' logs, under its key).
    if ($entry.Group.Vanilla -and $entry.State -eq "done" -and -not $problems.Count) {
        try {
            New-Item -ItemType Directory -Force $coopBaselines | Out-Null
            Copy-Item $entry.Group.HostLog $entry.Group.CacheHost -Force
            Copy-Item $entry.Group.ClientLog $entry.Group.CacheClient -Force
        }
        catch { Write-Output "note: $($entry.Group.Name) not kept as a baseline: $($_.Exception.Message)" }
    }
}
# The labels of a script's checksums taken where both peers are settled: right after a barrier (every player got there) or the
# combat's end, 'wait' lines and other such checksums between allowed. Elsewhere a checksum comes when that peer gets there, maybe
# before the other peer's last action reached it, at a pace that differs mod on and off.
function Get-SettledChecksums([string]$script) {
    $labels = @()
    $settled = $false
    foreach ($line in Get-Content $script) {
        $text = $line.Trim()
        if (-not $text -or $text.StartsWith('#') -or $text -match '^wait \d') { continue }
        if ($settled -and $text -match '^checksum (.+?)( vanilla)?$') { $labels += $Matches[1].Trim(); continue }
        $settled = $text -match '^(barrier |waitfor nocombat\b)'
    }
    $labels
}

# Mod on vs mod off, peer by peer: what each peer saw at each turn start (its player, its piles, the enemies), the run states,
# the settled checksums (the whole combat state: what counts, HP and powers don't show) and the synthetic foreign patches'
# counts ('STATE foreign <label> <key> <n>', foreignpatch report: a call this mod drops changes only that count; coop-foreign
# installs them before its imports and reports after a barrier, so both sides count the same calls). Checksums without their timing.
# $skip: line numbers left out (Get-UnsyncedTurnLines: a player-side turn start either side took after the other player's action ran).
function Get-PairLines([string]$log, [string[]]$checksums, $skip) {
    $pattern = "STATE floor \d+ round |STATE (?![^:]*\(local\))[^:]+: (floor \d+, act |\S+ gold )|STATE foreign "
    if ($checksums.Count) { $pattern += "|CHECKSUM (" + (($checksums | ForEach-Object { [regex]::Escape($_) }) -join "|") + "): " }
    @(Select-String -Path $log -Pattern $pattern -ErrorAction SilentlyContinue | Where-Object { $null -eq $skip -or -not $skip.Contains($_.LineNumber) } | ForEach-Object { ($_.Line -replace '^.*?((STATE|CHECKSUM) )', '$1') -replace '^(CHECKSUM [^:]+: \w+).*$', '$1' })
}
# Limitless's per-player combat RNG on v0.107.x (Patch_PerPlayerCombatRng: RunRngSet.GetRng's prefix picks the RNG slot from the
# running action's OwnerId, an AsyncLocal): the game's end of turn (phase two, the enemy turn, the next turn's start) runs inside
# whichever player's ReadyToBeginEnemyTurnAction executed last (CombatManager.SetReadyToBeginEnemyTurn -> TaskHelper.RunSafely), a
# network race, so a random draw there (Parrying Shield's target) comes from another slot when the last ready player differs
# between the sides. v0.111 and v0.113 resume the turn loop in its own context (it awaits CombatTurnState's
# BeginEnemyTurnSignalSource, which SetReadyToBeginEnemyTurn completes). Returns, per "floor F round N" in order, the owner of the
# last ReadyToBeginEnemyTurnAction before that round's first "STATE floor F round N Enemy" line; empty when the game isn't v0.107.x
# or the log hasn't Limitless's "per-player combat rng prefix: ACTIVE" line.
function Get-LastReadyOwners([string]$log) {
    $owners = [ordered]@{}
    if (-not (Test-Path $log)) { return $owners }
    $lines = Get-Content $log
    if (-not ($lines | Select-String -SimpleMatch -Quiet "release=v0.107.") -or -not ($lines | Select-String -SimpleMatch -Quiet "per-player combat rng prefix: ACTIVE")) { return $owners }
    $last = $null
    foreach ($line in $lines) {
        if ($line -match 'Executing action: ReadyToBeginEnemyTurnAction (\d+)') { $last = $Matches[1]; continue }
        if ($line -match 'STATE floor (\d+) round (\d+) Enemy: ') {
            $key = "floor $($Matches[1]) round $($Matches[2])"
            if (-not $owners.Contains($key)) { $owners[$key] = $last }
            $last = $null
        }
    }
    $owners
}

# The first round whose last ready player differs between the two host logs (the key "floor F round N"), or $null.
function Get-PaceRound([string]$onHost, [string]$offHost) {
    $on = Get-LastReadyOwners $onHost
    $off = Get-LastReadyOwners $offHost
    foreach ($key in $on.Keys) {
        if ($off.Contains($key) -and $on[$key] -ne $off[$key]) { return $key }
    }
    $null
}

# The lines before the first "STATE <key> Enemy" line (what the race can't have touched yet).
function Select-BeforeRound([string[]]$lines, [string]$key) {
    $cut = "STATE $key Enemy:"
    $kept = @()
    foreach ($line in $lines) {
        if ($line.StartsWith($cut)) { break }
        $kept += $line
    }
    $kept
}

foreach ($pair in $pairs) {
    $paceRound = Get-PaceRound $pair.On.HostLog $pair.Off.HostLog
    foreach ($role in "host", "client") {
        $onLog = if ($role -eq "host") { $pair.On.HostLog } else { $pair.On.ClientLog }
        $offLog = if ($role -eq "host") { $pair.Off.HostLog } else { $pair.Off.ClientLog }
        $checksums = @(Get-SettledChecksums $pair.On.Scripts[$role])
        # Mod on and off go at different paces: a player-side turn start taken after the other player's first action of the turn
        # ran on one side only is left out on both (the enemy-side ones and the settled checksums still show a real difference).
        $unsynced = Get-UnsyncedTurnLines $onLog $offLog
        $on = @(Get-PairLines $onLog $checksums $unsynced.First)
        $off = @(Get-PairLines $offLog $checksums $unsynced.Second)
        $fullOn = $on.Count; $fullOff = $off.Count
        if ($paceRound) {
            $on = @(Select-BeforeRound $on $paceRound)
            $off = @(Select-BeforeRound $off $paceRound)
        }
        $common = [Math]::Min($on.Count, $off.Count)
        $first = -1
        for ($i = 0; $i -lt $common; $i++) { if ($on[$i] -ne $off[$i]) { $first = $i; break } }
        # A twin made from the same script (-autooff) runs the same lines: a count that differs is a session that went another way
        # at its end (a combat over a turn sooner), which a comparison of the common lines alone would call identical.
        $sameScript = $pair.Off.Name -eq "$($pair.On.Name)-autooff"
        $verdict = if ($common -eq 0) { "NOT COMPARED" } elseif ($first -ge 0) { "DIFFERENT" } elseif ($sameScript -and $on.Count -ne $off.Count) { "DIFFERENT LENGTHS" } else { "IDENTICAL" }
        # Everything before the round whose last ready player differs is identical: the rest is the race's (not a problem).
        if ($paceRound -and $verdict -eq "IDENTICAL") { $verdict = "PACE (Limitless per-player RNG: last ready player differs from $paceRound)" }
        $note = if ($paceRound) { " (compared up to $paceRound's enemy turn: $common of $fullOn lines mod on, $fullOff mod off)" }
            elseif ($on.Count -ne $off.Count) { " (compared the first $common`: $($on.Count) lines mod on, $($off.Count) mod off)" } else { "" }
        if ($unsynced.Blocks) { $note += " ($($unsynced.Blocks) player turn starts after the other player's action left out)" }
        Write-Output ("{0} vs {1} ({2}): {3} lines, {4}{5}" -f $pair.On.Name, $pair.Off.Name, $role, $common, $verdict, $note)
        if ($first -ge 0) { Write-Output "    on : $($on[$first])"; Write-Output "    off: $($off[$first])" }
        if ($verdict -ne "IDENTICAL" -and -not $verdict.StartsWith("PACE ")) { $bad++ }
    }
}
Write-Output ("done in {0:F1} min, {1} with problems, logs in {2}" -f ((Get-Date) - $started).TotalMinutes, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
