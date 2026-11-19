using System;
using System.Linq;
using System.Reflection;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Whether any loaded mod defines a concrete type deriving from (or implementing) a BaseLib type, without referencing
/// BaseLib: only BaseLib and assemblies referencing it can.
/// </summary>
internal static class BaseLibTypes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _none = new();
    private static int _watching;
    private static long _changes;

    /// <summary>Bumped each time a <see cref="NoneDefined"/> answer turns false (an assembly defining such a type loaded).</summary>
    public static long Changes => System.Threading.Interlocked.Read(ref _changes);

    /// <summary>
    /// Whether no loaded assembly defines one (cached; an assembly loading later that defines one turns it false).
    /// </summary>
    public static bool NoneDefined(string baseTypeName)
    {
        if (System.Threading.Interlocked.Exchange(ref _watching, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                // Runs inside another assembly's load: never throw there. Can't tell = a type may exist.
                bool may;
                try
                {
                    may = MayDefine(args.LoadedAssembly);
                }
                catch
                {
                    may = true;
                }
                if (!may)
                {
                    return;
                }
                // An assembly depending on BaseLib loaded: assemblies loaded before it that reference it may now derive from
                // its types (they were judged without it, an answer not kept), so every loaded one is looked at again.
                foreach (string name in _none.Keys)
                {
                    if (_none.TryGetValue(name, out bool none) && none && (Defines(args.LoadedAssembly, name) || AnyDefined(name)))
                    {
                        _none[name] = false;
                        System.Threading.Interlocked.Increment(ref _changes);
                    }
                }
            };
        }
#if BENCH
        if (AssumedForTest.Contains(baseTypeName))
        {
            return false;
        }
#endif
        return _none.GetOrAdd(baseTypeName, name => !AnyDefined(name));
    }

#if BENCH
    /// <summary>
    /// Test builds (<c>bdperf baselibtypes assume &lt;BaseLib type&gt;</c>): answer as if a loaded mod defined a concrete type deriving
    /// from it, so the branch for that case runs (the subscriber walk BaseLib's card modifiers need, the type plaque walk) without a
    /// test assembly referencing BaseLib (this DLL must not; a dynamic one isn't looked at). The walks then find nothing, as the game.
    /// </summary>
    internal static readonly System.Collections.Generic.HashSet<string> AssumedForTest = new();
#endif

    /// <summary>True when some loaded assembly defines one, or the BaseLib type is missing (can't tell).</summary>
    public static bool AnyDefined(string baseTypeName)
    {
#if BENCH
        if (AssumedForTest.Contains(baseTypeName))
        {
            return true;
        }
#endif
        try
        {
            return Reflect.TypeByName(baseTypeName) == null
                || AppDomain.CurrentDomain.GetAssemblies().Any(a => MayDefine(a) && Defines(a, baseTypeName));
        }
        catch
        {
            return true; // Can't tell: don't skip.
        }
    }

    private static Func<System.Collections.IList>? _spireFields;
    private static bool _spireFieldsUnreadable;
    private static int _lookForBaseLib = 1, _watchingLoads;
    private static int _spireFieldsCounted = -1;
    private static Type[] _spireFieldTargets = Array.Empty<Type>();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _spireTargeted = new();
    private static readonly object _spireLock = new();

    /// <summary>
    /// Whether a BaseLib SavedSpireField targets models of this type: BaseLib's postfix on <c>SavedProperties.FromInternal</c>
    /// then exports its value into the model's saved properties, which the game's own property list for the type doesn't
    /// show. Fields are only ever added (registered at BaseLib's post-init): targets are read again when their count
    /// changes. No BaseLib: false. BaseLib whose registry can't be read: true (can't tell).
    /// </summary>
    public static bool SpireFieldTargets(Type type)
    {
        // A bundled copy holding a field (rule B): its fields' targets aren't read, every type counts (asked at each use).
        if (TwinHoldsField())
        {
            return true;
        }
        System.Collections.IList? fields = SpireFields();
        if (fields == null)
        {
            return _spireFieldsUnreadable;
        }
        try
        {
            lock (_spireLock)
            {
                if (fields.Count != _spireFieldsCounted)
                {
                    _spireFieldTargets = fields.Cast<object>().Select(f => (Type)f.GetType().GetProperty("TargetType")!.GetValue(f)!).ToArray();
                    _spireFieldsCounted = fields.Count;
                    _spireTargeted.Clear();
                }
            }
            return _spireTargeted.GetOrAdd(type, t => _spireFieldTargets.Any(target => target.IsAssignableFrom(t)));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>How many SavedSpireFields BaseLib has registered (-1: no BaseLib or unreadable): caches built on
    /// <see cref="SpireFieldTargets"/> are rebuilt when it changes.</summary>
    public static int SpireFieldCount()
    {
        try
        {
            // With the bundled copies' fields (rule B): both only grow, so the sum changes whenever either does.
            int own = SpireFields()?.Count ?? -1, twins = TwinFieldCount();
            return twins == 0 ? own : Math.Max(own, 0) + twins;
        }
        catch
        {
            return -1;
        }
    }

    private static System.Collections.IList? SpireFields()
    {
        if (_spireFields != null)
        {
            return _spireFields();
        }
        if (_spireFieldsUnreadable)
        {
            return null;
        }
        // No BaseLib yet: looked for again only after an assembly loads (this runs in hot paths).
        if (System.Threading.Interlocked.Exchange(ref _lookForBaseLib, 0) == 0)
        {
            return null;
        }
        if (System.Threading.Interlocked.Exchange(ref _watchingLoads, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, _) => System.Threading.Interlocked.Exchange(ref _lookForBaseLib, 1);
        }
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BaseLib"))
        {
            return null;
        }
        try
        {
            FieldInfo field = Reflect.Field(Reflect.TypeByName("BaseLib.Patches.Utils.SavedSpireFieldPatch"), "RegisteredFields")
                ?? throw new MissingFieldException("SavedSpireFieldPatch", "RegisteredFields");
            _spireFields = System.Linq.Expressions.Expression.Lambda<Func<System.Collections.IList>>(
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(null, field), typeof(System.Collections.IList))).Compile();
            return _spireFields();
        }
        catch
        {
            _spireFieldsUnreadable = true;
            return null;
        }
    }

    /// <summary>
    /// Whether the assembly may define a type deriving from a BaseLib type: BaseLib itself, or an assembly referencing
    /// BaseLib directly or through another loaded assembly (a type deriving from a mod's own BaseLib subclass needs no
    /// reference to BaseLib in its metadata).
    /// </summary>
    public static bool MayDefine(Assembly assembly)
    {
        try
        {
            return !assembly.IsDynamic && DependsOnBaseLib(assembly.GetName().Name!, assembly, 0, out _);
        }
        catch
        {
            return !assembly.IsDynamic; // Can't tell: it may.
        }
    }

    /// <summary>Per assembly name: whether it depends on BaseLib (only answers about loaded assemblies are kept).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _dependsOnBaseLib = new();

    private static System.Collections.Generic.Dictionary<string, Assembly>? _loadedByName;
    private static int _loads, _loadsTabled = -1, _countingLoads;
    private static readonly object _loadedLock = new();

    /// <summary>Assemblies loaded since this started counting: tables and incomplete answers are valid while it stays.</summary>
    private static int Loads
    {
        get
        {
            if (System.Threading.Interlocked.Exchange(ref _countingLoads, 1) == 0)
            {
                AppDomain.CurrentDomain.AssemblyLoad += (_, _) => System.Threading.Interlocked.Increment(ref _loads);
            }
            return System.Threading.Volatile.Read(ref _loads);
        }
    }

    /// <summary>Subscribes the load counter (idempotent; never throws).</summary>
    public static void StartCountingLoads()
    {
        try
        {
            _ = Loads;
        }
        catch
        {
            // Counting starts at the first use instead.
        }
    }

    /// <summary>The loaded (non-dynamic) assembly of that name, from a table rebuilt after assemblies load.</summary>
    private static Assembly? Loaded(string name)
    {
        lock (_loadedLock)
        {
            int loads = Loads;
            if (_loadedByName == null || loads != _loadsTabled)
            {
                _loadedByName = new System.Collections.Generic.Dictionary<string, Assembly>();
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!assembly.IsDynamic && assembly.GetName().Name is { } loadedName)
                    {
                        _loadedByName.TryAdd(loadedName, assembly);
                    }
                }
                _loadsTabled = loads;
            }
            return _loadedByName.TryGetValue(name, out Assembly? found) ? found : null;
        }
    }

    /// <summary>Incomplete "doesn't depend" answers, valid until the next assembly load.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _incompleteAt = new();

    /// <summary><paramref name="complete"/>: false when an unloaded reference was taken as not depending (answer not kept).</summary>
    private static bool DependsOnBaseLib(string name, Assembly? loaded, int depth, out bool complete)
    {
        complete = true;
        if (name == "BaseLib")
        {
            return true;
        }
        if (_dependsOnBaseLib.TryGetValue(name, out bool known))
        {
            return known;
        }
        if (_incompleteAt.TryGetValue(name, out int at) && at == Loads)
        {
            complete = false;
            return false;
        }
        loaded ??= Loaded(name);
        if (loaded == null || depth >= 16)
        {
            // Not loaded: none of its types exist yet. When one depending on BaseLib loads, the others are judged again.
            complete = false;
            return false;
        }
        bool depends = false;
        foreach (AssemblyName reference in loaded.GetReferencedAssemblies())
        {
            if (reference.Name == null)
            {
                continue;
            }
            if (DependsOnBaseLib(reference.Name, null, depth + 1, out bool referenceComplete))
            {
                depends = true;
                complete = true;
                break;
            }
            complete &= referenceComplete;
        }
        if (complete)
        {
            _dependsOnBaseLib[name] = depends;
        }
        else
        {
            _incompleteAt[name] = Loads;
        }
        return depends;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Loads, string[] Mods)> _definingMods = new();

    /// <summary>
    /// For the back-off notes (<see cref="PatchSafety.Note"/>): the mods whose assemblies define a concrete type deriving from
    /// that BaseLib type. Reads each assembly that depends on BaseLib (what <see cref="AnyDefined"/> reads), so only called
    /// from the preflight, once <see cref="NoneDefined"/> said some exist; kept until an assembly loads.
    /// </summary>
    public static string[] DefiningMods(string baseTypeName)
    {
        int loads = Loads;
        if (_definingMods.TryGetValue(baseTypeName, out var known) && known.Loads == loads)
        {
            return known.Mods;
        }
        string[] mods = PatchSafety.ModNamesOf(AppDomain.CurrentDomain.GetAssemblies().Where(a => MayDefine(a) && Defines(a, baseTypeName)));
        _definingMods[baseTypeName] = (loads, mods);
        return mods;
    }

    /// <summary>
    /// Records (or clears) a note of <paramref name="patch"/> when a loaded mod defines a concrete type deriving from that
    /// BaseLib type: without BaseLib, or without that type in it, nothing is looked at (a missing type would make
    /// <see cref="AnyDefined"/> scan every assembly). For the preflight only.
    /// </summary>
    /// <param name="whole">The note keeps all of that patch vanilla (<see cref="PatchSafety.Note"/>).</param>
    public static void NoteIfDefined(string patch, string key, string baseTypeName, string text, bool whole = false)
    {
        Assembly? baseLib = Loaded("BaseLib");
        if (baseLib == null || baseLib.GetType(baseTypeName, false) == null || NoneDefined(baseTypeName))
        {
            PatchSafety.ClearNote(patch, key);
            return;
        }
        PatchSafety.Note(patch, key, text, DefiningMods(baseTypeName), whole);
    }

    private static int _spireNotesCounted = -1;
    /// <summary>The loaded assemblies when the notes were worked out: a new one may bring a card implementing an interface key.</summary>
    private static long _spireNotesAssemblies = -1;
    private static (string[] Fields, string[] Mods, bool AllCards) _spireNotes = (Array.Empty<string>(), Array.Empty<string>(), false);

    /// <summary>
    /// The SavedSpireFields BaseLib has registered on cards or enchantments (their target is one of those model types, or a
    /// base of them): their names, the mods registering them (the mod's code behind the field's default value, else its
    /// target type's mod), and whether one targets every card. Read again when the count changes. Empty without BaseLib.
    /// </summary>
    public static (string[] Fields, string[] Mods, bool AllCards) SpireFieldsOnCards()
    {
        System.Collections.IList? fields = SpireFields();
        if (fields == null)
        {
            return (Array.Empty<string>(), Array.Empty<string>(), false);
        }
        lock (_spireLock)
        {
            long loaded = LoadedKey();
            if (fields.Count == _spireNotesCounted && loaded == _spireNotesAssemblies)
            {
                return _spireNotes;
            }
            if (loaded != _spireNotesAssemblies)
            {
                _interfaceOnCards.Clear();
            }
            var names = new System.Collections.Generic.List<string>();
            var assemblies = new System.Collections.Generic.List<Assembly?>();
            bool allCards = false;
            Assembly? baseLib = Loaded("BaseLib");
            foreach (object field in fields.Cast<object>().ToArray())
            {
                if (field?.GetType().GetProperty("TargetType")?.GetValue(field) is not Type target)
                {
                    continue;
                }
                bool all = target.IsAssignableFrom(typeof(MegaCrit.Sts2.Core.Models.CardModel));
                // An interface key (TKey only needs to be a class: BaseLib applies the field to every model implementing it, as
                // SpireFieldTargets does) may be on some cards or enchantments: counted, unless no card or enchantment can be one.
                bool some = typeof(MegaCrit.Sts2.Core.Models.CardModel).IsAssignableFrom(target) || typeof(MegaCrit.Sts2.Core.Models.EnchantmentModel).IsAssignableFrom(target)
                    || target.IsAssignableFrom(typeof(MegaCrit.Sts2.Core.Models.EnchantmentModel)) || MayBeOnCards(target);
                if (!all && !some)
                {
                    continue;
                }
                allCards |= all;
                names.Add(field.GetType().GetProperty("Name")?.GetValue(field) as string ?? target.Name);
                Assembly? from = PatchSafety.DelegateAssembly(Reflect.Field(field.GetType(), "_defaultVal")?.GetValue(field) as Delegate, baseLib);
                assemblies.Add(from != null && from != baseLib ? from : target.Assembly);
            }
            _spireNotes = (names.ToArray(), PatchSafety.ModNamesOf(assemblies), allCards);
            _spireNotesCounted = fields.Count;
            _spireNotesAssemblies = loaded;
            return _spireNotes;
        }
    }

    /// <summary>
    /// What the loaded code is, for the notes' cache: the assemblies loaded, and the types a dynamic assembly made so far (a mod
    /// emitting types at run time adds them to an assembly already counted). Display only; a dynamic assembly that can't list
    /// its types makes every call a new key (worked out again).
    /// </summary>
    private static long LoadedKey()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        long key = assemblies.Length;
        foreach (Assembly assembly in assemblies)
        {
            if (!assembly.IsDynamic)
            {
                continue;
            }
            try
            {
                key += (long)assembly.GetTypes().Length << 20;
            }
            catch
            {
                return System.Threading.Interlocked.Increment(ref _unknownKey) | long.MinValue;
            }
        }
        return key;
    }

    private static long _unknownKey;

    /// <summary>
    /// An interface key some loaded card or enchantment type implements (rule E, 2026-11-17): only then can BaseLib put that field
    /// on a card or enchantment (it applies a field to the models implementing its key). Before, any interface key counted, so
    /// CardAscension's ten <c>SavedSpireField&lt;IRunState, ...&gt;</c> gave three false notes (LimitlessReserialize,
    /// LimitlessRecalcSkip, SnapshotCardReuse). Worked out per interface, again when an assembly loads (SpireFieldsOnCards'
    /// cache); generic definitions, and types that can't be listed, count as implementing it (a type load error: conservative).
    /// Display only (the notes): the runtime's own test, <see cref="SpireFieldTargets"/>, is per type.
    /// </summary>
    private static bool MayBeOnCards(Type target) => target.IsInterface && _interfaceOnCards.GetOrAdd(target, AnyCardImplements);

#if BENCH
    /// <summary>The test tooling's view of <see cref="MayBeOnCards"/> (foreignpatch spiretarget).</summary>
    internal static bool MayBeOnCardsForTest(Type target) => MayBeOnCards(target);
#endif

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _interfaceOnCards = new();

    private static bool AnyCardImplements(Type target)
    {
        try
        {
            Type card = typeof(MegaCrit.Sts2.Core.Models.CardModel), enchantment = typeof(MegaCrit.Sts2.Core.Models.EnchantmentModel);
            Type? open = target.IsGenericType ? target.GetGenericTypeDefinition() : null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                    if (types.Any(t => t == null))
                    {
                        // Some types of this assembly can't be loaded: one of them could be a card implementing the key.
                        if (types.Any(t => t != null && (card.IsAssignableFrom(t) || enchantment.IsAssignableFrom(t))))
                        {
                            return true;
                        }
                    }
                }
                foreach (Type? type in types)
                {
                    if (type == null || type.IsInterface || !(card.IsAssignableFrom(type) || enchantment.IsAssignableFrom(type)
                        || (type.IsGenericTypeDefinition && (type.IsSubclassOf(card) || type.IsSubclassOf(enchantment)))))
                    {
                        continue;
                    }
                    if (target.IsAssignableFrom(type) || (open != null && type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == open)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return true; // Can't tell: counted.
        }
    }

    /// <summary>
    /// Records (or clears) the note of a patch whose shortcut skips cards and enchantments a SavedSpireField targets
    /// (<see cref="SpireFieldTargets"/>: BaseLib adds that field to their saved data). For the preflight only.
    /// </summary>
    public static void NoteSpireFields(string patch, string what)
    {
        (string[] fields, string[] mods, bool allCards) = SpireFieldsOnCards();
        if (fields.Length == 0)
        {
            PatchSafety.ClearNote(patch, "spire_fields");
            return;
        }
        PatchSafety.Note(patch, "spire_fields",
            $"saved fields other mods add to {(allCards ? "every card" : "some cards or enchantments")} (BaseLib SavedSpireField: {string.Join(", ", fields.Take(4))}{(fields.Length > 4 ? ", ..." : "")}): {what}", mods);
    }

    /// <summary>
    /// For the back-off notes: the ids of BaseLib extended saves (<c>ExtendedSaveHandlers.RegisteredSaves</c> entries) other
    /// than <paramref name="exceptId"/>, and the mods registering them (the code of each save's getter). For the preflight only.
    /// </summary>
    public static (string[] Ids, string[] Mods) ExtendedSaves(System.Collections.IList saves, string? exceptId)
    {
        var ids = new System.Collections.Generic.List<string>();
        var assemblies = new System.Collections.Generic.List<Assembly?>();
        Assembly? baseLib = Loaded("BaseLib");
        foreach (object save in saves.Cast<object>().ToArray())
        {
            if (save == null)
            {
                continue;
            }
            string id = Reflect.Property(save.GetType(), "Id")?.GetValue(save) as string ?? "?";
            if (id == exceptId)
            {
                continue;
            }
            ids.Add(id);
            var wrapper = Reflect.Property(save.GetType(), "Getter")?.GetValue(save) as Delegate;
            assemblies.Add(PatchSafety.DelegateAssembly(wrapper, baseLib));
        }
        return (ids.Distinct().ToArray(), PatchSafety.ModNamesOf(assemblies));
    }

    // --- Rule B (2026-11-18): bundled copies of BaseLib's SavedSpireFieldPatch ("twins") ---

    /// <summary>
    /// BaseLib 3.4.8's <c>SavedSpireFieldPatch.PostfixFromInternal</c>, <c>PostfixFillInternal</c> and <c>GetFieldsForModel</c> (with
    /// its lambda), as <see cref="OwnNormalizedIl"/> describes them (BaseLib's own types named without namespace): stored, since a
    /// mod bundling a copy may run without BaseLib. Acheron's copy describes alike (checked with tools/fingerprint's own mode).
    /// </summary>
    private const string TwinFromInternalFingerprint = "9DD9F273573E3F25";
    private const string TwinFillInternalFingerprint = "1441D774E03D0220";
    private const string TwinFieldsForModelFingerprint = "5D37A68AE0E65B5D";
    /// <summary>The game's <c>SavedProperties()</c> constructor (empty; v0.107.1 to v0.113.1): what a twin's FromInternal postfix allocates.</summary>
    private const string SavedPropertiesConstructorFingerprint = "928FE0813C43F346";

    private sealed class Twin
    {
        public Twin(MethodInfo postfix, bool fromInternal, Func<System.Collections.IList?> fields, MethodBase[] code, Type[] initialized)
        {
            Postfix = postfix;
            FromInternal = fromInternal;
            Fields = fields;
            Code = code;
            Initialized = initialized;
        }

        public readonly MethodInfo Postfix;
        public readonly bool FromInternal;
        /// <summary>The copy's own <c>RegisteredFields</c> list, read at each use.</summary>
        public readonly Func<System.Collections.IList?> Fields;
        /// <summary>The postfix, its GetFieldsForModel, the lambda and every method they call: none may be patched.</summary>
        public readonly MethodBase[] Code;
        /// <summary>The classes whose initializers were judged pure: what each relies on must be unpatched (asked per epoch).</summary>
        public readonly Type[] Initialized;
    }

    private sealed class TwinSet
    {
        public TwinSet(int epoch, long at, Twin[] twins)
        {
            Epoch = epoch;
            At = at;
            Twins = twins;
            From = twins.Where(t => t.FromInternal).Select(t => t.Postfix).ToArray();
            Fill = twins.Where(t => !t.FromInternal).Select(t => t.Postfix).ToArray();
        }

        public readonly int Epoch;
        public readonly long At;
        public readonly Twin[] Twins;
        public readonly MethodInfo[] From, Fill;
    }

    private static volatile TwinSet _twins = new(int.MinValue, long.MinValue / 2, Array.Empty<Twin>());
    private static readonly object _twinLock = new();
    /// <summary>Per postfix method: the static part of the verdict (its code, its class), or null when it isn't a twin.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeMethodHandle, Twin?> _twinShapes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _twinsLogged = new();
    /// <summary>One <see cref="WithTwins"/> answer: a class, so the three arrays are read together (one reference read).</summary>
    private sealed record WithTwinsAnswer(MethodInfo[] Base, MethodInfo[] Twins, MethodInfo[] Both);

    /// <summary>The last answers (any thread: the closure worker and the main thread; a reference is written and read atomically).</summary>
    private static volatile WithTwinsAnswer? _withFrom, _withFill;

    /// <summary>
    /// The twins found now: other mods' postfixes on <c>SavedProperties.FromInternal</c> / <c>FillInternal</c> whose code is BaseLib's
    /// (<see cref="JudgeTwin"/>), each on that method as a postfix only, none of their code patched. Worked out again at each
    /// Harmony change (<see cref="HarmonyWatch.Epoch"/>), every 2 s without HarmonyWatch. Never throws (none then).
    /// </summary>
    private static TwinSet CurrentTwins()
    {
        TwinSet set = _twins;
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        if (HarmonyWatch.Applied ? set.Epoch == epoch : now - set.At < 2000)
        {
            return set;
        }
        lock (_twinLock)
        {
            set = _twins;
            if (HarmonyWatch.Applied ? set.Epoch == epoch : now - set.At < 2000)
            {
                return set;
            }
            var found = new System.Collections.Generic.List<Twin>();
            try
            {
                foreach (bool fromInternal in new[] { true, false })
                {
                    MethodBase? original = Reflect.Method(typeof(MegaCrit.Sts2.Core.Saves.Runs.SavedProperties), fromInternal ? "FromInternal" : "FillInternal");
                    if (original == null || HarmonyLib.Harmony.GetPatchInfo(original) is not { } info)
                    {
                        continue;
                    }
                    foreach (HarmonyLib.Patch patch in info.Postfixes)
                    {
                        if (PatchGuard.IsOurs(patch.owner) || PatchSafety.PatchMethodOf(patch) is not { } method
                            || method.DeclaringType?.Assembly.GetName().Name == "BaseLib")
                        {
                            continue; // BaseLib's own postfixes are judged by their real fingerprints (RelicStateReuse, LimitlessReserialize).
                        }
                        Twin? twin = _twinShapes.GetOrAdd(method.MethodHandle, _ => JudgeTwin(method, fromInternal));
                        // Postfix only (the allowed lists match a patch method whatever its kind), and none of its code patched now.
                        if (twin == null || twin.FromInternal != fromInternal
                            || info.Prefixes.Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.InnerPrefixes).Concat(info.InnerPostfixes)
                                .Any(p => PatchSafety.PatchMethodOf(p) is { } other && other.MethodHandle == method.MethodHandle)
                            || twin.Code.Any(code => HarmonyLib.Harmony.GetPatchInfo(code) is { } patched
                                && (patched.Prefixes.Count + patched.Postfixes.Count + patched.Transpilers.Count + patched.Finalizers.Count
                                    + patched.InnerPrefixes.Count + patched.InnerPostfixes.Count) > 0)
                            || twin.Initialized.Any(type => PatchSafety.PatchedInitializerReliance(type) != null))
                        {
                            continue;
                        }
                        found.Add(twin);
                        if (_twinsLogged.TryAdd($"{patch.owner} {method.DeclaringType?.FullName} {fromInternal}", 0))
                        {
                            MegaCrit.Sts2.Core.Logging.Log.Info($"[{ModEntry.Id}] {patch.owner}'s bundled copy of BaseLib's saved-field patch on SavedProperties."
                                + $"{(fromInternal ? "FromInternal" : "FillInternal")} ({method.DeclaringType?.FullName}): read as BaseLib's while it holds no field: kept");
                        }
                    }
                }
            }
            catch
            {
                found.Clear(); // Can't tell: no twin, every copy counts as another mod's patch (as before rule B).
            }
            set = new TwinSet(epoch, now, found.ToArray());
            _twins = set;
            return set;
        }
    }

    /// <summary>
    /// A twin, or null: a static void non-generic postfix of a non-generic class outside BaseLib, no HarmonyArgument, whose code
    /// (<see cref="OwnNormalizedIl"/>, signature included) is BaseLib's for that original; its class declares a static
    /// <c>GetFieldsForModel(object)</c> with BaseLib's code (its lambda included) and a static readonly <c>RegisteredFields</c>
    /// list (the one that code reads: the descriptions name it, and every member they name under the class's name is its own); every class of its own assembly that code uses has no initializer,
    /// or a pure beforefieldinit one (<see cref="PatchSafety.InitializerFree"/>: run at another time with this mod, it must not
    /// matter; the pure part judged here, what it relies on asked at each recheck: <see cref="Twin.Initialized"/>); the game's <c>SavedProperties</c> constructor it may call has the known (empty) code. Faulted: null.
    /// </summary>
    private static Twin? JudgeTwin(MethodInfo postfix, bool fromInternal)
    {
        try
        {
            Type? owner = postfix.DeclaringType;
            if (owner == null || owner.IsGenericType || !postfix.IsStatic || postfix.IsGenericMethod || postfix.ReturnType != typeof(void))
            {
                return null;
            }
            static bool Renames(System.Collections.Generic.IEnumerable<CustomAttributeData> attributes) => attributes.Any(a => a.AttributeType.Name == "HarmonyArgument");
            if (Renames(postfix.GetCustomAttributesData()) || postfix.GetParameters().Any(p => Renames(p.GetCustomAttributesData())))
            {
                return null;
            }
            for (Type? t = owner; t != null; t = t.BaseType)
            {
                if (Renames(t.GetCustomAttributesData()))
                {
                    return null;
                }
            }
            var used = new System.Collections.Generic.List<MemberInfo>();
            if (!Hash(OwnNormalizedIl.Describe(postfix, used.Add)).Equals(fromInternal ? TwinFromInternalFingerprint : TwinFillInternalFingerprint, StringComparison.Ordinal))
            {
                return null;
            }
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo? fieldsFor = owner.GetMethod("GetFieldsForModel", statics, null, new[] { typeof(object) }, null);
            if (fieldsFor == null || !Hash(OwnNormalizedIl.Describe(fieldsFor, used.Add)).Equals(TwinFieldsForModelFingerprint, StringComparison.Ordinal))
            {
                return null;
            }
            FieldInfo? list = owner.GetField("RegisteredFields", statics);
            if (list == null || !list.IsInitOnly || !typeof(System.Collections.IList).IsAssignableFrom(list.FieldType))
            {
                return null;
            }
            Assembly own = owner.Assembly;
            // The descriptions name the own assembly's types without their namespace: a member of another class of the owner's name
            // (another namespace) describes as the owner's would. Every one used must be the owner's (or of a class nested in it): the
            // GetFieldsForModel judged above and the RegisteredFields list read below are then the ones the code uses.
            string ownerName = OwnNormalizedIl.TypeName(owner, own);
            foreach (MemberInfo member in used)
            {
                for (Type? type = member as Type ?? member.DeclaringType; type != null; type = type.IsNested ? type.DeclaringType : null)
                {
                    if (type != owner && type.Assembly == own && OwnNormalizedIl.TypeName(type, own) == ownerName)
                    {
                        return null;
                    }
                }
            }
            var code = new System.Collections.Generic.List<MethodBase> { postfix, fieldsFor };
            var initialized = new System.Collections.Generic.List<Type> { owner };
            foreach (MemberInfo member in used)
            {
                Type? type = member as Type ?? member.DeclaringType;
                if (type != null && type.Assembly == own)
                {
                    Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                    if (!PatchSafety.InitializerPure(definition))
                    {
                        return null;
                    }
                    initialized.Add(definition);
                }
                if (member is MethodBase method)
                {
                    if (method.DeclaringType == typeof(MegaCrit.Sts2.Core.Saves.Runs.SavedProperties)
                        && !(method.IsConstructor && method.GetParameters().Length == 0
                            && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), SavedPropertiesConstructorFingerprint)))
                    {
                        return null;
                    }
                    code.Add(method);
                }
            }
            if (!PatchSafety.InitializerPure(owner))
            {
                return null;
            }
            // The descriptions follow the generated methods but not constructors: the lambda's closure class (GetFieldsForModel's
            // DisplayClass) must have only object's constructor in its own.
            foreach (ConstructorInfo constructor in used.OfType<ConstructorInfo>().Where(c => c.DeclaringType?.Assembly == own))
            {
                if (!TrivialConstructor(constructor))
                {
                    return null;
                }
            }
            Func<System.Collections.IList?> fields = System.Linq.Expressions.Expression.Lambda<Func<System.Collections.IList?>>(
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(null, list), typeof(System.Collections.IList))).Compile();
            return new Twin(postfix, fromInternal, fields, code.Distinct().ToArray(), initialized.Distinct().ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>An instance constructor of no parameter whose code is only <c>ldarg.0; call object::.ctor(); ret</c> (nops aside).</summary>
    private static bool TrivialConstructor(ConstructorInfo constructor)
    {
        if (constructor.IsStatic || constructor.GetParameters().Length != 0)
        {
            return false;
        }
        var code = MethodFingerprint.Instructions(constructor, strict: true).Where(i => i.Op != System.Reflection.Emit.OpCodes.Nop).ToList();
        return code.Count == 3 && code[0].Op == System.Reflection.Emit.OpCodes.Ldarg_0
            && code[1].Op == System.Reflection.Emit.OpCodes.Call && code[1].Operand is ConstructorInfo { DeclaringType: var baseType } baseConstructor
            && baseType == typeof(object) && baseConstructor.GetParameters().Length == 0
            && code[2].Op == System.Reflection.Emit.OpCodes.Ret;
    }

    private static string Hash(string description) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(description)))[..16];

    /// <summary>
    /// The twins on <c>FromInternal</c> (or <c>FillInternal</c>) a check may let stand now: every twin while none holds a field;
    /// once one does, only those whose own list is still empty (read now, not from a cached answer). Main thread or any.
    /// </summary>
    internal static MethodInfo[] TwinPostfixes(bool fromInternal)
    {
        TwinSet set = CurrentTwins();
        MethodInfo[] all = fromInternal ? set.From : set.Fill;
        if (all.Length == 0 || !TwinHoldsField(set))
        {
            return all;
        }
        return set.Twins.Where(t => t.FromInternal == fromInternal && Empty(t)).Select(t => t.Postfix).ToArray();
    }

    /// <summary>
    /// <paramref name="allowed"/> and the twins <see cref="TwinPostfixes"/> lets stand on that method: the same array while neither
    /// changes (the patch checks' caches are keyed on the allowed set).
    /// </summary>
    internal static MethodInfo[] WithTwins(MethodInfo[] allowed, bool fromInternal)
    {
        MethodInfo[] twins = TwinPostfixes(fromInternal);
        if (twins.Length == 0)
        {
            return allowed;
        }
        WithTwinsAnswer? known = fromInternal ? _withFrom : _withFill;
        if (known != null && ReferenceEquals(known.Base, allowed) && ReferenceEquals(known.Twins, twins))
        {
            return known.Both;
        }
        MethodInfo[] both = allowed.Concat(twins).ToArray();
        if (fromInternal)
        {
            _withFrom = new WithTwinsAnswer(allowed, twins, both);
        }
        else
        {
            _withFill = new WithTwinsAnswer(allowed, twins, both);
        }
        return both;
    }

    /// <summary>Whether a twin's own list holds a field now (read at each call; unreadable counts as holding one).</summary>
    internal static bool TwinHoldsField() => TwinHoldsField(CurrentTwins());

    private static bool TwinHoldsField(TwinSet set)
    {
        foreach (Twin twin in set.Twins)
        {
            if (!Empty(twin))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Empty(Twin twin)
    {
        try
        {
            return twin.Fields() is { Count: 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The fields the twins hold now (each distinct list once; an unreadable one counts as many).</summary>
    private static int TwinFieldCount()
    {
        TwinSet set = CurrentTwins();
        if (set.Twins.Length == 0)
        {
            return 0;
        }
        int count = 0;
        var seen = new System.Collections.Generic.HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (Twin twin in set.Twins)
        {
            try
            {
                if (twin.Fields() is { } fields)
                {
                    count += seen.Add(fields) ? fields.Count : 0;
                    continue;
                }
            }
            catch
            {
                // Counted below.
            }
            count += 1 << 20;
        }
        return count;
    }

#if BENCH
    /// <summary>The test tooling's view: the twins found now (their postfixes) and whether one holds a field.</summary>
    internal static (MethodInfo[] From, MethodInfo[] Fill, bool HoldsField) TwinsForTest()
    {
        TwinSet set = CurrentTwins();
        return (set.From, set.Fill, TwinHoldsField(set));
    }
#endif

    public static bool Defines(Assembly assembly, string baseTypeName)
    {
        try
        {
            Type? baseType = Reflect.TypeByName(baseTypeName);
            if (baseType == null)
            {
                return true;
            }
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray()!;
            }
            return types.Any(t => !t.IsAbstract && !t.IsInterface && baseType.IsAssignableFrom(t));
        }
        catch
        {
            return true; // Can't tell: don't skip.
        }
    }
}
