# The release checks of docs/RELEASE.md section 1 as a battery: powershell -File tools\autotest\battery.ps1 -File tools\autotest\batteries\release.psd1
# (see battery.ps1's header for the schema). Scenario lists are examples: keep them in step with the checklist.
@{
    Name = 'release'
    Jobs = 14          # every par.ps1 gets the same -Jobs: the slots 0..13 are shared by the two copies
    TimeoutSec = 7200
    Config = 'Debug'
    Env = @{ BDTEST_WORKSHOP = 'C:\steamcmd\steamapps\workshop\content\2868840' }
    Copies = @(
        @{ Name = 'pub';  Dir = 'C:\Games\StS2-public-0.111.1' }
        @{ Name = 'beta'; Dir = 'C:\Games\StS2-beta-0.113.1' }
    )
    Blocks = @(
        # Solo A/Bs and audits: both copies at once, one pool of slots.
        @{ Name = 'solo'; Mode = 'Parallel'; Steps = @(
            @{ Name = 'solo-ab'; Par = 'ab-2k-off,ab-2k-on,audit-toggles-2k,audit-conflict-ab,audit-listeners-ab,combats3-2k-off,combats3-2k-on,outside-2k-off,outside-2k-on,shuffle-off,shuffle-on'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            @{ Name = 'solo-pace'; Par = 'pace-vanilla,pace-off,pace-on,pace-instant,effects-check,release-checks'; Args = @('-TimeoutSec', '1800') }
        ) }
        # Sweeps: each kind set runs on both copies together, one step after the other.
        @{ Name = 'sweeps'; Mode = 'Both'; Steps = @(
            @{ Name = 'parsweep-all'; Script = 'parsweep-all.ps1'; Args = @('-GameDir', '{copy}', '-Jobs', '{jobs}', '-UseBaselines') }
        ) }
        # Co-op, mod-off check, settings, mutations, memory: one thing at a time, copy by copy.
        @{ Name = 'exclusive'; Mode = 'Exclusive'; Steps = @(
            @{ Script = 'vanillacheck.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Name = 'coop'; Script = 'parcoop.ps1'; Args = @('-Names', 'coop,coopclone,coopfast', '-GameDir', '{copy}', '-OnOff', '-Jobs', '1') }
            @{ Name = 'coop-misc'; Script = 'parcoop.ps1'; Args = @('-Names', 'audit-cooptoggle,coopinstant,effects-coop', '-GameDir', '{copy}') }
            @{ Script = 'coop4.ps1'; Args = @('-Name', 'coop4', '-GameDir', '{copy}') }
            @{ Script = 'coop-modetag.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'started-off.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'settings.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'mutations.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Name = 'solo-8k'; Script = 'run.ps1'; Args = @('-Name', 'solo-8k', '-GameDir', '{copy}', '-TimeoutSec', '1500') }
            @{ Name = 'memory-on'; Script = 'run.ps1'; Args = @('-Name', 'memory-on', '-GameDir', '{copy}', '-TimeoutSec', '1500') }
        ) }
        # Extra Workshop mods most players combine: same scenarios with the mods enabled.
        @{ Name = 'extra-mods'; Mode = 'Exclusive'; Steps = @(
            @{ Name = 'extra-ab'; Par = 'ab-2k-off,ab-2k-on,outside-2k-off,outside-2k-on,effects-check'; Args = @('-UseBaselines'); Env = @{ BDTEST_EXTRA_MODS = 'SpeedX,ModConfig,sts2unlimited' } }
        ) }
    )
}
