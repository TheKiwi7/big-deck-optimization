using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Unattended test sessions: only when the game is started with <c>--bdtest &lt;script&gt;</c> (never on a normal
/// launch). Uses save profile <see cref="TestProfile"/> for the whole session without saving that choice, so the
/// player's profiles and last-profile setting are untouched. Runs the script's lines one by one, logs everything with
/// the "AutoTest" prefix, and quits the game at the end, on error, or after <c>timeout</c>.
///
/// Script lines (# comments):
///   newrun &lt;CHARACTER&gt; [seed]   wait &lt;s&gt;   waitfor menu|run|combat|idle|nocombat [timeout s]
///   cmd &lt;console command&gt;      play &lt;n&gt;   endturn   timeout &lt;s&gt;   log &lt;text&gt;   quit
///   {hash:NAME} anywhere = hash of the shared run file NAME   speed normal|fast|instant (default fast)   enemyhp &lt;hp&gt; (enemies of the next combats)   marker &lt;text&gt; (phase marks for trace analysis)
/// </summary>
public static class AutoTest
{
    public const string Name = "AutoTest";
    public const int TestProfile = 3;

    private static string? _script;
    private static DevConsole? _console;
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _deadline = 1800;

    public static void Apply(Harmony harmony)
    {
        _script = ScriptArg();
        if (_script == null)
        {
            throw new PatchSkippedException("no --bdtest argument");
        }
        harmony.Patch(AccessTools.Method(typeof(SaveManager), nameof(SaveManager.InitProfileId)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(InitProfilePostfix)));
        harmony.Patch(AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetUpCombat)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(SetUpCombatPostfix)));
        // Fur Coat reads the current map point, which is null when the fight command runs before visiting the map.
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.Relics.FurCoat), nameof(MegaCrit.Sts2.Core.Models.Relics.FurCoat.BeforeCombatStart)),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(SkipFurCoatWithoutMapPoint)));
        // The fight command seeds the encounter from the clock (seconds): peers running it in different seconds desync.
        harmony.Patch(AccessTools.Method(typeof(EncounterModel), nameof(EncounterModel.DebugRandomizeRng)),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(FixedEncounterRng)));
        harmony.Patch(AccessTools.Method(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsHost)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(LobbyScreenPostfix)));
        harmony.Patch(AccessTools.Method(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsClient)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(LobbyScreenPostfix)));
        Callable.From(Start).CallDeferred();
    }

    /// <summary>Script <c>enemyhp</c>: enemies get this HP before any combat-start hook (relics) can hit them.</summary>
    private static int? _enemyHp;

    /// <summary>Co-op lobby screen (game started with <c>--fastmp host_standard</c> or <c>--fastmp join</c>).</summary>
    private static NCharacterSelectScreen? _lobbyScreen;

    private static bool SkipFurCoatWithoutMapPoint(RelicModel __instance, ref Task __result)
    {
        if (__instance.Owner.RunState.CurrentMapPoint != null)
        {
            return true;
        }
        __result = Task.CompletedTask;
        return false;
    }

    private static bool FixedEncounterRng(EncounterModel __instance)
    {
        AccessTools.Field(typeof(EncounterModel), "_rng").SetValue(__instance, new MegaCrit.Sts2.Core.Random.Rng(12345u));
        return false;
    }

    private static void LobbyScreenPostfix(NCharacterSelectScreen __instance)
    {
        _lobbyScreen = __instance;
        Say("  co-op lobby screen ready");
    }

    /// <summary>Script <c>mplobby &lt;CHARACTER&gt; [players]</c>: once the lobby has that many players, pick and ready up.</summary>
    private static async Task MpLobby(SceneTree tree, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int players = parts.Length > 1 ? int.Parse(parts[1]) : 2;
        await Until(tree, () => _lobbyScreen?.Lobby is { } lobby && lobby.Players.Count >= players, 300, $"lobby with {players} players");
        CharacterModel character = ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), parts[0].ToUpperInvariant()));
        var startLobby = _lobbyScreen!.Lobby;
        startLobby.SetLocalCharacter(character);
        await Frames(tree, 30);
        startLobby.SetReady(true);
        Say($"  ready as {character.Id.Entry} ({startLobby.Players.Count} players)");
        _selector ??= CardSelectCmd.PushSelector(new FirstCardsSelector());
        SaveManager.Instance.Progress.EnableFtues = false;
    }

    private static IDisposable? _selector;

    /// <summary>Answers every card choice (choose a card, discard/exhaust prompts, rewards) with the first options.</summary>
    private sealed class FirstCardsSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            List<CardModel> list = options.ToList();
            int count = Math.Min(list.Count, minSelect > 0 ? minSelect : Math.Min(1, maxSelect));
            Say($"  auto-selected {count} of {list.Count} card(s) (min {minSelect}, max {maxSelect})");
            return Task.FromResult<IEnumerable<CardModel>>(list.Take(count).ToList());
        }

        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options, IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives)
        {
            Say($"  auto-picked card reward 1 of {options.Count}");
            return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { card = options.Count > 0 ? options[0].Card : null };
        }
    }

    private static void SetUpCombatPostfix(CombatState state)
    {
        if (_enemyHp is not { } hp)
        {
            return;
        }
        foreach (Creature enemy in state.Enemies)
        {
            enemy.SetMaxHpInternal(hp);
            enemy.SetCurrentHpInternal(hp);
        }
        Say($"  {state.Enemies.Count} enemies set to {hp} HP");
    }

    private static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Callable.From(Start).CallDeferred();
            return;
        }
        Guard(Run(tree));
    }

    private static string? ScriptArg()
    {
        string[] args = OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();
        int i = Array.IndexOf(args, "--bdtest");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void InitProfilePostfix(SaveManager __instance)
    {
        AccessTools.Field(typeof(SaveManager), "_currentProfileId").SetValue(__instance, (int?)TestProfile);
        Say($"using save profile {TestProfile} for this session (not saved as last profile)");
    }

    private static async void Guard(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            Say("FAILED: " + e);
            Quit();
        }
    }

    private static void Say(string text) => Log.Info($"[{ModEntry.Id}] {Name} {_clock.Elapsed.TotalSeconds,7:F1}s {text}");

    private static async Task Run(SceneTree tree)
    {
        string[] lines = File.ReadAllLines(_script!);
        Say($"script {_script} ({lines.Length} lines)");
        await Until(tree, () => NGame.Instance?.MainMenu != null, 300, "menu");
        await Frames(tree, 60);
        NGame game = NGame.Instance!;
        // Test profiles start at Normal speed; players with huge decks play in Fast.
        SetSpeed(FastModeType.Fast);
        foreach (string raw in lines)
        {
            // {hash:NAME} = hash of the shared run file NAME (co-op imports need it; each machine computes its own).
            string line = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"\{hash:([\w.-]+)\}",
                m => BigDeckConsoleCmd.SharedHash(m.Groups[1].Value) ?? "missing-shared-file");
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (_clock.Elapsed.TotalSeconds > _deadline)
            {
                Say("TIMEOUT: script deadline reached");
                break;
            }
            Say("> " + line);
            string[] parts = line.Split(' ', 2);
            string arg = parts.Length > 1 ? parts[1] : "";
            try
            {
            switch (parts[0].ToLowerInvariant())
            {
                case "enemyhp":
                    _enemyHp = int.Parse(arg);
                    break;
                case "state":
                    LogState(arg);
                    break;
                case "speed":
                    SetSpeed(Enum.Parse<FastModeType>(arg, ignoreCase: true));
                    break;
                case "baselibmenu":
                    await BaseLibMenu(tree, arg.Trim());
                    break;
                case "modsscreen":
                    await ModsScreen(tree, arg.Trim());
                    break;
                case "screenshot":
                    await Screenshot(tree, arg.Trim());
                    break;
                case "marker":
                    // A line the profiling scripts look for to cut traces into phases.
                    Say("MARKER " + arg);
                    break;
                case "timeout":
                    _deadline = double.Parse(arg);
                    break;
                case "log":
                    break;
                case "wait":
                    await Seconds(tree, double.Parse(arg));
                    break;
                case "waitfor":
                    await WaitFor(tree, arg);
                    break;
                case "newrun":
                    await NewRun(game, arg);
                    break;
                case "mplobby":
                    await MpLobby(tree, arg);
                    break;
                case "cmd":
                    await Command(arg);
                    break;
                case "play":
                    await Play(tree, int.Parse(arg));
                    break;
                case "deckview":
                    string[] viewArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await DeckView(tree, viewArgs.Length > 0 ? double.Parse(viewArgs[0]) : 3, viewArgs.Length > 1 ? viewArgs[1] : null);
                    break;
                case "deckscroll":
                    await DeckScroll(tree, int.Parse(arg));
                    break;
                case "playcard":
                    string[] playArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await PlayCard(tree, playArgs[0].ToUpperInvariant(), playArgs.Length > 1 ? int.Parse(playArgs[1]) : 0);
                    break;
                case "endturn":
                    EndTurn();
                    break;
                case "quit":
                    Quit();
                    return;
                default:
                    Say("unknown script command: " + parts[0]);
                    break;
            }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Say($"FAILED line '{line}': {e.Message}");
            }
        }
        Say("script finished");
        Quit();
    }

    private static Task Frames(SceneTree tree, int count)
    {
        TaskCompletionSource done = new();
        int left = count;
        void OnFrame()
        {
            if (--left <= 0)
            {
                tree.ProcessFrame -= OnFrame;
                done.SetResult();
            }
        }
        tree.ProcessFrame += OnFrame;
        return done.Task;
    }

    private static async Task Seconds(SceneTree node, double seconds)
    {
        double end = _clock.Elapsed.TotalSeconds + seconds;
        while (_clock.Elapsed.TotalSeconds < end)
        {
            await Frames(node, 1);
        }
    }

    /// <summary>Waits until the condition holds for 10 frames in a row.</summary>
    private static async Task Until(SceneTree node, Func<bool> condition, double timeout, string what)
    {
        double start = _clock.Elapsed.TotalSeconds;
        int held = 0;
        while (held < 10)
        {
            await Frames(node, 1);
            held = condition() ? held + 1 : 0;
            if (_clock.Elapsed.TotalSeconds - start > timeout)
            {
                throw new TimeoutException($"waitfor {what} timed out after {timeout:F0} s ({StateText()})");
            }
        }
        Say($"  {what} after {_clock.Elapsed.TotalSeconds - start:F1} s");
    }

    private static Task WaitFor(SceneTree node, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double timeout = parts.Length > 1 ? double.Parse(parts[1]) : 120;
        Func<bool> condition = parts[0] switch
        {
            "menu" => () => NGame.Instance?.MainMenu != null,
            "run" => () => RunManager.Instance.IsInProgress && NRun.Instance != null,
            "combat" => () => CombatManager.Instance.IsInProgress,
            "nocombat" => () => !CombatManager.Instance.IsInProgress,
            "idle" => IsIdle,
            _ => throw new ArgumentException("waitfor " + parts[0]),
        };
        return Until(node, condition, timeout, parts[0]);
    }

    /// <summary>The local player's turn with nothing executing: the moment a human could act.</summary>
    private static bool IsIdle()
    {
        CombatManager combat = CombatManager.Instance;
        return combat.IsInProgress && Me()?.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !combat.PlayerActionsDisabled && !combat.IsEnemyTurnStarted
            && !combat.EndingPlayerTurnPhaseOne && !combat.EndingPlayerTurnPhaseTwo && !combat.IsStarting
            && !RunManager.Instance.ActionExecutor.IsRunning;
    }

    private static string StateText()
    {
        CombatManager combat = CombatManager.Instance;
        return $"run {RunManager.Instance.IsInProgress}, combat {combat.IsInProgress}, actionsDisabled {combat.PlayerActionsDisabled}, "
            + $"enemyTurn {combat.IsEnemyTurnStarted}, executing {RunManager.Instance.ActionExecutor?.IsRunning}";
    }

    /// <summary>Script <c>state [label]</c>: creatures' HP, block and powers, plus the local piles, for A/B comparisons.</summary>
    private static void LogState(string label)
    {
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        Player? me = Me();
        if (combat == null || me?.PlayerCombatState == null)
        {
            Say($"  STATE {label}: no combat");
            return;
        }
        static string Describe(Creature c) =>
            $"{c.Name} hp {c.CurrentHp} block {c.Block} powers [{string.Join(", ", c.Powers.Select(p => $"{p.Id.Entry} {p.Amount}"))}]";
        Say($"  STATE {label}: turn {me.PlayerCombatState.TurnNumber}, energy {me.PlayerCombatState.Energy}, "
            + $"hand {PileType.Hand.GetPile(me).Cards.Count}, draw {PileType.Draw.GetPile(me).Cards.Count}, "
            + $"discard {PileType.Discard.GetPile(me).Cards.Count}, exhaust {PileType.Exhaust.GetPile(me).Cards.Count}");
        Say($"  STATE {label}: player {Describe(me.Creature)}");
        foreach (Creature enemy in combat.Enemies)
        {
            Say($"  STATE {label}: enemy {Describe(enemy)}");
        }
    }

    /// <summary>Script <c>screenshot &lt;name&gt;</c>: saves the game's frame as results/&lt;name&gt;.png (next to the scenarios folder).</summary>
    private static async Task Screenshot(SceneTree tree, string name)
    {
        await Frames(tree, 2);
        Image image = tree.Root.GetTexture().GetImage();
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_script!)!, "..", "results"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".png");
        image.SavePng(path);
        Say($"  screenshot {image.GetWidth()}x{image.GetHeight()}: {path}");
    }

    private static void SetSpeed(FastModeType speed)
    {
        SaveManager.Instance.PrefsSave.FastMode = speed;
        Say($"  animation speed {speed}");
    }

    private static Player? Me() => RunManager.Instance.DebugOnlyGetState() is { } state ? LocalContext.GetMe(state) : null;

    private static async Task NewRun(NGame game, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CharacterModel character = ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), parts[0].ToUpperInvariant()));
        string seed = parts.Length > 1 ? parts[1] : "BDTEST";
        _selector ??= CardSelectCmd.PushSelector(new FirstCardsSelector());
        // Fresh test profile: no tutorials (the combat one throws when combat is entered with the fight command).
        SaveManager.Instance.Progress.EnableFtues = false;
        await game.StartNewSingleplayerRun(character, shouldSave: true, ActModel.GetDefaultList().ToList(),
            Array.Empty<ModifierModel>(), seed, GameMode.Standard);
        Say($"  run started: {character.Id.Entry}, seed {seed}");
    }

    private static async Task Command(string line)
    {
        _console ??= new DevConsole(shouldAllowDebugCommands: true);
        // In co-op, networked commands must go through the action queue so every peer runs them.
        CmdResult result = RunManager.Instance.IsInProgress && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer
            ? _console.ProcessCommand(line)
            : _console.ProcessNetCommand(Me(), line);
        if (result.task != null)
        {
            await result.task;
        }
        Say($"  {(result.success ? "ok" : "FAILED")}: {result.msg}".Replace("\n", "\n    "));
    }

    /// <summary>Plays up to n cards from hand, like clicks: each one waits for the game to be idle again.</summary>
    private static async Task Play(SceneTree node, int count)
    {
        int played = 0;
        for (int i = 0; i < count; i++)
        {
            await Until(node, IsIdle, 120, "idle");
            Player? me = Me();
            CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
            if (me?.PlayerCombatState == null || combat == null)
            {
                break;
            }
            Creature? enemy = combat.Enemies.FirstOrDefault(e => e.IsAlive);
            CardModel? card = PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => c.CanPlayTargeting(enemy) || c.CanPlayTargeting(null));
            if (card == null)
            {
                Say("  no playable card left");
                break;
            }
            Creature? target = card.CanPlayTargeting(enemy) ? enemy : null;
            Say($"  play {card.Id.Entry} -> {target?.ToString() ?? "no target"}");
            if (!card.TryManualPlay(target))
            {
                break;
            }
            played++;
            await Frames(node, 5);
        }
        Say($"  played {played}");
    }

    /// <summary>Script <c>playcard &lt;CARD_ID&gt; [living enemy index]</c>: moves that card from the draw pile to the hand if needed, plays it, waits.</summary>
    private static async Task PlayCard(SceneTree tree, string id, int enemyIndex)
    {
        await Until(tree, IsIdle, 120, "idle");
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        CombatState combat = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        CardModel? card = PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => c.Id.Entry == id);
        if (card == null)
        {
            Say($"  {id} not in hand: use 'cmd card {id}' first");
            return;
        }
        Creature? enemy = combat.Enemies.Where(e => e.IsAlive).Skip(enemyIndex).FirstOrDefault();
        Creature? target = card.CanPlayTargeting(enemy) ? enemy : null;
        Say($"  playing {id} -> {target?.ToString() ?? "no target"}");
        long start = Stopwatch.GetTimestamp();
        if (!card.TryManualPlay(target))
        {
            Say($"  {id} cannot be played now");
            return;
        }
        await Frames(tree, 2);
        // Until the player can act again, or the combat is over (the card killed the last enemy).
        await Until(tree, () => IsIdle() || !CombatManager.Instance.IsInProgress, 600, "idle or combat end");
        Say($"  played {id} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms (until idle or combat end)");
    }

    private static MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen? _deckView;

    /// <summary>Script <c>deckview [s] [screenshot name]</c>: opens the deck view (timed), keeps it open, closes it (timed).</summary>
    private static async Task DeckView(SceneTree tree, double seconds, string? screenshot)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        long start = Stopwatch.GetTimestamp();
        _deckView = MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen.ShowScreen(me);
        double openMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        await Frames(tree, 1);
        Say($"  deck view opened: {openMs:F0} ms call, {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms to first frame ({me.Deck.Cards.Count} cards)");
        await Seconds(tree, seconds);
        if (screenshot != null)
        {
            await Screenshot(tree, screenshot);
        }
        start = Stopwatch.GetTimestamp();
        MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer.Instance?.Close();
        await Frames(tree, 1);
        Say($"  deck view closed in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");
        _deckView = null;
    }

    /// <summary>Script <c>deckscroll &lt;steps&gt;</c>: opens the deck view and scrolls its grid one step per frame.</summary>
    private static async Task DeckScroll(SceneTree tree, int steps)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        var screen = MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen.ShowScreen(me) ?? throw new InvalidOperationException("no deck view");
        await Frames(tree, 30);
        NCardGrid grid = FindChild<NCardGrid>(screen) ?? throw new InvalidOperationException("no card grid in the deck view");
        long start = Stopwatch.GetTimestamp();
        double worst = 0;
        for (int i = 0; i < steps; i++)
        {
            long frame = Stopwatch.GetTimestamp();
            grid.SetScrollPosition(-i * 120f);
            await Frames(tree, 1);
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(frame).TotalMilliseconds);
        }
        double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Say($"  scrolled {steps} steps in {total:F0} ms: {total / steps:F1} ms/frame, worst {worst:F0} ms");
        MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer.Instance?.Close();
        await Frames(tree, 5);
    }

    /// <summary>Script <c>modsscreen &lt;screenshot name&gt;</c>: opens the main menu's mods screen on this mod and captures it.</summary>
    private static async Task ModsScreen(SceneTree tree, string shot)
    {
        var menu = NGame.Instance?.MainMenu ?? throw new InvalidOperationException("not on the main menu");
        var screen = menu.SubmenuStack.PushSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModdingScreen>();
        await Frames(tree, 30);
        var row = FindAll<MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModMenuRow>(screen).FirstOrDefault(r => r.Mod?.manifest?.id == ModEntry.Id)
            ?? throw new InvalidOperationException("this mod is not in the list");
        screen.OnRowSelected(row);
        await Frames(tree, 30);
        await Screenshot(tree, shot);
    }

    /// <summary>Script <c>baselibmenu &lt;screenshot name&gt;</c>: BaseLib's mod configuration menu on this mod's settings.</summary>
    private static async Task BaseLibMenu(SceneTree tree, string shot)
    {
        var menu = NGame.Instance?.MainMenu ?? throw new InvalidOperationException("not on the main menu");
        var submenu = menu.SubmenuStack.PushSubmenuType<BaseLib.Config.UI.NModConfigSubmenu>();
        await Frames(tree, 30);
        var config = BaseLib.Config.ModConfigRegistry.Get(ModEntry.Id) ?? throw new InvalidOperationException("settings not registered in BaseLib");
        AccessTools.Method(submenu.GetType(), "LoadModConfig").Invoke(submenu, new object[] { config });
        await Frames(tree, 40);
        await Screenshot(tree, shot);
    }

    private static IEnumerable<T> FindAll<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T found)
            {
                yield return found;
            }
            foreach (T deeper in FindAll<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static T? FindChild<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T found)
            {
                return found;
            }
            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    private static void EndTurn()
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
    }

    private static void Quit()
    {
        Say("quitting");
        Callable.From(() => NGame.Instance?.Quit()).CallDeferred();
    }
}
