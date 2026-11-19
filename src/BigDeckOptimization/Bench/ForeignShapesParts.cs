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
        "ng-guard", "ng-true-guard", "ng-call-guard",
        "nullfill-postfix", "nullfill-nearmiss-postfix",
        "latch-postfix", "latch-reset-postfix", "latch-open-postfix",
        "pilegate-postfix", "pilegate-beq-postfix", "pilegate-nearmiss-postfix",
        // Not a shape: "foreignpatch c0-override constant|patch|subclass|stateful" (rule C0: classes of "another mod" emitted into a
        // dynamic assembly, overriding PowerModel.OwnerIsSecondaryEnemy, which CardFlightLimit's combat-end check reaches).
        "c0-override",
        // Not a shape: "foreignpatch parts-report <label>" prints the display counts of these shapes (FOREIGN lines, not compared).
        "parts-report",
        // Not a shape: "foreignpatch parts-require <counter>" fails unless that counter of these rules is above zero.
        "parts-require",
        // Not shapes: "foreignpatch parts-mark <label> <counter>" saves a counter of these rules, "parts-grew <label> <counter>"
        // and "parts-same <label> <counter>" fail unless it grew (stayed the same) since.
        "parts-mark", "parts-grew", "parts-same",
    };

    /// <summary>The counters <c>foreignpatch parts-require</c> knows (test builds).</summary>
    private static long Counter(string name) => name switch
    {
#if BENCH
        "skippedaddbumps" => FastListeners.SkippedAddBumps,
        "latched" => LazyGuard.LatchedChecks,
        "mergedflushes" => CardFlightLimit.MergedFlushes,
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
        if (shape is "parts-mark" or "parts-grew" or "parts-same")
        {
            string label = parts[1];
            long now = Counter(parts[2]);
            if (shape == "parts-mark")
            {
                _marks[(label, parts[2])] = now;
                return new[] { $"FOREIGN parts {parts[2]} at '{label}': {now}" };
            }
            long then = _marks.TryGetValue((label, parts[2]), out long saved) ? saved : throw new InvalidOperationException($"{shape} {label} {parts[2]}: no parts-mark");
            bool grew = now > then;
            if (grew != (shape == "parts-grew"))
            {
                throw new InvalidOperationException($"{shape} {label} {parts[2]}: {then} -> {now}");
            }
            return new[] { $"FOREIGN parts {parts[2]} {(grew ? "grew" : "same")} since '{label}': {then} -> {now}" };
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
                $"FOREIGN display {parts[1]} nullfill-postfix null results {NullFillNulls}, nearmiss every call {NullFillEvery}",
                $"FOREIGN display {parts[1]} latch runs {LatchRuns}",
                $"FOREIGN display {parts[1]} pilegate runs {PileGateRuns}, nearmiss every call {PileGateEvery}",
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
            case "ng-guard":
            case "ng-true-guard":
            case "ng-call-guard":
                // Rule NG (HonkaiLib's CombatStateContainsNullGuardPatch, on CombatState.Contains or RunState.Contains): a bool
                // prefix answering false for a null model and letting every other one through, emitted with Release-build IL (a
                // Debug build of this file would add nops, locals and branches); its controls: the same answering true for null,
                // and one calling a method on the non-null path. No game state changes (the game's lists hold no null model).
                if (target.DeclaringType?.Name is not ("CombatState" or "RunState") || target.Name != "Contains")
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Combat.CombatState:Contains or MegaCrit.Sts2.Core.Runs.RunState:Contains");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(EmitGuard(shape)) { priority = priority });
                break;
            case "nullfill-postfix":
            case "nullfill-nearmiss-postfix":
                // Rule null-fill (CardFusion's FusedCard_CombatState_Patch on CardModel.CombatState): a postfix doing nothing for a
                // non-null result (here it only counts the null ones: no game state changes), Release-build IL emitted; its near
                // miss counts every call before the test (a sort is the game's then).
                if (target.DeclaringType != typeof(CardModel) || target.Name != "get_CombatState")
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Models.CardModel:get_CombatState");
                }
                harmony.Patch(target, postfix: new HarmonyMethod(EmitNullFill(shape == "nullfill-nearmiss-postfix")) { priority = priority });
                break;
            case "latch-postfix":
            case "latch-reset-postfix":
            case "latch-open-postfix":
            {
                // Rule latch (All Timeline Unlock's GetProgressPatch on SaveManager.Progress): a postfix returning at once when its
                // private flag is set, the flag only ever set (latch-postfix: accepted once it ran, which the install makes happen by
                // reading the getter once); controls: the class also clears the flag in another method (latch-reset-postfix), the
                // flag set only by a method nobody calls (latch-open-postfix: the latch stays open). Release-build IL emitted. They
                // count their runs only (display).
                MethodInfo postfix = EmitLatch(shape);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = priority });
                if (target is MethodInfo { IsStatic: false } getter && getter.GetParameters().Length == 0 && target.DeclaringType == typeof(MegaCrit.Sts2.Core.Saves.SaveManager))
                {
                    _ = getter.Invoke(MegaCrit.Sts2.Core.Saves.SaveManager.Instance, null);
                }
                break;
            }
            case "pilegate-postfix":
            case "pilegate-beq-postfix":
            case "pilegate-nearmiss-postfix":
            {
                // Rule P (Downfall's Hermit HandChangedPatches on CardPile.InvokeCardAddFinished): a postfix acting only when the
                // pile's Type is the gate's (hand: accepted, the merged pile event only goes to draw and discard piles; draw: the
                // control, it acts on a merged pile). pilegate-beq-postfix: the same test compiled the other way round (beq to
                // the body, ret next); the near miss counts every call before the test. Release-build IL emitted; they count
                // their runs only (display).
                Require(typeof(CardPile), nameof(CardPile.InvokeCardAddFinished));
                PileType type = gate switch
                {
                    "hand" => PileType.Hand,
                    "draw" => PileType.Draw,
                    _ => throw new ArgumentException(shape + " gate: hand or draw"),
                };
                harmony.Patch(target, postfix: new HarmonyMethod(EmitPileGate(shape, type)) { priority = priority });
                break;
            }
            default:
                throw new ArgumentException("unknown shape " + shape);
        }
        installed(id, target);
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {target.DeclaringType?.Name}.{target.Name} as {id} (priority {priority})" };
        lines.AddRange(PatchSafety.Describe(target).Select(line => $"FOREIGN classify {target.DeclaringType?.Name}.{target.Name}: {line}"));
        return lines;
    }

    private static int _guards;

    private static readonly Dictionary<(string Label, string Counter), long> _marks = new();

    public static long PileGateRuns, PileGateEvery;

    public static void CountPileGate() => PileGateRuns++;

    public static void CountPileGateEvery() => PileGateEvery++;

    /// <summary>
    /// The pile-type gate as a Release build gives it: <c>ldarg.0; callvirt CardPile::get_Type; ldc.i4 K; bne.un.s L; call
    /// CountPileGate; L: ret</c> (parameter __instance); the beq form <c>...; ldc.i4 K; beq.s B; ret; B: call CountPileGate; ret</c>;
    /// the near miss with a call first.
    /// </summary>
    private static MethodInfo EmitPileGate(string shape, PileType gate)
    {
        if (_c0Module == null)
        {
            string unique = "BdoC0Shapes" + Environment.TickCount64.ToString("X");
            _c0Module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule(unique);
        }
        System.Reflection.Emit.TypeBuilder type = _c0Module.DefineType("BdoPileGate" + (++_guards),
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        System.Reflection.Emit.MethodBuilder method = type.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(void), new[] { typeof(CardPile) });
        method.DefineParameter(1, ParameterAttributes.None, "__instance");
        System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
        MethodInfo getType = AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Type))!;
        MethodInfo count = typeof(ForeignShapesParts).GetMethod(nameof(CountPileGate))!;
        if (shape == "pilegate-nearmiss-postfix")
        {
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(ForeignShapesParts).GetMethod(nameof(CountPileGateEvery))!);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, getType);
        // The short constant forms, as the compiler gives them (Hermit's ldc.i4.2).
        il.Emit(gate == PileType.Hand ? System.Reflection.Emit.OpCodes.Ldc_I4_2 : System.Reflection.Emit.OpCodes.Ldc_I4_1);
        System.Reflection.Emit.Label label = il.DefineLabel();
        if (shape == "pilegate-beq-postfix")
        {
            il.Emit(System.Reflection.Emit.OpCodes.Beq_S, label);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            il.MarkLabel(label);
            il.Emit(System.Reflection.Emit.OpCodes.Call, count);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
        }
        else
        {
            il.Emit(System.Reflection.Emit.OpCodes.Bne_Un_S, label);
            il.Emit(System.Reflection.Emit.OpCodes.Call, count);
            il.MarkLabel(label);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
        }
        return type.CreateType()!.GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)!;
    }

    public static long NullFillNulls, NullFillEvery, LatchRuns;

    public static void CountLatch() => LatchRuns++;

    /// <summary>
    /// The latch postfix as a Release build gives it: <c>ldsfld _ran; brtrue.s L; ldc.i4.1; stsfld _ran; call CountLatch; L: ret</c>
    /// (_ran private static bool of its own class). latch-reset-postfix adds a method storing false; latch-open-postfix stores the
    /// flag in a method of its own nobody calls, not in the postfix.
    /// </summary>
    private static MethodInfo EmitLatch(string shape)
    {
        if (_c0Module == null)
        {
            string unique = "BdoC0Shapes" + Environment.TickCount64.ToString("X");
            _c0Module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule(unique);
        }
        System.Reflection.Emit.TypeBuilder type = _c0Module.DefineType("BdoLatch" + (++_guards),
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        System.Reflection.Emit.FieldBuilder ran = type.DefineField("_ran", typeof(bool), FieldAttributes.Private | FieldAttributes.Static);
        System.Reflection.Emit.MethodBuilder method = type.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(void), Type.EmptyTypes);
        System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
        System.Reflection.Emit.Label done = il.DefineLabel();
        il.Emit(System.Reflection.Emit.OpCodes.Ldsfld, ran);
        il.Emit(System.Reflection.Emit.OpCodes.Brtrue_S, done);
        if (shape != "latch-open-postfix")
        {
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
            il.Emit(System.Reflection.Emit.OpCodes.Stsfld, ran);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(ForeignShapesParts).GetMethod(nameof(CountLatch))!);
        il.MarkLabel(done);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        if (shape != "latch-postfix")
        {
            System.Reflection.Emit.ILGenerator other = type.DefineMethod(shape == "latch-reset-postfix" ? "Reset" : "Set",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes).GetILGenerator();
            other.Emit(shape == "latch-reset-postfix" ? System.Reflection.Emit.OpCodes.Ldc_I4_0 : System.Reflection.Emit.OpCodes.Ldc_I4_1);
            other.Emit(System.Reflection.Emit.OpCodes.Stsfld, ran);
            other.Emit(System.Reflection.Emit.OpCodes.Ret);
        }
        return type.CreateType()!.GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)!;
    }

    public static void CountNullFill() => NullFillNulls++;

    public static void CountNullFillEvery() => NullFillEvery++;

    /// <summary>
    /// The null-fill postfix as a Release build gives it: <c>ldarg.1; ldind.ref; brtrue.s L; call CountNullFill; L: ret</c>
    /// (parameters __instance and __result), or the near miss with a call before the test.
    /// </summary>
    private static MethodInfo EmitNullFill(bool nearMiss)
    {
        if (_c0Module == null)
        {
            string unique = "BdoC0Shapes" + Environment.TickCount64.ToString("X");
            _c0Module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule(unique);
        }
        System.Reflection.Emit.TypeBuilder type = _c0Module.DefineType("BdoNullFill" + (++_guards),
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        System.Reflection.Emit.MethodBuilder method = type.DefineMethod("Postfix", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(void), new[] { typeof(CardModel), typeof(MegaCrit.Sts2.Core.Combat.ICombatState).MakeByRefType() });
        method.DefineParameter(1, ParameterAttributes.None, "__instance");
        method.DefineParameter(2, ParameterAttributes.None, "__result");
        System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
        System.Reflection.Emit.Label done = il.DefineLabel();
        if (nearMiss)
        {
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(ForeignShapesParts).GetMethod(nameof(CountNullFillEvery))!);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        il.Emit(System.Reflection.Emit.OpCodes.Ldind_Ref);
        il.Emit(System.Reflection.Emit.OpCodes.Brtrue_S, done);
        il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(ForeignShapesParts).GetMethod(nameof(CountNullFill))!);
        il.MarkLabel(done);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return type.CreateType()!.GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)!;
    }

    /// <summary>A call the ng-call-guard control makes on its non-null path (does nothing).</summary>
    public static void GuardCall()
    {
    }

    /// <summary>
    /// The null guard as the C# compiler's Release build gives it (<c>ldarg.0; brfalse.s L; ldc.i4.1; ret; L: ldarg.1; ldc.i4.0;
    /// stind.i1; ldc.i4.0; ret</c>), in a class of its own in a dynamic assembly (parameters named model and __result).
    /// </summary>
    private static MethodInfo EmitGuard(string shape)
    {
        if (_c0Module == null)
        {
            string unique = "BdoC0Shapes" + Environment.TickCount64.ToString("X");
            _c0Module = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule(unique);
        }
        System.Reflection.Emit.TypeBuilder type = _c0Module.DefineType("BdoNullGuard" + (++_guards),
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        System.Reflection.Emit.MethodBuilder method = type.DefineMethod("Prefix", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(bool), new[] { typeof(AbstractModel), typeof(bool).MakeByRefType() });
        method.DefineParameter(1, ParameterAttributes.None, "model");
        method.DefineParameter(2, ParameterAttributes.None, "__result");
        System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
        System.Reflection.Emit.Label isNull = il.DefineLabel();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Brfalse_S, isNull);
        if (shape == "ng-call-guard")
        {
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(ForeignShapesParts).GetMethod(nameof(GuardCall))!);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        il.MarkLabel(isNull);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        il.Emit(shape == "ng-true-guard" ? System.Reflection.Emit.OpCodes.Ldc_I4_1 : System.Reflection.Emit.OpCodes.Ldc_I4_0);
        il.Emit(System.Reflection.Emit.OpCodes.Stind_I1);
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        Type created = type.CreateType()!;
        return created.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)!;
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
