using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// In co-op, every action ends with a checksum of the full combat state on every machine
/// (<c>NetFullCombatState.FromRun</c>): <c>CardState.From</c> calls <c>card.ToSerializable()</c> for every card in
/// combat, and with BaseLib each new SerializableCard and SerializableEnchantment gets its extended save data in a
/// ConditionalWeakTable (with Limitless, stacked enchantments also copy their saved inner cards). At 4,100 cards that is
/// about half of everything allocated while cards are played, and the collections it triggers freeze each action
/// ~0.1 s.
///
/// The call in <c>CardState.From</c> goes through <see cref="ToSerializable"/>: when the card's previous result (kept
/// per card) still holds what <c>ToSerializable</c> would build, compared field by field as
/// <see cref="LimitlessReserialize"/> does (ids, upgrade, floor, enchantment and amount, saved properties, BaseLib's
/// extended data), that object is used again. The snapshot only serializes and keeps it (to print it on a divergence),
/// never changes it: same bytes, same checksum. Anything else, another mod patching what the comparison mirrors, or a
/// BaseLib getter whose result isn't known: the game's call.
///
/// One reuse in <see cref="VerifyEvery"/> is serialized next to a freshly built one; a byte difference or an error turns
/// this off for the session (the fresh one is used).
/// </summary>
public static class SnapshotCardReuse
{
    public const string Name = "SnapshotCardReuse";

    /// <summary>One reuse in this many is checked byte for byte against a fresh <c>ToSerializable</c>.</summary>
    public const int VerifyEvery = 2048;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    public static long Reused { get; private set; }
    public static long Built { get; private set; }
    public static long Verified { get; private set; }
    public static long Mismatches { get; private set; }

    private static MethodInfo? _toSerializable;
    private static bool _replaced;

    /// <summary>A card's last snapshot object, and the BaseLib data the comparison found for it.</summary>
    private sealed class Last
    {
        public SerializableCard Saved = null!;
        public LimitlessReserialize.StoredData Data = new();
    }

    private static readonly ConditionalWeakTable<CardModel, Last> _last = new();

    public static void Apply(Harmony harmony)
    {
        if (!LimitlessReserialize.Applied)
        {
            throw new PatchSkippedException("needs LimitlessReserialize's comparison (BaseLib and Limitless; applies with it if Limitless loads later)");
        }
        _toSerializable = AccessTools.Method(typeof(CardModel), nameof(CardModel.ToSerializable), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.ToSerializable));
        MethodInfo from = AccessTools.Method(typeof(NetFullCombatState.CardState), nameof(NetFullCombatState.CardState.From), new[] { typeof(CardModel) })
            ?? throw new MissingMethodException("NetFullCombatState.CardState", "From");
        // Only that call is replaced: the rest of From (affliction, keywords, cost) stays the game's whatever it becomes.
        int calls = 0;
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(from))
        {
            if (instruction.Calls(_toSerializable))
            {
                calls++;
            }
        }
        if (calls != 1)
        {
            throw new PatchSkippedException($"CardState.From calls ToSerializable {calls} times (expected once): left vanilla");
        }
        PatchGuard.Patch(harmony, from, transpiler: new HarmonyMethod(typeof(SnapshotCardReuse), nameof(Transpiler)));
        if (!_replaced)
        {
            throw new PatchSkippedException("CardState.From: ToSerializable call not replaced");
        }
        Applied = true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        bool replaced = false; // Harmony runs transpilers again each time the method is re-patched.
        foreach (CodeInstruction instruction in instructions)
        {
            if (!replaced && instruction.Calls(_toSerializable!))
            {
                replaced = true;
                _replaced = true;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SnapshotCardReuse), nameof(ToSerializable)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static int _sinceVerify;
    private static PacketWriter? _reusedWriter, _freshWriter;

    /// <summary><c>card.ToSerializable()</c> for a combat snapshot: the card's previous result when it still holds the same.</summary>
    private static SerializableCard ToSerializable(CardModel card)
    {
        Last? last = null;
        if (Enabled && !LimitlessReserialize.Faulted && LimitlessReserialize.CanCompareHere)
        {
            try
            {
                if (_last.TryGetValue(card, out last) && !LimitlessReserialize.PatchedByOthers()
                    && LimitlessReserialize.SameSerialized(card, last.Saved, last.Data))
                {
                    if (++_sinceVerify >= VerifyEvery)
                    {
                        _sinceVerify = 0;
                        return Verify(card, last);
                    }
                    Reused++;
                    return last.Saved;
                }
            }
            catch (Exception e)
            {
                Disable("exception", e.ToString());
                last = null;
            }
        }
        SerializableCard fresh = card.ToSerializable();
        if (Enabled)
        {
            Remember(card, last, fresh);
        }
        return fresh;
    }

    private static void Remember(CardModel card, Last? last, SerializableCard fresh)
    {
        try
        {
            last ??= _last.GetValue(card, static _ => new Last());
            if (!ReferenceEquals(last.Saved, fresh))
            {
                last.Saved = fresh;
                last.Data = new LimitlessReserialize.StoredData();
            }
            Built++;
        }
        catch (Exception e)
        {
            Disable("exception", e.ToString());
        }
    }

    /// <summary>The reused object serialized next to a fresh one: the fresh one (kept from now on) when they differ.</summary>
    private static SerializableCard Verify(CardModel card, Last last)
    {
        SerializableCard fresh = card.ToSerializable();
        Verified++;
        _reusedWriter ??= new() { WarnOnGrow = false };
        _freshWriter ??= new() { WarnOnGrow = false };
        _reusedWriter.Reset();
        _freshWriter.Reset();
        last.Saved.Serialize(_reusedWriter);
        fresh.Serialize(_freshWriter);
        if (!_reusedWriter.Buffer.AsSpan(0, _reusedWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition)))
        {
            Mismatches++;
            Disable("a reused card snapshot differs from a fresh one", $"{card.Id} ({_reusedWriter.BytePosition} vs {_freshWriter.BytePosition} bytes)");
            return fresh;
        }
        // Equal: keep the fresh one, so a stale object never lives long.
        last.Saved = fresh;
        last.Data = new LimitlessReserialize.StoredData();
        return fresh;
    }

    private static void Disable(string why, string details)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled ({why}), snapshots build every card again: {details}");
    }
}
