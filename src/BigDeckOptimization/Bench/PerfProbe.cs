using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Survey profiler for normal play: times a list of game methods suspected to scale with deck size (calls, total, max;
/// async ones also from call to completion), and logs every long frame with the probes that ran during it, so one play
/// session with a huge deck shows where hitches come from. Read-only: only records times. Shown by <c>bdperf probe</c>.
/// Nested probes are counted in each level (inclusive times).
/// </summary>
public static class PerfProbe
{
    public const string Name = "PerfProbe";

    private sealed class Stat
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
        public long Completed;
        public double WallTotalMs;
        public double WallMaxMs;
    }

    private static readonly Dictionary<MethodBase, string> _labels = new();
    private static readonly Dictionary<string, Stat> _stats = new();
    private static readonly Dictionary<string, double> _frameMs = new();
    private static readonly List<string> _hitches = new();
    private static readonly object _lock = new();
    private static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;
    private static long _lastFrameTicks;
    private static TimeSpan _lastGcPause;
    private static bool _subscribed;

    public static bool Enabled { get; set; } = true;

    /// <summary>Frames longer than this are logged with the probes that ran during them.</summary>
    public static double HitchMs { get; set; } = 100;

    public static void Apply(Harmony harmony)
    {
        // Sync: CPU time on the calling thread.
        Probe(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.AddInternal)), "CardPile.AddInternal");
        Probe(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.RemoveInternal)), "CardPile.RemoveInternal");
        Probe(harmony, AccessTools.Method(typeof(CardPile), nameof(CardPile.Clear)), "CardPile.Clear");
        Probe(harmony, AccessTools.Method(typeof(NetCombatCardDb), "OnPileContentsChanged"), "NetCombatCardDb.OnPileContentsChanged");
        Probe(harmony, AccessTools.Method(typeof(NetCombatCardDb), nameof(NetCombatCardDb.StartCombat)), "NetCombatCardDb.StartCombat");
        Probe(harmony, AccessTools.Method(typeof(NCombatCardPile), "AddCard"), "NCombatCardPile.AddCard");
        Probe(harmony, AccessTools.Method(typeof(NCombatCardPile), "RemoveCard"), "NCombatCardPile.RemoveCard");
        Probe(harmony, AccessTools.Method(typeof(Player), nameof(Player.AfterCombatEnd)), "Player.AfterCombatEnd");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetUpCombat)), "CombatManager.SetUpCombat");
        Probe(harmony, AccessTools.Method(typeof(Player), nameof(Player.PopulateCombatState)), "Player.PopulateCombatState");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Combat.CombatState), nameof(MegaCrit.Sts2.Core.Combat.CombatState.CloneCard)), "clone: CombatState.CloneCard");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), nameof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet.Clone)), "clone: DynamicVarSet.Clone");
        Probe(harmony, AccessTools.Method(typeof(CardPile), "RandomizeOrderInternal"), "clone: CardPile.RandomizeOrderInternal");
        Probe(harmony, AccessTools.Method(typeof(Player), nameof(Player.SyncWithSerializedPlayer)), "sync: Player.SyncWithSerializedPlayer");
        Probe(harmony, AccessTools.Method(typeof(Player), nameof(Player.ToSerializable)), "sync: Player.ToSerializable");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer), nameof(MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer.StartSync)), "sync: StartSync");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Messages.Game.SyncPlayerDataMessage), nameof(MegaCrit.Sts2.Core.Multiplayer.Messages.Game.SyncPlayerDataMessage.Deserialize)), "sync: message Deserialize");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Messages.Game.SyncPlayerDataMessage), nameof(MegaCrit.Sts2.Core.Multiplayer.Messages.Game.SyncPlayerDataMessage.Serialize)), "sync: message Serialize");
        Probe(harmony, AccessTools.Method(typeof(NCardGrid), nameof(NCardGrid.SetCards)), "NCardGrid.SetCards");
        Probe(harmony, AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable)), "NCard.FindOnTable");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState), "FromRun"), "NetFullCombatState.FromRun (co-op checksum)");
        // What one action costs after it (frames of a play): checksum, state refresh and its parts, hover.
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.ChecksumTracker), nameof(MegaCrit.Sts2.Core.Multiplayer.Game.ChecksumTracker.GenerateChecksum), new[] { typeof(string), typeof(MegaCrit.Sts2.Core.GameActions.GameAction) }), "ChecksumTracker.GenerateChecksum");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState.CombatPileState), "From"), "CombatPileState.From (one pile)");
        Probe(harmony, AccessTools.Method(typeof(RelicModel), nameof(RelicModel.ToSerializable)), "RelicModel.ToSerializable");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState), "Serialize"), "NetFullCombatState.Serialize");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "RecalculateCardValues"), "PlayerCombatState.RecalculateCardValues");
        Probe(harmony, AccessTools.Method(typeof(NPlayerHand), "OnCombatStateChanged"), "NPlayerHand.OnCombatStateChanged");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder), "UpdateCard"), "NHandCardHolder.UpdateCard");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Orbs.NOrbManager), "UpdateVisuals"), "NOrbManager.UpdateVisuals");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton), "StartOrStopPulseVfx"), "NEndTurnButton.StartOrStopPulseVfx");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NIntent), "UpdateVisuals"), "NIntent.UpdateVisuals");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay), "RefreshValues"), "NCreatureStateDisplay.RefreshValues");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet), nameof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet.CreateAndShow),
            new[] { typeof(Godot.Control), typeof(IEnumerable<MegaCrit.Sts2.Core.HoverTips.IHoverTip>), typeof(MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment) }), "NHoverTipSet.CreateAndShow");
        Probe(harmony, AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.HoverTips)), "CardModel.HoverTips");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Combat.CombatStateTracker), "CallCombatStateChangedDeferred"), "CombatStateTracker.CallCombatStateChangedDeferred");
        // Parts of the co-op checksum (this mod's own prefixes included): what each costs per action.
        Probe(harmony, AccessTools.Method(typeof(Patches.SnapshotCardReuse), "PilePrefix"), "checksum: SnapshotCardReuse.PilePrefix (one pile)");
        Probe(harmony, AccessTools.Method(typeof(Patches.SnapshotCardReuse), "PileSerializePrefix"), "checksum: SnapshotCardReuse.PileSerializePrefix (one pile)");
        Probe(harmony, AccessTools.Method(typeof(Patches.RelicStateReuse), "Serialize"), "checksum: RelicStateReuse.Serialize (one relic)");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RelicGrabBag), nameof(MegaCrit.Sts2.Core.Runs.RelicGrabBag.ToSerializable)), "checksum: RelicGrabBag.ToSerializable");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Random.PlayerRngSet), nameof(MegaCrit.Sts2.Core.Random.PlayerRngSet.ToSerializable)), "checksum: PlayerRngSet.ToSerializable");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState.PlayerState), "Serialize"), "checksum: PlayerState.Serialize");
        Probe(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState.CreatureState), "Serialize"), "checksum: CreatureState.Serialize");
        // The game's text effects, one call per glyph per frame: the bridge's entry (the glyph's wrapper built there) and the effect.
        foreach (Type effect in typeof(MegaCrit.Sts2.Core.RichTextTags.RichTextGold).Assembly.GetTypes().Where(t => t.Namespace == "MegaCrit.Sts2.Core.RichTextTags" && !t.IsAbstract
            && typeof(Godot.RichTextEffect).IsAssignableFrom(t)))
        {
            Probe(harmony, AccessTools.DeclaredMethod(effect, "InvokeGodotClassMethod"), "text effect: " + effect.Name);
        }
        if (AccessTools.TypeByName("BetterSpire2.DamageTracker") is { } tracker)
        {
            Probe(harmony, AccessTools.Method(tracker, "Recalculate"), "BetterSpire2 DamageTracker.Recalculate");
        }

        // Async: first slice (until the first await) + wall time until the task completes (includes animations).
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.EndCombatInternal)), "CombatManager.EndCombatInternal");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.HandlePlayerDeath)), "CombatManager.HandlePlayerDeath");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.EndPlayerTurnPhaseOneInternal)), "CombatManager.EndPlayerTurnPhaseOne");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.EndPlayerTurnPhaseTwoInternal)), "CombatManager.EndPlayerTurnPhaseTwo");
        Probe(harmony, AccessTools.Method(typeof(CombatManager), "FlushPlayerHand"), "CombatManager.FlushPlayerHand");
        Probe(harmony, AccessTools.Method(typeof(Hook), nameof(Hook.AfterCombatEnd)), "Hook.AfterCombatEnd");
        Probe(harmony, AccessTools.Method(typeof(Hook), nameof(Hook.AfterCombatVictory)), "Hook.AfterCombatVictory");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Shuffle)), "CardPileCmd.Shuffle");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Draw),
            new[] { typeof(PlayerChoiceContext), typeof(decimal), typeof(Player), typeof(bool) }), "CardPileCmd.Draw");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.RemoveFromCombat),
            new[] { typeof(IEnumerable<CardModel>), typeof(bool) }), "CardPileCmd.RemoveFromCombat");
        Probe(harmony, AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
            new[] { typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) }), "CardPileCmd.Add(many)");
        Probe(harmony, AccessTools.Method(typeof(PlayCardAction), "ExecuteAction"), "PlayCardAction.ExecuteAction");
        Probe(harmony, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.SaveRun)), "SaveManager.SaveRun");
        Probe(harmony, AccessTools.Method(typeof(RewardsSet), nameof(RewardsSet.GenerateWithoutOffering)), "RewardsSet.GenerateWithoutOffering");
        Subscribe();
    }

    /// <summary>
    /// <c>bdperf probe nodes</c>: every per-frame callback the game's and the mods' nodes declare (<c>_Process</c>,
    /// <c>_PhysicsProcess</c>, <c>_Input</c>, <c>_GuiInput</c>, <c>_UnhandledInput</c>, <c>_Draw</c>), so a long frame shows
    /// which node types ran in it. Each probe costs its own call: for finding where a frame goes, not for timings.
    /// </summary>
    public static void ApplyNodes(Harmony harmony)
    {
        string[] callbacks = { "_Process", "_PhysicsProcess", "_Input", "_GuiInput", "_UnhandledInput", "_Draw" };
        int probed = 0;
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name is null || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Godot", StringComparison.Ordinal)
                || name.StartsWith("Microsoft", StringComparison.Ordinal) || name == "0Harmony" || name == typeof(PerfProbe).Assembly.GetName().Name)
            {
                continue;
            }
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray()!;
            }
            foreach (Type type in types)
            {
                if (!typeof(Godot.Node).IsAssignableFrom(type) || type.IsGenericTypeDefinition)
                {
                    continue;
                }
                foreach (string callback in callbacks)
                {
                    if (AccessTools.DeclaredMethod(type, callback) is { IsAbstract: false } method)
                    {
                        Probe(harmony, method, $"node: {type.Name}.{callback}");
                        probed++;
                    }
                }
            }
        }
        // The engine work managed code starts: scenes instanced, nodes added and freed, tweens made.
        Probe(harmony, typeof(Godot.PackedScene).GetMethods().First(m => m.Name == nameof(Godot.PackedScene.Instantiate) && !m.IsGenericMethod), "engine: PackedScene.Instantiate");
        Probe(harmony, AccessTools.Method(typeof(Godot.Node), nameof(Godot.Node.AddChild)), "engine: Node.AddChild");
        Probe(harmony, AccessTools.Method(typeof(Godot.Node), nameof(Godot.Node.RemoveChild)), "engine: Node.RemoveChild");
        Probe(harmony, AccessTools.Method(typeof(Godot.Node), nameof(Godot.Node.QueueFree)), "engine: Node.QueueFree");
        Probe(harmony, AccessTools.Method(typeof(Godot.Node), nameof(Godot.Node.CreateTween)), "engine: Node.CreateTween");
        Probe(harmony, AccessTools.Method(typeof(Godot.Node), nameof(Godot.Node.Reparent)), "engine: Node.Reparent");
        Probe(harmony, AccessTools.PropertySetter(typeof(Godot.CanvasItem), nameof(Godot.CanvasItem.Visible)), "engine: CanvasItem.Visible set");
        Probe(harmony, AccessTools.PropertySetter(typeof(Godot.RichTextLabel), nameof(Godot.RichTextLabel.Text)), "engine: RichTextLabel.Text set");
        Probe(harmony, AccessTools.PropertySetter(typeof(Godot.Label), nameof(Godot.Label.Text)), "engine: Label.Text set");
        Log.Info($"[{ModEntry.Id}] {Name}: {probed} node callbacks probed");
    }

    private static void Probe(Harmony harmony, MethodBase? target, string label)
    {
        if (target == null)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: {label} not found, not probed");
            return;
        }
        _labels[target] = label;
        Type returnType = target is MethodInfo info ? info.ReturnType : typeof(void);
        try
        {
            Patches.PatchGuard.Patch(harmony, target,
                prefix: new HarmonyMethod(typeof(PerfProbe), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(PerfProbe), typeof(Task).IsAssignableFrom(returnType) ? nameof(AsyncPostfix) : nameof(SyncPostfix)));
        }
        catch (Exception e)
        {
            // One method Harmony can't wrap (some of this mod's own prefixes) leaves the others probed.
            _labels.Remove(target);
            Log.Warn($"[{ModEntry.Id}] {Name}: {label} not probed: {e.Message}");
        }
    }

    private static void Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    /// <summary>Probes whose callers are sampled (one call in <see cref="CallerSampleEvery"/>), to find who calls them.</summary>
    private static readonly HashSet<string> _sampledCallers = new() { "CardModel.Pile" };
    private const int CallerSampleEvery = 500;
    private static readonly Dictionary<string, int> _callers = new();
    private static long _sampleCounter;

    private static void SampleCaller(string label)
    {
        if (++_sampleCounter % CallerSampleEvery != 0)
        {
            return;
        }
        // Skip the probe and Harmony's wrapper: the probed method then up to 4 game frames above it.
        StackTrace trace = new(2, false);
        string key = label + " <- " + string.Join(" <- ", trace.GetFrames().Take(5)
            .Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
        lock (_lock)
        {
            _callers[key] = _callers.GetValueOrDefault(key) + 1;
        }
    }

    private static void SyncPostfix(MethodBase __originalMethod, long __state)
    {
        if (__state != 0)
        {
            if (_labels.TryGetValue(__originalMethod, out string? sampled) && _sampledCallers.Contains(sampled))
            {
                SampleCaller(sampled);
            }
            Record(__originalMethod, (Stopwatch.GetTimestamp() - __state) * _msPerTick);
        }
    }

    private static void AsyncPostfix(MethodBase __originalMethod, object? __result, long __state)
    {
        if (__state == 0)
        {
            return;
        }
        Record(__originalMethod, (Stopwatch.GetTimestamp() - __state) * _msPerTick);
        if (__result is Task task)
        {
            task.ContinueWith(_ => RecordWall(__originalMethod, (Stopwatch.GetTimestamp() - __state) * _msPerTick),
                TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private static void Record(MethodBase method, double ms)
    {
        if (!_labels.TryGetValue(method, out string? label))
        {
            return;
        }
        lock (_lock)
        {
            Stat stat = GetStat(label);
            stat.Calls++;
            stat.TotalMs += ms;
            stat.MaxMs = Math.Max(stat.MaxMs, ms);
            _frameMs[label] = _frameMs.GetValueOrDefault(label) + ms;
        }
        if (!_subscribed)
        {
            Subscribe();
        }
    }

    private static void RecordWall(MethodBase method, double ms)
    {
        if (!_labels.TryGetValue(method, out string? label))
        {
            return;
        }
        lock (_lock)
        {
            Stat stat = GetStat(label);
            stat.Completed++;
            stat.WallTotalMs += ms;
            stat.WallMaxMs = Math.Max(stat.WallMaxMs, ms);
        }
    }

    private static Stat GetStat(string label)
    {
        if (!_stats.TryGetValue(label, out Stat? stat))
        {
            _stats[label] = stat = new Stat();
        }
        return stat;
    }

    private static void Subscribe()
    {
        if (_subscribed || Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }
        _subscribed = true;
        _lastFrameTicks = Stopwatch.GetTimestamp();
        _lastGcPause = GC.GetTotalPauseDuration();
        tree.ProcessFrame += OnFrame;
    }

    private static void OnFrame()
    {
        long now = Stopwatch.GetTimestamp();
        double frameMs = (now - _lastFrameTicks) * _msPerTick;
        _lastFrameTicks = now;
        TimeSpan gcPause = GC.GetTotalPauseDuration();
        double gcMs = (gcPause - _lastGcPause).TotalMilliseconds;
        _lastGcPause = gcPause;
        lock (_lock)
        {
            if (Enabled && frameMs >= HitchMs)
            {
                string top = string.Join(", ", _frameMs.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} {p.Value:F1}"));
                string line = $"hitch {frameMs:F0} ms (GC {gcMs:F0} ms, {CardCounts()}): {(top.Length > 0 ? top : "no probe")}";
                _hitches.Add($"{DateTime.Now:HH:mm:ss} {line}");
                if (_hitches.Count > 200)
                {
                    _hitches.RemoveAt(0);
                }
                Log.Info($"[{ModEntry.Id}] {Name} {line}");
            }
            _frameMs.Clear();
        }
    }

    private static string CardCounts()
    {
        try
        {
            RunState? state = RunManager.Instance.DebugOnlyGetState();
            if (state == null)
            {
                return "no run";
            }
            int deck = state.Players.Sum(p => p.Deck.Cards.Count);
            int combat = state.Players.Sum(p => p.PlayerCombatState?.AllPiles.Sum(pile => pile.Cards.Count) ?? 0);
            return $"deck {deck}, combat {combat}";
        }
        catch (Exception)
        {
            return "counts n/a";
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _stats.Clear();
            _callers.Clear();
            _hitches.Clear();
            _frameMs.Clear();
        }
    }

    public static string Report(int hitches = 10)
    {
        StringBuilder sb = new();
        lock (_lock)
        {
            sb.Append($"probe {(Enabled ? "on" : "OFF")}, hitch threshold {HitchMs:F0} ms, {CardCounts()}\n");
            sb.Append("  total ms | calls | max ms | avg ms | [async: done, wall avg/max ms] | probe\n");
            foreach ((string label, Stat s) in _stats.OrderByDescending(p => p.Value.TotalMs))
            {
                string wall = s.Completed > 0 ? $" [{s.Completed}, {s.WallTotalMs / s.Completed:F0}/{s.WallMaxMs:F0}]" : "";
                sb.Append($"  {s.TotalMs,8:F0} | {s.Calls,7} | {s.MaxMs,6:F1} | {s.TotalMs / s.Calls,6:F3}{wall} | {label}\n");
            }
            foreach ((string key, int count) in _callers.OrderByDescending(p => p.Value).Take(12))
            {
                sb.Append($"  caller ~x{count * CallerSampleEvery}: {key}\n");
            }
            sb.Append($"  last {Math.Min(hitches, _hitches.Count)} of {_hitches.Count} hitches:\n");
            foreach (string hitch in _hitches.Skip(Math.Max(0, _hitches.Count - hitches)))
            {
                sb.Append("    ").Append(hitch).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Writes the full report to the game log, one line per row, for later analysis.</summary>
    public static void LogReport()
    {
        foreach (string line in Report(200).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Log.Info($"[{ModEntry.Id}] {Name} | {line}");
        }
    }
}
