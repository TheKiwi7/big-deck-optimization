# Co-op with more than two players, headless, without Steam: a host and N-1 clients on one lobby. Every other co-op tool stops at two
# peers; the runs this mod is for have four or five. The scenario pair is <name>-host.txt and <name>-client.txt, CHARACTER in the
# client's script is replaced for each one. Pass: every peer reaches its end, the same enemy and run states on all, no divergence.
# Logs: results/<game version>/<name>-host.log, -client1.log...
#   powershell -ExecutionPolicy Bypass -File tools/autotest/coop4.ps1 -Name coop4 -GameDir C:\Games\StS2-public-0.107.1 [-Peers 4] [-Rendered]
# -Rendered: the host renders, off screen, for fps and frame times; the clients stay headless (one GPU). -RenderedClient: the first
# client renders and the host is headless, for what a player who is not hosting sees.
param([Parameter(Mandatory)][string]$Name, [string]$GameDir = "", [int]$Peers = 4, [int]$TimeoutSec = 1200, [int]$StallSec = 240, [switch]$Rendered, [switch]$RenderedClient)
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
$exe = Join-Path $GameDir "SlayTheSpire2.exe"
Add-TestMods $GameDir
$folder = Join-Path $out "coop-scripts"
New-Item -ItemType Directory -Force $folder | Out-Null
$characters = "IRONCLAD", "SILENT", "DEFECT", "REGENT"
$realAppData = $env:APPDATA
$procs = [Collections.Generic.List[object]]::new()
$logs = [Collections.Generic.List[string]]::new()
$port = 33890
function Start-Peer([string]$log, [string]$script, [string[]]$mode) {
    Remove-Item $log -ErrorAction SilentlyContinue
    $logs.Add($log)
    $arguments = @("--force-steam", "off", "--bdport", $port, "--bdturnstates", "--bdmute", "--log-file", "`"$log`"", "--bdtest", "`"$script`"") + $mode
    if (($Rendered -and $log -like "*-host.log") -or ($RenderedClient -and $log -like "*-client1.log")) { $procs.Add((Start-HiddenGame $exe $GameDir ($arguments + @("--bdhidden", "--windowed", "--resolution", "1920x1080")))) }
    else { $procs.Add((Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden -ArgumentList (@("--headless") + $arguments))) }
}
$started = Get-Date
$state = $null
try {
    $slot = Sync-Slot 49 -Coop
    $profiles = Join-Path $slot "SlayTheSpire2\default"
    foreach ($i in 2..($Peers - 1)) { robocopy (Join-Path $profiles "1001") (Join-Path $profiles (1000 + $i)) /MIR /NFL /NDL /NJH /NJS | Out-Null }
    $env:APPDATA = $slot
    Start-Peer (Join-Path $out "$Name-host.log") (Join-Path $Scenarios "$Name-host.txt") @("--fastmp", "host_standard")
    Start-Sleep 12
    foreach ($i in 1..($Peers - 1)) {
        $script = Join-Path $folder "$Name-client$i.txt"
        [IO.File]::WriteAllText($script, ([IO.File]::ReadAllText((Join-Path $Scenarios "$Name-client.txt")) -replace "CHARACTER", $characters[$i - 1]))
        Start-Peer (Join-Path $out "$Name-client$i.log") $script @("--fastmp", "join", "--clientId", (1000 + $i))
        Start-Sleep 4
    }
    $env:APPDATA = $realAppData
    while (-not $state) {
        Start-Sleep 2
        $age = ((Get-Date) - $started).TotalSeconds
        $silent = ($logs | Where-Object { Test-Path $_ } | ForEach-Object { ((Get-Date) - (Get-Item $_).LastWriteTime).TotalSeconds } | Measure-Object -Maximum).Maximum
        $alive = @($procs | Where-Object { -not $_.HasExited }).Count
        if ($alive -eq 0) { $state = "done" }
        elseif ($age -gt $TimeoutSec) { $state = "TIMEOUT" }
        elseif ($silent -gt $StallSec) { $state = "STALLED" }
        elseif ($alive -lt $procs.Count -and $silent -gt 40) { $state = "A PEER LEFT" }
    }
}
finally {
    $env:APPDATA = $realAppData
    foreach ($p in $procs) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    Remove-TestMods $GameDir
}
$problems = @()
if ($state -ne "done") { $problems += $state }
$enemies = @(); $runs = @()
foreach ($log in $logs) {
    $role = [IO.Path]::GetFileNameWithoutExtension($log)
    if (-not (Select-String -Path $log -Pattern "AutoTest\s+[\d.]+s > quit" -Quiet)) { $problems += "$role did not reach its end" }
    Select-String -Path $log -CaseSensitive -Pattern "State divergence|FAILED line|MISMATCH|mismatches [1-9]|disabled after|turned off and removed" | Select-Object -First 2 | ForEach-Object { $problems += "$role`: $($_.Line.Trim())" }
    $enemies += ,@(Select-String -Path $log -Pattern "STATE floor .*: enemy " | ForEach-Object { $_.Line -replace '^.*?STATE ', '' })
    $runs += ,@(Select-String -Path $log -Pattern "STATE (?![^:]*\(local\))[^:]+: (floor \d+, act |\S+ gold )" | ForEach-Object { $_.Line -replace '^.*?STATE ', '' })
}
foreach ($i in 1..($logs.Count - 1)) {
    if (($enemies[0] -join "`n") -ne ($enemies[$i] -join "`n")) { $problems += "enemy states differ: host $($enemies[0].Count) lines, peer $i $($enemies[$i].Count)" }
    if (($runs[0] -join "`n") -ne ($runs[$i] -join "`n")) { $problems += "run states differ: host $($runs[0].Count) lines, peer $i $($runs[$i].Count)" }
}
Write-Output ("{0}: {1} peers, {2} in {3:F0} s, {4} enemy and {5} run state lines{6}" -f $Name, $logs.Count, $state, ((Get-Date) - $started).TotalSeconds, $enemies[0].Count, $runs[0].Count, $(if ($problems.Count) { "`n  PROBLEMS: " + ($problems -join "`n  ") } else { ", agree" }))
exit $(if ($problems.Count) { 1 } else { 0 })
