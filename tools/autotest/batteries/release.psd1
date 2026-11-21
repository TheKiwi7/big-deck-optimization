# The release checks of docs/RELEASE.md section 1 as a battery: powershell -File tools\autotest\battery.ps1 -File tools\autotest\batteries\release.psd1
# (see battery.ps1's header for the schema). Scenario lists are examples: keep them in step with the checklist.
# How the steps share the PC (docs/RELEASE.md, "How a battery runs"): every headless solo session of every series draws from one
# slot pool ($SlotPool in parlib.ps1, BDTEST_SLOTS), longest first; -Jobs only caps one series. The pace-independent steps run on
# both copies at once; what times something or uses the game's own port runs alone.
@{
    Name = 'release'
    Jobs = 14          # the whole pool: every series is given the same cap, never half each
    TimeoutSec = 7200
    Config = 'Debug'
    Env = @{ BDTEST_WORKSHOP = 'C:\steamcmd\steamapps\workshop\content\2868840' }
    Copies = @(
        @{ Name = 'pub';  Dir = 'C:\Games\StS2-public-0.111.1' }
        @{ Name = 'beta'; Dir = 'C:\Games\StS2-beta-0.113.1' }
    )
    Blocks = @(
        # Solo sessions: the par lists of both copies and the sweeps of both copies (one parsweep-all -GameDir pub,beta), in one pool.
        @{ Name = 'solo'; Mode = 'Parallel'; Steps = @(
            @{ Name = 'solo-ab'; Par = 'ab-2k-off,ab-2k-on,audit-toggles-2k,audit-conflict-ab,audit-listeners-ab,combats3-2k-off,combats3-2k-on,outside-2k-off,outside-2k-on,shuffle-off,shuffle-on'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            @{ Name = 'solo-snapcheck'; Par = 'snapcheck-5k-a-off,snapcheck-5k-a,snapcheck-5k-b-off,snapcheck-5k-b'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            # SaveBackupCheck (v0.113+; on v0.111 the pair and the control end at requiregame, the bench says "not needed"): its off
            # side turns only save_check off, so no baseline.
            @{ Name = 'solo-savecheck'; Par = 'savecheck-off,savecheck-on,savecheck-wrong,savecheck-bench'; Args = @('-TimeoutSec', '1800') }
            @{ Name = 'solo-pace'; Par = 'pace-vanilla,pace-off,pace-on,pace-instant,effects-check,release-checks'; Args = @('-TimeoutSec', '1800') }
            @{ Script = 'vanillacheck.ps1'; Args = @('-GameDir', '{copy}', '-Jobs', '{jobs}') }
            @{ Name = 'parsweep-all'; Once = $true; Script = 'parsweep-all.ps1'; Args = @('-GameDir', '{copies}', '-Jobs', '{jobs}', '-UseBaselines') }
        ) }
        # Both copies at once: the pace-independent steps (each takes its own kept slot of 80-89; mutations' sessions use the pool).
        @{ Name = 'both-at-once'; Mode = 'Parallel'; Steps = @(
            @{ Script = 'settings.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'baselib-optional.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'limitless-switches.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'started-off.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'mutations.ps1'; Args = @('-GameDir', '{copy}', '-Jobs', '{jobs}') }
        ) }
        # Co-op: one pair of mod-off sessions at a time (-OffJobs 1), the mod-on pairs beside it (-Jobs 3), mod-off sides from the baselines.
        @{ Name = 'coop'; Mode = 'Exclusive'; Steps = @(
            # Only coopfast has a mod-off twin: coop and coopclone play the full reference deck, whose vanilla combat start outlasts
            # the network timeout (their twins could never pass): their peers are compared only.
            @{ Name = 'coop'; Script = 'parcoop.ps1'; Args = @('-Names', 'coop,coopclone,coopfast', '-GameDir', '{copy}', '-OnOff', '-OnOffNames', 'coopfast', '-UseBaselines', '-OffJobs', '1', '-Jobs', '3') }
            @{ Name = 'coop-misc'; Script = 'parcoop.ps1'; Args = @('-Names', 'audit-cooptoggle,coopinstant,effects-coop', '-GameDir', '{copy}') }
            @{ Script = 'coop-modetag.ps1'; Args = @('-GameDir', '{copy}') }
            @{ Script = 'started-off-nomenu.ps1'; Args = @('-GameDir', '{copy}') }
        ) }
        # Four-player co-op: coop4 and coop4leak side by side (slots 49-52), one copy at a time.
        @{ Name = 'coop4-pub'; Mode = 'Parallel'; Copies = @('pub'); Steps = @(
            @{ Name = 'coop4'; Script = 'coop4.ps1'; Args = @('-Name', 'coop4', '-GameDir', '{copy}') }
            @{ Name = 'coop4leak'; Script = 'coop4.ps1'; Args = @('-Name', 'coop4leak', '-GameDir', '{copy}') }
        ) }
        @{ Name = 'coop4-beta'; Mode = 'Parallel'; Copies = @('beta'); Steps = @(
            @{ Name = 'coop4'; Script = 'coop4.ps1'; Args = @('-Name', 'coop4', '-GameDir', '{copy}') }
            @{ Name = 'coop4leak'; Script = 'coop4.ps1'; Args = @('-Name', 'coop4leak', '-GameDir', '{copy}') }
        ) }
        # Rendered run.ps1 steps (timings, the screen): alone on the PC, copy by copy.
        @{ Name = 'rendered'; Mode = 'Exclusive'; Steps = @(
            @{ Name = 'solo-8k'; Script = 'run.ps1'; Args = @('-Name', 'solo-8k', '-GameDir', '{copy}', '-TimeoutSec', '1500') }
            @{ Name = 'fast-start-check'; Script = 'run.ps1'; Args = @('-Name', 'fast-start-check', '-GameDir', '{copy}', '-TimeoutSec', '1500') }
            @{ Name = 'memory-on'; Script = 'run.ps1'; Args = @('-Name', 'memory-on', '-GameDir', '{copy}', '-TimeoutSec', '1500') }
            @{ Name = 'menu-languages'; Script = 'run.ps1'; Args = @('-Name', 'menu-languages', '-GameDir', '{copy}', '-TimeoutSec', '1500') }
        ) }
        # Extra Workshop mods most players combine: same scenarios with the mods enabled.
        @{ Name = 'extra-mods'; Mode = 'Exclusive'; Steps = @(
            @{ Name = 'extra-ab'; Par = 'ab-2k-off,ab-2k-on,outside-2k-off,outside-2k-on,effects-check'; Args = @('-UseBaselines'); Env = @{ BDTEST_EXTRA_MODS = 'SpeedX,ModConfig,sts2unlimited' } }
        ) }
    )
}
