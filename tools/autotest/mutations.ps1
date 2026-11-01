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
# The copy's sts2.dll is backed up (sts2.dll.bdmut-backup) and restored after each mutation, and at the start if a
# previous run was stopped; a modified mod goes to <copy>\mods\<Mod>-bdmut (overrides the Workshop copy, removed after).
# Never point -GameDir at the Steam install. Debug build deployed in the copy first.
param([string]$GameDir = "C:\Games\StS2-public-0.107.1", [string[]]$Only = @(), [string]$Scenario = "effects-check", [int]$TimeoutSec = 600)
# Logs of sessions on a modified game stay out of results/<version>/.
$env:BD_NO_VERSIONED_LOGS = "1"
$ErrorActionPreference = "Stop"
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-Only a,b" through powershell -File arrives as one string
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($GameDir -like "*steamapps*") { throw "Use a game copy, not the Steam install" }
$Data = Join-Path $GameDir "data_sts2_windows_x86_64"
$Dll = Join-Path $Data "sts2.dll"
$Backup = "$Dll.bdmut-backup"
$Workshop = "C:\Program Files (x86)\Steam\steamapps\workshop\content\2868840"
$Results = Join-Path $PSScriptRoot "results"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"; $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Restore-All {
    if (Test-Path $Backup) { Copy-Item $Backup $Dll -Force; Remove-Item $Backup }
    Get-ChildItem (Join-Path $GameDir "mods") -Directory -Filter "*-bdmut" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName ".bdtest-created") } | Remove-Item -Recurse -Force
}
Restore-All

# What leaves when the whole pile tracking does: the listener cache watches the same methods, hook lists are built inside it.
$PileWriters = "ListenerCache|HookFilter|PileIndex(\.Duplicates)?|CardDbScan|FastListeners\.(Cache|Moves)|LimitlessRecalcSkip\.Passes|SnapshotCardReuse\.(Trust|Piles|PileBits)"
# Target, pattern every "left vanilla" line must match ("" = nothing may be left vanilla), effects allowed to drop to zero ("" = none).
$Expected = [ordered]@{
    "renumber-hooks"  = @("game", "", "")
    "new-hook"        = @("game", "", "")
    "unrelated-body"  = @("game", "", "")
    "energy-hook"     = @("game", "ModifyEnergyCostInCombat", "")
    "default-hitting" = @("game", "ShouldAllowHitting", "")
    "default-task"    = @("game", "AfterCardPlayed", "")
    "helper-push"     = @("game", "choice context helpers changed|PushModel", "")
    "pile-add"        = @("game", "CardPile.AddInternal changed", "FastListeners\.Moves")
    "pile-remove"     = @("game", "CardPile.RemoveInternal changed", "FastListeners\.Moves")
    "pile-writer"     = @("game", "_cards", $PileWriters)
    "rename-method"   = @("game", "RemoveInternal|AutoTest", $PileWriters)
    "rename-field"    = @("game", "combat walk left vanilla", "FastListeners\.CombatWalk")
    "combat-walk"     = @("game", "combat walk left vanilla", "FastListeners\.CombatWalk")
    "run-walk"        = @("game", "run walk left vanilla", "FastListeners\.RunWalk")
    "internal-member" = @("game", "HookFilter|GetWithModifiers", "SnapshotCardReuse\.(Piles|Trust|CardBits|PileBits)")
    "signature"       = @("game", "HookFilter|ShouldPlay", "")
    "flight-count"    = @("game", "CardFlightLimit|AddCard", "CardFlightLimit")
    "replay-record"   = @("game", "ReplayStateSkip|RecordChecksum", "ReplayStateSkip")
    "theme-rename"    = @("game", "ThemeColorSkip|RefreshText", "")
    "upgrade-writer"  = @("game", "_currentUpgradeLevel", "SnapshotCardReuse\.Trust|LimitlessRecalcSkip\.Passes")
    "write-list"      = @("game", "pile bits not reused", "SnapshotCardReuse\.PileBits")
    "local-keywords"  = @("game", "read through the game's own calls|get_LocalKeywords", "SnapshotCardReuse\.Trust")
    "limitless-body"  = @("Limitless", "LimitlessReserialize|LimitlessRecalcSkip|SetSerializableCards", "LimitlessReserialize|LimitlessRecalcSkip\.(Memos|Passes)")
    "limitless-forward" = @("Limitless", "LimitlessHookSkip|AfterCardPlayed", "LimitlessHookSkip")
    "limitless-writer"  = @("Limitless", "unchanged piles|SerializableEnchantmentsOnCards", "SnapshotCardReuse\.Trust")
    "limitless-recalc"  = @("Limitless", "LimitlessRecalcSkip|RecalculateValues|not skipped", "LimitlessRecalcSkip\.(Memos|Passes)")
    "limitless-tabs"  = @("Limitless", "EnchantmentTabReuse|UpdateEnchantmentVisuals", "EnchantmentTabReuse")
    "baselib-plaque"  = @("BaseLib", "TypePlaqueSkip|TryModifyPlaqueText", "TypePlaqueSkip")
}
dotnet build (Join-Path $Root "tools\mutate") -c Release -v q | Out-Null
$mutate = Join-Path $Root "tools\mutate\bin\Release\net9.0\mutate.dll"
# The reference: every effect must have fired on the unmodified game, or the check below proves nothing.
powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run.ps1") -Name $Scenario -GameDir $GameDir -TimeoutSec $TimeoutSec | Select-Object -Last 1 | Out-Host
$Reference = Join-Path $Results "mutation-reference.log"
Copy-Item (Join-Path $Results "$Scenario.log") $Reference -Force
python -I (Join-Path $PSScriptRoot "effects.py") $Reference | Out-Host
$summary = @()
foreach ($name in $Expected.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    $target, $pattern, $mayLose = $Expected[$name]
    try {
        if ($target -eq "game") {
            Copy-Item $Dll $Backup -Force
            dotnet $mutate $Backup $Dll $name $Data | Out-Host
        } else {
            $source = Get-ChildItem $Workshop -Recurse -Filter "$target.dll" | Select-Object -First 1
            $folder = Join-Path $GameDir "mods\$target-bdmut"
            New-Item -ItemType Directory -Force $folder | Out-Null
            Set-Content (Join-Path $folder ".bdtest-created") ""
            Copy-Item (Join-Path $source.DirectoryName "*") $folder -Recurse -Force
            dotnet $mutate $source.FullName (Join-Path $folder "$target.dll") $name $Data $source.DirectoryName | Out-Host
        }
        powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "run.ps1") -Name $Scenario -GameDir $GameDir -TimeoutSec $TimeoutSec | Select-Object -Last 1 | Out-Host
        $log = Join-Path $Results "mutation-$name.log"
        Copy-Item (Join-Path $Results "$Scenario.log") $log -Force
        $text = Get-Content $log
        $vanilla = (python -I (Join-Path $Root "tools\check_versions.py") --local --log $log) | Where-Object { $_ -match "^\s+\[" -and $_ -notmatch "hooks filtered, \d+ left vanilla" }
        $vanilla = @($vanilla | ForEach-Object { $_.Trim() })
        $problems = @()
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
        $same = python -I (Join-Path $PSScriptRoot "compare.py") $Reference $log | Where-Object { $_ -match "^(IDENTICAL|DIFFERENT)" -or $_ -match "^checked lines" }
        if (-not ($same -contains "IDENTICAL")) { $problems += "game state differs from the unmodified game: $($same -join ' ')" }
        $ok = @($text | Where-Object { $_ -match "OK: identical" }).Count
        $verdict = if ($problems.Count -eq 0) { "PASS" } else { "FAIL" }
        $summary += [pscustomobject]@{ Mutation = $name; Verdict = $verdict; Vanilla = $vanilla.Count; Lost = ($lost -join " "); Identical = $ok; Problems = ($problems -join " | ") }
        Write-Output "== $name : $verdict ($($vanilla.Count) left vanilla, lost: $($lost -join ' '), $ok comparisons identical)"
        $vanilla | Select-Object -First 8 | ForEach-Object { Write-Output "     $($_.Substring(0, [Math]::Min(180, $_.Length)))" }
        $problems | ForEach-Object { Write-Output "   ! $_" }
    } finally {
        Restore-All
    }
}
$summary | Format-Table Mutation, Verdict, Vanilla, Lost, Identical -AutoSize | Out-String -Width 200 | Write-Output
