using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic patches of "another mod" for the generic rules on the Limitless parts (DESIGN "Re-verification of the leads
/// (2026-11-18)", rules A, B and L), installed through <c>foreignpatch &lt;shape&gt; &lt;Namespace.Type:Method&gt; [gate] [priority N]</c>
/// (Harmony ids <c>bdtest.foreign.&lt;shape&gt;[-gate]</c>); <c>foreignpatch limitless report &lt;label&gt;</c> prints their counts as
/// STATE lines (compared line for line between the on and off sides), <c>foreignpatch limitless remove</c> removes them.
/// <list type="bullet">
/// <item>Rule A, accepted: <c>lim-result-gated-postfix squeeze</c> on <c>CardModel:ToMutable</c>, a postfix doing nothing unless
/// <c>__result</c> is a Squeeze (Acts from the Past's shape on ToMutable), counting its calls.</item>
/// <item>Rule A, controls (must back off): <c>lim-enchantment-gated-postfix</c> on <c>AbstractModel:MutableClone</c> (gated on
/// an enchantment type: MutableClone also runs on the inner card's enchantment), <c>lim-cardmodel-gated-postfix</c> (gated on
/// <c>is CardModel</c>: every inner card is one), <c>lim-write-then-gated-postfix</c> (a count before the gate's branch).</item>
/// <item>Rule B, accepted: <c>lim-twin accepted</c> on <c>SavedProperties:FromInternal</c> / <c>:FillInternal</c>, a copy of
/// BaseLib's SavedSpireFieldPatch postfixes with BaseLib's IL (Acheron's shape), holding no field.</item>
/// <item>Rule B, controls (must back off): <c>lim-twin unfiltered</c> (GetFieldsForModel without its filter), <c>lim-twin
/// alwaysstores</c> (a null result stored), <c>lim-twin impure</c> (its class's initializer counts), <c>lim-twin-prefix accepted</c>
/// (installed as a prefix), <c>foreignpatch limitless twin patchcallee</c> (its GetFieldsForModel patched), <c>foreignpatch limitless
/// twin register</c> (a field in its own list: for the rest of the session).</item>
/// <item>Rule L: <c>lim-inner-count-postfix</c> on <c>CardModel:get_MaxUpgradeLevel</c> or <c>CardCmd::Enchant(EnchantmentModel,CardModel,Decimal)</c>,
/// a postfix counting every call (FOREIGN, not compared: the game's own calls elsewhere) and the calls on Limitless's storage card
/// (STATE, compared: 0 for level-0 inner cards on both sides). <c>foreignpatch limitless reach &lt;storage|CardClass&gt;
/// &lt;EnchantmentClass|none&gt; &lt;level&gt; [label]</c>: whether LimitlessCloneMemo would keep that saved content now (per content, the groups
/// it reaches); <c>foreignpatch limitless memo [label]</c>: the contents kept and the clears because the patch set changed.</item>
/// <item>Review C2: <c>lim-generic-count-postfix &lt;Namespace.Type:Method&gt; &lt;TypeArgument&gt;</c>, a counting postfix on one
/// closed instantiation of a generic member (<see cref="GenericCountPostfix"/>).</item>
/// </list>
/// </summary>
public static class ForeignShapesLimitless
{
    public static readonly string[] Shapes =
    {
        "lim-result-gated-postfix", "lim-enchantment-gated-postfix", "lim-cardmodel-gated-postfix", "lim-write-then-gated-postfix",
        "lim-twin", "lim-twin-prefix", "lim-inner-count-postfix", "lim-generic-count-postfix",
    };

    /// <summary>
    /// <c>foreignpatch lim-generic-count-postfix &lt;Namespace.Type:Method&gt; &lt;TypeArgument&gt;</c> (review C2): a counting postfix
    /// (uncompared) on one closed instantiation of a generic method, or of a generic type's method (<c>Type`1:.ctor</c> for its one
    /// constructor), as Harmony patches a generic member: the chain and the closures name the definition.
    /// </summary>
    private static IReadOnlyList<string> GenericCountPostfix(string spec, string argument)
    {
        System.Reflection.Assembly game = typeof(AbstractModel).Assembly;
        int colon = spec.LastIndexOf(':');
        if (colon <= 0)
        {
            throw new ArgumentException("lim-generic-count-postfix <Namespace.Type:Method> <TypeArgument>");
        }
        string typeName = spec[..colon], name = spec[(colon + 1)..];
        Type type = game.GetType(typeName) ?? AccessTools.TypeByName(typeName) ?? throw new ArgumentException("no type " + typeName);
        Type arg = game.GetType(argument) ?? Type.GetType(argument) ?? AccessTools.TypeByName(argument) ?? throw new ArgumentException("no type " + argument);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        MethodBase target;
        if (type.IsGenericTypeDefinition)
        {
            Type closed = type.MakeGenericType(arg);
            target = name == ".ctor" ? closed.GetConstructors(all).Single() : closed.GetMethods(all).Single(m => m.Name == name);
        }
        else
        {
            target = type.GetMethods(all).Single(m => m.Name == name && m.IsGenericMethodDefinition).MakeGenericMethod(arg);
        }
        string id = ForeignPatches.IdPrefix + "lim-generic-count-postfix";
        new Harmony(id).Patch(target, postfix: new HarmonyMethod(typeof(LimitlessGates), nameof(LimitlessGates.GenericCountPostfix)));
        lock (_installed)
        {
            _installed.Add((id, target));
        }
        return new[] { $"FOREIGN installed lim-generic-count-postfix on {target.DeclaringType}.{target.Name}<{arg.Name}> as {id}" };
    }

    /// <summary>Rule B's twins (Bench/ForeignShapesLimitlessTwins.cs) by variant.</summary>
    private static Type TwinClass(string variant) => variant switch
    {
        "accepted" => typeof(Twins.Accepted.SavedSpireFieldPatch),
        "unfiltered" => typeof(Twins.Unfiltered.SavedSpireFieldPatch),
        "alwaysstores" => typeof(Twins.AlwaysStores.SavedSpireFieldPatch),
        "impure" => typeof(Twins.ImpureInitializer.SavedSpireFieldPatch),
        _ => throw new ArgumentException("twin variant: accepted, unfiltered, alwaysstores or impure"),
    };

    /// <summary>The twin's postfix for SavedProperties.FromInternal or FillInternal.</summary>
    private static MethodInfo TwinPostfix(MethodBase target, string variant)
    {
        if (target.DeclaringType != typeof(MegaCrit.Sts2.Core.Saves.Runs.SavedProperties) || target.Name is not ("FromInternal" or "FillInternal"))
        {
            throw new ArgumentException("lim-twin goes on MegaCrit.Sts2.Core.Saves.Runs.SavedProperties:FromInternal or :FillInternal");
        }
        string name = target.Name == "FromInternal" ? "PostfixFromInternal" : "PostfixFillInternal";
        return TwinClass(variant).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new ArgumentException($"the {variant} twin has no {name}");
    }

    /// <summary>
    /// <c>foreignpatch limitless twin status|register|patchcallee</c>: the twins BaseLibTypes finds now (and whether the accepted
    /// one's IL is BaseLib's, as the stored constants say); a field registered in the accepted twin's own list (for the session:
    /// it can't be taken back); a no-op postfix on the accepted twin's GetFieldsForModel (removed with <c>limitless remove</c>).
    /// </summary>
    private static IReadOnlyList<string> TwinCommand(string what)
    {
        Type accepted = typeof(Twins.Accepted.SavedSpireFieldPatch);
        switch (what)
        {
            case "register":
                Twins.Accepted.SavedSpireFieldPatch.Register(new Twins.InertField());
                break;
            case "patchcallee":
            {
                MethodInfo callee = accepted.GetMethod("GetFieldsForModel", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(accepted.Name, "GetFieldsForModel");
                string id = ForeignPatches.IdPrefix + "lim-twin-callee";
                new Harmony(id).Patch(callee, postfix: new HarmonyMethod(typeof(LimitlessGates), nameof(LimitlessGates.NoOpPostfix)));
                lock (_installed)
                {
                    _installed.Add((id, callee));
                }
                break;
            }
            case "status":
                break;
            default:
                throw new ArgumentException("foreignpatch limitless twin status|register|patchcallee");
        }
        (MethodInfo[] from, MethodInfo[] fill, bool holds) = BaseLibTypes.TwinsForTest();
        string Il(string method, string expected) => OwnNormalizedIl.Of(accepted.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!) == expected ? "BaseLib's" : "other";
        return new[]
        {
            $"FOREIGN twin {what}: twins on FromInternal {from.Length} ({string.Join(", ", from.Select(m => m.DeclaringType?.FullName))}), on FillInternal {fill.Length}, one holds a field {holds}",
            $"FOREIGN twin accepted IL: FromInternal {Il("PostfixFromInternal", "9DD9F273573E3F25")}, FillInternal {Il("PostfixFillInternal", "1441D774E03D0220")}, GetFieldsForModel {Il("GetFieldsForModel", "5D37A68AE0E65B5D")}",
        };
    }

    private static readonly List<(string Id, MethodBase Target)> _installed = new();

    /// <summary>Whether this file handles that <c>foreignpatch</c> line.</summary>
    public static bool Handles(string[] parts) =>
        parts.Length >= 2 && (parts[0] == "limitless" || Shapes.Contains(parts[0]));

    public static IReadOnlyList<string> Run(string[] parts)
    {
        if (parts[0] == "limitless")
        {
            return parts[1] switch
            {
                "report" => LimitlessShapeCounts.ReportLines(parts.Length > 2 ? parts[2] : "end"),
                "remove" => Remove(),
                "twin" when parts.Length > 2 => TwinCommand(parts[2]),
                "reach" when parts.Length > 4 => ReachCommand(parts[2], parts[3], int.Parse(parts[4]), parts.Length > 5 ? parts[5] : "now"),
                "memo" => new[] { $"FOREIGN clonememo {(parts.Length > 2 ? parts[2] : "now")}: entries {LimitlessCloneMemo.MemoCount}, patch stamp clears {LimitlessCloneMemo.PatchStampClears}, reused {LimitlessCloneMemo.Reused}" },
                _ => throw new ArgumentException("foreignpatch limitless report [label] | remove | twin status|register|patchcallee | reach <storage|CardClass> <EnchantmentClass|none> <level> [label] | memo [label]"),
            };
        }
        string shape = parts[0];
        if (shape == "lim-generic-count-postfix")
        {
            return parts.Length > 2 ? GenericCountPostfix(parts[1], parts[2]) : throw new ArgumentException("lim-generic-count-postfix <Namespace.Type:Method> <TypeArgument>");
        }
        MethodBase target = Target(parts[1]);
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string gate = parts.Length > 2 && parts[2] != "priority" ? parts[2] : "";
        string id = ForeignPatches.IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "");
        var harmony = new Harmony(id);
        HarmonyMethod Gated(string name) => new(typeof(LimitlessGates), name) { priority = priority };
        string Gate(params string[] known) => known.Contains(gate) ? gate : throw new ArgumentException($"{shape} gate: {string.Join(" or ", known)}");
        switch (shape)
        {
            case "lim-result-gated-postfix":
                Gate("squeeze");
                if (target is not MethodInfo { ReturnType: var returned } || !typeof(CardModel).IsAssignableFrom(returned))
                {
                    throw new ArgumentException(shape + " goes on a method returning a card (CardModel:ToMutable)");
                }
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.ResultGatedPostfix)));
                break;
            case "lim-enchantment-gated-postfix":
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.EnchantmentGatedPostfix)));
                break;
            case "lim-cardmodel-gated-postfix":
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.CardModelGatedPostfix)));
                break;
            case "lim-write-then-gated-postfix":
                Gate("squeeze");
                harmony.Patch(target, postfix: Gated(nameof(LimitlessGates.WriteThenGatedPostfix)));
                break;
            case "lim-twin":
                harmony.Patch(target, postfix: new HarmonyMethod(TwinPostfix(target, gate)) { priority = priority });
                break;
            case "lim-inner-count-postfix":
                // Rule L: a strict patch (a count before any branch) on a method only the upgrade group or a second enchantment reaches.
                LimitlessShapeCounts.Ensure("lim-inner-count-postfix inner " + LimitlessShapeCounts.Name(target));
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(LimitlessGates), target.IsStatic ? nameof(LimitlessGates.InnerCountStaticPostfix)
                    : nameof(LimitlessGates.InnerCountPostfix)) { priority = priority });
                break;
            case "lim-twin-prefix":
                // Control: the accepted twin's postfix installed as a prefix (rule B lets a twin stand as a postfix only).
                Gate("accepted");
                harmony.Patch(target, prefix: new HarmonyMethod(TwinPostfix(target, gate)) { priority = priority });
                break;
            default:
                throw new ArgumentException($"unknown foreignpatch shape '{shape}': {string.Join(", ", Shapes)}");
        }
        lock (_installed)
        {
            _installed.Add((id, target));
        }
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {LimitlessShapeCounts.Name(target)} as {id} (priority {priority})" };
        lines.AddRange(Classification(target));
        return lines;
    }

    /// <summary>
    /// <c>foreignpatch limitless reach</c>: a saved card of that card (<c>storage</c>: Limitless's storage card, else a game card class),
    /// enchantment (a game enchantment class, or <c>none</c>) and upgrade level, judged as LimitlessCloneMemo judges a content now.
    /// </summary>
    private static IReadOnlyList<string> ReachCommand(string card, string enchantment, int level, string label)
    {
        System.Reflection.Assembly game = typeof(AbstractModel).Assembly;
        Type cardType = card == "storage" ? LimitlessCloneMemo.StorageCardType ?? throw new ArgumentException("Limitless has no storage card")
            : game.GetType("MegaCrit.Sts2.Core.Models.Cards." + card, true)!;
        var saved = new MegaCrit.Sts2.Core.Saves.Runs.SerializableCard { Id = ModelDb.GetId(cardType), CurrentUpgradeLevel = level };
        if (enchantment != "none")
        {
            saved.Enchantment = new MegaCrit.Sts2.Core.Saves.Runs.SerializableEnchantment
            {
                Id = ModelDb.GetId(game.GetType("MegaCrit.Sts2.Core.Models.Enchantments." + enchantment, true)!),
                Amount = 1,
            };
        }
        (bool kept, LimitlessCloneMemo.Reach reach) = LimitlessCloneMemo.KeptForTest(saved);
        return new[]
        {
            $"FOREIGN reach {label} {card} {enchantment} {level}: kept {kept}, reaches {reach}, hookskip patched {LimitlessCloneMemo.ContentPatched(saved)}",
            $"FOREIGN reach {label} closures done {LimitlessCloneMemo.ClosuresDone}: {LimitlessCloneMemo.ClosureStates()}",
        };
    }

    /// <summary>A method by <c>Namespace.Type:Method</c>, or any member by its fingerprint key <c>Namespace.Type::Method(ArgTypes)</c>.</summary>
    private static MethodBase Target(string spec) =>
        (spec.Contains("::", StringComparison.Ordinal) ? MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, spec) : AccessTools.Method(spec))
        ?? throw new ArgumentException("no method " + spec + " (Namespace.Type:Method, or Namespace.Type::Method(ArgTypes))");

    private static IReadOnlyList<string> Classification(MethodBase target) =>
        PatchSafety.Describe(target).Select(line => $"FOREIGN classify {LimitlessShapeCounts.Name(target)}: {line}")
            .DefaultIfEmpty($"FOREIGN classify {LimitlessShapeCounts.Name(target)}: no other mod's patch").ToList();

    private static IReadOnlyList<string> Remove()
    {
        (string Id, MethodBase Target)[] installed;
        lock (_installed)
        {
            installed = _installed.ToArray();
            _installed.Clear();
        }
        var lines = new List<string>();
        foreach ((string id, MethodBase target) in installed)
        {
            new Harmony(id).Unpatch(target, HarmonyPatchType.All, id);
            lines.Add($"FOREIGN removed {id} from {LimitlessShapeCounts.Name(target)}");
        }
        return lines.Count > 0 ? lines : new[] { "FOREIGN nothing to remove (limitless shapes)" };
    }
}

/// <summary>
/// The counts of <see cref="LimitlessGates"/>: a class of its own whose initializer only makes the dictionary (a pure one), so
/// the gated shapes' calls into it don't make their gates refused for an initializer.
/// </summary>
internal static class LimitlessShapeCounts
{
    private static readonly ConcurrentDictionary<string, long> _counts = new();

    internal static void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    /// <summary>A count reported even while 0 (a STATE line both sides print).</summary>
    internal static void Ensure(string key) => _counts.TryAdd(key, 0);

    /// <summary>Counts reported as FOREIGN lines, never compared (the game's own calls, which may differ in timing).</summary>
    private static readonly ConcurrentDictionary<string, long> _uncompared = new();

    internal static void CountUncompared(string key) => _uncompared.AddOrUpdate(key, 1, (_, n) => n + 1);

    internal static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    internal static IReadOnlyList<string> ReportLines(string label)
    {
        var lines = _counts.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"STATE foreign {label} {e.Key} {e.Value}")
            .Concat(_uncompared.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"FOREIGN count {label} {e.Key} {e.Value}")).ToList();
        return lines.Count > 0 ? lines : new[] { $"FOREIGN limitless shapes {label}: nothing counted" };
    }
}

/// <summary>The gated shapes and their near misses, in a class without a static initializer (one would be judged too).</summary>
internal static class LimitlessGates
{
    /// <summary>Rule A, accepted on CardModel.ToMutable: gated on the copy returned (the instance's runtime type).</summary>
    public static void ResultGatedPostfix(CardModel __result, MethodBase __originalMethod)
    {
        if (__result is Squeeze)
        {
            LimitlessShapeCounts.Count("lim-result-gated-postfix squeeze " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Control: gated on an enchantment type, on a method that also runs on the inner card's enchantment.</summary>
    public static void EnchantmentGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
    {
        if (__instance is EnchantmentModel)
        {
            LimitlessShapeCounts.Count("lim-enchantment-gated-postfix " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Control: gated on every card (the inner storage card is one).</summary>
    public static void CardModelGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
    {
        if (__instance is CardModel)
        {
            LimitlessShapeCounts.Count("lim-cardmodel-gated-postfix " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Rule B's patched-callee control: a postfix doing nothing (on the accepted twin's GetFieldsForModel).</summary>
    public static void NoOpPostfix()
    {
    }

    /// <summary>Rule L: every call counted (uncompared), then the calls on Limitless's storage card (compared).</summary>
    public static void InnerCountPostfix(CardModel __instance, MethodBase __originalMethod)
    {
        LimitlessShapeCounts.CountUncompared("lim-inner-count-postfix all " + LimitlessShapeCounts.Name(__originalMethod));
        if (__instance != null && __instance.GetType() == LimitlessCloneMemo.StorageCardType)
        {
            LimitlessShapeCounts.Count("lim-inner-count-postfix inner " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary><see cref="InnerCountPostfix"/> for CardCmd.Enchant(EnchantmentModel, CardModel, decimal): the card is its argument.</summary>
    public static void InnerCountStaticPostfix(CardModel card, MethodBase __originalMethod)
    {
        LimitlessShapeCounts.CountUncompared("lim-inner-count-postfix all " + LimitlessShapeCounts.Name(__originalMethod));
        if (card != null && card.GetType() == LimitlessCloneMemo.StorageCardType)
        {
            LimitlessShapeCounts.Count("lim-inner-count-postfix inner " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }

    /// <summary>Review C2: every call of the patched closed instantiation counted (uncompared).</summary>
    public static void GenericCountPostfix(MethodBase __originalMethod) =>
        LimitlessShapeCounts.CountUncompared("lim-generic-count-postfix " + LimitlessShapeCounts.Name(__originalMethod));

    /// <summary>Control: a call (a count) before the gate's branch.</summary>
    public static void WriteThenGatedPostfix(AbstractModel __instance, MethodBase __originalMethod)
    {
        LimitlessShapeCounts.Count("lim-write-then-gated-postfix every call");
        if (__instance is Squeeze)
        {
            LimitlessShapeCounts.Count("lim-write-then-gated-postfix squeeze " + LimitlessShapeCounts.Name(__originalMethod));
        }
    }
}
