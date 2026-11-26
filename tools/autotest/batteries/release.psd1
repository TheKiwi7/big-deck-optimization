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
        @{ Name = 'beta'; Dir = 'C:\Games\StS2-beta-0.114.0' }
    )
    Blocks = @(
        # Solo sessions: the par lists of both copies and the sweeps of both copies (one parsweep-all -GameDir pub,beta), in one pool.
        @{ Name = 'solo'; Mode = 'Parallel'; Steps = @(
            @{ Name = 'solo-ab'; Par = 'ab-2k-off,ab-2k-on,audit-toggles-2k,audit-conflict-ab,audit-listeners-ab,combats3-2k-off,combats3-2k-on,outside-2k-off,outside-2k-on,shuffle-off,shuffle-on,audit-conflict-saves'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            @{ Name = 'solo-snapcheck'; Par = 'snapcheck-5k-a-off,snapcheck-5k-a,snapcheck-5k-b-off,snapcheck-5k-b'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            # SaveBackupCheck (v0.113+; on v0.111 the pair and the control end at requiregame, the bench says "not needed"): its off
            # side turns only save_check off, so no baseline.
            @{ Name = 'solo-savecheck'; Par = 'savecheck-off,savecheck-on,savecheck-wrong,savecheck-bench'; Args = @('-TimeoutSec', '1800') }
            # The scenarios added by the audit rounds since v1.2.0 (SaveReadDecode, CombatCards, FirstTurn, hover tips, the foreign
            # shapes, DeckCountMemo, SortKeys partonly, the G rules). -UseBaselines: ten of its mod-off sides are real vanilla ones
            # ('cmd bdopt off' first: combatcards-2k, wrong-1k, firstturn-2k, foreign-firstturn-1k, deckcount-wah, deckcount,
            # foreign-gates-1k, foreign-hookfilter-2k, limitless-gated-2k, handselect), read from the baselines; the others
            # (saveread-off, foreign-limitless-u/umax/l-2k-off) turn only their own option or settings off and run this mod's code:
            # never cached whatever the flag says.
            @{ Name = 'solo-new'; Par = 'saveread-on,saveread-off,saveread-serve-on,saveread-wrong,saveread-bench,saveread-transpiler,combatcards-2k-on,combatcards-2k-off,wrong-combatcards-1k-on,wrong-1k-off,firstturn-2k-on,firstturn-2k-off,foreign-firstturn-1k-on,foreign-firstturn-1k-off,foreign-firstturn-controls,firstturn-selftest,hover-tips-prefixes,hover-tips-transpiler,hover-tips-ctl,foreign-parts-roprefix,foreign-walkgetters,foreign-limitless-u-2k-on,foreign-limitless-u-2k-off,foreign-limitless-umax-2k-on,foreign-limitless-umax-2k-off,foreign-limitless-controls,deckcount-wah-on,deckcount-wah-serve-on,deckcount-wah-off,deckcount-inplace-wrong,deckcount-on,deckcount-off,sortkeys-partonly-10k,foreign-gates-1k-on,foreign-gates-1k-off,foreign-hookfilter,foreign-hookfilter-2k-on,foreign-hookfilter-2k-off,limitless-gated-2k-on,limitless-gated-2k-off,foreign-limitless-l-2k-on,foreign-limitless-l-2k-off,foreign-controls,wrong-clonememo-1k-on,wrong-hookfilter-1k-on,wrong-hookskip-1k-on,wrong-listenercache-1k-on,wrong-moved-1k-on,wrong-pile-1k-on,wrong-pilebits-1k-on,wrong-recalc-1k-on,wrong-reserialize-1k-on,wrong-segments-1k-on,wrong-segmoves-1k-on,wrong-segshifts-1k-on,wrong-segsubs-1k-on,wrong-snapcard-1k-on,wrong-trusted-1k-on,audit-conflict-display-1k-on,sortkeys-conflict-1k-on,deckcount-wrong,audit-deckcount-conflict,audit-deckcount-fault,handselect-on,handselect-off'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            # The owner-approved premises and the foreign-shape pairs of docs/RELEASE.md (items "Foreign patches judged by their shape" and
            # "Owner-approved premises"), the pile-cards pair and the sort-keys controls (v0.113+: v0.111 ends them at requiregame): every
            # pair compared by name, the controls by their expect lines. Mod-off sides from the baselines (all start with 'cmd bdopt off').
            @{ Name = 'solo-premises'; Par = 'relicflash-mods-on,relicflash-mods-off,relicflash-display-on,relicflash-display-off,relicflash-ctl-on,relicflash-ctl-off,relicflash-sfx-on,relicflash-sfx-off,relicflash-deep-on,relicflash-deep-off,hover-tips-display-on,hover-tips-display-off,sortkeys-premise-1k-on,sortkeys-premise-1k-off,foreign-shapes-2k-on,foreign-shapes-2k-off,foreign-needless-2k-on,foreign-needless-2k-off,foreign-il-2k-on,foreign-il-2k-off,foreign-helpers-2k-on,foreign-helpers-2k-off,foreign-flights-2k-on,foreign-flights-2k-off,foreign-bodyonly-2k-on,foreign-bodyonly-2k-off,foreign-namegate-1k-on,foreign-namegate-1k-off,foreign-dmgbridge-1k-on,foreign-dmgbridge-1k-off,foreign-walkreuse-1k-on,foreign-walkreuse-1k-off,pile-cards-2k-on,pile-cards-2k-off,relicflash-lazy-on,relicflash-scene-ctl,hover-tips-move,sortkeys-wrong,sortkeys-violate-1k-on,sortkeys-postcombat,foreign-walkreuse-ctl,foreign-needless-controls,foreign-helpers-controls,foreign-flights-controls,foreign-keptmemo-ctl,foreign-keptmemo-fault-ctl,spirefield-interfaces'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            # The positive controls, checks and audits of RELEASE.md that run headless on one copy (no pair: each judged by its own
            # expect lines and judge.py). guard-async-on compares with guard-async-ref (same list). -UseBaselines: only
            # movecap-1k-off is a real vanilla mod-off side (read from the baselines); the rest has no mod-off twin and runs each time.
            # Added 2026-11-21 (audit of the checklist).
            @{ Name = 'solo-controls'; Par = 'snapcheck-purehooks,snapcheck-purehooks-wrong,snapcheck-apotheosis,snapcheck-apotheosis-wrong,relics-scalarblind,relics-scalarblind-verifier,pileindex-nested-limit,rebuild-control,rebuild-check,guard-checks,guard-async-ref,guard-async-on,walkcode-check,audit-idlebuttons,audit-parts-visible,movecap-1k-off,movecap-1k-on,clonememo-cap,clonememo-check,snapcheck-hooks,modelstack-badges,audit-finished-room-on,audit-verifier-relics,audit-gc-latency,audit-languages,conflict-check,audit-walk-conflict,preflight-check'; Args = @('-UseBaselines', '-TimeoutSec', '1800') }
            # solo-pace: no -UseBaselines on purpose. pace-vanilla starts with 'cmd bdopt off' but is not named -off (never a baseline
            # job), pace-off turns one option off (runs this mod's code): the flag would change nothing, and these time the combat
            # start, so each must run in this battery.
            @{ Name = 'solo-pace'; Par = 'pace-vanilla,pace-off,pace-on,pace-instant,effects-check,release-checks'; Args = @('-TimeoutSec', '1800') }
            @{ Script = 'vanillacheck.ps1'; Args = @('-GameDir', '{copy}', '-Jobs', '{jobs}') }
            @{ Name = 'parsweep-all'; Once = $true; Script = 'parsweep-all.ps1'; Args = @('-GameDir', '{copies}', '-Jobs', '{jobs}', '-UseBaselines') }
        ) }
        # The 8,400-card ones (RELEASE.md, "Deck size"): once per release, on one version (the public one: turns-off measured there,
        # ~25 min the first time, then from the baselines), a few paths only run at large sizes. A small pool (-Jobs 3): four 8,400-card
        # sessions are memory-heavy. turns-off with hookskip-on (compare-with), pile-cards-off with -on and -verify, the hover tips at 8k.
        @{ Name = 'big-ab'; Mode = 'Parallel'; Copies = @('pub'); Steps = @(
            # -AloneNames: turns-off and pile-cards-off, the 8,400-card vanilla sides, are made first, one at a time, when their baseline
            # is missing (every battery with -Fresh, or the first after a change of the game, the mods or those scenarios): beside
            # the other sessions pile-cards-off ran 2,417 s and hit its 2,400 s timeout (now 5,400 s). The step's own timeout covers
            # both made alone one after the other, then the rest (the baselines make later batteries read them: ~7 min here).
            @{ Name = 'ab-8k'; Script = 'par.ps1'; TimeoutSec = 14400; Args = @('-Names', 'turns-off,hookskip-on,pile-cards-off,pile-cards-on,pile-cards-verify,hover-tips-8k', '-GameDir', '{copy}', '-Jobs', '3', '-UseBaselines', '-AloneNames', 'turns-off,pile-cards-off', '-TimeoutSec', '3000') }
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
            # The foreign patches in co-op, peers compared only: its mod-off twin (the reference run's deck) stalled on both copies on
            # 2026-11-22 (host frames of 11-12 s, a peer left), as coop's and coopclone's twins can't finish; the foreign shapes'
            # on/off equivalence is the solo foreign-* pairs' (solo-premises).
            @{ Name = 'coop-foreign'; Script = 'parcoop.ps1'; Args = @('-Names', 'coop-foreign', '-GameDir', '{copy}', '-Jobs', '3') }
            # audit-coopdeath: peers only (its players' actions interleave, RELEASE.md), added 2026-11-21.
            @{ Name = 'coop-misc'; Script = 'parcoop.ps1'; Args = @('-Names', 'audit-cooptoggle,coopinstant,effects-coop,audit-coopdeath', '-GameDir', '{copy}') }
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
            # Not duplicates of solo-ab / solo-pace / solo-new although the scenario names repeat: BDTEST_EXTRA_MODS puts other mods in
            # the game (what is patched, the baseline key), and effects-check here checks the effects next to them. Checked 2026-11-22:
            # no other scenario of this battery runs twice with the same mods and settings (names and script bodies compared).
            @{ Name = 'extra-ab'; Par = 'ab-2k-off,ab-2k-on,outside-2k-off,outside-2k-on,effects-check'; Args = @('-UseBaselines'); Env = @{ BDTEST_EXTRA_MODS = 'SpeedX,ModConfig,sts2unlimited' } }
            # RitsuLib's hidden IL hooks next to ExtraScopes' first-turn filters.
            @{ Name = 'extra-ritsu'; Par = 'firstturn-real-2k-on,firstturn-real-2k-off'; Args = @('-UseBaselines'); Env = @{ BDTEST_EXTRA_MODS = 'STS2-RitsuLib' } }
        ) }
    )
}
