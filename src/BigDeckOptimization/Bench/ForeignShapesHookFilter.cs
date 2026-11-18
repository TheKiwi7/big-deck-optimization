using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic patches of "another mod" for HookFilter's mirrors (DESIGN "Re-verification of the leads (2026-11-18)", rule X:
/// BeforeReplace on the mirrored hooks), through <c>foreignpatch &lt;shape&gt; &lt;Type:Method&gt; [gate] [priority N]</c>:
/// <list type="bullet">
/// <item><c>hf-ref-write-postfix</c> (control): a postfix writing an out or ref argument back (the same value) on a mirrored
/// method: <c>Hook.ShouldPlay</c>'s <c>preventer</c>, the cost color helpers' <c>hookModifiedCost</c>. A by-reference write is
/// refused: the mirror must run the original there.</item>
/// </list>
/// <c>foreignpatch hookfilter throwonce</c> (in combat, with a listener overriding <c>TryModifyEnergyCostInCombat</c>, e.g. Spiked
/// Gauntlets): a listener throwing once inside the energy cost mirror. Its override gets a prefix emitted into a dynamic
/// assembly (not this mod's: the exception is the listener's own) that throws at the call reaching the last listener of the
/// filtered list, and the hook a counting postfix of "another mod". The hook is called inside a refresh scope (the mirror) and
/// through <see cref="ListenerCache.RunVanilla{T}"/> (the game's loop): both must throw the same exception after the same
/// calls of that override, with the postfix never run, the mirror having served its list, and HookFilter neither faulted nor
/// off. A mirror that ran the game's hook again after the throw would call the earlier listeners twice and return a value.
/// </summary>
public static class ForeignShapesHookFilter
{
    public static readonly string[] Shapes = { "hf-ref-write-postfix" };

    /// <summary>Whether this file runs the <c>foreignpatch</c> line (one of its shapes, or its <c>hookfilter</c> self-tests).</summary>
    public static bool Handles(string[] parts) => parts.Length >= 2 && (Shapes.Contains(parts[0]) || parts[0] == "hookfilter");

    /// <summary>Runs the line; <paramref name="installed"/> registers what it patched for <c>foreignpatch remove</c>.</summary>
    public static IReadOnlyList<string> Run(string[] parts, Action<string, MethodBase> installed)
    {
        if (parts[0] == "hookfilter")
        {
            return parts[1] switch
            {
                "throwonce" => ThrowOnce(),
                _ => throw new ArgumentException("foreignpatch hookfilter throwonce"),
            };
        }
        string shape = parts[0];
        MethodBase target = AccessTools.Method(parts[1]) ?? throw new ArgumentException("no method " + parts[1] + " (Namespace.Type:Method)");
        int priority = Priority.Normal;
        int at = Array.IndexOf(parts, "priority");
        if (at >= 0 && at + 1 < parts.Length)
        {
            priority = int.Parse(parts[at + 1]);
        }
        string gate = parts.Length > 2 && parts[2] != "priority" ? parts[2] : "";
        string id = ForeignPatches.IdPrefix + shape + (gate.Length > 0 ? "-" + gate : "");
        var harmony = new Harmony(id);
        HarmonyMethod Fix(string name) => new(typeof(ForeignShapesHookFilter), name) { priority = priority };
        switch (shape)
        {
            case "hf-ref-write-postfix":
                harmony.Patch(target, postfix: Fix(target.Name switch
                {
                    "ShouldPlay" => nameof(PreventerWritePostfix),
                    "TryModifyEnergyCostWithHooks" or "TryModifyStarCostWithHooks" => nameof(CostWritePostfix),
                    _ => throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Hooks.Hook:ShouldPlay or CardCostHelper:TryModify*CostWithHooks"),
                }));
                break;
        }
        installed(id, target);
        var lines = new List<string> { $"FOREIGN installed {shape}{(gate.Length > 0 ? " " + gate : "")} on {Name(target)} as {id} (priority {priority})" };
        lines.AddRange(Classification(target));
        return lines;
    }

    private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";

    private static IEnumerable<string> Classification(MethodBase target) =>
        PatchSafety.Describe(target).Select(line => $"FOREIGN classify {Name(target)}: {line}").DefaultIfEmpty($"FOREIGN classify {Name(target)}: no other mod's patch");

    private static void PreventerWritePostfix(ref AbstractModel? preventer)
    {
        AbstractModel? same = preventer;
        preventer = same;
    }

    private static void CostWritePostfix(ref decimal hookModifiedCost)
    {
        decimal same = hookModifiedCost;
        hookModifiedCost = same;
    }

    // --- hookfilter throwonce ---

    private static long _postfixCalls;

    private static void CountingPostfix() => _postfixCalls++;

    /// <summary>A static class in a new dynamic assembly: <c>Calls</c>, <c>ThrowAt</c>, and a prefix counting calls that throws once at <c>ThrowAt</c>.</summary>
    private static Type EmitThrower()
    {
        string unique = "BdoThrowOnce" + Environment.TickCount64.ToString("X");
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(unique), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule(unique);
        TypeBuilder type = module.DefineType(unique, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        FieldBuilder calls = type.DefineField("Calls", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        FieldBuilder throwAt = type.DefineField("ThrowAt", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        MethodBuilder prefix = type.DefineMethod("Prefix", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        ILGenerator il = prefix.GetILGenerator();
        Label done = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, calls);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, calls);
        il.Emit(OpCodes.Ldsfld, calls);
        il.Emit(OpCodes.Ldsfld, throwAt);
        il.Emit(OpCodes.Bne_Un_S, done);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stsfld, throwAt);
        il.Emit(OpCodes.Ldstr, "bdtest listener throws once");
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(done);
        il.Emit(OpCodes.Ret);
        return type.CreateType()!;
    }

    /// <summary>The most-derived implementation of <paramref name="baseMethod"/> on <paramref name="type"/> (null: the default).</summary>
    private static MethodInfo? Override(Type type, MethodInfo baseMethod)
    {
        MethodInfo? found = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == baseMethod.Name && m.GetBaseDefinition() == baseMethod);
        return found == null || found.DeclaringType == typeof(AbstractModel) ? null : found;
    }

    private static IReadOnlyList<string> ThrowOnce()
    {
        if (CombatManager.Instance.DebugOnlyGetState() is not { } combat)
        {
            throw new InvalidOperationException("hookfilter throwonce: start a combat first");
        }
        var lines = new List<string>();
        // The energy cost mirror's two lists, early then late: the first with a listener is used.
        foreach ((int hook, string method) in new[] { (HookFilter.EnergyCost, "TryModifyEnergyCostInCombat"), (HookFilter.EnergyCostLate, "TryModifyEnergyCostInCombatLate") })
        {
            MethodInfo baseMethod = Reflect.Method(typeof(AbstractModel), method) ?? throw new MissingMethodException(nameof(AbstractModel), method);
            List<AbstractModel>? listeners = null;
            CardModel? card = null;
            bool safe = false;
            ListenerCache.InvokeStateChangedInScope(_ =>
            {
                safe = HookFilter.HookSafe(HookFilter.EnergyCost) && HookFilter.HookSafe(HookFilter.EnergyCostLate);
                listeners = HookFilter.ForCombat(combat, hook);
                card = ListenerCache.GetListeners(combat).OfType<CardModel>().FirstOrDefault();
            }, combat);
            if (!safe || listeners == null || card == null)
            {
                lines.Add($"FOREIGN hookfilter throwonce: {method} not mirrored here (safe {safe}, list {(listeners == null ? "none" : "built")}, card {card != null})");
                continue;
            }
            if (listeners.Count == 0)
            {
                lines.Add($"FOREIGN hookfilter throwonce: no listener overrides {method} in this combat");
                continue;
            }
            Type thrower = listeners[^1].GetType();
            MethodInfo target = Override(thrower, baseMethod) ?? throw new InvalidOperationException($"{thrower.Name} kept in {method}'s list without overriding it");
            int reaching = listeners.Count(l => Override(l.GetType(), baseMethod) == target);
            lines.Add(Compare(combat, card, method, target, thrower, reaching, listeners.Count));
            return lines;
        }
        lines.Add("FOREIGN hookfilter throwonce: not run (no mirrored energy cost list with a listener: add one, e.g. cmd relic add SPIKED_GAUNTLETS)");
        return lines;
    }

    private static string Compare(CombatState combat, CardModel card, string method, MethodInfo target, Type thrower, int reaching, int count)
    {
        Type emitted = EmitThrower();
        FieldInfo calls = emitted.GetField("Calls")!;
        FieldInfo throwAt = emitted.GetField("ThrowAt")!;
        string listenerId = ForeignPatches.IdPrefix + "hf-throwonce-listener";
        string postfixId = ForeignPatches.IdPrefix + "hf-throwonce-postfix";
        MethodInfo hook = Reflect.Method(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat))!;
        new Harmony(listenerId).Patch(target, prefix: new HarmonyMethod(emitted.GetMethod("Prefix")!));
        new Harmony(postfixId).Patch(hook, postfix: new HarmonyMethod(typeof(ForeignShapesHookFilter), nameof(CountingPostfix)));
        try
        {
            (string? Thrown, int Calls, long Postfix, long Served) Call(bool mirror)
            {
                calls.SetValue(null, 0);
                throwAt.SetValue(null, reaching);
                long postfixBefore = _postfixCalls;
                long servedBefore = HookFilter.Served;
                string? thrown = "none";
                void Invoke()
                {
                    try
                    {
                        Hook.ModifyEnergyCostInCombat(combat, card, 1m);
                    }
                    catch (Exception e)
                    {
                        thrown = $"{e.GetType().Name}: {e.Message}";
                    }
                }
                if (mirror)
                {
                    ListenerCache.InvokeStateChangedInScope(_ => Invoke(), combat);
                }
                else
                {
                    ListenerCache.RunVanilla(() =>
                    {
                        Invoke();
                        return 0;
                    });
                }
                throwAt.SetValue(null, 0);
                return (thrown, (int)calls.GetValue(null)!, _postfixCalls - postfixBefore, HookFilter.Served - servedBefore);
            }
            var mirrored = Call(mirror: true);
            var game = Call(mirror: false);
            bool faulted = PatchSafety.IsFaulted(HookFilter.Name);
            var problems = new List<string>();
            if (mirrored.Thrown != game.Thrown || game.Thrown == "none")
            {
                problems.Add("not the same exception");
            }
            if (mirrored.Calls != game.Calls || game.Calls != reaching)
            {
                problems.Add("not the same calls of the thrower's override");
            }
            if (mirrored.Postfix != 0 || game.Postfix != 0)
            {
                problems.Add("a postfix ran after the throw");
            }
            if (mirrored.Served == 0)
            {
                problems.Add("the mirror served no list (it didn't run)");
            }
            if (faulted || !HookFilter.Enabled)
            {
                problems.Add("HookFilter faulted or off");
            }
            return $"FOREIGN hookfilter throwonce {method}: thrower {thrower.Name} ({reaching} of {count} listeners call {target.DeclaringType?.Name}.{target.Name}); "
                + $"mirror {mirrored.Thrown}, calls {mirrored.Calls}, postfix {mirrored.Postfix}, lists served {mirrored.Served}; "
                + $"game's hook {game.Thrown}, calls {game.Calls}, postfix {game.Postfix}; HookFilter faulted {faulted}, on {HookFilter.Enabled}: "
                + (problems.Count == 0 ? "same as the game's" : "DIFFERENT (" + string.Join("; ", problems) + ")");
        }
        finally
        {
            new Harmony(listenerId).UnpatchAll(listenerId);
            new Harmony(postfixId).UnpatchAll(postfixId);
        }
    }
}
