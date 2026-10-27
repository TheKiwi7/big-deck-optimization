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
    private const string CombatIterateFingerprint = "58F9C719E3433DE5";
    private const string RunIterateFingerprint = "3E3597C01EA54DE8";
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
        /// Filters keeping ExecutionFinished subscribers also depend on who subscribes, which changes through the
        /// methods that bump <see cref="SubscriptionVersion"/>; without that tracking they walk the shared
        /// <see cref="PileSnapshot"/> and check subscribers live.
        /// </summary>
        private static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThread;

        /// <summary>Snapshots and segments are usable: main thread, and not inside a shuffle (see <see cref="_shuffling"/>).</summary>
        private static bool CanCache => OnMainThread && _shuffling == 0;

        private sealed class Segment
        {
            public long Version = -1;
            public long Subscriptions = -1;
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
        /// The kept entries, from this filter's segment for these piles. After cards were only appended (see
        /// <see cref="PileSnapshot.Of"/>), the segment is extended with the snapshot's new entries; after anything else
        /// it is rebuilt from the snapshot.
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
            Segment segment = _segments.GetValue(key, _ => new Segment());
            bool subscriptionsSame = !_keepSubscribed || segment.Subscriptions == SubscriptionVersion;
            if (segment.Version != PileVersion || !subscriptionsSame)
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
            }
            else if (_keepSubscribed && Environment.TickCount64 >= _nextSubscriberCheck)
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
            for (int i = from; i < snapshot.Count; i++)
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
    /// tests an array per type id instead of looking each entry's type up. When the only changes since are cards
    /// appended to the last of its piles (cloning cards into the deck), the new entries are appended instead: the
    /// entries before them stay the same (same <see cref="Generation"/>). Main thread only.
    /// </summary>
    private sealed class PileSnapshot
    {
        public long Version = -1;
        /// <summary>Bumped on each full rebuild: within a generation, entries are only ever appended.</summary>
        public long Generation;
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
            if (snapshot.CatchUp(piles, withAfflictions))
            {
#if BENCH
                SnapshotCatchUps++;
#endif
                return snapshot;
            }
            int previous = snapshot.Count;
            snapshot.Count = 0;
            for (int l = 0; l < piles.Count; l++)
            {
                IReadOnlyList<CardModel> cards = piles[l].Cards;
                for (int m = 0; m < cards.Count; m++)
                {
                    snapshot.AddCard(cards[m], withAfflictions);
                }
            }
            if (previous > snapshot.Count)
            {
                // Don't keep cards that left the piles alive.
                Array.Clear(snapshot.Entries, snapshot.Count, previous - snapshot.Count);
            }
            snapshot.Version = PileVersion;
            snapshot.Generation++;
            return snapshot;
        }

        /// <summary>
        /// Appends the cards added since <see cref="Version"/> when every pile change since then is in
        /// <see cref="_appends"/> (nothing else bumped the version) and touched only the last pile or piles outside
        /// this snapshot. Each appended card is still at its index: only appends followed.
        /// </summary>
        private bool CatchUp(IReadOnlyList<CardPile> piles, bool withAfflictions)
        {
            int first = AppendsSince(Version);
            if (first < 0 || piles.Count == 0)
            {
                return false;
            }
            CardPile last = piles[piles.Count - 1];
            for (int i = first; i < _appends.Count; i++)
            {
                CardPile pile = _appends[i].Pile;
                if (pile == last)
                {
                    continue;
                }
                for (int l = 0; l < piles.Count - 1; l++)
                {
                    if (piles[l] == pile)
                    {
                        return false;
                    }
                }
            }
            IReadOnlyList<CardModel> cards = last.Cards;
            for (int i = first; i < _appends.Count; i++)
            {
                if (_appends[i].Pile == last)
                {
                    AddCard(cards[_appends[i].Index], withAfflictions);
                }
            }
            Version = PileVersion;
            return true;
        }

        private void AddCard(CardModel card, bool withAfflictions)
        {
            Add(card);
            if (withAfflictions && card.Affliction != null)
            {
                Add(card.Affliction);
            }
            if (card.Enchantment != null)
            {
                Add(card.Enchantment);
            }
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

    /// <summary>
    /// Bumped before and after every change to a pile's cards or order, and every enchantment/affliction change of a
    /// card in a pile (a card in none, like a fresh clone, is in no snapshot).
    /// </summary>
    internal static long PileVersion;

    /// <summary>Bumped before and after every ExecutionFinished subscription change (see <see cref="_subscriptionsTracked"/>).</summary>
    internal static long SubscriptionVersion;

    /// <summary>
    /// ExecutionFinished changes on a model through its add/remove accessors only (AfterCloned clears it on a fresh
    /// clone, in no pile yet): both are patched, so filters keeping subscribers can cache their segments.
    /// </summary>
    private static bool _subscriptionsTracked;

    /// <summary>A cached hit of the filters keeping subscribers is checked live at most this often (every build, main thread).</summary>
    private const long SubscriptionCheckMs = 2000;
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
    /// A card appended at the end of a pile: the version went from <see cref="From"/> to From + 2 (the bumps around
    /// <c>AddInternal</c>) and nothing else changed meanwhile.
    /// </summary>
    private readonly record struct Append(long From, CardPile Pile, int Index);

    /// <summary>
    /// The pile changes since the last other change, when all of them were appends: consecutive versions, the last
    /// one reaching <see cref="PileVersion"/> unless something else changed since. Main thread only.
    /// </summary>
    private static readonly List<Append> _appends = new();
    private const int MaxAppends = 4096;

#if BENCH
    /// <summary>One cache hit in this many is compared with a fresh walk (Debug builds).</summary>
    internal static int CacheVerifyEvery = 256;
    /// <summary>Every cached walk is compared with a fresh walk (tests).</summary>
    internal static bool VerifyAll;
    public static long CacheMismatches { get; private set; }
    /// <summary>Pile segments: rebuilt, extended after appends, served from cache, walked uncached (filters keeping
    /// subscribers, without subscription tracking); snapshots caught up after appends; and how many distinct pile
    /// versions needed a rebuild.</summary>
    public static long SegmentRebuilds, SegmentExtends, SegmentHits, UncachedWalks, SnapshotCatchUps, RebuiltVersions;
    private static long _lastRebuildVersion = -1;
#endif

    /// <summary>Index of the first append from <paramref name="version"/> on, if the appends lead from it to now; else -1.</summary>
    private static int AppendsSince(long version)
    {
        int count = _appends.Count;
        if (count == 0 || _appends[count - 1].From + 2 != PileVersion)
        {
            return -1;
        }
        long offset = version - _appends[0].From;
        if (offset < 0 || offset % 2 != 0 || offset / 2 >= count)
        {
            return -1;
        }
        int index = (int)(offset / 2);
        return _appends[index].From == version ? index : -1;
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
        if (_appends.Count != 0 && Thread.CurrentThread.ManagedThreadId == _mainThread)
        {
            _appends.Clear(); // Can't lead to the current version any more; don't keep the piles alive.
        }
    }

    private readonly record struct AddState(long From, int Count);

    private static void BeforeAdd(CardPile __instance, CardModel card, out AddState __state)
    {
        __state = new AddState(PileVersion, __instance.Cards.Count);
        PileVersion++;
    }

    /// <summary>Records the add when it appended the card at the end and nothing else changed meanwhile.</summary>
    private static Exception? AfterAdd(Exception? __exception, CardPile __instance, CardModel card, AddState __state)
    {
        PileVersion++;
        if (Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            return __exception;
        }
        IReadOnlyList<CardModel> cards = __instance.Cards;
        if (__exception != null || PileVersion != __state.From + 2
            || cards.Count != __state.Count + 1 || !ReferenceEquals(cards[__state.Count], card))
        {
            _appends.Clear();
            return __exception;
        }
        if (_appends.Count != 0 && _appends[_appends.Count - 1].From + 2 != __state.From)
        {
            _appends.Clear();
        }
        if (_appends.Count == MaxAppends)
        {
            _appends.RemoveRange(0, MaxAppends / 2);
        }
        _appends.Add(new Append(__state.From, __instance, __state.Count));
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
            if (method == nameof(CardPile.AddInternal))
            {
                PatchGuard.Patch(harmony, target, prefix: new HarmonyMethod(typeof(FastListeners), nameof(BeforeAdd)),
                    finalizer: new HarmonyMethod(typeof(FastListeners), nameof(AfterAdd)));
            }
            else
            {
                PatchGuard.Patch(harmony, target, prefix: bumpBefore, finalizer: bumpAfter);
            }
        }
        // The pile cache is bumped by those methods only: no other code may change the pile lists.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), "RandomizeOrderInternal"),
            prefix: new HarmonyMethod(typeof(FastListeners), nameof(EnterShuffle)), finalizer: new HarmonyMethod(typeof(FastListeners), nameof(ExitShuffle)));
        foreach (string property in new[] { nameof(CardModel.Enchantment), nameof(CardModel.Affliction) })
        {
            // The setter is the only code changing it (auto-property backing field), so bumping around it is enough.
            PatchSafety.RequireFieldWriters(typeof(CardModel), $"<{property}>k__BackingField", "set_" + property);
            PatchGuard.Patch(harmony, AccessTools.PropertySetter(typeof(CardModel), property) ?? throw new MissingMethodException(nameof(CardModel), property),
                prefix: new HarmonyMethod(typeof(FastListeners), nameof(BumpIfPiled)),
                finalizer: new HarmonyMethod(typeof(FastListeners), nameof(BumpIfPiledFinally)));
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
        try
        {
            PatchSafety.RequireFingerprint(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(ModHelper), nameof(ModHelper.IterateAllCombatStateSubscribers))), SubscribersFingerprint);
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
        // After ListenerCache: inside its scopes it answers from its cache and this does nothing.
        PatchGuard.Patch(harmony, combatIterate, prefix: new HarmonyMethod(typeof(FastListeners), nameof(CombatPrefix)) { priority = Priority.Last });
        PatchGuard.Patch(harmony, runIterate, prefix: new HarmonyMethod(typeof(FastListeners), nameof(RunPrefix)) { priority = Priority.Last });
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
                if (filter != null && Guarded(list, () => filter.AddPiles(list, player.PlayerCombatState, allPiles)))
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
        var snapshot = _combatSubscribers == null ? null : SubscriberSnapshot();
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
                if (filter != null && Guarded(list, () => filter.AddDeck(list, player.Deck)))
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

    /// <summary>
    /// Runs this mod's own part of a filtered walk (the pile entry cache): on an exception, FastListeners turns off (and
    /// HookSkip with it) and the walk adds every pile entry instead, as vanilla: a superset of the filtered entries whose
    /// extra ones keep the no-op defaults. So even a hook running in an async state machine, where an exception would
    /// only fault the hook's task and never reach <see cref="PatchGuard"/>, completes like vanilla and later hooks run vanilla.
    /// </summary>
#if BENCH
    /// <summary>Test: the next guarded add throws after adding its entries (bdperf fault piles).</summary>
    internal static bool FailNextPileWalk;
#endif

    private static bool Guarded(List<AbstractModel> list, Action add)
    {
        int count = list.Count;
        try
        {
            add();
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
