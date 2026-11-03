using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The co-op checksum after every action (<c>NetFullCombatState.FromRun</c>) serializes every relic of every player again, and
/// BaseLib's postfix on <c>RelicModel.ToSerializable</c> adds each new object to a <c>ConditionalWeakTable</c>. The game keeps every
/// snapshot of the combat (its replay recording), so that table grows by every relic at every action and each add gets slower: at
/// 20,580 cards per player with the reference run's relics, 1,049 ms of a 90 s co-op trace, more than reading every card pile.
///
/// There, a relic whose serialized content is the same as last time gives its previous object again. The content is read in full at
/// every snapshot: <c>SavedProperties.From</c> (the game's call, once per relic as in <c>ToSerializable</c>), the id and the floor,
/// compared with the previous object's. What the object's serialization writes beyond those fields is BaseLib's extended data, which
/// writes nothing while no relic save is registered: only then. A relic with saved cards, another mod's patch on what this relies on,
/// or a fingerprint change: the game's call. A new object is built as <c>ToSerializable</c> builds it, BaseLib's postfix included.
/// </summary>
public static class RelicStateReuse
{
    public const string Name = "RelicStateReuse";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Built { get; private set; }

#if BENCH
    /// <summary>Saved properties taken as unchanged (bdperf relicreuse blind): shows that the snapshot checks see a stale relic.</summary>
    public static bool BlindForTest { get; set; }
#endif

    /// <summary>v0.107.1 (and v0.111): <c>new SerializableRelic { Id, Props = SavedProperties.From(this), FloorAddedToDeck }</c> after AssertMutable.</summary>
    private const string ToSerializableFingerprint = "91A31F3A5C8BCB32";
    /// <summary>Writes the id, the properties and the floor, nothing else.</summary>
    private const string SerializeFingerprint = "FEE0E78D320AB4F9";

    private const string BaseLibAssembly = "BaseLib";
    private const string HandlersType = "BaseLib.Patches.Saves.ExtendedSaveHandlers`2";
    /// <summary>BaseLib 3.4.7: stores <c>new ExtendedSaveData(relic)</c> for the new object, nothing else.</summary>
    private const string BaseLibPostfix = "BaseLib.Patches.Saves.ExtendedSavePatches+PrepExtendedRelicData:ExtendedDataForRelic";
    private const string BaseLibPostfixFingerprint = "A0902464D285F2CB";
    /// <summary>BaseLib's postfix on <c>SerializableRelic.Serialize</c> and what it calls: one write per registered save, none without.</summary>
    private const string BaseLibWrite = "BaseLib.Patches.Saves.ExtendedSavePatches+SerializeExtendedRelicData:WriteExtended";
    private const string BaseLibWriteFingerprint = "2932D9B1B34FDBBC";
    private const string HandlersWriteFingerprint = "D1DE163AF605BE38";
    /// <summary>
    /// "Unlimited: No Player Limit" transpiles every <c>Serialize</c>: a list's count written on more bits (none in this one). The
    /// bits stay a function of the object's fields alone.
    /// </summary>
    private const string UnlimitedAssembly = "sts2unlimited";
    private const string UnlimitedTranspiler = "Sts2Unlimited.PacketSizePatch:Transpile_SerializeDeserialize";
    private const string UnlimitedTranspilerFingerprint = "908CDF014E4F04CF";
    private const string UnlimitedConstantFingerprint = "CB9DCCF74112728F";

    private static MethodInfo? _toSerializable, _serialize;
    private static readonly MethodInfo _savedPropertiesFrom = AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.From));
    private static MethodInfo[] _allowedOnToSerializable = Array.Empty<MethodInfo>();
    private static MethodInfo[] _allowedOnSerialize = Array.Empty<MethodInfo>();
    private static MethodInfo? _baseLibWrite, _unlimitedTranspiler;
    private static Action<RelicModel, SerializableRelic>? _baseLibPostfix;
    private static Func<IList>? _registeredSaves;
    private static int _mainThread;
    private static int _replaced;

    private static readonly ConditionalWeakTable<RelicModel, SerializableRelic> _last = new();

    public static void Apply(Harmony harmony)
    {
        _toSerializable = AccessTools.Method(typeof(RelicModel), nameof(RelicModel.ToSerializable), Type.EmptyTypes)
            ?? throw new MissingMethodException(nameof(RelicModel), nameof(RelicModel.ToSerializable));
        _serialize = AccessTools.Method(typeof(SerializableRelic), nameof(SerializableRelic.Serialize))
            ?? throw new MissingMethodException(nameof(SerializableRelic), nameof(SerializableRelic.Serialize));
        PatchSafety.RequireFingerprint(_toSerializable, ToSerializableFingerprint);
        PatchSafety.RequireFingerprint(_serialize, SerializeFingerprint);
        MethodInfo fromRun = AccessTools.Method(typeof(NetFullCombatState), nameof(NetFullCombatState.FromRun))
            ?? throw new MissingMethodException(nameof(NetFullCombatState), nameof(NetFullCombatState.FromRun));
        _mainThread = Environment.CurrentManagedThreadId;
        string[] loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToArray();
        if (loaded.Contains(BaseLibAssembly))
        {
            ResolveBaseLib();
        }
        if (loaded.Contains(UnlimitedAssembly))
        {
            ResolveUnlimited();
        }
        if (!loaded.Contains(BaseLibAssembly) || !loaded.Contains(UnlimitedAssembly))
        {
            // Mod load order isn't guaranteed. Until then their patches, if any, count as another mod's: the game's call.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
        _replaced = 0;
        PatchGuard.Patch(harmony, fromRun, transpiler: new HarmonyMethod(typeof(RelicStateReuse), nameof(Transpiler)));
        if (_replaced == 0)
        {
            throw new PatchSkippedException("FromRun no longer serializes relics this way");
        }
        Applied = true;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        string? name = args.LoadedAssembly.GetName().Name;
        if (name != BaseLibAssembly && name != UnlimitedAssembly)
        {
            return;
        }
        try
        {
            if (name == BaseLibAssembly)
            {
                ResolveBaseLib();
            }
            else
            {
                ResolveUnlimited();
            }
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {name}'s relic patches not understood, relics serialized by the game: {e.Message}");
        }
    }

    private static void ResolveUnlimited()
    {
        MethodInfo transpiler = AccessTools.Method(UnlimitedTranspiler) ?? throw new PatchSkippedException("Unlimited has no " + UnlimitedTranspiler);
        PatchSafety.RequireFingerprint(transpiler, UnlimitedTranspilerFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(transpiler.DeclaringType, "IsVanillaListBitsConstant")
            ?? throw new MissingMethodException("PacketSizePatch", "IsVanillaListBitsConstant"), UnlimitedConstantFingerprint);
        _unlimitedTranspiler = transpiler;
        AllowOnSerialize();
    }

    private static void AllowOnSerialize() =>
        _allowedOnSerialize = new[] { _baseLibWrite, _unlimitedTranspiler }.Where(m => m != null).ToArray()!;

    /// <summary>BaseLib's relic postfixes, accepted only with their known code; its relic saves, read at each use.</summary>
    private static void ResolveBaseLib()
    {
        MethodInfo postfix = AccessTools.Method(BaseLibPostfix) ?? throw new PatchSkippedException("BaseLib has no " + BaseLibPostfix);
        MethodInfo write = AccessTools.Method(BaseLibWrite) ?? throw new PatchSkippedException("BaseLib has no " + BaseLibWrite);
        PatchSafety.RequireFingerprint(postfix, BaseLibPostfixFingerprint);
        PatchSafety.RequireFingerprint(write, BaseLibWriteFingerprint);
        Type definition = AccessTools.TypeByName(HandlersType) ?? throw new PatchSkippedException("BaseLib has no " + HandlersType);
        PatchSafety.RequireFingerprint(AccessTools.Method(definition, "Write") ?? throw new MissingMethodException(HandlersType, "Write"), HandlersWriteFingerprint);
        Type handlers = definition.MakeGenericType(typeof(RelicModel), typeof(SerializableRelic));
        PropertyInfo registered = AccessTools.Property(handlers, "RegisteredSaves") ?? throw new MissingMemberException(handlers.Name, "RegisteredSaves");
        _registeredSaves = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.Property(null, registered), typeof(IList))).Compile();
        _baseLibPostfix = AccessTools.MethodDelegate<Action<RelicModel, SerializableRelic>>(postfix);
        _allowedOnToSerializable = new[] { postfix };
        _baseLibWrite = write;
        AllowOnSerialize();
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_toSerializable!))
            {
                _replaced++;
                // Same stack: (relic) -> serializable relic.
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RelicStateReuse), nameof(Serialize)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static SerializableRelic Serialize(RelicModel relic)
    {
        // Inside RunVanilla the result is the reference this is compared with (SnapshotVerifier, bdperf): the game's own.
        if (!Enabled || ListenerCache.IsSuspended || !relic.IsMutable || Environment.CurrentManagedThreadId != _mainThread
            || (_registeredSaves != null && _registeredSaves().Count != 0)
            || PatchSafety.IsPatchedByOthers(_toSerializable, _allowedOnToSerializable) || PatchSafety.IsPatchedByOthers(_serialize, _allowedOnSerialize)
            || PatchSafety.IsPatchedByOthers(_savedPropertiesFrom))
        {
            return relic.ToSerializable();
        }
        SavedProperties? props;
        try
        {
            props = SavedProperties.From(relic);
        }
        catch (Exception e) when (e is not System.Text.Json.JsonException)
        {
            Fail(e);
            return relic.ToSerializable();
        }
        // An invalid property throws above as it does in ToSerializable, at the same place: the game's error, nothing caught.
        try
        {
            if (_last.TryGetValue(relic, out SerializableRelic? previous) && previous.Id == relic.Id && previous.FloorAddedToDeck == relic.FloorAddedToDeck
                && Same(previous.Props, props))
            {
                Reused++;
                return previous;
            }
            // As ToSerializable builds it (Id, Props, FloorAddedToDeck), then BaseLib's postfix as it would run.
            var saved = new SerializableRelic { Id = relic.Id, Props = props, FloorAddedToDeck = relic.FloorAddedToDeck };
            _baseLibPostfix?.Invoke(relic, saved);
            _last.AddOrUpdate(relic, saved);
            Built++;
            return saved;
        }
        catch (Exception e)
        {
            Fail(e);
            return relic.ToSerializable();
        }
    }

    private static void Fail(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, relics serialized by the game: {e}");
    }

    /// <summary>Same saved properties, in the same order; any saved card makes them different (not compared).</summary>
    private static bool Same(SavedProperties? a, SavedProperties? b)
    {
#if BENCH
        if (BlindForTest)
        {
            return true;
        }
#endif
        if (a == null || b == null)
        {
            return a == null && b == null;
        }
        if (a.cards != null || b.cards != null || a.cardArrays != null || b.cardArrays != null)
        {
            return false;
        }
        return SameList(a.ints, b.ints, static (x, y) => x == y)
            && SameList(a.bools, b.bools, static (x, y) => x == y)
            && SameList(a.strings, b.strings, static (x, y) => string.Equals(x, y, StringComparison.Ordinal))
            && SameList(a.intArrays, b.intArrays, static (x, y) => x != null && y != null && x.AsSpan().SequenceEqual(y))
            && SameList(a.modelIds, b.modelIds, static (x, y) => Equals(x, y));
    }

    private static bool SameList<T>(List<SavedProperties.SavedProperty<T>>? a, List<SavedProperties.SavedProperty<T>>? b, Func<T, T, bool> same)
    {
        if (a == null || b == null)
        {
            return a == null && b == null;
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].name, b[i].name, StringComparison.Ordinal) || !same(a[i].value, b[i].value))
            {
                return false;
            }
        }
        return true;
    }
}
