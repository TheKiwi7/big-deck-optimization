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
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
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

    /// <summary>A reuse found wrong, or this (or its scalar part) turned off, while building a snapshot: <see cref="SnapshotVerifier"/> builds it again.</summary>
    internal static long Detections { get; private set; }

#if BENCH
    /// <summary>Saved properties taken as unchanged (bdperf relicreuse blind): shows that the snapshot checks see a stale relic.</summary>
    public static bool BlindForTest { get; set; }
    /// <summary>Every relic of a scalar plan taken as unchanged without reading it (bdperf relicreuse scalarblind): the checks must see it.</summary>
    public static bool ScalarBlindForTest { get; set; }
#endif

    /// <summary>v0.107.1, v0.111, v0.113.x: <c>new SerializableRelic { Id, Props = SavedProperties.From(this), FloorAddedToDeck }</c> after AssertMutable.</summary>
    private const string ToSerializableFingerprint = "91A31F3A5C8BCB32";
    /// <summary>Writes the id, the properties and the floor, nothing else (v0.107.1 to v0.113.x).</summary>
    private const string SerializeFingerprint = "FEE0E78D320AB4F9";

    private const string BaseLibAssembly = "BaseLib";
    private const string HandlersType = "BaseLib.Patches.Saves.ExtendedSaveHandlers`2";
    /// <summary>BaseLib 3.4.7 to 3.4.8: stores <c>new ExtendedSaveData(relic)</c> for the new object, nothing else.</summary>
    private const string BaseLibPostfix = "BaseLib.Patches.Saves.ExtendedSavePatches+PrepExtendedRelicData:ExtendedDataForRelic";
    private const string BaseLibPostfixFingerprint = "A0902464D285F2CB";
    /// <summary>BaseLib's postfix on <c>SerializableRelic.Serialize</c> and what it calls: one write per registered save, none without.</summary>
    private const string BaseLibWrite = "BaseLib.Patches.Saves.ExtendedSavePatches+SerializeExtendedRelicData:WriteExtended";
    private const string BaseLibWriteFingerprint = "2932D9B1B34FDBBC";
    private const string HandlersWriteFingerprint = "D1DE163AF605BE38";
    /// <summary>
    /// "Unlimited: No Player Limit" transpiles every <c>Serialize</c> (the relic's, its properties', its combat state's): a list's
    /// count written on more bits (none in the relic's). The bits stay a function of the object's fields alone.
    /// </summary>
    private const string UnlimitedAssembly = "sts2unlimited";
    private const string UnlimitedTranspiler = "Sts2Unlimited.PacketSizePatch:Transpile_SerializeDeserialize";
    private const string UnlimitedTranspilerFingerprint = "908CDF014E4F04CF";
    private const string UnlimitedConstantFingerprint = "CB9DCCF74112728F";

    private static MethodInfo? _toSerializable, _serialize;
    /// <summary>
    /// What writes a reused object again: its properties' and its combat state's <c>Serialize</c>. A patch there keyed on the object
    /// (data attached when it was made, as BaseLib's extended data is) would write a reused object's old data: the game's call.
    /// </summary>
    private static MethodInfo? _propertiesSerialize, _relicStateSerialize;
    private static readonly MethodInfo _savedPropertiesFrom = AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.From));
    private static MethodInfo[] _allowedOnToSerializable = Array.Empty<MethodInfo>();
    private static MethodInfo[] _allowedOnSerialize = Array.Empty<MethodInfo>();
    private static MethodInfo[] _allowedUnlimitedOnly = Array.Empty<MethodInfo>();
    private static MethodInfo? _baseLibWrite;
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
        _propertiesSerialize = AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.Serialize), new[] { typeof(PacketWriter) })
            ?? throw new MissingMethodException(nameof(SavedProperties), nameof(SavedProperties.Serialize));
        _relicStateSerialize = AccessTools.Method(typeof(NetFullCombatState.RelicState), nameof(NetFullCombatState.RelicState.Serialize), new[] { typeof(PacketWriter) })
            ?? throw new MissingMethodException(nameof(NetFullCombatState.RelicState), nameof(NetFullCombatState.RelicState.Serialize));
        _mainThread = Environment.CurrentManagedThreadId;
        string[] loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToArray();
        if (loaded.Contains(BaseLibAssembly))
        {
            ResolveBaseLib();
        }
        if (UnlimitedCompat.Error is { } unlimitedError)
        {
            // Unlimited is there and its transpiler isn't the known one: it patches SerializableRelic.Serialize, this would back off at every relic.
            throw new PatchSkippedException("Unlimited's transpiler not understood: " + unlimitedError.Message);
        }
        AllowOnSerialize();
        if (!loaded.Contains(BaseLibAssembly))
        {
            // Mod load order isn't guaranteed. Until then its patches, if any, count as another mod's: the game's call.
            // (Unlimited, loaded later, is resolved by UnlimitedCompat.)
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
            Log.Info($"[{ModEntry.Id}] {ScalarName} left vanilla, relics' saved properties read by the game each time: {e.Message}");
        }
        WatchForPreflight();
    }

    /// <summary>
    /// What GuardsPass asks about (display only, never throws): the allowed lists are read at each preflight (BaseLib or
    /// Unlimited loading later changes them), and BaseLib's relic saves, which turn the whole reuse off, as a note.
    /// </summary>
    private static void WatchForPreflight()
    {
        PatchSafety.Watch(Name, _toSerializable, () => _allowedOnToSerializable);
        PatchSafety.Watch(Name, _serialize, () => _allowedOnSerialize);
        PatchSafety.Watch(Name, _savedPropertiesFrom);
        PatchSafety.Watch(Name, _propertiesSerialize, () => _allowedUnlimitedOnly);
        PatchSafety.Watch(Name, _relicStateSerialize, () => _allowedUnlimitedOnly);
        if (ScalarApplied)
        {
            PatchSafety.Watch(Name, _fromInternal, () => _allowedOnFromInternal);
            PatchSafety.Watch(Name, _shouldSerialize);
        }
        PatchSafety.WatchNote(Name, NoteRelicSaves);
    }

    /// <summary>Other mods' BaseLib relic saves: GuardsPass then leaves every relic to the game's serialization.</summary>
    private static void NoteRelicSaves()
    {
        if (_registeredSaves?.Invoke() is not { Count: > 0 } saves)
        {
            PatchSafety.ClearNote(Name, "relic_saves");
            return;
        }
        (string[] ids, string[] mods) = BaseLibTypes.ExtendedSaves(saves, null);
        PatchSafety.Note(Name, "relic_saves", $"relic data other mods save with BaseLib ({string.Join(", ", ids.Take(4))}{(ids.Length > 4 ? ", ..." : "")}): every relic's state is built as the game does", mods);
    }

    // --- Scalar part: an unchanged relic recognized without SavedProperties.From ---

    public const string ScalarName = "RelicStateReuse.Scalar";
    public static bool ScalarEnabled { get; set; } = true;
    public static bool ScalarApplied { get; private set; }
    /// <summary>
    /// The main patch applied but its scalar part was skipped (a fingerprint or a member it relies on changed): bdopt and the menu
    /// list it as not applied, as <c>ModEntry.NotApplied</c> does for a skipped patch.
    /// </summary>
    internal static bool ScalarNotApplied => Applied && !ScalarApplied;
    /// <summary>Relics given again without the game's <c>SavedProperties.From</c>, and how many of those were checked against it.</summary>
    public static long ScalarReused { get; private set; }
    public static long ScalarVerified { get; private set; }
#if BENCH
    internal static int ScalarVerifyEvery = 16;
#else
    private const int ScalarVerifyEvery = 256;
#endif

    /// <summary>v0.107.1 <c>FromInternal</c> (v0.111's and v0.113.x's in EquivalentFingerprints: only its property lookup moved).</summary>
    private const string FromInternalFingerprint = "F73EEC2208D6E517";
    /// <summary><c>SerializationConditionExtensions.ShouldSerialize</c> and <c>GetTypeDefaultValue</c>, v0.107.1 to v0.113.x.</summary>
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
        try
        {
            plan = BuildScalarPlan(type, properties);
        }
        catch (Exception e)
        {
            // A property this can't compile a getter for (a mod's relic): that type's game call, not a wrong reuse.
            plan = null;
            Log.Info($"[{ModEntry.Id}] {ScalarName}: {type.Name} left vanilla, its saved properties read by the game: {e.Message}");
        }
        _scalarPlans[type] = plan;
        return plan;
    }

    /// <summary>
    /// Compiled getters by the getter they call, compiled against its declaring type: <c>IsWax</c> and <c>IsMelted</c>, declared on
    /// <c>RelicModel</c>, are one getter for every relic type. Keyed by handle (a <c>PropertyInfo</c> differs by its reflected type)
    /// and declaring type (a generic type's instantiations may share a handle).
    /// </summary>
    private static readonly Dictionary<(Type, RuntimeMethodHandle), Func<object, int>> _getters = new();
#if BENCH
    /// <summary>Getters compiled so far (bdperf relicreuse): a few dozen for all relic types, not two or more per type.</summary>
    internal static int GettersCompiled;
    /// <summary>Relic types with a scalar plan built so far, for comparison with <see cref="GettersCompiled"/>.</summary>
    internal static int PlansBuilt => _scalarPlans.Count;
#endif

    private static Func<object, int> GetterOf(PropertyInfo property, MethodInfo getter)
    {
        Type declaring = property.DeclaringType!;
        (Type, RuntimeMethodHandle) key = (declaring, getter.MethodHandle);
        if (!_getters.TryGetValue(key, out Func<object, int>? compiled))
        {
            Type valueType = property.PropertyType;
            ParameterExpression model = Expression.Parameter(typeof(object));
            Expression read = Expression.Property(Expression.Convert(model, declaring), property);
            Expression asInt = valueType == typeof(bool) ? Expression.Condition(read, Expression.Constant(1), Expression.Constant(0))
                : valueType == typeof(int) ? read : Expression.Convert(read, typeof(int));
            compiled = Expression.Lambda<Func<object, int>>(asInt, model).Compile();
            _getters[key] = compiled;
#if BENCH
            GettersCompiled++;
#endif
        }
        return compiled;
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
        for (int i = 0; i < count; i++)
        {
            PropertyInfo property = properties[i];
            SavedPropertyAttribute? attribute = property.GetCustomAttribute<SavedPropertyAttribute>();
            Type valueType = property.PropertyType;
            if (attribute == null || attribute.defaultBehaviour is not (SerializationCondition.AlwaysSave or SerializationCondition.SaveIfNotTypeDefault)
                || property.GetMethod is not { IsStatic: false } getter || property.GetIndexParameters().Length != 0
                || property.DeclaringType is not { } declaring || !declaring.IsAssignableFrom(type)
                || !(valueType == typeof(bool) || valueType == typeof(int) || (valueType.IsEnum && Enum.GetUnderlyingType(valueType) == typeof(int))))
            {
                return null;
            }
            plan.Names[i] = property.Name;
            plan.Getters[i] = GetterOf(property, getter);
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
                // No tolerance: serialization reused instead of run.
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
        // Relics before this one in the same snapshot took the same shortcut: SnapshotVerifier builds it again, this part off.
        Detections++;
        ScalarEnabled = false;
        PatchSafety.Fault(ScalarName);
        Log.Error($"[{ModEntry.Id}] {ScalarName} disabled, relics' saved properties read by the game: {e}");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name != BaseLibAssembly)
        {
            return;
        }
        try
        {
            ResolveBaseLib();
        }
        catch (Exception e)
        {
            // Its postfix on ToSerializable then counts as another mod's patch: every relic backs off to the game's call.
            Log.Info($"[{ModEntry.Id}] {Name}: BaseLib's relic patches not understood, left vanilla (relics serialized by the game): {e.Message}");
        }
    }

    /// <summary>
    /// Unlimited's list-width transpiler, resolved on its own whatever <see cref="Apply"/> does (a fingerprint of this patch changed,
    /// BaseLib's relic patches not understood): SnapshotCardReuse allows it on the card and pile serializers too, and loses nothing
    /// that doesn't depend on relic serialization. Read at first use when Unlimited is already loaded, or when it loads later.
    /// </summary>
    private static class UnlimitedCompat
    {
        internal static MethodInfo? Transpiler;
        /// <summary>Unlimited loaded and its transpiler not the known one: why.</summary>
        internal static Exception? Error;
        private static int _resolved;

        static UnlimitedCompat()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == UnlimitedAssembly))
                {
                    Resolve(late: false);
                }
            }
            catch (Exception e)
            {
                Error ??= e;
            }
        }

        private static void OnLoad(object? sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name == UnlimitedAssembly)
            {
                Resolve(late: true);
            }
        }

        private static void Resolve(bool late)
        {
            if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0)
            {
                return;
            }
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;
                MethodInfo transpiler = AccessTools.Method(UnlimitedTranspiler) ?? throw new PatchSkippedException("Unlimited has no " + UnlimitedTranspiler);
                PatchSafety.RequireFingerprint(transpiler, UnlimitedTranspilerFingerprint);
                PatchSafety.RequireFingerprint(AccessTools.Method(transpiler.DeclaringType, "IsVanillaListBitsConstant")
                    ?? throw new MissingMethodException("PacketSizePatch", "IsVanillaListBitsConstant"), UnlimitedConstantFingerprint);
                Transpiler = transpiler;
                AllowOnSerialize();
            }
            catch (Exception e)
            {
                Error = e;
                if (late)
                {
                    // Loaded after this mod's patches: its transpiler counts as another mod's patch where it runs (relics, cards, piles).
                    Log.Info($"[{ModEntry.Id}] {Name}: Unlimited's transpiler not understood, left vanilla where it patches: {e.Message}");
                }
            }
        }
    }

    /// <summary>Unlimited's list-width transpiler once its code is the known one (SnapshotCardReuse's upgrade bits allow it too).</summary>
    internal static MethodInfo? UnlimitedTranspilerMethod => UnlimitedCompat.Transpiler;

    private static void AllowOnSerialize()
    {
        MethodInfo? unlimited = UnlimitedCompat.Transpiler;
        _allowedOnSerialize = new[] { _baseLibWrite, unlimited }.Where(m => m != null).ToArray()!;
        _allowedUnlimitedOnly = unlimited != null ? new[] { unlimited } : Array.Empty<MethodInfo>();
    }

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
            // Its postfix on FromInternal then counts as another mod's patch: the scalar part backs off (recorded as a back-off).
            Log.Info($"[{ModEntry.Id}] {ScalarName}: BaseLib's saved fields not understood, left vanilla (relics' saved properties read by the game): {e.Message}");
        }
    }

    /// <summary>
    /// BaseLib 3.4.7 to 3.4.8 postfixes <c>FromInternal</c>: each of its registered "SavedSpireFields" whose target type the model has exports
    /// into the result, nothing else (a new object is only kept when one did). None registered: the game's result unchanged.
    /// </summary>
    internal const string BaseLibFieldsPostfix = "BaseLib.Patches.Utils.SavedSpireFieldPatch:PostfixFromInternal";
    internal const string BaseLibFieldsPostfixFingerprint = "860274C6B3FA909C";
    private const string BaseLibFieldsTargetsFingerprint = "791D9757ACB7F0A0";
    private static MethodInfo[] _allowedOnFromInternal = Array.Empty<MethodInfo>();
    private static Func<IList>? _baseLibFields;
    /// <summary>Relic types some registered field targets (its <c>TargetType.IsInstanceOfType</c>), for the fields counted then.</summary>
    private static readonly Dictionary<Type, bool> _fieldTargeted = new();
    private static int _fieldsCounted = -1;
    /// <summary>Null: a target couldn't be read, every relic type counts as targeted.</summary>
    private static List<Type>? _fieldTargets = new();

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
            try
            {
                _fieldTargets = fields.Cast<object>().Select(TargetOf).ToList();
            }
            catch (Exception e)
            {
                // A target this can't read: any relic may be one, every relic gets the game's call (not a wrong reuse).
                _fieldTargets = null;
                Log.Info($"[{ModEntry.Id}] {ScalarName}: a BaseLib saved field's target not understood, left vanilla: {e.Message}");
            }
            _fieldsCounted = fields.Count;
            _fieldTargeted.Clear();
        }
        if (_fieldTargets == null)
        {
            return true;
        }
        if (!_fieldTargeted.TryGetValue(type, out bool targeted))
        {
            targeted = _fieldTargets.Any(t => t.IsAssignableFrom(type));
            _fieldTargeted[type] = targeted;
        }
        return targeted;
    }

    private const string BaseLibFieldInterface = "BaseLib.Utils.ISavedSpireField";

    /// <summary>A registered field's target type, read through BaseLib's interface (a subclass hiding <c>TargetType</c> can't make it ambiguous).</summary>
    private static Type TargetOf(object field)
    {
        Type type = field.GetType();
        PropertyInfo target = type.GetInterface(BaseLibFieldInterface)?.GetProperty("TargetType") ?? type.GetProperty("TargetType")
            ?? throw new MissingMemberException(type.Name, "TargetType");
        return (Type)target.GetValue(field)!;
    }

    /// <summary>
    /// Each <c>relic.ToSerializable()</c> of <c>FromRun</c> becomes: <see cref="Serialize"/>, and when it gives null, the game's
    /// own call, made from <c>FromRun</c> itself. The game's errors there (an invalid saved property: a JsonException) then
    /// leave through no frame of this class, so <see cref="PatchGuard"/> doesn't take them for this patch's and turn it off.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        MethodInfo reuse = AccessTools.Method(typeof(RelicStateReuse), nameof(Serialize));
        LocalBuilder? kept = null;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_toSerializable!))
            {
                _replaced++;
                kept ??= generator.DeclareLocal(typeof(SerializableRelic));
                Label have = generator.DefineLabel();
                Label done = generator.DefineLabel();
                // (relic) -> (relic, reused or null)
                yield return new CodeInstruction(OpCodes.Dup).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                yield return new CodeInstruction(OpCodes.Call, reuse);
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Brtrue, have);
                // Null: (relic) -> the game's call, as it was.
                yield return new CodeInstruction(OpCodes.Pop);
                yield return new CodeInstruction(instruction.opcode, instruction.operand);
                yield return new CodeInstruction(OpCodes.Br, done);
                // (relic, reused) -> (reused)
                yield return new CodeInstruction(OpCodes.Stloc, kept).WithLabels(have);
                yield return new CodeInstruction(OpCodes.Pop);
                yield return new CodeInstruction(OpCodes.Ldloc, kept);
                yield return new CodeInstruction(OpCodes.Nop).WithLabels(done);
                continue;
            }
            yield return instruction;
        }
    }

    /// <summary>The relic's serialized object, built or given again; null: the game's <c>ToSerializable</c> runs instead.</summary>
    private static SerializableRelic? Serialize(RelicModel relic)
    {
        // Inside RunVanilla the result is the reference this is compared with (SnapshotVerifier, bdperf): the game's own.
        if (!Enabled || ListenerCache.IsSuspended || !relic.IsMutable || Environment.CurrentManagedThreadId != _mainThread || !GuardsPass())
        {
            return null;
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
        catch (System.Text.Json.JsonException)
        {
            // An invalid property: the game's error. Its own call throws it again, at the same place, from FromRun.
            return null;
        }
        catch (Exception e)
        {
            Fail(e);
            return null;
        }
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
            return null;
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
                // No tolerance: the previous object is given again instead of a new ToSerializable.
                && !PatchSafety.IsPatchedByOthers(_toSerializable, _allowedOnToSerializable) && !PatchSafety.IsPatchedByOthers(_serialize, _allowedOnSerialize)
                && !PatchSafety.IsPatchedByOthers(_savedPropertiesFrom)
                && !PatchSafety.IsPatchedByOthers(_propertiesSerialize, _allowedUnlimitedOnly) && !PatchSafety.IsPatchedByOthers(_relicStateSerialize, _allowedUnlimitedOnly);
            _scalarGuardsPass = ScalarApplied && !PatchSafety.IsPatchedByOthers(_fromInternal!, _allowedOnFromInternal) && !PatchSafety.IsPatchedByOthers(_shouldSerialize!);
            _guardsAt = now;
        }
        return _guardsPass;
    }

#if BENCH
    internal static SerializableRelic SerializeForBench(RelicModel relic) => Serialize(relic) ?? relic.ToSerializable();
    /// <summary>Why the scalar part last let the game's call run.</summary>
    internal static string ScalarWhy = "";
#endif

    private static void Fail(Exception e)
    {
        Detections++;
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
