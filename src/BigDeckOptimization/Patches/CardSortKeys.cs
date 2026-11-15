using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Game v0.113 made <c>CardModel.CompareTo</c> compare far more than the id and upgrade level: enchantment and affliction ids,
/// the resolved energy cost (<c>EnergyCost.GetResolved()</c>: in combat, the cost hook walking every listener twice), the
/// replay count, then each dynamic variable in key order (<c>DynamicVars.Keys.Order(...)</c>, allocated and sorted at each
/// call). <c>ListExtensions.StableShuffle</c> sorts a copy of its list with that comparison before shuffling, on every draw
/// pile reshuffle and in Stone Cracker's combat start (856 ms of a 959 ms combat start at ~1,900 cards, 6.5 s with 4 players).
///
/// Inside a <c>StableShuffle</c> over cards (its known callers, transpiled to <see cref="StableShuffleInScope"/>: a Harmony
/// patch on the generic method itself would replace its code shared by every reference type, which breaks the other lists),
/// <c>CompareTo</c> answers with the game's logic, every value read live but two computed once per card for the sort: the
/// resolved cost and the ordered key list. The sort itself is the game's: the same comparisons give the same order.
///
/// Exact because nothing changes while the sort runs: a sort is opened only when every card's type keeps
/// <c>CardModel.CompareTo</c>, and each combat the cards are in has only cost listeners known to just read
/// (<see cref="SnapshotCardReuse.PureHooks"/>; a card in no combat pile runs no cost hook). Otherwise, and when another mod
/// patches what this reproduces or skips, the game's comparison runs. One answer in <see cref="VerifyEvery"/> is compared
/// with the game's; a difference turns this off for the session.
/// </summary>
public static class CardSortKeys
{
    public const string Name = "CardSortKeys";

    /// <summary><c>CardModel.CompareTo</c>, game v0.113.0 and v0.113.1: the code mirrored.</summary>
    private const string CompareToFingerprint = "F6388632B42CF646";
    /// <summary>v0.107.1 and v0.111.0: id and upgrade level only, cheap: nothing to do.</summary>
    private const string CheapCompareToFingerprint = "2CC3990F98A5EF89";
    /// <summary>The rest, the same on v0.107.1, v0.111.0, v0.113.0 and v0.113.1 (ModelId.Compare: v0.113 only).</summary>
    private const string BaseCompareToFingerprint = "BB460DAC433857B6";
    private const string ModelIdCompareFingerprint = "9044DFF12964F53B";
    private const string StableShuffleFingerprint = "68E6CDB3F3082D3C";
    private const string GetResolvedFingerprint = "5FAA75114FDAFC70";
    private const string GetWithModifiersFingerprint = "60FE75B459B24CFC";
    private const string LocalModifyFingerprint = "E11E84E9412802B6";
    private const string ModifyEnergyCostFingerprint = "081395225799F6BD";
    private const string CostHookDefaultFingerprint = "DB289946DCE7F74D";
    private const string EnergyCostGetterFingerprint = "D6AF1AAE1A2FEE0D";
    private const string DynamicVarsGetterFingerprint = "19C079D25E68C079";
    private const string KeysGetterFingerprint = "E7FAD3B477754438";

    /// <summary>
    /// The types whose code shuffles a <c>List&lt;CardModel&gt;</c> with <c>StableShuffle</c> (the 15 methods of v0.113.1; PunchOfJoy and
    /// EcstasyPower shuffle a <c>List&lt;Sadness&gt;</c> of hand cards). A new caller just stays the game's.
    /// </summary>
    private static readonly string[] CallerTypes =
    {
        "MegaCrit.Sts2.Core.Commands.CardPileCmd",
        "MegaCrit.Sts2.Core.Entities.Merchant.BlitzMerchantRandomCardUpgradesEntry",
        "MegaCrit.Sts2.Core.Models.Cards.BeatDown",
        "MegaCrit.Sts2.Core.Models.Cards.Catastrophe",
        "MegaCrit.Sts2.Core.Models.Cards.SeekerStrike",
        "MegaCrit.Sts2.Core.Models.Cards.TheFool",
        "MegaCrit.Sts2.Core.Models.Cards.Uproar",
        "MegaCrit.Sts2.Core.Models.Events.BattlewornDummy",
        "MegaCrit.Sts2.Core.Models.Relics.FragrantMushroom",
        "MegaCrit.Sts2.Core.Models.Relics.PowerCell",
        "MegaCrit.Sts2.Core.Models.Relics.SandCastle",
        "MegaCrit.Sts2.Core.Models.Relics.StoneCracker",
        "MegaCrit.Sts2.Core.Models.Relics.WarHammer",
        "MegaCrit.Sts2.Core.Models.Relics.WarPaint",
        "MegaCrit.Sts2.Core.Models.Relics.Whetstone",
    };

    private static readonly string[] CostHooks = { "TryModifyEnergyCostInCombat", "TryModifyEnergyCostInCombatLate" };

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }

    /// <summary>This game's comparison is the cheap one: not applied, and nothing missing.</summary>
    public static bool NotNeeded { get; private set; }

    /// <summary>Sorts answered here, comparisons answered, answers compared with the game's and those that differed.</summary>
    public static long Sorts { get; private set; }
    public static long Answered { get; private set; }
    public static long Checks { get; private set; }
    public static long Mismatches { get; private set; }

    /// <summary>Sorts left to the game, and the last reason (bench, log).</summary>
    public static long Refused { get; private set; }
    public static string? LastRefusal { get; private set; }

#if BENCH
    internal static int VerifyEvery { get; set; } = 16;
    /// <summary>Positive control (<c>bdperf sortkeys wrong</c>): answers reversed.</summary>
    internal static bool WrongForTest { get; set; }
#else
    internal static int VerifyEvery { get; set; } = 1024;
#endif

    private static int _untilCheck = 1;
    private static bool _verifying;
    private static int _mainThread = -1;

    private static Func<ModelId?, ModelId?, int>? _modelIdCompare;
    private static MethodInfo? _inScope;
    /// <summary>What a sort answered here reproduces or skips calling: another mod's patch on one leaves the sort to the game.</summary>
    private static MethodBase?[] _watched = Array.Empty<MethodBase?>();

    /// <summary>Per card of the sort: its cost and ordered keys once read.</summary>
    private struct Entry
    {
        public bool HasCost;
        public int Cost;
        public string[]? Keys;
    }

    private sealed class Scope(Dictionary<CardModel, Entry> cards, Scope? previous)
    {
        public readonly Dictionary<CardModel, Entry> Cards = cards;
        public readonly Scope? Previous = previous;
        public long Answered;
    }

    /// <summary>The sort under way (main thread only).</summary>
    private static Scope? _scope;

    public static void Apply(Harmony harmony)
    {
        MethodInfo compareTo = AccessTools.DeclaredMethod(typeof(CardModel), nameof(CardModel.CompareTo), new[] { typeof(AbstractModel) })
            ?? throw new MissingMethodException(nameof(CardModel), nameof(CardModel.CompareTo));
        if (MethodFingerprint.Of(compareTo) == CheapCompareToFingerprint)
        {
            NotNeeded = true;
            throw new PatchSkippedException("not needed: CardModel.CompareTo compares only the id and upgrade level on this game version");
        }
        PatchSafety.RequireFingerprint(compareTo, CompareToFingerprint);
        MethodInfo baseCompareTo = Require(AccessTools.DeclaredMethod(typeof(AbstractModel), nameof(AbstractModel.CompareTo), new[] { typeof(AbstractModel) }), BaseCompareToFingerprint, "AbstractModel.CompareTo");
        MethodInfo modelIdCompare = Require(AccessTools.DeclaredMethod(typeof(ModelId), "Compare", new[] { typeof(ModelId), typeof(ModelId) }), ModelIdCompareFingerprint, "ModelId.Compare");
        MethodInfo shuffle = Require(AccessTools.DeclaredMethod(typeof(ListExtensions), nameof(ListExtensions.StableShuffle)), StableShuffleFingerprint, "ListExtensions.StableShuffle");
        MethodInfo getResolved = Require(AccessTools.DeclaredMethod(typeof(CardEnergyCost), nameof(CardEnergyCost.GetResolved)), GetResolvedFingerprint, "CardEnergyCost.GetResolved");
        MethodInfo withModifiers = Require(AccessTools.DeclaredMethod(typeof(CardEnergyCost), nameof(CardEnergyCost.GetWithModifiers)), GetWithModifiersFingerprint, "CardEnergyCost.GetWithModifiers");
        MethodInfo localModify = Require(AccessTools.DeclaredMethod(typeof(LocalCostModifier), nameof(LocalCostModifier.Modify)), LocalModifyFingerprint, "LocalCostModifier.Modify");
        MethodInfo costHook = Require(AccessTools.DeclaredMethod(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat)), ModifyEnergyCostFingerprint, "Hook.ModifyEnergyCostInCombat");
        MethodInfo costDefault = Require(AccessTools.DeclaredMethod(typeof(AbstractModel), CostHooks[0]), CostHookDefaultFingerprint, "AbstractModel." + CostHooks[0]);
        MethodInfo costLateDefault = Require(AccessTools.DeclaredMethod(typeof(AbstractModel), CostHooks[1]), CostHookDefaultFingerprint, "AbstractModel." + CostHooks[1]);
        MethodInfo energyCost = Require(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.EnergyCost)), EnergyCostGetterFingerprint, "CardModel.EnergyCost");
        MethodInfo dynamicVars = Require(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.DynamicVars)), DynamicVarsGetterFingerprint, "CardModel.DynamicVars");
        MethodInfo keys = Require(AccessTools.PropertyGetter(typeof(DynamicVarSet), nameof(DynamicVarSet.Keys)), KeysGetterFingerprint, "DynamicVarSet.Keys");
        _modelIdCompare = (Func<ModelId?, ModelId?, int>)Delegate.CreateDelegate(typeof(Func<ModelId?, ModelId?, int>), modelIdCompare);
        _inScope = AccessTools.Method(typeof(CardSortKeys), nameof(StableShuffleInScope));
        _watched = new MethodBase?[]
        {
            compareTo, baseCompareTo, modelIdCompare, shuffle, shuffle.MakeGenericMethod(typeof(CardModel)), getResolved, withModifiers,
            localModify, costHook, costDefault, costLateDefault, energyCost, dynamicVars, keys,
        };

        // Callers found before anything is patched: none found leaves the whole patch out.
        List<MethodBase> callers = new();
        foreach (string name in CallerTypes)
        {
            Type? type = typeof(CardModel).Assembly.GetType(name);
            if (type == null)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {name} not found: its shuffles stay the game's");
                continue;
            }
            int before = callers.Count;
            callers.AddRange(Calling(type));
            if (callers.Count == before)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {type.Name} no longer shuffles cards with StableShuffle");
            }
        }
        if (callers.Count == 0)
        {
            throw new PatchSkippedException("no known caller shuffles cards with StableShuffle");
        }
        _mainThread = Environment.CurrentManagedThreadId;
        PatchGuard.Patch(harmony, compareTo, prefix: new HarmonyMethod(typeof(CardSortKeys), nameof(ComparePrefix)));
        foreach (MethodBase caller in callers)
        {
            PatchGuard.Patch(harmony, caller, transpiler: new HarmonyMethod(typeof(CardSortKeys), nameof(CallerTranspiler)) { priority = Priority.Last });
        }
        Applied = true;
    }

    private static MethodInfo Require(MethodInfo? method, string fingerprint, string what)
    {
        if (method == null)
        {
            throw new MissingMethodException(what);
        }
        PatchSafety.RequireFingerprint(method, fingerprint);
        return method;
    }

    /// <summary>The methods of the type (state machines and lambdas included) calling <c>StableShuffle&lt;CardModel&gt;</c>.</summary>
    private static IEnumerable<MethodBase> Calling(Type type)
    {
        List<MethodBase> found = new();
        foreach (MethodBase method in MethodFingerprint.DeclaredMethods(type))
        {
            if (method.ContainsGenericParameters || method.GetMethodBody() == null)
            {
                continue;
            }
            try
            {
                if (MethodFingerprint.UsedMembers(method).Any(member => member is MethodInfo called && IsCardShuffle(called)))
                {
                    found.Add(method);
                }
            }
            catch (Exception e) when (e is MissingMemberException or TypeLoadException or BadImageFormatException or ArgumentException)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {type.Name}.{method.Name} not read ({e.Message}): its shuffles stay the game's");
            }
        }
        return found;
    }

    private static bool IsCardShuffle(MethodInfo method) =>
        method.IsGenericMethod && method.Name == nameof(ListExtensions.StableShuffle) && method.DeclaringType == typeof(ListExtensions)
        && method.GetGenericArguments() is [var t] && t == typeof(CardModel);

    /// <summary>Each <c>StableShuffle&lt;CardModel&gt;</c> call goes through <see cref="StableShuffleInScope"/> (same stack, same result).</summary>
    private static IEnumerable<CodeInstruction> CallerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo called && IsCardShuffle(called))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = _inScope;
            }
            yield return instruction;
        }
    }

    /// <summary>The game's <c>list.StableShuffle(rng)</c>, its sort answered here when that's exact. Its own exceptions reach the game.</summary>
    public static List<CardModel> StableShuffleInScope(List<CardModel> list, Rng rng)
    {
        Scope? scope = Open(list);
        try
        {
            return list.StableShuffle(rng);
        }
        finally
        {
            if (scope != null)
            {
                Close(scope);
            }
        }
    }

#if BENCH
    /// <summary>Bench: <c>list.Sort()</c> as <c>StableShuffle</c> runs it, inside a scope when one would open.</summary>
    internal static bool SortInScope(List<CardModel> list)
    {
        Scope? scope = Open(list);
        try
        {
            list.Sort();
        }
        finally
        {
            if (scope != null)
            {
                Close(scope);
            }
        }
        return scope != null;
    }

    /// <summary>Bench: the counters as they were, so a bench's sorts don't count as the game's (effects).</summary>
    internal static void RestoreCounters(long sorts, long answered)
    {
        Sorts = sorts;
        Answered = answered;
    }
#endif

    /// <summary>A scope for this list when its sort can be answered exactly; null otherwise (the game's comparison).</summary>
    private static Scope? Open(List<CardModel> list)
    {
        // Inside ListenerCache.RunVanilla the result is a reference others are compared with: the game's own.
        if (!Enabled || ListenerCache.IsSuspended || Environment.CurrentManagedThreadId != _mainThread || list == null)
        {
            return null;
        }
        try
        {
            foreach (MethodBase? method in _watched)
            {
                if (PatchSafety.IsPatchedByOthers(method))
                {
                    return Refuse($"another mod patches {method?.DeclaringType?.Name}.{method?.Name}");
                }
            }
            Dictionary<CardModel, Entry> cards = new(list.Count, ReferenceEqualityComparer.Instance);
            List<ICombatState>? combats = null;
            ICombatState? last = null;
            foreach (CardModel card in list)
            {
                if (card == null || !cards.TryAdd(card, default))
                {
                    continue;
                }
                if (!Plain(card.GetType()))
                {
                    return Refuse($"{card.GetType().Name} compares itself");
                }
                // Its cost runs the cost hook only in a combat pile (GetWithModifiers: CombatState != null), that combat's listeners.
                ICombatState? combat = card.CombatState;
                if (combat != null && !ReferenceEquals(combat, last))
                {
                    last = combat;
                    combats ??= new List<ICombatState>();
                    if (!combats.Any(c => ReferenceEquals(c, combat)))
                    {
                        combats.Add(combat);
                    }
                }
            }
            if (combats != null)
            {
                // The game's hook walks IterateCombatHookListeners: known code (a subset of IterateHookListeners), not patched.
                if (HookFilter.CombatHookWalkPatched())
                {
                    return Refuse("the combat hook walk changed or is patched");
                }
                foreach (ICombatState combat in combats)
                {
                    if (ImpureCostListener(combat) is { } impure)
                    {
                        return Refuse($"{impure} may change a cost");
                    }
                }
            }
            Scope scope = new(cards, _scope);
            _scope = scope;
            return scope;
        }
        catch (Exception e)
        {
            Disable(e);
            return null;
        }
    }

    private static Scope? Refuse(string why)
    {
        Refused++;
        LastRefusal = why;
        return null;
    }

    private static void Close(Scope scope)
    {
        _scope = scope.Previous;
        if (scope.Answered > 0)
        {
            Sorts++;
        }
    }

    // --- What a sort may rely on ---

    /// <summary>Per card type: it compares through <c>CardModel.CompareTo</c> (no override, no comparison interface of its own).</summary>
    private static readonly Dictionary<Type, bool> _plain = new();

    private static bool Plain(Type type)
    {
        if (!_plain.TryGetValue(type, out bool plain))
        {
            try
            {
                MethodInfo? compareTo = type.GetMethod(nameof(CardModel.CompareTo), BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(AbstractModel) }, null);
                plain = compareTo?.DeclaringType == typeof(CardModel)
                    && !type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IComparable<>) && i != typeof(IComparable<AbstractModel>));
            }
            catch (AmbiguousMatchException)
            {
                plain = false; // A CompareTo hiding the game's.
            }
            _plain[type] = plain;
        }
        return plain;
    }

    /// <summary>Per listener type: its cost hook overrides, all known to only read (<see cref="SnapshotCardReuse.PureHooks"/>); null otherwise.</summary>
    private static readonly Dictionary<Type, MethodInfo[]?> _costOverrides = new();

    /// <summary>The first listener of this combat whose cost hooks may do more than read; null when there is none.</summary>
    private static string? ImpureCostListener(ICombatState combat)
    {
        Type? lastType = null;
        MethodInfo[]? overrides = null;
        foreach (AbstractModel model in combat.IterateHookListeners())
        {
            if (model == null)
            {
                continue;
            }
            Type type = model.GetType();
            if (type != lastType)
            {
                lastType = type;
                overrides = CostOverrides(type);
                if (overrides == null)
                {
                    return type.Name;
                }
                foreach (MethodInfo method in overrides)
                {
                    if (PatchSafety.IsPatchedByOthers(method))
                    {
                        return type.Name + " (patched)";
                    }
                }
            }
        }
        return null;
    }

    private static MethodInfo[]? CostOverrides(Type type)
    {
        if (_costOverrides.TryGetValue(type, out MethodInfo[]? known))
        {
            return known;
        }
        List<MethodInfo>? overrides = new();
        try
        {
            foreach (string name in CostHooks)
            {
                MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
                if (method == null || method.DeclaringType == typeof(AbstractModel))
                {
                    continue;
                }
                int entry = Array.FindIndex(SnapshotCardReuse.PureHooks, h => h.Type == method.DeclaringType?.FullName && h.Method == name);
                if (entry < 0 || method.DeclaringType != type
                    || !PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), SnapshotCardReuse.PureHooks[entry].Fingerprint))
                {
                    overrides = null;
                    break;
                }
                overrides.Add(method);
            }
        }
        catch (Exception e) when (e is AmbiguousMatchException or MissingMemberException or TypeLoadException)
        {
            overrides = null;
        }
        return _costOverrides[type] = overrides?.ToArray();
    }

    // --- The comparison ---

    private static bool ComparePrefix(CardModel __instance, AbstractModel? __0, ref int __result)
    {
        Scope? scope = _scope;
        if (scope == null || _verifying || !Enabled)
        {
            return true;
        }
        try
        {
            CardModel? other = __0 as CardModel;
            if (__0 != null && other == null)
            {
                return true; // The game's cast throws.
            }
            ref Entry mine = ref CollectionsMarshal.GetValueRefOrNullRef(scope.Cards, __instance);
            if (Unsafe.IsNullRef(ref mine) || (other != null && !scope.Cards.ContainsKey(other)))
            {
                return true; // Not a card of this sort: nothing was judged about it.
            }
            int result = Compare(scope, __instance, ref mine, other);
#if BENCH
            if (WrongForTest && result != 0)
            {
                result = -result;
            }
#endif
            scope.Answered++;
            Answered++;
            if (--_untilCheck <= 0)
            {
                _untilCheck = VerifyEvery;
                result = Verify(__instance, __0, result);
            }
            __result = result;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    /// <summary>v0.113's <c>CardModel.CompareTo</c> (with <c>AbstractModel.CompareTo</c>), the same reads in the same order.</summary>
    private static int Compare(Scope scope, CardModel a, ref Entry entry, CardModel? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }
        if (b == null)
        {
            return 1;
        }
        int result = a.Id.CompareTo(b.Id);
        if (result != 0)
        {
            return result;
        }
        result = a.CurrentUpgradeLevel.CompareTo(b.CurrentUpgradeLevel);
        if (result != 0)
        {
            return result;
        }
        result = _modelIdCompare!(a.Enchantment?.Id, b.Enchantment?.Id);
        if (result != 0)
        {
            return result;
        }
        result = _modelIdCompare(a.Affliction?.Id, b.Affliction?.Id);
        if (result != 0)
        {
            return result;
        }
        int costA = Cost(a, ref entry);
        result = costA.CompareTo(Cost(b, ref CollectionsMarshal.GetValueRefOrNullRef(scope.Cards, b)));
        if (result != 0)
        {
            return result;
        }
        result = a.BaseReplayCount.CompareTo(b.BaseReplayCount);
        if (result != 0)
        {
            return result;
        }
        foreach (string key in entry.Keys ??= OrderedKeys(a))
        {
            if (b.DynamicVars.TryGetValue(key, out DynamicVar? value))
            {
                result = a.DynamicVars[key].BaseValue.CompareTo(value.BaseValue);
                if (result != 0)
                {
                    return result;
                }
            }
        }
        return 0;
    }

    private static int Cost(CardModel card, ref Entry entry)
    {
        if (!entry.HasCost)
        {
            entry.Cost = card.EnergyCost.GetResolved();
            entry.HasCost = true;
        }
        return entry.Cost;
    }

    /// <summary><c>DynamicVars.Keys.Order(StringComparer.Ordinal)</c>: dictionary keys are distinct, so any sort gives that order.</summary>
    private static string[] OrderedKeys(CardModel card)
    {
        string[] keys = card.DynamicVars.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }

    /// <summary>The game's answer for this pair (nothing in the sort can change what it reads: see <see cref="Open"/>).</summary>
    private static int Verify(CardModel card, AbstractModel? other, int answered)
    {
        int game;
        _verifying = true;
        try
        {
            game = card.CompareTo(other);
        }
        finally
        {
            _verifying = false;
        }
        Checks++;
        if (game != answered)
        {
            Mismatches++;
            Disable(new InvalidOperationException($"{card.Id} vs {(other as CardModel)?.Id}: answered {answered}, the game {game}"));
            return game;
        }
        return answered;
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, the game compares its cards: {e}");
    }
}
