# Shared paths for the unattended test launchers (dot-sourced). Override with environment variables if needed.
$GameDir = if ($env:STS2_GAME_DIR) { $env:STS2_GAME_DIR } else { "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }
$GameExe = Join-Path $GameDir "SlayTheSpire2.exe"
$AppId = "2868840"
$SteamExe = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamExe
$Workshop = Join-Path (Split-Path -Parent (Split-Path -Parent $GameDir)) "workshop\content\$AppId"
# A game copy outside Steam's library (STS2_GAME_DIR, e.g. C:\Games\StS2-beta-0.111.0): Steam's own Workshop folder.
if (-not (Test-Path $Workshop) -and $SteamExe) { $Workshop = Join-Path (Split-Path -Parent $SteamExe) "steamapps\workshop\content\$AppId" }
$UserData = Join-Path $env:APPDATA "SlayTheSpire2"
$Scenarios = Join-Path $PSScriptRoot "scenarios"
$Results = Join-Path $PSScriptRoot "results"
New-Item -ItemType Directory -Force $Results | Out-Null

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
