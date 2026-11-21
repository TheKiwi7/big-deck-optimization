using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic prefixes of "another mod" on <c>NCreature.ShowHoverTips</c>, for HoverTipReuse's <see cref="PatchTolerance.PrefixesOnly"/>
/// (2026-11-21): installed through <c>foreignpatch &lt;shape&gt; [priority N]</c> as ForeignPatches' own shapes (removed by
/// <c>foreignpatch remove</c>). Both must be let stand (a kept log line, no back-off) and the panels still reused.
/// <list type="bullet">
/// <item><c>tips-append-prefix</c>: HonkaiLib's shape (<c>void Prefix(NCreature __instance, ref IEnumerable&lt;IHoverTip&gt; hoverTips)</c>):
/// an enemy's tips get one more, appended lazily (<c>Concat</c>, as its imaginary-link prefix does), a fresh tip at each call whose
/// text holds the combat's round: equal within a round (the panel may be kept), different at the next one (it must be rebuilt).</item>
/// <item><c>tips-false-prefix</c>: St2bs' shape (a bool prefix returning false: the body skipped, the panel left as it was), returning
/// false only while <c>foreignpatch tips-block on</c> is set (its false returns counted).</item>
/// </list>
/// <c>foreignpatch tips-check &lt;label&gt; [enemy index]</c>: the enemy's panel on screen must show exactly the tips the game's body
/// gets (its own tips and the appended one while tips-append-prefix is installed, duplicates removed as the panel's build removes
/// them), else the line fails (TIPS MISMATCH); a panel the game hid is reported, not compared. <c>foreignpatch tips-report</c>:
/// the counts (FOREIGN lines, display only: not compared).
/// </summary>
public static class ForeignShapesHover
{
    public static readonly string[] Shapes = { "tips-append-prefix", "tips-false-prefix" };

    public static bool Handles(string[] parts) => parts.Length >= 1 && (Shapes.Contains(parts[0]) || parts[0] is "tips-block" or "tips-check" or "tips-report");

    /// <summary>Whether tips-append-prefix is on ShowHoverTips now (asked of Harmony: <c>foreignpatch remove</c> takes it away).</summary>
    private static bool AppendInstalled() =>
        AccessTools.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips)) is { } show && Harmony.GetPatchInfo(show) is { } info
        && info.Prefixes.Any(p => p.owner == ForeignPatches.IdPrefix + "tips-append-prefix");

    private static bool _block;
    private static long _appended, _falseReturns, _checked;

    public static IReadOnlyList<string> Run(string[] parts, Action<string, MethodBase> installed)
    {
        switch (parts[0])
        {
            case "tips-block" when parts.Length > 1 && parts[1] is "on" or "off":
                _block = parts[1] == "on";
                return new[] { $"FOREIGN tips-block {parts[1]}: tips-false-prefix returns {(_block ? "false (the body skipped)" : "true")}" };
            case "tips-report":
                return new[] { $"FOREIGN tips appended {_appended}, false returns {_falseReturns}, panels checked {_checked}; HoverTipReuse reused {HoverTipReuse.Reused}, rebuilt {HoverTipReuse.Rebuilt}" };
            case "tips-check":
                return Check(parts.Length > 1 ? parts[1] : "now", parts.Length > 2 ? int.Parse(parts[2]) : 0);
        }
        if (!Shapes.Contains(parts[0]))
        {
            throw new ArgumentException("foreignpatch tips-append-prefix|tips-false-prefix [priority N] | tips-block on|off | tips-check <label> [enemy] | tips-report");
        }
        MethodBase target = AccessTools.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips))
            ?? throw new MissingMethodException(nameof(NCreature), nameof(NCreature.ShowHoverTips));
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string shape = parts[0];
        string id = ForeignPatches.IdPrefix + shape;
        new Harmony(id).Patch(target, prefix: new HarmonyMethod(typeof(ForeignShapesHover), shape == "tips-append-prefix" ? nameof(AppendPrefix) : nameof(FalsePrefix)) { priority = priority });
        installed(id, target);
        var lines = new List<string> { $"FOREIGN installed {shape} on NCreature.ShowHoverTips as {id} (priority {priority})" };
        lines.AddRange(PatchSafety.Describe(target).Select(line => "FOREIGN classify NCreature.ShowHoverTips: " + line));
        return lines;
    }

    /// <summary>The tip appended to an enemy's tips: its text holds the combat's round (the same object never: a fresh one per call).</summary>
    private static HoverTip Synthetic() =>
        new(new LocString("static_hover_tips", "DRAW_PILE.title"), $"BDTEST synthetic tip, round {CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0}");

    /// <summary>HonkaiLib's shape: the argument rewritten by reference, lazily extended.</summary>
    public static void AppendPrefix(NCreature __instance, ref IEnumerable<IHoverTip> hoverTips)
    {
        if (__instance?.Entity is not { IsEnemy: true })
        {
            return;
        }
        _appended++;
        hoverTips = (hoverTips ?? Array.Empty<IHoverTip>()).Concat(new IHoverTip[] { Synthetic() });
    }

    /// <summary>St2bs' shape: false skips the body, the panel left as it was.</summary>
    public static bool FalsePrefix(NCreature __instance)
    {
        if (_block && __instance?.Entity is { IsEnemy: true })
        {
            _falseReturns++;
            return false;
        }
        return true;
    }

    private static IReadOnlyList<string> Check(string label, int enemyIndex)
    {
        CombatState state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("not in combat");
        NCreature node = NCombatRoom.Instance?.GetCreatureNode(state.Enemies[enemyIndex]) ?? throw new InvalidOperationException("no node for enemy " + enemyIndex);
        var active = (Dictionary<Control, NHoverTipSet>)AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips").GetValue(null)!;
        if (!active.TryGetValue(node.Hitbox, out NHoverTipSet? panel))
        {
            return new[] { $"FOREIGN TIPS hidden {label}: no panel registered for enemy {enemyIndex}; reused {HoverTipReuse.Reused}, rebuilt {HoverTipReuse.Rebuilt}" };
        }
        IEnumerable<IHoverTip> given = node.Entity.HoverTips;
        if (AppendInstalled())
        {
            given = given.Concat(new IHoverTip[] { Synthetic() });
        }
        List<string> expected = IHoverTip.RemoveDupes(given).OfType<HoverTip>().Select(t => $"{t.Title}: {t.Description}").ToList();
        var shown = new List<string>();
        var container = (Control)AccessTools.Field(panel.GetType(), "_textHoverTipContainer").GetValue(panel)!;
        foreach (Node tip in container.GetChildren())
        {
            var title = tip.GetNodeOrNull<Control>("%Title");
            string titleText = title is { Visible: true } ? (string)title.Get("text") : "";
            shown.Add($"{(titleText.Length > 0 ? titleText : null)}: {tip.GetNode<RichTextLabel>("%Description").Text}");
        }
        _checked++;
        bool same = shown.SequenceEqual(expected);
        string line = $"FOREIGN TIPS {(same ? "OK" : "MISMATCH")} {label}: {shown.Count} shown / {expected.Count} expected, reused {HoverTipReuse.Reused}, rebuilt {HoverTipReuse.Rebuilt}"
            + (same ? "" : $" | shown: {string.Join(" / ", shown)} | expected: {string.Join(" / ", expected)}");
        if (!same)
        {
            throw new InvalidOperationException(line);
        }
        return new[] { line };
    }
}
