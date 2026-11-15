# The release package in game: dist/BigDeckOptimization-v*.zip installed on a game copy in place of its Debug build, the
# account's Workshop mods beside it, one headless session without Steam in a test slot, then the Debug build put back.
# Reads its log for the version, every patch applied, nothing skipped or failed. Never on the Steam install.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/release-incopy.ps1 -Copy C:\Games\StS2-public-0.107.1 [-WaitSec 120]
param([Parameter(Mandatory)][string]$Copy, [int]$WaitSec = 120)
if ($Copy -match 'steamapps') { throw "$Copy is under steamapps: only a game copy" }
. (Join-Path $PSScriptRoot "parlib.ps1")
# common.ps1 (dot-sourced by parlib.ps1) sets $GameDir to the Steam install: this script only uses its copy.
$GameDir = Resolve-GameCopy $Copy
$zip = Get-ChildItem (Join-Path $PSScriptRoot "..\..\dist") -Filter "BigDeckOptimization-v*.zip" | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $zip) { throw "No dist/BigDeckOptimization-v*.zip: run tools/release/package.py first" }
$mods = Join-Path $GameDir "mods"
$installed = Join-Path $mods "BigDeckOptimization"
$backup = Join-Path $mods "BigDeckOptimization.debug-backup"
if (Test-Path $backup) { throw "$backup exists (an earlier run stopped from outside): put it back as $installed first" }
$log = Join-Path (Get-VersionDir $GameDir) "release-incopy.log"
$realAppData = $env:APPDATA
$process = $null
$slotLocked = $false
try {
    $slotLocked = Lock-Slot 90
    if (Test-Path $installed) { Rename-Item $installed $backup }
    Expand-Archive $zip.FullName -DestinationPath $mods -Force
    Add-TestMods $GameDir
    $env:APPDATA = Sync-Slot 90
    Remove-Item $log -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath (Join-Path $GameDir "SlayTheSpire2.exe") -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden `
        -ArgumentList @("--headless", "--force-steam", "off", "--log-file", "`"$log`"")
    $deadline = (Get-Date).AddSeconds($WaitSec)
    while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
        Start-Sleep 2
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern "\[BigDeckOptimization\] Patches applied" -Quiet)) { Start-Sleep 5; break }
    }
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 3 }
    $env:APPDATA = $realAppData
    if ($slotLocked) { Unlock-Slot 90 }
    Remove-TestMods $GameDir
    if (Test-Path $backup) {
        Remove-Item -Recurse -Force $installed -ErrorAction SilentlyContinue
        Rename-Item $backup $installed
    }
}
if (-not (Test-Path $log)) { throw "No log: the game didn't start" }
$text = Get-Content $log
$init = $text | Where-Object { $_ -match "\[BigDeckOptimization\] Initialized v" } | Select-Object -First 1
$applied = @($text | Where-Object { $_ -match "\[BigDeckOptimization\] Patch \S+ applied" }).Count
# Every problem judge.py sees (an error this mod logs, a part turned off, an error the game logs through this mod's code), not
# only skipped patches: HarmonyRebuild's "patched methods run without their patches" (other mods' patches lost, v1.0.0) among them.
# The session doesn't end by itself: closed once the patches are applied.
$problems = @($text | Where-Object { $_ -match "\[BigDeckOptimization\] Patch \S+ (skipped|could not)" }) + @(Invoke-Judge @($log) -NoEnd)
# HarmonyRebuild checked every patched method once the patches were applied: without that line (its internals unavailable), nothing
# says the methods this mod and the others patch still run their patches.
if (-not ($text | Where-Object { $_ -match "\[BigDeckOptimization\] HarmonyRebuild: all \d+ patched methods detoured" })) { $problems += "no 'HarmonyRebuild: all N patched methods detoured' line" }
# With BaseLib among the mods, the package's settings menu must register (a missing or broken BigDeckOptimization.Menu.dll only
# logs a warning: players would get no menu).
if ((Get-TestModIds) -contains "BaseLib" -and -not ($text | Where-Object { $_ -match "\[BigDeckOptimization\] BaseLibSettings: registered" })) { $problems += "BaseLib loaded but no 'BaseLibSettings: registered' line: the package's settings menu didn't load" }
# The package's version is the one being released (the newest zip could be an older build).
$version = (Get-Content (Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\BigDeckOptimization.json") -Raw | ConvertFrom-Json).version.TrimStart('v')
if ($init -notmatch "Initialized v$([regex]::Escape($version)) ") { $problems += "the package says '$init', the manifest version is v$version" }
# The Workshop mods the game must have found (a mods_STEAMTEST emptied by another series would go unseen).
$problems += @(Get-MissingTestMods $log $GameDir)
"package  $($zip.Name)"
"init     $init"
"applied  $applied patches"
$text | Where-Object { $_ -match "\[BigDeckOptimization\] (HookSkip: \d+ hooks|InlineGuard: \d+|Patches applied)" } | ForEach-Object { "         " + ($_ -replace '^.*\[BigDeckOptimization\] ', '') }
if ($problems.Count) { "PROBLEMS:"; $problems | ForEach-Object { "  $_" } } else { "no patch skipped, failed or disabled" }
"debug build back: $(Test-Path (Join-Path $installed 'BigDeckOptimization.dll')), test mods removed: $(-not (Test-Path (Join-Path $GameDir 'mods_STEAMTEST')))"
