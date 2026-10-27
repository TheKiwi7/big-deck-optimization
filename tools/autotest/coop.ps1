# Local co-op: two game instances without Steam (ENet on 127.0.0.1), each running its own scenario:
# scenarios/<Name>-host.txt and scenarios/<Name>-client.txt. Logs go to results/<Name>-host.log / -client.log.
#   powershell -File tools/autotest/coop.ps1 -Name coop [-TimeoutSec 1500] [-WithMods] [-TraceSec 120] [-Visible] [-Sound] [-Screen 1] [-HostVisible]
# -HostVisible: hands-on co-op (scenario cooptry): the host window stays on screen with sound for you to play, the
# client (a scripted fake player) stays hidden and is closed when you close the host.
# -TraceSec records a CPU trace of the host (results/<Name>-host.nettrace); keep the host alive until it ends.
# Without Steam only <game>/mods and <game>/mods_STEAMTEST load. -WithMods copies the Workshop mods enabled in your
# settings into mods_STEAMTEST for the test and always deletes it afterwards (a normal Steam launch reads that folder
# too and would load the mods twice). Copies, never links, so the cleanup can't touch the Workshop folders.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 1500, [switch]$WithMods, [int]$TraceSec = 0, [switch]$Visible, [switch]$Sound, [int]$Screen = 1, [switch]$HostVisible)
. (Join-Path $PSScriptRoot "common.ps1")
Assert-GameNotRunning
$hostScript = Join-Path $Scenarios "$Name-host.txt"
$clientScript = Join-Path $Scenarios "$Name-client.txt"
foreach ($s in $hostScript, $clientScript) { if (-not (Test-Path $s)) { throw "No scenario $s" } }

# Non-Steam instances keep their data in user://default/<net id>: the host is 1, the client 1001. Each folder needs
# the mods warning accepted once (do it by hand on the first launch), then the client reuses the host's settings.
$hostData = Join-Path $UserData "default\1"
$clientData = Join-Path $UserData "default\1001"
if ((Test-Path "$hostData\settings.save") -and -not (Test-Path "$clientData\settings.save")) {
    New-Item -ItemType Directory -Force $clientData | Out-Null
    Copy-Item "$hostData\settings.save" "$clientData\settings.save"
}

$testMods = Join-Path $GameDir "mods_STEAMTEST"
$ownMods = Join-Path $testMods ".bdtest-created"
# Both instances save bdopt changes in the shared mod_configs/BigDeckOptimization.cfg (with BaseLib): restored afterwards.
$config = Join-Path $UserData "mod_configs\BigDeckOptimization.cfg"
$configBackup = Join-Path $Results "BigDeckOptimization.cfg.coop-backup"
# Present while a test runs: a test stopped from outside (its cleanup never ran) is cleaned up by the next one.
$marker = Join-Path $Results "coop.inprogress"
if (Test-Path $marker) {
    Write-Output "the previous co-op test was interrupted: restoring its leftovers"
    if ((Get-Content $marker) -eq "config" -and (Test-Path $configBackup)) { Copy-Item $configBackup $config -Force; Write-Output "  BigDeckOptimization.cfg restored" }
    elseif ((Get-Content $marker) -eq "no config" -and (Test-Path $config)) { Remove-Item $config; Write-Output "  test BigDeckOptimization.cfg removed" }
    if (Test-Path $ownMods) { Remove-Item -Recurse -Force $testMods; Write-Output "  mods_STEAMTEST removed" }
    Remove-Item $marker
}
if (Test-Path $testMods) { throw "$testMods already exists (left over or not ours): remove it first" }
$hadConfig = Test-Path $config
if ($hadConfig) { Copy-Item $config $configBackup -Force; $configHash = (Get-FileHash $config).Hash }
Set-Content $marker $(if ($hadConfig) { "config" } else { "no config" })
if ($WithMods) {
    $settings = Get-Content (Join-Path (Get-SteamAccountDir) "settings.save") -Raw | ConvertFrom-Json
    $wanted = @($settings.mod_settings.mod_list | Where-Object { $_.is_enabled -and $_.source -eq "steam_workshop" -and $_.id -ne "BigDeckOptimization" } | ForEach-Object { $_.id })
    New-Item -ItemType Directory $testMods | Out-Null
    Set-Content $ownMods "created by tools/autotest/coop.ps1"
    foreach ($item in Get-ChildItem $Workshop -Directory) {
        $ids = Get-ChildItem $item.FullName -Recurse -Filter *.json | ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).id } catch { } }
        if (@($ids | Where-Object { $wanted -contains $_ }).Count -gt 0) { Copy-Item -Recurse $item.FullName (Join-Path $testMods $item.Name) }
    }
    Write-Output "copied $(@(Get-ChildItem $testMods -Directory).Count) Workshop mods for: $($wanted -join ', ')"
}

$h = $null; $c = $null
# As run.ps1: both windows off-screen (shown a moment on screen $Screen while the mod loads) and muted, unless -Visible / -Sound.
$extra = @()
if (-not $Visible) { $extra += "--bdhidden", "--screen", $Screen }
if (-not $Sound) { $extra += "--bdmute" }
try {
    $hostLog = Join-Path $Results "$Name-host.log"; $clientLog = Join-Path $Results "$Name-client.log"
    Remove-Item $hostLog, $clientLog -ErrorAction SilentlyContinue
    $hostArgs = @("--force-steam", "off", "--fastmp", "host_standard", "--log-file", "`"$hostLog`"", "--bdtest", "`"$hostScript`"") + $(if ($HostVisible) { @() } else { $extra })
    $h = Start-Process -FilePath $GameExe -WorkingDirectory $GameDir -PassThru -ArgumentList $hostArgs
    $trace = $null
    if ($TraceSec -gt 0) {
        Start-Sleep 5  # the runtime only accepts diagnostics once started
        $span = [TimeSpan]::FromSeconds($TraceSec).ToString("dd\:hh\:mm\:ss")
        $trace = Start-Process -FilePath "dotnet-trace" -PassThru -WindowStyle Hidden -ArgumentList "collect", "-p", $h.Id,
            "--profile", "cpu-sampling", "--duration", $span, "-o", "`"$Results\$Name-host.nettrace`""
    }
    Start-Sleep 12
    $clientArgs = @("--force-steam", "off", "--fastmp", "join", "--clientId", "1001", "--log-file", "`"$clientLog`"", "--bdtest", "`"$clientScript`"") + $extra
    $c = Start-Process -FilePath $GameExe -WorkingDirectory $GameDir -PassThru -ArgumentList $clientArgs
    $t = 0
    while ((-not $h.HasExited -or -not $c.HasExited) -and $t -lt $TimeoutSec -and -not ($HostVisible -and $h.HasExited)) { Start-Sleep 5; $t += 5 }
    if ($trace) { $trace.WaitForExit(60000) | Out-Null }
    Write-Output "done in $t s: $hostLog, $clientLog"
}
finally {
    foreach ($p in @($h, $c)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Output "closed instance $($p.Id)" } }
    if (Test-Path $testMods) { Start-Sleep 2; Remove-Item -Recurse -Force $testMods; Write-Output "removed mods_STEAMTEST: $(-not (Test-Path $testMods))" }
    if ($hadConfig) {
        Copy-Item $configBackup $config -Force
        if ($configHash -ne (Get-FileHash $config).Hash) { Write-Output "WARNING: BigDeckOptimization.cfg not restored, copy $configBackup back" }
    }
    elseif (Test-Path $config) { Remove-Item $config }
    Remove-Item $marker -ErrorAction SilentlyContinue
}
