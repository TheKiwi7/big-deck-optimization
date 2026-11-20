# Parallel test sessions (dot-sourced by par.ps1, parsweep.ps1, leakroots.ps1). Each session runs headless on a game copy
# with its own APPDATA, a mirror of the user data folder: logs, settings and bdopt saves stay in the slot, the real ones
# are never touched. Results are identical to a rendered session; timings aren't meaningful (one rendered session alone
# for fps and frame times).
. (Join-Path $PSScriptRoot "common.ps1")
$SlotRoot = Join-Path $env:LOCALAPPDATA "BigDeckOptimization\test-slots"
$BaselineRoot = Join-Path $Results "baselines"
$MinFreeKB = 3 * 1024 * 1024
$StartLimitSec = 60
$FreezeSilenceSec = 20
# The solo test slots every series on this PC shares (par, parsweep, vanillacheck, mutations: slots 0 to $SlotPool - 1): at most
# that many solo sessions at once whatever each series' -Jobs, which caps only that series. Two series side by side (both copies'
# par blocks, the sweeps) share them; Invoke-Sessions gives a free slot to the series whose next session is the longest
# (Test-OtherSeriesFirst). BDTEST_SLOTS sets the pool (1 to 40: parcoop's slots start at 40); by default the logical processors
# less two (14 on 16). The memory guard ($MinFreeKB) still holds every start.
$SlotPool = [Math]::Max(1, [Environment]::ProcessorCount - 2)
if ("$env:BDTEST_SLOTS" -match '^\d+$') { $SlotPool = [Math]::Min(40, [Math]::Max(1, [int]$env:BDTEST_SLOTS)) }
# run.ps1 -KeepSlot: the slot of the sessions that follow each other and share their user data (Lock-KeptSlot: one per process,
# 80 to 89, so settings.ps1 and the like run on two copies at once). $KeptSlot: the one a script locked (80 until then).
$KeptSlots = 80..89
$KeptSlot = 80
# A solo mod-off session's launch argument (Invoke-Sessions, Test-BaselineJob): the mod starts off as a player's saved off would
# leave it, no optimization patch installed (ModEntry.VanillaArg, Debug builds only). Its log says so in this line.
$VanillaArg = "--bdvanilla"
$VanillaLinePattern = '^\[INFO\] \[BigDeckOptimization\] BDVANILLA: launched with --bdvanilla '

function Resolve-GameCopy([string]$dir) {
    if (-not $dir) { $dir = "$env:STS2_GAME_DIR" }
    if (-not $dir) { throw "Give -GameDir or STS2_GAME_DIR: parallel sessions run on a game copy" }
    if (-not (Test-Path (Join-Path $dir "steam_appid.txt"))) { throw "No steam_appid.txt in $dir" }
    # An old mutations.ps1 stopped from outside left its modified game there: every result would be about that game.
    if (Test-Path (Join-Path $dir "data_sts2_windows_x86_64\sts2.dll.bdmut-backup")) { throw "$dir has a modified game left by mutations.ps1: restore sts2.dll.bdmut-backup first" }
    Set-CopyOverrides $dir
    Restore-AsideMenu $dir
    $dir
}

# started-off-nomenu.ps1 sets this mod's menu DLL aside for one session: one stopped from outside left it there, and every later
# session on the copy would run without the settings menu (a warning only). Put back, unless that script may be running (it holds
# the copy's menu-aside lock, Lock-MenuAside: this process, or another one).
function Restore-AsideMenu([string]$gameDir) {
    $menu = Join-Path $gameDir "mods\BigDeckOptimization\BigDeckOptimization.Menu.dll"
    $aside = "$menu.bdtest-aside"
    if (-not (Test-Path $aside)) { return }
    $lock = Lock-MenuAside $gameDir -Try
    if (-not $lock) { return }
    try {
        # A menu DLL there was deployed since: the one set aside is older.
        if (Test-Path $menu) { Remove-Item $aside -Force } else { Move-Item $aside $menu }
        Write-Host "put back $menu, set aside by an interrupted started-off-nomenu.ps1"
    }
    finally { $lock.Dispose() }
}

# Held by started-off-nomenu.ps1 while the copy's menu DLL is set aside: a file in the test slots' folder, open until disposed (or
# the process ends). -Try: $null when another process holds it; this process holding it already counts as held.
function Lock-MenuAside([string]$gameDir, [switch]$Try) {
    New-Item -ItemType Directory -Force $SlotRoot | Out-Null
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((Get-CopyKey $gameDir)))) -replace "-", "").Substring(0, 16)
    try { [IO.File]::Open((Join-Path $SlotRoot "menuaside-$hash.lock"), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch {
        if ($Try) { return $null }
        throw "$gameDir's settings menu is set aside by another started-off-nomenu.ps1: wait for it to end."
    }
}

# The kept slot of this process (run.ps1 -KeepSlot and the scripts calling it: settings.ps1, baselib-optional.ps1...): the one it
# holds already (one level deeper, Lock-Slot's Depth), else the first free one of $KeptSlots. Released with Unlock-Slot.
function Lock-KeptSlot {
    if ($null -ne $global:BdKeptSlot -and $global:BdSlotLocks.ContainsKey([int]$global:BdKeptSlot)) {
        [void](Lock-Slot $global:BdKeptSlot)
        return [int]$global:BdKeptSlot
    }
    foreach ($slot in $KeptSlots) {
        if (Lock-Slot $slot -Try) { $global:BdKeptSlot = $slot; return $slot }
    }
    throw "Every kept test slot ($($KeptSlots[0])-$($KeptSlots[-1]), $SlotRoot) is in use by another test series: wait for one to end."
}

# Sessions run without Steam (--force-steam off): with it the game writes saves straight to Steam Cloud through Steam's API,
# whatever APPDATA says, and parallel sessions of one account make cloud conflicts. So no Workshop either: the mods go in the
# copy's mods_STEAMTEST, as for co-op sessions, for as long as the script runs.
function Add-TestMods([string]$gameDir) {
    # Never on the Steam install: a normal launch reads its mods_STEAMTEST before the Workshop, so a copy left there
    # (a session stopped from outside) shadows the Workshop mods, and their updates, in the user's own games.
    if ($gameDir -match 'steamapps' -or -not (Test-Path (Join-Path $gameDir "steam_appid.txt"))) { throw "Add-TestMods: $gameDir is not a game copy (no steam_appid.txt, or under steamapps)" }
    Set-CopyOverrides $gameDir
    $key = Get-CopyKey $gameDir
    $testMods = Join-Path $gameDir "mods_STEAMTEST"
    $own = Join-Path $testMods ".bdtest-created"
    $wanted = @(Get-TestModIds)
    $items = @(Get-TestModItems)
    # Every wanted mod must be there: a series would otherwise run without it and still say it tested with it.
    $onDisk = @(foreach ($item in $items) { Get-WorkshopModIds $item })
    $absent = @($wanted | Where-Object { $onDisk -notcontains $_ })
    if ($absent.Count) { throw "Test mods not in the Workshop folder ${Workshop}: $($absent -join ', ') (subscribe to them, or leave them out with BDTEST_MODS)" }
    # What the folder must hold: the mods wanted and the state of their Workshop folders. A folder left by an interrupted series
    # (other BDTEST_EXTRA_MODS, older Workshop versions) is made again, never reused as it is.
    $stamp = "created by tools/autotest/parlib.ps1`n" + ((@("extra=$env:BDTEST_EXTRA_MODS") + @($items | ForEach-Object {
        $files = @(Get-ChildItem $_.FullName -Recurse -File)
        "{0}|{1}|{2}" -f $_.Name, ($files | Measure-Object Length -Sum).Sum, ($files | ForEach-Object { $_.LastWriteTimeUtc.Ticks } | Measure-Object -Maximum).Maximum
    })) -join "`n")
    # Every .json the game must report finding ("Found mod manifest file"): Get-MissingTestMods checks each session's log.
    $manifests = @(foreach ($item in $items) { Get-ChildItem $item.FullName -Recurse -Filter *.json -File | ForEach-Object { Join-Path $item.Name $_.FullName.Substring($item.FullName.Length + 1) } })
    $lock = Enter-ModLock
    try {
        if ($global:BdModHolds.ContainsKey($key)) {
            # This process holds it already (parsweep's rounds): the same folder, unless what it must hold changed meanwhile.
            if (-not (Test-Path $own) -or [IO.File]::ReadAllText($own).Trim() -ne $stamp.Trim()) { throw "$testMods changed since this script made it (BDTEST_EXTRA_MODS, a Workshop update)" }
            return
        }
        $complete = (Test-Path $own) -and [IO.File]::ReadAllText($own).Trim() -eq $stamp.Trim() -and @($manifests | Where-Object { -not (Test-Path (Join-Path $testMods $_)) }).Count -eq 0
        if (-not $complete) {
            if ((Test-Path $testMods) -and -not (Test-Path $own)) { throw "$testMods already exists (left over or not ours): remove it first" }
            # Never under another live series: its sessions load these mods, and would go on without them.
            if ((Test-Path $testMods) -and (Test-OtherModHolders $gameDir)) { throw "$testMods is in use by another test series with other mods (BDTEST_EXTRA_MODS, a Workshop update): wait for it to end" }
            if (Test-Path $testMods) { Remove-Item -Recurse -Force $testMods }
            New-Item -ItemType Directory $testMods | Out-Null
            foreach ($item in $items) { Copy-Item -Recurse $item.FullName (Join-Path $testMods $item.Name) }
            [IO.File]::WriteAllText($own, $stamp)
        }
        # Held open until Remove-TestMods or this process ends: another series sees it in use, a crashed one's holder is stale.
        $path = Join-Path (Get-ModHolderDir $gameDir) "$PID-$([guid]::NewGuid().ToString('N')).hold"
        $global:BdModHolds[$key] = [pscustomobject]@{ Path = $path; Stream = [IO.File]::Open($path, 'CreateNew', 'ReadWrite', 'None') }
        $global:BdTestModManifests[$key] = $manifests
        $global:BdTestModIds[$key] = $wanted
        Write-Host "test mods: $(if ($wanted.Count) { $wanted -join ', ' } else { 'none (BDTEST_MODS)' })"
    }
    finally { Exit-ModLock $lock }
}

# A copy's mods_STEAMTEST is shared by every series on that copy (par, parcoop, run.ps1 -GameDir...): each process using it holds
# a file open in test-mods\<copy hash>; the folder is made again or removed only when no other live process holds one.
$ModHolderRoot = Join-Path $env:LOCALAPPDATA "BigDeckOptimization\test-mods"
if (-not (Test-Path variable:global:BdModHolds)) { $global:BdModHolds = @{} }
if (-not (Test-Path variable:global:BdTestModManifests)) { $global:BdTestModManifests = @{} }
if (-not (Test-Path variable:global:BdTestModIds)) { $global:BdTestModIds = @{} }

function Get-CopyKey([string]$gameDir) { [IO.Path]::GetFullPath($gameDir).TrimEnd('\').ToLowerInvariant() }

function Get-ModHolderDir([string]$gameDir) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((Get-CopyKey $gameDir)))) -replace "-", "").Substring(0, 16)
    $dir = Join-Path $ModHolderRoot $hash
    New-Item -ItemType Directory -Force $dir | Out-Null
    $dir
}

# Whether another live process holds this copy's test mods. A holder whose process ended is no longer open: deleted on the way.
function Test-OtherModHolders([string]$gameDir) {
    $mine = $global:BdModHolds[(Get-CopyKey $gameDir)]
    $others = $false
    foreach ($file in @(Get-ChildItem (Get-ModHolderDir $gameDir) -Filter *.hold -File -ErrorAction SilentlyContinue)) {
        if ($mine -and $file.FullName -eq $mine.Path) { continue }
        try { Remove-Item $file.FullName -Force -ErrorAction Stop } catch { $others = $true }
    }
    $others
}

# One process at a time makes, checks or removes a copy's test mods (two series starting together).
function Enter-ModLock {
    $mutex = [Threading.Mutex]::new($false, "Local\BigDeckOptimization-testmods")
    try { if (-not $mutex.WaitOne(180000)) { $mutex.Dispose(); throw "mods_STEAMTEST: another test script kept its lock for 3 minutes" } }
    catch [Threading.AbandonedMutexException] { }
    $mutex
}

function Exit-ModLock($mutex) {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}

# The test mods a session's game did not find (no "Found mod manifest file" line for one of their .json files): a mods_STEAMTEST
# emptied under a running series would make both sides of an A/B run without those mods, and agree. Then each one's loading:
# found is not loaded (the game logs every .json before reading it). A test mod must finish its initialization, without an exception
# in its initializer (the game logs "Finished" all the same) or a degraded start. A mod the session's profile disables is a script's
# own choice (Sync-Slot enables every test mod in the slot's profiles: only a script's later edit, baselib-absent, disables one), and
# so are the mods depending on it. Known failures (a note, not a problem): LobbyBanlist's initializer throws on v0.113; Unlimited
# 2.0.0's throws on v0.107 and v0.111 (built for v0.113: a TypeLoadException on NBlitzScreen, a v0.113 type absent from v0.107.1
# and v0.111.1, before any of its patches; with or without this mod, so on those versions sessions run without Unlimited's patches).
$KnownModInitFailures = @{ "LobbyBanlist" = '^v0\.113\.'; "sts2unlimited" = '^v0\.1(07|11)\.' }
$DegradedMarkers = @{ "sts2unlimited" = "Sts2Unlimited loaded DEGRADED" }
if (-not (Test-Path variable:global:BdNotedModFailures)) { $global:BdNotedModFailures = [Collections.Generic.HashSet[string]]::new() }
function Get-MissingTestMods([string]$log, [string]$gameDir) {
    $key = Get-CopyKey $gameDir
    $manifests = @($global:BdTestModManifests[$key] | Where-Object { $_ })
    if ($manifests.Count -eq 0 -or -not (Test-Path $log)) { return }
    $found = @(Select-String -Path $log -SimpleMatch "Found mod manifest file" | ForEach-Object { $_.Line.Replace('/', '\') })
    foreach ($manifest in $manifests) {
        if (-not ($found | Where-Object { $_.EndsWith("mods_STEAMTEST\$manifest", [StringComparison]::OrdinalIgnoreCase) })) { "test mod not loaded: the game did not find mods_STEAMTEST\$manifest" }
    }
    $loading = @(Select-String -Path $log -Pattern "^\[\w+\] (Finished mod initialization for '.*' \((\S+)\)\.|Skipping loading mod (\S+), it is set to disabled|Tried to load mod (\S+), but it depends on mods which have not been loaded: (.+)!|Exception thrown when calling mod initializer of type ([\w.]+))")
    $finished = @($loading | Where-Object { $_.Matches[0].Groups[2].Success } | ForEach-Object { $_.Matches[0].Groups[2].Value })
    $excused = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $loading | Where-Object { $_.Matches[0].Groups[3].Success } | ForEach-Object { [void]$excused.Add($_.Matches[0].Groups[3].Value) }
    # A mod depending only on mods left out on purpose (or on mods left out because of them) is left out on purpose too.
    $dependents = @($loading | Where-Object { $_.Matches[0].Groups[4].Success } | ForEach-Object { [pscustomobject]@{ Id = $_.Matches[0].Groups[4].Value; On = @($_.Matches[0].Groups[5].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } })
    do {
        $grew = $false
        foreach ($d in $dependents) { if (-not $excused.Contains($d.Id) -and @($d.On | Where-Object { -not $excused.Contains($_) }).Count -eq 0) { [void]$excused.Add($d.Id); $grew = $true } }
    } while ($grew)
    $initFailed = @($loading | Where-Object { $_.Matches[0].Groups[6].Success } | ForEach-Object { $_.Matches[0].Groups[6].Value })
    $tag = Get-GameTag $gameDir
    foreach ($id in @($global:BdTestModIds[$key] | Where-Object { $_ })) {
        if ($excused.Contains($id)) { continue }
        if ($finished -notcontains $id) { "test mod not loaded: $id (no 'Finished mod initialization' line: it failed to load, or a profile disabled it)"; continue }
        # The initializer's type: its namespace's first part is the mod's id (LobbyBanlist.MainFile, Sts2Unlimited.*).
        $types = @($initFailed | Where-Object { ($_ -split '\.')[0] -eq $id })
        if ($types.Count) {
            if ($KnownModInitFailures.ContainsKey($id) -and $tag -match $KnownModInitFailures[$id]) {
                # Once per series, not per session.
                if ($global:BdNotedModFailures.Add("$id|$tag")) { Write-Host "note: test mod $id's initializer threw ($($types[0])), known on $tag" }
            }
            else { "test mod $id's initializer threw ($($types[0])): it runs degraded or not at all" }
        }
        if ($DegradedMarkers.ContainsKey($id) -and (Select-String -Path $log -SimpleMatch $DegradedMarkers[$id] -Quiet)) { "test mod $id loaded degraded ('$($DegradedMarkers[$id])')" }
    }
}

# The Workshop folders of the mods a test session loads besides this one (Get-TestModIds).
function Get-TestModItems {
    $wanted = @(Get-TestModIds)
    foreach ($item in Get-ChildItem $Workshop -Directory | Sort-Object Name) {
        if (@(Get-WorkshopModIds $item | Where-Object { $wanted -contains $_ }).Count -gt 0) { $item }
    }
}

function Get-WorkshopModIds($item) {
    Get-ChildItem $item.FullName -Recurse -Filter *.json | ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).id } catch { } } | Where-Object { $_ }
}

# The mods a test session loads besides this one. Not the account's list alone: a player who turns their mods off to play vanilla
# would make every series run without them, and every A/B agree. Always the supported mods this tooling verifies (BaseLib, Limitless,
# BetterSpire2: tools/versions.json), then the account's enabled Workshop mods, then BDTEST_EXTRA_MODS=SpeedX,ModConfig,...
# (Workshop mods on disk the account doesn't enable: what players combine with this mod). BDTEST_MODS=a,b replaces the whole list
# ("none": only this mod). Add-TestMods fails when one of them isn't in the Workshop folder.
$SupportedTestMods = @("BaseLib", "Limitless", "BetterSpire2")
function Get-TestModIds {
    if ($env:BDTEST_MODS) { return @($env:BDTEST_MODS -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne "none" -and $_ -ne "BigDeckOptimization" } | Sort-Object -Unique) }
    $settings = Get-Content (Join-Path (Get-SteamAccountDir) "settings.save") -Raw | ConvertFrom-Json
    $extra = @($env:BDTEST_EXTRA_MODS -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    @(@($SupportedTestMods) + @($extra) + @($settings.mod_settings.mod_list | Where-Object { $_.is_enabled -and $_.source -eq "steam_workshop" -and $_.id -ne "BigDeckOptimization" } | ForEach-Object { $_.id }) | Sort-Object -Unique)
}

# Releases this process's hold on a copy's test mods, and removes the folder when no other live series holds it.
function Remove-TestMods([string]$gameDir) {
    $key = Get-CopyKey $gameDir
    $testMods = Join-Path $gameDir "mods_STEAMTEST"
    $own = Join-Path $testMods ".bdtest-created"
    $lock = Enter-ModLock
    try {
        $hold = $global:BdModHolds[$key]
        if ($hold) {
            $hold.Stream.Dispose()
            Remove-Item $hold.Path -Force -ErrorAction SilentlyContinue
            # The manifests stay known: runners judge their logs after releasing the folder.
            $global:BdModHolds.Remove($key)
        }
        if (-not (Test-Path $own) -or (Test-OtherModHolders $gameDir)) { return }
        # The games just closed may still hold their files a moment. The stamp goes last: a folder left half removed is still
        # known as this tooling's, and made again by the next series.
        Start-Sleep 2
        Get-ChildItem $testMods -Force | Where-Object { $_.Name -ne ".bdtest-created" } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        if (@(Get-ChildItem $testMods -Force | Where-Object { $_.Name -ne ".bdtest-created" }).Count -eq 0) { Remove-Item -Recurse -Force $testMods -ErrorAction SilentlyContinue }
    }
    finally { Exit-ModLock $lock }
}

function Get-VersionDir([string]$gameDir) {
    $path = Join-Path $Results (Get-GameTag $gameDir)
    New-Item -ItemType Directory -Force $path | Out-Null
    $path
}

# Without Steam the game reads the profiles default/1 and default/1001, with their own settings (60 fps, less anti-aliasing):
# timings are only comparable with your account's. Everything but the mod list and the language, which stay English there.
# Solo sessions also take the test profile's progress and preferences (what's unlocked changes what a run offers), so they
# give the same results as sessions through Steam. Co-op keeps its own unlocked profiles.
function Copy-DisplaySettings([string]$data, [bool]$solo) {
    $accounts = @(Get-ChildItem (Join-Path $data "steam") -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+$' })
    if ($accounts.Count -ne 1) { return }
    $source = Get-Content (Join-Path $accounts[0].FullName "settings.save") -Raw | ConvertFrom-Json
    $itemIds = $null
    foreach ($id in "1", "1001") {
        $file = Join-Path $data "default\$id\settings.save"
        if (-not (Test-Path $file)) { continue }
        $settings = Get-Content $file -Raw | ConvertFrom-Json
        foreach ($property in $source.PSObject.Properties) {
            if ($property.Name -in "mod_settings", "language", "schema_version") { continue }
            $settings | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
        }
        # Every mod of the test mods' Workshop folders enabled, not only the test ids: an old profile may disable one (BaseLib's
        # BaseLibCompatibilityHelper, which BaseLib depends on, from the same item), and Get-MissingTestMods excuses a disabled mod
        # and its dependents: the series would run without them. A script disabling one on purpose does it after (Set-SlotModEnabled).
        if ($settings.mod_settings -and $settings.mod_settings.mod_list) {
            if ($null -eq $itemIds) { $itemIds = @(Get-TestModIds) + @(Get-TestModItems | ForEach-Object { Get-WorkshopModIds $_ }) }
            $settings.mod_settings.mod_list | Where-Object { $_.source -eq "steam_workshop" -and $itemIds -contains $_.id } | ForEach-Object { $_.is_enabled = $true }
        }
        [IO.File]::WriteAllText($file, ($settings | ConvertTo-Json -Depth 30))
    }
    if ($solo) {
        $saves = Join-Path $data "default\1\modded\profile3\saves"
        New-Item -ItemType Directory -Force $saves | Out-Null
        foreach ($name in "progress.save", "prefs.save") {
            $from = Join-Path $accounts[0].FullName "modded\profile3\saves\$name"
            if (Test-Path $from) { Copy-Item $from (Join-Path $saves $name) -Force }
        }
    }
}

# Test slots this process uses: slot -> its lock file, held while a session runs there (Depth: nested holders, e.g. settings.ps1
# then the run.ps1 it calls). Two series never share a user data folder (a save of one renamed under the other, a slot mirrored
# over a running session); series on other slots still run side by side. A process that ends releases its locks.
if (-not (Test-Path variable:global:BdSlotLocks)) { $global:BdSlotLocks = @{} }
function Lock-Slot([int]$slot, [switch]$Try) {
    if ($global:BdSlotLocks.ContainsKey($slot)) { $global:BdSlotLocks[$slot].Depth++; return $true }
    New-Item -ItemType Directory -Force $SlotRoot | Out-Null
    try { $stream = [IO.File]::Open((Join-Path $SlotRoot "slot$slot.lock"), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch {
        if ($Try) { return $false }
        throw "Test slot $slot ($SlotRoot) is in use by another test series: wait for it to end."
    }
    $global:BdSlotLocks[$slot] = [pscustomobject]@{ Stream = $stream; Depth = 1 }
    return $true
}

function Unlock-Slot([int]$slot) {
    $entry = $global:BdSlotLocks[$slot]
    if (-not $entry) { return }
    $entry.Depth--
    if ($entry.Depth -le 0) { $entry.Stream.Dispose(); $global:BdSlotLocks.Remove($slot) }
}

# A co-op test's local port (--bdport): one per test slot, so two series side by side (each on its own locked slots) never share
# one. 34000 + slot: apart from the game's fixed 33771 (coop.ps1, a single pair on slot 70, keeps it).
function Get-SlotPort([int]$slot) { 34000 + $slot }

# The launch arguments a scenario asks for: --bdoff when it has a 'launchoff' line (the whole mod off for the session from its
# launch, before the main menu's --fastmp lobby builds the co-op join lists; the line fails without it). Every launcher adds them.
#  Also --bdforeign <shape> for each 'launchforeign <shape>' line: another mod's patches that must be in before this mod applies
# (Bench/ForeignLaunch.cs; the line fails without it). Invoke-Sessions adds --bdvanilla to a mod-off job (Test-BaselineJob).
function Get-ScenarioLaunchArgs([string]$script) {
    if (-not (Test-Path $script)) { return @() }
    $launch = @()
    if (Select-String -Path $script -Pattern '^\s*launchoff\s*$' -Quiet) { $launch += "--bdoff" }
    foreach ($m in (Select-String -Path $script -Pattern '^\s*launchforeign\s+(\S+)\s*$')) { $launch += @("--bdforeign", $m.Matches[0].Groups[1].Value) }
    # --bdearly for a 'launchearly' line: the script starts as soon as the main menu shows (InlineGuard's rebuilds still going).
    if (Select-String -Path $script -Pattern '^\s*launchearly\s*$' -Quiet) { $launch += "--bdearly" }
    $launch
}

# Mirrors the user data into a slot: the caller holds the slot's lock (Lock-Slot) until its session ends.
function Sync-Slot([int]$slot, [switch]$Coop) {
    if (-not $global:BdSlotLocks.ContainsKey($slot)) { throw "Sync-Slot ${slot}: take its lock first (Lock-Slot)" }
    $dir = Join-Path $SlotRoot "slot$slot"
    # The profiles' progress stays (what's unlocked changes what a run offers). Runs in progress don't: a save left by the last
    # session here, maybe from another game version, is called corrupt by the game.
    # Few retries: a file held open (the game you play meanwhile, Steam) would make robocopy retry for days by default.
    robocopy $UserData (Join-Path $dir "SlayTheSpire2") /MIR /R:2 /W:1 /XD logs history replays sentry shader_cache vulkan /XF *.log *.tmp current_run* *.corrupt /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Sync-Slot ${slot}: robocopy failed (exit code $LASTEXITCODE): the slot's user data would be incomplete" }
    Get-ChildItem (Join-Path $dir "SlayTheSpire2") -Recurse -File -Include current_run*, *.corrupt -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    # This mod's settings start at their defaults (every optimization on), whatever yours are: a saved "Enabled": "False" (a
    # bdopt off saved by BaseLib) would leave every patch unapplied, and every A/B would compare the game with itself; an option
    # you turned off would go untested. A scenario's bdopt lines still apply; scripts testing saved settings write theirs after.
    Remove-Item (Join-Path $dir "SlayTheSpire2\mod_configs\BigDeckOptimization.cfg") -Force -ErrorAction SilentlyContinue
    Copy-DisplaySettings (Join-Path $dir "SlayTheSpire2") (-not $Coop)
    $dir
}

# Edits the mod list of a slot's solo profile: $change takes the list and returns the new one.
function Update-SlotModList([string]$slot, [scriptblock]$change) {
    $file = Join-Path $slot "SlayTheSpire2\default\1\settings.save"
    $data = Get-Content $file -Raw | ConvertFrom-Json
    $data.mod_settings.mod_list = @(& $change @($data.mod_settings.mod_list))
    # No BOM: the game's JSON reader expects plain UTF-8.
    [IO.File]::WriteAllText($file, ($data | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
}

# Takes a Workshop mod out of a slot's solo profile or back in. The profile may have no entry for it yet: a mod without one loads.
# The game goes by id and source, and reads mods_STEAMTEST as the Workshop.
function Set-SlotModEnabled([string]$slot, [string]$id, [bool]$enabled) {
    Update-SlotModList $slot {
        param($mods)
        $entry = @($mods | Where-Object { $_.id -eq $id })
        if ($entry.Count) { $entry | ForEach-Object { $_.is_enabled = $enabled }; $mods }
        else { $mods; [pscustomobject]@{ id = $id; is_enabled = $enabled; source = "steam_workshop" } }
    }
}

# Whether a session's log shows what the test is about: a setup that did nothing would otherwise pass unseen. A line missing proves
# something only in a session that ran to its end (judge.py's end lines): a missing or cut-short log is a PROBLEM for a negative
# check too. Each PROBLEM counts in $script:SessionLogProblems (the calling script's exit code). The pattern is case-sensitive and
# never matches the script's own lines (judge.py's: the echo '> checksum x' of a line, the game's echo of a console command, the
# EXPECT and EXPECTDECL lines): 'CHECKSUM x' is the result, '> checksum x' only the request.
$SessionEnd = 'AutoTest\s+[\d.]+s\s+(script finished|> quit\b|handed over to the player)'
$ScriptOwnLine = 'AutoTest\s+[\d.]+s\s+(> |EXPECT |EXPECTDECL )|Executing DevConsole command|DevConsole: '
# The log is run.ps1's on the calling script's copy ($GameDir: results\<version>\<name>.log, the same script on the other copy at the
# same time writing its own), or results\<name>.log (BD_NO_VERSIONED_LOGS, or no copy).
function Test-SessionLog([string]$name, [string]$pattern, [bool]$expected, [string]$meaning) {
    $log = if ($GameDir -and -not $env:BD_NO_VERSIONED_LOGS) { Join-Path (Get-VersionDir $GameDir) "$name.log" } else { Join-Path $Results "$name.log" }
    if (-not (Test-Path $log)) { $ok = $false; $meaning += " (no session log)" }
    else {
        $seen = [bool](Select-String -Path $log -Pattern $pattern -CaseSensitive | Where-Object { $_.Line -cnotmatch $ScriptOwnLine } | Select-Object -First 1)
        $ok = $seen -eq $expected
        if ($ok -and -not $expected -and -not (Select-String -Path $log -Pattern $SessionEnd -Quiet)) { $ok = $false; $meaning += " (the session did not reach its end: a missing line proves nothing)" }
    }
    if (-not $ok) { $script:SessionLogProblems = 1 + [int]$script:SessionLogProblems }
    $script:SessionLogChecks = 1 + [int]$script:SessionLogChecks
    Write-Output ("{0} {1}: {2}" -f $(if ($ok) { "ok     " } else { "PROBLEM" }), $name, $meaning)
}

# The closing line of a script built on Test-SessionLog (settings.ps1, started-off.ps1...), the last line of its work: how many
# checks ran and its problems (theirs, plus $runProblems, the run.ps1 sessions it judged with one; no check at all is one). A
# script cut short never prints it, and battery.ps1 fails a step without its runner's closing line. Returns the problem count.
function Write-SessionLogSummary([int]$runProblems) {
    $checks = [int]$script:SessionLogChecks
    $problems = [int]$script:SessionLogProblems + $runProblems + $(if ($checks -eq 0) { 1 } else { 0 })
    Write-Output ("{0} done: {1} session log check(s), {2} problem(s){3}" -f (Split-Path $MyInvocation.PSCommandPath -Leaf), $checks, $problems, $(if ($checks -eq 0) { " (no check ran)" } else { "" }))
}

# The runner's check that Invoke-Sessions gave a result for every job it was handed (by name): a job without one never ran, and
# its log, if any, is not this run's. One line per missing job, nothing when all are there.
function Get-MissingResults($jobs, $runs) {
    $have = [Collections.Generic.HashSet[string]]::new([string[]]@($runs | Where-Object { $_ } | ForEach-Object { "$($_.Name)" }))
    foreach ($job in $jobs) { if (-not $have.Contains("$($job.Name)")) { "PROBLEM $($job.Name): no result, the session never ran (Invoke-Sessions stopped before it)" } }
}

# Everything a mod-off log depends on: a baseline stays valid until the game, a test mod's files, which mods are loaded
# (Get-TestModIds), the shared run files the scenarios import, the test profile's progress (what a run offers), the other mods'
# settings and data the slots copy (mod_configs; mod_data, e.g. LobbyBanlist's ban profiles), the test tooling the scenario
# runs or the scenario changes. Not this mod's patches: a mod-off session is launched with --bdvanilla and none of them is
# installed (vanillacheck.ps1 proves a patched-off session, every patch installed then bdopt off, logs the same lines).
# 'ref=vanilla1': the logs of patched-off sessions, kept before --bdvanilla, never serve as vanilla ones.
$script:CoopProfilesSalt = $null
# The test tooling a script runs: the scenario engine and its helpers always, each optional part only when one of its commands is
# in the script (AutoTest.cs reaches them only through those commands; a few CPU-time helpers feed timings, which no comparison
# reads). A change elsewhere in Bench/ leaves this scenario's baseline valid. A console command counts through 'cmd' and through
# 'autoslayfirst' (the same Command() at the auto player's first room). A new script command goes in a part file as a
# [ScriptCommand] method (AutoTest.PartCommand), with its name added to that file's pattern here (check_scenarios.py checks
# both): a new case in AutoTest.cs's switch changes AutoTest.cs, which every key counts, and so invalidates every baseline.
# A file without a command of its own (the foreign shapes) is keyed by the lines that reach its callers: check_scenarios.py's
# CALLER_KEYED lists the only places allowed to name its types and the lines each is reached by, and fails on any other.
$BaselineBenchParts = [ordered]@{
    'AutoTestRooms.cs'      = '^\s*(autoroom|automap|barrier|continuerun|runstate|restsite|selector|eventoption)\b'
    'AutoTestMouse.cs'      = '^\s*(hover|checktips|mouseplay|mousesweep|movehovered|buttoninput|controllermap|forcefocus|tipsrebuilt|targethover)\b'
    'AutoTestSweep.cs'      = '^\s*(sweep|sweeptimeout)\b'
    'AutoTestLeak.cs'       = '^\s*leakcheck\b'
    'AutoTestTrace.cs'      = '^\s*eventtrace\b'
    # Also through 'autoslayfirst', 'barrier' (a networked 'bigdeck mark') and '{hash:NAME}' (BigDeckConsoleCmd.SharedHash, any line).
    'BigDeckConsoleCmd.cs'  = '^\s*((cmd|autoslayfirst)\s+bigdeck\b|barrier\b)|\{hash:'
    'RebuildCheck.cs'       = '^\s*rebuildcheck\b'
    'FaultProbe.cs'         = '^\s*faultprobe\b'
    'PerfConsoleCmd.cs'     = '^\s*(cmd|autoslayfirst)\s+bdperf\b'
    'GridBench.cs'          = '^\s*(cmd|autoslayfirst)\s+bdperf\s+grid\b'
    'PerfProbe.cs'          = '^\s*(cmd|autoslayfirst)\s+bdperf\s+(probe|nodes|hitch)\b'
    'RelicBench.cs'         = '^\s*(cmd|autoslayfirst)\s+bdperf\s+relics\b'
    'ShuffleBench.cs'       = '^\s*(cmd|autoslayfirst)\s+bdperf\s+shufflebench\b'
    'UpgradeBench.cs'       = '^\s*(cmd|autoslayfirst)\s+bdperf\s+upgradebench\b'
    'SortKeysBench.cs'      = '^\s*(cmd|autoslayfirst)\s+bdperf\s+sortkeys\b'
    'AutoTestStartup.cs'    = '^\s*(startsets|launchearly)\b'
    # Only the launch argument --bdstartprof turns it on, never a script line: no scripted session depends on it.
    'StartupProfile.cs'     = '(?!)'
    # The synthetic foreign patches, no [ScriptCommand]: reached only through the call sites check_scenarios.py's CALLER_KEYED
    # allows (it fails on any other reference to their types). ForeignPatches.cs runs from 'foreignpatch' (AutoTest.cs), 'bdperf
    # foreign' (PerfConsoleCmd.cs), 'launchforeign' (the launch scripts' --bdforeign: ForeignLaunch.Install) and 'faultprobe'
    # (FaultProbe.cs compiles its IdPrefix constant in); the others only from ForeignPatches.cs and each other. One pattern for all.
    'ForeignPatches.cs'              = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'ForeignShapesGates.cs'          = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'ForeignShapesHookFilter.cs'     = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'ForeignShapesLimitless.cs'      = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'ForeignShapesLimitlessTwins.cs' = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'ForeignShapesParts.cs'          = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'ForeignShapesUnchanged.cs'      = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    'IlJudgeShapes.cs'               = '^\s*(foreignpatch|launchforeign|faultprobe)\b|^\s*(cmd|autoslayfirst)\s+bdperf\s+foreign\b'
    # Only 'bdperf joinmsg' (PerfConsoleCmd.cs) runs it.
    'JoinMismatchBench.cs'           = '^\s*(cmd|autoslayfirst)\s+bdperf\s+joinmsg\b'
}
# 'leakcheck' (AutoTestLeak.cs) reads the static fields of every type of this assembly (their initializers run then): a script
# with it counts every Bench file, whatever its pattern.
$BaselineBenchAll = '^\s*leakcheck\b'
function Get-BaselineBenchFiles([string]$scriptText) {
    $bench = Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\Bench"
    $all = [regex]::IsMatch($scriptText, $BaselineBenchAll, 'Multiline, IgnoreCase')
    foreach ($file in Get-ChildItem $bench -Filter *.cs) {
        if ($file.Name -eq 'AutoTestVideo.cs') { continue }  # local only: video scenarios, never cached
        $pattern = $BaselineBenchParts[$file.Name]
        # A file not in the table (the engine, its helpers, a new file) always counts: unknown is never left out.
        # Case-insensitive, as the dispatcher (AutoTest.cs's switch on the lower-cased command, PartCommand's OrdinalIgnoreCase):
        # 'Hover' runs AutoTestMouse.cs as 'hover' does.
        if ($all -or -not $pattern -or [regex]::IsMatch($scriptText, $pattern, 'Multiline, IgnoreCase')) { $file }
    }
}

# A Bench file's part of the key: the hash of its code without comments nor the whitespace between tokens (BdCodeText.Normalize:
# string and char literals kept byte for byte, line breaks kept), so a comment-only or layout-only edit leaves every baseline
# valid; the hash of the file as it is when the lexer is unsure of anything (an unclosed literal, a raw string literal). Checked
# exact on every Bench file (2026-11-18): a Debug build of the normalized sources decompiles (ilspycmd) to the same C# as the
# original's. Only line numbers move, which no compared line holds (compare.py replaces numbers in the game errors it counts).
# The whole file's hash before this (9ca14ff) is no longer read: those baselines were all kept before 191480e, whose keys were
# taken when the session ended, from the tree as it was then (a log filed under a key it never ran).
if (-not ("BdCodeText" -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Text;
public static class BdCodeText {
    // C# source without its comments and without the whitespace between tokens (runs of spaces and tabs become one space, the
    // indentation and blank lines go, line breaks stay); string and char literals kept byte for byte. Null when unsure (an
    // unclosed literal or comment, a raw string literal, $$ interpolation): the caller then hashes the file as it is.
    public static string Normalize(string s) {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        bool pendingSpace = false, pendingLine = false;
        if (!Code(s, ref i, sb, ref pendingSpace, ref pendingLine, false)) return null;
        return sb.ToString();
    }

    static void Flush(StringBuilder sb, ref bool pendingSpace, ref bool pendingLine) {
        if (pendingLine) { if (sb.Length > 0) sb.Append('\n'); }
        else if (pendingSpace) { if (sb.Length > 0) sb.Append(' '); }
        pendingSpace = false; pendingLine = false;
    }

    // Code until the end (hole = false) or until the '}' closing an interpolation hole at nesting 0, or its format ':' (hole = true:
    // i is left on that '}' or ':').
    static bool Code(string s, ref int i, StringBuilder sb, ref bool pendingSpace, ref bool pendingLine, bool hole) {
        int nest = 0;
        while (i < s.Length) {
            char c = s[i];
            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '\r' || c == '\n') { pendingLine = true; i++; continue; }
            if (c == ' ' || c == '\t' || c == '\f' || c == '\v') { pendingSpace = true; i++; continue; }
            if (c == '/' && n == '/') {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                continue;
            }
            if (c == '/' && n == '*') {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                // A comment separates tokens as a space does.
                for (int k = i; k < end; k++) if (s[k] == '\n') pendingLine = true;
                pendingSpace = true;
                i = end + 2;
                continue;
            }
            if (hole && nest == 0 && (c == '}' || c == ':')) return true;
            if (c == '"' && n == '"' && i + 2 < s.Length && s[i + 2] == '"') return false; // raw string literal
            if (c == '$' && n == '$') return false;
            if (c == '"' || c == '\'' || (c == '@' && n == '"') || (c == '$' && (n == '"' || n == '@')) || (c == '@' && n == '$')) {
                Flush(sb, ref pendingSpace, ref pendingLine);
                if (!Literal(s, ref i, sb)) return false;
                continue;
            }
            Flush(sb, ref pendingSpace, ref pendingLine);
            if (c == '(' || c == '[' || c == '{') nest++;
            else if (c == ')' || c == ']' || c == '}') nest--;
            sb.Append(c);
            i++;
        }
        return !hole;
    }

    // A string or char literal at i, copied as it is; i past its end.
    static bool Literal(string s, ref int i, StringBuilder sb) {
        int start = i;
        bool interpolated = false, verbatim = false;
        while (i < s.Length && (s[i] == '$' || s[i] == '@')) { if (s[i] == '$') interpolated = true; else verbatim = true; i++; }
        if (i >= s.Length) return false;
        char quote = s[i];
        if (quote == '\'') {
            if (interpolated || verbatim) return false;
            i++;
            while (i < s.Length && s[i] != '\'') {
                if (s[i] == '\n' || s[i] == '\r') return false;
                if (s[i] == '\\') i++;
                i++;
            }
            if (i >= s.Length) return false;
            i++;
            sb.Append(s, start, i - start);
            return true;
        }
        if (quote != '"') return false;
        sb.Append(s, start, i - start + 1);
        i++;
        while (i < s.Length) {
            char c = s[i];
            if (!verbatim && (c == '\n' || c == '\r')) return false;
            if (!verbatim && c == '\\') {
                if (i + 1 >= s.Length) return false;
                sb.Append(c).Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '"') {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { sb.Append("\"\""); i += 2; continue; }
                sb.Append('"');
                i++;
                return true;
            }
            if (interpolated && (c == '{' || c == '}')) {
                if (i + 1 < s.Length && s[i + 1] == c) { sb.Append(c).Append(c); i += 2; continue; }
                if (c == '}') return false;
                // A hole: code (its own literals, comments and whitespace normalized as code), then an optional format up to '}'.
                sb.Append('{');
                i++;
                bool space = false, line = false;
                if (!Code(s, ref i, sb, ref space, ref line, true)) return false;
                if (i >= s.Length) return false;
                if (s[i] == ':') {
                    while (i < s.Length && s[i] != '}') {
                        if (s[i] == '"' || s[i] == '\n' || s[i] == '\r') return false;
                        sb.Append(s[i]);
                        i++;
                    }
                    if (i >= s.Length) return false;
                }
                sb.Append('}');
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return false;
    }
}
'@
}
$script:BenchCodeHashes = @{}
function Get-BenchFileHash($file) {
    $id = "$($file.FullName)|$($file.Length)|$($file.LastWriteTimeUtc.Ticks)"
    if (-not $script:BenchCodeHashes.ContainsKey($id)) {
        $code = [BdCodeText]::Normalize([IO.File]::ReadAllText($file.FullName))
        $script:BenchCodeHashes[$id] = if ($null -eq $code) { (Get-FileHash $file.FullName -Algorithm SHA256).Hash } else {
            $sha = [Security.Cryptography.SHA256]::Create()
            "code:" + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($code))) -replace "-", "")
        }
    }
    $script:BenchCodeHashes[$id]
}

# -Plain: no test mod loaded (parcoop.ps1 -Plain), whatever BDTEST_MODS says.
$script:BaselineSalts = @{}
function Get-BaselineSalt([string]$gameDir, [switch]$Plain) {
    $id = "$(Get-CopyKey $gameDir)|$($Plain.IsPresent)"
    if (-not $script:BaselineSalts.ContainsKey($id)) {
        $profileSaves = Join-Path (Get-SteamAccountDir) "modded\profile3\saves"
        # Every file of the test mods' Workshop folders, not only DLLs: a mod's own data and settings change the game too (Unlimited
        # 2.0.0's sts2unlimited.settings.json: its difficulty scaling).
        $files = @(Get-ChildItem (Join-Path $gameDir "data_sts2_windows_x86_64\sts2.dll")) + @(if (-not $Plain) { Get-TestModItems | ForEach-Object { Get-ChildItem $_.FullName -Recurse -File } }) +
            @(Get-ChildItem (Join-Path $UserData "bigdeck") -Filter *.run -ErrorAction SilentlyContinue) +
            @(Get-ChildItem (Join-Path $profileSaves "*") -File -Include progress.save, prefs.save -ErrorAction SilentlyContinue) +
            @(Get-ChildItem (Join-Path $UserData "mod_configs") -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne "BigDeckOptimization.cfg" }) +
            @(Get-ChildItem (Join-Path $UserData "mod_data") -Recurse -File -ErrorAction SilentlyContinue)
        $mods = if ($Plain) { "none" } else { @(Get-TestModIds) -join "," }
        $script:BaselineSalts[$id] = "ref=vanilla1|no-steam|mods=$mods|" + (($files | Sort-Object FullName | ForEach-Object { "$($_.Name)=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join "")
    }
    $script:BaselineSalts[$id]
}

# The Bench part of a key: the files the script text runs (Get-BaselineBenchFiles), each with its hash.
function Get-BaselineBench([string]$scriptText) {
    (@(Get-BaselineBenchFiles $scriptText) | Sort-Object Name | ForEach-Object { "$($_.Name)=" + (Get-BenchFileHash $_) }) -join ""
}

function Get-BaselineKey([string]$gameDir, [string]$scriptPath) {
    $salt = Get-BaselineSalt $gameDir
    $scriptText = [IO.File]::ReadAllText($scriptPath)
    $bench = Get-BaselineBench $scriptText
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes($salt + "|bench=" + $bench + "|" + $scriptText)
    ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace "-", "").Substring(0, 16)
}

# A co-op vanilla pair's key (parcoop.ps1 -OnOff's -autooff twins, both peers launched with --bdvanilla): the solo key's parts
# (the game, the test mods unless -Plain, run files, profile 3, mod_configs, mod_data), the co-op profiles the peers play
# (default\1 and default\1001: their saves and mod data, which Sync-Slot -Coop mirrors), CoopModeTag.cs's code (a patch a
# vanilla peer installs: its "+off" join entry, which the twin's 'coopmodetag off' checks), the Bench files both scripts run
# and both scripts' text. JoinMismatchNote, the other patch a vanilla peer installs, only rewrites the text of the joining
# player's mod mismatch popup, which a twin's peers (both vanilla, the same mods) never show: not in the key.
function Get-CoopBaselineKey([string]$gameDir, [string]$hostScript, [string]$clientScript, [bool]$plain) {
    if ($null -eq $script:CoopProfilesSalt) {
        $files = foreach ($id in "1", "1001") {
            $dir = Join-Path $UserData "default\$id"
            if (Test-Path $dir) { Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -in "progress.save", "prefs.save", "profile.save", "settings.save" -or $_.FullName -match '\\mod_data\\' } }
        }
        $script:CoopProfilesSalt = (@($files) | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($UserData.Length))=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ""
    }
    $hostText = [IO.File]::ReadAllText($hostScript)
    $clientText = [IO.File]::ReadAllText($clientScript)
    $tag = Get-Item (Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\Patches\CoopModeTag.cs")
    $bench = Get-BaselineBench ($hostText + "`n" + $clientText)
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes((Get-BaselineSalt $gameDir -Plain:$plain) + "|coop-vanilla1|profiles=" + $script:CoopProfilesSalt + "|tag=" + (Get-BenchFileHash $tag) + "|bench=" + $bench + "|host=" + $hostText + "|client=" + $clientText)
    ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace "-", "").Substring(0, 16)
}

# A patched-off job (vanillacheck.ps1, par.ps1 / parsweep.ps1 -PatchedOff): every patch installed, then 'bdopt off', as mod-off
# sessions ran before --bdvanilla. Never launched vanilla, never a baseline (its log depends on the patches' code).
function Test-PatchedOffJob($job) { [bool]($job.PSObject.Properties["PatchedOff"] -and $job.PatchedOff) }

# A real mod-off session, launched with --bdvanilla (nothing of this mod installed but the test tooling and the co-op check) and
# kept as a baseline: its name says off and its script turns the whole mod off ('cmd bdopt off') before the run exists (newrun,
# mplobby, autoslay, continuerun) and before any other console command (that 'bdopt off' then changes nothing: the mod started
# off). A scenario named -off that turns one option off (hookskip-off, turns-32k-off, snapcheck-5k-a-off) runs this mod's code:
# launched as any other, never cached. Solo only: parcoop.ps1, coop.ps1 and coop4.ps1 launch their own sessions (--bdoff, every
# patch installed).
function Test-BaselineJob($job) {
    if (Test-PatchedOffJob $job) { return $false }
    if ($job.Name -notmatch '-off(\d*$|-)') { return $false }
    foreach ($line in [IO.File]::ReadAllLines($job.Script)) {
        $text = $line.Trim()
        if ($text -eq "cmd bdopt off") { return $true }
        if ($text -match '^(newrun|mplobby|autoslay|continuerun|cmd)\b') { return $false }
    }
    $false
}

# How a session was launched, as its log shows it: a vanilla one (Test-BaselineJob) must have the BDVANILLA line, any other must
# not (a mod-on side launched vanilla would compare the game with itself), and a patched-off one must show this mod's patches
# applied. Nothing, or the problem.
function Get-LaunchProblem($job, [string]$log) {
    if (-not (Test-Path $log)) { return }
    $vanilla = [bool](Select-String -Path $log -Pattern $VanillaLinePattern -Quiet)
    if (Test-BaselineJob $job) {
        if (-not $vanilla) { "launched with $VanillaArg, but its log has no BDVANILLA line: not a vanilla session (a Release build, an old Debug build?)" }
    }
    elseif ($vanilla) { "its log has the BDVANILLA line, but this job is not a vanilla one (a mod-on side, a patched-off one): it ran without the mod's patches" }
    elseif ((Test-PatchedOffJob $job) -and -not (Select-String -Path $log -Pattern '\[BigDeckOptimization\] Patch (ListenerCache|PileIndex|FastListeners) applied' -Quiet)) {
        "a patched-off session, but its log shows none of this mod's patches applied"
    }
}

# A session's problems, judge.py's judgment (one rule for every runner): a session cut short, a failed line (a failed console
# command included), the script's timeout, a sweep stopping, an error or a difference this mod found itself, the mod off from
# the start, a leak check finding a path or blind, a positive control ("expect") that saw nothing.
# -Mutated: a job on a game mutations.ps1 modified (Test-MutatedJob).
function Get-FailedLines([string]$log, [switch]$Mutated) {
    if (-not (Test-Path $log)) { return @("no log: $log was never written") }
    @(Invoke-Judge @($log) -Mutated:$Mutated | ForEach-Object { $_ -replace '^PROBLEM [^:]+: ', '' })
}

# A job on a game mutations.ps1 modified (its Mutated property): the parts the mutation leaves vanilla compare nothing and count no
# effect, which judge.py --mutated doesn't hold against it (mutations.ps1 checks them against its table).
function Test-MutatedJob($job) { [bool]($job.PSObject.Properties["Mutated"] -and $job.Mutated) }

# A co-op log's turn-start states (--bdturnstates: AutoTest.LogTurnStates, at CombatManager.TurnStarted), block by block in log
# order, each saying whether it shows the point every peer shares. An enemy-side block does: TurnStarted comes just before the
# game's "After enemy turn start" checksum, the action queue paused. A player-side block may not: CombatManager.StartTurn takes its
# "After player turn start" checksum, then unpauses the queue (ActionQueueSynchronizer.SetCombatState(PlayPhase) starts it at
# once) and only then raises TurnStarted, so another player's first action of the turn, already received by a slower peer, has
# run there before its STATE lines (coop4 on v0.113.1: an enemy's hp 197 on three peers, 198 on the one that had not run a
# PURITY yet, every game checksum equal). A player-side block is Synced when no "Executing action: " line (ActionExecutor) but a
# barrier's 'bigdeck mark' comes between the last "Combat state becomes PlayPhase" line (ActionQueueSynchronizer) and it; a log without those debug lines leaves
# every block Synced (compared strictly, as before). Returns a list of [pscustomobject]@{ Label = "floor F round R Side";
# Lines = the block's lines after "STATE "; LineNumbers; Synced }.
function Get-TurnStates([string]$log) {
    $blocks = [Collections.Generic.List[object]]::new()
    if (-not (Test-Path $log)) { return ,$blocks }
    $ran = $false
    $current = $null
    foreach ($match in Select-String -Path $log -CaseSensitive -Pattern 'Combat state becomes PlayPhase|Executing action: |STATE (floor \d+ round \d+ (\w+)): ') {
        $groups = $match.Matches[0].Groups
        if ($groups[1].Success) {
            if ($null -eq $current -or $current.Label -cne $groups[1].Value) {
                $current = [pscustomobject]@{ Label = $groups[1].Value; Lines = [Collections.Generic.List[string]]::new(); LineNumbers = [Collections.Generic.List[int]]::new(); Synced = ($groups[2].Value -cne "Player" -or -not $ran) }
                $blocks.Add($current)
            }
            $current.Lines.Add(($match.Line -creplace '^.*?STATE ', ''))
            $current.LineNumbers.Add($match.LineNumber)
            continue
        }
        # Anything between two STATE lines ends a block (two fights on one floor give the same labels again).
        $current = $null
        if ($match.Line.Contains("Combat state becomes PlayPhase")) { $ran = $false }
        # A barrier's mark (bigdeck mark, BigDeckConsoleCmd) changes no game state: the other player's barrier, not a play.
        elseif ($match.Line -cnotmatch 'Executing action: ConsoleCmdGameAction player \d+ cmd bigdeck mark ') { $ran = $true }
    }
    ,$blocks
}

# One co-op session's peers' turn-start states (Get-TurnStates, one list per peer, $names[0] the reference): the same blocks in
# the same order on every peer, and in each block the lines $keep selects equal on every peer, a player-side block on the peers
# that took it Synced only (at least two, otherwise it is not compared). A real divergence still shows: in the enemy-side blocks,
# in the player-side ones of the synced peers, and in the game's own checksums ("State divergence", which the runners check).
# Player-side blocks there and none compared (each one taken after another player's action on every peer but one at most): the
# player side was never checked, a problem, not a note. No co-op log of either supported version has it (2026-11-20: every pair
# and coop4 run compares at least one), so it says a scenario or a change of pace made that half of the check blind.
# Returns [pscustomobject]@{ Problems; Compared; Skipped (player-side blocks with fewer than two synced peers); Lines (the
# reference's selected lines); PlayerBlocks, PlayerCompared (the player-side ones among the blocks every peer has) }.
function Compare-PeerTurnStates([object[]]$states, [string[]]$names, [scriptblock]$keep) {
    $problems = [Collections.Generic.List[string]]::new()
    $compared = 0; $skipped = 0; $lines = 0; $playerBlocks = 0; $playerCompared = 0
    # Indexed loops only: Windows PowerShell 5.1 throws "argument types do not match" on @() around these lists.
    $labels = for ($p = 0; $p -lt $states.Count; $p++) { $list = $states[$p]; $text = for ($i = 0; $i -lt $list.Count; $i++) { $list[$i].Label }; $text -join "|" }
    for ($p = 1; $p -lt $states.Count; $p++) {
        if ($labels[$p] -cne $labels[0]) { $problems.Add("turn starts differ: $($names[0]) $($states[0].Count) blocks, $($names[$p]) $($states[$p].Count)") }
    }
    $selected = { param($block) ($block.Lines | Where-Object $keep) -join "`n" }
    $count = $states[0].Count
    for ($p = 0; $p -lt $states.Count; $p++) { $count = [Math]::Min($count, $states[$p].Count) }
    for ($i = 0; $i -lt $states[0].Count; $i++) { $lines += ($states[0][$i].Lines | Where-Object $keep | Measure-Object).Count }
    for ($i = 0; $i -lt $count; $i++) {
        $player = $states[0][$i].Label -cmatch ' Player$'
        if ($player) { $playerBlocks++ }
        $peers = for ($p = 0; $p -lt $states.Count; $p++) { if ($states[$p][$i].Synced) { $p } }
        $peers = @($peers)
        if ($peers.Count -lt 2) { $skipped++; continue }
        $compared++
        if ($player) { $playerCompared++ }
        $first = $peers[0]
        $expected = & $selected $states[$first][$i]
        foreach ($p in $peers[1..($peers.Count - 1)]) {
            $actual = & $selected $states[$p][$i]
            if ($actual -cne $expected -and $problems.Count -lt 4) {
                $problems.Add("states differ at turn start $($i + 1) ($($states[$first][$i].Label)): $($names[$first]) vs $($names[$p])")
            }
        }
    }
    if ($playerBlocks -gt 0 -and $playerCompared -eq 0) {
        $problems.Add("player side never compared: all $playerBlocks player turn starts taken after another player's action on every peer but one at most")
    }
    [pscustomobject]@{ Problems = $problems; Compared = $compared; Skipped = $skipped; Lines = $lines; PlayerBlocks = $playerBlocks; PlayerCompared = $playerCompared }
}

# The log line numbers of the player-side turn-start blocks to leave out of a comparison of two sessions (mod on vs off, peer by
# peer): those either side took after another player's action ran (Get-TurnStates' Synced), block by block in order. Returns
# @{ First = line numbers in the first log; Second = in the second; Blocks = how many; PlayerBlocks = the player-side blocks both
# logs have (up to where they part); Blind = some there and every one left out, which the callers count as a problem (as
# Compare-PeerTurnStates does: the player side was never compared) }.
function Get-UnsyncedTurnLines([string]$first, [string]$second) {
    $a = Get-TurnStates $first
    $b = Get-TurnStates $second
    $left = [Collections.Generic.HashSet[int]]::new()
    $right = [Collections.Generic.HashSet[int]]::new()
    $blocks = 0; $playerBlocks = 0
    for ($i = 0; $i -lt [Math]::Min($a.Count, $b.Count); $i++) {
        # Sessions gone different ways: nothing after is left out, the comparison shows where.
        if ($a[$i].Label -cne $b[$i].Label) { break }
        if ($a[$i].Label -cmatch ' Player$') { $playerBlocks++ }
        if ($a[$i].Synced -and $b[$i].Synced) { continue }
        $blocks++
        foreach ($n in $a[$i].LineNumbers) { [void]$left.Add($n) }
        foreach ($n in $b[$i].LineNumbers) { [void]$right.Add($n) }
    }
    [pscustomobject]@{ First = $left; Second = $right; Blocks = $blocks; PlayerBlocks = $playerBlocks; Blind = ($playerBlocks -gt 0 -and $blocks -ge $playerBlocks) }
}

# results/status.txt: progress and time left (from the items done when jobs have an Items count, else the sessions done),
# each running session's current line. Read with status.ps1.
function Write-SessionStatus([string]$label, [int]$total, [int]$finished, [int]$waiting, $running, [datetime]$started, $jobs, [string]$file = "status.txt") {
    $itemsTotal = 0
    foreach ($job in $jobs) { if ($job.PSObject.Properties["Items"]) { $itemsTotal += $job.Items } }
    $itemsDone = 0
    $rows = foreach ($slot in ($running.Keys | Sort-Object)) {
        $run = $running[$slot]
        $count = 0
        $current = ""
        if (Test-Path $run.Job.Log) {
            $hits = @(Select-String -Path $run.Job.Log -Pattern "SWEEP \w+ (\S+) \S+ wall|> \S+" -ErrorAction SilentlyContinue)
            if ($hits.Count) { $current = ($hits[-1].Line -replace '^.*AutoTest\s+', '') }
            $count = @($hits | Where-Object { $_.Matches[0].Groups[1].Success } | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Unique).Count
        }
        $itemsDone += $count
        $text = "{0,-26} {1,4:F0} s  {2}" -f $run.Job.Name, ((Get-Date) - $run.Started).TotalSeconds, $current
        $text.Substring(0, [Math]::Min(110, $text.Length))
    }
    $elapsed = ((Get-Date) - $started).TotalSeconds
    if ($itemsTotal -gt 0) {
        foreach ($job in $jobs) {
            $alreadyRunning = @($running.Values | Where-Object { $_.Job.Name -eq $job.Name }).Count
            $notStarted = ($job.Log -and -not (Test-Path $job.Log))
            if (-not $alreadyRunning -and -not $notStarted -and $job.PSObject.Properties["Items"]) { $itemsDone += $job.Items }
        }
        $share = [Math]::Min(1.0, $itemsDone / [double]$itemsTotal)
        $progress = "{0}/{1} items (both sides)" -f $itemsDone, $itemsTotal
    }
    else {
        $share = if ($total) { $finished / [double]$total } else { 0 }
        $progress = "{0}/{1} sessions" -f $finished, $total
    }
    $left = if ($share -gt 0.02) { "about {0:F0} min left" -f (($elapsed * (1 - $share) / $share) / 60) } else { "time left: not known yet" }
    $header = "{0}  {1}: {2}, {3} running, {4} waiting, {5:F0} min so far, {6}, {7:F1} GB free" -f (Get-Date -Format "HH:mm:ss"), $label, $progress, $running.Count, $waiting, ($elapsed / 60), $left, ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB)
    Set-Content (Join-Path $Results $file) (@($header) + @($rows)) -Encoding utf8 -ErrorAction SilentlyContinue
}

# A game copy that costs nothing: hard links to every file of $source. Replacing a file: delete its link, then write a new
# one; writing through a link would change the original. Same volume as the source.
function New-ThinCopy([string]$source, [string]$target) {
    $root = (Get-Item $source).FullName
    foreach ($item in Get-ChildItem $root -Recurse -Force) {
        $path = Join-Path $target $item.FullName.Substring($root.Length + 1)
        if ($item.PSIsContainer) { New-Item -ItemType Directory -Force $path | Out-Null }
        elseif (-not [BdHardLink]::CreateHardLink($path, $item.FullName, [IntPtr]::Zero)) {
            # A file left out would go unseen until its session misbehaves.
            throw "New-ThinCopy: no hard link $path to $($item.FullName) (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
        }
    }
}
if (-not ("BdHardLink" -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class BdHardLink { [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool CreateHardLink(string link, string existing, IntPtr security); }'
}

# judge.py's positive control that saw nothing: not a problem of a session cut short (crashed or frozen) before the line it waits
# for. The job's new session is held to it.
$CutShortExpect = '^expected ".*" never seen \(a positive control that saw nothing\)$'

# Why a session that ended before its script's end is a problem, or nothing when it is the known benign end: the game gone right
# after a texture load outside the cache (Godot's dummy renderer, "[WARN] Asset not cached" as the log's last line), or before
# the script's first line. Either way with no problem in what it logged (judge.py, no end expected).
function Get-EarlyExitProblem([string]$log, $exitCode, [switch]$Mutated) {
    if (-not (Test-Path $log)) { return }
    $problems = @(Invoke-Judge @($log) -NoEnd -Mutated:$Mutated | ForEach-Object { $_ -replace '^PROBLEM [^:]+: ', '' } | Where-Object { $_ -notmatch $CutShortExpect })
    $last = @(Get-Content $log -Tail 6 | Where-Object { $_.Trim() }) | Select-Object -Last 1
    $started = [bool](Select-String -Path $log -Pattern "AutoTest\s+[\d.]+s > " -Quiet)
    if ($problems.Count) { return "ended early (exit code $exitCode, log $(Split-Path $log -Leaf)) with a problem: $($problems[0])" }
    if (-not $started -or $last -match '^\[WARN\] Asset not cached: ') { return }
    # The game's own loader stopped at the menu (AutoTest's WaitCommonPreload): started over, nothing of the mod involved.
    if (Select-String -Path $log -SimpleMatch "RESTART: the game's Common preload stopped loading" -Quiet) { return }
    "ended early (exit code $exitCode), not the known renderer crash: last line '$("$last".Substring(0, [Math]::Min(120, "$last".Length)))' ($(Split-Path $log -Leaf))"
}

# Why a session that froze is a problem of its job, or nothing. The game's own: its known freeze (Parameter "t" is null, which
# Set-CopyOverrides removes), or one before or after this mod's load work. This mod's: a freeze inside that work (the mod initialized,
# its patches not applied yet: ModEntry's applying over frames, InlineGuard, HarmonyRebuild), or a problem judge.py finds in what
# the session logged. A new session that gets past it would otherwise hide a freeze that comes one time in three.
function Get-FrozenProblem([string]$log, [switch]$Mutated) {
    if (-not (Test-Path $log)) { return }
    $leaf = Split-Path $log -Leaf
    # Cut short: its positive controls ('expect' lines still to come) can't be held against it, the other problems can.
    $problems = @(Invoke-Judge @($log) -NoEnd -Mutated:$Mutated | ForEach-Object { $_ -replace '^PROBLEM [^:]+: ', '' } | Where-Object { $_ -notmatch $CutShortExpect })
    if ($problems.Count) { return "froze (log $leaf) with a problem: $($problems[0])" }
    if (Get-Content $log -Tail 4 -ErrorAction SilentlyContinue | Select-String -SimpleMatch 'Parameter "t" is null') { return }
    $initialized = @(Select-String -Path $log -Pattern "\[BigDeckOptimization\] Initialized v" | Where-Object { $_.Line -notmatch "turned off" }).Count -gt 0
    if ($initialized -and -not (Select-String -Path $log -SimpleMatch "[BigDeckOptimization] Patches applied" -Quiet)) { return "froze while this mod applied its patches (no 'Patches applied' line, log $leaf)" }
}

# How long a kind of session took last time, to start the longest first (Invoke-Sessions): results\<version>\durations.tsv, one
# line per key (Get-DurationKey: the job's name, its script's speed line, rendered or not), its seconds and when, written at each
# session's end (done or timed out) by every series of that version under one lock. Only the order depends on it, never a result.
$DurationsFileName = "durations.tsv"
function Get-DurationKey($job, [bool]$rendered) {
    $speed = "default"
    if ($job.Script -and (Test-Path $job.Script)) {
        $m = Select-String -Path $job.Script -Pattern '^\s*speed\s+(\w+)' | Select-Object -First 1
        if ($m) { $speed = $m.Matches[0].Groups[1].Value }
    }
    "$($job.Name)|$speed$(if ($rendered) { '|rendered' })"
}

function Read-Durations([string]$file) {
    $map = @{}
    if (-not (Test-Path $file)) { return $map }
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        $parts = $line -split "`t"
        if ($parts.Count -ge 2 -and $parts[1] -match '^\d+$') { $map[$parts[0]] = [int]$parts[1] }
    }
    $map
}

# Merges $new (key -> seconds) into the file, under a lock (every series of a version writes it).
function Save-Durations([string]$file, [hashtable]$new) {
    if (-not $new.Count) { return }
    $mutex = [Threading.Mutex]::new($false, "Local\BigDeckOptimization-durations")
    try { if (-not $mutex.WaitOne(30000)) { $mutex.Dispose(); return } }
    catch [Threading.AbandonedMutexException] { }
    try {
        $when = Get-Date -Format "yyyy-MM-dd HH:mm"
        $lines = [Collections.Generic.List[string]]::new()
        if (Test-Path $file) { foreach ($line in [IO.File]::ReadAllLines($file)) { if ($line -and -not $new.ContainsKey(($line -split "`t")[0])) { $lines.Add($line) } } }
        foreach ($key in $new.Keys) { $lines.Add("$key`t$([int]$new[$key])`t$when") }
        $temp = "$file.$PID.tmp"
        [IO.File]::WriteAllLines($temp, [string[]]@($lines | Sort-Object))
        Move-Item $temp $file -Force
    }
    catch { Write-Host "note: session durations not saved ($($_.Exception.Message))" }
    finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
}

# A session's length from its log, when that log ran this same script: its last AutoTest time plus the game's start (~15 s).
function Get-LogDuration([string]$log, [string]$script) {
    if (-not $log -or -not $script -or -not (Test-Path $log)) { return $null }
    if (-not (Select-String -Path $log -SimpleMatch "script $script (" -Quiet)) { return $null }
    $last = @(Get-Content $log -Tail 400 -ErrorAction SilentlyContinue | Select-String -Pattern 'AutoTest\s+([\d.]+)s ') | Select-Object -Last 1
    if (-not $last) { return $null }
    [Math]::Round([double]$last.Matches[0].Groups[1].Value + 15)
}

# The logs of jobs that already ran (Name, Script, Log) into the durations file, for keys it has none of: parsweep.ps1's parts,
# whose folder is emptied before the next run makes them again.
function Save-LogDurations([string]$gameDir, $jobs) {
    $file = Join-Path (Get-VersionDir $gameDir) $DurationsFileName
    $known = Read-Durations $file
    $new = @{}
    foreach ($job in $jobs) {
        $key = Get-DurationKey $job $false
        if ($known.ContainsKey($key)) { continue }
        $seconds = Get-LogDuration $job.Log $job.Script
        if ($null -ne $seconds) { $new[$key] = $seconds }
    }
    Save-Durations $file $new
}

# The whole script's 'timeout <s>' line (AutoTest's own limit), or 0.
function Get-ScenarioTimeout([string]$script) {
    if (-not $script -or -not (Test-Path $script)) { return 0 }
    $m = Select-String -Path $script -Pattern '^\s*timeout\s+(\d+)\s*$' | Select-Object -First 1
    if ($m) { [int]$m.Matches[0].Groups[1].Value } else { 0 }
}

# Series side by side (par on both copies, the sweeps' kinds, mutations) share the solo slots ($SlotPool). Each one running
# Invoke-Sessions holds a file open in test-slots\queue saying how long its next session should take, while it could start one (a
# free slot, its -Jobs not reached): a series finding a free slot leaves it to another whose next session is longer (equal: the
# file name first), so the longest sessions of every series start first, as within one series. A file not held open is a series
# that ended (deleted on the way); one not rewritten for 15 s is ignored (a series busy elsewhere never holds the others up).
# Unknown lengths count as the longest ($UnknownLength): a new scenario or chunk starts early, as before this order existed.
$UnknownLength = 1e9
function Get-OtherSeriesHeads([string]$queueDir, [string]$mine) {
    foreach ($file in @(Get-ChildItem $queueDir -Filter *.queue -File -ErrorAction SilentlyContinue)) {
        if ($file.Name -eq $mine) { continue }
        try { Remove-Item $file.FullName -Force -ErrorAction Stop; continue } catch { }
        $text = $null
        try {
            $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
            try { $text = (New-Object IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
        }
        catch { continue }
        $parts = "$text".Trim() -split ' '
        if ($parts.Count -ne 2 -or $parts[0] -eq 'none' -or $parts[1] -notmatch '^\d+$') { continue }
        if (([datetime]::UtcNow.Ticks - [long]$parts[1]) -gt 150000000) { continue }
        [pscustomobject]@{ Name = $file.Name; Length = [double]$parts[0] }
    }
}

# Jobs: objects with Name, Script (scenario path), Log. Runs at most $MaxJobs at once, in the solo slots every series shares
# ($SlotPool): the longest first (durations.tsv, else the job's last log when it ran this script; unknown ones first, in the order
# given), and a free slot goes to whichever series has the longest session waiting (Get-OtherSeriesHeads). A slot this series
# isn't using goes back at once. A session is closed when it passes $TimeoutSec (-ScenarioTimeouts: or its script's own 'timeout'
# line plus 120 s, whichever is longer), or when its log stays silent for $StallSec. Returns one result per job: Name, State (done,
# cached, TIMEOUT, STALLED, FROZEN, CRASHED), Seconds, Failed (its problems: judge.py's, a test mod not loaded, an earlier session
# that ended early or froze in this mod's code), Restarts. -OnFinish { param($job, $result) ... }: called once per job with its
# final result (not for a session that starts over); the jobs it returns are queued at once, ahead of the others (parsweep.ps1's
# continuation of a chunk an item stopped: it starts as soon as a slot frees, not after every session of a round), and their
# results returned too. -StatusFile: the file in results/ the progress goes to (status.ps1 shows every status*.txt).
function Invoke-Sessions($Jobs, [string]$GameDir, [int]$MaxJobs = 8, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Rendered, [switch]$UseBaselines, [string]$Label = "sessions", [scriptblock]$OnFinish = $null, [string]$StatusFile = "status.txt", [switch]$ScenarioTimeouts) {
    $launchMode = if ($Rendered) { @("--bdhidden", "--windowed", "--resolution", "1920x1080") } else { @("--headless") }
    Add-TestMods $GameDir
    # In order; the jobs OnFinish returns go first.
    $pending = [Collections.Generic.List[object]]::new()
    $done = [Collections.Generic.List[object]]::new()
    $allJobs = [Collections.Generic.List[object]]::new()
    foreach ($job in $Jobs) { $allJobs.Add($job) }
    $baselineDir = Join-Path $BaselineRoot (Get-GameTag $GameDir)
    $durationFile = Join-Path (Get-VersionDir $GameDir) $DurationsFileName
    $durations = Read-Durations $durationFile
    # A job's expected seconds (kept on the job), $UnknownLength when nothing says.
    $lengthOf = {
        param($job)
        if (-not $job.PSObject.Properties["ExpectedSec"]) {
            $key = Get-DurationKey $job $Rendered.IsPresent
            $seconds = if ($durations.ContainsKey($key)) { $durations[$key] } else { Get-LogDuration $job.Log $job.Script }
            $job | Add-Member -NotePropertyName ExpectedSec -NotePropertyValue $(if ($null -ne $seconds) { [double]$seconds } else { $UnknownLength }) -Force
        }
        $job.ExpectedSec
    }
    # Queues a job (-First: ahead of the others), unless a cached mod-off log replaces its session. These two are child scopes: they
    # change the lists, never assign a variable of this function.
    # A queued job's log from an earlier series is deleted now, not at its launch: a series cut short (a session it could not start,
    # this function stopped by an error) would leave the runner comparing those old logs as this run's (its length, for the order,
    # is read first).
    $enqueue = {
        param($newJob, [bool]$first)
        if ($UseBaselines -and (Test-BaselineJob $newJob)) {
            $cached = Join-Path $baselineDir "$($newJob.Name)-$(Get-BaselineKey $GameDir $newJob.Script).log"
            if (Test-Path $cached) {
                Copy-Item $cached $newJob.Log -Force
                & $finish $newJob ([pscustomobject]@{ Name = $newJob.Name; State = "cached"; Seconds = 0; Failed = @(Get-FailedLines $newJob.Log -Mutated:(Test-MutatedJob $newJob)) + @(Get-LaunchProblem $newJob $newJob.Log) })
                return
            }
        }
        [void](& $lengthOf $newJob)
        Remove-Item $newJob.Log -Force -ErrorAction SilentlyContinue
        if ($first) { $pending.Insert(0, $newJob) } else { $pending.Add($newJob) }
    }
    # A job's final result: kept, then handed to OnFinish, whose jobs are queued first, in the order it returns them.
    $finish = {
        param($finishedJob, $result)
        $done.Add($result)
        if (-not $OnFinish) { return }
        $added = @(& $OnFinish $finishedJob $result | Where-Object { $_ })
        foreach ($newJob in $added) { $allJobs.Add($newJob) }
        for ($k = $added.Count - 1; $k -ge 0; $k--) { & $enqueue $added[$k] $true }
    }
    # The longest first; jobs nothing is known of first of all ($UnknownLength), in the order given (the callers put the mod-off
    # sides first). Sorted on the length then the given place: Windows PowerShell's Sort-Object isn't stable.
    $index = 0
    $ranked = @(foreach ($job in $Jobs) { [pscustomobject]@{ Job = $job; Length = (& $lengthOf $job); Index = $index++ } })
    $ordered = @($ranked | Sort-Object -Property @{ Expression = { $_.Length }; Descending = $true }, @{ Expression = { $_.Index }; Descending = $false } | ForEach-Object { $_.Job })
    foreach ($job in $ordered) { & $enqueue $job $false }
    $running = @{}
    $retries = @{}
    # Sessions that ended early, not in the known renderer crash: problems of their job, whatever the session that started over did.
    $earlyProblems = @{}
    $realAppData = $env:APPDATA
    $started = Get-Date
    $lastStatus = [datetime]::MinValue
    # The slots are shared by every series, whatever the game copy: a slot another series holds (Lock-Slot) is skipped until free.
    $locked = [Collections.Generic.HashSet[int]]::new()
    # This series' head in the shared queue (Get-OtherSeriesHeads).
    $queueDir = Join-Path $SlotRoot "queue"
    New-Item -ItemType Directory -Force $queueDir | Out-Null
    $queueName = "{0}-{1}.queue" -f $PID, [guid]::NewGuid().ToString('N')
    $queueStream = [IO.File]::Open((Join-Path $queueDir $queueName), 'CreateNew', 'ReadWrite', 'Read')
    $advertise = {
        param([string]$length)
        $bytes = [Text.Encoding]::ASCII.GetBytes("$length $([datetime]::UtcNow.Ticks)")
        $queueStream.SetLength(0)
        $queueStream.Write($bytes, 0, $bytes.Length)
        $queueStream.Flush()
    }
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            $canStart = $pending.Count -gt 0 -and $running.Count -lt $MaxJobs
            & $advertise $(if ($canStart) { [string](& $lengthOf $pending[0]) } else { "none" })
            $others = $null
            # The slots this series holds idle first (a session that ended or starts over), then the others of the pool.
            $candidates = @(@($locked | Where-Object { -not $running.ContainsKey($_) } | Sort-Object) + @(0..($SlotPool - 1) | Where-Object { -not $locked.Contains($_) }))
            foreach ($slot in $candidates) {
                if ($running.Count -ge $MaxJobs -or $pending.Count -eq 0) { break }
                if (-not $locked.Contains($slot)) {
                    if (-not (Lock-Slot $slot -Try)) { continue }
                    [void]$locked.Add($slot)
                }
                # Another series' next session is longer: this slot is its (released below, unused).
                if ($null -eq $others) { $others = @(Get-OtherSeriesHeads $queueDir $queueName) }
                $head = & $lengthOf $pending[0]
                if (@($others | Where-Object { $_.Length -gt $head -or ($_.Length -eq $head -and [string]::CompareOrdinal($_.Name, $queueName) -lt 0) }).Count) { break }
                # Each session takes 1-2 GB: the PC stays usable, the next one waits. Asked only with a slot in hand (every series polls).
                if ($running.Count -gt 0 -and (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt $MinFreeKB) { break }
                $job = $pending[0]
                $pending.RemoveAt(0)
                Remove-Item $job.Log -ErrorAction SilentlyContinue
                # A vanilla job's baseline key from the tree as this session reads it (its script, the Bench files it runs), kept
                # on the run and saved under at its end: computed then, an edit or a branch switch during the session would file
                # its log under a key it never ran.
                $baselineKey = if (Test-BaselineJob $job) { Get-BaselineKey $GameDir $job.Script } else { $null }
                $env:APPDATA = Sync-Slot $slot
                # A job may name its own game folder (mutations.ps1).
                $jobDir = if ($job.PSObject.Properties["GameDir"]) { $job.GameDir } else { $GameDir }
                $launch = @("--force-steam", "off", "--log-file", "`"$($job.Log)`"", "--bdtest", "`"$($job.Script)`"", "--bdmute") + $launchMode + @(Get-ScenarioLaunchArgs $job.Script)
                # A solo mod-off session is a vanilla one: the game as a player who started with the mod off has it.
                if (Test-BaselineJob $job) { $launch += $VanillaArg }
                $jobExe = Join-Path $jobDir "SlayTheSpire2.exe"
                # A launch that fails stops the series (-ErrorAction Stop: the runner's trap, exit code 1): left non-terminating, it
                # gave a session with no process, which failed only at its timeout, on a binding error that ended the series too.
                $p = if ($Rendered) { Start-HiddenGame $jobExe $jobDir $launch } else { Start-Process -FilePath $jobExe -WorkingDirectory $jobDir -PassThru -WindowStyle Hidden -ArgumentList $launch -ErrorAction Stop }
                $env:APPDATA = $realAppData
                [void]$p.Handle # or its exit code is lost
                $limit = if ($ScenarioTimeouts) { [Math]::Max($TimeoutSec, (Get-ScenarioTimeout $job.Script) + 120) } else { $TimeoutSec }
                $running[$slot] = [pscustomobject]@{ Job = $job; Process = $p; Started = Get-Date; ScriptStarted = $false; TimeoutSec = $limit; BaselineKey = $baselineKey }
                & $advertise $(if ($pending.Count -gt 0 -and $running.Count -lt $MaxJobs) { [string](& $lengthOf $pending[0]) } else { "none" })
            }
            # A slot this series holds and doesn't use goes back to the other series at once (a sweep's long mod-off tail, a slot left
            # to a longer session of another series). A session starting over is queued while its slot is still held: it takes it
            # first at the next pass.
            foreach ($slot in @($locked)) { if (-not $running.ContainsKey($slot)) { Unlock-Slot $slot; [void]$locked.Remove($slot) } }
            if ($running.Count -eq 0 -and $pending.Count -gt 0 -and -not $toldBusy) { $toldBusy = $true; Write-Host "every test slot 0-$($SlotPool - 1) is held by another series, or left to its longer sessions: waiting for one" }
            Start-Sleep -Milliseconds 300
            if (((Get-Date) - $lastStatus).TotalSeconds -ge 5) {
                Write-SessionStatus $Label ($done.Count + $pending.Count + $running.Count) $done.Count $pending.Count $running $started $allJobs $StatusFile
                $lastStatus = Get-Date
            }
            foreach ($slot in @($running.Keys)) {
                $run = $running[$slot]
                $state = $null
                $age = ((Get-Date) - $run.Started).TotalSeconds
                # The freeze Set-CopyOverrides removes (log: Parameter "t" is null, then silence) should not come back, nor any other at
                # that point; if one does, the session can only start over. Or it never reached the script's first line.
                $frozen = $false
                if (-not $run.Process.HasExited -and $age -gt 20 -and (Test-Path $run.Job.Log)) {
                    if (((Get-Date) - (Get-Item $run.Job.Log).LastWriteTime).TotalSeconds -gt $FreezeSilenceSec) {
                        $frozen = [bool](Get-Content $run.Job.Log -Tail 4 -ErrorAction SilentlyContinue | Select-String -SimpleMatch 'Parameter "t" is null')
                    }
                    if (-not $frozen -and -not $run.ScriptStarted -and $age -gt $StartLimitSec) {
                        $run.ScriptStarted = [bool](Select-String -Path $run.Job.Log -Pattern "AutoTest\s+[\d.]+s > " -Quiet)
                        $frozen = -not $run.ScriptStarted
                    }
                }
                if ($frozen) {
                    $name = $run.Job.Name
                    $retries[$name] = 1 + [int]$retries[$name]
                    Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep 1
                    # Kept beside the job's log (the next session deletes that one) and judged: a freeze in this mod's code is a
                    # problem of the job, whatever the session that starts over does.
                    $frozenLog = "$($run.Job.Log).frozen$(if ($retries[$name] -gt 1) { $retries[$name] })"
                    Copy-Item $run.Job.Log $frozenLog -Force -ErrorAction SilentlyContinue
                    $why = Get-FrozenProblem $frozenLog -Mutated:(Test-MutatedJob $run.Job)
                    if ($why) { $earlyProblems[$name] = @($earlyProblems[$name]) + @($why) }
                    $running.Remove($slot)
                    # First again: it was started early for being long.
                    if ($retries[$name] -le 2) { $pending.Insert(0, $run.Job) }
                    else { & $finish $run.Job ([pscustomobject]@{ Name = $name; State = "FROZEN"; Seconds = [int]$age; Failed = @("froze 3 times") + @($earlyProblems[$name] | Where-Object { $_ }); Restarts = $retries[$name] }) }
                    continue
                }
                if ($run.Process.HasExited) {
                    $state = "done"
                    # Gone before the script's end, without a word: a native crash, rare on Godot's dummy renderer (seen after a texture
                    # load outside the cache). Its log compares as a shorter session: it starts over, the first log kept beside it.
                    # Also gone before its script's first line: the session ran nothing (seen once at a launch, 8 s in, an 8,400-card A/B then compared nothing).
                    # Any other early end (this mod's code crashing the game, one time in three) is a problem even if a new session passes.
                    # No log at all: the game ended before its first line (a launch that failed, a log path it could not create; the game
                    # writes it before any mod loads): it ran nothing, started over the same way, and its reason said if it stays so.
                    # (Select-String on a missing file throws whatever -ErrorAction says: it once ended the whole series.)
                    $hasLog = Test-Path $run.Job.Log
                    if (-not $hasLog -or -not (Select-String -Path $run.Job.Log -Pattern "AutoTest\s+[\d.]+s (> quit|script finished|quitting)|ABORT|TIMEOUT: script|\[AutoSlay\] Action: Quitting" -Quiet -ErrorAction SilentlyContinue)) {
                        $name = $run.Job.Name
                        $retries[$name] = 1 + [int]$retries[$name]
                        if ($hasLog) {
                            $early = "$($run.Job.Log).early$(if ($retries[$name] -gt 1) { $retries[$name] })"
                            Copy-Item $run.Job.Log $early -Force -ErrorAction SilentlyContinue
                            $why = Get-EarlyExitProblem $early $run.Process.ExitCode -Mutated:(Test-MutatedJob $run.Job)
                            if ($why) { $earlyProblems[$name] = @($earlyProblems[$name]) + @($why) }
                        }
                        Start-Sleep 1
                        $running.Remove($slot)
                        if ($retries[$name] -le 2) { $pending.Insert(0, $run.Job) }
                        else {
                            $noLog = if (-not $hasLog) { "no log: the game ended (exit code $($run.Process.ExitCode)) without writing $($run.Job.Log)" }
                            & $finish $run.Job ([pscustomobject]@{ Name = $name; State = "CRASHED"; Seconds = [int]$age; Failed = @("stopped early 3 times, exit code $($run.Process.ExitCode)") + @($noLog | Where-Object { $_ }) + @($earlyProblems[$name] | Where-Object { $_ }); Restarts = $retries[$name] })
                        }
                        continue
                    }
                }
                elseif ($age -gt $run.TimeoutSec) { $state = "TIMEOUT" }
                elseif ($age -gt 60 -and (Test-Path $run.Job.Log) -and ((Get-Date) - (Get-Item $run.Job.Log).LastWriteTime).TotalSeconds -gt $StallSec) { $state = "STALLED" }
                if (-not $state) { continue }
                if ($state -ne "done") { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
                # Its length, for the next series' order (a timed-out one took at least that).
                if ($state -in "done", "TIMEOUT") {
                    $key = Get-DurationKey $run.Job $Rendered.IsPresent
                    $durations[$key] = [int]$age
                    Save-Durations $durationFile @{ $key = [int]$age }
                }
                $failed = @(Get-FailedLines $run.Job.Log -Mutated:(Test-MutatedJob $run.Job)) + @(Get-MissingTestMods $run.Job.Log $GameDir) + @($earlyProblems[$run.Job.Name] | Where-Object { $_ }) + @(Get-LaunchProblem $run.Job $run.Job.Log)
                $running.Remove($slot)
                & $finish $run.Job ([pscustomobject]@{ Name = $run.Job.Name; State = $state; Seconds = [int]$age; Failed = $failed; Restarts = [int]$retries[$run.Job.Name] })
                # Kept as a baseline whether or not this series reads them: a run without -UseBaselines is the freshest mod-off
                # log there is, and a rerun after a fix can then reuse it. Never stops the series.
                if ($state -eq "done" -and $failed.Count -eq 0 -and $run.BaselineKey) {
                    try {
                        New-Item -ItemType Directory -Force $baselineDir | Out-Null
                        Copy-Item $run.Job.Log (Join-Path $baselineDir "$($run.Job.Name)-$($run.BaselineKey).log") -Force
                    }
                    catch { Write-Host "note: $($run.Job.Name) not kept as a baseline: $($_.Exception.Message)" }
                }
            }
        }
    }
    finally {
        $env:APPDATA = $realAppData
        foreach ($run in $running.Values) { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue }
        # Held while this series has sessions to start (each start mirrors the user data into its slot again), released when none is left.
        foreach ($slot in @($locked)) { Unlock-Slot $slot }
        $queueStream.Dispose()
        Remove-Item (Join-Path $queueDir $queueName) -Force -ErrorAction SilentlyContinue
    }
    $done
}
