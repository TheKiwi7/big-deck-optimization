# How the mod reacts when the game or a mod changes: for each mutation of tools/mutate (renumbered generated code, a new
# hook, a changed mirrored method, a changed AbstractModel default or helper, a renamed method or field, a member made
# internal, a changed signature, changed Limitless / BaseLib code), writes the modified assembly into a game COPY, runs
# a scenario there (default release-checks: combat + vanilla-vs-optimized comparisons) and checks that
#   - the session ran to its end with no error from this mod outside what the mutation explains,
#   - exactly the parts relying on the changed code were left vanilla (every "left vanilla" line matches the
#     mutation's pattern; none for mutations that change nothing the mod relies on),
#   - every bdperf comparison still says identical,
#   - the optimizations not relying on the changed code still work: against a session on the unmodified game, only the
#     effects (AutoTest's "effects" counters) the mutation may take away dropped to zero,
#   - the game did the same: CHECKSUM, STATE and CARDPLAY lines identical to that session, whatever is left vanilla.
#   powershell -ExecutionPolicy Bypass -File tools/autotest/mutations.ps1 [-GameDir C:\Games\StS2-public-0.107.1] [-Only renumber-hooks,energy-hook] [-Scenario effects-check]
# Each mutation runs in parallel in its own folder of hard links to the copy (no disk used) holding only its modified file,
# headless and without Steam, so the copy itself is never modified and stopping this script leaves nothing behind.
# Debug build deployed in the copy first.
param([string]$GameDir = "C:\Games\StS2-public-0.107.1", [string[]]$Only = @(), [string]$Scenario = "effects-check", [int]$TimeoutSec = 600, [int]$Jobs = [Environment]::ProcessorCount)
$copy = $GameDir
. (Join-Path $PSScriptRoot "parlib.ps1")
if ($copy -like "*steamapps*") { throw "Use a game copy, not the Steam install" }
$GameDir = Resolve-GameCopy $copy
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-Only a,b" through powershell -File arrives as one string
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Data = Join-Path $GameDir "data_sts2_windows_x86_64"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"; $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# What leaves when the whole pile tracking does: the listener cache watches the same methods, hook lists are built inside it.
$PileWriters = "ListenerCache|HookFilter|PileIndex(\.Duplicates)?|CardDbScan|DeckCountMemo(\.Checks)?|FastListeners\.(Cache|Moves)|LimitlessRecalcSkip\.Passes|SnapshotCardReuse\.(Trust|Moved|Dirty|Piles|PileBits|Blocks)"
# Target, pattern every "left vanilla" line must match ("" = nothing may be left vanilla), effects allowed to drop to zero ("" = none).
$Expected = [ordered]@{
    "renumber-hooks"  = @("game", "", "")
    "new-hook"        = @("game", "", "")
    "unrelated-body"  = @("game", "", "")
    "energy-hook"     = @("game", "ModifyEnergyCostInCombat", "")
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
    "rename-field"    = @("game", "combat walk left vanilla", "FastListeners\.CombatWalk")
    "combat-walk"     = @("game", "combat walk left vanilla", "FastListeners\.CombatWalk")
    "run-walk"        = @("game", "run walk left vanilla", "FastListeners\.RunWalk")
    "internal-member" = @("game", "HookFilter|GetWithModifiers", "SnapshotCardReuse\.(Release|Piles|Trust|Moved|Dirty|Blocks|CardBits|UpgradeBits|PileBits)")
    "signature"       = @("game", "HookFilter|ShouldPlay", "")
    "flight-count"    = @("game", "CardFlightLimit|AddCard", "CardFlightLimit")
    "replay-record"   = @("game", "ReplayStateSkip|RecordChecksum", "ReplayStateSkip")
    "theme-rename"    = @("game", "ThemeColorSkip|RefreshText", "")
    "upgrade-writer"  = @("game", "_currentUpgradeLevel", "SnapshotCardReuse\.(Trust|Moved|Dirty)|LimitlessRecalcSkip\.Passes")
    "write-list"      = @("game", "pile bits not reused", "SnapshotCardReuse\.(PileBits|Blocks)")
    "local-keywords"  = @("game", "read through the game's own calls|get_LocalKeywords", "SnapshotCardReuse\.(Trust|Moved|Dirty)")
    "limitless-body"  = @("Limitless", "LimitlessReserialize|LimitlessRecalcSkip|SetSerializableCards", "LimitlessReserialize|LimitlessRecalcSkip\.(Memos|Passes)|SnapshotCardReuse\.Moved|LimitlessCloneMemo")
    "limitless-forward" = @("Limitless", "LimitlessHookSkip|AfterCardPlayed", "")
    "limitless-writer"  = @("Limitless", "unchanged piles|SerializableEnchantmentsOnCards", "SnapshotCardReuse\.(Trust|Moved|Dirty)")
    "limitless-recalc"  = @("Limitless", "LimitlessRecalcSkip|RecalculateValues|not skipped", "LimitlessRecalcSkip\.(Memos|Passes)")
    "limitless-clone" = @("Limitless", "LimitlessCloneMemo|DeepCloneFields", "LimitlessCloneMemo|LimitlessRecalcSkip\.Passes")
    "limitless-tabs"  = @("Limitless", "EnchantmentTabReuse|UpdateEnchantmentVisuals", "EnchantmentTabReuse")
    "baselib-plaque"  = @("BaseLib", "TypePlaqueSkip|TryModifyPlaqueText", "TypePlaqueSkip")
}
dotnet build (Join-Path $Root "tools\mutate") -c Release -v q | Out-Null
$mutate = Join-Path $Root "tools\mutate\bin\Release\net9.0\mutate.dll"
$started = Get-Date
Add-TestMods $GameDir
$thinRoot = Join-Path (Split-Path $GameDir) "bdmut-copies"
Remove-Item $thinRoot -Recurse -Force -ErrorAction SilentlyContinue
$scriptPath = Join-Path $Scenarios "$Scenario.txt"
# The copy must come out as it went in: hard links share their content with it.
$before = Get-ChildItem $GameDir -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\mods_STEAMTEST\\' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
try {
    # What each mutation changes, built side by side: most of the time here is reading and writing the game's assembly.
    $names = @($Expected.Keys | Where-Object { $Only.Count -eq 0 -or $Only -contains $_ })
    $building = foreach ($name in $names) {
        $folder = Join-Path $thinRoot $name
        New-ThinCopy $GameDir $folder
        $target = $Expected[$name][0]
        if ($target -eq "game") {
            $in = Join-Path $Data "sts2.dll"
            $out = Join-Path $folder "data_sts2_windows_x86_64\sts2.dll"
            $search = @("`"$Data`"")
        } else {
            $source = Get-ChildItem (Join-Path $GameDir "mods_STEAMTEST") -Recurse -Filter "$target.dll" | Select-Object -First 1
            $in = $source.FullName
            $out = Join-Path $folder $source.FullName.Substring($GameDir.TrimEnd('\').Length + 1)
            $search = @("`"$Data`"", "`"$($source.DirectoryName)`"")
        }
        Remove-Item $out -Force
        $process = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Results "mutation-$name.build.txt") -ArgumentList (@("`"$mutate`"", "`"$in`"", "`"$out`"", $name) + $search)
        [void]$process.Handle
        [pscustomobject]@{ Name = $name; Process = $process; Out = $out }
    }
    foreach ($b in $building) {
        $b.Process.WaitForExit()
        if ($b.Process.ExitCode -ne 0 -or -not (Test-Path $b.Out)) { throw "mutate failed for $($b.Name), see results\mutation-$($b.Name).build.txt" }
    }
    Write-Output ("{0} mutations built in {1:F0} s" -f $names.Count, ((Get-Date) - $started).TotalSeconds)
    New-ThinCopy $GameDir (Join-Path $thinRoot "reference")
    # The reference: every effect must have fired on the unmodified game, or the check below proves nothing.
    $sessions = @([pscustomobject]@{ Name = "reference"; Script = $scriptPath; Log = Join-Path $Results "mutation-reference.log"; GameDir = Join-Path $thinRoot "reference" })
    $sessions += foreach ($name in $names) { [pscustomobject]@{ Name = $name; Script = $scriptPath; Log = Join-Path $Results "mutation-$name.log"; GameDir = Join-Path $thinRoot $name } }
    $runs = Invoke-Sessions -Jobs $sessions -GameDir $GameDir -MaxJobs $Jobs -TimeoutSec $TimeoutSec -Label "mutations"
    $Reference = Join-Path $Results "mutation-reference.log"
    python -I (Join-Path $PSScriptRoot "effects.py") $Reference | Out-Host
    $summary = @()
    foreach ($name in $names) {
        $target, $pattern, $mayLose = $Expected[$name]
        $log = Join-Path $Results "mutation-$name.log"
        $text = Get-Content $log
        $vanilla = (python -I (Join-Path $Root "tools\check_versions.py") --local --log $log) | Where-Object { $_ -match "^\s+\[" -and $_ -notmatch "hooks filtered, \d+ left vanilla" }
        $vanilla = @($vanilla | ForEach-Object { $_.Trim() })
        $problems = @()
        $state = ($runs | Where-Object { $_.Name -eq $name }).State
        if ($state -ne "done") { $problems += "session: $state" }
        if (-not ($text -match "AutoTest .*> quit")) { $problems += "the scenario did not reach its end" + $(if ($text -match "Patch AutoTest (skipped|could not)") { " (the test runner itself could not load: test tooling must stay compatible)" } else { "" }) }
        if ($pattern -eq "") { if ($vanilla.Count -gt 0) { $problems += "$($vanilla.Count) part(s) left vanilla, expected none" } }
        else {
            if ($vanilla.Count -eq 0) { $problems += "nothing left vanilla, expected $pattern" }
            $problems += $vanilla | Where-Object { $_ -notmatch $pattern } | ForEach-Object { "unexpected: $_" }
        }
        $errors = $text | Where-Object { $_ -match "\[ERROR\].*\[BigDeckOptimization\]|BigDeckOptimization.*(disabled after exception|faulted)" -and ($pattern -eq "" -or $_ -notmatch $pattern) }
        $problems += $errors | Select-Object -First 5 | ForEach-Object { "error: $($_.Substring(0, [Math]::Min(200, $_.Length)))" }
        $mismatch = $text | Where-Object { $_ -cmatch "MISMATCH|mismatches [1-9]|differ" }
        $problems += $mismatch | Select-Object -First 3 | ForEach-Object { "comparison: $($_.Substring(0, [Math]::Min(200, $_.Length)))" }
        $lost = @(python -I (Join-Path $PSScriptRoot "effects.py") $log --reference $Reference --lost)
        $problems += $lost | Where-Object { $_ -and ($mayLose -eq "" -or $_ -notmatch "^($mayLose)$") } | ForEach-Object { "effect lost: $_" }
        $same = python -I (Join-Path $PSScriptRoot "compare.py") $Reference $log --no-times | Where-Object { $_ -match "^(IDENTICAL|DIFFERENT)" -or $_ -match "^checked lines" }
        if (-not ($same -contains "IDENTICAL")) { $problems += "game state differs from the unmodified game: $($same -join ' ')" }
        $ok = @($text | Where-Object { $_ -match "OK: identical" }).Count
        $verdict = if ($problems.Count -eq 0) { "PASS" } else { "FAIL" }
        $summary += [pscustomobject]@{ Mutation = $name; Verdict = $verdict; Vanilla = $vanilla.Count; Lost = ($lost -join " "); Identical = $ok; Problems = ($problems -join " | ") }
        Write-Output "== $name : $verdict ($($vanilla.Count) left vanilla, lost: $($lost -join ' '), $ok comparisons identical)"
        $vanilla | Select-Object -First 8 | ForEach-Object { Write-Output "     $($_.Substring(0, [Math]::Min(180, $_.Length)))" }
        $problems | ForEach-Object { Write-Output "   ! $_" }
    }
    $summary | Format-Table Mutation, Verdict, Vanilla, Lost, Identical -AutoSize | Out-String -Width 200 | Write-Output
    Write-Output ("{0}/{1} as expected in {2:F1} min" -f @($summary | Where-Object { $_.Verdict -eq "PASS" }).Count, $summary.Count, ((Get-Date) - $started).TotalMinutes)
}
finally {
    Remove-Item $thinRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-TestMods $GameDir
    $after = Get-ChildItem $GameDir -Recurse -File -Force | Where-Object { $_.FullName -notmatch '\\mods_STEAMTEST\\' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
    $changed = @(Compare-Object $before $after)
    Write-Output "files of the copy changed: $($changed.Count)"
    $changed | Select-Object -First 5 | ForEach-Object { Write-Output "  $($_.InputObject)" }
}
