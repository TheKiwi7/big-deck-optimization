using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic patches of "another mod" for the gate rules of 2026-11-18 (DESIGN "Re-verification of the leads"): F1 (pass-through
/// finalizers under TranspiledOnly), E (the type gates' path evaluated, Debug IL), G (a trivial static getter as a branch, both
/// sides explored), I (static calls into scanned code), the initializer extensions and FW (forwarders). Most shapes are emitted
/// with Reflection.Emit, so their IL is exactly the form a rule is about (the test build compiles C# optimized: Release IL).
/// <list type="bullet">
/// <item><c>foreignpatch gates selftest</c>: every shape's verdict through <see cref="PatchSafety"/>'s own judge (no patch
/// installed), accept cases and the controls that must still back off, each compared with its expected verdict.</item>
/// <item><c>foreignpatch gates-&lt;shape&gt; &lt;Type:Method&gt;</c>: installs one shape (Harmony id <c>bdtest.foreign.&lt;shape&gt;</c>),
/// removed by <c>foreignpatch remove</c>. Counting shapes count through <see cref="Hit"/> (STATE lines of
/// <c>foreignpatch report</c>, the same with this mod on or off).</item>
/// </list>
/// </summary>
public static class ForeignShapesGates
{
    /// <summary>The installable shapes (see <see cref="Build"/>).</summary>
    public static readonly string[] Shapes =
    {
        "gates-passthrough-finalizer", "gates-passthrough-finalizer-debug", "gates-swallow-finalizer", "gates-ternary-finalizer",
        "gates-debug-gated-postfix", "gates-debug-gated-prefix", "gates-bitor-gated-postfix", "gates-bool-local-postfix",
        "gates-copy-prologue-postfix", "gates-call-first-postfix",
        "gates-getter-gated-postfix", "gates-getter-ungated-postfix",
    };

    /// <summary>Whether a <c>foreignpatch</c> line is one of this file's.</summary>
    public static bool Handles(string[] parts) => parts.Length >= 2 && (parts[0] == "gates" || Shapes.Contains(parts[0]));

    /// <summary>Counts through <see cref="GatesCounter.Hit"/> (a class without an initializer: the gate rules judge what a gated path reaches).</summary>
    public static void Hit(string key) => GatesCounter.Hit(key);

    /// <summary>Runs one line: the self-test, or one shape installed on a target. <paramref name="installed"/> records it for removal.</summary>
    public static IReadOnlyList<string> Run(string[] parts, Action<string, MethodBase> installed, Func<MethodBase, IReadOnlyList<string>> classify)
    {
        if (parts[0] == "gates")
        {
            return parts[1] == "selftest" ? SelfTest() : throw new ArgumentException("foreignpatch gates selftest");
        }
        string shape = parts[0];
        MethodBase target = AccessTools.Method(parts[1]) ?? throw new ArgumentException("no method " + parts[1]);
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string id = ForeignPatches.IdPrefix + shape;
        (MethodInfo method, PatchSafety.PatchKind kind) = Build(shape, target);
        var fix = new HarmonyMethod(method) { priority = priority };
        new Harmony(id).Patch(target, prefix: kind == PatchSafety.PatchKind.Prefix ? fix : null, postfix: kind == PatchSafety.PatchKind.Postfix ? fix : null,
            finalizer: kind == PatchSafety.PatchKind.Finalizer ? fix : null);
        installed(id, target);
        var lines = new List<string> { $"FOREIGN installed {shape} on {target.DeclaringType?.Name}.{target.Name} as {id} (priority {priority})" };
        lines.AddRange(classify(target));
        return lines;
    }

    /// <summary>The patch method of an installable shape and its kind.</summary>
    private static (MethodInfo Method, PatchSafety.PatchKind Kind) Build(string shape, MethodBase target)
    {
        string key = $"{shape} {target.DeclaringType?.Name}.{target.Name}";
        return shape switch
        {
            "gates-passthrough-finalizer" => (PassThroughRelease(key), PatchSafety.PatchKind.Finalizer),
            "gates-passthrough-finalizer-debug" => (PassThroughDebug(key), PatchSafety.PatchKind.Finalizer),
            "gates-swallow-finalizer" => (SwallowFinalizer(key), PatchSafety.PatchKind.Finalizer),
            "gates-ternary-finalizer" => (TernaryFinalizer(key), PatchSafety.PatchKind.Finalizer),
            "gates-debug-gated-postfix" => (DebugGatedPostfix(key), PatchSafety.PatchKind.Postfix),
            "gates-debug-gated-prefix" => (DebugGatedPrefix(key), PatchSafety.PatchKind.Prefix),
            "gates-bitor-gated-postfix" => (BitOrGatedPostfix(key), PatchSafety.PatchKind.Postfix),
            "gates-bool-local-postfix" => (BoolLocalPostfix(key), PatchSafety.PatchKind.Postfix),
            "gates-copy-prologue-postfix" => (CopyProloguePostfix(key), PatchSafety.PatchKind.Postfix),
            "gates-call-first-postfix" => (CallFirstPostfix(key), PatchSafety.PatchKind.Postfix),
            "gates-getter-gated-postfix" => (FlagOn(GetterGatedPostfix(key)), PatchSafety.PatchKind.Postfix),
            "gates-getter-ungated-postfix" => (FlagOn(GetterUngatedPostfix(key)), PatchSafety.PatchKind.Postfix),
            _ => throw new ArgumentException("unknown gates shape " + shape),
        };
    }

    // --- Emitting ---

    private static ModuleBuilder? _module;
    private static int _serial;

    /// <summary>A public static method in a class of its own (no initializer unless <paramref name="initializer"/> emits one).</summary>
    private static MethodInfo Emit(string name, Type returns, (Type Type, string Name)[] parameters, Action<ILGenerator> body,
        Action<ILGenerator, TypeBuilder>? initializer = null, bool beforeFieldInit = true, Action<TypeBuilder>? members = null, Action<TypeBuilder, ILGenerator>? withType = null)
    {
        lock (Shapes)
        {
            if (_module == null)
            {
                var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BdoForeignShapesGates"), AssemblyBuilderAccess.Run);
                _module = assembly.DefineDynamicModule("BdoForeignShapesGates");
            }
            TypeAttributes attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class
                | (beforeFieldInit ? TypeAttributes.BeforeFieldInit : 0);
            TypeBuilder type = _module.DefineType($"BdoGates.{name.Replace('-', '_')}_{++_serial}", attributes);
            members?.Invoke(type);
            if (initializer != null)
            {
                ConstructorBuilder cctor = type.DefineTypeInitializer();
                initializer(cctor.GetILGenerator(), type);
            }
            MethodBuilder method = type.DefineMethod("Patch", MethodAttributes.Public | MethodAttributes.Static, returns, parameters.Select(p => p.Type).ToArray());
            for (int i = 0; i < parameters.Length; i++)
            {
                method.DefineParameter(i + 1, ParameterAttributes.None, parameters[i].Name);
            }
            ILGenerator code = method.GetILGenerator();
            body(code);
            withType?.Invoke(type, code);
            return type.CreateType()!.GetMethod("Patch")!;
        }
    }

    private static readonly MethodInfo HitMethod = typeof(GatesCounter).GetMethod(nameof(GatesCounter.Hit))!;

    private static void EmitHit(ILGenerator il, string key)
    {
        il.Emit(OpCodes.Ldstr, key);
        il.Emit(OpCodes.Call, HitMethod);
    }

    private static readonly (Type, string)[] ExceptionOnly = { (typeof(Exception), "__exception") };

    // --- F1: finalizers under TranspiledOnly ---

    /// <summary>Counts its calls, then <c>ldarg.0; ret</c> (Release IL of <c>return __exception;</c>): stands.</summary>
    private static MethodInfo PassThroughRelease(string key) => Emit("passthrough_release", typeof(Exception), ExceptionOnly, il =>
    {
        EmitHit(il, key);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>Roslyn's Debug IL of a counting <c>return __exception;</c>: through a local and a jump to the exit. Stands.</summary>
    private static MethodInfo PassThroughDebug(string key) => Emit("passthrough_debug", typeof(Exception), ExceptionOnly, il =>
    {
        il.DeclareLocal(typeof(Exception));
        Label exit = il.DefineLabel();
        il.Emit(OpCodes.Nop);
        EmitHit(il, key);
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Br_S, exit);
        il.MarkLabel(exit);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>Counts, returns null (KitLib's swallow): backs off.</summary>
    private static MethodInfo SwallowFinalizer(string key) => Emit("swallow", typeof(Exception), ExceptionOnly, il =>
    {
        EmitHit(il, key);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>
    /// <c>return Swallow ? null : __exception;</c> with the two values merged into one <c>ret</c> after <c>ldarg __exception</c>
    /// (the verification's counter-example to "every ret follows ldarg __exception"): backs off.
    /// </summary>
    private static MethodInfo TernaryFinalizer(string key) => Emit("ternary", typeof(Exception), ExceptionOnly, il =>
    {
        Label passOn = il.DefineLabel(), exit = il.DefineLabel();
        EmitHit(il, key);
        il.Emit(OpCodes.Ldsfld, typeof(GatesCounter).GetField(nameof(GatesCounter.Swallow))!);
        il.Emit(OpCodes.Brfalse_S, passOn);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Br_S, exit);
        il.MarkLabel(passOn);
        il.Emit(OpCodes.Ldarg_0);
        il.MarkLabel(exit);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>A local read on a path that never stored it (null there): backs off.</summary>
    private static MethodInfo UnwrittenLocalFinalizer() => Emit("unwritten_local", typeof(Exception), ExceptionOnly, il =>
    {
        il.DeclareLocal(typeof(Exception));
        Label read = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse_S, read);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc_0);
        il.MarkLabel(read);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);
    });

    /// <summary><c>__exception = null; return __exception;</c> (starg): backs off.</summary>
    private static MethodInfo StargFinalizer() => Emit("starg", typeof(Exception), ExceptionOnly, il =>
    {
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Starg_S, (byte)0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>The returned local stored with something else once (null): backs off.</summary>
    private static MethodInfo OtherStoreFinalizer() => Emit("other_store", typeof(Exception), ExceptionOnly, il =>
    {
        il.DeclareLocal(typeof(Exception));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>A void finalizer (Harmony rethrows what it caught): stands.</summary>
    public static void VoidFinalizer(Exception? __exception)
    {
    }

    /// <summary>Returns an exception it doesn't take: backs off.</summary>
    public static Exception? NoArgumentFinalizer() => null;

    /// <summary>Returns <c>__exception</c> but inside a try block (exception clauses): backs off.</summary>
    public static Exception? TryFinalizer(Exception? __exception)
    {
        try
        {
            Hit("gates try-finalizer");
        }
        catch (InvalidOperationException)
        {
            Hit("gates try-finalizer caught");
        }
        return __exception;
    }

    // --- E: the type gates' path evaluated (Roslyn's Debug IL, emitted) ---

    private static readonly (Type, string)[] InstanceOnly = { (typeof(AbstractModel), "__instance") };

    /// <summary><c>ldarg.0; isinst T; ldnull; cgt.un</c>: Roslyn's Debug test of <c>__instance is T</c>.</summary>
    private static void EmitIs(ILGenerator il, Type type, int argument = 0)
    {
        il.Emit(argument == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, type);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Cgt_Un);
    }

    /// <summary>Debug IL of <c>if (__instance is Squeeze) { Hit(key); }</c>: the test through a bool local. Stands, gated on Squeeze.</summary>
    private static MethodInfo DebugGatedPostfix(string key) => Emit("debug_gated_postfix", typeof(void), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        Label end = il.DefineLabel();
        il.Emit(OpCodes.Nop);
        EmitIs(il, typeof(Squeeze));
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, end);
        il.Emit(OpCodes.Nop);
        EmitHit(il, key);
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Nop);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>
    /// Debug IL of <c>if (!(__instance is Squeeze)) return true; Hit(key); return true;</c>: the negation as <c>ldc.i4.0; ceq</c>,
    /// both returns through a local and a jump. Stands, gated on Squeeze.
    /// </summary>
    private static MethodInfo DebugGatedPrefix(string key) => Emit("debug_gated_prefix", typeof(bool), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        il.DeclareLocal(typeof(bool));
        Label work = il.DefineLabel(), exit = il.DefineLabel();
        il.Emit(OpCodes.Nop);
        EmitIs(il, typeof(Squeeze));
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, work);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Br_S, exit);
        il.MarkLabel(work);
        EmitHit(il, key);
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Br_S, exit);
        il.MarkLabel(exit);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ret);
    });

    /// <summary><c>if (__instance is Squeeze | __instance is RelicModel) Hit(key);</c> (a non-short-circuit <c>or</c>, YutaMod's): gated on both.</summary>
    private static MethodInfo BitOrGatedPostfix(string key) => Emit("bitor_gated_postfix", typeof(void), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        Label end = il.DefineLabel();
        EmitIs(il, typeof(Squeeze));
        EmitIs(il, typeof(RelicModel));
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, end);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>BL: <c>bool hit = false; if (__instance is Squeeze) hit = true; if (hit) Hit(key);</c>: stands, gated on Squeeze.</summary>
    private static MethodInfo BoolLocalPostfix(string key) => Emit("bool_local_postfix", typeof(void), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        Label join = il.DefineLabel(), end = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(Squeeze));
        il.Emit(OpCodes.Brfalse_S, join);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc_0);
        il.MarkLabel(join);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, end);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    private static readonly (Type, string)[] InstanceAndOther = { (typeof(AbstractModel), "__instance"), (typeof(object), "card") };

    /// <summary>C: a copy prologue (<c>ldarg.1; stloc.0</c>, SynergyMod's) before the gate: stands, gated on Squeeze.</summary>
    private static MethodInfo CopyProloguePostfix(string key) => Emit("copy_prologue_postfix", typeof(void), InstanceAndOther, il =>
    {
        il.DeclareLocal(typeof(object));
        Label end = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(Squeeze));
        il.Emit(OpCodes.Brfalse_S, end);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Pop);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    private static readonly (Type, string)[] PassthroughParameters = { (typeof(bool), "__result"), (typeof(AbstractModel), "__instance") };

    /// <summary>
    /// A passthrough postfix (Debug IL): <c>if (__instance is Squeeze) { Hit(key); return false; } return __result;</c>, both
    /// returns through a local. Stands, gated on Squeeze; with <paramref name="constant"/> the no-op path returns true instead of
    /// <c>__result</c> (a control: it changes a false result); with <paramref name="name"/> the first parameter has another name.
    /// </summary>
    private static MethodInfo PassthroughPostfix(string key, bool constant = false, string name = "__result") =>
        Emit("passthrough_postfix", typeof(bool), new[] { (typeof(bool), name), (typeof(AbstractModel), "__instance") }, il =>
        {
            il.DeclareLocal(typeof(bool));
            il.DeclareLocal(typeof(bool));
            Label keep = il.DefineLabel(), exit = il.DefineLabel();
            EmitIs(il, typeof(Squeeze), 1);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Brfalse_S, keep);
            EmitHit(il, key);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Br_S, exit);
            il.MarkLabel(keep);
            il.Emit(constant ? OpCodes.Ldc_I4_1 : OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc_1);
            il.Emit(OpCodes.Br_S, exit);
            il.MarkLabel(exit);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Ret);
        });

    /// <summary>Debug IL of the name test <c>if (__instance.GetType().Name != "Squeeze") return; Hit(key);</c>: a name gate.</summary>
    private static MethodInfo DebugNameGatedPostfix(string key) => Emit("debug_name_gated_postfix", typeof(void), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        Label work = il.DefineLabel(), end = il.DefineLabel();
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(object).GetMethod(nameof(GetType))!);
        il.Emit(OpCodes.Callvirt, typeof(MemberInfo).GetProperty(nameof(MemberInfo.Name))!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "Squeeze");
        il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Inequality", new[] { typeof(string), typeof(string) })!);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, work);
        il.Emit(OpCodes.Br_S, end);
        il.MarkLabel(work);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>The runtime control of rule E: a count of every call before the Debug gate (counted with this mod on or off alike).</summary>
    private static MethodInfo CallFirstPostfix(string key) => Emit("call_first_postfix", typeof(void), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        Label end = il.DefineLabel();
        EmitHit(il, key + " every call");
        EmitIs(il, typeof(Squeeze));
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, end);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>Controls of rule E, each refused for its own reason (see the self-test's names).</summary>
    private static MethodInfo GateControl(string kind) => kind switch
    {
        // A branch on another argument (unknown) before the gate.
        "unknown-branch" => Emit("unknown_branch", typeof(void), new[] { (typeof(AbstractModel), "__instance"), (typeof(bool), "flag") }, il =>
        {
            Label end = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Brtrue_S, end);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, typeof(Squeeze));
            il.Emit(OpCodes.Brfalse_S, end);
            EmitHit(il, "control");
            il.MarkLabel(end);
            il.Emit(OpCodes.Ret);
        }),
        // A local never stored on the path, branched on (its zero isn't assumed).
        "unwritten-local" => Emit("unwritten_local", typeof(void), InstanceOnly, il =>
        {
            il.DeclareLocal(typeof(bool));
            Label end = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, typeof(Squeeze));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Brtrue_S, end);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(end);
            EmitHit(il, "control");
            il.Emit(OpCodes.Ret);
        }),
        // A test against a value type.
        "valuetype-gate" => Emit("valuetype_gate", typeof(void), InstanceOnly, il =>
        {
            Label end = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, typeof(int));
            il.Emit(OpCodes.Brfalse_S, end);
            EmitHit(il, "control");
            il.MarkLabel(end);
            il.Emit(OpCodes.Ret);
        }),
        // A call before the gate.
        "call-first" => Emit("call_first", typeof(void), InstanceOnly, il =>
        {
            Label end = il.DefineLabel();
            EmitHit(il, "control");
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, typeof(Squeeze));
            il.Emit(OpCodes.Brfalse_S, end);
            EmitHit(il, "control");
            il.MarkLabel(end);
            il.Emit(OpCodes.Ret);
        }),
        // A loop back to the test.
        "loop" => Emit("loop", typeof(void), InstanceOnly, il =>
        {
            Label top = il.DefineLabel();
            il.MarkLabel(top);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, typeof(Squeeze));
            il.Emit(OpCodes.Brfalse_S, top);
            il.Emit(OpCodes.Ret);
        }),
        // A bool prefix returning false for every other listener (skips the original).
        "false-prefix" => Emit("false_prefix", typeof(bool), InstanceOnly, il =>
        {
            Label gated = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, typeof(Squeeze));
            il.Emit(OpCodes.Brtrue_S, gated);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(gated);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        }),
        // A bool prefix returning (gate | 2): nonzero but not 1 (an or with a nonzero constant isn't taken as true).
        "or-two-prefix" => Emit("or_two_prefix", typeof(bool), InstanceOnly, il =>
        {
            EmitIs(il, typeof(Squeeze));
            il.Emit(OpCodes.Ldc_I4_2);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Ret);
        }),
        _ => throw new ArgumentException(kind),
    };

    /// <summary>NullSkipped through rule E: Debug IL of <c>if (__result != null) Hit(key);</c>, by value or through <c>ref __result</c>.</summary>
    private static MethodInfo DebugNullGatedPostfix(string key, bool byRef, bool callFirst = false) =>
        Emit("debug_null_gated", typeof(void), new[] { (byRef ? typeof(object).MakeByRefType() : typeof(object), "__result") }, il =>
        {
            il.DeclareLocal(typeof(bool));
            Label end = il.DefineLabel();
            if (callFirst)
            {
                EmitHit(il, key);
            }
            il.Emit(OpCodes.Ldarg_0);
            if (byRef)
            {
                il.Emit(OpCodes.Ldind_Ref);
            }
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Cgt_Un);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Brfalse_S, end);
            EmitHit(il, key);
            il.MarkLabel(end);
            il.Emit(OpCodes.Ret);
        });

    // --- G: a trivial static getter as a branch condition, both sides explored ---

    private static readonly MethodInfo ConfigGetter = typeof(GatesCounter).GetProperty(nameof(GatesCounter.Config))!.GetGetMethod()!;

    /// <summary>Installing a getter shape turns the flag on (both sides of its branch then run in game, the gated one counting).</summary>
    private static MethodInfo FlagOn(MethodInfo patch)
    {
        GatesCounter.Flag = true;
        return patch;
    }

    /// <summary>
    /// ZZZLib's shape: <c>if (!GatesCounter.Config) return; if (__instance is Squeeze) Hit(key);</c>, the flag read through a
    /// trivial getter (Debug IL: <c>ldc.i4.0; ceq</c> through a local). Both sides are no-ops for other listeners: stands, gated
    /// on Squeeze. With <paramref name="getter"/> another getter.
    /// </summary>
    private static MethodInfo GetterGatedPostfix(string key, MethodInfo? getter = null) => Emit("getter_gated_postfix", typeof(void), InstanceOnly, il =>
    {
        il.DeclareLocal(typeof(bool));
        il.DeclareLocal(typeof(bool));
        Label gate = il.DefineLabel(), end = il.DefineLabel();
        il.Emit(OpCodes.Call, getter ?? ConfigGetter);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, gate);
        il.Emit(OpCodes.Br_S, end);
        il.MarkLabel(gate);
        EmitIs(il, typeof(Squeeze));
        il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Brfalse_S, end);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>The control: <c>if (GatesCounter.Config || __instance is Squeeze) Hit(key);</c>: with the flag on it acts for every listener.</summary>
    private static MethodInfo GetterUngatedPostfix(string key) => Emit("getter_ungated_postfix", typeof(void), InstanceOnly, il =>
    {
        Label work = il.DefineLabel(), end = il.DefineLabel();
        il.Emit(OpCodes.Call, ConfigGetter);
        il.Emit(OpCodes.Brtrue_S, work);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(Squeeze));
        il.Emit(OpCodes.Brfalse_S, end);
        il.MarkLabel(work);
        EmitHit(il, key);
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    /// <summary>
    /// The control: the gated shape, its flag read by a getter of its own class whose initializer reads the clock (beforefieldinit):
    /// when it runs matters.
    /// </summary>
    private static MethodInfo GetterImpureClass() => Emit("getter_impure_class", typeof(void), InstanceOnly, il => { }, null, true, null, (type, il) =>
    {
        FieldBuilder flag = type.DefineField("Flag", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
        ConstructorBuilder cctor = type.DefineTypeInitializer();
        ILGenerator init = cctor.GetILGenerator();
        init.Emit(OpCodes.Call, typeof(Environment).GetProperty(nameof(Environment.TickCount64))!.GetGetMethod()!);
        init.Emit(OpCodes.Ldc_I4_0);
        init.Emit(OpCodes.Conv_I8);
        init.Emit(OpCodes.Cgt);
        init.Emit(OpCodes.Stsfld, flag);
        init.Emit(OpCodes.Ret);
        MethodBuilder get = type.DefineMethod("get_Flag", MethodAttributes.Public | MethodAttributes.Static, typeof(bool), Type.EmptyTypes);
        ILGenerator g = get.GetILGenerator();
        g.Emit(OpCodes.Ldsfld, flag);
        g.Emit(OpCodes.Ret);
        Label end = il.DefineLabel();
        il.Emit(OpCodes.Call, get);
        il.Emit(OpCodes.Brfalse_S, end);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(Squeeze));
        il.Emit(OpCodes.Brfalse_S, end);
        EmitHit(il, "control");
        il.MarkLabel(end);
        il.Emit(OpCodes.Ret);
    });

    // --- The self-test ---

    private readonly record struct Case(string Name, MethodInfo Patch, PatchSafety.PatchKind Kind, MethodBase Original, PatchTolerance Tolerance,
        bool Stands, string? Gates = null);

    private static IEnumerable<Case> Cases()
    {
        MethodBase hook = AccessTools.Method("MegaCrit.Sts2.Core.Hooks.Hook:ModifyHpLost");
        const PatchSafety.PatchKind finalizer = PatchSafety.PatchKind.Finalizer;
        MethodInfo Own(string name) => typeof(ForeignShapesGates).GetMethod(name)!;
        // F1.
        yield return new Case("F1 void finalizer", Own(nameof(VoidFinalizer)), finalizer, hook, PatchTolerance.TranspiledOnly, true);
        yield return new Case("F1 ldarg __exception; ret", PassThroughRelease("selftest"), finalizer, hook, PatchTolerance.TranspiledOnly, true);
        yield return new Case("F1 Debug pass-through through a local", PassThroughDebug("selftest"), finalizer, hook, PatchTolerance.TranspiledOnly, true);
        yield return new Case("F1 control: returns null", SwallowFinalizer("selftest"), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: merged ret (c ? null : __exception)", TernaryFinalizer("selftest"), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: local read before any store", UnwrittenLocalFinalizer(), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: starg __exception", StargFinalizer(), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: returned local stored with null", OtherStoreFinalizer(), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: no __exception parameter", Own(nameof(NoArgumentFinalizer)), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: exception clauses", Own(nameof(TryFinalizer)), finalizer, hook, PatchTolerance.TranspiledOnly, false);
        yield return new Case("F1 control: a pass-through finalizer under SurroundingOnly", PassThroughRelease("selftest"), finalizer, hook, PatchTolerance.SurroundingOnly, false);
        yield return new Case("F1 control: a pass-through finalizer under PrefixesOnly (HookFilter's mirror)", PassThroughRelease("selftest"), finalizer, hook, PatchTolerance.PrefixesOnly, false);
        // E: Debug IL gates on an AbstractModel default (the instance is the listener).
        MethodBase model = AccessTools.Method(typeof(AbstractModel), nameof(AbstractModel.AfterCardPlayed));
        const PatchSafety.PatchKind postfix = PatchSafety.PatchKind.Postfix, prefix = PatchSafety.PatchKind.Prefix;
        const PatchTolerance gated = PatchTolerance.TypeGated;
        yield return new Case("E Debug gated postfix (bool local)", DebugGatedPostfix("selftest"), postfix, model, gated, true, "Squeeze");
        yield return new Case("E Debug gated bool prefix (ceq negation, returns through a local)", DebugGatedPrefix("selftest"), prefix, model, gated, true, "Squeeze");
        yield return new Case("E non-short-circuit or of two tests", BitOrGatedPostfix("selftest"), postfix, model, gated, true, "RelicModel|Squeeze");
        yield return new Case("E (BL) a bool set as a constant before the branch", BoolLocalPostfix("selftest"), postfix, model, gated, true, "Squeeze");
        yield return new Case("E (C) a copy prologue before the gate", CopyProloguePostfix("selftest"), postfix, model, gated, true, "Squeeze");
        yield return new Case("E passthrough postfix returning its __result", PassthroughPostfix("selftest"), postfix, model, gated, true, "Squeeze");
        if (PatchSafety.TypesNamedForTest(false, "Squeeze") is { Length: > 0 })
        {
            yield return new Case("E Debug name test", DebugNameGatedPostfix("selftest"), postfix, model, gated, true, "Squeeze");
        }
        yield return new Case("E control: passthrough returning a constant", PassthroughPostfix("selftest", constant: true), postfix, model, gated, false);
        yield return new Case("E control: passthrough's first parameter not named __result", PassthroughPostfix("selftest", name: "result"), postfix, model, gated, false);
        yield return new Case("E control: a branch on another argument", GateControl("unknown-branch"), postfix, model, gated, false);
        yield return new Case("E control: a branch on a local never stored", GateControl("unwritten-local"), postfix, model, gated, false);
        yield return new Case("E control: a value-type gate", GateControl("valuetype-gate"), postfix, model, gated, false);
        yield return new Case("E control: a call before the gate", GateControl("call-first"), postfix, model, gated, false);
        yield return new Case("E control: a loop", GateControl("loop"), postfix, model, gated, false);
        yield return new Case("E control: a bool prefix returning false for other listeners", GateControl("false-prefix"), prefix, model, gated, false);
        yield return new Case("E control: a bool prefix returning (gate | 2)", GateControl("or-two-prefix"), prefix, model, gated, false);
        yield return new Case("E control: a Debug gated postfix under SurroundingOnly is judged by its parameters only", DebugGatedPostfix("selftest"), postfix, model, PatchTolerance.SurroundingOnly, true);
        // E in NullSkipped (NullResultGated).
        yield return new Case("E NullSkipped: Debug null test by value", DebugNullGatedPostfix("selftest", byRef: false), postfix, hook, PatchTolerance.NullSkipped, true);
        yield return new Case("E NullSkipped: Debug null test through ref __result", DebugNullGatedPostfix("selftest", byRef: true), postfix, hook, PatchTolerance.NullSkipped, true);
        yield return new Case("E NullSkipped control: a call before the null test", DebugNullGatedPostfix("selftest", byRef: false, callFirst: true), postfix, hook, PatchTolerance.NullSkipped, false);
        // G.
        yield return new Case("G a trivial static getter as the branch, both sides no-ops for other listeners", GetterGatedPostfix("selftest"), postfix, model, gated, true, "Squeeze");
        yield return new Case("G control: the getter's true side acts for every listener", GetterUngatedPostfix("selftest"), postfix, model, gated, false);
        yield return new Case("G control: a getter reading two fields (not trivial)", GetterGatedPostfix("selftest", typeof(GatesCounter).GetProperty(nameof(GatesCounter.ConfigBoth))!.GetGetMethod()), postfix, model, gated, false);
        yield return new Case("G control: a getter whose class initializer reads the clock", GetterImpureClass(), postfix, model, gated, false);
    }

    /// <summary>
    /// <c>foreignpatch gates selftest</c> (also run offline by a harness): every case's verdict from <see cref="PatchSafety.Intolerable"/>
    /// on a synthetic patch list holding that one patch, against the expected one. Throws on the first list of mismatches.
    /// </summary>
    public static IReadOnlyList<string> SelfTest()
    {
        var lines = new List<string>();
        var failures = new List<string>();
        foreach (Case c in Cases())
        {
            var patch = new Patch(c.Patch, 0, ForeignPatches.IdPrefix + "gates-selftest", Priority.Normal, null, null, false);
            Patch[] one = { patch }, none = Array.Empty<Patch>();
            var info = new HarmonyLib.Patches(c.Kind == PatchSafety.PatchKind.Prefix ? one : none, c.Kind == PatchSafety.PatchKind.Postfix ? one : none,
                none, c.Kind == PatchSafety.PatchKind.Finalizer ? one : none, none, none);
            var gates = new List<Type>();
            string? why = PatchSafety.Intolerable(c.Original, info, patch, c.Kind, c.Tolerance, gates);
            string found = string.Join("|", gates.Select(g => g.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal));
            bool ok = (why == null) == c.Stands && (c.Gates == null || c.Gates == found);
            string verdict = why == null ? "stands" + (found.Length > 0 ? " gated on " + found : "") : "backs off (" + why + ")";
            lines.Add($"FOREIGN gates selftest {(ok ? "ok" : "WRONG")}: {c.Name}: {verdict}");
            if (!ok)
            {
                failures.Add($"{c.Name}: {verdict}, expected {(c.Stands ? "stands" + (c.Gates != null ? " gated on " + c.Gates : "") : "a back-off")}");
            }
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("gates self-test: " + string.Join(" | ", failures) + " || " + string.Join(" || ", lines));
        }
        lines.Add($"FOREIGN gates selftest: {lines.Count} cases as expected");
        return lines;
    }
}

/// <summary>
/// What the emitted shapes call and read (public: the dynamic assembly calls it). No static initializer, so a gated path reaching
/// it is judged for its own shape only (InitializersFree).
/// </summary>
public static class GatesCounter
{
    /// <summary>A STATE line of the foreign report (IlJudgeShapes' counts).</summary>
    public static void Hit(string key) => IlJudgeShapes.Count(key);

    /// <summary>A mod's config flag (set at run time: the gate rules can't know it). No initializer.</summary>
    public static bool Flag;

    /// <summary>A trivial static getter of it (<c>ldsfld; ret</c>): rule G's branch condition.</summary>
    public static bool Config => Flag;

    /// <summary>Not trivial (two fields read): a branch on it is refused.</summary>
    public static bool ConfigBoth => Flag && Swallow;

    /// <summary>Read by the ternary finalizer (a value the judge can't know).</summary>
    public static bool Swallow;
}
