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
///
/// Three layers, each relying on its own code (a change only takes that layer away):
/// <list type="bullet">
/// <item>the walks (<see cref="CombatMirrored"/>, <see cref="RunMirrored"/>), mirrors of the game's iterators: without them
/// the game's own walk runs, and a filter drops its entries as they come (<see cref="KeptOnly"/>);</item>
/// <item>the cached pile entries (<see cref="_tracking"/>), which rely on no pile list changing outside the methods bumping
/// <see cref="PileVersion"/>, whatever these do: without them each filter reads the piles at each walk;</item>
/// <item>moves applied to the cached entries (<see cref="_scopedMoves"/>, <see cref="_shiftMoves"/>), which rely on what those
/// methods do: without them any move counts as any other change.</item>
/// </list>
/// </summary>
public static class FastListeners
{
    public const string Name = "FastListeners";

    /// <summary>v0.107.1 code mirrored: both iterators (their state machines) and both Contains.</summary>
    private const string CombatIterateFingerprint = "58F9C719E3433DE5";
    private const string RunIterateFingerprint = "3E3597C01EA54DE8";
    private const string CombatContainsFingerprint = "210F9D7CC594C500";
    private const string RunContainsFingerprint = "78124E08E9E87556";

    public static bool Enabled { get; set; } = true;

    /// <summary>Filtered walks are available, through the mirrors or the game's own walks.</summary>
    public static bool Applied { get; private set; }

    /// <summary>The game's walks run as these mirrors (their code is the one mirrored).</summary>
    public static bool CombatMirrored { get; private set; }
    public static bool RunMirrored { get; private set; }

    private static System.Reflection.MethodInfo? _combatIterate;
    private static System.Reflection.MethodInfo? _runIterate;
    /// <summary>What the mirrors also reproduce: each walk's state machine, its <c>Contains</c>, the mod subscriber walk and its state machine.</summary>
    private static MethodBase? _combatBody, _combatContains, _runBody, _runContains, _subscribersMethod, _subscribersBody;
    private static AccessTools.FieldRef<CombatState, List<Creature>>? _allies;
    private static AccessTools.FieldRef<CombatState, List<Creature>>? _enemies;

    [ThreadStatic] private static Stack<List<AbstractModel>>? _pool;

    /// <summary>Set by <see cref="HookSkip"/> just before a hook calls RunState.IterateHookListeners; taken by that call.</summary>
    [ThreadStatic] internal static PileFilter? PendingRunFilter;

    /// <summary>Filtered walks also leave out what isn't in a pile (relics, potions, orbs, powers...) when its type doesn't react.</summary>
    public static bool KindSkipEnabled { get; set; } = true;
#if BENCH
    /// <summary>Kinds of listener left out of a walk as a whole, and listeners outside piles left out one by one (the effects check).</summary>
    public static long KindsSkipped { get; internal set; }
    public static long OthersDropped { get; internal set; }
#endif

    /// <summary>
    /// The types of each kind of listener outside piles, from every loaded assembly: a kind none of whose types reacts to a hook
    /// needs no look at its objects. Scanned once, and again when an assembly loads later.
    /// </summary>
    private static class ListenerKinds
    {
        internal const int Relics = 0, Potions = 1, Orbs = 2, Count = 3;
        private static readonly Type[] _bases = { typeof(RelicModel), typeof(PotionModel), typeof(OrbModel) };
        private static List<Type>[]? _types;
        private static int _stamp;
        /// <summary>Set when an assembly loads: types are then looked for again. Asking for the assembly list at each walk allocated it each time.</summary>
        private static volatile bool _stale = true;
        private static bool _listening;

        /// <summary>Changes when types may have been added: answers per filter are then asked again.</summary>
        internal static int Stamp
        {
            get
            {
                Refresh();
                return _stamp;
            }
        }

        private static void Refresh()
        {
            if (!_stale)
            {
                return;
            }
            if (!_listening)
            {
                _listening = true;
                // Not for the dynamic ones Harmony and others make: they hold no model types.
                AppDomain.CurrentDomain.AssemblyLoad += (_, args) => _stale |= !args.LoadedAssembly.IsDynamic;
            }
            _stale = false;
            var found = new List<Type>[Count];
            for (int i = 0; i < Count; i++)
            {
                found[i] = new List<Type>();
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || assembly.FullName is not { } name || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.Ordinal) || name.StartsWith("Godot", StringComparison.Ordinal))
                {
                    continue;
                }
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // Types it couldn't load can't be instantiated either.
                    types = e.Types;
                }
                catch
                {
                    continue;
                }
                foreach (Type? type in types)
                {
                    if (type == null || type.IsAbstract)
                    {
                        continue;
                    }
                    for (int i = 0; i < Count; i++)
                    {
                        if (type.IsSubclassOf(_bases[i]))
                        {
                            found[i].Add(type);
                        }
                    }
                }
            }
            _types = found;
            _stamp++;
        }

        internal static bool Any(int kind, PileFilter filter)
        {
            Refresh();
            foreach (Type type in _types![kind])
            {
                if (filter.TypeReacts(type))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Adds these listeners of one kind, without those whose type doesn't react: only reads their type, then calls nothing on them.</summary>
    private static void AddReacting<T>(List<AbstractModel> list, IReadOnlyList<T> models, PileFilter? filter) where T : AbstractModel
    {
        for (int i = 0; i < models.Count; i++)
        {
            T model = models[i];
            if (filter == null || !KindSkipEnabled || filter.Keeps(model))
            {
                list.Add(model);
            }
#if BENCH
            else
            {
                OthersDropped++;
            }
#endif
        }
    }

    /// <summary>
    /// Which listeners a hook needs: those whose type reacts to it. Pile entries (cards, their enchantment/affliction, deck cards) come
    /// from cached segments; the others (powers, relics, potions, orbs, monsters, modifiers) are looked at in the walk, or not at all when
    /// no type of their kind reacts. Mod subscribers are always kept. A listener keeping a hook's defaults does nothing in it, whatever
    /// its kind (what <c>tools/hookskip</c> checks of each hook), so the walk gives the same result in the same order.
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
        /// Filters keeping ExecutionFinished subscribers also depend on who subscribes, which changes through the
        /// methods that bump <see cref="SubscriptionVersion"/>; without that tracking they walk the shared
        /// <see cref="PileSnapshot"/> and check subscribers live.
        /// </summary>
        private static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThread;

        /// <summary>Snapshots and segments are usable: piles tracked, main thread, and not inside a shuffle (see <see cref="_shuffling"/>).</summary>
        private static bool CanCache => _tracking && OnMainThread && _shuffling == 0;

        private sealed class Segment
        {
            public long Version = -1;
            public long Subscriptions = -1;
            /// <summary><see cref="AfflictionVersion"/> when built (combat piles only).</summary>
            public long Afflictions = -1;
            /// <summary><see cref="UnscopedVersion"/> and <see cref="SubscribedMoves"/> when built.</summary>
            public long Unscoped = -1;
            public long Moves = -1;
            /// <summary><see cref="LimitlessHookSkip.Stamp"/> when built.</summary>
            public long MultiStamp = long.MinValue;
            /// <summary>The snapshot generation and entry count <see cref="Items"/> was built from.</summary>
            public long Generation;
            public int Consumed;
            public readonly List<AbstractModel> Items = new();
        }

        private readonly ConditionalWeakTable<object, Segment> _segments = new();

        /// <summary>Whether each <see cref="PileSnapshot"/> type id reacts: 0 unknown, 1 yes, -1 no (main thread only).</summary>
        private sbyte[] _reactsById = Array.Empty<sbyte>();

        /// <summary>The kept entries of these piles, in walk order (card, affliction, enchantment).</summary>
        internal void AddPiles(List<AbstractModel> list, object key, IReadOnlyList<CardPile> piles)
        {
            if (!CanCache)
            {
                AddPilesNow(list, piles);
                return;
            }
            AddCached(list, key, piles, withAfflictions: true);
        }

        /// <summary>
        /// The kept entries, from this filter's segment for these piles. After moves of cards it keeps nothing of, the
        /// segment stays; after cards were only appended (see <see cref="PileSnapshot.Of"/>), it is extended with the
        /// snapshot's new entries; after anything else it is rebuilt from the snapshot.
        /// </summary>
        private void AddCached(List<AbstractModel> list, object key, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            if (_keepSubscribed && !_subscriptionsTracked)
            {
#if BENCH
                UncachedWalks++;
                int start = list.Count;
#endif
                AddKept(list, PileSnapshot.Of(key, piles, withAfflictions), 0);
#if BENCH
                VerifyUncached(list, start, piles, withAfflictions);
#endif
                return;
            }
            SyncMulti();
            Segment segment = _segments.GetValue(key, _ => new Segment());
            if (segment.MultiStamp != _multiStamp)
            {
                // What a MultiEnchantment counts as changed: same as any other unseen change.
                segment.Version = -1;
                segment.Unscoped = -1;
                segment.Generation = -1;
                segment.MultiStamp = _multiStamp;
            }
            bool subscriptionsSame = !_keepSubscribed || segment.Subscriptions == SubscriptionVersion;
            // Affliction changes only matter to filters keeping some affliction type (see AfflictionFree).
            bool afflictionsSame = !withAfflictions || segment.Afflictions == AfflictionVersion || AfflictionFree();
            if (segment.Version != PileVersion && subscriptionsSame && afflictionsSame && UntouchedSince(segment))
            {
                // Only cards this filter keeps nothing of moved: same kept entries, in the same order.
#if BENCH
                SegmentMoveHits++;
#endif
                segment.Version = PileVersion;
                if (CheckDue(_nextMoveCheck))
                {
                    _nextMoveCheck = Environment.TickCount64 + SubscriptionCheckMs;
                    CheckMoveHit(segment, piles, withAfflictions);
                }
            }
            if (segment.Version != PileVersion || !subscriptionsSame || !afflictionsSame)
            {
                PileSnapshot snapshot = PileSnapshot.Of(key, piles, withAfflictions);
                if (subscriptionsSame && segment.Generation == snapshot.Generation && segment.Consumed <= snapshot.Count)
                {
#if BENCH
                    SegmentExtends++;
#endif
                    AddKept(segment.Items, snapshot, segment.Consumed);
                }
                else
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
                    AddKept(segment.Items, snapshot, 0);
                    segment.Generation = snapshot.Generation;
                }
                segment.Consumed = snapshot.Count;
                segment.Version = PileVersion;
                segment.Subscriptions = SubscriptionVersion;
                segment.Afflictions = AfflictionVersion;
                segment.Unscoped = UnscopedVersion;
                segment.Moves = SubscribedMoves;
            }
            else if (_keepSubscribed && CheckDue(_nextSubscriberCheck))
            {
                _nextSubscriberCheck = Environment.TickCount64 + SubscriptionCheckMs;
                CheckSubscribers(segment, key, piles, withAfflictions);
            }
#if BENCH
            else if (++SegmentHits % CacheVerifyEvery == 0)
            {
                VerifySegment(segment, piles, withAfflictions);
            }
            if (VerifyAll)
            {
                VerifySegment(segment, piles, withAfflictions);
            }
#endif
            list.AddRange(segment.Items);
        }

        /// <summary>
        /// Every build: a cached hit of a filter keeping subscribers, at most once per <see cref="SubscriptionCheckMs"/>,
        /// is compared with the snapshot's entries checked live (one walk, whatever the deck size or the hook rate). A difference means a subscription changed without going
        /// through the patched accessors: those filters stop caching for the session.
        /// </summary>
        private void CheckSubscribers(Segment segment, object key, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var fresh = new List<AbstractModel>(segment.Items.Count);
            AddKept(fresh, PileSnapshot.Of(key, piles, withAfflictions), 0);
            bool same = fresh.SequenceEqual(segment.Items, ReferenceEqualityComparer.Instance);
            SubscriberChecks++;
            SubscriberCheckTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (same)
            {
                return;
            }
            _subscriptionsTracked = false;
            segment.Items.Clear();
            segment.Items.AddRange(fresh);
            segment.Version = -1;
            Log.Error($"[{ModEntry.Id}] {Name}: an ExecutionFinished subscription changed unseen, hooks keeping subscribers no longer cached");
        }

        /// <summary>This filter's reacting type ids, among the first <see cref="_classified"/> ids (main thread only).</summary>
        private readonly List<int> _reacting = new();
        private int _classified;

        /// <summary>
        /// Whether the segment is still right though <see cref="PileVersion"/> changed: every change since moved a card
        /// (<see cref="MarkMoved"/>) and no moved entry was of a type this filter reacts to, or subscribed if it keeps
        /// those. A moved entry it doesn't keep changes neither its entries nor their relative order.
        /// </summary>
        private bool UntouchedSince(Segment segment)
        {
            if (segment.Unscoped != UnscopedVersion || (_keepSubscribed && segment.Moves != SubscribedMoves))
            {
                return false;
            }
            for (int known = TypeCount; _classified < known; _classified++)
            {
                if (Reacts(_classified))
                {
                    _reacting.Add(_classified);
                }
            }
            long since = segment.Version;
            foreach (int id in _reacting)
            {
                if (_typeChanged[id] > since)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Every build, at most once per <see cref="SubscriptionCheckMs"/> (one walk, whatever the deck size or the hook
        /// rate): a segment kept across moves is compared with a fresh walk. A difference means a move went unseen: moves
        /// stop being scoped for the session and this segment is rebuilt.
        /// </summary>
        private void CheckMoveHit(Segment segment, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var fresh = new List<AbstractModel>(segment.Items.Count);
            AddNow(fresh, piles, withAfflictions);
            bool same = fresh.SequenceEqual(segment.Items, ReferenceEqualityComparer.Instance);
            MoveChecks++;
            SubscriberCheckTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (!same)
            {
                MoveMismatches++;
                _scopedMoves = false;
                UnscopedVersion++;
                segment.Version = -1;
                segment.Generation = -1;
                Log.Error($"[{ModEntry.Id}] {Name}: a hook's entries changed unseen across card moves ({segment.Items.Count} vs {fresh.Count}), every move now rebuilds them");
            }
        }

        private long _multiStamp = long.MinValue;

        /// <summary>After a new inner enchantment type (or a switch): the MultiEnchantment type is judged again.</summary>
        internal void SyncMulti()
        {
            long stamp = LimitlessHookSkip.Stamp;
            if (stamp == _multiStamp)
            {
                return;
            }
            _multiStamp = stamp;
            if (LimitlessHookSkip.Multi is { } multi)
            {
                _byType.TryRemove(multi, out _);
                if (_multiTypeId >= 0 && _multiTypeId < _reactsById.Length)
                {
                    _reactsById[_multiTypeId] = 0;
                }
                _lastFilter = null;
                _reacting.Clear();
                _classified = 0;
            }
        }

        private Func<Type, bool>? _innerOverrides;

        /// <summary>A kept MultiEnchantment that only forwards this hook to inner enchantments that all keep its default does nothing.</summary>
        private bool MultiDoesNothing(AbstractModel multi)
        {
            if (BaseMethods == null || !LimitlessHookSkip.Active)
            {
                return false;
            }
            foreach (MethodInfo hook in BaseMethods)
            {
                if (Overrides(multi.GetType(), hook) && !LimitlessHookSkip.Forwards(hook))
                {
                    return false;
                }
            }
            _innerOverrides ??= InnerOverrides;
            bool nothing = !LimitlessHookSkip.AnyInner(multi, _innerOverrides);
#if BENCH
            if (nothing)
            {
                LimitlessHookSkip.Dropped++;
            }
#endif
            return nothing;
        }

        private readonly Dictionary<Type, bool> _innerReacts = new();

        private bool InnerOverrides(Type type)
        {
            lock (_innerReacts)
            {
                if (!_innerReacts.TryGetValue(type, out bool reacts))
                {
                    reacts = BaseMethods!.Any(hook => Overrides(type, hook));
                    _innerReacts[type] = reacts;
                }
                return reacts;
            }
        }

        private int _afflictionFreeAt = -1;
        private bool _afflictionFree;

        /// <summary>
        /// Whether this filter keeps no affliction: not keeping subscribers, and reacting to none of the affliction types
        /// known (<see cref="KnownAffliction"/>). Then an affliction change of a known type leaves its entries as they were;
        /// a change involving a type not known yet bumps <see cref="PileVersion"/> instead. The known types only grow, so
        /// a filter free of them now was free of every type changed since its segment was built.
        /// </summary>
        private bool AfflictionFree()
        {
            if (_keepSubscribed)
            {
                return false;
            }
            int known = _afflictionTypesVersion;
            if (_afflictionFreeAt != known)
            {
                bool free = true;
                foreach (Type type in _afflictionTypes.Keys)
                {
                    if (_byType.GetOrAdd(type, _reacts))
                    {
                        free = false;
                        break;
                    }
                }
                _afflictionFree = free;
                _afflictionFreeAt = known;
            }
            return _afflictionFree;
        }

        private void AddNow(List<AbstractModel> list, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            if (withAfflictions)
            {
                AddPilesNow(list, piles);
            }
            else
            {
                AddDeckNow(list, piles[0]);
            }
        }

#if BENCH
        private void VerifySegment(Segment segment, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            var fresh = new List<AbstractModel>();
            AddNow(fresh, piles, withAfflictions);
            if (!fresh.SequenceEqual(segment.Items, ReferenceEqualityComparer.Instance))
            {
                CacheMismatches++;
                Log.Error($"[{ModEntry.Id}] {Name}: cached pile entries differ from a fresh walk ({segment.Items.Count} vs {fresh.Count})");
                segment.Items.Clear();
                segment.Items.AddRange(fresh);
                segment.Version = -1; // Rebuilt from a fresh snapshot next time.
                segment.Unscoped = -1;
                segment.Generation = -1;
            }
        }

        /// <summary>One uncached walk in <see cref="CacheVerifyEvery"/> is compared with the per-entry walk.</summary>
        private void VerifyUncached(List<AbstractModel> list, int start, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            if (UncachedWalks % CacheVerifyEvery != 0 && !VerifyAll)
            {
                return;
            }
            var fresh = new List<AbstractModel>();
            AddNow(fresh, piles, withAfflictions);
            if (!fresh.SequenceEqual(list.Skip(start), ReferenceEqualityComparer.Instance))
            {
                CacheMismatches++;
                Log.Error($"[{ModEntry.Id}] {Name}: snapshot walk differs from a fresh walk ({list.Count - start} vs {fresh.Count})");
            }
        }
#endif

        /// <summary>The snapshot's entries this filter keeps, from <paramref name="from"/> on, in order: <see cref="Keeps"/>
        /// without a type lookup per entry.</summary>
        private void AddKept(List<AbstractModel> list, PileSnapshot snapshot, int from)
        {
            AbstractModel[] entries = snapshot.Entries;
            int[] typeIds = snapshot.TypeIds;
            int multi = _multiTypeId;
            for (int i = from; i < snapshot.Count; i++)
            {
                int id = typeIds[i];
                if ((Reacts(id) && (id != multi || !MultiDoesNothing(entries[i]))) || (_keepSubscribed && _executionFinished!(entries[i]) != null))
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
            bool reacts = _byType.GetOrAdd(TypeOfId(typeId), _reacts);
            _reactsById[typeId] = reacts ? (sbyte)1 : (sbyte)-1;
            return reacts;
        }

        private void AddPilesNow(List<AbstractModel> list, IReadOnlyList<CardPile> piles)
        {
            SyncMulti();
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
            _deckPiles[0] = deck;
            try
            {
                AddCached(list, deck, _deckPiles, withAfflictions: false);
            }
            finally
            {
                _deckPiles[0] = null!; // Don't keep the deck alive after the run.
            }
        }

        private static readonly CardPile[] _deckPiles = new CardPile[1];

        private void AddDeckNow(List<AbstractModel> list, CardPile deck)
        {
            SyncMulti();
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
            if (!_tracking && ReferenceEquals(type, LimitlessHookSkip.Multi))
            {
                // No snapshot or card move records its inner enchantments then.
                NoteMulti(model);
                SyncMulti();
            }
            bool reacts;
            if (Environment.CurrentManagedThreadId == _mainThread)
            {
                // Per type id, an array read: the walks outside scopes ask this for every relic, potion, power and monster.
                reacts = Reacts(TypeIdOf(type));
            }
            else if (ReferenceEquals(_lastFilter, this) && _lastType == type)
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
            if (reacts && ReferenceEquals(type, LimitlessHookSkip.Multi))
            {
                reacts = !MultiDoesNothing(model);
            }
            return reacts || (_keepSubscribed && _executionFinished!(model) != null);
        }

        /// <summary>Per kind of listener outside piles: 0 unknown, 1 some type reacts, -1 none does.</summary>
        private readonly sbyte[] _kindReacts = new sbyte[ListenerKinds.Count];
        private int _kindReactsStamp = -1;

        /// <summary>
        /// Whether a whole kind of listener can be left out of a walk: no loaded type of it reacts to this hook, so a run's
        /// hundreds of relics aren't even looked at. Never for filters keeping ExecutionFinished subscribers (those depend on each object).
        /// </summary>
        internal bool Skips(int kind)
        {
            if (_keepSubscribed || !KindSkipEnabled)
            {
                return false;
            }
            int stamp = ListenerKinds.Stamp;
            if (stamp != _kindReactsStamp)
            {
                Array.Clear(_kindReacts);
                _kindReactsStamp = stamp;
            }
            sbyte known = _kindReacts[kind];
            if (known == 0)
            {
                known = ListenerKinds.Any(kind, this) ? (sbyte)1 : (sbyte)-1;
                _kindReacts[kind] = known;
            }
#if BENCH
            if (known < 0)
            {
                KindsSkipped++;
            }
#endif
            return known < 0;
        }

        /// <summary>Whether this type reacts, cached per filter: no subscriber check, no stacked enchantment check.</summary>
        internal bool TypeReacts(Type type) => Environment.CurrentManagedThreadId == _mainThread ? Reacts(TypeIdOf(type)) : _byType.GetOrAdd(type, _reacts);

        /// <summary>Whether each relic of a player's relic list reacts to this filter's hook, and the list's version then (main thread only).</summary>
        private sealed class KeptRelics
        {
            public List<RelicModel>? Source;
            public int Version;
            public bool[] Reacts = Array.Empty<bool>();
        }

        private readonly ConditionalWeakTable<Player, KeptRelics> _relics = new();

        /// <summary>
        /// The player's relics (not melted) that <see cref="Keeps"/> keeps, in order. Which types react never changes for a filter, so
        /// that is only looked up again when the relic list changed (its <c>List</c> version: every add, insert, removal or set bumps
        /// it); a filter keeping subscribers also checks each relic's subscribers, a field read, every time.
        /// </summary>
        internal void AddRelics(List<AbstractModel> list, Player player)
        {
            IReadOnlyList<RelicModel> relics = player.Relics;
            if (!OnMainThread || _relicListVersion == null || relics is not List<RelicModel> source)
            {
                for (int j = 0; j < relics.Count; j++)
                {
                    if (!relics[j].IsMelted && Keeps(relics[j]))
                    {
                        list.Add(relics[j]);
                    }
                }
                return;
            }
            KeptRelics kept = _relics.GetValue(player, _ => new KeptRelics());
            int version = _relicListVersion(source);
            if (!ReferenceEquals(kept.Source, source) || kept.Version != version || kept.Reacts.Length != source.Count)
            {
                kept.Source = source;
                kept.Version = version;
                kept.Reacts = new bool[source.Count];
                for (int j = 0; j < source.Count; j++)
                {
                    kept.Reacts[j] = TypeReacts(source[j].GetType());
                }
            }
            bool[] reacts = kept.Reacts;
            for (int j = 0; j < reacts.Length; j++)
            {
                RelicModel relic = source[j];
                if ((reacts[j] || (_keepSubscribed && _executionFinished!(relic) != null)) && !relic.IsMelted)
                {
                    list.Add(relic);
                }
#if BENCH
                else
                {
                    OthersDropped++;
                }
#endif
            }
        }

        /// <summary>The base methods whose no-op defaults the dropped listeners rely on (null for interface filters).</summary>
        public MethodInfo[]? BaseMethods { get; private init; }

        /// <summary>Types overriding any of these <see cref="AbstractModel"/> virtuals.</summary>
        public static PileFilter Overriding(bool keepSubscribed, params string[] methods)
        {
            MethodInfo[] bases = methods.Select(m => AccessTools.Method(typeof(AbstractModel), m)
                ?? throw new MissingMethodException(nameof(AbstractModel), m)).ToArray();
            return new PileFilter(type => bases.Any(b => OverridesForFilter(type, b)), keepSubscribed) { BaseMethods = bases };
        }

        /// <summary>A MultiEnchantment reacts to a hook it forwards only if some inner enchantment seen so far overrides it.</summary>
        private static bool OverridesForFilter(Type type, MethodInfo hook)
        {
            if (!Overrides(type, hook))
            {
                return false;
            }
            bool reacts = !ReferenceEquals(type, LimitlessHookSkip.Multi) || !LimitlessHookSkip.Active || !LimitlessHookSkip.Forwards(hook)
                || LimitlessHookSkip.AnyKnown(inner => Overrides(inner, hook));
#if BENCH
            if (!reacts)
            {
                LimitlessHookSkip.Dropped++;
            }
#endif
            return reacts;
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
    /// tests an array per type id instead of looking each entry's type up. When the changes since are all card moves in
    /// <see cref="_moves"/>, they are applied to the arrays instead of rebuilding them (one array shift per move, far less
    /// than reading every card). Cards appended to the last of its piles (cloning cards into the deck) keep the entries
    /// before them in place: same <see cref="Generation"/>. Main thread only.
    /// </summary>
    private sealed class PileSnapshot
    {
        public long Version = -1;
        /// <summary><see cref="AfflictionVersion"/> of the entries (combat piles; the deck's hold no affliction).</summary>
        public long Afflictions = -1;
        /// <summary>Bumped on each rebuild or shift: within a generation, entries are only ever appended.</summary>
        public long Generation;
        public AbstractModel[] Entries = new AbstractModel[1024];
        public int[] TypeIds = new int[1024];
        public int Count;
        /// <summary>The piles this snapshot walks (a copy: the deck's list is reused), and where each one's entries start.</summary>
        private CardPile[] _piles = Array.Empty<CardPile>();
        private int[] _starts = new int[1];

        private static readonly ConditionalWeakTable<object, PileSnapshot> _snapshots = new();

        public static PileSnapshot Of(object key, IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            PileSnapshot snapshot = _snapshots.GetValue(key, _ => new PileSnapshot());
            bool afflictionsSame = !withAfflictions || snapshot.Afflictions == AfflictionVersion;
            if (snapshot.Version == PileVersion && afflictionsSame)
            {
                return snapshot;
            }
            if (afflictionsSame && snapshot.CatchUp(piles, withAfflictions))
            {
#if BENCH
                SnapshotCatchUps++;
#endif
                return snapshot;
            }
            int previous = snapshot.Count;
            snapshot.Count = 0;
            if (snapshot._piles.Length != piles.Count)
            {
                snapshot._piles = new CardPile[piles.Count];
                snapshot._starts = new int[piles.Count + 1];
            }
            for (int l = 0; l < piles.Count; l++)
            {
                snapshot._piles[l] = piles[l];
                snapshot._starts[l] = snapshot.Count;
                IReadOnlyList<CardModel> cards = piles[l].Cards;
                for (int m = 0; m < cards.Count; m++)
                {
                    snapshot.AddCard(cards[m], withAfflictions);
                }
            }
            snapshot._starts[piles.Count] = snapshot.Count;
            if (previous > snapshot.Count)
            {
                // Don't keep cards that left the piles alive.
                Array.Clear(snapshot.Entries, snapshot.Count, previous - snapshot.Count);
            }
            snapshot.Version = PileVersion;
            snapshot.Afflictions = AfflictionVersion;
            snapshot.Generation++;
#if BENCH
            SnapshotRebuilds++;
#endif
            return snapshot;
        }

        /// <summary>
        /// Applies the moves since <see cref="Version"/> when every pile change since then is in <see cref="_moves"/>
        /// (nothing else bumped the version): card, affliction and enchantment of a card are as in the snapshot, since
        /// any change of them would have bumped a version. False: the caller rebuilds, whatever was applied.
        /// </summary>
        private bool CatchUp(IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            int first = MovesSince(Version);
            if (first < 0 || piles.Count == 0 || piles.Count != _piles.Length)
            {
                return false;
            }
            for (int l = 0; l < piles.Count; l++)
            {
                if (!ReferenceEquals(piles[l], _piles[l]))
                {
                    return false;
                }
            }
            int last = piles.Count - 1;
            int shifts = 0;
            for (int i = first; i < _moves.Count; i++)
            {
                Move move = _moves[i];
                int l = Array.IndexOf(_piles, move.Pile);
                if (l < 0)
                {
                    continue;
                }
                if (move.Add && move.AtEnd && l == last)
                {
                    AddCard(move.Card, withAfflictions);
                    _starts[piles.Count] = Count;
                    continue;
                }
                // Shifting costs a copy of the arrays: past a few moves, reading the piles again is cheaper.
                if (!_shiftMoves || ++shifts > MaxShiftsPerCatchUp || !(move.Add ? Insert(l, move, withAfflictions) : Remove(l, move.Card)))
                {
                    return false;
                }
            }
            if (shifts > 0)
            {
                Generation++;
                if (CheckDue(_nextShiftCheck))
                {
                    _nextShiftCheck = Environment.TickCount64 + SubscriptionCheckMs;
                    if (!MatchesPiles(withAfflictions))
                    {
                        return false;
                    }
                }
            }
            Version = PileVersion;
            return true;
        }

        private bool Insert(int l, Move move, bool withAfflictions)
        {
            if (!move.AtEnd && move.Index != 0)
            {
                return false; // Somewhere inside the pile (a random position): rare, the caller reads the piles again.
            }
            int entries = 1 + (withAfflictions && move.Card.Affliction != null ? 1 : 0) + (move.Card.Enchantment != null ? 1 : 0);
            int at = move.AtEnd ? _starts[l + 1] : _starts[l];
            Reserve(Count + entries);
            Array.Copy(Entries, at, Entries, at + entries, Count - at);
            Array.Copy(TypeIds, at, TypeIds, at + entries, Count - at);
            int count = Count;
            Count = at;
            AddCard(move.Card, withAfflictions);
            Count = count + entries;
            for (int p = l + 1; p < _starts.Length; p++)
            {
                _starts[p] += entries;
            }
            return true;
        }

        /// <summary>The list's <c>Remove</c>: the card's first occurrence in that pile, and the entries of its own after it.</summary>
        private bool Remove(int l, CardModel card)
        {
            AbstractModel[] entries = Entries;
            int end = _starts[l + 1];
            int at = _starts[l];
            while (at < end && !ReferenceEquals(entries[at], card))
            {
                at++;
            }
            if (at == end)
            {
                return false;
            }
            int next = at + 1;
            while (next < end && entries[next] is not CardModel)
            {
                next++;
            }
            int removed = next - at;
            Array.Copy(entries, next, entries, at, Count - next);
            Array.Copy(TypeIds, next, TypeIds, at, Count - next);
            Array.Clear(entries, Count - removed, removed);
            Count -= removed;
            for (int p = l + 1; p < _starts.Length; p++)
            {
                _starts[p] -= removed;
            }
            return true;
        }

        /// <summary>
        /// Every build, at most once per <see cref="SubscriptionCheckMs"/>: shifted entries against the piles (one read of
        /// the piles, no allocation). A difference stops shifting for the session.
        /// </summary>
        private bool MatchesPiles(bool withAfflictions)
        {
            int k = 0;
            bool same = true;
            for (int l = 0; l < _piles.Length && same; l++)
            {
                same = _starts[l] == k;
                IReadOnlyList<CardModel> cards = _piles[l].Cards;
                for (int m = 0; m < cards.Count && same; m++)
                {
                    CardModel card = cards[m];
                    same = k < Count && ReferenceEquals(Entries[k++], card);
                    if (same && withAfflictions && card.Affliction != null)
                    {
                        same = k < Count && ReferenceEquals(Entries[k++], card.Affliction);
                    }
                    if (same && card.Enchantment != null)
                    {
                        same = k < Count && ReferenceEquals(Entries[k++], card.Enchantment);
                    }
                }
            }
            MoveChecks++;
            if (same && k == Count)
            {
                return true;
            }
            MoveMismatches++;
            _shiftMoves = false;
            Log.Error($"[{ModEntry.Id}] {Name}: pile entries differ from the piles after card moves, moves now rebuild them");
            return false;
        }

        private void AddCard(CardModel card, bool withAfflictions)
        {
            Add(card);
            if (withAfflictions && card.Affliction != null)
            {
                KnownAffliction(card.Affliction); // e.g. on a clone, set without the setter
                Add(card.Affliction);
            }
            if (card.Enchantment != null)
            {
                Add(card.Enchantment);
            }
        }

        private void Reserve(int size)
        {
            if (size > Entries.Length)
            {
                int capacity = Math.Max(size, Entries.Length * 2);
                Array.Resize(ref Entries, capacity);
                Array.Resize(ref TypeIds, capacity);
            }
        }

        private void Add(AbstractModel model)
        {
            Reserve(Count + 1);
            Entries[Count] = model;
            int id = TypeIdOf(model.GetType());
            TypeIds[Count] = id;
            Count++;
            if (id == _multiTypeId)
            {
                NoteMulti(model);
            }
        }
    }

    /// <summary>A small id per listener type seen in a snapshot or a card move (main thread only).</summary>
    private static readonly Dictionary<Type, int> _typeIds = new();
    private static readonly List<Type> _typeList = new();
    private static int TypeCount => _typeList.Count;
    private static Type TypeOfId(int id) => _typeList[id];

    private static int TypeIdOf(Type type)
    {
        // Main thread only (its callers check): a pile filled at combat start asks about the same few types thousands of times.
        if (ReferenceEquals(type, _lastIdType))
        {
            return _lastId;
        }
        if (!_typeIds.TryGetValue(type, out int id))
        {
            id = _typeList.Count;
            _typeIds[type] = id;
            _typeList.Add(type);
            if (id >= _typeChanged.Length)
            {
                Array.Resize(ref _typeChanged, _typeChanged.Length * 2);
            }
            if (ReferenceEquals(type, LimitlessHookSkip.Multi))
            {
                _multiTypeId = id;
            }
        }
        _lastIdType = type;
        _lastId = id;
        return id;
    }

    private static Type? _lastIdType;
    private static int _lastId;

    /// <summary>The type id of Limitless's MultiEnchantment, once one was seen.</summary>
    private static int _multiTypeId = -1;

    private static void NoteMulti(AbstractModel multi)
    {
        if (LimitlessHookSkip.Active)
        {
            try
            {
                LimitlessHookSkip.Note(multi);
            }
            catch (Exception e)
            {
                LimitlessHookSkip.Disable(e);
            }
        }
    }

    /// <summary>Around Limitless's <c>AddEnchantment</c>: the filters' entries may change, as for an enchantment set on a card.</summary>
    internal static void MultiChanging() => BumpPileVersion();

    internal static void MultiChanged()
    {
        PileVersion++;
        UnscopedVersion++;
    }

    /// <summary>
    /// Pile changes all go through <see cref="PileVersion"/> and this is the main thread, outside a shuffle (which reorders a pile's list
    /// unseen). Not after a fault: <see cref="PatchGuard"/> may have removed what bumps the version.
    /// </summary>
    internal static bool PilesKnown => _tracking && _shuffling == 0 && Thread.CurrentThread.ManagedThreadId == _mainThread && !PatchSafety.IsFaulted(Name);

    /// <summary>
    /// Adds to <paramref name="count"/> what the changes since <paramref name="version"/> did to the number of cards of
    /// <paramref name="piles"/> that <paramref name="counted"/> accepts. False, nothing to rely on: not every change is in <see cref="_moves"/>.
    /// </summary>
    internal static bool ApplyMoves(long version, IReadOnlyList<CardPile> piles, Func<CardModel, bool> counted, ref int count)
    {
        int first = MovesSince(version);
        if (first < 0)
        {
            return false;
        }
        for (int i = first; i < _moves.Count; i++)
        {
            Move move = _moves[i];
            for (int p = 0; p < piles.Count; p++)
            {
                if (ReferenceEquals(piles[p], move.Pile))
                {
                    if (counted(move.Card))
                    {
                        count += move.Add ? 1 : -1;
                    }
                    break;
                }
            }
        }
        return true;
    }

    /// <summary><see cref="PileVersion"/> at the last move of a pile entry of each type id.</summary>
    private static long[] _typeChanged = new long[256];

    /// <summary>
    /// Bumped by every pile change not tied to the types of a moved card (shuffle, a whole pile cleared, an enchantment
    /// changing, a move off the main thread): every segment is rebuilt.
    /// </summary>
    private static long UnscopedVersion;

    /// <summary>Bumped when a moved entry has ExecutionFinished subscribers (filters keeping those keep it whatever its type).</summary>
    private static long SubscribedMoves;

    /// <summary>Off after a mismatch found by <see cref="PileFilter.CheckMoveHit"/>: moves count as any other change.</summary>
    private static bool _scopedMoves = true;
    /// <summary>Off after a mismatch found by <see cref="PileSnapshot.MatchesPiles"/>.</summary>
    private static bool _shiftMoves = true;
    private const int MaxShiftsPerCatchUp = 64;
    private static long _nextMoveCheck, _nextShiftCheck;
    /// <summary>Live checks of kept segments and shifted snapshots, and the differences found (0 expected).</summary>
    public static long MoveChecks { get; private set; }
    public static long MoveMismatches { get; private set; }

    /// <summary>Before and after a card moves: a filter's kept entries change only if it keeps one of this card's entries.</summary>
    private static void MarkMoved(CardModel card)
    {
        if (!_scopedMoves || Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            UnscopedVersion++;
            return;
        }
        MarkEntry(card);
        if (card.Affliction != null)
        {
            MarkEntry(card.Affliction);
        }
        if (card.Enchantment != null)
        {
            MarkEntry(card.Enchantment);
        }
    }

    private static void MarkEntry(AbstractModel model)
    {
        int id = TypeIdOf(model.GetType());
        _typeChanged[id] = PileVersion;
        if (id == _multiTypeId)
        {
            NoteMulti(model);
        }
        if (_executionFinished!(model) != null)
        {
            SubscribedMoves++;
        }
    }

    private static AccessTools.FieldRef<AbstractModel, Action<AbstractModel>?>? _executionFinished;

    /// <summary><c>List&lt;RelicModel&gt;._version</c>; null if the runtime has none (relics then checked one by one).</summary>
    private static AccessTools.FieldRef<List<RelicModel>, int>? _relicListVersion;

    private static int _mainThread;

    /// <summary>
    /// Bumped before and after every change to a pile's cards or order, and every enchantment/affliction change of a
    /// card in a pile (a card in none, like a fresh clone, is in no snapshot).
    /// </summary>
    internal static long PileVersion;

    /// <summary>
    /// Bumped before and after every affliction change of a card in a pile, when the old and new afflictions' types are
    /// both known (<see cref="KnownAffliction"/>; else <see cref="PileVersion"/> is bumped). Pile entries of combat
    /// piles depend on it, but only the filters keeping some affliction type do (Hex afflicting a whole deck no longer
    /// rebuilds every hook's entries for each card).
    /// </summary>
    internal static long AfflictionVersion;

    /// <summary>Changes when either version does: for caches built from pile entries without knowing their filters.</summary>
    internal static long EntriesVersion => PileVersion + AfflictionVersion;

    /// <summary>Affliction types seen on cards so far (any thread), and a counter bumped when one is added.</summary>
    private static readonly ConcurrentDictionary<Type, byte> _afflictionTypes = new();
    private static int _afflictionTypesVersion;

    /// <summary>Whether this affliction's type was already known (null: yes); records it if not.</summary>
    private static bool KnownAffliction(AbstractModel? affliction)
    {
        if (affliction == null || _afflictionTypes.ContainsKey(affliction.GetType()))
        {
            return true;
        }
        if (_afflictionTypes.TryAdd(affliction.GetType(), 0))
        {
            Interlocked.Increment(ref _afflictionTypesVersion);
        }
        return false;
    }

    /// <summary>Bumped before and after every ExecutionFinished subscription change (see <see cref="_subscriptionsTracked"/>).</summary>
    internal static long SubscriptionVersion;

    /// <summary>
    /// ExecutionFinished changes on a model through its add/remove accessors only (AfterCloned clears it on a fresh
    /// clone, in no pile yet): both are patched, so filters keeping subscribers can cache their segments.
    /// </summary>
    private static bool _subscriptionsTracked;

    /// <summary>A cached hit of the filters keeping subscribers is checked live at most this often (every build, main thread).</summary>
    private const long SubscriptionCheckMs = 2000;

    /// <summary>A due check waiting for a moment without an action running waits at most this much longer.</summary>
    private const long CheckWaitMs = 6000;

    /// <summary>
    /// Whether a live check (one walk of every pile: ~5 ms at 20,000 cards per player) runs now: when due and no action runs
    /// (hooks are also walked between actions: hover, costs), or overdue by <see cref="CheckWaitMs"/>. During an action it
    /// lands in the frames that already take longest. This moves when a mismatch would be found, never what is found.
    /// </summary>
    private static bool CheckDue(long next)
    {
        long now = Environment.TickCount64;
        if (now < next)
        {
            return false;
        }
        return now >= next + CheckWaitMs || RunManager.Instance?.ActionExecutor is not { IsRunning: true };
    }
    private static long _nextSubscriberCheck;
    /// <summary>Live subscriber checks run, and their total time (Stopwatch ticks).</summary>
    public static long SubscriberChecks { get; private set; }
    public static long SubscriberCheckTicks { get; private set; }

    /// <summary>v0.107.1 code the subscription tracking relies on.</summary>
    private static readonly (string Method, string Fingerprint)[] SubscriptionMethods =
    {
        ("add_ExecutionFinished", "2AFEADF7C5B46C3A"),
        ("remove_ExecutionFinished", "A56C4E17FEF3C32D"),
        ("AfterCloned", "0909DD3790FCD0E0"),
    };

    /// <summary>
    /// A card added to or removed from a pile through its methods, nothing else changing meanwhile: the version went from
    /// <see cref="From"/> to From + 2 (the bumps around the method). <see cref="AtEnd"/>: added after the pile's last card;
    /// <see cref="Index"/>: where it was added.
    /// </summary>
    private readonly record struct Move(long From, CardPile Pile, CardModel Card, bool Add, bool AtEnd, int Index);

    /// <summary>
    /// The pile changes since the last other change, when all of them were such moves: consecutive versions, the last
    /// one reaching <see cref="PileVersion"/> unless something else changed since. Main thread only.
    /// </summary>
    private static readonly List<Move> _moves = new();
    private const int MaxMoves = 4096;

#if BENCH
    /// <summary>One cache hit in this many is compared with a fresh walk (Debug builds).</summary>
    internal static int CacheVerifyEvery = 256;
    /// <summary>Every cached walk is compared with a fresh walk (tests).</summary>
    internal static bool VerifyAll;
    public static long CacheMismatches { get; private set; }
    /// <summary>Pile segments: rebuilt, extended after appends, served from cache, walked uncached (filters keeping
    /// subscribers, without subscription tracking); snapshots caught up after appends; and how many distinct pile
    /// versions needed a rebuild.</summary>
    public static long SegmentRebuilds, SegmentExtends, SegmentHits, SegmentMoveHits, UncachedWalks, SnapshotCatchUps, SnapshotRebuilds, RebuiltVersions;
    /// <summary>Walks run as the mirrors (the effects check).</summary>
    public static long CombatWalks, RunWalks;
    /// <summary>Shuffles in progress: 0 when nothing runs (the effects check).</summary>
    public static int OpenShuffles => _shuffling;
    private static long _lastRebuildVersion = -1;
#endif

    /// <summary>Index of the first move from <paramref name="version"/> on, if the moves lead from it to now; else -1.</summary>
    private static int MovesSince(long version)
    {
        int count = _moves.Count;
        if (count == 0 || _moves[count - 1].From + 2 != PileVersion)
        {
            return -1;
        }
        long offset = version - _moves[0].From;
        if (offset < 0 || offset % 2 != 0 || offset / 2 >= count)
        {
            return -1;
        }
        int index = (int)(offset / 2);
        return _moves[index].From == version ? index : -1;
    }

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

    private static void BumpPileVersion()
    {
        PileVersion++;
        UnscopedVersion++;
        if (_moves.Count != 0 && Thread.CurrentThread.ManagedThreadId == _mainThread)
        {
            _moves.Clear(); // Can't lead to the current version any more; don't keep the piles alive.
        }
    }

    private readonly record struct MoveState(long From, int Count);

    private static void BeforeAdd(CardPile __instance, CardModel card, out MoveState __state)
    {
        __state = new MoveState(PileVersion, __instance.Cards.Count);
        PileVersion++;
        MarkMoved(card);
    }

    /// <summary>Records the add when it went as asked and nothing else changed meanwhile.</summary>
    private static Exception? AfterAdd(Exception? __exception, CardPile __instance, CardModel card, int index, MoveState __state)
    {
        PileVersion++;
        MarkMoved(card);
        if (__exception != null)
        {
            UnscopedVersion++;
        }
        if (Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            return __exception;
        }
        IReadOnlyList<CardModel> cards = __instance.Cards;
        int at = index >= 0 ? index : __state.Count;
        if (__exception != null || PileVersion != __state.From + 2 || cards.Count != __state.Count + 1
            || at >= cards.Count || !ReferenceEquals(cards[at], card))
        {
            _moves.Clear();
            return __exception;
        }
        Record(new Move(__state.From, __instance, card, Add: true, AtEnd: at == __state.Count, at));
        return __exception;
    }

    private static void BeforeRemove(CardPile __instance, CardModel card, out MoveState __state)
    {
        __state = new MoveState(PileVersion, __instance.Cards.Count);
        PileVersion++;
        MarkMoved(card);
    }

    private static Exception? AfterRemove(Exception? __exception, CardPile __instance, CardModel card, MoveState __state)
    {
        PileVersion++;
        MarkMoved(card);
        if (__exception != null)
        {
            UnscopedVersion++;
        }
        if (Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            return __exception;
        }
        if (__exception != null || PileVersion != __state.From + 2 || __instance.Cards.Count != __state.Count - 1)
        {
            _moves.Clear();
            return __exception;
        }
        Record(new Move(__state.From, __instance, card, Add: false, AtEnd: false, -1));
        return __exception;
    }

    private static void Record(Move move)
    {
        if (_moves.Count != 0 && _moves[_moves.Count - 1].From + 2 != move.From)
        {
            _moves.Clear();
        }
        if (_moves.Count == MaxMoves)
        {
            _moves.RemoveRange(0, MaxMoves / 2);
        }
        _moves.Add(move);
    }

    /// <summary>A card moved inside its pile: the filters keeping it see a new order, snapshots read the piles again.</summary>
    private static void BeforeReorder(CardModel card)
    {
        PileVersion++;
        if (_moves.Count != 0 && Thread.CurrentThread.ManagedThreadId == _mainThread)
        {
            _moves.Clear();
        }
        MarkMoved(card);
    }

    private static Exception? AfterReorder(Exception? __exception, CardModel card)
    {
        PileVersion++;
        MarkMoved(card);
        if (__exception != null)
        {
            UnscopedVersion++;
        }
        return __exception;
    }

    /// <summary>An enchantment/affliction change only matters for cards in a pile (<see cref="PileIndex"/> knows; unsure: bump).</summary>
    private static void BumpIfPiled(CardModel __instance, out bool __state)
    {
        __state = !PileIndex.InNoPile(__instance);
        if (__state)
        {
            BumpPileVersion();
        }
    }

    private static Exception? BumpIfPiledFinally(Exception? __exception, bool __state)
    {
        if (__state)
        {
            PileVersion++;
            UnscopedVersion++;
        }
        return __exception;
    }

    /// <summary>
    /// Around the Affliction setter of a card in a pile: <see cref="AfflictionVersion"/> when the old and new affliction
    /// types are known, else <see cref="PileVersion"/> (every cache rebuilt, as for any other change). 0: in no pile.
    /// </summary>
    private static void BeforeAfflictionSet(CardModel __instance, AfflictionModel? value, out byte __state)
    {
        if (PileIndex.InNoPile(__instance))
        {
            __state = 0;
            return;
        }
        // Both checked: each records its type when new.
        bool known = KnownAffliction(__instance.Affliction) & KnownAffliction(value);
        if (known)
        {
            __state = 1;
            AfflictionVersion++;
        }
        else
        {
            __state = 2;
            BumpPileVersion();
        }
    }

    private static Exception? AfterAfflictionSet(Exception? __exception, byte __state)
    {
        if (__state == 1)
        {
            AfflictionVersion++;
        }
        else if (__state == 2)
        {
            PileVersion++;
            UnscopedVersion++;
        }
        return __exception;
    }

    private static void BumpSubscriptions() => SubscriptionVersion++;

    private static Exception? BumpSubscriptionsFinally(Exception? __exception)
    {
        SubscriptionVersion++;
        return __exception;
    }

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
        UnscopedVersion++;
        return __exception;
    }

    /// <summary>v0.107.1 <c>ModHelper.IterateAllCombatStateSubscribers</c> (state machine), mirrored by <see cref="Subscribers"/>.</summary>
    private const string SubscribersFingerprint = "B70A116760A4FE08";

    /// <summary>
    /// BaseLib 3.4.7's "BaseLibCardModifiers" subscriber: for every card in every combat pile, the card's CardModifier
    /// list (a table lookup per card, ~8% of an action at 8,400 cards). Only instances of a concrete CardModifier
    /// subclass can be in those lists, so with no such type loaded it can only return an empty list.
    /// </summary>
    private const string CardModifiersId = "BaseLibCardModifiers";
    private const string CardModifiersFingerprint = "8AB473DDA1E8065B";
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
        _executionFinished = AccessTools.FieldRefAccess<AbstractModel, Action<AbstractModel>?>("ExecutionFinished");
        _relicListVersion = AccessTools.Field(typeof(List<RelicModel>), "_version")?.FieldType == typeof(int)
            ? AccessTools.FieldRefAccess<List<RelicModel>, int>("_version") : null;
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        try
        {
            ApplyTracking(harmony);
        }
        catch (Exception e)
        {
            _tracking = false;
            Log.Info($"[{ModEntry.Id}] {Name}: pile entries read at each walk, left vanilla: {e.Message}");
        }
        ApplyWalks(harmony);
        Applied = true;
    }

    /// <summary>
    /// Whether the entries of a filtered walk can be cached: every pile and enchantment change goes through a patched method.
    /// Each method only has to be the one changing the list, so its code doesn't matter here; what it does only matters to
    /// moves applied to the cached entries.
    /// </summary>
    private static bool _tracking;

    private static void ApplyTracking(Harmony harmony)
    {
        // The pile cache is bumped by those methods only: no other code may change the pile lists.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        foreach (string property in new[] { nameof(CardModel.Enchantment), nameof(CardModel.Affliction) })
        {
            // The setter is the only code changing it (auto-property backing field), so bumping around it is enough.
            PatchSafety.RequireFieldWriters(typeof(CardModel), $"<{property}>k__BackingField", "set_" + property);
        }
        // Pile cache invalidation: before (a walk during the change sees a new version) and after (even on exceptions).
        var bumpBefore = new HarmonyMethod(typeof(FastListeners), nameof(BumpPileVersion));
        var bumpAfter = new HarmonyMethod(typeof(FastListeners), nameof(BumpPileVersionFinally));
        foreach ((string method, string fingerprint) in PileMethods)
        {
            MethodInfo target = AccessTools.Method(typeof(CardPile), method) ?? throw new MissingMethodException(nameof(CardPile), method);
            string actual = MethodFingerprint.Of(target);
            if (!PatchSafety.FingerprintMatches(actual, fingerprint))
            {
                _scopedMoves = false;
                _shiftMoves = false;
                Log.Info($"[{ModEntry.Id}] {Name}: CardPile.{method} changed (fingerprint {actual}, expected {fingerprint}): any card move now rebuilds the cached entries");
            }
            if (method == nameof(CardPile.AddInternal))
            {
                PatchGuard.Patch(harmony, target, prefix: new HarmonyMethod(typeof(FastListeners), nameof(BeforeAdd)),
                    finalizer: new HarmonyMethod(typeof(FastListeners), nameof(AfterAdd)));
            }
            else if (method == nameof(CardPile.RemoveInternal))
            {
                PatchGuard.Patch(harmony, target, prefix: new HarmonyMethod(typeof(FastListeners), nameof(BeforeRemove)),
                    finalizer: new HarmonyMethod(typeof(FastListeners), nameof(AfterRemove)));
            }
            else if (method is nameof(CardPile.MoveToBottomInternal) or nameof(CardPile.MoveToTopInternal))
            {
                PatchGuard.Patch(harmony, target, prefix: new HarmonyMethod(typeof(FastListeners), nameof(BeforeReorder)),
                    finalizer: new HarmonyMethod(typeof(FastListeners), nameof(AfterReorder)));
            }
            else
            {
                PatchGuard.Patch(harmony, target, prefix: bumpBefore, finalizer: bumpAfter);
            }
        }
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), "RandomizeOrderInternal"),
            prefix: new HarmonyMethod(typeof(FastListeners), nameof(EnterShuffle)), finalizer: new HarmonyMethod(typeof(FastListeners), nameof(ExitShuffle)));
        foreach (string property in new[] { nameof(CardModel.Enchantment), nameof(CardModel.Affliction) })
        {
            bool affliction = property == nameof(CardModel.Affliction);
            PatchGuard.Patch(harmony, AccessTools.PropertySetter(typeof(CardModel), property) ?? throw new MissingMethodException(nameof(CardModel), property),
                prefix: new HarmonyMethod(typeof(FastListeners), affliction ? nameof(BeforeAfflictionSet) : nameof(BumpIfPiled)),
                finalizer: new HarmonyMethod(typeof(FastListeners), affliction ? nameof(AfterAfflictionSet) : nameof(BumpIfPiledFinally)));
        }
        try
        {
            // Only these change a model's ExecutionFinished subscribers (AfterCloned clears a clone's copy).
            PatchSafety.RequireFieldWriters(typeof(AbstractModel), "ExecutionFinished", "AfterCloned", "add_ExecutionFinished", "remove_ExecutionFinished");
            foreach ((string method, string fingerprint) in SubscriptionMethods)
            {
                PatchSafety.RequireFingerprint(AccessTools.Method(typeof(AbstractModel), method)
                    ?? throw new MissingMethodException(nameof(AbstractModel), method), fingerprint);
            }
            foreach (string accessor in new[] { "add_ExecutionFinished", "remove_ExecutionFinished" })
            {
                PatchGuard.Patch(harmony, AccessTools.Method(typeof(AbstractModel), accessor),
                    prefix: new HarmonyMethod(typeof(FastListeners), nameof(BumpSubscriptions)),
                    finalizer: new HarmonyMethod(typeof(FastListeners), nameof(BumpSubscriptionsFinally)));
            }
            // The game's only subscriber (the hand after a card selection) also bumps it around its own calls, whatever
            // the JIT did with the accessors there.
            foreach (string method in new[] { "AfterCardsSelected", "OnSelectModeSourceFinished" })
            {
                PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand), method)
                        ?? throw new MissingMethodException("NPlayerHand", method),
                    prefix: new HarmonyMethod(typeof(FastListeners), nameof(BumpSubscriptions)),
                    finalizer: new HarmonyMethod(typeof(FastListeners), nameof(BumpSubscriptionsFinally)));
            }
            _subscriptionsTracked = true;
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: hooks keeping ExecutionFinished subscribers left uncached: {e.Message}");
        }
        _tracking = true;
    }

    /// <summary>The mirrors of the game's two walks, each on its own.</summary>
    private static void ApplyWalks(Harmony harmony)
    {
        try
        {
            MethodInfo combatIterate = AccessTools.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners))
                ?? throw new MissingMethodException("CombatState.IterateHookListeners");
            _combatBody = AccessTools.EnumeratorMoveNext(combatIterate);
            _combatContains = AccessTools.Method(typeof(CombatState), "Contains");
            PatchSafety.RequireFingerprint(_combatBody, CombatIterateFingerprint);
            PatchSafety.RequireFingerprint(_combatContains, CombatContainsFingerprint);
            _allies = AccessTools.FieldRefAccess<CombatState, List<Creature>>("_allies");
            _enemies = AccessTools.FieldRefAccess<CombatState, List<Creature>>("_enemies");
            ApplySubscribers();
            _combatIterate = combatIterate;
            // After ListenerCache: inside its scopes it answers from its cache and this does nothing.
            PatchGuard.Patch(harmony, combatIterate, prefix: new HarmonyMethod(typeof(FastListeners), nameof(CombatPrefix)) { priority = Priority.Last });
            CombatMirrored = true;
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: combat walk left vanilla (filters drop its entries as they come): {e.Message}");
        }
        try
        {
            MethodInfo runIterate = AccessTools.Method(typeof(RunState), nameof(RunState.IterateHookListeners), new[] { typeof(ICombatState) })
                ?? throw new MissingMethodException("RunState.IterateHookListeners");
            _runBody = AccessTools.EnumeratorMoveNext(runIterate);
            _runContains = AccessTools.Method(typeof(RunState), "Contains");
            PatchSafety.RequireFingerprint(_runBody, RunIterateFingerprint);
            PatchSafety.RequireFingerprint(_runContains, RunContainsFingerprint);
            _runIterate = runIterate;
            PatchGuard.Patch(harmony, runIterate, prefix: new HarmonyMethod(typeof(FastListeners), nameof(RunPrefix)) { priority = Priority.Last });
            RunMirrored = true;
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: run walk left vanilla (filters drop its entries as they come): {e.Message}");
        }
    }

    private static void ApplySubscribers()
    {
        try
        {
            _subscribersMethod = AccessTools.Method(typeof(ModHelper), nameof(ModHelper.IterateAllCombatStateSubscribers));
            _subscribersBody = AccessTools.EnumeratorMoveNext(_subscribersMethod);
            PatchSafety.RequireFingerprint(_subscribersBody, SubscribersFingerprint);
            // Everything resolved and typed before use: AccessTools returns null for a renamed member, which would only
            // fail later, inside every walk.
            var subscribers = AccessTools.Field(typeof(ModHelper), "_combatHookSubscribers")?.GetValue(null) as System.Collections.IList
                ?? throw new MissingFieldException(nameof(ModHelper), "_combatHookSubscribers");
            Type subscriber = AccessTools.Inner(typeof(ModHelper), "ModCombatHookSubscriber") ?? throw new TypeLoadException("ModHelper.ModCombatHookSubscriber");
            FieldInfo id = AccessTools.Field(subscriber, "id") is { FieldType: var idType } f1 && idType == typeof(string) ? f1
                : throw new MissingFieldException("ModCombatHookSubscriber", "id");
            FieldInfo del = AccessTools.Field(subscriber, "del") is { FieldType: var delType } f2 && delType == typeof(CombatHookSubscriptionDelegate) ? f2
                : throw new MissingFieldException("ModCombatHookSubscriber", "del");
            // A mod loading later may define CardModifier types: check just that assembly (dynamic ones, e.g. Harmony's,
            // and assemblies not referencing BaseLib can't). Runs inside that assembly's load: never throws.
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                if (_noCardModifierTypes == true && BaseLibTypes.MayDefine(args.LoadedAssembly) && BaseLibTypes.Defines(args.LoadedAssembly, CardModifierType))
                {
                    _noCardModifierTypes = false;
                }
            };
            (_subscriberId, _subscriberDelegate, _combatSubscribers) = (id, del, subscribers);
        }
        catch (Exception e)
        {
            _combatSubscribers = null; // ModHelper's own walk, as is.
            Log.Info($"[{ModEntry.Id}] {Name}: mod subscriber walk left vanilla: {e.Message}");
        }
    }

    /// <summary>Another mod's patch on what the combat mirror reproduces: its patch must run, so the game's walk does.</summary>
    private static bool CombatWalkPatched() =>
        PatchSafety.IsPatchedByOthers(_combatIterate) || PatchSafety.IsPatchedByOthers(_combatBody) || PatchSafety.IsPatchedByOthers(_combatContains);

    private static bool RunWalkPatched() =>
        PatchSafety.IsPatchedByOthers(_runIterate) || PatchSafety.IsPatchedByOthers(_runBody) || PatchSafety.IsPatchedByOthers(_runContains);

    private static bool CombatPrefix(CombatState __instance, ref IEnumerable<AbstractModel> __result, bool __runOriginal)
    {
        if (!__runOriginal || !Enabled || CombatWalkPatched())
        {
            return __runOriginal;
        }
        __result = Combat(__instance, null);
#if BENCH
        CombatWalks++;
#endif
        return false;
    }

    private static bool RunPrefix(RunState __instance, ICombatState? childCombatState, ref IEnumerable<AbstractModel> __result, bool __runOriginal)
    {
        PileFilter? filter = PendingRunFilter;
        PendingRunFilter = null;
        if (!__runOriginal || RunWalkPatched())
        {
            return __runOriginal;
        }
        if (!Enabled)
        {
            return true;
        }
#if BENCH
        RunWalks++;
#endif
        __result = Run(__instance, childCombatState, filter);
        return false;
    }

    /// <summary>Combat listeners as vanilla yields them, minus the pile entries the filter drops. Option off: the game's own walk, none of this class's lists.</summary>
    internal static IEnumerable<AbstractModel> Filtered(CombatState state, PileFilter filter) =>
        CombatMirrored && Enabled && !CombatWalkPatched() ? Combat(state, filter) : KeptOnly(state.IterateHookListeners(), filter);

    /// <summary>Run (and child combat) listeners as vanilla yields them, minus the pile entries the filter drops.</summary>
    internal static IEnumerable<AbstractModel> Filtered(RunState run, ICombatState? child, PileFilter filter) =>
        RunMirrored && Enabled && !RunWalkPatched() ? Run(run, child, filter) : KeptOnly(run.IterateHookListeners(child), filter);

    /// <summary>
    /// The game's own walk, whatever it became, minus what a filter drops: cards, enchantments and afflictions (wherever they
    /// come from) whose type keeps the defaults of the hook. No mirror, so nothing here depends on the walk's code.
    /// </summary>
    private static IEnumerable<AbstractModel> KeptOnly(IEnumerable<AbstractModel> walk, PileFilter filter)
    {
        foreach (AbstractModel model in walk)
        {
            if (ReferenceEquals(model.GetType(), LimitlessHookSkip.Multi))
            {
                // No snapshot records its inner enchantments on this path, whatever the tracking.
                NoteMulti(model);
                filter.SyncMulti();
            }
            if (model is not (CardModel or EnchantmentModel or AfflictionModel) || filter.Keeps(model))
            {
                yield return model;
            }
        }
    }

    private static List<AbstractModel> Rent()
    {
        _pool ??= new Stack<List<AbstractModel>>();
        return _pool.Count > 0 ? _pool.Pop() : new List<AbstractModel>(1024);
    }

    private static void Return(List<AbstractModel> list)
    {
        list.Clear();
        // A walk can end on another thread than the one it started on (a hook awaiting between two entries).
        _pool ??= new Stack<List<AbstractModel>>();
        if (_pool.Count < 8)
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
                AddReacting(list, creature.Powers, filter);
                Player? player = creature.Player;
                if (player == null)
                {
                    if (filter == null || !KindSkipEnabled || filter.Keeps(creature.Monster!))
                    {
                        list.Add(creature.Monster!);
                    }
                    continue;
                }
                if (!player.IsActiveForHooks)
                {
                    continue;
                }
                if (filter != null && KindSkipEnabled)
                {
                    if (!filter.Skips(ListenerKinds.Relics))
                    {
                        filter.AddRelics(list, player);
                    }
                }
                else
                {
                    IReadOnlyList<RelicModel> relics = player.Relics;
                    for (int j = 0; j < relics.Count; j++)
                    {
                        if (!relics[j].IsMelted)
                        {
                            list.Add(relics[j]);
                        }
                    }
                }
                if (filter == null || !filter.Skips(ListenerKinds.Potions))
                {
                    IReadOnlyList<PotionModel?> potionSlots = player.PotionSlots;
                    for (int k = 0; k < potionSlots.Count; k++)
                    {
                        if (potionSlots[k] != null && (filter == null || !KindSkipEnabled || filter.Keeps(potionSlots[k]!)))
                        {
                            list.Add(potionSlots[k]!);
                        }
                    }
                }
                if (player.PlayerCombatState == null)
                {
                    continue;
                }
                if (filter == null || !filter.Skips(ListenerKinds.Orbs))
                {
                    AddReacting(list, player.PlayerCombatState.OrbQueue.Orbs, filter);
                }
                IReadOnlyList<CardPile> allPiles = player.PlayerCombatState.AllPiles;
                if (filter != null && GuardedPiles(list, filter, player.PlayerCombatState, allPiles))
                {
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
            AddReacting(list, state.Modifiers, filter);
            AddReacting(list, state.BadgeModels, filter);
            if (state.MultiplayerScalingModel != null && (filter == null || !KindSkipEnabled || filter.Keeps(state.MultiplayerScalingModel)))
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
        var snapshot = _combatSubscribers == null || PatchSafety.IsPatchedByOthers(_subscribersMethod) || PatchSafety.IsPatchedByOthers(_subscribersBody) ? null : SubscriberSnapshot();
        if (snapshot == null)
        {
            foreach (AbstractModel item in ModHelper.IterateAllCombatStateSubscribers(state))
            {
                yield return item;
            }
            yield break;
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

    /// <summary>
    /// The (id, delegate) of each mod subscriber, rebuilt when the list grows; null when it can't be read (ModHelper's own
    /// walk then, for the rest of the session: this runs inside walks, often in async hooks, where an exception would
    /// only fault the hook's task).
    /// </summary>
    private static (string Id, CombatHookSubscriptionDelegate Del)[]? SubscriberSnapshot()
    {
        try
        {
            var snapshot = _subscriberSnapshot;
            System.Collections.IList subscribers = _combatSubscribers!;
            if (snapshot.Length != subscribers.Count)
            {
                snapshot = new (string, CombatHookSubscriptionDelegate)[subscribers.Count];
                for (int i = 0; i < snapshot.Length; i++)
                {
                    object subscriber = subscribers[i]!;
                    snapshot[i] = ((string)_subscriberId!.GetValue(subscriber)!, (CombatHookSubscriptionDelegate)_subscriberDelegate!.GetValue(subscriber)!);
                }
                _subscriberSnapshot = snapshot;
            }
            return snapshot;
        }
        catch (Exception e)
        {
            _combatSubscribers = null;
            Log.Error($"[{ModEntry.Id}] {Name}: mod subscribers unreadable, ModHelper's own walk from now on: {e}");
            return null;
        }
    }

    private static bool CardModifiersEmpty(string id, Delegate del)
    {
        if (id != CardModifiersId || !Enabled)
        {
            return false;
        }
        // Inside walks (often in async hooks): a method that can't be fingerprinted is simply not known as empty.
        if (!_emptyModifierLists.GetOrAdd(del.Method, m => { try { return MethodFingerprint.Of(m) == CardModifiersFingerprint; } catch { return false; } }))
        {
            return false;
        }
        _noCardModifierTypes ??= !BaseLibTypes.AnyDefined(CardModifierType);
        return _noCardModifierTypes.Value;
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
                if (filter != null && GuardedDeck(list, filter, player.Deck))
                {
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
                        if (filter != null && KindSkipEnabled)
                        {
                            if (!filter.Skips(ListenerKinds.Relics))
                            {
                                filter.AddRelics(list, player);
                            }
                        }
                        else
                        {
                            foreach (RelicModel relic in player.Relics)
                            {
                                if (!relic.IsMelted)
                                {
                                    list.Add(relic);
                                }
                            }
                        }
                        if (filter == null)
                        {
                            list.AddRange(player.Potions);
                        }
                        else if (!filter.Skips(ListenerKinds.Potions))
                        {
                            foreach (PotionModel potion in player.Potions)
                            {
                                if (!KindSkipEnabled || filter.Keeps(potion))
                                {
                                    list.Add(potion);
                                }
                            }
                        }
                    }
                }
                AddReacting(list, run.Modifiers, filter);
                AddReacting(list, run.BadgeModels, filter);
                if (filter == null || !KindSkipEnabled || filter.Keeps(run.MultiplayerScalingModel))
                {
                    list.Add(run.MultiplayerScalingModel);
                }
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
            ? Filtered(combat, filter)
            : childCombatState.IterateHookListeners();
        foreach (AbstractModel item in child)
        {
            yield return item;
        }
    }

#if BENCH
    /// <summary>Test: the next guarded add throws after adding its entries (bdperf fault piles).</summary>
    internal static bool FailNextPileWalk;
#endif

    /// <summary>
    /// Runs this mod's own part of a filtered walk (the pile entry cache): on an exception, FastListeners turns off (and
    /// HookSkip with it) and the walk adds every pile entry instead, as vanilla: a superset of the filtered entries whose
    /// extra ones keep the no-op defaults. So even a hook running in an async state machine, where an exception would
    /// only fault the hook's task and never reach <see cref="PatchGuard"/>, completes like vanilla and later hooks run vanilla.
    /// </summary>
    private static bool GuardedPiles(List<AbstractModel> list, PileFilter filter, object key, IReadOnlyList<CardPile> piles)
    {
        int count = list.Count;
        try
        {
            filter.AddPiles(list, key, piles);
#if BENCH
            if (FailNextPileWalk)
            {
                FailNextPileWalk = false;
                throw new InvalidOperationException("simulated error in the pile entry cache (bdperf fault piles)");
            }
#endif
            return true;
        }
        catch (Exception e)
        {
            list.RemoveRange(count, list.Count - count);
            Disable(e);
            return false;
        }
    }

    /// <summary>As <see cref="GuardedPiles"/>, for a deck.</summary>
    private static bool GuardedDeck(List<AbstractModel> list, PileFilter filter, CardPile deck)
    {
        int count = list.Count;
        try
        {
            filter.AddDeck(list, deck);
#if BENCH
            if (FailNextPileWalk)
            {
                FailNextPileWalk = false;
                throw new InvalidOperationException("simulated error in the pile entry cache (bdperf fault piles)");
            }
#endif
            return true;
        }
        catch (Exception e)
        {
            list.RemoveRange(count, list.Count - count);
            Disable(e);
            return false;
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
        // HookSkip walks through FastListeners: its option lists this fault too.
        Config.Settings.ApplyAll();
    }
}
