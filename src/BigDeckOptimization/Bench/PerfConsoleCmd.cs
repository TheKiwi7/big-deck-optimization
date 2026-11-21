using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// bdperf console cmd (test builds only): the optimizations against the game's code, and test switches. Local only
/// (<see cref="IsNetworked"/> false): nothing is sent to peers.
///   Comparisons, vanilla (the whole mod off, <see cref="Vanilla{T}"/>) vs optimized, same result required, and the times:
///   checksum [n] (the post-action co-op checksum: snapshot + serialize + hash), refresh [n] (the combat refresh), listeners [n],
///   scan, pile, text, plaque [n], grid [n], sortkeys, relics [n], upgradebench [n], shufflebench [n], savecheck [file] (the
///   game's check of the previous save before its backup, SaveCheckBench). Each puts back the switches
///   it forces (a part that faulted meanwhile stays off; one that faulted earlier isn't forced back on: nothing to compare) and
///   the effect counters (<see cref="EffectCounters"/>). checksum and refresh freeze the game for seconds and refresh writes the
///   hand's values (then puts them back): singleplayer only.
///   Reports: verify, combatstart, probe, piletimes, costhooks, deckcount, clonememo, reserialize, gcprobe, waitspin.
///   joinmsg [backoff]: JoinMismatchNote's texts on the game's own mod mismatch error built from synthetic lists (JoinMismatchBench).
///   Switches for tests (they change this mod's behaviour, never the game's state): opt (the master switch for this session
///   only, never saved: <see cref="Config.Settings.MasterChoice"/>),
///   checks, blocks, upgradebits, envrelease, gcmin, movecap, clonememocap, release, purehooks, dirtyreads, snapmoved, segments, relicreuse, fault,
///   sortkeys wrong, deckcount wrong, snapgameerror.
///   Changes the game's state: costwrites (the first hand card free this turn, on this peer only), and foreign with a shape
///   changing it (keyword-prefix, cost-postfix, nested-postfix: <see cref="ForeignPatches.ChangesState"/>): singleplayer only,
///   since in co-op it would only make this peer's checksum differ and the host drop it (a co-op script installs them with its
///   'foreignpatch' line on every peer, before any combat). <c>bdperf opt on|off</c> in a co-op run warns and asks for
///   <c>bdperf opt on|off yes</c> either way, as <c>bdopt</c> does.
/// </summary>
public partial class PerfConsoleCmd : AbstractConsoleCmd
{
    private readonly struct Sample
    {
        public double SnapshotMs { get; init; }
        public double HashMs { get; init; }
        public double WorstMs { get; init; }
        public double AllocatedKb { get; init; }
        public uint Checksum { get; init; }

        public string Format(string label) =>
            $"  {label}: snapshot {SnapshotMs:F2} ms + hash {HashMs:F2} ms = {SnapshotMs + HashMs:F2} ms (worst {WorstMs:F2}), {AllocatedKb:F0} KB, checksum {Checksum}";
    }

    public override string CmdName => "bdperf";

    public override string Args => "checksum [iterations:int] | refresh [iterations:int] | costcolors [iterations:int] | verify [off|sample|all] | snapnet | combatstart | grid [cards:int] | probe [reset|log|on|off|nodes|hitch <ms>] | pile [verify <n>] | plaque [cards:int] | sortkeys [wrong|deck|partonly [cards] [passes]] | savecheck [<file>|wrong|verify <n>|counters] | checks on|off | opt on|off [yes] | listeners [iterations:int] | costwrites | fault piles|walk | badges [clear|guard off|on] | snapgameerror <n> | reserialize [verify <n>] | clonememo [verify <n>] | conflicts | states | roomleft | modorder [none|<owner>] | firstturn inner | foreign remove|report|classify <Type:Method> | foreign remove|report|classify <Type:Method> | combatcards [passes:int]";

    public override string Description => "[BigDeckOptimization] Measures the multiplayer post-action checksum cost, vanilla vs optimized, or shows/sets live snapshot verification.";

    public override bool IsNetworked => false;

    /// <summary>Flashes the local player's first relic <paramref name="count"/> times in one frame and reports RelicFlashMerge's counters.</summary>
    private static CmdResult RelicFlashBurst(int count)
    {
        if (RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me || me.Relics.FirstOrDefault() is not { } relic)
        {
            return new CmdResult(success: false, "RELICFLASH no run or no relic");
        }
        long merged = RelicFlashMerge.Merged, creature = RelicFlashMerge.MergedCreature, holder = RelicFlashMerge.MergedHolder,
            noNode = RelicFlashMerge.MergedNoNode, refused = RelicFlashMerge.SceneRefusals;
        for (int i = 0; i < count; i++)
        {
            relic.Flash();
        }
        return new CmdResult(success: true, $"RELICFLASH {relic.Id.Entry} x{count} in combat {CombatManager.Instance.IsInProgress}: merged +{RelicFlashMerge.Merged - merged}, "
            + $"creature +{RelicFlashMerge.MergedCreature - creature}, holder +{RelicFlashMerge.MergedHolder - holder}, no node +{RelicFlashMerge.MergedNoNode - noNode}, "
            + $"scene refusals +{RelicFlashMerge.SceneRefusals - refused}");
    }

    private static int _spins;

    /// <summary>Settings.StatesChanged raised since the first <c>bdperf states</c> (which subscribes).</summary>
    private static int _statesChanged;
    private static bool _statesSubscribed;

    /// <summary>
    /// <c>bdperf states</c>: after the preflight (as bdopt), one line per option with <c>Settings.StateOf</c> (the menu's title
    /// colour: Healthy normal, Partly orange, Inactive red), the patches registered whole (<c>WholePatch</c>/<c>WholeMethods</c>),
    /// how many times <c>StatesChanged</c> was raised since the first call, and who listens to it (the menu's refresh once BaseLib
    /// loaded the menu DLL).
    /// </summary>
    private static string StatesReport()
    {
        if (!_statesSubscribed)
        {
            _statesSubscribed = true;
            Config.Settings.StatesChanged += () => System.Threading.Interlocked.Increment(ref _statesChanged);
        }
        Config.Settings.RefreshConflicts();
        var lines = new List<string>();
        foreach ((string key, Config.Settings.PartState state) in Config.Settings.States().OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            lines.Add($"STATES {key} {state}");
        }
        lines.Add("STATES whole " + string.Join(", ", PatchSafety.WholeNames().OrderBy(n => n, StringComparer.Ordinal)));
        lines.Add($"STATES changed {_statesChanged}");
        Delegate[] listeners = typeof(Config.Settings).GetField(nameof(Config.Settings.StatesChanged), BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Delegate handler
            ? handler.GetInvocationList() : Array.Empty<Delegate>();
        lines.Add("STATES listeners " + string.Join(", ", listeners.Select(d => $"{d.Method.DeclaringType?.Name}.{d.Method.Name}")));
        return string.Join('\n', lines);
    }

    /// <summary>DeckCountMemo's own answers (kept + updated) and the game's counts (unsure) at <c>bdperf deckcount mark</c>.</summary>
    private static (long Answers, long Unsure) _deckCountMark;

    /// <summary>Whether a scenario set <c>verify</c> for DeckCountMemo / LimitlessCloneMemo / LimitlessReserialize: its later reports then need checks.</summary>
    private static bool _deckCountVerifySet, _cloneVerifySet, _reserializeVerifySet;

    /// <summary>
    /// A loop pacing itself on Cmd.Wait, as the transform preview and PunchOff do on v0.107.1 and v0.111: with waits skipped it
    /// must still give the frame back. On v0.113 they call Cmd.TryWait, which ends their loop on a skipped wait.
    /// </summary>
    private static CmdResult WaitSpin((int Min, int Max)? expected)
    {
        _spins = 0;
        ulong frame = Godot.Engine.GetProcessFrames();
        System.Threading.Tasks.Task spin = Spin(frame);
        bool inRange = expected is not { } range || (_spins >= range.Min && _spins <= range.Max);
        return new CmdResult(success: !spin.IsCompleted && inRange, $"WAITSPIN {_spins} waits returned at once"
            + (expected is { } r ? $" ({(inRange ? "within" : "MISMATCH: outside")} {r.Min}-{r.Max})" : "")
            + $", then {(spin.IsCompleted ? "NEVER gave the frame back" : "the frame was given back")}");
    }

    private static async System.Threading.Tasks.Task Spin(ulong frame)
    {
        using var cancel = new System.Threading.CancellationTokenSource();
        while (_spins < 200000 && Godot.Engine.GetProcessFrames() == frame)
        {
            _spins++;
            await MegaCrit.Sts2.Core.Commands.Cmd.Wait(0.2f, cancel.Token, ignoreCombatEnd: true);
        }
    }

    /// <summary>
    /// A switch a bench forced, as it should be put back: as it was, unless one of the part's fault names was recorded
    /// meanwhile (a fault keeps its part off for the session, as <see cref="Config.Settings.ApplyAll"/> does). Not ApplyAll
    /// itself: it would also reset the switches the test tooling sets directly (see <see cref="Vanilla{T}"/>).
    /// </summary>
    internal static bool Restored(bool was, params string[] faults) => was && !faults.Any(PatchSafety.IsFaulted);

    /// <summary>
    /// A part that faulted earlier in the session (one of its fault names recorded) stays off: a bench doesn't force it back
    /// on, or its faulted code would run for the whole bench. The first fault name recorded, null when none.
    /// </summary>
    internal static string? FaultedBefore(params string[] faults) => faults.FirstOrDefault(PatchSafety.IsFaulted);

    private static CmdResult? RefuseFaulted(string part, params string[] faults) =>
        FaultedBefore(faults) is { } fault
            ? new CmdResult(success: false, $"{part} faulted earlier in the session ({fault}, see the log): it stays off, nothing to compare.")
            : null;

    /// <summary>
    /// The game's own result, the reference of most comparisons here (pile compares with <see cref="PileIndex.Vanilla"/>)
    /// and of the script's <c>checksum vanilla</c>: the whole
    /// mod off as <c>bdopt off</c> turns it off (every option with its parts, nothing saved), inside
    /// <see cref="ListenerCache.RunVanilla"/> (what checks it stays out too), then the settings back as they were. Turning
    /// off only the optimization under test left the others (listener walks, pile lookup, saved property lookup) on both sides.
    /// </summary>
    internal static T Vanilla<T>(Func<T> action)
    {
        bool master = Config.Settings.Master;
        // Switches the test tooling sets directly (script gcpacing, bdperf release/envrelease): the settings' value would
        // otherwise come back with them.
        bool gcPacing = GcPacing.Enabled, syncedRelease = SyncedCardRelease.Enabled, envRelease = RichTextEnvRelease.Enabled;
        Config.Settings.SetMaster(false, notify: false, sessionOnly: true);
        try
        {
            return ListenerCache.RunVanilla(action);
        }
        finally
        {
            Config.Settings.SetMaster(master, notify: false, sessionOnly: true);
            GcPacing.Enabled = gcPacing;
            SyncedCardRelease.Enabled = syncedRelease;
            RichTextEnvRelease.Enabled = envRelease;
        }
    }

    /// <summary>A comparison with the mod off compares vanilla with vanilla: it fails instead of saying identical.</summary>
    private static CmdResult? ModOff() =>
        ModEntry.StartedOff || !Config.Settings.Master
            ? new CmdResult(success: false, "The mod is off (bdopt off, or off in the settings when the game started): optimized and vanilla would be the same code, nothing to compare.")
            : null;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (sub is "checksum" or "refresh" or "listeners" or "pile" or "text" or "scan" or "plaque" or "grid" or "sortkeys" or "snapnet" or "combatcards" && !(sub is "pile" or "sortkeys" && args.Length > 1) && ModOff() is { } off)
        {
            return off;
        }
        bool coop = RunManager.Instance.IsInProgress && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer;
        // The benches freeze the game for seconds (vanilla runs) and replay game code: keep them out of real co-op.
        if (sub is "checksum" or "refresh" or "snapnet" && coop)
        {
            return new CmdResult(success: false, "bdperf " + sub + " is singleplayer only. In co-op use 'bdperf verify'.");
        }
        // Changes this peer's game state only (bdperf isn't networked): in co-op the next checksum differs and the host drops this
        // client. No co-op use, so no confirmation either (the warning-then-yes is bdperf opt's, as bdopt's).
        if (sub == "costwrites" && coop)
        {
            return new CmdResult(success: false, "bdperf costwrites is singleplayer only: it changes a card's cost on your game only, "
                + "the other players' games don't see it and the host would disconnect you.");
        }
        // Effect counters as the game's own calls left them (see EffectCounters); the benches read their own deltas inside.
        using IDisposable? frozen = sub is "checksum" or "refresh" or "listeners" or "pile" or "text" or "scan" or "plaque" or "grid" or "sortkeys"
            or "relics" or "upgradebench" or "shufflebench" or "costhooks" or "snapnet" or "joinmsg" or "combatcards"
            || (sub == "savecheck" && !(args.Length > 1 && args[1] is "wrong" or "verify" or "counters"))
            ? EffectCounters.Freeze() : null;
        // Positive controls and limits of the safety nets (Bench/PerfInjections.cs).
        if (Injection(sub, args, coop) is { } injected)
        {
            return injected;
        }
        switch (sub)
        {
            case "states":
                return new CmdResult(success: true, StatesReport());
            case "firstturn" when args.Length >= 2 && args[1] == "inner":
                try
                {
                    return new CmdResult(success: true, ExtraScopes.BenchInnerPatch());
                }
                catch (InvalidOperationException e)
                {
                    return new CmdResult(success: false, e.Message);
                }
            case "joinmsg":
                // Works with the mod off too (JoinMismatchNote doesn't follow the master switch): no ModOff refusal.
                try
                {
                    return new CmdResult(success: true, JoinMismatchBench.Run(args.Length >= 2 ? args[1] : null));
                }
                catch (InvalidOperationException e)
                {
                    return new CmdResult(success: false, e.Message);
                }
                catch (Exception e)
                {
                    return new CmdResult(success: false, $"JOINMSG failed: {e}");
                }
            case "modorder":
                try
                {
                    return new CmdResult(success: true, CoopModeTag.BenchOrder(args.Length >= 2 ? args[1] : null));
                }
                catch (InvalidOperationException e)
                {
                    return new CmdResult(success: false, e.Message);
                }
            case "roomleft":
                try
                {
                    return new CmdResult(success: true, FastCombatStart.BenchRoomLeft());
                }
                catch (InvalidOperationException e)
                {
                    return new CmdResult(success: false, e.Message);
                }
            case "conflicts":
                // bdopt why's report, plus the preflight's effect counters (checks run, back-offs and notes recorded).
                Config.Settings.RefreshConflicts();
                return new CmdResult(success: true, Config.Settings.WhyReport()
                    + $"\nSettings page summary: {Config.Settings.ConflictSummary(null) ?? "(none)"}"
                    + $"\nPreflight: {PatchSafety.PreflightRuns} runs, {PatchSafety.PreflightChecks} checks, {PatchSafety.RecordedBackOffs} back-offs and notes recorded"
                    + $"\nTolerated: {PatchSafety.ToleratedChecks} checks" + string.Concat(PatchSafety.ToleratedReport().Select(t => "\n  " + t))
                    + "\nRuntime (other mods' patches, per call site):" + string.Concat(PatchSafety.RuntimeReport().Select(t => "\n  " + t)));
            case "foreign":
                // ForeignPatches: synthetic patches of each shape under a foreign Harmony id (same arguments as the script's foreignpatch).
                if (coop && ForeignPatches.ChangesState(string.Join(" ", args.Skip(1))))
                {
                    return new CmdResult(success: false, "bdperf foreign with this shape is singleplayer only: it changes the game's state on your game "
                        + "only, the other players' games don't see it and the host would disconnect you (a co-op script's 'foreignpatch' line installs it on every peer).");
                }
                try
                {
                    return new CmdResult(success: true, string.Join('\n', ForeignPatches.Run(string.Join(" ", args.Skip(1)))));
                }
                catch (Exception e)
                {
                    return new CmdResult(success: false, "bdperf foreign: " + e.Message);
                }
            case "checksum":
                int iterations = args.Length >= 2 && int.TryParse(args[1], out int n) && n > 0 ? n : 5;
                return CompareChecksum(iterations);
            case "verify":
                return Verify(args.Length >= 2 ? args[1] : null);
            case "snapnet":
                return SnapNet();
            case "blocks" when args.Length >= 2:
                SnapshotCardReuse.BlockCopies = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: unchanged runs of a pile's cards {(SnapshotCardReuse.BlockCopies ? "copied as blocks" : "composed card by card")}; {SnapshotCardReuse.BlockCopiedCards} cards copied in blocks so far.");
            case "upgradebits" when args.Length >= 2 && args[1] is "on" or "off" or "wrong":
                SnapshotCardReuse.UpgradeBits = args[1] != "off";
                SnapshotCardReuse.UpgradeBitsWrongForTest = args[1] == "wrong";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: an upgraded card's bits {(SnapshotCardReuse.UpgradeBits ? "patched" : "written in full")}; patched {SnapshotCardReuse.UpgradeBitsPatched}, mismatches {SnapshotCardReuse.Mismatches}; not patched: "
                    + string.Join(", ", SnapshotCardReuse.WhyNoUpgradeBits.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")) + $"; cards upgraded (light snapshot objects) {SnapshotCardReuse.LightBuilt}");
            case "envrelease" when args.Length >= 2 && args[1] is "on" or "off":
                RichTextEnvRelease.Enabled = args[1] == "on";
                return new CmdResult(success: true, $"{RichTextEnvRelease.Name}: text effects' dictionaries {(RichTextEnvRelease.Enabled ? "disposed after use" : "left to their finalizers")}; released {RichTextEnvRelease.Released}.");
            case "gcmin" when args.Length >= 2 && int.TryParse(args[1], out int minCards) && minCards >= 0:
                GcPacing.MinCards = minCards;
                return new CmdResult(success: true, $"{GcPacing.Name}: combats from {minCards} cards; regions {GcPacing.Regions}, refused {GcPacing.RegionsRefused}, collections {GcPacing.Collections}.");
            case "release" when args.Length >= 2:
                // What a finished combat or a co-op sync leaves behind, kept as before: comparison sessions.
                SyncedCardRelease.Enabled = args[1] != "off";
                SnapshotCardReuse.ReleaseCombats = args[1] != "off";
                return new CmdResult(success: true, $"releases {args[1]}");
            case "movecap":
                // FastListeners' move journal limit, lowered so a small deck overflows it (the path only 4,000+ card combats reach).
                if (args.Length >= 2)
                {
                    if (!int.TryParse(args[1], out int moveCap) || moveCap < 2)
                    {
                        return new CmdResult(success: false, "bdperf movecap <limit, 2 or more>");
                    }
                    FastListeners.MaxMoves = moveCap;
                }
                return new CmdResult(success: true, $"MOVECAP limit {FastListeners.MaxMoves}, overflows {FastListeners.MoveOverflows}");
            case "clonememocap":
                // LimitlessCloneMemo's limit, lowered so a small run clears it (16,384 saved contents otherwise).
                if (args.Length >= 2)
                {
                    if (!int.TryParse(args[1], out int memoCap) || memoCap < 1)
                    {
                        return new CmdResult(success: false, "bdperf clonememocap <limit, 1 or more>");
                    }
                    LimitlessCloneMemo.MaxContents = memoCap;
                }
                return new CmdResult(success: true, $"CLONEMEMOCAP limit {LimitlessCloneMemo.MaxContents}, clears {LimitlessCloneMemo.MemoClears}");
            case "clonememo":
                bool setCloneVerify = args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int cloneEvery) && cloneEvery > 0;
                if (setCloneVerify)
                {
                    LimitlessCloneMemo.VerifyEvery = int.Parse(args[2]);
                    _cloneVerifySet = true;
                }
                string cloneText = $"CLONEMEMO enabled {LimitlessCloneMemo.Enabled}, reused {LimitlessCloneMemo.Reused}, verified {LimitlessCloneMemo.Verified}, options reused {LimitlessCloneMemo.OptionsReused}";
                if (ModEntry.NotApplied(LimitlessCloneMemo.Name))
                {
                    return new CmdResult(success: true, cloneText + $" ({LimitlessCloneMemo.Name} not applied: nothing to compare)");
                }
                // A report after 'verify' was set must have compared something: verified 0 would pass a part that never ran.
                bool cloneVerifiedNone = !setCloneVerify && _cloneVerifySet && LimitlessCloneMemo.Verified == 0;
                return new CmdResult(success: LimitlessCloneMemo.Enabled && !cloneVerifiedNone,
                    cloneText + (cloneVerifiedNone ? " (verify set but nothing verified: nothing compared)" : ""));
            case "reserialize":
                bool setReserializeVerify = args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int reserializeEvery) && reserializeEvery > 0;
                if (setReserializeVerify)
                {
                    LimitlessReserialize.VerifyEvery = int.Parse(args[2]);
                    _reserializeVerifySet = true;
                }
                string reserializeText = $"RESERIALIZE enabled {LimitlessReserialize.Enabled}, kept {LimitlessReserialize.Kept}, rebuilt {LimitlessReserialize.Rebuilt}, "
                    + $"verified {LimitlessReserialize.Verified}, mismatches {LimitlessReserialize.VerifyMismatches}";
                if (ModEntry.NotApplied(LimitlessReserialize.Name))
                {
                    return new CmdResult(success: true, reserializeText + $" ({LimitlessReserialize.Name} not applied: nothing to compare)");
                }
                // As clonememo: a report after 'verify' was set must have compared something.
                bool reserializeVerifiedNone = !setReserializeVerify && _reserializeVerifySet && LimitlessReserialize.Verified == 0;
                return new CmdResult(success: LimitlessReserialize.Enabled && LimitlessReserialize.VerifyMismatches == 0 && !reserializeVerifiedNone,
                    reserializeText + (reserializeVerifiedNone ? " (verify set but nothing verified: nothing compared)" : ""));
            case "snapgameerror" when args.Length >= 2 && int.TryParse(args[1], out int snapErrors) && snapErrors >= 0:
                // The next n reads of a card's state throw as a game error would (test of SnapshotCardReuse's fallback to the game).
                SnapshotCardReuse.GameExceptionsForTest = snapErrors;
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: the next {snapErrors} card reads throw as the game's would; game exceptions so far {SnapshotCardReuse.GameExceptions}, enabled {SnapshotCardReuse.Enabled}.");
            case "deckcount":
                bool setCountVerify = args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int countEvery) && countEvery > 0;
                if (setCountVerify)
                {
                    DeckCountMemo.VerifyEvery = int.Parse(args[2]);
                    _deckCountVerifySet = true;
                }
                else if (args.Length >= 2 && args[1] == "wrong")
                {
                    DeckCountMemo.WrongForTest = 1;
                }
                // mark, then unsure-grew (the game counted since: a card of a changing type in the piles), answers-grew (it
                // counted itself since: on) or answers-same (no count of its own since: off after a fault): self-checking scenarios.
                bool deckCountOk = DeckCountMemo.Mismatches == 0;
                string deckCountCheck = "";
                if (!setCountVerify && args.Length >= 1 && !(args.Length >= 2 && args[1] is "wrong" or "mark") && _deckCountVerifySet
                    && DeckCountMemo.Checks == 0 && !ModEntry.NotApplied(DeckCountMemo.Name))
                {
                    // Verify was set and no answer was ever compared: the part never answered, the report would pass on nothing.
                    deckCountOk = false;
                    deckCountCheck = " (verify set but no answer checked: nothing compared)";
                }
                if (args.Length >= 2 && args[1] == "mark")
                {
                    _deckCountMark = (DeckCountMemo.Kept + DeckCountMemo.Updated, DeckCountMemo.Unsure);
                    deckCountCheck = " (marked)";
                }
                else if (args.Length >= 2 && args[1] is "unsure-grew" or "answers-grew" or "answers-same")
                {
                    long answers = DeckCountMemo.Kept + DeckCountMemo.Updated;
                    bool grew = args[1] switch
                    {
                        "unsure-grew" => DeckCountMemo.Unsure > _deckCountMark.Unsure,
                        "answers-grew" => answers > _deckCountMark.Answers,
                        // Stopped answering: only means something if it had answered before the mark.
                        _ => answers == _deckCountMark.Answers && _deckCountMark.Answers > 0,
                    };
                    deckCountOk &= grew;
                    deckCountCheck += $" ({args[1]} since the mark: {(grew ? "ok" : "MISMATCH")}; answers {_deckCountMark.Answers}, unsure {_deckCountMark.Unsure} at the mark)";
                }
                return new CmdResult(success: deckCountOk, $"DECKCOUNT enabled {DeckCountMemo.Enabled}, kept {DeckCountMemo.Kept}, updated {DeckCountMemo.Updated}, walks {DeckCountMemo.Walks}, "
                    + $"unsure {DeckCountMemo.Unsure}, checks {DeckCountMemo.Checks}, mismatches {DeckCountMemo.Mismatches}{deckCountCheck}");
            case "combatcards":
                // bdperf combatcards [passes]: CombatCardSet and RemoveInternal's single scan against the game's code (Bench/CombatCardsBench.cs).
                return CombatCardsBench.Run(args.Length >= 2 && int.TryParse(args[1], out int containsPasses) && containsPasses > 0 ? containsPasses : 3);
            case "shufflebench":
                return ShuffleBench.Run(args.Length >= 2 && int.TryParse(args[1], out int shufflePasses) && shufflePasses > 0 ? shufflePasses : 5);
            case "upgradebench":
                return UpgradeBench.Run(args.Length >= 2 && int.TryParse(args[1], out int upgradePasses) && upgradePasses > 0 ? upgradePasses : 20);
            case "relics":
                return RelicBench.Run(args.Length >= 2 && int.TryParse(args[1], out int passes) && passes > 0 ? passes : 20);
            case "waitspin":
                // bdperf waitspin [min max]: the waits that may return at once (FastCombatStart.MaxSkipsPerFrame + 1 at most).
                return WaitSpin(args.Length >= 3 && int.TryParse(args[1], out int spinMin) && int.TryParse(args[2], out int spinMax) ? ((int, int)?)(spinMin, spinMax) : null);
            case "relicreuse" when args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int relicEvery) && relicEvery > 0:
                RelicStateReuse.ScalarVerifyEvery = relicEvery;
                return new CmdResult(success: true, $"{RelicStateReuse.ScalarName}: one answer in {relicEvery} compared with the game's.");
            case "relicreuse" when args.Length >= 3 && args[1] == "scalarblind":
                RelicStateReuse.ScalarBlindForTest = args[2] == "on";
                return new CmdResult(success: true, $"{RelicStateReuse.ScalarName}: relics {(RelicStateReuse.ScalarBlindForTest ? "taken as unchanged without reading them (test)" : "read")}; reused {RelicStateReuse.ScalarReused}, verified {RelicStateReuse.ScalarVerified}.");
            case "relicreuse" when args.Length >= 3 && args[1] == "blind":
                RelicStateReuse.BlindForTest = args[2] == "on";
                return new CmdResult(success: true, $"{RelicStateReuse.Name}: saved properties {(RelicStateReuse.BlindForTest ? "taken as unchanged (test)" : "compared")}; reused {RelicStateReuse.Reused}, built {RelicStateReuse.Built}.");
            case "grid":
                int tabCards = args.Length >= 2 && int.TryParse(args[1], out int g) && g > 0 ? g : 100;
                return GridBench.Run(tabCards);
            case "relicflash":
                // bdperf relicflash [n]: the local player's first relic flashes n times in this frame (display only: the relic's
                // Flashed event, whose handlers are the relic bar's and the player's effects and sound; R3-1's in-combat test).
                return RelicFlashBurst(args.Length >= 2 && int.TryParse(args[1], out int flashes) && flashes > 0 ? flashes : 50);
            case "combatstart":
                return new CmdResult(success: true, CombatStartTimer.LastReport);
            case "probe":
                return ProbeCmd(args.Skip(1).ToArray());
            case "pile" when args.Length >= 3 && args[1] == "verify" && int.TryParse(args[2], out int every) && every > 0:
                PileIndex.VerifyEvery = every;
                return new CmdResult(success: true, $"PileIndex verifies 1 answer in {every}.");
            case "pile":
                return ComparePile();
            case "text":
                return CompareText();
            case "scan":
                return CompareScan();
            case "costhooks":
                // Who keeps SnapshotCardReuse from trusting piles: the listeners of the keyword and energy cost hooks in this combat.
                if (CombatManager.Instance.DebugOnlyGetState() is not { } costCombat)
                {
                    return new CmdResult(success: false, "Start a combat first.");
                }
                string Types(int hook)
                {
                    // A local: a static would keep the combat's models (and the run) alive after the run ends.
                    List<AbstractModel>? costList = null;
                    ListenerCache.InvokeStateChangedInScope(_ => costList = HookFilter.ForCombat(costCombat, hook), costCombat);
                    return costList is { } list
                    ? $"{list.Count}: " + string.Join(", ", list.GroupBy(m => m.GetType().Name).Select(g => $"{g.Key} x{g.Count()}"))
                    : "not filtered";
                }
                return new CmdResult(success: true, $"keywords {Types(HookFilter.Keywords)} | cost {Types(HookFilter.EnergyCost)} | cost late {Types(HookFilter.EnergyCostLate)}");
            case "piletimes":
                lock (SnapshotCardReuse.PileTimes)
                {
                    string times = string.Join(", ", SnapshotCardReuse.PileTimes.OrderByDescending(p => p.Value.Ms).Select(p => $"{p.Key}: {p.Value.Calls} calls {p.Value.Ms:F1} ms {p.Value.Cards / Math.Max(1, p.Value.Calls)} cards avg"));
                    if (args.Length >= 2 && args[1] == "reset")
                    {
                        SnapshotCardReuse.PileTimes.Clear();
                    }
                    return new CmdResult(success: true, "PILE TIMES " + times);
                }
            case "costwrites":
                {
                    // Whether a cost change reaches SnapshotCardReuse's write prefix, called directly and through the card.
                    if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } costRun || LocalContext.GetMe(costRun) is not { } costMe
                        || PileType.Hand.GetPile(costMe).Cards.FirstOrDefault() is not { } costCard)
                    {
                        return new CmdResult(success: false, "Start a combat with a card in hand first.");
                    }
                    long before = SnapshotCardReuse.CostWriteCalls;
                    costCard.EnergyCost.SetThisTurnOrUntilPlayed(costCard.EnergyCost.GetWithModifiers(CostModifiers.Local));
                    long direct = SnapshotCardReuse.CostWriteCalls - before;
                    before = SnapshotCardReuse.CostWriteCalls;
                    costCard.SetToFreeThisTurn();
                    long viaCard = SnapshotCardReuse.CostWriteCalls - before;
                    return new CmdResult(success: true, $"COSTWRITES {costCard.Id}: SetThisTurnOrUntilPlayed called directly {direct}, through SetToFreeThisTurn {viaCard} (total {SnapshotCardReuse.CostWriteCalls})");
                }
            case "purehooks" when args.Length >= 2 && args[1] == "blind":
                SnapshotCardReuse.PureHooksBlindForTest = true;
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: listeners compared without their amounts (test).");
            case "purehooks" when args.Length >= 2 && args[1] is "on" or "off":
                SnapshotCardReuse.PureHookReads = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: piles {(SnapshotCardReuse.PureHookReads ? "trusted" : "read in full")} with pure cost or keyword listeners; {SnapshotCardReuse.PilesWithHooks} so far.");
            case "dirtyreads" when args.Length >= 2 && args[1] is "on" or "off":
                SnapshotCardReuse.DirtyReads = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name}: piles with written cards {(SnapshotCardReuse.DirtyReads ? "read those only" : "read in full")}; {SnapshotCardReuse.DirtyPiles} so far.");
            case "snapmoved" when args.Length >= 2 && args[1] is "on" or "off":
                SnapshotCardReuse.MovedPiles = args[1] == "on";
                return new CmdResult(success: true, $"{SnapshotCardReuse.Name} moved piles {args[1]}.");
            case "sortkeys" when args.Length >= 2 && args[1] == "counters":
                // R3-4: every answered sort's first answer is compared with the game's (Checks >= Sorts).
                bool everySortChecked = CardSortKeys.Checks >= CardSortKeys.Sorts;
                return new CmdResult(everySortChecked, $"SORTKEYS counters sorts {CardSortKeys.Sorts}, answered {CardSortKeys.Answered}, checks {CardSortKeys.Checks}, "
                    + $"mismatches {CardSortKeys.Mismatches}: {(everySortChecked ? "OK: every sort checked" : "FEWER CHECKS THAN SORTS")}");
            case "sortkeys" when args.Length >= 2 && args[1] == "partonly":
                // bdperf sortkeys partonly [cards] [passes]: the part's own sort alone, shared keys on and off (SortKeysBench.RunPartOnly).
                return SortKeysBench.RunPartOnly(args.Length >= 3 && int.TryParse(args[2], out int partCards) && partCards > 0 ? partCards : 0,
                    args.Length >= 4 && int.TryParse(args[3], out int partPasses) && partPasses > 0 ? partPasses : 5);
            case "sortkeys" when args.Length >= 2 && args[1] == "deck":
                return SortKeysBench.RunDeck();
            case "sortkeys":
                return SortKeysBench.Run(args.Length >= 2 && args[1] == "wrong");
            case "savecheck":
                return SaveCheckBench.Run(args);
            case "plaque":
                int plaqueCards = args.Length >= 2 && int.TryParse(args[1], out int pc) && pc > 0 ? pc : 200;
                return CompareTypePlaque(plaqueCards);
            case "segments" when args.Length >= 3 && args[1] == "verify" && args[2] is "all" or "sample" or "off":
                FastListeners.VerifyAll = args[2] == "all";
                FastListeners.CacheVerifyEvery = args[2] == "off" ? int.MaxValue : 256;
                return new CmdResult(success: true, $"{FastListeners.Name} compares {(FastListeners.VerifyAll ? "every" : "1 in " + FastListeners.CacheVerifyEvery)} cached pile walk with a fresh walk.");
            case "checks" when args.Length >= 2 && args[1] is "on" or "off":
                ExtraScopes.BenchChecks = args[1] == "on";
                PileIndex.VerifyEvery = ExtraScopes.BenchChecks ? 1024 : int.MaxValue;
                // CardSortKeys keeps its release builds' check: 1 answer in 1024.
                CardSortKeys.VerifyEvery = ExtraScopes.BenchChecks ? 16 : 1024;
                return new CmdResult(success: true, $"Bench re-checks {args[1]} (first-turn filter, ShouldAllowHitting, PileIndex 1/1024, CardSortKeys 1/{CardSortKeys.VerifyEvery}).");
            case "fault" when args.Length >= 2 && args[1] == "piles":
                FastListeners.FailNextPileWalk = true;
                return new CmdResult(success: true, "The next filtered pile walk throws inside FastListeners' cache (test of its fallback).");
            case "fault" when args.Length >= 2 && args[1] == "walk":
                FastListeners.FailNextFilteredWalk = true;
                return new CmdResult(success: true, "The next filtered mirror walk throws after its verdicts (test of FastListeners' walk fallback).");
            case "gcprobe":
                // bdperf gcprobe [latency mode]: fails when GCSettings.LatencyMode isn't that one.
                return GcProbe(args.Length >= 2 ? args[1] : null);
            case "opt":
                return SetOptimizations(args.Length >= 2 ? args[1] : null, args.Length >= 3 && args[2].Equals("yes", StringComparison.OrdinalIgnoreCase));
            case "listeners":
                int listenerIterations = args.Length >= 2 && int.TryParse(args[1], out int li) && li > 0 ? li : 50;
                return CompareListeners(listenerIterations);
            case "refresh":
                int refreshIterations = args.Length >= 2 && int.TryParse(args[1], out int r) && r > 0 ? r : 5;
                return CompareRefresh(refreshIterations);
            case "costcolors":
                int colorIterations = args.Length >= 2 && int.TryParse(args[1], out int ci) && ci > 0 ? ci : 20;
                return TimeCostColors(colorIterations);
            default:
                return new CmdResult(success: false, "Usage: bdperf " + Args);
        }
    }

    /// <summary>
    /// <c>bdperf costcolors [iterations]</c> (leads measurement, 2026-11-20): what the hand's cost colors cost per hand refresh, through
    /// the game's public <c>CardCostHelper.GetEnergyCostColor</c> / <c>GetStarCostColor</c> (HookFilter's mirrors when they serve, the
    /// game's helpers otherwise): in a display refresh scope as the game's own CombatStateChanged handlers, in the dispatch scope
    /// with this bench's handler (another mod's for ListenerCache: no walk output reused), and outside any scope.
    /// </summary>
    private static CmdResult TimeCostColors(int iterations)
    {
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        NPlayerHand? hand = NPlayerHand.Instance;
        if (combat == null || hand == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        Type helper = typeof(MegaCrit.Sts2.Core.Helpers.Models.CardCostHelper);
        MethodInfo? energy = AccessTools.Method(helper, "GetEnergyCostColor");
        MethodInfo? star = AccessTools.Method(helper, "GetStarCostColor");
        if (energy == null || star == null)
        {
            return new CmdResult(success: false, "CardCostHelper color methods not found");
        }
        List<CardModel> cards = hand.ActiveHolders.Select(h => h.CardNode?.Model).OfType<CardModel>().ToList();
        int listeners = combat.IterateHookListeners().Count();
        long served0 = HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCostColor], starServed0 = HookFilter.MirrorAnswers[HookFilter.MirrorStarCostColor];
        void Once()
        {
            foreach (CardModel card in cards)
            {
                energy.Invoke(null, new object?[] { card, card.CombatState });
                star.Invoke(null, new object?[] { card, card.CombatState });
            }
        }
        // 0: one display refresh scope (as the game's own handlers of the CombatStateChanged dispatch, which update the hand: the
        // augmented walks' outputs reused there); 1: the dispatch scope with this bench's handler (another mod's for ListenerCache:
        // its walks at the exact tier, nothing reused); 2: outside any scope.
        void Run(int mode)
        {
            switch (mode)
            {
                case 0:
                    ListenerCache.EnterDisplayRefresh(out int state);
                    try
                    {
                        Once();
                    }
                    finally
                    {
                        ListenerCache.ExitRefresh(null, state);
                    }
                    break;
                case 1:
                    ListenerCache.InvokeStateChangedInScope(_ => Once(), combat);
                    break;
                default:
                    Once();
                    break;
            }
        }
        double Time(int mode)
        {
            Run(mode);
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                Run(mode);
            }
            return sw.Elapsed.TotalMilliseconds / iterations;
        }
        double display = Time(0);
        double inScope = Time(1), outside = Time(2);
        long served = HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCostColor] - served0, starServed = HookFilter.MirrorAnswers[HookFilter.MirrorStarCostColor] - starServed0;
        string result = $"COSTCOLORS hand {cards.Count} cards, combat listeners {listeners}, per hand refresh: {display:F3} ms in a display scope (the game's handlers), "
            + $"{inScope:F3} ms in a dispatch scope with this bench as another mod's handler, {outside:F3} ms outside; mirror answers in both scopes energy {served}, "
            + $"star {starServed} of {2 * (iterations + 1) * cards.Count} calls each";
        Log.Info($"[{ModEntry.Id}] {result}");
        return new CmdResult(success: true, result);
    }

    /// <summary>
    /// bdperf gcprobe: what an ephemeral collection costs right now, and how much of it each 100,000 extra dependent
    /// handles (ConditionalWeakTable entries, scanned by every collection) add.
    /// </summary>
    private static CmdResult GcProbe(string? expectedMode)
    {
        static double Pause(int generation)
        {
            double total = 0;
            for (int i = 0; i < 8; i++)
            {
                TimeSpan before = GC.GetTotalPauseDuration();
                GC.Collect(generation, GCCollectionMode.Forced, blocking: true);
                total += (GC.GetTotalPauseDuration() - before).TotalMilliseconds;
            }
            return total / 8;
        }
        double gen0 = Pause(0), gen1 = Pause(1);
        var keys = new object[100_000];
        var values = new object[keys.Length];
        var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new object();
            values[i] = new object();
            table.Add(keys[i], values[i]);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        double gen0Extra = Pause(0), gen1Extra = Pause(1);
        GC.KeepAlive(keys);
        GC.KeepAlive(values);
        string mode = System.Runtime.GCSettings.LatencyMode.ToString();
        bool expected = expectedMode == null || mode.Equals(expectedMode, StringComparison.OrdinalIgnoreCase);
        return new CmdResult(success: expected, (expected ? "" : $"MISMATCH: latency {mode}, expected {expectedMode}. ") + $"GCPROBE gen0 {gen0:F1} ms, gen1 {gen1:F1} ms; with 100,000 more dependent handles gen0 {gen0Extra:F1} ms, gen1 {gen1Extra:F1} ms; latency {System.Runtime.GCSettings.LatencyMode}, server {System.Runtime.GCSettings.IsServerGC}");
    }

    /// <summary>
    /// <c>bdperf scan</c>: reading <c>Keywords</c> of every card of the draw pile, as Catastrophe, Uproar or Hidden Gem do outside any
    /// scope: vanilla walk, filtered walk, filtered walk leaving out kinds of listener. Same keywords for every card, or it says so;
    /// a row whose optimized walk never ran (back-off) is "not compared", never "OK".
    /// </summary>
    private static CmdResult CompareScan()
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!FastListeners.Applied)
        {
            return new CmdResult(success: true, "FastListeners not applied (see the log): nothing to compare.");
        }
        if (RefuseFaulted("FastListeners' kind skip", HookSkip.Name, FastListeners.Name) is { } faultedScan)
        {
            return faultedScan;
        }
        List<CardModel> cards = PileType.Draw.GetPile(me).Cards.ToList();
        (double Ms, List<string> Sets) Scan()
        {
            Stopwatch sw = Stopwatch.StartNew();
            foreach (CardModel card in cards)
            {
                _ = card.Keywords.Contains(CardKeyword.Unplayable);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            return (ms, cards.Select(c => string.Join(",", c.Keywords.OrderBy(k => k))).ToList());
        }
        bool kinds = FastListeners.KindSkipEnabled;
        // The walks this mod answers itself: FastListeners' mirrors and HookSkip's filtered walks.
        static long Walks() => FastListeners.CombatWalks + FastListeners.RunWalks + HookSkip.FilteredWalks;
        try
        {
            (double Ms, List<string> Sets) vanilla = Vanilla(() => Scan());
            FastListeners.KindSkipEnabled = false;
            Scan();
            long walksBefore = Walks();
            (double Ms, List<string> Sets) filtered = Scan();
            long walks = Walks() - walksBefore;
            FastListeners.KindSkipEnabled = true;
            Scan();
            long kindsBefore = FastListeners.KindsSkipped;
            (double Ms, List<string> Sets) skipped = Scan();
            long kindsSkipped = FastListeners.KindsSkipped - kindsBefore;
            bool same = vanilla.Sets.SequenceEqual(filtered.Sets) && vanilla.Sets.SequenceEqual(skipped.Sets);
            string verdict = !same ? "MISMATCH: different"
                : cards.Count > 0 && walks == 0 ? "not compared: no walk of this mod's ran (back-off), vanilla against vanilla"
                : cards.Count > 0 && kindsSkipped == 0 ? "identical, but the kinds row skipped no kind (back-off or none here): that row not compared"
                : "OK: identical";
            return new CmdResult(success: same, $"keyword scan of {cards.Count} cards: vanilla {vanilla.Ms:F1} ms, filtered {filtered.Ms:F1} ms ({walks} walks of this mod), "
                + $"kinds left out {skipped.Ms:F1} ms ({kindsSkipped} kinds skipped); keywords {verdict}");
        }
        finally
        {
            // Off when it faulted meanwhile (its option's fault names).
            FastListeners.KindSkipEnabled = Restored(kinds, HookSkip.Name, FastListeners.Name);
        }
    }

    private static CmdResult CompareListeners(int iterations)
    {
        RunState? run = RunManager.Instance.DebugOnlyGetState();
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        if (run == null || combat == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!FastListeners.Applied)
        {
            // Not applied (the game's code changed: a mutation, another version): a legitimate state, said as such, no "OK".
            return new CmdResult(success: true, "FastListeners not applied (see the log): nothing to compare.");
        }
        if (RefuseFaulted(FastListeners.Name, FastListeners.Name) is { } faultedListeners)
        {
            return faultedListeners;
        }

        (double CombatMs, double RunMs, List<AbstractModel> Combat, List<AbstractModel> Run) Walk()
        {
            var combatList = combat.IterateHookListeners().ToList();
            var runList = run.IterateHookListeners(combat).ToList();
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                foreach (AbstractModel _ in combat.IterateHookListeners())
                {
                }
            }
            double combatMs = sw.Elapsed.TotalMilliseconds / iterations;
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                foreach (AbstractModel _ in run.IterateHookListeners(combat))
                {
                }
            }
            return (combatMs, sw.Elapsed.TotalMilliseconds / iterations, combatList, runList);
        }

        bool wasEnabled = FastListeners.Enabled;
        (double CombatMs, double RunMs, List<AbstractModel> Combat, List<AbstractModel> Run) vanilla, fast;
        int gen2 = GC.CollectionCount(2);
        try
        {
            // The reference: the whole mod off (HookSkip's kinds, the listener caches), not FastListeners alone.
            int vanillaGen2 = 0;
            vanilla = Vanilla(() =>
            {
                Walk();
                int before = GC.CollectionCount(2);
                var walked = Walk();
                vanillaGen2 = GC.CollectionCount(2) - before;
                return walked;
            });
            FastListeners.Enabled = true;
            Walk();
            long mirrored = FastListeners.CombatWalks + FastListeners.RunWalks;
            gen2 = GC.CollectionCount(2);
            fast = Walk();
            int fastGen2 = GC.CollectionCount(2) - gen2;
            if (FastListeners.CombatWalks + FastListeners.RunWalks == mirrored)
            {
                // Its mirrors backed off (another mod patches the walks) or don't apply here: vanilla against vanilla.
                return new CmdResult(success: true, "FastListeners left the walks vanilla (back-off or not mirrored here): nothing to compare.");
            }
            // The same walks inside one of ListenerCache's refresh scopes, each twice (built, then replayed): with other mods'
            // postfixes on the walks let stand (Needless back-offs made safe), they run on the replayed unpatched list once per
            // call, so these must equal the reference too (a postfix applied to its own kept output would differ).
            long servedBefore = ListenerCache.Served;
            List<AbstractModel> scopeCombat, scopeRun, scopeCombatAgain, scopeRunAgain;
            ListenerCache.EnterRefresh();
            try
            {
                scopeCombat = combat.IterateHookListeners().ToList();
                scopeCombatAgain = combat.IterateHookListeners().ToList();
                scopeRun = run.IterateHookListeners(combat).ToList();
                scopeRunAgain = run.IterateHookListeners(combat).ToList();
            }
            finally
            {
                ListenerCache.ExitRefresh(null);
            }
            long scopeServed = ListenerCache.Served - servedBefore;
            bool scopeSame = new[] { scopeCombat, scopeCombatAgain }.All(l => vanilla.Combat.SequenceEqual(l, ReferenceEqualityComparer.Instance))
                && new[] { scopeRun, scopeRunAgain }.All(l => vanilla.Run.SequenceEqual(l, ReferenceEqualityComparer.Instance));
            bool same = vanilla.Combat.SequenceEqual(fast.Combat, ReferenceEqualityComparer.Instance)
                && vanilla.Run.SequenceEqual(fast.Run, ReferenceEqualityComparer.Instance) && scopeSame;
            string text = $"{(same ? "OK: identical" : "MISMATCH")} listeners: combat {vanilla.Combat.Count}/{fast.Combat.Count}, " +
                $"in a refresh scope {(scopeSame ? "identical" : "DIFFERENT")} ({scopeServed} of 4 walks served from its lists, combat {scopeCombat.Count}/{scopeCombatAgain.Count}, run {scopeRun.Count}/{scopeRunAgain.Count}), " +
                $"run {vanilla.Run.Count}/{fast.Run.Count}. Walk: combat {vanilla.CombatMs:F2} -> {fast.CombatMs:F2} ms, " +
                $"run {vanilla.RunMs:F2} -> {fast.RunMs:F2} ms; gen2 collections {vanillaGen2} -> {fastGen2} over {iterations * 2} walks.";
            Log.Info($"[{ModEntry.Id}] bdperf listeners: {text}");
            return new CmdResult(same, text);
        }
        finally
        {
            // Off when it faulted meanwhile (FastListeners.Disable also ran ApplyAll, which this would undo).
            FastListeners.Enabled = Restored(wasEnabled, FastListeners.Name);
        }
    }

    /// <summary>
    /// <c>bdperf opt on|off [yes]</c>: every optimization off/on for an A/B comparison in the same session, as <c>bdopt</c> does
    /// but for this session only, never saved: BaseLib saves <see cref="Config.Settings.MasterChoice"/>, the player's choice, at any
    /// settings save and on quit (every config, on NGame.Quit), so the next game start isn't "started off"; the menu box keeps
    /// showing the saved choice. In a co-op run a switch either way warns and needs <c>bdperf opt on|off yes</c>, as <c>bdopt</c>
    /// does (the lock both ways): off turns off the fast combat start the other players keep (<see cref="Config.Settings.CoopOffWarning"/>),
    /// on in an all-off game makes the same mixed pair (<see cref="Config.Settings.CoopOnWarning"/>). The run only (no lobby
    /// walk); not after a start with the mod off (nothing of it runs this session).
    /// </summary>
    private static CmdResult SetOptimizations(string? value, bool confirmed)
    {
        if (value is not ("on" or "off"))
        {
            return new CmdResult(success: false, "Usage: bdperf opt on|off");
        }
        bool on = value == "on";
        if (!confirmed && on != Config.Settings.Master && !ModEntry.StartedOff && Config.Settings.InCoopRun)
        {
            return new CmdResult(success: false, (on ? Config.Settings.CoopOnWarning : Config.Settings.CoopOffWarning) + " " + Config.Settings.CoopRejoinNow
                + $"\nTo turn it {value} anyway, type: bdperf opt {value} yes");
        }
        Config.Settings.SetMaster(on, notify: false, sessionOnly: true);
        return new CmdResult(success: true, $"BigDeckOptimization optimizations {(on ? "ON" : "OFF (vanilla)")} (this session only, never saved; the menu box keeps showing the saved choice).");
    }

    /// <summary>
    /// bdperf plaque [n]: BaseLib's card type line for the first n cards of the local player's piles, TypePlaqueSkip
    /// off (BaseLib's walk of every hook listener) then on: the same LocString object for every card, and the times.
    /// </summary>
    private static CmdResult CompareTypePlaque(int count)
    {
        Player? me = RunManager.Instance.DebugOnlyGetState() is { } state ? LocalContext.GetMe(state) : null;
        if (me == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (!TypePlaqueSkip.Applied)
        {
            return new CmdResult(success: true, "TypePlaqueSkip not applied (BaseLib absent or changed, see the log): nothing to compare.");
        }
        if (RefuseFaulted(TypePlaqueSkip.Name, TypePlaqueSkip.Name) is { } faultedPlaque)
        {
            return faultedPlaque;
        }
        List<CardModel> cards = me.Piles.SelectMany(p => p.Cards).Take(count).ToList();
        var originals = cards.Select(c => c.Type.ToLocString()).ToList();
        bool wasEnabled = TypePlaqueSkip.Enabled;
        long skippedBefore = TypePlaqueSkip.Skipped;
        List<LocString> expected, actual;
        // Timed inside Vanilla: its two switch passes (every option set off, then back) aren't BaseLib's cost.
        Stopwatch vanilla = new();
        TypePlaqueSkip.Enabled = false;
        try
        {
            // BaseLib's walk of every hook listener, with the whole mod off (listener walks included), as the game runs it.
            expected = Vanilla(() =>
            {
                vanilla.Start();
                List<LocString> lines = cards.Select((c, i) => TypePlaqueSkip.Invoke(originals[i], c)).ToList();
                vanilla.Stop();
                return lines;
            });
        }
        finally
        {
            TypePlaqueSkip.Enabled = Restored(wasEnabled, TypePlaqueSkip.Name);
        }
        Stopwatch fast = Stopwatch.StartNew();
        TypePlaqueSkip.Enabled = true;
        try
        {
            actual = cards.Select((c, i) => TypePlaqueSkip.Invoke(originals[i], c)).ToList();
        }
        finally
        {
            TypePlaqueSkip.Enabled = Restored(wasEnabled, TypePlaqueSkip.Name);
        }
        fast.Stop();
        if (cards.Count > 0 && TypePlaqueSkip.Skipped == skippedBefore)
        {
            // Every render left to BaseLib (a mod changes the type line, a back-off): BaseLib's walk against itself.
            return new CmdResult(success: true, $"{TypePlaqueSkip.Name} skipped none of {cards.Count} cards: nothing to compare.");
        }
        int mismatches = Enumerable.Range(0, cards.Count)
            .Count(i => !ReferenceEquals(expected[i], actual[i]) || expected[i].GetFormattedText() != actual[i].GetFormattedText());
        string verdict = mismatches == 0 ? "OK: identical type lines" : $"MISMATCH: {mismatches} cards differ";
        return new CmdResult(mismatches == 0,
            $"type line — {cards.Count} cards ({me.Piles.Sum(p => p.Cards.Count)} in piles), skipped {TypePlaqueSkip.Skipped - skippedBefore}\n"
            + $"  BaseLib: {vanilla.Elapsed.TotalMilliseconds:F1} ms ({vanilla.Elapsed.TotalMilliseconds / Math.Max(1, cards.Count):F3} ms/card)\n"
            + $"  skip   : {fast.Elapsed.TotalMilliseconds:F1} ms ({fast.Elapsed.TotalMilliseconds / Math.Max(1, cards.Count):F3} ms/card)\n  {verdict}");
    }

    /// <summary>
    /// <c>bdperf pile</c>: CardModel.Pile for every card of every pile, indexed vs the game's scan
    /// (<see cref="PileIndex.Vanilla"/>): same answers, and the times.
    /// </summary>
    private static CmdResult ComparePile()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return new CmdResult(success: false, "Start a run first.");
        }
        if (!PileIndex.Applied)
        {
            // Not applied (the game's code changed: a mutation, another version): a legitimate state, said as such, no "OK".
            return new CmdResult(success: true, $"{PileIndex.Name} not applied (see the log): nothing to compare.");
        }
        if (!PileIndex.Enabled)
        {
            return new CmdResult(success: false, $"{PileIndex.Name} is off (setting, fault or back-off): the index would be compared with itself.");
        }
        System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel> cards = state.Players
            .SelectMany(p => p.Piles).SelectMany(p => p.Cards).ToList();
        int mismatches = 0;
        Stopwatch vanilla = Stopwatch.StartNew();
        var expected = cards.Select(PileIndex.Vanilla).ToList();
        vanilla.Stop();
        long fastBefore = PileIndex.Fast;
        Stopwatch fast = Stopwatch.StartNew();
        var actual = cards.Select(c => c.Pile).ToList();
        fast.Stop();
        if (cards.Count > 0 && PileIndex.Fast == fastBefore)
        {
            // No answer came from the index (a back-off, every card unknown): the game's answers against themselves.
            return new CmdResult(success: true, $"{PileIndex.Name} answered none of {cards.Count} cards (back-off or fallbacks {PileIndex.Fallbacks}): nothing to compare.");
        }
        for (int i = 0; i < cards.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                mismatches++;
            }
        }
        string verdict = mismatches == 0 ? "OK: identical piles" : $"MISMATCH: {mismatches} cards differ";
        return new CmdResult(mismatches == 0,
            $"pile lookup — {cards.Count} cards, index {(PileIndex.Enabled ? "on" : "OFF")}\n"
            + $"  vanilla: {vanilla.Elapsed.TotalMilliseconds:F1} ms ({vanilla.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, cards.Count):F2} us/card)\n"
            + $"  index  : {fast.Elapsed.TotalMilliseconds:F1} ms ({fast.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, cards.Count):F2} us/card)\n"
            + $"  fast {PileIndex.Fast}, fallbacks {PileIndex.Fallbacks}, verified {PileIndex.Verified}\n  {verdict}");
    }

    /// <summary>
    /// Every card's description (with its enchantment and affliction texts) formatted with TextPoolReuse on and off:
    /// identical texts, time and bytes allocated each way.
    /// </summary>
    private static CmdResult CompareText()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return new CmdResult(success: false, "Start a run first.");
        }
        if (!TextPoolReuse.Applied)
        {
            return new CmdResult(success: true, $"{TextPoolReuse.Name} not applied (see the log): nothing to compare.");
        }
        if (RefuseFaulted(TextPoolReuse.Name, TextPoolReuse.Name) is { } faultedText)
        {
            return faultedText;
        }
        System.Collections.Generic.List<MegaCrit.Sts2.Core.Models.CardModel> cards = state.Players
            .SelectMany(p => p.Piles).SelectMany(p => p.Cards).ToList();
        bool was = TextPoolReuse.Enabled;
        try
        {
            (System.Collections.Generic.List<string> texts, double ms, long bytes) Format(bool reuse)
            {
                TextPoolReuse.Enabled = reuse;
                long before = GC.GetAllocatedBytesForCurrentThread();
                Stopwatch watch = Stopwatch.StartNew();
                var texts = cards.Select(c => c.GetDescriptionForPile(c.Pile?.Type ?? PileType.Deck)).ToList();
                return (texts, watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            var vanilla = Vanilla(() => Format(false));
            long servedBefore = TextPoolReuse.Served;
            var reused = Format(true);
            if (cards.Count > 0 && TextPoolReuse.Served == servedBefore)
            {
                return new CmdResult(success: true, $"{TextPoolReuse.Name} served no text of {cards.Count} cards: nothing to compare.");
            }
            int mismatches = vanilla.texts.Zip(reused.texts).Count(pair => !string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
            string verdict = mismatches == 0 ? "OK: identical texts" : $"MISMATCH: {mismatches} texts differ";
            return new CmdResult(mismatches == 0,
                $"card texts — {cards.Count} cards, {TextPoolReuse.Name} {(TextPoolReuse.Applied ? "applied" : "NOT applied")}\n"
                + $"  vanilla: {vanilla.ms:F1} ms, {vanilla.bytes / 1048576.0:F1} MB allocated\n"
                + $"  shared : {reused.ms:F1} ms, {reused.bytes / 1048576.0:F1} MB allocated\n  {verdict}");
        }
        finally
        {
            TextPoolReuse.Enabled = Restored(was, TextPoolReuse.Name);
        }
    }

    private static bool _probeApplied, _nodesProbed;

    private static CmdResult ProbeCmd(string[] args)
    {
        // Only when asked: a probe's stub allocates on every call of very hot methods (CardModel.Pile), which falsifies measures.
        if (!_probeApplied && !(args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            _probeApplied = true;
            ModEntry.TryApply(PerfProbe.Name, PerfProbe.Apply);
        }
        switch (args.Length > 0 ? args[0].ToLowerInvariant() : "")
        {
            case "":
                return new CmdResult(success: true, PerfProbe.Report());
            case "reset":
                PerfProbe.Reset();
                return new CmdResult(success: true, "Probe stats cleared.");
            case "log":
                PerfProbe.LogReport();
                return new CmdResult(success: true, "Probe report written to the game log.");
            case "on" or "off":
                PerfProbe.Enabled = args[0].ToLowerInvariant() == "on";
                return new CmdResult(success: true, $"Probe {(PerfProbe.Enabled ? "on" : "off")}.");
            case "nodes":
                // Once: a second Harmony patch of the same callbacks would run every probe twice (rows counted twice).
                if (_nodesProbed)
                {
                    return new CmdResult(success: true, "Node callbacks already probed.");
                }
                _nodesProbed = true;
                ModEntry.TryApply(PerfProbe.Name + ".Nodes", PerfProbe.ApplyNodes);
                return new CmdResult(success: true, "Node callbacks probed.");
            case "hitch" when args.Length > 1 && double.TryParse(args[1], out double ms) && ms > 0:
                PerfProbe.HitchMs = ms;
                return new CmdResult(success: true, $"Hitch threshold {ms:F0} ms.");
            default:
                return new CmdResult(success: false, "Usage: bdperf probe [reset|log|on|off|nodes|hitch <ms>]");
        }
    }

    private static CmdResult Verify(string? mode)
    {
        // "check": fails when a snapshot differed, so a scenario's cmd line ends the session there.
        if (mode == "check")
        {
            return new CmdResult(success: SnapshotVerifier.Mismatches == 0 && SnapshotVerifier.Verified > 0,
                $"verified {SnapshotVerifier.Verified}, mismatches {SnapshotVerifier.Mismatches}");
        }
        if (mode != null)
        {
            if (!TryParseEnum(mode, out VerifyMode parsed))
            {
                return new CmdResult(success: false, "Mode must be off, sample or all.");
            }
            SnapshotVerifier.Mode = parsed;
        }
        return new CmdResult(success: true,
            $"verify mode: {SnapshotVerifier.Mode} — snapshots {SnapshotVerifier.Snapshots}, verified {SnapshotVerifier.Verified}, mismatches {SnapshotVerifier.Mismatches}, skipped (too large) {SnapshotVerifier.SkippedTooLarge}\n"
            + $"  {ListenerCache.Name}: snapshot {(ListenerCache.SnapshotEnabled ? "on" : "OFF")}, refresh {(ListenerCache.RefreshEnabled ? "on" : "OFF")}, {HookFilter.Name}: {(HookFilter.Enabled ? "on" : "OFF")}, reuse {(ListenerCache.ReuseEnabled ? "on" : "OFF")} (reused {ListenerCache.Reused}, verified {ListenerCache.ReuseVerified}), hitting checks {HookFilter.HittingChecks} mismatches {HookFilter.HittingMismatches}");
    }

    /// <summary>A highlight color nothing in the game sets: a refresh that leaves it didn't write the highlight.</summary>
    private static readonly Godot.Color DirtyColor = new(0.123f, 0.456f, 0.789f, 0.5f);

    /// <summary>
    /// Replays what the game does on every combat state change (enchantment recalculation + hand refresh), vanilla (the whole
    /// mod off) vs cached, and checks both leave the exact same result: the enchantments' values, the vanilla snapshot
    /// checksum, and what the hand refresh writes, the part the cached listener lists serve (each hand card's and its
    /// enchantment's preview and enchanted values, from hooks such as ModifyDamage, and its highlight color, from CanPlay and
    /// the glows). The game refreshed after the last action already, so a refresh that skipped its work would leave that state
    /// as it is and look right: before each side, every plain enchantment's values, the hand cards' preview values and highlight
    /// colors are set to values nothing computes, so each side has to recompute them from the same starting state. Most
    /// enchantments' RecalculateValues is the empty default (Adroit overrides it in v0.107): their dirtied values stay so on both
    /// sides, and the line says how many the game did recompute. Stacked Limitless enchantments' values aren't touched
    /// (LimitlessRecalcSkip's own verification covers them: a write it never sees would only turn it off). Everything is put
    /// back afterwards.
    /// </summary>
    private static CmdResult CompareRefresh(int iterations)
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        NPlayerHand? hand = NPlayerHand.Instance;
        Player? me = state == null ? null : LocalContext.GetMe(state);
        if (state == null || combat == null || hand == null || me?.PlayerCombatState == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        if (ModEntry.NotApplied(ListenerCache.Name))
        {
            // Its switch stays on when it didn't apply: the cached side would be the game's code.
            return new CmdResult(success: true, $"{ListenerCache.Name} not applied (see the log): nothing to compare.");
        }
        if (!ListenerCache.RefreshEnabled)
        {
            return new CmdResult(success: false, "combat_refresh is off (setting, fault or back-off): the cached refresh would be compared with itself.");
        }
        MethodInfo handRefresh = AccessTools.Method(typeof(NPlayerHand), "OnCombatStateChanged");
        ChecksumTracker tracker = RunManager.Instance.ChecksumTracker;
        Type? multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType);
        List<EnchantmentModel> enchantments = me.PlayerCombatState.AllCards.Select(c => c.Enchantment).OfType<EnchantmentModel>().ToList();
        List<MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> plain = enchantments
            .Where(e => multi == null || !multi.IsInstanceOfType(e))
            .SelectMany(e => e.DynamicVars.Select(v => v.Value)).ToList();
        // What the recalculation writes, stacked enchantments' inner ones included (read only).
        List<EnchantmentModel> all = enchantments.Concat(enchantments.Where(e => multi != null && multi.IsInstanceOfType(e))
            .SelectMany(e => AccessTools.Property(multi!, "EnchantmentsOnCards")?.GetValue(e) as IEnumerable<CardModel> ?? Enumerable.Empty<CardModel>())
            .Select(c => c.Enchantment).OfType<EnchantmentModel>()).ToList();
        string Values() => string.Join(";", all.SelectMany(e => e.DynamicVars.Select(v => $"{v.Key}={v.Value.BaseValue}/{v.Value.EnchantedValue}")));
        var saved = plain.Select(v => (v.BaseValue, v.EnchantedValue, v.PreviewValue)).ToList();
        // What the hand refresh writes: the shown cards' variables (theirs and their enchantment's) and highlights.
        List<NHandCardHolder> holders = hand.ActiveHolders.Where(h => h.CardNode?.Model != null).ToList();
        IEnumerable<DynamicVar> VarsOf(CardModel card) =>
            card.DynamicVars.Values.Concat(card.Enchantment?.DynamicVars.Values ?? Enumerable.Empty<DynamicVar>());
        List<DynamicVar> shown = holders.SelectMany(h => VarsOf(h.CardNode!.Model!)).Distinct(ReferenceEqualityComparer.Instance)
            .Cast<DynamicVar>().ToList();
        var savedShown = shown.Select(v => (v.EnchantedValue, v.PreviewValue)).ToList();
        var savedHighlights = holders.Select(h => h.CardNode!.CardHighlight.Modulate).ToList();
        string Shown() => string.Join(";", holders.Select(h => $"{h.CardNode!.Model!.Id.Entry}:{h.CardNode.CardHighlight.Modulate}:"
            + string.Join(",", VarsOf(h.CardNode.Model!).Select(v => $"{v.PreviewValue}/{v.EnchantedValue}"))));

        void RefreshOnce()
        {
            me.PlayerCombatState.RecalculateCardValues();
            // Same wrapping as the game's CombatStateChanged dispatch (one scope for all handlers).
            ListenerCache.InvokeStateChangedInScope(s => handRefresh.Invoke(hand, new object[] { s }), combat);
        }

        double Time()
        {
            RefreshOnce(); // warm-up
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                RefreshOnce();
            }
            return sw.Elapsed.TotalMilliseconds / iterations;
        }

        // How many of the dirtied values the game itself wrote again (vanilla side): what the comparison really covers.
        int enchantmentsRecomputed = 0, previewsRecomputed = 0;

        (string Values, string Shown, uint Checksum) FromDirty(bool vanilla)
        {
            foreach (var v in plain)
            {
                v.BaseValue = -7m; // Also sets the enchanted and preview values (ResetToBase).
            }
            foreach (var v in shown)
            {
                v.PreviewValue = -7m;
            }
            foreach (NHandCardHolder h in holders)
            {
                h.CardNode!.CardHighlight.Modulate = DirtyColor;
            }
            if (vanilla)
            {
                Vanilla(() => { RefreshOnce(); return 0; });
                enchantmentsRecomputed = plain.Count(v => v.BaseValue != -7m || v.EnchantedValue != -7m);
                previewsRecomputed = shown.Count(v => v.PreviewValue != -7m);
            }
            else
            {
                RefreshOnce();
            }
            return (Values(), Shown(), Vanilla(() => tracker.GenerateChecksum(NetFullCombatState.FromRun(state, null))));
        }

        bool wasEnabled = ListenerCache.RefreshEnabled;
        VerifyMode verifyMode = SnapshotVerifier.Mode;
        double vanillaMs, cachedMs;
        (string Values, string Shown, uint Checksum) vanilla, cached;
        // What this mod answered on the cached side (the checksum inside FromDirty runs with the mod off: it counts nothing).
        // Nothing at all: every part backed off, the cached side was the game's code: "not compared", never "OK".
        static (long Cache, long Filter, long Walks, long Others) Served() => (ListenerCache.Served, HookFilter.Served,
            FastListeners.CombatWalks + FastListeners.RunWalks + HookSkip.FilteredWalks,
            TextPoolReuse.Served + ThemeColorSkip.Skipped + DeckCountMemo.Kept + DeckCountMemo.Updated);
        (long Cache, long Filter, long Walks, long Others) served = default;
        try
        {
            SnapshotVerifier.Mode = VerifyMode.Off;
            vanillaMs = Vanilla(() => Time());
            vanilla = FromDirty(true);
            var servedBefore = Served();
            cachedMs = Time();
            cached = FromDirty(false);
            var servedAfter = Served();
            served = (servedAfter.Cache - servedBefore.Cache, servedAfter.Filter - servedBefore.Filter,
                servedAfter.Walks - servedBefore.Walks, servedAfter.Others - servedBefore.Others);
        }
        finally
        {
            for (int i = 0; i < plain.Count; i++)
            {
                plain[i].BaseValue = saved[i].BaseValue;
                plain[i].EnchantedValue = saved[i].EnchantedValue;
                plain[i].PreviewValue = saved[i].PreviewValue;
            }
            for (int i = 0; i < shown.Count; i++)
            {
                shown[i].EnchantedValue = savedShown[i].EnchantedValue;
                shown[i].PreviewValue = savedShown[i].PreviewValue;
            }
            for (int i = 0; i < holders.Count; i++)
            {
                holders[i].CardNode!.CardHighlight.Modulate = savedHighlights[i];
            }
            ListenerCache.RefreshEnabled = Restored(wasEnabled, ListenerCache.Name);
            SnapshotVerifier.Mode = verifyMode;
            // The hand shows the values put back, as after any action.
            ListenerCache.InvokeStateChangedInScope(s => handRefresh.Invoke(hand, new object[] { s }), combat);
        }

        bool identical = vanilla.Checksum == cached.Checksum && vanilla.Values == cached.Values && vanilla.Shown == cached.Shown;
        bool engaged = served.Cache + served.Filter + served.Walks + served.Others > 0;
        string msg = $"refresh x{iterations} — hand: {me.PlayerCombatState.Hand.Cards.Count} cards, combat cards: {me.PlayerCombatState.AllCards.Count()}, "
            + $"from a dirtied state: enchantment values dirtied {plain.Count} (of {all.Sum(e => e.DynamicVars.Count)}), recomputed by the game {enchantmentsRecomputed} "
            + $"(the others' RecalculateValues writes nothing: compared as left), hand cards {holders.Count} with {shown.Count} shown values, "
            + $"{previewsRecomputed} recomputed by the game, highlights dirtied\n"
            + $"  vanilla: {vanillaMs:F2} ms per refresh, state checksum {vanilla.Checksum}\n"
            + $"  cached : {cachedMs:F2} ms per refresh, state checksum {cached.Checksum}\n"
            + $"  served on the cached side: listener cache {served.Cache}, filter {served.Filter}, walks {served.Walks}, texts/colors/counts {served.Others}\n"
            + (identical && !engaged ? "  not compared: nothing of this mod served the cached side (every part backed off): identical, but the game's code against itself"
                : identical ? $"  OK: identical state, x{vanillaMs / Math.Max(0.001, cachedMs):F1} faster"
                : vanilla.Checksum != cached.Checksum ? "  MISMATCH: refresh left a different game state!"
                : vanilla.Values != cached.Values ? "  MISMATCH: refresh left different enchantment values!"
                : $"  MISMATCH: refresh left the hand showing something else!\n    vanilla: {vanilla.Shown}\n    cached : {cached.Shown}");
        if (identical)
        {
            Log.Info("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        else
        {
            Log.Error("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        return new CmdResult(success: identical, msg);
    }

    /// <summary>
    /// <c>bdperf snapnet</c>: the snapshot walk reuse's whole path through the game's own call (<c>NetFullCombatState.FromRun</c>
    /// with every patch: ListenerCache's EnterSnapshot, the walks, SnapshotVerifier's postfix, the net, RebuildIfDetected), with
    /// other mods' walk postfixes installed (foreignpatch walk shapes). Four snapshots of the same state, each one's checksum
    /// compared with the game's own (the whole mod off): plain (reused, compared by the net, nothing found); with the net made
    /// to report a difference (a detection, the snapshot rebuilt: the rebuilt one is what FromRun returns); both again inside a
    /// display refresh with game logic resuming inline (the checksum after an action on a peer whose hand selection ended inside
    /// the CombatStateChanged dispatch): the same reuses as the plain one (decided as a snapshot with nothing around it).
    /// SnapshotVerifier's live re-check is off meanwhile. Singleplayer.
    /// </summary>
    private static CmdResult SnapNet()
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        ChecksumTracker tracker = RunManager.Instance.ChecksumTracker;
        VerifyMode mode = SnapshotVerifier.Mode;
        var lines = new List<string>();
        bool ok = true;
        try
        {
            SnapshotVerifier.Mode = VerifyMode.Off;
            uint vanilla = Vanilla(() => tracker.GenerateChecksum(NetFullCombatState.FromRun(state, null)));
            (uint Checksum, long Reuses, long Kept, long Detections, int Rebuilt) Run(bool inject, bool nested)
            {
                long reuses = ListenerCache.SnapshotWalkReuses, kept = ListenerCache.SnapshotWalksKept, detections = ListenerCache.SnapshotDetections;
                int rebuilt = SnapshotVerifier.Rebuilt;
                if (inject)
                {
                    ListenerCache.InjectNetDifferenceForTest();
                }
                NetFullCombatState snapshot = nested
                    ? ListenerCache.InsideSuspendedDispatchForTest(() => NetFullCombatState.FromRun(state, null))
                    : NetFullCombatState.FromRun(state, null);
                return (tracker.GenerateChecksum(snapshot), ListenerCache.SnapshotWalkReuses - reuses, ListenerCache.SnapshotWalksKept - kept,
                    ListenerCache.SnapshotDetections - detections, SnapshotVerifier.Rebuilt - rebuilt);
            }
            var plain = Run(inject: false, nested: false);
            var injected = Run(inject: true, nested: false);
            var nestedPlain = Run(inject: false, nested: true);
            var nestedInjected = Run(inject: true, nested: true);
            void Line(string what, (uint Checksum, long Reuses, long Kept, long Detections, int Rebuilt) r, bool expected)
            {
                lines.Add($"  {what}: checksum {r.Checksum} (game's {vanilla}), kept {r.Kept}, reused {r.Reuses}, detections {r.Detections}, rebuilt {r.Rebuilt}: {(expected ? "as expected" : "UNEXPECTED")}");
                ok &= expected;
            }
            Line("plain", plain, plain.Checksum == vanilla && plain.Reuses > 0 && plain.Detections == 0 && plain.Rebuilt == 0);
            Line("net made to differ", injected, injected.Checksum == vanilla && injected.Reuses > 0 && injected.Detections == 1 && injected.Rebuilt == 1);
            Line("inside a suspended dispatch", nestedPlain, nestedPlain.Checksum == vanilla && nestedPlain.Reuses == plain.Reuses
                && nestedPlain.Kept == plain.Kept && nestedPlain.Detections == 0 && nestedPlain.Rebuilt == 0);
            Line("inside a suspended dispatch, net made to differ", nestedInjected, nestedInjected.Checksum == vanilla
                && nestedInjected.Reuses == plain.Reuses && nestedInjected.Detections == 1 && nestedInjected.Rebuilt == 1);
        }
        catch (Exception e)
        {
            lines.Add("  threw: " + e);
            ok = false;
        }
        finally
        {
            SnapshotVerifier.Mode = mode;
        }
        string msg = $"snapnet — the snapshot walk reuse through FromRun ({(ok ? "OK" : "MISMATCH")})\n" + string.Join("\n", lines);
        if (ok)
        {
            Log.Info("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        else
        {
            Log.Error("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        return new CmdResult(success: ok, msg);
    }

    private static CmdResult CompareChecksum(int iterations)
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Start a combat first: the checksum covers combat piles.");
        }
        ChecksumTracker tracker = RunManager.Instance.ChecksumTracker;
        // Each row adds one of the three as the settings have it: one off would compare that row with the previous one.
        string? off = !ModEntry.NotApplied(ListenerCache.Name) && !ListenerCache.SnapshotEnabled ? "coop_checksum (ListenerCache)"
            : !ModEntry.NotApplied(HookFilter.Name) && !HookFilter.Enabled ? "hook_filter (HookFilter)"
            : !ModEntry.NotApplied(SnapshotCardReuse.Name) && !SnapshotCardReuse.Enabled ? "coop_checksum (SnapshotCardReuse)"
            : null;
        if (off != null)
        {
            return new CmdResult(success: false, $"{off} is off (setting, fault or back-off): its checksum would be compared with itself.");
        }

        bool cacheWasEnabled = ListenerCache.SnapshotEnabled, filterWasEnabled = HookFilter.Enabled, reuseWasEnabled = SnapshotCardReuse.Enabled;
        VerifyMode verifyMode = SnapshotVerifier.Mode;
        Sample vanilla, others, cacheOnly, filtered, optimized;
        // What each row's optimization served while measured: a row whose part backed off at runtime compares the row before it
        // with itself.
        static long ReuseCount() => SnapshotCardReuse.Reused + SnapshotCardReuse.PilesReused + SnapshotCardReuse.PilesTrusted + SnapshotCardReuse.BitsReused
            + SnapshotCardReuse.PileBitsReused + SnapshotCardReuse.PileBitsComposed + SnapshotCardReuse.MovedReused;
        static long? FilterCount() => HookFilter.Served;
        long cacheServed = 0, reuseServed = 0;
        long? filterServed = null;
        // The checksum snapshot's walk reuse (other mods' postfixes on the walks), in every row with the state check on: reuses,
        // fresh walks its net compared, snapshots it found wrong (built again the game's way: the row's checksum is the game's).
        long snapReusesBefore = ListenerCache.SnapshotWalkReuses, snapComparedBefore = ListenerCache.SnapshotNetCompared, snapDetectionsBefore = ListenerCache.SnapshotDetections;
        try
        {
            // The bench compares against vanilla itself; live verification would skew timings.
            SnapshotVerifier.Mode = VerifyMode.Off;
            // The whole mod off; then every other optimization on (listener walks, pile lookup, saved properties...) without
            // the snapshot's own three; then those added one by one, each as the settings have it.
            vanilla = Vanilla(() => Measure(state, tracker, iterations));
            ListenerCache.SnapshotEnabled = false;
            HookFilter.Enabled = false;
            SnapshotCardReuse.Enabled = false;
            others = Measure(state, tracker, iterations);
            ListenerCache.SnapshotEnabled = Restored(cacheWasEnabled, ListenerCache.Name, ListenerCache.SnapshotFault);
            long before = ListenerCache.Served;
            cacheOnly = Measure(state, tracker, iterations);
            cacheServed = ListenerCache.Served - before;
            HookFilter.Enabled = Restored(filterWasEnabled, HookFilter.Name);
            long? filterBefore = FilterCount();
            filtered = Measure(state, tracker, iterations);
            filterServed = FilterCount() - filterBefore;
            SnapshotCardReuse.Enabled = Restored(reuseWasEnabled, SnapshotCardReuse.Name, ListenerCache.SnapshotFault);
            before = ReuseCount();
            optimized = Measure(state, tracker, iterations);
            reuseServed = ReuseCount() - before;
        }
        finally
        {
            // A part that faulted during the bench (its own check saw a difference) stays off, with the fault names that turn
            // it off in the settings (coop_checksum's, hook_filter's, and the card reuse's own plus the state check's).
            ListenerCache.SnapshotEnabled = Restored(cacheWasEnabled, ListenerCache.Name, ListenerCache.SnapshotFault);
            HookFilter.Enabled = Restored(filterWasEnabled, HookFilter.Name);
            SnapshotCardReuse.Enabled = Restored(reuseWasEnabled, SnapshotCardReuse.Name, ListenerCache.SnapshotFault);
            SnapshotVerifier.Mode = verifyMode;
        }

        int cards = 0, relics = 0;
        foreach (Player p in state.Players)
        {
            relics += p.Relics.Count;
            if (p.PlayerCombatState != null)
            {
                foreach (var _ in p.PlayerCombatState.AllCards)
                {
                    cards++;
                }
            }
        }

        bool identical = vanilla.Checksum == others.Checksum && vanilla.Checksum == cacheOnly.Checksum && vanilla.Checksum == filtered.Checksum && vanilla.Checksum == optimized.Checksum;
        // Applied and on (checked above), yet nothing served while its row was measured: that row proved nothing. Said ("not compared",
        // never "OK"), not failed, as the other benches do for a part that backed off: a game change another part backs off on
        // (a new pile writer: ListenerCache gone, HookFilter has no list to serve) idles a row by design. effects-check proves each
        // part serves in a normal session.
        var idle = new List<string>();
        // The cache row may serve nothing and that's no failure: with HookFilter off there, every hook walk HookSkip filters goes
        // through FastListeners' filtered walks, never through the walk ListenerCache caches (all 154 of v0.113's hooks). Said, not "OK".
        string cacheNote = !ModEntry.NotApplied(ListenerCache.Name) && cacheServed == 0
            ? "  cache row: nothing served (HookSkip's filtered walks answered every hook), nothing to compare there\n" : "";
        if (!ModEntry.NotApplied(HookFilter.Name) && filterServed == 0)
        {
            idle.Add("filter (HookFilter served 0)");
        }
        if (!ModEntry.NotApplied(SnapshotCardReuse.Name) && reuseServed == 0)
        {
            idle.Add("card reuse (SnapshotCardReuse reused 0)");
        }
        bool engaged = idle.Count == 0;
        double speedup = (vanilla.SnapshotMs + vanilla.HashMs) / Math.Max(0.001, optimized.SnapshotMs + optimized.HashMs);
        string msg = $"checksum x{iterations} — players: {state.Players.Count}, combat cards: {cards}, relics: {relics}\n"
            + vanilla.Format("vanilla     ") + "\n"
            + others.Format("others      ") + "\n"
            + cacheOnly.Format("cache       ") + "\n"
            + filtered.Format("cache+filter") + "\n"
            + optimized.Format("+card reuse ") + "\n"
            + $"  served: cache {cacheServed}, filter {(filterServed is { } f ? f.ToString() : "not counted")}, card reuse {reuseServed}\n"
            + $"  snapshot walks: reused {ListenerCache.SnapshotWalkReuses - snapReusesBefore}, compared by the net {ListenerCache.SnapshotNetCompared - snapComparedBefore}, "
            + $"caught by the net and built again {ListenerCache.SnapshotDetections - snapDetectionsBefore}\n"
            + cacheNote
            + (!identical ? "  MISMATCH: optimized checksum differs from vanilla!"
                : !engaged ? $"  not compared: {string.Join(", ", idle)}: identical checksums, but that row compared the row before it with itself"
                : $"  OK: identical checksums, x{speedup:F1} faster");
        if (identical)
        {
            Log.Info("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        else
        {
            Log.Error("[BigDeckOptimization] " + msg.Replace("\n", " |"));
        }
        return new CmdResult(success: identical, msg);
    }

    private static Sample Measure(RunState state, ChecksumTracker tracker, int iterations)
    {
        // Warm-up so JIT doesn't pollute the first sample.
        tracker.GenerateChecksum(NetFullCombatState.FromRun(state, null));

        double snapshotMs = 0, hashMs = 0, worstMs = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        uint checksum = 0;
        Stopwatch sw = new();
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            NetFullCombatState snapshot = NetFullCombatState.FromRun(state, null);
            double snap = sw.Elapsed.TotalMilliseconds;
            checksum = tracker.GenerateChecksum(snapshot);
            double total = sw.Elapsed.TotalMilliseconds;
            snapshotMs += snap;
            hashMs += total - snap;
            worstMs = Math.Max(worstMs, total);
        }
        return new Sample
        {
            SnapshotMs = snapshotMs / iterations,
            HashMs = hashMs / iterations,
            WorstMs = worstMs,
            AllocatedKb = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024.0 / iterations,
            Checksum = checksum,
        };
    }
}
