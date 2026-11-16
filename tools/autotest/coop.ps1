# Local co-op: two game instances without Steam (ENet on 127.0.0.1), each running its own scenario:
# scenarios/<Name>-host.txt and scenarios/<Name>-client.txt. Logs go to results/<Name>-host.log / -client.log.
#   powershell -File tools/autotest/coop.ps1 -Name coop [-TimeoutSec 1500] [-WithMods] [-TraceSec 120 [-TraceProfile gc-verbose] [-TraceProviders <providers>]] [-Visible] [-Sound] [-Screen 1] [-HostVisible]
# -HostVisible: hands-on co-op (scenario cooptry): the host window stays on screen with sound for you to play, the
# client (a scripted fake player) stays hidden and is closed when you close the host.
# -TraceSec records a CPU trace of the host (results/<Name>-host.nettrace); keep the host alive until it ends.
# Without Steam only <game>/mods and <game>/mods_STEAMTEST load. -WithMods copies the Workshop mods enabled in your
# settings (and BDTEST_EXTRA_MODS) into a game copy's mods_STEAMTEST for the test (parlib.ps1's Add-TestMods: shared with the
# other series on that copy, removed when the last one ends). Copies, never links, so the cleanup can't touch the Workshop folders.
# -StopWhen <regex>: ends the test as soon as the client log matches it (a client without this mod never quits by itself).
# -ClientHeadless: the client has no window at all (a client without this mod can't hide one).
# -ClientWithout <mod id>: that mod disabled in the client's profile (coop-required.ps1: the client without this mod).
# -TraceDelaySec: the trace starts that long after the host: dotnet-trace's convert overflows its stack on long traces with many plays.
# Both instances run in test slot 70 (a mirror of your user data, as parcoop.ps1): their saves, bdopt changes (with BaseLib) and
# profiles stay there, your files are never touched, even when this script is stopped from outside and its games play on.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 1500, [switch]$WithMods, [int]$TraceSec = 0, [int]$TraceDelaySec = 0, [string]$TraceProfile = "cpu-sampling", [string]$TraceProviders = "", [switch]$Visible, [switch]$Sound, [int]$Screen = 1, [switch]$HostVisible, [string]$StopWhen = "", [switch]$ClientHeadless, [string]$ClientWithout = "")
. (Join-Path $PSScriptRoot "parlib.ps1")
Assert-GameNotRunning
Set-CopyOverrides $GameDir
$hostScript = Join-Path $Scenarios "$Name-host.txt"
$clientScript = Join-Path $Scenarios "$Name-client.txt"
foreach ($s in $hostScript, $clientScript) { if (-not (Test-Path $s)) { throw "No scenario $s" } }
$CoopSlot = 70

# Never on the Steam install: a normal launch reads its mods_STEAMTEST before the Workshop, so a copy left there (a test stopped
# from outside) would shadow the Workshop mods, and their updates, in your own games. Set STS2_GAME_DIR to a game copy.
if ($WithMods -and ($GameDir -match 'steamapps' -or -not (Test-Path (Join-Path $GameDir "steam_appid.txt")))) { throw "-WithMods copies mods into $GameDir\mods_STEAMTEST: use a game copy (STS2_GAME_DIR, with a steam_appid.txt), not the Steam install" }
# An older coop.ps1 used your own user data and set your BigDeckOptimization.cfg aside: put back what one stopped from outside left.
$config = Join-Path $UserData "mod_configs\BigDeckOptimization.cfg"
$configBackup = Join-Path $Results "BigDeckOptimization.cfg.coop-backup"
$marker = Join-Path $Results "coop.inprogress"
if (Test-Path $marker) {
    Write-Output "an older co-op test was interrupted: restoring its leftovers"
    $stopped = (Get-Item $marker).LastWriteTime.AddSeconds(15)
    if ((Test-Path $config) -and (Get-Item $config).LastWriteTime -gt $stopped) { Write-Output "  BigDeckOptimization.cfg changed since, kept as it is (the one before that test: $configBackup)" }
    elseif ((Get-Content $marker) -eq "config" -and (Test-Path $configBackup)) { Copy-Item $configBackup $config -Force; Write-Output "  BigDeckOptimization.cfg restored" }
    elseif ((Get-Content $marker) -eq "no config" -and (Test-Path $config)) { Remove-Item $config; Write-Output "  test BigDeckOptimization.cfg removed" }
    Remove-Item $marker
}
$testMods = Join-Path $GameDir "mods_STEAMTEST"
# Without -WithMods only this mod: a mods_STEAMTEST another series uses on this copy would load its mods too.
if (-not $WithMods -and (Test-Path $testMods)) { Remove-TestMods $GameDir; if (Test-Path $testMods) { throw "$testMods exists (another series on this copy, or not this tooling's): without -WithMods its mods would load" } }
$realAppData = $env:APPDATA
$slotLocked = $false
$modsAdded = $false

$h = $null; $c = $null
# Exit code 1 when either log has a problem (or the script failed before judging), for the scripts and monitors waiting on this one.
$exitCode = 0
# As run.ps1: both windows off-screen and muted, unless -Visible / -Sound.
$extra = @()
if (-not $Visible) { $extra += "--bdhidden", "--windowed", "--resolution", "1920x1080" }
if (-not $Sound) { $extra += "--bdmute" }
try {
    $slotLocked = Lock-Slot $CoopSlot
    if ($WithMods) { Add-TestMods $GameDir; $modsAdded = $true; Write-Output "test mods: $((Get-TestModIds) -join ', ')" }
    $slot = Sync-Slot $CoopSlot -Coop
    # Non-Steam instances keep their data in user://default/<net id>: the host is 1, the client 1001. Each folder needs the mods
    # warning accepted once (by hand on a first launch, in your own user data, which the slot mirrors); the client reuses the host's.
    $hostData = Join-Path $slot "SlayTheSpire2\default\1"
    $clientData = Join-Path $slot "SlayTheSpire2\default\1001"
    if ((Test-Path "$hostData\settings.save") -and -not (Test-Path "$clientData\settings.save")) {
        New-Item -ItemType Directory -Force $clientData | Out-Null
        Copy-Item "$hostData\settings.save" "$clientData\settings.save"
    }
    if ($ClientWithout) {
        $file = Join-Path $clientData "settings.save"
        $data = Get-Content $file -Raw | ConvertFrom-Json
        $entries = @($data.mod_settings.mod_list | Where-Object { $_.id -eq $ClientWithout })
        if (-not $entries.Count) { throw "-ClientWithout ${ClientWithout}: no such mod in the client's mod list ($file)" }
        $entries | ForEach-Object { $_.is_enabled = $false }
        # No BOM: the game's JSON reader expects plain UTF-8.
        [IO.File]::WriteAllText($file, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
    }
    $hostLog = Join-Path $Results "$Name-host.log"; $clientLog = Join-Path $Results "$Name-client.log"
    Remove-Item $hostLog, $clientLog -ErrorAction SilentlyContinue
    $env:APPDATA = $slot
    $hostArgs = @("--force-steam", "off", "--fastmp", "host_standard", "--log-file", "`"$hostLog`"", "--bdtest", "`"$hostScript`"") + $(if ($HostVisible) { @() } else { $extra }) + @(Get-ScenarioLaunchArgs $hostScript)
    $h = if ($Visible -or $HostVisible) { Start-Process -FilePath $GameExe -WorkingDirectory $GameDir -PassThru -ArgumentList $hostArgs } else { Start-HiddenGame $GameExe $GameDir $hostArgs }
    $trace = $null
    if ($TraceSec -gt 0) {
        Start-Sleep 5  # the runtime only accepts diagnostics once started
        $span = [TimeSpan]::FromSeconds($TraceSec).ToString("dd\:hh\:mm\:ss")
        Remove-Item "$Results\$Name-host.nettrace" -ErrorAction SilentlyContinue
        $traceArgs = @("collect", "-p", $h.Id, $(if ($TraceProviders) { "--providers" } else { "--profile" }), $(if ($TraceProviders) { $TraceProviders } else { $TraceProfile }), "--duration", $span, "-o")
        $trace = if ($TraceDelaySec -gt 0) {
            Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList "-NoProfile", "-Command", "`"Start-Sleep $TraceDelaySec; & dotnet-trace $($traceArgs -join ' ') '$Results\$Name-host.nettrace'`""
        } else { Start-Process -FilePath "dotnet-trace" -PassThru -WindowStyle Hidden -ArgumentList ($traceArgs + "`"$Results\$Name-host.nettrace`"") }
    }
    Start-Sleep 12
    $clientArgs = @("--force-steam", "off", "--fastmp", "join", "--clientId", "1001", "--log-file", "`"$clientLog`"", "--bdtest", "`"$clientScript`"") + $extra + @(Get-ScenarioLaunchArgs $clientScript)
    if ($ClientHeadless) { $clientArgs = @("--headless") + $clientArgs }
    $c = if ($Visible) { Start-Process -FilePath $GameExe -WorkingDirectory $GameDir -PassThru -ArgumentList $clientArgs } else { Start-HiddenGame $GameExe $GameDir $clientArgs }
    $env:APPDATA = $realAppData
    $t = 0
    while ((-not $h.HasExited -or -not $c.HasExited) -and $t -lt $TimeoutSec -and -not ($HostVisible -and $h.HasExited)) {
        Start-Sleep 5; $t += 5
        if ($StopWhen -and (Test-Path $clientLog) -and (Select-String -Path $clientLog -Pattern $StopWhen -Quiet)) { Write-Output "client log matched '$StopWhen': closing"; break }
        # The test runner never started on the host (no Debug build, or it failed to load): nothing would run, the windows would
        # wait until the timeout.
        if ($t -ge 90 -and (Test-Path $hostLog) -and -not (Select-String -Path $hostLog -Pattern "\] AutoTest " -Quiet)) { Write-Output "the test runner (AutoTest) did not start on the host: closing"; break }
        if ($t -le 120 -and (Test-Path $hostLog) -and (Select-String -Path $hostLog -Pattern "Patch AutoTest (skipped|could not)" -Quiet)) { Write-Output "the test runner (AutoTest) did not load: closing"; break }
    }
    if ($trace) { $trace.WaitForExit(60000) | Out-Null }
    Save-VersionedLog $hostLog $GameDir; Save-VersionedLog $clientLog $GameDir
    Write-Output "done in $t s: $hostLog, $clientLog"
    # Each peer's problems (judge.py): a failed line, an error or a difference this mod found itself, a session cut short...
    # A divergence is one too (judge.py), seen here as in parcoop.ps1 and coop4.ps1 even when no wait of the client's script was on.
    # -StopWhen ends both sessions on purpose (a client without this mod has no test runner to end it): no end expected there.
    $judged = @(Show-Judgment @($hostLog, $clientLog) -NoEnd:([bool]$StopWhen))
    # -WithMods: the Workshop mods each game must have found (a client without a mod, -ClientWithout, still finds its files).
    if ($modsAdded) { foreach ($log in $hostLog, $clientLog) { Get-MissingTestMods $log $GameDir | ForEach-Object { $judged += "PROBLEM $(Split-Path $log -Leaf): $_" } } }
    foreach ($log in $hostLog, $clientLog) {
        Select-String -Path $log -CaseSensitive -Pattern "State divergence" -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $judged += "PROBLEM $(Split-Path $log -Leaf): $($_.Line.Trim())" }
    }
    $judged | Select-Object -Unique | ForEach-Object { Write-Output "  $_" }
    if (-not ($judged -like "PROBLEM *")) { Write-Output "  no problem in either log" }
    else { $exitCode = 1 }
}
finally {
    $env:APPDATA = $realAppData
    foreach ($p in @($h, $c)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Output "closed instance $($p.Id)" } }
    if ($slotLocked) { Unlock-Slot $CoopSlot }
    if ($modsAdded) { Remove-TestMods $GameDir; Write-Output "test mods released: mods_STEAMTEST $(if (Test-Path $testMods) { 'kept (another series on this copy uses it)' } else { 'removed' })" }
}
exit $exitCode
