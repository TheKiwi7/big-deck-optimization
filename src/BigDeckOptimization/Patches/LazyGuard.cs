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

    /// <summary>
    /// The guard is shut: its field holds an instance (read now: the methods behind it can't run from the set), and no other mod
    /// patches the getter (review M1: its IL was read from metadata; a patch there, or a hidden IL hook, may call the builder or
    /// what only it reaches whatever the field holds). Asked at each use (the patch state through PatchSafety's 2 s cache).
    /// </summary>
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
            // HasOtherPatches counts the hidden IL hooks' owners too (WithHidden); no back-off recorded for the getter here (where
            // it is a strict member of its own, it is judged there).
            return !PatchSafety.HasOtherPatches(guard.Getter) && guard.Field.GetValue(null) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The latch fields of a method's other mods' patches, for the Harmony epoch and 2 s they were worked out at (null: not all latched).</summary>
    private sealed record Latches(int Epoch, long CheckedAt, FieldInfo[]? Fields);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodBase, Latches> _latches = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeMethodHandle, FieldInfo?> _latchShapes = new();

#if BENCH
    /// <summary>Strict members found latched shut (the effects check: rule latch ran).</summary>
    public static long LatchedChecks;
#endif

    /// <summary>
    /// Rule latch (2026-11-18, All Timeline Unlock's <c>GetProgressPatch</c>): whether every other mod's patch on
    /// <paramref name="method"/> is a latch that is shut now, so none of their code can run there: a void prefix or postfix whose
    /// code starts (nops aside) <c>ldsfld F; brtrue L</c> with only nops, unconditional branches and <c>ret</c> from L, F a private
    /// static bool field of the patch's own class, every store to F in that class and its nested classes <c>ldc.i4.1; stsfld F</c>
    /// (the <c>stsfld</c> no branch target) and none taking its address (<c>ldsflda</c>): once true, F stays true and the patch returns at its first test. F is read
    /// at each use; the patch list (and each patch method unpatched, no hidden IL hook) per Harmony epoch and at most every 2 s.
    /// Keyed on the first test only (what follows it never runs). False when no other mod patches the method, or it can't tell.
    /// </summary>
    internal static bool LatchedShut(MethodBase method)
    {
        try
        {
            if (!PatchSafety.HasOtherPatches(method))
            {
                return false;
            }
            int epoch = HarmonyWatch.Epoch;
            long now = Environment.TickCount64;
            if (!_latches.TryGetValue(method, out Latches? known) || known.Epoch != epoch || now - known.CheckedAt >= 2000)
            {
                known = new Latches(epoch, now, LatchFields(method));
                _latches[method] = known;
            }
            if (known.Fields is not { Length: > 0 } fields)
            {
                return false;
            }
            foreach (FieldInfo field in fields)
            {
                if (field.GetValue(null) is not true)
                {
                    return false;
                }
            }
#if BENCH
            LatchedChecks++;
#endif
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FieldInfo[]? LatchFields(MethodBase method)
    {
        if (HarmonyWatch.HiddenOwners(method).Length > 0 || HarmonyLib.Harmony.GetPatchInfo(method) is not { } info)
        {
            return null;
        }
        var fields = new List<FieldInfo>();
        foreach (HarmonyLib.Patch patch in PatchSafety.AllPatches(info))
        {
            if (PatchGuard.IsOurs(patch.owner) || !(info.Prefixes.Contains(patch) || info.Postfixes.Contains(patch)) || PatchSafety.PatchMethodOf(patch) is not { } patchMethod
                || PatchSafety.IsFactory(patchMethod) || (HarmonyLib.Harmony.GetPatchInfo(patchMethod) is { } own && PatchSafety.AllPatches(own).Any())
                || _latchShapes.GetOrAdd(patchMethod.MethodHandle, _ => LatchShape(patchMethod)) is not { } field)
            {
                return null;
            }
            fields.Add(field);
        }
        return fields.Count > 0 ? fields.ToArray() : null;
    }

#if BENCH
    /// <summary>Test tooling: the latch field of a patch method of the latch's shape, or null (not cached).</summary>
    internal static FieldInfo? LatchShapeForTest(MethodInfo patch) => LatchShape(patch);
#endif

    /// <summary>The latch field of a patch method of the latch's shape (see <see cref="LatchedShut"/>), or null.</summary>
    private static FieldInfo? LatchShape(MethodInfo patch)
    {
        try
        {
            if (!patch.IsStatic || patch.ReturnType != typeof(void) || patch.ContainsGenericParameters || patch.DeclaringType is not { } owner)
            {
                return null;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(patch, strict: true);
            var at = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++)
            {
                at[code[i].Offset] = i;
            }
            int index = 0;
            while (index < code.Count && code[index].Op == OpCodes.Nop)
            {
                index++;
            }
            if (index + 1 >= code.Count || code[index].Op != OpCodes.Ldsfld || code[index].Operand is not FieldInfo field
                || (code[index + 1].Op != OpCodes.Brtrue && code[index + 1].Op != OpCodes.Brtrue_S) || code[index + 1].Operand is not int target
                || !at.TryGetValue(target, out int next))
            {
                return null;
            }
            if (!field.IsStatic || !field.IsPrivate || field.FieldType != typeof(bool) || field.DeclaringType != owner)
            {
                return null;
            }
            // From the branch target: only nops and unconditional branches before a ret.
            var visited = new HashSet<int>();
            while (true)
            {
                if (next < 0 || next >= code.Count || !visited.Add(next))
                {
                    return null;
                }
                OpCode op = code[next].Op;
                if (op == OpCodes.Ret)
                {
                    break;
                }
                if (op == OpCodes.Nop)
                {
                    next++;
                }
                else if (op == OpCodes.Br || op == OpCodes.Br_S)
                {
                    next = code[next].Operand is int jump && at.TryGetValue(jump, out int to) ? to : -1;
                }
                else
                {
                    return null;
                }
            }
            // Every store to the field in its class and the classes nested in it: ldc.i4.1 right before, the store no branch target
            // (`F = a || b` compiles to `brtrue L1; ldarg b; br L2; L1: ldc.i4.1; L2: stsfld F`: the br path stores b); no address taken.
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var types = new List<Type> { owner };
            for (int i = 0; i < types.Count; i++)
            {
                types.AddRange(types[i].GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            }
            foreach (Type type in types)
            {
                foreach (MethodBase member in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if (member.GetMethodBody() == null)
                    {
                        continue;
                    }
                    List<MethodFingerprint.Instruction> body = MethodFingerprint.Instructions(member, strict: true);
                    HashSet<int>? targets = null;
                    for (int i = 0; i < body.Count; i++)
                    {
                        if (!Equals(body[i].Operand, field))
                        {
                            continue;
                        }
                        if (body[i].Op == OpCodes.Ldsflda || (body[i].Op == OpCodes.Stsfld && (i == 0 || body[i - 1].Op != OpCodes.Ldc_I4_1
                            || (targets ??= PatchSafety.BranchTargets(body, PatchSafety.IndexByOffset(body))).Contains(i))))
                        {
                            return null;
                        }
                    }
                }
            }
            return field;
        }
        catch
        {
            return null;
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
