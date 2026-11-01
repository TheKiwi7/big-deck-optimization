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
