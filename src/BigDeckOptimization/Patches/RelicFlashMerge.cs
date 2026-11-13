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
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        System.Reflection.MethodInfo? doFlash = AccessTools.Method(typeof(NRelicInventoryHolder), "DoFlash");
        bool holder = doFlash != null && Known(_holderFlash, HolderFlashFingerprint) && Known(doFlash, HolderDoFlashFingerprint);
        bool player = Known(_playerFlash, PlayerFlashFingerprint);
        if (!holder && !player)
        {
            throw new PatchSkippedException("neither handler has the known code");
        }
        // What a merged flash skips calling: another mod's patch there keeps every flash.
        _holderSkipped = new System.Reflection.MethodBase?[] { _holderFlash, doFlash };
        _playerSkipped = new System.Reflection.MethodBase?[]
        {
            _playerFlash,
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.SfxCmd), nameof(MegaCrit.Sts2.Core.Commands.SfxCmd.Play), new[] { typeof(string), typeof(float) }),
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NRelicFlashVfx), nameof(MegaCrit.Sts2.Core.Nodes.Vfx.NRelicFlashVfx.Create), new[] { typeof(RelicModel), typeof(Creature) }),
        };
        if (holder)
        {
            PatchGuard.Patch(harmony, _holderFlash, prefix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(HolderPrefix)));
        }
        if (player)
        {
            PatchGuard.Patch(harmony, _playerFlash, prefix: new HarmonyMethod(typeof(RelicFlashMerge), nameof(PlayerPrefix)));
        }
        Applied = true;
    }

    /// <summary>The two effects don't depend on each other: a changed one only leaves that one to the game.</summary>
    private static bool Known(System.Reflection.MethodInfo method, string fingerprint)
    {
        string actual = MethodFingerprint.Of(method);
        if (PatchSafety.FingerprintMatches(actual, fingerprint))
        {
            return true;
        }
        Log.Info($"[{ModEntry.Id}] {Name}: {method.DeclaringType?.Name}.{method.Name} changed (fingerprint {actual}, expected {fingerprint}): left vanilla");
        return false;
    }

    /// <summary>The relic bar effect: once per holder per frame.</summary>
    private static bool HolderPrefix(NRelicInventoryHolder __instance)
    {
        try
        {
            if (!Usable(_holderSkipped))
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
            if (!Usable(_playerSkipped) || SingleTarget(targets) is not { } target)
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

    private static System.Reflection.MethodBase?[] _holderSkipped = Array.Empty<System.Reflection.MethodBase?>(), _playerSkipped = Array.Empty<System.Reflection.MethodBase?>();

    /// <summary>On, on the main thread, and no other mod patches the handler or what it calls (a missing one counts as patched).</summary>
    private static bool Usable(System.Reflection.MethodBase?[] skipped)
    {
        if (!Enabled || Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            return false;
        }
        foreach (System.Reflection.MethodBase? method in skipped)
        {
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return false;
            }
        }
        return true;
    }

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
