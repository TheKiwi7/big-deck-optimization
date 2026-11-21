using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace BigDeckOptimization.Patches;

/// <summary>
/// While the mouse is on a creature, the game rebuilds its hover tips (one panel per power and intent, rich text with
/// effects) after every combat state change: with huge decks, actions change the state many times per second, and the
/// rebuilds were ~half of the post-action UI refresh at 8,400 cards.
///
/// Decided inside <c>NCreature.ShowHoverTips</c>' body (2026-11-21): a transpiler (at <see cref="Priority.Last"/>: it reads
/// what other mods' transpilers left, and leaves code it doesn't recognise as it is) replaces its <c>HideHoverTips()</c> with
/// <see cref="HideFirst"/> and its <c>NHoverTipSet.CreateAndShow(Hitbox, hoverTips, alignment)</c> with <see cref="Show"/>,
/// the first one's answer kept in a local. <see cref="HideFirst"/> runs the game's hide in place, and the game's
/// CreateAndShow then gets the body's own argument, whenever no reuse is possible: the part off, a back-off, tips blocked or
/// hidden, no combat. Otherwise <see cref="Show"/> gets the final argument (what every other mod's prefix left: they all run
/// before the body as in the game) and the alignment the game computed. The tips are read once (the list passed on to
/// CreateAndShow: its Init reads them once too; a throw while reading is replayed to Init at the same item) and compared with
/// the ones of the panel registered for the hitbox right now (every field of each tip: title, description with its numbers,
/// icon, debuff look...). Only when they are all identical, that panel is still the one built for them and everything the
/// game places it from is unchanged (see <see cref="Placement"/>) is the rebuild left out; any difference rebuilds it with the
/// game's two calls. Every caller goes through it (the state-change handler, focus, NIntent, NPower, NCreatureStateDisplay):
/// the comparison is with the panel actually registered, so a panel another caller built or removed is never kept. UI only.
/// On that path the game's order changes in one place: its <c>HideHoverTips()</c> runs after the hitbox, alignment and tips
/// reads (the node's own position and viewport, the tips; none reads the panels).
/// Residual: another mod's transpiler registered at <see cref="Priority.Last"/> after this mod's runs after it and sees the
/// helper calls; it is a transpiler, so the part backs off at run time (the game's sequence through the helpers, its change
/// kept), as for inner patches.
///
/// The hover-tip display premise (owner-approved 2026-11-17; DESIGN "Owner-approved premises"): other mods' patches on the
/// panel's own methods a reuse doesn't run (<see cref="SkippedNames"/>, <c>NHoverTipSet</c> only: building, placing, registering
/// and freeing the panel) only change what the tips show; a reused panel keeps what they added when it was built (a decoration
/// that depends on state outside the tips may stay as it was until the tips change: taken as display). What a reuse also leaves
/// out, under the same premise (re-audit of the premises, D1): what building the panel runs besides those methods, the panel's
/// constructor and <c>_Ready</c>, the scripts of the tip scenes it instantiates (<c>hover_tip.tscn</c>), <c>AddChildSafely</c> and the
/// labels' <c>SetTextAutoSize</c>. <c>NCreature.ShowHoverTips</c> itself: <see cref="PatchTolerance.AnyPrefix"/> (every prefix
/// stands, HonkaiLib's and TH_Patchouli's tips rewritten by reference, St2bs' false-returning one; postfixes, finalizers
/// (pass-through ones included), transpilers and inner patches back off; sound because every callee a reuse skips is judged
/// here: the hide strictly, the panel's methods under the premise). Strict still: <c>NCreature.HideHoverTips</c>, the
/// fingerprints, the comparison, the "seen" marks the build makes for a tip of a card, relic or potion (<see cref="_marks"/>:
/// the game's are idempotent, another mod's patch on them is a save write a reuse would leave out: such a panel is rebuilt),
/// and every one of these methods with <see cref="PatchSafety.ShapeRules"/> off.
/// </summary>
public static class HoverTipReuse
{
    public const string Name = "HoverTipReuse";

    /// <summary>v0.107.1 to v0.113.1 code mirrored or relied on: the show transpiled, the hide it calls, and the panel build.</summary>
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
    /// <summary><c>NCreature.ShowHoverTips</c>, transpiled: judged with <see cref="PatchTolerance.AnyPrefix"/>.</summary>
    private static MethodInfo? _show;
    private static MethodInfo? _hide;
    private static MethodInfo? _createAndShow;
    private static int _replaced;
    /// <summary>What a reuse skips calling, strict whatever the premise: the creature's own hide.</summary>
    private static MethodBase[] _skipped = Array.Empty<MethodBase>();
    /// <summary>The panel's methods a reuse skips (<see cref="SkippedNames"/>): under the display premise with the shape rules on.</summary>
    private static MethodBase[] _panel = Array.Empty<MethodBase>();

    /// <summary>
    /// The "seen" marks <c>NHoverTipSet.Init</c> makes for each tip whose canonical model is a card, relic or potion
    /// (<c>SaveManager.Mark*AsSeen</c>, the progress manager's and <c>ProgressState</c>'s they call, every version). The game's own
    /// only add an id once (a second call does nothing), so a reuse of equal tips leaves out no-ops; another mod's patch there
    /// (RandomForeseer's prefix on <c>MarkCardAsSeen</c>) may decide otherwise at another time: a panel with such a tip is rebuilt.
    /// </summary>
    private static MethodBase[] _marks = Array.Empty<MethodBase>();

    /// <summary>A tip of this panel would be marked seen by the build, and another mod patches a mark (or one can't be found).</summary>
    private static bool MarksPatched(List<HoverTip> tips)
    {
        bool marked = false;
        foreach (HoverTip tip in tips)
        {
            if (((IHoverTip)tip).CanonicalModel is MegaCrit.Sts2.Core.Models.CardModel or MegaCrit.Sts2.Core.Models.RelicModel or MegaCrit.Sts2.Core.Models.PotionModel)
            {
                marked = true;
                break;
            }
        }
        if (!marked)
        {
            return false;
        }
        if (_marks.Length == 0)
        {
            return true;
        }
        foreach (MethodBase method in _marks)
        {
            // No tolerance: a save write a reuse leaves out.
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The methods checked strictly now: the creature's hide, and the panel's with the shape rules off.</summary>
    private static MethodBase[] Strict() => PatchSafety.ShapeRules ? _skipped : _skipped.Concat(_panel).ToArray();

    private static bool SkippedPatched()
    {
        foreach (MethodBase method in _skipped)
        {
            // No tolerance: the hide a reuse leaves out.
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        if (!PatchSafety.ShapeRules)
        {
            // As before the premise: the panel's methods strict too.
            foreach (MethodBase method in _panel)
            {
                if (PatchSafety.IsPatchedByOthers(method))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Another mod's patch keeps the game's two calls (asked at each call: a mod loading later may patch them too).</summary>
    private static bool BackedOff() => PatchSafety.IsPatchedByOthers(_show, PatchTolerance.AnyPrefix) || SkippedPatched();

    public static void Apply(Harmony harmony)
    {
        MethodInfo show = Reflect.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips))
            ?? throw new MissingMethodException("NCreature.ShowHoverTips");
        MethodInfo hide = Reflect.Method(typeof(NCreature), nameof(NCreature.HideHoverTips))
            ?? throw new MissingMethodException("NCreature.HideHoverTips");
        PatchSafety.RequireFingerprint(show, ShowFingerprint);
        PatchSafety.RequireFingerprint(hide, HideFingerprint);
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
        _createAndShow = Reflect.DeclaredMethod(typeof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShow),
                new[] { typeof(Control), typeof(IEnumerable<IHoverTip>), typeof(HoverTipAlignment) })
            ?? throw new MissingMethodException(nameof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShow));
        _show = show;
        _hide = hide;
        // What a reuse skips calling: another mod's patch on the creature's hide keeps the rebuilds; on the panel's own methods
        // (adding content to the tips, or placing them) too with the shape rules off, under the display premise with them.
        _skipped = new MethodBase[] { hide };
        _panel = typeof(NHoverTipSet).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => SkippedNames.Contains(m.Name)).Cast<MethodBase>().ToArray();
        // All of them found, or none trusted (MarksPatched answers true for a marked tip then).
        MethodBase?[] marks =
        {
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "MarkCardAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "MarkRelicAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "MarkPotionAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager), "MarkCardAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager), "MarkRelicAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager), "MarkPotionAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.ProgressState), "MarkCardAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.ProgressState), "MarkRelicAsSeen"),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.ProgressState), "MarkPotionAsSeen"),
        };
        _marks = marks.All(m => m != null) ? marks.Cast<MethodBase>().ToArray() : Array.Empty<MethodBase>();
        if (_marks.Length == 0)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: the hover tips' seen marks not found: panels with a card, relic or potion tip are rebuilt");
        }
        var field = Reflect.Field(typeof(NHoverTipSet), "_activeHoverTips") ?? throw new MissingFieldException("NHoverTipSet._activeHoverTips");
        var active = (Dictionary<Control, NHoverTipSet>)field.GetValue(null)!;
        _activeHoverTips = () => active;
        _replaced = 0;
        // Priority.Last: other mods' transpilers run first; this one judges their output (the game's shape or nothing changed).
        PatchGuard.Patch(harmony, show, transpiler: new HarmonyMethod(typeof(HoverTipReuse), nameof(Transpiler)) { priority = Priority.Last });
        if (_replaced != 1)
        {
            // ModEntry.TryApply removes the transpiler that replaced nothing.
            throw new PatchSkippedException($"NCreature.ShowHoverTips: {_replaced} hide-then-show sequences found where 1 was expected, left vanilla");
        }
        Applied = true;
        // The body transpiled: every prefix of another mod runs before it as in the game (AnyPrefix); the rest backs off.
        PatchSafety.Watch(Name, show, PatchTolerance.AnyPrefix);
        PatchSafety.Watch(Name, Strict());
        // Display only: one gate in front of all it does, any of these back-offs keeps the whole patch vanilla (the menu's red title).
        PatchSafety.WholePatch(Name);
        // Other mods' patches on the panel's methods let stand by the premise: a kept note (bdopt why, the menu's hover).
        PatchSafety.WatchNote(Name, NoteDisplay);
    }

    /// <summary>
    /// <c>ldarg.0; call HideHoverTips; ...; call NHoverTipSet.CreateAndShow(Control, IEnumerable, HoverTipAlignment)</c> becomes
    /// <c>ldarg.0; call HideFirst(NCreature); stloc hidden; ...; ldarg.0; ldloc hidden; call Show(Control, IEnumerable,
    /// HoverTipAlignment, NCreature, bool)</c>: HideFirst hides in place when no reuse is possible, Show gets CreateAndShow's own
    /// three arguments, the creature and that answer, and returns what CreateAndShow returns (popped as before). Only when the code
    /// it is handed (the game's, after any other mod's transpiler: this one runs last) has exactly that shape (one hide on the
    /// instance, one CreateAndShow after it, no branch landing between them); otherwise nothing changes (at load: the patch is
    /// left off; later, another mod's transpiler changed that code: it stays as that mod left it, and the part backs off).
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        List<CodeInstruction> code = instructions.ToList();
        int hide = -1, create = -1, hides = 0, creates = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Calls(_hide))
            {
                hides++;
                hide = i;
            }
            else if (code[i].Calls(_createAndShow))
            {
                creates++;
                create = i;
            }
        }
        if (hides != 1 || creates != 1 || hide < 1 || create < hide || code[hide - 1].opcode != OpCodes.Ldarg_0
            || code[hide].labels.Count > 0 || code.Skip(hide + 1).Take(create - hide).Any(c => c.labels.Count > 0 || c.blocks.Count > 0)
            || code[hide - 1].blocks.Count > 0 || code[hide].blocks.Count > 0)
        {
            return code;
        }
        // The branch over the card play lands on the ldarg.0: it stays, labels and all.
        LocalBuilder hidden = generator.DeclareLocal(typeof(bool));
        code[hide] = new CodeInstruction(OpCodes.Call, Reflect.Method(typeof(HoverTipReuse), nameof(HideFirst)));
        code.Insert(hide + 1, new CodeInstruction(OpCodes.Stloc, hidden));
        create++;
        code[create] = new CodeInstruction(OpCodes.Call, Reflect.Method(typeof(HoverTipReuse), nameof(Show)));
        code.Insert(create, new CodeInstruction(OpCodes.Ldloc, hidden));
        code.Insert(create, new CodeInstruction(OpCodes.Ldarg_0));
        _replaced++;
        return code;
    }

    /// <summary>
    /// The kept note of the display premise: which other mods' patches (or hidden IL hooks) on the panel's methods don't run
    /// when a creature's unchanged tips are kept on screen. Cleared with the shape rules off, when there is none, and while the
    /// show or the hide backs off (no reuse then: the back-off shows instead).
    /// </summary>
    private static void NoteDisplay()
    {
        var patched = new List<MethodBase>();
        // Only while panels can be reused (G, re-audit of the premises): not once faulted (with the mod off: what would run once on).
        if (PatchSafety.ShapeRules && Applied && !PatchSafety.IsFaulted(Name) && _show != null && !PatchSafety.IsPatchedByOthers(_show, PatchTolerance.AnyPrefix)
            && !_skipped.Any(PatchSafety.HasOtherPatches))
        {
            patched.AddRange(_panel.Where(PatchSafety.HasOtherPatches));
        }
        if (patched.Count == 0)
        {
            PatchSafety.ClearKept(Name, "tips_display");
            return;
        }
        var mods = new List<string>(PatchSafety.OtherModsPatching(patched));
        foreach (MethodBase method in patched)
        {
            mods.AddRange(HarmonyWatch.HiddenOwners(method));
        }
        string names = string.Join(", ", patched.Select(m => $"{m.DeclaringType?.Name}.{m.Name}").Distinct());
        PatchSafety.Kept(Name, "tips_display", $"other mods' code on the hover-tip panel ({names}) doesn't run while a creature's unchanged "
            + "tips are kept on screen, nor what building the panel runs (its nodes' scripts and setup, their text sizing, adding it to the "
            + "screen): taken as display only (a stated premise: what other mods add to hover tips is display; it may stay as it was until the "
            + "tips change; a panel whose tips would be marked seen is rebuilt while another mod patches those marks)", mods.ToArray());
    }

    /// <summary>Where the game would place the panel from now; null when it couldn't (no game node: never reused).</summary>
    private static Placement? PlacementOf(Control hitbox) => NGame.Instance is { } game
        ? new Placement(hitbox.GlobalPosition, hitbox.Size, hitbox.Scale, game.GetViewportRect().Size)
        : null;

    /// <summary>The game's two calls, as its body makes them.</summary>
    private static NHoverTipSet? GameShow(NCreature creature, Control hitbox, IEnumerable<IHoverTip> hoverTips, HoverTipAlignment alignment)
    {
        creature.HideHoverTips();
        return NHoverTipSet.CreateAndShow(hitbox, hoverTips, alignment);
    }

    /// <summary>
    /// In place of the body's <c>HideHoverTips()</c>: true, after the game's hide, when no reuse is possible (the part off or
    /// faulted, another mod's patch it backs off from, tips blocked or hidden, no combat); <see cref="Show"/> then makes the game's
    /// CreateAndShow on the body's own argument, so the game's sequence runs as it is. False (nothing done yet) otherwise.
    /// </summary>
    internal static bool HideFirst(NCreature creature)
    {
        bool game;
        try
        {
            // Asked at each call: a mod loading later may patch them too. Tips blocked (a potion popup open, targeting) or hidden
            // (the debug toggle): the game removes the panel and builds none (its CreateAndRegister returns before reading the
            // tips) or a hidden one, which a reuse would keep on screen. Out of combat the creature's tips are empty and the panel
            // isn't refreshed by state changes.
            game = !Enabled || BackedOff() || creature.Entity?.CombatState == null || NHoverTipSet.shouldBlockHoverTips || NGame.IsDebugHidingHoverTips;
        }
        catch (Exception e)
        {
            Fault(e);
            game = true;
        }
        if (game)
        {
            // The game's own call, in its place, outside any try of this mod.
            creature.HideHoverTips();
        }
        return game;
    }

    /// <summary>
    /// The tips read so far, then what reading them threw: CreateAndShow's Init meets the same exception at the same item as when
    /// it reads the argument itself (the argument read once, as in the game).
    /// </summary>
    private sealed class Replay(List<IHoverTip> read, System.Runtime.ExceptionServices.ExceptionDispatchInfo thrown) : IEnumerable<IHoverTip>
    {
        public IEnumerator<IHoverTip> GetEnumerator()
        {
            foreach (IHoverTip tip in read)
            {
                yield return tip;
            }
            thrown.Throw();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// In place of the body's <c>NHoverTipSet.CreateAndShow(hitbox, hoverTips, alignment)</c>. After <see cref="HideFirst"/> hid
    /// (<paramref name="hidden"/>): the game's call on its own argument. Otherwise both the hide and the build are left out when the
    /// panel registered for the hitbox shows exactly these tips, placed from the same values; else the game's two calls.
    /// </summary>
    internal static NHoverTipSet? Show(Control hitbox, IEnumerable<IHoverTip> hoverTips, HoverTipAlignment alignment, NCreature creature, bool hidden)
    {
        if (hidden)
        {
            return NHoverTipSet.CreateAndShow(hitbox, hoverTips, alignment);
        }
        if (hoverTips == null)
        {
            // Init throws on it, as in the game.
            return GameShow(creature, hitbox, hoverTips!, alignment);
        }
        List<IHoverTip> tips = new();
        try
        {
            // Read once, as CreateAndShow's Init reads its argument (one foreach through RemoveDupes): that list goes on to it.
            foreach (IHoverTip tip in hoverTips)
            {
                tips.Add(tip);
            }
        }
        catch (Exception e)
        {
            // The game's two calls; Init meets what reading threw, at the same item, without the argument being read again.
            return GameShow(creature, hitbox, new Replay(tips, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e)), alignment);
        }
        List<HoverTip>? text = null;
        Placement? placement = null;
        try
        {
            List<IHoverTip> unique = IHoverTip.RemoveDupes(tips).ToList();
            text = unique.All(t => t is HoverTip)
                ? unique.Cast<HoverTip>().ToList()
                : null; // Card previews: no field-by-field comparison, always rebuilt.
            // Exact values (Vector2 equality): a creature moved (enemies laid out again for a summon, pets placed again) or resized
            // (Osty growing) has its panel placed again by the game, so it is rebuilt.
            placement = PlacementOf(hitbox);
            if (text != null && placement is { } now && _shown.TryGetValue(creature, out Shown? shown) && shown.Alignment == alignment
                && shown.Placement == now
                && _activeHoverTips!().TryGetValue(hitbox, out NHoverTipSet? panel) && panel == shown.Panel
                && GodotObject.IsInstanceValid(panel) && shown.Tips.SequenceEqual(text) && !MarksPatched(text))
            {
                Reused++;
                return panel;
            }
        }
        catch (Exception e)
        {
            Fault(e);
            text = null;
        }
        // The game's two calls, outside any try of this mod: what they throw reaches the game's code unchanged.
        NHoverTipSet? built = GameShow(creature, hitbox, tips, alignment);
        Rebuilt++;
        try
        {
            if (text != null && placement is { } at && _activeHoverTips!().TryGetValue(hitbox, out NHoverTipSet? panelBuilt))
            {
                _shown.AddOrUpdate(creature, new Shown { Tips = text, Alignment = alignment, Placement = at, Panel = panelBuilt });
            }
            else
            {
                // Never matched again anyway once the panel registered is another: let it go.
                _shown.Remove(creature);
            }
        }
        catch (Exception e)
        {
            Fault(e);
        }
        return built;
    }

    private static void Fault(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Warn($"[{ModEntry.Id}] {Name} disabled after exception (vanilla rebuilds from now on): {e}");
    }
}
