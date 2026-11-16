using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
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
/// identical, the panel shown is still the one built for them and everything the game places it from is unchanged
/// (see <see cref="Placement"/>) is the rebuild skipped; any difference rebuilds it like the game does. UI only.
/// </summary>
public static class HoverTipReuse
{
    public const string Name = "HoverTipReuse";

    /// <summary>v0.107.1 code mirrored: the state-change handler and the show/hide it calls, and the panel build.</summary>
    private const string HandlerFingerprint = "17A5EAD081E993CC";
    private const string ShowFingerprint = "52AF78E3A3104784";
    private const string HideFingerprint = "3EF252278767F901";
    private const string InitFingerprint = "CF7925571216B6E1";

    /// <summary>
    /// The rest of what a reuse skips and what its comparison assumes, per game version (v0.107.1 and v0.111.0 share their code, v0.113.0
    /// and v0.113.1 split the build into <c>CreateAndRegister</c> and the placement into a <c>Rect2</c> overload): the panel is registered
    /// under its owner and freed by <c>Remove</c>, and placing it reads only the owner's global position, size and scale, the viewport's
    /// size and the panel's own containers. A method missing on this version is skipped; one with other code leaves the patch off.
    /// </summary>
    private static readonly (string Method, Type[] Parameters, bool OnlySome, string[] Fingerprints)[] Pinned =
    {
        ("CreateAndShow", new[] { typeof(Control), typeof(IEnumerable<IHoverTip>), typeof(HoverTipAlignment) }, false,
            new[] { "B2635D85E5E32995" /* v0.107.1, v0.111.0 */, "BE8AE2D8FAA82189" /* v0.113.0, v0.113.1 */ }),
        ("Remove", new[] { typeof(Control) }, false,
            new[] { "5400AFCD9E85C80C" /* v0.107.1, v0.111.0 */, "1ECE15B8F87D6D35" /* v0.113.0, v0.113.1 */ }),
        ("SetAlignment", new[] { typeof(Control), typeof(HoverTipAlignment) }, false,
            new[] { "70516C5EC55A7470" /* v0.107.1, v0.111.0 */, "193E48B81EAAA68B" /* v0.113.0 */, "F5729F9DEFF10EF1" /* v0.113.1 */ }),
        ("SetAlignment", new[] { typeof(Rect2), typeof(HoverTipAlignment) }, true, new[] { "46A0C179E0C89C26" /* v0.113.0, v0.113.1 */ }),
        ("CreateAndRegister", new[] { typeof(Control), typeof(IEnumerable<IHoverTip>) }, true, new[] { "76E0E4BDEDAC4594" /* v0.113.0, v0.113.1 */ }),
        ("ReplaceExistingTip", new[] { typeof(Control) }, true, new[] { "F9CFF9A2C869158C" /* v0.113.0, v0.113.1 */ }),
        ("ConnectTreeExiting", new[] { typeof(Control) }, true, new[] { "D31DF1F437953E4F" /* v0.113.0, v0.113.1 */ }),
        ("CorrectVerticalOverflow", Type.EmptyTypes, false, new[] { "3C61A082CA4BA0FF" /* every version */ }),
        ("CorrectHorizontalOverflow", Type.EmptyTypes, false, new[] { "EB44F2FCCC825F3E" /* every version */ }),
    };

    /// <summary>NHoverTipSet methods a reuse no longer runs: another mod's patch on any of them keeps the rebuilds.</summary>
    private static readonly HashSet<string> SkippedNames = new()
    {
        "CreateAndShow", "Remove", "Init", "SetAlignment", "CreateAndRegister", "ReplaceExistingTip", "ConnectTreeExiting",
        "CorrectVerticalOverflow", "CorrectHorizontalOverflow",
    };

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Rebuilt { get; private set; }

    /// <summary>
    /// What the game places a creature's panel from (<c>SetAlignment</c> and its overflow corrections, every version): the hitbox's global
    /// position, size and local scale, and the viewport's size. The panel's own container sizes follow from its tips, compared apart.
    /// </summary>
    private readonly record struct Placement(Vector2 Position, Vector2 Size, Vector2 Scale, Vector2 Viewport);

    private sealed class Shown
    {
        public required List<HoverTip> Tips;
        public required HoverTipAlignment Alignment;
        public required Placement Placement;
        public required NHoverTipSet Panel;
    }

    private static readonly ConditionalWeakTable<NCreature, Shown> _shown = new();
    private static Func<Dictionary<Control, NHoverTipSet>>? _activeHoverTips;
    private static MethodInfo? _handler;
    private static MethodBase[] _skipped = Array.Empty<MethodBase>();

    private static bool SkippedPatched()
    {
        foreach (MethodBase method in _skipped)
        {
            // No tolerance: the rebuild these methods do is skipped.
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    public static void Apply(Harmony harmony)
    {
        var handler = Reflect.Method(typeof(NCreature), "ShowCreatureHoverTips")
            ?? throw new MissingMethodException("NCreature.ShowCreatureHoverTips");
        PatchSafety.RequireFingerprint(handler, HandlerFingerprint);
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips)), ShowFingerprint);
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(NCreature), nameof(NCreature.HideHoverTips)), HideFingerprint);
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(NHoverTipSet), "Init"), InitFingerprint);
        foreach ((string name, Type[] parameters, bool onlySome, string[] fingerprints) in Pinned)
        {
            MethodInfo? method = Reflect.DeclaredMethod(typeof(NHoverTipSet), name, parameters);
            if (method == null)
            {
                if (onlySome)
                {
                    continue;
                }
                throw new MissingMethodException(nameof(NHoverTipSet), name);
            }
            string actual = MethodFingerprint.Of(method);
            if (!fingerprints.Any(expected => PatchSafety.FingerprintMatches(actual, expected)))
            {
                throw new PatchSkippedException($"NHoverTipSet.{name} changed (fingerprint {actual}, expected one of {string.Join(", ", fingerprints)}): left vanilla");
            }
        }
        _handler = handler;
        // What a reuse skips calling: another mod's patch there (e.g. adding content to the tips, or placing them) keeps the rebuilds.
        _skipped = new MethodBase[]
            {
                Reflect.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips))!,
                Reflect.Method(typeof(NCreature), nameof(NCreature.HideHoverTips))!,
            }
            .Concat(typeof(NHoverTipSet).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => SkippedNames.Contains(m.Name)))
            .ToArray();
        var field = Reflect.Field(typeof(NHoverTipSet), "_activeHoverTips") ?? throw new MissingFieldException("NHoverTipSet._activeHoverTips");
        var active = (Dictionary<Control, NHoverTipSet>)field.GetValue(null)!;
        _activeHoverTips = () => active;
        PatchGuard.Patch(harmony, handler, prefix: new HarmonyMethod(typeof(HoverTipReuse), nameof(Prefix)));
        Applied = true;
        // What Prefix asks about (display only).
        PatchSafety.Watch(Name, handler);
        PatchSafety.Watch(Name, _skipped);
        // Display only: one gate in front of all it does, any of these back-offs keeps the whole patch vanilla (the menu's red title).
        PatchSafety.WholePatch(Name);
    }

    /// <summary>Where the game would place the panel from now; null when it couldn't (no game node: never reused).</summary>
    private static Placement? PlacementOf(Control hitbox) => NGame.Instance is { } game
        ? new Placement(hitbox.GlobalPosition, hitbox.Size, hitbox.Scale, game.GetViewportRect().Size)
        : null;

    /// <summary>Does what ShowCreatureHoverTips does, skipping the rebuild when nothing shown would change.</summary>
    private static bool Prefix(NCreature __instance)
    {
        // Asked at each call: a mod loading later may patch it too.
        // No tolerance: the handler's rebuild is skipped.
        if (!Enabled || PatchSafety.IsPatchedByOthers(_handler) || SkippedPatched())
        {
            return true;
        }
        try
        {
            // Cases where the game shows nothing new: let it run as is. Tips blocked (a potion popup open, targeting) or hidden
            // (the debug toggle): the game removes the panel and builds none or a hidden one, which a reuse would keep on screen.
            // The entry left in _shown is never matched again: the panel it names is no longer the one registered.
            if (__instance.Entity.CombatState == null || NCombatRoom.Instance is not { } room || room.Ui.Hand.InCardPlay
                || NHoverTipSet.shouldBlockHoverTips || NGame.IsDebugHidingHoverTips)
            {
                return true;
            }
            List<IHoverTip> tips = __instance.Entity.HoverTips.ToList();
            List<IHoverTip> unique = IHoverTip.RemoveDupes(tips).ToList();
            List<HoverTip>? text = unique.All(t => t is HoverTip)
                ? unique.Cast<HoverTip>().ToList()
                : null; // Card previews: no field-by-field comparison, always rebuilt.
            HoverTipAlignment alignment = HoverTip.GetHoverTipAlignment(__instance, 0.5f);
            Control hitbox = __instance.Hitbox;
            // Exact values (Vector2 equality): a creature moved (enemies laid out again for a summon, pets placed again) or resized (Osty
            // growing) has its panel placed again by the game, so it is rebuilt.
            Placement? placement = PlacementOf(hitbox);
            if (text != null && placement is { } now && _shown.TryGetValue(__instance, out Shown? shown) && shown.Alignment == alignment
                && shown.Placement == now
                && _activeHoverTips!().TryGetValue(hitbox, out NHoverTipSet? panel) && panel == shown.Panel
                && GodotObject.IsInstanceValid(panel) && shown.Tips.SequenceEqual(text))
            {
                Reused++;
                return false;
            }
            __instance.ShowHoverTips(tips);
            Rebuilt++;
            if (text != null && placement is { } built && _activeHoverTips!().TryGetValue(hitbox, out NHoverTipSet? panelBuilt))
            {
                _shown.AddOrUpdate(__instance, new Shown { Tips = text, Alignment = alignment, Placement = built, Panel = panelBuilt });
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
