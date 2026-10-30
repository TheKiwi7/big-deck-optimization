using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <c>CardModel.Pile</c> is <c>_owner?.Piles.FirstOrDefault(p =&gt; p.Cards.Contains(this))</c>: a scan of every card the
/// owner has, on each call. <c>CardModel.CombatState</c> goes through it, and that is read everywhere (keywords, costs,
/// CanPlay, hooks...). Loops over the draw pile become O(n²): the first turn's Innate check alone froze the game 6 s
/// with 8000 cards, and every frame spent milliseconds there.
///
/// <c>CardPile._cards</c> only changes through CardPile's own methods, so we track in which piles each card is:
/// <c>AddInternal</c> / <c>RemoveInternal</c> (also used by <c>Clear</c>) change membership, the Move/Randomize
/// methods only reorder. When a card is in exactly one pile, the answer is that pile if it is one of the owner's
/// <c>Piles</c> (else null, like vanilla); in no pile, null. Anything unusual (several piles, a failed clear, a
/// shuffle hook that changed the list) falls back to the vanilla scan. One fast answer in <see cref="VerifyEvery"/> is
/// compared with the vanilla scan; a difference disables the patch for the session.
///
/// Two more scans of every card, once per card added (cloning thousands of cards at a campfire: O(n²)):
/// <c>AddInternal</c>'s duplicate check <c>Cards.Contains(card)</c> answers false at once when the index says the card
/// was in no pile, and <c>RunState.ContainsCard</c> (<c>_allCards.Contains</c>) searches from the end, where a card
/// just created is (same comparer, so the same answer as from the start).
/// </summary>
public static class PileIndex
{
    public const string Name = "PileIndex";

    /// <summary>v0.107.1 code this relies on: the mirrored getter and every CardPile method that touches the list.</summary>
    private static readonly (string Method, string Fingerprint)[] Fingerprints =
    {
        ("AddInternal", "0A80C8EF3B697A4E"),
        ("RemoveInternal", "7CF07A4BBD094E41"),
        ("MoveToBottomInternal", "8A6B91EFCD757B3B"),
        ("MoveToTopInternal", "0C41AC449A8E2DBB"),
        ("RandomizeOrderInternal", "FF906423481AC743"),
        ("Clear", "A2B6A279CA18ABC1"),
    };
    private const string GetterFingerprint = "6A1FE5E50F9ECAC2";
    /// <summary>v0.107.1 <c>RunState.ContainsCard</c>, mirrored by <see cref="ContainsCardPrefix"/>.</summary>
    private const string ContainsCardFingerprint = "DC5BAC12C08BE24D";

    /// <summary>One fast answer in this many is checked against the vanilla scan (1 = all, for tests).</summary>
    internal static int VerifyEvery { get; set; } = 1024;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Fast { get; private set; }
    public static long Fallbacks { get; private set; }
    public static long Verified { get; private set; }

    private sealed class Entry
    {
        /// <summary>Number of piles holding the card.</summary>
        public int Count;
        /// <summary>The pile when <see cref="Count"/> is 1 and known.</summary>
        public CardPile? Pile;
        /// <summary>Membership no longer known exactly (removed from one of several piles): vanilla scan from now on.</summary>
        public bool Unknown;
    }

    private static readonly ConditionalWeakTable<CardModel, Entry> _entries = new();
    private static AccessTools.FieldRef<CardModel, Player?>? _owner;
    /// <summary><c>Player._runPiles</c>, null when <see cref="Player.Piles"/> isn't the code <see cref="OwnsPile"/> mirrors.</summary>
    private static AccessTools.FieldRef<Player, CardPile[]?>? _runPiles;
    /// <summary>v0.107.1 and v0.111 <c>Player.Piles</c>: <c>(PlayerCombatState?.AllPiles ?? []).Concat(_runPiles)</c>, _runPiles = [Deck] made on first call.</summary>
    private const string PlayerPilesFingerprint = "1C4E1C85C62DCE1D";
    private static System.Reflection.MethodInfo? _getter;
    /// <summary>Set by the verification and the bench to get the vanilla answer.</summary>
    [ThreadStatic] private static bool _vanilla;

    public static void Apply(Harmony harmony)
    {
        _getter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Pile)) ?? throw new MissingMethodException("CardModel.Pile");
        PatchSafety.RequireFingerprint(_getter, GetterFingerprint);
        foreach ((string method, string fingerprint) in Fingerprints)
        {
            PatchSafety.RequireFingerprint(AccessTools.Method(typeof(CardPile), method) ?? throw new MissingMethodException("CardPile." + method), fingerprint);
        }
        // ...and no other code changes the pile lists.
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        _owner = AccessTools.FieldRefAccess<CardModel, Player?>("_owner");
        try
        {
            // Player.Piles builds a Concat iterator on every call: read the same piles without it while its code is known.
            PatchSafety.RequireFingerprint(AccessTools.PropertyGetter(typeof(Player), nameof(Player.Piles))
                ?? throw new MissingMethodException("Player.Piles"), PlayerPilesFingerprint);
            _runPiles = AccessTools.FieldRefAccess<Player, CardPile[]?>("_runPiles");
        }
        catch (Exception e)
        {
            _runPiles = null;
            Log.Info($"[{ModEntry.Id}] {Name}: owner's piles read through Player.Piles ({e.Message})");
        }

        // Tracking first, so the getter never answers from an index that missed changes.
        // Updated before the original runs: it fires CardAdded/ContentsChanged (whose listeners read card.Pile) right
        // after changing the list. Last prefix, so it sees whether another mod skips the original; undone if it throws.
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)),
            prefix: new HarmonyMethod(typeof(PileIndex), nameof(BeforeAdd)) { priority = Priority.Last },
            finalizer: Method(nameof(UndoIfThrown)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)),
            prefix: new HarmonyMethod(typeof(PileIndex), nameof(BeforeRemove)) { priority = Priority.Last },
            finalizer: Method(nameof(UndoIfThrown)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.Clear)), finalizer: Method(nameof(ClearFinalizer)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.RandomizeOrderInternal)),
            prefix: Method(nameof(BeforeRandomize)), postfix: Method(nameof(AfterRandomize)));
        PatchGuard.Patch(harmony, _getter, prefix: Method(nameof(PilePrefix)));
        Applied = true;
        try
        {
            MethodInfo addInternal = AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal))!;
            PatchGuard.Patch(harmony, addInternal, transpiler: Method(nameof(AddInternalTranspiler)));
            Log.Info($"[{ModEntry.Id}] {Name}: AddInternal duplicate check {(_duplicateCheckPatched ? "patched" : "left vanilla (call not found)")}");
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: AddInternal duplicate check left vanilla: {e.Message}");
        }
        try
        {
            _containsCard = AccessTools.Method(typeof(RunState), nameof(RunState.ContainsCard)) ?? throw new MissingMethodException("RunState.ContainsCard");
            PatchSafety.RequireFingerprint(_containsCard, ContainsCardFingerprint);
            _allCards = AccessTools.FieldRefAccess<RunState, List<CardModel>>("_allCards");
            PatchGuard.Patch(harmony, _containsCard, prefix: Method(nameof(ContainsCardPrefix)));
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: RunState.ContainsCard left vanilla: {e.Message}");
        }
    }

    /// <summary>The card is in no pile (known exactly), or false when unsure.</summary>
    internal static bool InNoPile(CardModel card)
    {
        if (!Enabled || !Applied)
        {
            return false;
        }
        return !_entries.TryGetValue(card, out Entry? entry) || (entry.Count == 0 && !entry.Unknown);
    }

    private static bool _duplicateCheckPatched;
    private static System.Reflection.MethodInfo? _containsCard;
    private static AccessTools.FieldRef<RunState, List<CardModel>>? _allCards;
    public static long FastDuplicateChecks { get; private set; }

    /// <summary>AddInternal's <c>Cards.Contains(card)</c> (Enumerable.Contains on the pile's list) goes through <see cref="ContainsBeingAdded"/>.</summary>
    private static IEnumerable<CodeInstruction> AddInternalTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo contains = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(CardModel));
        bool replaced = false; // Harmony runs transpilers again each time the method is re-patched.
        foreach (CodeInstruction instruction in instructions)
        {
            if (!replaced && instruction.Calls(contains))
            {
                replaced = true;
                _duplicateCheckPatched = true;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PileIndex), nameof(ContainsBeingAdded)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    /// <summary>
    /// Inside AddInternal, after <see cref="BeforeAdd"/> counted this add: a count of 1 means the card was in no pile
    /// before, so it isn't in this one. Anything else: the game's scan.
    /// </summary>
    private static bool ContainsBeingAdded(IEnumerable<CardModel> cards, CardModel card)
    {
        if (Enabled && _entries.TryGetValue(card, out Entry? entry) && entry.Count == 1 && !entry.Unknown)
        {
            FastDuplicateChecks++;
            if (FastDuplicateChecks % VerifyEvery == 0 && cards.Contains(card))
            {
                Fail($"{card} is already in the pile it is added to, the index said no pile");
                return true;
            }
            return false;
        }
        SlowDuplicateChecks++;
#if BENCH
        if (SlowDuplicateChecks <= 5)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: slow duplicate check for {card}: entry {(_entries.TryGetValue(card, out Entry? e) ? $"count {e.Count}, unknown {e.Unknown}, pile {e.Pile?.Type}" : "none")}, enabled {Enabled}");
        }
#endif
        return cards.Contains(card);
    }

    public static long SlowDuplicateChecks { get; private set; }

    /// <summary><c>_allCards.Contains(card)</c> from the end: the same answer, found at once for cards just created.</summary>
    private static bool ContainsCardPrefix(RunState __instance, CardModel card, ref bool __result)
    {
        if (!Enabled || PatchSafety.IsPatchedByOthers(_containsCard))
        {
            return true;
        }
        __result = _allCards!(__instance).LastIndexOf(card) >= 0;
        return false;
    }

    private static HarmonyMethod Method(string name) => new(typeof(PileIndex), name);

    /// <summary>The entry as it was before an add/remove, restored if the original throws.</summary>
    private readonly struct Saved(Entry? entry, int count, CardPile? pile, bool unknown)
    {
        public void Restore()
        {
            if (entry != null)
            {
                entry.Count = count;
                entry.Pile = pile;
                entry.Unknown = unknown;
            }
        }

        public static Saved Of(Entry entry) => new(entry, entry.Count, entry.Pile, entry.Unknown);
    }

    private static void BeforeAdd(CardPile __instance, CardModel card, bool __runOriginal, out Saved __state)
    {
        __state = default;
        if (!__runOriginal)
        {
            return;
        }
        try
        {
            Entry entry = _entries.GetOrCreateValue(card);
            __state = Saved.Of(entry);
            entry.Count++;
            entry.Pile = entry.Count == 1 ? __instance : null;
        }
        catch (Exception e)
        {
            // Runs on every card move: never let it break the game's own add; the index can't be trusted any more.
            Fail("index update failed: " + e);
        }
    }

    /// <summary>AddInternal throws on duplicates or canonical cards, RemoveInternal on a card not in the pile.</summary>
    private static Exception? UndoIfThrown(Exception? __exception, Saved __state)
    {
        if (__exception != null)
        {
            __state.Restore();
        }
        return __exception;
    }

    private static void BeforeRemove(CardPile __instance, CardModel card, bool __runOriginal, out Saved __state)
    {
        __state = default;
        if (!__runOriginal)
        {
            return;
        }
        try
        {
            Entry entry = _entries.GetOrCreateValue(card);
            __state = Saved.Of(entry);
            entry.Count--;
            if (entry.Count <= 0)
            {
                entry.Count = 0;
                entry.Pile = null;
                entry.Unknown = false;
            }
            else if (entry.Pile != __instance)
            {
                // It was in several piles and is still in at least one: which one isn't tracked.
                entry.Pile = null;
                entry.Unknown = true;
            }
        }
        catch (Exception e)
        {
            Fail("index update failed: " + e);
        }
    }

    /// <summary>A clear that throws midway empties the list without RemoveInternal for the rest: stop trusting the index.</summary>
    private static Exception? ClearFinalizer(Exception? __exception)
    {
        if (__exception != null && Enabled)
        {
            Fail("CardPile.Clear threw, the index may be stale: " + __exception.Message);
        }
        return __exception;
    }

    /// <summary>Shuffle hooks (<c>ModifyShuffleOrder</c>) receive the pile's own list and could add or remove cards.</summary>
    private static void BeforeRandomize(CardPile __instance, out int __state)
    {
        __state = __instance.Cards.Count;
    }

    private static void AfterRandomize(CardPile __instance, int __state)
    {
        if (!Enabled)
        {
            return;
        }
        if (__instance.Cards.Count != __state)
        {
            Fail("a shuffle hook changed the pile's card count");
            return;
        }
        foreach (CardModel card in __instance.Cards)
        {
            if (!_entries.TryGetValue(card, out Entry? entry) || (entry.Pile != __instance && !entry.Unknown && entry.Count < 2))
            {
                Fail($"a shuffle hook put {card} in the {__instance.Type} pile");
                return;
            }
        }
    }

    private static bool PilePrefix(CardModel __instance, ref CardPile? __result)
    {
        if (!Enabled || _vanilla)
        {
            return true;
        }
        try
        {
            Player? owner = _owner!(__instance);
            CardPile? answer;
            if (owner == null)
            {
                answer = null;
            }
            else if (!_entries.TryGetValue(__instance, out Entry? entry) || entry.Count == 0)
            {
                answer = null;
            }
            else if (entry.Unknown || entry.Count > 1 || entry.Pile == null)
            {
                Fallbacks++;
                return true;
            }
            else
            {
                answer = OwnsPile(owner, entry.Pile) ? entry.Pile : null;
            }
            Fast++;
            if (Fast % VerifyEvery == 0)
            {
                Verified++;
                CardPile? vanilla = Vanilla(__instance);
                if (vanilla != answer)
                {
                    Fail($"{__instance} index says {answer?.Type.ToString() ?? "none"}, vanilla {vanilla?.Type.ToString() ?? "none"}");
                    __result = vanilla;
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

    /// <summary>Whether <c>owner.Piles</c> holds the pile, read without allocating when that getter's code is known.</summary>
    private static bool OwnsPile(Player owner, CardPile pile)
    {
        CardPile[]? run = _runPiles?.Invoke(owner);
        if (run == null)
        {
            // Not known, or not made yet (the getter makes it).
            foreach (CardPile candidate in owner.Piles)
            {
                if (candidate == pile)
                {
                    return true;
                }
            }
            return false;
        }
        IReadOnlyList<CardPile>? combat = owner.PlayerCombatState?.AllPiles;
        if (combat != null)
        {
            for (int i = 0; i < combat.Count; i++)
            {
                if (combat[i] == pile)
                {
                    return true;
                }
            }
        }
        for (int i = 0; i < run.Length; i++)
        {
            if (run[i] == pile)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The game's own answer (the getter with this patch bypassed).</summary>
    internal static CardPile? Vanilla(CardModel card)
    {
        bool was = _vanilla;
        _vanilla = true;
        try
        {
            return card.Pile;
        }
        finally
        {
            _vanilla = was;
        }
    }

    private static void Fail(string reason)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, falling back to vanilla: {reason}");
    }
}
