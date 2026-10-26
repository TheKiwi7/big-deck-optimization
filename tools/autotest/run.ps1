# Unattended singleplayer session through Steam: runs scenarios/<Name>.txt with the Debug build's AutoTest runner,
# optionally records a CPU trace, and copies the game log to results/<Name>.log.
#   powershell -File tools/autotest/run.ps1 -Name solo-8k [-TimeoutSec 900] [-TraceSec 60 [-TraceProfile gc-verbose]] [-KeepConfig] [-Visible] [-Sound] [-Screen 1] [-Show] [-GameDir <copy>]
# -GameDir: a full copy of another game version (e.g. the beta, C:\Games\StS2-beta-0.111.0) with a steam_appid.txt
# (2868840) next to its exe, launched directly instead of through Steam (Steam must be running; Workshop mods load as
# usual). Deploy the Debug build into it with dotnet build ... -p:ModDeployDir=<copy>\mods\BigDeckOptimization.
# A trace must end while the game still runs (method names are written at the end): give the scenario a final wait.
# With BaseLib, bdopt saves its changes in mod_configs/BigDeckOptimization.cfg: the file is restored byte for byte after
# the session (a scenario stopped before its final 'bdopt on' would leave the mod off), unless -KeepConfig.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 900, [int]$TraceSec = 0, [string]$TraceProfile = "cpu-sampling", [switch]$KeepConfig, [switch]$Visible, [switch]$Sound, [int]$Screen = 1, [switch]$Show, [string]$GameDir = "")
$copyDir = $GameDir
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$script = Join-Path $Scenarios "$Name.txt"
if (-not (Test-Path $script)) { throw "No scenario $script" }
$log = Join-Path $UserData "logs\godot.log"
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
    # Hidden sessions open on screen $Screen (Godot's --screen, 0 = first): the window shows there for a moment before
    # the mod moves it away.
    # -Show (record.ps1): fullscreen on screen $Screen instead, for a screen recording.
    if ($Show) { $launch += "--bdshow", $Screen, "--screen", $Screen }
    elseif (-not $Visible) { $launch += "--bdhidden", "--screen", $Screen }
    if (-not $Sound) { $launch += "--bdmute" }
    if ($copyDir) {
        if (-not (Test-Path (Join-Path $copyDir "steam_appid.txt"))) { throw "No steam_appid.txt in $copyDir" }
        Start-Process -FilePath (Join-Path $copyDir "SlayTheSpire2.exe") -WorkingDirectory $copyDir -ArgumentList $launch
    }
    else {
        Start-Process -FilePath $SteamExe -ArgumentList (@("-applaunch", $AppId) + $launch)
    }
    $t = 0
    while (-not (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep 2; $t += 2 }
    $trace = $null
    if ($TraceSec -gt 0 -and ($game = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue)) {
        Start-Sleep 8
        $span = [TimeSpan]::FromSeconds($TraceSec).ToString("dd\:hh\:mm\:ss")
        $trace = Start-Process -FilePath "dotnet-trace" -PassThru -WindowStyle Hidden -ArgumentList "collect", "-p", $game.Id,
            "--profile", $TraceProfile, "--duration", $span, "-o", "`"$Results\$Name.nettrace`""
    }
    $t = 0
    while ((Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) -and $t -lt $TimeoutSec) { Start-Sleep 5; $t += 5 }
    if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
        Write-Output "TIMEOUT after $t s: closing the game"
        Stop-Process -Name SlayTheSpire2 -Force
        Start-Sleep 3
    }
    if ($trace) { $trace.WaitForExit(60000) | Out-Null }
    Copy-Item $log (Join-Path $Results "$Name.log") -Force
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
