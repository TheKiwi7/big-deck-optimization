using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// What a checksum snapshot that reuses walk outputs must leave as it found (ListenerCache's snapshot walk reuse, DESIGN "Snapshot
/// walk reuse"). <c>NetFullCombatState.FromRun</c> only reads the game in vanilla; another mod's lazy walk postfix may still do
/// something while its output is enumerated (consume an RNG, bump a counter), and a reused output is enumerated fewer times than
/// the game's walks would be: what it did then differs from vanilla, unseen by a comparison of walk outputs. Sealed at the
/// snapshot's start, compared at its net: the run's RNGs and every player's (their counters: FromRun writes the run's at its start
/// and each player's before that player's piles), the next choice and reward ids FromRun writes, and the cheap numbers it reads
/// before and between the walks (each creature's HP, max HP, block, powers and their amounts; each player's gold, potion slots,
/// energy, stars, turn and phase). A difference is a detection. The RNGs can be put back as they were (game v0.111+:
/// <c>Rng.LoadFromSerializable</c>, in place; v0.107.1 has no way back for an RNG, only forward), so that the snapshot built again
/// draws them as the game's own <c>FromRun</c> would; the other numbers can't. Never throws (an error: "changed", the safe side).
/// </summary>
internal sealed class SnapshotSeal
{
    private static readonly FieldInfo? RunRngs = Reflect.Field(typeof(RunRngSet), "_rngs");
    private static readonly FieldInfo? PlayerRngs = Reflect.Field(typeof(PlayerRngSet), "_rngs");
    private static readonly FieldInfo? Counter = Reflect.Field(typeof(Rng), "_counter") ?? Reflect.Field(typeof(Rng), "<Counter>k__BackingField");
    /// <summary>v0.111+: an RNG's whole state, and putting it back in place. Absent on v0.107.1.</summary>
    private static readonly MethodInfo? Save = Reflect.Method(typeof(Rng), "ToSerializable");
    private static readonly MethodInfo? Load = Reflect.Method(typeof(Rng), "LoadFromSerializable");

    // The code the seal and the put-back rely on (v0.111.0 to v0.113.1, identical): the sets' lookups hand out the Rng objects
    // their _rngs dictionaries hold (the seal reads those); Rng.ToSerializable takes _counter and MegaRandom's whole state
    // (FillSerializableState: _s0 to _s3, its only fields), LoadFromSerializable writes them back in place (Reinitialise).
    private const string RunGetRngFingerprint = "A453852030B5A2BF";
    private const string PlayerGetRngFingerprint = "4D51EEAED4BCB8FA";
    private const string SaveFingerprint = "8396A57F9FA34CA5";
    private const string LoadFingerprint = "17522D6B7BA93E55";
    private const string FillFingerprint = "0B6D75718A3BE098";
    private const string ReinitialiseFingerprint = "F60E405CDB1A5CEC";

    /// <summary>The sets still hand out what their _rngs hold (a changed lookup: no seal, so no snapshot reuse).</summary>
    private static readonly Lazy<bool> Sealable = new(() => Matches(Reflect.Method(typeof(RunRngSet), "GetRng"), RunGetRngFingerprint)
        && Matches(Reflect.Method(typeof(PlayerRngSet), "GetRng"), PlayerGetRngFingerprint));

    /// <summary>The put-back's code as checked (a change: the RNGs aren't put back, a drawn RNG then only makes a rebuild).</summary>
    private static readonly Lazy<bool> Restorable = new(() =>
    {
        if (Save == null || Load == null || Counter == null || Load.GetParameters() is not { Length: 1 } parameters)
        {
            return false;
        }
        Type? megaRandom = typeof(Rng).Assembly.GetType("MegaCrit.Sts2.Core.Random.MegaRandom");
        return Matches(Save, SaveFingerprint) && Matches(Load, LoadFingerprint)
            && Matches(Reflect.Method(megaRandom, "FillSerializableState"), FillFingerprint)
            && Matches(Reflect.Method(megaRandom, "Reinitialise", new[] { parameters[0].ParameterType }), ReinitialiseFingerprint);
    });

    private static bool Matches(MethodBase? method, string expected)
    {
        try
        {
            return method != null && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), expected);
        }
        catch
        {
            return false;
        }
    }

    private readonly List<(Rng Rng, int Counter, object? Saved)> _rngs = new();
    private readonly List<long> _numbers = new();
    private readonly List<uint> _choiceIds = new();
    private readonly List<int> _rewardIds = new();

    /// <summary>Whether the RNGs can be put back on this game version, its code as checked (see the class).</summary>
    public static bool CanRestore => Restorable.Value;

    /// <summary>The seal of <paramref name="runState"/> now. Null when it can't be taken (an error): no reuse then.</summary>
    public static SnapshotSeal? Take(IRunState runState)
    {
        try
        {
            if (!Sealable.Value)
            {
                return null;
            }
            var seal = new SnapshotSeal();
            seal.AddRngs(RunRngs?.GetValue(runState.Rng) ?? throw new MissingFieldException(nameof(RunRngSet), "_rngs"));
            foreach (Player player in runState.Players)
            {
                seal.AddRngs(PlayerRngs?.GetValue(player.PlayerRng) ?? throw new MissingFieldException(nameof(PlayerRngSet), "_rngs"));
            }
            Numbers(runState, seal._numbers);
            Ids(seal._choiceIds, seal._rewardIds);
            return seal;
        }
        catch
        {
            return null;
        }
    }

    private void AddRngs(object dictionary)
    {
        foreach (object? value in ((IDictionary)dictionary).Values)
        {
            if (value is Rng rng)
            {
                _rngs.Add((rng, CounterOf(rng), CanRestore ? Save!.Invoke(rng, null) : null));
            }
        }
    }

    private static int CounterOf(Rng rng) => Counter != null ? (int)Counter.GetValue(rng)! : throw new MissingFieldException(nameof(Rng), "_counter");

    private static void Numbers(IRunState runState, List<long> into)
    {
        IReadOnlyList<Creature> creatures = runState.Players.Count > 0 ? runState.Players[0].Creature.CombatState?.Creatures ?? Array.Empty<Creature>() : Array.Empty<Creature>();
        into.Add(creatures.Count);
        foreach (Creature creature in creatures)
        {
            into.Add(creature.CurrentHp);
            into.Add(creature.MaxHp);
            into.Add(creature.Block);
            foreach (PowerModel power in creature.Powers)
            {
                into.Add(RuntimeHelpers.GetHashCode(power));
                into.Add(power.Amount);
            }
            into.Add(-1);
        }
        foreach (Player player in runState.Players)
        {
            into.Add(player.Gold);
            into.Add(player.MaxPotionCount);
            PlayerCombatState? combat = player.PlayerCombatState;
            into.Add(combat?.Energy ?? 0);
            into.Add(combat?.Stars ?? 0);
            into.Add(combat?.TurnNumber ?? 0);
            into.Add((long)(combat?.Phase ?? default));
        }
    }

    private static void Ids(List<uint> choiceIds, List<int> rewardIds)
    {
        if (RunManager.Instance is { } run)
        {
            choiceIds.AddRange(run.PlayerChoiceSynchronizer.ChoiceIds);
            rewardIds.AddRange(run.RewardsSetSynchronizer.GetNextRewardIds());
        }
    }

    /// <summary>What changed since the seal was taken (the first thing found), or null. An error: said as a change.</summary>
    public string? Changed(IRunState runState)
    {
        (bool rngs, string? other) = Diff(runState);
        return other ?? (rngs ? "an RNG was drawn during the check" : null);
    }

    /// <summary>
    /// Whether an RNG was drawn since the seal was taken, and what else changed (null: nothing): the part an RNG put-back can
    /// undo and the part it can't. An error: said as another change.
    /// </summary>
    public (bool Rngs, string? Other) Diff(IRunState runState)
    {
        bool rngs = false;
        try
        {
            foreach ((Rng rng, int counter, object? _) in _rngs)
            {
                rngs |= CounterOf(rng) != counter;
            }
            return (rngs, Others(runState));
        }
        catch (Exception e)
        {
            return (rngs, "the state's seal couldn't be compared: " + e.Message);
        }
    }

    private string? Others(IRunState runState)
    {
        {
            var numbers = new List<long>(_numbers.Count);
            Numbers(runState, numbers);
            if (!SameList(numbers, _numbers))
            {
                return "a creature's or player's numbers changed during the check";
            }
            var choiceIds = new List<uint>(_choiceIds.Count);
            var rewardIds = new List<int>(_rewardIds.Count);
            Ids(choiceIds, rewardIds);
            if (!SameList(choiceIds, _choiceIds) || !SameList(rewardIds, _rewardIds))
            {
                return "the next choice or reward ids changed during the check";
            }
            return null;
        }
    }

    /// <summary>
    /// Every RNG drawn since the seal put back as it was (v0.111+), so the snapshot built again draws them as the game's own
    /// FromRun would. True when no drawn RNG is left as it is (nothing drawn, or all put back). Never throws.
    /// </summary>
    public bool RestoreRngs()
    {
        bool all = true;
        foreach ((Rng rng, int counter, object? saved) in _rngs)
        {
            try
            {
                if (CounterOf(rng) == counter)
                {
                    continue;
                }
                if (saved == null || Load == null)
                {
                    all = false;
                    continue;
                }
                Load.Invoke(rng, new[] { saved });
                all &= CounterOf(rng) == counter;
            }
            catch
            {
                all = false;
            }
        }
        return all;
    }

    private static bool SameList<T>(List<T> a, List<T> b) where T : IEquatable<T>
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }
        return true;
    }
}
