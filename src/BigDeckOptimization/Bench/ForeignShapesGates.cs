using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using HarmonyLib;

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
    };

    /// <summary>Whether a <c>foreignpatch</c> line is one of this file's.</summary>
    public static bool Handles(string[] parts) => parts.Length >= 2 && (parts[0] == "gates" || Shapes.Contains(parts[0]));

    /// <summary>Counted by the emitted shapes (public: the dynamic assembly calls it): a STATE line of the foreign report.</summary>
    public static void Hit(string key) => IlJudgeShapes.Count(key);

    /// <summary>Read by the ternary finalizer (a value the judge can't know).</summary>
    public static bool Swallow;

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
            _ => throw new ArgumentException("unknown gates shape " + shape),
        };
    }

    // --- Emitting ---

    private static ModuleBuilder? _module;
    private static int _serial;

    /// <summary>A public static method in a class of its own (no initializer unless <paramref name="initializer"/> emits one).</summary>
    private static MethodInfo Emit(string name, Type returns, (Type Type, string Name)[] parameters, Action<ILGenerator> body,
        Action<ILGenerator, TypeBuilder>? initializer = null, bool beforeFieldInit = true, Action<TypeBuilder>? members = null)
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
            body(method.GetILGenerator());
            return type.CreateType()!.GetMethod("Patch")!;
        }
    }

    private static readonly MethodInfo HitMethod = typeof(ForeignShapesGates).GetMethod(nameof(Hit))!;

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
        il.Emit(OpCodes.Ldsfld, typeof(ForeignShapesGates).GetField(nameof(Swallow))!);
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
