using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <c>CombatState.ContainsCard</c> is <c>_allCards.Contains(card)</c>: a scan of every card of the combat, every player's. The
/// game asks it for each card <c>CardPileCmd.Add</c> moves (its check that a combat card belongs to the combat), so a reshuffle,
/// which moves the whole discard and draw piles, scans the combat's cards once per card: 60-80 ms of a ~290 ms reshuffle frame at
/// 10,300 cards solo, four times that with four players.
///
/// The answer comes from a count per card of the combat's list (<see cref="Set"/>, one per combat), built from <c>_allCards</c>
/// at its first answer and kept by the list's two writers (the private <c>AddCard(CardModel)</c>, which the public overload and
/// <c>CloneCard</c>/<c>CreateCard</c> go through, and <c>RemoveCard</c>; <c>RequireFieldWriters</c>). Exact because the set is
/// only read while it is the list's: it records the list itself, its own version (<c>List&lt;T&gt;._version</c>, which every change
/// of the list bumps) and count when it was last brought in step, and an answer first checks the three (constant time). A
/// writer's call brings it along only when its original surely ran to its end (<c>__runOriginal</c>, no exception) and the list
/// moved by exactly that call: version +1, and the card appended last (<c>AddCard</c>) or one element fewer with the card held
/// (<c>RemoveCard</c>), the set in step before it. Anything else (a nested or unseen change, another mod's code writing the list
/// by reflection: a snapshot restore clearing and refilling it) leaves the set behind its list.
///
/// A set behind its list gets the game's scan for <see cref="ScansBeforeRebuild"/> answers, then is rebuilt from the list. A
/// rebuild costs several of the game's scans (each card hashed into the set), so a list changing between answers would cost
/// more than the game: after <see cref="MaxRebuilds"/> rebuilds in a combat, that combat keeps the game's scan to its end. A
/// set built before a back-off period (another mod's patch this class can't stand next to) is rebuilt after it, never trusted
/// again: the writers' calls made meanwhile are not counted.
///
/// <c>List.Contains</c> compares with each element's <c>Equals</c>: the set answers by reference, which is the same while every
/// card of the list has a type keeping <c>object.Equals</c> (CardModel and AbstractModel do) or overriding it with only
/// <c>this == obj</c> (<see cref="CardEquality"/>): other overrides are counted per set, and any one leaves the answers to the
/// game. A set behind its list while it counts one is not rebuilt (no rebuild could answer while that card is there): the
/// game's scan for the rest of that combat, unless the card is a <c>this == obj</c> one whose other mod's patch goes away. Main
/// thread only. One answer in <see cref="VerifyEvery"/> is compared with the game's scan; a difference turns this off for the
/// session.
/// </summary>
public static class CombatCardSet
{
    public const string Name = "CombatCardSet";

    /// <summary><c>CombatState.ContainsCard</c>, game v0.111.1 and v0.113.1 (the same code): replaced.</summary>
    private const string ContainsCardFingerprint = "FA7C980E1C384DC0";
    /// <summary>The private <c>CombatState.AddCard(CardModel)</c>, v0.111.1 and v0.113.1: its <c>_allCards.Add(card)</c> is counted.</summary>
    private const string AddCardFingerprint = "66CEDAA9C9DA62CC";
    /// <summary><c>CombatState.RemoveCard</c>, v0.111.1 and v0.113.1: its <c>_allCards.Remove(card)</c> is counted.</summary>
    private const string RemoveCardFingerprint = "5F1AE5EB3537ABDF";

    /// <summary>The game's scans answered while a set is behind its list, before it is rebuilt.</summary>
    private const int ScansBeforeRebuild = 4;
    /// <summary>Rebuilds of one combat's set (its first build aside) after which that combat keeps the game's scan.</summary>
    private const int MaxRebuilds = 16;

#if BENCH
    internal static int VerifyEvery { get; set; } = 16;
#else
    internal static int VerifyEvery { get; set; } = 256;
#endif

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    /// <summary>Answers given here (the effect counter), the game's scans run instead, sets (re)built, answers compared.</summary>
    public static long Answered { get; private set; }
    public static long Fallbacks { get; private set; }
    public static long Builds { get; private set; }
    public static long Checks { get; private set; }
    /// <summary>Combats whose set was given up after <see cref="MaxRebuilds"/> rebuilds.</summary>
    public static long GaveUp { get; private set; }

    private sealed class Set
    {
        /// <summary>How many times each card is in the list (by reference; the list may hold a card twice: AddCard doesn't check).</summary>
        public readonly Dictionary<CardModel, int> Counts = new(ReferenceEqualityComparer.Instance);
        /// <summary>The list, its count and version when the set was last in step with it (<see cref="Built"/>).</summary>
        public List<CardModel>? List;
        public int Total;
        public int Version;
        public bool Built;
        /// <summary>The back-off generation the set was built in: another one means it missed changes (<see cref="_generation"/>).</summary>
        public int Generation;
        /// <summary>Cards of the list whose type's <c>Equals</c> may not answer by reference: while any, the game's scan.</summary>
        public int Overriders;
        /// <summary>Cards of the list whose type overrides <c>Equals</c> with <c>this == obj</c>: by reference while that code is unpatched.</summary>
        public int ReferenceOverriders;
        /// <summary>The game's scans answered since the set fell behind its list, and its rebuilds after the first build.</summary>
        public int BehindScans;
        public int Rebuilds;
        public bool GaveUp;
    }

    private static readonly ConditionalWeakTable<CombatState, Set> _sets = new();
    private static AccessTools.FieldRef<CombatState, List<CardModel>>? _allCards;
    private static AccessTools.FieldRef<List<CardModel>, int>? _listVersion;
    private static MethodBase? _contains, _add, _remove;
    private static int _mainThread = -1;

    public static void Apply(Harmony harmony)
    {
        _contains = Reflect.DeclaredMethod(typeof(CombatState), nameof(CombatState.ContainsCard), new[] { typeof(CardModel) })
            ?? throw new MissingMethodException(nameof(CombatState), nameof(CombatState.ContainsCard));
        _add = Reflect.DeclaredMethod(typeof(CombatState), "AddCard", new[] { typeof(CardModel) })
            ?? throw new MissingMethodException(nameof(CombatState), "AddCard(CardModel)");
        _remove = Reflect.DeclaredMethod(typeof(CombatState), nameof(CombatState.RemoveCard), new[] { typeof(CardModel) })
            ?? throw new MissingMethodException(nameof(CombatState), nameof(CombatState.RemoveCard));
        PatchSafety.RequireFingerprint(_contains, ContainsCardFingerprint);
        PatchSafety.RequireFingerprint(_add, AddCardFingerprint);
        PatchSafety.RequireFingerprint(_remove, RemoveCardFingerprint);
        // The two writers counted here and the field's initializer: no other game code changes the list.
        PatchSafety.RequireFieldWriters(typeof(CombatState), "_allCards", ".ctor", "AddCard", "RemoveCard");
        _allCards = Reflect.FieldRefAccess<CombatState, List<CardModel>>("_allCards");
        // The runtime's own List<T>: every Add, Remove, Clear, Insert, indexer write... bumps it.
        _listVersion = Reflect.FieldRefAccess<List<CardModel>, int>("_version");
        _mainThread = Environment.CurrentManagedThreadId;

        // Writers first, so no answer comes from a set that missed a change (the version check would catch it anyway).
        // A first prefix that always runs (instance and __state only) takes the list's version and count; the finalizer (run
        // after a throw or a skipped original too) brings the set along when the original ran and moved the list by that call only.
        PatchGuard.Patch(harmony, _add, prefix: new HarmonyMethod(typeof(CombatCardSet), nameof(WatchAdd)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(CombatCardSet), nameof(AfterAdd)));
        PatchGuard.Patch(harmony, _remove, prefix: new HarmonyMethod(typeof(CombatCardSet), nameof(WatchRemove)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(CombatCardSet), nameof(AfterRemove)));
        PatchGuard.Patch(harmony, _contains, prefix: new HarmonyMethod(typeof(CombatCardSet), nameof(ContainsPrefix)));
        Applied = true;
        // What Backoff asks about (display only).
        PatchSafety.Watch(Name, _contains, PatchTolerance.BeforeReplace);
        PatchSafety.Watch(Name, new[] { _add, _remove }, PatchTolerance.SurroundingOnly);
    }

    private static int _checkCalls;
    private static int _epoch = int.MinValue;
    private static bool _backoff = true;
    /// <summary>Bumped at each back-off seen: a set built in an older generation is rebuilt (writers weren't counted meanwhile).</summary>
    private static int _generation;

    /// <summary>
    /// Another mod's patch the set can't stand next to, rechecked every 1,024 answers and at once after any Patch or Unpatch
    /// (<see cref="HarmonyWatch.Epoch"/>):
    /// <list type="bullet">
    /// <item><c>ContainsCard</c>, which <see cref="ContainsPrefix"/> answers for: <see cref="PatchTolerance.BeforeReplace"/> (prefixes
    /// Harmony runs before it, one answering skips it as it skips the original; postfixes by value).</item>
    /// <item><c>AddCard(CardModel)</c> and <c>RemoveCard</c>: <see cref="PatchTolerance.SurroundingOnly"/>, so the original surely runs
    /// (no prefix of another mod can skip it): void prefixes (one rewriting the card by reference writes the slot the finalizer
    /// reads) and postfixes by value (RitsuLib's and YuWanCard's identity registrations on both AddCard overloads) only run around
    /// the list's change; one changing the list itself makes the version move by more than the call's own change, the set left
    /// behind. Bool prefixes (one skipping the call could change the list as the call would, or not), transpilers (the body could
    /// insert at the front or remove another card) and finalizers back off; the finalizer also checks <c>__runOriginal</c>.</item>
    /// </list>
    /// </summary>
    private static bool Backoff()
    {
        if (--_checkCalls < 0 || _epoch != HarmonyWatch.Epoch)
        {
            _checkCalls = 1023;
            _epoch = HarmonyWatch.Epoch;
            bool backoff = PatchSafety.IsPatchedByOthers(_contains, PatchTolerance.BeforeReplace)
                | PatchSafety.IsPatchedByOthers(_add, PatchTolerance.SurroundingOnly)
                | PatchSafety.IsPatchedByOthers(_remove, PatchTolerance.SurroundingOnly);
            if (backoff)
            {
                _generation++;
            }
            _backoff = backoff;
        }
        return _backoff;
    }

    /// <summary>
    /// The writers' calls are counted only while the last check found no back-off and no Patch or Unpatch came since (else the
    /// set falls behind, rebuilt once the back-off ends, <see cref="Set.Generation"/>).
    /// </summary>
    private static bool Counting() => !_backoff && _epoch == HarmonyWatch.Epoch;

    /// <summary>The list's version and count before a writer's call.</summary>
    private readonly record struct Before(int Version, int Count, bool Known);

    [OrderFreePrefix]
    private static void WatchAdd(CombatState __instance, out Before __state) => __state = Take(__instance);

    [OrderFreePrefix]
    private static void WatchRemove(CombatState __instance, out Before __state) => __state = Take(__instance);

    private static Before Take(CombatState combat)
    {
        List<CardModel>? list = _allCards!(combat);
        return list == null ? default : new Before(_listVersion!(list), list.Count, true);
    }

    /// <summary>
    /// <c>_allCards.Add(card)</c> appends: counted when the original ran to its end, the list moved by exactly one change since the
    /// prefix, its last element is the call's card, and the set was in step then. Anything else leaves the set behind its list.
    /// Checked even while switched off: the set stays in step for when it is on again.
    /// </summary>
    private static void AfterAdd(CombatState __instance, CardModel card, Before __state, bool __runOriginal, Exception? __exception)
    {
        try
        {
            if (!__runOriginal || __exception != null || !__state.Known || !Counting() || Environment.CurrentManagedThreadId != _mainThread
                || !_sets.TryGetValue(__instance, out Set? set) || !InStep(set, __state))
            {
                return;
            }
            List<CardModel> list = _allCards!(__instance);
            int version = _listVersion!(list);
            if (!ReferenceEquals(list, set.List) || version != unchecked(__state.Version + 1) || list.Count != __state.Count + 1
                || !ReferenceEquals(list[list.Count - 1], card))
            {
                return;
            }
            Count(set, card, +1);
            set.Total = list.Count;
            set.Version = version;
        }
        catch (Exception e)
        {
            Fail("add count failed: " + e);
        }
    }

    /// <summary>
    /// <c>_allCards.Remove(card)</c> removes the first element equal to the card: with every type of the list answering by
    /// reference, that card itself. Counted when the original ran to its end, the list lost one element by exactly one change
    /// since the prefix, and the set was in step then and held the card; else the set is left behind (rebuilt later).
    /// </summary>
    private static void AfterRemove(CombatState __instance, CardModel card, Before __state, bool __runOriginal, Exception? __exception)
    {
        try
        {
            if (!__runOriginal || __exception != null || !__state.Known || !Counting() || Environment.CurrentManagedThreadId != _mainThread
                || !_sets.TryGetValue(__instance, out Set? set) || !InStep(set, __state))
            {
                return;
            }
            List<CardModel> list = _allCards!(__instance);
            int version = _listVersion!(list);
            if (!ReferenceEquals(list, set.List) || version != unchecked(__state.Version + 1) || list.Count != __state.Count - 1
                || !ByReference(set) || card == null || !set.Counts.ContainsKey(card))
            {
                return;
            }
            Count(set, card, -1);
            set.Total = list.Count;
            set.Version = version;
        }
        catch (Exception e)
        {
            Fail("remove count failed: " + e);
        }
    }

    /// <summary>The set was the list's when the writer's call began.</summary>
    private static bool InStep(Set set, Before before) =>
        set.Built && !set.GaveUp && set.Generation == _generation && set.Version == before.Version && set.Total == before.Count;

    /// <summary>Every card of the set answers <c>Equals</c> by reference now.</summary>
    private static bool ByReference(Set set) => set.Overriders == 0 && (set.ReferenceOverriders == 0 || !CardEquality.ReferenceOverridePatched());

    private static void Count(Set set, CardModel? card, int delta)
    {
        if (card == null)
        {
            return; // List.Contains(card) never matches a null element for a card that isn't null (the only ones answered here).
        }
        switch (CardEquality.Of(card.GetType()))
        {
            case CardEquality.Kind.Other:
                set.Overriders += delta;
                break;
            case CardEquality.Kind.ReferenceOverride:
                set.ReferenceOverriders += delta;
                break;
        }
        ref int count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(set.Counts, card, out _);
        count += delta;
        if (count <= 0)
        {
            set.Counts.Remove(card);
        }
    }

    /// <summary>The set brought in step with the list from scratch.</summary>
    private static void Build(Set set, List<CardModel> list)
    {
        set.Counts.Clear();
        set.Overriders = 0;
        set.ReferenceOverriders = 0;
        for (int i = 0; i < list.Count; i++)
        {
            Count(set, list[i], +1);
        }
        set.List = list;
        set.Total = list.Count;
        set.Version = _listVersion!(list);
        set.Generation = _generation;
        set.BehindScans = 0;
        if (set.Built)
        {
            set.Rebuilds++;
        }
        set.Built = true;
        Builds++;
    }

    private static bool ContainsPrefix(CombatState __instance, CardModel card, ref bool __result)
    {
        // Inside ListenerCache.RunVanilla the answer is the reference others are compared with: the game's own.
        if (!Enabled || card == null || ListenerCache.IsSuspended || Environment.CurrentManagedThreadId != _mainThread)
        {
            return true;
        }
        try
        {
            // Inside the try: the back-off check reads Harmony's patch lists, which a fault there must not throw into the game.
            if (Backoff())
            {
                return true;
            }
            List<CardModel>? list = _allCards!(__instance);
            if (list == null)
            {
                return true; // The game's own NullReferenceException.
            }
            Set set = _sets.GetOrCreateValue(__instance);
            if (set.GaveUp)
            {
                Fallbacks++;
                return true;
            }
            if (!set.Built || !ReferenceEquals(set.List, list) || set.Version != _listVersion!(list) || set.Total != list.Count || set.Generation != _generation)
            {
                if (set.Built && !ByReference(set))
                {
                    // A card not answering Equals by reference held the list when the set was last in step: every RemoveCard
                    // since left the set behind (AfterRemove can't count one then), and a rebuild couldn't answer either while
                    // it is there. The game's scan, no rebuild (one per few answers would cost more than the scans).
                    Fallbacks++;
                    return true;
                }
                if (set.Built && set.BehindScans++ < ScansBeforeRebuild)
                {
                    Fallbacks++;
                    return true;
                }
                if (set.Built && set.Rebuilds >= MaxRebuilds)
                {
                    // A list that keeps changing out of step: a rebuild per few answers would cost more than the game's scans.
                    set.GaveUp = true;
                    set.Counts.Clear();
                    GaveUp++;
                    Fallbacks++;
                    return true;
                }
                Build(set, list);
            }
            if (!ByReference(set))
            {
                Fallbacks++;
                return true;
            }
            bool answer = set.Counts.ContainsKey(card);
            Answered++;
#if BENCH
            // Test: an answer made wrong, checked against the game's scan now.
            bool wrong = TestWrong.Take("combatcards");
            if (wrong)
            {
                answer = !answer;
            }
            if (Answered % VerifyEvery == 0 || wrong)
#else
            if (Answered % VerifyEvery == 0)
#endif
            {
                Checks++;
                bool game = list.Contains(card);
                if (game != answer)
                {
                    Fail($"{card} answered {answer}, the combat's list {game}");
                    __result = game;
                    return false;
                }
            }
            __result = answer;
            return false;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
            return true;
        }
    }

    private static bool _failed;

    private static void Fail(string reason)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        if (_failed)
        {
            return;
        }
        _failed = true;
        Log.Error($"[{ModEntry.Id}] {Name} disabled, falling back to vanilla: {reason}");
    }

#if BENCH
    /// <summary>Bench: this combat's answer for the card here (null when the game's scan would run), counted as no effect.</summary>
    internal static bool? AnswerForTest(CombatState combat, CardModel card)
    {
        bool result = false;
        // Every counter put back (the self-test's scratch combats build and give up sets of their own): only the game's calls count.
        long answered = Answered, fallbacks = Fallbacks, checks = Checks, builds = Builds, gaveUp = GaveUp;
        bool answeredHere;
        try
        {
            answeredHere = !ContainsPrefix(combat, card, ref result);
        }
        finally
        {
            Answered = answered;
            Fallbacks = fallbacks;
            Checks = checks;
            Builds = builds;
            GaveUp = gaveUp;
        }
        return answeredHere ? result : null;
    }

    /// <summary>
    /// Bench self-test (<c>bdperf combatcards</c>): a scratch <c>CombatState</c> (never constructed, only its list: no game code
    /// runs and nothing of the combat changes) holding the given cards, and the writers' finalizers driven as other mods' patches
    /// could make them run: a skipped <c>AddCard</c> whose prefix inserted the card at the front, a call whose body inserted at
    /// the front (a transpiler's), a nested call, a skipped <c>RemoveCard</c> whose prefix removed another card, normal calls, a
    /// list swapped for a copy. After each, every card's answer must be the list's (<c>List.Contains</c>) or the game's scan.
    /// Returns the failures (empty when none).
    /// </summary>
    internal static List<string> SelfTest(IReadOnlyList<CardModel> cards)
    {
        var failures = new List<string>();
        if (cards.Count < 8 || _allCards == null)
        {
            failures.Add($"self-test needs 8 cards and the part applied (cards {cards.Count})");
            return failures;
        }
        var combat = (CombatState)RuntimeHelpers.GetUninitializedObject(typeof(CombatState));
        List<CardModel> list = new();
        for (int i = 0; i < cards.Count - 4; i++)
        {
            list.Add(cards[i]);
        }
        _allCards(combat) = list;
        CardModel a = cards[^4], b = cards[^3], c = cards[^2], d = cards[^1];
        int answered = 0;

        void Check(string step)
        {
            List<CardModel> current = _allCards!(combat);
            foreach (CardModel card in cards)
            {
                bool? mine = AnswerForTest(combat, card);
                answered += mine == null ? 0 : 1;
                if (mine != null && mine != current.Contains(card))
                {
                    failures.Add($"{step}: {card} answered {mine}, the list {!mine}");
                    return;
                }
            }
        }

        Check("built");
        // A skipped AddCard whose prefix inserted the card at the front: version +1, count +1, but the original didn't run.
        Before before = Take(combat);
        list.Insert(0, a);
        AfterAdd(combat, a, before, __runOriginal: false, __exception: null);
        Check("skipped add, inserted at the front");
        // A body inserting at the front (what a transpiler could make it do): its card isn't the last element.
        before = Take(combat);
        list.Insert(0, b);
        AfterAdd(combat, b, before, __runOriginal: true, __exception: null);
        Check("add inserting at the front");
        // A nested add (a postfix adding another card through AddCard): the outer call's change is two appends.
        Before outer = Take(combat);
        Before inner = Take(combat);
        list.Add(c);
        AfterAdd(combat, c, inner, true, null);
        list.Add(d);
        AfterAdd(combat, d, outer, true, null);
        Check("nested add");
        // A skipped RemoveCard whose prefix removed another card.
        before = Take(combat);
        list.Remove(cards[0]);
        AfterRemove(combat, cards[1], before, __runOriginal: false, __exception: null);
        Check("skipped remove, another card removed");
        // Normal calls, counted.
        before = Take(combat);
        list.Remove(c);
        AfterRemove(combat, c, before, true, null);
        Check("remove");
        before = Take(combat);
        list.Add(c);
        AfterAdd(combat, c, before, true, null);
        Check("add");
        // The list swapped for a copy without one card (a reflection write): same version count possible, another list.
        List<CardModel> copy = new(list);
        copy.Remove(cards[2]);
        _allCards(combat) = copy;
        Check("list swapped");
        if (answered == 0)
        {
            failures.Add("the set answered nothing (back-off, or a card type whose Equals isn't by reference): nothing tested");
        }
        return failures;
    }
#endif
}

/// <summary>
/// Whether a card type's <c>Equals(object)</c> answers by reference, as <c>List.Contains</c>/<c>IndexOf</c> call it on each element
/// (CombatCardSet, PileIndex's single scan): <c>object.Equals</c> itself, or (rule of 2026-11-21) an override on a class whose IL
/// is only <c>this == obj</c> on references (<c>ldarg.0; ldarg.1; ceq; ret</c>, or the Debug compiler's
/// <c>nop; ldarg.0; ldarg.1; ceq; stloc.0; br.s next; ldloc.0; ret</c>), as Guzhenren's cards have. Such an override answers by
/// reference only while no other mod patches it (<see cref="ReferenceOverridePatched"/>, asked at each Patch or Unpatch). Any other
/// override, a hiding <c>new Equals(object)</c> (reflection finds it, <c>List</c> calls the override), or a type reflection can't
/// read: not by reference.
/// </summary>
internal static class CardEquality
{
    internal enum Kind
    {
        Reference,
        ReferenceOverride,
        Other,
    }

    private static readonly Dictionary<Type, Kind> _kinds = new();
    private static readonly List<MethodInfo> _referenceOverrides = new();
    private static int _epoch = int.MinValue;
    private static long _checkedAt;
    private static bool _patched;

    internal static Kind Of(Type type)
    {
        lock (_kinds)
        {
            if (!_kinds.TryGetValue(type, out Kind kind))
            {
                kind = Classify(type, out MethodInfo? method);
                _kinds[type] = kind;
                if (kind == Kind.ReferenceOverride && !_referenceOverrides.Contains(method!))
                {
                    _referenceOverrides.Add(method!);
                    _epoch = int.MinValue; // Asked again at the next ReferenceOverridePatched.
                }
            }
            return kind;
        }
    }

    /// <summary>
    /// Another mod patches one of the <c>this == obj</c> overrides met so far (then none of them counts as by reference). Asked
    /// again after any Patch or Unpatch (<see cref="HarmonyWatch.Epoch"/>), and each second while HarmonyWatch isn't applied (its
    /// epoch then never moves).
    /// </summary>
    internal static bool ReferenceOverridePatched()
    {
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (epoch != _epoch || (!HarmonyWatch.Applied && now - _checkedAt >= 1000))
        {
            lock (_kinds)
            {
                bool patched = false;
                foreach (MethodInfo method in _referenceOverrides)
                {
                    patched |= PatchSafety.HasOtherPatches(method);
                }
                _patched = patched;
                _epoch = epoch;
                _checkedAt = now;
            }
        }
        return _patched;
    }

    internal static Kind Classify(Type type, out MethodInfo? method)
    {
        method = null;
        try
        {
            method = type.GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
            if (method == null)
            {
                return Kind.Other;
            }
            if (method.DeclaringType == typeof(object))
            {
                return Kind.Reference;
            }
            // Only the virtual override of object.Equals is what List.Contains calls (through the default comparer). GetMethod
            // returns a hiding `new bool Equals(object)` (virtual or not) in its place: the override then runs elsewhere, unread.
            if (!method.IsVirtual || (method.Attributes & MethodAttributes.NewSlot) != 0 || method.GetBaseDefinition().DeclaringType != typeof(object))
            {
                method = null;
                return Kind.Other;
            }
            return method.DeclaringType is { IsClass: true } && !method.IsGenericMethod && method.GetMethodBody()?.GetILAsByteArray() is { } il
                && IsReferenceComparison(il) ? Kind.ReferenceOverride : Kind.Other;
        }
        catch
        {
            // Anything reflection throws (an ambiguous match, a type of a missing optional dependency in an Equals overload:
            // FileNotFound, TypeLoad...): not known to answer by reference. Never thrown on: it runs on the game's card moves.
            method = null;
            return Kind.Other;
        }
    }

    /// <summary>The IL is <c>ldarg.0; ldarg.1; ceq; ret</c>, or with a <c>bool</c> local as the Debug compiler writes it, nops anywhere.</summary>
    internal static bool IsReferenceComparison(byte[] il)
    {
        // Opcodes read: nop 00, ldarg.0 02, ldarg.1 03, ldloc.0 06, stloc.0 0A, br.s 2B xx, ret 2A, ceq FE 01. Anything else: no.
        var ops = new List<int>();
        for (int i = 0; i < il.Length;)
        {
            byte op = il[i];
            if (op == 0x00)
            {
                i++;
            }
            else if (op is 0x02 or 0x03 or 0x06 or 0x0A or 0x2A)
            {
                ops.Add(op);
                i++;
            }
            else if (op == 0xFE && i + 1 < il.Length && il[i + 1] == 0x01)
            {
                ops.Add(0xFE01);
                i += 2;
            }
            else if (op == 0x2B && i + 1 < il.Length)
            {
                // br.s: only to the next instruction (offset 0 or over nops alone).
                int target = i + 2 + (sbyte)il[i + 1];
                if (target < i + 2 || target > il.Length)
                {
                    return false;
                }
                for (int j = i + 2; j < target; j++)
                {
                    if (il[j] != 0x00)
                    {
                        return false;
                    }
                }
                ops.Add(0x2B);
                i += 2;
            }
            else
            {
                return false;
            }
        }
        return ops.Count switch
        {
            4 => ops[0] == 0x02 && ops[1] == 0x03 && ops[2] == 0xFE01 && ops[3] == 0x2A,
            7 => ops[0] == 0x02 && ops[1] == 0x03 && ops[2] == 0xFE01 && ops[3] == 0x0A && ops[4] == 0x2B && ops[5] == 0x06 && ops[6] == 0x2A,
            _ => false,
        };
    }
}
