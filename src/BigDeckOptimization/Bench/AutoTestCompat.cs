using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Game APIs the test tooling calls whose signature changed between supported versions, called through reflection so
/// one build runs on all of them. Game v0.113 (beta): <c>NGame.StartNewSingleplayerRun</c> takes a <c>Player</c> and
/// <c>isCustomGame</c>, <c>StartRunLobby.SetLocalCharacter</c> gained <c>isRotatingDread</c>, <c>AutoSlayer.Start</c> a
/// <c>mode</c>, shop purchases go through <c>RunManager.MerchantSynchronizer.PurchaseLocalEntry</c>. Missing parameters
/// get their default value (or the one named here).
/// </summary>
public static partial class AutoTest
{
    /// <summary>Invokes <paramref name="method"/> with the leading <paramref name="leading"/> arguments, the named ones,
    /// and every other parameter's default value.</summary>
    private static object? InvokeWithDefaults(object? target, MethodInfo method, object?[] leading, params (string Name, object? Value)[] named)
    {
        object?[] args = method.GetParameters().Select((p, i) =>
            i < leading.Length ? leading[i]
            : named.FirstOrDefault(n => n.Name == p.Name) is { Name: not null } n ? n.Value
            : p.HasDefaultValue ? p.DefaultValue
            : throw new MissingMethodException($"{method.DeclaringType?.Name}.{method.Name}: no value for {p.Name}")).ToArray();
        return method.Invoke(target, args);
    }

    private static MethodInfo Overload(Type type, string name, Func<ParameterInfo[], bool> match) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).FirstOrDefault(m => m.Name == name && match(m.GetParameters()))
        ?? throw new MissingMethodException(type.Name, name);

    /// <summary>A new singleplayer run as the character select screen starts one (v0.107/v0.111: from the character;
    /// v0.113: from a player made the way the game's own singleplayer start makes it).</summary>
    private static Task StartSingleplayerRun(NGame game, CharacterModel character, string seed) => StartSingleplayerRun(game, character, seed, GameMode.Standard);

    /// <summary>The same in a game mode (Dread, Blitz: v0.113+, <see cref="ParseGameMode"/>).</summary>
    private static Task StartSingleplayerRun(NGame game, CharacterModel character, string seed, GameMode mode)
    {
        var acts = ActModel.GetDefaultList().ToList();
        MethodInfo start = Overload(typeof(NGame), nameof(NGame.StartNewSingleplayerRun),
            p => p.Length > 0 && (p[0].ParameterType == typeof(CharacterModel) || p[0].ParameterType == typeof(Player)));
        object first = start.GetParameters()[0].ParameterType == typeof(Player)
            ? Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL)
            : character;
        return (Task)InvokeWithDefaults(game, start, new object?[] { first, true, acts, Array.Empty<ModifierModel>(), seed, mode },
            ("isCustomGame", false))!;
    }

    private static void SetLobbyCharacter(object lobby, CharacterModel character) =>
        InvokeWithDefaults(lobby, Overload(lobby.GetType(), "SetLocalCharacter", p => p.Length > 0 && p[0].ParameterType == typeof(CharacterModel)),
            new object?[] { character });

    private static void StartAutoSlayer(string seed) => StartAutoSlayer(seed, null);

    /// <summary>The game's auto player in a mode (v0.113's <c>--mode</c>: dread, blitz; null: standard). False, nothing started,
    /// when this game version's auto player has no mode (v0.107.1).</summary>
    private static bool StartAutoSlayer(string seed, string? mode)
    {
        var slayer = new MegaCrit.Sts2.Core.AutoSlay.AutoSlayer();
        MethodInfo start = Overload(slayer.GetType(), "Start", p => p.Length > 0 && p[0].ParameterType == typeof(string));
        if (mode != null && !start.GetParameters().Any(p => p.Name == "mode"))
        {
            return false;
        }
        // v0.113's mode has no default: null is a standard run.
        InvokeWithDefaults(slayer, start, new object?[] { seed }, ("mode", mode));
        return true;
    }

    /// <summary>
    /// A game mode by name, as this game version's enum has it: Dread and Blitz exist from v0.113 only (this build may be
    /// compiled against v0.107.1, whose enum lacks them: never named in code). Null when this version has no such mode.
    /// </summary>
    private static GameMode? ParseGameMode(string name) =>
        Enum.GetNames(typeof(GameMode)).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found
            ? (GameMode)Enum.Parse(typeof(GameMode), found) : null;

    /// <summary>A game mode whose run models and dominion the mirrored walks reach (Dread, Blitz) was started in this session:
    /// their effect counters are listed from then on.</summary>
    private static bool _modeRun;

    /// <summary>
    /// Script <c>dominion [require]</c>: <c>STATE dominion: run &lt;id&gt;, combat &lt;id&gt;, run models &lt;types&gt;</c> (v0.113's
    /// Dread mode sets the run's dominion at each act entry and gives it to each combat built). "require": fails unless the
    /// current combat has one (a scenario meant to test the walks' dominion tail). Through reflection: absent before v0.113.
    /// </summary>
    private static void LogDominion(string arg)
    {
        object? run = RunManager.Instance.DebugOnlyGetState();
        object? combat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
        string Read(object? owner, string property) =>
            owner == null ? "-" : AccessTools.Property(owner.GetType(), property) is { } info
                ? info.GetValue(owner) switch
                {
                    null => "none",
                    AbstractModel model => model.Id.Entry,
                    System.Collections.IEnumerable list => "[" + string.Join(" ", list.Cast<object>().Select(m => m is AbstractModel am ? am.Id.Entry : m.GetType().Name)) + "]",
                    var other => other.ToString() ?? "?",
                }
                : "absent";
        string combatDominion = Read(combat, "Dominion");
        Say($"  STATE dominion: run {Read(run, "Dominion")}, combat {combatDominion}, run models {Read(run, "RunModels")}, combat run models {Read(combat, "RunModels")}");
        if (arg == "require" && combatDominion is "none" or "-" or "absent")
        {
            throw new InvalidOperationException($"the combat has no dominion ({combatDominion})");
        }
    }

    /// <summary>
    /// A purchase as the shop screen makes it: v0.113 through the run's merchant synchronizer (synced in co-op), before
    /// through the entry's own wrapper.
    /// </summary>
    private static Task<bool> TryPurchase(MerchantEntry entry, MerchantInventory? inventory)
    {
        if (AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")?.GetValue(RunManager.Instance) is { } synchronizer
            && AccessTools.Method(synchronizer.GetType(), "PurchaseLocalEntry", new[] { typeof(MerchantEntry) }) is { } purchase)
        {
            return (Task<bool>)purchase.Invoke(synchronizer, new object[] { entry })!;
        }
        return (Task<bool>)InvokeWithDefaults(entry, Overload(entry.GetType(), "OnTryPurchaseWrapper",
            p => p.Length > 0 && p[0].ParameterType == typeof(MerchantInventory)), new object?[] { inventory })!;
    }
}
