using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace BigDeckOptimization.Patches;

/// <summary>
/// A hand holder subscribes to its card node's <c>ModelChanged</c>, and only unsubscribes when it gets another card. At the end of a
/// combat the hand is freed with its holders, but a card node can outlive it as a C# object (BaseLib keeps one alive through a signal
/// handler of its cost display). That node still holds its freed holder, which holds the hand, which holds the whole combat that ended
/// (every cloned card of every pile): every combat of a run stayed in memory, ~40 MB each with 10,000 cards, mod on or off, and each new
/// combat start got slower as the heap grew.
///
/// The card nodes a hand holder took are noted (weakly); about once a second, the handlers of theirs whose target is a freed node are
/// removed (a freed holder can do nothing right with a card any more), and a card node itself freed lets go of its model, preview
/// target and enchantment (nothing can show or use a freed node). UI only.
/// </summary>
public static class PooledCardRelease
{
    public const string Name = "PooledCardRelease";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Released { get; private set; }

    private static AccessTools.FieldRef<NCard, Action<CardModel?>?>? _modelChanged;
    private static AccessTools.FieldRef<NCard, CardModel?>? _model;
    private static AccessTools.FieldRef<NCard, Creature?>? _previewTarget;
    private static AccessTools.FieldRef<NCard, EnchantmentModel?>? _subscribedEnchantment;
    private static readonly List<WeakReference<NCard>> _cards = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NCard, object> _noted = new();
    private static int _frames;

    public static void Apply(Harmony harmony)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            throw new PatchSkippedException("no scene tree");
        }
        _modelChanged = AccessTools.FieldRefAccess<NCard, Action<CardModel?>?>("ModelChanged");
        _model = AccessTools.FieldRefAccess<NCard, CardModel?>("_model");
        _previewTarget = AccessTools.FieldRefAccess<NCard, Creature?>("_previewTarget");
        _subscribedEnchantment = AccessTools.FieldRefAccess<NCard, EnchantmentModel?>("_subscribedEnchantment");
        var setCard = AccessTools.Method(typeof(NHandCardHolder), "SetCard")
            ?? throw new MissingMethodException(nameof(NHandCardHolder), "SetCard");
        PatchGuard.Patch(harmony, setCard, postfix: new HarmonyMethod(typeof(PooledCardRelease), nameof(Noted)));
        tree.ProcessFrame += Tick;
        Applied = true;
    }

    private static void Noted(NCard node)
    {
        if (Enabled && node != null && !_noted.TryGetValue(node, out _))
        {
            _noted.Add(node, _cards);
            _cards.Add(new WeakReference<NCard>(node));
        }
    }

    private static void Tick()
    {
        if (!Enabled || ++_frames < 60 || _cards.Count == 0)
        {
            return;
        }
        _frames = 0;
        try
        {
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                if (!_cards[i].TryGetTarget(out NCard? card))
                {
                    _cards.RemoveAt(i);
                    continue;
                }
                if (!GodotObject.IsInstanceValid(card))
                {
                    // A freed node, kept as a C# object by someone else: nothing can show or use it any more.
                    if (_modelChanged!(card) != null || _model!(card) != null)
                    {
                        Released++;
                    }
                    _modelChanged!(card) = null;
                    _model!(card) = null;
                    _previewTarget!(card) = null;
                    _subscribedEnchantment!(card) = null;
                    _cards.RemoveAt(i);
                    continue;
                }
                ref Action<CardModel?>? handlers = ref _modelChanged!(card);
                if (handlers == null)
                {
                    continue;
                }
                Delegate[] list = handlers.GetInvocationList();
                bool anyFreed = false;
                foreach (Delegate handler in list)
                {
                    anyFreed |= handler.Target is GodotObject target && !GodotObject.IsInstanceValid(target);
                }
                if (!anyFreed)
                {
                    continue; // Nearly always: nothing to build again.
                }
                Action<CardModel?>? kept = null;
                foreach (Delegate handler in list)
                {
                    if (handler.Target is GodotObject target && !GodotObject.IsInstanceValid(target))
                    {
                        Released++;
                        continue;
                    }
                    kept += (Action<CardModel?>)handler;
                }
                handlers = kept;
            }
        }
        catch (Exception e)
        {
            Enabled = false;
            _cards.Clear();
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, card nodes keep their freed holders as the game does: {e}");
        }
    }
}
