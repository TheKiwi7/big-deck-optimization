using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// <c>SavedProperties.FromInternal</c> (each relic, card and enchantment saved or put in a co-op checksum) reads the
/// <c>[SavedProperty]</c> attribute of every saved property through reflection, each time: a new attribute object per property per
/// call. In co-op, the checksum after every action saves every player's relics that way: with the reference run's 368 relics that was
/// 85% of the checksum (~4 ms per action).
///
/// There, the attribute is read once per property and the same object given again: its fields are readonly, and the method only reads
/// them. Same saved properties, same checksum.
/// </summary>
public static class SavedPropertyLookup
{
    public const string Name = "SavedPropertyLookup";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Served { get; private set; }

    private static readonly MethodInfo _lookup = AccessTools.Method(typeof(CustomAttributeExtensions), nameof(CustomAttributeExtensions.GetCustomAttribute),
        new[] { typeof(MemberInfo) }, new[] { typeof(SavedPropertyAttribute) });
    private static readonly ConcurrentDictionary<MemberInfo, SavedPropertyAttribute?> _cache = new();
    private static int _replaced;

    public static void Apply(Harmony harmony)
    {
        MethodInfo fromInternal = AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.FromInternal))
            ?? throw new MissingMethodException(nameof(SavedProperties), nameof(SavedProperties.FromInternal));
        _replaced = 0;
        PatchGuard.Patch(harmony, fromInternal, transpiler: new HarmonyMethod(typeof(SavedPropertyLookup), nameof(Transpiler)));
        if (_replaced == 0)
        {
            throw new PatchSkippedException("FromInternal no longer reads the attribute this way");
        }
        Applied = true;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_lookup))
            {
                _replaced++;
                // Same stack: (member) -> attribute.
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SavedPropertyLookup), nameof(Attribute)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static SavedPropertyAttribute? Attribute(MemberInfo member)
    {
        if (!Enabled)
        {
            return member.GetCustomAttribute<SavedPropertyAttribute>();
        }
        try
        {
            if (_cache.TryGetValue(member, out SavedPropertyAttribute? attribute))
            {
                Served++;
                return attribute;
            }
            attribute = member.GetCustomAttribute<SavedPropertyAttribute>();
            _cache[member] = attribute;
            return attribute;
        }
        catch (Exception e) when (e is not AmbiguousMatchException)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, attributes are read each time: {e}");
            return member.GetCustomAttribute<SavedPropertyAttribute>();
        }
    }
}
