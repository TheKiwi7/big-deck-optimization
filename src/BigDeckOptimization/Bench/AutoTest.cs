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
/// Script lines (# comments; the switch in <see cref="Run"/> lists every command, each documented on its own method), among them:
///   newrun &lt;CHARACTER&gt; [seed]   mplobby &lt;CHARACTER&gt; [players] [seed]   wait &lt;s&gt;   timeout &lt;s&gt;   log &lt;text&gt;   quit   onfail continue
///   waitfor menu|run|combat|nocombat|idle|turn|round &lt;n&gt;|room &lt;type&gt;|event|restsite|deckcount [timeout s]   barrier &lt;label&gt; [timeout s]
///   cmd &lt;console command&gt;   play &lt;n&gt;   playcard &lt;ID&gt;   handselect &lt;ID&gt; [hold &lt;s&gt;] [free]   endturn   avoid ID,ID   enemyhp &lt;hp&gt;   immortal [off | until &lt;round&gt;]
///   state [label]   checksum [label] [vanilla|same]   runstate [label]   effects [label]   effectcheck all ran [except re] | &lt;Effect&gt; zero|ran|same|grew [label]   counters [label]   gcfull [label]   leakcheck [label]
///   autoroom ...   automap   autoslay &lt;seed&gt;   sweep ...   eventoption ...   restsite ...   fpsstat [label]   marker &lt;text&gt;   selector ui|auto|local
///   {hash:NAME} anywhere = hash of the shared run file NAME   speed normal|fast|instant (default fast)
/// </summary>
public static partial class AutoTest
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
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.InitProfileId)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(InitProfilePostfix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(CombatManager), nameof(CombatManager.SetUpCombat)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(SetUpCombatPostfix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(Creature), nameof(Creature.LoseHpInternal)),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(ImmortalPrefix)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(ImmortalPostfix)));
        // Timings must not depend on which window has focus: no 30 fps cap while the game is in the background.
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.NBackgroundModeHandler), "EnterBackgroundMode"),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(StayForeground)));
        // Fur Coat reads the current map point, which is null when the fight command runs before visiting the map.
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.Relics.FurCoat), nameof(MegaCrit.Sts2.Core.Models.Relics.FurCoat.BeforeCombatStart)),
            prefix: new HarmonyMethod(typeof(AutoTest), nameof(SkipFurCoatWithoutMapPoint)));
        // The fight command seeds the encounter from the clock in seconds (up to v0.111) or a random seed (v0.113,
        // Rng.CreateChaoticSeed): peers running it at different moments desync, and two sessions fight different groups.
        // v0.113 returns the seed it picked (the command logs it): the prefix answers the fixed one.
        MethodInfo randomizeRng = AccessTools.Method(typeof(EncounterModel), nameof(EncounterModel.DebugRandomizeRng));
        PatchGuard.Patch(harmony, randomizeRng,
            prefix: new HarmonyMethod(typeof(AutoTest), randomizeRng?.ReturnType == typeof(ulong) ? nameof(FixedEncounterRngWithSeed) : nameof(FixedEncounterRng)));
        // In co-op a networked command is only enqueued (DevConsole.ProcessCommand answers "Enqueued"): its result reaches the
        // console's output only. Every peer runs it through ProcessNetCommand: a failure there fails the session.
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(DevConsole), nameof(DevConsole.ProcessNetCommand)),
            finalizer: new HarmonyMethod(typeof(AutoTest), nameof(NetCommandFinalizer)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsHost)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(LobbyScreenPostfix)));
        PatchGuard.Patch(harmony, AccessTools.Method(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsClient)),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(LobbyScreenPostfix)));
        string[] launchArgs = OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).ToArray();
        // --bdforeign <shape> (a script's 'launchforeign' line): another mod's patches that must be in before this mod applies.
        ForeignLaunch.FromLaunch(launchArgs);
        int portArg = Array.IndexOf(launchArgs, "--bdport");
        if (portArg >= 0 && portArg + 1 < launchArgs.Length && ushort.TryParse(launchArgs[portArg + 1], out _port))
        {
            // Several co-op tests at once: each pair of instances on its own local port instead of the game's fixed one.
            var usePort = new HarmonyMethod(typeof(AutoTest), nameof(UseTestPort));
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.NetHostGameService), "StartENetHost"), prefix: usePort);
            PatchGuard.Patch(harmony, typeof(MegaCrit.Sts2.Core.Multiplayer.Connection.ENetClientConnectionInitializer).GetConstructors()[0], prefix: usePort);
        }
        if (launchArgs.Contains("--bdoff"))
        {
            // --bdoff (a script with a 'launchoff' line): the whole mod off for the session, before the main menu's --fastmp
            // lobby builds the co-op join lists (v0.111+ keeps the one from when hosting or joining started).
            _launchedOff = true;
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu), "CheckCommandLineArgs"),
                prefix: new HarmonyMethod(typeof(AutoTest), nameof(LaunchOffPrefix)));
        }
        int show = Array.IndexOf(launchArgs, "--bdshow");
        if (show >= 0 && show + 1 < launchArgs.Length && int.TryParse(launchArgs[show + 1], out int showScreen))
        {
            // --bdshow <screen>: fullscreen on that screen, for watching a session. Only the window moves.
            _showScreen = showScreen;
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(NGame), nameof(NGame.ApplyDisplaySettings)),
                postfix: new HarmonyMethod(typeof(AutoTest), nameof(ShowOnScreen)));
            // The game keeps its sound while another window has the focus ("mute in background" setting).
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.NMuteInBackgroundHandler), "Mute"),
                prefix: new HarmonyMethod(typeof(AutoTest), nameof(StayForeground)));
            Callable.From(ShowOnScreen).CallDeferred();
        }
        else if (launchArgs.Contains("--bdhidden"))
        {
            _hidden = true;
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(NGame), nameof(NGame.ApplyDisplaySettings)),
                prefix: new HarmonyMethod(typeof(AutoTest), nameof(WindowStaysPut)),
                postfix: new HarmonyMethod(typeof(AutoTest), nameof(HideWindow)), finalizer: new HarmonyMethod(typeof(AutoTest), nameof(WindowMayMove)));
            // The game's fullscreen and screen would show the window on a screen for a moment, however soon it moves away.
            var stay = new HarmonyMethod(typeof(AutoTest), nameof(KeepWindowWhereItIs));
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(DisplayServer), nameof(DisplayServer.WindowSetMode)), prefix: stay);
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(DisplayServer), nameof(DisplayServer.WindowSetCurrentScreen)), prefix: stay);
            Callable.From(HideWindow).CallDeferred();
        }
        if (launchArgs.Contains("--bdturnstates"))
        {
            LogTurnStates();
        }
        if (OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).Contains("--bdmute"))
        {
            // --bdmute (run.ps1 without -Sound): every master volume the game sets (startup, mute in background) is 0.
            // The volume settings themselves are untouched.
            var mute = new HarmonyMethod(typeof(AutoTest), nameof(MuteVolume));
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager), "SetMasterVol"), prefix: mute);
            PatchGuard.Patch(harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager), "SetMasterAudioVolume"), prefix: mute);
            Callable.From(() =>
            {
                NGame.Instance?.AudioManager?.SetMasterVol(0f);
                NGame.Instance?.DebugAudio?.SetMasterAudioVolume(0f);
            }).CallDeferred();
        }
        // The test tooling applies first (CombatStartTimer, then this): what InlineGuard protected so far was for its own patches.
        _benchProtected = InlineGuard.Protected;
        Callable.From(Start).CallDeferred();
    }

    /// <summary>
    /// InlineGuard protections made for the test tooling's own patches (at load and as script lines patch more): left out of the
    /// "InlineGuard" effect, which must say whether the optimizations' patches got protected.
    /// </summary>
    private static long _benchProtected;

    /// <summary>Patches of the test tooling made while a script runs: their InlineGuard protections are the tooling's.</summary>
    private static void BenchPatches(Action patch)
    {
        long before = InlineGuard.Protected;
        try
        {
            patch();
        }
        finally
        {
            _benchProtected += InlineGuard.Protected - before;
        }
    }

    private static void MuteVolume(ref float __0) => __0 = 0f;

    /// <summary>
    /// Script <c>deckfilter [ID,ID...] [stacked]</c> (on the map): removes from the deck the cards with these ids and,
    /// with <c>stacked</c>, those holding several stacked enchantments (Limitless' MultiEnchantment), e.g. to keep long
    /// multi-hit animations out of a session.
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
            // Through reflection: test tooling must keep working on game versions where this method changed.
            AccessTools.Method(typeof(CardPile), "RemoveInternal")!.Invoke(me.Deck, new object[] { card, true });
            (me.RunState as ICardScope)?.RemoveCard(card);
        }
        // The silent removal raises no event: the top bar's deck count would keep the old size.
        var nodes = new Stack<Node>();
        nodes.Push(((SceneTree)Engine.GetMainLoop()).Root);
        while (nodes.Count > 0)
        {
            Node node = nodes.Pop();
            if (node is MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarDeckButton button)
            {
                button.Call("OnPileContentsChanged");
            }
            foreach (Node child in node.GetChildren())
            {
                nodes.Push(child);
            }
        }
        Say($"  deckfilter: removed {removed.Count} cards ({removed.Count(c => ids.Contains(c.Id.Entry))} by id), deck {me.Deck.Cards.Count} cards");
    }

    private static bool TruePrefix() => true;

    private static void NoOpPostfix()
    {
    }

    private static bool _hidden, _quitting, _applyingDisplaySettings;

    private static void WindowStaysPut() => _applyingDisplaySettings = !_quitting;

    private static void WindowMayMove() => _applyingDisplaySettings = false;

    private static bool KeepWindowWhereItIs() => !_applyingDisplaySettings;

    private static int _showScreen = -1;

    /// <summary>
    /// <c>--bdshow &lt;screen&gt;</c>: the game fullscreen and on top on that screen (to watch a session). Only the
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
        _applyingDisplaySettings = false;
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
            // --bdedge: one column of pixels on screen. Windows throttles the presents of a window entirely off-screen, which an
            // outside measure (PresentMon) would see as half the frames.
            DisplayServer.WindowSetPosition(new Vector2I(OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()).Contains("--bdedge") ? left - 1919 : left - 1920 - 400, top));
            // Launched hidden (run.ps1): shown only now it is beyond the screens, without the focus, so it renders as a normal window.
            const int ShowNoActivate = 4;
            ShowWindow((IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle), ShowNoActivate);
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

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
    /// Cards whose manual play the combat history saw start since <see cref="Play"/> last cleared it (it waits for its own). A set,
    /// not the last one: in co-op the other player's plays start in between.
    /// </summary>
    private static readonly HashSet<CardModel> _manualPlaysStarted = new(ReferenceEqualityComparer.Instance);

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
            if (!history.Entries.Any())
            {
                _manualPlaysStarted.Clear(); // A new combat (the history cleared): the last one's cards hold their run.
            }
            if (history.Entries.LastOrDefault() is MegaCrit.Sts2.Core.Combat.History.Entries.CardPlayStartedEntry { CardPlay: var play })
            {
                if (!play.IsAutoPlay)
                {
                    _manualPlaysStarted.Add(play.Card);
                }
                Say($"  CARDPLAY {(play.IsAutoPlay ? "auto" : "manual")} {play.Card.Id.Entry} -> {play.Target?.ToString() ?? "no target"}");
            }
        };
    }

    /// <summary>Script <c>enemyhp</c>: enemies get this HP before any combat-start hook (relics) can hit them.</summary>
    private static int? _enemyHp;

    /// <summary>From this peer's combat set-up to its end (or tomenu): see <see cref="RequireNoCombatYetInCoop"/>.</summary>
    private static bool _combatOpen;

    /// <summary>
    /// <c>enemyhp</c> and <c>immortal</c> are local: each peer applies them to its own combats. In co-op, one coming after this peer's
    /// combat was set up (the host's <c>cmd fight</c> ran first: only wall-clock waits ordered them) leaves the peers with different
    /// enemies, a state divergence that would look like the mod's. Fails the line instead; a <c>barrier</c> before the host's fight
    /// orders them.
    /// </summary>
    private static void RequireNoCombatYetInCoop(string line)
    {
        bool coop = RunManager.Instance.IsInProgress
            && RunManager.Instance.NetService?.Type is MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host or MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client;
        if (coop && (_combatOpen || CombatManager.Instance.IsInProgress))
        {
            throw new InvalidOperationException($"'{line}' in co-op after this peer's combat was set up: the peers' enemies would differ "
                + "(put a 'barrier' on every peer after it, before the host's 'cmd fight')");
        }
    }

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

    /// <summary>Set while <see cref="Command"/> runs a command itself: it sees the result (and a sweep's refusals are expected).</summary>
    private static bool _directCommand;

    /// <summary>A networked command that failed when the action queue ran it, until a script line or a wait reports it.</summary>
    private static string? _netCommandFailed;

    private static void NetCommandFinalizer(Player? player, string inputValue, CmdResult __result, Exception? __exception)
    {
        if (_directCommand)
        {
            return;
        }
        if (__exception != null || !__result.success)
        {
            NetCommandFailed($"networked command '{inputValue}' (player {player?.NetId}) failed: {__exception?.Message ?? __result.msg}");
        }
        else if (__result.task is { } task)
        {
            // The queue awaits the command's task (NDevConsole.ProcessNetCommand returns it): a fault there only reaches the game's log,
            // without a frame of this mod's patches, which judge.py doesn't count.
            task.ContinueWith(t => NetCommandFailed($"networked command '{inputValue}' (player {player?.NetId}) failed in its task: {t.Exception?.GetBaseException().Message}"),
                System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static void NetCommandFailed(string failed)
    {
        failed = failed.Replace("\n", " ");
        _netCommandFailed ??= failed;
        Say("FAILED: " + failed);
    }

    /// <summary>Fails the current script line or wait when a queued networked command failed meanwhile.</summary>
    private static void ThrowIfNetCommandFailed()
    {
        if (_netCommandFailed is { } failed)
        {
            _netCommandFailed = null;
            throw new InvalidOperationException(failed);
        }
    }

    private const uint EncounterSeed = 12345u;

    private static bool FixedEncounterRng(EncounterModel __instance)
    {
        AccessTools.Field(typeof(EncounterModel), "_rng").SetValue(__instance, SeededRng(EncounterSeed));
        return false;
    }

    /// <summary>Game v0.113: the method returns the seed it used, which the fight command logs ("rng seed"): this one, not 0.</summary>
    private static bool FixedEncounterRngWithSeed(EncounterModel __instance, ref ulong __result)
    {
        FixedEncounterRng(__instance);
        __result = EncounterSeed;
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

    /// <summary>
    /// Script <c>mplobby &lt;CHARACTER&gt; [players] [seed]</c>: once the lobby has that many players, pick and ready up.
    /// The host sets the run seed (default BDTEST, as newrun): without it the game draws one from <c>Rng.Chaotic</c>, the
    /// clock in seconds up to v0.111 (two sessions started in the same second got the same run, by chance) and a random
    /// number since v0.113, so an on/off pair of co-op sessions played two different runs. Through
    /// <c>NGame.DebugSeedOverride</c>, which the lobby reads when everyone is ready (as the game's AutoSlayer does):
    /// <c>StartRunLobby.SetSeed</c> throws in standard mode (the character screen refuses seed changes). The lobby screen
    /// resets it when it initializes, before this runs (v0.113: through <c>NGame.InitDebugSeedFromBootstrap</c>, null without
    /// bootstrap settings), so a later lobby doesn't reuse it.
    /// </summary>
    private static async Task MpLobby(SceneTree tree, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int players = parts.Length > 1 ? int.Parse(parts[1]) : 2;
        string seed = parts.Length > 2 ? parts[2] : "BDTEST";
        await Until(tree, () => _lobbyScreen?.Lobby is { } lobby && LobbyPlayerCount(lobby) >= players, 300, $"lobby with {players} players");
        CharacterModel character = ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), parts[0].ToUpperInvariant()));
        var startLobby = _lobbyScreen!.Lobby;
        if (startLobby.NetService.Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host)
        {
            NGame.Instance!.DebugSeedOverride = seed;
            Say($"  run seed {seed}");
        }
        SetLobbyCharacter(startLobby, character);
        await Frames(tree, 30);
        startLobby.SetReady(true);
        Say($"  ready as {character.Id.Entry} ({LobbyPlayerCount(startLobby)} players)");
        if (!_uiSelection)
        {
            _selector ??= PushSelector(new FirstCardsSelector());
        }
        SaveManager.Instance.Progress.EnableFtues = false;
    }

    private static IDisposable? _selector;

    /// <summary>
    /// <c>handselect</c> lines in this session's script: ListenerCache's suspension (<c>ListenerCache.Suspended</c>, hand selections
    /// revalidated by a state change) is listed in the effects only then, the only sessions that can reach it (a held hand choice),
    /// from the first <c>effects</c> line on (an <c>effectcheck ... grew</c> needs it before the line).
    /// </summary>
    private static int _handSelects;

    /// <summary>
    /// Effects listed only in sessions whose script names them in an <c>effectcheck &lt;Effect&gt; ...</c> line: finer counters of
    /// parts the other effects already cover, or paths only some scenarios reach (SnapshotVerifier's Sample mode skips combats over
    /// 1,000 cards, which most scenarios are; HookFilter's mirrors one by one; BaseLib's paths). Listed everywhere, they would read
    /// as effects lost in sessions that never could run them.
    /// </summary>
    private static readonly HashSet<string> _optIn = new();

    private static (string Name, long Count)[] OptInCounts()
    {
        var all = new List<(string, long)>
        {
            (SnapshotVerifier.Name, SnapshotVerifier.Verified), (ListenerCache.Name + ".Reused", ListenerCache.Reused),
            (FastListeners.Name + ".ModifierSkip", FastListeners.SkippedModifierWalks), (HookSkip.Name + ".BaseLib", HookSkip.BaseLibWalks),
            // The model stack premise made exact (A4): the badge guard's fallbacks, filtered walks emptied (0 expected), KeptOnly's first listeners kept.
            (HookSkip.Name + ".NoBadge", HookSkip.NoBadgeWalks), (HookSkip.Name + ".Emptied", HookSkip.EmptiedWalks), (FastListeners.Name + ".FirstKept", FastListeners.FirstKept),
            (ExtraScopes.Name + ".BetterSpire2", ExtraScopes.BetterSpireScopes), (CardDbScan.Name + ".Verified", CardDbScan.Verified),
            (RelicFlashMerge.Name + ".SoundsReplayed", RelicFlashMerge.SoundsReplayed),
            (CardFlightLimit.Name + ".KeptAtCombatEnd", CardFlightLimit.KeptAtCombatEnd),
            (ListenerCache.WalkReuseName, ListenerCache.WalksReused), (ListenerCache.WalkReuseName + ".Verified", ListenerCache.WalksVerified),
            (ListenerCache.WalkReuseName + ".Kept", ListenerCache.WalksMaterialized), (ListenerCache.WalkReuseName + ".ForeignDispatches", ListenerCache.ForeignDispatches),
            (ListenerCache.WalkReuseName + ".OutOfScope", ListenerCache.ReusesOutOfScope),
            // The rule of 2026-11-17: dispatches other mods handle too reuse in the game's handlers, run theirs suspended, drop after them.
            (ListenerCache.WalkReuseName + ".GameHandlerReuses", ListenerCache.GameHandlerReuses),
            (ListenerCache.WalkReuseName + ".ForeignHandlers", ListenerCache.ForeignHandlerCalls),
            (ListenerCache.WalkReuseName + ".DroppedAfterForeign", ListenerCache.KeptDroppedAfterForeign),
            (ListenerCache.WalkReuseName + ".KeptAfterForeign", ListenerCache.KeptAfterForeign),
            // KeptOnly's memo over the kept outputs: filtered walks replayed, memos made, uses checked against KeptOnly's own loop.
            (ListenerCache.WalkMemoName, ListenerCache.MemoHits), (ListenerCache.WalkMemoName + ".Built", ListenerCache.MemoBuilt),
            (ListenerCache.WalkMemoName + ".Verified", ListenerCache.MemoVerified),
            // The checksum snapshot's walk reuse: walks answered from a kept output, outputs kept, reuses checked in the snapshot,
            // fresh walks compared by its net, snapshots the net found wrong and the snapshots built again.
            (ListenerCache.SnapshotWalksName, ListenerCache.SnapshotWalkReuses), (ListenerCache.SnapshotWalksName + ".Kept", ListenerCache.SnapshotWalksKept),
            (ListenerCache.SnapshotWalksName + ".Checked", ListenerCache.SnapshotWalkChecks), (ListenerCache.SnapshotWalksName + ".Net", ListenerCache.SnapshotNetCompared),
            (ListenerCache.SnapshotWalksName + ".Detections", ListenerCache.SnapshotDetections), (SnapshotVerifier.Name + ".Rebuilt", SnapshotVerifier.Rebuilt),
            (ListenerCache.SnapshotWalksName + ".Memos", ListenerCache.SnapshotMemosCompared), (ListenerCache.SnapshotWalksName + ".Sealed", ListenerCache.SnapshotSealChanges),
            (RelicFlashMerge.Name + ".Holder", RelicFlashMerge.MergedHolder), (RelicFlashMerge.Name + ".Creature", RelicFlashMerge.MergedCreature),
            (RelicFlashMerge.Name + ".SceneRefusals", RelicFlashMerge.SceneRefusals), (RelicFlashMerge.Name + ".NoNode", RelicFlashMerge.MergedNoNode),
            // Only where another mod patches a small method after this mod's load (foreign-helpers-2k-on).
            ("InlineGuard.Refreshed", InlineGuard.Refreshed),
            // Only where a join is refused over this mod's entry (coop-modetag.ps1's mixed pair; bdperf joinmsg puts it back).
            (JoinMismatchNote.Name, JoinMismatchNote.Explained),
        };
        for (int i = 0; i < HookFilter.MirrorNames.Length; i++)
        {
            all.Add((HookFilter.Name + "." + HookFilter.MirrorNames[i], HookFilter.MirrorAnswers[i]));
        }
        return all.Where(e => _optIn.Contains(e.Item1)).ToArray();
    }

    /// <summary>Each optimization's effect counter (the <c>effects</c> line), by name.</summary>
    /// Keep in step with Bench/EffectCounters.cs (the counters a bdperf bench puts back).
    private static (string Name, long Count)[] EffectCounts() => new (string, long)[]
    {
        // CardSortKeys only where this game version needs it (v0.113+; before, CardModel.CompareTo is cheap): a needed one that
        // didn't apply (a changed fingerprint, a fault) shows at 0, as every other optimization does. NotNeeded is only known
        // once its Apply ran: with the mod off at game start (StartedOff) Apply never runs, so no line there.
        (ListenerCache.Name, ListenerCache.Served), (ListenerCache.Name + ".Creatures", ListenerCache.CreatureInvalidations), (HookFilter.Name, HookFilter.ListenersDropped), (ExtraScopes.Name, ExtraScopes.FirstTurnPasses),
        (IdleButtonInput.Name, IdleButtonInput.Muted), (GridSortCache.Name, GridSortCache.Sorts), (EnchantmentTabReuse.Name, EnchantmentTabReuse.Reused),
        (PoolSignalScan.Name, PoolSignalScan.Nodes), (CardDbScan.Name, CardDbScan.Skipped), (PileIndex.Name, PileIndex.Fast), (PileIndex.Name + ".Duplicates", PileIndex.FastDuplicateChecks),
        (PileCounterBatch.Name, PileCounterBatch.Batched), (FastCombatStart.Name, FastCombatStart.SkippedWaits), (HoverTipReuse.Name, HoverTipReuse.Reused),
        (FastListeners.Name + ".CombatWalk", FastListeners.CombatWalks), (FastListeners.Name + ".RunWalk", FastListeners.RunWalks),
        (FastListeners.Name + ".Cache", FastListeners.SegmentHits + FastListeners.SegmentMoveHits), (FastListeners.Name + ".Moves", FastListeners.SegmentMoveHits + FastListeners.SnapshotCatchUps),
        (FastListeners.Name + ".Kinds", FastListeners.KindsSkipped), (FastListeners.Name + ".Others", FastListeners.OthersDropped),
        (HookSkip.Name, HookSkip.FilteredWalks), (LimitlessReserialize.Name, LimitlessReserialize.Kept), (LimitlessRecalcSkip.Name + ".Memos", LimitlessRecalcSkip.Skipped), (LimitlessRecalcSkip.Name + ".Passes", LimitlessRecalcSkip.PassesSkipped),
        (SnapshotCardReuse.Name + ".Cards", SnapshotCardReuse.Reused), (SnapshotCardReuse.Name + ".Release", SnapshotCardReuse.CombatCardsReleased), (SnapshotCardReuse.Name + ".Piles", SnapshotCardReuse.PilesReused + SnapshotCardReuse.PilesTrusted), (SnapshotCardReuse.Name + ".Trust", SnapshotCardReuse.PilesTrusted), (SnapshotCardReuse.Name + ".Moved", SnapshotCardReuse.MovedReused), (SnapshotCardReuse.Name + ".Dirty", SnapshotCardReuse.DirtyPiles), (SnapshotCardReuse.Name + ".Hooks", SnapshotCardReuse.PilesWithHooks), (SnapshotCardReuse.Name + ".Blocks", SnapshotCardReuse.BlockCopiedCards),
        (SnapshotCardReuse.Name + ".CardBits", SnapshotCardReuse.BitsReused), (SnapshotCardReuse.Name + ".UpgradeBits", SnapshotCardReuse.UpgradeBitsPatched), (SnapshotCardReuse.Name + ".PileBits", SnapshotCardReuse.PileBitsReused + SnapshotCardReuse.PileBitsComposed),
        (DeckCountMemo.Name, DeckCountMemo.Kept + DeckCountMemo.Updated), (DeckCountMemo.Name + ".Checks", DeckCountMemo.Checks), (LimitlessHookSkip.Name, LimitlessHookSkip.Dropped), (LimitlessHookSkip.Name + ".Saved", LimitlessHookSkip.NotesFromSaved), (LimitlessCloneMemo.Name, LimitlessCloneMemo.Reused), (TextPoolReuse.Name, TextPoolReuse.Served), (ThemeColorSkip.Name, ThemeColorSkip.Skipped), (ReplayStateSkip.Name, ReplayStateSkip.Skipped),
        (GcPacing.Name, GcPacing.Regions), (RelicFlashMerge.Name, RelicFlashMerge.Merged), (CardFlightLimit.Name, CardFlightLimit.Skipped), (TypePlaqueSkip.Name, TypePlaqueSkip.Skipped),
        (ControllerActionSkip.Name, ControllerActionSkip.Skipped), (RoomCollect.Name, RoomCollect.Background), (PooledCardRelease.Name, PooledCardRelease.Released), (SyncedCardRelease.Name, SyncedCardRelease.Released), (SavedPropertyLookup.Name, SavedPropertyLookup.Served), (RelicStateReuse.Name, RelicStateReuse.Reused), (RelicStateReuse.ScalarName, RelicStateReuse.ScalarReused), (RichTextEnvRelease.Name, RichTextEnvRelease.Released), (RichTextEnvRelease.Name + ".Untracked", RichTextEnvRelease.Untracked), ("InlineGuard", InlineGuard.Protected - _benchProtected),
    }.Concat(_handSelects > 0 ? new (string, long)[] { (ListenerCache.Name + ".Suspended", ListenerCache.SuspendedRevalidations) } : Array.Empty<(string, long)>())
        .Concat(OptInCounts())
        // A Dread or Blitz run (v0.113+) started in this session: the walks' tails and invalidations only those modes reach.
        .Concat(_modeRun ? new (string, long)[] { (FastListeners.Name + ".Dominion", FastListeners.DominionTails), (FastListeners.Name + ".ModeRunModels", FastListeners.ModeRunModelTails), (ListenerCache.Name + ".Dominion", ListenerCache.DominionInvalidations), (ListenerCache.Name + ".ModeRunModels", ListenerCache.ModeRunModelInvalidations) } : Array.Empty<(string, long)>()).Concat(CardSortKeys.Applied || (!CardSortKeys.NotNeeded && !ModEntry.StartedOff) ? new (string, long)[] { (CardSortKeys.Name, CardSortKeys.Sorts), (CardSortKeys.Name + ".Answers", CardSortKeys.Answered) } : Array.Empty<(string, long)>()).ToArray();

    /// <summary>
    /// The game starts its "Common" preload at the main menu without awaiting it. A run started meanwhile (a script's newrun 0.4 s
    /// after the menu; a player takes seconds) unloads what that preload is still loading, and the run's own preload never
    /// completes: the session stops in "Preloading 'characters=...'", mod on or off (v0.113.1, 2026-11-15). Waits until the Common and
    /// main menu sets are loaded. Nothing loaded for 15 s with some still missing: the game's loader stopped (seen once under a full
    /// machine, the run's preload then never ends): false, and the session starts over. A game whose own assets fail to load
    /// would start every session over: the runner reports that after its retries.
    /// </summary>
    private static async Task<bool> WaitCommonPreload(SceneTree tree)
    {
        try
        {
            var wanted = MegaCrit.Sts2.Core.Assets.AssetSets.CommonAssets.Concat(MegaCrit.Sts2.Core.Assets.AssetSets.MainMenuSet).ToHashSet();
            long start = Stopwatch.GetTimestamp();
            long changed = start;
            int missing = -1;
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < 60)
            {
                IReadOnlySet<string> loaded = MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetLoadedCacheAssets();
                int now = wanted.Count(path => !loaded.Contains(path));
                if (now == 0)
                {
                    missing = 0;
                    break;
                }
                if (now != missing)
                {
                    missing = now;
                    changed = Stopwatch.GetTimestamp();
                }
                else if (Stopwatch.GetElapsedTime(changed).TotalSeconds > 15)
                {
                    break;
                }
                await Frames(tree, 5);
            }
            Say($"  common preload: {(missing == 0 ? "complete" : $"{missing} asset(s) never loaded")} after {Stopwatch.GetElapsedTime(start).TotalSeconds:F1} s");
            return missing == 0;
        }
        catch (Exception e)
        {
            Say($"  common preload not checked: {e.GetType().Name}: {e.Message}");
            return true;
        }
    }

    /// <summary>The counters of each <c>effects &lt;label&gt;</c> line, for <see cref="EffectCheck"/>.</summary>
    private static readonly Dictionary<string, Dictionary<string, long>> _effectsAt = new();

    /// <summary>
    /// Script <c>effectcheck &lt;Effect&gt; same|grew &lt;label&gt;</c>: that effect counter must have stayed the same (an optimization
    /// that must be off or idle since then) or grown since the <c>effects &lt;label&gt;</c> line, or the line fails: what a scenario
    /// would otherwise leave to be read by eye.
    /// </summary>
    private static void EffectCheck(string arg)
    {
        if (ModEntry.StartedOff || !Config.Settings.Master)
        {
            // The mod-off twin of a scenario (launchoff, --bdvanilla, parcoop -OnOff's autooff, bdopt off): nothing of it runs, and
            // the script's steps must stay those of the mod-on side.
            Say($"  EFFECTCHECK {arg} skipped: the mod is off");
            return;
        }
        System.Text.RegularExpressions.Match all = System.Text.RegularExpressions.Regex.Match(arg, @"^all\s+ran(?:\s+except\s+(\S.*))?$");
        if (all.Success)
        {
            EffectCheckAllRan(all.Groups[1].Success ? all.Groups[1].Value.Trim() : null);
            return;
        }
        string[] parts = arg.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1] is "zero" or "ran")
        {
            // A counter its optimization resets (FastCombatStart's, at each combat room entered) can't be compared with an earlier line.
            // "ran": above zero now (what a scenario is there to make run, without an earlier effects line).
            (string Name, long Count)[] counts = EffectCounts();
            if (!counts.Any(e => e.Name == parts[0]))
            {
                throw new ArgumentException($"no effect {parts[0]} in this session (effects: {string.Join(", ", counts.Select(e => e.Name))})");
            }
            long count = counts.First(e => e.Name == parts[0]).Count;
            Say($"  EFFECTCHECK {parts[0]} {parts[1]}: {count}");
            if (parts[1] == "zero" ? count != 0 : count == 0)
            {
                throw new InvalidOperationException(parts[1] == "zero" ? $"{parts[0]} is {count}, not 0" : $"{parts[0]} never ran (0)");
            }
            return;
        }
        if (parts.Length < 3 || parts[1] is not ("same" or "grew"))
        {
            throw new ArgumentException("effectcheck all ran [except <regex>] | <Effect> zero|ran | <Effect> same|grew <label of an earlier 'effects' line>");
        }
        if (!_effectsAt.TryGetValue(parts[2], out Dictionary<string, long>? then) || !then.TryGetValue(parts[0], out long before))
        {
            throw new ArgumentException($"no effect {parts[0]} in an earlier 'effects {parts[2]}' line");
        }
        long now = EffectCounts().First(e => e.Name == parts[0]).Count;
        bool ok = parts[1] == "same" ? now == before : now > before;
        Say($"  EFFECTCHECK {parts[0]} {parts[1]} since '{parts[2]}': {before} -> {now}");
        if (!ok)
        {
            throw new InvalidOperationException($"{parts[0]} {(parts[1] == "same" ? "grew" : "didn't grow")} since 'effects {parts[2]}': {before} -> {now}");
        }
    }

    /// <summary>
    /// Script <c>effectcheck all ran [except &lt;regex&gt;]</c>: every effect counter of this session (the <c>effects</c> line's) above
    /// zero, or the line fails naming those at zero: what tools/autotest/effects.py checks, in the session itself (no runner reads
    /// effects.py's verdict but mutations.ps1). The regex (searched, so anchor it) names the effects allowed at zero, matched
    /// against the effect's name and against <c>&lt;name&gt;@&lt;game version&gt;</c> (<c>ReplayStateSkip@v0.107.1</c>): an
    /// exception for one game version only reads <c>^ReplayStateSkip@v0\.107\.</c>. The effect counts are logged as <c>EFFECT</c>
    /// lines would be, with the verdict.
    /// </summary>
    private static void EffectCheckAllRan(string? except)
    {
        System.Text.RegularExpressions.Regex? allowed = except == null ? null : new System.Text.RegularExpressions.Regex(except);
        string version = Reflect.GameVersion() ?? "unknown";
        var counts = EffectCounts();
        var zero = counts.Where(e => e.Count == 0).Select(e => e.Name).ToList();
        var allowedZero = zero.Where(name => allowed != null && (allowed.IsMatch(name) || allowed.IsMatch(name + "@" + version))).ToList();
        var never = zero.Except(allowedZero).ToList();
        Say($"  EFFECTCHECK all ran ({version}): {counts.Length - zero.Count}/{counts.Length} ran"
            + (allowedZero.Count > 0 ? $", allowed at zero: {string.Join(", ", allowedZero)}" : "")
            + (never.Count > 0 ? $", NEVER RAN: {string.Join(", ", never)}" : ""));
        if (never.Count > 0)
        {
            throw new InvalidOperationException($"effects at zero: {string.Join(", ", never)}");
        }
    }

    /// <summary>The current room when this peer's last cmd room / cmd event ran, until a waitfor room saw it replaced.</summary>
    private static WeakReference<object>? _roomBeforeJump;
    /// <summary>Script <c>selector ui</c>: card choices go through the game's screens; a new run pushes no selector.</summary>
    private static bool _uiSelection;

    /// <summary>
    /// CardSelectCmd.PushSelector through reflection: game v0.111 added an optional parameter (localOnly), which changes
    /// the method's signature; called with its default values on any version. Game v0.113 moved the selectors to
    /// per-player stacks of the run (<c>RunManager.CardSelectorStack</c>); its static test selector answers every player
    /// like the old global stack: set there, a selector already set suspended until this one is disposed. With
    /// <c>selector local</c> on v0.113, the run's own local selector instead (<see cref="RunLocalSelector"/>).
    /// </summary>
    private static IDisposable PushSelector(MegaCrit.Sts2.Core.TestSupport.ICardSelector selector)
    {
        MethodInfo? push = typeof(CardSelectCmd).GetMethods().FirstOrDefault(m => m.Name == "PushSelector");
        if (push != null)
        {
            object?[] args = push.GetParameters().Select((p, i) => i == 0 ? selector : p.Name == "localOnly" ? _localSelector : p.DefaultValue).ToArray();
            return (IDisposable)push.Invoke(null, args)!;
        }
        if (_localSelector)
        {
            return RunLocalSelector.Supported ? new RunLocalSelector(selector)
                : throw new NotSupportedException("this game version has no local-only card selector");
        }
        return SelectorStack.Push(selector);
    }

    /// <summary>
    /// Game v0.113's local-only selector (<c>CardSelectorStack.UseLocalSelector</c>: it answers this player's own choices, sent to
    /// the others like a human's). Each run has its own stack: installed on the run in progress, or on the next run once it
    /// exists (<c>mplobby</c> asks before the run starts). Disposing it (tomenu, another selector line) takes it off.
    /// </summary>
    private sealed class RunLocalSelector : IDisposable
    {
        private static readonly PropertyInfo? StackProperty = AccessTools.Property(typeof(RunManager), "CardSelectorStack");
        private static readonly MethodInfo? UseLocal = StackProperty == null ? null : AccessTools.Method(StackProperty.PropertyType, "UseLocalSelector");

        public static bool Supported => UseLocal != null;

        private readonly MegaCrit.Sts2.Core.TestSupport.ICardSelector _selector;
        /// <summary>The stack of a run that was there when asked but not in progress (left): not the one to install on.</summary>
        private readonly object? _staleStack;
        private IDisposable? _installed;
        private bool _waiting, _disposed;

        public RunLocalSelector(MegaCrit.Sts2.Core.TestSupport.ICardSelector selector)
        {
            _selector = selector;
            if (CurrentStack() is { } stack && RunManager.Instance.IsInProgress && NRun.Instance != null)
            {
                Install(stack);
                return;
            }
            _staleStack = StackProperty!.GetValue(RunManager.Instance);
            _waiting = true;
            ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Poll;
            Say("  local card selector: installed once the run starts");
        }

        private static object? CurrentStack() => RunManager.Instance.IsInProgress ? StackProperty!.GetValue(RunManager.Instance) : null;

        private void Poll()
        {
            if (_disposed || CurrentStack() is not { } stack || ReferenceEquals(stack, _staleStack) || NRun.Instance == null)
            {
                return;
            }
            StopWaiting();
            try
            {
                Install(stack);
            }
            catch (Exception e)
            {
                Say($"FAILED: local card selector not installed on the run: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
            }
        }

        private void Install(object stack)
        {
            _installed = (IDisposable)UseLocal!.Invoke(stack, new object[] { _selector })!;
            Say("  local card selector installed on the run");
        }

        private void StopWaiting()
        {
            if (_waiting)
            {
                _waiting = false;
                ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= Poll;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopWaiting();
            _installed?.Dispose();
            _installed = null;
        }
    }

    /// <summary>
    /// Game v0.113: the test tooling's own stack of selectors, answering through the game's one static test selector,
    /// set while the stack holds any (another test selector set meanwhile, e.g. AutoSlay's, is suspended until then). A
    /// scope removes its own selector wherever it is, as the old global stack's scopes did.
    /// </summary>
    private sealed class SelectorStack : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        private static readonly SelectorStack Instance = new();
        private readonly List<MegaCrit.Sts2.Core.TestSupport.ICardSelector> _items = new();
        private IDisposable? _registered;

        public static IDisposable Push(MegaCrit.Sts2.Core.TestSupport.ICardSelector selector)
        {
            if (Instance._items.Count == 0)
            {
                Type stack = AccessTools.TypeByName("MegaCrit.Sts2.Core.CardSelection.CardSelectorStack") ?? throw new MissingMethodException("CardSelectCmd", "PushSelector");
                MethodInfo set = AccessTools.Method(stack, "SetTestSelector") ?? throw new MissingMethodException(stack.Name, "SetTestSelector");
                MethodInfo suspend = AccessTools.Method(stack, "SuspendTestSelector") ?? throw new MissingMethodException(stack.Name, "SuspendTestSelector");
                bool already = AccessTools.Field(stack, "_testSelector")?.GetValue(null) != null;
                IDisposable? suspended = already ? (IDisposable)suspend.Invoke(null, null)! : null;
                IDisposable scope = (IDisposable)set.Invoke(null, new object[] { Instance })!;
                Instance._registered = new Scopes(scope, suspended);
            }
            Instance._items.Add(selector);
            return new Scopes(new Removal(selector));
        }

        private sealed class Removal(MegaCrit.Sts2.Core.TestSupport.ICardSelector selector) : IDisposable
        {
            public void Dispose()
            {
                int at = Instance._items.LastIndexOf(selector);
                if (at < 0)
                {
                    return;
                }
                Instance._items.RemoveAt(at);
                if (Instance._items.Count == 0)
                {
                    Instance._registered?.Dispose();
                    Instance._registered = null;
                }
            }
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
            _items[^1].GetSelectedCards(options, minSelect, maxSelect);

        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives) =>
            _items[^1].GetSelectedCardReward(options, alternatives);
    }

    /// <summary>Disposes its scopes in order, once.</summary>
    private sealed class Scopes : IDisposable
    {
        private IDisposable?[]? _scopes;

        public Scopes(params IDisposable?[] scopes) => _scopes = scopes;

        public void Dispose()
        {
            IDisposable?[]? scopes = _scopes;
            _scopes = null;
            foreach (IDisposable? scope in scopes ?? Array.Empty<IDisposable?>())
            {
                scope?.Dispose();
            }
        }
    }

    /// <summary>
    /// Script <c>selector local</c> (before newrun/mplobby): the automatic card selector only answers this player's
    /// own choices, synchronized like a human's (game v0.111's localOnly selector). The default, global selector also
    /// answers the other players' choices locally: fine when every peer runs a script (they all pick the same cards),
    /// a divergence when a human plays the other side (hands-on co-op, cooptry).
    /// </summary>
    private static bool _localSelector;

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
        _combatOpen = true;
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

    /// <summary>Cards the run holds on to (<c>RunState._allCards</c>), for gcfull's line: what a co-op sync replaced stays there in the game.</summary>
    private static string RunCardCount()
    {
        object? run = RunManager.Instance.DebugOnlyGetState();
        return run == null ? "" : $", run cards {(HarmonyLib.AccessTools.Field(run.GetType(), "_allCards")?.GetValue(run) as System.Collections.ICollection)?.Count}";
    }

    private static void Say(string text) => Log.Info($"[{ModEntry.Id}] {Name} {_clock.Elapsed.TotalSeconds,7:F1}s {text}");

    private static bool _continueOnFail, _turnLoopDied, _coopDiverged;
    private static ushort _port;

    private static void UseTestPort(ref ushort port) => port = _port;

    /// <summary>Launched with <c>--bdoff</c>; <see cref="_launchOffDone"/>: the mod was turned off by it.</summary>
    private static bool _launchedOff, _launchOffDone;

    /// <summary>
    /// The main menu reads its <c>--fastmp</c> argument (hosting or joining at once): the whole mod goes off first, for this
    /// session only (never saved: BaseLib saves the player's choice, <see cref="Config.Settings.MasterChoice"/>), as a script's
    /// <c>cmd bdopt off</c> would but before any co-op lobby exists. Every patch still applies (not a start with the mod off,
    /// <see cref="ModEntry.StartedOff"/>, which applies none): the same mod-off state as <c>bdopt off</c>.
    /// </summary>
    private static void LaunchOffPrefix()
    {
        if (_launchOffDone)
        {
            return;
        }
        _launchOffDone = true;
        Config.Settings.SetMaster(false, notify: false, sessionOnly: true);
        Say("  mod off from the launch (--bdoff), before the main menu's co-op lobby");
    }

    /// <summary>
    /// Script <c>launchoff</c>: this scenario needs the mod off from its launch (the launch scripts add <c>--bdoff</c> when the
    /// scenario has this line): fails unless it is, so a mod-off control never runs with the mod on.
    /// </summary>
    private static void RequireLaunchedOff()
    {
        if (!_launchedOff || !_launchOffDone)
        {
            throw new InvalidOperationException(_launchedOff
                ? "launched with --bdoff, but the main menu never read its arguments: the mod wasn't turned off"
                : "this scenario must be launched with --bdoff (parcoop.ps1, coop.ps1, coop4.ps1 and par.ps1 add it for a 'launchoff' line)");
        }
        if (Config.Settings.Master)
        {
            throw new InvalidOperationException("launched with --bdoff, but the mod is on (a script line turned it back on?)");
        }
        Say("  LAUNCHOFF the mod is off since the launch");
    }

    /// <summary>
    /// Script <c>coopmodetag on|off|exactsnap</c>: this mod's entry in the co-op join list the game uses now (v0.111+: the lobby's net
    /// service's own, kept since hosting or joining started; v0.107.1: the game's list as built at each connection) must say
    /// that state ("+off", "+exactsnap" or neither). Logs it with <see cref="CoopModeTag.Tagged"/>.
    /// </summary>
    private static void CheckCoopModeTag(string arg)
    {
        string wanted = arg.Trim();
        if (wanted is not ("on" or "off" or "exactsnap" or "exactsnap-c" or "exactsnap-r"))
        {
            throw new ArgumentException("coopmodetag on|off|exactsnap|exactsnap-c|exactsnap-r");
        }
        List<string>? list = null;
        string source = "the game's list now";
        // The run's net service once it exists (the lobby's own, kept), else the lobby screen's.
        object? net = RunManager.Instance.IsInProgress ? RunManager.Instance.NetService : _lobbyScreen?.Lobby?.NetService;
        if (net != null && AccessTools.Property(net.GetType(), "LocalVersion") is { } localVersion)
        {
            object? info = localVersion.GetValue(net);
            list = info == null ? null : AccessTools.Field(info.GetType(), "gameplayAffectingMods")?.GetValue(info) as List<string>;
            source = "the net service's handshake list";
        }
        list ??= MegaCrit.Sts2.Core.Modding.ModManager.GetGameplayRelevantModNameList();
        string? entry = list?.FirstOrDefault(e => e != null && e.StartsWith(ModEntry.Id + "-", StringComparison.Ordinal));
        Say($"  COOPMODETAG {entry ?? "(no entry)"} in {source}, tagged {CoopModeTag.Tagged} times");
        if (entry == null)
        {
            throw new InvalidOperationException($"no {ModEntry.Id} entry in {source}");
        }
        string says = entry.EndsWith(CoopModeTag.OffSuffix, StringComparison.Ordinal) ? "off"
            : entry.EndsWith(CoopModeTag.ExactSnapSuffix + "-c", StringComparison.Ordinal) ? "exactsnap-c"
            : entry.EndsWith(CoopModeTag.ExactSnapSuffix + "-r", StringComparison.Ordinal) ? "exactsnap-r"
            : entry.EndsWith(CoopModeTag.ExactSnapSuffix, StringComparison.Ordinal) ? "exactsnap" : "on";
        if (says != wanted)
        {
            throw new InvalidOperationException($"{source} says {says}, expected {wanted}: {entry}");
        }
    }

    /// <summary>
    /// A fakeconflict target, <c>Namespace.Type[+Nested]:Method</c>, in the game or any loaded mod (Limitless, BaseLib): an
    /// overloaded name takes its first declared overload (said in the line), <c>Method#n</c> the overload with n parameters,
    /// <c>Method(IPoolable)</c> the one with those parameter types (short names),
    /// <c>Prefix*</c> the one method whose name starts so (a compiler-named lambda: <c>MultiEnchantment+&lt;&gt;c:&lt;DeepCloneFields&gt;*</c>).
    /// </summary>
    private static MethodInfo ConflictTarget(string spec)
    {
        int colon = spec.LastIndexOf(':');
        if (colon <= 0)
        {
            throw new ArgumentException("fakeconflict <Namespace.Type:Method> [boolprefix]");
        }
        Type type = AccessTools.TypeByName(spec[..colon]) ?? throw new InvalidOperationException("no type " + spec[..colon] + " (its mod not loaded?)");
        string name = spec[(colon + 1)..];
        string[]? parameterTypes = null;
        if (name.IndexOf('(') is int open and > 0 && name.EndsWith(')'))
        {
            // Method(TypeName,TypeName): the overload whose parameter types have these short names.
            parameterTypes = name[(open + 1)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            name = name[..open];
        }
        int? arity = null;
        if (name.LastIndexOf('#') is int hash and > 0 && int.TryParse(name[(hash + 1)..], out int n))
        {
            arity = n;
            name = name[..hash];
        }
        bool prefix = name.EndsWith('*');
        string wanted = prefix ? name[..^1] : name;
        MethodInfo[] found = AccessTools.GetDeclaredMethods(type)
            .Where(m => (prefix ? m.Name.StartsWith(wanted, StringComparison.Ordinal) : m.Name == wanted) && (arity == null || m.GetParameters().Length == arity)
                && (parameterTypes == null || m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(parameterTypes)))
            .ToArray();
        if (found.Length == 0)
        {
            throw new InvalidOperationException("no method " + spec);
        }
        if (found.Length > 1 && (prefix || arity != null || parameterTypes != null))
        {
            throw new InvalidOperationException($"{spec} names {found.Length} methods: {string.Join(", ", found.Select(m => m.Name + "#" + m.GetParameters().Length))}");
        }
        if (found.Length > 1)
        {
            Say($"  {spec}: {found.Length} overloads, the first declared taken ({found[0].GetParameters().Length} parameters; Method#n picks one)");
        }
        return found[0];
    }

    /// <summary>
    /// Script <c>requiregame v0.113</c>: the rest of the scenario needs this game version or a later one (a patch or mode only it
    /// has): on an earlier one the line logs SKIPPED and the script ends as finished (<see cref="SkipRest"/>).
    /// </summary>
    private static void RequireGame(string wanted)
    {
        string? running = Reflect.GameVersion();
        if (!Version.TryParse(wanted.TrimStart('v'), out Version? need))
        {
            throw new ArgumentException("requiregame v<major.minor[.patch]>");
        }
        if (running == null || !Version.TryParse(running.TrimStart('v').Split('-', '+', ' ')[0], out Version? have))
        {
            throw new InvalidOperationException($"the game's version is unknown ({running ?? "none"}): can't tell whether {wanted} is met");
        }
        if (have < need)
        {
            SkipRest($"game {running} is older than {wanted}");
            return;
        }
        Say($"  game {running}: {wanted} or later");
    }

    /// <summary>
    /// Script <c>runtimebackoff &lt;Patch&gt; &lt;Type.Method&gt; [none]</c>: the optimization's own runtime check backed off from that
    /// method (another mod's patch there), counted apart from the preflight bdopt and the menu run
    /// (<see cref="PatchSafety.RuntimeCounts"/>); with <c>none</c>, that it never did (a preflight-only session).
    /// </summary>
    private static void CheckRuntimeBackOff(string arg)
    {
        string[] args = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "none"))
        {
            throw new ArgumentException("runtimebackoff <Patch> <Type.Method> [none]");
        }
        (long kept, long backedOff) = PatchSafety.RuntimeCounts(args[0], args[1]);
        Say($"  RUNTIMEBACKOFF {args[0]} {args[1]}: backed off {backedOff}, kept {kept}");
        if (args.Length == 3 ? backedOff != 0 : backedOff == 0)
        {
            throw new InvalidOperationException(args.Length == 3
                ? $"{args[0]}'s runtime check backed off from {args[1]} {backedOff} times, expected none"
                : $"{args[0]}'s runtime check never backed off from {args[1]} (only the preflight may have reported it)");
        }
    }

    /// <summary>
    /// The combat's state as each turn starts: the same point of the run whatever the session's speed, on both co-op peers too. A
    /// script's <c>state</c> line comes when that session gets there, maybe after the other peer's plays of the turn.
    /// </summary>
    private static void LogTurnStates()
    {
        CombatManager.Instance.TurnStarted += combat =>
            LogState($"floor {RunManager.Instance.DebugOnlyGetState()?.TotalFloor} round {combat.RoundNumber} {combat.CurrentSide}");
    }

    private static readonly List<string> _autoSlayFirst = new();
    private static AccessTools.FieldRef<object, DateTime>? _watchdogProgress;
    private static string? _autoSlaySeed;

    /// <summary>
    /// AutoSlay gives up after 30 s on a fight or without progress, 2 minutes on a room, 25 on a run: too short with the mod off
    /// or a big deck, and then the run stops at a point that depends on the session's speed.
    /// </summary>
    private static bool _autoSlayLimitsPatched;

    private static void PatchAutoSlayLimits()
    {
        if (_autoSlayLimitsPatched)
        {
            return;
        }
        _autoSlayLimitsPatched = true;
        BenchPatches(PatchAutoSlayLimitsNow);
    }

    private static void PatchAutoSlayLimitsNow()
    {
        foreach (MethodInfo until in AccessTools.GetDeclaredMethods(typeof(MegaCrit.Sts2.Core.AutoSlay.Helpers.WaitHelper)).Where(m => m.Name == "Until" && m.GetParameters().Any(p => p.Name == "timeout")))
        {
            bool lazyMessage = until.GetParameters().Last().ParameterType != typeof(string);
            PatchGuard.Patch(ModEntry.Harmony, until, prefix: new HarmonyMethod(typeof(AutoTest), lazyMessage ? nameof(LongerAutoSlayWaitLazyMessage) : nameof(LongerAutoSlayWait)));
        }
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.AutoSlay.Helpers.WaitHelper), "WithTimeout"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(LongerAutoSlayRoom)));
        // v0.113's map handler waits for the room through ForTask (30 s): a combat start with the mod off at ~2,000 cards is longer.
        if (AccessTools.Method(typeof(MegaCrit.Sts2.Core.AutoSlay.Helpers.WaitHelper), "ForTask") is { } forTask)
        {
            PatchGuard.Patch(ModEntry.Harmony, forTask, prefix: new HarmonyMethod(typeof(AutoTest), nameof(LongerAutoSlayTask)));
        }
        // The character screen clears the seed AutoSlay asked for (public version), and the run takes one from the clock, in seconds:
        // only sessions started together played the same run.
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Helpers.SeedHelper), nameof(MegaCrit.Sts2.Core.Helpers.SeedHelper.GetRandomSeed)), prefix: new HarmonyMethod(typeof(AutoTest), nameof(AutoSlayRunSeed)));
        foreach (MethodInfo handler in AccessTools.GetDeclaredMethods(typeof(MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms.CombatRoomHandler)).Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)))
        {
            PatchGuard.Patch(ModEntry.Harmony, AccessTools.AsyncMoveNext(handler), transpiler: new HarmonyMethod(typeof(AutoTest), nameof(AutoSlayWaitsForActions)));
        }
        _watchdogProgress = AccessTools.FieldRefAccess<DateTime>(typeof(MegaCrit.Sts2.Core.AutoSlay.Helpers.Watchdog), "_lastProgressTime");
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.AutoSlay.Helpers.Watchdog), "Check"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(LongerAutoSlayWatchdog)));
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.AutoSlay.AutoSlayer), "HandleRoomAsync"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(AutoSlayEntersRoom)));
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.AutoSlay.Helpers.Watchdog), "Reset"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(AutoSlayPhase)));
    }

    private static string? _phase;
    private static long _phaseStart, _phaseCpu, _phaseBytes;
    private static TimeSpan _phasePause;

    /// <summary>
    /// AutoSlay names what it's doing at each step (a room, a screen, the map, each card played): a <c>PHASE</c> line for the step
    /// that ends, whatever the session's speed. <c>tools/autotest/phasereport.py</c> adds them up by kind of step.
    /// </summary>
    private static void AutoSlayPhase(string activity)
    {
        if (_phase != null && _frameMs.Count > 0)
        {
            double wall = Stopwatch.GetElapsedTime(_phaseStart).TotalMilliseconds;
            double cpu = (MainThreadCpuTicks() - _phaseCpu) / 10000.0;
            double mb = (GC.GetTotalAllocatedBytes(false) - _phaseBytes) / (1024.0 * 1024);
            double gc = (GC.GetTotalPauseDuration() - _phasePause).TotalMilliseconds;
            string frames = string.Join(",", _frameMs.Where(ms => ms > 8.33).Select(ms => ms.ToString("F0")));
            Say($"  PHASE {_phase} | wall {wall:F0} frames {_frameMs.Count} worst {_frameMs.Max():F0} gc {gc:F0} mb {mb:F1} cpu {cpu:F0} long {frames}");
        }
        _recordFrames = true;
        _frameMs.Clear();
        _phase = activity;
        _phaseStart = Stopwatch.GetTimestamp();
        _phaseCpu = MainThreadCpuTicks();
        _phaseBytes = GC.GetTotalAllocatedBytes(false);
        _phasePause = GC.GetTotalPauseDuration();
    }

    // Its limits are only guards against a stuck game: no caller goes on after one. A fixed value, as one overload calls the other on
    // the beta. Not the end of the run: with Limitless neither the game over screen nor the main menu comes (its last event goes on),
    // and 30 s are enough.
    private static void LongerAutoSlayTask(ref TimeSpan? timeout) => timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Set while the tooling's own code (autoroom's screens) calls <c>WaitHelper.Until</c> with its own short limit: those are left
    /// as they are, so a screen that never fills fails the line at once with its message instead of a silent wait until the runner's
    /// stall kill. Read synchronously at the call (both overloads' prefixes run before its first await).
    /// </summary>
    [ThreadStatic] private static bool _ownWait;

    /// <summary><c>WaitHelper.Until</c> with this limit kept (not AutoSlay's 10 minutes).</summary>
    private static Task OwnWait(Func<bool> condition, System.Threading.CancellationToken ct, double seconds, string timeoutMessage)
    {
        _ownWait = true;
        try
        {
            return MegaCrit.Sts2.Core.AutoSlay.Helpers.WaitHelper.Until(condition, ct, TimeSpan.FromSeconds(seconds), timeoutMessage);
        }
        finally
        {
            _ownWait = false;
        }
    }

    private static void LongerAutoSlayWait(ref Func<bool> condition, ref TimeSpan? timeout, string? timeoutMessage)
    {
        if (_ownWait)
        {
            return;
        }
        timeout = timeoutMessage != null && (timeoutMessage.StartsWith("Main menu") || timeoutMessage.StartsWith("Game over")) ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10);
        if (timeoutMessage == "Play phase not started")
        {
            // After ending a turn it goes straight to its next one while the phase is still Play: with nothing playable, its limit of
            // 100 turns is gone in a moment, however fast the session, and nobody plays the fight any more ("Combat did not end").
            Func<bool> started = condition;
            condition = () => !CombatManager.Instance.IsInProgress || (started() && (Me() is not { } me || !CombatManager.Instance.IsPlayerReadyToEndTurn(me)));
        }
    }

    private static void LongerAutoSlayWaitLazyMessage(ref Func<bool> condition, ref TimeSpan? timeout, Func<string>? timeoutMessage)
    {
        if (!_ownWait)
        {
            LongerAutoSlayWait(ref condition, ref timeout, timeoutMessage?.Invoke());
        }
    }

    // Per room and for the whole run: the scenario's own timeout is the limit that counts.
    private static void LongerAutoSlayRoom(ref TimeSpan timeout) => timeout = TimeSpan.FromHours(3);

    private static bool LongerAutoSlayWatchdog(object __instance) => DateTime.UtcNow - _watchdogProgress!(__instance) > TimeSpan.FromMinutes(10);

    /// <summary>
    /// In a fight it waits a fixed time after a potion or a card, then picks among what is playable: a potion not finished by then
    /// (Snecko Oil draws and changes costs) makes another pick, on a session's speed.
    /// </summary>
    private static IEnumerable<CodeInstruction> AutoSlayWaitsForActions(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo delay = AccessTools.Method(typeof(Task), nameof(Task.Delay), new[] { typeof(int), typeof(System.Threading.CancellationToken) });
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(delay))
            {
                instruction.operand = AccessTools.Method(typeof(AutoTest), nameof(DelayThenActionsDone));
            }
            yield return instruction;
        }
    }

    private static async Task DelayThenActionsDone(int milliseconds, System.Threading.CancellationToken token)
    {
        await Task.Delay(milliseconds, token);
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        for (int calm = 0; calm < 2; calm = RunManager.Instance.ActionExecutor.IsRunning || !RunManager.Instance.ActionQueueSet.IsEmpty ? 0 : calm + 1)
        {
            token.ThrowIfCancellationRequested();
            await Frames(tree, 1);
        }
    }

    private static bool AutoSlayRunSeed(ref string __result)
    {
        if (_autoSlaySeed == null)
        {
            return true; // autoroom's combats: no autoslay run, the game's own seed.
        }
        __result = _autoSlaySeed;
        return false;
    }

    private static void AutoSlayEntersRoom()
    {
        foreach (string line in _autoSlayFirst)
        {
            Say("> (first room) " + line);
            FirstRoomCommand(line);
        }
        _autoSlayFirst.Clear();
    }

    /// <summary>An <c>autoslayfirst</c> command: it fails the session as a script line would (nothing awaits it).</summary>
    private static async void FirstRoomCommand(string line)
    {
        try
        {
            await Command(line);
        }
        catch (Exception e)
        {
            Say($"FAILED line 'autoslayfirst {line}': {e.Message}");
            if (!_continueOnFail)
            {
                // The autoslay line awaiting this room is the one running: it and what follows it are left.
                Say($"ABORT: first failed line (a scenario expecting failures says 'onfail continue') [lines left {LinesLeft(_scriptLine)}]");
                Quit();
            }
        }
    }

    private static async Task Run(SceneTree tree)
    {
        // The game's errors only (v0.111+): not its Debug line on a normal teardown ("turn loop died of cancellation"), and not this
        // tooling's own FAILED line quoting a wait's message, which would set the flag again and fail every later wait at once.
        Log.LogCallback += (level, text, _) =>
        {
            if (level != MegaCrit.Sts2.Core.Logging.LogLevel.Error || text.Contains($"[{ModEntry.Id}]"))
            {
                return;
            }
            // A combat's turn loop threw: the combat stays stuck, no wait on it can end.
            _turnLoopDied |= text.Contains("turn loop died while its combat is in progress");
            // The client then leaves for the main menu and the host goes on alone: no wait on the other peer can end.
            _coopDiverged |= text.Contains("State divergence message received");
        };
        CombatManager.Instance.CombatEnded += _ => _combatOpen = false;
        string[] lines = File.ReadAllLines(_script!);
        _scriptLines = lines;
        Say($"script {_script} ({lines.Length} lines)");
        // Every expect line of the script, before any runs: one an ABORT leaves unrun is still known (tools/autotest/judge.py).
        foreach (string expectLine in lines.Select(l => l.Trim()).Where(l => l.StartsWith("expect ", StringComparison.OrdinalIgnoreCase)))
        {
            Say("  EXPECTDECL " + expectLine["expect ".Length..].Trim());
        }
        _handSelects = lines.Count(l => l.TrimStart().StartsWith("handselect ", StringComparison.OrdinalIgnoreCase));
        foreach (string effectLine in lines.Select(l => l.Trim()).Where(l => l.StartsWith("effectcheck ", StringComparison.OrdinalIgnoreCase)))
        {
            _optIn.Add(effectLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        }
        await Until(tree, () => NGame.Instance?.MainMenu != null && ModEntry.Ready, 300, "menu");
        await Frames(tree, 60);
        if (!await WaitCommonPreload(tree))
        {
            // The game's loader stopped (seen under a full machine: a run started then never finishes its own preload). Nothing
            // can go on: quit at once, the runner starts the session over (parlib.ps1, Get-EarlyExitProblem) instead of a stall.
            Say("RESTART: the game's Common preload stopped loading: the session starts over");
            Quit();
            return;
        }
        NGame game = NGame.Instance!;
        if (DisplayServer.GetName() == "headless")
        {
            // Nothing to show: only the game's own fps setting would slow the session.
            Engine.MaxFps = 0;
        }
        tree.ProcessFrame += RecordFrame;
        Say($"  display: max fps {Engine.MaxFps} (0 = uncapped), vsync {DisplayServer.WindowGetVsyncMode()}");
        // Test profiles start at Normal speed; players with huge decks play in Fast.
        SetSpeed(FastModeType.Fast);
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string raw = lines[lineIndex];
            _scriptLine = lineIndex;
            // {hash:NAME} = hash of the shared run file NAME (co-op imports need it; each machine computes its own).
            string? missingShared = null;
            string line = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"\{hash:([\w.-]+)\}",
                m => BigDeckConsoleCmd.SharedHash(m.Groups[1].Value) ?? (missingShared = m.Groups[1].Value));
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (_skipRest)
            {
                break;
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
            if (missingShared != null)
            {
                throw new FileNotFoundException($"no shared run file '{missingShared}' in user://bigdeck for {{hash:{missingShared}}}");
            }
            ThrowIfNetCommandFailed();
            switch (parts[0].ToLowerInvariant())
            {
                case "expect":
                    // A positive control: the problem lines this regex matches are expected (tools/autotest/judge.py lists them
                    // apart), and a line of the session must match it, or the control saw nothing.
                    _ = new System.Text.RegularExpressions.Regex(arg.Trim());
                    Say("  EXPECT " + arg.Trim());
                    break;
                case "enemyhp":
                    RequireNoCombatYetInCoop(line);
                    _enemyHp = int.Parse(arg);
                    break;
                case "immortal":
                    RequireNoCombatYetInCoop(line);
                    string[] immortalArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    _immortal = immortalArgs.FirstOrDefault() != "off";
                    _immortalUntilRound = immortalArgs.Length > 1 && immortalArgs[0] == "until" ? int.Parse(immortalArgs[1]) : 0;
                    Say($"  enemies immortal: {_immortal}{(_immortalUntilRound > 0 ? $" until round {_immortalUntilRound}" : "")}");
                    break;
                case "state":
                    // Outside a combat it compares nothing (both sides of an A/B log "no combat"): the line then fails unless "optional"
                    // (StateOutsideCombatFails).
                    List<string> stateArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool optionalState = stateArgs.Remove("optional");
                    LogState(string.Join(" ", stateArgs), optionalState);
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
                    // Another "mod" patching a game method (no-op postfix under another Harmony id, or with "boolprefix" a prefix
                    // returning true at Priority.Last, installed after this mod's patches so sorted after every prefix of this mod,
                    // HookFilter's mirrors at Priority.Last included: no tolerance lets that stand, BeforeReplace included, for a
                    // method whose checks let postfixes stand): the
                    // optimization using it must back off and report it. Other shapes: foreignpatch (ForeignPatches).
                    string[] conflictArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    MethodInfo conflictTarget = ConflictTarget(conflictArgs[0]);
                    bool boolPrefix = conflictArgs.Length > 1 && conflictArgs[1] == "boolprefix";
                    if (boolPrefix)
                    {
                        new Harmony("bdtest.fakeconflict").Patch(conflictTarget, prefix: new HarmonyMethod(typeof(AutoTest), nameof(TruePrefix)) { priority = Priority.Last });
                    }
                    else
                    {
                        new Harmony("bdtest.fakeconflict").Patch(conflictTarget, postfix: new HarmonyMethod(typeof(AutoTest), nameof(NoOpPostfix)));
                    }
                    Say($"  patched {conflictTarget.DeclaringType?.Name}.{conflictTarget.Name} as bdtest.fakeconflict ({(boolPrefix ? "bool prefix" : "postfix")})");
                    break;
                case "foreignpatch":
                    // Synthetic patches of each shape under a foreign Harmony id, and PatchSafety's verdict on them (ForeignPatches).
                    // One changing this peer's game state, once its combat is set up in co-op, would make the peers differ.
                    if (ForeignPatches.ChangesState(arg))
                    {
                        RequireNoCombatYetInCoop(line);
                    }
                    foreach (string foreignLine in ForeignPatches.Run(arg))
                    {
                        Say(foreignLine.StartsWith("STATE ", StringComparison.Ordinal) ? foreignLine : "  " + foreignLine);
                    }
                    break;
                case "launchoff":
                    RequireLaunchedOff();
                    break;
                case "launchforeign":
                    Say("  " + ForeignLaunch.Require(arg));
                    break;
                case "coopmodetag":
                    CheckCoopModeTag(arg);
                    break;
                case "runtimebackoff":
                    CheckRuntimeBackOff(arg);
                    break;
                case "requiregame":
                    RequireGame(arg.Trim());
                    break;
                case "selector" when arg.Trim() is "ui" or "auto":
                    // ui: no automatic selector, the game opens its card choice screens (deck grids) and autoroom's handlers answer them.
                    _selector?.Dispose();
                    _selector = arg.Trim() == "auto" ? PushSelector(new FirstCardsSelector()) : null;
                    _uiSelection = _selector == null;
                    if (_uiSelection)
                    {
                        WatchCombatsForSelector();
                    }
                    Say($"  card choices: {(_uiSelection ? "the game's screens" : "automatic selector")}");
                    break;
                case "autoroom":
                    await AutoRoom(tree, arg);
                    break;
                case "automap":
                    await AutoMap(tree, arg);
                    break;
                case "runstate":
                    await LogRunState(tree, arg.Trim());
                    break;
                case "barrier":
                    await Barrier(tree, arg);
                    break;
                case "continuerun":
                    await ContinueRun(tree, arg);
                    break;
                case "selector":
                    _localSelector = arg.Trim() == "local";
                    // v0.111: CardSelectCmd.PushSelector's localOnly; v0.113: the run's CardSelectorStack.UseLocalSelector.
                    bool supported = typeof(CardSelectCmd).GetMethods().Any(m => m.Name == "PushSelector" && m.GetParameters().Any(p => p.Name == "localOnly"))
                        || RunLocalSelector.Supported;
                    if (_localSelector && !supported)
                    {
                        throw new NotSupportedException("this game version has no local-only card selector");
                    }
                    Say($"  card selector: {(_localSelector ? "local player only" : "global")}");
                    break;
                case "faultprobe":
                    // Its patches go on before its first await: the tooling's, not the optimizations' (InlineGuard effect).
                    Task<string>? probeRun = null;
                    BenchPatches(() => probeRun = FaultProbe.Run(() => Frames(tree, 3)));
                    string probe = await probeRun!;
                    Say("  FAULTPROBE " + probe);
                    if (probe.Contains("FAILED"))
                    {
                        throw new InvalidOperationException("a PatchGuard check failed: " + probe);
                    }
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
                case "gcpacing":
                    GcPacing.Enabled = arg.Trim() != "off";
                    break;
                case "uncap":
                    // Frame times above the screen's refresh rate: what a 120+ fps target needs to see.
                    Engine.MaxFps = 0;
                    DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
                    Say($"  display: max fps {Engine.MaxFps}, vsync {DisplayServer.WindowGetVsyncMode()}");
                    break;
                case "sweep":
                    if (!await Sweep(tree, arg))
                    {
                        // Aborted (SWEEP ABORT, quitting): the lines after it would log for a state nothing after the failure means.
                        return;
                    }
                    break;
                case "sweeptimeout":
                    SweepItemTimeout = double.Parse(arg);
                    break;
                case "pilemove":
                    string[] moveArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    PileMove(moveArgs[0], moveArgs[1], moveArgs.Length > 2 ? int.Parse(moveArgs[2]) : 0);
                    break;
                case "gcfull":
                    // Live memory after a full blocking collection: comparable between sessions, shows leaks.
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    Say($"  GCFULL {arg.Trim()}: live heap {GC.GetTotalMemory(false) / (1024.0 * 1024):F1} MB"
                        + (_leftRuns.Count == 0 ? "" : $", left runs alive {_leftRuns.Count(r => r.IsAlive)}/{_leftRuns.Count}") + RunCardCount());
                    break;
                case "autoslayfirst":
                    // A console command run as the auto player enters its first room: at the same point whatever the session's
                    // speed (a deck imported from the script would land at a different moment, and change the run).
                    _autoSlayFirst.Add(arg);
                    break;
                case "autoslay":
                    // The game's own auto player: a whole seeded run from the main menu, then it closes the game. Replaces newrun.
                    // "state": the combat's state at each turn start, for the A/B to compare more than the cards played.
                    string[] slayArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    PatchAutoSlayLimits();
                    _autoSlaySeed = slayArgs[0];
                    if (slayArgs.Contains("state"))
                    {
                        LogTurnStates();
                    }
                    // autoslay <seed> [state] [dread|blitz]: the game's mode (v0.113+; skipped where its auto player has none).
                    string? slayMode = slayArgs.Skip(1).FirstOrDefault(a => a is not "state");
                    if (slayMode != null && ParseGameMode(slayMode) is null)
                    {
                        SkipRest($"game {Reflect.GameVersion() ?? "version unknown"} has no {slayMode} mode");
                        break;
                    }
                    if (!StartAutoSlayer(slayArgs[0], slayMode?.ToLowerInvariant()))
                    {
                        SkipRest($"the auto player of game {Reflect.GameVersion() ?? "version unknown"} has no mode");
                        break;
                    }
                    _modeRun |= slayMode != null && !slayMode.Equals("standard", StringComparison.OrdinalIgnoreCase);
                    break;
                case "dominion":
                    LogDominion(arg.Trim());
                    break;
                case "tomenu":
                    // Leaves the run as the game does; the next gcfull says how many of the left runs something still holds.
                    _leftRuns.Add(new WeakReference(RunManager.Instance.DebugOnlyGetState()));
                    await game.ReturnToMainMenu();
                    // CleanUp emptied the game's selector stack (before v0.113; v0.113's test selector outlives the run): drop
                    // ours either way. newrun must push one again, or the next card choice waits forever.
                    _selector?.Dispose();
                    _combatSelector?.Dispose();
                    _selector = null;
                    _combatSelector = null;
                    _combatOpen = false; // A combat left without its end.
                    // Its cards hold the left run: leak checks and gcfull must see only what the game and the mod keep.
                    _manualPlaysStarted.Clear();
                    Say("  on the main menu");
                    break;
                case "leakcheck":
                    LeakCheck(arg.Trim());
                    break;
                case "fpsstat":
                    FpsStat(arg.Trim());
                    break;
                case "framedetail":
                    FrameDetailOn(tree);
                    break;
                case "forcefocus":
                    BenchPatches(ForceFocus);
                    break;
                case "controllermap" when arg.Trim() == "stale":
                    ControllerMapStale();
                    break;
                case "controllermap":
                    ControllerMapDefault();
                    break;
                case "buttoninput":
                    ButtonInput(tree, arg.Trim());
                    break;
                case "mousesweep":
                    await MouseSweep(tree, double.Parse(arg));
                    break;
                case "mouseplay":
                    await MousePlay(tree, int.Parse(arg));
                    break;
                case "hover":
                    Hover(int.Parse(arg));
                    break;
                case "eventtrace":
                    BenchPatches(EventTrace);
                    break;
                case "checktips":
                    CheckTips(arg.Trim());
                    break;
                case "tipsrebuilt":
                    TipsRebuilt(arg.Trim());
                    break;
                case "movehovered":
                    MoveHovered(arg.Trim());
                    break;
                case "menuvalues":
                    // What BaseLib's menu shows (each box reads its property), by reflection: this DLL must not reference BaseLib.
                    Type? menu = AccessTools.TypeByName("BigDeckOptimization.Config.BaseLibSettings");
                    Say(menu == null ? "  MENU: not loaded" : "  MENU " + arg.Trim() + ": " + string.Join(", ", menu.GetProperties(BindingFlags.Public | BindingFlags.Static)
                        .Where(p => p.PropertyType == typeof(bool)).Select(p => $"{p.Name}={p.GetValue(null)}")));
                    break;
                case "counters":
                    Say($"  COUNTERS {arg.Trim()}: {LimitlessReserialize.Name} kept {LimitlessReserialize.Kept}, rebuilt {LimitlessReserialize.Rebuilt}, skipped getters {LimitlessReserialize.SkippedGetters}, known data {LimitlessReserialize.KnownData}, "
                        + $"verified {LimitlessReserialize.Verified}, verify mismatches {LimitlessReserialize.VerifyMismatches}; {LimitlessRecalcSkip.Name} passes skipped {LimitlessRecalcSkip.PassesSkipped}, run {LimitlessRecalcSkip.PassesRun}, mismatches {LimitlessRecalcSkip.PassMismatches}, skipped {LimitlessRecalcSkip.Skipped}, verified {LimitlessRecalcSkip.Verified}, mismatches {LimitlessRecalcSkip.Mismatches}; {LimitlessHookSkip.Name} samples {LimitlessHookSkip.Samples}, mismatches {LimitlessHookSkip.SampleMismatches}; {SnapshotCardReuse.Name} reused {SnapshotCardReuse.Reused}, built {SnapshotCardReuse.Built}, upgraded {SnapshotCardReuse.LightBuilt}, piles trusted {SnapshotCardReuse.PilesTrusted}, pile bits reused {SnapshotCardReuse.PileBitsReused}, composed {SnapshotCardReuse.PileBitsComposed}, pile mismatches {SnapshotCardReuse.PileMismatches}, verified {SnapshotCardReuse.Verified}, mismatches {SnapshotCardReuse.Mismatches}, piles reused {SnapshotCardReuse.PilesReused}, built {SnapshotCardReuse.PilesBuilt}, bits reused {SnapshotCardReuse.BitsReused}, captured {SnapshotCardReuse.BitsCaptured}, checked {SnapshotCardReuse.BitChecks}, game exceptions {SnapshotCardReuse.GameExceptions}; {LimitlessCloneMemo.Name} options reused {LimitlessCloneMemo.OptionsReused}; {ListenerCache.Name} suspended revalidations {ListenerCache.SuspendedRevalidations}; {FastListeners.Name} cache mismatches {FastListeners.CacheMismatches}, "
                        + $"segments rebuilt {FastListeners.SegmentRebuilds} (versions {FastListeners.RebuiltVersions}, pile version {FastListeners.PileVersion}), "
                        + $"extended {FastListeners.SegmentExtends}, kept across moves {FastListeners.SegmentMoveHits}, snapshot catch-ups {FastListeners.SnapshotCatchUps}, rebuilds {FastListeners.SnapshotRebuilds}, move checks {FastListeners.MoveChecks}, mismatches {FastListeners.MoveMismatches}, hits {FastListeners.SegmentHits}, uncached walks {FastListeners.UncachedWalks}; "
                        + $"{HookFilter.Name} walk checks {HookFilter.WalkChecks}, mismatches {HookFilter.WalkMismatches}, cost color checks {HookFilter.CostColorChecks / 64}, mismatches {HookFilter.CostColorMismatches}; {RelicFlashMerge.Name} merged {RelicFlashMerge.Merged}; {CardFlightLimit.Name} skipped {CardFlightLimit.Skipped}; {ReplayStateSkip.Name} skipped {ReplayStateSkip.Skipped}; {ThemeColorSkip.Name} skipped {ThemeColorSkip.Skipped}, set {ThemeColorSkip.Set}; {GcPacing.Name} regions {GcPacing.Regions}, refused {GcPacing.RegionsRefused}, exhausted {GcPacing.Exhausted}, {GcPacing.RegionMs:F0} ms, collections {GcPacing.Collections} (full {GcPacing.Fulls}) in {GcPacing.CollectMs:F0} ms, worst {GcPacing.WorstCollectMs:F0} ms; {PileIndex.Name} duplicate checks fast {PileIndex.FastDuplicateChecks}, slow {PileIndex.SlowDuplicateChecks}, fast ContainsCard {PileIndex.FastContainsCards}, nested-move self-check faults {PileIndex.NestedMoveFaults}; {HookSkip.Name} pending filters dropped {HookSkip.PendingFiltersDropped}; subscriber checks {FastListeners.SubscriberChecks} in {FastListeners.SubscriberCheckTicks * 1000.0 / Stopwatch.Frequency:F1} ms; "
                        + $"{CardSortKeys.Name} sorts {CardSortKeys.Sorts}, answered {CardSortKeys.Answered}, refused {CardSortKeys.Refused} (last: {CardSortKeys.LastRefusal ?? "none"}), checks {CardSortKeys.Checks}, mismatches {CardSortKeys.Mismatches}; "
                        + $"{CardDbScan.Name} walks {CardDbScan.Walks}, skipped {CardDbScan.Skipped}, verified {CardDbScan.Verified}, overflows {CardDbScan.Overflows}, failed adds dropped {CardDbScan.DroppedFailedAdds}, skipped originals {CardDbScan.SkippedAdds} (pile changed {CardDbScan.SkippedAddsChanged}); "
                        + $"{PileIndex.Name} skipped originals judged {PileIndex.JudgedSkips}; {SnapshotVerifier.Name} snapshots {SnapshotVerifier.Snapshots}, verified {SnapshotVerifier.Verified}, skipped (too large) {SnapshotVerifier.SkippedTooLarge}");
                    break;
                case "effects":
                    // What each optimization really did so far: tools/autotest/effects.py checks none stayed at zero. Kept per
                    // label for effectcheck.
                    Dictionary<string, long> effectsNow = new();
                    foreach ((string name, long count) in EffectCounts())
                    {
                        effectsNow[name] = count;
                        Say($"EFFECT {name} {count}");
                    }
                    _effectsAt[arg.Trim()] = effectsNow;
                    Say("PILE READS " + string.Join(", ", SnapshotCardReuse.WhyRead.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
                    Say("WRITES " + string.Join(", ", SnapshotCardReuse.WritesByMethod.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
                    Say("PILE BITS " + string.Join(", ", SnapshotCardReuse.WhyPileBits.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
                    lock (SnapshotCardReuse.PileTimes) Say("PILE TIMES " + string.Join(", ", SnapshotCardReuse.PileTimes.OrderByDescending(p => p.Value.Ms).Select(p => $"{p.Key}: {p.Value.Calls} calls {p.Value.Ms:F0} ms {p.Value.Cards / Math.Max(1, p.Value.Calls)} cards avg")));
                    Say("NO UPGRADE BITS " + string.Join(", ", SnapshotCardReuse.WhyNoUpgradeBits.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
                    // Nothing runs here: what a patch opens before a method and closes after it must be closed.
                    Say($"BALANCE {nameof(ListenerCache)}.OpenScopes {ListenerCache.OpenScopes}");
                    Say($"BALANCE {nameof(FastListeners)}.OpenShuffles {FastListeners.OpenShuffles}");
                    Say($"BALANCE {nameof(PileCounterBatch)}.OpenClears {PileCounterBatch.OpenClears}");
                    break;
                case "effectcheck":
                    EffectCheck(arg.Trim());
                    break;
                case "patchlist":
                    // For tools/profiling/inlines.
                    Say($"  InlineGuard available {InlineGuard.Available}, methods protected {InlineGuard.Protected}");
                    foreach (MethodBase patched in Harmony.GetAllPatchedMethods())
                    {
                        HarmonyLib.Patches? info = Harmony.GetPatchInfo(patched);
                        string kinds = (info?.Prefixes.Count > 0 ? "prefix " : "") + (info?.Postfixes.Count > 0 ? "postfix " : "") + (info?.Transpilers.Count > 0 ? "transpiler " : "") + (info?.Finalizers.Count > 0 ? "finalizer " : "");
                        Say($"  PATCHED {patched.DeclaringType?.FullName}::{patched.Name} {kinds}il {patched.GetMethodBody()?.GetILAsByteArray()?.Length} by {(info == null ? "" : string.Join(",", info.Owners.Select(o => o.Split('.')[0]).Distinct()))} finalizers {(info == null ? "" : string.Join(",", info.Finalizers.Select(f => f.PatchMethod.DeclaringType?.Name + "." + f.PatchMethod.Name)))}");
                    }
                    break;
                case "rebuildcheck":
                    RebuildCheck.Run(arg, Say);
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
                    if (System.Text.RegularExpressions.Regex.IsMatch(arg, @"^\s*(room|event)\s"))
                    {
                        // The room this peer leaves: waitfor room must not take it for the one asked (both shops: the map drew one before).
                        _roomBeforeJump = new WeakReference<object>(RunManager.Instance.DebugOnlyGetState()?.CurrentRoom ?? new object());
                    }
                    await Command(arg);
                    break;
                case "play":
                    await Play(tree, int.Parse(arg));
                    break;
                case "avoid":
                    _avoid.UnionWith(arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "deckview":
                    string[] viewArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await DeckView(tree, viewArgs.Length > 0 ? double.Parse(viewArgs[0]) : 3, viewArgs.Length > 1 ? viewArgs[1] : null);
                    break;
                case "deckscroll":
                    await DeckScroll(tree, int.Parse(arg));
                    break;
                case "decksort":
                    await DeckSort(tree);
                    break;
                case "pilescroll":
                    string[] pileArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await PileScroll(tree, pileArgs[0], int.Parse(pileArgs[1]), pileArgs.Length > 2 ? float.Parse(pileArgs[2]) : 40f);
                    break;
                case "deckpages":
                    string[] pageArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await DeckPages(tree, pageArgs[0], int.Parse(pageArgs[1]));
                    break;
                case "handselect":
                    await HandSelect(tree, arg);
                    break;
                case "playcard":
                    List<string> playArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool free = playArgs.Remove("free");
                    bool optionalCard = playArgs.Remove("optional");
                    await PlayCard(tree, playArgs[0].ToUpperInvariant(), playArgs.Count > 1 ? int.Parse(playArgs[1]) : 0, free, optionalCard);
                    break;
                case "endturn":
                    await EndTurnLine(tree);
                    break;
                case "watch":
                    Watch(tree, double.Parse(arg.Trim()));
                    break;
                case "nodes":
                    LogNodes(tree, arg.Trim());
                    break;
                case "eventoption":
                    List<string> eventArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool optionalEvent = eventArgs.Remove("optional");
                    await EventOption(tree, eventArgs[0], eventArgs.Count > 1 ? double.Parse(eventArgs[1]) : 600, optionalEvent);
                    break;
                case "restsite":
                    List<string> restArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    bool optionalRest = restArgs.Remove("optional");
                    await RestSite(restArgs[0], optionalRest);
                    break;
                case "quit":
                    EndChecks();
                    Quit();
                    return;
                case "handoff":
                    // The player takes over: stop answering card choices, keep the game open.
                    _selector?.Dispose();
                    _selector = null;
                    Say("handed over to the player");
                    return;
                case "onfail":
                    _continueOnFail = arg.Trim() == "continue";
                    break;
                default:
                    // Commands of local, untracked tooling (another partial file implements this; without it, nothing).
                    bool handled = false;
                    LocalCommand(tree, parts[0], arg, ref handled);
                    if (!handled)
                    {
                        throw new ArgumentException("unknown script command: " + parts[0]);
                    }
                    break;
            }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Say($"FAILED line '{line}': {e.Message}");
                if (!_continueOnFail)
                {
                    // What follows a failed line rarely means anything, and each later wait would time out in turn.
                    Say($"ABORT: first failed line (a scenario expecting failures says 'onfail continue') [lines left {LinesLeft(lineIndex + 1)}]");
                    Quit();
                    return;
                }
            }
        }
        EndChecks();
        Say("script finished");
        Quit();
    }

    /// <summary>A line found this game version can't run the scenario (<see cref="SkipRest"/>): the script ends there.</summary>
    private static bool _skipRest;

    /// <summary>
    /// The scenario doesn't apply to this game version (a game mode it lacks): <c>SKIPPED: &lt;why&gt;</c>, and the script ends
    /// as finished (<c>script finished</c>), nothing after this line run. Both sides of an A/B skip at the same line.
    /// </summary>
    private static void SkipRest(string why)
    {
        Say($"SKIPPED: {why}: the rest of this scenario doesn't apply to this game version [lines left {LinesLeft(_scriptLine + 1)}]");
        _skipRest = true;
    }

    /// <summary>The script's lines and the index of the one running (for <see cref="LinesLeft"/>).</summary>
    private static string[] _scriptLines = Array.Empty<string>();
    private static int _scriptLine;

    /// <summary>The script lines from <paramref name="from"/> on that would run (neither blank nor comments): the ABORT line's
    /// <c>[lines left N]</c>, so a runner knows whether an expected failure was the script's last line.</summary>
    private static int LinesLeft(int from) => _scriptLines.Skip(Math.Max(0, from)).Count(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));

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

    /// <summary>
    /// A wait still going after this long logs one line with the state: the runners kill a session whose log stays silent for their
    /// StallSec (240-300 s), before a longer wait's own timeout could say what it waited on. Once only: a stuck session is still
    /// killed, StallSec later.
    /// </summary>
    private const double WaitNoticeSeconds = 180;

    /// <summary>Waits until the condition holds for 10 frames in a row.</summary>
    private static async Task Until(SceneTree node, Func<bool> condition, double timeout, string what)
    {
        double start = _clock.Elapsed.TotalSeconds;
        int held = 0;
        bool told = false;
        while (held < 10)
        {
            await Frames(node, 1);
            held = condition() ? held + 1 : 0;
            if (!told && timeout > WaitNoticeSeconds && _clock.Elapsed.TotalSeconds - start > WaitNoticeSeconds)
            {
                told = true;
                Say($"  still waiting for {what} after {WaitNoticeSeconds:F0} s (limit {timeout:F0} s; {StateText()})");
            }
            ThrowIfNetCommandFailed();
            if (_turnLoopDied)
            {
                _turnLoopDied = false;
                throw new InvalidOperationException($"waitfor {what}: the combat's turn loop died ({StateText()})");
            }
            if (_coopDiverged)
            {
                throw new InvalidOperationException($"waitfor {what}: the peers' states diverged ({StateText()})");
            }
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
        if (parts[0] == "round")
        {
            // waitfor round <n> [timeout]: the combat's round n has begun and this player can act (both peers ended the turn before),
            // or the combat is over. "turn" after this player's endturn waits for the next round as well (ending the turn disables
            // this player's actions until then, and the endturn line waits until the game took it); "round" names the round.
            int round = int.Parse(parts[1]);
            return Until(node, () => !CombatManager.Instance.IsInProgress
                || (CombatManager.Instance.DebugOnlyGetState()?.RoundNumber >= round && IsIdle()), parts.Length > 2 ? double.Parse(parts[2]) : 120, "round " + round);
        }
        if (parts[0] == "room")
        {
            // waitfor room <type> [timeout]: that room is the current one and its node is in the tree (a co-op peer following the host's
            // cmd room / cmd event, or the map's travel).
            MegaCrit.Sts2.Core.Rooms.RoomType wanted = Enum.Parse<MegaCrit.Sts2.Core.Rooms.RoomType>(parts[1], ignoreCase: true);
            // The room's own node, by the name the game's handlers look for: in co-op the jump is queued, and until it runs the previous
            // room's node is still there (a script acting on it then finds it freed).
            // Through NRun's typed accessors (the room scene of that kind): the node's name changed in v0.113.
            Func<Node?>? roomNode = wanted switch
            {
                MegaCrit.Sts2.Core.Rooms.RoomType.Shop => () => NRun.Instance?.MerchantRoom,
                MegaCrit.Sts2.Core.Rooms.RoomType.RestSite => () => NRun.Instance?.RestSiteRoom,
                MegaCrit.Sts2.Core.Rooms.RoomType.Treasure => () => NRun.Instance?.TreasureRoom,
                MegaCrit.Sts2.Core.Rooms.RoomType.Event => () => NRun.Instance?.EventRoom,
                _ => null,
            };
            WeakReference<object>? before = _roomBeforeJump;
            _roomBeforeJump = null;
            return Until(node, () => RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is { } current && current.RoomType == wanted
                && !(before != null && before.TryGetTarget(out object? left) && ReferenceEquals(left, current))
                && NRun.Instance?.GetNodeOrNull("RoomContainer") is { } rooms && rooms.GetChildCount() > 0
                && (roomNode == null || roomNode() is { } room && GodotObject.IsInstanceValid(room) && room.IsInsideTree() && !room.IsQueuedForDeletion()),
                parts.Length > 2 ? double.Parse(parts[2]) : 120, "room " + wanted);
        }
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

    /// <summary>
    /// A script <c>state</c> line outside a combat fails unless it says <c>optional</c> (both sides of an A/B would log "no combat"
    /// and compare equal, a false pass). The scenarios that log it there on purpose say <c>optional</c>: clone-on/off and
    /// cloneopt-on/off ('state before'/'state after'), audit-coopdeath-client (the dead player's 'state dead' and 'state t3' to 't6').
    /// </summary>
    private const bool StateOutsideCombatFails = true;

    /// <summary>
    /// Script <c>state [label] [optional]</c>: creatures' HP, block and powers, plus the local piles, for A/B comparisons. Without a
    /// combat it compares nothing (<c>runstate</c> is the one outside combat): the script line then fails unless <c>optional</c>
    /// (<see cref="StateOutsideCombatFails"/>); the turn-start and sweep states never do.
    /// </summary>
    private static void LogState(string label, bool optional = true)
    {
        CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
        Player? me = Me();
        if (combat == null || me?.PlayerCombatState == null)
        {
            if (StateOutsideCombatFails && !optional)
            {
                MissingStep(optional: false, $"STATE {label}: no combat");
            }
            else
            {
                Say($"  STATE {label}: no combat");
            }
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

    /// <summary>
    /// Frame times are kept once something measures them (an fpsstat, an AutoSlay phase, a sweep step, framedetail): a session
    /// without (the leak series) would otherwise grow the list by a frame each, millions headless, counted in every gcfull's heap.
    /// </summary>
    private static bool _recordFrames;

    private static void RecordFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0 && _recordFrames)
        {
            _frameMs.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
        }
        _lastFrame = now;
        if (_frameDetail)
        {
            _frameStart = now;
            int collections = GC.CollectionCount(0);
            if (collections != _lastCollections)
            {
                _lastCollections = collections;
                GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
                Say($"  GCEVENT gen{info.Generation} concurrent {info.Concurrent} compacted {info.Compacted} pauses {info.PauseDurations[0].TotalMilliseconds:F0}+{info.PauseDurations[1].TotalMilliseconds:F0} ms, "
                    + $"last frame {(_frameMs.Count > 0 ? _frameMs[^1] : 0):F0} ms, heap {info.HeapSizeBytes / (1024 * 1024)} MB, phase {_phase ?? "-"}");
            }
            Rid viewport = ((SceneTree)Engine.GetMainLoop()).Root.GetViewportRid();
            _frameRenderCpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport) + RenderingServer.GetFrameSetupTimeCpu());
            _frameRenderGpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport));
            _frameDraws += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            _frameObjects += Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        }
    }

    private static bool _frameDetail;
    private static int _lastCollections;
    private static readonly List<double> _frameProcess = new(), _frameDraw = new(), _frameRenderCpu = new(), _frameRenderGpu = new();
    private static double _frameDraws, _frameObjects;
    private static long _frameStart, _framePreDraw, _detailCpu, _detailBytes;
    private static TimeSpan _detailPause;

    /// <summary>Script <c>framedetail</c>: each <c>fpsstat</c> also logs a <c>FRAMES</c> line, where a frame's time goes.</summary>
    private static void FrameDetailOn(SceneTree tree)
    {
        RenderingServer.ViewportSetMeasureRenderTime(tree.Root.GetViewportRid(), true);
        // Godot's own TimeProcess monitor is the highest value of the last second, not this frame's: the frame's own timeline instead.
        // process_frame -> scripts, deferred calls, the game's awaits -> frame_pre_draw -> rendering -> frame_post_draw -> present, input.
        RenderingServer.FramePreDraw += () =>
        {
            _framePreDraw = Stopwatch.GetTimestamp();
            if (_frameStart != 0)
            {
                _frameProcess.Add(Stopwatch.GetElapsedTime(_frameStart, _framePreDraw).TotalMilliseconds);
            }
        };
        RenderingServer.FramePostDraw += () =>
        {
            if (_framePreDraw != 0)
            {
                _frameDraw.Add(Stopwatch.GetElapsedTime(_framePreDraw).TotalMilliseconds);
            }
        };
        _frameDetail = true;
        _recordFrames = true;
        FrameDetailReset();
    }

    private static void FrameDetailReset()
    {
        _frameProcess.Clear();
        _frameDraw.Clear();
        _frameRenderCpu.Clear();
        _frameRenderGpu.Clear();
        _frameDraws = _frameObjects = 0;
        _detailCpu = MainThreadCpuTicks();
        _detailBytes = GC.GetTotalAllocatedBytes(false);
        _detailPause = GC.GetTotalPauseDuration();
    }

    private static double Percentile(double[] sorted, double fraction) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * fraction))];

    /// <summary>
    /// Where the frames of an fpsstat window went, in ms per frame: the process step (scripts, the game's awaits, a GC pause they
    /// meet), the draw step on the main thread (of which rendercpu is the renderer's own measure), and the rest (present, driver,
    /// input, physics); GPU time apart, it runs alongside. Main thread CPU against wall time tells a frame that works from one
    /// that waits.
    /// </summary>
    private static void FrameDetail(string label, double[] sorted)
    {
        double[] process = _frameProcess.OrderBy(ms => ms).ToArray();
        double[] draw = _frameDraw.OrderBy(ms => ms).ToArray();
        double[] cpu = _frameRenderCpu.OrderBy(ms => ms).ToArray();
        double[] gpu = _frameRenderGpu.OrderBy(ms => ms).ToArray();
        double total = sorted.Sum();
        int frames = Math.Max(process.Length, 1);
        double rest = total / sorted.Length - process.Sum() / frames - draw.Sum() / Math.Max(draw.Length, 1);
        double mainCpu = (MainThreadCpuTicks() - _detailCpu) / 10000.0;
        Say($"  FRAMES {label}: p50 {Percentile(sorted, 0.5):F2} p90 {Percentile(sorted, 0.9):F2} p99 {Percentile(sorted, 0.99):F2} p999 {Percentile(sorted, 0.999):F2} "
            + $"over8 {sorted.Count(ms => ms > 8.33)} over16 {sorted.Count(ms => ms > 16.67)} "
            + $"process avg {process.Sum() / frames:F2} p99 {Percentile(process, 0.99):F2} max {(process.Length > 0 ? process[^1] : 0):F1} "
            + $"draw avg {draw.Sum() / Math.Max(draw.Length, 1):F2} p99 {Percentile(draw, 0.99):F2} max {(draw.Length > 0 ? draw[^1] : 0):F1} "
            + $"rendercpu avg {cpu.Sum() / Math.Max(cpu.Length, 1):F2} gpu avg {gpu.Sum() / Math.Max(gpu.Length, 1):F2} p99 {Percentile(gpu, 0.99):F2} rest avg {rest:F2} "
            + $"maincpu {mainCpu * 100 / Math.Max(total, 1):F0}% draws {_frameDraws / frames:F0} objects {_frameObjects / frames:F0} "
            + $"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0} mb {(GC.GetTotalAllocatedBytes(false) - _detailBytes) / (1024.0 * 1024):F1} gc {(GC.GetTotalPauseDuration() - _detailPause).TotalMilliseconds:F0}");
    }

    /// <summary>
    /// Script <c>fpsstat [label]</c>: frame rate since the previous fpsstat: average fps, "1% low" (fps of the 99th
    /// percentile frame time), worst frame and how many frames went over 33 / 50 / 100 ms.
    /// </summary>
    private static void FpsStat(string label)
    {
        _recordFrames = true; // Every script's first fpsstat (bare) opens the window the next one reads.
        if (label.Length > 0 && _frameMs.Count > 0)
        {
            double[] sorted = _frameMs.OrderBy(ms => ms).ToArray();
            double total = sorted.Sum();
            double p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
            Say($"  FPSSTAT {label}: {sorted.Length} frames in {total / 1000:F1} s, avg {sorted.Length * 1000 / total:F0} fps, " +
                $"1% low {1000 / p99:F0} fps, worst {sorted[^1]:F0} ms, over 33 ms {sorted.Count(ms => ms > 33)}, " +
                $"over 50 ms {sorted.Count(ms => ms > 50)}, over 100 ms {sorted.Count(ms => ms > 100)}");
            if (_frameDetail)
            {
                FrameDetail(label, sorted);
            }
        }
        _frameMs.Clear();
        if (_frameDetail)
        {
            FrameDetailReset();
        }
    }

    /// <summary>
    /// Script <c>pilemove &lt;from&gt; &lt;to&gt; [keep]</c>: moves a pile's cards to another pile silently, leaving the first
    /// <c>keep</c> ones, to set up a situation (a huge discard pile for the next reshuffle).
    /// </summary>
    private static void PileMove(string from, string to, int keep)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        CardPile source = Enum.Parse<PileType>(from, ignoreCase: true).GetPile(me);
        CardPile target = Enum.Parse<PileType>(to, ignoreCase: true).GetPile(me);
        List<CardModel> moved = source.Cards.Skip(keep).ToList();
        for (int i = moved.Count - 1; i >= 0; i--)
        {
            // Through reflection: test tooling must keep working on game versions where this method changed.
            typeof(CardPile).GetMethods().First(m => m.Name.StartsWith("RemoveInternal", StringComparison.Ordinal)).Invoke(source, new object[] { moved[i], true });
        }
        foreach (CardModel card in moved)
        {
            target.AddInternal(card, -1, silent: true);
        }
        Say($"  moved {moved.Count} cards {from} -> {to}: {source.Cards.Count} / {target.Cards.Count}");
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
        // checktips compares each panel with what it was placed for: watched from the first panel on.
        BenchPatches(WatchTips);
        _hitboxAtLastChange = null;
        AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature), "OnFocus").Invoke(_hovered, null);
        if (MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.Ui.Hand.InCardPlay == false)
        {
            // OnFocus places the panel itself (ShowHoverTips), not through a state change.
            _hitboxAtLastChange = _hovered.Hitbox.GlobalPosition;
        }
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
        _tipsChecks++;
        if (!active.TryGetValue(_hovered.Hitbox, out var panel))
        {
            // The game hides them itself (targeting a card, card play in progress): nothing to compare here, and a session
            // where every checktips found them hidden fails at its end (TipsCompared).
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
        _tipsCompared += same ? 1 : 0;

        // Where the panel is. Creatures animate (idle bob, hit reactions): their hitbox moves every frame, while the game (and
        // HoverTipReuse alike) places the panel only at each ShowHoverTips, so a rebuild "now" would measure the animation.
        // Two checks instead:
        // 1. Input: the shown panel was placed for the hitbox where it was at the last state change that re-placed it in the
        //    game (a panel reused at the old place after the creature moved fails here: hover-tips-move).
        // 2. Layout: the game's own full rebuild (ShowHoverTips: HoverTipReuse only patches ShowCreatureHoverTips), the hitbox
        //    put back for that rebuild where the shown panel was placed for, gives the same panel, overflow corrections included.
        // HoverTipReuse then rebuilds once at its next state change (the panel is no longer its own).
        Control hitbox = _hovered.Hitbox;
        TipsInput? shownInput = _placedAt.TryGetValue(panel, out TipsInput? placedFor) ? placedFor : null;
        if (shownInput == null)
        {
            Say($"  TIPS INPUT not known {label}: the shown panel was placed before checktips watched placements");
        }
        else if (_hitboxAtLastChange is { } expectedAt)
        {
            bool inputOk = Near(shownInput.Position, expectedAt);
            Say($"  TIPS INPUT {(inputOk ? "OK" : "MISMATCH")} {label}: placed for the hitbox at {P(shownInput.Position)}, "
                + $"at the last state change it was at {P(expectedAt)}, now at {P(hitbox.GlobalPosition)}");
        }
        TipsLayout before = TipsPlacement(panel);
        Vector2 hitboxLocal = hitbox.Position;
        string? notCompared = shownInput == null ? "what the shown panel was placed for isn't known"
            : !Near(shownInput.Size, hitbox.Size * hitbox.Scale) ? $"the hitbox's size changed since the panel was placed ({P(shownInput.Size)} -> {P(hitbox.Size * hitbox.Scale)})"
            : null;
        TipsLayout? after = null;
        if (notCompared == null)
        {
            try
            {
                hitbox.GlobalPosition = shownInput!.Position;
                _hovered.ShowHoverTips(_hovered.Entity.HoverTips);
                if (!active.TryGetValue(hitbox, out var rebuilt) || ReferenceEquals(rebuilt, panel))
                {
                    // ShowHoverTips does nothing during a card play: the same panel would be compared with itself.
                    notCompared = "the game didn't rebuild the panel (card play in progress)";
                }
                else
                {
                    after = TipsPlacement(rebuilt);
                }
            }
            finally
            {
                hitbox.Position = hitboxLocal;
            }
            // The panel on screen as the game places it for the hitbox now, the reference of the next check's input.
            _hovered.ShowHoverTips(_hovered.Entity.HoverTips);
            if (active.TryGetValue(hitbox, out var current) && _placedAt.TryGetValue(current, out TipsInput? currentInput))
            {
                _hitboxAtLastChange = currentInput.Position;
            }
        }
        if (after is not { } rebuiltLayout)
        {
            Say($"  TIPS PLACEMENT not compared {label}: {notCompared}");
            return;
        }
        bool placed = before.Matches(rebuiltLayout);
        Say($"  TIPS PLACEMENT {(placed ? "OK" : "MISMATCH")} {label}: shown {before}" + (placed ? "" : $" | rebuilt for the same hitbox place {rebuiltLayout}"));
    }

    /// <summary>What a session must have done by its end (a normal end or <c>quit</c>), or it fails.</summary>
    private static void EndChecks()
    {
        if (_tipsChecks > 0 && _tipsCompared == 0)
        {
            // Every checktips found the panel hidden or different: the session compared no tips at all.
            Say($"FAILED: checktips compared no panel ({_tipsChecks} checks, none with its tips on screen and matching)");
        }
    }

    /// <summary><c>checktips</c> lines run, and those that found the panel on screen with the expected tips.</summary>
    private static int _tipsChecks, _tipsCompared;

    /// <summary>What a tip panel was placed for: the owner's global position and its size times scale (what SetAlignment reads).</summary>
    private sealed record TipsInput(Vector2 Position, Vector2 Size);

    /// <summary>Each tip panel's placement input, recorded by the game's own SetAlignment (Bench postfixes, see <see cref="WatchTips"/>).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet, TipsInput> _placedAt = new();

    /// <summary>The hovered hitbox's place at the last state change after which the game re-places its panel (or at hover).</summary>
    private static Vector2? _hitboxAtLastChange;

    private static bool _watchingTips;

    /// <summary>
    /// Records each panel's placement input and the hovered hitbox at each state change that re-places it in the game
    /// (<c>ShowCreatureHoverTips</c> with a combat and no card play, as <c>ShowHoverTips</c> decides). Postfixes run even when
    /// HoverTipReuse's prefix skips the original. Under this mod's Harmony id: no back-off sees them as another mod's.
    /// </summary>
    private static void WatchTips()
    {
        if (_watchingTips)
        {
            return;
        }
        _watchingTips = true;
        Type set = typeof(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet);
        Type alignment = typeof(MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment);
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(set, "SetAlignment", new[] { typeof(Control), alignment }),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(AlignedToControl)));
        // v0.113: Left and Right go through the Rect2 overload (a target's rect, maybe where it will end up).
        if (AccessTools.Method(set, "SetAlignment", new[] { typeof(Rect2), alignment }) is { } toRect)
        {
            PatchGuard.Patch(ModEntry.Harmony, toRect, postfix: new HarmonyMethod(typeof(AutoTest), nameof(AlignedToRect)));
        }
        PatchGuard.Patch(ModEntry.Harmony, AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature), "ShowCreatureHoverTips"),
            postfix: new HarmonyMethod(typeof(AutoTest), nameof(CreatureTipsShown)));
    }

    // The Control overload runs last when it calls the Rect2 one (v0.113): both record the same input.
    private static void AlignedToControl(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet __instance, Control __0) =>
        _placedAt.AddOrUpdate(__instance, new TipsInput(__0.GlobalPosition, __0.Size * __0.Scale));

    private static void AlignedToRect(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet __instance, Rect2 __0) =>
        _placedAt.AddOrUpdate(__instance, new TipsInput(__0.Position, __0.Size));

    private static void CreatureTipsShown(MegaCrit.Sts2.Core.Nodes.Combat.NCreature __instance)
    {
        if (ReferenceEquals(__instance, _hovered) && __instance.Entity.CombatState != null
            && MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance?.Ui.Hand.InCardPlay == false)
        {
            _hitboxAtLastChange = __instance.Hitbox.GlobalPosition;
        }
    }

    /// <summary>A tip panel's place: its own, its text container's (and size), its card container's.</summary>
    private readonly record struct TipsLayout(Vector2 Panel, Vector2 Text, Vector2 TextSize, Vector2 Cards)
    {
        public bool Matches(TipsLayout other) => Near(Panel, other.Panel) && Near(Text, other.Text) && Near(TextSize, other.TextSize) && Near(Cards, other.Cards);

        public override string ToString() => $"panel {P(Panel)} text {P(Text)} size {P(TextSize)} cards {P(Cards)}";
    }

    private static TipsLayout TipsPlacement(MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet panel)
    {
        var text = (Control)AccessTools.Field(panel.GetType(), "_textHoverTipContainer").GetValue(panel)!;
        var cards = (Control)AccessTools.Field(panel.GetType(), "_cardHoverTipContainer").GetValue(panel)!;
        return new TipsLayout(panel.GlobalPosition, text.GlobalPosition, text.Size, cards.GlobalPosition);
    }

    private static bool Near(Vector2 a, Vector2 b) => Math.Abs(a.X - b.X) <= 0.01f && Math.Abs(a.Y - b.Y) <= 0.01f;

    private static string P(Vector2 v) => $"({v.X:F2},{v.Y:F2})";

    /// <summary>
    /// Script <c>tipsrebuilt mark|grew</c>: HoverTipReuse's rebuilds since the mark must have grown (a moved creature: the panel
    /// is placed again, not reused), or the line fails.
    /// </summary>
    private static long _tipsRebuiltMark;

    private static void TipsRebuilt(string arg)
    {
        if (arg == "mark")
        {
            _tipsRebuiltMark = HoverTipReuse.Rebuilt;
            Say($"  TIPSREBUILT mark: {_tipsRebuiltMark}");
            return;
        }
        if (arg != "grew")
        {
            throw new ArgumentException("tipsrebuilt mark|grew");
        }
        Say($"  TIPSREBUILT grew: {_tipsRebuiltMark} -> {HoverTipReuse.Rebuilt}");
        if (HoverTipReuse.Rebuilt <= _tipsRebuiltMark)
        {
            throw new InvalidOperationException($"HoverTipReuse didn't rebuild since the mark ({_tipsRebuiltMark} -> {HoverTipReuse.Rebuilt})");
        }
    }

    /// <summary>Script <c>movehovered &lt;dx&gt; &lt;dy&gt;</c>: moves the hovered creature's node (and its hitbox) on screen, nothing else.</summary>
    private static void MoveHovered(string arg)
    {
        if (_hovered == null || !GodotObject.IsInstanceValid(_hovered))
        {
            throw new InvalidOperationException("hover a creature first");
        }
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _hovered.Position += new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        Say($"  moved {_hovered.Entity} to {_hovered.Position}, hitbox at {_hovered.Hitbox.GlobalPosition}");
    }

    /// <summary>The last <c>checksum</c> line of this session: its label, whether it was a plain one, its value.</summary>
    private static (string Label, bool Plain, uint Value)? _lastChecksum;

    /// <summary>
    /// Script <c>checksum [label] [vanilla|same]</c>: the full combat state checksum (what co-op peers compare after every
    /// action). Two sessions running the same script must log the same values. By default through the game's path with
    /// this mod's optimizations (verified identical, ~40 ms); "vanilla" turns the whole mod off for it, as <c>bdopt off</c>
    /// does (tens of seconds with huge decks), and fails the line unless the immediately preceding checksum line was the plain
    /// <c>checksum &lt;label&gt;</c> with the same value (nothing else compares the two lines of one session: labels repeat across
    /// fights, so only the preceding line counts). "same" computes it as a plain line does and fails unless the immediately
    /// preceding checksum line had the same label and value (a switch flipped between the two, as snapcheckcoop does).
    /// The CARDIDS line after it (<see cref="LogCardIds"/>): the card ids this mod's CardDbScan maps, compared between the sides.
    /// </summary>
    private static void LogChecksum(string arg)
    {
        bool vanilla = arg.EndsWith(" vanilla", StringComparison.Ordinal) || arg == "vanilla";
        bool same = arg.EndsWith(" same", StringComparison.Ordinal) || arg == "same";
        string label = vanilla || same ? arg[..^(vanilla ? "vanilla" : "same").Length].TrimEnd() : arg;
        RunState state = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        Func<uint> compute = () => RunManager.Instance.ChecksumTracker.GenerateChecksum(
            MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState.FromRun(state, null));
        long start = Stopwatch.GetTimestamp();
        uint checksum = vanilla ? PerfConsoleCmd.Vanilla(compute) : compute();
        Say($"  CHECKSUM {label}: {checksum:X8} ({Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms)");
        (string Label, bool Plain, uint Value)? previous = _lastChecksum;
        _lastChecksum = (label, !vanilla && !same, checksum);
        LogCardIds(label);
        if (vanilla || same)
        {
            string kind = vanilla ? "vanilla" : "same";
            if (previous is not { } before || before.Label != label || (vanilla && !before.Plain))
            {
                throw new InvalidOperationException($"'checksum {label} {kind}' must follow a {(vanilla ? "plain " : "")}'checksum {label}' line (the line before was {(previous is { } p ? $"'checksum {p.Label}{(p.Plain ? "" : " ...")}'" : "none")})");
            }
            Say($"  CHECKSUMPAIR {label} {kind}: {before.Value:X8} vs {checksum:X8} {(before.Value == checksum ? "identical" : "DIFFERENT")}");
            if (before.Value != checksum)
            {
                throw new InvalidOperationException($"checksum '{label}' {kind} {checksum:X8} differs from the line before ({before.Value:X8})");
            }
        }
    }

    /// <summary>
    /// After each <c>checksum</c> line: <c>STATE &lt;label&gt;: cardids &lt;hash&gt; (&lt;n&gt; ids, next &lt;id&gt;, &lt;m&gt; without)</c>, the network
    /// ids NetCombatCardDb gave the cards of every player's combat piles (players in the run's order, piles in AllPiles order,
    /// cards in pile order; a card without one counts apart). The checksum doesn't cover them (NetFullCombatState carries no card
    /// id) though co-op actions name cards by them: what CardDbScan changes is only visible here. A STATE line, which
    /// tools/autotest/compare.py compares line for line between the two sides; no timing in it. Outside a combat: 0 ids.
    /// </summary>
    private static void LogCardIds(string label)
    {
        try
        {
            var db = MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb.Instance;
            uint hash = 2166136261;
            void Mix(uint value)
            {
                for (int b = 0; b < 4; b++)
                {
                    hash = (hash ^ ((value >> (8 * b)) & 0xFF)) * 16777619;
                }
            }
            int ids = 0, without = 0;
            RunState? state = RunManager.Instance.DebugOnlyGetState();
            if (state != null && CombatManager.Instance.IsInProgress)
            {
                uint playerIndex = 0;
                foreach (Player player in state.Players)
                {
                    Mix(0xA0000000u | playerIndex++);
                    if (player.PlayerCombatState == null)
                    {
                        continue;
                    }
                    uint pileIndex = 0;
                    foreach (CardPile pile in player.PlayerCombatState.AllPiles)
                    {
                        Mix(0xB0000000u | pileIndex++);
                        foreach (CardModel card in pile.Cards)
                        {
                            if (db.TryGetCardId(card, out uint id))
                            {
                                ids++;
                                Mix(id);
                            }
                            else
                            {
                                without++;
                                Mix(0xFFFFFFFFu);
                            }
                        }
                    }
                }
            }
            object? next = AccessTools.Field(db.GetType(), "_nextId")?.GetValue(db);
            Say($"  STATE {label}: cardids {hash:X8} ({ids} ids, next {next ?? "?"}, {without} without)");
        }
        catch (Exception e)
        {
            // Same line on both sides when the game's API differs: compared as equal, said here.
            Say($"  STATE {label}: cardids not read ({e.GetType().Name}: {e.Message})");
        }
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
            GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Ephemeral);
            Say($"    last ephemeral GC: gen{info.Generation}, pause {info.PauseDurations[0].TotalMilliseconds:F0} ms, promoted {info.PromotedBytes / (1024 * 1024)} MB, compacted {info.Compacted}, pinned {info.PinnedObjectsCount}, "
                + $"gen0 {info.GenerationInfo[0].SizeBeforeBytes / (1024 * 1024)}->{info.GenerationInfo[0].SizeAfterBytes / (1024 * 1024)} MB, gen1 {info.GenerationInfo[1].SizeBeforeBytes / (1024 * 1024)}->{info.GenerationInfo[1].SizeAfterBytes / (1024 * 1024)} MB, "
                + $"gen2 {info.GenerationInfo[2].SizeAfterBytes / (1024 * 1024)} MB, loh {info.GenerationInfo[3].SizeAfterBytes / (1024 * 1024)} MB, fragmented {info.FragmentedBytes / (1024 * 1024)} MB");
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

    /// <summary>A script command of a local tool file (untracked): sets <paramref name="handled"/> when it knows it.</summary>
    static partial void LocalCommand(SceneTree tree, string command, string arg, ref bool handled);

    private static Player? Me() => RunManager.Instance.DebugOnlyGetState() is { } state ? LocalContext.GetMe(state) : null;

    private static readonly List<WeakReference> _leftRuns = new();

    private static async Task NewRun(NGame game, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CharacterModel character = ModelDb.GetById<CharacterModel>(new ModelId(ModelId.SlugifyCategory<CharacterModel>(), parts[0].ToUpperInvariant()));
        string seed = parts.Length > 1 ? parts[1] : "BDTEST";
        GameMode mode = GameMode.Standard;
        if (parts.Length > 2)
        {
            // newrun <CHARACTER> <seed> dread|blitz|standard: Dread and Blitz from game v0.113 only.
            if (ParseGameMode(parts[2]) is not { } parsed)
            {
                SkipRest($"game {Reflect.GameVersion() ?? "version unknown"} has no {parts[2]} mode");
                return;
            }
            mode = parsed;
            _modeRun |= !mode.ToString().Equals("Standard", StringComparison.Ordinal);
        }
        if (!_uiSelection)
        {
            _selector ??= PushSelector(new FirstCardsSelector());
        }
        // Fresh test profile: no tutorials (the combat one throws when combat is entered with the fight command).
        SaveManager.Instance.Progress.EnableFtues = false;
        // The character select screen stops the menu music before starting a run; without it, it keeps playing under
        // the act music.
        MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager.Instance?.StopMusic();
        await StartSingleplayerRun(game, character, seed, mode);
        Say($"  run started: {character.Id.Entry}, seed {seed}{(parts.Length > 2 ? ", mode " + mode : "")}");
    }

    /// <summary>
    /// A console command. One that fails throws by default: a script's <c>cmd</c> line then fails like any other line
    /// (a deck import that found no file, a bdperf comparison saying MISMATCH, an unknown bdopt option would otherwise
    /// go on unseen, and an A/B compare two sessions that both skipped it). <paramref name="mayFail"/>: the sweeps' own
    /// steps, where a refused command (a relic its effect already removed) is the same on both sides and logged as such.
    /// </summary>
    private static async Task Command(string line, bool mayFail = false)
    {
        _console ??= new DevConsole(shouldAllowDebugCommands: true);
        // In co-op, networked commands must go through the action queue so every peer runs them.
        // A queued networked command's result is only seen when the queue runs it (NetCommandFinalizer); every other one here.
        // (ProcessCommand never calls ProcessNetCommand itself: the flag only covers the direct call.)
        CmdResult result;
        if (RunManager.Instance.IsInProgress && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            result = _console.ProcessCommand(line);
        }
        else
        {
            _directCommand = true;
            try
            {
                result = _console.ProcessNetCommand(Me(), line);
            }
            finally
            {
                _directCommand = false;
            }
        }
        if (result.task != null)
        {
            await result.task;
        }
        string text = result.msg.Replace("\n", "\n    ");
        if (result.success)
        {
            Say($"  ok: {text}");
        }
        else if (mayFail)
        {
            Say($"  refused: {text}");
        }
        else
        {
            throw new InvalidOperationException($"the command failed: {text}");
        }
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
    private static async Task EventOption(SceneTree tree, string key, double timeout, bool optional)
    {
        var sync = RunManager.Instance.EventSynchronizer;
        MegaCrit.Sts2.Core.Models.EventModel? model = sync.GetLocalEvent();
        if (model == null)
        {
            MissingStep(optional, "EVENTOPTION: no event");
            return;
        }
        List<MegaCrit.Sts2.Core.Events.EventOption> options = model.CurrentOptions.ToList();
        int index = options.FindIndex(o => o.TextKey.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            MissingStep(optional, $"EVENTOPTION: no {key} option ({string.Join(", ", options.Select(o => o.TextKey))})");
            return;
        }
        int before = Me()!.Deck.Cards.Count;
        double start = _clock.Elapsed.TotalSeconds;
        sync.ChooseLocalOption(index);
        await Until(tree, () => sync.GetLocalEvent()?.IsFinished ?? true, timeout, "event finished");
        Say($"  EVENTOPTION {options[index].TextKey}: {(_clock.Elapsed.TotalSeconds - start) * 1000:F0} ms, deck {before} -> {Me()!.Deck.Cards.Count}");
    }

    /// <summary>
    /// What a script asked for isn't there (an option, a card): the line fails, or with <c>optional</c> (a scenario where it
    /// legitimately lacks, e.g. a co-op client without the relic) it says so and goes on. A silent skip let both sides of an
    /// A/B skip the step and compare identical.
    /// </summary>
    private static void MissingStep(bool optional, string what)
    {
        if (!optional)
        {
            throw new InvalidOperationException(what + " (add 'optional' if this scenario may lack it)");
        }
        Say($"  {what}: skipped (optional)");
    }

    /// <summary>Script <c>restsite &lt;option class&gt; [optional]</c> (e.g. CloneRestSiteOption): picks it like a click, timed.</summary>
    private static async Task RestSite(string name, bool optional)
    {
        var sync = RunManager.Instance.RestSiteSynchronizer;
        List<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options = sync.GetLocalOptions().ToList();
        int index = options.FindIndex(o => o.GetType().Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            MissingStep(optional, $"RESTSITE: no {name} option ({string.Join(", ", options.Select(o => o.GetType().Name))})");
            return;
        }
        int before = Me()!.Deck.Cards.Count;
        long start = Stopwatch.GetTimestamp();
        bool ok = await sync.ChooseLocalOption(index);
        if (!ok)
        {
            throw new InvalidOperationException($"RESTSITE {name}: the game refused the option");
        }
        Say($"  RESTSITE {name}: ok in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms, deck {before} -> {Me()!.Deck.Cards.Count}");
    }

    /// <summary>Script <c>avoid ID,ID</c>: cards <c>play</c> leaves in hand (Apotheosis upgrades a whole huge deck at each play).</summary>
    private static readonly HashSet<string> _avoid = new();

    /// <summary>
    /// Script <c>play &lt;n&gt;</c>: plays up to n cards from hand, like clicks: each one waits for the game to be idle again. Up to:
    /// it stops when the combat ends or nothing is playable, and "played 0" is a normal outcome (a combat already won, an empty hand).
    /// </summary>
    private static async Task Play(SceneTree node, int count)
    {
        int played = 0;
        string? last = null;
        long lastStart = 0;
        for (int i = 0; i < count; i++)
        {
            await Until(node, () => !CombatManager.Instance.IsInProgress || IsIdle(), 120, "idle"); // stops once the combat is over
            if (last != null)
            {
                // From the click to when the player can act again: what a player waits for, animations included.
                Say($"  PLAYTIME {last} {Stopwatch.GetElapsedTime(lastStart).TotalMilliseconds:F0} ms");
            }
            Player? me = Me();
            CombatState? combat = CombatManager.Instance.DebugOnlyGetState();
            if (!CombatManager.Instance.IsInProgress || me?.PlayerCombatState == null || combat == null)
            {
                break;
            }
            Creature? enemy = combat.Enemies.FirstOrDefault(e => e.IsAlive);
            CardModel? card = PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => !_avoid.Contains(c.Id.Entry) && (c.CanPlayTargeting(enemy) || c.CanPlayTargeting(null)));
            if (card == null)
            {
                Say("  no playable card left");
                break;
            }
            Creature? target = card.CanPlayTargeting(enemy) ? enemy : null;
            Say($"  play {card.Id.Entry} -> {target?.ToString() ?? "no target"}");
            last = card.Id.Entry;
            lastStart = Stopwatch.GetTimestamp();
            LogCardPlays();
            _manualPlaysStarted.Clear();
            if (!card.TryManualPlay(target))
            {
                break;
            }
            played++;
            await Frames(node, 5);
            // In co-op the play goes through the networked action queue: the player can look idle before it ran, and the next pick
            // would take the same card again (the queue then drops that play). How fast the queue runs depends on the session (mod
            // off is slower), so a mod-on and a mod-off session played different cards: wait until the combat history saw this
            // card's play start (not "out of the hand": a card can come back to it during its own play).
            await Until(node, () => !CombatManager.Instance.IsInProgress || _manualPlaysStarted.Contains(card), 120, $"{card.Id.Entry}'s play started");
            _manualPlaysStarted.Clear(); // Its cards hold the run (leak checks after tomenu).
        }
        Say($"  played {played}");
    }

    /// <summary>
    /// Script <c>playcard &lt;CARD_ID&gt; [living enemy index] [free] [optional]</c>: plays that card from the hand, waits. "free" sets
    /// its energy and star costs to 0 this turn first (not X costs: X is the energy spent). Not in hand or not playable fails the
    /// line, unless "optional".
    /// </summary>
    private static async Task PlayCard(SceneTree tree, string id, int enemyIndex, bool free, bool optional)
    {
        await Until(tree, IsIdle, 120, "idle");
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        CombatState combat = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat");
        CardModel? card = PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => c.Id.Entry == id);
        if (card == null)
        {
            MissingStep(optional, $"{id} not in hand: use 'cmd card {id}' first");
            return;
        }
        // Through reflection: test tooling must keep loading on game versions where this member changed.
        object? energyCost = AccessTools.Property(typeof(CardModel), "EnergyCost")?.GetValue(card);
        bool costsX = energyCost != null && AccessTools.Property(energyCost.GetType(), "CostsX")?.GetValue(energyCost) is true;
        if (free && !costsX)
        {
            card.SetToFreeThisTurn();
        }
        Creature? enemy = combat.Enemies.Where(e => e.IsAlive).Skip(enemyIndex).FirstOrDefault();
        Creature? target = card.CanPlayTargeting(enemy) ? enemy : null;
        Say($"  playing {id} -> {target?.ToString() ?? "no target"}");
        long start = Stopwatch.GetTimestamp();
        if (!card.TryManualPlay(target))
        {
            MissingStep(optional, $"{id} cannot be played now");
            return;
        }
        await Frames(tree, 2);
        // Until the player can act again, or the combat is over (the card killed the last enemy).
        await Until(tree, () => IsIdle() || !CombatManager.Instance.IsInProgress, 600, "idle or combat end");
        Say($"  played {id} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms (until idle or combat end)");
    }

    /// <summary>Script <c>deckview [s] [screenshot name]</c>: opens the deck view (timed), keeps it open, closes it (timed).</summary>
    private static async Task DeckView(SceneTree tree, double seconds, string? screenshot)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        long start = Stopwatch.GetTimestamp();
        MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen.ShowScreen(me);
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
    }

    /// <summary>Script <c>decksort</c>: opens the deck view and sorts its grid by cost then title, as its sort buttons do.</summary>
    private static async Task DeckSort(SceneTree tree)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        var screen = MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen.ShowScreen(me) ?? throw new InvalidOperationException("no deck view");
        await Frames(tree, 30);
        NCardGrid grid = FindChild<NCardGrid>(screen) ?? throw new InvalidOperationException("no card grid in the deck view");
        long start = Stopwatch.GetTimestamp();
        grid.SetCards(me.Deck.Cards, PileType.Deck, new List<MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.SortingOrders>
        {
            MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.SortingOrders.CostAscending,
            MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.SortingOrders.AlphabetAscending,
            MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.SortingOrders.Ascending,
        });
        Say($"  deck sorted in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms ({me.Deck.Cards.Count} cards), sorts {GridSortCache.Sorts}");
        await Frames(tree, 10);
        MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer.Instance?.Close();
        await Frames(tree, 5);
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

    /// <summary>
    /// Script <c>baselibmenu &lt;screenshot name&gt; [row to hover]</c>: BaseLib's mod configuration menu on this mod's settings. By
    /// reflection: this DLL must not reference BaseLib (optional).
    /// </summary>
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

    /// <summary>Asks to end this player's turn (a click on the button): the action runs later, through the queue.</summary>
    private static void EndTurn()
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
    }

    /// <summary>
    /// Script <c>endturn</c>: ends the turn and waits until the game took it (this player's actions disabled or marked ready, the turn
    /// moved on, or the combat over). A co-op client's request only reaches its own queue when the host's echo comes back: without
    /// the wait, the next <c>waitfor turn</c> passed at once, and its <c>state</c> and <c>play</c> lines landed in the turn just ended.
    /// </summary>
    private static async Task EndTurnLine(SceneTree tree)
    {
        Player me = Me() ?? throw new InvalidOperationException("no local player");
        int turn = me.PlayerCombatState?.TurnNumber ?? throw new InvalidOperationException("not in combat");
        EndTurn();
        await Until(tree, () =>
        {
            CombatManager combat = CombatManager.Instance;
            return !combat.IsInProgress || combat.PlayerActionsDisabled || combat.IsPlayerReadyToEndTurn(me)
                || me.PlayerCombatState?.TurnNumber != turn;
        }, 120, "turn ended");
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
