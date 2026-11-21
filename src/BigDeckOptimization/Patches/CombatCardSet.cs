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
/// only read while it is the list's: it records the list's own version (<c>List&lt;T&gt;._version</c>, which every change of the
/// list bumps) and count when it was last brought in step, and an answer first checks both (constant time). A writer's call
/// brings it along only when the list moved by exactly that call (version +1, count ±1, the set in step before it): a call
/// another mod's prefix skipped leaves the list as it was, a nested or unseen change (another mod's code writing the list by
/// reflection: a snapshot restore clearing and refilling it) leaves the set behind, rebuilt from the list at a later answer
/// (after a few of the game's own scans, so a list changing between every answer costs no more than the game's).
///
/// <c>List.Contains</c> compares with each element's <c>Equals</c>: the set answers by reference, which is the same while no card
/// of the list has a type overriding <c>Equals(object)</c> (CardModel and AbstractModel don't): counted per set, any one leaves
/// the answers to the game. Main thread only. One answer in <see cref="VerifyEvery"/> is compared with the game's scan; a
/// difference turns this off for the session.
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

    private sealed class Set
    {
        /// <summary>How many times each card is in the list (by reference; the list may hold a card twice: AddCard doesn't check).</summary>
        public readonly Dictionary<CardModel, int> Counts = new(ReferenceEqualityComparer.Instance);
        /// <summary>The list's count and version when the set was last in step with it (<see cref="Built"/>).</summary>
        public int Total;
        public int Version;
        public bool Built;
        /// <summary>Cards of the list whose type overrides <c>Equals(object)</c>: while any, the game's scan.</summary>
        public int Overriders;
        /// <summary>The game's scans answered since the set fell behind its list.</summary>
        public int BehindScans;
    }

    private static readonly ConditionalWeakTable<CombatState, Set> _sets = new();
    private static AccessTools.FieldRef<CombatState, List<CardModel>>? _allCards;
    private static AccessTools.FieldRef<List<CardModel>, int>? _listVersion;
    private static MethodBase? _contains, _add, _remove;
    private static int _mainThread = -1;
    private static readonly Dictionary<Type, bool> _overridesEquals = new();

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
        // after a throw or a skipped original too) brings the set along when the list moved by exactly this call.
        PatchGuard.Patch(harmony, _add, prefix: new HarmonyMethod(typeof(CombatCardSet), nameof(WatchAdd)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(CombatCardSet), nameof(AfterAdd)));
        PatchGuard.Patch(harmony, _remove, prefix: new HarmonyMethod(typeof(CombatCardSet), nameof(WatchRemove)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(CombatCardSet), nameof(AfterRemove)));
        PatchGuard.Patch(harmony, _contains, prefix: new HarmonyMethod(typeof(CombatCardSet), nameof(ContainsPrefix)));
        Applied = true;
        // What Backoff asks about (display only).
        PatchSafety.Watch(Name, new[] { _contains, _add, _remove }, PatchTolerance.BeforeReplace);
    }

    private static int _checkCalls;
    private static int _epoch;
    private static bool _backoff;

    /// <summary>
    /// Another mod's patch the set can't stand next to, rechecked every 1,024 answers and at once after any Patch or Unpatch
    /// (<see cref="HarmonyWatch.Epoch"/>). <see cref="PatchTolerance.BeforeReplace"/> on the three methods:
    /// <list type="bullet">
    /// <item><c>ContainsCard</c>, which <see cref="ContainsPrefix"/> answers for: prefixes Harmony runs before it (one answering
    /// skips it as it skips the original) and postfixes by value stand.</item>
    /// <item><c>AddCard(CardModel)</c> and <c>RemoveCard</c>: this class's only prefixes there are order-free (they take the
    /// list's version and count, never skip), so every other mod's prefix runs before what this class counts. A void one rewriting
    /// the card by reference writes the slot the finalizer reads; a bool one skipping the call leaves the list's version as it
    /// was, nothing counted; postfixes (RitsuLib's and YuWanCard's identity registrations on both AddCard overloads) only run
    /// around the list's change, and one changing the list through these methods is a nested call, which leaves the outer one's
    /// count behind (the set rebuilt later). Transpilers back off: one could make the body change the list in another way than
    /// one append (an insert at the front) or one remove of that card, which the version and count alone wouldn't tell.</item>
    /// </list>
    /// </summary>
    private static bool Backoff()
    {
        if (--_checkCalls < 0 || _epoch != HarmonyWatch.Epoch)
        {
            _checkCalls = 1023;
            _epoch = HarmonyWatch.Epoch;
            _backoff = PatchSafety.IsPatchedByOthers(_contains, PatchTolerance.BeforeReplace)
                | PatchSafety.IsPatchedByOthers(_add, PatchTolerance.BeforeReplace)
                | PatchSafety.IsPatchedByOthers(_remove, PatchTolerance.BeforeReplace);
        }
        return _backoff;
    }

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
    /// <c>_allCards.Add(card)</c> appends: when the list moved by exactly one append since the prefix and the set was in step
    /// then, the appended element (the list's last, whatever card a patch let through) is counted. Anything else leaves the set
    /// behind its list (rebuilt at a later answer). Checked even while switched off: the set stays in step for when it is on again.
    /// </summary>
    private static void AfterAdd(CombatState __instance, Before __state)
    {
        try
        {
            if (!__state.Known || Environment.CurrentManagedThreadId != _mainThread || !_sets.TryGetValue(__instance, out Set? set) || !set.Built)
            {
                return;
            }
            List<CardModel> list = _allCards!(__instance);
            int version = _listVersion!(list);
            if (set.Version != __state.Version || set.Total != __state.Count || version != unchecked(__state.Version + 1) || list.Count != __state.Count + 1)
            {
                return;
            }
            Count(set, list[list.Count - 1], +1);
            set.Total = list.Count;
            set.Version = version;
        }
        catch (Exception e)
        {
            Fail("add count failed: " + e);
        }
    }

    /// <summary>
    /// <c>_allCards.Remove(card)</c> removes the first element equal to the card: with no type overriding <c>Equals</c> in the
    /// list, that card itself. Counted when the list moved by exactly one remove since the prefix and the set was in step then;
    /// a card the set didn't hold leaves it behind (rebuilt later).
    /// </summary>
    private static void AfterRemove(CombatState __instance, CardModel card, Before __state)
    {
        try
        {
            if (!__state.Known || Environment.CurrentManagedThreadId != _mainThread || !_sets.TryGetValue(__instance, out Set? set) || !set.Built)
            {
                return;
            }
            List<CardModel> list = _allCards!(__instance);
            int version = _listVersion!(list);
            if (set.Version != __state.Version || set.Total != __state.Count || version != unchecked(__state.Version + 1) || list.Count != __state.Count - 1
                || set.Overriders > 0 || card == null || !set.Counts.ContainsKey(card))
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

    private static void Count(Set set, CardModel? card, int delta)
    {
        if (card == null)
        {
            return; // List.Contains(card) never matches a null element for a card that isn't null (the only ones answered here).
        }
        if (OverridesEquals(card.GetType()))
        {
            set.Overriders += delta;
        }
        ref int count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(set.Counts, card, out _);
        count += delta;
        if (count <= 0)
        {
            set.Counts.Remove(card);
        }
    }

    private static bool OverridesEquals(Type type)
    {
        if (!_overridesEquals.TryGetValue(type, out bool overrides))
        {
            MethodInfo? equals = type.GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
            overrides = equals == null || equals.DeclaringType != typeof(object);
            _overridesEquals[type] = overrides;
        }
        return overrides;
    }

    /// <summary>The set brought in step with the list from scratch.</summary>
    private static void Build(Set set, List<CardModel> list)
    {
        set.Counts.Clear();
        set.Overriders = 0;
        for (int i = 0; i < list.Count; i++)
        {
            Count(set, list[i], +1);
        }
        set.Total = list.Count;
        set.Version = _listVersion!(list);
        set.Built = true;
        set.BehindScans = 0;
        Builds++;
    }

    private static bool ContainsPrefix(CombatState __instance, CardModel card, ref bool __result)
    {
        // Inside ListenerCache.RunVanilla the answer is the reference others are compared with: the game's own.
        if (!Enabled || card == null || ListenerCache.IsSuspended || Environment.CurrentManagedThreadId != _mainThread || Backoff())
        {
            return true;
        }
        try
        {
            List<CardModel>? list = _allCards!(__instance);
            if (list == null)
            {
                return true; // The game's own NullReferenceException.
            }
            Set set = _sets.GetOrCreateValue(__instance);
            if (!set.Built || set.Version != _listVersion!(list) || set.Total != list.Count)
            {
                if (set.Built && set.BehindScans++ < ScansBeforeRebuild)
                {
                    Fallbacks++;
                    return true;
                }
                Build(set, list);
            }
            if (set.Overriders > 0)
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
        long answered = Answered, fallbacks = Fallbacks, checks = Checks;
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
        }
        return answeredHere ? result : null;
    }
#endif
}
