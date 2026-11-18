# A declared battery of test steps on the game copies, with checks before it starts and output meant for a Monitor.
#
#   battery.ps1 -File <battery.psd1 | battery.json> [-DryRun] [-SkipChecks] [-Only <block name>] [-Build <dir>] [-Log <file>]
#
# The file (see batteries\release.psd1) declares:
#   Name, Jobs (the shared -Jobs pool: every par of the battery gets the same value, slots 0..Jobs-1), TimeoutSec (default per
#   step, seconds), Config ('Debug', the build the copies must hold), Env (variables for every step), Copies (@{ Name; Dir } list),
#   Blocks = @( @{ Name; Mode; Copies (names, default all); Steps = @( step... ) } ).
#   Mode: Parallel   every step of the block on every copy at once (solo par lists: they share the Jobs pool);
#         Exclusive  copy by copy, step by step, one thing at a time (co-op, rendered, memory, settings, mutations);
#         Both       step by step, the copies at once (sweeps: each step on both copies together).
#   Step: @{ Name; Script = 'par.ps1'; Args = @('-Names','x,y','-GameDir','{copy}'); Env = @{...}; TimeoutSec }, or the shortcut
#         @{ Par = 'x,y'; Args = @('-UseBaselines') } for par.ps1 -Names x,y -GameDir {copy} -Jobs {jobs}. {copy} is the copy's
#         folder, {jobs} the pool, {version} the copy's game version. STS2_GAME_DIR is the copy's folder for every step.
# Checks before the start (-SkipChecks leaves them out): each copy holds the DLLs of the build folder (hash), no game of a copy is
# running, no test slot is locked by another series. A failed check ends with "BATTERY ABORT <why>" and exit code 2.
# Output (stdout and the -Log file, default results\battery\<name>.log; the steps' full output next to it, one file per step):
#   STEP <name> <copy> exit N problems K        one per finished step
#   FAIL <name> <copy> exit N problems K | <first line of triage.py>      only for a failed step
#   BATTERY DONE <name> steps S failed F        the last line
# Exit code: 0 all steps passed, 1 a step failed, 2 a check failed.
param([Parameter(Mandatory)][string]$File, [switch]$DryRun, [switch]$SkipChecks, [string]$Only = "", [string]$Build = "", [string]$Log = "")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")
$auto = $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $auto)

function Read-Battery([string]$path) {
    if ($path -like "*.json") { Get-Content $path -Raw | ConvertFrom-Json } else { Import-PowerShellDataFile $path }
}
# The (name, value) pairs of a hashtable or a JSON object.
function Get-Pairs($obj) {
    if (-not $obj) { return @() }
    if ($obj -is [hashtable]) { return @($obj.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Name = "$($_.Key)"; Value = "$($_.Value)" } }) }
    @($obj.PSObject.Properties | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Value = "$($_.Value)" } })
}
$bat = Read-Battery $File
$batName = if ($bat.Name) { "$($bat.Name)" } else { [IO.Path]::GetFileNameWithoutExtension($File) }
$jobsPool = if ($bat.Jobs) { [int]$bat.Jobs } else { 14 }
$config = if ($bat.Config) { "$($bat.Config)" } else { "Debug" }
$copies = if ($bat.Copies) { @($bat.Copies | ForEach-Object { [pscustomobject]@{ Name = "$($_.Name)"; Dir = "$($_.Dir)" } }) }
          else { @([pscustomobject]@{ Name = "pub"; Dir = "C:\Games\StS2-public-0.111.1" }, [pscustomobject]@{ Name = "beta"; Dir = "C:\Games\StS2-beta-0.113.1" }) }
foreach ($c in $copies) { $c | Add-Member Version (Get-GameTag $c.Dir) }
$outDir = Join-Path (Join-Path $Results "battery") $batName
if (-not $Log) { $Log = Join-Path (Join-Path $Results "battery") "$batName.log" }
New-Item -ItemType Directory -Force $outDir | Out-Null
[IO.File]::WriteAllText($Log, "")

function Say([string]$line) {
    Write-Output $line
    [IO.File]::AppendAllText($Log, $line + "`r`n", (New-Object Text.UTF8Encoding $false))
}

# ---- checks -------------------------------------------------------------------------------------------------------------
function Test-Battery {
    $problems = @()
    $buildDir = if ($Build) { $Build } else { Join-Path $repo "src\BigDeckOptimization\bin\$config" }
    foreach ($c in $copies) {
        if (-not (Test-Path $c.Dir)) { $problems += "copy $($c.Name): $($c.Dir) not found"; continue }
        foreach ($dll in "BigDeckOptimization.dll", "BigDeckOptimization.Menu.dll") {
            $built, $deployed = (Join-Path $buildDir $dll), (Join-Path $c.Dir "mods\BigDeckOptimization\$dll")
            if (-not (Test-Path $built)) { $problems += "build output $built missing"; continue }
            if (-not (Test-Path $deployed)) { $problems += "copy $($c.Name): $dll not deployed"; continue }
            if ((Get-FileHash $built).Hash -ne (Get-FileHash $deployed).Hash) { $problems += "copy $($c.Name): $dll differs from $buildDir (deploy the last build)" }
        }
        $running = @(Get-Process -Name "SlayTheSpire2*" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($c.Dir, "OrdinalIgnoreCase") })
        if ($running.Count) { $problems += "copy $($c.Name): $($running.Count) game process(es) running (pid $($running[0].Id))" }
    }
    # Test slots: a lock file another series holds can't be opened (parlib.ps1's Lock-Slot).
    $slotRoot = Join-Path $env:LOCALAPPDATA "BigDeckOptimization\test-slots"
    $busy = @()
    if (Test-Path $slotRoot) {
        foreach ($lock in Get-ChildItem $slotRoot -Filter "slot*.lock") {
            try { $s = [IO.File]::Open($lock.FullName, "Open", "ReadWrite", "None"); $s.Dispose() } catch { $busy += $lock.BaseName }
        }
    }
    if ($busy.Count) { $problems += "test slots in use by another series: $($busy -join ', ')" }
    $problems
}

# ---- steps --------------------------------------------------------------------------------------------------------------
function Expand-Step($step, $copy) {
    $script = "$($step.Script)"
    $argList = @($step.Args | Where-Object { $null -ne $_ } | ForEach-Object { "$_" })
    if ($step.Par) {
        $script = "par.ps1"
        $argList = @("-Names", "$($step.Par)", "-GameDir", "{copy}", "-Jobs", "{jobs}") + $argList
    }
    if (-not $script) { throw "A step of the battery has neither Script nor Par: $($step | Out-String)" }
    $argList = @($argList | ForEach-Object { $_.Replace("{copy}", $copy.Dir).Replace("{jobs}", "$jobsPool").Replace("{version}", $copy.Version) })
    $first = if ($step.Par) { "$($step.Par)" } elseif ($argList -contains "-Names") { $argList[[array]::IndexOf($argList, "-Names") + 1] } elseif ($argList -contains "-Name") { $argList[[array]::IndexOf($argList, "-Name") + 1] } else { "" }
    $label = if ($step.Name) { "$($step.Name)" } else { ([IO.Path]::GetFileNameWithoutExtension($script) + $(if ($first) { ":" + $first } else { "" })) }
    $label = $label -replace "\s+", "_"
    if ($label.Length -gt 60) { $label = $label.Substring(0, 57) + "..." }
    $timeout = if ($step.TimeoutSec) { [int]$step.TimeoutSec } elseif ($bat.TimeoutSec) { [int]$bat.TimeoutSec } else { 7200 }
    [pscustomobject]@{ Label = $label; Script = $script; Args = $argList; Env = (@(Get-Pairs $bat.Env) + @(Get-Pairs $step.Env)); Timeout = $timeout; Copy = $copy }
}

$jobBody = {
    param($auto, $script, $argList, $copyDir, $envPairs)
    $env:STS2_GAME_DIR = $copyDir
    foreach ($p in $envPairs) { Set-Item -Path "Env:$($p.Name)" -Value $p.Value }
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $(if ([IO.Path]::IsPathRooted($script)) { $script } else { Join-Path $auto $script }) @argList 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = @($out) }
}

function Start-Step($s) {
    $job = Start-Job -ScriptBlock $jobBody -ArgumentList $auto, $s.Script, $s.Args, $s.Copy.Dir, $s.Env
    $s | Add-Member Job $job -Force
    $s | Add-Member Started (Get-Date) -Force
    $s
}

$script:failed = 0
$script:stepCount = 0
# A finished (or timed out) step: its output file, the STEP line, and for a failure the FAIL line with triage.py's first label.
function Complete-Step($s, [bool]$timedOut) {
    $exit, $lines = 1, @()
    if ($timedOut) {
        Stop-Job $s.Job; $lines = @("battery.ps1: step timed out after $($s.Timeout) s")
        Get-Process -Name "SlayTheSpire2*" -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($s.Copy.Dir, "OrdinalIgnoreCase") } | Stop-Process -Force -ErrorAction SilentlyContinue
    } else {
        $got = @(Receive-Job $s.Job -ErrorAction Continue 2>&1)
        $res = $got | Where-Object { $_ -is [pscustomobject] -and $null -ne $_.Exit } | Select-Object -Last 1
        if ($res) { $exit, $lines = [int]$res.Exit, @($res.Out) } else { $lines = @($got | ForEach-Object { "$_" }) }
    }
    Remove-Job $s.Job -Force
    [IO.File]::WriteAllLines((Join-Path $outDir "$($s.Label)-$($s.Copy.Name).out"), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
    $text = $lines -join "`n"
    # Problems: the runner's own count ("N problem(s)", "N with problems"), else its PROBLEM lines.
    $k = 0
    if ($text -match "(\d+) problem\(s\)") { $k = [int]$Matches[1] }
    elseif ($text -match "(\d+) with problems") { $k = [int]$Matches[1] }
    else { $k = @($lines | Where-Object { $_ -cmatch "^\s*PROBLEM|\bPROBLEMS\b|^problem" }).Count }
    if ($timedOut) { $exit = 124 }
    $script:stepCount++
    Say "STEP $($s.Label) $($s.Copy.Name) exit $exit problems $k"
    if ($exit -ne 0 -or $k -gt 0) {
        $script:failed++
        $since = $s.Started.AddMinutes(-1).ToString("HH:mm")
        $dir = Join-Path $Results $s.Copy.Version
        $first = ""
        try {
            $tri = @(& python -I (Join-Path $auto "triage.py") $dir --since $since --lines 8 2>&1 | ForEach-Object { "$_" })
            $first = ($tri | Where-Object { $_ -match "^(LOOK|ALARM)" } | Select-Object -First 1)
        } catch { $first = "triage.py failed: $($_.Exception.Message)" }
        if (-not $first) { $first = ($lines | Where-Object { $_ -match "PROBLEM|FAILED|TIMEOUT|DIFFERENT|timed out" } | Select-Object -First 1) }
        if ($first -and $first.Length -gt 200) { $first = $first.Substring(0, 200) }
        Say "FAIL $($s.Label) $($s.Copy.Name) exit $exit problems $k | $first"
    }
}

# Waits for the started steps; each completes as it ends, or at its timeout.
function Wait-Steps($running) {
    $pending = @($running)
    while ($pending.Count) {
        $still = @()
        foreach ($s in $pending) {
            if ($s.Job.State -ne "Running") { Complete-Step $s $false }
            elseif (((Get-Date) - $s.Started).TotalSeconds -gt $s.Timeout) { Complete-Step $s $true }
            else { $still += $s }
        }
        $pending = $still
        if ($pending.Count) { Start-Sleep 5 }
    }
}

# ---- run ----------------------------------------------------------------------------------------------------------------
$blocks = @($bat.Blocks | Where-Object { -not $Only -or "$($_.Name)" -eq $Only })
if (-not $blocks.Count) { Say "BATTERY ABORT no block named '$Only' in $File"; exit 2 }
if (-not $SkipChecks) {
    $problems = @(Test-Battery)
    if ($problems.Count) { foreach ($p in $problems) { Say "CHECK FAILED $p" }; Say "BATTERY ABORT $($problems.Count) check(s) failed (-SkipChecks to go on)"; exit 2 }
    Say "CHECKS ok: copies hold the $config build, no game running, test slots free"
}
$plan = @()
foreach ($b in $blocks) {
    $names = if ($b.Copies) { @($b.Copies) } else { @($copies | ForEach-Object { $_.Name }) }
    $bc = @($copies | Where-Object { $names -contains $_.Name })
    $mode = if ($b.Mode) { "$($b.Mode)" } else { "Exclusive" }
    if ($mode -notin "Parallel", "Exclusive", "Both") { Say "BATTERY ABORT block $($b.Name): unknown Mode $mode"; exit 2 }
    $plan += [pscustomobject]@{ Name = "$($b.Name)"; Mode = $mode; Copies = $bc; Steps = @($b.Steps) }
}
if ($DryRun) {
    foreach ($b in $plan) {
        Say "BLOCK $($b.Name) $($b.Mode) on $(($b.Copies | ForEach-Object { $_.Name }) -join ',')"
        foreach ($step in $b.Steps) { foreach ($c in $b.Copies) { $s = Expand-Step $step $c; Say "  $($s.Label) $($c.Name): $($s.Script) $($s.Args -join ' ')" } }
    }
    Say "BATTERY DONE $batName steps 0 failed 0 (dry run)"
    exit 0
}
$began = Get-Date
foreach ($b in $plan) {
    Say "BLOCK $($b.Name) $($b.Mode) $(Get-Date -Format HH:mm)"
    switch ($b.Mode) {
        "Parallel" {
            Wait-Steps @(foreach ($step in $b.Steps) { foreach ($c in $b.Copies) { Start-Step (Expand-Step $step $c) } })
        }
        "Both" {
            foreach ($step in $b.Steps) { Wait-Steps @(foreach ($c in $b.Copies) { Start-Step (Expand-Step $step $c) }) }
        }
        "Exclusive" {
            foreach ($c in $b.Copies) { foreach ($step in $b.Steps) { Wait-Steps @(Start-Step (Expand-Step $step $c)) } }
        }
    }
}
Say ("BATTERY DONE $batName steps $($script:stepCount) failed $($script:failed) in {0:F0} min" -f ((Get-Date) - $began).TotalMinutes)
exit $(if ($script:failed) { 1 } else { 0 })
