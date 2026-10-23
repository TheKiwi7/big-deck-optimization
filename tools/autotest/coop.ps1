# Local co-op: two game instances without Steam (ENet on 127.0.0.1), each running its own scenario:
# scenarios/<Name>-host.txt and scenarios/<Name>-client.txt. Logs go to results/<Name>-host.log / -client.log.
#   powershell -File tools/autotest/coop.ps1 -Name coop [-TimeoutSec 1500] [-WithMods]
# Without Steam only <game>/mods and <game>/mods_STEAMTEST load. -WithMods copies the Workshop mods enabled in your
# settings into mods_STEAMTEST for the test and always deletes it afterwards (a normal Steam launch reads that folder
# too and would load the mods twice). Copies, never links, so the cleanup can't touch the Workshop folders.
param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 1500, [switch]$WithMods)
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
if (Test-Path $testMods) { throw "$testMods already exists (left over or not ours): remove it first" }
if ($WithMods) {
    $settings = Get-Content (Join-Path (Get-SteamAccountDir) "settings.save") -Raw | ConvertFrom-Json
    $wanted = @($settings.mod_settings.mod_list | Where-Object { $_.is_enabled -and $_.source -eq "steam_workshop" -and $_.id -ne "BigDeckOptimization" } | ForEach-Object { $_.id })
    New-Item -ItemType Directory $testMods | Out-Null
    foreach ($item in Get-ChildItem $Workshop -Directory) {
        $ids = Get-ChildItem $item.FullName -Recurse -Filter *.json | ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).id } catch { } }
        if (@($ids | Where-Object { $wanted -contains $_ }).Count -gt 0) { Copy-Item -Recurse $item.FullName (Join-Path $testMods $item.Name) }
    }
    Write-Output "copied $(@(Get-ChildItem $testMods -Directory).Count) Workshop mods for: $($wanted -join ', ')"
}

$h = $null; $c = $null
try {
    $hostLog = Join-Path $Results "$Name-host.log"; $clientLog = Join-Path $Results "$Name-client.log"
    Remove-Item $hostLog, $clientLog -ErrorAction SilentlyContinue
    $h = Start-Process -FilePath $GameExe -WorkingDirectory $GameDir -PassThru -ArgumentList "--force-steam", "off",
        "--fastmp", "host_standard", "--log-file", "`"$hostLog`"", "--bdtest", "`"$hostScript`""
    Start-Sleep 12
    $c = Start-Process -FilePath $GameExe -WorkingDirectory $GameDir -PassThru -ArgumentList "--force-steam", "off",
        "--fastmp", "join", "--clientId", "1001", "--log-file", "`"$clientLog`"", "--bdtest", "`"$clientScript`""
    $t = 0
    while ((-not $h.HasExited -or -not $c.HasExited) -and $t -lt $TimeoutSec) { Start-Sleep 5; $t += 5 }
    Write-Output "done in $t s: $hostLog, $clientLog"
}
finally {
    foreach ($p in @($h, $c)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Output "closed instance $($p.Id)" } }
    if (Test-Path $testMods) { Start-Sleep 2; Remove-Item -Recurse -Force $testMods; Write-Output "removed mods_STEAMTEST: $(-not (Test-Path $testMods))" }
}
