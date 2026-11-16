# Co-op tests in parallel: each scenario pair (scenarios/<name>-host.txt, -client.txt) headless, without Steam, on its own
# local port and its own copy of the user data, so nothing shows on screen and your settings aren't touched. Equivalence only:
# the peers must agree (no divergence, same enemy states), timings aren't meaningful; coop.ps1 for fps or hands-on.
# Logs: results/<game version>/<name>-host.log, -client.log.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/parcoop.ps1 -Names coopfast,coopclone -GameDir C:\Games\StS2-public-0.107.1 [-Plain] [-Jobs 6] [-TimeoutSec 900] [-Instant] [-OnOff]
# -Plain: only this mod, no other Workshop mod (coop.ps1 without -WithMods).
# Mod on vs mod off: peers agreeing says nothing of what the game would have done (both run the same code). Each scenario
# <x> whose mod-off twin <x>off or <x>-off is in -Names too is compared with it, peer by peer (host with host, client with
# client): the states at each turn start (--bdturnstates), the runstate lines and the checksums taken where both peers are
# settled (a 'checksum' line right after a 'barrier' or 'waitfor nocombat', 'wait' lines between allowed) and the synthetic foreign
# patches' counts ('STATE foreign', foreignpatch report), over what both ran (a shorter control compares its own length). -OnOff:
# every scenario also runs as its own mod-off twin (<x>-autooff: launched with the mod off, 'launchoff', so the co-op join lists
# and the run's creation are the game's own too, without its bdperf, bdopt, counters, effects and expect lines), compared the same
# way.
param([Parameter(Mandatory)][string[]]$Names, [string]$GameDir = "", [int]$Jobs = 6, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Plain, [switch]$Instant, [switch]$OnOff)
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
$waiting = [Collections.Generic.Queue[object]]::new()
# A scenario's mod-off twin: each peer launched with the mod off ('launchoff': Start-Peer adds --bdoff, which turns it off for
# the session before the main menu's --fastmp lobby exists; a script line comes too late there: the lobby is already open, so
# 'bdopt off' would ask for 'bdopt off yes', and v0.111+ would have built its join list with the mod on). Then 'coopmodetag
# off' after the lobby: the join list the game compares says "-off" on both peers, which joined. The run's creation is the
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
$groups = @(foreach ($name in $Names) {
    foreach ($twin in $(if ($OnOff) { @($false, $true) } else { @($false) })) {
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
        # Port: set from the slot the group runs in (Start-Peer's caller), unique while that slot is locked.
        [pscustomobject]@{ Name = $groupName; Scripts = $scripts; Port = 0; HostLog = Join-Path $out "$groupName-host.log"; ClientLog = Join-Path $out "$groupName-client.log" }
    }
})
foreach ($group in $groups) { $waiting.Enqueue($group) }
# Mod on / mod off pairs: <x> with <x>off, <x>-off (scenarios) or <x>-autooff (-OnOff).
$pairs = @(foreach ($group in $groups) {
    foreach ($suffix in "off", "-off", "-autooff") {
        $twin = $groups | Where-Object { $_.Name -eq "$($group.Name)$suffix" } | Select-Object -First 1
        if ($twin) { [pscustomobject]@{ On = $group; Off = $twin } }
    }
})
function Start-Peer($group, [string]$role, [string[]]$mode) {
    $log = if ($role -eq "host") { $group.HostLog } else { $group.ClientLog }
    Remove-Item $log -ErrorAction SilentlyContinue
    # --bdoff for a script with a 'launchoff' line (the -autooff twins, the mod-off controls).
    $arguments = @("--headless", "--force-steam", "off", "--bdport", $group.Port, "--bdturnstates", "--bdmute", "--log-file", "`"$log`"", "--bdtest", "`"$($group.Scripts[$role])`"") + $mode + @(Get-ScenarioLaunchArgs $group.Scripts[$role])
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden -ArgumentList $arguments
}
$started = Get-Date
$running = @{}
$done = [Collections.Generic.List[object]]::new()
$realAppData = $env:APPDATA
# Slots 40 and up, each held for the whole series (Lock-Slot): a slot another series holds is skipped.
$locked = [Collections.Generic.HashSet[int]]::new()
try {
    while ($waiting.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($slot in 0..($Jobs - 1)) {
            if ($running.ContainsKey($slot) -or $waiting.Count -eq 0) { continue }
            if (-not $locked.Contains(40 + $slot)) {
                if (-not (Lock-Slot (40 + $slot) -Try)) { continue }
                [void]$locked.Add(40 + $slot)
            }
            $group = $waiting.Dequeue()
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
            $running.Remove($slot)
        }
        $lines = @("{0}  co-op {1}: {2}/{3} done, {4} running, {5:F0} min so far" -f (Get-Date -Format "HH:mm:ss"), (Get-GameTag $GameDir), $done.Count, $groups.Count, $running.Count, ((Get-Date) - $started).TotalMinutes)
        Set-Content (Join-Path $Results "status.txt") $lines -Encoding utf8 -ErrorAction SilentlyContinue
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
    if ($entry.State -ne "done") { $problems += $entry.State }
    foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) {
        $role = if ($log -eq $entry.Group.HostLog) { "host" } else { "client" }
        # judge.py: not ended, a failed line, an error or a difference this mod found itself, a positive control that saw nothing...
        # A mod-off side (-autooff twin, *off / *van pair): launched off ('launchoff', never saved), or a hand-run 'bdopt off' saved
        # in the slot both peers share, so a peer may start with the mod off, the game's own code: no problem there.
        $modOff = $entry.Group.Name -match '(-autooff|off|van|vanilla)$'
        Invoke-Judge @($log) -ModOff:$modOff | Select-Object -First 3 | ForEach-Object { $problems += "$role`: " + ($_ -replace '^PROBLEM [^:]+: ', '') }
        Select-String -Path $log -CaseSensitive -Pattern "State divergence" -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $problems += "$role`: state divergence" }
        # The Workshop mods its game must have found (none with -Plain): a mods_STEAMTEST emptied by another series would go unseen.
        if (-not $Plain) { Get-MissingTestMods $log $GameDir | Select-Object -First 2 | ForEach-Object { $problems += "$role`: $_" } }
    }
    # Each peer logs its own player and plays; the enemies are the same for both. At each turn start only (--bdturnstates): a
    # script's state line comes when that peer gets there, maybe after the other one's plays of the turn.
    $enemies = foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) { ,@(Select-String -Path $log -Pattern "STATE floor .*: enemy " -ErrorAction SilentlyContinue | ForEach-Object { $_.Line -replace '^.*STATE ', '' }) }
    $same = (($enemies[0] -join "`n") -eq ($enemies[1] -join "`n"))
    if (-not $same) { $problems += "enemy states differ ($($enemies[0].Count) vs $($enemies[1].Count) lines)" }
    # runstate lines (outside combat, every player's gold, HP, potions, relics, deck): each peer logs them for all players.
    $runStates = foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) { ,@(Select-String -Path $log -Pattern "STATE (?![^:]*\(local\))[^:]+: (floor \d+, act |\S+ gold )" -ErrorAction SilentlyContinue | ForEach-Object { $_.Line -replace '^.*STATE ', '' }) }
    if (($runStates[0] -join "`n") -ne ($runStates[1] -join "`n")) { $problems += "run states differ ($($runStates[0].Count) vs $($runStates[1].Count) lines)" }
    if ($enemies[0].Count -eq 0 -and $runStates[0].Count -eq 0) { $problems += "no enemy or run state line" }
    Write-Output ("{0,-16} {1,-8} {2,4} s  {3} enemy, {4} run state lines{5}" -f $entry.Group.Name, $entry.State, $entry.Seconds, $enemies[0].Count, $runStates[0].Count, $(if ($problems.Count) { "  PROBLEMS: " + ($problems -join "; ") } else { "  agree" }))
    if ($problems.Count) { $bad++ }
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
function Get-PairLines([string]$log, [string[]]$checksums) {
    $pattern = "STATE floor \d+ round |STATE (?![^:]*\(local\))[^:]+: (floor \d+, act |\S+ gold )|STATE foreign "
    if ($checksums.Count) { $pattern += "|CHECKSUM (" + (($checksums | ForEach-Object { [regex]::Escape($_) }) -join "|") + "): " }
    @(Select-String -Path $log -Pattern $pattern -ErrorAction SilentlyContinue | ForEach-Object { ($_.Line -replace '^.*?((STATE|CHECKSUM) )', '$1') -replace '^(CHECKSUM [^:]+: \w+).*$', '$1' })
}
foreach ($pair in $pairs) {
    foreach ($role in "host", "client") {
        $onLog = if ($role -eq "host") { $pair.On.HostLog } else { $pair.On.ClientLog }
        $offLog = if ($role -eq "host") { $pair.Off.HostLog } else { $pair.Off.ClientLog }
        $checksums = @(Get-SettledChecksums $pair.On.Scripts[$role])
        $on = @(Get-PairLines $onLog $checksums)
        $off = @(Get-PairLines $offLog $checksums)
        $common = [Math]::Min($on.Count, $off.Count)
        $first = -1
        for ($i = 0; $i -lt $common; $i++) { if ($on[$i] -ne $off[$i]) { $first = $i; break } }
        # A twin made from the same script (-autooff) runs the same lines: a count that differs is a session that went another way
        # at its end (a combat over a turn sooner), which a comparison of the common lines alone would call identical.
        $sameScript = $pair.Off.Name -eq "$($pair.On.Name)-autooff"
        $verdict = if ($common -eq 0) { "NOT COMPARED" } elseif ($first -ge 0) { "DIFFERENT" } elseif ($sameScript -and $on.Count -ne $off.Count) { "DIFFERENT LENGTHS" } else { "IDENTICAL" }
        $note = if ($on.Count -ne $off.Count) { " (compared the first $common`: $($on.Count) lines mod on, $($off.Count) mod off)" } else { "" }
        Write-Output ("{0} vs {1} ({2}): {3} lines, {4}{5}" -f $pair.On.Name, $pair.Off.Name, $role, $common, $verdict, $note)
        if ($first -ge 0) { Write-Output "    on : $($on[$first])"; Write-Output "    off: $($off[$first])" }
        if ($verdict -ne "IDENTICAL") { $bad++ }
    }
}
Write-Output ("done in {0:F1} min, {1} with problems, logs in {2}" -f ((Get-Date) - $started).TotalMinutes, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
