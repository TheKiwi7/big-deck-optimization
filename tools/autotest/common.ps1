# Shared paths for the unattended test launchers (dot-sourced). Override with environment variables if needed.
$GameDir = if ($env:STS2_GAME_DIR) { $env:STS2_GAME_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }
$GameExe = Join-Path $GameDir "SlayTheSpire2.exe"
$AppId = "2868840"
$SteamExe = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamExe
$Workshop = Join-Path (Split-Path -Parent (Split-Path -Parent $GameDir)) "workshop\content\$AppId"
# A game copy outside Steam's library (STS2_GAME_DIR, e.g. C:\Games\StS2-beta-0.113.1): Steam's own Workshop folder.
if (-not (Test-Path $Workshop) -and $SteamExe) { $Workshop = Join-Path (Split-Path -Parent $SteamExe) "steamapps\workshop\content\$AppId" }
# BDTEST_WORKSHOP: another folder of Workshop items (a SteamCMD download folder, steamapps\workshop\content\<appid>), for mods the
# account isn't subscribed to; test sessions copy what they need from it into a copy's mods_STEAMTEST, never into the Steam install.
if ($env:BDTEST_WORKSHOP) { $Workshop = $env:BDTEST_WORKSHOP }
$UserData = Join-Path $env:APPDATA "SlayTheSpire2"
$Scenarios = Join-Path $PSScriptRoot "scenarios"
$Results = Join-Path $PSScriptRoot "results"
New-Item -ItemType Directory -Force $Results | Out-Null

# What a runner's trap prints for an error nothing caught, then exits 1 ('trap { Write-RunnerError $_; exit 1 }' after the
# dot-sourcing). Without it a terminating error (Select-String on a missing file, a throw) skips only the statement it stopped,
# its finally blocks run, and the script goes on to its exit code 0 with its work half done (the mutation runs of 2026-11-18/19).
function Write-RunnerError($record) {
    $where = if ($record.InvocationInfo -and $record.InvocationInfo.ScriptName) { " ($(Split-Path $record.InvocationInfo.ScriptName -Leaf):$($record.InvocationInfo.ScriptLineNumber))" } else { "" }
    Write-Output "RUNNER ERROR$where`: $($record.Exception.Message) - stopped, exit code 1"
}

# A setting for a test copy, in the file Godot reads next to the exe; a copy keeps it, the Steam install never gets it (no
# steam_appid.txt there). Sentry's logger deadlocked one headless session in 40: Godot's error on the dummy renderer's textures,
# reported on the main thread while resource loads hold their locks (native stacks: main thread in libsentry, workers on locks).
# A modded game rejects Sentry's events anyway.
# Starts the game with no window on screen and without the keyboard. Hidden: Godot brings a window created beyond the screens back
# onto one, and the mod only moves it later. Through WMI: a process started from the foreground one may take the focus, and did
# for 3 s (Godot's no_focus setting stops that, but its window then ignores the hidden start). The mod shows the window off-screen.
function Start-HiddenGame([string]$exe, [string]$workingDir, [string[]]$arguments) {
    $environment = [string[]]@(Get-ChildItem Env: | ForEach-Object { "$($_.Name)=$($_.Value)" })
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0; EnvironmentVariables = $environment }
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" " + ($arguments -join " "); CurrentDirectory = $workingDir; ProcessStartupInformation = $startup }
    if ($created.ReturnValue -ne 0) { throw "Could not start the game: Win32_Process.Create returned $($created.ReturnValue)" }
    Get-Process -Id $created.ProcessId
}

function Set-CopyOverrides([string]$gameDir) {
    if (-not (Test-Path (Join-Path $gameDir "steam_appid.txt"))) { return }
    $text = "[sentry]`n`nlogger/logger_enabled=false`n"
    $override = Join-Path $gameDir "override.cfg"
    if (-not (Test-Path $override) -or [IO.File]::ReadAllText($override) -ne $text) { [IO.File]::WriteAllText($override, $text) }
}

# The game version of an install or copy ("v0.107.1"): logs are kept per version under results/<version>/.
function Get-GameTag([string]$dir) {
    $info = Join-Path $dir "release_info.json"
    if (Test-Path $info) { (Get-Content $info -Raw | ConvertFrom-Json).version } else { "unknown" }
}

# A second copy of a session's log per game version: results/<name>.log is overwritten by the next session on any version.
# BD_NO_VERSIONED_LOGS: sessions on a modified game (mutations.ps1).
function Save-VersionedLog([string]$log, [string]$dir) {
    if ($env:BD_NO_VERSIONED_LOGS -or -not (Test-Path $log)) { return }
    $target = Join-Path $Results (Get-GameTag $dir)
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $log (Join-Path $target (Split-Path $log -Leaf)) -Force
}

# judge.py's verdict on logs (one rule for every runner): Show-Judgment its "PROBLEM <log>: <text>" and "expected" lines, Invoke-Judge
# the PROBLEM lines alone. A judge that crashed or never ran prints no JUDGED line: that is a problem too, never "no problem".
# -NoEnd: a session not expected to end by itself. -Mutated: a session on a game mutations.ps1 modified (judge.py --mutated: the
# parts the mutation leaves vanilla compare nothing and count no effect there; mutations.ps1 checks those against its table).
function Show-Judgment([string[]]$Logs, [switch]$NoEnd, [switch]$ModOff, [switch]$Mutated) {
    $arguments = @("-I", (Join-Path $PSScriptRoot "judge.py"))
    if ($NoEnd) { $arguments += "--no-end" }
    if ($ModOff) { $arguments += "--mod-off" }
    if ($Mutated) { $arguments += "--mutated" }
    $out = @(& python @arguments @Logs 2>&1 | ForEach-Object { "$_" })
    $out | Where-Object { $_ -like "PROBLEM *" -or $_ -like "expected *" }
    if (-not ($out | Where-Object { $_ -match '^JUDGED \d+ problem' })) {
        "PROBLEM judge.py: did not finish, not judged: $(($Logs | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') ($(@($out | Select-Object -Last 2) -join ' | '))"
    }
}

function Invoke-Judge([string[]]$Logs, [switch]$NoEnd, [switch]$ModOff, [switch]$Mutated) {
    @(Show-Judgment $Logs -NoEnd:$NoEnd -ModOff:$ModOff -Mutated:$Mutated) | Where-Object { $_ -like "PROBLEM *" }
}

# The game a test launched through Steam (no process handle comes back from Steam): the newest SlayTheSpire2 started after $after
# whose command line runs this scenario. Never a game found by its name alone: yours could be running.
function Find-TestGame([string]$script, [datetime]$after) {
    $leaf = [IO.Path]::GetFileName($script)
    $found = Get-CimInstance Win32_Process -Filter "Name = 'SlayTheSpire2.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains("--bdtest") -and $_.CommandLine.Contains($leaf) -and $_.CreationDate -ge $after.AddSeconds(-2) } |
        Sort-Object CreationDate | Select-Object -Last 1
    if ($found) { Get-Process -Id $found.ProcessId -ErrorAction SilentlyContinue }
}

# A Debug build of this mod (with the test runner, Bench/ AutoTest): its assembly names the BigDeckOptimization.Bench namespace (UTF-8
# in the metadata's strings), which a Release build, without Bench/, never has.
function Test-DebugBuild([string]$dll) {
    $bytes = [IO.File]::ReadAllBytes($dll)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $text.Contains("BigDeckOptimization.Bench")
}

function Assert-GameNotRunning {
    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { throw "Slay the Spire 2 is already running" }
}

# The Steam account folder holding settings.save (the single one under steam/, or $env:STS2_STEAM_ID).
function Get-SteamAccountDir {
    if ($env:STS2_STEAM_ID) { return Join-Path $UserData "steam\$env:STS2_STEAM_ID" }
    $accounts = @(Get-ChildItem (Join-Path $UserData "steam") -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+$' })
    if ($accounts.Count -ne 1) { throw "Found $($accounts.Count) Steam accounts under $UserData\steam: set STS2_STEAM_ID" }
    return $accounts[0].FullName
}
