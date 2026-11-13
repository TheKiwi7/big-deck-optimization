using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace BigDeckOptimization.Patches;

/// <summary>
/// A card moving between the draw and discard piles without a card on screen spawns a "card soul" flying to its pile
/// (<c>NCardFlyShuffleVfx</c>), with a sound and a trail: a scene instantiated per card, then a few nodes working every
/// frame for 2-4 seconds. A reshuffle squeezes its whole discard pile into 0.8 s whatever its size: at 16,800 cards,
/// 16,800 souls alive at once, each frame taking seconds (5 minutes in all, frames up to a minute).
///
/// At most <see cref="PerSecond"/> souls per second now fly, with a burst of <see cref="Burst"/>: a reshuffle of up to ~64
/// cards and a move of a few cards look as in the game; a bigger pile sends a steady stream. A soul only flies and
/// counts on the pile's button when it lands, so the button of a pile that got no soul counts those cards itself, once
/// per frame. Sound and visuals only, on the local player's screen (the game shows these only for the local player).
/// </summary>
public static class CardFlightLimit
{
    public const string Name = "CardFlightLimit";

    /// <summary>v0.107.1 code relied on: a soul only plays its sound, animates and tells its pile when it lands.</summary>
    private const string CreateFingerprint = "482216868A87BB4C";
    private const string ReadyFingerprint = "C82F2DB6F48B5EC6";
    private const string PlayAnimFingerprint = "8986E87813FC7715";
    private const string AddCardFingerprint = "005700D530E09ECC";

    private const double PerSecond = 80;
    private const double Burst = 12;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Skipped { get; private set; }

    private static System.Reflection.MethodInfo? _create, _addCard;
    /// <summary>What a skipped soul never runs: its _Ready, its flight, its sound and trail.</summary>
    private static System.Reflection.MethodBase?[] _skipped = Array.Empty<System.Reflection.MethodBase?>();
    private static System.Reflection.MethodInfo[]? _allowedOnReady;
    private static AccessTools.FieldRef<CardPile, Action?>? _cardAddFinished;

    /// <summary>
    /// Other mods' patches on the soul's _Ready a skipped soul may leave out: SpeedX's, which only speeds that soul up
    /// (fingerprinted). Any other patch there keeps every soul.
    /// </summary>
    private static readonly (string Method, string Fingerprint)[] AllowedReadyPatches =
    {
        ("SpeedX.Scripts.Patches.CardFlyShuffleSpeedPatch:Postfix", "9F8A3DE9AFF615B7"),
    };
    private static AccessTools.FieldRef<NCombatCardPile, int>? _currentCount;
    private static int _mainThread;
    private static double _tokens = Burst;
    private static long _lastRefill;

    /// <summary>Souls not spawned per target pile since the last flush (one frame), and the flush's extra count.</summary>
    private static readonly List<(CardPile Pile, int Count)> _pending = new();
    private static int _extra;

    public static void Apply(Harmony harmony)
    {
        _create = AccessTools.Method(typeof(NCardFlyShuffleVfx), nameof(NCardFlyShuffleVfx.Create))
            ?? throw new MissingMethodException(nameof(NCardFlyShuffleVfx), nameof(NCardFlyShuffleVfx.Create));
        PatchSafety.RequireFingerprint(_create, CreateFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(NCardFlyShuffleVfx), "_Ready")
            ?? throw new MissingMethodException(nameof(NCardFlyShuffleVfx), "_Ready"), ReadyFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.AsyncMoveNext(AccessTools.Method(typeof(NCardFlyShuffleVfx), "PlayAnim"))
            ?? throw new MissingMethodException(nameof(NCardFlyShuffleVfx), "PlayAnim"), PlayAnimFingerprint);
        var addCard = AccessTools.Method(typeof(NCombatCardPile), "AddCard") ?? throw new MissingMethodException(nameof(NCombatCardPile), "AddCard");
        PatchSafety.RequireFingerprint(addCard, AddCardFingerprint);
        _currentCount = AccessTools.FieldRefAccess<NCombatCardPile, int>("_currentCount");
        _addCard = addCard;
        _cardAddFinished = AccessTools.FieldRefAccess<CardPile, Action?>("CardAddFinished");
        System.Reflection.MethodInfo ready = AccessTools.Method(typeof(NCardFlyShuffleVfx), "_Ready")!;
        System.Reflection.MethodInfo playAnim = AccessTools.Method(typeof(NCardFlyShuffleVfx), "PlayAnim")!;
        _skipped = new System.Reflection.MethodBase?[]
        {
            playAnim, AccessTools.AsyncMoveNext(playAnim),
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.SfxCmd), "PlayCardSwooshSfx"),
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NCardTrailVfx), "Create"),
        };
        _readyMethod = ready;
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        PatchGuard.Patch(harmony, _create, prefix: new HarmonyMethod(typeof(CardFlightLimit), nameof(CreatePrefix)));
        PatchGuard.Patch(harmony, addCard, prefix: new HarmonyMethod(typeof(CardFlightLimit), nameof(AddCardPrefix)));
        Applied = true;
    }

    private static bool CreatePrefix(CardPile targetPile, ref NCardFlyShuffleVfx? __result)
    {
        try
        {
            if (!Enabled || targetPile.Type is not (PileType.Draw or PileType.Discard)
                || Thread.CurrentThread.ManagedThreadId != _mainThread || PatchSafety.IsPatchedByOthers(_create) || SkippedPatched())
            {
                return true;
            }
            long now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(Burst, _tokens + (now - _lastRefill) * PerSecond / Stopwatch.Frequency);
            _lastRefill = now;
            if (_tokens >= 1)
            {
                _tokens--;
                return true;
            }
            Skipped++;
            Count(targetPile);
            __result = null; // The game adds it through AddChildSafely, which ignores null, as when its test mode creates none.
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    private static System.Reflection.MethodInfo? _readyMethod;

    private static bool SkippedPatched()
    {
        _allowedOnReady ??= AllowedReady();
        if (PatchSafety.IsPatchedByOthers(_readyMethod, _allowedOnReady))
        {
            return true;
        }
        foreach (System.Reflection.MethodBase? method in _skipped)
        {
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    private static System.Reflection.MethodInfo[] AllowedReady()
    {
        var allowed = new List<System.Reflection.MethodInfo>();
        foreach ((string name, string fingerprint) in AllowedReadyPatches)
        {
            if (AccessTools.Method(name) is { } method && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint))
            {
                allowed.Add(method);
            }
        }
        return allowed.ToArray();
    }

    private static void Count(CardPile pile)
    {
        if (_pending.Count == 0)
        {
            Callable.From(Flush).CallDeferred();
        }
        for (int i = 0; i < _pending.Count; i++)
        {
            if (ReferenceEquals(_pending[i].Pile, pile))
            {
                _pending[i] = (pile, _pending[i].Count + 1);
                return;
            }
        }
        _pending.Add((pile, 1));
    }

    /// <summary>What the skipped souls would have told their pile on landing, one label fit per pile and frame.</summary>
    private static void Flush()
    {
        try
        {
            // A copy: the events run other code (another mod's listener) that may move cards and skip souls again, which
            // then queues them for the next flush instead of changing this list.
            (CardPile Pile, int Count)[] pending = _pending.ToArray();
            _pending.Clear();
            foreach ((CardPile pile, int count) in pending)
            {
                if (OnlyPileButtons(pile))
                {
                    _extra = count - 1;
                    pile.InvokeCardAddFinished();
                    _extra = 0;
                }
                else
                {
                    // Someone else listens (or AddCard is overridden or patched): one event per card, as the souls would.
                    for (int i = 0; i < count; i++)
                    {
                        pile.InvokeCardAddFinished();
                    }
                }
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
        finally
        {
            _extra = 0;
        }
    }

    /// <summary>
    /// Whether the pile's CardAddFinished only reaches pile buttons' own <c>NCombatCardPile.AddCard</c> (not overridden,
    /// not patched by another mod): one event then stands for several cards through <see cref="AddCardPrefix"/>.
    /// </summary>
    private static bool OnlyPileButtons(CardPile pile)
    {
        if (PatchSafety.IsPatchedByOthers(_addCard))
        {
            return false;
        }
        Action? handlers = _cardAddFinished!(pile);
        if (handlers == null)
        {
            return true;
        }
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            if (handler.Target is not NCombatCardPile || handler.Method.MethodHandle != _addCard!.MethodHandle)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Vanilla adds one to the count (capped by the pile's size); a flush for several cards adds the others first.</summary>
    private static void AddCardPrefix(NCombatCardPile __instance)
    {
        if (_extra > 0)
        {
            _currentCount!(__instance) += _extra;
        }
    }

    public static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
