using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Every <c>RelicModel.Flash()</c> spawns a particle effect over the relic bar (<c>NRelicInventoryHolder</c>), plus a
/// sound and an effect on the target creature (<c>Player.OnRelicFlashed</c>). A relic that triggers once per card
/// (Bing Bong copying each card cloned at a campfire) flashes thousands of times in the same frame: thousands of GPU
/// particle systems stacked on the same spot, which blacks out the screen (the renderer runs out of descriptors) and
/// can crash the game.
///
/// The same relic flashing again within one frame, on the same creature, now shows one flash: the extra ones would
/// draw identical effects at the same place and time. Sound and visuals only, nothing gameplay-related.
/// </summary>
public static class RelicFlashMerge
{
    public const string Name = "RelicFlashMerge";

    /// <summary>v0.107.1 handlers skipped for repeats: they only create effects and play a sound.</summary>
    private const string HolderFlashFingerprint = "266F6745573AB781";
    private const string HolderDoFlashFingerprint = "1F67DBB3C0633077";
    private const string PlayerFlashFingerprint = "EE9B58B0BB307D27";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Merged { get; private set; }

    private static System.Reflection.MethodInfo? _holderFlash;
    private static System.Reflection.MethodInfo? _playerFlash;
    private static int _mainThread;

    private sealed class Last
    {
        public ulong Frame = ulong.MaxValue;
        public Creature? Target;
    }

    private static readonly ConditionalWeakTable<NRelicInventoryHolder, Last> _holders = new();
    private static readonly ConditionalWeakTable<RelicModel, Last> _relics = new();

    public static void Apply(Harmony harmony)
    {
        _holderFlash = AccessTools.Method(typeof(NRelicInventoryHolder), "OnRelicFlashed")
            ?? throw new MissingMethodException(nameof(NRelicInventoryHolder), "OnRelicFlashed");
        _playerFlash = AccessTools.Method(typeof(Player), "OnRelicFlashed")
            ?? throw new MissingMethodException(nameof(Player), "OnRelicFlashed");
        PatchSafety.RequireFingerprint(_holderFlash, HolderFlashFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(NRelicInventoryHolder), "DoFlash")
            ?? throw new MissingMethodException(nameof(NRelicInventoryHolder), "DoFlash"), HolderDoFlashFingerprint);
        PatchSafety.RequireFingerprint(_playerFlash, PlayerFlashFingerprint);
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        PatchGuard.Patch(harmony, _holderFlash, prefix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(HolderPrefix)));
        PatchGuard.Patch(harmony, _playerFlash, prefix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(PlayerPrefix)));
        Applied = true;
    }

    /// <summary>The relic bar effect: once per holder per frame.</summary>
    private static bool HolderPrefix(NRelicInventoryHolder __instance)
    {
        try
        {
            if (!Usable(_holderFlash))
            {
                return true;
            }
            return FirstThisFrame(_holders.GetValue(__instance, _ => new Last()), null);
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>The sound and the creature effect: once per relic and target per frame (several targets: never merged).</summary>
    private static bool PlayerPrefix(RelicModel relic, IEnumerable<Creature> targets)
    {
        try
        {
            if (!Usable(_playerFlash) || SingleTarget(targets) is not { } target)
            {
                return true;
            }
            return FirstThisFrame(_relics.GetValue(relic, _ => new Last()), target);
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    private static bool Usable(System.Reflection.MethodBase? method) =>
        Enabled && Thread.CurrentThread.ManagedThreadId == _mainThread && !PatchSafety.IsPatchedByOthers(method);

    private static Creature? SingleTarget(IEnumerable<Creature> targets)
    {
        if (targets is IReadOnlyList<Creature> { Count: 1 } list)
        {
            return list[0];
        }
        return null;
    }

    /// <summary>True (run the handler) unless the same key already flashed this frame on the same target.</summary>
    private static bool FirstThisFrame(Last last, Creature? target)
    {
        ulong frame = Engine.GetProcessFrames();
        if (last.Frame == frame && last.Target == target)
        {
            Merged++;
            return false;
        }
        last.Frame = frame;
        last.Target = target;
        return true;
    }

    public static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
