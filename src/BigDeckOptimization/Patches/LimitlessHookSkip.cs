using System;
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
/// added (<c>AddEnchantment</c>, the only code adding to the list). Off with <c>limitless_saves</c>, a changed hook, or
/// LimitlessReserialize not applied (its fingerprint of <c>SetSerializableCards</c> is what "rebuilding gives what it holds"
/// rests on).
/// Known limit: an enchantment of a type no card has yet, reacting to a hook, added to a stacked enchantment during that
/// very hook's walk, isn't called by that walk.
/// </summary>
public static class LimitlessHookSkip
{
    public const string Name = "LimitlessHookSkip";

    /// <summary>Limitless 1.1.7 code: each forwarding hook (state machine, or the method for the sync one) and the only code adding inner enchantments.</summary>
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
            if (!Applied || !_enabled || !LimitlessReserialize.Applied || !LimitlessReserialize.Enabled || PatchedByOthers() || LimitlessReserialize.PatchedByOthers())
            {
                _armed = false;
                return false;
            }
            if (!_armed && !(MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress ?? false))
            {
                _armed = true;
            }
            return _armed;
        }
    }

    private static bool _armed;

    private static MethodBase[] _watched = Array.Empty<MethodBase>();
    private static long _patchesCheckedAt = long.MinValue / 2; // So `now - at` can't overflow: the first check runs.
    private static bool _patchedByOthers;

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
                patched |= PatchSafety.IsPatchedByOthers(method);
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
    /// Limitless 1.1.7's getter builds the inner cards from the saved list on first use (<c>CardModel.FromSerializable</c> each), and
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
        Type multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        var watched = new List<MethodBase>();
        var forwarded = new HashSet<string>();
        foreach ((string method, string fingerprint, bool isAsync) in Forwarders)
        {
            // A forwarder that changed only keeps that hook's calls: the others are still known to do nothing.
            try
            {
                MethodInfo target = AccessTools.Method(multi, method) ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, method);
                MethodBase body = isAsync ? AccessTools.AsyncMoveNext(target) ?? throw new MissingMethodException(method + " state machine") : target;
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
        watched.Add(AccessTools.Method(multi, "SetSerializableCards", new[] { typeof(List<CardModel>) })
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "SetSerializableCards"));
        _watched = watched.Distinct().ToArray();
        MethodInfo add = AccessTools.Method(multi, "AddEnchantment") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "AddEnchantment");
        PatchSafety.RequireFingerprint(add, AddEnchantmentFingerprint);
        MethodInfo getter = AccessTools.PropertyGetter(multi, "EnchantmentsOnCards") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "get_EnchantmentsOnCards");
        ParameterExpression model = Expression.Parameter(typeof(object));
        _inner = Expression.Lambda<Func<object, List<CardModel>>>(Expression.Call(Expression.Convert(model, multi), getter), model).Compile();
        _addEnchantment = add;
        try
        {
            PatchSafety.RequireFingerprint(getter, InnerGetterFingerprint);
            PatchSafety.RequireFingerprint(AccessTools.Method(typeof(CardModel), nameof(CardModel.FromSerializable)), CardFromSerializableFingerprint);
            PatchSafety.RequireFingerprint(AccessTools.Method(typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable)), EnchantmentFromSerializableFingerprint);
            _built = AccessTools.FieldRefAccess<object, List<CardModel>?>(AccessTools.Field(multi, "_enchantmentsOnCards")
                ?? throw new MissingFieldException(LimitlessReserialize.MultiEnchantmentType, "_enchantmentsOnCards"));
            _saved = AccessTools.FieldRefAccess<object, List<SerializableCard>?>(AccessTools.Field(multi, "<SerializableEnchantmentsOnCards>k__BackingField")
                ?? throw new MissingFieldException(LimitlessReserialize.MultiEnchantmentType, "SerializableEnchantmentsOnCards"));
        }
        catch (Exception e)
        {
            _built = null;
            _saved = null;
            Log.Info($"[{ModEntry.Id}] {Name}: inner enchantment types read from the built cards only: {e.Message}");
        }
        PatchGuard.Patch(harmony, add, prefix: new HarmonyMethod(typeof(LimitlessHookSkip), nameof(BeforeAdd)),
            finalizer: new HarmonyMethod(typeof(LimitlessHookSkip), nameof(AfterAdd)));
        Multi = multi;
        Applied = true;
    }

    private static void BeforeAdd() => FastListeners.MultiChanging();

    private static Exception? AfterAdd(Exception? __exception, EnchantmentModel __instance)
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
        return __exception;
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
                if (inner[i].Enchantment is { } built && saved[i] is { Id: { } cardId, Enchantment.Id: { } enchantmentId }
                    && cardId == inner[i].Id && enchantmentId == built.Id)
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

    /// <summary>Whether a recorded inner enchantment type answers yes.</summary>
    internal static bool AnyKnown(Func<Type, bool> overrides)
    {
        lock (_known)
        {
            foreach (Type type in _known)
            {
                if (overrides(type))
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
                if (saved[i] is { Id: { } cardId, Enchantment.Id: { } enchantmentId } && overrides(_seenPairs[(cardId, enchantmentId)]))
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
            if (enchantment != null && overrides(enchantment.GetType()))
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
}
