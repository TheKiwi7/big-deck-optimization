# Big Deck Optimization — Design

A Slay the Spire 2 mod that removes lag when playing with huge decks (200+ cards).

Target game version: **v0.107.1** (Godot 4, C# / .NET 9, game logic in `sts2.dll`).

## Goal

- **Success criterion:** no visible frame drop or lag during combat with **1000 cards**.
- Comfort target 500 cards; must not collapse at **2000+**.
- In scope: combat (draw/shuffle/end of turn), deck/draw/discard pile views, combat start, out-of-combat screens listing the deck (rewards, shop, campfire upgrade/remove), **co-op** (the original lag report is a 4-player co-op run).
- Out of scope: memory leaks / degradation over a long run.

## Hard constraints

1. **`affects_gameplay: false`.** One co-op player can run the mod while the others don't. No patch may change game state.
2. **Bit-identical.** Every patch on a logic path (hook dispatch, per-card recalculation, checksum, serialization) must give exactly the vanilla result, and is checked against vanilla: in the bench (`bdperf`) and, for the checksum, live (`SnapshotVerifier`).
3. **Never crash.**
   - At startup, each patch is applied in isolation; a missing target only disables that patch (logged).
   - At runtime, an exception inside a patch disables it for the session and falls back to vanilla.
   - An optimization backs off when another mod patches its target (`PatchSafety`).

## Optimization categories

- **(a) Invisible — always on.** Everything shipped so far is in this category.
- **(b) Visual concessions — "Auto" mode.** Enabled by default only above a deck-size threshold (default 300, configurable), each one toggleable. Examples: capped shuffle animation, faster draw animations, per-card VFX cut. Not started.
- **(c) Gameplay logic changes — never.**

## Technical choices

- Harmony runtime patching (`0Harmony.dll` ships with the game). C# .NET 9 class library referencing `sts2.dll`, `GodotSharp.dll`, `0Harmony.dll` from the game folder (not copied).
- Decompile with ILSpy (`ilspycmd`) into `/decompiled` — **gitignored, never committed** (MegaCrit and other mod authors' code). The public repo contains only our patches.
- Standalone: no hard dependency. ModConfig is an optional soft dependency (not wired yet); config files must go to `%APPDATA%/SlayTheSpire2/mod_configs`, **never as `.json` in the mod folder** (see modding facts).
- Compatibility: **must** work with Limitless and BetterSpire2; best effort with SpeedX, CampfireTrade, BaseLib. Patches are prefixes/postfixes/finalizers that only skip the original inside well-defined read-only scopes.
- Language: English only for v1.

## Game modding facts (v0.107.1)

- Mods load from `<game dir>/mods/` (recursive) and Steam Workshop (app 2868840). Each mod: `<Id>.json` manifest + `<Id>.dll` (+ optional `.pck`).
- **Every `.json` file in a mod folder is read as a manifest** → the build disables `deps.json` generation.
- Manifest fields: `id`, `name`, `author`, `description`, `version`, `has_pck`, `has_dll`, `dependencies` (`[{id, min_version}]`), `affects_gameplay` (**defaults to `true`** — must be set explicitly), `min_game_version`.
- Entry point: a class with `[ModInitializer("MethodName")]` (static method); without it the game calls `Harmony.PatchAll` on the assembly.
- Multiplayer: only mods with `affects_gameplay: true` are included in the gameplay mod list compared between players (`ModManager`).
- Console commands: subclass `AbstractConsoleCmd`; the dev console auto-discovers subtypes in mods. Debug commands are allowed whenever the game runs modded. `IsNetworked` commands go through the action queue in multiplayer and run on **every** peer (`ConsoleCmdGameAction`), so they must be deterministic and every peer needs the command.
- Console key: `` ` ``, `'`, `*` or `^` (AZERTY: `*` or `^`).
- Logs: `%APPDATA%/SlayTheSpire2/logs/` — **only the last few sessions are kept**; copy them right after a session worth analyzing.
- Run history: `%APPDATA%/SlayTheSpire2/steam/<steamid>/modded/profileN/saves/history/*.run` (JSON, full decks and relics per player).

## Findings

### Phase 0 (2026-10-21): the co-op checksum

- Singleplayer with a 791-card deck: no lag except at combat start. The co-op run (4 players, 791-card deck incl. 736 Squeeze / 735 enchanted, a teammate with 189 relics) lagged for **every** player during the whole combat.
- The post-action checksum (`ChecksumTracker`, **multiplayer only**) snapshots the full state (`NetFullCombatState.FromRun`) on every peer after every action. Measured: **92 ms and 64 MB allocated per checksum** with 802 combat cards; serialize + hash is only 0.6 ms.
- Root cause: `CombatState.IterateHookListeners()` allocates a fresh list of all listeners (every card, enchantment, relic, power, mod subscriber…) on each call. The snapshot calls it 3× per card (keywords, energy cost, late energy cost): O(cards²).
- Fix: `ListenerCache` (snapshot scope) + hook filter (now `HookFilter`) → **94 ms → 3.8 ms per checksum (x24.8), bit-identical**.
- Each peer computes its own checksum: a player only gets the gain if they have the mod.

### Phase 2 (2026-10-21): dotnet-trace, singleplayer, 935 cards + 368 relics

- Combat setup itself is cheap: `SetUpCombat` 66 ms (card cloning 38 ms).
- **After every action**, `CombatStateTracker.CallCombatStateChangedDeferred` takes 80–100 ms (356 ms at combat start): `RecalculateCardValues` (every enchantment, incl. Limitless multi-enchantments) + hand refresh (`NCard.UpdateVisuals`, `CanPlay` ×5 per card). Every hook call rebuilds the listener list → O(cards²) again. Fix: `ListenerCache` refresh scopes. **Not measured in game yet.**
- Relic bar: every `NButton` gets every input event via `_Input` (only used for drag-cancel while pressed): 368 Godot→C# calls per mouse move. Fix: `IdleButtonInput`. **Not measured in game yet.**
- `CombatState.Players` allocates a list (LINQ) on each call; `IterateHookListeners` calls it every time (720 ms of the trace). Covered inside cache scopes only.
- **Measured in game (2026-10-21, singleplayer, 2830 combat cards + 370 relics)**, all identical to vanilla:
  - checksum: vanilla 1723 ms → cache 422 ms → cache+filter **40 ms** (x42.8). Relics make vanilla far worse (1.7 s vs 94 ms with 2 relics).
  - post-action refresh (`bdperf refresh`): 80 ms → **21 ms** (x3.9).
  - relic bar input: 2333 ms → 46 ms of a 60 s trace.
- Remaining in the refresh (second trace): Limitless `MultiEnchantment.RecalculateValues` over every card (~27%), `CanPlay` → BaseLib's `HasEnoughResourcesFor` patch and `Hook.ShouldPlay` still walk the whole (cached) listener list per hand card. Relic hover tooltips (vanilla, created on hover) are the rest of the relic bar cost.
- Third trace + `bdperf combatstart` (945 cards + 368 relics): refresh 21.7 → 6.1 ms; enemy intents (`RunState.IterateHookListeners`, which also walks the whole permanent deck) now covered. Combat start is ~5.9 s wall clock but almost all of it is relic animations/waits (AfterRoomEntered ~2.9 s ≈ 6 StoneCracker × 0.5 s, BeforeCombatStart ~0.7 s, first turn ~1.6 s); assets + scene + SetUpCombat ≈ 100–170 ms; only one long frame (99–208 ms, first render of the room = the "black screen"). Shortening waits would change timing, which SpeedX itself flags as a co-op desync risk → only as an opt-in (b) concession.
- Not addressed: `RichTextSine` / `RichTextGold` custom text effects (~10% of the main thread, per character per frame, mostly Godot→C# wrapper creation, unavoidable from C#).

### Step D (2026-10-22): card grids (deck view), singleplayer, 945-card deck + 370 relics, Limitless

- `NCardGrid` is already virtualized by the game (sliding window of ~6 rows of pooled holders), on every grid screen (deck view, piles, card selection). No virtualization needed.
- 4 traces of the same gestures (scroll, each sort twice, inspect). Before: scrolling 45 ms per row (max 275 ms), half of it Limitless' `UpdateEnchantmentVisuals` (`Node.Duplicate` of every tab on every render, 9.1 s of 90 s); a sort click 30–114 ms (`List.IndexOf` tie-break per comparison, culture string compare, cost).
- After `EnchantmentTabReuse` + `GridSortCache`: duplicates 9.1 s → 0.2 s, sort ≤ 4 ms (bench 24–39 ms → 2.4 ms, identical orders); scrolling 34 ms per row.
- Remaining scroll cost is vanilla per-card rendering (~7 ms per card: title/type/description autosize, energy/star cost visuals), the same for any deck size.
- `ListenerCache` was dropping its cache on every `CardModel.Enchantment` set, including Limitless' ownerless enchantment storage cards rebuilt while rendering every enchanted card: now only owned cards invalidate. Combat refresh with Limitless: 23.6 → 2.0 ms (x11.7, was x3.9).
- Tried and removed: listener scopes around `NCardGrid.AssignCardsToRow`/`InitGrid` (BaseLib's type plaque walks every run listener per rendered card): no measurable gain, materializing the list costs as much as the walks it saves.
- Vanilla, deck-size independent: `NodePool.Free` → `DisconnectIncomingAndOutgoingSignals` walks every node of every freed card and lists all its signals (Godot dictionaries), 60–340 ms on each grid init (open, sort); the actual disconnects are ~6 ms. `PoolSignalScan`: 12.3 s → 5.9 s over comparable gestures (avg 118 → 47 ms per grid init, worst ~280 ms). What remains is reading connections that do exist (engine/internal ones) only for `DisconnectSignal` to skip them: unavoidable without changing behavior.
- Harmony on `NodePool<NCard>` methods does not reach the shared code reference-type instantiations run (`NodePool`1[__Canon]`): patch non-generic entry points instead.
- Logs: a singleplayer session log is ~34 KB, nothing repeated at volume: no log reduction needed.

### Co-op desyncs (unverified hypothesis)

The group often desynced. `RecalculateCardValues` only runs for the local player, and Limitless' `MultiEnchantment.RecalculateValues` rewrites its serialized nested cards (`SetSerializableCards`), which are part of the checksum. SpeedX's own log also warns its multiplayer timer override risks desyncs. Needs the logs of a desynced session.

## Implemented patches (`src/BigDeckOptimization/Patches`)

| Patch | What | Scope / safety |
|---|---|---|
| `ListenerCache` | Materializes `CombatState.IterateHookListeners()` once and replays it | Only inside scopes: **snapshot** (`NetFullCombatState.FromRun`) and **refresh** (`PlayerCombatState.RecalculateCardValues`; the whole `CombatStateChanged` dispatch via a transpiler on `CombatStateTracker.CallCombatStateChangedDeferred` → one scope for hand, end turn button, intents, creature state, energy, orbs; `NHandCardHolder.UpdateCard`, `NCard.UpdateVisuals` when called outside it). Also caches `RunState.IterateHookListeners` (deck + combat). Invalidated on `CombatStateTracker.NotifyCombatStateChanged` and `Player.Add/RemoveRelicInternal`. Thread-static state. Backs off if another mod patches `IterateHookListeners`. |
| `HookFilter` | Inside any `ListenerCache` scope (snapshot or refresh), `Hook.ModifyEnergyCostInCombat`, `Hook.ModifyKeywordsInCombat`, `Hook.ShouldPlay` and `Hook.ModifyDamageInternal` (additive/multiplicative/cap) only call listeners whose type overrides the `AbstractModel` hook (base = no-op: +0, ×1m, cap MaxValue, true), same order | Mirrors the vanilla hook bodies. Backs off per hook if another mod patches the hook or its base method (BaseLib patches `Hook.ModifyDamage`, not `ModifyDamageInternal`). |
| `SnapshotVerifier` | Live safety net for the checksum: first 3 snapshots of the session then 1 in 25 rebuilt vanilla and compared byte for byte | On mismatch: vanilla snapshot is used, snapshot optimizations off for the session, error logged. |
| `IdleButtonInput` | Input processing off for idle `NButton`s whose `_Input` is NButton's own (drag-cancel), on while pressed | UI only. |
| `GridSortCache` | Inside `NCardGrid.SetCards`, the sort comparers (obtained order, cost, title; asc/desc) answer from per-card values computed once: index map with `IndexOf`'s equality, resolved cost, culture `SortKey`s | Display order only. Each comparer is replaced only if its fingerprint matches the vanilla lambda. |
| `EnchantmentTabReuse` | Limitless only (applied when its assembly loads, skipped if absent): mirrors `CardExtensions.UpdateEnchantmentVisuals` but reuses the card's existing tabs instead of freeing and duplicating them | UI only. Off unless the mirrored method's fingerprint matches (Limitless v1.1.7); backs off if another mod patches it. |
| `PoolSignalScan` | Prefix on non-generic `NodePool.Free(IPoolable)` mirroring `NodePool<T>.Free`: same walk, disconnects (pool's own `DisconnectSignal`) and bookkeeping, but signal names cached per (type, class, script) and connection lists read only when `HasConnections` | UI only. Fingerprints of the 3 mirrored methods required; names re-checked vs vanilla for the first 3 nodes per type then 1 in 64, any difference turns it off. |
| `MethodFingerprint` | Helper: hash of a method's IL with tokens resolved to member names (stable across recompiles) | Patches that mirror code require the fingerprint of the code they mirror, so updates only disable them when that exact code changes. |
| `PatchSafety` | Helper: is a method Harmony-patched by another owner? | — |

All patches catch their own exceptions and fall back to vanilla.

## Console commands

Public (`src/BigDeckOptimization/Commands`, in every build):

- `bdopt [on|off]` — every optimization on/off at runtime (vanilla when off), or the state of each one (incl. patches skipped or self-disabled). Local, safe in co-op.

## Test tooling (`src/BigDeckOptimization/Bench`, Debug builds only: excluded from Release by the csproj, `BENCH` symbol)

- `bigdeck <count> [card-id] [seed]` — fill the deck up to count (one card, or random from the character pool). Networked + seeded (seed 0 default): usable in co-op if **every** player has the mod.
- `bigdeck relics <count> [relic-id] [seed]` — add relics (character + shared pools), no obtain effects. Networked.
- `bigdeck import <file.run|shared-name> [all|i|i,j] [cards|relics|all] [hash]` — exact decks (upgrades, enchantments) and relics from a run history file, added like a save load (no obtain/card-added hooks). Players can be merged. In co-op: only bare names from the shared folder `user://bigdeck` (`%APPDATA%\SlayTheSpire2\bigdeck`), and the SHA-256 prefix from `bigdeck hash <name>` is required, so every peer provably reads the same file.
- `bigdeck tank [hp]` — sets every living enemy's max and current HP (default 1,000,000) for long perf-test combats. Networked.
- `bigdeck hash <shared-name>` — prints the SHA-256 prefix of a shared run file (each peer prints its own).
- Caps: deck ≤ 5000, ≤ 1000 relics per command (any peer can issue networked commands).
- `bdperf checksum [n]` — vanilla vs cache vs cache+filter checksum cost, checks identical checksums.
- `bdperf refresh [n]` — vanilla vs cached post-action refresh (enchantments + hand), checks the game state is identical afterwards.
- `bdperf verify [off|sample|all]` — live verification mode and counters (snapshots, verified, mismatches), patch on/off state. `all` costs a vanilla snapshot per action: singleplayer debugging only.
- `bdperf grid [cards]` — deck view sort orders (vanilla vs cached, identical orders), enchantment tab rendering on cards spread over the deck (vanilla vs reuse, identical tab state), and freeing grid holders to the pool (vanilla vs scan, identical remaining connections). Bench timings of rendering/freeing are far below in-game ones (hidden container, no GC pressure): use them for identity, traces for gains. UI only, usable anywhere in a run.
- `bdperf combatstart` — wall-clock breakdown of the last combat start (phases, GC pauses, longest frames). `checksum` and `refresh` refuse to run in real co-op.
- `bdperf opt on|off` — all optimizations on/off at runtime for an A/B comparison in the same session (safe in co-op: both paths give identical results).
- Planned, not built: frame-time overlay, scripted `bench` scenario with CSV export.

## Distribution

- Steam Workshop (primary), GitHub (MIT, private until v0.1) for source and releases, Nexus on demand.
- ID `BigDeckOptimization`, display name "Big Deck Optimization".
- README / Workshop text: don't present Limitless as the source of big decks; only list it among compatible mods.

## Roadmap

| Step | Content | Status |
|---|---|---|
| 0 | Test bench + profiling | Done (bench commands, dotnet-trace workflow) |
| A | Checksum snapshot (co-op lag) | Done, measured x24.8 |
| B | Post-action refresh + relic bar input | Done, measured x3.9 (refresh), relic input ~x50 |
| C | Co-op test with a friend (both with the mod, default `sample` verification) | Done 2026-10-22: mismatches 0 on both peers, no desync |
| D | Card grids (deck view, piles, upgrade/remove, shop) + log reduction | Done 2026-10-22: sort x10–17, Limitless tab reuse, pool free x2.5, combat refresh x11.7; grids already virtualized, logs not an issue |
| E | Visual concessions (b), config (ModConfig soft dep) | Not started |
| F | Release: hide bench commands, README, Workshop page → v0.1 | Prepared 2026-10-22: Release build without test commands (`bdopt` kept), `tools/release/package.py`, README/LICENSE/CHANGELOG, Workshop text, `docs/RELEASE.md` checklist. Preview image generated. Co-op with a player without the mod verified (2026-10-22). Left: rest of the checklist, Workshop upload |


### Lesson (stress trace 2, 2026-10-21)

Per-handler scopes cost more than they saved: each outermost scope rebuilt and re-filtered ~12k listeners for one or two hook calls (filter 7.3 s + rebuild 2.8 s per 60 s). Scopes must be coarse (one per refresh) and the filter single-pass.

### Stress results (2026-10-21, singleplayer, ~4,690 cards + ~1,840 relics = 5x the reference run)

| Build | Refreshes / 60 s | Avg per refresh | `bdperf refresh` |
|---|---|---|---|
| cache only | 670 | 27.4 ms | — |
| + HookFilter, per-handler scopes | 625 | 29.8 ms | x5.9 (hand only) |
| + single dispatch scope, single-pass filter | 798 | 16.9 ms | 152 → 14 ms, x10.7 |

Remaining per refresh (~12 ms inside the scope): run listener materialization ~2.3 ms, filter ~1.6 ms, Limitless enchantment visuals, CardCostHelper energy/star cost loops. Out of scope: gameplay hooks (damage, HP loss, after-damage...) ~20 ms per hit at this load — caching them needs lazy per-item `Contains` semantics and a dedicated verifier (desync risk), deferred to v0.2. Reusing lists across refreshes was rejected: BaseLib card modifiers can change between refreshes without a hook we can track.
