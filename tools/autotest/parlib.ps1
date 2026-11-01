# Parallel test sessions (dot-sourced by par.ps1, parsweep.ps1, leakroots.ps1). Each session runs headless on a game copy
# with its own APPDATA, a mirror of the user data folder: logs, settings and bdopt saves stay in the slot, the real ones
# are never touched. Results are identical to a rendered session; timings aren't meaningful (one rendered session alone
# for fps and frame times).
. (Join-Path $PSScriptRoot "common.ps1")
$SlotRoot = Join-Path $env:LOCALAPPDATA "BigDeckOptimization\test-slots"
$BaselineRoot = Join-Path $Results "baselines"
$MinFreeKB = 3 * 1024 * 1024
$StartLimitSec = 60
$FreezeSilenceSec = 20

function Resolve-GameCopy([string]$dir) {
    if (-not $dir) { $dir = "$env:STS2_GAME_DIR" }
    if (-not $dir) { throw "Give -GameDir or STS2_GAME_DIR: parallel sessions run on a game copy" }
    if (-not (Test-Path (Join-Path $dir "steam_appid.txt"))) { throw "No steam_appid.txt in $dir" }
    # mutations.ps1 stopped from outside: its modified game is still there, and every result would be about that game.
    if (Test-Path (Join-Path $dir "data_sts2_windows_x86_64\sts2.dll.bdmut-backup")) { throw "$dir has a modified game left by mutations.ps1: restore sts2.dll.bdmut-backup first" }
    $dir
}

# Sessions run without Steam (--force-steam off): with it the game writes saves straight to Steam Cloud through Steam's API,
# whatever APPDATA says, and parallel sessions of one account make cloud conflicts. So no Workshop either: the mods go in the
# copy's mods_STEAMTEST, as for co-op sessions, for as long as the script runs.
function Add-TestMods([string]$gameDir) {
    $testMods = Join-Path $gameDir "mods_STEAMTEST"
    $own = Join-Path $testMods ".bdtest-created"
    if (Test-Path $own) { return }
    if (Test-Path $testMods) { throw "$testMods already exists (left over or not ours): remove it first" }
    $settings = Get-Content (Join-Path (Get-SteamAccountDir) "settings.save") -Raw | ConvertFrom-Json
    $wanted = @($settings.mod_settings.mod_list | Where-Object { $_.is_enabled -and $_.source -eq "steam_workshop" -and $_.id -ne "BigDeckOptimization" } | ForEach-Object { $_.id })
    New-Item -ItemType Directory $testMods | Out-Null
    Set-Content $own "created by tools/autotest/parlib.ps1"
    foreach ($item in Get-ChildItem $Workshop -Directory) {
        $ids = Get-ChildItem $item.FullName -Recurse -Filter *.json | ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).id } catch { } }
        if (@($ids | Where-Object { $wanted -contains $_ }).Count -gt 0) { Copy-Item -Recurse $item.FullName (Join-Path $testMods $item.Name) }
    }
}

function Remove-TestMods([string]$gameDir) {
    $testMods = Join-Path $gameDir "mods_STEAMTEST"
    if (Test-Path (Join-Path $testMods ".bdtest-created")) { Start-Sleep 2; Remove-Item -Recurse -Force $testMods -ErrorAction SilentlyContinue }
}

function Get-VersionDir([string]$gameDir) {
    $path = Join-Path $Results (Get-GameTag $gameDir)
    New-Item -ItemType Directory -Force $path | Out-Null
    $path
}

function Sync-Slot([int]$slot) {
    $dir = Join-Path $SlotRoot "slot$slot"
    # The profiles' progress stays (what's unlocked changes what a run offers). Runs in progress don't: a save left by the last
    # session here, maybe from another game version, is called corrupt by the game.
    robocopy $UserData (Join-Path $dir "SlayTheSpire2") /MIR /XD logs history replays sentry shader_cache vulkan /XF *.log *.tmp current_run* *.corrupt /NFL /NDL /NJH /NJS /NP | Out-Null
    Get-ChildItem (Join-Path $dir "SlayTheSpire2") -Recurse -File -Include current_run*, *.corrupt -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $dir
}

# Everything a mod-off log depends on but this mod's own patches: a baseline stays valid until the game, a Workshop mod,
# the test tooling or the scenario changes.
$script:BaselineSalt = $null
function Get-BaselineKey([string]$gameDir, [string]$scriptPath) {
    if (-not $script:BaselineSalt) {
        $bench = Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\Bench"
        $files = @(Get-ChildItem (Join-Path $gameDir "data_sts2_windows_x86_64\sts2.dll")) + @(Get-ChildItem $Workshop -Recurse -Filter *.dll) + @(Get-ChildItem $bench -Filter *.cs)
        $script:BaselineSalt = "no-steam" + ($files | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ""
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes($script:BaselineSalt + [IO.File]::ReadAllText($scriptPath))
    ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace "-", "").Substring(0, 16)
}

function Get-FailedLines([string]$log) {
    if (-not (Test-Path $log)) { return @("no log") }
    @(Select-String -Path $log -CaseSensitive -Pattern "FAILED line|ABORT: first failed|TIMEOUT: script|SWEEP FAILED" | ForEach-Object { ($_.Line -replace '^.*AutoTest\s+[\d.]+s\s+', '') })
}

# results/status.txt: progress and time left (from the items done when jobs have an Items count, else the sessions done),
# each running session's current line. Read with status.ps1.
function Write-SessionStatus([string]$label, [int]$total, [int]$finished, [int]$waiting, $running, [datetime]$started, $jobs) {
    $itemsTotal = 0
    foreach ($job in $jobs) { if ($job.PSObject.Properties["Items"]) { $itemsTotal += $job.Items } }
    $itemsDone = 0
    $rows = foreach ($slot in ($running.Keys | Sort-Object)) {
        $run = $running[$slot]
        $count = 0
        $current = ""
        if (Test-Path $run.Job.Log) {
            $hits = @(Select-String -Path $run.Job.Log -Pattern "SWEEP \w+ (\S+) \S+ wall|> \S+" -ErrorAction SilentlyContinue)
            if ($hits.Count) { $current = ($hits[-1].Line -replace '^.*AutoTest\s+', '') }
            $count = @($hits | Where-Object { $_.Matches[0].Groups[1].Success } | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Unique).Count
        }
        $itemsDone += $count
        $text = "{0,-26} {1,4:F0} s  {2}" -f $run.Job.Name, ((Get-Date) - $run.Started).TotalSeconds, $current
        $text.Substring(0, [Math]::Min(110, $text.Length))
    }
    $elapsed = ((Get-Date) - $started).TotalSeconds
    if ($itemsTotal -gt 0) {
        foreach ($job in $jobs) {
            $alreadyRunning = @($running.Values | Where-Object { $_.Job.Name -eq $job.Name }).Count
            $notStarted = ($job.Log -and -not (Test-Path $job.Log))
            if (-not $alreadyRunning -and -not $notStarted -and $job.PSObject.Properties["Items"]) { $itemsDone += $job.Items }
        }
        $share = [Math]::Min(1.0, $itemsDone / [double]$itemsTotal)
        $progress = "{0}/{1} items (both sides)" -f $itemsDone, $itemsTotal
    }
    else {
        $share = if ($total) { $finished / [double]$total } else { 0 }
        $progress = "{0}/{1} sessions" -f $finished, $total
    }
    $left = if ($share -gt 0.02) { "about {0:F0} min left" -f (($elapsed * (1 - $share) / $share) / 60) } else { "time left: not known yet" }
    $header = "{0}  {1}: {2}, {3} running, {4} waiting, {5:F0} min so far, {6}, {7:F1} GB free" -f (Get-Date -Format "HH:mm:ss"), $label, $progress, $running.Count, $waiting, ($elapsed / 60), $left, ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB)
    Set-Content (Join-Path $Results "status.txt") (@($header) + @($rows)) -Encoding utf8 -ErrorAction SilentlyContinue
}

# Jobs: objects with Name, Script (scenario path), Log. Runs at most $MaxJobs at once. A session is closed when it passes
# $TimeoutSec, or when its log stays silent for $StallSec. Returns one result per job: Name, State (done, cached, TIMEOUT,
# STALLED), Seconds, Failed.
function Invoke-Sessions($Jobs, [string]$GameDir, [int]$MaxJobs = 8, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Rendered, [switch]$UseBaselines, [string]$Label = "sessions") {
    $exe = Join-Path $GameDir "SlayTheSpire2.exe"
    $launchMode = if ($Rendered) { @("--bdhidden", "--screen", "1") } else { @("--headless") }
    Add-TestMods $GameDir
    $pending = [Collections.Generic.Queue[object]]::new()
    $done = [Collections.Generic.List[object]]::new()
    $baselineDir = Join-Path $BaselineRoot (Get-GameTag $GameDir)
    foreach ($job in $Jobs) {
        # A cached mod-off log replaces the session.
        $isOff = $job.Name -match '-off(\d*$|-)'
        if ($UseBaselines -and $isOff) {
            $cached = Join-Path $baselineDir "$($job.Name)-$(Get-BaselineKey $GameDir $job.Script).log"
            if (Test-Path $cached) {
                Copy-Item $cached $job.Log -Force
                $done.Add([pscustomobject]@{ Name = $job.Name; State = "cached"; Seconds = 0; Failed = @(Get-FailedLines $job.Log) })
                continue
            }
        }
        $pending.Enqueue($job)
    }
    $running = @{}
    $retries = @{}
    $realAppData = $env:APPDATA
    $total = $pending.Count + $done.Count
    $started = Get-Date
    $lastStatus = [datetime]::MinValue
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            foreach ($slot in 0..($MaxJobs - 1)) {
                if ($running.ContainsKey($slot) -or $pending.Count -eq 0) { continue }
                # Each session takes 1-2 GB: the PC stays usable, the next one waits.
                if ($running.Count -gt 0 -and (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt $MinFreeKB) { break }
                $job = $pending.Dequeue()
                Remove-Item $job.Log -ErrorAction SilentlyContinue
                $env:APPDATA = Sync-Slot $slot
                $p = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden -ArgumentList (@("--force-steam", "off", "--log-file", "`"$($job.Log)`"", "--bdtest", "`"$($job.Script)`"", "--bdmute") + $launchMode)
                $env:APPDATA = $realAppData
                $running[$slot] = [pscustomobject]@{ Job = $job; Process = $p; Started = Get-Date; ScriptStarted = $false }
            }
            Start-Sleep -Milliseconds 300
            if (((Get-Date) - $lastStatus).TotalSeconds -ge 5) {
                Write-SessionStatus $Label $total $done.Count $pending.Count $running $started $Jobs
                $lastStatus = Get-Date
            }
            foreach ($slot in @($running.Keys)) {
                $run = $running[$slot]
                $state = $null
                $age = ((Get-Date) - $run.Started).TotalSeconds
                # About one headless session in 40 freezes on Godot's dummy renderer (log: Parameter "t" is null, then silence),
                # at the start or later; the session can only start over. Or it never reached the script's first line.
                $frozen = $false
                if (-not $run.Process.HasExited -and $age -gt 20 -and (Test-Path $run.Job.Log)) {
                    if (((Get-Date) - (Get-Item $run.Job.Log).LastWriteTime).TotalSeconds -gt $FreezeSilenceSec) {
                        $frozen = [bool](Get-Content $run.Job.Log -Tail 4 -ErrorAction SilentlyContinue | Select-String -SimpleMatch 'Parameter "t" is null')
                    }
                    if (-not $frozen -and -not $run.ScriptStarted -and $age -gt $StartLimitSec) {
                        $run.ScriptStarted = [bool](Select-String -Path $run.Job.Log -Pattern "AutoTest\s+[\d.]+s > " -Quiet)
                        $frozen = -not $run.ScriptStarted
                    }
                }
                if ($frozen) {
                    $name = $run.Job.Name
                    $retries[$name] = 1 + [int]$retries[$name]
                    Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep 1
                    $running.Remove($slot)
                    if ($retries[$name] -le 2) { $pending.Enqueue($run.Job) }
                    else { $done.Add([pscustomobject]@{ Name = $name; State = "FROZEN"; Seconds = [int]$age; Failed = @("froze 3 times") }) }
                    continue
                }
                if ($run.Process.HasExited) { $state = "done" }
                elseif ($age -gt $TimeoutSec) { $state = "TIMEOUT" }
                elseif ($age -gt 60 -and (Test-Path $run.Job.Log) -and ((Get-Date) - (Get-Item $run.Job.Log).LastWriteTime).TotalSeconds -gt $StallSec) { $state = "STALLED" }
                if (-not $state) { continue }
                if ($state -ne "done") { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
                $failed = @(Get-FailedLines $run.Job.Log)
                $done.Add([pscustomobject]@{ Name = $run.Job.Name; State = $state; Seconds = [int]$age; Failed = $failed })
                $running.Remove($slot)
                if ($UseBaselines -and $state -eq "done" -and $failed.Count -eq 0 -and $run.Job.Name -match '-off(\d*$|-)') {
                    New-Item -ItemType Directory -Force $baselineDir | Out-Null
                    Copy-Item $run.Job.Log (Join-Path $baselineDir "$($run.Job.Name)-$(Get-BaselineKey $GameDir $run.Job.Script).log") -Force
                }
            }
        }
    }
    finally {
        $env:APPDATA = $realAppData
        foreach ($run in $running.Values) { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue }
    }
    $done
}
