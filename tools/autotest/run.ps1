# Unattended singleplayer session through Steam: runs scenarios/<Name>.txt with the Debug build's AutoTest runner,
# optionally records a CPU trace, and copies the game log to results/<Name>.log.
#   powershell -File tools/autotest/run.ps1 -Name solo-8k [-TimeoutSec 900] [-TraceSec 60 [-TraceProfile gc-verbose] [-TraceProviders <providers>]] [-KeepConfig] [-Visible] [-Sound] [-Screen 1] [-Show] [-GameDir <copy>]
# -GameDir: a full copy of another game version (e.g. the beta, C:\Games\StS2-beta-0.111.0) with a steam_appid.txt
# (2868840) next to its exe, launched directly instead of through Steam (Steam must be running; Workshop mods load as
# usual). Deploy the Debug build into it with dotnet build ... -p:ModDeployDir=<copy>\mods\BigDeckOptimization.
# A trace must end while the game still runs (method names are written at the end): give the scenario a final wait.
# On a copy the session runs without Steam, in its own user data (-KeepSlot: as the previous session left it, for a check over a restart). With BaseLib, bdopt saves its changes in mod_configs/BigDeckOptimization.cfg: the file is restored byte for byte after
# the session (a scenario stopped before its final 'bdopt on' would leave the mod off), unless -KeepConfig.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 900, [int]$TraceSec = 0, [string]$TraceProfile = "cpu-sampling", [string]$TraceProviders = "", [switch]$KeepConfig, [switch]$Visible, [switch]$Sound, [int]$Screen = 1, [switch]$Show, [string]$GameDir = "", [int]$StallSec = 300, [switch]$KeepSlot, [switch]$PresentMon)
# -GameDir, else STS2_GAME_DIR (scripts calling run.ps1, e.g. settings.ps1, then test that copy too).
$copyDir = if ($GameDir) { $GameDir } else { "$env:STS2_GAME_DIR" }
. (Join-Path $PSScriptRoot "parlib.ps1")
Assert-GameNotRunning
# On a copy: no Steam, its own user data, as par.ps1 (with Steam the game writes the test profile to Steam Cloud).
$noSteam = [bool]$copyDir
$script = Join-Path $Scenarios "$Name.txt"
if (-not (Test-Path $script)) { throw "No scenario $script" }
$log = if ($noSteam) { Join-Path $Results "$Name.session.log" } else { Join-Path $UserData "logs\godot.log" }
$UserDataParent = $env:APPDATA
$config = Join-Path $UserData "mod_configs\BigDeckOptimization.cfg"
$backup = Join-Path $Results "BigDeckOptimization.cfg.run-backup"
# Present while a session runs: one stopped from outside (its cleanup never ran) is restored by the next one.
$marker = Join-Path $Results "run.inprogress"
if (Test-Path $marker) {
    if ((Get-Content $marker) -eq "config" -and (Test-Path $backup)) { Copy-Item $backup $config -Force; Write-Output "previous session was interrupted: BigDeckOptimization.cfg restored" }
    elseif ((Get-Content $marker) -eq "no config" -and (Test-Path $config)) { Remove-Item $config; Write-Output "previous session was interrupted: its BigDeckOptimization.cfg removed" }
    Remove-Item $marker
}
$hadConfig = Test-Path $config
if (-not $KeepConfig) {
    if ($hadConfig) { Copy-Item $config $backup -Force; $before = (Get-FileHash $config).Hash }
    Set-Content $marker $(if ($hadConfig) { "config" } else { "no config" })
}
try {
    # A direct exe launch fails Steam initialization without a steam_appid.txt: go through Steam, which passes the
    # extra arguments on (a -GameDir copy has that file and is launched directly).
    # The game window goes off-screen and hands the focus back (AutoTest --bdhidden), unless -Visible; the game is
    # muted (--bdmute, volume settings untouched), unless -Sound.
    $launch = @("--bdtest", "`"$script`"")
    # -Show: fullscreen on screen $Screen instead, to watch the session.
    if ($noSteam) {
        Add-TestMods $copyDir
        Remove-Item $log -ErrorAction SilentlyContinue
        $launch += "--force-steam", "off", "--log-file", "`"$log`""
        $env:APPDATA = if ($KeepSlot) { Join-Path $SlotRoot "slot$KeptSlot" } else { Sync-Slot $KeptSlot }
    }
    if ($Show) { $launch += "--bdshow", $Screen, "--screen", $Screen }
    elseif (-not $Visible) {
        $launch += "--bdhidden", "--windowed", "--resolution", "1920x1080"
        if ($PresentMon) { $launch += "--bdedge" }
    }
    if (-not $Sound) { $launch += "--bdmute" }
    if ($copyDir) {
        if (-not (Test-Path (Join-Path $copyDir "steam_appid.txt"))) { throw "No steam_appid.txt in $copyDir" }
        $copyExe = Join-Path $copyDir "SlayTheSpire2.exe"
        if ($Show -or $Visible) { Start-Process -FilePath $copyExe -WorkingDirectory $copyDir -ArgumentList $launch }
        else { Start-HiddenGame $copyExe $copyDir $launch | Out-Null }
        $env:APPDATA = $UserDataParent
    }
    else {
        Start-Process -FilePath $SteamExe -ArgumentList (@("-applaunch", $AppId) + $launch)
    }
    $t = 0
    while (-not (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep 2; $t += 2 }
    # -PresentMon: frame times measured outside the game, GPU included (results/<Name>.presentmon.csv, presentmon.py). Windows
    # throttles the presents of a window entirely off-screen: one column of pixels stays on screen (--bdedge). By process id:
    # without administrator rights it can't find a process by name once started.
    if ($PresentMon -and ($game = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        $tool = (Get-Command presentmon -ErrorAction SilentlyContinue).Source
        if (-not $tool) { $tool = (Get-ChildItem (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet") -Recurse -Filter "presentmon*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
        if (-not $tool) { throw "PresentMon not found: winget install Intel.PresentMon.Console" }
        $presentCsv = Join-Path $Results "$Name.presentmon.csv"
        Remove-Item $presentCsv -ErrorAction SilentlyContinue
        $presentProcess = Start-Process -FilePath $tool -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $Results "$Name.presentmon.err") -ArgumentList "--process_id", $game.Id, "--output_file", "`"$presentCsv`"", "--terminate_on_proc_exit", "--stop_existing_session", "--no_console_stats"
    }
    $trace = $null
    if ($TraceSec -gt 0 -and ($game = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue)) {
        Start-Sleep 8
        $span = [TimeSpan]::FromSeconds($TraceSec).ToString("dd\:hh\:mm\:ss")
        $trace = Start-Process -FilePath "dotnet-trace" -PassThru -WindowStyle Hidden -ArgumentList "collect", "-p", $game.Id,
            $(if ($TraceProviders) { "--providers" } else { "--profile" }), $(if ($TraceProviders) { $TraceProviders } else { $TraceProfile }), "--duration", $span, "-o", "`"$Results\$Name.nettrace`""
    }
    $t = 0
    $waitStart = Get-Date
    while ((Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and $t -lt $TimeoutSec) {
        Start-Sleep 5; $t += 5
        # A silent log: nothing is moving, the rest of the timeout would only wait. A session handed to the player stays.
        if ($t -gt 60 -and (Test-Path $log) -and ((Get-Date) - (Get-Item $log).LastWriteTime).TotalSeconds -gt $StallSec -and -not (Select-String -Path $log -SimpleMatch "handed over to the player" -Quiet)) {
            Write-Output "STALLED: no log for $StallSec s, closing the game"
            Stop-Process -Name SlayTheSpire2 -Force -ErrorAction SilentlyContinue
            Start-Sleep 3
            break
        }
        # The test runner couldn't load (e.g. a modified game in tools/autotest/mutations.ps1): nothing would run and the
        # window would stay on screen until the timeout.
        if ($t -le 120 -and (Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $waitStart.AddSeconds(-30) -and (Select-String -Path $log -Pattern "Patch AutoTest (skipped|could not)" -Quiet)) {
            Write-Output "the test runner (AutoTest) did not load: closing the game"
            Stop-Process -Name SlayTheSpire2 -Force -ErrorAction SilentlyContinue
            Start-Sleep 3
            break
        }
    }
    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
        Write-Output "TIMEOUT after $t s: closing the game"
        Stop-Process -Name SlayTheSpire2 -Force
        Start-Sleep 3
    }
    if ($trace) { $trace.WaitForExit(60000) | Out-Null }
    if ($presentProcess) {
        $presentProcess.WaitForExit(15000) | Out-Null
        if (-not $presentProcess.HasExited) { Stop-Process -Id $presentProcess.Id -Force -ErrorAction SilentlyContinue }
        python -I (Join-Path $PSScriptRoot "presentmon.py") $presentCsv 10 | ForEach-Object { Write-Output "PresentMon: $_" }
    }
    Copy-Item $log (Join-Path $Results "$Name.log") -Force
    if ($noSteam) { Remove-TestMods $copyDir; Remove-Item $log -ErrorAction SilentlyContinue }
    Save-VersionedLog (Join-Path $Results "$Name.log") $(if ($copyDir) { $copyDir } else { $GameDir })
    Write-Output "done in $t s: $Results\$Name.log"
}
finally {
    if (-not $KeepConfig) {
        if ($hadConfig) {
            Copy-Item $backup $config -Force
            if ($before -ne (Get-FileHash $config).Hash) { Write-Output "WARNING: BigDeckOptimization.cfg not restored, copy $backup back" }
        }
        elseif (Test-Path $config) { Remove-Item $config }
        Remove-Item $marker -ErrorAction SilentlyContinue
    }
}
