using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// At every co-op combat start the game rebuilds each other player's deck from the message they sent
/// (<c>Player.SyncWithSerializedPlayer</c>: <c>Deck.Clear</c>, then a new model per card through <c>RunState.LoadCard</c>). The cards
/// replaced are never taken out of <c>RunState._allCards</c>, the only thing still holding them, so the run keeps every other player's
/// whole deck once per combat: +24 MB per combat at 1,900 cards, for the rest of the run, and every collection walks it.
///
/// Cards that left the deck during that call are removed from the list. The list is only read by <c>RunState.ContainsCard</c>, asked
/// about cards being added to a deck or just made: a replaced card is unreachable, nothing can ask. Memory only: cards are not touched.
/// </summary>
public static class SyncedCardRelease
{
    public const string Name = "SyncedCardRelease";

    /// <summary>v0.107.1 <c>Player.SyncWithSerializedPlayer</c> (same on v0.111).</summary>
    private const string SyncFingerprint = "AE35544973BCD76F";

    /// <summary>Everything in <c>RunState</c> using <c>_allCards</c>: a new reader could see what was removed.</summary>
    private static readonly string[] ListUsers = { ".ctor", "AddCard", "ContainsCard", "RemoveCard" };

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Released { get; private set; }

    private static AccessTools.FieldRef<RunState, List<CardModel>>? _allCards;

    public static void Apply(Harmony harmony)
    {
        var sync = AccessTools.Method(typeof(Player), nameof(Player.SyncWithSerializedPlayer))
            ?? throw new MissingMethodException(nameof(Player), nameof(Player.SyncWithSerializedPlayer));
        PatchSafety.RequireFingerprint(sync, SyncFingerprint);
        string[] unknown = MethodFingerprint.FieldUsers(typeof(RunState), "_allCards").Where(user => !ListUsers.Contains(user)).ToArray();
        if (unknown.Length > 0)
        {
            throw new PatchSkippedException($"RunState._allCards also used by {string.Join(", ", unknown)}: left vanilla");
        }
        _allCards = AccessTools.FieldRefAccess<RunState, List<CardModel>>("_allCards");
        PatchGuard.Patch(harmony, sync, prefix: new HarmonyMethod(typeof(SyncedCardRelease), nameof(Before)), postfix: new HarmonyMethod(typeof(SyncedCardRelease), nameof(After)));
        Applied = true;
    }

    private static void Before(Player __instance, out HashSet<CardModel>? __state)
    {
        __state = null;
        if (!Enabled)
        {
            return;
        }
        try
        {
            if (__instance.RunState is RunState)
            {
                __state = new HashSet<CardModel>(__instance.Deck.Cards, ReferenceEqualityComparer.Instance);
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    private static void After(Player __instance, HashSet<CardModel>? __state)
    {
        if (__state == null || !Enabled)
        {
            return;
        }
        try
        {
            __state.ExceptWith(__instance.Deck.Cards);
            if (__state.Count > 0)
            {
                Released += _allCards!((RunState)__instance.RunState).RemoveAll(__state.Contains);
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, the run keeps replaced cards as the game does: {e}");
    }
}
