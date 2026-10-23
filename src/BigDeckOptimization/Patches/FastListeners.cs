using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Every hook the game fires (damage received, card moved, HP lost...) walks <c>IterateHookListeners</c>: it builds a
/// list of every listener (each card of every pile, its enchantment, relics, powers...) then yields those still in the
/// state. With 8,400 cards that is 17,000-34,000 entries, dozens of times per action, outside the scopes where
/// <see cref="ListenerCache"/> replays a materialized list. The list grows from a tiny capacity (copies) and, past
/// ~10,000 entries, lands on the large object heap: a gen2 garbage collection every few actions.
///
/// Same code, same laziness: the list is built on the first MoveNext in the same order, each entry is checked when it
/// is yielded (the state may change in between, as vanilla allows), then mod subscribers are walked lazily. Only the
/// list is reused (pooled, emptied, per thread and per nesting level) and the per-entry type test checks cards first.
/// The yielded sequence is the same object for object; <c>bdperf listeners</c> compares it with vanilla.
/// </summary>
public static class FastListeners
{
    public const string Name = "FastListeners";

    /// <summary>v0.107.1 code mirrored: both iterators (their state machines) and both Contains.</summary>
    private const string CombatIterateFingerprint = "297497BEE6AC98A8";
    private const string RunIterateFingerprint = "787F879A9B4B17D4";
    private const string CombatContainsFingerprint = "210F9D7CC594C500";
    private const string RunContainsFingerprint = "78124E08E9E87556";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    private static System.Reflection.MethodInfo? _combatIterate;
    private static System.Reflection.MethodInfo? _runIterate;
    private static AccessTools.FieldRef<CombatState, List<Creature>>? _allies;
    private static AccessTools.FieldRef<CombatState, List<Creature>>? _enemies;

    [ThreadStatic] private static Stack<List<AbstractModel>>? _pool;

    /// <summary>Set by <see cref="HookSkip"/> just before a hook calls RunState.IterateHookListeners; taken by that call.</summary>
    [ThreadStatic] internal static PileFilter? PendingRunFilter;

    /// <summary>
    /// Which pile entries (cards, their enchantment/affliction, deck cards) a hook needs: those whose type reacts to it.
    /// Every other listener (powers, relics, potions, orbs, monsters, modifiers, mod subscribers) is always kept.
    /// </summary>
    public sealed class PileFilter
    {
        private readonly Func<Type, bool> _reacts;
        private readonly ConcurrentDictionary<Type, bool> _byType = new();
        private readonly bool _keepSubscribed;

        /// <summary>Last lookup on this thread (no allocation: cards often come in runs of one type).</summary>
        [ThreadStatic] private static PileFilter? _lastFilter;
        [ThreadStatic] private static Type? _lastType;
        [ThreadStatic] private static bool _lastReacts;

        /// <param name="reacts">Whether a listener type reacts to the hook (overrides its method, implements its interface).</param>
        /// <param name="keepSubscribed">The hook calls InvokeExecutionFinished on every listener: keep those someone listens to.</param>
        public PileFilter(Func<Type, bool> reacts, bool keepSubscribed)
        {
            _reacts = reacts;
            _keepSubscribed = keepSubscribed;
        }

        /// <summary>
        /// The pile entries this filter keeps depend only on what the piles hold (and in which order) and on each
        /// card's enchantment/affliction: all of it changes through the methods that bump <see cref="PileVersion"/>.
        /// Filters keeping ExecutionFinished subscribers also depend on who subscribes: never cached, but they walk the
        /// shared <see cref="PileSnapshot"/> and check subscribers live.
        /// </summary>
        private static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThread;

        /// <summary>Snapshots and segments are usable: main thread, and not inside a shuffle (see <see cref="_shuffling"/>).</summary>
        private static bool CanCache => OnMainThread && _shuffling == 0;

        private sealed class Segment
        {
            public long Version = -1;
            public readonly List<AbstractModel> Items = new();
        }

        private readonly ConditionalWeakTable<object, Segment> _segments = new();

        /// <summary>Whether each <see cref="PileSnapshot"/> type id reacts: 0 unknown, 1 yes, -1 no (main thread only).</summary>
        private sbyte[] _reactsById = Array.Empty<sbyte>();

        /// <summary>The kept entries of these piles, in walk order (card, affliction, enchantment), rebuilt after any pile change.</summary>
        internal void AddPiles(List<AbstractModel> list, object key, IReadOnlyList<CardPile> piles)
        {
            if (!CanCache)
            {
                AddPilesNow(list, piles);
                return;
            }
            if (_keepSubscribed)
            {
#if BENCH
                UncachedWalks++;
                int start = list.Count;
#endif
                AddKept(list, PileSnapshot.Of(key, piles, withAfflictions: true));
#if BENCH
                VerifyUncached(list, start, piles);
#endif
                return;
            }
            Segment segment = _segments.GetValue(key, _ => new Segment());
            if (segment.Version != PileVersion)
            {
#if BENCH
                SegmentRebuilds++;
                if (_lastRebuildVersion != PileVersion)
                {
                    _lastRebuildVersion = PileVersion;
                    RebuiltVersions++;
                }
#endif
                segment.Items.Clear();
                AddKept(segment.Items, PileSnapshot.Of(key, piles, withAfflictions: true));
                segment.Version = PileVersion;
            }
#if BENCH
            else if (++SegmentHits % CacheVerifyEvery == 0)
            {
                var fresh = new List<AbstractModel>();
                AddPilesNow(fresh, piles);
                if (!fresh.SequenceEqual(segment.Items, ReferenceEqualityComparer.Instance))
                {
                    CacheMismatches++;
                    Log.Error($"[{ModEntry.Id}] {Name}: cached pile entries differ from a fresh walk ({segment.Items.Count} vs {fresh.Count})");
                    segment.Items.Clear();
                    segment.Items.AddRange(fresh);
                }
            }
#endif
            list.AddRange(segment.Items);
        }

#if BENCH
        /// <summary>One uncached walk in <see cref="CacheVerifyEvery"/> is compared with the per-entry walk.</summary>
        private void VerifyUncached(List<AbstractModel> list, int start, IReadOnlyList<CardPile> piles)
        {
            if (UncachedWalks % CacheVerifyEvery != 0)
            {
                return;
            }
            var fresh = new List<AbstractModel>();
            AddPilesNow(fresh, piles);
            if (!fresh.SequenceEqual(list.Skip(start), ReferenceEqualityComparer.Instance))
            {
                CacheMismatches++;
                Log.Error($"[{ModEntry.Id}] {Name}: snapshot walk differs from a fresh walk ({list.Count - start} vs {fresh.Count})");
            }
        }
#endif

        /// <summary>The snapshot's entries this filter keeps, in order: <see cref="Keeps"/> without a type lookup per entry.</summary>
        private void AddKept(List<AbstractModel> list, PileSnapshot snapshot)
        {
            AbstractModel[] entries = snapshot.Entries;
            int[] typeIds = snapshot.TypeIds;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (Reacts(typeIds[i]) || (_keepSubscribed && _executionFinished!(entries[i]) != null))
                {
                    list.Add(entries[i]);
                }
            }
        }

        private bool Reacts(int typeId)
        {
            if (typeId < _reactsById.Length && _reactsById[typeId] != 0)
            {
                return _reactsById[typeId] > 0;
            }
            if (typeId >= _reactsById.Length)
            {
                Array.Resize(ref _reactsById, Math.Max(typeId + 1, _reactsById.Length * 2));
            }
            bool reacts = _byType.GetOrAdd(PileSnapshot.TypeOf(typeId), _reacts);
            _reactsById[typeId] = reacts ? (sbyte)1 : (sbyte)-1;
            return reacts;
        }

        private void AddPilesNow(List<AbstractModel> list, IReadOnlyList<CardPile> piles)
        {
            for (int l = 0; l < piles.Count; l++)
            {
                IReadOnlyList<CardModel> cards = piles[l].Cards;
                for (int m = 0; m < cards.Count; m++)
                {
                    CardModel card = cards[m];
                    if (Keeps(card))
                    {
                        list.Add(card);
                    }
                    if (card.Affliction != null && Keeps(card.Affliction))
                    {
                        list.Add(card.Affliction);
                    }
                    if (card.Enchantment != null && Keeps(card.Enchantment))
                    {
                        list.Add(card.Enchantment);
                    }
                }
            }
        }

        /// <summary>The run walk's deck entries (card, enchantment).</summary>
        internal void AddDeck(List<AbstractModel> list, CardPile deck)
        {
            if (!CanCache)
            {
                AddDeckNow(list, deck);
                return;
            }
            if (_keepSubscribed)
            {
                AddKept(list, DeckSnapshot(deck));
                return;
            }
            Segment segment = _segments.GetValue(deck, _ => new Segment());
            if (segment.Version != PileVersion)
            {
                segment.Items.Clear();
                AddKept(segment.Items, DeckSnapshot(deck));
                segment.Version = PileVersion;
            }
            list.AddRange(segment.Items);
        }

        private static readonly CardPile[] _deckPiles = new CardPile[1];

        private static PileSnapshot DeckSnapshot(CardPile deck)
        {
            _deckPiles[0] = deck;
            try
            {
                return PileSnapshot.Of(deck, _deckPiles, withAfflictions: false);
            }
            finally
            {
                _deckPiles[0] = null!; // Don't keep the deck alive after the run.
            }
        }

        private void AddDeckNow(List<AbstractModel> list, CardPile deck)
        {
            foreach (CardModel card in deck.Cards)
            {
                if (Keeps(card))
                {
                    list.Add(card);
                }
                if (card.Enchantment != null && Keeps(card.Enchantment))
                {
                    list.Add(card.Enchantment);
                }
            }
        }

        public bool Keeps(AbstractModel model)
        {
            Type type = model.GetType();
            bool reacts;
            if (ReferenceEquals(_lastFilter, this) && _lastType == type)
            {
                reacts = _lastReacts;
            }
            else
            {
                reacts = _byType.GetOrAdd(type, _reacts);
                _lastFilter = this;
                _lastType = type;
                _lastReacts = reacts;
            }
            return reacts || (_keepSubscribed && _executionFinished!(model) != null);
        }

        /// <summary>The base methods whose no-op defaults the dropped listeners rely on (null for interface filters).</summary>
        public MethodInfo[]? BaseMethods { get; private init; }

        /// <summary>Types overriding any of these <see cref="AbstractModel"/> virtuals.</summary>
        public static PileFilter Overriding(bool keepSubscribed, params string[] methods)
        {
            MethodInfo[] bases = methods.Select(m => AccessTools.Method(typeof(AbstractModel), m)
                ?? throw new MissingMethodException(nameof(AbstractModel), m)).ToArray();
            return new PileFilter(type => bases.Any(b => Overrides(type, b)), keepSubscribed) { BaseMethods = bases };
        }

        private static readonly ConcurrentDictionary<Type, MethodInfo[]> _methodsByType = new();

        private static bool Overrides(Type type, MethodInfo baseMethod)
        {
            try
            {
                // Reflection collapses overridden virtuals: only the most-derived implementation is returned. Shared by
                // every filter (~150) asking about the same type.
                MethodInfo[] methods = _methodsByType.GetOrAdd(type, t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                foreach (MethodInfo m in methods)
                {
                    if (m.Name == baseMethod.Name && m.GetBaseDefinition() == baseMethod)
                    {
                        return m.DeclaringType != typeof(AbstractModel);
                    }
                }
                return true;
            }
            catch
            {
                return true; // When in doubt, keep it.
            }
        }
    }

    /// <summary>
    /// Every pile entry of a player's combat piles (card, affliction, enchantment) or deck (card, enchantment), in walk
    /// order, with a small id per type: built once per <see cref="PileVersion"/> and shared by every filter, which then
    /// tests an array per type id instead of looking each entry's type up. Main thread only.
    /// </summary>
    private sealed class PileSnapshot
    {
        public long Version = -1;
        public AbstractModel[] Entries = new AbstractModel[1024];
        public int[] TypeIds = new int[1024];
        public int Count;

        private static readonly ConditionalWeakTable<object, PileSnapshot> _snapshots = new();
        private static readonly Dictionary<Type, int> _ids = new();
        private static readonly List<Type> _types = new();

        public static Type TypeOf(int id) => _types[id];

        public static PileSnapshot Of(object key, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            PileSnapshot snapshot = _snapshots.GetValue(key, _ => new PileSnapshot());
            if (snapshot.Version == PileVersion)
            {
                return snapshot;
            }
            int previous = snapshot.Count;
            snapshot.Count = 0;
            for (int l = 0; l < piles.Count; l++)
            {
                IReadOnlyList<CardModel> cards = piles[l].Cards;
                for (int m = 0; m < cards.Count; m++)
                {
                    CardModel card = cards[m];
                    snapshot.Add(card);
                    if (withAfflictions && card.Affliction != null)
                    {
                        snapshot.Add(card.Affliction);
                    }
                    if (card.Enchantment != null)
                    {
                        snapshot.Add(card.Enchantment);
                    }
                }
            }
            if (previous > snapshot.Count)
            {
                // Don't keep cards that left the piles alive.
                Array.Clear(snapshot.Entries, snapshot.Count, previous - snapshot.Count);
            }
            snapshot.Version = PileVersion;
            return snapshot;
        }

        private void Add(AbstractModel model)
        {
            if (Count == Entries.Length)
            {
                Array.Resize(ref Entries, Count * 2);
                Array.Resize(ref TypeIds, Count * 2);
            }
            Type type = model.GetType();
            if (!_ids.TryGetValue(type, out int id))
            {
                id = _types.Count;
                _ids[type] = id;
                _types.Add(type);
            }
            Entries[Count] = model;
            TypeIds[Count] = id;
            Count++;
        }
    }

    private static AccessTools.FieldRef<AbstractModel, Action<AbstractModel>?>? _executionFinished;

    private static int _mainThread;

    /// <summary>Bumped before and after every change to a pile's cards or order, and every enchantment/affliction change.</summary>
    internal static long PileVersion;

#if BENCH
    /// <summary>One cache hit in this many is compared with a fresh walk (Debug builds).</summary>
    internal static int CacheVerifyEvery = 256;
    public static long CacheMismatches { get; private set; }
    /// <summary>Combat pile segments: rebuilt, served from cache, walked uncached (filters keeping subscribers); and
    /// how many distinct pile versions needed a rebuild.</summary>
    public static long SegmentRebuilds, SegmentHits, UncachedWalks, RebuiltVersions;
    private static long _lastRebuildVersion = -1;
#endif

    /// <summary>v0.107.1 code the pile cache relies on: every CardPile method touching its list (PileIndex checks the same).</summary>
    private static readonly (string Method, string Fingerprint)[] PileMethods =
    {
        ("AddInternal", "0A80C8EF3B697A4E"),
        ("RemoveInternal", "7CF07A4BBD094E41"),
        ("MoveToBottomInternal", "8A6B91EFCD757B3B"),
        ("MoveToTopInternal", "0C41AC449A8E2DBB"),
        ("RandomizeOrderInternal", "FF906423481AC743"),
        ("Clear", "A2B6A279CA18ABC1"),
    };

    private static void BumpPileVersion() => PileVersion++;

    /// <summary>
    /// Inside <c>CardPile.RandomizeOrderInternal</c>: the game hands the pile's own list to <c>ModifyShuffleOrder</c>
    /// listeners, which reorder it in place without going through a bumping method, so walks there read the piles
    /// directly (main thread).
    /// </summary>
    private static int _shuffling;

    private static void EnterShuffle()
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThread)
        {
            _shuffling++;
        }
    }

    private static Exception? ExitShuffle(Exception? __exception)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThread)
        {
            _shuffling--;
        }
        return __exception;
    }

    private static Exception? BumpPileVersionFinally(Exception? __exception)
    {
        PileVersion++;
        return __exception;
    }

    /// <summary>v0.107.1 <c>ModHelper.IterateAllCombatStateSubscribers</c> (state machine), mirrored by <see cref="Subscribers"/>.</summary>
    private const string SubscribersFingerprint = "99CA5CFD9D21F34D";

    /// <summary>
    /// BaseLib 3.4.7's "BaseLibCardModifiers" subscriber: for every card in every combat pile, the card's CardModifier
    /// list (a table lookup per card, ~8% of an action at 8,400 cards). Only instances of a concrete CardModifier
    /// subclass can be in those lists, so with no such type loaded it can only return an empty list.
    /// </summary>
    private const string CardModifiersId = "BaseLibCardModifiers";
    private const string CardModifiersFingerprint = "6B671EFF3A900922";
    private const string CardModifierType = "BaseLib.Abstracts.CardModifier";

    private static System.Collections.IList? _combatSubscribers;
    /// <summary>(id, delegate) of each subscriber, rebuilt when the list grows (mods only ever add to it).</summary>
    private static (string Id, CombatHookSubscriptionDelegate Del)[] _subscriberSnapshot = Array.Empty<(string, CombatHookSubscriptionDelegate)>();
    private static FieldInfo? _subscriberId, _subscriberDelegate;
    private static readonly ConcurrentDictionary<MethodInfo, bool> _emptyModifierLists = new();
    /// <summary>Null = unknown (recomputed when an assembly loads, which may add CardModifier types).</summary>
    private static bool? _noCardModifierTypes;
    public static long SkippedModifierWalks { get; private set; }

    public static void Apply(Harmony harmony)
    {
        var combatIterate = AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners))
            ?? throw new MissingMethodException("CombatState.IterateHookListeners");
        var runIterate = AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) })
            ?? throw new MissingMethodException("RunState.IterateHookListeners");
        PatchSafety.RequireFingerprint(AccessTools.EnumeratorMoveNext(combatIterate), CombatIterateFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.EnumeratorMoveNext(runIterate), RunIterateFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(CombatState), "Contains"), CombatContainsFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(RunState), "Contains"), RunContainsFingerprint);
        _combatIterate = combatIterate;
        _runIterate = runIterate;
        _allies = AccessTools.FieldRefAccess<CombatState, List<Creature>>("_allies");
        _enemies = AccessTools.FieldRefAccess<CombatState, List<Creature>>("_enemies");
        _executionFinished = AccessTools.FieldRefAccess<AbstractModel, Action<AbstractModel>?>("ExecutionFinished");
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        // Pile cache invalidation: before (a walk during the change sees a new version) and after (even on exceptions).
        var bumpBefore = new HarmonyMethod(typeof(FastListeners), nameof(BumpPileVersion));
        var bumpAfter = new HarmonyMethod(typeof(FastListeners), nameof(BumpPileVersionFinally));
        foreach ((string method, string fingerprint) in PileMethods)
        {
            MethodInfo target = AccessTools.Method(typeof(CardPile), method) ?? throw new MissingMethodException(nameof(CardPile), method);
            PatchSafety.RequireFingerprint(target, fingerprint);
            harmony.Patch(target, prefix: bumpBefore, finalizer: bumpAfter);
        }
        harmony.Patch(AccessTools.Method(typeof(CardPile), "RandomizeOrderInternal"),
            prefix: new HarmonyMethod(typeof(FastListeners), nameof(EnterShuffle)), finalizer: new HarmonyMethod(typeof(FastListeners), nameof(ExitShuffle)));
        foreach (string property in new[] { nameof(CardModel.Enchantment), nameof(CardModel.Affliction) })
        {
            harmony.Patch(AccessTools.PropertySetter(typeof(CardModel), property) ?? throw new MissingMethodException(nameof(CardModel), property),
                prefix: bumpBefore, finalizer: bumpAfter);
        }
        try
        {
            PatchSafety.RequireFingerprint(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.IterateAllCombatStateSubscribers))), SubscribersFingerprint);
            _combatSubscribers = (System.Collections.IList)AccessTools.Field(typeof(ModHelper), "_combatHookSubscribers").GetValue(null)!;
            Type subscriber = AccessTools.Inner(typeof(ModHelper), "ModCombatHookSubscriber");
            _subscriberId = AccessTools.Field(subscriber, "id");
            _subscriberDelegate = AccessTools.Field(subscriber, "del");
            // A mod loading later may define CardModifier types: check just that assembly (dynamic ones, e.g. Harmony's,
            // and assemblies not referencing BaseLib can't).
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                if (_noCardModifierTypes == true && MayDefineCardModifiers(args.LoadedAssembly) && DefinesCardModifier(args.LoadedAssembly))
                {
                    _noCardModifierTypes = false;
                }
            };
        }
        catch (Exception e)
        {
            _combatSubscribers = null; // ModHelper's own walk, as is.
            Log.Info($"[{ModEntry.Id}] {Name}: mod subscriber walk left vanilla: {e.Message}");
        }
        // After ListenerCache: inside its scopes it answers from its cache and this does nothing.
        harmony.Patch(combatIterate, prefix: new HarmonyMethod(typeof(FastListeners), nameof(CombatPrefix)) { priority = Priority.Last });
        harmony.Patch(runIterate, prefix: new HarmonyMethod(typeof(FastListeners), nameof(RunPrefix)) { priority = Priority.Last });
        Applied = true;
    }

    private static bool CombatPrefix(CombatState __instance, ref IEnumerable<AbstractModel> __result, bool __runOriginal)
    {
        if (!__runOriginal || !Enabled || PatchSafety.IsPatchedByOthers(_combatIterate))
        {
            return __runOriginal;
        }
        __result = Combat(__instance, null);
        return false;
    }

    private static bool RunPrefix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result, bool __runOriginal)
    {
        PileFilter? filter = PendingRunFilter;
        PendingRunFilter = null;
        if (!__runOriginal || PatchSafety.IsPatchedByOthers(_runIterate))
        {
            return __runOriginal;
        }
        if (filter != null)
        {
            __result = Run(__instance, childCombatState, filter);
            return false;
        }
        if (!Enabled)
        {
            return true;
        }
        __result = Run(__instance, childCombatState, null);
        return false;
    }

    /// <summary>Combat listeners as vanilla yields them, minus the pile entries the filter drops.</summary>
    internal static IEnumerable<AbstractModel> Filtered(CombatState state, PileFilter filter) => Combat(state, filter);

    /// <summary>Run (and child combat) listeners as vanilla yields them, minus the pile entries the filter drops.</summary>
    internal static IEnumerable<AbstractModel> Filtered(RunState run, ICombatState? child, PileFilter filter) => Run(run, child, filter);

    private static List<AbstractModel> Rent()
    {
        _pool ??= new Stack<List<AbstractModel>>();
        return _pool.Count > 0 ? _pool.Pop() : new List<AbstractModel>(1024);
    }

    private static void Return(List<AbstractModel> list)
    {
        list.Clear();
        if (_pool!.Count < 8)
        {
            _pool.Push(list);
        }
    }

    /// <summary><c>CombatState.IterateHookListeners</c>, pooled list.</summary>
    private static IEnumerable<AbstractModel> Combat(CombatState state, PileFilter? filter)
    {
        List<AbstractModel> list = Rent();
        try
        {
            List<Creature> allies = _allies!(state);
            List<Creature> enemies = _enemies!(state);
            for (int i = 0; i < allies.Count + enemies.Count; i++)
            {
                Creature creature = i < allies.Count ? allies[i] : enemies[i - allies.Count];
                list.AddRange(creature.Powers);
                Player? player = creature.Player;
                if (player == null)
                {
                    list.Add(creature.Monster!);
                    continue;
                }
                if (!player.IsActiveForHooks)
                {
                    continue;
                }
                IReadOnlyList<RelicModel> relics = player.Relics;
                for (int j = 0; j < relics.Count; j++)
                {
                    if (!relics[j].IsMelted)
                    {
                        list.Add(relics[j]);
                    }
                }
                IReadOnlyList<PotionModel?> potionSlots = player.PotionSlots;
                for (int k = 0; k < potionSlots.Count; k++)
                {
                    if (potionSlots[k] != null)
                    {
                        list.Add(potionSlots[k]!);
                    }
                }
                if (player.PlayerCombatState == null)
                {
                    continue;
                }
                list.AddRange(player.PlayerCombatState.OrbQueue.Orbs);
                IReadOnlyList<CardPile> allPiles = player.PlayerCombatState.AllPiles;
                if (filter != null)
                {
                    filter.AddPiles(list, player.PlayerCombatState, allPiles);
                    continue;
                }
                for (int l = 0; l < allPiles.Count; l++)
                {
                    IReadOnlyList<CardModel> cards = allPiles[l].Cards;
                    for (int m = 0; m < cards.Count; m++)
                    {
                        CardModel card = cards[m];
                        list.Add(card);
                        if (card.Affliction != null)
                        {
                            list.Add(card.Affliction);
                        }
                        if (card.Enchantment != null)
                        {
                            list.Add(card.Enchantment);
                        }
                    }
                }
            }
            for (int n = 0; n < state.Modifiers.Count; n++)
            {
                list.Add(state.Modifiers[n]);
            }
            for (int n = 0; n < state.BadgeModels.Count; n++)
            {
                list.Add(state.BadgeModels[n]);
            }
            if (state.MultiplayerScalingModel != null)
            {
                list.Add(state.MultiplayerScalingModel);
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (CombatContains(list[i]))
                {
                    yield return list[i];
                }
            }
        }
        finally
        {
            Return(list);
        }
        foreach (AbstractModel item in Subscribers(state))
        {
            yield return item;
        }
    }

    /// <summary>
    /// <c>ModHelper.IterateAllCombatStateSubscribers</c>: each subscriber's models, non-null, in order; BaseLib's card
    /// modifier subscriber is skipped while it can only return an empty list (its code checked, no CardModifier type).
    /// </summary>
    private static IEnumerable<AbstractModel> Subscribers(CombatState state)
    {
        if (_combatSubscribers == null)
        {
            foreach (AbstractModel item in ModHelper.IterateAllCombatStateSubscribers(state))
            {
                yield return item;
            }
            yield break;
        }
        var snapshot = _subscriberSnapshot;
        if (snapshot.Length != _combatSubscribers.Count)
        {
            snapshot = new (string, CombatHookSubscriptionDelegate)[_combatSubscribers.Count];
            for (int i = 0; i < snapshot.Length; i++)
            {
                object subscriber = _combatSubscribers[i]!;
                snapshot[i] = ((string)_subscriberId!.GetValue(subscriber)!, (CombatHookSubscriptionDelegate)_subscriberDelegate!.GetValue(subscriber)!);
            }
            _subscriberSnapshot = snapshot;
        }
        foreach ((string id, CombatHookSubscriptionDelegate del) in snapshot)
        {
            if (CardModifiersEmpty(id, del))
            {
                SkippedModifierWalks++;
                continue;
            }
            IEnumerable<AbstractModel>? models = del(state);
            if (models == null)
            {
                continue;
            }
            foreach (AbstractModel item in models)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }
    }

    private static bool CardModifiersEmpty(string id, Delegate del)
    {
        if (id != CardModifiersId || !Enabled)
        {
            return false;
        }
        if (!_emptyModifierLists.GetOrAdd(del.Method, m => MethodFingerprint.Of(m) == CardModifiersFingerprint))
        {
            return false;
        }
        _noCardModifierTypes ??= !AnyCardModifierType();
        return _noCardModifierTypes.Value;
    }

    /// <summary>Whether any loaded assembly defines a concrete CardModifier subclass (or the base type is missing).</summary>
    private static bool AnyCardModifierType() =>
        AccessTools.TypeByName(CardModifierType) == null
        || AppDomain.CurrentDomain.GetAssemblies().Any(a => MayDefineCardModifiers(a) && DefinesCardModifier(a));

    /// <summary>Only BaseLib and assemblies referencing it can derive from its CardModifier.</summary>
    private static bool MayDefineCardModifiers(Assembly assembly) =>
        !assembly.IsDynamic && (assembly.GetName().Name == "BaseLib" || assembly.GetReferencedAssemblies().Any(r => r.Name == "BaseLib"));

    private static bool DefinesCardModifier(Assembly assembly)
    {
        Type? baseType = AccessTools.TypeByName(CardModifierType);
        if (baseType == null)
        {
            return true;
        }
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).ToArray()!;
        }
        catch
        {
            return true; // Can't tell: don't skip.
        }
        return types.Any(t => !t.IsAbstract && baseType.IsAssignableFrom(t));
    }

    /// <summary><c>RunState.IterateHookListeners</c>, pooled list.</summary>
    private static IEnumerable<AbstractModel> Run(RunState run, ICombatState? childCombatState, PileFilter? filter)
    {
        List<AbstractModel> list = Rent();
        try
        {
            foreach (Player player in run.Players)
            {
                if (!player.IsActiveForHooks)
                {
                    continue;
                }
                if (filter != null)
                {
                    filter.AddDeck(list, player.Deck);
                    continue;
                }
                foreach (CardModel card in player.Deck.Cards)
                {
                    list.Add(card);
                    if (card.Enchantment != null)
                    {
                        list.Add(card.Enchantment);
                    }
                }
            }
            if (childCombatState == null)
            {
                foreach (Player player in run.Players)
                {
                    if (player.IsActiveForHooks)
                    {
                        foreach (RelicModel relic in player.Relics)
                        {
                            if (!relic.IsMelted)
                            {
                                list.Add(relic);
                            }
                        }
                        list.AddRange(player.Potions);
                    }
                }
                list.AddRange(run.Modifiers);
                list.AddRange(run.BadgeModels);
                list.Add(run.MultiplayerScalingModel);
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (RunContains(list[i]))
                {
                    yield return list[i];
                }
            }
        }
        finally
        {
            Return(list);
        }
        foreach (AbstractModel item in ModHelper.IterateAllRunStateSubscribers(run))
        {
            yield return item;
        }
        if (childCombatState == null)
        {
            yield break;
        }
        // Filtered: the combat walk with the same filter. Otherwise through the interface, as vanilla.
        IEnumerable<AbstractModel> child = filter != null && childCombatState is CombatState combat
            ? Combat(combat, filter)
            : childCombatState.IterateHookListeners();
        foreach (AbstractModel item in child)
        {
            yield return item;
        }
    }

    /// <summary><c>CombatState.Contains</c>: the model types are disjoint, so testing cards first gives the same answer.</summary>
    private static bool CombatContains(AbstractModel model) => model switch
    {
        CardModel card => !card.HasBeenRemovedFromState && card.Owner.IsActiveForHooks,
        EnchantmentModel enchantment => enchantment.HasCard && !enchantment.Card.HasBeenRemovedFromState && enchantment.Card.Owner.IsActiveForHooks,
        AfflictionModel affliction => affliction.HasCard && !affliction.Card.HasBeenRemovedFromState && affliction.Card.Owner.IsActiveForHooks,
        PowerModel power => power.Owner.CombatState != null && (power.Owner.Player?.IsActiveForHooks ?? true),
        RelicModel relic => !relic.HasBeenRemovedFromState && relic.Owner.IsActiveForHooks,
        PotionModel potion => !potion.HasBeenRemovedFromState && potion.Owner.IsActiveForHooks,
        OrbModel orb => !orb.HasBeenRemovedFromState && orb.Owner.IsActiveForHooks,
        MonsterModel monster => monster.Creature.CombatState != null,
        AchievementModel or BadgeModel or ModifierModel or MultiplayerScalingModel => true,
        _ => throw new ArgumentOutOfRangeException("model", model, $"Invalid model type {model.GetType()} ({model})"),
    };

    /// <summary><c>RunState.Contains</c>, cards first.</summary>
    private static bool RunContains(AbstractModel model) => model switch
    {
        CardModel card => !card.HasBeenRemovedFromState && card.Owner.IsActiveForHooks,
        EnchantmentModel enchantment => enchantment.HasCard && !enchantment.Card.HasBeenRemovedFromState && enchantment.Card.Owner.IsActiveForHooks,
        RelicModel relic => !relic.HasBeenRemovedFromState && relic.Owner.IsActiveForHooks,
        PotionModel potion => !potion.HasBeenRemovedFromState && potion.Owner.IsActiveForHooks,
        AchievementModel or BadgeModel or ModifierModel or MultiplayerScalingModel => true,
        _ => throw new ArgumentOutOfRangeException("model", model, $"Invalid model type {model.GetType()} ({model})"),
    };

    public static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
