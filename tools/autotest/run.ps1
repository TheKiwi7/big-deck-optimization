# Unattended singleplayer session through Steam: runs scenarios/<Name>.txt with the Debug build's AutoTest runner,
# optionally records a CPU trace, and copies the game log to results/<Name>.log.
#   powershell -File tools/autotest/run.ps1 -Name solo-8k [-TimeoutSec 900] [-TraceSec 60 [-TraceProfile gc-verbose] [-TraceProviders <providers>]] [-KeepConfig] [-Visible] [-Sound] [-Screen 1] [-Show] [-GameDir <copy>]
# -GameDir: a full copy of another game version (e.g. the beta, C:\Games\StS2-beta-0.111.0) with a steam_appid.txt
# (2868840) next to its exe, launched directly instead of through Steam (Steam must be running; Workshop mods load as
# usual). Deploy the Debug build into it with dotnet build ... -p:ModDeployDir=<copy>\mods\BigDeckOptimization.
# A trace must end while the game still runs (method names are written at the end): give the scenario a final wait.
# On a copy the session runs without Steam, in its own user data (-KeepSlot: as the previous session left it, for a check over a
# restart), your files untouched. Through Steam it uses yours: with BaseLib, bdopt saves its changes in
# mod_configs/BigDeckOptimization.cfg, restored byte for byte after the session (a scenario stopped before its final 'bdopt on'
# would leave the mod off), unless -KeepConfig. Only this session's game is waited on and closed (by process id), never yours.
# Through Steam only with a Debug build in the Steam install's mods folder (the Workshop's Release build has no test runner): refused
# otherwise. A game whose test runner never logs a line is closed after 90 s. Exit code 1 when the session has a problem.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 900, [int]$TraceSec = 0, [string]$TraceProfile = "cpu-sampling", [string]$TraceProviders = "", [switch]$KeepConfig, [switch]$Visible, [switch]$Sound, [int]$Screen = 1, [switch]$Show, [string]$GameDir = "", [int]$StallSec = 300, [switch]$KeepSlot, [switch]$PresentMon)
# -GameDir, else STS2_GAME_DIR (scripts calling run.ps1, e.g. settings.ps1, then test that copy too).
$copyDir = if ($GameDir) { $GameDir } else { "$env:STS2_GAME_DIR" }
. (Join-Path $PSScriptRoot "parlib.ps1")
Assert-GameNotRunning
# On a copy: no Steam, its own user data, as par.ps1 (with Steam the game writes the test profile to Steam Cloud).
$noSteam = [bool]$copyDir
$script = Join-Path $Scenarios "$Name.txt"
if (-not (Test-Path $script)) { throw "No scenario $script" }
# Through Steam the game loads the Steam install's mods folder and the Workshop: the test runner (AutoTest, in Bench/) is only in a
# Debug build, never in the Workshop's Release one. Without a Debug build there, nothing would run, hide or mute the game: it would
# sit on screen with sound at the main menu until the stall check.
if (-not $noSteam) {
    $steamDll = Join-Path $GameDir "mods\BigDeckOptimization\BigDeckOptimization.dll"
    if (-not (Test-Path $steamDll) -or -not (Test-DebugBuild $steamDll)) { throw "Through Steam no test runner would load: no Debug build in $(Split-Path $steamDll) (the Workshop copy is a Release build, without AutoTest). Give -GameDir <copy> or set STS2_GAME_DIR." }
}
$log = if ($noSteam) { Join-Path $Results "$Name.session.log" } else { Join-Path $UserData "logs\godot.log" }
$UserDataParent = $env:APPDATA
$config = Join-Path $UserData "mod_configs\BigDeckOptimization.cfg"
$backup = Join-Path $Results "BigDeckOptimization.cfg.run-backup"
# Only through Steam does the session use your user data, and so your BigDeckOptimization.cfg: set aside for it (this mod's
# settings at their defaults, a saved bdopt off would leave every patch unapplied) and put back after (-KeepConfig: yours, as
# they are). On a copy the session has its own user data (a slot): your file is never touched.
$touchConfig = -not $noSteam -and -not $KeepConfig
# Present while a session through Steam runs, rewritten every 5 s with the file's hash as the session sees it: one stopped from
# outside (its cleanup never ran) is restored by the next one, unless your file changed after it stopped (yours is kept). Its game
# outlives the script (Steam started it), and may write the file after the last mark: a change is the test's when a log of that
# session was still written then. Decided only with no game running (Assert-GameNotRunning above): never while that game still runs.
$marker = Join-Path $Results "run.inprogress"
function Get-ConfigHash { if (-not (Test-Path $config)) { return "none" }; try { (Get-FileHash $config -ErrorAction Stop).Hash } catch { $null } }
if (Test-Path $marker) {
    $lines = @(Get-Content $marker -Encoding UTF8)
    $state = $lines[0]
    $seen = @($lines | Where-Object { $_ -like "seen *" } | ForEach-Object { $_.Substring(5) }) | Select-Object -First 1
    $markLog = @($lines | Where-Object { $_ -like "log *" } | ForEach-Object { $_.Substring(4) }) | Select-Object -First 1
    $markScript = @($lines | Where-Object { $_ -like "script *" } | ForEach-Object { $_.Substring(7) }) | Select-Object -First 1
    $now = Get-ConfigHash
    $byTest = $now -eq "none" -or $now -eq $seen
    if (-not $byTest -and $markLog -and $markScript) {
        # Godot renames godot.log at its next start: that session's log may be any of the folder's.
        $written = (Get-Item $config).LastWriteTime.AddSeconds(-5)
        $byTest = [bool](Get-ChildItem (Split-Path $markLog) -Filter *.log -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $written -and (Select-String -Path $_.FullName -SimpleMatch "script $markScript (" -Quiet) })
    }
    # A marker of an older run.ps1 (no hash): its rule, a file written over 15 s after the last mark is yours.
    if (-not $seen -and $now -ne "none") { $byTest = (Get-Item $config).LastWriteTime -le (Get-Item $marker).LastWriteTime.AddSeconds(15) }
    if (-not $byTest) {
        # This run writes its own backup over $backup below: the one from before that session moves aside, under the name said.
        $older = "none"
        if (Test-Path $backup) { $older = "$backup.$(Get-Date -Format yyyyMMdd-HHmmss)"; Move-Item $backup $older -Force }
        Write-Output "previous session was interrupted: BigDeckOptimization.cfg changed since, kept as it is (the one before that session: $older)"
    }
    elseif ($state -eq "config" -and (Test-Path $backup)) { Copy-Item $backup $config -Force; Write-Output "previous session was interrupted: BigDeckOptimization.cfg restored" }
    elseif ($state -eq "no config" -and (Test-Path $config)) { Remove-Item $config; Write-Output "previous session was interrupted: its BigDeckOptimization.cfg removed" }
    Remove-Item $marker
}
$hadConfig = Test-Path $config
if ($touchConfig) {
    if ($hadConfig) { Copy-Item $config $backup -Force; $before = (Get-FileHash $config).Hash }
    $markState = if ($hadConfig) { "config" } else { "no config" }
    if ($hadConfig) { Remove-Item $config -Force }
    Set-Content $marker @($markState, "seen none", "log $log", "script $script") -Encoding UTF8
}
$resultLog = Join-Path $Results "$Name.log"
$slotLocked = $false
$modsAdded = $false
$game = $null
$exitCode = 0
try {
    # A log left by an earlier session must never be judged as this one's.
    Remove-Item $resultLog -ErrorAction SilentlyContinue
    # A direct exe launch fails Steam initialization without a steam_appid.txt: go through Steam, which passes the
    # extra arguments on (a -GameDir copy has that file and is launched directly).
    # The game window goes off-screen and hands the focus back (AutoTest --bdhidden), unless -Visible; the game is
    # muted (--bdmute, volume settings untouched), unless -Sound.
    $launch = @("--bdtest", "`"$script`"")
    # -Show: fullscreen on screen $Screen instead, to watch the session.
    if ($noSteam) {
        Restore-AsideMenu $copyDir
        Add-TestMods $copyDir
        $modsAdded = $true
        Remove-Item $log -ErrorAction SilentlyContinue
        $launch += "--force-steam", "off", "--log-file", "`"$log`""
        $slotLocked = Lock-Slot $KeptSlot
        $env:APPDATA = if ($KeepSlot) { Join-Path $SlotRoot "slot$KeptSlot" } else { Sync-Slot $KeptSlot }
    }
    if ($Show) { $launch += "--bdshow", $Screen, "--screen", $Screen }
    elseif (-not $Visible) {
        $launch += "--bdhidden", "--windowed", "--resolution", "1920x1080"
        if ($PresentMon) { $launch += "--bdedge" }
    }
    if (-not $Sound) { $launch += "--bdmute" }
    $launched = Get-Date
    if ($copyDir) {
        if (-not (Test-Path (Join-Path $copyDir "steam_appid.txt"))) { throw "No steam_appid.txt in $copyDir" }
        $copyExe = Join-Path $copyDir "SlayTheSpire2.exe"
        $game = if ($Show -or $Visible) { Start-Process -FilePath $copyExe -WorkingDirectory $copyDir -ArgumentList $launch -PassThru } else { Start-HiddenGame $copyExe $copyDir $launch }
        $env:APPDATA = $UserDataParent
    }
    else {
        Start-Process -FilePath $SteamExe -ArgumentList (@("-applaunch", $AppId) + $launch)
        # Steam starts the game: found by its command line (this scenario's --bdtest), never by its name alone, which would also
        # find your own game if you start it meanwhile.
        # Steam may start cold, update or log in first: up to 3 minutes. A game that starts even later is looked for once more and
        # closed in the cleanup below, and the marker stays for the next run (it would play the scenario in your user data).
        $t = 0
        while (-not $game -and $t -lt 180) { Start-Sleep 2; $t += 2; $game = Find-TestGame $script $launched }
    }
    if (-not $game) { throw "the game did not start within 3 minutes" }
    [void]$game.Handle # or HasExited can't be read once it ends
    # -PresentMon: frame times measured outside the game, GPU included (results/<Name>.presentmon.csv, presentmon.py). Windows
    # throttles the presents of a window entirely off-screen: one column of pixels stays on screen (--bdedge). By process id:
    # without administrator rights it can't find a process by name once started.
    if ($PresentMon) {
        $tool = (Get-Command presentmon -ErrorAction SilentlyContinue).Source
        if (-not $tool) { $tool = (Get-ChildItem (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet") -Recurse -Filter "presentmon*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
        if (-not $tool) { throw "PresentMon not found: winget install Intel.PresentMon.Console" }
        $presentCsv = Join-Path $Results "$Name.presentmon.csv"
        Remove-Item $presentCsv -ErrorAction SilentlyContinue
        $presentProcess = Start-Process -FilePath $tool -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $Results "$Name.presentmon.err") -ArgumentList "--process_id", $game.Id, "--output_file", "`"$presentCsv`"", "--terminate_on_proc_exit", "--stop_existing_session", "--no_console_stats"
    }
    $trace = $null
    if ($TraceSec -gt 0) {
        Start-Sleep 8
        $span = [TimeSpan]::FromSeconds($TraceSec).ToString("dd\:hh\:mm\:ss")
        $trace = Start-Process -FilePath "dotnet-trace" -PassThru -WindowStyle Hidden -ArgumentList "collect", "-p", $game.Id,
            $(if ($TraceProviders) { "--providers" } else { "--profile" }), $(if ($TraceProviders) { $TraceProviders } else { $TraceProfile }), "--duration", $span, "-o", "`"$Results\$Name.nettrace`""
    }
    $t = 0
    $waitStart = Get-Date
    $runnerSeen = $false
    # This session's game only, by its process id: another game running meanwhile (yours, another test's) is never waited on nor closed.
    while (-not $game.HasExited -and $t -lt $TimeoutSec) {
        Start-Sleep 5; $t += 5
        # The hash only when readable (the game may be writing the file).
        if ($touchConfig -and ($hash = Get-ConfigHash)) { Set-Content $marker @($markState, "seen $hash", "log $log", "script $script") -Encoding UTF8 }
        # A silent log: nothing is moving, the rest of the timeout would only wait. A session handed to the player stays.
        if ($t -gt 60 -and (Test-Path $log) -and ((Get-Date) - (Get-Item $log).LastWriteTime).TotalSeconds -gt $StallSec -and -not (Select-String -Path $log -SimpleMatch "handed over to the player" -Quiet)) {
            Write-Output "STALLED: no log for $StallSec s, closing the game"
            Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep 3
            break
        }
        # The test runner couldn't load (e.g. a modified game in tools/autotest/mutations.ps1): nothing would run and the
        # window would stay on screen until the timeout.
        if ($t -le 120 -and (Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $waitStart.AddSeconds(-30) -and (Select-String -Path $log -Pattern "Patch AutoTest (skipped|could not)" -Quiet)) {
            Write-Output "the test runner (AutoTest) did not load: closing the game"
            Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep 3
            break
        }
        # The test runner never started (no Debug build in the copy, the mod not loaded or disabled): nothing would hide, mute nor
        # run the game, which would sit at the main menu until the stall check. Its first line comes within seconds of the menu.
        if ($t -ge 90 -and -not $runnerSeen -and (Test-Path $log) -and (Get-Item $log).LastWriteTime -ge $launched) {
            $runnerSeen = [bool](Select-String -Path $log -Pattern "\] AutoTest " -Quiet)
            if (-not $runnerSeen) {
                Write-Output "AUTOTEST NOT LOADED: no test runner line after $t s (no Debug build, or the mod not loaded): closing the game"
                Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
                Start-Sleep 3
                break
            }
        }
    }
    if (-not $game.HasExited) {
        Write-Output "TIMEOUT after $t s: closing the game"
        Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep 3
    }
    if ($trace) { $trace.WaitForExit(60000) | Out-Null }
    if ($presentProcess) {
        $presentProcess.WaitForExit(15000) | Out-Null
        if (-not $presentProcess.HasExited) { Stop-Process -Id $presentProcess.Id -Force -ErrorAction SilentlyContinue }
        python -I (Join-Path $PSScriptRoot "presentmon.py") $presentCsv 10 | ForEach-Object { Write-Output "PresentMon: $_" }
    }
    # Only this session's log: written since the launch and naming this scenario (through Steam godot.log may still be an older
    # session's, or your own game's). Otherwise no results log, and the judge says so.
    if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -ge $launched -and (Select-String -Path $log -SimpleMatch "script $script (" -Quiet)) {
        Copy-Item $log $resultLog -Force
        Save-VersionedLog $resultLog $(if ($copyDir) { $copyDir } else { $GameDir })
    }
    else { Write-Output "NO SESSION LOG: $log is missing or isn't this session's" }
    # On a copy, the Workshop mods the game must have found (a mods_STEAMTEST emptied by another series would go unseen).
    $missing = if ($noSteam) { @(Get-MissingTestMods $resultLog $copyDir) } else { @() }
    if ($noSteam) { Remove-Item $log -ErrorAction SilentlyContinue }
    Write-Output "done in $t s: $resultLog"
    # The session's problems (judge.py): a failed line, an error or a difference this mod found itself, a session cut short...
    $judged = @(Show-Judgment @($resultLog)) + @($missing | Where-Object { $_ } | ForEach-Object { "PROBLEM $Name.log: $_" })
    $judged | ForEach-Object { Write-Output "  $_" }
    if (-not ($judged -like "PROBLEM *")) { Write-Output "  no problem in the log" }
    # Exit code 1 on any problem (a session cut short, stalled or timed out has no end line: judge.py says so), for the scripts and
    # monitors waiting on this one.
    else { $exitCode = 1 }
}
finally {
    $env:APPDATA = $UserDataParent
    if ($game -and -not $game.HasExited) { Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue }
    # Through Steam and no game found in time: one that starts later would play the scenario unwatched, off-screen, in your user data.
    $lateGame = $false
    if (-not $game -and -not $noSteam -and $launched) {
        for ($wait = 0; $wait -lt 60 -and -not $game; $wait += 2) { Start-Sleep 2; $game = Find-TestGame $script $launched }
        if ($game) { Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 3; Write-Output "the game started late: closed" }
        else { $lateGame = $true }
    }
    if ($slotLocked) { Unlock-Slot $KeptSlot }
    if ($modsAdded) { Remove-TestMods $copyDir }
    if ($touchConfig) {
        if ($hadConfig) {
            Copy-Item $backup $config -Force
            if ($before -ne (Get-FileHash $config).Hash) { Write-Output "WARNING: BigDeckOptimization.cfg not restored, copy $backup back" }
        }
        elseif (Test-Path $config) { Remove-Item $config }
        # A game Steam may still start: the marker stays, with the file as restored, so that the next run.ps1 puts it back if that
        # game's session changes it (its log names this scenario).
        if ($lateGame) {
            Set-Content $marker @($markState, "seen $(Get-ConfigHash)", "log $log", "script $script") -Encoding UTF8
            Write-Output "no game found: if Steam starts it later, close it; the next run.ps1 restores BigDeckOptimization.cfg if that session changed it"
        }
        else { Remove-Item $marker -ErrorAction SilentlyContinue }
    }
}
exit $exitCode
