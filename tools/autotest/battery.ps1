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
#         folder, {jobs} the pool, {version} the copy's game version, {copies} the folders of every copy of the block, comma-joined.
#         STS2_GAME_DIR is the copy's folder for every step. Once = $true (Parallel and Both blocks): the step runs once for the
#         whole block, not once per copy (parsweep-all.ps1 -GameDir {copies}: the sweeps of both copies in one pool beside the solo
#         par lists), reported as copy "all". Closing = '<regex>': the step's closing line, for a script $ClosingLines doesn't know.
# Checks before the start (-SkipChecks leaves them out): each copy holds the DLLs of the build folder (hash), no game of a copy is
# running, no test slot is locked by another series. A failed check ends with "BATTERY ABORT <why>" and exit code 2.
# Output (stdout and the -Log file, default results\battery\<name>.log; the steps' full output next to it, one file per step):
#   STEP <name> <copy> exit N problems K        one per finished step
#   FAIL <name> <copy> exit N problems K | <first line of triage.py>      only for a failed step
# A step passes on exit code 0, no problem counted and its runner's closing line ($ClosingLines: the summary each runner prints as
# the last line of its work, which a runner cut short by an error never reaches; K is read from it when it counts problems). A
# step whose script has no known closing line stops the battery before it starts (BATTERY ABORT). A step past its timeout is
# stopped with every process it started (its process tree: the runner, its games, its child runners); games of its copy outside
# that tree (started through WMI: rendered ones) are closed too only when no other step is running.
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
function Expand-Step($step, $copy, $blockCopies) {
    $script = "$($step.Script)"
    $argList = @($step.Args | Where-Object { $null -ne $_ } | ForEach-Object { "$_" })
    if ($step.Par) {
        $script = "par.ps1"
        $argList = @("-Names", "$($step.Par)", "-GameDir", "{copy}", "-Jobs", "{jobs}") + $argList
    }
    if (-not $script) { throw "A step of the battery has neither Script nor Par: $($step | Out-String)" }
    $argList = @($argList | ForEach-Object { $_.Replace("{copies}", (@($blockCopies | ForEach-Object { $_.Dir }) -join ",")).Replace("{copy}", $copy.Dir).Replace("{jobs}", "$jobsPool").Replace("{version}", $copy.Version) })
    $first = if ($step.Par) { "$($step.Par)" } elseif ($argList -contains "-Names") { $argList[[array]::IndexOf($argList, "-Names") + 1] } elseif ($argList -contains "-Name") { $argList[[array]::IndexOf($argList, "-Name") + 1] } else { "" }
    $label = if ($step.Name) { "$($step.Name)" } else { ([IO.Path]::GetFileNameWithoutExtension($script) + $(if ($first) { ":" + $first } else { "" })) }
    $label = $label -replace "\s+", "_"
    if ($label.Length -gt 60) { $label = $label.Substring(0, 57) + "..." }
    $timeout = if ($step.TimeoutSec) { [int]$step.TimeoutSec } elseif ($bat.TimeoutSec) { [int]$bat.TimeoutSec } else { 7200 }
    [pscustomobject]@{ Label = $label; Script = $script; Args = $argList; Env = (@(Get-Pairs $bat.Env) + @(Get-Pairs $step.Env)); Timeout = $timeout; Copy = $copy; Closing = $(if ($step.Closing) { "$($step.Closing)" } else { "" }) }
}

$jobBody = {
    param($auto, $script, $argList, $copyDir, $envPairs, $pidFile)
    # This job's process: the root of every process the step starts (Stop-StepTree at its timeout).
    [IO.File]::WriteAllText($pidFile, "$PID")
    $env:STS2_GAME_DIR = $copyDir
    foreach ($p in $envPairs) { Set-Item -Path "Env:$($p.Name)" -Value $p.Value }
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $(if ([IO.Path]::IsPathRooted($script)) { $script } else { Join-Path $auto $script }) @argList 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = @($out) }
}

# Each runner's closing line: the summary it prints as the last line of its work (a runner cut short by an error, or one that
# reached its exit code with its work half done, has none); (?<k>...) its problem count when it gives one.
$ClosingLines = @{
    'par.ps1'                = '^done in \d+ s, (?<k>\d+) problem\(s\), logs in '
    'vanillacheck.ps1'       = '^vanillacheck done in \d+ s, (?<k>\d+) problem\(s\), logs in '
    'mutations.ps1'          = '^\d+/\d+ as expected in [\d.,]+ min'
    'parcoop.ps1'            = '^done in [\d.,]+ min, (?<k>\d+) with problems, logs in '
    'parsweep.ps1'           = '^\w+( at \d+ imports)?: \d+ sessions in [\d.,]+ min'
    'parsweep-all.ps1'       = '^\d+ kind\(s\) in [\d.,]+ min, (?<k>\d+) with a problem; outputs in '
    'coop4.ps1'              = '^\S+: \d+ peers, '
    'run.ps1'                = '^done in \d+ s: '
    'coop.ps1'               = '^done in \d+ s: '
    'coop-modetag.ps1'       = '^coop-modetag done: (?<k>\d+) problem\(s\)'
    'settings.ps1'           = '^settings\.ps1 done: \d+ session log check\(s\), (?<k>\d+) problem\(s\)'
    'baselib-optional.ps1'   = '^baselib-optional\.ps1 done: \d+ session log check\(s\), (?<k>\d+) problem\(s\)'
    'limitless-switches.ps1' = '^limitless-switches\.ps1 done: \d+ session log check\(s\), (?<k>\d+) problem\(s\)'
    'started-off.ps1'        = '^started-off\.ps1 done: \d+ session log check\(s\), (?<k>\d+) problem\(s\)'
    'started-off-nomenu.ps1' = '^started-off-nomenu\.ps1 done: \d+ session log check\(s\), (?<k>\d+) problem\(s\)'
    'continue-check.ps1'     = '^continue-check\.ps1 done: \d+ session log check\(s\), (?<k>\d+) problem\(s\)'
}
# A step's closing-line pattern: its own Closing, else its script's in $ClosingLines, else nothing (Test-Closings stops the battery).
function Get-ClosingPattern($s) {
    if ($s.Closing) { return $s.Closing }
    $ClosingLines[[IO.Path]::GetFileName($s.Script)]
}

# Every process descending from $rootId (the job's own process first), parents before children; a child counts only when created
# after its parent (a reused process id is not a descendant).
function Get-ProcessTree([int]$rootId) {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name, ExecutablePath -ErrorAction SilentlyContinue)
    $byParent = @{}
    foreach ($p in $all) { $key = [int]$p.ParentProcessId; if (-not $byParent.ContainsKey($key)) { $byParent[$key] = [Collections.Generic.List[object]]::new() }; $byParent[$key].Add($p) }
    $root = @($all | Where-Object { $_.ProcessId -eq $rootId })
    if (-not $root.Count) { return @() }
    $tree = [Collections.Generic.List[object]]::new()
    $queue = [Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($root[0]); $tree.Add($root[0])
    while ($queue.Count) {
        $parent = $queue.Dequeue()
        if (-not $byParent.ContainsKey([int]$parent.ProcessId)) { continue }
        foreach ($child in $byParent[[int]$parent.ProcessId]) {
            if ($child.ProcessId -eq $parent.ProcessId -or ($child.CreationDate -and $parent.CreationDate -and $child.CreationDate -lt $parent.CreationDate)) { continue }
            $tree.Add($child); $queue.Enqueue($child)
        }
    }
    $tree
}

# A timed-out step's processes: its whole tree, the job's own process last (Stop-Job would leave its grandchildren running: the
# runner and its games), twice (a runner may start a game between the listing and its end). Returns the games it closed.
function Stop-StepTree($s) {
    $closed = 0
    $pidText = if (Test-Path $s.PidFile) { [IO.File]::ReadAllText($s.PidFile).Trim() } else { "" }
    if ($pidText -match '^\d+$') {
        foreach ($pass in 1, 2) {
            $tree = @(Get-ProcessTree ([int]$pidText))
            [array]::Reverse($tree)
            foreach ($p in $tree) {
                if ($p.Name -like "SlayTheSpire2*") { $closed++ }
                Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            }
            Start-Sleep 1
        }
    }
    $closed
}

function Start-Step($s) {
    $pidFile = Join-Path $outDir "$($s.Label)-$($s.Copy.Name).pid"
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    $s | Add-Member PidFile $pidFile -Force
    $job = Start-Job -ScriptBlock $jobBody -ArgumentList $auto, $s.Script, $s.Args, $s.Copy.Dir, $s.Env, $pidFile
    $s | Add-Member Job $job -Force
    $s | Add-Member Started (Get-Date) -Force
    $s
}

$script:failed = 0
$script:stepCount = 0
# A finished (or timed out) step: its output file, the STEP line, and for a failure the FAIL line with triage.py's first label.
function Complete-Step($s, [bool]$timedOut, [int]$othersRunning = 0) {
    $exit, $lines = 1, @()
    if ($timedOut) {
        # Its own processes only: another step running on the same copy keeps its games.
        $closed = Stop-StepTree $s
        Stop-Job $s.Job
        $lines = @("battery.ps1: step timed out after $($s.Timeout) s: its process tree stopped ($closed game(s))")
        $dirs = if ($s.Copy.Dirs) { @($s.Copy.Dirs) } else { @($s.Copy.Dir) }
        $left = @(Get-Process -Name "SlayTheSpire2*" -ErrorAction SilentlyContinue | Where-Object { $p = $_.Path; $p -and @($dirs | Where-Object { $p.StartsWith($_, "OrdinalIgnoreCase") }).Count })
        # Games of its copy outside the tree (started through WMI, rendered): its own when nothing else runs, closed; else said.
        if ($left.Count -and $othersRunning -eq 0) { $left | Stop-Process -Force -ErrorAction SilentlyContinue; $lines += "battery.ps1: $($left.Count) other game(s) of the copy closed (no other step running)" }
        elseif ($left.Count) { $lines += "battery.ps1: $($left.Count) game(s) of the copy outside the step's process tree left running ($othersRunning other step(s) running): close them if they are this step's (pid $(@($left | ForEach-Object { $_.Id }) -join ', '))" }
    } else {
        $got = @(Receive-Job $s.Job -ErrorAction Continue 2>&1)
        $res = $got | Where-Object { $_ -is [pscustomobject] -and $null -ne $_.Exit } | Select-Object -Last 1
        if ($res) { $exit, $lines = [int]$res.Exit, @($res.Out) } else { $lines = @($got | ForEach-Object { "$_" }) }
    }
    Remove-Job $s.Job -Force
    Remove-Item $s.PidFile -Force -ErrorAction SilentlyContinue
    # Its closing line (Get-ClosingPattern): the runner reached the end of its work.
    $closingPattern = Get-ClosingPattern $s
    $closingLine = @($lines | Where-Object { $_ -match $closingPattern }) | Select-Object -Last 1
    if (-not $closingLine) { $lines += "battery.ps1: no closing line matching '$closingPattern': the runner did not reach the end of its work" }
    [IO.File]::WriteAllLines((Join-Path $outDir "$($s.Label)-$($s.Copy.Name).out"), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
    $text = $lines -join "`n"
    # Problems: the closing line's count when it gives one, else the runner's own count ("N problem(s)", "N with problems"), else
    # its PROBLEM lines.
    $k = 0
    if ($closingLine -and $closingLine -match $closingPattern -and $Matches['k']) { $k = [int]$Matches['k'] }
    elseif ($text -match "(\d+) problem\(s\)") { $k = [int]$Matches[1] }
    elseif ($text -match "(\d+) with problems") { $k = [int]$Matches[1] }
    else { $k = @($lines | Where-Object { $_ -cmatch "^\s*PROBLEM|\bPROBLEMS\b|^problem" }).Count }
    if ($timedOut) { $exit = 124 }
    $script:stepCount++
    Say "STEP $($s.Label) $($s.Copy.Name) exit $exit problems $k$(if (-not $closingLine) { ' no-closing-line' })"
    if ($exit -ne 0 -or $k -gt 0 -or -not $closingLine) {
        $script:failed++
        $since = $s.Started.AddMinutes(-1).ToString("HH:mm")
        $dirs = @(@(if ($s.Copy.Versions) { $s.Copy.Versions } else { $s.Copy.Version }) | ForEach-Object { Join-Path $Results $_ })
        $first = ""
        try {
            $tri = @(& python -I (Join-Path $auto "triage.py") @dirs --since $since --lines 8 2>&1 | ForEach-Object { "$_" })
            $first = ($tri | Where-Object { $_ -match "^(LOOK|ALARM)" } | Select-Object -First 1)
        } catch { $first = "triage.py failed: $($_.Exception.Message)" }
        if (-not $first) { $first = ($lines | Where-Object { $_ -match "RUNNER ERROR|PROBLEM|FAILED|TIMEOUT|DIFFERENT|timed out|no closing line" } | Select-Object -First 1) }
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
            elseif (((Get-Date) - $s.Started).TotalSeconds -gt $s.Timeout) { Complete-Step $s $true @($pending | Where-Object { $_ -ne $s -and $_.Job.State -eq "Running" }).Count }
            else { $still += $s }
        }
        $pending = $still
        if ($pending.Count) { Start-Sleep 5 }
    }
}

# The copies a step runs on: one pseudo copy "all" (the first copy's folder and version, every folder in Dirs) for a Once step.
function Get-Instances($step, $bc) {
    if ($step.Once) { return @([pscustomobject]@{ Name = "all"; Dir = $bc[0].Dir; Version = $bc[0].Version; Dirs = @($bc | ForEach-Object { $_.Dir }); Versions = @($bc | ForEach-Object { $_.Version }) }) }
    $bc
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
# Every step's closing line is known before anything starts: a step without one could never pass.
$unknownClosing = @(foreach ($b in $plan) { foreach ($step in $b.Steps) { $s = Expand-Step $step $b.Copies[0] $b.Copies; if (-not (Get-ClosingPattern $s)) { "$($b.Name)/$($s.Label) ($($s.Script))" } } })
if ($unknownClosing.Count) { Say "BATTERY ABORT no closing line known for $($unknownClosing -join ', '): add the script's to battery.ps1's `$ClosingLines or give the step Closing = '<regex>'"; exit 2 }
if ($DryRun) {
    foreach ($b in $plan) {
        Say "BLOCK $($b.Name) $($b.Mode) on $(($b.Copies | ForEach-Object { $_.Name }) -join ',')"
        foreach ($step in $b.Steps) { foreach ($c in (Get-Instances $step $b.Copies)) { $s = Expand-Step $step $c $b.Copies; Say "  $($s.Label) $($c.Name): $($s.Script) $($s.Args -join ' ')" } }
    }
    Say "BATTERY DONE $batName steps 0 failed 0 (dry run)"
    exit 0
}
$began = Get-Date
foreach ($b in $plan) {
    Say "BLOCK $($b.Name) $($b.Mode) $(Get-Date -Format HH:mm)"
    switch ($b.Mode) {
        "Parallel" {
            Wait-Steps @(foreach ($step in $b.Steps) { foreach ($c in (Get-Instances $step $b.Copies)) { Start-Step (Expand-Step $step $c $b.Copies) } })
        }
        "Both" {
            foreach ($step in $b.Steps) { Wait-Steps @(foreach ($c in (Get-Instances $step $b.Copies)) { Start-Step (Expand-Step $step $c $b.Copies) }) }
        }
        "Exclusive" {
            foreach ($c in $b.Copies) { foreach ($step in $b.Steps) { Wait-Steps @(Start-Step (Expand-Step $step $c $b.Copies)) } }
        }
    }
}
Say ("BATTERY DONE $batName steps $($script:stepCount) failed $($script:failed) in {0:F0} min" -f ((Get-Date) - $began).TotalMinutes)
exit $(if ($script:failed) { 1 } else { 0 })
