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
# run.ps1 -KeepSlot: the slot of the sessions that follow each other and share their user data.
$KeptSlot = 80

function Resolve-GameCopy([string]$dir) {
    if (-not $dir) { $dir = "$env:STS2_GAME_DIR" }
    if (-not $dir) { throw "Give -GameDir or STS2_GAME_DIR: parallel sessions run on a game copy" }
    if (-not (Test-Path (Join-Path $dir "steam_appid.txt"))) { throw "No steam_appid.txt in $dir" }
    # An old mutations.ps1 stopped from outside left its modified game there: every result would be about that game.
    if (Test-Path (Join-Path $dir "data_sts2_windows_x86_64\sts2.dll.bdmut-backup")) { throw "$dir has a modified game left by mutations.ps1: restore sts2.dll.bdmut-backup first" }
    Set-CopyOverrides $dir
    $dir
}

# Sessions run without Steam (--force-steam off): with it the game writes saves straight to Steam Cloud through Steam's API,
# whatever APPDATA says, and parallel sessions of one account make cloud conflicts. So no Workshop either: the mods go in the
# copy's mods_STEAMTEST, as for co-op sessions, for as long as the script runs.
function Add-TestMods([string]$gameDir) {
    Set-CopyOverrides $gameDir
    $testMods = Join-Path $gameDir "mods_STEAMTEST"
    $own = Join-Path $testMods ".bdtest-created"
    if (Test-Path $own) { return }
    if (Test-Path $testMods) { throw "$testMods already exists (left over or not ours): remove it first" }
    $settings = Get-Content (Join-Path (Get-SteamAccountDir) "settings.save") -Raw | ConvertFrom-Json
    # BDTEST_EXTRA_MODS=SpeedX,ModConfig,...: Workshop mods on disk that the account doesn't enable, loaded too (what players combine with this mod).
    $extra = @($env:BDTEST_EXTRA_MODS -split "," | Where-Object { $_ })
    $wanted = @($extra) + @($settings.mod_settings.mod_list | Where-Object { $_.is_enabled -and $_.source -eq "steam_workshop" -and $_.id -ne "BigDeckOptimization" } | ForEach-Object { $_.id })
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

# Without Steam the game reads the profiles default/1 and default/1001, with their own settings (60 fps, less anti-aliasing):
# timings are only comparable with your account's. Everything but the mod list and the language, which stay English there.
# Solo sessions also take the test profile's progress and preferences (what's unlocked changes what a run offers), so they
# give the same results as sessions through Steam. Co-op keeps its own unlocked profiles.
function Copy-DisplaySettings([string]$data, [bool]$solo) {
    $accounts = @(Get-ChildItem (Join-Path $data "steam") -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+$' })
    if ($accounts.Count -ne 1) { return }
    $source = Get-Content (Join-Path $accounts[0].FullName "settings.save") -Raw | ConvertFrom-Json
    foreach ($id in "1", "1001") {
        $file = Join-Path $data "default\$id\settings.save"
        if (-not (Test-Path $file)) { continue }
        $settings = Get-Content $file -Raw | ConvertFrom-Json
        foreach ($property in $source.PSObject.Properties) {
            if ($property.Name -in "mod_settings", "language", "schema_version") { continue }
            $settings | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
        }
        [IO.File]::WriteAllText($file, ($settings | ConvertTo-Json -Depth 30))
    }
    if ($solo) {
        $saves = Join-Path $data "default\1\modded\profile3\saves"
        New-Item -ItemType Directory -Force $saves | Out-Null
        foreach ($name in "progress.save", "prefs.save") {
            $from = Join-Path $accounts[0].FullName "modded\profile3\saves\$name"
            if (Test-Path $from) { Copy-Item $from (Join-Path $saves $name) -Force }
        }
    }
}

function Sync-Slot([int]$slot, [switch]$Coop) {
    $dir = Join-Path $SlotRoot "slot$slot"
    # The profiles' progress stays (what's unlocked changes what a run offers). Runs in progress don't: a save left by the last
    # session here, maybe from another game version, is called corrupt by the game.
    robocopy $UserData (Join-Path $dir "SlayTheSpire2") /MIR /XD logs history replays sentry shader_cache vulkan /XF *.log *.tmp current_run* *.corrupt /NFL /NDL /NJH /NJS /NP | Out-Null
    Get-ChildItem (Join-Path $dir "SlayTheSpire2") -Recurse -File -Include current_run*, *.corrupt -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-DisplaySettings (Join-Path $dir "SlayTheSpire2") (-not $Coop)
    $dir
}

# Edits the mod list of a slot's solo profile: $change takes the list and returns the new one.
function Update-SlotModList([string]$slot, [scriptblock]$change) {
    $file = Join-Path $slot "SlayTheSpire2\default\1\settings.save"
    $data = Get-Content $file -Raw | ConvertFrom-Json
    $data.mod_settings.mod_list = @(& $change @($data.mod_settings.mod_list))
    # No BOM: the game's JSON reader expects plain UTF-8.
    [IO.File]::WriteAllText($file, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
}

# Takes a Workshop mod out of a slot's solo profile or back in. The profile may have no entry for it yet: a mod without one loads.
# The game goes by id and source, and reads mods_STEAMTEST as the Workshop.
function Set-SlotModEnabled([string]$slot, [string]$id, [bool]$enabled) {
    Update-SlotModList $slot {
        param($mods)
        $entry = @($mods | Where-Object { $_.id -eq $id })
        if ($entry.Count) { $entry | ForEach-Object { $_.is_enabled = $enabled }; $mods }
        else { $mods; [pscustomobject]@{ id = $id; is_enabled = $enabled; source = "steam_workshop" } }
    }
}

# Whether a session's log shows what the test is about: a setup that did nothing would otherwise pass unseen.
function Test-SessionLog([string]$name, [string]$pattern, [bool]$expected, [string]$meaning) {
    $found = Select-String -Path (Join-Path $Results "$name.log") -Pattern $pattern -Quiet
    Write-Output ("{0} {1}: {2}" -f $(if ([bool]$found -eq $expected) { "ok     " } else { "PROBLEM" }), $name, $meaning)
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

# A game copy that costs nothing: hard links to every file of $source. Replacing a file: delete its link, then write a new
# one; writing through a link would change the original. Same volume as the source.
function New-ThinCopy([string]$source, [string]$target) {
    $root = (Get-Item $source).FullName
    foreach ($item in Get-ChildItem $root -Recurse -Force) {
        $path = Join-Path $target $item.FullName.Substring($root.Length + 1)
        if ($item.PSIsContainer) { New-Item -ItemType Directory -Force $path | Out-Null }
        else { [void][BdHardLink]::CreateHardLink($path, $item.FullName, [IntPtr]::Zero) }
    }
}
if (-not ("BdHardLink" -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class BdHardLink { [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool CreateHardLink(string link, string existing, IntPtr security); }'
}

# Jobs: objects with Name, Script (scenario path), Log. Runs at most $MaxJobs at once. A session is closed when it passes
# $TimeoutSec, or when its log stays silent for $StallSec. Returns one result per job: Name, State (done, cached, TIMEOUT,
# STALLED, FROZEN, CRASHED), Seconds, Failed, Restarts.
function Invoke-Sessions($Jobs, [string]$GameDir, [int]$MaxJobs = 8, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Rendered, [switch]$UseBaselines, [string]$Label = "sessions") {
    $launchMode = if ($Rendered) { @("--bdhidden", "--windowed", "--resolution", "1920x1080") } else { @("--headless") }
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
    # The slots are shared by every series, whatever the game copy: two at once would run two games in one user data folder
    # (a save of one renamed under the other, a slot mirrored over a running session).
    New-Item -ItemType Directory -Force $SlotRoot | Out-Null
    try { $slotLock = [IO.File]::Open((Join-Path $SlotRoot "slots.lock"), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw "Another parallel test series uses the test slots ($SlotRoot): wait for it to end." }
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            foreach ($slot in 0..($MaxJobs - 1)) {
                if ($running.ContainsKey($slot) -or $pending.Count -eq 0) { continue }
                # Each session takes 1-2 GB: the PC stays usable, the next one waits.
                if ($running.Count -gt 0 -and (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt $MinFreeKB) { break }
                $job = $pending.Dequeue()
                Remove-Item $job.Log -ErrorAction SilentlyContinue
                $env:APPDATA = Sync-Slot $slot
                # A job may name its own game folder (mutations.ps1).
                $jobDir = if ($job.PSObject.Properties["GameDir"]) { $job.GameDir } else { $GameDir }
                $launch = @("--force-steam", "off", "--log-file", "`"$($job.Log)`"", "--bdtest", "`"$($job.Script)`"", "--bdmute") + $launchMode
                $jobExe = Join-Path $jobDir "SlayTheSpire2.exe"
                $p = if ($Rendered) { Start-HiddenGame $jobExe $jobDir $launch } else { Start-Process -FilePath $jobExe -WorkingDirectory $jobDir -PassThru -WindowStyle Hidden -ArgumentList $launch }
                $env:APPDATA = $realAppData
                [void]$p.Handle # or its exit code is lost
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
                # The freeze Set-CopyOverrides removes (log: Parameter "t" is null, then silence) should not come back, nor any other at
                # that point; if one does, the session can only start over. Or it never reached the script's first line.
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
                    else { $done.Add([pscustomobject]@{ Name = $name; State = "FROZEN"; Seconds = [int]$age; Failed = @("froze 3 times"); Restarts = $retries[$name] }) }
                    continue
                }
                if ($run.Process.HasExited) {
                    $state = "done"
                    # Gone before the script's end, without a word: a native crash, rare on Godot's dummy renderer (seen after a texture
                    # load outside the cache). Its log compares as a shorter session: it starts over, the first log kept beside it.
                    # Also gone before its script's first line: the session ran nothing (seen once at a launch, 8 s in, an 8,400-card A/B then compared nothing).
                    if (-not (Select-String -Path $run.Job.Log -Pattern "AutoTest\s+[\d.]+s > quit|ABORT|TIMEOUT: script|\[AutoSlay\] Action: Quitting" -Quiet -ErrorAction SilentlyContinue)) {
                        $name = $run.Job.Name
                        $retries[$name] = 1 + [int]$retries[$name]
                        Copy-Item $run.Job.Log "$($run.Job.Log).early" -Force
                        Start-Sleep 1
                        $running.Remove($slot)
                        if ($retries[$name] -le 2) { $pending.Enqueue($run.Job) }
                        else { $done.Add([pscustomobject]@{ Name = $name; State = "CRASHED"; Seconds = [int]$age; Failed = @("stopped early 3 times, exit code $($run.Process.ExitCode)"); Restarts = $retries[$name] }) }
                        continue
                    }
                }
                elseif ($age -gt $TimeoutSec) { $state = "TIMEOUT" }
                elseif ($age -gt 60 -and (Test-Path $run.Job.Log) -and ((Get-Date) - (Get-Item $run.Job.Log).LastWriteTime).TotalSeconds -gt $StallSec) { $state = "STALLED" }
                if (-not $state) { continue }
                if ($state -ne "done") { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
                $failed = @(Get-FailedLines $run.Job.Log)
                $done.Add([pscustomobject]@{ Name = $run.Job.Name; State = $state; Seconds = [int]$age; Failed = $failed; Restarts = [int]$retries[$run.Job.Name] })
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
        $slotLock.Dispose()
    }
    $done
}
