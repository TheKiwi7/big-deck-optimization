using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

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
/// added (<c>AddEnchantment</c>, the only code adding to the list). Off with <c>limitless_saves</c> or a changed hook.
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

    /// <summary>Whether MultiEnchantments may be dropped now.</summary>
    internal static bool Active => Applied && _enabled && LimitlessReserialize.Enabled && !PatchedByOthers();

    private static MethodBase[] _watched = Array.Empty<MethodBase>();
    private static long _patchesCheckedAt = long.MinValue;
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
    private static readonly HashSet<string> _forwarded = new(Forwarders.Select(f => f.Method));
    private static Func<object, List<CardModel>>? _inner;
    private static MethodBase? _addEnchantment;

    public static void Apply(Harmony harmony)
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == LimitlessReserialize.LimitlessAssembly))
        {
            throw new PatchSkippedException("Limitless not loaded (applies if it loads later)");
        }
        Type multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        var watched = new List<MethodBase>();
        foreach ((string method, string fingerprint, bool isAsync) in Forwarders)
        {
            MethodInfo target = AccessTools.Method(multi, method) ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, method);
            MethodBase body = isAsync ? AccessTools.AsyncMoveNext(target) ?? throw new MissingMethodException(method + " state machine") : target;
            PatchSafety.RequireFingerprint(body, fingerprint);
            watched.Add(target);
            watched.Add(body);
        }
        watched.Add(AccessTools.Method(multi, "SetSerializableCards", new[] { typeof(List<CardModel>) })
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "SetSerializableCards"));
        _watched = watched.Distinct().ToArray();
        MethodInfo add = AccessTools.Method(multi, "AddEnchantment") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "AddEnchantment");
        PatchSafety.RequireFingerprint(add, AddEnchantmentFingerprint);
        MethodInfo getter = AccessTools.PropertyGetter(multi, "EnchantmentsOnCards") ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "get_EnchantmentsOnCards");
        ParameterExpression model = Expression.Parameter(typeof(object));
        _inner = Expression.Lambda<Func<object, List<CardModel>>>(Expression.Call(Expression.Convert(model, multi), getter), model).Compile();
        _addEnchantment = add;
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

    /// <summary>Records the types of a MultiEnchantment's inner enchantments.</summary>
    internal static void Note(AbstractModel multi)
    {
        List<CardModel> inner = _inner!(multi);
        for (int i = 0; i < inner.Count; i++)
        {
            EnchantmentModel? enchantment = inner[i].Enchantment;
            if (enchantment == null)
            {
                continue;
            }
            Type type = enchantment.GetType();
            lock (_known)
            {
                if (_known.Add(type))
                {
                    _version++;
                }
            }
        }
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
