using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BigDeckOptimization.Bench;

/// <summary>
/// A player's mouse, as events through the game's real input path: the OS cursor never moves, and nothing reaches the session from the
/// real mouse (its window is off-screen). Scripted plays (<c>play</c>) call the card directly: no hover, no drag, no targeting.
/// </summary>
public static partial class AutoTest
{
    private static Vector2 _mouse;

    /// <summary>Script <c>forcefocus</c>: the game's input code runs as in a focused window (a session's window never has the focus).</summary>
    private static void ForceFocus()
    {
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(NGame), nameof(NGame.IsGameFocusedWindow)), prefix: new HarmonyMethod(typeof(AutoTest), nameof(GameIsFocused)));
        // Godot's root viewport reads the real cursor, not the last event: without this, a dragged card is wherever the player's mouse is.
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(Viewport), nameof(Viewport.GetMousePosition)), prefix: new HarmonyMethod(typeof(AutoTest), nameof(SimulatedViewportMouse)));
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(CanvasItem), nameof(CanvasItem.GetGlobalMousePosition)), prefix: new HarmonyMethod(typeof(AutoTest), nameof(SimulatedViewportMouse)));
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(CanvasItem), nameof(CanvasItem.GetLocalMousePosition)), prefix: new HarmonyMethod(typeof(AutoTest), nameof(SimulatedLocalMouse)));
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        // Windows only sends these when the real cursor enters: without them Godot doesn't look for what's under the mouse.
        tree.Root.Notification((int)Node.NotificationWMMouseEnter);
        tree.Root.Notification((int)Node.NotificationVpMouseEnter);
        Say("  input as in a focused window, mouse over the window");
    }

    private static bool GameIsFocused(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static Vector2 SimulatedCanvasMouse() => ((SceneTree)Engine.GetMainLoop()).Root.GetFinalTransform().AffineInverse() * _mouse;

    private static bool SimulatedViewportMouse(ref Vector2 __result)
    {
        __result = SimulatedCanvasMouse();
        return false;
    }

    private static bool SimulatedLocalMouse(CanvasItem __instance, ref Vector2 __result)
    {
        __result = __instance.GetGlobalTransform().AffineInverse() * SimulatedCanvasMouse();
        return false;
    }

    /// <summary>Script <c>controllermap default</c>: the game's default controller actions for this session, without saving them.</summary>
    private static void ControllerMapDefault()
    {
        NInputManager manager = NInputManager.Instance ?? throw new InvalidOperationException("no input manager");
        AccessTools.Field(typeof(NInputManager), "_controllerInputMap").SetValue(manager, manager.ControllerManager.GetDefaultControllerInputMap);
        Say("  controller map: the game's defaults");
    }

    /// <summary>
    /// Script <c>controllermap stale</c>: adds to this session's controller map actions the InputMap doesn't have, as a
    /// <c>settings.save</c> written by an older game version leaves them on v0.107 (v0.111 and later keep the defaults for such
    /// bindings, <c>MergeSavedControllerBindings</c>): <c>NInputManager._UnhandledInput</c> then asks about missing actions at every
    /// unhandled event, what ControllerActionSkip answers. Not saved.
    /// </summary>
    private static void ControllerMapStale()
    {
        NInputManager manager = NInputManager.Instance ?? throw new InvalidOperationException("no input manager");
        var field = AccessTools.Field(typeof(NInputManager), "_controllerInputMap");
        var map = new Dictionary<StringName, StringName>((Dictionary<StringName, StringName>)field.GetValue(manager)!);
        int added = 0;
        // Keys are the game's actions, values the controller actions bound to them: _UnhandledInput asks the event about each value.
        foreach (string action in new[] { "controller_d_pad_up", "controller_r_stick_up", "bdtest_removed_controller_action" })
        {
            if (!InputMap.HasAction(action))
            {
                map["bdtest_stale_binding_" + added] = action;
                added++;
            }
        }
        if (added == 0)
        {
            throw new InvalidOperationException("every stale action exists in this game's InputMap");
        }
        field.SetValue(manager, map);
        Say($"  controller map: {added} actions this game doesn't have added");
    }

    /// <summary>
    /// Script <c>buttoninput [expect-muted|expect-none]</c>: the buttons in the scene whose <c>_Input</c> is exactly NButton's (those
    /// IdleButtonInput may keep from input, same rule), how many are enabled, processing input, and enabled, not pressed and not
    /// processing input (muted: vanilla gives an enabled button its input). <c>expect-muted</c>: some must be muted (the mod on);
    /// <c>expect-none</c>: none may be (the mod off: the unmodified game). Otherwise the line fails.
    /// </summary>
    private static void ButtonInput(SceneTree tree, string expect)
    {
        var isPressed = AccessTools.FieldRefAccess<NClickableControl, bool>("_isPressed");
        int buttons = 0, enabled = 0, processing = 0, muted = 0;
        foreach (NButton button in FindAll<NButton>(tree.Root))
        {
            System.Reflection.MethodInfo? input = button.GetType().GetMethod("_Input",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, new[] { typeof(InputEvent) });
            if (input?.DeclaringType != typeof(NButton))
            {
                continue;
            }
            buttons++;
            bool on = button.IsEnabled, inputOn = button.IsProcessingInput();
            enabled += on ? 1 : 0;
            processing += inputOn ? 1 : 0;
            muted += on && !inputOn && !isPressed(button) ? 1 : 0;
        }
        Say($"  BUTTONINPUT {expect}: buttons={buttons} enabled={enabled} processing={processing} idleEnabledNotProcessing={muted}");
        if (expect == "expect-muted" && muted == 0)
        {
            throw new InvalidOperationException("no idle button is kept from input: IdleButtonInput didn't act (or no eligible button)");
        }
        if (expect == "expect-none" && (muted > 0 || enabled == 0))
        {
            throw new InvalidOperationException(enabled == 0 ? "no enabled button to look at" : $"{muted} enabled idle buttons still kept from input: not the unmodified game");
        }
    }

    /// <summary>Global canvas position to the window position an input event carries.</summary>
    private static Vector2 ToWindow(SceneTree tree, Vector2 global) => tree.Root.GetFinalTransform() * global;

    private static void MouseMotion(SceneTree tree, Vector2 global, MouseButtonMask buttons)
    {
        Vector2 position = ToWindow(tree, global);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = position, GlobalPosition = position, Relative = position - _mouse, ButtonMask = buttons });
        _mouse = position;
    }

    private static void MouseButton(SceneTree tree, Vector2 global, bool pressed)
    {
        Vector2 position = ToWindow(tree, global);
        Input.ParseInputEvent(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = Godot.MouseButton.Left, Pressed = pressed, ButtonMask = pressed ? MouseButtonMask.Left : 0 });
    }

    /// <summary>One motion per frame, in a line, over a time a hand takes: the same gesture whatever the frame rate.</summary>
    private static async Task MouseGlide(SceneTree tree, Vector2 from, Vector2 to, double seconds, MouseButtonMask buttons = 0)
    {
        double start = _clock.Elapsed.TotalSeconds;
        double t;
        do
        {
            t = Math.Min(1, (_clock.Elapsed.TotalSeconds - start) / seconds);
            MouseMotion(tree, from.Lerp(to, (float)t), buttons);
            await Frames(tree, 1);
        }
        while (t < 1);
    }

    /// <summary>Script <c>mousesweep &lt;s&gt;</c>: back and forth over the hand, the creatures and the top bar, a motion each frame.</summary>
    private static async Task MouseSweep(SceneTree tree, double seconds)
    {
        Vector2 size = tree.Root.GetVisibleRect().Size;
        double start = _clock.Elapsed.TotalSeconds;
        int focused = 0, frames = 0;
        while (_clock.Elapsed.TotalSeconds - start < seconds)
        {
            double t = _clock.Elapsed.TotalSeconds - start;
            float x = (float)(0.5 + 0.48 * Math.Sin(t * 2.1));
            float y = (float)(0.5 + 0.46 * Math.Sin(t * 0.7));
            MouseMotion(tree, new Vector2(size.X * x, size.Y * y), 0);
            await Frames(tree, 1);
            frames++;
            focused += NPlayerHand.Instance?.FocusedHolder != null ? 1 : 0;
        }
        Say($"  mouse swept {frames} frames, over a hand card for {focused}");
    }

    /// <summary>Script <c>mouseplay &lt;n&gt;</c>: hovers a playable card, drags it, targets, releases, as a player does. Logs PLAYTIME from the release.</summary>
    private static async Task MousePlay(SceneTree tree, int count)
    {
        int played = 0;
        Vector2 size = tree.Root.GetVisibleRect().Size;
        Vector2 at = size / 2;
        for (int i = 0; i < count; i++)
        {
            await Until(tree, () => !CombatManager.Instance.IsInProgress || IsIdle(), 120, "idle");
            Player? me = Me();
            CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
            NPlayerHand? hand = NPlayerHand.Instance;
            if (!CombatManager.Instance.IsInProgress || me?.PlayerCombatState == null || combat == null || hand == null)
            {
                break;
            }
            Creature? enemy = combat.Enemies.FirstOrDefault(e => e.IsAlive);
            // Cards overlap in the hand: what counts is the one the mouse really ends up on, as for a player.
            NHandCardHolder? holder = null;
            Vector2 over = at;
            foreach (NHandCardHolder candidate in hand.ActiveHolders.ToList())
            {
                Vector2 point = candidate.Hitbox.GetGlobalRect().GetCenter();
                await MouseGlide(tree, over, point, 0.15);
                over = point;
                await Seconds(tree, 0.15);
                if (hand.FocusedHolder is { CardModel: { } hovered } focused && !_avoid.Contains(hovered.Id.Entry) && (hovered.CanPlayTargeting(enemy) || hovered.CanPlayTargeting(null)))
                {
                    holder = focused;
                    break;
                }
            }
            if (holder == null)
            {
                Say("  no playable card left");
                break;
            }
            CardModel card = holder.CardModel!;
            bool targeted = card.CanPlayTargeting(enemy);
            MouseButton(tree, over, true);
            await Frames(tree, 3);
            NCreature? node = targeted ? NCombatRoom.Instance?.GetCreatureNode(enemy) : null;
            Vector2 aim = node != null ? node.Hitbox.GetGlobalRect().GetCenter() : new Vector2(size.X / 2, size.Y * 0.35f);
            await MouseGlide(tree, over, aim, 0.2, MouseButtonMask.Left);
            await Seconds(tree, 0.1);
            MouseButton(tree, aim, false);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            at = aim;
            await Frames(tree, 5);
            await Until(tree, () => !CombatManager.Instance.IsInProgress || IsIdle(), 120, "idle");
            // A card that wasn't played is still in the hand: a failed drag must not pass for a play.
            bool left = card.Pile?.Type != MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand;
            Say($"  MOUSEPLAY {card.Id.Entry} played {left} target {node != null}");
            if (left)
            {
                Say($"  PLAYTIME {card.Id.Entry} {watch.Elapsed.TotalMilliseconds:F0} ms");
                played++;
            }
        }
        Say($"  played {played} of {count} with the mouse");
    }
}
