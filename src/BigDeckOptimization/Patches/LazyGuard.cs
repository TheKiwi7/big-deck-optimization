using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Methods of a skipped call's set (<see cref="CodeSet"/>) that only run while a lazily built singleton doesn't exist yet: the
/// game's <c>static T Instance { get { if (_instance == null) _instance = Build(); return _instance; } }</c> (v0.111.1 and
/// v0.113.1 <c>SaveManager.Instance</c> and <c>ConstructDefault</c>). Once the field holds an instance, no call the set makes
/// reaches <c>Build</c> or what only <c>Build</c> reaches, so a skipped call leaves none of it out: another mod's patch there
/// (Unlimited's save folder postfix on <c>ConstructDefault</c>) can't see a difference. Generic (the IL shape, no list of
/// types), found once from the game's code, asked per use (the field read then: an instance already built).
///
/// A guard holds when, all checked in the game's code: the getter's IL is exactly <c>ldsfld F; brtrue L; call Build;
/// stsfld F; L: ldsfld F; ret</c> (nops aside), after any number of early returns <c>ldsfld X; brfalse M; ldsfld X; ret; M:</c>
/// on static fields of its type (SaveManager's <c>_mockInstance</c>: they never reach the builder), F a static field of the getter's own type, <c>Build</c> a static game method
/// without parameters; within the set, the getter is the only method calling <c>Build</c> (call, callvirt, ldftn, generated code
/// included) and the only one storing F (no reset to null in the set); the methods behind it are those the set no longer
/// reaches when the closure stops at <c>Build</c> (<see cref="MethodFingerprint.GameClosure"/>, the set's own options). Any
/// other shape: no guard, the caller's strict checks as before.
/// </summary>
internal static class LazyGuard
{
    /// <summary>One guard: the field whose value says the methods behind it can't run, and those methods.</summary>
    internal sealed record Guard(FieldInfo Field, MethodBase Builder, MethodBase Getter, HashSet<MethodBase> Behind);

    /// <summary>
    /// The guards of a set computed from <paramref name="roots"/> (the same roots and limit as the set), for the builders among
    /// <paramref name="candidates"/>'s members or their callers: each candidate method behind a guard maps to it. Methods behind
    /// no guard are absent. Never throws: a failure gives no guard.
    /// </summary>
    internal static Dictionary<MethodBase, Guard> Find(IEnumerable<MethodBase?> roots, CodeSet set, IEnumerable<MethodBase> candidates, int limit)
    {
        var result = new Dictionary<MethodBase, Guard>();
        try
        {
            var wanted = new HashSet<MethodBase>(candidates);
            if (wanted.Count == 0)
            {
                return result;
            }
            Assembly game = typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly;
            var inSet = new HashSet<MethodBase>(set.Methods);
            MethodBase[] rootList = roots.Where(r => r != null).Select(r => r!).ToArray();
            foreach (MethodBase getter in set.Methods)
            {
                if (Shape(getter, game) is not { } shape || !inSet.Contains(shape.Builder))
                {
                    continue;
                }
                // A method whose code can't be read throws below (strict reads): no guard at all, logged.
                // The getter alone calls the builder and stores the field, within the set.
                bool alone = true;
                foreach (MethodBase method in set.Methods)
                {
                    if (method == getter || method.GetMethodBody() == null)
                    {
                        continue;
                    }
                    if (MethodFingerprint.GameCallees(game, method, new List<MethodInfo>(), initializers: false, dispatch: false).Contains(shape.Builder)
                        || StoresField(method, shape.Field))
                    {
                        alone = false;
                        break;
                    }
                }
                if (!alone)
                {
                    continue;
                }
                // What only the builder reaches: the set without it.
                var without = new HashSet<MethodBase>(MethodFingerprint.GameClosure(game, rootList, limit, new List<MethodInfo>(), initializers: false, dispatch: false,
                    stopAt: new HashSet<MethodBase> { shape.Builder }));
                var behind = new HashSet<MethodBase>(set.Methods.Where(m => !without.Contains(m)));
                if (!behind.Contains(shape.Builder))
                {
                    continue;
                }
                var guard = new Guard(shape.Field, shape.Builder, getter, behind);
                foreach (MethodBase method in behind)
                {
                    if (wanted.Contains(method))
                    {
                        result[method] = guard;
                    }
                }
            }
        }
        catch (Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Info($"[{ModEntry.Id}] LazyGuard: lazily built instances not worked out ({e.GetType().Name}: {e.Message}): strict checks as before");
            result.Clear();
        }
        return result;
    }

#if BENCH
    /// <summary>Test tooling (foreignpatch lazyguard open): every guard read as open, as if its instance weren't built yet.</summary>
    internal static bool ForceOpen { get; set; }
#endif

    /// <summary>The guard is shut: its field holds an instance (read now: the methods behind it can't run from the set).</summary>
    internal static bool Shut(Guard guard)
    {
#if BENCH
        if (ForceOpen)
        {
            return false;
        }
#endif
        try
        {
            return guard.Field.GetValue(null) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The lazy getter's field and builder, or null for any other shape.</summary>
    private static (FieldInfo Field, MethodBase Builder)? Shape(MethodBase getter, Assembly game)
    {
        if (getter is not MethodInfo info || !info.IsStatic || info.GetParameters().Length != 0 || info.GetMethodBody() == null
            || getter.Module.Assembly != game)
        {
            return null;
        }
        List<MethodFingerprint.Instruction> code;
        try
        {
            code = MethodFingerprint.Instructions(getter, strict: true).Where(i => i.Op != OpCodes.Nop).ToList();
        }
        catch
        {
            return null;
        }
        // Early returns first, each "if (X != null) return X;" on a static field of the getter's type (SaveManager's
        // _mockInstance): they never reach the builder. Then the lazy core, exactly.
        int at = 0;
        while (code.Count - at > 6
            && code[at].Op == OpCodes.Ldsfld && code[at].Operand is FieldInfo early && early.IsStatic && early.DeclaringType == getter.DeclaringType
            && (code[at + 1].Op == OpCodes.Brfalse || code[at + 1].Op == OpCodes.Brfalse_S) && code[at + 1].Operand is int skip
            && code[at + 2].Op == OpCodes.Ldsfld && Equals(code[at + 2].Operand, early)
            && code[at + 3].Op == OpCodes.Ret
            && code[at + 4].Offset == skip)
        {
            at += 4;
        }
        if (code.Count - at != 6
            || code[at].Op != OpCodes.Ldsfld || code[at].Operand is not FieldInfo field
            || (code[at + 1].Op != OpCodes.Brtrue && code[at + 1].Op != OpCodes.Brtrue_S) || code[at + 1].Operand is not int target
            || code[at + 2].Op != OpCodes.Call || code[at + 2].Operand is not MethodInfo builder
            || code[at + 3].Op != OpCodes.Stsfld || !Equals(code[at + 3].Operand, field)
            || code[at + 4].Op != OpCodes.Ldsfld || !Equals(code[at + 4].Operand, field) || code[at + 4].Offset != target
            || code[at + 5].Op != OpCodes.Ret)
        {
            return null;
        }
        if (!field.IsStatic || field.DeclaringType != getter.DeclaringType || field.FieldType.IsValueType
            || !builder.IsStatic || builder.GetParameters().Length != 0 || builder.IsGenericMethod || builder.Module.Assembly != game
            || !field.FieldType.IsAssignableFrom(builder.ReturnType) || !info.ReturnType.IsAssignableFrom(field.FieldType))
        {
            return null;
        }
        return (field, builder);
    }

    /// <summary>Whether the method's code (its generated code included) stores the field, or takes its address.</summary>
    private static bool StoresField(MethodBase method, FieldInfo field)
    {
        foreach (MethodBase code in MethodFingerprint.GeneratedCode(method).Prepend(method).Distinct())
        {
            if (code.GetMethodBody() == null)
            {
                continue;
            }
            foreach (MethodFingerprint.Instruction i in MethodFingerprint.Instructions(code, strict: true))
            {
                if ((i.Op == OpCodes.Stsfld || i.Op == OpCodes.Ldsflda) && Equals(i.Operand, field))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
