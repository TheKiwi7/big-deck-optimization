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
/// <item><c>hf-gated-damage-postfix squeeze|relic</c> (rule R3, on <c>AbstractModel:ModifyDamageAdditive</c>): a postfix adding 1 to
/// the default's result when the listener is a Squeeze (or a relic), nothing otherwise: gated on the instance's type, so HookFilter's
/// damage lists keep those listeners (their calls reach the patched default) and every damage changes by the same amount on or off.</item>
/// </list>
/// <c>foreignpatch hookfilter synthetic &lt;Type:Method&gt;</c>: PatchSafety's verdicts on synthetic patches added to the method's real
/// patch list, nothing installed (ReplayStateSkip's and JoinMismatchNote's controls, next to an accepted patch installed for real):
/// a no-op postfix and a bool prefix at the default priority (each registered after every patch there), and a postfix and a bool
/// prefix at Priority.Last registered after this mod's (sorted after them).
/// <c>foreignpatch hookfilter gatedlists</c> (in combat): inside a refresh scope, each damage default's filtered run list against
/// the run's listeners: every listener kept overrides the default or matches this scope's gates, and every listener matching a gate
/// without overriding is kept (run again after a second gated postfix: the lists built with the first gates must not be reused).
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
    public static readonly string[] Shapes = { "hf-ref-write-postfix", "hf-gated-damage-postfix" };

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
                "walkthrow" => WalkThrow(),
                "gatedlists" => GatedLists(),
                "synthetic" when parts.Length > 2 => Synthetic(AccessTools.Method(parts[2]) ?? throw new ArgumentException("no method " + parts[2])),
                _ => throw new ArgumentException("foreignpatch hookfilter throwonce|walkthrow|gatedlists|synthetic <Type:Method>"),
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
            case "hf-gated-damage-postfix":
                if (target.DeclaringType != typeof(AbstractModel) || target.Name != nameof(AbstractModel.ModifyDamageAdditive))
                {
                    throw new ArgumentException(shape + " goes on MegaCrit.Sts2.Core.Models.AbstractModel:ModifyDamageAdditive");
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(DamageGates), gate switch
                {
                    "squeeze" => nameof(DamageGates.SqueezePostfix),
                    "relic" => nameof(DamageGates.RelicPostfix),
                    _ => throw new ArgumentException(shape + " gate: squeeze or relic"),
                }) { priority = priority });
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

    private static bool SyntheticTruePrefix() => true;

    private static void SyntheticNoOpPostfix()
    {
    }

    private static IReadOnlyList<string> Synthetic(MethodBase target)
    {
        HarmonyLib.Patches real = Harmony.GetPatchInfo(target) ?? throw new InvalidOperationException(Name(target) + " has no patch (this mod's should be there)");
        int next = real.Prefixes.Concat(real.Postfixes).Select(p => p.index).DefaultIfEmpty(-1).Max() + 1;
        MethodInfo prefix = AccessTools.Method(typeof(ForeignShapesHookFilter), nameof(SyntheticTruePrefix));
        MethodInfo postfix = AccessTools.Method(typeof(ForeignShapesHookFilter), nameof(SyntheticNoOpPostfix));
        var lines = new List<string>();
        foreach ((string label, int priority) in new[] { ("default", Priority.Normal), ("last", Priority.Last) })
        {
            var addedPrefix = new Patch(prefix, next, ForeignPatches.IdPrefix + "synthetic-bool-prefix-" + label, priority, null, null, false);
            var addedPostfix = new Patch(postfix, next + 1, ForeignPatches.IdPrefix + "synthetic-postfix-" + label, priority, null, null, false);
            var info = new HarmonyLib.Patches(real.Prefixes.Append(addedPrefix).ToArray(), real.Postfixes.Append(addedPostfix).ToArray(),
                real.Transpilers.ToArray(), real.Finalizers.ToArray(), real.InnerPrefixes.ToArray(), real.InnerPostfixes.ToArray());
            lines.AddRange(PatchSafety.Describe(target, info).Where(l => l.Contains("synthetic-", StringComparison.Ordinal))
                .Select(l => $"FOREIGN classify {Name(target)} (synthetic): {l}"));
        }
        return lines;
    }

    /// <summary>The gated damage postfixes: no static field nor initializer (a static constructor would refuse the gate).</summary>
    private static class DamageGates
    {
        public static void SqueezePostfix(AbstractModel __instance, ref decimal __result)
        {
            if (__instance is MegaCrit.Sts2.Core.Models.Cards.Squeeze)
            {
                __result += 1m;
            }
        }

        public static void RelicPostfix(AbstractModel __instance, ref decimal __result)
        {
            if (__instance is RelicModel)
            {
                __result += 1m;
            }
        }
    }

    private static IReadOnlyList<string> GatedLists()
    {
        if (CombatManager.Instance.DebugOnlyGetState() is not { } combat || MegaCrit.Sts2.Core.Runs.RunManager.Instance.DebugOnlyGetState() is not MegaCrit.Sts2.Core.Runs.RunState run)
        {
            throw new InvalidOperationException("hookfilter gatedlists: start a combat first");
        }
        var lines = new List<string>();
        foreach ((int hook, string method) in new[] { (HookFilter.DamageAdditive, nameof(AbstractModel.ModifyDamageAdditive)),
            (HookFilter.DamageMultiplicative, nameof(AbstractModel.ModifyDamageMultiplicative)), (HookFilter.DamageCap, nameof(AbstractModel.ModifyDamageCap)) })
        {
            MethodInfo baseMethod = Reflect.Method(typeof(AbstractModel), method) ?? throw new MissingMethodException(nameof(AbstractModel), method);
            List<AbstractModel>? list = null;
            Type[] gates = Type.EmptyTypes;
            IReadOnlyList<AbstractModel> all = Array.Empty<AbstractModel>();
            ListenerCache.InvokeStateChangedInScope(_ =>
            {
                list = HookFilter.ForRun(run, combat, hook);
                gates = HookFilter.ScopeGates(hook);
                all = ListenerCache.GetRunListeners(run, combat).ToList();
            }, combat);
            if (list == null)
            {
                lines.Add($"FOREIGN hookfilter gatedlists {method}: not filtered");
                continue;
            }
            bool Gated(AbstractModel l) => gates.Any(g => g.IsAssignableFrom(l.GetType()));
            var kept = new HashSet<AbstractModel>(list, ReferenceEqualityComparer.Instance);
            int stray = list.Count(l => Override(l.GetType(), baseMethod) == null && !Gated(l));
            int gateKept = list.Count(l => Override(l.GetType(), baseMethod) == null && Gated(l));
            int gateMissed = all.Count(l => Override(l.GetType(), baseMethod) == null && Gated(l) && !kept.Contains(l));
            lines.Add($"FOREIGN hookfilter gatedlists {method}: gates {(gates.Length == 0 ? "none" : string.Join("|", gates.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal)))}, "
                + $"{list.Count} of {all.Count} listeners kept, {gateKept} by a gate: "
                + (stray == 0 && gateMissed == 0 ? "as the gates say" : $"DIFFERENT ({stray} kept without reason, {gateMissed} gated ones left out)"));
        }
        return lines;
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

    // --- hookfilter walkthrow ---

    /// <summary>The step the walk postfix runs before each element it yields (the emitted thrower's prefix), null when not testing.</summary>
    private static Action? _walkStep;

    private static long _helperPostfixCalls;

    private static void HelperCountingPostfix() => _helperPostfixCalls++;

    /// <summary>A walk postfix of "another mod" running a step before each element (lazy, by value): the step throws once when armed.</summary>
    private static void WalkStepPostfix(ref IEnumerable<AbstractModel> __result)
    {
        if (_walkStep is { } step)
        {
            __result = Stepped(__result, step);
        }
    }

    private static IEnumerable<AbstractModel> Stepped(IEnumerable<AbstractModel> walk, Action step)
    {
        foreach (AbstractModel model in walk)
        {
            step();
            yield return model;
        }
    }

    /// <summary>
    /// <c>foreignpatch hookfilter walkthrow</c> (in combat, the combat walk already carrying a postfix of "another mod", e.g.
    /// walk-expand-cost-postfix): the energy cost color mirror over the game's walk (HookFilterCostColors.EnergyBesideWalks)
    /// meeting a throw from the walk's own code. A postfix on the walk runs a step emitted into a dynamic assembly (not this
    /// mod's) before each element, which throws once at a given step: in the helper's first walk, then in its second. The helper
    /// is called inside a refresh scope (the mirror) and through <see cref="ListenerCache.RunVanilla{T}"/> (the game's helper):
    /// both must throw the same exception after the same steps, with a counting postfix on the helper never run, the mirror
    /// having passed the walk's exception on (HookFilter.WalkThrowsPassedOn), and HookFilter neither faulted nor off. Before that,
    /// one call without a throw must be answered by the mirror over the game's walk (EnergyCostColorWalk). Bench checks off
    /// meanwhile (their 1-in-64 call of the game's helper would walk again).
    /// </summary>
    private static IReadOnlyList<string> WalkThrow()
    {
        if (CombatManager.Instance.DebugOnlyGetState() is not { } combat)
        {
            throw new InvalidOperationException("hookfilter walkthrow: start a combat first");
        }
        MethodInfo helper = HookFilterCostColors.EnergyCostColor ?? throw new InvalidOperationException("hookfilter walkthrow: the energy cost color helper isn't mirrored here");
        CardModel card = ListenerCache.GetListeners(combat).OfType<CardModel>().FirstOrDefault() ?? throw new InvalidOperationException("hookfilter walkthrow: no card in this combat");
        MethodInfo walk = Reflect.Method(typeof(CombatState), nameof(CombatState.IterateHookListeners)) ?? throw new MissingMethodException(nameof(CombatState), nameof(CombatState.IterateHookListeners));
        Type emitted = EmitThrower();
        FieldInfo calls = emitted.GetField("Calls")!;
        FieldInfo throwAt = emitted.GetField("ThrowAt")!;
        // A delegate, not MethodInfo.Invoke: the exception is the emitted method's own, thrown from its frame.
        Action step = emitted.GetMethod("Prefix")!.CreateDelegate<Action>();
        string walkId = ForeignPatches.IdPrefix + "hf-walkthrow-postfix";
        string helperId = ForeignPatches.IdPrefix + "hf-walkthrow-helper";
        bool benchChecks = ExtraScopes.BenchChecks;
        new Harmony(walkId).Patch(walk, postfix: new HarmonyMethod(typeof(ForeignShapesHookFilter), nameof(WalkStepPostfix)));
        new Harmony(helperId).Patch(helper, postfix: new HarmonyMethod(typeof(ForeignShapesHookFilter), nameof(HelperCountingPostfix)));
        _walkStep = step;
        ExtraScopes.BenchChecks = false;
        var lines = new List<string>();
        try
        {
            (string Thrown, int Steps, long Postfix, long Passed, long Answered) Call(bool mirror, int at)
            {
                calls.SetValue(null, 0);
                throwAt.SetValue(null, at);
                long postfixBefore = _helperPostfixCalls;
                long passedBefore = HookFilter.WalkThrowsPassedOn;
                long answeredBefore = HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCostColorWalk];
                string thrown = "none";
                void Invoke()
                {
                    try
                    {
                        helper.Invoke(null, new object[] { card, combat, 0m });
                    }
                    catch (TargetInvocationException e) when (e.InnerException != null)
                    {
                        thrown = $"{e.InnerException.GetType().Name}: {e.InnerException.Message}";
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
                return (thrown, (int)calls.GetValue(null)!, _helperPostfixCalls - postfixBefore, HookFilter.WalkThrowsPassedOn - passedBefore,
                    HookFilter.MirrorAnswers[HookFilter.MirrorEnergyCostColorWalk] - answeredBefore);
            }
            // The walk's length (steps per walk: the helper walks twice), then one call without a throw: the mirror over the game's
            // walk must answer it.
            var plain = Call(mirror: true, at: 0);
            int length = plain.Steps / 2;
            var plainGame = Call(mirror: false, at: 0);
            bool served = plain.Answered == 1 && plain.Thrown == "none" && plain.Postfix == 1 && plainGame.Steps == plain.Steps;
            lines.Add($"FOREIGN hookfilter walkthrow plain: walk of {length} listeners, mirror answered {plain.Answered}, steps {plain.Steps} (game's {plainGame.Steps}), "
                + $"helper postfix {plain.Postfix}: " + (served ? "served over the game's walk" : "NOT SERVED"));
            if (!served || length < 2)
            {
                return lines;
            }
            foreach ((string where, int at) in new[] { ("first walk", length / 2), ("second walk", length + length / 2) })
            {
                var mirrored = Call(mirror: true, at: at);
                var game = Call(mirror: false, at: at);
                bool faulted = PatchSafety.IsFaulted(HookFilter.Name);
                var problems = new List<string>();
                if (mirrored.Thrown != game.Thrown || game.Thrown == "none")
                {
                    problems.Add("not the same exception");
                }
                if (mirrored.Steps != game.Steps || game.Steps != at)
                {
                    problems.Add("not the same steps of the walk");
                }
                if (mirrored.Postfix != 0 || game.Postfix != 0)
                {
                    problems.Add("a postfix ran after the throw");
                }
                if (mirrored.Passed != 1)
                {
                    problems.Add("the mirror didn't pass the walk's exception on");
                }
                if (faulted || !HookFilter.Enabled)
                {
                    problems.Add("HookFilter faulted or off");
                }
                lines.Add($"FOREIGN hookfilter walkthrow {where}: step {at}; mirror {mirrored.Thrown}, steps {mirrored.Steps}, postfix {mirrored.Postfix}, passed on {mirrored.Passed}; "
                    + $"game's helper {game.Thrown}, steps {game.Steps}, postfix {game.Postfix}; HookFilter faulted {faulted}, on {HookFilter.Enabled}: "
                    + (problems.Count == 0 ? "same as the game's" : "DIFFERENT (" + string.Join("; ", problems) + ")"));
            }
            return lines;
        }
        finally
        {
            ExtraScopes.BenchChecks = benchChecks;
            _walkStep = null;
            new Harmony(walkId).UnpatchAll(walkId);
            new Harmony(helperId).UnpatchAll(helperId);
        }
    }
}
