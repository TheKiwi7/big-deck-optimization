using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <see cref="CombatState.IterateHookListeners"/> and <see cref="RunState.IterateHookListeners"/> rebuild a list of
/// every listener (all combat cards, deck cards, enchantments, relics, powers, mod subscribers...) on each call.
/// Read-only passes call them once or more per card, which is O(cards²):
///   - Snapshot scope: the multiplayer post-action checksum (<see cref="NetFullCombatState.FromRun"/>).
///   - Refresh scope: UI refreshes after every action (hand cards, end turn button, enemy intents, creature state,
///     energy, orbs) and enchantment value recalculation.
///
/// Inside a scope nothing changes the listener set, so we materialize it once and replay it (game logic that resumes inline
/// inside the dispatch scope, a hand selection ended by a state change, runs vanilla: <see cref="SuspendPrefix"/>). As defense in depth, the
/// cache is dropped on every change that can alter the listener set: combat state changes reported by the game, any
/// card pile mutation (deck included), enchantment/affliction changes, relic (incl. melting), potion and orb changes,
/// players becoming active/inactive for hooks, creatures added, removed or reordered and their powers added or removed.
/// Lists are reused across display scopes only while BaseLib card modifiers (changed with nothing watched) can't exist.
/// </summary>
public static class ListenerCache
{
    public const string Name = "ListenerCache";

    /// <summary>Snapshot-scope caching. Toggled by the bench, or off for the session by the fail-safes.</summary>
    public static bool SnapshotEnabled { get; set; } = true;

    /// <summary>Fault recorded when the snapshot verifier saw an optimized snapshot differ from vanilla.</summary>
    public const string SnapshotFault = Name + ".Snapshot";

    /// <summary>Refresh-scope caching. Toggled by the bench, or off for the session by the fail-safes.</summary>
    public static bool RefreshEnabled { get; set; } = true;

    private static MethodInfo? _combatIterate;
    private static MethodInfo? _runIterate;
    private static AccessTools.FieldRef<CardModel, Player?>? _cardOwner;

    private sealed class RefComparer : IEqualityComparer<(object, object?)>
    {
        public bool Equals((object, object?) a, (object, object?) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((object, object?) k) => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Item1), k.Item2 == null ? 0 : RuntimeHelpers.GetHashCode(k.Item2));

        /// <summary>Same models, by reference.</summary>
        public static readonly IEqualityComparer<AbstractModel> Models = ReferenceEqualityComparer.Instance;
    }

    // Main thread in practice; thread-static state keeps any other caller fully vanilla.
    [ThreadStatic] private static int _snapshotDepth;
    [ThreadStatic] private static int _refreshDepth;
    [ThreadStatic] private static bool _materializingCombat;
    [ThreadStatic] private static bool _materializingRun;
    [ThreadStatic] private static bool _suspended;
    [ThreadStatic] private static bool _safe;
    /// <summary>
    /// Other mods' patches on the combat (or run) walk, all let stand (postfixes run on each replay, see <see cref="EnterAny"/>):
    /// the lists are built from FastListeners' unpatched mirrors, and nothing but the game's own call uses them.
    /// </summary>
    [ThreadStatic] private static bool _combatAugmented, _runAugmented;
    /// <summary>
    /// FastListeners' unpatched mirror declined in this augmented scope (<see cref="UnpatchedWalkMissing"/>): the rest of the
    /// scope's augmented walks take the game's call at once, with no exception thrown per walk. Cleared when the next outermost
    /// scope opens (<see cref="EnterAny"/>).
    /// </summary>
    [ThreadStatic] private static bool _mirrorDeclined;
    [ThreadStatic] private static int _version;
    /// <summary>Key: (CombatState, null) or (RunState, child combat state or null).</summary>
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, ReadOnlyCollection<AbstractModel> Listeners)>? _cache;

    /// <summary>True while inside a checksum snapshot where the snapshot optimizations may run.</summary>
    public static bool IsInSnapshot => SnapshotEnabled && _snapshotDepth > 0 && _safe && !_suspended;

    /// <summary>
    /// True inside any scope (snapshot or refresh) where listener lists are cached and may be used directly (HookFilter's
    /// per-hook lists). False while other mods' postfixes on the walks are let stand: a list built here is the unpatched
    /// walk's, only the game's own call (where those postfixes run on it) may use it then.
    /// </summary>
    public static bool IsCaching => Serving && !_combatAugmented && !_runAugmented;

    /// <summary>Inside a scope where the walks' own calls are answered from the cached lists (<see cref="CombatIteratePrefix"/>).</summary>
    private static bool Serving => _safe && !_suspended
        && ((SnapshotEnabled && _snapshotDepth > 0) || (RefreshEnabled && _refreshDepth > 0));

    /// <summary>Raised when the outermost scope ends, so dependent caches can drop their state.</summary>
    public static event Action? ScopeEnded;

    /// <summary><c>OrbQueue.TryEnqueue</c> (v0.107.1, v0.111): adds the orb before its first await.</summary>
    private const string TryEnqueueFingerprint = "DC22B902D9BCF8AA";

    public static void Apply(Harmony harmony)
    {
        // Order matters: if any of these throws, nothing below is patched and the cache never turns on.
        // 1. Invalidation.
        MethodBase?[] invalidators =
        {
            Watched(Reflect.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged"), typeof(CombatStateTracker), "NotifyCombatStateChanged"),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.AddInternal)), typeof(CardPile), nameof(CardPile.AddInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)), typeof(CardPile), nameof(CardPile.RemoveInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.MoveToBottomInternal)), typeof(CardPile), nameof(CardPile.MoveToBottomInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.MoveToTopInternal)), typeof(CardPile), nameof(CardPile.MoveToTopInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)), typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)),
            Watched(Reflect.Method(typeof(CardPile), nameof(CardPile.Clear)), typeof(CardPile), nameof(CardPile.Clear)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.AddRelicInternal)), typeof(Player), nameof(Player.AddRelicInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.RemoveRelicInternal)), typeof(Player), nameof(Player.RemoveRelicInternal)),
            // Both filter what IterateHookListeners yields (melted relics, dead players' models).
            Watched(Reflect.PropertySetter(typeof(RelicModel), nameof(RelicModel.IsMelted)), typeof(RelicModel), nameof(RelicModel.IsMelted)),
            Watched(Reflect.PropertySetter(typeof(Player), nameof(Player.IsActiveForHooks)), typeof(Player), nameof(Player.IsActiveForHooks)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.AddPotionInternal)), typeof(Player), nameof(Player.AddPotionInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.DiscardPotionInternal)), typeof(Player), nameof(Player.DiscardPotionInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.RemoveUsedPotionInternal)), typeof(Player), nameof(Player.RemoveUsedPotionInternal)),
            Watched(Reflect.Method(typeof(Player), nameof(Player.SubtractFromMaxPotionCount)), typeof(Player), nameof(Player.SubtractFromMaxPotionCount)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)), typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.Remove)), typeof(OrbQueue), nameof(OrbQueue.Remove)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.Insert)), typeof(OrbQueue), nameof(OrbQueue.Insert)),
            Watched(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.Clear)), typeof(OrbQueue), nameof(OrbQueue.Clear)),
        };
        // The methods above (or the ones they call) are the only code changing the piles, relics and potion slots: a new
        // one would leave cached lists stale.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        PatchSafety.RequireFieldWriters(typeof(Player), "_relics", PatchSafety.RelicListWriters);
        PatchSafety.RequireFieldWriters(typeof(Player), "_potionSlots", PatchSafety.PotionSlotWriters);
        PatchSafety.RequireFieldWriters(typeof(OrbQueue), "_orbs", ".ctor", "<TryEnqueue>d__#+MoveNext", "Clear", "Insert", "Remove");
        // TryEnqueue is async: its finalizer runs at its first await, which comes after the orb is added (pinned: same in v0.111).
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(OrbQueue), nameof(OrbQueue.TryEnqueue)), TryEnqueueFingerprint);
        // Finalizers, not postfixes: a method throwing after its change (an event handler of AddInternal, v0.113's arrow
        // slot) must still invalidate; a postfix doesn't run then.
        foreach (MethodBase? target in invalidators)
        {
            PatchGuard.Patch(harmony, Require(target), finalizer: Method(nameof(Invalidate)));
        }
        // Game v0.113: the walks also yield the run's dominion. Every write goes through its setter (the field's only
        // writer: checked), which invalidates. Absent before v0.113.
        foreach (Type owner in new[] { typeof(RunState), typeof(CombatState) })
        {
            if (Reflect.PropertySetter(owner, "Dominion") is { } setDominion)
            {
                PatchSafety.RequireFieldWriters(owner, "<Dominion>k__BackingField", "set_Dominion");
                PatchGuard.Patch(harmony, setDominion, finalizer: Method(nameof(InvalidateDominion)));
            }
            // Game v0.113: the run models the walks yield sit behind a private setter too. The game sets them once (the run's
            // creation, the combat's construction), a mod may set them again later (a framework adding its run models):
            // invalidated the same way. Absent before v0.113 (the walks read them elsewhere, unchanged).
            if (Reflect.PropertySetter(owner, "RunModels") is { } setRunModels)
            {
                PatchSafety.RequireFieldWriters(owner, "<RunModels>k__BackingField", "set_RunModels");
                PatchGuard.Patch(harmony, setRunModels, finalizer: Method(nameof(InvalidateRunModels)));
            }
        }
        // The combat's creatures (the walks start from its ally and enemy lists, in that order) and their powers. Every change
        // of them comes today with a watched one (CombatManager.CreaturesChanged, the tracker's power events,
        // History.MonsterPerformedMove right before a monster killed during its move leaves), but a killed creature is
        // unsubscribed from the tracker while its powers are still listed until it leaves: watched here so the cache doesn't
        // rest on that order. Optional: one missing or not patched only loses that watch (the order above still holds).
        foreach ((Type owner, string name) in new[]
        {
            (typeof(CombatState), nameof(CombatState.AddCreature)), (typeof(CombatState), nameof(CombatState.RemoveCreature)),
            (typeof(CombatState), nameof(CombatState.SortEnemiesBySlotName)), (typeof(CombatState), nameof(CombatState.SetEnemyIndex)),
            (typeof(Creature), nameof(Creature.ApplyPowerInternal)), (typeof(Creature), nameof(Creature.RemovePowerInternal)),
        })
        {
            try
            {
                if (Reflect.Method(owner, name) is { } creatures)
                {
                    PatchGuard.Patch(harmony, creatures, finalizer: Method(nameof(InvalidateCreatures)));
                }
                else
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: {owner.Name}.{name} not found: not watched");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: {owner.Name}.{name} not watched: {e.Message}");
            }
        }
        // A card's enchantment/affliction is only listed through a pile, and only owned cards sit in piles. Ownerless
        // cards are detached models, e.g. Limitless' enchantment storage cards rebuilt while rendering every card.
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Enchantment>k__BackingField", "set_Enchantment");
        PatchSafety.RequireFieldWriters(typeof(CardModel), "<Affliction>k__BackingField", "set_Affliction");
        _cardOwner = Reflect.FieldRefAccess<CardModel, Player?>("_owner");
        PatchGuard.Patch(harmony, Require(Reflect.PropertySetter(typeof(CardModel), nameof(CardModel.Enchantment))), finalizer: Method(nameof(InvalidateIfOwned)));
        PatchGuard.Patch(harmony, Require(Reflect.PropertySetter(typeof(CardModel), nameof(CardModel.Affliction))), finalizer: Method(nameof(InvalidateIfOwned)));

        // 2. The caches themselves.
        _combatIterate = Require(Reflect.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)));
        _runIterate = Require(Reflect.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) }));
        PatchGuard.Patch(harmony, _combatIterate, prefix: Method(nameof(CombatIteratePrefix)));
        PatchGuard.Patch(harmony, _runIterate, prefix: Method(nameof(RunIteratePrefix)));
        // The augmented-walk premise (shape rules on): the walks' outputs after other mods' postfixes, kept per display scope.
        // Its own part: a failure here leaves only it off (the rest of this class applies as before).
        if (PatchSafety.ShapeRules)
        {
            try
            {
                MethodInfo combatPostfix = Require(Reflect.Method(typeof(ListenerCache), nameof(CombatAugmentedPostfix)));
                MethodInfo runPostfix = Require(Reflect.Method(typeof(ListenerCache), nameof(RunAugmentedPostfix)));
                PatchGuard.Patch(harmony, _combatIterate, postfix: new HarmonyMethod(combatPostfix) { priority = Priority.Last });
                _combatWalkPostfix = combatPostfix;
                PatchGuard.Patch(harmony, _runIterate, postfix: new HarmonyMethod(runPostfix) { priority = Priority.Last });
                _runWalkPostfix = runPostfix;
            }
            catch (Exception e)
            {
                WalkReuseEnabled = false;
                Log.Warn($"[{ModEntry.Id}] {Name}: the augmented walks' reuse not applied (walks with other mods' postfixes as before): {e.Message}");
            }
        }
        // A left run: the lists kept for the next display scope (and HookFilter's filtered ones) would hold it until then.
        if (Reflect.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), nameof(MegaCrit.Sts2.Core.Runs.RunManager.CleanUp)) is { } cleanUp)
        {
            PatchGuard.Patch(harmony, cleanUp, postfix: Method(nameof(RunCleanedUp)));
        }

        // 3. Scopes. Each one is independent: a missing target only loses that scope.
        TryScope(harmony, Reflect.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun)), nameof(EnterSnapshot), nameof(ExitSnapshot));
        // Read-only refresh passes: they only read game state and update visuals/display values.
        TryScope(harmony, Reflect.Method(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues)), nameof(EnterRefreshPatch), nameof(ExitRefresh));
        // One scope around the whole CombatStateChanged dispatch (hand, end turn button, intents, creature state,
        // energy, orbs...): the listener lists are built and filtered once per refresh instead of once per handler. Only
        // while the game code that resumes game logic inside it is the one SuspendPrefix covers (DispatchResumesKnown).
        if (DispatchResumesKnown())
        {
            TryDispatchScope(harmony);
        }
        TryScope(harmony, Reflect.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard)), nameof(EnterDisplayRefresh), nameof(ExitRefresh));
        TryScope(harmony, Reflect.Method(typeof(NCard), nameof(NCard.UpdateVisuals)), nameof(EnterDisplayRefresh), nameof(ExitRefresh));

        // 4. Game logic resuming inside the dispatch scope: run vanilla there (see SuspendPrefix). Required: without it a
        // hand selection ended by a state change would run the card's play on the scope's lists.
        PatchGuard.Patch(harmony, Require(Reflect.Method(typeof(NPlayerHand), "RevalidateSelectionAfterStateChange")),
            prefix: Method(nameof(SuspendPrefix)), finalizer: Method(nameof(ResumeFinalizer)));

        // BaseLib loading later (it is optional): whether card modifiers may exist is looked at again (CardModifiersMayChange).
        if (System.Threading.Interlocked.Exchange(ref _watchingBaseLib, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
        // Answered once at load rather than at the first kept-list hit in a combat (with BaseLib, BaseLibTypes scans the
        // assemblies referencing it); it never throws.
        CardModifiersMayChange();
        // Last: a throw above leaves the scopes vanilla. ModEntry then removes the invalidators, but the scopes ExtraScopes
        // and HookFilter open through this class would still cache (on a version nothing bumps any more) without this.
        _applied = true;
        // What the scope check asks (display only): both walks, and the walk code FastListeners knows (resolved lazily).
        PatchSafety.Watch(Name, new MethodBase?[] { _combatIterate, _runIterate }, PatchTolerance.SurroundingOnly);
        PatchSafety.WatchAll(Name, FastListeners.WalkCodeMethods);
        // Display only: these gate every scope (EnterAny's _safe), any back-off keeps all of it vanilla (the menu's red title).
        PatchSafety.WholePatch(Name);
        // With other mods' postfixes on the walks let stand, the lists come from FastListeners' unpatched mirrors: with that
        // option off (or its mirrors not applied) no scope caches then. Display only.
        PatchSafety.WatchNote(Name, NoteWalkMirrors);
        // Rule NG: other mods' null guards on the walks' Contains, let stand by the scope check (FastListeners.WalkCodePatchedByOthers).
        PatchSafety.WatchNote(Name, () => FastListeners.NoteNullGuard(Name));
        // Kept lists aren't reused across display scopes while card modifiers may change (CardModifiersMayChange).
        PatchSafety.WatchNote(ReuseFault, () => BaseLibTypes.NoteIfDefined(ReuseFault, "card_modifiers", CardModifierType,
            "listener lists are rebuilt in each refresh instead of reused: other mods define BaseLib card modifiers", whole: true));
        PatchSafety.WatchNote(WalkReuseName, NoteAugmentedWalks);
        PatchSafety.WatchNote(SnapshotWalksName, NoteSnapshotWalks);
        PatchSafety.WatchNote(SortWalksName, NoteSortWalks);
    }

    // ---- The augmented-walk premise (Walks Tier 2, owner-approved 2026-11-17; DESIGN "Owner-approved premises") ----

    /// <summary>The augmented walks' reuse (a part of the combat refresh): its switch, off for the session after a mismatch.</summary>
    public const string WalkReuseName = Name + ".Walks";

    public static bool WalkReuseEnabled { get; set; } = true;

    /// <summary>One reuse in this many is compared with a fresh call of the game's walk (its postfixes included).</summary>
    private const int WalkVerifyEvery = 16;

    private static MethodInfo? _combatWalkPostfix, _runWalkPostfix;

    /// <summary>One walk key's kept output in the current outermost scope: the state version it was made at, and whether a reuse of it was checked yet.</summary>
    private sealed class KeptOutput
    {
        public readonly int Version;
        public readonly LazyOutput Output;
        public bool Checked;
        /// <summary>
        /// Reused in the current checksum snapshot or answered card sort: compared with a fresh walk at its end
        /// (<see cref="EndSnapshotWalks"/>, <see cref="EndSortWalks"/>).
        /// </summary>
        public bool InNet;

        public KeptOutput(int version, LazyOutput output)
        {
            Version = version;
            Output = output;
        }
    }

    /// <summary>
    /// Per walk, the output after every other mod's postfix (the first walk's own, <see cref="LazyOutput"/>: pulled only as far as
    /// a caller enumerates), kept from the first walk of the current outermost display scope and state version; dropped at its
    /// end, at a version change (the key's version), when a snapshot, a game-value refresh or a suspension comes in it
    /// (<see cref="_exactScope"/>), and after another mod's CombatStateChanged handler ran (<see cref="EndForeignHandler"/>).
    /// </summary>
    [ThreadStatic] private static Dictionary<(object, object?), KeptOutput>? _augmented;

    /// <summary>
    /// A snapshot, a game-state refresh scope or a suspension (game logic resuming inline) is (or was) in the current outermost
    /// scope: no augmented reuse in it.
    /// </summary>
    [ThreadStatic] private static bool _exactScope;

    /// <summary>
    /// Another mod's CombatStateChanged handler (or one this class can't tell is the game's) is running, at any depth: no
    /// augmented output is kept, reused or compared until it returns (its walks, and every walk of the code it runs or waits on
    /// inline, are the game's walks at the scope's exact tier: the unpatched lists by state version, every other mod's postfix
    /// run at each call, as in the scope's walks without the walk premise). Restored, not decremented, by
    /// <see cref="EndForeignHandler"/>.
    /// </summary>
    [ThreadStatic] private static int _foreignDepth;

    /// <summary>
    /// The current outermost scope's number, moved on too when another mod's CombatStateChanged handler starts and ends: a reused
    /// output enumerated after its scope (or that part of it) ended is the call's own (<see cref="ReusedOutput"/>).
    /// </summary>
    [ThreadStatic] private static int _scopeSerial;

    /// <summary>
    /// Reuses per walk key, surviving across scopes (re-audit of the premises, W-4: a counter per scope never checked a scope
    /// with fewer than 16 reuses). Weak on the state, so a finished combat or run isn't held; the child combat by its identity
    /// hash (two children sharing one only share a count: which reuse is checked, nothing else).
    /// </summary>
    [ThreadStatic] private static ConditionalWeakTable<object, Dictionary<int, int>>? _reuseCounts;

    [ThreadStatic] private static bool _materializingAugmented;

    /// <summary>The last answer of <see cref="PostfixLast"/> per walk: a record replaced whole (no torn read).</summary>
    private sealed record LastVerdict(int Epoch, long At, bool Last);

    private static LastVerdict? _combatLast, _runLast;

#if BENCH
    /// <summary>Augmented walks answered from a kept output, outputs materialized, reuses compared and the differences (the effects check).</summary>
    public static long WalksReused { get; private set; }
    public static long WalksMaterialized { get; private set; }
    public static long WalksVerified { get; private set; }
#endif
    public static long WalkMismatches { get; private set; }

    /// <summary>
    /// Whether an augmented walk's output may be kept and reused now: the shape rules on, the part on, inside a scope this class
    /// serves (<see cref="Serving"/>) whose outermost scope opened no snapshot nor game-state refresh, the walk carrying other
    /// mods' postfixes (<paramref name="augmented"/>), and this class's postfix the last one Harmony runs there. Not once
    /// FastListeners' unpatched mirror declined in this scope (<see cref="_mirrorDeclined"/>: another mod patched the walk's code
    /// mid-scope; re-audit of the premises, W-6).
    /// </summary>
    private static bool AugmentedReuse(bool augmented, MethodBase? walk, MethodInfo? postfix, ref LastVerdict? last) =>
        PatchSafety.ShapeRules && augmented && ReuseOpen && postfix != null && PostfixLast(walk, postfix, ref last);

    /// <summary>
    /// The part of <see cref="AugmentedReuse"/> that doesn't depend on the walk: the part on, no exact mode, no output being
    /// pulled, no declined mirror, inside a scope this class serves, and no other mod's CombatStateChanged handler running
    /// (<see cref="_foreignDepth"/>).
    /// </summary>
    private static bool ReuseOpen =>
        PartOn && !_exactScope && _foreignDepth == 0 && !_materializingAugmented && !_mirrorDeclined && Serving;

    /// <summary>
    /// The part this scope's reuse belongs to is on: in a checksum snapshot (<see cref="_snapshotReuse"/>) its own part, at the
    /// state version the snapshot started at; in an answered card sort (<see cref="_sortReuse"/>) its own part, at the state version
    /// the sort started at and not while CardSortKeys checks an answer against the game's comparison (<see cref="PauseSort"/>);
    /// elsewhere the display refreshes' part. The display part's switch and session faults never decide anything in a snapshot or
    /// a sort (peer determinism, DESIGN "Snapshot walk reuse").
    /// </summary>
    private static bool PartOn => _snapshotReuse ? SnapshotWalksEnabled && _version == _snapshotVersion
        : _sortReuse ? SortWalksEnabled && !_sortPaused && _version == _sortVersion
        : WalkReuseEnabled;

    /// <summary>
    /// This class's postfix runs after every other mod's on the walk (Harmony's sort, asked again when Harmony's epoch moves, each
    /// second without HarmonyWatch): else what it keeps would be read by a postfix after it. A factory-made postfix: no (its sort
    /// would run another mod's code here).
    /// </summary>
    private static bool PostfixLast(MethodBase? walk, MethodInfo postfix, ref LastVerdict? last)
    {
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (last is { } known && known.Epoch == epoch && (HarmonyWatch.Applied || now - known.At < 1000))
        {
            return known.Last;
        }
        bool isLast = false;
        try
        {
            if (walk != null && Harmony.GetPatchInfo(walk) is { } info)
            {
                Patch[] postfixes = info.Postfixes.ToArray();
                var methods = new MethodInfo[postfixes.Length];
                bool ok = true;
                for (int i = 0; i < postfixes.Length && ok; i++)
                {
                    MethodInfo? method = PatchSafety.PatchMethodOf(postfixes[i]);
                    ok = method != null && !PatchSafety.IsFactory(method);
                    if (ok)
                    {
                        postfixes[i].PatchMethod = method;
                    }
                }
                if (ok)
                {
                    List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(walk, postfixes);
                    isLast = sorted.Count > 0 && sorted[^1].MethodHandle == postfix.MethodHandle
                        && sorted.Count(m => m.MethodHandle == postfix.MethodHandle) == 1;
                }
            }
        }
        catch
        {
            isLast = false;
        }
        last = new LastVerdict(epoch, now, isLast);
        return isLast;
    }

    private static void CombatAugmentedPostfix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
    {
        if (!AugmentedReuse(_combatAugmented, _combatIterate, _combatWalkPostfix, ref _combatLast))
        {
            return;
        }
        __result = Augmented((__instance, null), __result);
    }

    private static void RunAugmentedPostfix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result)
    {
        if (!AugmentedReuse(_runAugmented, _runIterate, _runWalkPostfix, ref _runLast))
        {
            return;
        }
        __result = Augmented((__instance, childCombatState), __result);
    }

    /// <summary>
    /// The walk's output for the caller: the one kept for this scope and version, or this call's own, kept. The first reuse of
    /// each key in each scope and one in <see cref="WalkVerifyEvery"/> per key after it get this call's own output instead,
    /// compared with the kept one as the caller enumerates it (<see cref="CheckedOutput"/>). Nothing is enumerated here: the
    /// kept output is pulled only as far as a caller goes (<see cref="LazyOutput"/>, re-audit of the premises W-2: an eager
    /// materialization ran other mods' code for listeners a caller stopping early never reaches).
    /// </summary>
    private static IEnumerable<AbstractModel> Augmented((object, object?) key, IEnumerable<AbstractModel> output)
    {
        try
        {
            _augmented ??= new Dictionary<(object, object?), KeptOutput>(new RefComparer());
#if BENCH
            // This key's first walk since another mod's handler returned while it was kept: must be kept anew, never reused.
            bool afterForeign = _keysAtForeignEnd != null && _keysAtForeignEnd.Remove(key);
#endif
            if (_augmented.TryGetValue(key, out KeptOutput? entry) && entry.Version == _version)
            {
#if BENCH
                if (afterForeign)
                {
                    ReusedAfterForeign++;
                }
#endif
                // D1 (re-audit of c2f613b): Checked is set by a check that covered everything its caller read (CheckedOutput), not
                // when a check is handed out. D2: the per-key sample's position moves with the scope (display only). In a checksum
                // snapshot the schedule is the snapshot's own (its first reuse of each key until one covered what its caller read,
                // none after it): no count kept across scopes, so every peer checks the same walks for the same state (R3-3). In an
                // answered card sort, the same schedule as the snapshot's (owner's decision of 2026-11-20): its first reuse of each
                // key until one covered what its caller read, none after it, and every key it reused compared with one fresh walk
                // at the sort's end (EndSortWalks; a synchronized game action: every peer answering it checks the same walks).
                bool check = _snapshotReuse || _sortReuse ? !entry.Checked
                    : !entry.Checked || CountReuse(key) % WalkVerifyEvery == 0;
                if (_snapshotReuse && !entry.InNet)
                {
                    // Compared with a fresh walk at the snapshot's end, before the snapshot is used (EndSnapshotWalks).
                    entry.InNet = true;
                    (_snapshotReused ??= new List<KeptOutput>()).Add(entry);
                }
                if (!check)
                {
                    if (_sortReuse && !entry.InNet)
                    {
                        // Handed to a walk after its first: compared with a fresh walk at the sort's end (EndSortWalks).
                        entry.InNet = true;
                        (_sortReused ??= new List<KeptOutput>()).Add(entry);
                    }
#if BENCH
                    if (_snapshotReuse)
                    {
                        SnapshotWalkReuses++;
                    }
                    else if (_sortReuse)
                    {
                        SortWalkReuses++;
                    }
                    else
                    {
                        WalksReused++;
                    }
                    if (_inGameHandler)
                    {
                        GameHandlerReuses++;
                    }
#endif
                    return new ReusedOutput(entry.Output, output, _scopeSerial, _version);
                }
#if BENCH
                if (_snapshotReuse)
                {
                    SnapshotWalkChecks++;
                }
                else if (_sortReuse)
                {
                    SortWalkChecks++;
                }
                else
                {
                    WalksVerified++;
                }
#endif
                return new CheckedOutput(output, entry.Output, key, _scopeSerial, _version, entry);
            }
            // D3: a kept output of another state version is closed (its source disposed once no caller is in it).
            entry?.Output.Close();
            var lazy = new LazyOutput(output, key);
            var kept = new KeptOutput(_version, lazy);
            _augmented[key] = kept;
            if (_snapshotReuse)
            {
                // Every output a snapshot keeps is compared by its net, a key walked once too (its pulls then interleaved with that
                // caller's loop only, but the comparison costs one fresh walk per key, and nothing is left to an argument).
                kept.InNet = true;
                (_snapshotReused ??= new List<KeptOutput>()).Add(kept);
            }
#if BENCH
            if (_snapshotReuse)
            {
                SnapshotWalksKept++;
            }
            else if (_sortReuse)
            {
                SortWalksKept++;
            }
            else
            {
                WalksMaterialized++;
            }
            if (afterForeign)
            {
                KeptAfterForeign++;
            }
#endif
            // D6: the first walk's own caller is guarded as the reuses are (outside its scope, or once the scope's reuse ended,
            // the call's own output).
            return new ReusedOutput(lazy, output, _scopeSerial, _version);
        }
        catch (Exception e)
        {
            // This class's own error, before the output was enumerated: the part off, this call's output as the game made it.
            WalkReuseFailed(e);
            return output;
        }
    }

    /// <summary>
    /// D2 (re-audit of c2f613b; review of the memo, 2026-11-17): where in each run of <see cref="WalkVerifyEvery"/> uses a
    /// counter's sample falls, fixed per counter (a walk key's reuses, a memo's uses) and spread between counters (0 in the
    /// self-test). Each counter then samples exactly one use in 16; an offset moving with the scope could skip or repeat
    /// samples (aliasing with the scope's count of uses).
    /// </summary>
    private static int StartOffset() => SelfTesting ? 0 : (int)((uint)(_nextStart++ * 7) % WalkVerifyEvery);

    [ThreadStatic] private static int _nextStart;

    /// <summary>
    /// D3 (re-audit of c2f613b): every kept output closed (an unfinished source disposed once no caller is in it: other mods'
    /// iterators' finally blocks run, EnchantLib returns its ArrayPool arrays there), then dropped.
    /// </summary>
    private static void ClearAugmented()
    {
        Dictionary<(object, object?), KeptOutput>? kept = _augmented;
        if (kept == null || kept.Count == 0)
        {
            return;
        }
        KeptOutput[] entries = kept.Values.ToArray();
        kept.Clear();
        foreach (KeptOutput entry in entries)
        {
            entry.Output.Close();
        }
    }

    private static int CountReuse((object, object?) key)
    {
        _reuseCounts ??= new ConditionalWeakTable<object, Dictionary<int, int>>();
        Dictionary<int, int> counts = _reuseCounts.GetValue(key.Item1, _ => new Dictionary<int, int>());
        int child = key.Item2 == null ? 0 : RuntimeHelpers.GetHashCode(key.Item2);
        if (!counts.TryGetValue(child, out int count))
        {
            count = StartOffset();
        }
        counts[child] = ++count;
        return count;
    }

    private static void WalkReuseFailed(Exception e)
    {
        if (_snapshotReuse)
        {
            // In a checksum snapshot: that snapshot is built again the game's way (its net), nothing turned off for the session.
            SnapshotTaint("this class's error: " + e.Message);
            return;
        }
        if (_sortReuse)
        {
            SortWalksFailed("this class's error: " + e);
            return;
        }
        WalkReuseEnabled = false;
        ClearAugmented();
        PatchSafety.Fault(WalkReuseName);
        Log.Error($"[{ModEntry.Id}] {Name}: the augmented walks' reuse failed, off for this session: {e}");
    }

    private static void WalkMismatch((object, object?) key, string what)
    {
        if (_snapshotReuse)
        {
            // A checked reuse in a checksum snapshot (CheckedOutput compares only in the scope it was made for): that snapshot is
            // built again the game's way before it is used; the display part and its session state are not touched.
            SnapshotTaint($"a checked reuse differs ({what})");
            return;
        }
        if (_sortReuse)
        {
            // A checked reuse in an answered card sort (compared only in the scope it was made for): the sort's part off for the
            // session, nothing reused in the rest of this sort; the display part is not touched.
#if BENCH
            if (_selfTesting)
            {
                _selfTestMismatches++;
                ExactFromHere();
                return;
            }
#endif
            SortWalkMismatches++;
            SortWalksFailed($"a kept listener walk output differs from a fresh one at its checked reuse ({KeyName(key)}: {what})");
            return;
        }
#if BENCH
        if (_selfTesting)
        {
            _selfTestMismatches++;
            return;
        }
#endif
        if (!WalkReuseEnabled)
        {
            return;
        }
        WalkMismatches++;
        WalkReuseEnabled = false;
        ClearAugmented();
        PatchSafety.Fault(WalkReuseName);
        Log.Error($"[{ModEntry.Id}] {Name}: a kept listener walk output (other mods' postfixes included) differs from a fresh one "
            + $"({key.Item1.GetType().Name}{(key.Item2 == null ? "" : " with a child combat")}: {what}): their reuse turned off for this session. Please report this log.");
    }

    /// <summary>
    /// A walk's own output (the game's call, other mods' postfixes included), enumerated only as far as its callers go, one
    /// element at a time, interleaved with their loops as the game's would be; what was pulled is replayed for the scope's later
    /// walks of that key, which pull further if they go further. A throw from the output's code reaches the caller at the
    /// element the game's enumeration would have met it, and every later caller of this output there too; the key is then no
    /// longer reused. The source is disposed at its end or on its throw, as a foreach does; a source no caller enumerated to its
    /// end is not disposed when the callers stop (a later walk may go further) but left to the collector (the game's iterators'
    /// finally blocks only dispose list enumerators: a stated part of the premise). Not a list: an enumerable only, as the game's
    /// iterator (nothing to test, cast or change). Internal only for <see cref="FilteredMemo"/> (KeptOnly's memo over it).
    /// </summary>
    internal sealed class LazyOutput : IEnumerable<AbstractModel>
    {
        private readonly IEnumerable<AbstractModel> _source;
        private readonly (object, object?) _key;
        private readonly List<AbstractModel> _items = new();
        private IEnumerator<AbstractModel>? _enumerator;
        private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _error;
        private bool _done;
        private bool _pulling;
        /// <summary>Callers enumerating it now, and a close asked while one was (D3: done when the last one leaves).</summary>
        private int _consumers;
        private bool _closeAsked;

        public LazyOutput(IEnumerable<AbstractModel> source, (object, object?) key)
        {
            _source = source;
            _key = key;
        }

        /// <summary>Its walk key: (combat state, null) or (run state, child).</summary>
        public (object, object?) Key => _key;

        /// <summary>The elements pulled so far, and whether the source reached its end (or threw, or was closed).</summary>
        public int Count => _items.Count;
        public bool Done => _done;
        /// <summary>Its source is running now (a reuse started from inside it takes its own output).</summary>
        public bool Pulling => _pulling;
        /// <summary>The source reached its end (not a throw, not closed early): its length is known.</summary>
        public bool Complete { get; private set; }
        /// <summary>Its source threw (every caller met that exception at the same element).</summary>
        public bool Failed => _error != null;
        /// <summary>
        /// Closed, or a close asked while a caller was in it (round 4): what it pulled is all it will ever give, so a reuse handed
        /// out before and enumerated now would be cut short there (<see cref="ReusedOutput"/> gives the call's own output then).
        /// </summary>
        public bool Closed => _closed || _closeAsked;
        private bool _closed;
        public AbstractModel this[int index] => _items[index];

        public IEnumerator<AbstractModel> GetEnumerator()
        {
            Enter();
            try
            {
                for (int i = 0; ; i++)
                {
                    if (i >= _items.Count && !Pull())
                    {
                        yield break;
                    }
                    yield return _items[i];
                }
            }
            finally
            {
                Leave();
            }
        }

        /// <summary>
        /// A caller starts enumerating it (its own enumerator, or KeptOnly's memo over it: <see cref="FilteredMemo"/>), and leaves
        /// it: a close asked meanwhile is done when the last one leaves (D3), whichever way they read it.
        /// </summary>
        internal void Enter() => _consumers++;

        internal void Leave()
        {
            _consumers--;
            if (_closeAsked && _consumers == 0)
            {
                Close();
            }
        }

        /// <summary>
        /// Element <paramref name="index"/>, pulled if not yet (as the enumerator's step to it would: its end, a throw met
        /// there and rethrown to every later caller, the source disposed). For a caller between <see cref="Enter"/> and
        /// <see cref="Leave"/>.
        /// </summary>
        internal bool TryAt(int index, out AbstractModel model)
        {
            while (index >= _items.Count)
            {
                if (!Pull())
                {
                    model = null!;
                    return false;
                }
            }
            model = _items[index];
            return true;
        }

        /// <summary>Type ids of the pulled elements, worked out once per element for every filter's memo (0: not yet, else id + 1).</summary>
        private int[] _typeIds = Array.Empty<int>();

        /// <summary>
        /// FastListeners' type id of pulled element <paramref name="index"/> (<paramref name="idOf"/>, main thread only), worked
        /// out once and kept: an object's type never changes and a type's id is never given again (the ids only grow).
        /// </summary>
        internal int TypeIdAt(int index, Func<AbstractModel, int> idOf)
        {
            if (index >= _typeIds.Length)
            {
                Array.Resize(ref _typeIds, Math.Max(index + 1, Math.Max(64, _typeIds.Length * 2)));
            }
            int known = _typeIds[index];
            if (known == 0)
            {
                known = idOf(_items[index]) + 1;
                _typeIds[index] = known;
            }
            return known - 1;
        }

        /// <summary>KeptOnly's memos over this output, per filter (<see cref="FilteredMemo"/>): they live and go with it.</summary>
        private Dictionary<object, FilteredMemo>? _memos;

        /// <summary>The memos over this output now, with their filters (the snapshot net compares them).</summary>
        internal KeyValuePair<object, FilteredMemo>[] MemoList() =>
            _memos == null ? Array.Empty<KeyValuePair<object, FilteredMemo>>() : _memos.ToArray();

        /// <summary>This filter's memo over this output, made now if it has none (<paramref name="built"/>).</summary>
        internal FilteredMemo MemoOf(object filter, out bool built)
        {
            _memos ??= new Dictionary<object, FilteredMemo>(ReferenceEqualityComparer.Instance);
            built = !_memos.TryGetValue(filter, out FilteredMemo? memo);
            if (built)
            {
                memo = new FilteredMemo(this);
                _memos[filter] = memo;
            }
            return memo!;
        }

        /// <summary>
        /// Every memo over this output turned off and dropped (the output closed, the memo's part turned off): a caller still
        /// in one goes on with KeptOnly's own loop from where it is, over this output, as without the memo.
        /// </summary>
        internal void DropMemos()
        {
            Dictionary<object, FilteredMemo>? memos = _memos;
            _memos = null;
            if (memos != null)
            {
                foreach (FilteredMemo memo in memos.Values)
                {
                    memo.Off = true;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// D3 (re-audit of c2f613b): no later walk pulls it (its scope ended, exact mode started, another version): an unfinished
        /// source is disposed, as a foreach leaving early would, so other mods' iterators run their finally blocks. Not while a
        /// caller is in it or its source runs (closed when the last one leaves): what a caller enumerates goes on. Never throws.
        /// </summary>
        public void Close()
        {
            // Its memos go with it at once (a complete output too: no later walk reads it), also when the close waits for a
            // caller (a version replacing it, exact mode, a drop): a memo caller in it goes on with KeptOnly's own loop over it.
            DropMemos();
            if (_consumers > 0 || _pulling)
            {
                _closeAsked = true;
                return;
            }
            _closeAsked = false;
            IEnumerator<AbstractModel>? open = _enumerator;
            if (open == null || _done)
            {
                return;
            }
            _enumerator = null;
            _done = true;
            _closed = true;
            Complete = false;
            bool wasMaterializing = _materializingAugmented;
            _materializingAugmented = true;
            try
            {
                open.Dispose();
            }
            catch (Exception e)
            {
                // Another mod's finally block threw: logged, nothing else to do (no caller is in it).
                Log.Info($"[{ModEntry.Id}] {Name}: disposing a kept listener walk output threw: {e.Message}");
            }
            finally
            {
                _materializingAugmented = wasMaterializing;
            }
        }

        /// <summary>One more element from the source: false at its end; its exception rethrown (now and for every later caller).</summary>
        private bool Pull()
        {
            if (_error != null)
            {
                _error.Throw();
            }
            if (_done)
            {
                return false;
            }
            if (_pulling)
            {
                // The source's own code enumerating this output again: never seen; this class's error, not the game's.
                throw new InvalidOperationException("a kept listener walk output enumerated from inside its own enumeration");
            }
            bool wasMaterializing = _materializingAugmented;
            // A walk called by the output's own code (another mod's postfix) gets its own output, never this one.
            _materializingAugmented = true;
            _pulling = true;
            try
            {
                _enumerator ??= _source.GetEnumerator();
                if (_enumerator.MoveNext())
                {
                    _items.Add(_enumerator.Current);
                    return true;
                }
                _done = true;
                Complete = true;
                // As a foreach at its end.
                IEnumerator<AbstractModel> finished = _enumerator;
                _enumerator = null;
                finished.Dispose();
                return false;
            }
            catch (Exception e)
            {
                _done = true;
                Complete = false;
                _error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                // As a foreach leaving on a throw: the enumerator disposed, the exception passed on.
                IEnumerator<AbstractModel>? failed = _enumerator;
                _enumerator = null;
                try
                {
                    failed?.Dispose();
                }
                catch
                {
                    // The first exception is the one the caller meets.
                }
                Forget();
                throw;
            }
            finally
            {
                _pulling = false;
                _materializingAugmented = wasMaterializing;
            }
        }

        /// <summary>No later walk reuses it (a throw while pulling: they get their own outputs, as in the game).</summary>
        private void Forget()
        {
            if (_augmented != null && _augmented.TryGetValue(_key, out KeptOutput? entry) && ReferenceEquals(entry.Output, this))
            {
                _augmented.Remove(_key);
            }
        }

    }

    /// <summary>
    /// A reuse handed to a walk's caller: the kept output while enumerated inside the scope it was kept for, reuse still allowed
    /// there (no snapshot, game-value refresh, suspension or other mod's CombatStateChanged handler since, the part on); otherwise this call's own
    /// output, the game's (re-audit of the premises, W-1b: a kept output never reaches code outside its display scope, even an
    /// enumerable kept by a caller and enumerated later). Round 4: also at the state version it was handed out at, and the kept
    /// output not closed (a version bump in the scope, then another walk of that key, closes it: enumerated again, a held reuse
    /// would have given only what was pulled before; the game's own output, lazy, reads the state as it is then).
    /// </summary>
    private sealed class ReusedOutput : IEnumerable<AbstractModel>
    {
        private readonly LazyOutput _kept;
        private readonly IEnumerable<AbstractModel> _own;
        private readonly int _scope, _version;

        public ReusedOutput(LazyOutput kept, IEnumerable<AbstractModel> own, int scope, int version)
        {
            _kept = kept;
            _own = own;
            _scope = scope;
            _version = version;
        }

        /// <summary>Whether enumerating it now gives the kept output (else the call's own).</summary>
        private bool InScope => _scope == _scopeSerial && _version == ListenerCache._version && !_kept.Closed && PartOn && !_exactScope
            && _foreignDepth == 0 && !_mirrorDeclined && Serving && !_kept.Pulling;

        /// <summary>
        /// The kept output if enumerating this reuse now would give it (<see cref="GetEnumerator"/>'s own test, nothing counted),
        /// else null: KeptOnly's memo (<see cref="MemoFor"/>) stands for an enumeration of exactly that output.
        /// </summary>
        public LazyOutput? KeptNow() => InScope ? _kept : null;

        public IEnumerator<AbstractModel> GetEnumerator()
        {
            bool inScope = InScope;
#if BENCH
            if (!inScope)
            {
                ReusesOutOfScope++;
            }
#endif
            return (inScope ? _kept : _own).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

#if BENCH
    /// <summary>Reused outputs enumerated outside the scope they were kept for: given the call's own output (W-1b's test).</summary>
    public static long ReusesOutOfScope { get; private set; }
#endif

    // ---- KeptOnly's memo over a kept walk output (Walks Tier 2: the filtered walks of a display refresh) ----

    /// <summary>The memo of the filtered walks over kept walk outputs (a part of the combat refresh): its switch, off for the session after a difference or an error.</summary>
    public const string WalkMemoName = Name + ".WalkMemo";

    public static bool WalkMemoEnabled { get; set; } = true;

    /// <summary>
    /// One filter's <c>FastListeners.KeptOnly</c> result over one kept walk output, built as its callers go: the kept elements
    /// in order, each one's index in the output, how many of the output's elements were judged, whether the output's end was
    /// reached. A filtered walk whose walk is a <see cref="ReusedOutput"/> giving the kept output when enumerated (the call
    /// KeptOnly's loop would make) replays it, judging further elements only when it goes further, so the kept output is pulled
    /// exactly as far as KeptOnly's own loop over it would pull it. A stacked enchantment is a live entry, judged at every
    /// replay (its verdict reads its inner enchantments). Lives with its output (<see cref="LazyOutput.DropMemos"/>:
    /// its close at the scope's end, a version change of its key, exact mode, after another mod's CombatStateChanged handler,
    /// the parts' faults). Off: callers go on with KeptOnly's own loop from where they are (a verdict that threw, a drop).
    /// Main thread only.
    /// </summary>
    internal sealed class FilteredMemo
    {
        public readonly LazyOutput Source;
        public readonly List<AbstractModel> Kept = new();
        public readonly List<int> KeptAt = new();
        /// <summary>Per entry: a live one (a stacked enchantment), judged with the filter's Keeps at every replay, kept or not then.</summary>
        public readonly List<bool> Live = new();
        public int Judged;
        public bool Done;
        public bool Off;
#if BENCH
        /// <summary>The filter would have dropped the first element (KeptOnly keeps it: A4): FastListeners.FirstKept counts each replay.</summary>
        public bool FirstWouldDrop;
        /// <summary>The first element is a live one: FastListeners.FirstKept's test runs on it at each replay, as KeptOnly's loop.</summary>
        public bool FirstLive;
#endif

        /// <summary>Its filter's uses over its walk key, across scopes (<see cref="MemoStats"/>).</summary>
        internal MemoStats? Stats;

        /// <summary>A checked use of it covered what its caller read (its output lives in one scope: the snapshot's schedule).</summary>
        public bool CheckedHere;

        public FilteredMemo(LazyOutput source)
        {
            Source = source;
        }
    }

    /// <summary>
    /// Per filter and walk key ((combat state) or (run state, child), as W-4's per-key counts), across scopes: the memos' uses
    /// (from a fixed start, <see cref="StartOffset"/>) and whether one was checked yet. The first use of a filter's memo over a
    /// key is checked, then one in 16. Weak on the state; the child combat by its identity hash (as <see cref="CountReuse"/>).
    /// </summary>
    internal sealed class MemoStats
    {
        public int Uses;
        public bool Checked;
    }

    [ThreadStatic] private static ConditionalWeakTable<object, Dictionary<(object Filter, int Child), MemoStats>>? _memoStats;

    private static MemoStats StatsOf(object filter, (object, object?) key)
    {
        _memoStats ??= new ConditionalWeakTable<object, Dictionary<(object Filter, int Child), MemoStats>>();
        Dictionary<(object Filter, int Child), MemoStats> byFilter = _memoStats.GetValue(key.Item1, _ => new Dictionary<(object Filter, int Child), MemoStats>());
        var at = (filter, key.Item2 == null ? 0 : RuntimeHelpers.GetHashCode(key.Item2));
        if (!byFilter.TryGetValue(at, out MemoStats? stats))
        {
            stats = new MemoStats { Uses = StartOffset() };
            byFilter[at] = stats;
        }
        return stats;
    }

#if BENCH
    /// <summary>Filtered walks answered from a memo, memos made, memo uses compared with KeptOnly's own loop (the effects check).</summary>
    public static long MemoHits { get; private set; }
    public static long MemoBuilt { get; private set; }
    public static long MemoVerified { get; private set; }

    /// <summary>Test-only injections (<c>foreignpatch keptmemo fault|wrong</c>): an error in the memo's own code, a memo made different.</summary>
    private static bool _memoInjectFault, _memoInjectWrong;
#endif
    public static long MemoMismatches { get; private set; }

    /// <summary>
    /// The memo of <paramref name="filter"/> over the kept output <paramref name="walk"/> gives when enumerated now, or null:
    /// the walk isn't a reuse, or enumerating it now gives the call's own output (<see cref="ReusedOutput.KeptNow"/>: outside
    /// its scope, a version or exact mode since, another mod's handler running, its output closed or running), or the part is
    /// off, or the memo is off. Called by KeptOnly at its first step, where its loop would call the walk's GetEnumerator.
    /// <paramref name="check"/>: this use runs KeptOnly's own loop, compared with the memo (the first use per filter, then one
    /// in <see cref="WalkVerifyEvery"/>). Never throws: an error here turns the part off and KeptOnly runs its own loop.
    /// </summary>
    internal static FilteredMemo? MemoFor(IEnumerable<AbstractModel> walk, object filter, out bool check)
    {
        check = false;
        if (!MemoOn || walk is not ReusedOutput reused)
        {
            return null;
        }
        try
        {
            LazyOutput? kept = reused.KeptNow();
            if (kept == null)
            {
                return null;
            }
#if BENCH
            if (_memoInjectFault)
            {
                _memoInjectFault = false;
                throw new InvalidOperationException("injected memo fault (foreignpatch keptmemo fault)");
            }
#endif
            FilteredMemo memo = kept.MemoOf(filter, out bool built);
            if (memo.Off)
            {
                return null;
            }
            if (built)
            {
#if BENCH
                MemoBuilt++;
#endif
                return memo;
            }
            if (_snapshotReuse)
            {
                // In a checksum snapshot: the snapshot's own schedule (this memo's first replay in it until one covered what its
                // caller read), no count kept across scopes: every peer checks the same uses for the same state.
                check = !memo.CheckedHere;
            }
            else
            {
                MemoStats stats = memo.Stats ??= StatsOf(filter, kept.Key);
                check = !stats.Checked || ++stats.Uses % WalkVerifyEvery == 0;
            }
#if BENCH
            if (_memoInjectWrong && memo.Kept.Count >= 2)
            {
                // The control: a memo made different (its first kept element gone: compared by any caller reading one), and
                // this use checked: the caller gets KeptOnly's own loop, the comparison must find the difference and turn the part off.
                _memoInjectWrong = false;
                memo.Kept.RemoveAt(0);
                memo.KeptAt.RemoveAt(0);
                memo.Live.RemoveAt(0);
                check = true;
            }
            if (check)
            {
                MemoVerified++;
            }
            else
            {
                MemoHits++;
            }
#endif
            return memo;
        }
        catch (Exception e)
        {
            check = false;
            WalkMemoFailed(e);
            return null;
        }
    }

    /// <summary>A checked use ended: it compared everything its caller read (the filter's memos over that key then sampled one in 16), or not (checked again next time).</summary>
    internal static void MemoCheckDone(FilteredMemo memo, bool covered)
    {
        if (covered)
        {
            memo.CheckedHere = true;
            if (memo.Stats != null)
            {
                memo.Stats.Checked = true;
            }
        }
    }

    /// <summary>
    /// The memo may be used now: while its part is on, in a checksum snapshot too (a memo faulted in a display refresh stays off
    /// there). The memo is exact, KeptOnly's result over the same output, so whether a peer uses it never changes a snapshot; in
    /// a snapshot its checks follow the snapshot's own schedule, and the net compares every memo used there with KeptOnly's
    /// loop over the fresh walk (<see cref="EndSnapshotWalks"/>).
    /// </summary>
    internal static bool MemoOn => WalkMemoEnabled;

    /// <summary>A memo differs from KeptOnly's own loop over the same output: the part off for the session, every memo dropped, logged.</summary>
    internal static void MemoMismatch(string what)
    {
        if (_snapshotReuse)
        {
            // Callers of this checksum snapshot read the memo before: the snapshot is built again the game's way before it is used.
            SnapshotTaint($"a memoized filtered walk differs ({what})");
        }
#if BENCH
        if (_selfTesting)
        {
            _selfTestMismatches++;
            return;
        }
#endif
        if (!WalkMemoEnabled)
        {
            return;
        }
        MemoMismatches++;
        WalkMemoEnabled = false;
        DropAllMemos();
        PatchSafety.Fault(WalkMemoName);
        Log.Error($"[{ModEntry.Id}] {Name}: a memoized filtered walk differs from the filter run again over the same kept walk output ({what}): "
            + "the memo turned off for this session (filtered walks run their own loop, as before). Please report this log.");
    }

    /// <summary>An error in the memo's own code (never the filter's verdicts, never the output's): the part off for the session, logged.</summary>
    internal static void WalkMemoFailed(Exception e)
    {
        if (_snapshotReuse)
        {
            SnapshotTaint("the filtered walks' memo failed: " + e.Message);
        }
#if BENCH
        if (_selfTesting)
        {
            _selfTestMemoFaults++;
            return;
        }
#endif
        if (!WalkMemoEnabled)
        {
            return; // Off already: logged once.
        }
        WalkMemoEnabled = false;
        DropAllMemos();
        PatchSafety.Fault(WalkMemoName);
        Log.Error($"[{ModEntry.Id}] {Name}: the filtered walks' memo failed, off for this session (filtered walks run their own loop, as before): {e}");
    }

    /// <summary>Every memo over the kept outputs turned off and dropped (callers in one go on with KeptOnly's own loop). Never throws.</summary>
    private static void DropAllMemos()
    {
        try
        {
            if (_augmented != null)
            {
                foreach (KeptOutput entry in _augmented.Values)
                {
                    entry.Output.DropMemos();
                }
            }
        }
        catch
        {
            // The part is off: no memo is used again whatever was left.
        }
    }

#if BENCH
    /// <summary>
    /// <c>foreignpatch keptmemo fault|wrong|status|require on|require off</c>: the memo's controls (test builds; a fault is for
    /// the session, as any part's), its counters, and whether it is on (the walk reuse with it) or off (faulted, a difference logged).
    /// </summary>
    internal static string MemoControl(string what)
    {
        string status = $"FOREIGN keptmemo built {MemoBuilt}, hits {MemoHits}, checked {MemoVerified}, mismatches {MemoMismatches}, "
            + $"on {WalkMemoEnabled}, faulted {PatchSafety.IsFaulted(WalkMemoName)}, walk reuse on {WalkReuseEnabled}, live verdicts {FastListeners.MemoLiveJudged}";
        switch (what)
        {
            case "fault":
                _memoInjectFault = true;
                return "FOREIGN keptmemo: the next memo lookup throws";
            case "wrong":
                _memoInjectWrong = true;
                return "FOREIGN keptmemo: the next memo use with two kept elements loses its first one and is checked";
            case "status":
                return status;
            case "require on":
                // Memos made and used, every check equal, the walk reuse still on.
                if (!WalkMemoEnabled || MemoMismatches > 0 || MemoBuilt == 0 || MemoHits == 0 || !WalkReuseEnabled)
                {
                    throw new InvalidOperationException(status);
                }
                return status;
            case "require off":
                // The control: the memo off and faulted, the walk reuse itself still on (only the memo falls back).
                if (WalkMemoEnabled || !PatchSafety.IsFaulted(WalkMemoName) || !WalkReuseEnabled)
                {
                    throw new InvalidOperationException(status);
                }
                return status;
            default:
                throw new ArgumentException("foreignpatch keptmemo fault|wrong|status|require on|require off");
        }
    }

    [ThreadStatic] private static int _selfTestMemoFaults;

    /// <summary>
    /// <c>foreignpatch snapwalks status</c>, <c>foreignpatch require snapwalks on|detected</c>: the checksum snapshot's walk reuse's
    /// counters, and its checks. on: walks reused and compared by the net, no detection, the part on and not faulted, no live
    /// re-check (SnapshotVerifier) differing. detected: the net found snapshots wrong and they were built again, the part still
    /// on (per snapshot, nothing turned off), no live re-check differing (each snapshot used was the game's).
    /// </summary>
    internal static string SnapshotWalksControl(string what)
    {
        string status = $"FOREIGN snapwalks kept {SnapshotWalksKept}, reused {SnapshotWalkReuses}, checked {SnapshotWalkChecks}, net compared {SnapshotNetCompared}, "
            + $"detections {SnapshotDetections}, net errors {SnapshotNetErrors}, memos compared {SnapshotMemosCompared}, seal changes {SnapshotSealChanges} "
            + $"(RNGs not put back {SnapshotSealUnrestored}), rebuilt {SnapshotVerifier.Rebuilt}, re-checked {SnapshotVerifier.Verified}, "
            + $"re-check mismatches {SnapshotVerifier.Mismatches}, re-checks discarded {SnapshotVerifier.ReChecksDiscarded}, on {SnapshotWalksEnabled}, faulted {PatchSafety.IsFaulted(SnapshotWalksName)}, "
            + $"co-op tag '{CoopModeTag.Suffix}'";
        switch (what)
        {
            case "status":
                return status;
            case "require on":
                if (SnapshotWalkReuses == 0 || SnapshotNetCompared == 0 || SnapshotDetections > 0 || !SnapshotWalksEnabled
                    || PatchSafety.IsFaulted(SnapshotWalksName) || SnapshotVerifier.Mismatches > 0)
                {
                    throw new InvalidOperationException(status);
                }
                return status;
            case "require sealed":
                // RNGs drawn during the snapshots (walk-snaprng-postfix): every one put back, each such snapshot rebuilt.
                if (SnapshotSealChanges == 0 || SnapshotSealUnrestored > 0 || SnapshotDetections == 0 || SnapshotVerifier.Rebuilt == 0
                    || SnapshotVerifier.Mismatches > 0 || SnapshotVerifier.ReChecksDiscarded > 0
                    || !SnapshotWalksEnabled || PatchSafety.IsFaulted(SnapshotWalksName))
                {
                    throw new InvalidOperationException(status);
                }
                return status;
            case "require detected":
                if (SnapshotDetections == 0 || SnapshotVerifier.Rebuilt == 0 || SnapshotWalkReuses == 0 || !SnapshotWalksEnabled
                    || PatchSafety.IsFaulted(SnapshotWalksName) || SnapshotVerifier.Mismatches > 0)
                {
                    throw new InvalidOperationException(status);
                }
                return status;
            default:
                throw new ArgumentException("foreignpatch snapwalks status | foreignpatch require snapwalks on|detected");
        }
    }
#endif

    /// <summary>
    /// A checked reuse: this call's own output (the game's, other mods' postfixes included) for the caller, as far as it
    /// enumerates, each element compared with the kept output's pulled so far, and its end with the kept one's when known; a
    /// difference turns the part off for the session. The caller gets exactly the game's output (its exceptions included).
    /// </summary>
    private sealed class CheckedOutput : IEnumerable<AbstractModel>
    {
        private readonly IEnumerable<AbstractModel> _fresh;
        private readonly LazyOutput _kept;
        private readonly (object, object?) _key;
        private readonly int _scope, _version;
        private readonly KeptOutput? _entry;

        public CheckedOutput(IEnumerable<AbstractModel> fresh, LazyOutput kept, (object, object?) key, int scope, int version, KeptOutput? entry = null)
        {
            _fresh = fresh;
            _kept = kept;
            _key = key;
            _scope = scope;
            _version = version;
            _entry = entry;
        }

        /// <summary>Compared only in the scope and at the state the kept output is for (enumerated later, the state may differ).</summary>
        private bool Comparable => _scope == _scopeSerial && _version == ListenerCache._version && !_exactScope && _foreignDepth == 0;

        public IEnumerator<AbstractModel> GetEnumerator()
        {
            using IEnumerator<AbstractModel> fresh = _fresh.GetEnumerator();
            // D1 (re-audit of c2f613b): the check counts as done (the key's Checked) only if its comparison covered everything its
            // caller read: each element within what the kept output had pulled (or the kept one complete), and, at the fresh
            // output's end, the kept one's length known and compared. The kept output is never pulled further to compare.
            bool covered = true;
            try
            {
            for (int i = 0; ; i++)
            {
                bool has;
                bool wasMaterializing = _materializingAugmented;
                // A walk called by the output's own code gets its own output, as in the game.
                _materializingAugmented = true;
                try
                {
                    has = fresh.MoveNext();
                }
                finally
                {
                    _materializingAugmented = wasMaterializing;
                }
                if (!has)
                {
                    // Its end: the kept output has no more pulled, and ended there if its length is known.
                    if (Comparable && (_kept.Count > i || (_kept.Complete && _kept.Count != i)))
                    {
                        WalkMismatch(_key, $"{i} listeners, the kept one {(_kept.Complete ? "" : "at least ")}{_kept.Count}");
                    }
                    covered &= _kept.Complete;
                    yield break;
                }
                AbstractModel model = fresh.Current;
                if (Comparable && (i < _kept.Count ? !ReferenceEquals(_kept[i], model) : _kept.Complete))
                {
                    WalkMismatch(_key, i < _kept.Count ? $"listener {i} differs" : $"more than the kept one's {_kept.Count} listeners");
                }
                covered &= i < _kept.Count || _kept.Complete;
                yield return model;
            }
            }
            finally
            {
                if (_entry != null && covered && Comparable)
                {
                    _entry.Checked = true;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// From here to the end of the outermost scope, no augmented output is kept or reused (a snapshot, a game-state scope, a
    /// suspension). Outputs being enumerated go on (their callers' own, as in the game).
    /// </summary>
    private static void ExactFromHere()
    {
        _exactScope = true;
        ClearAugmented();
    }

    // ---- The answered card sort's walk reuse (owner-approved 2026-11-20 under Walks Tier 2's premise, DESIGN "Sort walk reuse") ----

    /// <summary>
    /// The listener walks inside one answered card sort (CardSortKeys: a <c>StableShuffle</c>'s <c>List.Sort</c> whose comparisons
    /// it answers). v0.113's comparison reads each card's resolved cost, whose hook walks every listener: one walk per card read.
    /// The sort is a scope of this class, a game-value one (<see cref="EnterSort"/>): the unpatched lists served by state version
    /// (exact, as every scope's), HookFilter's lists with them while no other mod patches the walks. With other mods' postfixes on a
    /// walk, its output after them is kept at the sort's first walk and reused for the sort's later walks (<see cref="Augmented"/>,
    /// KeptOnly's memo over it), on Walks Tier 2's premise, narrower scope: within one synchronous sort, at one state version, the
    /// walk's output after other mods' postfixes is the same at every call. The snapshot's schedule (owner's decision of 2026-11-20):
    /// each key's first reuse checked, then one fresh walk of each reused key at the sort's end (<see cref="EndSortWalks"/>). Its
    /// own part and switch: a difference found, or an error here, turns it off for the session (the display part is not touched).
    /// </summary>
    public const string SortWalksName = Name + ".SortWalks";

    public static bool SortWalksEnabled { get; set; } = true;

    /// <summary>The current outermost scope is an answered card sort (<see cref="EnterSort"/>): its part decides the augmented reuse.</summary>
    [ThreadStatic] private static bool _sortReuse;
    /// <summary>The state version the sort started at: no reuse at another one (a version change ends it for the rest of the sort).</summary>
    [ThreadStatic] private static int _sortVersion;
    /// <summary>CardSortKeys checks an answer against the game's comparison (<see cref="PauseSort"/>): its walks get their own outputs.</summary>
    [ThreadStatic] private static bool _sortPaused;
    /// <summary>The kept outputs the current sort handed to a walk after its first (unchecked): what its end compares (<see cref="EndSortWalks"/>).</summary>
    [ThreadStatic] private static List<KeptOutput>? _sortReused;

    /// <summary>Reuses in sorts that differed from a fresh walk, at a checked reuse or at the sort's end (each turned the part off).</summary>
    public static long SortWalkMismatches { get; private set; }
#if BENCH
    /// <summary>Sort scopes opened, walks answered from a kept output, outputs kept, reuses compared with a fresh walk (the effects check).</summary>
    public static long SortScopes { get; private set; }
    public static long SortWalkReuses { get; private set; }
    public static long SortWalksKept { get; private set; }
    public static long SortWalkChecks { get; private set; }
    /// <summary>Fresh walks compared at sorts' ends; reused outputs left out there (kept at a state version the sort no longer had).</summary>
    public static long SortEndWalks { get; private set; }
    public static long SortEndSkipped { get; private set; }
    /// <summary>Of <see cref="SortWalkMismatches"/>, those found by the fresh walks at a sort's end.</summary>
    public static long SortEndMismatches { get; private set; }

    /// <summary>
    /// The test postfixes' view (<c>walk-sortcheck-postfix</c>, <c>walk-sortend-postfix</c>): 1 inside an answered sort whose walks
    /// may reuse (not paused), 2 in the fresh walks of its end, else 0.
    /// </summary>
    internal static int SortPhaseForTest => _sortEnding ? 2 : _sortReuse && !_sortPaused && !_exactScope ? 1 : 0;

    /// <summary>The scope a sort phase belongs to (<see cref="SortScopes"/> at its start): the test postfixes count calls per sort.</summary>
    internal static long SortSerialForTest => SortScopes;

    [ThreadStatic] private static bool _sortEnding;
#endif

    /// <summary>
    /// CardSortKeys opened a sort it answers (<c>StableShuffleInScope</c>): its scope, when nothing else of this class is open on this
    /// thread (no display refresh, snapshot, suspension, vanilla reference or another mod's handler around it: else the sort runs as
    /// before, inside what is open). Returns the state <see cref="ExitSort"/> takes (0: nothing opened). Never throws.
    /// </summary>
    internal static int EnterSort()
    {
        try
        {
            if (!SortWalksEnabled || !_applied || _snapshotDepth + _refreshDepth != 0 || _vanillaRef != 0 || _suspended || _enclosing != null
                || _foreignDepth != 0 || _materializingAugmented || _materializingCombat || _materializingRun)
            {
                return 0;
            }
            EnterAny();
            // A game-value pass: no list kept from an earlier display scope is used in it (and none handed on from it).
            BlockReuse();
            _sortVersion = _version;
            _sortPaused = false;
            _sortReused?.Clear();
            // The augmented reuse only with the shape rules on (AugmentedReuse asks the rest per walk: other mods' postfixes on it,
            // this class's postfix last); without, no output is kept (exact mode, as a game-value refresh).
            if (PatchSafety.ShapeRules)
            {
                _sortReuse = true;
            }
            else
            {
                ExactFromHere();
            }
#if BENCH
            SortScopes++;
#endif
            _refreshDepth++;
            return 1;
        }
        catch (Exception e)
        {
            _sortReuse = false;
            SortWalksFailed("opening a sort's scope failed: " + e);
            return 0;
        }
    }

    /// <summary>
    /// The sort's end (its <c>finally</c>): every output it reused compared with a fresh walk (<see cref="EndSortWalks"/>), then the
    /// scope closed, every kept output with it. Never throws.
    /// </summary>
    internal static void ExitSort(int state)
    {
        if (state == 0)
        {
            return;
        }
        _sortReuse = false;
        _sortPaused = false;
        try
        {
            EndSortWalks();
        }
        finally
        {
            ExitRefresh(null, state);
        }
    }

    /// <summary>
    /// The net at an answered sort's end (the snapshot's schedule, owner's decision of 2026-11-20): for each kept output the sort
    /// handed to a walk after its first and unchecked, one fresh call of the game's walk (every other mod's postfix run as the game
    /// runs it; this mod's listener optimizations suspended), enumerated as far as the kept output was pulled and compared element
    /// by element, by reference (<see cref="CompareWithFresh"/>, as the snapshot's net). A difference, a reused output that threw,
    /// or the comparison failing (another mod's code throwing in the fresh walk, an error here): the part off for the session,
    /// logged (the sort is not redone: the residue of the premise). An output kept at a state version the sort no longer has (a
    /// change ended its reuse) is not compared: its walks read a state that is gone. Nothing is reused from here. Never throws.
    /// </summary>
    private static void EndSortWalks()
    {
        List<KeptOutput>? reused = _sortReused;
        if (reused == null || reused.Count == 0)
        {
            return;
        }
        (object, object?) key = default;
        try
        {
            _exactScope = true;
            ClearAugmented();
            if (!SortWalksEnabled)
            {
                return; // Turned off in this sort already (a checked reuse differed): logged there.
            }
#if BENCH
            _sortEnding = true;
#endif
            foreach (KeptOutput entry in reused)
            {
                if (entry.Version != _version)
                {
#if BENCH
                    SortEndSkipped++;
#endif
                    continue;
                }
#if BENCH
                SortEndWalks++;
#endif
                key = entry.Output.Key;
                if (CompareWithFresh(entry.Output) is { } why)
                {
                    SortEndFound($"a kept listener walk output differs from a fresh one at the sort's end ({KeyName(key)}: {why})", mismatch: true);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            SortEndFound($"the comparison with a fresh walk at the sort's end failed ({(key.Item1 == null ? "no walk" : KeyName(key))}): {e}", mismatch: false);
        }
        finally
        {
#if BENCH
            _sortEnding = false;
#endif
            reused.Clear();
        }
    }

    /// <summary>The sort's net found a difference or failed: the part off for the session, logged. Never throws.</summary>
    private static void SortEndFound(string what, bool mismatch)
    {
#if BENCH
        if (_selfTesting)
        {
            _selfTestMismatches++;
            _selfTestSortEnd = what;
            return;
        }
#endif
        if (mismatch)
        {
            SortWalkMismatches++;
#if BENCH
            SortEndMismatches++;
#endif
        }
        SortWalksFailed(what);
    }

    /// <summary>A walk key as the log lines name it.</summary>
    private static string KeyName((object, object?) key) =>
        $"{key.Item1?.GetType().Name ?? "none"}{(key.Item2 == null ? "" : " with a child combat")}";

    /// <summary>
    /// CardSortKeys' check of an answer against the game's comparison: while paused, the walks get their own outputs (other mods'
    /// postfixes run on the unpatched list as at every scope's exact tier), so the check stays independent of the reuse; a kept
    /// output is neither reused nor kept then. Returns the previous state, to restore.
    /// </summary>
    internal static bool PauseSort(bool pause)
    {
        bool was = _sortPaused;
        _sortPaused = pause;
        return was;
    }

    /// <summary>A checked reuse differed in a sort, or this class's error there: no more reuse in it, the part off for the session, logged. Never throws.</summary>
    private static void SortWalksFailed(string what)
    {
        try
        {
            ExactFromHere();
        }
        catch
        {
            _exactScope = true;
        }
        if (!SortWalksEnabled)
        {
            return;
        }
        SortWalksEnabled = false;
        PatchSafety.Fault(SortWalksName);
        Log.Error($"[{ModEntry.Id}] {Name}: in a card sort, {what}: the listener walks' reuse in sorts turned off for this session. Please report this log.");
    }

#if BENCH
    /// <summary>
    /// <c>foreignpatch sortwalks status</c>, <c>foreignpatch require sortwalks on|exact|off</c>: the sort's walk reuse's counters and
    /// checks. on: sort scopes opened, outputs kept and reused in them, reuses compared with a fresh walk, no difference, the part on.
    /// exact: sort scopes opened, nothing kept (no other mod's postfix on the walks: the lists served by version only), the part on.
    /// off (the control): a difference found, the part off and faulted.
    /// </summary>
    internal static string SortWalksControl(string what)
    {
        string status = $"FOREIGN sortwalks scopes {SortScopes}, kept {SortWalksKept}, reused {SortWalkReuses}, checked {SortWalkChecks}, "
            + $"end walks {SortEndWalks}, end skipped {SortEndSkipped}, mismatches {SortWalkMismatches} (at the end {SortEndMismatches}), on {SortWalksEnabled}, faulted {PatchSafety.IsFaulted(SortWalksName)}, served {Served}, "
            + $"memo hits {MemoHits}, live verdicts {FastListeners.MemoLiveJudged}, held verdicts {FastListeners.MultiHeldVerdicts}";
        bool fail = what switch
        {
            "status" => false,
            "require on" => SortScopes == 0 || SortWalksKept == 0 || SortWalkReuses == 0 || SortWalkChecks == 0 || SortEndWalks == 0
                || SortWalkMismatches > 0 || !SortWalksEnabled || PatchSafety.IsFaulted(SortWalksName),
            "require exact" => SortScopes == 0 || SortWalksKept > 0 || SortWalkMismatches > 0 || !SortWalksEnabled,
            "require off" => SortWalkMismatches == 0 || SortWalksEnabled || !PatchSafety.IsFaulted(SortWalksName),
            _ => throw new ArgumentException("foreignpatch sortwalks status | foreignpatch require sortwalks on|exact|off"),
        };
        if (fail)
        {
            throw new InvalidOperationException(status);
        }
        return status;
    }
#endif

    /// <summary>The kept note of the sort's walk reuse: which other mods' postfixes on the walks run once per answered sort instead of per card.</summary>
    private static void NoteSortWalks()
    {
        try
        {
            var walks = new List<MethodBase>();
            if (PatchSafety.ShapeRules && SortWalksEnabled && RefreshEnabled && _applied && CardSortKeys.Applied && CardSortKeys.Enabled
                && _combatWalkPostfix != null && _runWalkPostfix != null)
            {
                if (PatchSafety.HasOtherPatches(_combatIterate) && PostfixLast(_combatIterate, _combatWalkPostfix, ref _combatLast))
                {
                    walks.Add(_combatIterate!);
                }
                if (PatchSafety.HasOtherPatches(_runIterate) && PostfixLast(_runIterate, _runWalkPostfix, ref _runLast))
                {
                    walks.Add(_runIterate!);
                }
            }
            if (walks.Count == 0)
            {
                PatchSafety.ClearKept(SortWalksName, "sort_walks");
                return;
            }
            var mods = new List<string>(PatchSafety.OtherModsPatching(walks));
            foreach (MethodBase walk in walks)
            {
                mods.AddRange(HarmonyWatch.HiddenOwners(walk));
            }
            string names = string.Join(", ", walks.Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
            PatchSafety.Kept(SortWalksName, "sort_walks", $"other mods' code on the listener walks ({names}) runs once per card sort before "
                + "a shuffle instead of once per card's cost read: its output, enumerated only as far as the game goes, is reused for that "
                + "sort's walks (a stated premise: within one sort, at one state, it gives the same listeners; each sort's first reuse of a "
                + "walk is compared with a fresh call, and each walk it reused with one more fresh call at the sort's end, a difference "
                + "turns this off; the sort's own checks against the game's comparison take fresh walks)", mods.ToArray());
        }
        catch
        {
            // Display only.
        }
    }

    // ---- The checksum snapshot's walk reuse (owner-approved 2026-11-17, DESIGN "Snapshot walk reuse") ----

    /// <summary>
    /// The augmented walks' reuse inside the checksum's outermost snapshot (a part of the co-op state check). No session state
    /// of its own: a snapshot whose reuse its net finds wrong is built again the game's way, and the next one reuses again.
    /// Turned off only by its setting, or a fault of the net itself (a snapshot returned without its comparison).
    /// </summary>
    public const string SnapshotWalksName = Name + ".SnapshotWalks";

    public static bool SnapshotWalksEnabled { get; set; } = true;

    /// <summary>The current outermost scope is a checksum snapshot reusing walk outputs (<see cref="BeginSnapshotReuse"/>), until its net ran.</summary>
    [ThreadStatic] private static bool _snapshotReuse;
    /// <summary>The state version that snapshot started at: no reuse at another one, and a reuse before a change is a detection.</summary>
    [ThreadStatic] private static int _snapshotVersion;
    /// <summary>Something of the snapshot found its reuse wrong (a checked reuse, a memo check, this class's error): what, first.</summary>
    [ThreadStatic] private static string? _snapshotTaint;
    /// <summary>
    /// The state FromRun only reads, sealed at the snapshot's start (<see cref="SnapshotSeal"/>): a reused output enumerated fewer
    /// times than the game's walks ran less of other mods' enumeration code, whose side effects (an RNG drawn, a counter bumped)
    /// the net compares too. Null in the self-test (no game state).
    /// </summary>
    [ThreadStatic] private static SnapshotSeal? _snapshotSeal;
    [ThreadStatic] private static MegaCrit.Sts2.Core.Runs.IRunState? _snapshotRun;

    /// <summary>Snapshots whose seal changed (a detection each), and of those, RNGs drawn that couldn't be put back (v0.107.1, an error).</summary>
    public static long SnapshotSealChanges { get; private set; }
    public static long SnapshotSealUnrestored { get; private set; }

    /// <summary>The kept outputs that snapshot made (each one's walks after the first given it): what its net compares.</summary>
    [ThreadStatic] private static List<KeptOutput>? _snapshotReused;

    /// <summary>Snapshots whose walk reuse the net found wrong (each built again the game's way: SnapshotVerifier.Detections).</summary>
    public static long SnapshotDetections { get; private set; }
    /// <summary>Of those, the net's own comparison failing (another mod's code throwing in the fresh walk, an error here).</summary>
    public static long SnapshotNetErrors { get; private set; }

#if BENCH
    /// <summary>Snapshot walks answered from a kept output, outputs kept, reuses checked in the snapshot, fresh walks compared at its end.</summary>
    public static long SnapshotWalkReuses { get; private set; }
    public static long SnapshotWalksKept { get; private set; }
    public static long SnapshotWalkChecks { get; private set; }
    public static long SnapshotNetCompared { get; private set; }
    public static long SnapshotMemosCompared { get; private set; }

    /// <summary>The self-test's fresh walks (no game state): the net's source instead of the game's walk.</summary>
    [ThreadStatic] private static Func<(object, object?), IEnumerable<AbstractModel>>? _netSourceForTest;

    /// <summary><c>bdperf snapnet</c>: the next net that compared something reports a difference (the whole path then runs: rebuild).</summary>
    [ThreadStatic] private static bool _injectNetDifference;

    internal static void InjectNetDifferenceForTest() => _injectNetDifference = true;

    /// <summary>
    /// <c>bdperf snapnet nested</c>: <paramref name="run"/> inside a display refresh scope with game logic resuming inline
    /// (<see cref="SuspendPrefix"/>), as the checksum after an action runs on a peer whose hand selection ended inside the
    /// CombatStateChanged dispatch: a snapshot there must be decided as one with nothing around it.
    /// </summary>
    internal static T InsideSuspendedDispatchForTest<T>(Func<T> run)
    {
        EnterDisplayRefresh(out int scope);
        SuspendPrefix(out int suspension);
        try
        {
            return run();
        }
        finally
        {
            ResumeFinalizer(suspension);
            ExitRefresh(null, scope);
        }
    }
#endif

    /// <summary>
    /// At the outermost snapshot's start: whether it reuses walk outputs. Only where its net can run and nothing peer-dependent
    /// can see a reused snapshot: the part on, the state check on and serving, other mods' postfixes on a walk, and
    /// <see cref="SnapshotVerifier.WalkNetFor"/> (the net's postfix in place, and no sampled live re-check possible for this
    /// snapshot: its schedule depends on timing, so a peer's re-check finding a difference the net missed would make only that
    /// peer use another snapshot). Every input is the same on every peer for the same state and the same mods and settings
    /// (CoopModeTag tags a player whose snapshots can't reuse). Never throws.
    /// </summary>
    private static bool BeginSnapshotReuse(MegaCrit.Sts2.Core.Runs.IRunState? runState)
    {
        _snapshotReuse = false;
        _snapshotTaint = null;
        _snapshotReused?.Clear();
        _snapshotSeal = null;
        _snapshotRun = null;
        try
        {
            _threwAfterReuse = false;
            if (_noSnapshotReuse > 0 || !SnapshotWalksEnabled || !SnapshotEnabled || !_safe || _suspended || !PatchSafety.ShapeRules
                || !(_combatAugmented || _runAugmented) || _combatWalkPostfix == null || _runWalkPostfix == null
                || !NetFor(runState))
            {
                return false;
            }
            if (runState != null)
            {
                // No seal, no reuse: the net couldn't see what other mods' enumeration code did to the state.
                _snapshotSeal = SnapshotSeal.Take(runState);
                if (_snapshotSeal == null)
                {
                    return false;
                }
                _snapshotRun = runState;
            }
            _snapshotVersion = _version;
            _snapshotReuse = true;
            return true;
        }
        catch
        {
            _snapshotReuse = false;
            return false;
        }
    }

    /// <summary>The last outermost snapshot threw before its net ran, with reused outputs (<see cref="TakeThrewAfterReuse"/>).</summary>
    [ThreadStatic] private static bool _threwAfterReuse;

    /// <summary>
    /// For SnapshotVerifier's FromRun finalizer (after this class's): whether the snapshot ending now threw with reused outputs
    /// before its net ran (it must then be built again exactly). Cleared.
    /// </summary>
    internal static bool TakeThrewAfterReuse()
    {
        bool threw = _threwAfterReuse;
        _threwAfterReuse = false;
        return threw;
    }

    /// <summary>Snapshots built inside <see cref="WithoutSnapshotReuse"/> never reuse walk outputs (an exact rebuild after the scope closed).</summary>
    [ThreadStatic] private static int _noSnapshotReuse;

    internal static T WithoutSnapshotReuse<T>(Func<T> build)
    {
        int was = _noSnapshotReuse;
        _noSnapshotReuse = was + 1;
        try
        {
            return build();
        }
        finally
        {
            _noSnapshotReuse = was;
        }
    }

    private static bool NetFor(MegaCrit.Sts2.Core.Runs.IRunState? runState)
    {
#if BENCH
        if (_snapshotNetForTest)
        {
            return true;
        }
#endif
        return runState != null && SnapshotVerifier.WalkNetFor(runState);
    }

#if BENCH
    /// <summary>The self-test's snapshots: no game state, the net run by the test itself.</summary>
    [ThreadStatic] private static bool _snapshotNetForTest;
#endif

    /// <summary>
    /// Something in the snapshot found its reuse wrong: no more reuse in it (outputs dropped), and its net reports a detection
    /// (the snapshot built again the game's way). Never throws.
    /// </summary>
    private static void SnapshotTaint(string what)
    {
        _snapshotTaint ??= what;
        try
        {
            _exactScope = true;
            ClearAugmented();
        }
        catch
        {
            // The net reports the taint whatever was left: the snapshot is built again.
        }
    }

    /// <summary>
    /// The net, at the end of the checksum's outermost snapshot (SnapshotVerifier's FromRun postfix, before the snapshot is
    /// returned to the game's checksum): true when the snapshot must be built again the game's way. For each kept output the
    /// snapshot reused, one fresh call of the game's walk (its postfixes, every other mod's, run as the game runs them; this
    /// mod's listener optimizations suspended) is enumerated as far as the kept output was pulled, and compared element by
    /// element, by reference, with it: every element any caller of the snapshot read is one of those (a reuse replays the kept
    /// elements, a memo judges them). Where the kept output reached its end, the fresh one must end there too; where it didn't
    /// (every caller stopped early), the fresh one must have at least as many. Also a detection: a kept output that threw, a
    /// state version change during the snapshot, anything the snapshot's own checks found (a checked reuse, a memo check) or an
    /// error of this code or in the fresh walk. A detection is counted (<see cref="SnapshotDetections"/>, part of
    /// SnapshotVerifier.Detections); nothing is turned off. From here to the scope's end nothing is reused. Never throws.
    /// </summary>
    internal static bool EndSnapshotWalks()
    {
        if (!_snapshotReuse || _snapshotDepth != 1)
        {
            return false;
        }
        List<KeptOutput>? reused = _snapshotReused;
        // The walks this snapshot kept outputs of (combat, run): whose code the seal names if the state changed.
        bool combatWalked = false, runWalked = false;
        if (reused != null)
        {
            foreach (KeptOutput entry in reused)
            {
                combatWalked |= entry.Output.Key.Item1 is CombatState;
                runWalked |= entry.Output.Key.Item1 is RunState;
            }
        }
        string? why = _snapshotTaint;
        bool versionMoved = _version != _snapshotVersion;
        SnapshotSeal? seal = _snapshotSeal;
        MegaCrit.Sts2.Core.Runs.IRunState? run = _snapshotRun;
        _snapshotReuse = false;
        _snapshotTaint = null;
        _snapshotSeal = null;
        _snapshotRun = null;
        // The memos used in the snapshot, taken before the outputs close (their close drops them).
        var memos = new List<(LazyOutput Output, object Filter, FilteredMemo Memo)>();
        try
        {
            if (reused != null)
            {
                foreach (KeptOutput entry in reused)
                {
                    foreach (KeyValuePair<object, FilteredMemo> pair in entry.Output.MemoList())
                    {
                        memos.Add((entry.Output, pair.Key, pair.Value));
                    }
                }
            }
        }
        catch (Exception e)
        {
            why ??= "the snapshot's memos couldn't be listed: " + e.Message;
        }
        try
        {
            _exactScope = true;
            ClearAugmented();
        }
        catch
        {
            // Nothing reused from here: _exactScope is set first.
        }
        if (why == null && (reused == null || reused.Count == 0))
        {
            return false;
        }
        try
        {
            if (why == null && versionMoved)
            {
                why = "the state changed during the check";
            }
            for (int i = 0; why == null && i < reused!.Count; i++)
            {
#if BENCH
                SnapshotNetCompared++;
#endif
                why = CompareWithFresh(reused[i].Output);
            }
            // Every memo used in the snapshot against KeptOnly's own loop over the fresh walk (equal to its kept output, element by
            // element, once the comparisons above found nothing).
            for (int i = 0; why == null && i < memos.Count; i++)
            {
#if BENCH
                SnapshotMemosCompared++;
#endif
                why = FastListeners.MemoAgainstLoop(memos[i].Memo, memos[i].Filter, memos[i].Output);
            }
        }
        catch (Exception e)
        {
            SnapshotNetErrors++;
            why = "the comparison with a fresh walk failed: " + e.Message;
        }
        finally
        {
            reused?.Clear();
        }
#if BENCH
        if (_injectNetDifference && why == null)
        {
            _injectNetDifference = false;
            why = "injected (bdperf snapnet)";
        }
#endif
        // What other mods' enumeration code did to the state FromRun only reads (after the fresh walks: they ran it too, as the
        // game's walks would have): a change is a detection, and the RNGs drawn are put back as they were before the snapshot
        // (v0.111+), so the snapshot built again draws them as the game's own FromRun does. Nothing is turned off: the next
        // snapshot is decided the same way on every peer (a switch-off kept per run or session would differ on a peer that
        // rejoined or restarted).
        if (seal != null && run != null && seal.Changed(run) is { } changed)
        {
            SnapshotSealChanges++;
            bool restored = seal.RestoreRngs();
            if (!restored)
            {
                SnapshotSealUnrestored++;
            }
            string walks = WalkOwners(combatWalked, runWalked);
            string sealedLine = $"other mods' code on {walks} changed what a co-op state check reads while it was enumerated ({changed}): "
                + (restored ? "the RNGs drawn were put back and the check is built again the game's way"
                    : "it can't be put back on this game version; the check is built again the game's way");
            why ??= sealedLine;
            if (_sealedWalks == null || !_sealedWalks.Contains(walks))
            {
                // A line per walk set and session (the counter, ListenerCache.SnapshotWalks.Sealed, counts every one), the mods named
                // in bdopt why too (the kept note).
                (_sealedWalks ??= new HashSet<string>(StringComparer.Ordinal)).Add(walks);
                Log.Info($"[{ModEntry.Id}] {Name}: {sealedLine}");
                NoteSnapshotWalks();
            }
        }
        if (why == null)
        {
            return false;
        }
        SnapshotDetected(why);
        return true;
    }

    /// <summary>
    /// One fresh call of the game's walk for <paramref name="kept"/>'s key compared with it (see <see cref="EndSnapshotWalks"/>):
    /// null when the same, else what differs. Its exceptions (another mod's code in the fresh walk) reach the caller.
    /// </summary>
    private static string? CompareWithFresh(LazyOutput kept)
    {
        if (kept.Failed)
        {
            return "a reused output threw";
        }
        (object state, object? child) = kept.Key;
        return RunVanilla<string?>(() =>
        {
            IEnumerable<AbstractModel> fresh = FreshWalk(state, child);
            using IEnumerator<AbstractModel> enumerator = fresh.GetEnumerator();
            for (int i = 0; i < kept.Count; i++)
            {
                if (!enumerator.MoveNext())
                {
                    return $"{i} listeners, the reused output {kept.Count}";
                }
                if (!ReferenceEquals(enumerator.Current, kept[i]))
                {
                    return $"listener {i} differs";
                }
            }
            return kept.Complete && enumerator.MoveNext() ? $"more than the reused output's {kept.Count} listeners" : null;
        });
    }

    /// <summary>The game's walk of a key: (combat state, null) or (run state, child combat or null), called as the game calls it.</summary>
    private static IEnumerable<AbstractModel> FreshWalk(object state, object? child)
    {
#if BENCH
        if (_netSourceForTest != null)
        {
            return _netSourceForTest((state, child));
        }
#endif
        return state switch
        {
            CombatState combat when child == null => combat.IterateHookListeners(),
            RunState run => run.IterateHookListeners(child as ICombatState),
            _ => throw new InvalidOperationException("unknown walk key " + state.GetType().Name),
        };
    }

    private static bool _detectionLogged;

    /// <summary>The walks (with their other mods' patches) whose code changed what a state check reads, this session (the seal).</summary>
    private static HashSet<string>? _sealedWalks;

    /// <summary>
    /// The walks a snapshot kept outputs of, each with the Harmony ids of the other mods' patches on it and their mod names (as
    /// back-off lines give them): what the seal's log line and bdopt why name. Never throws.
    /// </summary>
    private static string WalkOwners(bool combat, bool run)
    {
        var parts = new List<string>();
        try
        {
            foreach ((bool walked, MethodBase? walk) in new[] { (combat, (MethodBase?)_combatIterate), (run, (MethodBase?)_runIterate) })
            {
                if (!walked || walk == null)
                {
                    continue;
                }
                string[] ids = Harmony.GetPatchInfo(walk) is { } info
                    ? info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                        .Select(p => p.owner).Where(owner => !PatchGuard.IsOurs(owner)).Distinct().OrderBy(o => o, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
                string[] mods = PatchSafety.OtherModsPatching(new[] { walk }).Concat(HarmonyWatch.HiddenOwners(walk)).Distinct().ToArray();
                parts.Add($"{walk.DeclaringType?.Name}.{walk.Name} (Harmony ids: {(ids.Length > 0 ? string.Join(", ", ids) : "none")}; "
                    + $"mods: {(mods.Length > 0 ? string.Join(", ", mods) : "unknown")})");
            }
        }
        catch
        {
            // The line still says what changed.
        }
        return parts.Count > 0 ? string.Join(" and ", parts) : "the listener walks";
    }

    private static void SnapshotDetected(string why)
    {
        SnapshotDetections++;
#if BENCH
        if (_selfTesting)
        {
            return;
        }
#endif
#if BENCH
        if (why.StartsWith("injected", StringComparison.Ordinal))
        {
            Log.Info($"[{ModEntry.Id}] {Name}: a state check's walk reuse reported different by the test tooling: built again the game's way");
            return;
        }
#endif
        if (!_detectionLogged)
        {
            _detectionLogged = true;
            Log.Error($"[{ModEntry.Id}] {Name}: a co-op state check that reused listener walk outputs (other mods' postfixes included) "
                + $"differs from the game's walks ({why}): that check is built again the game's way before it is used (each time this happens). "
                + "Please report this log.");
            NoteSnapshotWalks();
        }
        else
        {
            Log.Info($"[{ModEntry.Id}] {Name}: a state check's walk reuse differs again ({why}): built again the game's way ({SnapshotDetections} so far).");
        }
    }

    /// <summary>
    /// At the outermost snapshot scope's end (its finalizer): a snapshot that reused walk outputs and returned without its net
    /// having run (SnapshotVerifier's postfix didn't run: never seen) was used unchecked. The part is turned off for the session
    /// (a fault of the net itself: from then on snapshots run exact, as before this part; CoopModeTag retags). Never throws.
    /// </summary>
    private static void EndSnapshotScope(Exception? exception)
    {
        if (!_snapshotReuse)
        {
            return;
        }
        bool reused = _snapshotTaint != null || _snapshotReused is { Count: > 0 };
        SnapshotSeal? seal = _snapshotSeal;
        _snapshotReuse = false;
        _snapshotTaint = null;
        _snapshotReused?.Clear();
        _snapshotSeal = null;
        _snapshotRun = null;
        if (!reused)
        {
            return;
        }
        if (exception != null)
        {
            // FromRun threw before its net ran (the game's body, a prefix or a postfix before SnapshotVerifier's), with reused
            // outputs: the game's own FromRun might not have. SnapshotVerifier's finalizer builds it again exactly and returns
            // that (or its exception); the RNGs drawn meanwhile are put back first.
            seal?.RestoreRngs();
            _threwAfterReuse = true;
            return;
        }
        try
        {
            SnapshotWalksEnabled = false;
            PatchSafety.Fault(SnapshotWalksName);
            Log.Error($"[{ModEntry.Id}] {Name}: a co-op state check reused listener walk outputs but its comparison didn't run: "
                + "this reuse is off for this session (state checks run as before). Please report this log.");
            Config.Settings.ApplyAll();
        }
        catch
        {
            SnapshotWalksEnabled = false;
        }
    }

    /// <summary>
    /// This player's co-op tag for the snapshot walk reuse (CoopModeTag), from the walks' per-walk decisions: "" when every walk
    /// carrying other mods' postfixes is reused in snapshots (or none carries any: nothing to reuse anywhere); <c>+exactsnap</c>
    /// when none of them is; <c>+exactsnap-c</c> / <c>+exactsnap-r</c> when the combat / run walk carries them but isn't reused
    /// while the other is. Two players whose snapshots would reuse different walks carry different tags (a premise violation the
    /// net misses would make their checksums differ at the same point every time): the game's join check keeps them apart.
    /// Inputs: what <see cref="BeginSnapshotReuse"/> and <see cref="AugmentedReuse"/> read that doesn't depend on the snapshot
    /// or the moment: the switches and faults, the net in place, the walks' patches (setup), the scope check without the
    /// thread's suspension (<see cref="ScopesSafe"/> with <c>atLoad</c>), and this mod's walk postfix sorting last after another
    /// mod's on each walk (Harmony's sort, HarmonyWatch's cached verdict). Left out: the state (the Sample mode's 1,000-card gate
    /// is the same on every peer; Mode itself is test tooling), a mid-scope declined mirror, RunVanilla. Unsure: <c>+exactsnap</c>.
    /// Never throws.
    /// </summary>
    public static string SnapshotWalksTag
    {
        get
        {
            try
            {
                MethodBase? combatWalk = _combatIterate ?? Reflect.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners));
                MethodBase? runWalk = _runIterate ?? Reflect.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) });
                bool combatOthers = PatchSafety.HasOtherPatches(combatWalk), runOthers = PatchSafety.HasOtherPatches(runWalk);
                if (!combatOthers && !runOthers)
                {
                    return "";
                }
                bool on = _applied && SnapshotWalksEnabled && SnapshotEnabled && PatchSafety.ShapeRules && _combatWalkPostfix != null && _runWalkPostfix != null
                    && SnapshotVerifier.NetApplied && !PatchSafety.IsFaulted(Name) && !PatchSafety.IsFaulted(SnapshotWalksName)
                    && ScopesSafe(out _, out _, atLoad: true);
                bool combatReused = on && combatOthers && PostfixLast(_combatIterate, _combatWalkPostfix!, ref _combatLast);
                bool runReused = on && runOthers && PostfixLast(_runIterate, _runWalkPostfix!, ref _runLast);
                bool combatExact = combatOthers && !combatReused, runExact = runOthers && !runReused;
                return !combatExact && !runExact ? ""
                    : !combatReused && !runReused ? CoopModeTag.ExactSnapSuffix
                    : combatExact ? CoopModeTag.ExactSnapSuffix + "-c"
                    : CoopModeTag.ExactSnapSuffix + "-r";
            }
            catch
            {
                return CoopModeTag.ExactSnapSuffix;
            }
        }
    }

    /// <summary>The kept note of this part (preflight): which walks a state check may reuse, with the mods patching them.</summary>
    private static void NoteSnapshotWalks()
    {
        try
        {
            var walks = new List<MethodBase>();
            if (PatchSafety.ShapeRules && SnapshotWalksEnabled && SnapshotEnabled && _applied && SnapshotVerifier.NetApplied
                && _combatWalkPostfix != null && _runWalkPostfix != null)
            {
                if (PatchSafety.HasOtherPatches(_combatIterate) && PostfixLast(_combatIterate, _combatWalkPostfix, ref _combatLast))
                {
                    walks.Add(_combatIterate!);
                }
                if (PatchSafety.HasOtherPatches(_runIterate) && PostfixLast(_runIterate, _runWalkPostfix, ref _runLast))
                {
                    walks.Add(_runIterate!);
                }
            }
            if (walks.Count == 0)
            {
                PatchSafety.ClearKept(SnapshotWalksName, "snapshot_walks");
                return;
            }
            var mods = new List<string>(PatchSafety.OtherModsPatching(walks));
            foreach (MethodBase walk in walks)
            {
                mods.AddRange(HarmonyWatch.HiddenOwners(walk));
            }
            string names = string.Join(", ", walks.Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
            PatchSafety.Kept(SnapshotWalksName, "snapshot_walks", $"other mods' code on the listener walks ({names}) runs once per co-op state "
                + $"check instead of once per walk, above {SnapshotVerifier.MaxCombatCards} combat cards: its output is reused for that check's walks "
                + "(a stated premise: within one state check, at one state, it gives the same listeners); at the check's end, before the "
                + "state is used, every reused output is compared with a fresh call of the game's walk, and a difference builds that check "
                + "again the game's way (the checksum sent is then the game's)"
                + (SnapshotDetections > 0 ? $"; differences found this session: {SnapshotDetections}" : "")
                + (_sealedWalks is { Count: > 0 } ? "; this session, other mods' code on " + string.Join("; ", _sealedWalks)
                    + " changed what a state check reads while it was enumerated (each time: the RNGs drawn put back, the check built again)" : ""),
                mods.ToArray());
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>
    /// Which of the CombatStateChanged dispatch's handlers are not the game's (re-audit of the premises, W-1a; the rule of
    /// 2026-11-17): another mod's handler may walk the real state (RandomForeseer's prediction refresh) or do more than display,
    /// so it runs with the reuse suspended (<see cref="InvokeHandlers"/>); the game's own keep it. Null: all the game's. Cached
    /// per delegate (a subscription makes a new one; its invocation list is fixed). The flags follow
    /// <paramref name="handlers"/>' invocation list (<paramref name="list"/>, given when some aren't the game's), index for index.
    /// A handler it can't tell is not the game's.
    /// </summary>
    private static bool[]? ForeignHandlers(Action<CombatState> handlers, out Delegate[]? list)
    {
        list = null;
        HandlersVerdict? last = _lastHandlers;
        if (last != null && last.Handlers.TryGetTarget(out Action<CombatState>? known) && ReferenceEquals(handlers, known))
        {
            if (last.Foreign != null)
            {
                list = handlers.GetInvocationList();
            }
            return last.Foreign;
        }
        list = handlers.GetInvocationList();
        var flags = new bool[list.Length];
        var names = new List<string>();
        var assemblies = new List<Assembly?>();
        for (int i = 0; i < list.Length; i++)
        {
            if (IsGameHandler(list[i]))
            {
                continue;
            }
            flags[i] = true;
            try
            {
                MethodInfo method = list[i].Method;
                string name = $"{method.DeclaringType?.FullName}.{method.Name}";
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
                // A game method on another mod's object (its subclass of a game node): that mod's.
                Assembly? assembly = method.DeclaringType?.Assembly;
                assemblies.Add(assembly == Reflect.Game && list[i].Target is { } target ? target.GetType().Assembly : assembly);
            }
            catch
            {
                // Named or not, it runs with the reuse suspended.
                if (!names.Contains("(unnamed)"))
                {
                    names.Add("(unnamed)");
                }
            }
        }
        bool[]? foreign = Array.IndexOf(flags, true) >= 0 ? flags : null;
        _lastHandlers = new HandlersVerdict(new WeakReference<Action<CombatState>>(handlers), foreign);
        if (!SelfTesting)
        {
            NoteForeignHandlers(names, assemblies);
        }
        return foreign;
    }

    /// <summary>
    /// A handler of the game's own: its method declared in the game's assembly (closures and lambdas the game compiles
    /// included), on no target or a target of a game type (not another mod's subclass of a game node), no type argument from
    /// outside the game or the runtime. Other mods' Harmony patches on a game handler don't change this (D4, owner decision:
    /// taken as display under the walk premise). Never throws: can't tell, not the game's.
    /// </summary>
    private static bool IsGameHandler(Delegate handler)
    {
        try
        {
            MethodInfo method = handler.Method;
            Type? declaring = method.DeclaringType;
            if (declaring == null || method.Module.Assembly != Reflect.Game || !GameType(declaring)
                || (method.IsGenericMethod && !method.GetGenericArguments().All(GameOrRuntimeType)))
            {
                return false;
            }
            object? target = handler.Target;
            return target == null || GameType(target.GetType());
        }
        catch
        {
            return false;
        }
    }

    private static bool GameType(Type type) =>
        type.Assembly == Reflect.Game && (!type.IsGenericType || type.GetGenericArguments().All(GameOrRuntimeType));

    private static bool GameOrRuntimeType(Type type) =>
        !type.IsGenericParameter && (type.Assembly == Reflect.Game || type.Assembly == typeof(object).Assembly)
        && (!type.IsGenericType || type.GetGenericArguments().All(GameOrRuntimeType))
        && (!type.HasElementType || GameOrRuntimeType(type.GetElementType()!));

    /// <summary>
    /// bdopt why's note on other mods' CombatStateChanged handlers (no walk premise for them: they get the game's walks at the
    /// scope's exact tier), recorded or cleared per change of the handlers' delegate. Display only, never throws.
    /// </summary>
    private static void NoteForeignHandlers(List<string> names, List<Assembly?> assemblies)
    {
        try
        {
            if (names.Count == 0)
            {
                PatchSafety.ClearExact(WalkReuseName, "foreign_handlers");
                return;
            }
            // Sorted: the four named don't change with the handlers' order (a node subscribing again), so the note logs once.
            names.Sort(StringComparer.Ordinal);
            PatchSafety.Exact(WalkReuseName, "foreign_handlers", $"other mods handle CombatStateChanged ({string.Join(", ", names.Take(4))}"
                + (names.Count > 4 ? ", ..." : "") + "): their handlers run with the game's listener walks at the scope's exact tier (the "
                + "unpatched lists by state version, every other mod's postfix run at each call: no walk output kept for the game's handlers "
                + "reaches them, and none kept before them is reused after them)", PatchSafety.ModNamesOf(assemblies));
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>The last dispatch's verdict, its delegate held weakly (D5, re-audit of c2f613b: it holds the handlers' nodes).</summary>
    private sealed record HandlersVerdict(WeakReference<Action<CombatState>> Handlers, bool[]? Foreign);

#if BENCH
    private static bool SelfTesting => _selfTesting;
#else
    private const bool SelfTesting = false;
#endif

    private static HandlersVerdict? _lastHandlers;

#if BENCH
    // ---- Self-test of the kept walk outputs (foreignpatch walkreuse selftest; offline harness too: no game state used) ----

    /// <summary>Set while the self-test runs: a difference found is counted here instead of turning the part off.</summary>
    [ThreadStatic] private static int _selfTestMismatches;
    /// <summary>The self-test's last difference found by a sort's end (<see cref="SortEndFound"/>).</summary>
    [ThreadStatic] private static string? _selfTestSortEnd;
    /// <summary>The self-test's errors of the dispatch's own code (<see cref="DispatchFailed"/>), and its injected one.</summary>
    [ThreadStatic] private static int _selfTestDispatchFaults;
    [ThreadStatic] private static bool _selfTestFailPlan;
    [ThreadStatic] private static bool _selfTesting;

    /// <summary>A source counting what the walk's caller makes it do: elements produced, disposals, and the flag seen while producing.</summary>
    private sealed class CountingSource : IEnumerable<AbstractModel>
    {
        private readonly AbstractModel[] _items;
        private readonly int _throwAt;
        public int Produced, Disposed, Enumerations, SeenUnmaterializing;

        public CountingSource(AbstractModel[] items, int throwAt = -1)
        {
            _items = items;
            _throwAt = throwAt;
        }

        public IEnumerator<AbstractModel> GetEnumerator()
        {
            Enumerations++;
            try
            {
                for (int i = 0; i < _items.Length; i++)
                {
                    if (i == _throwAt)
                    {
                        throw new SelfTestException();
                    }
                    Produced++;
                    if (!_materializingAugmented)
                    {
                        SeenUnmaterializing++;
                    }
                    yield return _items[i];
                }
            }
            finally
            {
                Disposed++;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SelfTestException : Exception
    {
        public SelfTestException() : base("walk output self-test throw")
        {
        }
    }

    /// <summary>
    /// The kept walk outputs' contract (re-audit of the premises, W-2, W-1b, W-4): pulled only as far as a caller goes and
    /// interleaved with its loop, replayed and pulled further by later callers, the source disposed at its end or throw only,
    /// a throw met at the same element by every caller and the key forgotten, a checked reuse finding every difference (and
    /// none where it can't know), a reuse outside its scope given the call's own output.
    /// </summary>
    internal static IReadOnlyList<string> WalkOutputSelfTest()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        void Expect(bool ok, string what)
        {
            lines.Add($"FOREIGN walkreuse selftest {what}: {(ok ? "ok" : "FAILED")}");
            if (!ok)
            {
                failures.Add(what);
            }
        }
        Type concrete = typeof(AbstractModel).Assembly.GetTypes().First(t => !t.IsAbstract && !t.ContainsGenericParameters && typeof(AbstractModel).IsAssignableFrom(t));
        AbstractModel[] models = Enumerable.Range(0, 6).Select(_ => (AbstractModel)RuntimeHelpers.GetUninitializedObject(concrete)).ToArray();
        bool wasEnabled = WalkReuseEnabled, wasSafe = _safe;
        int wasDepth = _refreshDepth;
        // The dispatch counters are the effects check's: what the cases below count is put back.
        long wasDispatches = ForeignDispatches, wasGameReuses = GameHandlerReuses, wasForeignCalls = ForeignHandlerCalls, wasDropped = KeptDroppedAfterForeign;
        long wasKeptAfter = KeptAfterForeign, wasReusedAfter = ReusedAfterForeign;
        long wasMemoBuilt = MemoBuilt, wasMemoHits = MemoHits, wasMemoVerified = MemoVerified;
        bool wasMemoEnabled = WalkMemoEnabled;
        var wasAugmented = _augmented;
        _selfTesting = true;
        try
        {
            // 1. Lazy: a caller stopping at the second element pulls two; a later caller replays them and pulls the rest.
            var source = new CountingSource(models);
            var lazy = new LazyOutput(source, (source, null));
            bool any = lazy.Take(2).Count() == 2;
            Expect(any && source.Produced == 2 && source.Disposed == 0, "early stop pulls two, source not disposed");
            var all = lazy.ToList();
            Expect(all.SequenceEqual(models) && source.Produced == 6 && source.Enumerations == 1 && source.Disposed == 1, "later caller replays then pulls on, one enumeration, disposed at its end");
            Expect(lazy.ToList().SequenceEqual(models) && source.Produced == 6, "replay pulls nothing more");
            Expect(source.SeenUnmaterializing == 0, "the source runs flagged (a walk from inside it gets its own output)");

            // 2. Interleaved: element i is produced when the caller asks for it, not before.
            var source2 = new CountingSource(models);
            var lazy2 = new LazyOutput(source2, (source2, null));
            bool interleaved = true;
            int index = 0;
            foreach (AbstractModel model in lazy2)
            {
                interleaved &= source2.Produced == index + 1 && ReferenceEquals(model, models[index]);
                index++;
            }
            Expect(interleaved && index == 6, "pulled one element per step of the caller's loop");

            // 3. A throw at element 3: every caller meets it after the same three elements; the key forgotten, the source disposed.
            var source3 = new CountingSource(models, throwAt: 3);
            var key3 = ((object)source3, (object?)null);
            var lazy3 = new LazyOutput(source3, key3);
            _augmented = new Dictionary<(object, object?), KeptOutput>(new RefComparer()) { [key3] = new KeptOutput(_version, lazy3) };
            (int seen, Exception? error) Run(IEnumerable<AbstractModel> output)
            {
                int n = 0;
                try
                {
                    foreach (AbstractModel _ in output)
                    {
                        n++;
                    }
                }
                catch (Exception e)
                {
                    return (n, e);
                }
                return (n, null);
            }
            bool earlyNoThrow;
            try
            {
                earlyNoThrow = lazy3.Take(2).Count() == 2 && source3.Produced == 2;
            }
            catch
            {
                earlyNoThrow = false;
            }
            Expect(earlyNoThrow, "a caller stopping before the throwing element meets no throw (as the game's lazy walk)");
            var (first, firstError) = Run(lazy3);
            var (second, secondError) = Run(lazy3);
            Expect(first == 3 && firstError is SelfTestException && second == 3 && secondError is SelfTestException, "a throw met after the same elements by every caller");
            Expect(!_augmented.ContainsKey(key3) && source3.Disposed == 1 && source3.Enumerations == 1, "a throw forgets the key and disposes the source once");

            // 4. Checked reuses: every difference found, none where the kept length isn't known.
            int Check(IEnumerable<AbstractModel> fresh, LazyOutput kept)
            {
                int before = _selfTestMismatches;
                var output = new CheckedOutput(fresh, kept, (kept, null), _scopeSerial, _version);
                List<AbstractModel> got = output.ToList();
                if (!got.SequenceEqual(fresh))
                {
                    failures.Add("a checked reuse changed the call's own output");
                }
                return _selfTestMismatches - before;
            }
            LazyOutput Kept(int pulled)
            {
                var k = new LazyOutput(models.Take(3).ToArray(), ((object)models, null));
                if (pulled == 3)
                {
                    k.ToList();
                }
                else
                {
                    k.Take(pulled).Count();
                }
                return k;
            }
            Expect(Check(models.Take(3).ToArray(), Kept(3)) == 0, "same output: no difference");
            Expect(Check(models.Take(2).ToArray(), Kept(3)) >= 1, "shorter: found");
            Expect(Check(models.Take(4).ToArray(), Kept(3)) >= 1, "longer: found");
            Expect(Check(new[] { models[0], models[4], models[2] }, Kept(3)) >= 1, "another listener: found");
            Expect(Check(new[] { models[1], models[0], models[2] }, Kept(3)) >= 1, "another order: found");
            Expect(Check(models.Take(3).ToArray(), Kept(2)) == 0, "longer than a partly pulled kept one: not a difference");
            Expect(Check(models.Take(1).ToArray(), Kept(2)) >= 1, "shorter than a partly pulled kept one: found");
            int stale = _selfTestMismatches;
            new CheckedOutput(models.Take(2).ToArray(), Kept(3), (models, null), _scopeSerial - 1, _version).ToList();
            new CheckedOutput(models.Take(2).ToArray(), Kept(3), (models, null), _scopeSerial, _version - 1).ToList();
            Expect(_selfTestMismatches == stale, "enumerated after its scope or state: not compared");

            // 5. A reuse: the kept output inside its scope, the call's own outside it (or after the scope's reuse ended).
            var own = new CountingSource(models.Skip(3).ToArray());
            LazyOutput keptAll = Kept(3);
            _safe = true;
            _refreshDepth = Math.Max(1, _refreshDepth);
            bool exact = _exactScope, declined = _mirrorDeclined;
            _exactScope = false;
            _mirrorDeclined = false;
            WalkReuseEnabled = true;
            List<AbstractModel> inside = new ReusedOutput(keptAll, own, _scopeSerial, _version).ToList();
            List<AbstractModel> outside = new ReusedOutput(keptAll, own, _scopeSerial - 1, _version).ToList();
            List<AbstractModel> otherVersion = new ReusedOutput(keptAll, own, _scopeSerial, _version - 1).ToList();
            // Round 4: a kept output pulled to its second element then closed (a bump, then another walk of its key): a reuse held
            // from before gives the call's own output, never the two elements pulled.
            LazyOutput keptClosed = Kept(2);
            keptClosed.Close();
            List<AbstractModel> afterClose = new ReusedOutput(keptClosed, own, _scopeSerial, _version).ToList();
            _exactScope = true;
            List<AbstractModel> afterExact = new ReusedOutput(keptAll, own, _scopeSerial, _version).ToList();
            _exactScope = exact;
            _mirrorDeclined = declined;
            Expect(inside.SequenceEqual(models.Take(3)) && outside.SequenceEqual(models.Skip(3)) && afterExact.SequenceEqual(models.Skip(3)),
                "a reuse is the kept output in its scope only");
            Expect(otherVersion.SequenceEqual(models.Skip(3)) && afterClose.SequenceEqual(models.Skip(3)),
                "a reuse held across a state version or its kept output's close is the call's own output");

            // 6. W-7: in an outermost display scope, a nested game-value refresh and a suspension (game logic resuming inline) each
            // end the reuse for the rest of the scope (kept outputs dropped, a reuse handed out before gives the call's own output);
            // another mod's CombatStateChanged handler suspends it while it runs (6b); a reuse enumerated after the scope's end
            // gives the call's own output.
            bool ScopeCase(Action inner)
            {
                bool wasExact = _exactScope;
                int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial;
                bool wasSuspended = _suspended, wasSafeHere = _safe;
                try
                {
                    _snapshotDepth = 0;
                    _refreshDepth = 0;
                    EnterDisplayRefresh(out int outer);
                    _safe = true;
                    var key = ((object)models[5], (object?)null);
                    IEnumerable<AbstractModel> first = Augmented(key, models.Take(3).ToArray());
                    first.ToList();
                    Augmented(key, models.Take(3).ToArray()).ToList(); // The first reuse of the scope: checked.
                    IEnumerable<AbstractModel> reused = Augmented(key, models.Skip(3).ToArray());
                    bool wasReuse = reused is ReusedOutput && reused.SequenceEqual(models.Take(3));
                    inner();
                    bool stopped = _exactScope && (_augmented == null || _augmented.Count == 0) && reused.SequenceEqual(models.Skip(3));
                    ExitRefresh(null, outer);
                    return wasReuse && stopped;
                }
                finally
                {
                    _exactScope = wasExact;
                    _snapshotDepth = wasSnapshot;
                    _refreshDepth = wasRefresh;
                    _scopeSerial = wasSerial;
                    _suspended = wasSuspended;
                    _safe = wasSafeHere;
                }
            }
            Expect(ScopeCase(() =>
            {
                EnterRefresh();
                ExitRefresh(null);
            }), "a game-value refresh nested in a display scope ends the reuse");
            Expect(ScopeCase(() =>
            {
                SuspendPrefix(out int state);
                ResumeFinalizer(state);
            }), "a suspension ends the reuse");
            // 6b. The rule of 2026-11-17: in a CombatStateChanged dispatch other mods handle too, the handlers run one at a time,
            // each once, in order, with the same argument, a throw stopping the rest as Invoke does; the game's own keep the reuse,
            // another mod's runs with it suspended (its walks are the calls' own, a reuse handed out before gives the call's own
            // inside it), and nothing kept before it is reused after it. An error of this class's code before any handler ran: Invoke.
            System.Reflection.MethodInfo? gameHandler = typeof(MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter).GetMethod("OnCombatStateChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(CombatState) }, null);
            if (gameHandler != null)
            {
                object counter = RuntimeHelpers.GetUninitializedObject(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter));
                var game = (Action<CombatState>)Delegate.CreateDelegate(typeof(Action<CombatState>), counter, gameHandler);
                Action<CombatState> mixed = game + (_ => { }) + game;
                bool[]? gameOnly = ForeignHandlers(game, out _);
                bool[]? mixedFlags = ForeignHandlers(mixed, out Delegate[]? mixedList);
                Expect(gameOnly == null && mixedFlags is { Length: 3 } && !mixedFlags[0] && mixedFlags[1] && !mixedFlags[2] && mixedList is { Length: 3 },
                    "the game's own handlers are told apart from one of another assembly, index for index");
                _lastHandlers = null;
            }
            var argument = (CombatState)RuntimeHelpers.GetUninitializedObject(typeof(CombatState));
            (List<string> Calls, Exception? Error) Dispatch(Action<Action<CombatState>> invoke, bool throwAt3)
            {
                var calls = new List<string>();
                Action<CombatState> chain = null!;
                for (int n = 0; n < 5; n++)
                {
                    int index = n;
                    chain += s =>
                    {
                        calls.Add($"{index}{(ReferenceEquals(s, argument) ? "" : "?")}");
                        if (index == 0)
                        {
                            // A subscription during the dispatch: not called by it (the delegate as read).
                            chain += _ => calls.Add("subscribed during the dispatch");
                        }
                        if (index == 3 && throwAt3)
                        {
                            throw new SelfTestException();
                        }
                    };
                }
                try
                {
                    invoke(chain);
                    return (calls, null);
                }
                catch (Exception e)
                {
                    return (calls, e);
                }
            }
            bool[] flags = { false, true, false, true, false };
            Action<CombatState>[] Typed(Action<CombatState> chain) => chain.GetInvocationList().Cast<Action<CombatState>>().ToArray();
            {
                int wasSerial = _scopeSerial, wasForeign = _foreignDepth;
                try
                {
                    foreach (bool throwAt3 in new[] { false, true })
                    {
                        (List<string> vanilla, Exception? vanillaError) = Dispatch(chain => chain(argument), throwAt3);
                        (List<string> split, Exception? splitError) = Dispatch(chain => InvokeHandlers(Typed(chain), flags, argument), throwAt3);
                        Expect(split.SequenceEqual(vanilla) && (vanillaError == null) == (splitError == null)
                            && (splitError == null || splitError is SelfTestException) && _foreignDepth == wasForeign,
                            throwAt3 ? "one handler at a time: a throw stops the rest and passes on as Invoke's"
                                : "one handler at a time: each once, in order, the same argument, the delegate as read");
                    }
                    // Through the dispatch itself (every handler of this assembly), then with its own code failing before any ran.
                    bool wasCombat = _combatAugmented;
                    int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth;
                    bool wasSafeHere = _safe;
                    try
                    {
                        _snapshotDepth = 0;
                        _refreshDepth = 0;
                        EnterDisplayRefresh(out int outer);
                        _combatAugmented = true;
                        (List<string> vanilla, Exception? _) = Dispatch(chain => chain(argument), throwAt3: true);
                        long callsBefore = ForeignHandlerCalls;
                        (List<string> split, Exception? splitError) = Dispatch(chain => InvokeStateChangedInScope(chain, argument), throwAt3: true);
                        // With the shape rules off nothing is reused, so the dispatch is Invoke itself.
                        bool fourForeign = !PatchSafety.ShapeRules || ForeignHandlerCalls - callsBefore == 4;
                        _selfTestFailPlan = true;
                        int faultsBefore = _selfTestDispatchFaults;
                        (List<string> fallback, Exception? fallbackError) = Dispatch(chain => InvokeStateChangedInScope(chain, argument), throwAt3: true);
                        _selfTestFailPlan = false;
                        ExitRefresh(null, outer);
                        Expect(split.SequenceEqual(vanilla) && splitError is SelfTestException && fourForeign, "the dispatch runs other mods' handlers one at a time, as Invoke");
                        Expect(fallback.SequenceEqual(vanilla) && fallbackError is SelfTestException && (!PatchSafety.ShapeRules || _selfTestDispatchFaults == faultsBefore + 1),
                            "an error of the dispatch's own code before any handler ran: Invoke, each handler once");
                    }
                    finally
                    {
                        _selfTestFailPlan = false;
                        _combatAugmented = wasCombat;
                        _snapshotDepth = wasSnapshot;
                        _refreshDepth = wasRefresh;
                        _safe = wasSafeHere;
                        _lastHandlers = null;
                    }
                }
                finally
                {
                    _scopeSerial = wasSerial;
                    _foreignDepth = wasForeign;
                }
            }
            {
                int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial;
                bool wasSafeHere = _safe, wasExact = _exactScope;
                try
                {
                    _snapshotDepth = 0;
                    _refreshDepth = 0;
                    EnterDisplayRefresh(out int outer);
                    _safe = true;
                    var key = ((object)models[2], (object?)null);
                    // The walk as the postfix makes it: kept or reused only where the reuse is open.
                    IEnumerable<AbstractModel> Walk(IEnumerable<AbstractModel> output) => ReuseOpen ? Augmented(key, output) : output;
                    IEnumerable<AbstractModel>? held = null;
                    bool beforeReused = false, insideOwn = false, heldInside = false, insideClosed = false, afterDropped = false, afterKeptAgain = false, heldAfter = false;
                    long droppedBefore = KeptDroppedAfterForeign, keptAfterBefore = KeptAfterForeign, reusedAfterBefore = ReusedAfterForeign;
                    Action<CombatState>[] handlers =
                    {
                        _ =>
                        {
                            Walk(models.Take(3).ToArray()).ToList();
                            Walk(models.Take(3).ToArray()).ToList(); // The first reuse of the scope: checked.
                            held = Walk(models.Skip(3).ToArray());
                            beforeReused = held is ReusedOutput && held.SequenceEqual(models.Take(3));
                        },
                        _ =>
                        {
                            IEnumerable<AbstractModel> own = models.Skip(3).ToArray();
                            insideOwn = ReferenceEquals(Walk(own), own);
                            heldInside = held != null && held.SequenceEqual(models.Skip(3));
                            insideClosed = !ReuseOpen;
                        },
                        _ =>
                        {
                            afterDropped = _augmented == null || _augmented.Count == 0;
                            IEnumerable<AbstractModel> first = Walk(models.Skip(3).ToArray());
                            afterKeptAgain = first is ReusedOutput && first.SequenceEqual(models.Skip(3)) && ReuseOpen;
                            heldAfter = held != null && held.SequenceEqual(models.Skip(3));
                        },
                    };
                    InvokeHandlers(handlers, new[] { false, true, false }, argument);
                    ExitRefresh(null, outer);
                    Expect(beforeReused, "a game handler before another mod's reuses");
                    Expect(insideOwn && heldInside && insideClosed, "another mod's handler runs with the reuse suspended: its walks and a reuse handed out before are the calls' own");
                    Expect(afterDropped && KeptDroppedAfterForeign > droppedBefore && heldAfter, "nothing kept before another mod's handler is reused after it");
                    Expect(afterKeptAgain, "a game handler after another mod's keeps and reuses again");
                    Expect(KeptAfterForeign > keptAfterBefore && ReusedAfterForeign == reusedAfterBefore,
                        "the first walk of a dropped key after another mod's handler is counted as kept anew, not reused");
                }
                finally
                {
                    _snapshotDepth = wasSnapshot;
                    _refreshDepth = wasRefresh;
                    _scopeSerial = wasSerial;
                    _safe = wasSafeHere;
                    _exactScope = wasExact;
                }
            }
            bool afterEnd;
            {
                int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial;
                bool wasSafeHere = _safe;
                try
                {
                    _snapshotDepth = 0;
                    _refreshDepth = 0;
                    EnterDisplayRefresh(out int outer);
                    _safe = true;
                    var key = ((object)models[4], (object?)null);
                    Augmented(key, models.Take(3).ToArray()).ToList();
                    Augmented(key, models.Take(3).ToArray()).ToList();
                    IEnumerable<AbstractModel> reused = Augmented(key, models.Skip(3).ToArray());
                    ExitRefresh(null, outer);
                    afterEnd = reused.SequenceEqual(models.Skip(3));
                }
                finally
                {
                    _snapshotDepth = wasSnapshot;
                    _refreshDepth = wasRefresh;
                    _scopeSerial = wasSerial;
                    _safe = wasSafeHere;
                }
            }
            Expect(afterEnd, "a reuse enumerated after its scope ended is the call's own output");

            // 7. Re-audit of c2f613b (D1, D3, D6): a checked reuse after a first caller stopping early doesn't count as checked (the
            // next reuse is checked again); one covering everything its caller read does; the first walk's output is guarded as the
            // reuses are; at the scope's end the kept source no caller finished is disposed (its finally blocks run).
            bool d1, d3, d6;
            {
                int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial;
                bool wasSafeHere = _safe;
                try
                {
                    _snapshotDepth = 0;
                    _refreshDepth = 0;
                    EnterDisplayRefresh(out int outer);
                    _safe = true;
                    var key = ((object)models[3], (object?)null);
                    var source7 = new CountingSource(models.Take(3).ToArray());
                    IEnumerable<AbstractModel> firstWalk = Augmented(key, source7);
                    d6 = firstWalk is ReusedOutput;
                    firstWalk.Take(1).Count(); // The first caller stops at the first element.
                    IEnumerable<AbstractModel> check1 = Augmented(key, models.Take(3).ToArray());
                    check1.ToList(); // Reads three, the kept output pulled one: not covered.
                    IEnumerable<AbstractModel> check2 = Augmented(key, models.Take(3).ToArray());
                    bool again = check2 is CheckedOutput;
                    check2.Take(1).Count(); // Within what the kept one pulled: covered.
                    IEnumerable<AbstractModel> reuse = Augmented(key, models.Skip(3).ToArray());
                    d1 = again && reuse is ReusedOutput;
                    int disposedBefore = source7.Disposed;
                    ExitRefresh(null, outer);
                    d3 = disposedBefore == 0 && source7.Disposed == 1;
                }
                finally
                {
                    _snapshotDepth = wasSnapshot;
                    _refreshDepth = wasRefresh;
                    _scopeSerial = wasSerial;
                    _safe = wasSafeHere;
                }
            }
            Expect(d1, "a checked reuse that couldn't compare everything read leaves the key unchecked (D1)");
            Expect(d6, "the first walk's output is guarded by its scope (D6)");
            Expect(d3, "at the scope's end a kept source no caller finished is disposed (D3)");

            // 7b. An answered card sort (EnterSort, DESIGN "Sort walk reuse"): opened only with nothing else open; the first walk
            // kept, the next reuse checked, none after it (the same in every sort), each reused key compared with one fresh walk at
            // the sort's end; a pause (CardSortKeys' check) and a state version change give the call's own output; a nested
            // game-value refresh ends the reuse; a difference (a checked reuse, the end's fresh walk) ends it and leaves the display
            // part alone; the scope's end closes everything.
            SortSelfTest(Expect, models);

            // 8. KeptOnly's memo over a kept output (Walks Tier 2): the same result, the same pulls, every way it falls back.
            MemoSelfTest(Expect);

            // 9. The checksum snapshot's walk reuse and its net (DESIGN "Snapshot walk reuse").
            SnapshotSelfTest(Expect, models);
        }
        catch (Exception e)
        {
            failures.Add("threw: " + e);
        }
        finally
        {
            _selfTesting = false;
            // The handlers' verdict cached by the cases above is the test's: the next dispatch works it out (and logs) again.
            _lastHandlers = null;
            ForeignDispatches = wasDispatches;
            GameHandlerReuses = wasGameReuses;
            ForeignHandlerCalls = wasForeignCalls;
            KeptDroppedAfterForeign = wasDropped;
            KeptAfterForeign = wasKeptAfter;
            ReusedAfterForeign = wasReusedAfter;
            MemoBuilt = wasMemoBuilt;
            MemoHits = wasMemoHits;
            MemoVerified = wasMemoVerified;
            WalkMemoEnabled = wasMemoEnabled;
            _memoInjectFault = false;
            _memoInjectWrong = false;
            _keysAtForeignEnd?.Clear();
            WalkReuseEnabled = wasEnabled;
            _safe = wasSafe;
            _refreshDepth = wasDepth;
            _augmented = wasAugmented;
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("walk output self-test: " + string.Join(" | ", failures));
        }
        lines.Add($"FOREIGN walkreuse selftest: {lines.Count} cases as expected");
        return lines;
    }

    /// <summary>
    /// The answered card sort's scope and walk reuse (no game state: the walks are the test's arrays, the end's fresh walks the
    /// test's source). Removing the end's net (EndSortWalks returning at once) fails the end cases.
    /// </summary>
    private static void SortSelfTest(Action<bool, string> expect, AbstractModel[] models)
    {
        bool wasExact = _exactScope, wasSafeHere = _safe, wasSuspended = _suspended, wasPart = SortWalksEnabled, wasDisplay = WalkReuseEnabled;
        bool wasRefreshEnabled = RefreshEnabled;
        int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial, wasVersion = _version;
        long wasScopes = SortScopes, wasKept = SortWalksKept, wasReuses = SortWalkReuses, wasChecks = SortWalkChecks;
        long wasEndWalks = SortEndWalks, wasEndSkipped = SortEndSkipped;
        var key = ((object)models[2], (object?)null);
        AbstractModel[] kept = models.Take(3).ToArray(), other = models.Skip(3).ToArray();
        // The end's fresh walks: the test's source (the same as the kept output unless a case says otherwise), counted.
        int freshCalls = 0;
        AbstractModel[] fresh = kept;
        // One sort: EnterSort, then body inside it (the scope's _safe forced: no game state here), then ExitSort.
        bool Sort(Func<bool> body)
        {
            _snapshotDepth = 0;
            _refreshDepth = 0;
            _suspended = false;
            RefreshEnabled = true;
            SortWalksEnabled = true;
            int state = EnterSort();
            if (state == 0)
            {
                return false;
            }
            _safe = true;
            bool ok = _sortReuse && _refreshDepth == 1 && body();
            ExitSort(state);
            return ok && !_sortReuse && _refreshDepth == 0 && (_augmented == null || _augmented.Count == 0) && (_sortReused == null || _sortReused.Count == 0);
        }
        // The kinds of output 40 walks of one key get in a sort: k kept, c checked, r reused.
        string Pattern()
        {
            var kinds = new System.Text.StringBuilder();
            for (int i = 0; i < 40; i++)
            {
                IEnumerable<AbstractModel> output = Augmented(key, kept);
                kinds.Append(output is CheckedOutput ? 'c' : i == 0 ? 'k' : 'r');
                output.ToList();
            }
            return kinds.ToString();
        }
        try
        {
            _netSourceForTest = _ =>
            {
                freshCalls++;
                return fresh;
            };
            int mismatches = _selfTestMismatches;
            string first = "", second = "";
            bool scheduled = Sort(() => (first = Pattern()).Length == 40) && Sort(() => (second = Pattern()).Length == 40);
            string expected = "kc" + new string('r', 38);
            expect(scheduled && first == expected && second == expected && freshCalls == 2 && _selfTestMismatches == mismatches,
                $"a sort keeps, checks its first reuse, none after it, and compares each reused key once at its end ({first}, {freshCalls} fresh walks)");

            freshCalls = 0;
            bool lone = Sort(() =>
            {
                Augmented(key, kept).ToList();
                Augmented(key, kept).ToList(); // Checked: the call's own output, nothing reused.
                return true;
            });
            expect(lone && freshCalls == 0, "a sort that reused nothing (a key kept, then checked) walks nothing at its end");

            freshCalls = 0;
            bool paused = Sort(() =>
            {
                Augmented(key, kept).ToList();
                Augmented(key, kept).ToList(); // Checked.
                IEnumerable<AbstractModel> held = Augmented(key, other);
                bool wasPaused = PauseSort(true);
                bool own = !ReuseOpen && held.SequenceEqual(other);
                PauseSort(wasPaused);
                return own && ReuseOpen && held.SequenceEqual(kept);
            });
            expect(paused && freshCalls == 1, "a pause (CardSortKeys' check) gives the call's own output, the reuse back after it");

            freshCalls = 0;
            long skippedBefore = SortEndSkipped;
            bool versioned = Sort(() =>
            {
                Augmented(key, kept).ToList();
                Augmented(key, kept).ToList();
                Augmented(key, kept).ToList(); // Reused at the sort's first version.
                IEnumerable<AbstractModel> held = Augmented(key, other);
                _version++;
                return !ReuseOpen && held.SequenceEqual(other);
            });
            expect(versioned && freshCalls == 0 && SortEndSkipped == skippedBefore + 1,
                "a state version change ends a sort's reuse; what was reused before it isn't walked again at the end");

            freshCalls = 0;
            bool nested = Sort(() =>
            {
                Augmented(key, kept).ToList();
                Augmented(key, kept).ToList();
                IEnumerable<AbstractModel> held = Augmented(key, other);
                held.ToList(); // Reused.
                EnterRefresh();
                ExitRefresh(null);
                return _exactScope && !ReuseOpen && held.SequenceEqual(other);
            });
            expect(nested && freshCalls == 1, "a game-value refresh nested in a sort ends its reuse; what was reused before it is compared at the end");

            int before = _selfTestMismatches;
            freshCalls = 0;
            bool differs = Sort(() =>
            {
                Augmented(key, kept).ToList();
                Augmented(key, other).ToList(); // The first reuse is checked: another output.
                return _exactScope && !ReuseOpen;
            });
            expect(differs && _selfTestMismatches == before + 1 && freshCalls == 0 && WalkReuseEnabled == wasDisplay,
                "a difference at a sort's checked reuse ends its reuse, the display part untouched");

            // The end's fresh walk differing: another listener, a shorter walk, a longer one than a complete output; none past a
            // partly pulled output.
            bool EndFinds(AbstractModel[] freshNow, int read)
            {
                fresh = freshNow;
                int was = _selfTestMismatches;
                _selfTestSortEnd = null;
                bool ok = Sort(() =>
                {
                    Augmented(key, kept).Take(read).ToList();
                    Augmented(key, kept).Take(read).ToList(); // Checked, covered (stopped within what was pulled).
                    Augmented(key, kept).Take(read).ToList(); // Reused.
                    return ReuseOpen;
                });
                fresh = kept;
                return ok && _selfTestMismatches == was + 1 && _selfTestSortEnd != null && _selfTestSortEnd.Contains("at the sort's end");
            }
            expect(EndFinds(new[] { models[0], models[1], models[5] }, 99), "a sort's end: another listener in the fresh walk found");
            expect(EndFinds(models.Take(2).ToArray(), 99), "a sort's end: a shorter fresh walk found");
            expect(EndFinds(models.Take(4).ToArray(), 99), "a sort's end: longer than a complete reused output found");
            expect(!EndFinds(models.Take(4).ToArray(), 2), "a sort's end: longer than a partly pulled output: no difference (nothing past it was read)");
            expect(WalkReuseEnabled == wasDisplay, "a sort's end finding a difference leaves the display part untouched");

            // The end's fresh walk throwing (another mod's code): found, nothing escapes.
            Func<(object, object?), IEnumerable<AbstractModel>>? wasSource = _netSourceForTest;
            _netSourceForTest = _ => new CountingSource(kept, throwAt: 1);
            int beforeThrow = _selfTestMismatches;
            bool escaped = false;
            try
            {
                Sort(() =>
                {
                    Augmented(key, kept).ToList();
                    Augmented(key, kept).ToList();
                    Augmented(key, kept).ToList();
                    return true;
                });
            }
            catch
            {
                escaped = true;
            }
            _netSourceForTest = wasSource;
            expect(!escaped && _selfTestMismatches == beforeThrow + 1 && _selfTestSortEnd != null && _selfTestSortEnd.Contains("failed"),
                "a sort's end: a fresh walk that throws is found, nothing escapes");

            _refreshDepth = 1;
            int refused = EnterSort();
            _refreshDepth = 0;
            expect(refused == 0, "no sort scope inside another scope");
        }
        finally
        {
            _netSourceForTest = null;
            _selfTestSortEnd = null;
            _sortReused?.Clear();
            _exactScope = wasExact;
            _safe = wasSafeHere;
            _suspended = wasSuspended;
            SortWalksEnabled = wasPart;
            WalkReuseEnabled = wasDisplay;
            RefreshEnabled = wasRefreshEnabled;
            _snapshotDepth = wasSnapshot;
            _refreshDepth = wasRefresh;
            _scopeSerial = wasSerial;
            _version = wasVersion;
            _sortReuse = false;
            _sortPaused = false;
            SortScopes = wasScopes;
            SortWalksKept = wasKept;
            SortWalkReuses = wasReuses;
            SortWalkChecks = wasChecks;
            SortEndWalks = wasEndWalks;
            SortEndSkipped = wasEndSkipped;
        }
    }

    /// <summary>
    /// The checksum snapshot's walk reuse (no game state: the net's fresh walks are the test's): the snapshot's own check schedule
    /// whatever was counted before, the net finding nothing when the fresh walk is the same, and a detection for every way it can
    /// differ (another listener, shorter, longer than a complete output, none past a partly pulled one), for a checked reuse that
    /// differed, a state change during the snapshot, a reused output that threw and the fresh walk throwing; a key walked once
    /// compared too; nothing reused after the net. Removing the net (EndSnapshotWalks returning false) fails the detection cases.
    /// </summary>
    private static void SnapshotSelfTest(Action<bool, string> expect, AbstractModel[] models)
    {
        bool wasExact = _exactScope, wasSafeHere = _safe, wasSuspended = _suspended, wasCombatAugmented = _combatAugmented, wasRunAugmented = _runAugmented;
        bool wasSnapshotEnabled = SnapshotEnabled, wasPart = SnapshotWalksEnabled, wasDisplayPart = WalkReuseEnabled, wasDeclined = _mirrorDeclined;
        int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial, wasVersion = _version;
        MethodInfo? wasCombatPostfix = _combatWalkPostfix, wasRunPostfix = _runWalkPostfix;
        long wasDetections = SnapshotDetections, wasErrors = SnapshotNetErrors, wasReuses = SnapshotWalkReuses, wasKept = SnapshotWalksKept;
        long wasChecks = SnapshotWalkChecks, wasCompared = SnapshotNetCompared;
        MethodInfo anyPostfix = typeof(ListenerCache).GetMethod(nameof(CombatAugmentedPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
        int freshCalls = 0;
        AbstractModel[] fresh = models.Take(4).ToArray();
        try
        {
            _snapshotNetForTest = true;
            _netSourceForTest = _ =>
            {
                freshCalls++;
                return fresh;
            };
            _combatWalkPostfix ??= anyPostfix;
            _runWalkPostfix ??= anyPostfix;
            SnapshotEnabled = true;
            SnapshotWalksEnabled = true;
            _suspended = false;

            // A snapshot as EnterSnapshot opens it (outermost), the walks of a key, its net, its scope's end.
            bool Snapshot(Func<(object, object?), bool> body, out bool detected)
            {
                _snapshotDepth = 0;
                _refreshDepth = 0;
                EnterAny();
                _safe = true;
                _combatAugmented = true;
                _mirrorDeclined = false;
                BlockReuse();
                bool began = BeginSnapshotReuse(null);
                _snapshotDepth = 1;
                var key = ((object)models[5], (object?)null);
                bool ok = began && body(key);
                detected = EndSnapshotWalks();
                ExitSnapshot(null, 1);
                return ok;
            }
            AbstractModel[] four = models.Take(4).ToArray();

            // a. Kept, the first reuse checked, then reused; the same fresh walk: nothing found; nothing reused after the net.
            IEnumerable<AbstractModel>? held = null;
            bool sameOk = Snapshot(key =>
            {
                bool firstOk = Augmented(key, four).SequenceEqual(four);
                IEnumerable<AbstractModel> second = Augmented(key, four);
                bool checkedOk = second is CheckedOutput && second.SequenceEqual(four);
                held = Augmented(key, models.Skip(4).ToArray());
                return firstOk && checkedOk && held is ReusedOutput && held.SequenceEqual(four) && ReuseOpen;
            }, out bool sameDetected);
            expect(sameOk && !sameDetected && freshCalls == 1 && SnapshotDetections == wasDetections, "snapshot: kept, first reuse checked, then reused; the same fresh walk: no detection");
            expect(held != null && held.SequenceEqual(models.Skip(4)), "snapshot: a reuse enumerated after the net is the call's own output");

            // b. The schedule is the snapshot's own: walks 2 and 3 checked then reused whatever the display counts were.
            string Schedule()
            {
                string kinds = "";
                Snapshot(key =>
                {
                    Augmented(key, four).ToList();
                    for (int i = 0; i < 3; i++)
                    {
                        IEnumerable<AbstractModel> output = Augmented(key, four);
                        kinds += output is CheckedOutput ? "C" : output is ReusedOutput ? "R" : "?";
                        output.ToList();
                    }
                    return true;
                }, out _);
                return kinds;
            }
            string scheduleA = Schedule();
            for (int i = 0; i < 7; i++)
            {
                CountReuse(((object)models[5], (object?)null));
            }
            string scheduleB = Schedule();
            expect(scheduleA == "CRR" && scheduleB == "CRR", $"snapshot: the check schedule is the snapshot's own ({scheduleA}, {scheduleB})");

            // c. Every way the fresh walk can differ: found (each one a detection); none past a partly pulled output.
            bool Detects(AbstractModel[] freshNow, int read)
            {
                fresh = freshNow;
                Snapshot(key =>
                {
                    Augmented(key, four).Take(read).ToList();
                    Augmented(key, four).Take(read).ToList(); // Checked, covered (stopped within what was pulled).
                    Augmented(key, four).Take(read).ToList(); // Reused.
                    return true;
                }, out bool found);
                return found;
            }
            long before = SnapshotDetections;
            expect(Detects(new[] { models[0], models[1], models[5], models[3] }, 99), "snapshot net: another listener found");
            expect(Detects(models.Take(3).ToArray(), 99), "snapshot net: a shorter fresh walk found");
            expect(Detects(models.Take(5).ToArray(), 99), "snapshot net: longer than a complete output found");
            expect(!Detects(models.Take(5).ToArray(), 2), "snapshot net: longer than a partly pulled output: no difference (nothing past it was read)");
            expect(Detects(new[] { models[0], models[4] }, 2), "snapshot net: a difference within a partly pulled output found");
            expect(SnapshotDetections == before + 4, "snapshot net: each difference counted as a detection");
            fresh = four;

            // d. A checked reuse differing in the snapshot: reported by the net (no fresh walk needed), the display part untouched.
            int callsBefore = freshCalls;
            WalkReuseEnabled = true;
            Snapshot(key =>
            {
                Augmented(key, four).ToList();
                Augmented(key, models.Take(3).ToArray()).ToList(); // Checked: shorter than the complete kept output.
                return true;
            }, out bool checkedDetected);
            expect(checkedDetected && freshCalls == callsBefore && WalkReuseEnabled && SnapshotWalksEnabled,
                "snapshot: a checked reuse that differs is a detection, nothing turned off");

            // e. A state version change after a reuse: a detection, and no reuse after it.
            bool openAfterBump = true;
            Snapshot(key =>
            {
                Augmented(key, four).ToList();
                Augmented(key, four).ToList();
                Bump();
                openAfterBump = ReuseOpen;
                return true;
            }, out bool bumpDetected);
            expect(bumpDetected && !openAfterBump, "snapshot: a state change during it is a detection, no reuse after it");

            // f. The fresh walk throwing (another mod's code): a detection, nothing escapes.
            Func<(object, object?), IEnumerable<AbstractModel>>? wasSource = _netSourceForTest;
            _netSourceForTest = _ => new CountingSource(four, throwAt: 1);
            long errorsBefore = SnapshotNetErrors;
            bool escaped = false;
            bool throwDetected = false;
            try
            {
                Snapshot(key =>
                {
                    Augmented(key, four).ToList();
                    Augmented(key, four).ToList();
                    Augmented(key, four).ToList();
                    return true;
                }, out throwDetected);
            }
            catch
            {
                escaped = true;
            }
            _netSourceForTest = wasSource;
            expect(throwDetected && !escaped && SnapshotNetErrors == errorsBefore + 1, "snapshot net: a fresh walk that throws is a detection, nothing escapes");

            // g. A reused output that threw: a detection.
            bool reusedThrew = false;
            Snapshot(key =>
            {
                var source = new CountingSource(four, throwAt: 2);
                Augmented(key, source).Take(2).ToList();
                Augmented(key, four).Take(2).ToList(); // Checked, covered.
                try
                {
                    Augmented(key, four).ToList(); // Reused, pulled past the throw.
                }
                catch (SelfTestException)
                {
                    reusedThrew = true;
                }
                return true;
            }, out bool failedDetected);
            expect(reusedThrew && failedDetected, "snapshot net: a reused output that threw is a detection");

            // h. One walk of a key: its kept output is compared too (the same fresh walk: nothing found).
            callsBefore = freshCalls;
            Snapshot(key =>
            {
                Augmented(key, four).ToList();
                return true;
            }, out bool loneDetected);
            expect(!loneDetected && freshCalls == callsBefore + 1, "snapshot: a key walked once is compared too");
            fresh = models.Take(3).ToArray();
            Snapshot(key =>
            {
                Augmented(key, four).ToList();
                return true;
            }, out bool loneDifferent);
            fresh = four;
            expect(loneDifferent, "snapshot net: a key walked once that differs is found");

            // j. The first snapshot inside a display scope with game logic resuming inline (a peer whose hand selection ended): its
            // own context (nothing open around it, no suspension), the enclosing one back after it, exact from there as before.
            _snapshotDepth = 0;
            _refreshDepth = 0;
            EnterDisplayRefresh(out int dispatch);
            SuspendPrefix(out int suspension);
            EnterSnapshot(null, out int snapState);
            bool ownContext = _refreshDepth == 0 && !_suspended && _snapshotDepth == 1 && _enclosing != null;
            ExitSnapshot(null, snapState);
            ExitSnapshot(null, snapState); // Harmony may run a finalizer twice: restored once.
            bool restored = _refreshDepth == 1 && _suspended && _snapshotDepth == 0 && _exactScope && _enclosing == null;
            ResumeFinalizer(suspension);
            ExitRefresh(null, dispatch);
            expect(ownContext && restored && _refreshDepth == 0 && !_suspended,
                "snapshot: the first one inside a suspended display scope runs in its own context, the enclosing one restored after it");
            // Inside the vanilla reference (RunVanilla) a snapshot keeps the context it runs in (exact).
            bool vanillaKept = RunVanilla(() =>
            {
                EnterDisplayRefresh(out int scope);
                EnterSnapshot(null, out int state);
                bool kept = _refreshDepth == 1 && _suspended && _enclosing == null;
                ExitSnapshot(null, state);
                ExitRefresh(null, scope);
                return kept;
            });
            expect(vanillaKept, "snapshot: inside the vanilla reference it stays in its context");

            // i. The part off: no snapshot reuse at all (BeginSnapshotReuse), the snapshot exact.
            SnapshotWalksEnabled = false;
            bool offBegan = Snapshot(_ => true, out bool offDetected);
            SnapshotWalksEnabled = true;
            expect(!offBegan && !offDetected && _exactScope == false, "snapshot: the part off, no reuse");
        }
        catch (Exception e)
        {
            expect(false, "snapshot cases threw: " + e);
        }
        finally
        {
            _snapshotNetForTest = false;
            _netSourceForTest = null;
            _enclosing = null;
            _snapshotReuse = false;
            _snapshotTaint = null;
            _snapshotReused?.Clear();
            _combatWalkPostfix = wasCombatPostfix;
            _runWalkPostfix = wasRunPostfix;
            SnapshotEnabled = wasSnapshotEnabled;
            SnapshotWalksEnabled = wasPart;
            WalkReuseEnabled = wasDisplayPart;
            _exactScope = wasExact;
            _safe = wasSafeHere;
            _suspended = wasSuspended;
            _combatAugmented = wasCombatAugmented;
            _runAugmented = wasRunAugmented;
            _mirrorDeclined = wasDeclined;
            _snapshotDepth = wasSnapshot;
            _refreshDepth = wasRefresh;
            _scopeSerial = wasSerial;
            _version = Math.Max(_version, wasVersion);
            SnapshotDetections = wasDetections;
            SnapshotNetErrors = wasErrors;
            SnapshotWalkReuses = wasReuses;
            SnapshotWalksKept = wasKept;
            SnapshotWalkChecks = wasChecks;
            SnapshotNetCompared = wasCompared;
        }
    }

    /// <summary>
    /// KeptOnly's memo (<see cref="FilteredMemo"/>) against KeptOnly's own loop over the same elements: the same result in the
    /// same order (the first element kept though the filter drops it), the kept output pulled exactly as far, a later caller
    /// extending a partial memo, two callers interleaved, a caller going on when the memo is dropped under it, the checked use
    /// finding a memo made different (the caller getting KeptOnly's own result), an error in the memo's code falling back,
    /// a reuse after the scope's end or exact mode reading no memo and the memos dropped. Main thread (FastListeners' type ids).
    /// </summary>
    private static void MemoSelfTest(Action<bool, string> expect)
    {
        Type[] types = typeof(CardModel).Assembly.GetTypes();
        bool Concrete(Type t) => !t.IsAbstract && !t.ContainsGenericParameters;
        Type[] cards = types.Where(t => Concrete(t) && typeof(CardModel).IsAssignableFrom(t)).Take(2).ToArray();
        Type? other = types.FirstOrDefault(t => Concrete(t) && typeof(AbstractModel).IsAssignableFrom(t)
            && !typeof(CardModel).IsAssignableFrom(t) && !typeof(EnchantmentModel).IsAssignableFrom(t) && !typeof(AfflictionModel).IsAssignableFrom(t));
        if (cards.Length < 2 || other == null)
        {
            expect(false, "memo: two card types and a listener type outside piles found");
            return;
        }
        AbstractModel Make(Type t) => (AbstractModel)RuntimeHelpers.GetUninitializedObject(t);
        // B0 A1 B2 R3 A4 B5 A6, the filter keeping A: KeptOnly gives B0 (the first, A4), A1, R3 (not a pile entry), A4, A6.
        AbstractModel[] walk = { Make(cards[1]), Make(cards[0]), Make(cards[1]), Make(other), Make(cards[0]), Make(cards[1]), Make(cards[0]) };
        AbstractModel[] expected = { walk[0], walk[1], walk[3], walk[4], walk[6] };
        FastListeners.PileFilter NewFilter() => new(t => t == cards[0], keepSubscribed: false);
        List<AbstractModel> Fresh(IEnumerable<AbstractModel> items, FastListeners.PileFilter f) => FastListeners.KeptOnlyForTest(items.ToArray(), f).ToList();
        bool mainThread = FastListeners.MemoThreadForTest;
        expect(mainThread, "memo: run on FastListeners' main thread");
        expect(Fresh(walk, NewFilter()).SequenceEqual(expected), "memo: KeptOnly's own loop gives the expected listeners (the reference)");

        bool wasExact = _exactScope, wasSafeHere = _safe, wasFastListeners = FastListeners.Enabled, wasMemo = WalkMemoEnabled;
        int wasSnapshot = _snapshotDepth, wasRefresh = _refreshDepth, wasSerial = _scopeSerial;
        int faultsBefore = _selfTestMemoFaults, mismatchesBefore = _selfTestMismatches;
        try
        {
            _snapshotDepth = 0;
            _refreshDepth = 0;
            _exactScope = false;
            WalkMemoEnabled = true;
            FastListeners.Enabled = true;
            EnterDisplayRefresh(out int outer);
            _safe = true;
            // A kept output (the first walk of a key: a reuse guarded by its scope), read through KeptOnly several times.
            var source = new CountingSource(walk);
            var key = ((object)source, (object?)null);
            IEnumerable<AbstractModel> reuse = Augmented(key, source);
            LazyOutput? kept = (reuse as ReusedOutput)?.KeptNow();
            expect(kept != null, "memo: the first walk's output is a reuse giving the kept output in its scope");
            if (kept == null)
            {
                ExitRefresh(null, outer);
                return;
            }
            FastListeners.PileFilter filter = NewFilter();
            long built = MemoBuilt, hits = MemoHits, verified = MemoVerified;
            // A caller stopping at the second kept listener pulls two elements, as KeptOnly's own loop would (B0, A1).
            bool early = FastListeners.KeptOnlyForTest(reuse, filter).Take(2).SequenceEqual(expected.Take(2)) && source.Produced == 2;
            expect(early && MemoBuilt == built + 1, "memo: made by the first filtered walk, which pulls only as far as its caller goes");
            // The filter's first use of a memo is checked: KeptOnly's own loop, compared as far as the memo knows. One going
            // further than the memo knows isn't covered (checked again next time); this one stops within it: covered.
            bool uncovered = FastListeners.KeptOnlyForTest(reuse, filter).Take(3).SequenceEqual(expected.Take(3)) && source.Produced == 4;
            bool checkedUse = FastListeners.KeptOnlyForTest(reuse, filter).Take(2).SequenceEqual(expected.Take(2));
            expect(uncovered && checkedUse && MemoVerified == verified + 2 && MemoHits == hits, "memo: the filter's first uses are checked until one covers what its caller read");
            // The memo is still partial (two kept, two judged): the next caller replays them, then judges on, pulling only as
            // far as KeptOnly's loop would (to R3: four elements), and the next one completes it.
            bool extended = FastListeners.KeptOnlyForTest(reuse, filter).Take(3).SequenceEqual(expected.Take(3)) && source.Produced == 4;
            bool rest = FastListeners.KeptOnlyForTest(reuse, filter).SequenceEqual(expected);
            expect(extended && rest && source.Produced == 7 && source.Enumerations == 1 && source.Disposed == 1 && MemoHits == hits + 2,
                "memo: later callers replay it and extend it as far as they go, one enumeration of the source");
            bool hit = FastListeners.KeptOnlyForTest(reuse, filter).SequenceEqual(expected);
            expect(hit && MemoHits == hits + 3 && source.Produced == 7 && _selfTestMismatches == mismatchesBefore, "memo: replayed whole, no difference");
            // A second filter over the same output (keeping B) has its own memo: B0, B2, R3, B5 (one keyed by the output only
            // would replay the first filter's).
            FastListeners.PileFilter filterB = new(t => t == cards[1], keepSubscribed: false);
            List<AbstractModel> expectedB = Fresh(walk, new FastListeners.PileFilter(t => t == cards[1], keepSubscribed: false));
            bool secondFilter = expectedB.SequenceEqual(new[] { walk[0], walk[2], walk[3], walk[5] })
                && FastListeners.KeptOnlyForTest(reuse, filterB).SequenceEqual(expectedB)
                && FastListeners.KeptOnlyForTest(reuse, filterB).SequenceEqual(expectedB)
                && FastListeners.KeptOnlyForTest(reuse, filterB).SequenceEqual(expectedB)
                && FastListeners.KeptOnlyForTest(reuse, filter).SequenceEqual(expected);
            expect(secondFilter && _selfTestMismatches == mismatchesBefore, "memo: each filter has its own memo over the same output");
            // Live entries (Limitless's stacked enchantments in game; here B's type stands for it): judged with Keeps at every
            // replay as KeptOnly's loop judges them (B2 and B5, dropped by this filter), the first one kept as always; a checked
            // use doesn't count a live entry the filter dropped as a difference.
            FastListeners._liveTypeForTest = cards[1];
            try
            {
                FastListeners.PileFilter liveFilter = NewFilter();
                long liveBefore = FastListeners.MemoLiveJudged;
                bool liveBuild = FastListeners.KeptOnlyForTest(reuse, liveFilter).SequenceEqual(expected);
                bool liveCheck = FastListeners.KeptOnlyForTest(reuse, liveFilter).SequenceEqual(expected); // Its first check, covered.
                bool liveHit = FastListeners.KeptOnlyForTest(reuse, liveFilter).SequenceEqual(expected);
                expect(liveBuild && liveCheck && liveHit && FastListeners.MemoLiveJudged == liveBefore + 4 && _selfTestMismatches == mismatchesBefore,
                    "memo: a stacked enchantment is a live entry, judged at every replay, the checked use equal");
            }
            finally
            {
                FastListeners._liveTypeForTest = null;
            }
            // Two callers of one memo interleaved: one step of the first, the second whole (extending it), the first's rest.
            FastListeners.PileFilter filter2 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse, filter2).Take(1).Count();
            FastListeners.KeptOnlyForTest(reuse, filter2).Take(1).Count(); // Its first check, covered.
            using (IEnumerator<AbstractModel> first = FastListeners.KeptOnlyForTest(reuse, filter2).GetEnumerator())
            {
                bool one = first.MoveNext() && ReferenceEquals(first.Current, expected[0]);
                bool second = FastListeners.KeptOnlyForTest(reuse, filter2).SequenceEqual(expected);
                var firstAll = new List<AbstractModel> { first.Current };
                while (first.MoveNext())
                {
                    firstAll.Add(first.Current);
                }
                expect(one && second && firstAll.SequenceEqual(expected), "memo: two callers interleaved each get the whole result");
            }
            // A caller in a memo when it is dropped goes on with KeptOnly's own loop from where it is.
            FastListeners.PileFilter filter3 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse, filter3).Take(2).Count();
            FastListeners.KeptOnlyForTest(reuse, filter3).Take(2).Count(); // Its first check, covered.
            using (IEnumerator<AbstractModel> inMemo = FastListeners.KeptOnlyForTest(reuse, filter3).GetEnumerator())
            {
                var got = new List<AbstractModel>();
                for (int n = 0; n < 2 && inMemo.MoveNext(); n++)
                {
                    got.Add(inMemo.Current);
                }
                kept.DropMemos();
                while (inMemo.MoveNext())
                {
                    got.Add(inMemo.Current);
                }
                expect(got.SequenceEqual(expected), "memo: a caller in a memo dropped under it goes on as KeptOnly's own loop");
            }
            // The control: a memo made different, its use checked: the caller gets KeptOnly's own result, the difference found.
            FastListeners.PileFilter filter4 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse, filter4).ToList();
            FastListeners.KeptOnlyForTest(reuse, filter4).ToList(); // Its first check, covered.
            _memoInjectWrong = true;
            bool wrongUse = FastListeners.KeptOnlyForTest(reuse, filter4).SequenceEqual(expected);
            expect(wrongUse && !_memoInjectWrong && _selfTestMismatches > mismatchesBefore, "memo: a memo made different is found by its check, the caller unaffected");
            // An error in the memo's own code: that walk runs KeptOnly's own loop.
            _memoInjectFault = true;
            bool faulted = FastListeners.KeptOnlyForTest(reuse, NewFilter()).SequenceEqual(expected);
            expect(faulted && !_memoInjectFault && _selfTestMemoFaults == faultsBefore + 1, "memo: an error in its code falls back to KeptOnly's own loop");
            // The type ids kept per element are FastListeners' own.
            bool ids = Enumerable.Range(0, walk.Length).All(i => kept.TypeIdAt(i, FastListeners.ModelTypeIdForTest) == FastListeners.ModelTypeIdForTest(walk[i]));
            expect(ids, "memo: the per-element type ids are FastListeners' type ids");
            // An error in the memo's own code while extending it (a type id): that caller goes on with KeptOnly's loop from that
            // element, the memo off, the part's fault counted, FastListeners untouched (never its fault, never the caller's).
            var source7 = new CountingSource(walk);
            IEnumerable<AbstractModel> reuse7 = Augmented(((object)source7, (object?)null), source7);
            FastListeners.PileFilter filter7 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse7, filter7).Take(1).Count();
            FastListeners.KeptOnlyForTest(reuse7, filter7).Take(1).Count(); // Its first check, covered.
            int faultsBeforeTypeId = _selfTestMemoFaults;
            long hitsBeforeTypeId = MemoHits;
            FastListeners._typeIdFaultForTest = true;
            bool typeIdFault = FastListeners.KeptOnlyForTest(reuse7, filter7).SequenceEqual(expected);
            FastListeners._typeIdFaultForTest = false;
            bool offAfter = FastListeners.KeptOnlyForTest(reuse7, filter7).SequenceEqual(expected) && MemoHits == hitsBeforeTypeId + 1;
            expect(typeIdFault && offAfter && _selfTestMemoFaults == faultsBeforeTypeId + 1 && FastListeners.Enabled,
                "memo: an error in its own code while extending it falls back from that element, the memo off, FastListeners on");
            // The part turned off under a caller (a difference or an error elsewhere): it goes on with KeptOnly's loop from its
            // position, the memo no longer replayed nor extended.
            FastListeners.PileFilter filter8 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse, filter8).Take(1).Count();
            FastListeners.KeptOnlyForTest(reuse, filter8).Take(1).Count(); // Its first check, covered.
            var afterOff = new List<AbstractModel>();
            using (IEnumerator<AbstractModel> replaying = FastListeners.KeptOnlyForTest(reuse, filter8).GetEnumerator())
            {
                if (replaying.MoveNext())
                {
                    afterOff.Add(replaying.Current);
                }
                WalkMemoEnabled = false;
                while (replaying.MoveNext())
                {
                    afterOff.Add(replaying.Current);
                }
                WalkMemoEnabled = true;
            }
            expect(afterOff.SequenceEqual(expected) && kept.MemoOf(filter8, out _).Judged == 1, "memo: the part turned off under a caller stops its replay, KeptOnly's loop goes on");
            // A filter verdict throwing while a memo is extended (B's type here): as KeptOnly's loop, that element and the rest
            // kept for that caller and the verdict's error handled as KeptOnly's (Disable, counted here), the memo off.
            FastListeners._disablesForTest = 0;
            FastListeners._countDisablesForTest = true;
            try
            {
                Func<Type, bool> throwing = t => t == cards[1] ? throw new SelfTestException() : t == cards[0];
                bool freshThrow = Fresh(walk, new FastListeners.PileFilter(throwing, keepSubscribed: false)).SequenceEqual(walk);
                int disablesFresh = FastListeners._disablesForTest;
                FastListeners.PileFilter throwingFilter = new(throwing, keepSubscribed: false);
                bool memoThrow = FastListeners.KeptOnlyForTest(reuse, throwingFilter).SequenceEqual(walk);
                bool thenOwn = FastListeners.KeptOnlyForTest(reuse, throwingFilter).SequenceEqual(walk);
                expect(freshThrow && memoThrow && thenOwn && disablesFresh == 1 && FastListeners._disablesForTest == 3 && kept.MemoOf(throwingFilter, out _).Off,
                    "memo: a verdict throwing during an extension is handled as KeptOnly's loop handles it, the memo off");
            }
            finally
            {
                FastListeners._countDisablesForTest = false;
            }
            // A caller resumed on another thread (a hook awaiting between two listeners): the type ids and verdict arrays are
            // main-thread state, so it goes on with KeptOnly's loop there, the memo not extended.
            FastListeners.PileFilter filter9 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse, filter9).Take(1).Count();
            FastListeners.KeptOnlyForTest(reuse, filter9).Take(1).Count(); // Its first check, covered.
            var crossed = new List<AbstractModel>();
            using (IEnumerator<AbstractModel> resumed = FastListeners.KeptOnlyForTest(reuse, filter9).GetEnumerator())
            {
                if (resumed.MoveNext())
                {
                    crossed.Add(resumed.Current);
                }
                System.Threading.Tasks.Task.Run(() =>
                {
                    while (resumed.MoveNext())
                    {
                        crossed.Add(resumed.Current);
                    }
                }).Wait();
            }
            expect(crossed.SequenceEqual(expected) && kept.MemoOf(filter9, out _).Judged == 1, "memo: a caller resumed on another thread goes on with KeptOnly's loop, the memo not extended");
            // A memo caller is a caller of the kept output (D3): its close asked meanwhile (exact mode) waits for it, so it goes on
            // to the end as KeptOnly's own loop over the output would, and the close is done when it leaves.
            var source6 = new CountingSource(walk);
            IEnumerable<AbstractModel> reuse6 = Augmented(((object)source6, (object?)null), source6);
            FastListeners.PileFilter filter6 = NewFilter();
            FastListeners.KeptOnlyForTest(reuse6, filter6).Take(1).Count();
            FastListeners.KeptOnlyForTest(reuse6, filter6).Take(1).Count(); // Its first check, covered.
            var through = new List<AbstractModel>();
            int disposedInside;
            using (IEnumerator<AbstractModel> inside = FastListeners.KeptOnlyForTest(reuse6, filter6).GetEnumerator())
            {
                for (int n = 0; n < 2 && inside.MoveNext(); n++)
                {
                    through.Add(inside.Current);
                }
                kept.MemoOf(filter, out _);
                ExactFromHere();
                while (inside.MoveNext())
                {
                    through.Add(inside.Current);
                }
                disposedInside = source6.Disposed;
            }
            expect(through.SequenceEqual(expected) && source6.Produced == 7 && disposedInside == 1 && source6.Enumerations == 1,
                "memo: a caller in it when its output's close is asked goes on to the end (the close waits for it)");
            // Exact mode in the scope (started just above): a reuse held from before gives the call's own output (its source
            // enumerated again, no memo read), the memos dropped.
            long builtBeforeExact = MemoBuilt;
            bool exactOwn = FastListeners.KeptOnlyForTest(reuse, NewFilter()).SequenceEqual(expected) && source.Enumerations == 2;
            kept.MemoOf(filter, out bool madeAgain);
            expect(exactOwn && madeAgain && MemoBuilt == builtBeforeExact, "memo: exact mode reads no memo and drops them");
            ExitRefresh(null, outer);
            // After the scope's end, a reuse held from it is the call's own output, read without a memo.
            var source5 = new CountingSource(walk);
            _exactScope = false;
            EnterDisplayRefresh(out int outer5);
            _safe = true;
            IEnumerable<AbstractModel> held = Augmented(((object)source5, (object?)null), new List<AbstractModel>(walk.Take(3)));
            ExitRefresh(null, outer5);
            long builtBeforeEnd = MemoBuilt;
            bool afterEnd = FastListeners.KeptOnlyForTest(held, NewFilter()).SequenceEqual(Fresh(walk.Take(3), NewFilter()));
            expect(afterEnd && MemoBuilt == builtBeforeEnd, "memo: a reuse enumerated after its scope reads no memo");
        }
        finally
        {
            _exactScope = wasExact;
            _snapshotDepth = wasSnapshot;
            _refreshDepth = wasRefresh;
            _scopeSerial = wasSerial;
            _safe = wasSafeHere;
            FastListeners.Enabled = wasFastListeners;
            WalkMemoEnabled = wasMemo;
        }
    }
#endif

#if BENCH
    /// <summary>CombatStateChanged dispatches with another mod's handler, run one handler at a time (<see cref="InvokeHandlers"/>).</summary>
    public static long ForeignDispatches { get; private set; }
    /// <summary>Walk outputs reused inside the game's own handlers of those dispatches (the rule of 2026-11-17: the reuse stays there).</summary>
    public static long GameHandlerReuses { get; private set; }
    /// <summary>Other mods' handlers run with the reuse suspended.</summary>
    public static long ForeignHandlerCalls { get; private set; }
    /// <summary>Kept outputs dropped when another mod's handler returned (none reused after it).</summary>
    public static long KeptDroppedAfterForeign { get; private set; }

    /// <summary>A game handler of a dispatch run one handler at a time is running (counts <see cref="GameHandlerReuses"/>).</summary>
    [ThreadStatic] private static bool _inGameHandler;

    /// <summary>
    /// Keys kept when another mod's handler returned, whose next walk must keep a new output (<see cref="KeptAfterForeign"/>) and
    /// never reuse one (<see cref="ReusedAfterForeign"/>: a drop missing, or another path keeping across it). Cleared per outermost scope.
    /// </summary>
    [ThreadStatic] private static HashSet<(object, object?)>? _keysAtForeignEnd;
    /// <summary>Keys' first walks after another mod's handler that kept a new output (expected), and that were reused or checked (must stay 0).</summary>
    public static long KeptAfterForeign { get; private set; }
    public static long ReusedAfterForeign { get; private set; }
#endif

    /// <summary>
    /// The augmented-walk premise's kept note (bdopt why, the menu's hover): the walks carrying other mods' postfixes whose output
    /// is reused within a display refresh. Cleared with the shape rules off, the part off, or nothing of the kind there.
    /// </summary>
    private static void NoteAugmentedWalks()
    {
        try
        {
            var walks = new List<MethodBase>();
            if (PatchSafety.ShapeRules && WalkReuseEnabled && RefreshEnabled && _applied)
            {
                if (PatchSafety.HasOtherPatches(_combatIterate) && _combatWalkPostfix != null && PostfixLast(_combatIterate, _combatWalkPostfix, ref _combatLast))
                {
                    walks.Add(_combatIterate!);
                }
                if (PatchSafety.HasOtherPatches(_runIterate) && _runWalkPostfix != null && PostfixLast(_runIterate, _runWalkPostfix, ref _runLast))
                {
                    walks.Add(_runIterate!);
                }
            }
            if (walks.Count == 0)
            {
                PatchSafety.ClearKept(WalkReuseName, "walk_reuse");
                // Nothing reused: other mods' handlers see the game's walks however they run (the next dispatch notes them again).
                PatchSafety.ClearExact(WalkReuseName, "foreign_handlers");
                _lastHandlers = null;
                return;
            }
            var mods = new List<string>(PatchSafety.OtherModsPatching(walks));
            foreach (MethodBase walk in walks)
            {
                mods.AddRange(HarmonyWatch.HiddenOwners(walk));
            }
            string names = string.Join(", ", walks.Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
            PatchSafety.Kept(WalkReuseName, "walk_reuse", $"other mods' code on the listener walks ({names}) runs once per display refresh instead of "
                + "once per walk: its output, enumerated only as far as the game goes, is reused for that refresh's walks (a stated premise: "
                + "within one display refresh, at one state, it gives the same listeners; each refresh's first reuse of a walk and one in "
                + $"{WalkVerifyEvery} after it are compared with a fresh call, a difference turns this off; the co-op state check, game-value "
                + "refreshes and game logic resuming in a refresh never reuse it, nor other mods' CombatStateChanged handlers)", mods.ToArray());
        }
        catch
        {
            // Display only.
        }
    }

    /// <summary>The note (back-off preflight) for scopes that can't cache while other mods patch the walks: see <see cref="EnterAny"/>.</summary>
    private static void NoteWalkMirrors()
    {
        var mods = new List<Assembly?>();
        foreach (MethodInfo? walk in new[] { _combatIterate, _runIterate })
        {
            if (walk != null && PatchSafety.HasOtherPatches(walk) && Harmony.GetPatchInfo(walk) is { } info)
            {
                mods.AddRange(info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                    .Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => p.PatchMethod?.DeclaringType?.Assembly));
            }
        }
        if (mods.Count == 0 || FastListeners.MirrorsUnpatched)
        {
            PatchSafety.ClearNote(Name, "walk_mirrors");
            return;
        }
        PatchSafety.Note(Name, "walk_mirrors", "other mods patch the listener walks: their lists are built from the listener-walk optimization's "
            + "copies of the walks, which is off here, so no list is kept", PatchSafety.ModNamesOf(mods), whole: true);
    }

    /// <summary>Set once <see cref="Apply"/> got through: until then (or when it threw) no scope caches.</summary>
    private static bool _applied;

    /// <summary>
    /// <c>NPlayerHand.RevalidateSelectionAfterStateChange</c> runs inside the CombatStateChanged dispatch scope (while the
    /// player chooses cards in hand) and ends the selection when no hand card is valid any more: <c>TrySetResult</c> on a
    /// task made without <c>RunContinuationsAsynchronously</c>, awaited on the same context, so the card's play goes on
    /// inline, inside the scope (on host or solo, the action queue with it). The game's walks there must be its lazy
    /// ones (<c>Contains</c> at each yield: a listener leaving the state during an async hook's walk is skipped), not a
    /// list built for the scope: everything this mod does with listeners runs vanilla until the method returns.
    /// </summary>
    private static void SuspendPrefix(out int __state)
    {
        __state = _suspended ? 2 : 1;
        _suspended = true;
        // Game logic may change the state from here: no walk output kept before is reused after it (re-audit of the premises, W-5).
        ExactFromHere();
#if BENCH
        SuspendedRevalidations++;
#endif
    }

    /// <summary>
    /// Restores what the prefix found; 0: the prefix never ran (an earlier prefix threw: Harmony runs the finalizers with the
    /// default state). A prefix returning false doesn't stop this one: it returns void and takes only <c>__state</c>.
    /// </summary>
    private static void ResumeFinalizer(int __state)
    {
        if (__state != 0)
        {
            _suspended = __state == 2;
        }
    }

    /// <summary>
    /// The code the dispatch scope's suspension relies on (SuspendPrefix): the hand's CombatStateChanged handler, which calls
    /// <c>RevalidateSelectionAfterStateChange</c>, that method, which ends a selection no hand card fits any more
    /// (<c>TrySetResult</c>), and <c>SelectCards</c>, which makes that task without <c>RunContinuationsAsynchronously</c> (the
    /// card's play then goes on inline). Same on v0.107.1, v0.111.0, v0.113.0 and v0.113.1 but SelectCards' body (v0.107.1 vs
    /// v0.111+: the task made the same way in both).
    /// </summary>
    private static readonly (string Method, bool Body, string[] Fingerprints)[] DispatchResumeCode =
    {
        ("OnCombatStateChanged", false, new[] { "B1E8150FD5A853F6" }),
        ("RevalidateSelectionAfterStateChange", false, new[] { "92DB756795D6C546" }),
        ("SelectCards", true, new[] { "ED039D9684E57FC5", "D189372D06A00A80" }),
    };

    /// <summary>
    /// Whether the game code that can resume game logic inside the CombatStateChanged dispatch is still the one SuspendPrefix
    /// covers. Changed, another place may end a selection (or a new handler resume a play) inside the scope, unsuspended: the
    /// whole dispatch scope stays vanilla then (logged), not only the suspension.
    /// </summary>
    private static bool DispatchResumesKnown()
    {
        foreach ((string name, bool body, string[] fingerprints) in DispatchResumeCode)
        {
            MethodInfo? method = Reflect.Method(typeof(NPlayerHand), name);
            MethodBase? code = method == null ? null : body ? Reflect.AsyncMoveNext(method) : method;
            string actual = code == null ? "missing" : MethodFingerprint.Of(code);
            if (!fingerprints.Any(f => PatchSafety.FingerprintMatches(actual, f)))
            {
                Log.Info($"[{ModEntry.Id}] {Name}: NPlayerHand.{name} changed (fingerprint {actual}, expected {string.Join(" or ", fingerprints)}): "
                    + "the CombatStateChanged dispatch scope stays vanilla (game logic may resume inside it)");
                return false;
            }
        }
        return true;
    }

    /// <summary>A method the cache must watch to stay up to date: missing, the whole patch is skipped (named in the log).</summary>
    private static MethodBase Watched(MethodBase? method, Type type, string name) =>
        method ?? throw new PatchSkippedException($"{type.Name}.{name}, which it watches to keep its cache up to date, is missing (game update?): left vanilla");

    private static MethodBase Require(MethodBase? method) =>
        method ?? throw new MissingMethodException("ListenerCache target not found (game update?)");

    private static MethodInfo Require(MethodInfo? method) =>
        method ?? throw new MissingMethodException("ListenerCache target not found (game update?)");

    private static void TryScope(Harmony harmony, MethodBase? target, string enter, string exit)
    {
        try
        {
            PatchGuard.Patch(harmony, Require(target), prefix: Method(enter), finalizer: Method(exit));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: scope {target?.DeclaringType?.Name}.{target?.Name} unavailable: {e.Message}");
        }
    }

    private static HarmonyMethod Method(string name) => new(typeof(ListenerCache), name);

    private static MethodInfo? _invokeInScope;

    private static void TryDispatchScope(Harmony harmony)
    {
        try
        {
            MethodInfo dispatch = Require(Reflect.Method(typeof(CombatStateTracker), "CallCombatStateChangedDeferred"));
            MethodInfo moveNext = Require(Reflect.AsyncMoveNext(dispatch));
            _invokeInScope = Require(Reflect.Method(typeof(ListenerCache), nameof(InvokeStateChangedInScope)));
            PatchGuard.Patch(harmony, moveNext, transpiler: Method(nameof(DispatchTranspiler)));
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: CombatStateChanged dispatch scope unavailable: {e.Message}");
        }
    }

    /// <summary>
    /// Replaces <c>this.CombatStateChanged?.Invoke(_state)</c> with <see cref="InvokeStateChangedInScope"/>. Leaves the
    /// method untouched unless exactly one such call is found.
    /// </summary>
    private static IEnumerable<CodeInstruction> DispatchTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo invoke = Reflect.Method(typeof(Action<CombatState>), nameof(Action<CombatState>.Invoke));
        List<CodeInstruction> calls = code.Where(c => c.Calls(invoke)).ToList();
        if (calls.Count != 1 || _invokeInScope == null)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: unexpected CombatStateChanged dispatch ({calls.Count} invoke calls), scope not installed.");
            return code;
        }
        // Same stack shape: (Action<CombatState>, CombatState) -> void.
        calls[0].opcode = System.Reflection.Emit.OpCodes.Call;
        calls[0].operand = _invokeInScope;
        return code;
    }

    /// <summary>
    /// Invokes the CombatStateChanged handlers inside one refresh scope. <paramref name="handlers"/> is the delegate the game's
    /// <c>this.CombatStateChanged?.Invoke(_state)</c> read and tested for null (the transpiler replaces only the Invoke call:
    /// the field read once, before <c>_state</c>, as in the game), so a handler subscribing or unsubscribing during the dispatch
    /// changes nothing here, as with Invoke (a delegate's invocation list never changes). Where an augmented walk output may be
    /// reused and some handler isn't the game's, they run one at a time (<see cref="InvokeHandlers"/>); otherwise Invoke.
    /// </summary>
    public static void InvokeStateChangedInScope(Action<CombatState> handlers, CombatState state)
    {
        EnterRefresh(display: true);
        try
        {
            Delegate[]? list = null;
            bool[]? foreign = null;
            try
            {
                // Only where a walk carries other mods' postfixes (the only case anything is reused).
                if ((_combatAugmented || _runAugmented) && PatchSafety.ShapeRules && WalkReuseEnabled)
                {
                    foreign = ForeignHandlers(handlers, out Delegate[]? invocation);
#if BENCH
                    if (_selfTestFailPlan)
                    {
                        throw new SelfTestException();
                    }
#endif
                    if (foreign != null)
                    {
                        // Checked here, before any handler runs, so the loop's unchecked casts can't fail (an Action<CombatState>'s
                        // invocation list holds only Action<CombatState> delegates).
                        if (invocation == null || invocation.Length != foreign.Length || Array.Exists(invocation, d => d is not Action<CombatState>))
                        {
                            throw new InvalidOperationException("CombatStateChanged handlers' verdict doesn't match their invocation list");
                        }
                        list = invocation;
                    }
                }
            }
            catch (Exception e)
            {
                // This class's own error, before any handler ran: Invoke, as the game, and the part off for the session.
                list = null;
                DispatchFailed(e);
            }
            if (list == null || foreign == null)
            {
                handlers(state);
                return;
            }
            InvokeHandlers(list, foreign, state);
        }
        finally
        {
            ExitRefresh(null);
        }
    }

    /// <summary>
    /// The rule of 2026-11-17 (owner-approved; DESIGN "Owner-approved premises", Walks Tier 2): every handler once, in the
    /// delegate's order, with the same argument, as Invoke calls them (a throw stops the rest and reaches the game's dispatch
    /// unchanged). The game's own handlers run with the reuse as before. Another mod's (or one this class can't tell) runs with
    /// it suspended for its whole run (<see cref="_foreignDepth"/>): its walks, and those of everything it runs or waits on
    /// inline, are the game's walks at the scope's exact tier (the unpatched lists by state version, every other mod's postfix
    /// run at each call), never a kept output; an output handed out before it gives its caller the call's own there
    /// (the scope serial moves on); and no output kept before it is reused after it: dropped when it returns, since another
    /// mod's handler may change state no version bump sees (its own mod's, which other mods' walk postfixes may read). This
    /// class's code between the handlers never throws: an error there turns the part off (the rest run with no reuse at all).
    /// </summary>
    private static void InvokeHandlers(Delegate[] list, bool[] foreign, CombatState state)
    {
#if BENCH
        ForeignDispatches++;
#endif
        for (int i = 0; i < list.Length; i++)
        {
            if (!foreign[i])
            {
#if BENCH
                bool wasGame = _inGameHandler;
                _inGameHandler = true;
                try
                {
                    Unsafe.As<Action<CombatState>>(list[i])(state);
                }
                finally
                {
                    _inGameHandler = wasGame;
                }
#else
                Unsafe.As<Action<CombatState>>(list[i])(state);
#endif
                continue;
            }
            int was = BeginForeignHandler();
            try
            {
                Unsafe.As<Action<CombatState>>(list[i])(state);
            }
            finally
            {
                EndForeignHandler(was);
            }
        }
    }

    /// <summary>Another mod's handler starts: the reuse suspended (any depth), outputs handed out before no longer in scope. Never throws.</summary>
    private static int BeginForeignHandler()
    {
        int was = _foreignDepth;
        _foreignDepth = was + 1;
        _scopeSerial++;
#if BENCH
        ForeignHandlerCalls++;
#endif
        return was;
    }

    /// <summary>
    /// Another mod's handler returned (or threw): the depth it found back, outputs handed out during it out of scope, and every
    /// kept output dropped (closed: an unfinished source disposed once no caller is in it). Never throws.
    /// </summary>
    private static void EndForeignHandler(int was)
    {
        _foreignDepth = was;
        _scopeSerial++;
        try
        {
            int kept = _augmented?.Count ?? 0;
#if BENCH
            // Recorded apart from the drop (the effects check: a missing drop shows as ReusedAfterForeign, not in this count).
            if (kept > 0)
            {
                (_keysAtForeignEnd ??= new HashSet<(object, object?)>(new RefComparer())).UnionWith(_augmented!.Keys);
            }
#endif
            ClearAugmented();
#if BENCH
            KeptDroppedAfterForeign += kept;
#endif
        }
        catch (Exception e)
        {
            DispatchFailed(e);
        }
    }

    /// <summary>This class's error in the dispatch's own code: the part off for the session (no reuse anywhere from here). Never throws.</summary>
    private static void DispatchFailed(Exception e)
    {
#if BENCH
        if (_selfTesting)
        {
            _selfTestDispatchFaults++;
            return;
        }
#endif
        try
        {
            WalkReuseFailed(e);
        }
        catch
        {
            WalkReuseEnabled = false;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with every listener optimization off, producing a pure vanilla result.
    /// </summary>
    public static T RunVanilla<T>(Func<T> action)
    {
        bool wasSuspended = _suspended;
        int wasVanilla = _vanillaRef;
        _suspended = true;
        _vanillaRef = wasVanilla + 1;
        try
        {
            return action();
        }
        finally
        {
            _suspended = wasSuspended;
            _vanillaRef = wasVanilla;
        }
    }

    /// <summary>Bumped by every invalidation (this thread).</summary>
    public static int Version => _version;

    /// <summary>Inside <see cref="RunVanilla"/>: listener optimizations must stay off (the result is the reference).</summary>
    public static bool IsSuspended => _suspended;

    /// <summary>
    /// The combat listener sequence, materialized once per scope and state version.
    /// </summary>
    public static ReadOnlyCollection<AbstractModel> GetListeners(CombatState combatState)
    {
        _cache ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
        if (_cache.TryGetValue((combatState, null), out var entry) && entry.Version == _version)
        {
            return entry.Listeners;
        }
        _materializingCombat = true;
        try
        {
            ReadOnlyCollection<AbstractModel>? kept = Kept((combatState, null));
            ReadOnlyCollection<AbstractModel> listeners = kept != null && !VerifyThisReuse()
                ? kept
                : CheckedAgainstKept(kept, Materialize(CombatSource(combatState), entry.Listeners?.Count ?? kept?.Count ?? 0));
            _cache[(combatState, null)] = (_version, listeners);
            return listeners;
        }
        finally
        {
            _materializingCombat = false;
        }
    }

    /// <summary>
    /// The run listener sequence (deck + run subscribers + combat), materialized once per scope and state version.
    /// </summary>
    public static ReadOnlyCollection<AbstractModel> GetRunListeners(RunState runState, ICombatState? child)
    {
        _cache ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
        if (_cache.TryGetValue((runState, child), out var entry) && entry.Version == _version)
        {
            return entry.Listeners;
        }
        _materializingRun = true;
        try
        {
            ReadOnlyCollection<AbstractModel>? kept = Kept((runState, child));
            ReadOnlyCollection<AbstractModel> listeners = kept != null && !VerifyThisReuse()
                ? kept
                : CheckedAgainstKept(kept, Materialize(RunSource(runState, child), entry.Listeners?.Count ?? kept?.Count ?? 0));
            _cache[(runState, child)] = (_version, listeners);
            return listeners;
        }
        finally
        {
            _materializingRun = false;
        }
    }

    /// <summary>
    /// What a combat list is built from: the game's call (no other mod's patch there: <see cref="EnterAny"/>), or, while other
    /// mods' postfixes on it are let stand, the unpatched walk (FastListeners' mirror, called directly): built through the
    /// game's call, the list would hold their output and they would run on it again at each replay.
    /// </summary>
    private static IEnumerable<AbstractModel> CombatSource(CombatState combatState) =>
        !_combatAugmented ? combatState.IterateHookListeners()
            : FastListeners.UnpatchedCombat(combatState) ?? throw new UnpatchedWalkMissing();

    /// <summary>
    /// <see cref="CombatSource"/> for the run walk. Its child combat walk goes through the game's call either way (as the
    /// original's): never served with a child while the combat walk carries other mods' patches (<see cref="RunIteratePrefix"/>),
    /// whose output would then be kept.
    /// </summary>
    private static IEnumerable<AbstractModel> RunSource(RunState runState, ICombatState? child) =>
        !_runAugmented ? runState.IterateHookListeners(child)
            : FastListeners.UnpatchedRun(runState, child) ?? throw new UnpatchedWalkMissing();

    /// <summary>
    /// An augmented scope's list can't be built: FastListeners' unpatched mirror declined (another mod patched the walk's state
    /// machine or Contains since the scope opened: <see cref="HarmonyWatch"/> makes that visible mid-scope). Not an error of this
    /// mod: the prefixes then let the game's call run (as outside a scope) for the rest of the scope (<see cref="_mirrorDeclined"/>:
    /// thrown once per scope, not once per walk), ListenerCache stays on.
    /// </summary>
    private sealed class UnpatchedWalkMissing : Exception
    {
        public UnpatchedWalkMissing() : base("the unpatched listener walk is unavailable (its code is patched by another mod now)")
        {
        }
    }

    /// <summary>
    /// <c>listeners.ToList().AsReadOnly()</c>, sized from the previous count: thousands of entries grown by doubling
    /// would allocate twice that, the last arrays on the large object heap.
    /// </summary>
    private static ReadOnlyCollection<AbstractModel> Materialize(IEnumerable<AbstractModel> listeners, int sizeHint)
    {
        var list = new List<AbstractModel>(sizeHint);
        foreach (AbstractModel listener in listeners)
        {
            list.Add(listener);
        }
        return list.AsReadOnly();
    }

    /// <summary>
    /// Harmony finalizer of every watched method: runs whether it returned or threw (a second run only bumps again). Void:
    /// with every finalizer of a method void, Harmony rethrows the game's exception with its own stack trace.
    /// </summary>
    private static void Invalidate()
    {
        Bump();
    }

    /// <summary><see cref="Invalidate"/> for the creature lists and powers (counted apart in test builds).</summary>
    private static void InvalidateCreatures()
    {
        Bump();
#if BENCH
        CreatureInvalidations++;
#endif
    }

    /// <summary><see cref="Invalidate"/> for the dominion's setters (v0.113; test builds count a dominion set, not cleared).</summary>
#if BENCH
    private static void InvalidateDominion(object? value)
    {
        Bump();
        if (value != null)
        {
            DominionInvalidations++;
        }
    }

    /// <summary>Dominions set (a Dread run: its act entry on the run, each combat built in it), each invalidating (effect counter).</summary>
    public static long DominionInvalidations { get; private set; }
#else
    private static void InvalidateDominion() => Bump();
#endif

    /// <summary><see cref="Invalidate"/> for the run models' setters (v0.113; test builds count the lists of more than one model).</summary>
#if BENCH
    private static void InvalidateRunModels(System.Collections.IEnumerable? value)
    {
        Bump();
        if (value != null && value.Cast<object>().Skip(1).Any())
        {
            ModeRunModelInvalidations++;
        }
    }

    /// <summary>Run model lists set with a game mode's model (Dread, Blitz: the run's creation, each combat built in it).</summary>
    public static long ModeRunModelInvalidations { get; private set; }
#else
    private static void InvalidateRunModels() => Bump();
#endif

    private static void InvalidateIfOwned(CardModel __instance)
    {
        // Never throws into the setter: unsure, invalidate.
        try
        {
            // A card in no pile is in no walk: its enchantment or affliction changing changes no listener list. Owned clones
            // outside every pile (a mod's previews, predictions) no longer drop the kept lists then. Only when the pile index
            // knows it exactly (PileIndex.InNoPile: false when unsure, off or mid-move); else invalidate as before.
            if (_cardOwner == null || (_cardOwner(__instance) != null && !PileIndex.InNoPile(__instance)))
            {
                Bump();
            }
        }
        catch
        {
            Bump();
        }
    }

    /// <summary>
    /// A new version: the kept lists can never be used again (<see cref="Kept"/> needs the same version) and go now, so they
    /// don't hold a finished combat (its piles cleared at its end) until the next display scope.
    /// </summary>
    private static void Bump()
    {
        _version++;
        if (_kept is { Count: > 0 })
        {
            _kept.Clear();
        }
    }

    private static void EnterAny()
    {
        if (_snapshotDepth + _refreshDepth == 0)
        {
            _exactScope = false;
            _augmented?.Clear();
            _scopeSerial++;
#if BENCH
            _keysAtForeignEnd?.Clear();
#endif
            try
            {
                // Checked per outermost scope: another mod may patch after we load. A replayed list stands for the whole
                // walk: the iterators' state machines, Contains and the mod subscriber walks too (FastListeners knows them):
                // no tolerance there. On IterateHookListeners itself, postfixes (and void prefixes sorted before every prefix
                // of this mod) are let stand (SurroundingOnly): the lists are then built from the unpatched walk
                // (FastListeners' mirror, not the game's call: CombatSource), and Harmony runs those postfixes after the
                // serving prefix on every call, on the replayed list, once, as on the game's stream without this mod. Only
                // the unpatched part is kept (what the scopes already assume stable); what the postfixes add, drop or reorder
                // is computed live at each call. HookFilter, which uses the lists directly, stays off then (IsCaching).
                _safe = ScopesSafe(out bool combatOthers, out bool runOthers);
                _combatAugmented = combatOthers;
                _runAugmented = runOthers;
                _mirrorDeclined = false;
#if BENCH
                if (_safe && (combatOthers || runOthers))
                {
                    AugmentedScopes++;
                }
#endif
            }
            catch (Exception e)
            {
                // Runs in the game's methods (scope prefixes): never throw there; this scope stays vanilla.
                _safe = false;
                _combatAugmented = _runAugmented = false;
                _mirrorDeclined = false;
                if (!_safeCheckFailed)
                {
                    _safeCheckFailed = true;
                    Log.Warn($"[{ModEntry.Id}] {Name}: patch check failed, scopes left vanilla while it does: {e.Message}");
                }
            }
        }
    }

    private static bool _safeCheckFailed;

    /// <summary>
    /// The scope check an outermost scope makes at its start (<see cref="EnterAny"/>): no other mod's patch this class can't let
    /// stand on the walks or the walk code FastListeners knows, and FastListeners' unpatched mirrors where other mods' postfixes
    /// on the walks are let stand (<paramref name="combatOthers"/>, <paramref name="runOthers"/>). Setup only (the mods' patches):
    /// the same on every peer with the same mods. May throw.
    /// </summary>
    private static bool ScopesSafe(out bool combatOthers, out bool runOthers, bool atLoad = false)
    {
        combatOthers = PatchSafety.HasOtherPatches(_combatIterate);
        runOthers = PatchSafety.HasOtherPatches(_runIterate);
        // atLoad (the co-op tag): the mirrors as applied and switched, not whether this thread is suspended right now.
        return _applied && !PatchSafety.IsPatchedByOthers(_combatIterate, PatchTolerance.SurroundingOnly)
            && !PatchSafety.IsPatchedByOthers(_runIterate, PatchTolerance.SurroundingOnly)
            && !FastListeners.WalkCodePatchedByOthers()
            && (!(combatOthers || runOthers) || (atLoad ? FastListeners.MirrorsApplied : FastListeners.MirrorsUnpatched));
    }

    [ThreadStatic] private static bool _releaseAtExit;

    /// <summary>Raised after the game cleans a run up: caches holding its models drop them.</summary>
    public static event Action? RunReleased;

    private static void RunCleanedUp()
    {
        try
        {
            if (_snapshotDepth + _refreshDepth == 0)
            {
                _kept?.Clear();
                _fromKept?.Clear();
                _cache?.Clear();
                _augmented?.Clear();
            }
            else
            {
                _releaseAtExit = true; // Inside a scope: its exit hands nothing on.
            }
            RunReleased?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: releasing the left run's lists failed: {e.Message}");
        }
    }

    /// <summary>
    /// Runs in the scopes' finalizers: never throws (Harmony would run every finalizer of the method again, then throw into
    /// the game). Safe to run twice: the second run finds its cache empty and leaves what the first handed on.
    /// </summary>
    private static void ExitAny()
    {
        if (_snapshotDepth + _refreshDepth != 0)
        {
            return;
        }
        try
        {
            // Refresh-only scopes hand their lists to the next refresh scope (valid while the version is unchanged). A scope
            // that built nothing hands nothing new: the lists kept before stay, each checked against the version on use.
            bool release = _releaseAtExit;
            _releaseAtExit = false;
            if (release || _reuseBlocked || !ReuseEnabled)
            {
                _kept?.Clear();
            }
            if (release)
            {
                RunReleased?.Invoke(); // Lists filtered later in that scope for the left run go too.
            }
            else if (!_reuseBlocked && ReuseEnabled && _cache is { Count: > 0 })
            {
                _kept ??= new Dictionary<(object, object?), (int, ReadOnlyCollection<AbstractModel>)>(new RefComparer());
                _kept.Clear();
                foreach (var pair in _cache)
                {
                    _kept[pair.Key] = pair.Value;
                }
            }
            _reuseBlocked = false;
            _fromKept?.Clear();
            _cache?.Clear();
            ClearAugmented();
            _exactScope = false;
            ScopeEnded?.Invoke();
        }
        catch (Exception e)
        {
            // A dependent cache (ScopeEnded) may not have dropped its lists: no scope caches any more, so none is used.
            _reuseBlocked = false;
            _fromKept?.Clear();
            Fail(e);
        }
    }

    /// <summary>
    /// Reuse of listener lists across display refresh scopes (UI refreshing several times a second, e.g. BetterSpire2's
    /// damage preview): never in or after a snapshot or game-state refresh scope, only while no invalidation happened.
    /// </summary>
    public static bool ReuseEnabled { get; set; } = true;
    public const string ReuseFault = Name + ".Reuse";
    private const int ReuseVerifyEvery = 16;
    public static long Reused { get; private set; }
    public static long ReuseVerified { get; private set; }
    [ThreadStatic] private static Dictionary<(object, object?), (int Version, ReadOnlyCollection<AbstractModel> Listeners)>? _kept;
    /// <summary>A snapshot or a game-state refresh scope is (or was) open in the current outermost scope.</summary>
    [ThreadStatic] private static bool _reuseBlocked;
    /// <summary>Cache keys whose list came from a kept one, dropped if a game-state scope opens inside.</summary>
    [ThreadStatic] private static HashSet<(object, object?)>? _fromKept;

    private static ReadOnlyCollection<AbstractModel>? Kept((object, object?) key)
    {
        if (!ReuseEnabled || _reuseBlocked || _kept == null
            || !_kept.TryGetValue(key, out var entry) || entry.Version != _version || CardModifiersMayChange())
        {
            return null;
        }
        Reused++;
        (_fromKept ??= new HashSet<(object, object?)>(new RefComparer())).Add(key);
        return entry.Listeners;
    }

#if BENCH
    /// <summary>Test builds: also when <c>listenercache</c> is armed (<see cref="TestWrong"/>): this reuse is checked, against a wrong kept list.</summary>
    private static bool VerifyThisReuse() => Reused % ReuseVerifyEvery == 0 || (_wrongKept = TestWrong.Take("listenercache"));

    [ThreadStatic] private static bool _wrongKept;
#else
    private static bool VerifyThisReuse() => Reused % ReuseVerifyEvery == 0;
#endif

    /// <summary>BaseLib's card modifier base type.</summary>
    private const string CardModifierType = "BaseLib.Abstracts.CardModifier";
    private const string BaseLibAssembly = "BaseLib";
    /// <summary>0: not looked up yet (or BaseLib loaded since), 1: no such type (no BaseLib), 2: BaseLib has it.</summary>
    private static int _cardModifierBase;
    private static int _watchingBaseLib;

    /// <summary>
    /// Whether a mod may apply BaseLib card modifiers. BaseLib's "BaseLibCardModifiers" subscriber lists each pile card's
    /// modifiers, which BaseLib changes with no method this cache watches: a kept list may then miss one applied or removed
    /// since, so lists aren't reused across display scopes (each scope still builds its own once). No BaseLib, or no mod
    /// defining a concrete CardModifier (every supported setup today): reused as before. Never throws: can't tell, no reuse.
    /// </summary>
    private static bool CardModifiersMayChange()
    {
        try
        {
            int known = System.Threading.Volatile.Read(ref _cardModifierBase);
            if (known == 0)
            {
                // Looked up in BaseLib's assembly only: AccessTools.TypeByName on a missing type (no BaseLib, the common
                // setup) falls back to GetTypes() on every loaded assembly, a hitch in the middle of a combat.
                known = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name == BaseLibAssembly && a.GetType(CardModifierType, false) != null) ? 2 : 1;
                System.Threading.Interlocked.CompareExchange(ref _cardModifierBase, known, 0);
            }
            return known == 2 && !BaseLibTypes.NoneDefined(CardModifierType);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Runs inside another assembly's load: never throws.</summary>
    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            if (args.LoadedAssembly.GetName().Name == BaseLibAssembly)
            {
                System.Threading.Volatile.Write(ref _cardModifierBase, 0);
            }
        }
        catch
        {
            System.Threading.Volatile.Write(ref _cardModifierBase, 0);
        }
    }

    /// <summary>Fresh list built while a kept one was available: they must match, else reuse is unsafe.</summary>
    private static ReadOnlyCollection<AbstractModel> CheckedAgainstKept(ReadOnlyCollection<AbstractModel>? kept, ReadOnlyCollection<AbstractModel> fresh)
    {
        if (kept != null)
        {
            ReuseVerified++;
#if BENCH
            if (_wrongKept)
            {
                // Test: the kept list missing its last entry, or with the fresh list's first twice (the check must see it).
                _wrongKept = false;
                kept = new ReadOnlyCollection<AbstractModel>(kept.Count > 0 ? kept.Take(kept.Count - 1).ToList() : fresh.Take(1).Concat(fresh).ToList());
            }
#endif
            if (!kept.SequenceEqual(fresh, RefComparer.Models))
            {
                ReuseEnabled = false;
                _kept?.Clear();
                PatchSafety.Fault(ReuseFault);
                Log.Error($"[{ModEntry.Id}] {Name}: a kept listener list differs from a fresh one ({kept.Count} vs {fresh.Count}): "
                    + "reuse across refreshes disabled for this session. Please report this log.");
            }
        }
        return fresh;
    }

    // Scope prefixes keep the depth they found plus one in __state and their finalizers restore it instead of decrementing:
    // Harmony 2.4 runs every finalizer of a method a second time when one of them throws (another mod's included), and a
    // second decrement would leave this thread's depth negative (no scope ever caching again, or one never closing).
    // A state of 0 is the default Harmony gives a finalizer whose prefix never ran (an earlier prefix threw): no scope was
    // opened, nothing to restore (restoring 0 would close the scopes this call runs nested in).

    /// <summary>
    /// The checksum snapshot's scope (<c>NetFullCombatState.FromRun</c>). The first one (no snapshot open) runs in a context of its
    /// own (<see cref="SetAside"/>): whatever encloses it on this peer (a refresh scope, the CombatStateChanged dispatch with a
    /// hand selection's end resuming the card's play inline, its suspension, another mod's handler) is set aside for its
    /// duration, so it is decided and served exactly as a snapshot with nothing around it, the same on every peer (a peer whose
    /// selection ended runs the action queue, and the checksum after each action, inside that dispatch: R3-3). It may reuse the
    /// augmented walks' outputs (<see cref="BeginSnapshotReuse"/>, checked by its net at its end: <see cref="EndSnapshotWalks"/>).
    /// A nested snapshot, or one inside the vanilla reference (<see cref="RunVanilla"/>), is exact from its start.
    /// </summary>
    private static void EnterSnapshot(MegaCrit.Sts2.Core.Runs.IRunState? runState, out int __state)
    {
        __state = _snapshotDepth + 1;
        if (_snapshotDepth == 0 && _vanillaRef == 0 && _enclosing == null
            && (_refreshDepth > 0 || _suspended || _foreignDepth > 0 || _materializingAugmented || _materializingCombat || _materializingRun))
        {
            SetAside();
            __state |= SetAsideFlag;
        }
        bool outermost = _snapshotDepth + _refreshDepth == 0;
        EnterAny();
        BlockReuse();
        if (!(outermost && _vanillaRef == 0 && BeginSnapshotReuse(runState)))
        {
            ExactFromHere();
        }
        _snapshotDepth++;
    }

    private static void ExitSnapshot(Exception? __exception, int __state)
    {
        if (__state <= 0)
        {
            return;
        }
        bool setAside = (__state & SetAsideFlag) != 0;
        _snapshotDepth = (__state & ~SetAsideFlag) - 1;
        if (_snapshotDepth + _refreshDepth == 0)
        {
            EndSnapshotScope(__exception);
        }
        ExitAny();
        // Harmony runs the finalizers a second time when one throws: the context is restored once (_enclosing taken).
        if (setAside && _enclosing != null)
        {
            Restore();
        }
    }

    /// <summary>In a snapshot scope's state: its context was set aside at its start (<see cref="SetAside"/>).</summary>
    private const int SetAsideFlag = 1 << 24;

    /// <summary>
    /// Inside <see cref="RunVanilla"/> (the vanilla reference: SnapshotVerifier's re-check, the benches): its snapshots stay exact
    /// and in the context they run in. Apart from <see cref="_suspended"/>, which a suspension (game logic resuming inline) sets too.
    /// </summary>
    [ThreadStatic] private static int _vanillaRef;

    /// <summary>What a first snapshot set aside (<see cref="SetAside"/>), until its end.</summary>
    private sealed class EnclosingContext
    {
        public int RefreshDepth, ForeignDepth;
        public bool Suspended, ExactScope, ReuseBlocked, Safe, CombatAugmented, RunAugmented, MirrorDeclined, ReleaseAtExit;
        public bool MaterializingCombat, MaterializingRun, MaterializingAugmented;
        public bool SortReuse, SortPaused;
        public int SortVersion;
#if BENCH
        public bool InGameHandler;
#endif
        public Dictionary<(object, object?), KeptOutput>? Augmented;
        public Dictionary<(object, object?), (int Version, ReadOnlyCollection<AbstractModel> Listeners)>? Cache;
        public HashSet<(object, object?)>? FromKept;
    }

    [ThreadStatic] private static EnclosingContext? _enclosing;

    /// <summary>
    /// A first snapshot inside another context: this thread's scope state as it is (refresh depth, suspension, another mod's
    /// handler, the kept outputs and lists) put aside, and a context with nothing open in its place: the snapshot then opens as
    /// an outermost scope (its own lists, its own kept outputs, its own exact flag and serial). Never throws.
    /// </summary>
    private static void SetAside()
    {
        _enclosing = new EnclosingContext
        {
            RefreshDepth = _refreshDepth, ForeignDepth = _foreignDepth, Suspended = _suspended, ExactScope = _exactScope,
            ReuseBlocked = _reuseBlocked, Safe = _safe, CombatAugmented = _combatAugmented, RunAugmented = _runAugmented,
            MirrorDeclined = _mirrorDeclined, ReleaseAtExit = _releaseAtExit, MaterializingCombat = _materializingCombat,
            MaterializingRun = _materializingRun, MaterializingAugmented = _materializingAugmented,
            SortReuse = _sortReuse, SortPaused = _sortPaused, SortVersion = _sortVersion,
            Augmented = _augmented, Cache = _cache, FromKept = _fromKept,
        };
        _sortReuse = false;
        _sortPaused = false;
        _refreshDepth = 0;
        _foreignDepth = 0;
        _suspended = false;
        _exactScope = false;
        _reuseBlocked = false;
        _releaseAtExit = false;
        _materializingCombat = _materializingRun = _materializingAugmented = false;
#if BENCH
        _enclosing.InGameHandler = _inGameHandler;
        _inGameHandler = false;
#endif
        _augmented = null;
        _cache = null;
        _fromKept = null;
    }

    /// <summary>
    /// The snapshot set aside its context ended (its own scope closed): that context back, with what a snapshot inside it always
    /// did to it (no kept list or walk output reused in it from here: <see cref="BlockReuse"/>, <see cref="ExactFromHere"/>, a new
    /// serial so nothing handed out before is the kept output when enumerated later). Never throws.
    /// </summary>
    private static void Restore()
    {
        EnclosingContext? outer = _enclosing;
        _enclosing = null;
        if (outer == null)
        {
            return;
        }
        _refreshDepth = outer.RefreshDepth;
        _foreignDepth = outer.ForeignDepth;
        _suspended = outer.Suspended;
        _exactScope = outer.ExactScope;
        _reuseBlocked = outer.ReuseBlocked;
        _safe = outer.Safe;
        _combatAugmented = outer.CombatAugmented;
        _runAugmented = outer.RunAugmented;
        _mirrorDeclined = outer.MirrorDeclined;
        _releaseAtExit = outer.ReleaseAtExit;
        _materializingCombat = outer.MaterializingCombat;
        _materializingRun = outer.MaterializingRun;
        _materializingAugmented = outer.MaterializingAugmented;
        _sortReuse = outer.SortReuse;
        _sortPaused = outer.SortPaused;
        _sortVersion = outer.SortVersion;
#if BENCH
        _inGameHandler = outer.InGameHandler;
#endif
        _augmented = outer.Augmented;
        _cache = outer.Cache;
        _fromKept = outer.FromKept;
        _scopeSerial++;
        try
        {
            BlockReuse();
            ExactFromHere();
        }
        catch
        {
            _exactScope = true;
        }
    }

    /// <summary>
    /// Refresh scope around a pass whose results can feed game state (enchantment values, first-turn filters): lists
    /// are always built fresh here, never reused from an earlier scope. For callers pairing it with <c>ExitRefresh(null)</c>
    /// in a try/finally; patches use <see cref="EnterRefreshPatch"/>.
    /// </summary>
    internal static void EnterRefresh() => EnterRefresh(display: false);

    /// <summary>
    /// <see cref="EnterRefresh()"/>; <paramref name="display"/>: the CombatStateChanged dispatch (its handlers are the game's
    /// display, game logic resuming inside it runs vanilla: <see cref="SuspendPrefix"/>), where the augmented walks' outputs may
    /// still be reused within the scope. Any other caller's pass may feed game state: none reused from there on.
    /// </summary>
    private static void EnterRefresh(bool display)
    {
        EnterAny();
        BlockReuse();
        if (!display)
        {
            ExactFromHere();
        }
        _refreshDepth++;
    }

    /// <summary><see cref="EnterRefresh"/> as a Harmony prefix (finalizer <see cref="ExitRefresh"/>).</summary>
    private static void EnterRefreshPatch(out int __state)
    {
        __state = _refreshDepth + 1;
        EnterRefresh();
    }

    /// <summary>
    /// Refresh scope around a display-only pass (card visuals, other mods' previews): may reuse kept lists. Harmony prefix
    /// (finalizer <see cref="ExitRefresh"/>); its state (the depth found plus one, never 0) is what <see cref="ExitRefresh"/> takes.
    /// </summary>
    internal static void EnterDisplayRefresh(out int __state)
    {
        __state = _refreshDepth + 1;
        EnterAny();
        _refreshDepth++;
    }

    /// <summary>From here to the end of the outermost scope, no kept list is used, and lists already taken from one are dropped.</summary>
    private static void BlockReuse()
    {
        _reuseBlocked = true;
        if (_fromKept is { Count: > 0 })
        {
            foreach (var key in _fromKept)
            {
                _cache?.Remove(key);
            }
            _fromKept.Clear();
        }
    }

    /// <summary>
    /// Harmony finalizer of <see cref="EnterRefreshPatch"/> and <see cref="EnterDisplayRefresh"/> (restores the depth they
    /// found: state - 1; 0, their prefix never ran: nothing to close), or called directly after <see cref="EnterRefresh"/>
    /// in a finally (no state, -1: one level less).
    /// </summary>
    internal static void ExitRefresh(Exception? __exception, int __state = -1)
    {
        if (__state == 0)
        {
            return;
        }
        _refreshDepth = __state > 0 ? __state - 1 : Math.Max(0, _refreshDepth - 1);
        ExitAny();
    }

#if BENCH
    /// <summary>Walks answered from a materialized list (the effects check).</summary>
    public static long Served { get; private set; }
    /// <summary>Invalidations by the creature lists and powers watched in <see cref="Apply"/> (the effects check: they run).</summary>
    public static long CreatureInvalidations { get; private set; }
    /// <summary>Hand selections revalidated with the listener optimizations suspended (<see cref="SuspendPrefix"/>).</summary>
    public static long SuspendedRevalidations { get; private set; }
    /// <summary>Scopes open on this thread: 0 when nothing runs (the effects check; a lost finalizer leaves them open).</summary>
    public static int OpenScopes => _snapshotDepth + _refreshDepth;
#endif

    private static bool CombatIteratePrefix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
    {
        // Other mods' postfixes let stand: only while the unpatched mirror can build the list (else the game's call).
        if (!Serving || _materializingCombat || (_combatAugmented && (_mirrorDeclined || !FastListeners.MirrorsUnpatched)))
        {
            return true;
        }
        try
        {
            // Another mod's postfix let stand gets the list behind a plain enumerable, as the game's iterator is (no list
            // interface it could test, cast or change: the list is shared by every call of the scope).
            __result = _combatAugmented ? new Replay(GetListeners(__instance)) : GetListeners(__instance);
#if BENCH
            Served++;
#endif
            return false;
        }
        catch (UnpatchedWalkMissing)
        {
            // The game's call: what an augmented scope relies on no longer holds (see UnpatchedWalkMissing), for the rest of it.
            _mirrorDeclined = true;
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    private static bool RunIteratePrefix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result)
    {
        // HookSkip asked for a filtered walk (BaseLib's hand size): no need for every listener. A child combat walk with other
        // mods' patches: what their postfixes return would be kept inside the run's list, so the game's call then (it calls the
        // combat walk, served from the combat's own list, where they run live).
        if (!Serving || _materializingRun || FastListeners.RunFilterPending || (childCombatState != null && _combatAugmented)
            || (_runAugmented && (_mirrorDeclined || !FastListeners.MirrorsUnpatched)))
        {
            return true;
        }
        try
        {
            __result = _runAugmented ? new Replay(GetRunListeners(__instance, childCombatState)) : GetRunListeners(__instance, childCombatState);
#if BENCH
            Served++;
#endif
            return false;
        }
        catch (UnpatchedWalkMissing)
        {
            // The game's call: what an augmented scope relies on no longer holds (see UnpatchedWalkMissing), for the rest of it.
            _mirrorDeclined = true;
            return true;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    /// <summary>
    /// A replayed list behind an enumerable only (no <c>IList</c>/<c>ICollection</c> to test, cast or change), handed to the game's
    /// call while other mods' postfixes run on it: they see what the game's iterator offers, never the shared list itself.
    /// </summary>
    private sealed class Replay : IEnumerable<AbstractModel>
    {
        private readonly ReadOnlyCollection<AbstractModel> _listeners;

        public Replay(ReadOnlyCollection<AbstractModel> listeners) => _listeners = listeners;

        public IEnumerator<AbstractModel> GetEnumerator() => _listeners.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

#if BENCH
    /// <summary>Outermost scopes caching with other mods' patches on the walks let stand (the effects check of the tolerance).</summary>
    public static long AugmentedScopes { get; private set; }
#endif

    private static void Fail(Exception e)
    {
        SnapshotEnabled = false;
        RefreshEnabled = false;
        PatchSafety.Fault(Name);
        _cache?.Clear();
        _kept?.Clear();
        _augmented?.Clear();
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
        // Snapshots can no longer reuse walk outputs here: the co-op join list says so (SnapshotWalksTag).
        CoopModeTag.Refresh();
    }
}
