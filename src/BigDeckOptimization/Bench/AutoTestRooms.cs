using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Handlers;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Rooms outside combat, played one at a time by the game's own auto player (AutoSlay's room and screen handlers): a scenario
/// picks where to go (<c>cmd room Shop</c>, <c>cmd event ID</c>, <c>automap</c>), plays it (<c>autoroom</c>) and logs the run's
/// state (<c>runstate</c>: STATE lines, which compare.py checks line for line between two sessions).
/// </summary>
public static partial class AutoTest
{
    private static Dictionary<RoomType, IRoomHandler>? _roomHandlers;
    private static Dictionary<Type, IScreenHandler>? _screenHandlers;
    private static IHandler? _mapHandler;
    private static Rng? _roomRng;
    private static readonly List<string> _screensHandled = new();

    /// <summary>
    /// The game's handlers, found by their interfaces: the same code on every version. Game v0.111's shop handler takes a function
    /// that resolves the screens a purchase opens (the purchase waits for them): it gets <see cref="DrainScreensUntil"/>.
    /// </summary>
    private static void EnsureRoomHandlers()
    {
        if (_roomHandlers != null)
        {
            return;
        }
        // Their waits are short for a big deck or a co-op peer (a co-op treasure waits for every player's vote): autoslay's limits.
        PatchAutoSlayLimits();
        // Same seed on every peer and every session: the same picks for the same state.
        ConstructorInfo rngCtor = typeof(Rng).GetConstructors().Single(c => c.GetParameters() is { Length: 2 } p && p[1].ParameterType == typeof(string));
        _roomRng = (Rng)rngCtor.Invoke(new[] { Convert.ChangeType(1234u, rngCtor.GetParameters()[0].ParameterType), "bdtest" });
        _roomHandlers = new Dictionary<RoomType, IRoomHandler>();
        _screenHandlers = new Dictionary<Type, IScreenHandler>();
        foreach (Type type in typeof(IHandler).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(IHandler).IsAssignableFrom(t)))
        {
            IHandler handler = NewHandler(type);
            if (type.Name == "MapScreenHandler")
            {
                _mapHandler = handler; // Travels; not one of the screens to drain.
            }
            else if (handler is IRoomHandler room)
            {
                foreach (RoomType handled in room.HandledTypes)
                {
                    _roomHandlers[handled] = room;
                }
            }
            else if (handler is IScreenHandler screen)
            {
                _screenHandlers[screen.ScreenType] = screen;
            }
        }
        Say($"  room handlers: {string.Join(", ", _roomHandlers.Keys)}; screen handlers {_screenHandlers.Count}, map {_mapHandler != null}");
    }

    private static IHandler NewHandler(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is { } plain)
        {
            return (IHandler)plain.Invoke(null);
        }
        ConstructorInfo withDrain = type.GetConstructors().Single(c => c.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(Func<Task, CancellationToken, Task>));
        return (IHandler)withDrain.Invoke(new object[] { (Func<Task, CancellationToken, Task>)DrainScreensUntil });
    }

    private static Task DrainScreensUntil(Task task, CancellationToken ct) => DrainScreens(task, ct);

    /// <summary>
    /// The screens on the overlay stack handled one by one by the game's handlers until none is left (and, with <paramref name="until"/>,
    /// that task has ended), as AutoSlay does between rooms. A screen still there after three tries fails the line.
    /// </summary>
    private static async Task DrainScreens(Task? until, CancellationToken ct)
    {
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        HashSet<IOverlayScreen> seen = new();
        int stuck = 0;
        double waitingSince = _clock.Elapsed.TotalSeconds;
        bool told = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            IOverlayScreen? top = NOverlayStack.Instance is { ScreenCount: > 0 } stack ? stack.Peek() : null;
            if (top == null || !_screenHandlers!.TryGetValue(top.GetType(), out IScreenHandler? handler))
            {
                if (until == null || until.IsCompleted)
                {
                    if (top != null)
                    {
                        Say($"  no handler for screen {top.GetType().Name}");
                    }
                    break;
                }
                if (!told && _clock.Elapsed.TotalSeconds - waitingSince > 30)
                {
                    told = true;
                    Say($"  still waiting after 30 s, overlay {top?.GetType().Name ?? "none"}, focus {tree.Root.GuiGetFocusOwner()?.GetPath()}");
                }
                await Frames(tree, 1);
                continue;
            }
            waitingSince = _clock.Elapsed.TotalSeconds;
            if (!seen.Add(top))
            {
                if (++stuck >= 20)
                {
                    throw new InvalidOperationException($"screen {top.GetType().Name} not closing after being handled 20 times in 10 s");
                }
                // Still there: its handler found it not ready (a grid of 2,000 cards not filled yet: the beta's upgrade handler then
                // returns at once, "No cards found"). Headless frames take no time: wait in time.
                if (stuck == 3 || stuck == 19)
                {
                    DescribeScreen((Node)top);
                }
                await Seconds(tree, 0.5);
            }
            _screensHandled.Add(top.GetType().Name);
            if (top is NDeckEnchantSelectScreen enchant)
            {
                await EnchantScreen(tree, enchant, ct);
            }
            else
            {
                await handler.HandleAsync(_roomRng!, ct);
            }
            if (top is NRewardsScreen && (NMapScreen.Instance?.IsOpen ?? false))
            {
                break;
            }
            await Seconds(tree, 0.1); // As AutoSlay waits between screens.
        }
        if (until != null)
        {
            await until;
        }
    }

    /// <summary>
    /// Script <c>autoroom [remove] [timeout s]</c>: plays the current room with the game's handler (shop: buys what it can afford;
    /// rest site: a random option; treasure; event: random options), then every screen it leaves (rewards, card rewards, upgrade,
    /// transform, enchant and card choices), then the rest site's or event's proceed. A combat room must be won first: its rewards
    /// are taken. <c>remove</c> (shop): the card removal service first. Logs an <c>AUTOROOM</c> line.
    /// </summary>
    private static async Task AutoRoom(SceneTree tree, string arg)
    {
        EnsureRoomHandlers();
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double timeout = parts.Where(p => char.IsDigit(p[0])).Select(double.Parse).DefaultIfEmpty(300).First();
        RunState run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        Player me = Me()!;
        RoomType type = run.CurrentRoom?.RoomType ?? RoomType.Unassigned;
        (int deck, int gold, int relics) = (me.Deck.Cards.Count, me.Gold, me.Relics.Count);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeout));
        CancellationToken ct = cts.Token;
        long start = Stopwatch.GetTimestamp();
        _screensHandled.Clear();
        string? restOption = parts.FirstOrDefault(p => p.EndsWith("RestSiteOption", StringComparison.Ordinal));
        if (type != RoomType.Map && NMapScreen.Instance is { IsOpen: true } map)
        {
            // A debug jump (cmd room / cmd event) leaves the map open, as travel never does: game v0.111 hides every screen pushed
            // while the map is open (NOverlayStack.StackIsCovered), and the handlers skip a hidden screen. Closed as travel would.
            map.Close(animateOut: false);
            Say("  map closed (left open by a jump to this room)");
        }
        if (type is RoomType.Monster or RoomType.Elite or RoomType.Boss)
        {
            await AutoCombat(tree, timeout);
            await WaitHelper.Until(() => NOverlayStack.Instance?.Peek() is NRewardsScreen || (NMapScreen.Instance?.IsOpen ?? false), ct, TimeSpan.FromSeconds(30), "no rewards screen after the combat");
        }
        else if (type == RoomType.RestSite && restOption != null)
        {
            await RestSiteOption(tree, restOption, ct);
        }
        else
        {
            if (type == RoomType.Shop)
            {
                await Shop(tree, parts.Contains("remove"), ct);
            }
            else if (_roomHandlers!.TryGetValue(type, out IRoomHandler? handler))
            {
                await handler.HandleAsync(_roomRng!, ct);
            }
            else
            {
                Say($"  no handler for room {type}");
            }
        }
        await DrainScreens(null, ct);
        if (type == RoomType.RestSite && tree.Root.GetNodeOrNull<NProceedButton>("/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom/ProceedButton") is { IsEnabled: true } restProceed)
        {
            await UiHelper.Click(restProceed);
        }
        if (type == RoomType.Event)
        {
            await EventProceed(tree, timeout, ct);
        }
        await Frames(tree, 10);
        Say($"  AUTOROOM {type}: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms, deck {deck} -> {me.Deck.Cards.Count}, gold {gold} -> {me.Gold}, "
            + $"relics {relics} -> {me.Relics.Count}, screens [{string.Join(", ", _screensHandled)}]");
    }

    /// <summary>
    /// A combat room's fight, played like <c>play</c> and <c>endturn</c> until it ends. Not the game's combat handler: it applies
    /// powers outside the action queue (a co-op peer never sees them). Choices from the hand need the automatic selector meanwhile.
    /// </summary>
    private static async Task AutoCombat(SceneTree tree, double timeout)
    {
        IDisposable? selector = _selector == null && _combatSelector == null ? PushSelector(new FirstCardsSelector()) : null;
        try
        {
            double end = _clock.Elapsed.TotalSeconds + timeout;
            for (int turn = 1; CombatManager.Instance.IsInProgress; turn++)
            {
                if (_clock.Elapsed.TotalSeconds > end)
                {
                    throw new TimeoutException($"combat not over after {timeout:F0} s");
                }
                await Until(tree, () => !CombatManager.Instance.IsInProgress || IsIdle(), timeout, $"turn {turn}");
                int round = CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0;
                // In co-op only the host plays: two peers playing at once interleave their actions by timing, and the random draws
                // after them (rewards) with it, so two sessions (mod off and on) would differ while each pair still agrees.
                if (RunManager.Instance.NetService.Type != MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
                {
                    await Play(tree, 10);
                }
                if (CombatManager.Instance.IsInProgress && IsIdle())
                {
                    // In co-op a player who ended the turn is idle at once: the next round tells the turn is over for both.
                    EndTurn();
                    await Until(tree, () => !CombatManager.Instance.IsInProgress || CombatManager.Instance.DebugOnlyGetState()?.RoundNumber > round, timeout, "next round");
                }
            }
        }
        finally
        {
            selector?.Dispose();
        }
    }

    /// <summary>A screen its handler can't close: what it holds (cards offered, how many to pick, the nodes under it by type).</summary>
    private static void DescribeScreen(Node screen)
    {
        string cards = "";
        if (screen is NCardGridSelectionScreen grid)
        {
            var offered = AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards").GetValue(grid) as IReadOnlyList<CardModel>;
            object? prefs = AccessTools.Field(screen.GetType(), "_prefs")?.GetValue(screen);
            string Pref(string name) => prefs?.GetType().GetField(name)?.GetValue(prefs)?.ToString() ?? prefs?.GetType().GetProperty(name)?.GetValue(prefs)?.ToString() ?? "?";
            cards = $"cards offered {offered?.Count}, upgradable {offered?.Count(c => c.IsUpgradable)}, min {Pref("MinSelect")}, max {Pref("MaxSelect")}, cancelable {Pref("Cancelable")}; ";
        }
        string nodes = string.Join(", ", FindAll<Node>(screen).GroupBy(n => n.GetType().Name).OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} {g.Count()}"));
        Say($"  SCREEN {screen.GetType().Name}: visible {((CanvasItem)screen).IsVisibleInTree()}, {cards}nodes: {nodes}");
    }

    /// <summary>
    /// The enchant choice, answered here rather than by the game's handler: when the screen allows choosing no card (<c>MinSelect</c> 0,
    /// "up to N"), that handler confirms with none selected, and the screen's confirm does nothing without a selection: it never closes
    /// (seen buying a relic in the shop, mod off as well). One random card, then the confirm of whichever preview shows.
    /// </summary>
    private static async Task EnchantScreen(SceneTree tree, NDeckEnchantSelectScreen screen, CancellationToken ct)
    {
        List<NGridCardHolder> cards = new();
        await WaitHelper.Until(() => (cards = UiHelper.FindAll<NGridCardHolder>(screen)).Count > 0, ct, TimeSpan.FromSeconds(10), "no cards in the enchant screen");
        await Seconds(tree, 0.3);
        NGridCardHolder card = _roomRng!.NextItem(cards)!;
        card.EmitSignal(NCardHolder.SignalName.Pressed, card);
        await Seconds(tree, 0.3);
        Control Field(string name) => (Control)AccessTools.Field(typeof(NDeckEnchantSelectScreen), name).GetValue(screen)!;
        Control single = Field("_enchantSinglePreviewContainer"), multi = Field("_enchantMultiPreviewContainer");
        if (!single.Visible && !multi.Visible && Field("_confirmButton") is NClickableControl { IsEnabled: true } main)
        {
            await UiHelper.Click(main);
        }
        await WaitHelper.Until(() => single.Visible || multi.Visible, ct, TimeSpan.FromSeconds(10), "no enchant preview");
        await Seconds(tree, 0.3);
        await UiHelper.Click((NClickableControl)Field(single.Visible ? "_singlePreviewConfirmButton" : "_multiPreviewConfirmButton"));
        await WaitHelper.Until(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(), ct, TimeSpan.FromSeconds(30), "enchant screen still open after its confirm");
    }

    private static IDisposable? _combatSelector;
    private static bool _watchingCombats;

    /// <summary>
    /// With <c>selector ui</c>, fights still get the automatic selector, from their setup to their end: a relic's choice at the first
    /// turn (or any choice from the hand) would otherwise wait for a player, whatever line the script is on then.
    /// </summary>
    private static void WatchCombatsForSelector()
    {
        if (_watchingCombats)
        {
            return;
        }
        _watchingCombats = true;
        CombatManager.Instance.CombatSetUp += _ =>
        {
            if (_uiSelection && _combatSelector == null)
            {
                _combatSelector = PushSelector(new FirstCardsSelector());
            }
        };
        CombatManager.Instance.CombatEnded += _ =>
        {
            _combatSelector?.Dispose();
            _combatSelector = null;
        };
        if (CombatManager.Instance.IsInProgress && _combatSelector == null)
        {
            _combatSelector = PushSelector(new FirstCardsSelector());
        }
    }

    /// <summary>One rest site option, clicked like the game's handler clicks its random pick; its screen, if any, is left to the drain.</summary>
    private static async Task RestSiteOption(SceneTree tree, string optionType, CancellationToken ct)
    {
        NRestSiteRoom room = await WaitHelper.ForNode<NRestSiteRoom>(tree.Root, "/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom", ct);
        await WaitHelper.Until(() => UiHelper.FindAll<NRestSiteButton>(room).Count > 0, ct, TimeSpan.FromSeconds(10), "no rest site buttons");
        List<NRestSiteButton> buttons = UiHelper.FindAll<NRestSiteButton>(room);
        NRestSiteButton? button = buttons.FirstOrDefault(b => b.Option.IsEnabled && b.Option.GetType().Name == optionType);
        if (button == null)
        {
            Say($"  no enabled {optionType} ({string.Join(", ", buttons.Select(b => b.Option.GetType().Name + (b.Option.IsEnabled ? "" : " (disabled)")))})");
            return;
        }
        await UiHelper.Click(button);
        await WaitHelper.Until(() => room.ProceedButton.IsEnabled || NOverlayStack.Instance is { ScreenCount: > 0 }, ct, TimeSpan.FromSeconds(30), "rest site option did not respond");
    }

    /// <summary>
    /// The shop as the game's handler plays it in v0.111 (random affordable items until none is left, then leave), on every version:
    /// v0.107's handler awaits a purchase without answering the screen it opens. With <paramref name="remove"/>, the card removal
    /// service first. Each purchase is logged (a purchase that never ends then names itself).
    /// </summary>
    private static async Task Shop(SceneTree tree, bool remove, CancellationToken ct)
    {
        NMerchantRoom room = await WaitHelper.ForNode<NMerchantRoom>(tree.Root, "/root/Game/RootSceneContainer/Run/RoomContainer/MerchantRoom", ct);
        room.OpenInventory();
        await Frames(tree, 30);
        if (remove)
        {
            NMerchantSlot? removal = room.Inventory.GetAllSlots().FirstOrDefault(s => s is NMerchantCardRemoval);
            if (removal == null || !removal.Entry.IsStocked || !removal.Entry.EnoughGold)
            {
                Say($"  no card removal to buy (found {removal != null}, stocked {removal?.Entry.IsStocked}, gold {removal?.Entry.EnoughGold})");
            }
            else
            {
                int deck = Me()!.Deck.Cards.Count;
                await DrainScreens(removal.Entry.OnTryPurchaseWrapper(room.Inventory.Inventory), ct);
                Say($"  card removal bought: deck {deck} -> {Me()!.Deck.Cards.Count}");
            }
        }
        List<string> bought = new();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            List<NMerchantSlot> affordable = room.Inventory.GetAllSlots().Where(s => s is not NMerchantCardRemoval && s.Entry.IsStocked && s.Entry.EnoughGold).ToList();
            if (affordable.Count == 0)
            {
                break;
            }
            NMerchantSlot slot = _roomRng!.NextItem(affordable)!;
            bought.Add($"{slot.Entry.GetType().Name.Replace("Merchant", "").Replace("Entry", "")} {slot.Entry.Cost}");
            await DrainScreens(slot.Entry.OnTryPurchaseWrapper(room.Inventory.Inventory), ct);
            await Frames(tree, 10);
        }
        Say($"  bought: {string.Join(", ", bought)}");
        if (UiHelper.FindFirst<NBackButton>(room) is { } back)
        {
            await UiHelper.Click(back);
            await Frames(tree, 20);
        }
        await UiHelper.Click(room.ProceedButton);
    }

    /// <summary>The event's proceed option once it shows, unless the map is already open (AutoSlay's own step after an event).</summary>
    private static async Task EventProceed(SceneTree tree, double timeout, CancellationToken ct)
    {
        // An event can go on after the handler's one choice: another page (Dense Vegetation's rest, then "Fight"), a fight in the event
        // room, its rewards. Each played in turn until a proceed option or the map shows.
        for (int pass = 0; pass < 12; pass++)
        {
            Node? eventRoom = tree.Root.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
            if (eventRoom == null || (NMapScreen.Instance?.IsOpen ?? false))
            {
                return;
            }
            List<NEventOptionButton> options = new();
            await WaitHelper.Until(() => (NMapScreen.Instance?.IsOpen ?? false) || CombatManager.Instance.IsInProgress
                || (options = UiHelper.FindAll<NEventOptionButton>(eventRoom).Where(o => !o.Option.IsLocked).ToList()).Count > 0,
                ct, TimeSpan.FromSeconds(10), "no event option, no fight and no map");
            if (CombatManager.Instance.IsInProgress)
            {
                await AutoCombat(tree, timeout);
                await WaitHelper.Until(() => NOverlayStack.Instance is { ScreenCount: > 0 } || (NMapScreen.Instance?.IsOpen ?? false)
                    || UiHelper.FindAll<NEventOptionButton>(eventRoom).Any(o => !o.Option.IsLocked), ct, TimeSpan.FromSeconds(30), "nothing after the event's fight");
                await DrainScreens(null, ct);
                continue;
            }
            if (NMapScreen.Instance?.IsOpen ?? false)
            {
                return;
            }
            if (options.FirstOrDefault(o => o.Option.IsProceed) is { } proceed)
            {
                await UiHelper.Click(proceed);
                return;
            }
            Say($"  event goes on: {string.Join(", ", options.Select(o => o.Option.TextKey))}");
            await _roomHandlers![RoomType.Event].HandleAsync(_roomRng!, ct);
            await DrainScreens(null, ct);
        }
        throw new InvalidOperationException("the event didn't end after 12 pages");
    }

    /// <summary>
    /// Script <c>automap [timeout s]</c>: the game's map handler picks the next room and travels there (in co-op, both peers' votes);
    /// waits until that room is entered. Logs an <c>AUTOMAP</c> line.
    /// </summary>
    private static async Task AutoMap(SceneTree tree, string arg)
    {
        EnsureRoomHandlers();
        double timeout = arg.Trim().Length > 0 ? double.Parse(arg) : 300;
        RunState run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        int floor = run.TotalFloor;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeout));
        long start = Stopwatch.GetTimestamp();
        await (_mapHandler ?? throw new InvalidOperationException("no map handler")).HandleAsync(_roomRng!, cts.Token);
        await Until(tree, () => run.TotalFloor > floor && run.CurrentRoom is { RoomType: not RoomType.Map and not RoomType.Unassigned }, timeout, "next room");
        Say($"  AUTOMAP -> {run.CurrentRoom!.RoomType} (act {run.CurrentActIndex + 1}, floor {run.ActFloor}) in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// Script <c>continuerun [timeout s]</c>: on the main menu (after <c>tomenu</c>), clicks Continue like a player: the run comes
    /// back from its save. With <c>runstate</c> before leaving and after, the save and load of a big run are compared.
    /// </summary>
    private static async Task ContinueRun(SceneTree tree, string arg)
    {
        double timeout = arg.Trim().Length > 0 ? double.Parse(arg) : 300;
        await Until(tree, () => NGame.Instance?.MainMenu != null, timeout, "main menu");
        NMainMenu menu = NGame.Instance!.MainMenu!;
        NClickableControl button = (NClickableControl)AccessTools.Field(typeof(NMainMenu), "_continueButton").GetValue(menu)!;
        await Until(tree, () => button.IsVisibleInTree() && button.IsEnabled, 60, "continue button");
        long start = Stopwatch.GetTimestamp();
        await UiHelper.Click(button);
        await Until(tree, () => RunManager.Instance.IsInProgress && NRun.Instance != null && RunManager.Instance.DebugOnlyGetState()?.CurrentRoom != null, timeout, "run continued");
        if (!_uiSelection)
        {
            _selector ??= PushSelector(new FirstCardsSelector());
        }
        Say($"  CONTINUED in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms: room {RunManager.Instance.DebugOnlyGetState()!.CurrentRoom!.RoomType}, deck {Me()?.Deck.Cards.Count}");
    }

    /// <summary>
    /// Script <c>barrier &lt;label&gt; [timeout s]</c>: in co-op, waits until every player got to this label (a networked
    /// <c>bigdeck mark</c>), so one peer doesn't leave a room or read the run's state while another is still playing it.
    /// </summary>
    private static async Task Barrier(SceneTree tree, string arg)
    {
        string[] parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double timeout = parts.Length > 1 && char.IsDigit(parts[^1][0]) ? double.Parse(parts[^1]) : 600;
        string label = parts[0];
        RunState run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        await Command("bigdeck mark " + label);
        await Until(tree, () =>
        {
            lock (BigDeckConsoleCmd.Marks)
            {
                return run.Players.All(p => BigDeckConsoleCmd.Marks.Contains((p.NetId, label)));
            }
        }, timeout, "barrier " + label);
    }

    /// <summary>
    /// Script <c>runstate [label]</c>: outside combat, each player's gold, HP, potions, relics and deck (a hash of every card's id,
    /// upgrade and enchantment, in deck order) as STATE lines. In a combat room (a run continued into one), once the combat has
    /// started (its start-of-combat effects change HP and cards, and with this mod it gets there sooner): the same point either way.
    /// </summary>
    private static async Task LogRunState(SceneTree tree, string label)
    {
        if (CombatManager.Instance.IsInProgress)
        {
            await Until(tree, () => !CombatManager.Instance.IsInProgress || IsIdle(), 300, "combat started");
        }
        RunState run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no run");
        // "local": this player only (a co-op shop: the other players' purchases reach this peer when they leave it).
        bool local = label.EndsWith(" local", StringComparison.Ordinal);
        if (local)
        {
            label = label[..^" local".Length] + " (local)";
        }
        Say($"  STATE {label}: floor {run.TotalFloor}, act {run.CurrentActIndex + 1}, room {run.CurrentRoom?.RoomType}");
        foreach (Player player in local ? new[] { Me()! } : run.Players)
        {
            ulong deckHash = 14695981039346656037ul;
            int upgraded = 0, enchanted = 0;
            foreach (CardModel card in player.Deck.Cards)
            {
                deckHash = Fnv(deckHash, $"{card.Id.Entry}+{card.CurrentUpgradeLevel}/{card.Enchantment?.Id.Entry}:{card.Enchantment?.Amount};");
                upgraded += card.CurrentUpgradeLevel > 0 ? 1 : 0;
                enchanted += card.Enchantment != null ? 1 : 0;
            }
            ulong relicHash = player.Relics.Aggregate(14695981039346656037ul, (h, r) => Fnv(h, r.Id.Entry + ";"));
            Say($"  STATE {label}: {player.Character.Id.Entry} gold {player.Gold}, hp {player.Creature.CurrentHp}/{player.Creature.MaxHp}, "
                + $"potions [{string.Join(", ", player.Potions.Select(p => p.Id.Entry))}], relics {player.Relics.Count} {relicHash:x16}, "
                + $"deck {player.Deck.Cards.Count} {deckHash:x16} (upgraded {upgraded}, enchanted {enchanted})");
        }
    }

    /// <summary>FNV-1a over a string's chars: the same value in every process (string.GetHashCode is randomized per process).</summary>
    private static ulong Fnv(ulong hash, string text)
    {
        foreach (char c in text)
        {
            hash = (hash ^ c) * 1099511628211ul;
        }
        return hash;
    }
}
