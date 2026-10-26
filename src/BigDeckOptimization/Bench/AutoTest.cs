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
        harmony.Patch(AccessTools.Method(typeof(Creature), nameof(Creature.LoseHpInternal)),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(ImmortalPrefix)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(ImmortalPostfix)));
        // Timings must not depend on which window has focus: no 30 fps cap while the game is in the background.
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.NBackgroundModeHandler), "EnterBackgroundMode"),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(StayForeground)));
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
        string[] launchArgs = OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();
        int show = Array.IndexOf(launchArgs, "--bdshow");
        if (show >= 0 && show + 1 < launchArgs.Length && int.TryParse(launchArgs[show + 1], out int showScreen))
        {
            // --bdshow <screen> (record.ps1): fullscreen on that screen, for a screen recording. Only the window moves.
            _showScreen = showScreen;
            harmony.Patch(AccessTools.Method(typeof(NGame), nameof(NGame.ApplyDisplaySettings)),
                postfix: new HarmonyMethod(typeof(AutoTest), nameof(ShowOnScreen)));
            // The recording keeps the game's sound while another window has the focus ("mute in background" setting).
            harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.NMuteInBackgroundHandler), "Mute"),
                prefix: new HarmonyMethod(typeof(AutoTest), nameof(StayForeground)));
            Callable.From(ShowOnScreen).CallDeferred();
        }
        else if (launchArgs.Contains("--bdhidden"))
        {
            _hidden = true;
            harmony.Patch(AccessTools.Method(typeof(NGame), nameof(NGame.ApplyDisplaySettings)),
                postfix: new HarmonyMethod(typeof(AutoTest), nameof(HideWindow)));
            Callable.From(HideWindow).CallDeferred();
        }
        if (OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).Contains("--bdmute"))
        {
            // --bdmute (run.ps1 without -Sound): every master volume the game sets (startup, mute in background) is 0.
            // The volume settings themselves are untouched.
            var mute = new HarmonyMethod(typeof(AutoTest), nameof(MuteVolume));
            harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager), "SetMasterVol"), prefix: mute);
            harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager), "SetMasterAudioVolume"), prefix: mute);
            Callable.From(() =>
            {
                NGame.Instance?.AudioManager?.SetMasterVol(0f);
                NGame.Instance?.DebugAudio?.SetMasterAudioVolume(0f);
            }).CallDeferred();
        }
        Callable.From(Start).CallDeferred();
    }

    private static void MuteVolume(ref float __0) => __0 = 0f;

    /// <summary>
    /// Script <c>deckfilter [ID,ID...] [stacked]</c> (on the map): removes from the deck the cards with these ids and,
    /// with <c>stacked</c>, those holding several stacked enchantments (Limitless' MultiEnchantment), e.g. to keep long
    /// multi-hit animations out of a video.
    /// </summary>
    private static void DeckFilter(string[] args)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        var ids = new HashSet<string>(args.Where(a => a != "stacked").SelectMany(a => a.Split(',')).Select(a => a.ToUpperInvariant()));
        bool stacked = args.Contains("stacked");
        Type? multi = AccessTools.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment");
        MethodInfo? inner = multi == null ? null : AccessTools.Method(multi, "InnerEnchantments");
        bool Stacked(CardModel card) => stacked && card.Enchantment != null && multi != null && multi.IsInstanceOfType(card.Enchantment)
            && inner?.Invoke(null, new object?[] { card.Enchantment }) is System.Collections.ICollection { Count: > 1 };
        List<CardModel> removed = me.Deck.Cards.Where(c => ids.Contains(c.Id.Entry) || Stacked(c)).ToList();
        foreach (CardModel card in removed)
        {
            me.Deck.RemoveInternal(card, silent: true);
            (me.RunState as ICardScope)?.RemoveCard(card);
        }
        Say($"  deckfilter: removed {removed.Count} cards ({removed.Count(c => ids.Contains(c.Id.Entry))} by id), deck {me.Deck.Cards.Count} cards");
    }

    private static void NoOpPostfix()
    {
    }

    private static bool _hidden, _quitting;
    private static int _showScreen = -1;

    /// <summary>
    /// <c>--bdshow &lt;screen&gt;</c>: the game fullscreen and on top on that screen (a screen recording of it). Only the
    /// window moves; a fullscreen game saves no window position on exit.
    /// </summary>
    private static void ShowOnScreen()
    {
        if (_quitting || _showScreen < 0 || _showScreen >= DisplayServer.GetScreenCount())
        {
            return;
        }
        try
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetCurrentScreen(_showScreen);
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            // Stays above every window of that screen while recorded (handing the focus back raised the previous
            // window over it).
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.AlwaysOnTop, true);
            Say($"window fullscreen and on top on screen {_showScreen} at {DisplayServer.WindowGetPosition()}");
        }
        catch (Exception e)
        {
            Say("could not move the window: " + e.Message);
        }
    }

    /// <summary>
    /// <c>--bdhidden</c> (run.ps1 without -Visible): the window, windowed at 1920x1080 (the fullscreen size here, for
    /// comparable timings), sits beyond every screen and hands the focus back to the window that had it. It still
    /// renders (not minimized): screenshots and fps are unchanged. Only the window moves, no setting changes; the game
    /// saves the window position on exit only with fullscreen off, and <see cref="Quit"/> reapplies the display
    /// settings first.
    /// </summary>
    private static void HideWindow()
    {
        if (_quitting)
        {
            return;
        }
        try
        {
            if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            }
            DisplayServer.WindowSetSize(new Vector2I(1920, 1080));
            int left = 0, top = 0;
            for (int screen = 0; screen < DisplayServer.GetScreenCount(); screen++)
            {
                Vector2I position = DisplayServer.ScreenGetPosition(screen);
                left = Math.Min(left, position.X);
                top = Math.Min(top, position.Y);
            }
            DisplayServer.WindowSetPosition(new Vector2I(left - 1920 - 400, top));
            Say($"window hidden at {DisplayServer.WindowGetPosition()} ({DisplayServer.WindowGetSize()}), focus handed back: {ReturnFocus()}");
        }
        catch (Exception e)
        {
            Say("could not hide the window: " + e.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>While the game window has the focus (it takes it at launch), gives it to the next visible window below it.</summary>
    private static bool ReturnFocus()
    {
        if (OS.GetName() != "Windows")
        {
            return false;
        }
        IntPtr game = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        if (game == IntPtr.Zero || GetForegroundWindow() != game)
        {
            return false;
        }
        const uint NextWindow = 2; // GW_HWNDNEXT: the next window down the z-order.
        for (IntPtr window = GetWindow(game, NextWindow); window != IntPtr.Zero; window = GetWindow(window, NextWindow))
        {
            GetWindowThreadProcessId(window, out uint process);
            if (IsWindowVisible(window) && !IsIconic(window) && GetWindowTextLength(window) > 0 && process != (uint)System.Environment.ProcessId)
            {
                return SetForegroundWindow(window);
            }
        }
        return false;
    }

    private static bool _loggingPlays;

    /// <summary>
    /// Logs every card play, manual or auto-played by an effect (Havoc, Cascade...), from the combat history: a patch
    /// on the play methods misses the calls the JIT inlined.
    /// </summary>
    private static void LogCardPlays()
    {
        if (_loggingPlays)
        {
            return;
        }
        _loggingPlays = true;
        MegaCrit.Sts2.Core.Combat.History.CombatHistory history = CombatManager.Instance.History;
        history.Changed += () =>
        {
            if (history.Entries.LastOrDefault() is MegaCrit.Sts2.Core.Combat.History.Entries.CardPlayStartedEntry { CardPlay: var play })
            {
                Say($"  CARDPLAY {(play.IsAutoPlay ? "auto" : "manual")} {play.Card.Id.Entry} -> {play.Target?.ToString() ?? "no target"}");
            }
        };
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
        AccessTools.Field(typeof(EncounterModel), "_rng").SetValue(__instance, SeededRng(12345u));
        return false;
    }

    // Game v0.111 (beta) changed Rng's seed constructor (uint, int) to (ulong) and StartRunLobby.Players' element type:
    // reached through reflection so this test tooling runs on both versions.
    private static MegaCrit.Sts2.Core.Random.Rng SeededRng(uint seed)
    {
        ConstructorInfo ctor = typeof(MegaCrit.Sts2.Core.Random.Rng).GetConstructors()
            .First(c => c.GetParameters() is { Length: > 0 } p && (p[0].ParameterType == typeof(uint) || p[0].ParameterType == typeof(ulong))
                && p.Skip(1).All(q => q.HasDefaultValue));
        object?[] args = ctor.GetParameters().Select((p, i) => i == 0 ? Convert.ChangeType(seed, p.ParameterType) : p.DefaultValue).ToArray();
        return (MegaCrit.Sts2.Core.Random.Rng)ctor.Invoke(args);
    }

    private static int LobbyPlayerCount(object lobby) =>
        ((System.Collections.ICollection)AccessTools.Property(lobby.GetType(), "Players").GetValue(lobby)!).Count;

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
        await Until(tree, () => _lobbyScreen?.Lobby is { } lobby && LobbyPlayerCount(lobby) >= players, 300, $"lobby with {players} players");
        CharacterModel character = ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), parts[0].ToUpperInvariant()));
        var startLobby = _lobbyScreen!.Lobby;
        startLobby.SetLocalCharacter(character);
        await Frames(tree, 30);
        startLobby.SetReady(true);
        Say($"  ready as {character.Id.Entry} ({LobbyPlayerCount(startLobby)} players)");
        _selector ??= PushSelector(new FirstCardsSelector());
        SaveManager.Instance.Progress.EnableFtues = false;
    }

    private static IDisposable? _selector;

    /// <summary>
    /// CardSelectCmd.PushSelector through reflection: game v0.111 added an optional parameter (localOnly), which changes
    /// the method's signature; called with its default values on any version.
    /// </summary>
    private static IDisposable PushSelector(MegaCrit.Sts2.Core.TestSupport.ICardSelector selector)
    {
        MethodInfo push = typeof(CardSelectCmd).GetMethods().Single(m => m.Name == nameof(CardSelectCmd.PushSelector));
        object?[] args = push.GetParameters().Select((p, i) => i == 0 ? selector : p.DefaultValue).ToArray();
        return (IDisposable)push.Invoke(null, args)!;
    }

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

    private static bool StayForeground() => false;

    private static bool _immortal;
    /// <summary>With <c>immortal until &lt;round&gt;</c>: immortal before that combat round only (0 = always).</summary>
    private static int _immortalUntilRound;

    /// <summary>
    /// Script <c>immortal [off | until &lt;round&gt;]</c>: enemies never drop below 1 HP from a hit (long benches, huge
    /// decks). "until" ties the end to the game state (the combat round), so co-op peers running the same script stay
    /// identical, and the combat ends by itself once the enemies can die again.
    /// </summary>
    private static bool Immortal(Creature creature) =>
        _immortal && creature.IsEnemy && (_immortalUntilRound == 0 || (creature.CombatState?.RoundNumber ?? 0) < _immortalUntilRound);

    private static void ImmortalPrefix(Creature __instance, ref decimal amount)
    {
        if (Immortal(__instance) && __instance.CurrentHp > 1)
        {
            amount = Math.Min(amount, __instance.CurrentHp - 1);
        }
    }

    /// <summary>...and are healed back to full below half, so the next hits land the same way.</summary>
    private static void ImmortalPostfix(Creature __instance)
    {
        if (Immortal(__instance) && __instance.CurrentHp < __instance.MaxHp / 2)
        {
            __instance.SetCurrentHpInternal(__instance.MaxHp);
        }
    }

    private static void SetUpCombatPostfix(CombatState state)
    {
        LogCardPlays();
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
        tree.ProcessFrame += RecordFrame;
        Say($"  display: max fps {Engine.MaxFps} (0 = uncapped), vsync {DisplayServer.WindowGetVsyncMode()}");
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
                case "immortal":
                    string[] immortalArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _immortal = immortalArgs.FirstOrDefault() != "off";
                    _immortalUntilRound = immortalArgs.Length > 1 && immortalArgs[0] == "until" ? int.Parse(immortalArgs[1]) : 0;
                    Say($"  enemies immortal: {_immortal}{(_immortalUntilRound > 0 ? $" until round {_immortalUntilRound}" : "")}");
                    break;
                case "state":
                    LogState(arg);
                    break;
                case "checksum":
                    LogChecksum(arg.Trim());
                    break;
                case "speed":
                    SetSpeed(Enum.Parse<FastModeType>(arg, ignoreCase: true));
                    break;
                case "baselibmenu":
                    string[] menuArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await BaseLibMenu(tree, menuArgs[0], menuArgs.Length > 1 ? menuArgs[1] : null);
                    break;
                case "language":
                    await Language(tree, arg.Trim());
                    break;
                case "fakeconflict":
                    // Another "mod" patching a game method (no-op postfix under another Harmony id): the optimization
                    // using it must back off and report it.
                    MethodInfo conflictTarget = AccessTools.Method(arg.Trim()) ?? throw new InvalidOperationException("no method " + arg.Trim());
                    new Harmony("bdtest.fakeconflict").Patch(conflictTarget, postfix: new HarmonyMethod(typeof(AutoTest), nameof(NoOpPostfix)));
                    Say($"  patched {conflictTarget.DeclaringType?.Name}.{conflictTarget.Name} as bdtest.fakeconflict");
                    break;
                case "faultprobe":
                    Say("  FAULTPROBE " + await FaultProbe.Run(() => Frames(tree, 3)));
                    break;
                case "menutext":
                    string menuKey = $"BIGDECKOPTIMIZATION-{arg.Trim().ToUpperInvariant()}.hover.desc";
                    Say($"  MENUTEXT {arg.Trim()}: {MegaCrit.Sts2.Core.Localization.LocString.GetIfExists("settings_ui", menuKey)?.GetFormattedText() ?? "(missing)"}");
                    break;
                case "menuback":
                    NGame.Instance?.MainMenu?.SubmenuStack.Pop();
                    await Frames(tree, 20);
                    break;
                case "modsscreen":
                    await ModsScreen(tree, arg.Trim());
                    break;
                case "screenshot":
                    await Screenshot(tree, arg.Trim());
                    break;
                case "gcstat":
                    GcStat(arg.Trim());
                    break;
                case "setenergy":
                    // Exact energy (the game's 'energy' command adds): X-cost cards spend all of it.
                    (Me()?.PlayerCombatState ?? throw new InvalidOperationException("not in combat")).Energy = int.Parse(arg);
                    break;
                case "emptyhand":
                    await EmptyHand(tree);
                    break;
                case "deckfilter":
                    DeckFilter(arg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "gcfull":
                    // Live memory after a full blocking collection: comparable between sessions, shows leaks.
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    Say($"  GCFULL {arg.Trim()}: live heap {GC.GetTotalMemory(false) / (1024.0 * 1024):F1} MB");
                    break;
                case "fpsstat":
                    FpsStat(arg.Trim());
                    break;
                case "hover":
                    Hover(int.Parse(arg));
                    break;
                case "checktips":
                    CheckTips(arg.Trim());
                    break;
                case "menuvalues":
                    // What BaseLib's menu shows (each box reads its property), by reflection: this DLL must not reference BaseLib.
                    Type? menu = AccessTools.TypeByName("BigDeckOptimization.Config.BaseLibSettings");
                    Say(menu == null ? "  MENU: not loaded" : "  MENU " + arg.Trim() + ": " + string.Join(", ", menu.GetProperties(BindingFlags.Public | BindingFlags.Static)
                        .Where(p => p.PropertyType == typeof(bool)).Select(p => $"{p.Name}={p.GetValue(null)}")));
                    break;
                case "counters":
                    Say($"  COUNTERS {arg.Trim()}: {LimitlessReserialize.Name} kept {LimitlessReserialize.Kept}, rebuilt {LimitlessReserialize.Rebuilt}, skipped getters {LimitlessReserialize.SkippedGetters}, known data {LimitlessReserialize.KnownData}, "
                        + $"verify mismatches {LimitlessReserialize.VerifyMismatches}; {FastListeners.Name} cache mismatches {FastListeners.CacheMismatches}, "
                        + $"segments rebuilt {FastListeners.SegmentRebuilds} (versions {FastListeners.RebuiltVersions}, pile version {FastListeners.PileVersion}), "
                        + $"extended {FastListeners.SegmentExtends}, snapshot catch-ups {FastListeners.SnapshotCatchUps}, hits {FastListeners.SegmentHits}, uncached walks {FastListeners.UncachedWalks}; "
                        + $"{HookFilter.Name} walk checks {HookFilter.WalkChecks}, mismatches {HookFilter.WalkMismatches}, cost color checks {HookFilter.CostColorChecks / 64}, mismatches {HookFilter.CostColorMismatches}; {RelicFlashMerge.Name} merged {RelicFlashMerge.Merged}; {PileIndex.Name} duplicate checks fast {PileIndex.FastDuplicateChecks}, slow {PileIndex.SlowDuplicateChecks}; subscriber checks {FastListeners.SubscriberChecks} in {FastListeners.SubscriberCheckTicks * 1000.0 / Stopwatch.Frequency:F1} ms");
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
                case "pilescroll":
                    string[] pileArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await PileScroll(tree, pileArgs[0], int.Parse(pileArgs[1]), pileArgs.Length > 2 ? float.Parse(pileArgs[2]) : 40f);
                    break;
                case "deckpages":
                    string[] pageArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await DeckPages(tree, pageArgs[0], int.Parse(pageArgs[1]));
                    break;
                case "playcard":
                    List<string> playArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool free = playArgs.Remove("free");
                    await PlayCard(tree, playArgs[0].ToUpperInvariant(), playArgs.Count > 1 ? int.Parse(playArgs[1]) : 0, free);
                    break;
                case "endturn":
                    EndTurn();
                    break;
                case "watch":
                    Watch(tree, double.Parse(arg.Trim()));
                    break;
                case "nodes":
                    LogNodes(tree, arg.Trim());
                    break;
                case "eventoption":
                    string[] eventArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await EventOption(tree, eventArgs[0], eventArgs.Length > 1 ? double.Parse(eventArgs[1]) : 600);
                    break;
                case "restsite":
                    await RestSite(arg.Trim());
                    break;
                case "quit":
                    Quit();
                    return;
                case "handoff":
                    // The player takes over: stop answering card choices, keep the game open.
                    _selector?.Dispose();
                    _selector = null;
                    Say("handed over to the player");
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
            "turn" => () => !CombatManager.Instance.IsInProgress || IsIdle(), // ready to play, or the combat is over
            "deckcount" => () => DeckCountShown(node),
            "restsite" => () => MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.Instance != null
                && RunManager.Instance.RestSiteSynchronizer.GetLocalOptions() is { Count: > 0 },
            "event" => () =>
            {
                try
                {
                    return RunManager.Instance.EventSynchronizer.GetLocalEvent() is { IsFinished: false };
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false; // No event state for this player yet.
                }
            },
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

    private static readonly List<double> _frameMs = new();
    private static long _lastFrame;

    private static void RecordFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0)
        {
            _frameMs.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
        }
        _lastFrame = now;
    }

    /// <summary>
    /// Script <c>fpsstat [label]</c>: frame rate since the previous fpsstat: average fps, "1% low" (fps of the 99th
    /// percentile frame time), worst frame and how many frames went over 33 / 50 / 100 ms.
    /// </summary>
    private static void FpsStat(string label)
    {
        if (label.Length > 0 && _frameMs.Count > 0)
        {
            double[] sorted = _frameMs.OrderBy(ms => ms).ToArray();
            double total = sorted.Sum();
            double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
            Say($"  FPSSTAT {label}: {sorted.Length} frames in {total / 1000:F1} s, avg {sorted.Length * 1000 / total:F0} fps, " +
                $"1% low {1000 / p99:F0} fps, worst {sorted[^1]:F0} ms, over 33 ms {sorted.Count(ms => ms > 33)}, " +
                $"over 50 ms {sorted.Count(ms => ms > 50)}, over 100 ms {sorted.Count(ms => ms > 100)}");
        }
        _frameMs.Clear();
    }

    /// <summary>Script <c>emptyhand</c>: moves the hand to the discard pile (room for the next test card).</summary>
    private static async Task EmptyHand(SceneTree tree)
    {
        await Until(tree, IsIdle, 120, "idle");
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        List<CardModel> hand = PileType.Hand.GetPile(me).Cards.ToList();
        await CardPileCmd.Add(hand, PileType.Discard);
        await Frames(tree, 2);
    }

    private static MegaCrit.Sts2.Core.Nodes.Combat.NCreature? _hovered;

    /// <summary>Script <c>hover &lt;enemy index&gt;</c>: as if the mouse rested on that enemy (its focus handler).</summary>
    private static void Hover(int enemyIndex)
    {
        CombatState state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("not in combat");
        Creature enemy = state.Enemies[enemyIndex];
        if (_hovered != null && GodotObject.IsInstanceValid(_hovered))
        {
            // Mouse leaves the previous one first, as it would (focus subscribes to state changes).
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature), "OnUnfocus").Invoke(_hovered, null);
        }
        _hovered = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.GetCreatureNode(enemy) ?? throw new InvalidOperationException("no node for " + enemy);
        AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature), "OnFocus").Invoke(_hovered, null);
        Say($"  hovering {enemy}");
    }

    /// <summary>
    /// Script <c>checktips [label]</c>: the hovered creature's tip panel on screen must show exactly its current tips
    /// (titles and descriptions, in order); logs TIPS OK / TIPS MISMATCH and HoverTipReuse's counters.
    /// </summary>
    private static void CheckTips(string label)
    {
        if (_hovered == null)
        {
            throw new InvalidOperationException("hover a creature first");
        }
        var active = (Dictionary<Control, MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet>)AccessTools.Field(
            typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet), "_activeHoverTips").GetValue(null)!;
        List<string> expected = MegaCrit.Sts2.Core.HoverTips.IHoverTip.RemoveDupes(_hovered.Entity.HoverTips)
            .OfType<MegaCrit.Sts2.Core.HoverTips.HoverTip>().Select(t => $"{t.Title}: {t.Description}").ToList();
        List<string> shown = new();
        if (!active.TryGetValue(_hovered.Hitbox, out var panel))
        {
            // The game hides them itself (targeting a card, card play in progress): nothing to compare.
            Say($"  TIPS hidden {label}: reused {HoverTipReuse.Reused}, rebuilt {HoverTipReuse.Rebuilt}");
            return;
        }
        {
            var container = (Control)AccessTools.Field(panel.GetType(), "_textHoverTipContainer").GetValue(panel)!;
            foreach (Node tip in container.GetChildren())
            {
                var title = tip.GetNodeOrNull<Control>("%Title");
                string titleText = title is { Visible: true } ? (string)title.Get("text") : "";
                shown.Add($"{(titleText.Length > 0 ? titleText : null)}: {tip.GetNode<RichTextLabel>("%Description").Text}");
            }
        }
        bool same = shown.SequenceEqual(expected);
        Say($"  TIPS {(same ? "OK" : "MISMATCH")} {label}: {shown.Count} shown / {expected.Count} expected, " +
            $"reused {HoverTipReuse.Reused}, rebuilt {HoverTipReuse.Rebuilt}" +
            (same ? "" : $" | shown: {string.Join(" / ", shown)} | expected: {string.Join(" / ", expected)}"));
    }

    /// <summary>
    /// Script <c>checksum [label] [vanilla]</c>: the full combat state checksum (what co-op peers compare after every
    /// action). Two sessions running the same script must log the same values. By default through the game's path with
    /// this mod's snapshot optimizations (verified identical, ~40 ms); "vanilla" disables them (tens of seconds with huge
    /// decks).
    /// </summary>
    private static void LogChecksum(string arg)
    {
        bool vanilla = arg.EndsWith(" vanilla", StringComparison.Ordinal);
        string label = vanilla ? arg[..^" vanilla".Length] : arg;
        RunState state = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        Func<uint> compute = () => RunManager.Instance.ChecksumTracker.GenerateChecksum(
            MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState.FromRun(state, null));
        long start = Stopwatch.GetTimestamp();
        uint checksum = vanilla ? ListenerCache.RunVanilla(compute) : compute();
        Say($"  CHECKSUM {label}: {checksum:X8} ({Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
    }

    private static double _gcWall;
    private static TimeSpan _gcPause;
    private static readonly int[] _gcCounts = new int[3];

    /// <summary>Script <c>gcstat [label]</c>: wall time, GC pause time and collections since the previous gcstat.</summary>
    private static void GcStat(string label)
    {
        double wall = _clock.Elapsed.TotalMilliseconds;
        TimeSpan pause = GC.GetTotalPauseDuration();
        int[] counts = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        if (label.Length > 0)
        {
            Say($"  GCSTAT {label}: {wall - _gcWall:F0} ms, GC pauses {(pause - _gcPause).TotalMilliseconds:F0} ms, " +
                $"collections gen0 {counts[0] - _gcCounts[0]} gen1 {counts[1] - _gcCounts[1]} gen2 {counts[2] - _gcCounts[2]}, " +
                $"heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        }
        _gcWall = wall;
        _gcPause = pause;
        counts.CopyTo(_gcCounts, 0);
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
        _selector ??= PushSelector(new FirstCardsSelector());
        // Fresh test profile: no tutorials (the combat one throws when combat is entered with the fight command).
        SaveManager.Instance.Progress.EnableFtues = false;
        // The character select screen stops the menu music before starting a run; without it, it keeps playing under
        // the act music (heard in recordings).
        MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager.Instance?.StopMusic();
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

    /// <summary>Script <c>nodes [label]</c>: scene nodes in all, card previews, cards flying to a pile and their trails.</summary>
    private static void LogNodes(SceneTree tree, string label)
    {
        var ui = NRun.Instance?.GlobalUi;
        int previews = ui == null ? 0 : ui.CardPreviewContainer.GetChildCount() + ui.MessyCardPreviewContainer.GetChildCount()
            + ui.EventCardPreviewContainer.GetChildCount() + ui.GridCardPreviewContainer.GetChildCount();
        int trail = ui?.TopBar.TrailContainer.GetChildCount() ?? 0;
        int cards = FindAll<NCard>(tree.Root).Count();
        int flying = FindAll<MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx>(tree.Root).Count();
        Say($"  NODES {label}: {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0} nodes, {cards} card nodes, "
            + $"{previews} previews, {flying} flying, trail container {trail}, deck {Me()?.Deck.Cards.Count}");
    }

    /// <summary>
    /// Script <c>watch &lt;seconds&gt;</c>: from now on, in the background, 4 lines a second with the card previews on
    /// screen (popping up or flying to a pile) and the deck size, for that long.
    /// </summary>
    private static void Watch(SceneTree tree, double seconds)
    {
        double end = _clock.Elapsed.TotalSeconds + seconds;
        double next = 0;
        void Sample()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now > end)
            {
                tree.ProcessFrame -= Sample;
                return;
            }
            if (now < next)
            {
                return;
            }
            next = now + 0.25;
            var ui = NRun.Instance?.GlobalUi;
            int previews = ui == null ? 0 : ui.CardPreviewContainer.GetChildCount() + ui.MessyCardPreviewContainer.GetChildCount();
            int trail = ui?.TopBar.TrailContainer.GetChildCount() ?? 0;
            Say($"  WATCH: {previews} previews, {trail / 2} flying, deck {Me()?.Deck.Cards.Count}");
        }
        tree.ProcessFrame += Sample;
    }

    /// <summary>
    /// Script <c>eventoption &lt;text key part&gt; [timeout s]</c>: picks the current event's option whose text key
    /// contains it (e.g. SHATTER), like a click, and waits for the event to finish, timed.
    /// </summary>
    private static async Task EventOption(SceneTree tree, string key, double timeout)
    {
        var sync = RunManager.Instance.EventSynchronizer;
        MegaCrit.Sts2.Core.Models.EventModel? model = sync.GetLocalEvent();
        if (model == null)
        {
            Say("  EVENTOPTION: no event");
            return;
        }
        List<MegaCrit.Sts2.Core.Events.EventOption> options = model.CurrentOptions.ToList();
        int index = options.FindIndex(o => o.TextKey.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            Say($"  EVENTOPTION: no {key} option ({string.Join(", ", options.Select(o => o.TextKey))})");
            return;
        }
        int before = Me()!.Deck.Cards.Count;
        double start = _clock.Elapsed.TotalSeconds;
        sync.ChooseLocalOption(index);
        await Until(tree, () => sync.GetLocalEvent()?.IsFinished ?? true, timeout, "event finished");
        Say($"  EVENTOPTION {options[index].TextKey}: {(_clock.Elapsed.TotalSeconds - start) * 1000:F0} ms, deck {before} -> {Me()!.Deck.Cards.Count}");
    }

    /// <summary>Script <c>restsite &lt;option class&gt;</c> (e.g. CloneRestSiteOption): picks it like a click, timed.</summary>
    private static async Task RestSite(string name)
    {
        var sync = RunManager.Instance.RestSiteSynchronizer;
        List<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options = sync.GetLocalOptions().ToList();
        int index = options.FindIndex(o => o.GetType().Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            Say($"  RESTSITE: no {name} option ({string.Join(", ", options.Select(o => o.GetType().Name))})");
            return;
        }
        int before = Me()!.Deck.Cards.Count;
        long start = Stopwatch.GetTimestamp();
        bool ok = await sync.ChooseLocalOption(index);
        Say($"  RESTSITE {name}: {(ok ? "ok" : "FAILED")} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms, deck {before} -> {Me()!.Deck.Cards.Count}");
    }

    /// <summary>Plays up to n cards from hand, like clicks: each one waits for the game to be idle again.</summary>
    private static async Task Play(SceneTree node, int count)
    {
        int played = 0;
        for (int i = 0; i < count; i++)
        {
            await Until(node, () => !CombatManager.Instance.IsInProgress || IsIdle(), 120, "idle"); // stops once the combat is over
            Player? me = Me();
            CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
            if (!CombatManager.Instance.IsInProgress || me?.PlayerCombatState == null || combat == null)
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

    /// <summary>
    /// Script <c>playcard &lt;CARD_ID&gt; [living enemy index] [free]</c>: plays that card from the hand, waits. "free" sets
    /// its energy and star costs to 0 this turn first (not X costs: X is the energy spent).
    /// </summary>
    private static async Task PlayCard(SceneTree tree, string id, int enemyIndex, bool free)
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
        if (free && !card.EnergyCost.CostsX)
        {
            card.SetToFreeThisTurn();
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

    /// <summary>
    /// Script <c>pilescroll draw|discard|exhaust &lt;frames&gt; [px per frame]</c>: opens a combat pile view as its button
    /// does, then moves the grid's scroll target down every frame like a held mouse wheel, so the grid's own
    /// <c>_Process</c> scrolls and recycles its holders as in play.
    /// </summary>
    private static async Task PileScroll(SceneTree tree, string pileName, int frames, float step)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        PileType type = Enum.Parse<PileType>(pileName, ignoreCase: true);
        CardPile pile = type.GetPile(me);
        long start = Stopwatch.GetTimestamp();
        var screen = MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen.ShowScreen(pile, Array.Empty<string>());
        double openMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        await Frames(tree, 1);
        Say($"  {type} pile view opened: {openMs:F0} ms call, {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms to first frame ({pile.Cards.Count} cards)");
        await Frames(tree, 30);
        NCardGrid grid = FindChild<NCardGrid>(screen) ?? throw new InvalidOperationException("no card grid in the pile view");
        FieldInfo target = AccessTools.Field(typeof(NCardGrid), "_targetDrag");
        var times = new List<double>(frames);
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < frames; i++)
        {
            long frame = Stopwatch.GetTimestamp();
            target.SetValue(grid, (float)target.GetValue(grid)! - step);
            await Frames(tree, 1);
            times.Add(Stopwatch.GetElapsedTime(frame).TotalMilliseconds);
        }
        double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        times.Sort();
        Say($"  scrolled {frames} frames ({step:F0} px each) in {total:F0} ms: {total / frames:F1} ms/frame, "
            + $"median {times[frames / 2]:F1} ms, 95th {times[frames * 95 / 100]:F1} ms, worst {times[^1]:F0} ms");
        start = Stopwatch.GetTimestamp();
        MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer.Instance?.Close();
        await Frames(tree, 1);
        Say($"  pile view closed in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");
        await Frames(tree, 5);
    }

    /// <summary>
    /// Script <c>deckpages &lt;prefix&gt; &lt;pages&gt;</c>: deck view screenshots scrolled two card rows per page, so every
    /// page shows its cards at the same screen positions (card art for the Workshop preview).
    /// </summary>
    private static async Task DeckPages(SceneTree tree, string prefix, int pages)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        var screen = MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen.ShowScreen(me) ?? throw new InvalidOperationException("no deck view");
        await Frames(tree, 30);
        NCardGrid grid = FindChild<NCardGrid>(screen) ?? throw new InvalidOperationException("no card grid in the deck view");
        float pitch = Traverse.Create(grid).Field("_cardSize").GetValue<Vector2>().Y + 40f; // + NCardGrid.CardPadding
        MethodInfo allocate = AccessTools.Method(typeof(NCardGrid), "AllocateCardHolders");
        for (int page = 0; page < pages; page++)
        {
            // SetScrollPosition skips the grid's holder recycling (it only runs while animating a scroll), which
            // moves one row at a time: move there in small steps and recycle after each.
            for (int step = 1; page > 0 && step <= 16; step++)
            {
                grid.SetScrollPosition(-((page - 1) * 2 + step / 8f) * pitch);
                allocate.Invoke(grid, null);
                await Frames(tree, 1);
            }
            await Frames(tree, 20);
            await Screenshot(tree, $"{prefix}-{page:D2}");
        }
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

    /// <summary>
    /// Script <c>baselibmenu &lt;screenshot name&gt;</c>: BaseLib's mod configuration menu on this mod's settings. By
    /// reflection: this DLL must not reference BaseLib (optional).
    /// </summary>
    /// <summary>
    /// Script <c>language &lt;code&gt;</c>: switches the game language for this session only (LocManager, not saved), then
    /// logs this mod's menu texts as the game's settings_ui table now has them.
    /// </summary>
    private static async Task Language(SceneTree tree, string code)
    {
        MegaCrit.Sts2.Core.Localization.LocManager.Instance.SetLanguage(code);
        await Frames(tree, 5);
        string Text(string key) => MegaCrit.Sts2.Core.Localization.LocString.GetIfExists("settings_ui", "BIGDECKOPTIMIZATION" + key)?.GetFormattedText() ?? "(missing)";
        Say($"  LANG {MegaCrit.Sts2.Core.Localization.LocManager.Instance.Language}: general \"{Text("-GENERAL.title")}\", "
            + $"enabled \"{Text("-ENABLED.title")}\", hook_skip \"{Text("-HOOK_SKIP.title")}\", "
            + $"card_type_line \"{Text("-CARD_TYPE_LINE.title")}\" / \"{Text("-CARD_TYPE_LINE.hover.desc")}\"");
    }

    private static async Task BaseLibMenu(SceneTree tree, string shot, string? hover = null)
    {
        var menu = NGame.Instance?.MainMenu ?? throw new InvalidOperationException("not on the main menu");
        Type submenuType = AccessTools.TypeByName("BaseLib.Config.UI.NModConfigSubmenu") ?? throw new InvalidOperationException("BaseLib not loaded");
        object submenu = menu.SubmenuStack.PushSubmenuType(submenuType);
        await Frames(tree, 30);
        object config = AccessTools.Method("BaseLib.Config.ModConfigRegistry:Get", new[] { typeof(string) }).Invoke(null, new object[] { ModEntry.Id })
            ?? throw new InvalidOperationException("settings not registered in BaseLib");
        AccessTools.Method(submenuType, "LoadModConfig").Invoke(submenu, new[] { config });
        await Frames(tree, 40);
        // Each row: shown dimmed (greyed out) and whether its switch can be clicked.
        AccessTools.FieldRef<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl, bool> isEnabled =
            AccessTools.FieldRefAccess<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl, bool>("_isEnabled");
        Control? hoverRow = null;
        foreach (Control row in FindAll<Control>(tree.Root).Where(n => n.GetType().Name == "NConfigOptionRow"))
        {
            var control = AccessTools.Property(row.GetType(), "SettingControl").GetValue(row) as MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl;
            Say($"  MENUROW {row.Name}: alpha {row.Modulate.A:F2}, clickable {(control == null ? "-" : isEnabled(control).ToString())}");
            if (hover != null && row.Name == hover)
            {
                hoverRow = row;
            }
        }
        if (hoverRow != null)
        {
            // Scroll the page so the row is in view (its switch may be disabled, so it can't take the focus).
            Node? scroller = hoverRow.GetParent();
            while (scroller != null && scroller is not MegaCrit.Sts2.Core.Nodes.GodotExtensions.NScrollableContainer)
            {
                scroller = scroller.GetParent();
            }
            if (scroller is MegaCrit.Sts2.Core.Nodes.GodotExtensions.NScrollableContainer scroll
                && AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NScrollableContainer), "_content").GetValue(scroll) is Control content)
            {
                float limit = (float)AccessTools.Property(scroll.GetType(), "ScrollLimitBottom").GetValue(scroll)!;
                float target = Math.Clamp(content.GlobalPosition.Y - hoverRow.GlobalPosition.Y + 300f, Math.Min(limit, 0f), 0f);
                AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NScrollableContainer), "_targetDragPosY").SetValue(scroll, target);
                await Frames(tree, 60);
            }
            AccessTools.Method(hoverRow.GetType(), "OnHovered").Invoke(hoverRow, null);
            string key = $"BIGDECKOPTIMIZATION-{MegaCrit.Sts2.Core.Helpers.StringHelper.Slugify(hoverRow.Name).ToUpperInvariant()}.hover.desc";
            Say($"  MENUHOVER {hoverRow.Name}: {MegaCrit.Sts2.Core.Localization.LocString.GetIfExists("settings_ui", key)?.GetFormattedText() ?? "(no text)"}");
            await Frames(tree, 20);
        }
        await Screenshot(tree, shot);
    }

    private static AccessTools.FieldRef<MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarDeckButton, MegaCrit.Sts2.addons.mega_text.MegaLabel>? _deckCountLabel;
    private static MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarDeckButton? _deckButton;

    /// <summary>The top bar's deck counter shows the deck's size (it counts cards as their add animations finish).</summary>
    private static bool DeckCountShown(SceneTree tree)
    {
        Player? me = Me();
        if (_deckButton == null || !GodotObject.IsInstanceValid(_deckButton) || !_deckButton.IsInsideTree())
        {
            _deckButton = FindAll<MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarDeckButton>(tree.Root).FirstOrDefault();
        }
        if (me == null || _deckButton == null)
        {
            return false;
        }
        _deckCountLabel ??= AccessTools.FieldRefAccess<MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarDeckButton, MegaCrit.Sts2.addons.mega_text.MegaLabel>("_countLabel");
        return _deckCountLabel(_deckButton).Text == me.Deck.Cards.Count.ToString();
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
        Callable.From(() =>
        {
            if ((_hidden || _showScreen >= 0) && SaveManager.Instance.SettingsSave is { Fullscreen: false })
            {
                // Back where the settings put it, so a windowed game doesn't save the hidden position on exit (a
                // fullscreen game saves no position: it closes where it is, off-screen).
                _quitting = true;
                NGame.Instance?.ApplyDisplaySettings();
            }
            NGame.Instance?.Quit();
        }).CallDeferred();
    }
}
