using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Limitless (optional) stacks enchantments in a <c>MultiEnchantment</c> that keeps a saved copy of its inner cards
/// (<c>SerializableEnchantmentsOnCards</c>) and rebuilds it with <c>ToSerializable</c> after every call it forwards:
/// <c>RecalculateValues</c> alone runs for every card in combat at every state change. Each <c>ToSerializable</c> makes
/// BaseLib attach a new entry (its extended save data) to the new SerializableCard / SerializableEnchantment in a
/// ConditionalWeakTable: tens of thousands of entries per action at 8,400 cards, ~40% of an action plus the garbage
/// collection cost of their dependent handles.
///
/// Before Limitless replaces the list, this computes what the new list would hold with the calls vanilla makes
/// (<c>SavedProperties.From</c>, BaseLib's registered save getters) and compares it with the list in place: ids,
/// upgrade, floor, amounts, saved properties (cards by reference) and extended save data. When all of it is equal the
/// list in place stays: same content, and its objects keep their BaseLib entries (nothing is removed from any table, a
/// mod looking them up still finds them). Anything else, or another mod patching these methods: Limitless runs as is.
/// </summary>
public static class LimitlessReserialize
{
    public const string Name = "LimitlessReserialize";

    private const string LimitlessAssembly = "Limitless";
    private const string MultiEnchantmentType = "Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment";
    private const string HandlersType = "BaseLib.Patches.Saves.ExtendedSaveHandlers`2";

    /// <summary>Limitless v1.1.7 <c>MultiEnchantment.SetSerializableCards</c>: the list it builds is what this compares with.</summary>
    private const string SetSerializableCardsFingerprint = "3C0315F6F71E02A5";
    /// <summary>v0.107.1 <c>CardModel.ToSerializable</c> and <c>EnchantmentModel.ToSerializable</c>, mirrored by the comparison.</summary>
    private const string CardToSerializableFingerprint = "BBD751B8837741A9";
    private const string EnchantmentToSerializableFingerprint = "9AD225E2780EB1EA";
    /// <summary>BaseLib 3.4.7: the postfixes storing <c>new ExtendedSaveData(model)</c> for the new object, that data's
    /// constructor (runs the registered getters) and <c>DictForType</c> (a missing dictionary reads as an empty one).</summary>
    private const string ExtendedDataForCardFingerprint = "0122CA32FEFEE27D";
    private const string ExtendedDataForEnchantmentFingerprint = "426B8A8581C7A3C1";
    private const string ExtendedSaveDataCtorFingerprint = "FDA42A386A712C90";
    private const string DictForTypeFingerprint = "3FAE5C0C641C9839";
    /// <summary>v0.107.1 <c>SavedProperties.From</c> / <c>FromInternal</c>: null when the type has no saved property.</summary>
    private const string SavedPropertiesFromFingerprint = "AABDC7CD9516404E";
    private const string SavedPropertiesFromInternalFingerprint = "34B1A98D9F811477";

    public static bool Enabled { get; set; } = true;

    public static bool Applied { get; private set; }

    /// <summary>Lists kept (identical content), and lists left to Limitless to rebuild.</summary>
    public static long Kept { get; private set; }

    public static long Rebuilt { get; private set; }

    private static MethodInfo? _original;
    private static MethodInfo? _cardToSerializable;
    private static MethodInfo? _enchantmentToSerializable;
    private static MethodInfo? _savedPropertiesFrom;
    private static AccessTools.FieldRef<object, List<SerializableCard>?>? _stored;
    private static ExtendedData? _cardData;
    private static ExtendedData? _enchantmentData;

    public static void Apply(Harmony harmony)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == LimitlessAssembly))
        {
            ApplyNow(harmony);
            return;
        }
        // Mod load order isn't guaranteed: apply when Limitless loads, if it ever does.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        throw new PatchSkippedException("Limitless not loaded (applies if it loads later)");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == LimitlessAssembly)
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            ModEntry.TryApply(Name, ApplyNow);
        }
    }

    private static void ApplyNow(Harmony harmony)
    {
        Type multi = AccessTools.TypeByName(MultiEnchantmentType) ?? throw new PatchSkippedException("Limitless has no " + MultiEnchantmentType);
        MethodInfo original = AccessTools.Method(multi, "SetSerializableCards", new[] { typeof(List<CardModel>) })
            ?? throw new MissingMethodException(MultiEnchantmentType, "SetSerializableCards");
        PatchSafety.RequireFingerprint(original, SetSerializableCardsFingerprint);
        _cardToSerializable = AccessTools.Method(typeof(CardModel), nameof(CardModel.ToSerializable));
        _enchantmentToSerializable = AccessTools.Method(typeof(EnchantmentModel), nameof(EnchantmentModel.ToSerializable));
        PatchSafety.RequireFingerprint(_cardToSerializable, CardToSerializableFingerprint);
        PatchSafety.RequireFingerprint(_enchantmentToSerializable, EnchantmentToSerializableFingerprint);
        _savedPropertiesFrom = AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.From));
        PatchSafety.RequireFingerprint(_savedPropertiesFrom, SavedPropertiesFromFingerprint);
        PatchSafety.RequireFingerprint(AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.FromInternal)), SavedPropertiesFromInternalFingerprint);
        Type handlers = AccessTools.TypeByName(HandlersType) ?? throw new PatchSkippedException("BaseLib has no " + HandlersType);
        _cardData = new ExtendedData(handlers, typeof(CardModel), typeof(SerializableCard),
            "BaseLib.Patches.Saves.ExtendedSavePatches+PrepExtendedCardData:ExtendedDataForCard", ExtendedDataForCardFingerprint);
        _enchantmentData = new ExtendedData(handlers, typeof(EnchantmentModel), typeof(SerializableEnchantment),
            "BaseLib.Patches.Saves.ExtendedSavePatches+PrepExtendedEnchantmentData:ExtendedDataForEnchantment", ExtendedDataForEnchantmentFingerprint);
        _stored = AccessTools.FieldRefAccess<List<SerializableCard>?>(multi, "<SerializableEnchantmentsOnCards>k__BackingField");
        _original = original;
        _mainThread = Environment.CurrentManagedThreadId;
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(LimitlessReserialize), nameof(Prefix)));
        Applied = true;
    }

    /// <summary>
    /// BaseLib's <c>ExtendedSaveHandlers&lt;TModel, TSerializable&gt;</c> by reflection (this DLL must not reference
    /// BaseLib): the data its postfix would store for a model, and the data an object already has, read without adding.
    /// </summary>
    private sealed class ExtendedData
    {
        public readonly MethodInfo Postfix;
        public readonly Func<object, object?> Stored;
        public readonly Func<object, Dictionary<Type, IDictionary>> Dictionaries;

        /// <summary>
        /// What <c>new ExtendedSaveData(model)</c> holds, built without allocating a new one: a reused data object whose
        /// dictionaries are emptied, then every registered getter run into it, as that constructor does (getters only
        /// add through <c>DictForType</c>, which returns the existing dictionary).
        /// </summary>
        public object Expected(object model)
        {
            foreach (IDictionary dictionary in Dictionaries(_scratch).Values)
            {
                dictionary.Clear();
            }
            _fill(model, _scratch);
            return _scratch;
        }

        /// <summary>The ids of the registered saves (the only keys a getter adds), refreshed when one is registered.</summary>
        public string[] Ids
        {
            get
            {
                IList saves = _registeredSaves();
                if (_ids.Length != saves.Count)
                {
                    _ids = saves.Cast<object>().Select(save => (string)AccessTools.Property(save.GetType(), "Id").GetValue(save)!).ToArray();
                }
                return _ids;
            }
        }

        private readonly object _scratch;
        private readonly Action<object, object> _fill;
        private readonly Func<IList> _registeredSaves;
        private string[] _ids = Array.Empty<string>();

        public ExtendedData(Type handlersDefinition, Type model, Type serializable, string postfix, string postfixFingerprint)
        {
            Postfix = AccessTools.Method(postfix) ?? throw new PatchSkippedException("BaseLib has no " + postfix);
            PatchSafety.RequireFingerprint(Postfix, postfixFingerprint);
            Type dataDefinition = handlersDefinition.GetNestedType("ExtendedSaveData", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new PatchSkippedException("BaseLib has no ExtendedSaveData");
            PatchSafety.RequireFingerprint(dataDefinition.GetConstructors().Single(c => c.GetParameters().Length == 1), ExtendedSaveDataCtorFingerprint);
            PatchSafety.RequireFingerprint(AccessTools.Method(dataDefinition, "DictForType"), DictForTypeFingerprint);
            Type handlers = handlersDefinition.MakeGenericType(model, serializable);
            Type data = dataDefinition.MakeGenericType(model, serializable);

            ParameterExpression arg = Expression.Parameter(typeof(object));
            Dictionaries = Expression.Lambda<Func<object, Dictionary<Type, IDictionary>>>(
                Expression.Field(Expression.Convert(arg, data), "Dictionaries"), arg).Compile();
            _scratch = (data.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException(data.Name, ".ctor()")).Invoke(null);

            // The constructor's loop: foreach (save in RegisteredSaves) save.Getter(model, data).
            PropertyInfo registered = AccessTools.Property(handlers, "RegisteredSaves") ?? throw new MissingMemberException(handlers.Name, "RegisteredSaves");
            _registeredSaves = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.Property(null, registered), typeof(IList))).Compile();
            ParameterExpression target = Expression.Parameter(typeof(object));
            ParameterExpression saves = Expression.Variable(registered.PropertyType);
            ParameterExpression i = Expression.Variable(typeof(int));
            LabelTarget done = Expression.Label();
            Expression save = Expression.Property(saves, "Item", i);
            _fill = Expression.Lambda<Action<object, object>>(Expression.Block(new[] { saves, i },
                Expression.Assign(saves, Expression.Property(null, registered)),
                Expression.Assign(i, Expression.Constant(0)),
                Expression.Loop(Expression.IfThenElse(
                    Expression.LessThan(i, Expression.Property(saves, "Count")),
                    Expression.Block(
                        Expression.Invoke(Expression.Property(save, "Getter"), Expression.Convert(arg, model), Expression.Convert(target, data)),
                        Expression.PostIncrementAssign(i)),
                    Expression.Break(done)), done)), arg, target).Compile();

            // ExtendedData is a NotNullSpireField whose indexer adds a default entry when missing: read its table instead.
            object field = AccessTools.Property(handlers, "ExtendedData").GetValue(null)
                ?? throw new PatchSkippedException("BaseLib's ExtendedData is null");
            object table = AccessTools.Field(field.GetType(), "_table")?.GetValue(field)
                ?? throw new PatchSkippedException("BaseLib's ExtendedData has no _table");
            MethodInfo tryGet = table.GetType().GetMethod("TryGetValue") ?? throw new MissingMethodException(table.GetType().Name, "TryGetValue");
            ParameterExpression found = Expression.Variable(data);
            Stored = Expression.Lambda<Func<object, object?>>(Expression.Block(new[] { found },
                Expression.Condition(Expression.Call(Expression.Constant(table), tryGet, Expression.Convert(arg, serializable), found),
                    Expression.Convert(found, typeof(object)), Expression.Constant(null))), arg).Compile();
        }
    }

    private static long _patchesCheckedAt = long.MinValue;
    private static bool _patchedByOthers;

    /// <summary>
    /// Another mod's patch on the methods this reproduces (BaseLib's known postfixes aside). Runs millions of times per
    /// combat: rechecked every 2 s, as <see cref="PatchSafety.IsPatchedByOthers(MethodBase?)"/> caches it.
    /// </summary>
    private static bool PatchedByOthers()
    {
        long now = Environment.TickCount64;
        if (now - _patchesCheckedAt >= 2000)
        {
            _patchedByOthers = PatchSafety.IsPatchedByOthers(_original) || PatchSafety.IsPatchedByOthers(_savedPropertiesFrom)
                || PatchSafety.IsPatchedByOthers(_cardToSerializable, _cardData!.Postfix)
                || PatchSafety.IsPatchedByOthers(_enchantmentToSerializable, _enchantmentData!.Postfix);
            _patchesCheckedAt = now;
        }
        return _patchedByOthers;
    }

    private static bool Prefix(EnchantmentModel __instance, List<CardModel> cards)
    {
        if (!Enabled || PatchedByOthers())
        {
            return true;
        }
        // The comparison reuses one data object: main thread only, and a getter re-entering this lets Limitless do the
        // inner call.
        if (_comparing || Environment.CurrentManagedThreadId != _mainThread)
        {
            return true;
        }
        _comparing = true;
        try
        {
            if (Unchanged(__instance, cards))
            {
                Kept++;
#if BENCH
                if (Kept % VerifyEvery == 0)
                {
                    Verify(__instance, cards);
                }
#endif
                return false;
            }
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to Limitless: {e}");
        }
        finally
        {
            _comparing = false;
        }
        Rebuilt++;
        return true;
    }

    private static bool _comparing;
    private static int _mainThread;

#if BENCH
    /// <summary>One kept list in this many is compared with what Limitless would have built (Debug builds).</summary>
    private const int VerifyEvery = 4096;
    public static long VerifyMismatches { get; private set; }
    private static MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter? _keptWriter, _freshWriter;

    /// <summary>Packet bytes (BaseLib's extended data included) of the kept list and of a list built as Limitless would.</summary>
    private static void Verify(EnchantmentModel multi, List<CardModel> cards)
    {
        _keptWriter ??= new() { WarnOnGrow = false };
        _freshWriter ??= new() { WarnOnGrow = false };
        _keptWriter.Reset();
        _freshWriter.Reset();
        foreach (SerializableCard card in _stored!(multi)!)
        {
            card.Serialize(_keptWriter);
        }
        foreach (CardModel card in cards)
        {
            card.ToSerializable().Serialize(_freshWriter);
        }
        if (!_keptWriter.Buffer.AsSpan(0, _keptWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition)))
        {
            VerifyMismatches++;
            Log.Error($"[{ModEntry.Id}] {Name}: kept list differs from a rebuilt one ({_keptWriter.BytePosition} vs {_freshWriter.BytePosition} bytes)");
        }
    }
#endif

    /// <summary>Whether <c>cards.Select(c => c.ToSerializable()).ToList()</c> would hold what the stored list holds.</summary>
    private static bool Unchanged(EnchantmentModel multi, List<CardModel>? cards)
    {
        List<SerializableCard>? stored = _stored!(multi);
        if (cards == null || stored == null || stored.Count != cards.Count)
        {
            return false;
        }
        for (int i = 0; i < cards.Count; i++)
        {
            if (!SameCard(cards[i], stored[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><c>CardModel.ToSerializable</c> and BaseLib's postfix, compared field by field.</summary>
    private static bool SameCard(CardModel? card, SerializableCard? saved)
    {
        // Not mutable: ToSerializable throws, let Limitless do it.
        if (card == null || saved == null || !card.IsMutable)
        {
            return false;
        }
        if (!Equals(saved.Id, card.Id) || saved.CurrentUpgradeLevel != card.CurrentUpgradeLevel
            || saved.FloorAddedToDeck != card.FloorAddedToDeck || !SameProps(PropsOf(card), saved.Props))
        {
            return false;
        }
        EnchantmentModel? enchantment = card.Enchantment;
        if (enchantment == null ? saved.Enchantment != null : !SameEnchantment(enchantment, saved.Enchantment))
        {
            return false;
        }
        return SameData(_cardData!, card, saved);
    }

    /// <summary><c>EnchantmentModel.ToSerializable</c> and BaseLib's postfix.</summary>
    private static bool SameEnchantment(EnchantmentModel enchantment, SerializableEnchantment? saved) =>
        saved != null && enchantment.IsMutable && Equals(saved.Id, enchantment.Id) && saved.Amount == enchantment.Amount
        && SameProps(PropsOf(enchantment), saved.Props) && SameData(_enchantmentData!, enchantment, saved);

    /// <summary><c>SavedProperties.From</c>, which returns null without reading anything when the type has no saved property.</summary>
    private static SavedProperties? PropsOf(AbstractModel model) =>
        SavedPropertiesTypeCache.GetJsonPropertiesForType(model.GetType()) == null ? null : SavedProperties.From(model);

    /// <summary>
    /// The extended save data BaseLib would store for the model against what the saved object has. Its readers get a
    /// missing dictionary as an empty one (DictForType), so both count as the same.
    /// </summary>
    private static bool SameData(ExtendedData data, object model, object saved)
    {
        object? stored = data.Stored(saved);
        if (stored == null)
        {
            return false;
        }
        Dictionary<Type, IDictionary> expected = data.Dictionaries(data.Expected(model));
        Dictionary<Type, IDictionary> actual = data.Dictionaries(stored);
        string[] ids = data.Ids;
        foreach ((Type type, IDictionary dictionary) in expected)
        {
            IDictionary? other = actual.GetValueOrDefault(type);
            if (dictionary.Count != (other?.Count ?? 0))
            {
                return false;
            }
            // Same count and every expected key (a registered id) present with the same value: same entries.
            int matched = 0;
            for (int i = 0; i < ids.Length && matched < dictionary.Count; i++)
            {
                if (!dictionary.Contains(ids[i]))
                {
                    continue;
                }
                if (!other!.Contains(ids[i]) || !SameValue(dictionary[ids[i]], other[ids[i]]))
                {
                    return false;
                }
                matched++;
            }
            if (matched != dictionary.Count)
            {
                return false;
            }
        }
        foreach ((Type type, IDictionary dictionary) in actual)
        {
            if (dictionary.Count != 0 && (!expected.TryGetValue(type, out IDictionary? mine) || mine.Count == 0))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Values compared only when that is safe: primitives, strings, enums, and empty arrays or standard generic
    /// collections of one type (nothing else to save in them).
    /// </summary>
    private static bool SameValue(object? a, object? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }
        Type type = a.GetType();
        if (type != b.GetType())
        {
            return false;
        }
        if (a is string || type.IsPrimitive || type.IsEnum)
        {
            return a.Equals(b);
        }
        return (type.IsArray || type.Namespace == "System.Collections.Generic")
            && a is ICollection { Count: 0 } && b is ICollection { Count: 0 };
    }

    private static bool SameProps(SavedProperties? a, SavedProperties? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }
        return SameList(a.ints, b.ints, static (x, y) => x == y)
            && SameList(a.bools, b.bools, static (x, y) => x == y)
            && SameList(a.strings, b.strings, static (x, y) => string.Equals(x, y, StringComparison.Ordinal))
            && SameList(a.intArrays, b.intArrays, static (x, y) => x == null ? y == null : y != null && x.AsSpan().SequenceEqual(y))
            && SameList(a.modelIds, b.modelIds, static (x, y) => Equals(x, y))
            // Saved properties hold the model's own SerializableCard objects: the same ones, not equal copies.
            && SameList(a.cards, b.cards, static (x, y) => ReferenceEquals(x, y))
            && SameList(a.cardArrays, b.cardArrays, static (x, y) => x == null ? y == null : y != null && x.Length == y.Length
                && x.Zip(y).All(pair => ReferenceEquals(pair.First, pair.Second)));
    }

    private static bool SameList<T>(List<SavedProperties.SavedProperty<T>>? a, List<SavedProperties.SavedProperty<T>>? b, Func<T, T, bool> same)
    {
        if (a == null || b == null)
        {
            return a == b;
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
