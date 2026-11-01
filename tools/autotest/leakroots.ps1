# Who holds the runs a session has left: runs memory-runs-on (or -Scenario) headless on a game copy, dumps the process at
# the last menu, and for every RunState prints what roots it (dotnet-dump gcroot), counting chains through this mod.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/leakroots.ps1 -GameDir C:\Games\StS2-public-0.107.1 [-Scenario memory-runs-off]
# The dump (about 1.5 GB) is deleted afterwards. Needs dotnet-dump (dotnet tool install -g dotnet-dump --version 9.0.*).
param([string]$GameDir = "", [string]$Scenario = "memory-runs-on")
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
$GameDir = Resolve-GameCopy $copy
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
$out = Join-Path (Get-VersionDir $GameDir) "leakroots-$Scenario"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
$script = Join-Path $out "scenario.txt"
[IO.File]::WriteAllText($script, ([IO.File]::ReadAllText((Join-Path $Scenarios "$Scenario.txt")) -replace "(?m)^quit\r?$", "wait 240`nquit"))
$log = Join-Path $out "session.log"
$realAppData = $env:APPDATA
$env:APPDATA = Sync-Slot 90
Add-TestMods $GameDir
$game = Start-Process -FilePath (Join-Path $GameDir "SlayTheSpire2.exe") -WorkingDirectory $GameDir -PassThru -WindowStyle Hidden -ArgumentList @("--force-steam", "off", "--log-file", "`"$log`"", "--bdtest", "`"$script`"", "--bdmute", "--headless")
$env:APPDATA = $realAppData
$dump = Join-Path $out "game.dmp"
try {
    $end = (Get-Date).AddSeconds(400)
    Start-Sleep 20
    while ((Get-Date) -lt $end -and -not $game.HasExited -and -not (Select-String -Path $log -Pattern "> wait 240" -Quiet)) { Start-Sleep 2 }
    if ($game.HasExited -or (Get-Date) -ge $end) { throw "The session didn't reach its last menu: $log" }
    Select-String -Path $log -Pattern "GCFULL menu" | Select-Object -Last 1 | ForEach-Object { Write-Output ($_.Line -replace '^.*AutoTest\s+[\d.]+s\s+', '') }
    dotnet-dump collect -p $game.Id --type Heap -o $dump | Select-Object -Last 1 | Out-Null
}
finally {
    Stop-Process -Id $game.Id -Force -ErrorAction SilentlyContinue
    Remove-TestMods $GameDir
}
$runState = "MegaCrit.Sts2.Core.Runs.RunState"
$stat = dotnet-dump analyze $dump -c "dumpheap -type $runState -stat" -c "exit"
$table = ($stat | Where-Object { $_ -match "\s$([regex]::Escape($runState))\s*$" } | Select-Object -First 1)
if (-not $table) { throw "No $runState in the dump" }
$methodTable = ($table.Trim() -split '\s+')[0]
$addresses = @(dotnet-dump analyze $dump -c "dumpheap -mt $methodTable -short" -c "exit" | Where-Object { $_ -match '^\s*[0-9a-f]{12,16}\s*$' } | ForEach-Object { $_.Trim() })
Write-Output "$($addresses.Count) RunState object(s) alive"
$inMod = 0
foreach ($address in $addresses) {
    $file = Join-Path $out "gcroot-$address.txt"
    dotnet-dump analyze $dump -c "gcroot $address" -c "exit" | Set-Content $file -Encoding utf8
    $text = Get-Content $file
    # The test runner awaiting a frame holds NGame: not in a Release build.
    $mod = @($text | Where-Object { $_ -match "BigDeckOptimization" -and $_ -notmatch "BigDeckOptimization\.Bench" }).Count
    $inMod += $mod
    # The first chain, without addresses: the type holding the run, from its root.
    $chain = @($text | Where-Object { $_ -match '->' } | ForEach-Object { ($_ -replace '^.*->\s*[0-9a-f]+\s+', '').Trim() } | Select-Object -First 12)
    $roots = ($text | Where-Object { $_ -match 'Found \d+ unique' } | Select-Object -First 1)
    Write-Output "$address`: $roots; lines through this mod: $mod"
    Write-Output "    $($chain -join ' -> ')"
}
Remove-Item $dump -Force -ErrorAction SilentlyContinue
Write-Output "lines through this mod, all runs: $inMod (details in $out)"
exit $(if ($inMod) { 1 } else { 0 })
