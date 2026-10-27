using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BigDeckOptimization.Patches;

/// <summary>
/// While the mouse is on a creature, the game rebuilds its hover tips (one panel per power and intent, rich text with
/// effects) after every combat state change: with huge decks, actions change the state many times per second, and the
/// rebuilds were ~half of the post-action UI refresh at 8,400 cards.
///
/// Tips stay live: on each state change the creature's tips are computed as usual and compared with the ones on
/// screen (every field of each tip: title, description with its numbers, icon, debuff look...). Only when they are all
/// identical and the panel shown is still the one built for them is the rebuild skipped; any difference rebuilds it
/// like the game does. UI only.
/// </summary>
public static class HoverTipReuse
{
    public const string Name = "HoverTipReuse";

    /// <summary>v0.107.1 code mirrored: the state-change handler and the show/hide it calls, and the panel build.</summary>
    private const string HandlerFingerprint = "17A5EAD081E993CC";
    private const string ShowFingerprint = "52AF78E3A3104784";
    private const string HideFingerprint = "3EF252278767F901";
    private const string InitFingerprint = "CF7925571216B6E1";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Rebuilt { get; private set; }

    private sealed class Shown
    {
        public required List<HoverTip> Tips;
        public required HoverTipAlignment Alignment;
        public required NHoverTipSet Panel;
    }

    private static readonly ConditionalWeakTable<NCreature, Shown> _shown = new();
    private static Func<Dictionary<Control, NHoverTipSet>>? _activeHoverTips;

    public static void Apply(Harmony harmony)
    {
        var handler = AccessTools.Method(typeof(NCreature), "ShowCreatureHoverTips")
            ?? throw new MissingMethodException("NCreature.ShowCreatureHoverTips");
        PatchSafety.RequireFingerprint(handler, HandlerFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips)), ShowFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(NCreature), nameof(NCreature.HideHoverTips)), HideFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(NHoverTipSet), "Init"), InitFingerprint);
        if (PatchSafety.IsPatchedByOthers(handler))
        {
            throw new PatchSkippedException("NCreature.ShowCreatureHoverTips is patched by another mod");
        }
        var field = AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips") ?? throw new MissingFieldException("NHoverTipSet._activeHoverTips");
        var active = (Dictionary<Control, NHoverTipSet>)field.GetValue(null)!;
        _activeHoverTips = () => active;
        PatchGuard.Patch(harmony, handler, prefix: new HarmonyMethod(typeof(HoverTipReuse), nameof(Prefix)));
        Applied = true;
    }

    /// <summary>Does what ShowCreatureHoverTips does, skipping the rebuild when nothing shown would change.</summary>
    private static bool Prefix(NCreature __instance)
    {
        if (!Enabled)
        {
            return true;
        }
        try
        {
            // Cases where the game shows nothing new: let it run as is.
            if (__instance.Entity.CombatState == null || NCombatRoom.Instance is not { } room || room.Ui.Hand.InCardPlay)
            {
                return true;
            }
            List<IHoverTip> tips = __instance.Entity.HoverTips.ToList();
            List<HoverTip>? text = IHoverTip.RemoveDupes(tips).All(t => t is HoverTip)
                ? IHoverTip.RemoveDupes(tips).Cast<HoverTip>().ToList()
                : null; // Card previews: no field-by-field comparison, always rebuilt.
            HoverTipAlignment alignment = HoverTip.GetHoverTipAlignment(__instance, 0.5f);
            Control hitbox = __instance.Hitbox;
            if (text != null && _shown.TryGetValue(__instance, out Shown? shown) && shown.Alignment == alignment
                && _activeHoverTips!().TryGetValue(hitbox, out NHoverTipSet? panel) && panel == shown.Panel
                && GodotObject.IsInstanceValid(panel) && shown.Tips.SequenceEqual(text))
            {
                Reused++;
                return false;
            }
            __instance.ShowHoverTips(tips);
            Rebuilt++;
            if (text != null && _activeHoverTips!().TryGetValue(hitbox, out NHoverTipSet? built))
            {
                _shown.AddOrUpdate(__instance, new Shown { Tips = text, Alignment = alignment, Panel = built });
            }
            else
            {
                _shown.Remove(__instance);
            }
            return false;
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Warn($"[{ModEntry.Id}] {Name} disabled after exception (vanilla rebuilds from now on): {e}");
            return true;
        }
    }
}
