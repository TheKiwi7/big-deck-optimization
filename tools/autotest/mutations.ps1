# How the mod reacts when the game or a mod changes: for each mutation of tools/mutate (renumbered generated code, a new
# hook, a changed mirrored method, a changed AbstractModel default or helper, a renamed method or field, a member made
# internal, a changed signature, changed Limitless / BaseLib code), writes the modified assembly into a game COPY, runs
# a scenario there (default effects-check: every optimization's effect counter, its bdperf comparisons, a fixed seed) and checks that
#   - the session ran to its end with no error from this mod outside what the mutation explains (a part left vanilla at load;
#     a part faulting at run time, "disabled after exception", "turned off and removed", is never explained by the mutation),
#   - exactly the parts relying on the changed code were left vanilla (every "left vanilla" line matches the
#     mutation's pattern; none for mutations that change nothing the mod relies on; the reference's own lines, pinned by
#     $ReferenceBackOffs, don't count),
#   - every bdperf comparison still says identical,
#   - the optimizations not relying on the changed code still work: against a session on the unmodified game, only the
#     effects (AutoTest's "effects" counters) the mutation may take away dropped to zero,
#   - the game did the same: CHECKSUM, STATE and CARDPLAY lines identical to that session, whatever is left vanilla.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/mutations.ps1 [-GameDir C:\Games\StS2-public-0.111.1] [-Only renumber-hooks,energy-hook] [-Scenario effects-check] [-AllowUndefined]
# tools/mutate is built first: a build that fails stops the run (an old mutate.dll would run old mutations). A mutation of the
# table that tools/mutate doesn't define is a failure, unless -AllowUndefined (a name in -Only that isn't in the table stops the run).
# One run at a time per copy (its thin copies, bdmut-copies-<copy> beside it, and its results/<version>/mutation-*.log): the two
# supported copies' runs go side by side, their sessions sharing the solo test slots (parlib.ps1's $SlotPool).
# Each mutation runs in parallel in its own folder of hard links to the copy (no disk used) holding only its modified file,
# headless and without Steam, so the copy itself is never modified and stopping this script leaves nothing behind.
# Debug build deployed in the copy first.
# -DryRun: each mutation's files (the assembly mutate reads, the one it writes in the mutation's thin copy, its build output, its
# session log) and the reference's log, checked (Test-MutationPlan) and printed, then the end: nothing built, copied or launched.
# The run prints one '== <mutation> : PASS|FAIL' line per mutation, then its closing line '<n>/<m> as expected in <t> min'.
param([string]$GameDir = "C:\Games\StS2-public-0.111.1", [string[]]$Only = @(), [string]$Scenario = "effects-check", [int]$TimeoutSec = 600, [int]$Jobs = 0, [switch]$AllowUndefined, [switch]$DryRun)
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
# An error nothing catches ends this script with exit code 1 (common.ps1's Write-RunnerError), never with its work half done.
trap { Write-RunnerError $_; exit 1 }
if ($Jobs -le 0) { $Jobs = $SlotPool }
if ($copy -like "*steamapps*") { throw "Use a game copy, not the Steam install" }
$GameDir = Resolve-GameCopy $copy
$out = Get-VersionDir $GameDir
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-Only a,b" through powershell -File arrives as one string
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Data = Join-Path $GameDir "data_sts2_windows_x86_64"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"; $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# What leaves when the whole pile tracking does: the listener cache watches the same methods, hook lists are built inside it.
$PileWriters = "ListenerCache(\.(Creatures|Suspended))?|HookFilter|PileIndex(\.Duplicates)?|CardDbScan|DeckCountMemo(\.Checks)?|FastListeners\.(Cache|Moves)|LimitlessRecalcSkip\.Passes|SnapshotCardReuse\.(Trust|Moved|Dirty|Hooks|Piles|PileBits|Blocks)"
# Target, pattern every "left vanilla" line must match ("" = nothing may be left vanilla), effects allowed to drop to zero ("" = none).
$Expected = [ordered]@{
    "renumber-hooks"  = @("game", "", "")
    "new-hook"        = @("game", "", "")
    "unrelated-body"  = @("game", "", "")
    "energy-hook"     = @("game", "ModifyEnergyCostInCombat", "CardSortKeys(\.Answers)?")
    "default-hitting" = @("game", "ShouldAllowHitting", "")
    "default-task"    = @("game", "AfterCardPlayed", "")
    "helper-push"     = @("game", "choice context helpers changed|PushModel", "")
    "pile-add"        = @("game", "CardPile.AddInternal changed", "FastListeners\.Moves|LimitlessHookSkip\.Saved|DeckCountMemo(\.Checks)?")
    "pile-remove"     = @("game", "CardPile.RemoveInternal changed", "FastListeners\.Moves|LimitlessHookSkip\.Saved|DeckCountMemo(\.Checks)?")
    "pile-writer"     = @("game", "_cards", $PileWriters)
    "sync-body"       = @("game", "SyncedCardRelease|SyncWithSerializedPlayer", "")
    "allcards-reader" = @("game", "SyncedCardRelease|_allCards", "")
    "squeeze-count"   = @("game", "DeckCountMemo|Squeeze", "DeckCountMemo(\.Checks)?")
    "button-input"    = @("game", "IdleButtonInput|_Input", "IdleButtonInput")
    "wait-body"       = @("game", "FastCombatStart|Wait", "")
    "renumber-cards"  = @("game", "", "")
    "hook-rename"     = @("game", "HookFilter|ShouldAllowHitting", "")
    "setup-turn"      = @("game", "ExtraScopes|first-turn|SetupPlayerTurn", "ExtraScopes")
    "relic-holder"    = @("game", "RelicFlashMerge|OnRelicFlashed", "")
    "rename-method"   = @("game", "RemoveInternal|AutoTest", $PileWriters)
    # CombatState's getters reading _allies are in HookSkip's per-hook helper sets (fingerprinted): those hooks' helpers go vanilla;
    # in CardFlightLimit's computed set too (not reviewed: no soul skipped).
    "rename-field"    = @("game", "combat walk left vanilla|choice context helpers changed", "FastListeners\.CombatWalk|CardFlightLimit")
    # The walk's iterator is also in CardFlightLimit's computed set (CombatManager.IsEnding walks the listeners): not reviewed, no soul skipped.
    "combat-walk"     = @("game", "combat walk left vanilla", "FastListeners\.CombatWalk|CardFlightLimit")
    "run-walk"        = @("game", "run walk left vanilla", "FastListeners\.RunWalk")
    "internal-member" = @("game", "HookFilter|GetWithModifiers", "SnapshotCardReuse\.(Release|Piles|Trust|Moved|Dirty|Hooks|Blocks|CardBits|UpgradeBits|PileBits)")
    "signature"       = @("game", "HookFilter|ShouldPlay", "")
    "flight-count"    = @("game", "CardFlightLimit|AddCard", "CardFlightLimit")
    "replay-record"   = @("game", "ReplayStateSkip|RecordChecksum", "ReplayStateSkip")
    "theme-rename"    = @("game", "ThemeColorSkip|RefreshText", "")
    "upgrade-writer"  = @("game", "_currentUpgradeLevel", "SnapshotCardReuse\.(Trust|Moved|Dirty|Hooks)|LimitlessRecalcSkip\.Passes")
    "write-list"      = @("game", "pile bits not reused", "SnapshotCardReuse\.(PileBits|Blocks)")
    "local-keywords"  = @("game", "read through the game's own calls|get_LocalKeywords", "SnapshotCardReuse\.(Trust|Moved|Dirty|Hooks)")
    "limitless-body"  = @("Limitless", "LimitlessReserialize|LimitlessRecalcSkip|SetSerializableCards", "LimitlessReserialize|LimitlessRecalcSkip\.(Memos|Passes)|SnapshotCardReuse\.Moved|LimitlessCloneMemo|LimitlessHookSkip(\.Saved)?")
    # A changed forwarder: one of the known writers of the inner list (2026-11-15), so HookSkip and the pass skipping go vanilla whole.
    "limitless-forward" = @("Limitless", "LimitlessHookSkip|AfterCardPlayed|inner list|passes not skipped", "LimitlessRecalcSkip\.Passes|LimitlessHookSkip(\.Saved)?")
    "limitless-writer"  = @("Limitless", "unchanged piles|SerializableEnchantmentsOnCards|inner list may also be changed", "SnapshotCardReuse\.(Trust|Moved|Dirty|Hooks)|LimitlessHookSkip(\.Saved)?|LimitlessRecalcSkip\.Passes")
    "limitless-recalc"  = @("Limitless", "LimitlessRecalcSkip|RecalculateValues|not skipped|inner list", "LimitlessRecalcSkip\.(Memos|Passes)|LimitlessHookSkip(\.Saved)?")
    "limitless-clone" = @("Limitless", "LimitlessCloneMemo|DeepCloneFields", "LimitlessCloneMemo|LimitlessRecalcSkip\.Passes|LimitlessHookSkip(\.Saved)?")
    "limitless-tabs"  = @("Limitless", "EnchantmentTabReuse|UpdateEnchantmentVisuals", "EnchantmentTabReuse")
    "baselib-plaque"  = @("BaseLib", "TypePlaqueSkip|TryModifyPlaqueText", "TypePlaqueSkip")
    # LimitlessCloneMemo checks the game's clone and build chain (GameChain): a changed CardModel.FromSerializable leaves it vanilla,
    # and LimitlessHookSkip, which needs it, inactive.
    "clone-from"      = @("game", "LimitlessCloneMemo|LimitlessHookSkip|clone|FromSerializable", "LimitlessCloneMemo|LimitlessHookSkip(\.Saved)?")
    # LimitlessHookSkip relies on what the getter hands out (the inner list only known code changes).
    "limitless-getter" = @("Limitless", "LimitlessHookSkip|LimitlessRecalcSkip|EnchantmentsOnCards", "LimitlessHookSkip(\.Saved)?|LimitlessRecalcSkip(\.Memos|\.Passes)?")
    # Coverage hardening (2026-11-17): an ExecutionFinished accessor changed, a hand selection or orb queue method changed.
    # Calibrated on the proof run of 2026-11-17 (v0.107.1): AfterCloned is also the clone chain LimitlessCloneMemo pins (it skips,
    # LimitlessHookSkip's saved lists with it, as clone-from); orb-enqueue stops ListenerCache's whole Apply, and HookFilter serves
    # its lists only inside ListenerCache's scopes, SnapshotCardReuse's kept piles only with HookFilter active (ReadsFor).
    "execfinished-add"   = @("game", "ExecutionFinished", "")
    "execfinished-clone" = @("game", "ExecutionFinished|AfterCloned", "LimitlessCloneMemo|LimitlessHookSkip(\.Saved)?")
    "hand-revalidate"    = @("game", "RevalidateSelectionAfterStateChange|dispatch scope", "ListenerCache\.Suspended")
    "hand-select"        = @("game", "SelectCards|dispatch scope", "ListenerCache\.Suspended")
    "orb-enqueue"        = @("game", "ListenerCache|TryEnqueue", "ListenerCache(\.\w+)?|HookFilter|SnapshotCardReuse\.(Trust|Moved|Dirty|Hooks)")
}
# What the unmodified game itself leaves vanilla next to the test mods (the reference's own "left vanilla" lines): each must
# match this pattern (a new one is a reference problem), and a mutation's lines that the reference has too are not its doing.
# LimitlessCloneMemo: SoulsPower's closure reaches NCard.UpdateVisuals, which BetterSpire2 and Limitless patch: that content stays
# vanilla (owner's decision of 2026-11-19, DESIGN "Owner's decisions (2026-11-19)").
$ReferenceBackOffs = "LimitlessCloneMemo: NCard\.UpdateVisuals is also patched by BetterSpire2, Limitless: normal game for that part"
$unknown = @($Only | Where-Object { -not $Expected.Contains($_) })
if ($unknown.Count) { throw "-Only: no such mutation in the table: $($unknown -join ', ')" }
$thinRoot = Join-Path (Split-Path $GameDir) "bdmut-copies-$(Split-Path $GameDir -Leaf)"
$Reference = Join-Path $out "mutation-reference.log"

# Where a mutation's files go, for the run and -DryRun alike: its thin copy (Folder), the assembly mutate reads (In, the copy's)
# and the one it writes in the thin copy (Dll), the folders mutate resolves references in (Search), its build output and its
# session log (BuildOut, Log: results/<version>/, $out). $out stays the version folder: a loop variable of that name once made
# every path after the first build point inside a mutated assembly's path (no log ever written, 2026-11-18 to 11-20).
function Get-MutationPlan([string]$name) {
    $folder = Join-Path $thinRoot $name
    $target = $Expected[$name][0]
    if ($target -eq "game") {
        $in = Join-Path $Data "sts2.dll"
        $dll = Join-Path $folder "data_sts2_windows_x86_64\sts2.dll"
        $search = @("`"$Data`"")
    }
    else {
        $source = Get-ChildItem (Join-Path $GameDir "mods_STEAMTEST") -Recurse -Filter "$target.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $source) { throw "mutation $name`: no $target.dll under $(Join-Path $GameDir 'mods_STEAMTEST') (the test mods not added?)" }
        $in = $source.FullName
        $dll = Join-Path $folder $source.FullName.Substring($GameDir.TrimEnd('\').Length + 1)
        $search = @("`"$Data`"", "`"$($source.DirectoryName)`"")
    }
    [pscustomobject]@{ Name = $name; Folder = $folder; In = $in; Dll = $dll; Search = $search; BuildOut = Join-Path $out "mutation-$name.build.txt"; Log = Join-Path $out "mutation-$name.log" }
}
# The plan's paths where they belong, or a throw: the assembly read exists, the one written lies inside the mutation's own thin
# copy (never the copy itself) and is that file's twin there, the build output and the log are files of the version folder.
function Test-MutationPlan($plan) {
    $problems = @()
    if (-not (Test-Path $plan.In -PathType Leaf)) { $problems += "In $($plan.In) is not a file" }
    if (-not $plan.Dll.StartsWith($plan.Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $problems += "Dll $($plan.Dll) is not inside its thin copy $($plan.Folder)" }
    elseif ($plan.Dll.Substring($plan.Folder.TrimEnd('\').Length) -ne $plan.In.Substring($GameDir.TrimEnd('\').Length)) { $problems += "Dll $($plan.Dll) is not In's place in the thin copy" }
    foreach ($file in $plan.BuildOut, $plan.Log) {
        if ((Split-Path $file) -ne $out) { $problems += "$file is not in the version folder $out" }
    }
    if ($problems.Count) { throw "mutation $($plan.Name): $($problems -join '; ')" }
}

if ($DryRun) {
    Write-Output "dry run on $GameDir ($(Get-GameTag $GameDir)): thin copies in $thinRoot, logs in $out"
    $names = @($Expected.Keys | Where-Object { $Only.Count -eq 0 -or $Only -contains $_ })
    foreach ($name in $names) {
        $target = $Expected[$name][0]
        if ($target -ne "game" -and -not (Get-ChildItem (Join-Path $GameDir "mods_STEAMTEST") -Recurse -Filter "$target.dll" -ErrorAction SilentlyContinue)) {
            Write-Output "== $name ($target): not planned, no $target.dll in this copy's mods_STEAMTEST (the run adds the test mods first)"
            continue
        }
        $plan = Get-MutationPlan $name
        Test-MutationPlan $plan
        Write-Output "== $name ($($Expected[$name][0])): reads $($plan.In)"
        Write-Output "     writes $($plan.Dll)"
        Write-Output "     build output $($plan.BuildOut), log $($plan.Log)"
    }
    Write-Output "reference: log $Reference, thin copy $(Join-Path $thinRoot 'reference')"
    Write-Output "dry run: $($names.Count) mutation(s) planned, every path checked; nothing built, copied or launched"
    exit 0
}
# One run at a time per copy: two would delete each other's thin copies and write the same results/<version>/mutation-<name>.log.
$sha = [Security.Cryptography.SHA256]::Create()
$copyHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((Get-CopyKey $GameDir)))) -replace "-", "").Substring(0, 16)
$runLock = [Threading.Mutex]::new($false, "Local\BigDeckOptimization-mutations-$copyHash")
try { if (-not $runLock.WaitOne(0)) { throw "another mutations.ps1 is running on $GameDir`: wait for it to end" } }
catch [Threading.AbandonedMutexException] { }
# tools/mutate is built by one run at a time (the other copy's run may be building it): the second waits, then finds it built.
$buildLock = [Threading.Mutex]::new($false, "Local\BigDeckOptimization-mutate-build")
try { if (-not $buildLock.WaitOne(600000)) { throw "tools/mutate: another mutations.ps1 kept building it for 10 minutes" } }
catch [Threading.AbandonedMutexException] { }
try {
    $buildOutput = @(dotnet build (Join-Path $Root "tools\mutate") -c Release -v q 2>&1)
    if ($LASTEXITCODE -ne 0) { $buildOutput | Select-Object -Last 20 | ForEach-Object { Write-Output "  $_" }; throw "tools/mutate did not build: its old mutate.dll would run old mutations" }
    $mutate = Join-Path $Root "tools\mutate\bin\Release\net9.0\mutate.dll"
    # A mutation tools/mutate doesn't define (yet) is said, not run: mutate would fail on it and stop every other. It counts as a
    # failure, unless -AllowUndefined.
    $defined = @(dotnet $mutate list | ForEach-Object { ($_ -split '\s+')[0] } | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0 -or $defined.Count -eq 0) { throw "tools/mutate lists no mutation (exit code $LASTEXITCODE)" }
}
finally { $buildLock.ReleaseMutex(); $buildLock.Dispose() }
$undefined = @()
foreach ($name in @($Expected.Keys)) { if ($defined -notcontains $name -and ($Only.Count -eq 0 -or $Only -contains $name)) { Write-Output "NOT DEFINED in tools/mutate: $name (not run)"; $undefined += $name; $Expected.Remove($name) } }
$started = Get-Date
Add-TestMods $GameDir
Remove-Item $thinRoot -Recurse -Force -ErrorAction SilentlyContinue
$scriptPath = Join-Path $Scenarios "$Scenario.txt"
# The copy must come out as it went in: hard links share their content with it.
$before = Get-ChildItem $GameDir -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\mods_STEAMTEST\\' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
try {
    # What each mutation changes, built side by side: most of the time here is reading and writing the game's assembly.
    $names = @($Expected.Keys | Where-Object { $Only.Count -eq 0 -or $Only -contains $_ })
    $building = foreach ($name in $names) {
        $plan = Get-MutationPlan $name
        Test-MutationPlan $plan
        New-ThinCopy $GameDir $plan.Folder
        # The hard link goes first: mutate writes a new file there, never through the link into the copy's own.
        Remove-Item $plan.Dll -Force
        $process = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $plan.BuildOut -ArgumentList (@("`"$mutate`"", "`"$($plan.In)`"", "`"$($plan.Dll)`"", $name) + $plan.Search)
        [void]$process.Handle
        [pscustomobject]@{ Plan = $plan; Process = $process }
    }
    foreach ($b in $building) {
        $b.Process.WaitForExit()
        if ($b.Process.ExitCode -ne 0 -or -not (Test-Path $b.Plan.Dll)) { throw "mutate failed for $($b.Plan.Name) (exit code $($b.Process.ExitCode)), see $($b.Plan.BuildOut)" }
    }
    Write-Output ("{0} mutations built in {1:F0} s" -f $names.Count, ((Get-Date) - $started).TotalSeconds)
    New-ThinCopy $GameDir (Join-Path $thinRoot "reference")
    # The reference: every effect must have fired on the unmodified game, or the check below proves nothing.
    $sessions = @([pscustomobject]@{ Name = "reference"; Script = $scriptPath; Log = $Reference; GameDir = Join-Path $thinRoot "reference" })
    # Mutated: judge.py --mutated (parlib.ps1's Test-MutatedJob): a part the mutation leaves vanilla compares nothing and counts no
    # effect there, which this script checks against its table, not judge.py.
    $sessions += foreach ($b in $building) { [pscustomobject]@{ Name = $b.Plan.Name; Script = $scriptPath; Log = $b.Plan.Log; GameDir = $b.Plan.Folder; Mutated = $true } }
    $runs = Invoke-Sessions -Jobs $sessions -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -Label "mutations"
    # A session without its result never ran: its mutation (or the reference) is not as expected.
    $notRun = @(Get-MissingResults $sessions $runs)
    $notRun | ForEach-Object { Write-Output $_ }
    # Co-op only: effects-check, solo, can't make SyncedCardRelease run, nor ReplayStateSkip on v0.107.x (the game checksums solo
    # combats from v0.111 on: there it must run). SnapshotCardReuse.Hooks must run (effects-check applies Curious): a mutation losing
    # it is seen. (judge.py checks the same from effects-check's '# effects-ran' lines: the reference's problems below.)
    # HookSkip.NoBadge and HookSkip.Emptied count the badge guard's fallbacks: 0 in the game's runs since the exact model stack
    # (their positive control is modelstack-badges), as effects-check's own 'effectcheck all ran except' says.
    $allowZero = if ((Get-GameTag $GameDir) -match '^v0\.107\.') { "^(ReplayStateSkip|SyncedCardRelease|HookSkip\.NoBadge|HookSkip\.Emptied)$" } else { "^(SyncedCardRelease|HookSkip\.NoBadge|HookSkip\.Emptied)$" }
    python -I (Join-Path $PSScriptRoot "effects.py") $Reference --allow-zero $allowZero | Out-Host
    # The reference: every effect fired and nothing left unbalanced on the unmodified game, and its comparisons said identical,
    # or the checks below (lost effects, comparisons) prove nothing for what it lacks.
    $referenceOk = $LASTEXITCODE -eq 0
    # bdperf comparisons only (ExtraScopes' first-turn line is the scope's own check, gone when ExtraScopes is the part a mutation leaves vanilla).
    # Each comparison prints twice: a one-line log line ('[INFO] [BigDeckOptimization] bdperf ...') and the console echo, whose first
    # line is '[INFO] ... AutoTest <t>s   ok: ...' and whose other lines carry no prefix; a part not applied prints its "nothing to
    # compare" line once, in the echo. Count the echo alone ($EchoLine), so each comparison counts once either way.
    $EchoLine = { param($l) $l -match 'AutoTest\s+[\d.]+s\s+ok: ' -or $l -notmatch '^\[' }
    $IdenticalLine = { param($l) $l -match "OK: identical" -and $l -notmatch "first-turn filter" -and (& $EchoLine $l) }
    $referenceIdentical = @(Get-Content $Reference | Where-Object { & $IdenticalLine $_ }).Count
    $referenceProblems = @(Invoke-Judge @($Reference)) + @(($runs | Where-Object { $_.Name -eq "reference" }).Failed | Where-Object { $_ -match '^(ended early|froze|test mod )' })
    if (-not $referenceOk) { Write-Output "REFERENCE: an effect never ran (or stayed open) on the unmodified game: what a mutation loses there can't be seen" }
    if ($referenceIdentical -eq 0) { Write-Output "REFERENCE: no bdperf comparison said 'OK: identical' on the unmodified game" }
    # The reference's own "left vanilla" lines: each pinned by $ReferenceBackOffs, then not held against any mutation.
    $referenceListed = @(python -I (Join-Path $Root "tools\check_versions.py") --local --log $Reference)
    if ($LASTEXITCODE -ge 2) { $referenceProblems += "check_versions.py --log failed on the reference: $(@($referenceListed | Select-Object -Last 1))" }
    $referenceVanilla = @($referenceListed | Where-Object { $_ -match "^\s+\[" -and $_ -notmatch "hooks filtered, \d+ left vanilla" } | ForEach-Object { $_.Trim() })
    $referenceVanilla | ForEach-Object { Write-Output "reference left vanilla: $($_.Substring(0, [Math]::Min(180, $_.Length)))" }
    $referenceProblems += @($referenceVanilla | Where-Object { $_ -notmatch $ReferenceBackOffs } | ForEach-Object { "left vanilla on the unmodified game, not in `$ReferenceBackOffs: $_" })
    $referenceProblems | ForEach-Object { Write-Output "REFERENCE: $_" }
    $RuntimeFault = "disabled after exception|turned off and removed|faulted|error inside the game's code"
    $summary = @()
    foreach ($name in $names) {
        $target, $pattern, $mayLose = $Expected[$name]
        $log = @($building | Where-Object { $_.Plan.Name -eq $name })[0].Plan.Log
        $text = @(Get-Content $log -ErrorAction SilentlyContinue)
        $problems = @()
        if (-not (Test-Path $log)) { $problems += "no log: $log was never written" }
        $listed = @(python -I (Join-Path $Root "tools\check_versions.py") --local --log $log)
        # Exit code 2: it crashed (1 only says versions differ): a list cut short could make "nothing left vanilla" pass.
        $listCrashed = $LASTEXITCODE -ge 2
        $vanilla = $listed | Where-Object { $_ -match "^\s+\[" -and $_ -notmatch "hooks filtered, \d+ left vanilla" }
        $vanilla = @($vanilla | ForEach-Object { $_.Trim() } | Where-Object { $referenceVanilla -notcontains $_ })
        if ($listCrashed) { $problems += "check_versions.py --log failed: $(@($listed | Select-Object -Last 1))" }
        $state = ($runs | Where-Object { $_.Name -eq $name }).State
        if ($state -ne "done") { $problems += "session: $(if ($state) { $state } else { 'never ran (no result)' })" }
        # An earlier session of it that ended early or froze in this mod's code (not the known renderer crash), a test mod not loaded.
        $problems += @(($runs | Where-Object { $_.Name -eq $name }).Failed | Where-Object { $_ -match '^(ended early|froze|test mod )' })
        if (-not ($text -match "AutoTest .*> quit")) { $problems += "the scenario did not reach its end" + $(if ($text -match "Patch AutoTest (skipped|could not)") { " (the test runner itself could not load: test tooling must stay compatible)" } else { "" }) }
        if ($pattern -eq "") { if ($vanilla.Count -gt 0) { $problems += "$($vanilla.Count) part(s) left vanilla, expected none" } }
        else {
            if ($vanilla.Count -eq 0) { $problems += "nothing left vanilla, expected $pattern" }
            $problems += $vanilla | Where-Object { $_ -notmatch $pattern } | ForEach-Object { "unexpected: $_" }
        }
        # The mutation's pattern explains what is left vanilla at load, never a part faulting at run time: that is the regression this
        # test is for, even when its line names a part the pattern lists.
        $errors = $text | Where-Object { $_ -match "\[ERROR\].*\[BigDeckOptimization\]|BigDeckOptimization.*(disabled after exception|faulted)" -and ($pattern -eq "" -or $_ -match $RuntimeFault -or $_ -notmatch $pattern) }
        $problems += $errors | Select-Object -First 5 | ForEach-Object { "error: $($_.Substring(0, [Math]::Min(200, $_.Length)))" }
        $mismatch = $text | Where-Object { $_ -cmatch "MISMATCH|mismatches [1-9]|differ" }
        $problems += $mismatch | Select-Object -First 3 | ForEach-Object { "comparison: $($_.Substring(0, [Math]::Min(200, $_.Length)))" }
        $lost = @(python -I (Join-Path $PSScriptRoot "effects.py") $log --reference $Reference --lost)
        $problems += $lost | Where-Object { $_ -and ($mayLose -eq "" -or $_ -notmatch "^($mayLose)$") } | ForEach-Object { "effect lost: $_" }
        $same = python -I (Join-Path $PSScriptRoot "compare.py") $Reference $log --no-times --lines-only | Where-Object { $_ -match "^(IDENTICAL|DIFFERENT|NOT COMPARED|PROBLEMS)$" -or $_ -match "^checked lines" }
        if (-not ($same -contains "IDENTICAL")) { $problems += "game state differs from the unmodified game: $($same -join ' ')" }
        $ok = @($text | Where-Object { & $IdenticalLine $_ }).Count
        # A comparison of a part the mutation left vanilla says so ("not applied ...: nothing to compare") instead of "OK"; so does
        # bdperf checksum's row of a part another part's back-off idles ("not compared: filter ...": no list once ListenerCache left).
        $ok += @($text | Where-Object { $_ -match "not applied.*nothing to compare|not compared: " -and (& $EchoLine $_) }).Count
        # Every comparison the reference made must have run and said identical here too (a failed one now ends the session).
        if ($ok -lt $referenceIdentical) { $problems += "$ok comparison(s) said identical, the reference $referenceIdentical" }
        $verdict = if ($problems.Count -eq 0) { "PASS" } else { "FAIL" }
        $summary += [pscustomobject]@{ Mutation = $name; Verdict = $verdict; Vanilla = $vanilla.Count; Lost = ($lost -join " "); Identical = $ok; Problems = ($problems -join " | ") }
        Write-Output "== $name : $verdict ($($vanilla.Count) left vanilla, lost: $($lost -join ' '), $ok comparisons identical)"
        $vanilla | Select-Object -First 8 | ForEach-Object { Write-Output "     $($_.Substring(0, [Math]::Min(180, $_.Length)))" }
        $problems | ForEach-Object { Write-Output "   ! $_" }
    }
    $summary | Format-Table Mutation, Verdict, Vanilla, Lost, Identical -AutoSize | Out-String -Width 200 | Write-Output
    Write-Output ("{0}/{1} as expected in {2:F1} min" -f @($summary | Where-Object { $_.Verdict -eq "PASS" }).Count, $summary.Count, ((Get-Date) - $started).TotalMinutes)
    $failed = @($summary | Where-Object { $_.Verdict -ne "PASS" }).Count + $(if (-not $referenceOk -or $referenceIdentical -eq 0 -or $referenceProblems.Count -or -not (Test-Path $Reference)) { 1 } else { 0 })
    if ($undefined.Count -and -not $AllowUndefined) { $failed += $undefined.Count; Write-Output "NOT RUN: $($undefined -join ', ') (not defined in tools/mutate; -AllowUndefined to accept)" }
    if ($failed) { Write-Output "NOT AS EXPECTED: $failed (the reference counts as one when it is wrong, each mutation not run as one)" }
}
finally {
    Remove-Item $thinRoot -Recurse -Force -ErrorAction SilentlyContinue
    $runLock.ReleaseMutex(); $runLock.Dispose()
    Remove-TestMods $GameDir
    $after = Get-ChildItem $GameDir -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\mods_STEAMTEST\\' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
    $changed = @(Compare-Object $before $after)
    Write-Output "files of the copy changed: $($changed.Count)"
    $changed | Select-Object -First 5 | ForEach-Object { Write-Output "  $($_.InputObject)" }
}

exit $(if ($failed) { 1 } else { 0 })
