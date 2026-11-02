# Co-op tests in parallel: each scenario pair (scenarios/<name>-host.txt, -client.txt) headless, without Steam, on its own
# local port and its own copy of the user data, so nothing shows on screen and your settings aren't touched. Equivalence only:
# the peers must agree (no divergence, same enemy states), timings aren't meaningful; coop.ps1 for fps or hands-on.
# Logs: results/<game version>/<name>-host.log, -client.log.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/parcoop.ps1 -Names coopfast,coopclone -GameDir C:\Games\StS2-public-0.107.1 [-Plain] [-Jobs 6] [-TimeoutSec 900] [-Instant]
# -Plain: only this mod, no other Workshop mod (coop.ps1 without -WithMods).
param([Parameter(Mandatory)][string[]]$Names, [string]$GameDir = "", [int]$Jobs = 6, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Plain, [switch]$Instant)
$Names = @($Names | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
$exe = Join-Path $GameDir "SlayTheSpire2.exe"
if ($Plain) { Remove-TestMods $GameDir } else { Add-TestMods $GameDir }
$folder = Join-Path $out "coop-scripts"
New-Item -ItemType Directory -Force $folder | Out-Null
$waiting = [Collections.Generic.Queue[object]]::new()
$port = 33800
foreach ($name in $Names) {
    $scripts = @{}
    foreach ($role in "host", "client") {
        $source = Join-Path $Scenarios "$name-$role.txt"
        if (-not (Test-Path $source)) { throw "No scenario $source" }
        $scripts[$role] = $source
        if ($Instant) {
            $scripts[$role] = Join-Path $folder "$name-$role.txt"
            [IO.File]::WriteAllText($scripts[$role], ((@("speed instant") + @(Get-Content $source | Where-Object { $_ -notmatch '^speed ' })) -join "`n") + "`n")
        }
    }
    $waiting.Enqueue([pscustomobject]@{ Name = $name; Scripts = $scripts; Port = $port; HostLog = Join-Path $out "$name-host.log"; ClientLog = Join-Path $out "$name-client.log" })
    $port++
}
function Start-Peer($group, [string]$role, [string[]]$mode) {
    $log = if ($role -eq "host") { $group.HostLog } else { $group.ClientLog }
    Remove-Item $log -ErrorAction SilentlyContinue
    $arguments = @("--headless", "--force-steam", "off", "--bdport", $group.Port, "--bdturnstates", "--bdmute", "--log-file", "`"$log`"", "--bdtest", "`"$($group.Scripts[$role])`"") + $mode
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden -ArgumentList $arguments
}
$started = Get-Date
$running = @{}
$done = [Collections.Generic.List[object]]::new()
$realAppData = $env:APPDATA
try {
    while ($waiting.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($slot in 0..($Jobs - 1)) {
            if ($running.ContainsKey($slot) -or $waiting.Count -eq 0) { continue }
            $group = $waiting.Dequeue()
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
            if ($run.Host.HasExited -and $run.Client.HasExited) { $state = "done" }
            elseif ($age -gt $TimeoutSec) { $state = "TIMEOUT" }
            elseif ($silent -gt $StallSec) { $state = "STALLED" }
            elseif (($run.Host.HasExited -or $run.Client.HasExited) -and $silent -gt 30) { $state = "ONE PEER LEFT" }
            if (-not $state) { continue }
            foreach ($p in $run.Host, $run.Client) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
            $done.Add([pscustomobject]@{ Group = $run.Group; State = $state; Seconds = [int]$age })
            $running.Remove($slot)
        }
        $lines = @("{0}  co-op {1}: {2}/{3} done, {4} running, {5:F0} min so far" -f (Get-Date -Format "HH:mm:ss"), (Get-GameTag $GameDir), $done.Count, $Names.Count, $running.Count, ((Get-Date) - $started).TotalMinutes)
        Set-Content (Join-Path $Results "status.txt") $lines -Encoding utf8 -ErrorAction SilentlyContinue
    }
}
finally {
    $env:APPDATA = $realAppData
    foreach ($run in $running.Values) { foreach ($p in $run.Host, $run.Client) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } } }
    Remove-TestMods $GameDir
}
$bad = 0
foreach ($entry in $done) {
    $problems = @()
    if ($entry.State -ne "done") { $problems += $entry.State }
    foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) {
        $role = if ($log -eq $entry.Group.HostLog) { "host" } else { "client" }
        if (-not (Test-Path $log)) { $problems += "$role`: no log"; continue }
        if (-not (Select-String -Path $log -Pattern "AutoTest\s+[\d.]+s > quit" -Quiet)) { $problems += "$role`: did not reach its end" }
        Select-String -Path $log -CaseSensitive -Pattern "State divergence|FAILED line|MISMATCH|mismatches [1-9]|disabled after|turned off and removed" | Select-Object -First 2 | ForEach-Object { $problems += "$role`: " + ($_.Line -replace '^.*AutoTest\s+[\d.]+s\s+', '').Substring(0, [Math]::Min(110, ($_.Line -replace '^.*AutoTest\s+[\d.]+s\s+', '').Length)) }
    }
    # Each peer logs its own player and plays; the enemies are the same for both. At each turn start only (--bdturnstates): a
    # script's state line comes when that peer gets there, maybe after the other one's plays of the turn.
    $enemies = foreach ($log in $entry.Group.HostLog, $entry.Group.ClientLog) { ,@(Select-String -Path $log -Pattern "STATE floor .*: enemy " -ErrorAction SilentlyContinue | ForEach-Object { $_.Line -replace '^.*STATE ', '' }) }
    $same = (($enemies[0] -join "`n") -eq ($enemies[1] -join "`n"))
    if (-not $same) { $problems += "enemy states differ ($($enemies[0].Count) vs $($enemies[1].Count) lines)" }
    if ($enemies[0].Count -eq 0) { $problems += "no enemy state line" }
    Write-Output ("{0,-16} {1,-8} {2,4} s  {3} enemy state lines{4}" -f $entry.Group.Name, $entry.State, $entry.Seconds, $enemies[0].Count, $(if ($problems.Count) { "  PROBLEMS: " + ($problems -join "; ") } else { "  agree" }))
    if ($problems.Count) { $bad++ }
}
Write-Output ("done in {0:F1} min, {1} with problems, logs in {2}" -f ((Get-Date) - $started).TotalMinutes, $bad, $out)
exit $(if ($bad) { 1 } else { 0 })
