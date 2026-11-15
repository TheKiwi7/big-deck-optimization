using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <c>NetCombatCardDb</c> gives every combat card a network ID. It listens to every combat pile and, on each content
/// change, walks the whole pile to ID cards that have none yet. With thousands of cards that is a full pile walk per
/// moved card: O(n²) when the draw pile is reshuffled or the piles are emptied at combat end.
///
/// A card can only enter a pile through <c>CardPile.AddInternal</c> (no other code changes the pile lists: checked), and
/// IDs are never removed during a combat. So we track, per pile the database walks (the piles it IDed at combat start or
/// walked since, never assumed from the pile's type), the cards added since its last walk: when all of them already have an ID, the walk would
/// ID nothing and is skipped; otherwise the vanilla walk runs (same cards, same order, same IDs). One skipped walk in
/// <see cref="VerifyEvery"/> is checked against the full pile; any card without an ID disables the patch.
///
/// Another mod's patch on <c>AddInternal</c> could put a card in a pile unseen (a prefix skipping the original and adding it
/// itself, a transpiler): while one is there, and until the next combat start rebuilds the tracking from the piles, every walk
/// is the game's. An add whose original was skipped but which changed the pile anyway also gives that pile a full walk.
/// </summary>
public static class CardDbScan
{
    public const string Name = "CardDbScan";

    /// <summary>v0.107.1 code this relies on: the pile walk, the ID assignment and the combat start that IDs every card
    /// already in a pile (same code in v0.111, v0.113.0 and v0.113.1). <c>AddInternal</c> is deliberately not fingerprinted
    /// (its body changed on v0.113): it only has to be the only way into a pile (the list's writers are checked) and to put
    /// the very card it is given in the list, which the sampled whole-pile check (<see cref="VerifyEvery"/>) would catch
    /// otherwise. Recording a card whose add then fails costs one more ID check.</summary>
    private const string WalkFingerprint = "207017C48274E22C";
    private const string StartCombatFingerprint = "01491A6EA42EBCE2";
    private const string IdFingerprint = "DEC87C0A856497A5";

    private const int VerifyEvery = 64;
    private const int MaxPending = 256;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Walks { get; private set; }
    public static long Skipped { get; private set; }
    public static long Verified { get; private set; }

    private sealed class Pending
    {
        public readonly List<CardModel> Cards = new();
        /// <summary>Too many adds since the last walk (silent bulk adds): the next walk runs vanilla.</summary>
        public bool Overflow;
    }

    private static readonly ConditionalWeakTable<CardPile, Pending> _pending = new();
    private static AccessTools.FieldRef<NetCombatCardDb, Dictionary<CardModel, uint>>? _cardToId;
    private static MethodInfo? _walk, _id, _add;

    /// <summary>
    /// The code that writes the database's ids (v0.107.1, v0.111, v0.113: the same class): "IDs never removed during a
    /// combat" holds while nothing else does, which no fingerprint would show (a new method removing one).
    /// </summary>
    private static readonly string[] IdWriters = { ".ctor", "StartCombat", "IdCardIfNecessary", "ClearCardsForTesting" };

    /// <summary>Another mod patched <c>AddInternal</c> since the last combat start: the tracking may have missed adds.</summary>
    private static bool _addPatchedSinceStart;

    /// <summary>
    /// Other mods' patches on IdCardIfNecessary a skipped walk may leave out: Limitless 1.2.1's card id ledger (its
    /// multiplayer test mode), which only logs the ids of cards it sees get one, and a skipped walk only skips calls for
    /// cards that already have one. Fingerprinted; any other patch there leaves walks vanilla.
    /// </summary>
    private static readonly (string Method, string Fingerprint)[] AllowedIdPatches =
    {
        ("Limitless.LimitlessCode.MpTest.CardIdLedger:BeforeId", "D78D1C6778A103EC"),
        ("Limitless.LimitlessCode.MpTest.CardIdLedger:AfterId", "927A9D1E6E3E08E9"),
    };
    private static MethodInfo[]? _allowedOnId;

    private static MethodInfo[] AllowedOnId() => _allowedOnId ??= AllowedIdPatches
        .Select(p => AccessTools.Method(p.Method) is { } m && PatchSafety.FingerprintMatches(MethodFingerprint.Of(m), p.Fingerprint) ? m : null)
        .Where(m => m != null).ToArray()!;

    public static void Apply(Harmony harmony)
    {
        var walk = AccessTools.Method(typeof(NetCombatCardDb), "OnPileContentsChanged")
            ?? throw new MissingMethodException("NetCombatCardDb.OnPileContentsChanged");
        var id = AccessTools.Method(typeof(NetCombatCardDb), "IdCardIfNecessary")
            ?? throw new MissingMethodException("NetCombatCardDb.IdCardIfNecessary");
        var add = AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal))
            ?? throw new MissingMethodException("CardPile.AddInternal");
        PatchSafety.RequireFingerprint(walk, WalkFingerprint);
        _walk = walk;
        PatchSafety.RequireFingerprint(id, IdFingerprint);
        _id = id;
        MethodInfo startCombat = AccessTools.Method(typeof(NetCombatCardDb), nameof(NetCombatCardDb.StartCombat))
            ?? throw new MissingMethodException("NetCombatCardDb.StartCombat");
        PatchSafety.RequireFingerprint(startCombat, StartCombatFingerprint);
        PatchSafety.RequireFieldWriters(typeof(CardPile), "_cards", PatchSafety.CardListWriters);
        PatchSafety.RequireFieldWriters(typeof(NetCombatCardDb), "_cardToId", IdWriters);
        PatchSafety.RequireFieldWriters(typeof(NetCombatCardDb), "_nextId", IdWriters);
        _add = add;
        _cardToId = AccessTools.FieldRefAccess<NetCombatCardDb, Dictionary<CardModel, uint>>("_cardToId");

        // Tracking first: it must see every add before the walk can be skipped.
        PatchGuard.Patch(harmony, add, prefix: new HarmonyMethod(typeof(CardDbScan), nameof(RecordAdd)));
        // Harmony doesn't call RecordAdd (it takes the card) once an earlier prefix returned false: the pile's count is taken
        // by a first prefix that always runs, and an add skipped that way but changing the pile anyway is seen after it.
        PatchGuard.Patch(harmony, add, prefix: new HarmonyMethod(typeof(CardDbScan), nameof(WatchAdd)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(CardDbScan), nameof(AfterAdd)));
        PatchGuard.Patch(harmony, startCombat,
            postfix: new HarmonyMethod(typeof(CardDbScan), nameof(AfterStartCombat)));
        PatchGuard.Patch(harmony, walk,
            prefix: new HarmonyMethod(typeof(CardDbScan), nameof(WalkPrefix)),
            postfix: new HarmonyMethod(typeof(CardDbScan), nameof(WalkPostfix)));
        Applied = true;
    }

    /// <summary>Recorded whatever <see cref="Enabled"/> says, so turning the patch on mid-combat stays exact.</summary>
    private static void RecordAdd(CardPile __instance, CardModel card)
    {
        try
        {
            // Only piles the database walks are tracked; a pile not tracked yet gets a vanilla walk first.
            if (!_pending.TryGetValue(__instance, out Pending? pending) || pending.Overflow)
            {
                return;
            }
            if (pending.Cards.Count >= MaxPending)
            {
                pending.Overflow = true;
                pending.Cards.Clear();
                return;
            }
            pending.Cards.Add(card);
        }
        catch (Exception e)
        {
            // Runs on every card move: a missed record can't be trusted, so stop skipping walks.
            Fail(e.ToString());
        }
    }

    private static void WatchAdd(CardPile __instance, out int __state) => __state = __instance.Cards.Count;

    /// <summary>An add whose original didn't run but whose pile changed anyway: what entered isn't known, that pile walks in full.</summary>
    private static void AfterAdd(Exception? __exception, CardPile __instance, int __state, bool __runOriginal)
    {
        if (!__runOriginal)
        {
            try
            {
                if (__instance.Cards.Count != __state && _pending.TryGetValue(__instance, out Pending? pending))
                {
                    pending.Overflow = true;
                    pending.Cards.Clear();
                }
            }
            catch (Exception e)
            {
                Fail(e.ToString());
            }
        }
    }

    /// <summary>StartCombat IDs every card already in a pile (vanilla refuses ownerless ones: keep those pending).</summary>
    private static void AfterStartCombat(NetCombatCardDb __instance, IReadOnlyList<Player> players)
    {
        _pending.Clear();
        // Tracking starts again from the piles themselves: earlier unseen adds no longer matter.
        _addPatchedSinceStart = PatchSafety.IsPatchedByOthers(_add);
        try
        {
            Dictionary<CardModel, uint> ids = _cardToId!(__instance);
            foreach (Player player in players)
            {
                if (player.PlayerCombatState == null)
                {
                    continue;
                }
                foreach (CardPile pile in player.PlayerCombatState.AllPiles)
                {
                    Pending pending = _pending.GetOrCreateValue(pile);
                    foreach (CardModel card in pile.Cards)
                    {
                        if (!ids.ContainsKey(card))
                        {
                            pending.Cards.Add(card);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Fail(e.ToString());
        }
    }

    private static bool WalkPrefix(NetCombatCardDb __instance, CardPile pile)
    {
        Walks++;
        if (!Enabled || PatchSafety.IsPatchedByOthers(_walk) || PatchSafety.IsPatchedByOthers(_id, AllowedOnId()))
        {
            return true;
        }
        if (_addPatchedSinceStart || PatchSafety.IsPatchedByOthers(_add))
        {
            _addPatchedSinceStart = true;
            return true;
        }
        try
        {
            Dictionary<CardModel, uint> ids = _cardToId!(__instance);
            // A pile this hasn't tracked since combat start: vanilla walk (its postfix starts tracking it).
            if (!_pending.TryGetValue(pile, out Pending? pending) || pending.Overflow)
            {
                return true;
            }
            foreach (CardModel card in pending.Cards)
            {
                if (!ids.ContainsKey(card))
                {
                    return true; // Something to ID: vanilla walk, in pile order.
                }
            }
            Skipped++;
            if (Skipped % VerifyEvery == 0)
            {
                Verified++;
                foreach (CardModel card in pile.Cards)
                {
                    if (!ids.ContainsKey(card))
                    {
                        Fail($"card {card} in {pile.Type} pile has no ID although no walk was needed");
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
            return true;
        }
    }

    /// <summary>
    /// After any walk (vanilla or skipped) every card of the pile has an ID, except ownerless cards vanilla refuses to
    /// ID: those stay pending so the next change walks again, like vanilla.
    /// </summary>
    private static void WalkPostfix(NetCombatCardDb __instance, CardPile pile)
    {
        try
        {
            Dictionary<CardModel, uint> ids = _cardToId!(__instance);
            if (!_pending.TryGetValue(pile, out Pending? pending))
            {
                // A pile tracked from now on (not one of the piles at combat start): what the walk left without an ID
                // (ownerless cards) is read from the pile itself, as after an overflow.
                pending = _pending.GetOrCreateValue(pile);
                pending.Overflow = true;
            }
            if (pending.Overflow)
            {
                pending.Overflow = false;
                pending.Cards.Clear();
                foreach (CardModel card in pile.Cards)
                {
                    if (!ids.ContainsKey(card))
                    {
                        pending.Cards.Add(card);
                    }
                }
                return;
            }
            if (pending.Cards.Count != 0)
            {
                pending.Cards.RemoveAll(card => ids.ContainsKey(card));
            }
        }
        catch (Exception e)
        {
            Fail(e.ToString());
        }
    }

    private static void Fail(string reason)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, falling back to vanilla: {reason}");
    }
}
