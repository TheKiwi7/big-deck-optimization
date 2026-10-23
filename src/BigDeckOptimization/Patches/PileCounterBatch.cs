using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The combat pile buttons (draw, discard, exhaust) update their counter on every removed card:
/// <c>NCombatCardPile.RemoveCard</c> re-fits the label text (<c>SetTextAutoSize</c>). At combat end every pile is
/// emptied card by card, so thousands of text fits for a counter nobody sees change.
///
/// Inside <c>CardPile.Clear</c>, the counter value is updated exactly like vanilla on each removal, but the label is
/// fitted once when the clear ends, with the final value: same text, same pivot. UI only.
/// </summary>
public static class PileCounterBatch
{
    public const string Name = "PileCounterBatch";

    /// <summary>v0.107.1 <c>NCombatCardPile.RemoveCard</c>, mirrored below.</summary>
    private const string RemoveCardFingerprint = "6D713C03573931DA";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    [ThreadStatic] private static int _clearDepth;
    [ThreadStatic] private static List<NCombatCardPile>? _dirty;

    private static AccessTools.FieldRef<NCombatCardPile, CardPile?>? _pile;
    private static AccessTools.FieldRef<NCombatCardPile, int>? _currentCount;
    private static AccessTools.FieldRef<NCombatCardPile, MegaCrit.Sts2.addons.mega_text.MegaLabel>? _countLabel;

    public static void Apply(Harmony harmony)
    {
        var removeCard = AccessTools.Method(typeof(NCombatCardPile), "RemoveCard") ?? throw new MissingMethodException("NCombatCardPile.RemoveCard");
        PatchSafety.RequireFingerprint(removeCard, RemoveCardFingerprint);
        _pile = AccessTools.FieldRefAccess<NCombatCardPile, CardPile?>("_pile");
        _currentCount = AccessTools.FieldRefAccess<NCombatCardPile, int>("_currentCount");
        _countLabel = AccessTools.FieldRefAccess<NCombatCardPile, MegaCrit.Sts2.addons.mega_text.MegaLabel>("_countLabel");
        var clear = AccessTools.Method(typeof(CardPile), nameof(CardPile.Clear)) ?? throw new MissingMethodException("CardPile.Clear");
        harmony.Patch(clear,
            prefix: new HarmonyMethod(typeof(PileCounterBatch), nameof(ClearPrefix)),
            finalizer: new HarmonyMethod(typeof(PileCounterBatch), nameof(ClearFinalizer)));
        harmony.Patch(removeCard, prefix: new HarmonyMethod(typeof(PileCounterBatch), nameof(RemoveCardPrefix)));
        Applied = true;
    }

    private static void ClearPrefix()
    {
        _clearDepth++;
    }

    private static Exception? ClearFinalizer(Exception? __exception)
    {
        if (--_clearDepth == 0 && _dirty is { Count: > 0 })
        {
            List<NCombatCardPile> dirty = _dirty;
            _dirty = null;
            foreach (NCombatCardPile node in dirty)
            {
                try
                {
                    if (GodotObject.IsInstanceValid(node))
                    {
                        FitLabel(node);
                    }
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }
        return __exception;
    }

    /// <summary>Vanilla: <c>_currentCount = Math.Max(_currentCount - 1, _pile.Cards.Count)</c>, then fits the label.</summary>
    private static bool RemoveCardPrefix(NCombatCardPile __instance)
    {
        if (!Enabled || _clearDepth == 0)
        {
            return true;
        }
        try
        {
            CardPile? pile = _pile!(__instance);
            if (pile == null)
            {
                return false;
            }
            ref int count = ref _currentCount!(__instance);
            count = Math.Max(count - 1, pile.Cards.Count);
            _dirty ??= new List<NCombatCardPile>();
            if (!_dirty.Contains(__instance))
            {
                _dirty.Add(__instance);
            }
            return false;
        }
        catch (Exception e)
        {
            Fail(e);
            return true;
        }
    }

    private static void FitLabel(NCombatCardPile node)
    {
        var label = _countLabel!(node);
        label.SetTextAutoSize(_currentCount!(node).ToString());
        label.PivotOffset = label.Size * 0.5f;
    }

    private static void Fail(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
    }
}
