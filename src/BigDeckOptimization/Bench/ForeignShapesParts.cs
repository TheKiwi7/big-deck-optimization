using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic patches of "another mod" for the generic rules of 2026-11-18 on the pile parts (PileIndex, CardDbScan, FastListeners,
/// CardSortKeys, CardFlightLimit, RelicFlashMerge), installed through <c>foreignpatch &lt;shape&gt; &lt;Type:Method&gt; [gate]
/// [priority N]</c> as ForeignPatches' own shapes (removed by <c>foreignpatch remove</c>, counted in <c>foreignpatch report</c>'s
/// STATE lines). Each shape's own comment says which rule it accepts or controls.
/// </summary>
public static class ForeignShapesParts
{
    public static readonly string[] Shapes =
    {
        "redirect-prefix",
        "sort-gated-postfix", "sort-nearmiss-postfix",
        // Not a shape: "foreignpatch c0-override constant|patch|subclass|stateful" (rule C0: classes of "another mod" emitted into a
        // dynamic assembly, overriding PowerModel.OwnerIsSecondaryEnemy, which CardFlightLimit's combat-end check reaches).
        "c0-override",
        // Not a shape: "foreignpatch parts-report <label>" prints the display counts of these shapes (FOREIGN lines, not compared).
        "parts-report",
        // Not a shape: "foreignpatch parts-require <counter>" fails unless that counter of these rules is above zero.
        "parts-require",
    };

    /// <summary>The counters <c>foreignpatch parts-require</c> knows (test builds).</summary>
    private static long Counter(string name) => name switch
    {
#if BENCH
        "skippedaddbumps" => FastListeners.SkippedAddBumps,
#endif
        _ => throw new ArgumentException("parts-require: unknown counter " + name),
    };

    private static Action<string>? _count;

    private static void Count(string key) => _count?.Invoke(key);

    /// <summary>Installs one shape; <paramref name="installed"/> records it for <c>foreignpatch remove</c>, <paramref name="count"/> counts into the report.</summary>
    internal static IReadOnlyList<string> Install(string[] parts, Action<string, MethodBase> installed, Action<string> count)
    {
        _count = count;
        string shape = parts[0];
        if (shape == "parts-require")
        {
            long value = Counter(parts[1]);
            if (value <= 0)
            {
                throw new InvalidOperationException($"parts-require {parts[1]}: {value}");
            }
            return new[] { $"FOREIGN parts {parts[1]} {value}" };
        }
        if (shape == "c0-override")
        {
            return C0Override(parts[1], installed);
        }
        if (shape == "parts-report")
        {
            return new[]
            {
                $"FOREIGN display {parts[1]} sort-gated-postfix squeeze calls {SortGates.SqueezeCalls}",
                $"FOREIGN display {parts[1]} sort-nearmiss-postfix every call {SortGates.EveryCall}",
            };
        }
        MethodBase target = (parts[1].Contains("::", StringComparison.Ordinal) ? MethodFingerprint.Resolve(typeof(AbstractModel).Assembly, parts[1]) : AccessTools.Method(parts[1]))
            ?? throw new ArgumentException("no method " + parts[1]);
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string gate = parts.Length > 2 && parts[2] != "priority" ? parts[2] : "";
        string id = ForeignPatches.IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "") + (priority != Priority.Normal ? "-p" + priority : "");
        var harmony = new Harmony(id);
        HarmonyMethod Fix(string name) => new(typeof(ForeignShapesParts), name) { priority = priority };
        void Require(Type type, string method)
        {
            if (target.DeclaringType != type || target.Name != method)
            {
                throw new ArgumentException($"{shape} goes on {type.FullName}:{method}");
            }
        }
        switch (shape)
        {
            case "redirect-prefix":
                // Rule AS (accepted when sorted before every prefix of this mod on AddInternal; the default priority is): Chimera's
                // shape, a bool prefix putting a card in another pile itself and skipping the original. Here: a Squeeze added to
                // the discard pile goes to the end of its owner's draw pile instead (through AddInternal: the nested add is tracked
                // as any other). Changes the game's state: installed on both sides of the A/B alike. With "priority 0" (sorted after
                // this mod's prefixes) it is the control that backs off.
                Require(typeof(CardPile), nameof(CardPile.AddInternal));
                harmony.Patch(target, prefix: Fix(nameof(RedirectPrefix)));
                break;
            case "sort-gated-postfix":
            case "sort-nearmiss-postfix":
                // Rule TG on CardSortKeys (Card Upgrader's shape on CardModel.get_DynamicVars): a postfix doing nothing unless the
                // instance is a Squeeze (accepted: sorts without a Squeeze stay answered, a sort holding one is the game's), and its
                // near miss (a static write before the type test: every call counted, so every sort is the game's). Their counts
                // are display lines (other code reads DynamicVars at the display's pace): the A/B compares the sorts' results.
                if (gate != "squeeze")
                {
                    throw new ArgumentException(shape + " gate: squeeze");
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SortGates), shape == "sort-gated-postfix" ? nameof(SortGates.GatedPostfix) : nameof(SortGates.NearMissPostfix)) { priority = priority });
                break;
            default:
                throw new ArgumentException("unknown shape " + shape);
        }
        installed(id, target);
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {target.DeclaringType?.Name}.{target.Name} as {id} (priority {priority})" };
        lines.AddRange(PatchSafety.Describe(target).Select(line => $"FOREIGN classify {target.DeclaringType?.Name}.{target.Name}: {line}"));
        return lines;
    }

    private static System.Reflection.Emit.ModuleBuilder? _c0Module;
    private static int _c0Types;
    private static Type? _c0Constant;

    /// <summary>
    /// Rule C0's synthetic overrides (abstract classes deriving from PowerModel: never instantiated, so they change no game state;
    /// their mere presence among the loaded types is what CardFlightLimit's combat-end check judges). constant: <c>ldc.i4.1; ret</c>
    /// (let stand); patch: a no-op postfix of another "mod" on that constant override (counted while installed, removed by
    /// <c>foreignpatch remove</c>); subclass: a constant base and a subclass reading state (the subclass counted); stateful: an
    /// override reading the instance (counted). Each emission logs rule C0's verdict on each type. Types can't be unloaded: what
    /// counts stays counted for the session (a scenario runs them last).
    /// </summary>
    private static IReadOnlyList<string> C0Override(string what, Action<string, MethodBase> installed)
    {
        MethodInfo virtualGetter = AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Models.PowerModel), nameof(MegaCrit.Sts2.Core.Models.PowerModel.OwnerIsSecondaryEnemy))
            ?? throw new MissingMethodException("PowerModel.OwnerIsSecondaryEnemy");
        var lines = new List<string>();
        void Verdict(Type type) => lines.Add($"FOREIGN c0 {type.Name}: {DescribeC0(type, virtualGetter)}");
        switch (what)
        {
            case "constant":
                _c0Constant = EmitPower("BdoC0Constant", typeof(MegaCrit.Sts2.Core.Models.PowerModel), constant: true);
                Verdict(_c0Constant);
                break;
            case "patch":
            {
                Type type = _c0Constant ?? throw new InvalidOperationException("c0-override patch: emit the constant one first");
                MethodInfo own = type.GetMethod("get_OwnerIsSecondaryEnemy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!;
                string id = ForeignPatches.IdPrefix + "c0-override-patch";
                new Harmony(id).Patch(own, postfix: new HarmonyMethod(typeof(ForeignShapesParts), nameof(C0NoOpPostfix)));
                installed(id, own);
                Verdict(type);
                break;
            }
            case "subclass":
            {
                Type constantBase = EmitPower("BdoC0ConstantBase", typeof(MegaCrit.Sts2.Core.Models.PowerModel), constant: true);
                Type sub = EmitPower("BdoC0StatefulSub", constantBase, constant: false);
                Verdict(constantBase);
                Verdict(sub);
                break;
            }
            case "stateful":
                Verdict(EmitPower("BdoC0Stateful", typeof(MegaCrit.Sts2.Core.Models.PowerModel), constant: false));
                break;
            default:
                throw new ArgumentException("c0-override constant|patch|subclass|stateful");
        }
        return lines;
    }

    private static string DescribeC0(Type type, MethodInfo virtualGetter)
    {
#if BENCH
        return CodeSet.OverrideVerdictForTest(type, virtualGetter);
#else
        return "not judged in this build";
#endif
    }

    private static void C0NoOpPostfix()
    {
    }

    /// <summary>An abstract class deriving from <paramref name="baseType"/> overriding OwnerIsSecondaryEnemy: a constant true, or a read of the instance.</summary>
    private static Type EmitPower(string name, Type baseType, bool constant)
    {
        if (_c0Module == null)
        {
            string unique = "BdoC0Shapes" + Environment.TickCount64.ToString("X");
            _c0Module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule(unique);
        }
        System.Reflection.Emit.TypeBuilder type = _c0Module.DefineType(name + (++_c0Types), TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class, baseType);
        ConstructorInfo baseCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
            ?? throw new MissingMethodException(baseType.Name + "()");
        System.Reflection.Emit.ILGenerator ctor = type.DefineConstructor(MethodAttributes.Family, CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
        ctor.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        ctor.Emit(System.Reflection.Emit.OpCodes.Call, baseCtor);
        ctor.Emit(System.Reflection.Emit.OpCodes.Ret);
        System.Reflection.Emit.MethodBuilder getter = type.DefineMethod("get_OwnerIsSecondaryEnemy",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName, typeof(bool), Type.EmptyTypes);
        System.Reflection.Emit.ILGenerator il = getter.GetILGenerator();
        if (constant)
        {
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
        }
        else
        {
            // A read of the instance: not a constant (never called: the class is abstract and never instantiated).
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(object).GetMethod(nameof(GetHashCode))!);
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
            il.Emit(System.Reflection.Emit.OpCodes.Cgt);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return type.CreateType()!;
    }

    /// <summary>The TG shapes' class: no static field initializer (no type initializer to judge), counts only.</summary>
    private static class SortGates
    {
        public static long SqueezeCalls;
        public static long EveryCall;

        public static void GatedPostfix(AbstractModel __instance)
        {
            if (__instance is Squeeze)
            {
                SqueezeCalls++;
            }
        }

        public static void NearMissPostfix(AbstractModel __instance)
        {
            EveryCall++;
            if (__instance is Squeeze)
            {
                SqueezeCalls++;
            }
        }
    }

    /// <summary>redirect-prefix: a Squeeze added to a discard pile goes to its owner's draw pile instead (counted), the original skipped.</summary>
    private static bool RedirectPrefix(CardPile __instance, CardModel card)
    {
        if (card is not Squeeze || __instance.Type != PileType.Discard || card.Owner?.PlayerCombatState == null)
        {
            return true;
        }
        PileType.Draw.GetPile(card.Owner).AddInternal(card);
        Count("redirect-prefix AddInternal");
        return false;
    }
}
