using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Limitless's <c>MultiEnchantment</c> overrides five hooks (AfterCardPlayed, AfterCardDrawn, BeforeFlush,
/// AfterAutoPrePlayPhaseEntered, ModifyShuffleOrder) just to forward them to its inner enchantments, then refresh its saved
/// list. <see cref="HookSkip"/> keeps every listener whose type overrides a hook, so each of these hooks called every
/// stacked enchantment of every card (combat piles and deck): 20,000+ async calls and player choice contexts per hook at
/// 10,000 cards, a third of everything allocated while cards are played in co-op, all doing nothing.
///
/// A MultiEnchantment whose inner enchantments all keep the default of a hook does what a listener keeping that default
/// does: the inner calls are no-ops, and rebuilding the saved list gives what it holds since they changed nothing. So <see cref="FastListeners"/>' filters drop it, instance by
/// instance; and when no inner enchantment of any MultiEnchantment seen so far overrides the hook, the type itself counts
/// as not reacting, so moving such a card rebuilds nothing.
///
/// Inner enchantments are recorded when a MultiEnchantment first shows up in a pile walk or card move, and when one is
/// added (<c>AddEnchantment</c>, the only code adding to the list: checked at load, <see cref="KnownInnerListWriters"/>).
/// Off with <c>limitless_saves</c>, a changed hook, LimitlessReserialize not applied (its fingerprint of
/// <c>SetSerializableCards</c> is what "rebuilding gives what it holds" rests on), or another mod patching what a drop skips
/// (the forwarders, the inner list's getter and the build of a clone's inner cards, the saved list's setter), or the game's
/// code of that build changed (<see cref="LimitlessCloneMemo.GameChainKnown"/>, the getter's own code too). One forwarded
/// hook call in <see cref="SampleEvery"/> checks a dropped one against what its forwarder would have done.
/// Known limit: an enchantment of a type no card has yet, reacting to a hook, added to a stacked enchantment during that
/// very hook's walk, isn't called by that walk. Only another mod can do that: the game enchants with a reacting type
/// (Goopy, Glam, Swift, Sown, Slither, PerfectFit...) only outside combat, at events, relics' AfterObtained and card
/// rewards (checked on v0.113.1); its one in-combat enchant (BladeOfInk's Inky) reacts to none of the five hooks.
/// Writes to the inner list by reflection can't be scanned either (<see cref="ScanAssembly"/>).
/// </summary>
public static class LimitlessHookSkip
{
    public const string Name = "LimitlessHookSkip";

    /// <summary>Limitless 1.1.7 to 1.2.1 code (same): each forwarding hook (state machine, or the method for the sync one) and the only code adding inner enchantments.</summary>
    private static readonly (string Method, string Fingerprint, bool Async)[] Forwarders =
    {
        ("AfterCardPlayed", "02C27949CA8A21E5", true),
        ("AfterAutoPrePlayPhaseEntered", "96D8F5AB94F692A8", true),
        ("AfterCardDrawn", "2FAB2A820853A212", true),
        ("BeforeFlush", "E516493805A1E3CB", true),
        ("ModifyShuffleOrder", "5D6BF67BCC576562", false),
    };
    private const string AddEnchantmentFingerprint = "CBB669F3C802665F";

    private static bool _enabled = true;
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
    public static bool Applied { get; private set; }
#if BENCH
    /// <summary>Stacked enchantments, or hooks for the whole type, judged as doing nothing (the effects check).</summary>
    public static long Dropped { get; internal set; }
#endif

    /// <summary>The MultiEnchantment type, null until applied.</summary>
    internal static Type? Multi { get; private set; }

    /// <summary>
    /// Whether MultiEnchantments may be dropped now. Their inner types are noted only while this is on: turned on (or a
    /// back-off lifted) during a combat, the stacked enchantments already in its piles are unknown, so it only arms
    /// outside a combat (the next combat's piles are filled, and noted, while on). Also off while LimitlessReserialize
    /// backs off: a dropped one skips the list it would rebuild.
    /// </summary>
    internal static bool Active
    {
        get
        {
            // The game's clone chain too: a dropped clone's inner cards are never built, as the game would on its first call.
            // And no assembly loaded since the load check may change the inner list (a type no note would see).
            if (!Applied || !_enabled || !LimitlessReserialize.Applied || !LimitlessReserialize.Enabled || !LimitlessCloneMemo.GameChainKnown()
                || !InnerListWritersKnown || PatchedByOthers() || LimitlessReserialize.PatchedByOthers())
            {
                _armed = false;
                return false;
            }
            if (!_armed && !CombatUnderWay())
            {
                _armed = true;
            }
            return _armed;
        }
    }

    /// <summary>
    /// A combat set up, running or not yet reset (its piles may still be walked, e.g. the last play's hooks after the win):
    /// <c>IsInProgress</c> is only set once the first turn starts, after <c>SetUpCombat</c> filled the draw pile with clones,
    /// so arming on it alone missed those. <c>IsStarting</c> and the combat state are set before the piles are filled, the
    /// state cleared when the room is left (every version).
    /// </summary>
    private static bool CombatUnderWay() =>
        MegaCrit.Sts2.Core.Combat.CombatManager.Instance is { } combat && (combat.IsInProgress || combat.IsStarting || combat.DebugOnlyGetState() != null);

    private static bool _armed;

    private static MethodBase[] _watched = Array.Empty<MethodBase>();
    /// <summary>Limitless's EnchantmentStorageCard, the type of every inner card AddEnchantment makes.</summary>
    private static Type? _storageCard;
    private static long _patchesCheckedAt = long.MinValue / 2; // So `now - at` can't overflow: the first check runs.
    private static bool _patchedByOthers;

    /// <summary>The storage card's overrides on the clone's way (preflight source), none when unknown.</summary>
    private static IEnumerable<MethodBase?> StorageOverrides() =>
        _storageCard != null && LimitlessCloneMemo.OnTheWay(_storageCard) is { } storage ? storage : Array.Empty<MethodInfo>();

    /// <summary>
    /// Another mod's patch on what a dropped MultiEnchantment would have run: its hooks, the list it rebuilds. Asked per
    /// stacked enchantment: rechecked every 2 s, as <see cref="PatchSafety.IsPatchedByOthers(MethodBase?, string)"/> caches it.
    /// </summary>
    private static bool PatchedByOthers()
    {
        long now = Environment.TickCount64;
        if (now - _patchesCheckedAt >= 2000)
        {
            bool patched = false;
            foreach (MethodBase method in _watched)
            {
                // No tolerance: forwarded hooks and defaults whose calls a dropped enchantment skips.
                patched |= PatchSafety.IsPatchedByOthers(method);
            }
            // A clone's inner cards are built by the getter's CardModel.FromSerializable on its first forwarded call: a drop
            // skips that build, the same chain LimitlessCloneMemo skips (its known patches allowed there too), down to the
            // overrides of the inner card's type and of every inner enchantment type seen (types with a mod's code there are
            // never dropped: AnyKnown, AnyInner).
            // The chain's card methods with patches gated on the card's type count when the storage card is of a gate type.
            patched = patched || _storageCard == null || LimitlessCloneMemo.ChainPatchedFor(_storageCard)
                || LimitlessCloneMemo.OnTheWay(_storageCard) is not { } storage || LimitlessCloneMemo.AnyPatched(storage);
            foreach (Type type in _knownRead)
            {
                patched = patched || (LimitlessCloneMemo.OnTheWay(type) is { } overrides && LimitlessCloneMemo.AnyPatched(overrides));
            }
            _patchedByOthers = patched;
            _patchesCheckedAt = now;
        }
        return _patchedByOthers;
    }

    /// <summary>Changes when filters must judge MultiEnchantments again; -1 when none may be dropped.</summary>
    internal static long Stamp => Active ? _version : -1;

    private static long _version;
    private static readonly HashSet<Type> _known = new();

    /// <summary>A copy of <see cref="_known"/>, never changed once published (replaced on each new type): read without the lock.</summary>
    private static volatile HashSet<Type> _knownRead = new();
    /// <summary>The hooks whose forwarding has the known code.</summary>
    private static HashSet<string> _forwarded = new();
    private static Func<object, List<CardModel>>? _inner;
    private static MethodBase? _addEnchantment;

    /// <summary>
    /// Limitless's getter (1.1.7 to 1.2.1, same code) builds the inner cards from the saved list on first use (<c>CardModel.FromSerializable</c> each), and
    /// every clone starts without them: noting a combat's clones built them all (~170 ms at a 20,000-card combat start). What a saved
    /// entry gives is fixed by its card and enchantment ids (the game's deserializers, fingerprinted): once a (card, enchantment) pair
    /// was seen built, its type is known, and a list holding only such pairs adds nothing.
    /// </summary>
    private const string InnerGetterFingerprint = "0AA01E688AC72C3C";
    private const string CardFromSerializableFingerprint = "663431D80AC6CCDF";
    private const string EnchantmentFromSerializableFingerprint = "F59AC9E4E33F6805";
    private static AccessTools.FieldRef<object, List<CardModel>?>? _built;
    private static AccessTools.FieldRef<object, List<SerializableCard>?>? _saved;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ModelId Card, ModelId Enchantment), Type> _seenPairs = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<SerializableCard>, object> _pairsRecorded = new();

    /// <summary>Notes answered from the saved list, without building the inner cards.</summary>
    public static long NotesFromSaved { get; private set; }

    public static void Apply(Harmony harmony)
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == LimitlessReserialize.LimitlessAssembly))
        {
            throw new PatchSkippedException("Limitless not loaded (applies if it loads later)");
        }
        Type multi = Reflect.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        var watched = new List<MethodBase>();
        var forwarded = new HashSet<string>();
        foreach ((string method, string fingerprint, bool isAsync) in Forwarders)
        {
            // A forwarder that changed only keeps that hook's calls: the others are still known to do nothing.
            try
            {
                MethodInfo target = Reflect.Method(multi, method) ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, method);
                MethodBase body = isAsync ? Reflect.AsyncMoveNext(target) ?? throw new MissingMethodException(method + " state machine") : target;
                PatchSafety.RequireFingerprint(body, fingerprint);
                forwarded.Add(method);
                watched.Add(target);
                watched.Add(body);
            }
            catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {method} left vanilla: {e.Message}");
            }
        }
        if (forwarded.Count == 0)
        {
            throw new PatchSkippedException("none of the forwarding hooks has the known code");
        }
        _forwarded = forwarded;
        watched.Add(Reflect.Method(multi, "SetSerializableCards", new[] { typeof(List<CardModel>) })
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "SetSerializableCards"));
        MethodInfo add = Reflect.Method(multi, "AddEnchantment") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "AddEnchantment");
        PatchSafety.RequireFingerprint(add, AddEnchantmentFingerprint);
        MethodInfo getter = Reflect.PropertyGetter(multi, "EnchantmentsOnCards") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "get_EnchantmentsOnCards");
        // Each forwarder reads the inner cards through it (building them on a clone's first call): a dropped one skips it,
        // so its code must be the known one for any drop (not only for the saved-pairs shortcut below).
        PatchSafety.RequireFingerprint(getter, InnerGetterFingerprint);
        watched.Add(getter);
        _watched = watched.Distinct().ToArray();
        // Inner types are noted where AddEnchantment adds one: no other code may change the inner list.
        RequireKnownInnerListWriters(multi);
        _storageCard = Reflect.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.EnchantmentStorageCard")
            ?? throw new PatchSkippedException("Limitless has no EnchantmentStorageCard");
        ParameterExpression model = Expression.Parameter(typeof(object));
        _inner = Expression.Lambda<Func<object, List<CardModel>>>(Expression.Call(Expression.Convert(model, multi), getter), model).Compile();
        _addEnchantment = add;
        try
        {
            PatchSafety.RequireFingerprint(Reflect.Method(typeof(CardModel), nameof(CardModel.FromSerializable)), CardFromSerializableFingerprint);
            PatchSafety.RequireFingerprint(Reflect.Method(typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable)), EnchantmentFromSerializableFingerprint);
            _built = Reflect.FieldRefAccess<object, List<CardModel>?>(Reflect.Field(multi, "_enchantmentsOnCards")
                ?? throw new MissingFieldException(LimitlessReserialize.MultiEnchantmentType, "_enchantmentsOnCards"));
            _saved = Reflect.FieldRefAccess<object, List<SerializableCard>?>(Reflect.Field(multi, "<SerializableEnchantmentsOnCards>k__BackingField")
                ?? throw new MissingFieldException(LimitlessReserialize.MultiEnchantmentType, "SerializableEnchantmentsOnCards"));
        }
        catch (Exception e)
        {
            _built = null;
            _saved = null;
            Log.Info($"[{ModEntry.Id}] {Name}: inner enchantment types read from the built cards only: {e.Message}");
        }
        // Checked (and a change logged) now rather than at the first walk; a changed chain only keeps this from arming.
        LimitlessCloneMemo.GameChainKnown();
        PatchGuard.Patch(harmony, add, prefix: new HarmonyMethod(typeof(LimitlessHookSkip), nameof(BeforeAdd)),
            finalizer: new HarmonyMethod(typeof(LimitlessHookSkip), nameof(AfterAdd)));
        Multi = multi;
        Applied = true;
        // What PatchedByOthers asks about (display only): its own methods, and the clone chain and storage card overrides
        // under LimitlessCloneMemo's name (whose code asks them, so its runtime check records them there), and under this
        // one's too: with LimitlessCloneMemo skipped or faulted, bdopt why and the menu leave its name out, while this part
        // still backs off on them.
        PatchSafety.Watch(Name, _watched);
        foreach (string patch in new[] { LimitlessCloneMemo.Name, Name })
        {
            LimitlessCloneMemo.WatchChain(patch);
            PatchSafety.WatchAll(patch, StorageOverrides);
        }
        try
        {
            ApplySamples(harmony, multi);
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: dropped stacked enchantments not sampled: {e.Message}");
        }
    }

    private static void BeforeAdd() => FastListeners.MultiChanging();

    private static void AfterAdd(Exception? __exception, EnchantmentModel __instance)
    {
        FastListeners.MultiChanged();
        try
        {
            Note(__instance);
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    /// <summary>
    /// Records the types of a MultiEnchantment's inner enchantments. Runs for every card with one added to a pile (20,000 at a
    /// combat start): types already known are found in a published copy, without the lock.
    /// </summary>
    internal static void Note(AbstractModel multi)
    {
        List<SerializableCard>? saved = _saved?.Invoke(multi);
        if (saved != null && _built!(multi) == null && AllSeen(saved))
        {
            NotesFromSaved++;
            return;
        }
        List<CardModel> inner = _inner!(multi);
        HashSet<Type> known = _knownRead;
        Type? last = null;
        for (int i = 0; i < inner.Count; i++)
        {
            EnchantmentModel? enchantment = inner[i].Enchantment;
            if (enchantment == null)
            {
                continue;
            }
            Type type = enchantment.GetType();
            if (ReferenceEquals(type, last) || known.Contains(type))
            {
                last = type;
                continue;
            }
            lock (_known)
            {
                if (_known.Add(type))
                {
                    _version++;
                    _knownRead = new HashSet<Type>(_known);
                }
            }
            known = _knownRead;
            last = type;
        }
        // Once their types are recorded above: the pairs built from this saved list (same count, same ids at each place), once
        // per list (this runs at every move of a card holding one; a pair's type never changes, so a pair left out only means
        // a later note builds the cards, as before).
        saved = _saved?.Invoke(multi);
        if (saved != null && saved.Count == inner.Count && !_pairsRecorded.TryGetValue(saved, out _))
        {
            _pairsRecorded.AddOrUpdate(saved, _pairsRecorded);
            for (int i = 0; i < inner.Count; i++)
            {
                // Only pairs whose card type has known code on the way: a saved list of others is built to be judged.
                if (inner[i].Enchantment is { } built && saved[i] is { Id: { } cardId, Enchantment.Id: { } enchantmentId }
                    && cardId == inner[i].Id && enchantmentId == built.Id && !Unknown(inner[i].GetType()))
                {
                    _seenPairs.TryAdd((cardId, enchantmentId), built.GetType());
                }
            }
        }
    }

    /// <summary>Whether every saved entry with an enchantment is a (card, enchantment) pair already seen built, its type recorded.</summary>
    private static bool AllSeen(List<SerializableCard> saved)
    {
        for (int i = 0; i < saved.Count; i++)
        {
            SerializableCard card = saved[i];
            if (card.Enchantment == null)
            {
                continue;
            }
            if (card.Id is not { } cardId || card.Enchantment.Id is not { } enchantmentId || !_seenPairs.ContainsKey((cardId, enchantmentId)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether Multi only forwards this hook, so it needs an inner enchantment overriding it to react.</summary>
    internal static bool Forwards(MethodInfo hook) => _forwarded.Contains(hook.Name);

    /// <summary>
    /// A card or enchantment type with another mod's code on the way of <c>CardModel.FromSerializable</c>
    /// (<see cref="LimitlessCloneMemo.OnTheWay"/>): building a clone's inner cards may then not give what is saved, so a
    /// stacked enchantment holding one counts as reacting (never dropped, its forwarder runs as in the game).
    /// </summary>
    private static bool Unknown(Type type) => LimitlessCloneMemo.OnTheWay(type) == null;

    /// <summary>Whether a recorded inner enchantment type answers yes (or has unknown code on the way).</summary>
    internal static bool AnyKnown(Func<Type, bool> overrides)
    {
        lock (_known)
        {
            foreach (Type type in _known)
            {
                if (overrides(type) || Unknown(type))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether one of this MultiEnchantment's inner enchantment types answers yes.</summary>
    internal static bool AnyInner(AbstractModel multi, Func<Type, bool> overrides)
    {
        // Not built yet, and every pair seen: the types are known without building anything. A combat's clones start unbuilt, and this
        // runs on all of them at its first walk (~17 MB of 83 at 10,000 cards); nothing else needs those cards until one is played.
        List<SerializableCard>? saved = _saved?.Invoke(multi);
        if (saved != null && _built!(multi) == null && AllSeen(saved))
        {
            for (int i = 0; i < saved.Count; i++)
            {
                if (saved[i] is { Id: { } cardId, Enchantment.Id: { } enchantmentId }
                    && (overrides(_seenPairs[(cardId, enchantmentId)]) || Unknown(_seenPairs[(cardId, enchantmentId)])))
                {
                    return true;
                }
            }
            return false;
        }
        List<CardModel> inner = _inner!(multi);
        for (int i = 0; i < inner.Count; i++)
        {
            EnchantmentModel? enchantment = inner[i].Enchantment;
            if ((enchantment != null && (overrides(enchantment.GetType()) || Unknown(enchantment.GetType()))) || Unknown(inner[i].GetType()))
            {
                return true;
            }
        }
        return false;
    }

    public static void Disable(Exception e)
    {
        _enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, stacked enchantments are asked again: {e}");
    }

    /// <summary>
    /// Limitless 1.2.1's code that takes a MultiEnchantment's inner card list from <c>EnchantmentsOnCards</c> and may
    /// change it (a mutating call on such a list, or the list passed on), with its fingerprint: AddEnchantment adds to it,
    /// the others only hand it to SetSerializableCards. Any other such code (a Limitless update, another mod) could add an
    /// inner enchantment no note sees, which no fingerprint above would show: this patch and the recalculation passes'
    /// skip (<see cref="LimitlessRecalcSkip"/>) then stay off. Names as <c>Type+Nested::Method</c>, compiler numbers as #.
    /// </summary>
    private static readonly Dictionary<string, string> KnownInnerListWriters = new()
    {
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::AddEnchantment"] = "CBB669F3C802665F",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::RecalculateValues"] = "1180845F3EE40FDB",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::OnEnchant"] = "7EF043E764C1E325",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::EnchantBlockAdditive"] = "5CD8988D0F3C9B61",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::EnchantBlockMultiplicative"] = "69D591456C5D9E84",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::EnchantDamageAdditive"] = "C85C1CB0A7DB62ED",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::EnchantDamageMultiplicative"] = "1E0643BC0C1BA286",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::EnchantPlayCount"] = "C57A6A9A40AA81B6",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::ModifyShuffleOrder"] = "5D6BF67BCC576562",
        ["Limitless.LimitlessCode.UncappedEnchantments.EnchantmentPatches.GoopyPatches::AddAmountToGoopyFromMulti"] = "2A4D2677E41300D6",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment+<AfterAutoPrePlayPhaseEntered>d__#::MoveNext"] = "96D8F5AB94F692A8",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment+<AfterCardDrawn>d__#::MoveNext"] = "2FAB2A820853A212",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment+<AfterCardPlayed>d__#::MoveNext"] = "02C27949CA8A21E5",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment+<BeforeFlush>d__#::MoveNext"] = "E516493805A1E3CB",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment+<OnPlay>d__#::MoveNext"] = "2C52914BFD559352",
    };

    /// <summary>
    /// The inner list's other ways in, with no call to its getter: the saved list a clone's getter builds the inner cards
    /// from (its accessors, SetSerializableCards) and the fields of both lists. Limitless 1.2.1's code using any of them
    /// besides <see cref="KnownInnerListWriters"/> (which hand the getter's list to SetSerializableCards), with its
    /// fingerprint: the getter, the saved list's accessors and setter, the clone and the constructor. Any other use (a
    /// saved list replaced on a clone not built yet, a publicized mod's field write) counts as a writer.
    /// </summary>
    private static readonly Dictionary<string, string> KnownSavedListUsers = new()
    {
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::get_EnchantmentsOnCards"] = "0AA01E688AC72C3C",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::get_SerializableEnchantmentsOnCards"] = "C13992F090F53A53",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::set_SerializableEnchantmentsOnCards"] = "C70F0868040EF2A5",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::SetSerializableCards"] = "916B119C60C89D96",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::DeepCloneFields"] = "B0598AD4D39BD71C",
        ["Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment::.ctor"] = "4E1BC01ABB0A5309",
    };

    private static readonly string[] SavedListMethods = { "SetSerializableCards", "get_SerializableEnchantmentsOnCards", "set_SerializableEnchantmentsOnCards" };
    private static readonly string[] InnerListFields = { "_enchantmentsOnCards", "<SerializableEnchantmentsOnCards>k__BackingField" };

    /// <summary>Whether a member a method uses is one of these (by handle: the same member resolved from any module).</summary>
    private static bool IsOneOf(MemberInfo used, MemberInfo[] members)
    {
        foreach (MemberInfo member in members)
        {
            if ((used is MethodBase a && member is MethodBase b && a.MethodHandle == b.MethodHandle)
                || (used is FieldInfo f && member is FieldInfo g && f.FieldHandle == g.FieldHandle))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Calls changing a list of cards: the <c>List&lt;T&gt;</c> ones of <see cref="MethodFingerprint.FieldUsers"/>' mutators.</summary>
    private static readonly HashSet<string> ListMutators = new()
    {
        "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "set_Item",
        "Sort", "Reverse", "CopyTo", "EnsureCapacity", "TrimExcess",
    };

    private static readonly System.Text.RegularExpressions.Regex _generatedNumber =
        new(@"(d__|DisplayClass|b__|9__|\||AnonymousType|<)\d+(_\d+)?(?=\W|$)");

    private static readonly object _writersLock = new();
    /// <summary>Null until checked, then "" (only the known writers) or why not.</summary>
    private static string? _writersCheck;

    /// <summary>
    /// Throws <see cref="PatchSkippedException"/> unless the only code that may change a MultiEnchantment's inner list is
    /// <see cref="KnownInnerListWriters"/> with its known code. Scans Limitless and the assemblies referencing it once
    /// (~10 ms): in Limitless's own module, only methods holding the getter's token are read in full.
    /// </summary>
    internal static void RequireKnownInnerListWriters(Type multi)
    {
        string check;
        lock (_writersLock)
        {
            check = _writersCheck ??= CheckInnerListWriters(multi);
        }
        if (check.Length > 0)
        {
            throw new PatchSkippedException(check);
        }
    }

    /// <summary>Limitless's MultiEnchantment, once the writers were checked: assemblies loading later are scanned against it.</summary>
    private static Type? _writersMulti;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Assembly> _lateAssemblies = new();
    private static int _lateQueued;
    /// <summary>Null while no assembly loaded after the check changes the inner list; else what does (for the session).</summary>
    private static volatile string? _lateWriters;

    /// <summary>
    /// False once an assembly loaded after the load check (a Limitless addon later in the load order: the game loads and
    /// initializes mods one at a time) may change a MultiEnchantment's inner list: this patch then stays inactive and the
    /// recalculation passes are no longer skipped. Scans what loaded since the last call (outside the load itself).
    /// </summary>
    internal static bool InnerListWritersKnown
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _lateQueued) != 0)
            {
                ScanLate();
            }
            return _lateWriters == null;
        }
    }

    private static void OnLateLoad(object? sender, AssemblyLoadEventArgs args)
    {
        // Runs inside another assembly's load: only queue it (never throw here).
        try
        {
            Assembly assembly = args.LoadedAssembly;
            string? limitless = _writersMulti?.Assembly.GetName().Name;
            if (!assembly.IsDynamic && (limitless == null || assembly.GetReferencedAssemblies().Any(r => r.Name == limitless)))
            {
                _lateAssemblies.Enqueue(assembly);
                System.Threading.Volatile.Write(ref _lateQueued, 1);
            }
        }
        catch
        {
            _lateWriters ??= "an assembly that couldn't be read loaded";
        }
    }

    private static void ScanLate()
    {
        lock (_writersLock)
        {
            System.Threading.Interlocked.Exchange(ref _lateQueued, 0);
            var unknown = new List<string>();
            while (_lateAssemblies.TryDequeue(out Assembly? assembly))
            {
                try
                {
                    if (_writersMulti != null && assembly != _writersMulti.Assembly)
                    {
                        ScanAssembly(assembly, _writersMulti, unknown);
                    }
                }
                catch (Exception e)
                {
                    unknown.Add($"{assembly.GetName().Name} ({e.GetType().Name})"); // Can't tell: count it.
                }
            }
            if (unknown.Count > 0 && _lateWriters == null)
            {
                _lateWriters = string.Join(", ", unknown);
                Log.Error($"[{ModEntry.Id}] {Name}: stacked enchantments' inner list may also be changed by {_lateWriters} (loaded after the check): "
                    + "stacked enchantments are asked again and recalculation passes always run, as the game does");
            }
        }
    }

    private static string CheckInnerListWriters(Type multi)
    {
        // Subscribed before the scan: an assembly loading meanwhile is scanned again later, never missed.
        _writersMulti = multi;
        AppDomain.CurrentDomain.AssemblyLoad += OnLateLoad;
        var unknown = new List<string>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            ScanAssembly(assembly, multi, unknown);
        }
        return unknown.Count == 0 ? "" : "stacked enchantments' inner list may also be changed by " + string.Join(", ", unknown) + " (Limitless update?)";
    }

    /// <summary>
    /// Adds to <paramref name="unknown"/> the assembly's methods that may change the inner list, other than the known ones:
    /// a mutating call on (or a pass of) the getter's list, or any use of the saved list or of either list's field
    /// (<see cref="KnownSavedListUsers"/>). Code reaching them by reflection (a name in a string) isn't seen.
    /// </summary>
    private static void ScanAssembly(Assembly assembly, Type multi, List<string> unknown)
    {
        MethodInfo getter = Reflect.PropertyGetter(multi, "EnchantmentsOnCards")
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "get_EnchantmentsOnCards");
        var mutable = new HashSet<Type> { getter.ReturnType, typeof(IList<CardModel>), typeof(ICollection<CardModel>) };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        // Every overload; one missing (a Limitless update) is simply not looked for, as before this check.
        MemberInfo[] listMembers = multi.GetMethods(all).Where(m => SavedListMethods.Contains(m.Name)).Cast<MemberInfo>()
            .Concat(multi.GetFields(all).Where(f => InnerListFields.Contains(f.Name)))
            .ToArray();
        // In Limitless's own module a use carries the member's definition token: code holding none of them uses none.
        byte[][] tokens = listMembers.Append(getter).Select(m => BitConverter.GetBytes(m.MetadataToken)).ToArray();
        string limitless = multi.Assembly.GetName().Name!;
        bool own = assembly == multi.Assembly;
        if (assembly.IsDynamic || (!own && !assembly.GetReferencedAssemblies().Any(r => r.Name == limitless)))
        {
            return;
        }
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types;
        }
        foreach (Type? type in types)
        {
            if (type == null)
            {
                continue;
            }
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                string name = type.FullName + "::" + method.Name;
                if (name.Contains('<'))
                {
                    name = _generatedNumber.Replace(name, "$1#");
                }
                try
                {
                    byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                    // A use from its own module carries one of the tokens: code without any can't use them.
                    if (il == null || (own && !tokens.Any(token => new ReadOnlySpan<byte>(il).IndexOf(new ReadOnlySpan<byte>(token)) >= 0)))
                    {
                        continue;
                    }
                    MemberInfo[] used = MethodFingerprint.UsedMembers(method).ToArray();
                    // The saved list or a list's field, used without the getter, changes what the next build gives.
                    bool listMember = used.Any(u => IsOneOf(u, listMembers));
                    if (!listMember && !used.Any(u => u is MethodInfo m && m.MethodHandle == getter.MethodHandle))
                    {
                        continue;
                    }
                    bool writer = listMember || used.Any(u => u is MethodInfo call
                        && ((call.DeclaringType != null && mutable.Contains(call.DeclaringType) && ListMutators.Contains(call.Name))
                            || call.GetParameters().Any(p => mutable.Contains(p.ParameterType))));
                    if (writer && !((KnownInnerListWriters.TryGetValue(name, out string? fingerprint) || KnownSavedListUsers.TryGetValue(name, out fingerprint))
                        && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint)))
                    {
                        unknown.Add(name);
                    }
                }
                catch (Exception e) when (own)
                {
                    // Unreadable code in Limitless itself that may hold the getter's token: can't tell, count it.
                    unknown.Add($"{name} ({e.GetType().Name})");
                }
                catch
                {
                    // Another mod's code this game version can't resolve: it can't run either.
                }
            }
        }
    }

    /// <summary>One forwarded hook call in this many samples a stacked enchantment of the combat piles.</summary>
    public const int SampleEvery = 16;
    /// <summary>Dropped stacked enchantments checked against what their forwarder would have done, and the differences found (0 expected).</summary>
    public static long Samples { get; private set; }
    public static long SampleMismatches { get; private set; }

    /// <summary>The <see cref="AbstractModel"/> hook each sampling prefix stands for (the base definition Multi overrides).</summary>
    private static MethodInfo?[] _sampledHooks = Array.Empty<MethodInfo?>();
    private static readonly string[] SampledHookNames = { "AfterCardPlayed", "AfterCardDrawn", "BeforeFlush", "AfterAutoPrePlayPhaseEntered" };
    private static int _sinceSample;
    private static int _sampleCursor;

    /// <summary>
    /// A prefix on the game's <c>Hook</c> method of each forwarded async hook (not ModifyShuffleOrder: it runs inside a
    /// shuffle, the piles being reordered): when it walks the combat, a dropped stacked enchantment would have run its
    /// forwarder, whose only effect then is SetSerializableCards. Sampled there: every inner type noted, and the list kept
    /// by a rebuild. A difference turns this patch off (the walks ask every stacked enchantment again).
    /// </summary>
    private static void ApplySamples(Harmony harmony, Type multi)
    {
        var hooks = new MethodInfo?[SampledHookNames.Length];
        for (int i = 0; i < SampledHookNames.Length; i++)
        {
            string hook = SampledHookNames[i];
            if (!_forwarded.Contains(hook))
            {
                continue;
            }
            hooks[i] = Reflect.Method(multi, hook)?.GetBaseDefinition();
            MethodInfo target = Reflect.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), hook)
                ?? throw new MissingMethodException("Hook", hook);
            PatchGuard.Patch(harmony, target, prefix: new HarmonyMethod(typeof(LimitlessHookSkip), "Sample" + i));
        }
        _sampledHooks = hooks;
    }

    private static void Sample0() => Sample(0);
    private static void Sample1() => Sample(1);
    private static void Sample2() => Sample(2);
    private static void Sample3() => Sample(3);

    private static void Sample(int hook)
    {
        if (++_sinceSample < SampleEvery)
        {
            return;
        }
        try
        {
            if (_built == null || hook >= _sampledHooks.Length || _sampledHooks[hook] is not { } baseHook || !LimitlessReserialize.CanCompareHere || !Active)
            {
                return;
            }
            _sinceSample = 0;
            if (NextBuiltMulti() is not { } multi)
            {
                return;
            }
            List<CardModel> inner = _built(multi)!;
            foreach (CardModel card in inner)
            {
                if ((card.Enchantment is { } enchantment && (OverridesHook(enchantment.GetType(), baseHook) || Unknown(enchantment.GetType())))
                    || Unknown(card.GetType()))
                {
                    return; // Kept by the walks for this hook: its forwarder runs as in the game.
                }
            }
            Samples++;
            HashSet<Type> known = _knownRead;
            string? why = null;
            foreach (CardModel card in inner)
            {
                if (card.Enchantment is { } enchantment && !known.Contains(enchantment.GetType()))
                {
                    why = $"inner {enchantment.Id} never noted";
                    break;
                }
            }
            if (why == null && !LimitlessReserialize.WouldKeep((EnchantmentModel)multi, inner))
            {
                why = "its forwarder would have rebuilt its saved list";
            }
            if (why != null)
            {
                SampleMismatches++;
                Disable(new InvalidOperationException($"a dropped stacked enchantment on {(multi as EnchantmentModel)?.Card?.Id} differs from the game's: {why}"));
            }
        }
        catch (Exception e)
        {
            Disable(e);
        }
    }

    /// <summary>The next stacked enchantment whose inner cards are built, in the combat's piles, from a cursor moving over them.</summary>
    private static AbstractModel? NextBuiltMulti()
    {
        if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.DebugOnlyGetState() is not { } state)
        {
            return null;
        }
        var piles = new List<CardPile>();
        int total = 0;
        foreach (MegaCrit.Sts2.Core.Entities.Players.Player player in state.Players)
        {
            if (player.PlayerCombatState is { } combat)
            {
                foreach (CardPile pile in combat.AllPiles)
                {
                    piles.Add(pile);
                    total += pile.Cards.Count;
                }
            }
        }
        if (total == 0)
        {
            return null;
        }
        // Looks at up to 64 cards from the cursor: a few in a sample, every card over a combat.
        for (int looked = 0; looked < Math.Min(64, total); looked++)
        {
            int at = (int)((uint)_sampleCursor++ % (uint)total);
            foreach (CardPile pile in piles)
            {
                IReadOnlyList<CardModel> cards = pile.Cards;
                if (at < cards.Count)
                {
                    if (cards[at].Enchantment is { } enchantment && ReferenceEquals(enchantment.GetType(), Multi) && _built!(enchantment) != null)
                    {
                        return enchantment;
                    }
                    break;
                }
                at -= cards.Count;
            }
        }
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, MethodInfo), bool> _overridesHook = new();

    /// <summary>As the filters judge it: the type's most-derived implementation of the hook isn't AbstractModel's.</summary>
    private static bool OverridesHook(Type type, MethodInfo baseHook) => _overridesHook.GetOrAdd((type, baseHook), static key =>
    {
        foreach (MethodInfo method in key.Item1.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name == key.Item2.Name && method.GetBaseDefinition() == key.Item2)
            {
                return method.DeclaringType != typeof(AbstractModel);
            }
        }
        return true;
    });
}
