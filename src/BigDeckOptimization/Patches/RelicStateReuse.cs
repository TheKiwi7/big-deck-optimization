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
    /// <summary>Every relic of a scalar plan taken as unchanged without reading it (bdperf relicreuse scalarblind): the checks must see it.</summary>
    public static bool ScalarBlindForTest { get; set; }
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
        try
        {
            ApplyScalar();
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: relics' saved properties read by the game each time ({e.Message})");
        }
    }

    // --- Scalar part: an unchanged relic recognized without SavedProperties.From ---

    public const string ScalarName = "RelicStateReuse.Scalar";
    public static bool ScalarEnabled { get; set; } = true;
    public static bool ScalarApplied { get; private set; }
    /// <summary>Relics given again without the game's <c>SavedProperties.From</c>, and how many of those were checked against it.</summary>
    public static long ScalarReused { get; private set; }
    public static long ScalarVerified { get; private set; }
#if BENCH
    internal static int ScalarVerifyEvery = 16;
#else
    private const int ScalarVerifyEvery = 256;
#endif

    /// <summary>v0.107.1 <c>FromInternal</c> (v0.111's in EquivalentFingerprints: only its property lookup moved).</summary>
    private const string FromInternalFingerprint = "F73EEC2208D6E517";
    /// <summary><c>SerializationConditionExtensions.ShouldSerialize</c> and <c>GetTypeDefaultValue</c>, v0.107.1 and v0.111.</summary>
    private const string ShouldSerializeFingerprint = "DA2DAEE9D953EEDD";
    private const string TypeDefaultFingerprint = "9E99D152BAEC2E1E";

    private static MethodInfo? _fromInternal, _shouldSerialize;
    private static Func<Type, List<PropertyInfo>?>? _jsonProperties;

    private static void ApplyScalar()
    {
        _fromInternal = AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.FromInternal))
            ?? throw new MissingMethodException(nameof(SavedProperties), nameof(SavedProperties.FromInternal));
        PatchSafety.RequireFingerprint(_fromInternal, FromInternalFingerprint);
        _shouldSerialize = AccessTools.Method(typeof(SerializationConditionExtensions), nameof(SerializationConditionExtensions.ShouldSerialize))
            ?? throw new MissingMethodException(nameof(SerializationConditionExtensions), nameof(SerializationConditionExtensions.ShouldSerialize));
        PatchSafety.RequireFingerprint(_shouldSerialize, ShouldSerializeFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(SerializationConditionExtensions), "GetTypeDefaultValue")
            ?? throw new MissingMethodException(nameof(SerializationConditionExtensions), "GetTypeDefaultValue"), TypeDefaultFingerprint);
        _jsonProperties = LimitlessReserialize.JsonPropertiesLookup();
        ScalarApplied = true;
    }

    /// <summary>
    /// What <c>FromInternal</c> does with a relic type whose saved properties are all <c>bool</c>, <c>int</c> or int-based enums,
    /// saved always or when not their type's default (every relic of the game saves <c>IsWax</c> and <c>IsMelted</c> that way; 47 of
    /// its relic properties are counters): each one read in order, kept when saved, its value (an enum's as <c>Convert.ToInt32</c>
    /// gives it) appended to <c>ints</c> or <c>bools</c>. Null: a property this doesn't mirror, so the game's call.
    /// </summary>
    private sealed class ScalarPlan
    {
        public List<PropertyInfo>? Properties;
        public string[] Names = Array.Empty<string>();
        public Func<object, int>[] Getters = Array.Empty<Func<object, int>>();
        public bool[] IsBool = Array.Empty<bool>();
        public bool[] Always = Array.Empty<bool>();
    }

    private static readonly Dictionary<Type, ScalarPlan?> _scalarPlans = new();

    private static ScalarPlan? ScalarPlanOf(Type type)
    {
        List<PropertyInfo>? properties = _jsonProperties!(type);
        if (_scalarPlans.TryGetValue(type, out ScalarPlan? plan) && (plan == null || ReferenceEquals(plan.Properties, properties)))
        {
            return plan;
        }
        plan = BuildScalarPlan(type, properties);
        _scalarPlans[type] = plan;
        return plan;
    }

    private static ScalarPlan? BuildScalarPlan(Type type, List<PropertyInfo>? properties)
    {
        var plan = new ScalarPlan { Properties = properties };
        if (properties == null)
        {
            return plan; // FromInternal iterates nothing: null.
        }
        int count = properties.Count;
        plan.Names = new string[count];
        plan.Getters = new Func<object, int>[count];
        plan.IsBool = new bool[count];
        plan.Always = new bool[count];
        ParameterExpression model = Expression.Parameter(typeof(object));
        for (int i = 0; i < count; i++)
        {
            PropertyInfo property = properties[i];
            SavedPropertyAttribute? attribute = property.GetCustomAttribute<SavedPropertyAttribute>();
            Type valueType = property.PropertyType;
            if (attribute == null || attribute.defaultBehaviour is not (SerializationCondition.AlwaysSave or SerializationCondition.SaveIfNotTypeDefault)
                || property.GetMethod is not { IsStatic: false } || property.GetIndexParameters().Length != 0
                || !(valueType == typeof(bool) || valueType == typeof(int) || (valueType.IsEnum && Enum.GetUnderlyingType(valueType) == typeof(int))))
            {
                return null;
            }
            Expression read = Expression.Property(Expression.Convert(model, type), property);
            Expression asInt = valueType == typeof(bool) ? Expression.Condition(read, Expression.Constant(1), Expression.Constant(0))
                : valueType == typeof(int) ? read : Expression.Convert(read, typeof(int));
            plan.Names[i] = property.Name;
            plan.Getters[i] = Expression.Lambda<Func<object, int>>(asInt, model).Compile();
            plan.IsBool[i] = valueType == typeof(bool);
            // SaveIfNotTypeDefault: !Equals(value, default of the property's type): false, 0, the enum's 0.
            plan.Always[i] = attribute.defaultBehaviour == SerializationCondition.AlwaysSave;
        }
        return plan;
    }

    private static long _scalarHits;

    /// <summary>
    /// The previous object of a relic whose id, floor and saved properties would come out of <c>ToSerializable</c> the same: what
    /// <see cref="Same"/> would conclude after the game's <c>From</c>, read here without reflection, boxing or allocation. One answer
    /// in <see cref="ScalarVerifyEvery"/> is checked against the game's call; a difference turns this part off.
    /// </summary>
    private static bool ScalarSame(RelicModel relic, out SerializableRelic? same)
    {
        same = null;
        if (!ScalarEnabled || !_scalarGuardsPass || !_last.TryGetValue(relic, out SerializableRelic? previous)
            || previous.Id != relic.Id || previous.FloorAddedToDeck != relic.FloorAddedToDeck
            || FieldTargeted(relic.GetType())
            || ScalarPlanOf(relic.GetType()) is not { } plan)
        {
#if BENCH
            ScalarWhy = !ScalarEnabled || !_scalarGuardsPass ? "off or patched" : !_last.TryGetValue(relic, out SerializableRelic? p) ? "no previous" : p.Id != relic.Id || p.FloorAddedToDeck != relic.FloorAddedToDeck ? "id/floor"
                : PatchSafety.IsPatchedByOthers(_fromInternal!, _allowedOnFromInternal) ? "FromInternal patched" : PatchSafety.IsPatchedByOthers(_shouldSerialize!) ? "ShouldSerialize patched"
                : FieldTargeted(relic.GetType()) ? "BaseLib field on " + relic.GetType().Name : "no plan " + relic.GetType().Name;
#endif
            return false;
        }
        SavedProperties? props = previous.Props;
        if (props != null && (props.strings != null || props.intArrays != null || props.modelIds != null || props.cards != null || props.cardArrays != null))
        {
            return false;
        }
        List<SavedProperties.SavedProperty<int>>? ints = props?.ints;
        List<SavedProperties.SavedProperty<bool>>? bools = props?.bools;
        int intsAt = 0, boolsAt = 0;
        bool blind = false;
#if BENCH
        blind = ScalarBlindForTest;
#endif
        for (int i = 0; !blind && i < plan.Getters.Length; i++)
        {
            int value = plan.Getters[i](relic);
            if (value == 0 && !plan.Always[i])
            {
                continue;
            }
            if (plan.IsBool[i])
            {
                if (bools == null || boolsAt >= bools.Count || bools[boolsAt].value != (value != 0) || !string.Equals(bools[boolsAt].name, plan.Names[i], StringComparison.Ordinal))
                {
                    return false;
                }
                boolsAt++;
            }
            else
            {
                if (ints == null || intsAt >= ints.Count || ints[intsAt].value != value || !string.Equals(ints[intsAt].name, plan.Names[i], StringComparison.Ordinal))
                {
                    return false;
                }
                intsAt++;
            }
        }
        if (!blind && ((ints?.Count ?? 0) != intsAt || (bools?.Count ?? 0) != boolsAt))
        {
            return false;
        }
        if (++_scalarHits % ScalarVerifyEvery == 0)
        {
            ScalarVerified++;
            if (!Same(previous.Props, SavedProperties.From(relic)))
            {
                ScalarFail(new InvalidOperationException($"{relic.Id}: taken as unchanged, but the game's saved properties differ"));
                return false;
            }
        }
        ScalarReused++;
        same = previous;
        return true;
    }

    private static void ScalarFail(Exception e)
    {
        ScalarEnabled = false;
        PatchSafety.Fault(ScalarName);
        Log.Error($"[{ModEntry.Id}] {ScalarName} disabled, relics' saved properties read by the game: {e}");
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

    /// <summary>Unlimited's list-width transpiler once its code is the known one (SnapshotCardReuse's upgrade bits allow it too).</summary>
    internal static MethodInfo? UnlimitedTranspilerMethod => _unlimitedTranspiler;

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
        try
        {
            ResolveBaseLibFields();
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {ScalarName}: BaseLib's saved fields not understood, relics' saved properties read by the game: {e.Message}");
        }
    }

    /// <summary>
    /// BaseLib 3.4.7 postfixes <c>FromInternal</c>: each of its registered "SavedSpireFields" whose target type the model has exports
    /// into the result, nothing else (a new object is only kept when one did). None registered: the game's result unchanged.
    /// </summary>
    private const string BaseLibFieldsPostfix = "BaseLib.Patches.Utils.SavedSpireFieldPatch:PostfixFromInternal";
    private const string BaseLibFieldsPostfixFingerprint = "860274C6B3FA909C";
    private const string BaseLibFieldsTargetsFingerprint = "791D9757ACB7F0A0";
    private static MethodInfo[] _allowedOnFromInternal = Array.Empty<MethodInfo>();
    private static Func<IList>? _baseLibFields;
    /// <summary>Relic types some registered field targets (its <c>TargetType.IsInstanceOfType</c>), for the fields counted then.</summary>
    private static readonly Dictionary<Type, bool> _fieldTargeted = new();
    private static int _fieldsCounted = -1;
    private static List<Type> _fieldTargets = new();

    private static void ResolveBaseLibFields()
    {
        MethodInfo postfix = AccessTools.Method(BaseLibFieldsPostfix) ?? throw new PatchSkippedException("BaseLib has no " + BaseLibFieldsPostfix);
        PatchSafety.RequireFingerprint(postfix, BaseLibFieldsPostfixFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(postfix.DeclaringType, "GetFieldsForModel")
            ?? throw new MissingMethodException(postfix.DeclaringType!.Name, "GetFieldsForModel"), BaseLibFieldsTargetsFingerprint);
        FieldInfo fields = AccessTools.Field(postfix.DeclaringType, "RegisteredFields") ?? throw new MissingFieldException(postfix.DeclaringType!.Name, "RegisteredFields");
        _baseLibFields = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.Field(null, fields), typeof(IList))).Compile();
        _allowedOnFromInternal = new[] { postfix };
    }

    /// <summary>Whether BaseLib's postfix would export a field into this relic type's saved properties (Limitless: Fur Coat, Golden Compass).</summary>
    private static bool FieldTargeted(Type type)
    {
        if (_baseLibFields == null)
        {
            return false;
        }
        IList fields = _baseLibFields();
        if (fields.Count != _fieldsCounted)
        {
            // Fields are only ever added (Register): their targets are read again when the count changes.
            _fieldTargets = fields.Cast<object>().Select(f => (Type)f.GetType().GetProperty("TargetType")!.GetValue(f)!).ToList();
            _fieldsCounted = fields.Count;
            _fieldTargeted.Clear();
        }
        if (!_fieldTargeted.TryGetValue(type, out bool targeted))
        {
            targeted = _fieldTargets.Any(t => t.IsAssignableFrom(type));
            _fieldTargeted[type] = targeted;
        }
        return targeted;
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
        if (!Enabled || ListenerCache.IsSuspended || !relic.IsMutable || Environment.CurrentManagedThreadId != _mainThread || !GuardsPass())
        {
            return relic.ToSerializable();
        }
        try
        {
            if (ScalarSame(relic, out SerializableRelic? same))
            {
                Reused++;
                return same!;
            }
        }
        catch (Exception e)
        {
            ScalarFail(e);
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

    private static long _guardsAt = long.MinValue;
    private static bool _guardsPass, _scalarGuardsPass;

    /// <summary>
    /// BaseLib's relic saves and other mods' patches on what this mirrors, read once per tick of <c>Environment.TickCount64</c> (one
    /// snapshot's hundreds of relics, not each of them): PatchSafety's answers are themselves reused for a while.
    /// </summary>
    private static bool GuardsPass()
    {
        long now = Environment.TickCount64;
        if (now != _guardsAt)
        {
            _guardsPass = !(_registeredSaves != null && _registeredSaves().Count != 0)
                && !PatchSafety.IsPatchedByOthers(_toSerializable, _allowedOnToSerializable) && !PatchSafety.IsPatchedByOthers(_serialize, _allowedOnSerialize)
                && !PatchSafety.IsPatchedByOthers(_savedPropertiesFrom);
            _scalarGuardsPass = ScalarApplied && !PatchSafety.IsPatchedByOthers(_fromInternal!, _allowedOnFromInternal) && !PatchSafety.IsPatchedByOthers(_shouldSerialize!);
            _guardsAt = now;
        }
        return _guardsPass;
    }

#if BENCH
    internal static SerializableRelic SerializeForBench(RelicModel relic) => Serialize(relic);
    /// <summary>Why the scalar part last let the game's call run.</summary>
    internal static string ScalarWhy = "";
#endif

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
