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
    private static Task StartSingleplayerRun(NGame game, CharacterModel character, string seed)
    {
        var acts = ActModel.GetDefaultList().ToList();
        MethodInfo start = Overload(typeof(NGame), nameof(NGame.StartNewSingleplayerRun),
            p => p.Length > 0 && (p[0].ParameterType == typeof(CharacterModel) || p[0].ParameterType == typeof(Player)));
        object first = start.GetParameters()[0].ParameterType == typeof(Player)
            ? Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL)
            : character;
        return (Task)InvokeWithDefaults(game, start, new object?[] { first, true, acts, Array.Empty<ModifierModel>(), seed, GameMode.Standard },
            ("isCustomGame", false))!;
    }

    private static void SetLobbyCharacter(object lobby, CharacterModel character) =>
        InvokeWithDefaults(lobby, Overload(lobby.GetType(), "SetLocalCharacter", p => p.Length > 0 && p[0].ParameterType == typeof(CharacterModel)),
            new object?[] { character });

    private static void StartAutoSlayer(string seed)
    {
        var slayer = new MegaCrit.Sts2.Core.AutoSlay.AutoSlayer();
        // v0.113's mode has no default: null is a standard run.
        InvokeWithDefaults(slayer, Overload(slayer.GetType(), "Start", p => p.Length > 0 && p[0].ParameterType == typeof(string)), new object?[] { seed },
            ("mode", null));
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
