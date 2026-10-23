using System;
using System.Collections.Generic;
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
/// A card can only enter a pile through <c>CardPile.AddInternal</c>, and IDs are never removed during a combat. So we
/// track, per combat pile, the cards added since its last walk: when all of them already have an ID, the walk would
/// ID nothing and is skipped; otherwise the vanilla walk runs (same cards, same order, same IDs). One skipped walk in
/// <see cref="VerifyEvery"/> is checked against the full pile; any card without an ID disables the patch.
/// </summary>
public static class CardDbScan
{
    public const string Name = "CardDbScan";

    /// <summary>v0.107.1 code this relies on: the pile walk, the ID assignment and the only way into a pile.</summary>
    private const string WalkFingerprint = "207017C48274E22C";
    private const string IdFingerprint = "DEC87C0A856497A5";
    private const string AddFingerprint = "0A80C8EF3B697A4E";

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

    public static void Apply(Harmony harmony)
    {
        var walk = AccessTools.Method(typeof(NetCombatCardDb), "OnPileContentsChanged")
            ?? throw new MissingMethodException("NetCombatCardDb.OnPileContentsChanged");
        var id = AccessTools.Method(typeof(NetCombatCardDb), "IdCardIfNecessary")
            ?? throw new MissingMethodException("NetCombatCardDb.IdCardIfNecessary");
        var add = AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal))
            ?? throw new MissingMethodException("CardPile.AddInternal");
        PatchSafety.RequireFingerprint(walk, WalkFingerprint);
        PatchSafety.RequireFingerprint(id, IdFingerprint);
        PatchSafety.RequireFingerprint(add, AddFingerprint);
        _cardToId = AccessTools.FieldRefAccess<NetCombatCardDb, Dictionary<CardModel, uint>>("_cardToId");

        // Tracking first: it must see every add before the walk can be skipped.
        harmony.Patch(add, prefix: new HarmonyMethod(typeof(CardDbScan), nameof(RecordAdd)));
        harmony.Patch(AccessTools.Method(typeof(NetCombatCardDb), nameof(NetCombatCardDb.StartCombat)),
            postfix: new HarmonyMethod(typeof(CardDbScan), nameof(AfterStartCombat)));
        harmony.Patch(walk,
            prefix: new HarmonyMethod(typeof(CardDbScan), nameof(WalkPrefix)),
            postfix: new HarmonyMethod(typeof(CardDbScan), nameof(WalkPostfix)));
        Applied = true;
    }

    /// <summary>Recorded whatever <see cref="Enabled"/> says, so turning the patch on mid-combat stays exact.</summary>
    private static void RecordAdd(CardPile __instance, CardModel card)
    {
        if (!__instance.IsCombatPile)
        {
            return;
        }
        Pending pending = _pending.GetOrCreateValue(__instance);
        if (pending.Overflow)
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

    /// <summary>StartCombat IDs every card already in a pile (vanilla refuses ownerless ones: keep those pending).</summary>
    private static void AfterStartCombat(NetCombatCardDb __instance, IReadOnlyList<Player> players)
    {
        _pending.Clear();
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
                    foreach (CardModel card in pile.Cards)
                    {
                        if (!ids.ContainsKey(card))
                        {
                            _pending.GetOrCreateValue(pile).Cards.Add(card);
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
        if (!Enabled)
        {
            return true;
        }
        try
        {
            Dictionary<CardModel, uint> ids = _cardToId!(__instance);
            if (!_pending.TryGetValue(pile, out Pending? pending))
            {
                pending = _pending.GetOrCreateValue(pile);
            }
            if (pending.Overflow)
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
            Pending pending = _pending.GetOrCreateValue(pile);
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
            pending.Cards.RemoveAll(card => ids.ContainsKey(card));
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
