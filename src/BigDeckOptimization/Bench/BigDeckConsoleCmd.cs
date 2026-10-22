using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// bigdeck console cmd. Inflates a player's deck and relics to reproduce big-deck lag.
///   bigdeck &lt;count&gt; [card-id] [seed] — fill the deck up to count, with one card or random character cards.
///   bigdeck relics &lt;count&gt; [relic-id] [seed] — add count relics, one kind or random from character + shared pools.
///   bigdeck import &lt;file.run|shared-name&gt; [all|i|i,j] [cards|relics|all] [hash] — add exact decks (upgrades,
///   enchantments) and relics from a run history file. In co-op only from the shared folder (user://bigdeck) with the
///   file hash, so every peer provably reads the same data.
///   bigdeck hash &lt;shared-name&gt; — print the hash of a shared file (each peer prints its own).
///
/// Networked: in co-op the game runs the command through the action queue on every peer, so it must be deterministic
/// (seeded System.Random, never the game's RNG) and EVERY player needs this mod, or peers diverge.
/// </summary>
public class BigDeckConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "bigdeck";

    public override string Args => "<count:int> [card-id] [seed:int] | relics <count:int> [relic-id] [seed:int] | import <file.run|shared-name> [all|i|i,j] [cards|relics|all] [hash] | hash <shared-name> | tank [hp]";

    public override string Description => "[BigDeckOptimization] Inflates your deck/relics for perf testing. In co-op, every player must have the mod.";

    public override bool IsNetworked => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, "Usage: bigdeck " + Args);
        }
        if (!RunManager.Instance.IsInProgress || issuingPlayer == null)
        {
            return new CmdResult(success: false, "A run is currently not in progress!");
        }
        ICardScope? scope = RunManager.Instance.DebugOnlyGetState();
        if (scope == null)
        {
            return new CmdResult(success: false, "No run state available.");
        }

        switch (args[0].ToLowerInvariant())
        {
            case "import":
                return Import(issuingPlayer, scope, args.Skip(1).ToArray());
            case "hash":
                return Hash(args.Skip(1).ToArray());
            case "tank":
                return Tank(args.Skip(1).ToArray());
            case "relics":
                return AddRelics(issuingPlayer, args.Skip(1).ToArray());
            default:
                return Fill(issuingPlayer, scope, args);
        }
    }

    // Any peer can issue a networked command: cap it so nobody can freeze everyone's game with a huge count.
    private const int MaxDeckSize = 5000;
    private const int MaxRelicsPerCommand = 1000;

    /// <summary>Seeded so every peer generates the same sequence. Seed 0 when omitted.</summary>
    private static Random SeededRandom(string[] args, int seedIndex) =>
        new(args.Length > seedIndex && int.TryParse(args[seedIndex], out int seed) ? seed : 0);

    private static CmdResult Fill(Player player, ICardScope scope, string[] args)
    {
        if (!int.TryParse(args[0], out int target) || target <= 0 || target > MaxDeckSize)
        {
            return new CmdResult(success: false, $"Count must be between 1 and {MaxDeckSize}.");
        }
        int missing = target - PileType.Deck.GetPile(player).Cards.Count;
        if (missing <= 0)
        {
            return new CmdResult(success: true, $"Deck already has {target} cards or more.");
        }

        List<CardModel> pool;
        int seedIndex = 1;
        if (args.Length >= 2 && !int.TryParse(args[1], out _))
        {
            string cardName = args[1].ToUpperInvariant();
            CardModel? card = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == cardName);
            if (card == null)
            {
                return new CmdResult(success: false, "Card '" + cardName + "' not found");
            }
            pool = new List<CardModel> { card };
            seedIndex = 2;
        }
        else
        {
            pool = player.Character.CardPool.AllCards.OrderBy(c => c.Id.Entry, StringComparer.Ordinal).ToList();
        }
        if (pool.Count == 0)
        {
            return new CmdResult(success: false, "Character card pool is empty.");
        }

        Random rng = SeededRandom(args, seedIndex);
        List<CardModel> cards = new(missing);
        for (int i = 0; i < missing; i++)
        {
            cards.Add(scope.CreateCard(pool[rng.Next(pool.Count)], player));
        }
        return AddToDeck(cards, $"Added {missing} cards, deck now has {target}.");
    }

    private static CmdResult AddRelics(Player player, string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int count) || count <= 0 || count > MaxRelicsPerCommand)
        {
            return new CmdResult(success: false, $"Usage: bigdeck relics <count 1-{MaxRelicsPerCommand}> [relic-id] [seed]");
        }

        List<RelicModel> pool;
        int seedIndex = 1;
        if (args.Length >= 2 && !int.TryParse(args[1], out _))
        {
            string relicName = args[1].ToUpperInvariant();
            RelicModel? relic = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == relicName);
            if (relic == null)
            {
                return new CmdResult(success: false, "Relic '" + relicName + "' not found");
            }
            pool = new List<RelicModel> { relic };
            seedIndex = 2;
        }
        else
        {
            pool = player.Character.RelicPool.AllRelics
                .Concat(ModelDb.RelicPool<SharedRelicPool>().AllRelics)
                .Distinct()
                .OrderBy(r => r.Id.Entry, StringComparer.Ordinal)
                .ToList();
        }
        if (pool.Count == 0)
        {
            return new CmdResult(success: false, "Relic pool is empty.");
        }

        // Same path as loading a save: adds relic state without running obtain effects (no gold, cards, choices...).
        Random rng = SeededRandom(args, seedIndex);
        for (int i = 0; i < count; i++)
        {
            AddRelic(player, pool[rng.Next(pool.Count)].ToMutable());
        }
        return new CmdResult(success: true, $"Added {count} relics, {player.Relics.Count} total.");
    }

    private const string ImportUsage =
        "Usage: bigdeck import <file.run|shared-name> [players: all|i|i,j] [cards|relics|all] [hash]. " +
        "Co-op: the file must be in the shared folder on every machine and the hash (from 'bigdeck hash <name>') is required.";

    /// <summary>
    /// Folder every player can put the same run history in, so a networked import reads identical data on each peer.
    /// </summary>
    private static string SharedFolder => ProjectSettings.GlobalizePath("user://bigdeck");

    private const long MaxRunFileBytes = 64L * 1024 * 1024;

    private static CmdResult Hash(string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, "Usage: bigdeck hash <shared-name>");
        }
        byte[]? bytes = ReadShared(args[0], out string? error);
        return bytes == null
            ? new CmdResult(success: false, error)
            : new CmdResult(success: true, $"{args[0]}: hash {ShortHash(bytes)} ({bytes.Length} bytes) in {SharedFolder}");
    }

    private static CmdResult Import(Player player, ICardScope scope, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, ImportUsage);
        }
        bool multiplayer = !RunManager.Instance.IsSingleplayerOrFakeMultiplayer;

        // Every check below must give the same answer on every peer, before anything is touched.
        byte[]? bytes;
        string? error;
        if (Path.IsPathRooted(args[0]))
        {
            if (multiplayer)
            {
                return new CmdResult(success: false, "In co-op, import only reads the shared folder: put the .run file in " + SharedFolder + " on every machine and pass its name.");
            }
            bytes = ReadFile(args[0], out error);
        }
        else
        {
            bytes = ReadShared(args[0], out error);
            if (bytes == null && !multiplayer)
            {
                // Singleplayer convenience: a run of the current profile's history.
                return ImportHistory(player, scope, LoadProfileHistory(args[0], out error), args, error);
            }
        }
        if (bytes == null)
        {
            return new CmdResult(success: false, error);
        }
        if (multiplayer)
        {
            string localHash = ShortHash(bytes);
            if (args.Length < 4 || !args[3].Equals(localHash, StringComparison.OrdinalIgnoreCase))
            {
                return new CmdResult(success: false, $"Hash required in co-op and must match on every machine (here: {localHash}). Run 'bigdeck hash {args[0]}' first.");
            }
        }
        RunHistory? history = ParseHistory(bytes, out error);
        return ImportHistory(player, scope, history, args, error);
    }

    private static CmdResult ImportHistory(Player player, ICardScope scope, RunHistory? history, string[] args, string? error)
    {
        if (history == null)
        {
            return new CmdResult(success: false, error);
        }
        if (history.Players.Count == 0)
        {
            return new CmdResult(success: false, "Run history has no players.");
        }

        // Default: the biggest deck. "all" or "i,j" merge players, to approximate a bigger co-op snapshot.
        List<RunHistoryPlayer> sources = new();
        if (args.Length < 2)
        {
            sources.Add(history.Players.MaxBy(p => p.Deck.Count())!);
        }
        else if (args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            sources.AddRange(history.Players);
        }
        else
        {
            foreach (string part in args[1].Split(','))
            {
                if (!int.TryParse(part, out int index) || index < 0 || index >= history.Players.Count)
                {
                    return new CmdResult(success: false, $"Players must be 'all' or indexes between 0 and {history.Players.Count - 1} (e.g. 0,1).");
                }
                sources.Add(history.Players[index]);
            }
        }

        string what = args.Length >= 3 ? args[2].ToLowerInvariant() : "all";
        if (what is not ("cards" or "relics" or "all"))
        {
            return new CmdResult(success: false, ImportUsage);
        }

        // Same path as loading a save: no "card added" hooks, so relics like BingBong don't duplicate imported cards.
        int cards = 0, relics = 0;
        foreach (RunHistoryPlayer source in sources)
        {
            if (what is "cards" or "all")
            {
                foreach (SerializableCard save in source.Deck)
                {
                    CardModel card = CardModel.FromSerializable(save);
                    scope.AddCard(card, player);
                    player.Deck.AddInternal(card);
                    cards++;
                }
            }
            if (what is "relics" or "all")
            {
                foreach (SerializableRelic save in source.Relics)
                {
                    AddRelic(player, RelicModel.FromSerializable(save));
                    relics++;
                }
            }
        }
        // AddInternal skips the UI notification the deck counter listens to.
        player.Deck.InvokeCardAddFinished();
        string from = string.Join(", ", sources.Select(s => s.Character.Entry));
        return new CmdResult(success: true, $"Imported {cards} cards and {relics} relics from {from} ({history.Players.Count} players in run). Deck: {player.Deck.Cards.Count} cards.");
    }

    /// <summary>
    /// Adds a relic like loading a save (no obtain effects), then reveals its icon in the local relic bar the way
    /// obtaining a relic does (the bar adds new holders hidden until animated).
    /// </summary>
    private static void AddRelic(Player player, RelicModel relic)
    {
        player.AddRelicInternal(relic);
        if (LocalContext.IsMe(player))
        {
            NRun.Instance?.GlobalUi.RelicInventory.AnimateRelic(relic);
        }
    }

    /// <summary>Reads a file of the shared folder. Bare names only: a peer can't make us read anything else.</summary>
    private static byte[]? ReadShared(string name, out string? error)
    {
        if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
        {
            error = "Shared files are given by bare name (no path).";
            return null;
        }
        string fileName = name.EndsWith(".run", StringComparison.OrdinalIgnoreCase) ? name : name + ".run";
        return ReadFile(Path.Combine(SharedFolder, fileName), out error);
    }

    private static byte[]? ReadFile(string path, out string? error)
    {
        error = null;
        FileInfo info = new(path);
        if (!info.Exists)
        {
            error = "File not found: " + path;
            return null;
        }
        if (info.Length > MaxRunFileBytes)
        {
            error = "File too large for a run history: " + path;
            return null;
        }
        return File.ReadAllBytes(path);
    }

    private static string ShortHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant();

    private static RunHistory? ParseHistory(byte[] bytes, out string? error)
    {
        error = null;
        ReadSaveResult<RunHistory> parsed = SaveManager.FromJson<RunHistory>(Encoding.UTF8.GetString(bytes));
        if (!parsed.Success || parsed.SaveData == null)
        {
            error = $"Could not parse run history: {parsed.Status}";
            return null;
        }
        return parsed.SaveData;
    }

    private static RunHistory? LoadProfileHistory(string name, out string? error)
    {
        error = null;
        string fileName = name.EndsWith(".run", StringComparison.OrdinalIgnoreCase) ? name : name + ".run";
        ReadSaveResult<RunHistory> result = SaveManager.Instance.LoadRunHistory(fileName);
        if (!result.Success || result.SaveData == null)
        {
            error = $"'{fileName}' not found in {SharedFolder} nor in the current profile's history: {result.Status}. Pass an absolute path for other profiles.";
            return null;
        }
        return result.SaveData;
    }

    private const int MaxTankHp = 100_000_000;

    /// <summary>Sets every living enemy's max and current HP, so long combats can be played for perf testing.</summary>
    private static CmdResult Tank(string[] args)
    {
        CombatState? combat = CombatManager.Instance.IsInProgress ? CombatManager.Instance.DebugOnlyGetState() : null;
        if (combat == null)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        int hp = 1_000_000;
        if (args.Length >= 1 && (!int.TryParse(args[0], out hp) || hp <= 0 || hp > MaxTankHp))
        {
            return new CmdResult(success: false, $"HP must be between 1 and {MaxTankHp}.");
        }
        List<Creature> enemies = combat.Enemies.Where(e => e.IsAlive).ToList();
        return new CmdResult(SetHp(enemies, hp), success: true, $"Set {enemies.Count} enemies to {hp} HP.");
    }

    private static async Task SetHp(List<Creature> enemies, int hp)
    {
        foreach (Creature enemy in enemies)
        {
            await CreatureCmd.SetMaxAndCurrentHp(enemy, hp);
        }
    }

    private static CmdResult AddToDeck(List<CardModel> cards, string message)
    {
        Task task = CardPileCmd.Add(cards, PileType.Deck, skipVisuals: true);
        return new CmdResult(task, success: true, message);
    }
}
