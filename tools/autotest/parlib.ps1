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
# run.ps1 -KeepSlot: the slot of the sessions that follow each other and share their user data.
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
# the kept slot: this process, or another one).
function Restore-AsideMenu([string]$gameDir) {
    $menu = Join-Path $gameDir "mods\BigDeckOptimization\BigDeckOptimization.Menu.dll"
    $aside = "$menu.bdtest-aside"
    if (-not (Test-Path $aside) -or $global:BdSlotLocks.ContainsKey($KeptSlot) -or -not (Lock-Slot $KeptSlot -Try)) { return }
    try {
        # A menu DLL there was deployed since: the one set aside is older.
        if (Test-Path $menu) { Remove-Item $aside -Force } else { Move-Item $aside $menu }
        Write-Host "put back $menu, set aside by an interrupted started-off-nomenu.ps1"
    }
    finally { Unlock-Slot $KeptSlot }
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
function Test-SessionLog([string]$name, [string]$pattern, [bool]$expected, [string]$meaning) {
    $log = Join-Path $Results "$name.log"
    if (-not (Test-Path $log)) { $ok = $false; $meaning += " (no session log)" }
    else {
        $seen = [bool](Select-String -Path $log -Pattern $pattern -CaseSensitive | Where-Object { $_.Line -cnotmatch $ScriptOwnLine } | Select-Object -First 1)
        $ok = $seen -eq $expected
        if ($ok -and -not $expected -and -not (Select-String -Path $log -Pattern $SessionEnd -Quiet)) { $ok = $false; $meaning += " (the session did not reach its end: a missing line proves nothing)" }
    }
    if (-not $ok) { $script:SessionLogProblems = 1 + [int]$script:SessionLogProblems }
    Write-Output ("{0} {1}: {2}" -f $(if ($ok) { "ok     " } else { "PROBLEM" }), $name, $meaning)
}

# Everything a mod-off log depends on: a baseline stays valid until the game, a test mod's files, which mods are loaded
# (Get-TestModIds), the shared run files the scenarios import, the test profile's progress (what a run offers), the other mods'
# settings and data the slots copy (mod_configs; mod_data, e.g. LobbyBanlist's ban profiles), the test tooling the scenario
# runs or the scenario changes. Not this mod's patches: a mod-off session is launched with --bdvanilla and none of them is
# installed (vanillacheck.ps1 proves a patched-off session, every patch installed then bdopt off, logs the same lines).
# 'ref=vanilla1': the logs of patched-off sessions, kept before --bdvanilla, never serve as vanilla ones.
$script:BaselineSalt = $null
# The test tooling a script runs: the scenario engine and its helpers always, each optional part only when one of its commands is
# in the script (AutoTest.cs reaches them only through those commands; a few CPU-time helpers feed timings, which no comparison
# reads). A change elsewhere in Bench/ leaves this scenario's baseline valid. A console command counts through 'cmd' and through
# 'autoslayfirst' (the same Command() at the auto player's first room).
$BaselineBenchParts = [ordered]@{
    'AutoTestRooms.cs'      = '^\s*(autoroom|automap|barrier|continuerun|runstate|restsite|selector|eventoption)\b'
    'AutoTestMouse.cs'      = '^\s*(hover|checktips|mouseplay|mousesweep|movehovered|buttoninput|controllermap|forcefocus|tipsrebuilt)\b'
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
}
function Get-BaselineBenchFiles([string]$scriptText) {
    $bench = Join-Path $PSScriptRoot "..\..\src\BigDeckOptimization\Bench"
    foreach ($file in Get-ChildItem $bench -Filter *.cs) {
        if ($file.Name -eq 'AutoTestVideo.cs') { continue }  # local only: video scenarios, never cached
        $pattern = $BaselineBenchParts[$file.Name]
        # A file not in the table (the engine, its helpers, a new file) always counts: unknown is never left out.
        if (-not $pattern -or [regex]::IsMatch($scriptText, $pattern, 'Multiline')) { $file }
    }
}

function Get-BaselineKey([string]$gameDir, [string]$scriptPath) {
    if (-not $script:BaselineSalt) {
        $profileSaves = Join-Path (Get-SteamAccountDir) "modded\profile3\saves"
        # Every file of the test mods' Workshop folders, not only DLLs: a mod's own data and settings change the game too (Unlimited
        # 2.0.0's sts2unlimited.settings.json: its difficulty scaling).
        $files = @(Get-ChildItem (Join-Path $gameDir "data_sts2_windows_x86_64\sts2.dll")) + @(Get-TestModItems | ForEach-Object { Get-ChildItem $_.FullName -Recurse -File }) +
            @(Get-ChildItem (Join-Path $UserData "bigdeck") -Filter *.run -ErrorAction SilentlyContinue) +
            @(Get-ChildItem (Join-Path $profileSaves "*") -File -Include progress.save, prefs.save -ErrorAction SilentlyContinue) +
            @(Get-ChildItem (Join-Path $UserData "mod_configs") -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne "BigDeckOptimization.cfg" }) +
            @(Get-ChildItem (Join-Path $UserData "mod_data") -Recurse -File -ErrorAction SilentlyContinue)
        $script:BaselineSalt = "ref=vanilla1|no-steam|mods=" + (@(Get-TestModIds) -join ",") + "|" + (($files | Sort-Object FullName | ForEach-Object { "$($_.Name)=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join "")
    }
    $scriptText = [IO.File]::ReadAllText($scriptPath)
    $bench = (@(Get-BaselineBenchFiles $scriptText) | Sort-Object Name | ForEach-Object { "$($_.Name)=" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ""
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes($script:BaselineSalt + "|bench=" + $bench + "|" + $scriptText)
    ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace "-", "").Substring(0, 16)
}

# A patched-off job (vanillacheck.ps1, par.ps1 / parsweep.ps1 -PatchedOff): every patch installed, then 'bdopt off', as mod-off
# sessions ran before --bdvanilla. Never launched vanilla, never a baseline (its log depends on the patches' code).
function Test-PatchedOffJob($job) { [bool]($job.PSObject.Properties["PatchedOff"] -and $job.PatchedOff) }

# A real mod-off session, launched with --bdvanilla (nothing of this mod installed but the test tooling and the co-op check) and
# kept as a baseline: its name says off and its script turns the whole mod off ('cmd bdopt off') before the run exists (newrun,
# mplobby, autoslay, continuerun) and before any other console command (that 'bdopt off' then changes nothing: the mod started
# off). A scenario named -off that turns one option off (hookskip-off, turns-32k-off, snapcheck-5k-off) runs this mod's code:
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
    if (-not (Test-Path $log)) { return @("no log") }
    @(Invoke-Judge @($log) -Mutated:$Mutated | ForEach-Object { $_ -replace '^PROBLEM [^:]+: ', '' })
}

# A job on a game mutations.ps1 modified (its Mutated property): the parts the mutation leaves vanilla compare nothing and count no
# effect, which judge.py --mutated doesn't hold against it (mutations.ps1 checks them against its table).
function Test-MutatedJob($job) { [bool]($job.PSObject.Properties["Mutated"] -and $job.Mutated) }

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

# Jobs: objects with Name, Script (scenario path), Log. Runs at most $MaxJobs at once. A session is closed when it passes
# $TimeoutSec, or when its log stays silent for $StallSec. Returns one result per job: Name, State (done, cached, TIMEOUT,
# STALLED, FROZEN, CRASHED), Seconds, Failed (its problems: judge.py's, a test mod not loaded, an earlier session that ended early
# or froze in this mod's code), Restarts. -OnFinish { param($job, $result) ... }: called once per job with its final result (not
# for a session that starts over); the jobs it returns are queued at once, ahead of the others (parsweep.ps1's continuation of a
# chunk an item stopped: it starts as soon as a slot frees, not after every session of a round), and their results returned too.
# -StatusFile: the file in results/ the progress goes to (status.ps1 shows every status*.txt).
function Invoke-Sessions($Jobs, [string]$GameDir, [int]$MaxJobs = 8, [int]$TimeoutSec = 900, [int]$StallSec = 240, [switch]$Rendered, [switch]$UseBaselines, [string]$Label = "sessions", [scriptblock]$OnFinish = $null, [string]$StatusFile = "status.txt") {
    $launchMode = if ($Rendered) { @("--bdhidden", "--windowed", "--resolution", "1920x1080") } else { @("--headless") }
    Add-TestMods $GameDir
    # In order; the jobs OnFinish returns go first.
    $pending = [Collections.Generic.List[object]]::new()
    $done = [Collections.Generic.List[object]]::new()
    $allJobs = [Collections.Generic.List[object]]::new()
    foreach ($job in $Jobs) { $allJobs.Add($job) }
    $baselineDir = Join-Path $BaselineRoot (Get-GameTag $GameDir)
    # Queues a job (-First: ahead of the others), unless a cached mod-off log replaces its session. These two are child scopes: they
    # change the lists, never assign a variable of this function.
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
    foreach ($job in $Jobs) { & $enqueue $job $false }
    $running = @{}
    $retries = @{}
    # Sessions that ended early, not in the known renderer crash: problems of their job, whatever the session that started over did.
    $earlyProblems = @{}
    $realAppData = $env:APPDATA
    $started = Get-Date
    $lastStatus = [datetime]::MinValue
    # The slots are shared by every series, whatever the game copy: a slot another series holds (Lock-Slot) is skipped until free.
    $locked = [Collections.Generic.HashSet[int]]::new()
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            foreach ($slot in 0..($MaxJobs - 1)) {
                if ($running.ContainsKey($slot) -or $pending.Count -eq 0) { continue }
                # Each session takes 1-2 GB: the PC stays usable, the next one waits.
                if ($running.Count -gt 0 -and (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt $MinFreeKB) { break }
                if (-not $locked.Contains($slot)) {
                    if (-not (Lock-Slot $slot -Try)) { continue }
                    [void]$locked.Add($slot)
                }
                $job = $pending[0]
                $pending.RemoveAt(0)
                Remove-Item $job.Log -ErrorAction SilentlyContinue
                $env:APPDATA = Sync-Slot $slot
                # A job may name its own game folder (mutations.ps1).
                $jobDir = if ($job.PSObject.Properties["GameDir"]) { $job.GameDir } else { $GameDir }
                $launch = @("--force-steam", "off", "--log-file", "`"$($job.Log)`"", "--bdtest", "`"$($job.Script)`"", "--bdmute") + $launchMode + @(Get-ScenarioLaunchArgs $job.Script)
                # A solo mod-off session is a vanilla one: the game as a player who started with the mod off has it.
                if (Test-BaselineJob $job) { $launch += $VanillaArg }
                $jobExe = Join-Path $jobDir "SlayTheSpire2.exe"
                $p = if ($Rendered) { Start-HiddenGame $jobExe $jobDir $launch } else { Start-Process -FilePath $jobExe -WorkingDirectory $jobDir -PassThru -WindowStyle Hidden -ArgumentList $launch }
                $env:APPDATA = $realAppData
                [void]$p.Handle # or its exit code is lost
                $running[$slot] = [pscustomobject]@{ Job = $job; Process = $p; Started = Get-Date; ScriptStarted = $false }
            }
            if ($running.Count -eq 0 -and $locked.Count -eq 0 -and -not $toldBusy) { $toldBusy = $true; Write-Host "every test slot 0-$($MaxJobs - 1) is held by another series: waiting for one" }
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
                    if ($retries[$name] -le 2) { $pending.Add($run.Job) }
                    else { & $finish $run.Job ([pscustomobject]@{ Name = $name; State = "FROZEN"; Seconds = [int]$age; Failed = @("froze 3 times") + @($earlyProblems[$name] | Where-Object { $_ }); Restarts = $retries[$name] }) }
                    continue
                }
                if ($run.Process.HasExited) {
                    $state = "done"
                    # Gone before the script's end, without a word: a native crash, rare on Godot's dummy renderer (seen after a texture
                    # load outside the cache). Its log compares as a shorter session: it starts over, the first log kept beside it.
                    # Also gone before its script's first line: the session ran nothing (seen once at a launch, 8 s in, an 8,400-card A/B then compared nothing).
                    # Any other early end (this mod's code crashing the game, one time in three) is a problem even if a new session passes.
                    if (-not (Select-String -Path $run.Job.Log -Pattern "AutoTest\s+[\d.]+s (> quit|script finished|quitting)|ABORT|TIMEOUT: script|\[AutoSlay\] Action: Quitting" -Quiet -ErrorAction SilentlyContinue)) {
                        $name = $run.Job.Name
                        $retries[$name] = 1 + [int]$retries[$name]
                        $early = "$($run.Job.Log).early$(if ($retries[$name] -gt 1) { $retries[$name] })"
                        Copy-Item $run.Job.Log $early -Force -ErrorAction SilentlyContinue
                        $why = Get-EarlyExitProblem $early $run.Process.ExitCode -Mutated:(Test-MutatedJob $run.Job)
                        if ($why) { $earlyProblems[$name] = @($earlyProblems[$name]) + @($why) }
                        Start-Sleep 1
                        $running.Remove($slot)
                        if ($retries[$name] -le 2) { $pending.Add($run.Job) }
                        else { & $finish $run.Job ([pscustomobject]@{ Name = $name; State = "CRASHED"; Seconds = [int]$age; Failed = @("stopped early 3 times, exit code $($run.Process.ExitCode)") + @($earlyProblems[$name] | Where-Object { $_ }); Restarts = $retries[$name] }) }
                        continue
                    }
                }
                elseif ($age -gt $TimeoutSec) { $state = "TIMEOUT" }
                elseif ($age -gt 60 -and (Test-Path $run.Job.Log) -and ((Get-Date) - (Get-Item $run.Job.Log).LastWriteTime).TotalSeconds -gt $StallSec) { $state = "STALLED" }
                if (-not $state) { continue }
                if ($state -ne "done") { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
                $failed = @(Get-FailedLines $run.Job.Log -Mutated:(Test-MutatedJob $run.Job)) + @(Get-MissingTestMods $run.Job.Log $GameDir) + @($earlyProblems[$run.Job.Name] | Where-Object { $_ }) + @(Get-LaunchProblem $run.Job $run.Job.Log)
                $running.Remove($slot)
                & $finish $run.Job ([pscustomobject]@{ Name = $run.Job.Name; State = $state; Seconds = [int]$age; Failed = $failed; Restarts = [int]$retries[$run.Job.Name] })
                # Kept as a baseline whether or not this series reads them: a run without -UseBaselines is the freshest mod-off
                # log there is, and a rerun after a fix can then reuse it. Never stops the series.
                if ($state -eq "done" -and $failed.Count -eq 0 -and (Test-BaselineJob $run.Job)) {
                    try {
                        New-Item -ItemType Directory -Force $baselineDir | Out-Null
                        Copy-Item $run.Job.Log (Join-Path $baselineDir "$($run.Job.Name)-$(Get-BaselineKey $GameDir $run.Job.Script).log") -Force
                    }
                    catch { Write-Host "note: $($run.Job.Name) not kept as a baseline: $($_.Exception.Message)" }
                }
            }
            # Nothing left to start: a slot whose session ended goes back to the other series (a sweep's long mod-off tail would
            # otherwise hold idle slots). A session starting over is queued before this, while its slot is still held.
            if ($pending.Count -eq 0) {
                foreach ($slot in @($locked)) { if (-not $running.ContainsKey($slot)) { Unlock-Slot $slot; [void]$locked.Remove($slot) } }
            }
        }
    }
    finally {
        $env:APPDATA = $realAppData
        foreach ($run in $running.Values) { Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue }
        # Held while this series has sessions to start (each start mirrors the user data into its slot again), released when none is left.
        foreach ($slot in @($locked)) { Unlock-Slot $slot }
    }
    $done
}
