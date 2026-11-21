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
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic prefixes of "another mod" on <c>NCreature.ShowHoverTips</c>, for HoverTipReuse's <see cref="PatchTolerance.AnyPrefix"/>
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
///
/// Controls of the off and back-off paths and of transpilers (fix round of 2026-11-21):
/// <list type="bullet">
/// <item><c>tips-identity</c>: a void prefix on ShowHoverTips (<see cref="Priority.Last"/>: the final argument) and one on
/// <c>NHoverTipSet.CreateAndShow</c> comparing the argument it gets for that hitbox by reference; <c>foreignpatch tips-identity-check</c>
/// fails unless every compared call got the very object (the game's sequence on the body's own argument: off or backed off) and
/// one at least was compared.</item>
/// <item><c>tips-count-prefix</c>: a void prefix wrapping the argument in an enumerable counting its reads (<c>GetEnumerator</c>);
/// <c>foreignpatch tips-uiblock on|off</c> sets the game's <c>NHoverTipSet.shouldBlockHoverTips</c> (as a potion popup does);
/// <c>foreignpatch tips-reads mark|zero</c>: no read since the mark (the game's CreateAndRegister returns before reading them).</item>
/// <item><c>hide-transpiler</c> on ShowHoverTips and <c>readfile-transpiler</c> on <c>GodotFileIo.ReadFile</c>: a transpiler of
/// "another mod" (default priority, so before this mod's Priority.Last one) replacing the call this mod looks for
/// (<c>HideHoverTips</c>, <c>GetAsText</c>) with its own counting wrapper of it; <c>foreignpatch transpiled mark|grew hide|readfile</c>:
/// that wrapper ran since the mark (its change kept, not dropped by this mod's transpiler).</item>
/// </list>
/// </summary>
public static class ForeignShapesHover
{
    public static readonly string[] Shapes = { "tips-append-prefix", "tips-false-prefix", "tips-identity", "tips-count-prefix", "hide-transpiler", "readfile-transpiler" };

    public static bool Handles(string[] parts) => parts.Length >= 1 && (Shapes.Contains(parts[0])
        || parts[0] is "tips-block" or "tips-check" or "tips-report" or "tips-identity-check" or "tips-uiblock" or "tips-reads" or "transpiled");

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
            {
                // tips-check <label words> [enemy index]: a last word that is a number is the index.
                bool indexed = parts.Length > 2 && int.TryParse(parts[^1], out _);
                string label = string.Join(' ', parts.Skip(1).Take(parts.Length - 1 - (indexed ? 1 : 0)));
                return Check(label.Length > 0 ? label : "now", indexed ? int.Parse(parts[^1]) : 0);
            }
            case "tips-identity-check":
            {
                string line = $"FOREIGN tips identity: {_sameArgument} calls got the body's own argument, {_otherArgument} another object";
                if (_otherArgument > 0 || _sameArgument == 0)
                {
                    throw new InvalidOperationException(line + " (MISMATCH: the game's sequence gets the body's own argument)");
                }
                return new[] { line };
            }
            case "tips-uiblock" when parts.Length > 1 && parts[1] is "on" or "off":
                NHoverTipSet.shouldBlockHoverTips = parts[1] == "on";
                return new[] { $"FOREIGN tips-uiblock {parts[1]}: NHoverTipSet.shouldBlockHoverTips {NHoverTipSet.shouldBlockHoverTips}" };
            case "tips-reads" when parts.Length > 1 && parts[1] is "mark" or "zero":
            {
                if (parts[1] == "mark")
                {
                    _readsMark = _reads;
                    _blockedCallsMark = _blockedCalls;
                    return new[] { $"FOREIGN tips reads mark: {_reads}" };
                }
                // Zero reads prove nothing unless ShowHoverTips' body really ran in the window with the block on, outside a card play.
                long calls = _blockedCalls - _blockedCallsMark;
                string line = $"FOREIGN tips reads since the mark: {_reads - _readsMark} ({calls} ShowHoverTips calls outside a card play while blocked)";
                if (_reads != _readsMark)
                {
                    throw new InvalidOperationException(line + " (MISMATCH: the game reads no tip while they are blocked)");
                }
                if (calls < 1)
                {
                    throw new InvalidOperationException(line + " (NOTHING CHECKED: no ShowHoverTips call ran in the window while blocked, outside a card play)");
                }
                return new[] { line };
            }
            case "transpiled" when parts.Length > 2 && parts[1] is "mark" or "grew" && parts[2] is "hide" or "readfile":
            {
                long now = parts[2] == "hide" ? _foreignHides : _foreignReads;
                if (parts[1] == "mark")
                {
                    _transpiledMarks[parts[2]] = now;
                    return new[] { $"FOREIGN transpiled {parts[2]} mark: {now}" };
                }
                long then = _transpiledMarks.TryGetValue(parts[2], out long saved) ? saved : throw new InvalidOperationException($"transpiled grew {parts[2]}: no mark");
                string line = $"FOREIGN transpiled {parts[2]} since the mark: {then} -> {now}";
                if (now <= then)
                {
                    throw new InvalidOperationException(line + " (the other mod's transpiler never ran: its change was dropped)");
                }
                return new[] { line };
            }
        }
        if (!Shapes.Contains(parts[0]))
        {
            throw new ArgumentException("foreignpatch " + string.Join("|", Shapes) + " [priority N] | tips-block on|off | tips-check <label> [enemy] | tips-report"
                + " | tips-identity-check | tips-uiblock on|off | tips-reads mark|zero | transpiled mark|grew hide|readfile");
        }
        string shape = parts[0];
        MethodBase target = shape == "readfile-transpiler"
            ? AccessTools.Method(typeof(GodotFileIo), nameof(GodotFileIo.ReadFile), new[] { typeof(string) }) ?? throw new MissingMethodException(nameof(GodotFileIo), nameof(GodotFileIo.ReadFile))
            : AccessTools.Method(typeof(NCreature), nameof(NCreature.ShowHoverTips)) ?? throw new MissingMethodException(nameof(NCreature), nameof(NCreature.ShowHoverTips));
        int priority = shape == "tips-identity" ? Priority.Last : Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string id = ForeignPatches.IdPrefix + shape;
        var harmony = new Harmony(id);
        HarmonyMethod Fix(string name) => new(typeof(ForeignShapesHover), name) { priority = priority };
        switch (shape)
        {
            case "tips-append-prefix":
                harmony.Patch(target, prefix: Fix(nameof(AppendPrefix)));
                break;
            case "tips-false-prefix":
                harmony.Patch(target, prefix: Fix(nameof(FalsePrefix)));
                break;
            case "tips-count-prefix":
                harmony.Patch(target, prefix: Fix(nameof(CountReadsPrefix)));
                break;
            case "tips-identity":
            {
                harmony.Patch(target, prefix: Fix(nameof(IdentityShowPrefix)));
                MethodBase create = AccessTools.Method(typeof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShow), new[] { typeof(Control), typeof(IEnumerable<IHoverTip>), typeof(HoverTipAlignment) })
                    ?? throw new MissingMethodException(nameof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShow));
                harmony.Patch(create, prefix: Fix(nameof(IdentityCreatePrefix)));
                installed(id, create);
                break;
            }
            case "hide-transpiler":
                harmony.Patch(target, transpiler: Fix(nameof(HideTranspiler)));
                break;
            case "readfile-transpiler":
                harmony.Patch(target, transpiler: Fix(nameof(ReadFileTranspiler)));
                break;
        }
        installed(id, target);
        string name = $"{target.DeclaringType?.Name}.{target.Name}";
        var lines = new List<string> { $"FOREIGN installed {shape} on {name} as {id} (priority {priority})" };
        lines.AddRange(PatchSafety.Describe(target).Select(line => $"FOREIGN classify {name}: " + line));
        return lines;
    }

    // ---- The controls of the fix round ----

    private static Control? _identityHitbox;
    private static IEnumerable<IHoverTip>? _identityArgument;
    private static long _sameArgument, _otherArgument;

    /// <summary>The body's final argument and the hitbox it goes with (sorted last among the prefixes).</summary>
    public static void IdentityShowPrefix(NCreature __instance, IEnumerable<IHoverTip> hoverTips)
    {
        _identityHitbox = __instance?.Hitbox;
        _identityArgument = hoverTips;
    }

    /// <summary>CreateAndShow for that hitbox: the very argument the body got, or another object.</summary>
    public static void IdentityCreatePrefix(Control owner, IEnumerable<IHoverTip> hoverTips)
    {
        if (_identityHitbox == null || !ReferenceEquals(owner, _identityHitbox))
        {
            return;
        }
        if (ReferenceEquals(hoverTips, _identityArgument))
        {
            _sameArgument++;
        }
        else
        {
            _otherArgument++;
        }
        _identityHitbox = null;
        _identityArgument = null;
    }

    private static long _reads, _readsMark, _blockedCalls, _blockedCallsMark;

    /// <summary>The argument wrapped in an enumerable counting its reads (each <c>GetEnumerator</c>).</summary>
    public static void CountReadsPrefix(ref IEnumerable<IHoverTip> hoverTips)
    {
        if (NHoverTipSet.shouldBlockHoverTips && MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.Ui.Hand.InCardPlay == false)
        {
            _blockedCalls++;
        }
        if (hoverTips != null)
        {
            hoverTips = new CountingTips(hoverTips);
        }
    }

    private sealed class CountingTips(IEnumerable<IHoverTip> inner) : IEnumerable<IHoverTip>
    {
        public IEnumerator<IHoverTip> GetEnumerator()
        {
            _reads++;
            return inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static long _foreignHides, _foreignReads;
    private static readonly Dictionary<string, long> _transpiledMarks = new();

    /// <summary>"Another mod's" own hide: the game's, counted.</summary>
    public static void ForeignHide(NCreature creature)
    {
        _foreignHides++;
        creature.HideHoverTips();
    }

    /// <summary>"Another mod's" own read as text: the game's, counted.</summary>
    public static string ForeignGetAsText(Godot.FileAccess file, bool skipCr)
    {
        _foreignReads++;
        return file.GetAsText(skipCr);
    }

    /// <summary>Every <c>call HideHoverTips</c> replaced with <see cref="ForeignHide"/> (same stack).</summary>
    public static IEnumerable<CodeInstruction> HideTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo hide = AccessTools.Method(typeof(NCreature), nameof(NCreature.HideHoverTips));
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction.Calls(hide)
                ? new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(ForeignShapesHover), nameof(ForeignHide))).WithLabels(instruction.labels).WithBlocks(instruction.blocks)
                : instruction;
        }
    }

    /// <summary>Every <c>callvirt GetAsText(bool)</c> replaced with <see cref="ForeignGetAsText"/> (same stack).</summary>
    public static IEnumerable<CodeInstruction> ReadFileTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getAsText = AccessTools.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetAsText), new[] { typeof(bool) });
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction.Calls(getAsText)
                ? new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(ForeignShapesHover), nameof(ForeignGetAsText))).WithLabels(instruction.labels).WithBlocks(instruction.blocks)
                : instruction;
        }
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
