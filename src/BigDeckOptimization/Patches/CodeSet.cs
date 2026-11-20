using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game's code a skipped call leaves out, computed from the game's own code (<see cref="MethodFingerprint.GameClosure"/>:
/// every game method the roots reach through calls, the game's own overrides of the game's virtual methods they call included,
/// type initializers and calls into other assemblies left out) and pinned per game version (<see cref="Reviewed"/>: the set's
/// fingerprint, names and code, as tools/fingerprint <c>closure</c> prints it). Answers, cached per Harmony epoch and for at
/// most 2 s like PatchSafety's own checks, whether another mod patches any of it (a generic member, named by its definition, on
/// every closed instantiation Harmony patches: <see cref="PatchSafety.PatchedInstantiations"/>), and whether a type outside the game (another
/// mod's model or node) overrides one of the game's virtual methods the set calls: code the skip would also leave out.
/// Built once at Apply; asked on the main thread or any other (answers are read and replaced whole).
/// </summary>
internal sealed class CodeSet
{
    /// <summary>The game methods of the set (the roots first).</summary>
    public readonly MethodBase[] Methods;
    /// <summary>The methods <see cref="AnyPatched"/> and <see cref="AnyHasPatches"/> ask about: <see cref="Methods"/> but those the caller checks itself.</summary>
    public readonly MethodBase[] Checked;
    /// <summary>The game's virtual (non-final) methods the set calls: another assembly's override runs in their place.</summary>
    public readonly MethodInfo[] Virtuals;
    /// <summary>The delegate types whose Invoke the set calls (their targets aren't known from the code), by name.</summary>
    public readonly string[] Delegates;
    /// <summary>The set's fingerprint (<see cref="MethodFingerprint.SetFingerprint"/>, the delegates' names included).</summary>
    public readonly string Fingerprint;
    /// <summary>The fingerprint is one of the reviewed ones (the set is the one reviewed for this game version).</summary>
    public readonly bool Reviewed;
    /// <summary>
    /// Rule C0 (2026-11-18, opt-in per set: CardFlightLimit's combat-end check): another assembly's override whose whole code is
    /// one constant returned (<see cref="ConstantBody"/>: <c>nop*; ldc.*|ldnull|ldstr; ret</c>), not patched by anyone (its
    /// HarmonyWatch hidden owners included), doesn't count as an overrider: it reads, writes and calls nothing, so a skip
    /// leaving its call out changes nothing. Its patch state is asked again per Harmony epoch and at most every 2 s.
    /// </summary>
    public readonly bool ConstantOverridesHarmless;

    private sealed record Answer(int Epoch, long CheckedAt, bool Value);

    /// <summary>
    /// An overriders answer for the loaded code (<see cref="LoadedCode.Key"/>), read and replaced whole (A6, re-audit round 2).
    /// <see cref="Constant"/>: the constant overrides let stand (<see cref="ConstantOverridesHarmless"/>), whose patch state
    /// (<see cref="ConstantPatched"/>) holds for <see cref="Epoch"/> and 2 s from <see cref="CheckedAt"/>.
    /// </summary>
    private sealed record OverridesAnswer(long Key, bool Value, MethodInfo[] Constant, int Epoch, long CheckedAt, bool ConstantPatched);

    /// <summary><see cref="Virtuals"/> but those rooted in a per-instance class (RelicModel), worked out once per class.</summary>
    private sealed record Outside(Type PerInstance, MethodInfo[] Virtuals);

    private Answer? _patched, _hasPatches;
    private OverridesAnswer? _overrides, _overridesOutside;
    private Outside? _outside;

    private CodeSet(MethodBase[] methods, MethodBase[] checkedMethods, MethodInfo[] virtuals, string[] delegates, string fingerprint, bool reviewed,
        bool constantOverridesHarmless)
    {
        Methods = methods;
        Checked = checkedMethods;
        Virtuals = virtuals;
        Delegates = delegates;
        Fingerprint = fingerprint;
        Reviewed = reviewed;
        ConstantOverridesHarmless = constantOverridesHarmless;
    }

    /// <summary>
    /// The set reached from <paramref name="roots"/> (null roots: a missing method, the caller's own check), at most
    /// <paramref name="limit"/> methods (more throws). Reviewed when its fingerprint is in <paramref name="reviewed"/>;
    /// <paramref name="name"/> names it in the log. <paramref name="checkedElsewhere"/>: methods of the set the caller checks
    /// itself (with allowed patches, or because it still calls them), left out of <see cref="Checked"/>.
    /// <paramref name="interfaces"/> (A1, re-audit round 2 of 2026-11-17): <see cref="Virtuals"/> also holds every game interface
    /// or abstract method the set calls virtually (no body, so never in the closure: <c>ICombatState.IterateHookListeners</c>
    /// reached by <c>Hook.ShouldStopCombatFromEnding</c>), so another assembly's implementation of it counts as an override (an
    /// implicit interface implementation is <c>virtual final</c>, which the closure's own virtual methods leave out). For the sets
    /// whose code is game logic or decides a replay; the visual sets keep the closure's own virtual methods (tools/fingerprint
    /// closure prints both: "virtual" and "dispatch" lines).
    /// </summary>
    /// <paramref name="stopAt"/>: methods the closure neither adds nor follows (a subtree judged as a set of its own).
    /// <paramref name="constantOverridesHarmless"/>: <see cref="ConstantOverridesHarmless"/> (rule C0).
    public static CodeSet Compute(string name, IEnumerable<MethodBase?> roots, int limit, string[] reviewed, IEnumerable<MethodBase?>? checkedElsewhere = null,
        bool interfaces = false, IEnumerable<MethodBase?>? stopAt = null, bool constantOverridesHarmless = false)
    {
        Assembly game = typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly;
        var delegates = new List<MethodInfo>();
        var dispatches = new List<(MethodInfo Callee, List<MethodBase> Implementations)>();
        List<MethodBase> closure = MethodFingerprint.GameClosure(game, roots.Where(r => r != null)!, limit, delegates, initializers: false, dispatch: false,
            dispatches: dispatches, stopAt: stopAt == null ? null : new HashSet<MethodBase>(stopAt.Where(m => m != null).Select(m => MethodFingerprint.Definition(m!))));
        string[] delegateNames = delegates.Select(d => MethodFingerprint.MemberName(d)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string fingerprint = MethodFingerprint.SetFingerprint(closure, delegateNames);
        bool known = Array.IndexOf(reviewed, fingerprint) >= 0;
        if (!known)
        {
            Log.Info($"[{ModEntry.Id}] {name}: the game code it relies on changed ({closure.Count} methods, set {fingerprint}): reviewed per game version");
        }
        IEnumerable<MethodInfo> virtualMethods = closure.OfType<MethodInfo>().Where(m => m.IsVirtual && !m.IsFinal && m.Module.Assembly == game);
        if (interfaces)
        {
            virtualMethods = virtualMethods.Concat(dispatches.Select(d => d.Callee).Where(c => c.Module.Assembly == game)
                .Select(c => (MethodInfo)MethodFingerprint.Definition(c)).Where(c => c.IsVirtual && !c.IsFinal));
        }
        MethodInfo[] virtuals = virtualMethods.Distinct().ToArray();
        var elsewhere = new HashSet<MethodBase>((checkedElsewhere ?? Enumerable.Empty<MethodBase?>()).Where(m => m != null)!);
        return new CodeSet(closure.ToArray(), closure.Where(m => !elsewhere.Contains(m)).ToArray(), virtuals, delegateNames, fingerprint, known,
            constantOverridesHarmless);
    }

    private static bool Fresh(Answer? answer) =>
        answer != null && answer.Epoch == HarmonyWatch.Epoch && Environment.TickCount64 - answer.CheckedAt < 2000;

    /// <summary>
    /// Whether another mod patches a method of the set, recorded as a back-off of <paramref name="caller"/> (its source path or
    /// patch name), as the strict check of each method would.
    /// </summary>
    public bool AnyPatched(string caller)
    {
        Answer? last = _patched;
        if (Fresh(last))
        {
            return last!.Value;
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        bool patched = false;
        foreach (MethodBase method in Checked)
        {
            // Every method asked (not the first found only): each back-off is recorded, bdopt why names them all. A generic
            // member (named by its definition) on each closed instantiation Harmony patches too (review D3).
            patched |= PatchSafety.IsPatchedByOthersWithInstances(method, caller);
        }
        _patched = new Answer(epoch, now, patched);
        return patched;
    }

    /// <summary>
    /// <see cref="AnyPatched(string)"/> letting stand, on each method, the patches <paramref name="allowed"/> gives for it (rule RO:
    /// CardFlightLimit's read-only postfixes on its combat-end head); one allowed function per set (its answer cached as the
    /// strict one's: per Harmony epoch and 2 s).
    /// </summary>
    public bool AnyPatched(string caller, Func<MethodBase, MethodInfo[]> allowed)
    {
        Answer? last = _patchedAllowed;
        if (Fresh(last))
        {
            return last!.Value;
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        bool patched = false;
        foreach (MethodBase method in Checked)
        {
            patched |= PatchSafety.IsPatchedByOthersWithInstances(method, allowed, caller);
        }
        _patchedAllowed = new Answer(epoch, now, patched);
        return patched;
    }

    private Answer? _patchedAllowed;

    /// <summary>Whether another mod patches a method of the set, without recording a back-off (a replay decision).</summary>
    public bool AnyHasPatches()
    {
        Answer? last = _hasPatches;
        if (Fresh(last))
        {
            return last!.Value;
        }
        int epoch = HarmonyWatch.Epoch;
        long now = Environment.TickCount64;
        bool patched = false;
        foreach (MethodBase method in Checked)
        {
            if (PatchSafety.HasOtherPatchesWithInstances(method))
            {
                patched = true;
                break;
            }
        }
        _hasPatches = new Answer(epoch, now, patched);
        return patched;
    }

    /// <summary>
    /// Whether a loaded type outside the game's assembly and this mod's overrides (or implements) one of <see cref="Virtuals"/>:
    /// its code runs where the game's would, and a skip leaves it out too. With <paramref name="perInstance"/> (RelicModel):
    /// the virtual methods rooted in that class (or a class deriving from it) are left to <see cref="InstanceOverrides"/>, the
    /// set calling them on the one instance the skipped call is about (the flashing relic). Worked out again when the loaded code
    /// changes (<see cref="LoadedCode.Key"/>: an assembly loads, a dynamic one gains types); true when it can't tell. Allocates
    /// nothing while the loaded code stays the same (C2, re-audit round 2: asked per flash).
    /// </summary>
    public bool ForeignOverrides(Type? perInstance = null)
    {
        MethodInfo[] virtuals = Virtuals;
        if (perInstance != null)
        {
            Outside? outside = _outside;
            if (outside == null || outside.PerInstance != perInstance)
            {
                _outside = outside = new Outside(perInstance, Virtuals.Where(v => !RootedIn(v, perInstance)).ToArray());
            }
            virtuals = outside.Virtuals;
        }
        if (virtuals.Length == 0)
        {
            return false;
        }
        long key = LoadedCode.Key();
        OverridesAnswer? known = perInstance == null ? _overrides : _overridesOutside;
        if (known != null && known.Key == key)
        {
            if (known.Value || known.Constant.Length == 0)
            {
                return known.Value;
            }
            // Constant overrides let stand: their patch state per Harmony epoch and at most 2 s (the scan stays per loaded code).
            int epoch = HarmonyWatch.Epoch;
            long now = Environment.TickCount64;
            if (known.Epoch == epoch && now - known.CheckedAt < 2000)
            {
                return known.ConstantPatched;
            }
            bool patched = known.Constant.Any(PatchSafety.HasOtherPatches);
            Store(perInstance, known with { Epoch = epoch, CheckedAt = now, ConstantPatched = patched });
            return patched;
        }
        bool found;
        var constant = ConstantOverridesHarmless ? new List<(Type Type, MethodInfo Override)>() : null;
        try
        {
            found = Overriders(virtuals, constant).Any();
        }
        catch
        {
            found = true;
        }
        MethodInfo[] constants = found || constant == null ? Array.Empty<MethodInfo>() : constant.Select(c => c.Override).Distinct().ToArray();
        bool constantPatched = constants.Any(PatchSafety.HasOtherPatches);
        Store(perInstance, new OverridesAnswer(key, found, constants, HarmonyWatch.Epoch, Environment.TickCount64, constantPatched));
        return found || constantPatched;
    }

    private void Store(Type? perInstance, OverridesAnswer answer)
    {
        if (perInstance == null)
        {
            _overrides = answer;
        }
        else
        {
            _overridesOutside = answer;
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> (the instance's class) or one of its base classes outside the game's assembly overrides
    /// one of <see cref="Virtuals"/> rooted in <paramref name="perInstance"/>: that instance's own code would run where the
    /// game's does (a modded relic's own icon path). Cached per type; true when it can't tell.
    /// </summary>
    public bool InstanceOverrides(Type type, Type perInstance) => _instance.TryGetValue(type, out object? known) ? known == Boxed.True
        : _instance.GetValue(type, t => InstanceOverridesOf(t, perInstance)) == Boxed.True;

    /// <summary><see cref="InstanceOverrides"/> worked out (a lambda only on the first ask per type: C2).</summary>
    private object InstanceOverridesOf(Type t, Type perInstance)
    {
        try
        {
            MethodInfo[] rooted = Virtuals.Where(v => RootedIn(v, perInstance)).ToArray();
            Assembly game = typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly;
            for (Type? c = t; c != null && c.Assembly != game; c = c.BaseType)
            {
                if (rooted.Any(v => Overrides(c, v)))
                {
                    return Boxed.True;
                }
            }
            return Boxed.False;
        }
        catch
        {
            return Boxed.True;
        }
    }

    private static class Boxed
    {
        public static readonly object True = new(), False = new();
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, object> _instance = new();

    private static bool RootedIn(MethodInfo method, Type root)
    {
        Type? declaring = method.GetBaseDefinition().DeclaringType;
        return declaring != null && (declaring == root || declaring.IsSubclassOf(root));
    }

    /// <summary>The types (outside the game and this mod) overriding one of <see cref="Virtuals"/>, by name (display).</summary>
    public string[] ForeignOverriders()
    {
        if (!ConstantOverridesHarmless)
        {
            return ForeignOverridersOf(Virtuals);
        }
        try
        {
            return CountedOverriders().Select(t => t.FullName ?? t.Name).Distinct().ToArray();
        }
        catch
        {
            return new[] { "?" };
        }
    }

    /// <summary>The types (outside the game and this mod) overriding one of <see cref="Virtuals"/> (display: their mods); empty when it can't tell.</summary>
    public Type[] ForeignOverriderTypes()
    {
        try
        {
            return (ConstantOverridesHarmless ? CountedOverriders() : Overriders(Virtuals, null)).Distinct().ToArray();
        }
        catch
        {
            return Type.EmptyTypes;
        }
    }

    /// <summary>
    /// With <see cref="ConstantOverridesHarmless"/>: the overriders <see cref="ForeignOverrides"/> counts, so bdopt why names the
    /// same ones (those with a non-constant override, and those whose constant override someone patches). Throws as
    /// <see cref="Overriders"/>.
    /// </summary>
    private List<Type> CountedOverriders()
    {
        var constant = new List<(Type Type, MethodInfo Override)>();
        List<Type> types = Overriders(Virtuals, constant).ToList();
        types.AddRange(constant.Where(c => PatchSafety.HasOtherPatches(c.Override)).Select(c => c.Type));
        return types;
    }

#if BENCH
    /// <summary>Test builds: how rule C0 judges this type's own code for <paramref name="virtualMethod"/>.</summary>
    internal static string OverrideVerdictForTest(Type type, MethodInfo virtualMethod) =>
        OverridingMethod(type, virtualMethod) is not { } m ? "no override"
            : !ConstantBody(m) ? "counted (not a constant)"
            : PatchSafety.HasOtherPatches(m) ? "counted (a constant, patched)" : "let stand (a constant)";
#endif

    /// <summary>Per method: <see cref="ConstantBody"/> (a loaded method's IL doesn't change).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeMethodHandle, bool> _constant = new();

    /// <summary>
    /// Rule C0: the method's whole code returns one constant (<c>nop*; ldc.i4*|ldc.i8|ldc.r4|ldc.r8|ldnull|ldstr; ret</c>): no
    /// argument, field, call or store, no exception clause, not generic. A Debug build's block body (<c>stloc; br; ldloc; ret</c>)
    /// isn't matched. False when the IL can't be read.
    /// </summary>
    internal static bool ConstantBody(MethodInfo method) => _constant.GetOrAdd(method.MethodHandle, _ =>
    {
        try
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is not { } body || body.ExceptionHandlingClauses.Count > 0)
            {
                return false;
            }
            List<MethodFingerprint.Instruction> code = MethodFingerprint.Instructions(method);
            int i = 0;
            while (i < code.Count && code[i].Op == System.Reflection.Emit.OpCodes.Nop)
            {
                i++;
            }
            if (i + 2 != code.Count || code[i + 1].Op != System.Reflection.Emit.OpCodes.Ret)
            {
                return false;
            }
            System.Reflection.Emit.OpCode op = code[i].Op;
            return op == System.Reflection.Emit.OpCodes.Ldnull || op == System.Reflection.Emit.OpCodes.Ldstr
                || op == System.Reflection.Emit.OpCodes.Ldc_I4 || op == System.Reflection.Emit.OpCodes.Ldc_I4_S || op == System.Reflection.Emit.OpCodes.Ldc_I4_M1
                || op == System.Reflection.Emit.OpCodes.Ldc_I4_0 || op == System.Reflection.Emit.OpCodes.Ldc_I4_1 || op == System.Reflection.Emit.OpCodes.Ldc_I4_2
                || op == System.Reflection.Emit.OpCodes.Ldc_I4_3 || op == System.Reflection.Emit.OpCodes.Ldc_I4_4 || op == System.Reflection.Emit.OpCodes.Ldc_I4_5
                || op == System.Reflection.Emit.OpCodes.Ldc_I4_6 || op == System.Reflection.Emit.OpCodes.Ldc_I4_7 || op == System.Reflection.Emit.OpCodes.Ldc_I4_8
                || op == System.Reflection.Emit.OpCodes.Ldc_I8 || op == System.Reflection.Emit.OpCodes.Ldc_R4 || op == System.Reflection.Emit.OpCodes.Ldc_R8;
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// The loaded types outside the game's assembly and this mod overriding or implementing one of <paramref name="virtuals"/>
    /// (HookSkip's helper groups: a mod's ICombatState, say), by name; "?" when it can't tell.
    /// </summary>
    internal static string[] ForeignOverridersOf(MethodInfo[] virtuals)
    {
        try
        {
            return Overriders(virtuals, null).Select(t => t.FullName ?? t.Name).Distinct().ToArray();
        }
        catch
        {
            return new[] { "?" };
        }
    }

    /// <summary>
    /// The candidate types (<see cref="LoadedCode.Types"/>) overriding or implementing one of <paramref name="virtuals"/>. Throws
    /// when the candidates aren't all known (an assembly that can't list its types): the callers answer "can't tell" then.
    /// With <paramref name="constant"/> (rule C0): a type whose every override of them is a <see cref="ConstantBody"/> (each
    /// declared on that type: a subclass's own override is judged as its own candidate) isn't yielded but added there with
    /// those overrides, for the caller to ask their patch state.
    /// </summary>
    private static IEnumerable<Type> Overriders(MethodInfo[] virtuals, List<(Type Type, MethodInfo Override)>? constant)
    {
        LoadedCode.TypeTable table = LoadedCode.Types();
        if (!table.Complete)
        {
            throw new InvalidOperationException("a loaded assembly's types can't be listed");
        }
        var declaring = new HashSet<Type>(virtuals.Select(v => v.DeclaringType!));
        foreach (Type type in table.Types)
        {
            bool overrides;
            List<MethodInfo>? constants = null;
            try
            {
                if (constant == null)
                {
                    overrides = !type.IsInterface && Derives(type, declaring) && virtuals.Any(v => Overrides(type, v));
                }
                else
                {
                    overrides = false;
                    if (!type.IsInterface && Derives(type, declaring))
                    {
                        foreach (MethodInfo v in virtuals)
                        {
                            if (OverridingMethod(type, v) is not { } own)
                            {
                                continue;
                            }
                            if (own is MethodInfo m && ConstantBody(m))
                            {
                                (constants ??= new List<MethodInfo>()).Add(m);
                            }
                            else
                            {
                                overrides = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                overrides = true; // Can't tell for this type: counted as overriding.
            }
            if (overrides)
            {
                yield return type;
            }
            else if (constants != null)
            {
                foreach (MethodInfo m in constants)
                {
                    constant!.Add((type, m));
                }
            }
        }
    }

    private static bool Derives(Type type, HashSet<Type> declaring)
    {
        foreach (Type d in declaring)
        {
            if (d.IsInterface ? d.IsAssignableFrom(type) : type.IsSubclassOf(d))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Overrides(Type type, MethodInfo virtualMethod) => OverridingMethod(type, virtualMethod) != null;

    /// <summary>The type's own code for <paramref name="virtualMethod"/> (its override, or the implementation <see cref="Overrides"/> counts), or null.</summary>
    private static MethodInfo? OverridingMethod(Type type, MethodInfo virtualMethod)
    {
        Type declaring = virtualMethod.DeclaringType!;
        if (declaring.IsInterface)
        {
            if (!declaring.IsAssignableFrom(type) || type.IsAbstract && type.IsSealed)
            {
                return null;
            }
            InterfaceMapping map = type.GetInterfaceMap(declaring);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                // Its own implementation, or (A5, re-audit round 2) a default method of an interface outside the game's assembly
                // (a mod interface deriving from the game's, giving the member a body): that code runs for this type. A target
                // inherited from a base class is that base class's (a candidate itself when outside the game).
                if (map.InterfaceMethods[i] == virtualMethod && map.TargetMethods[i] is { } target
                    && (target.DeclaringType == type || (target.DeclaringType is { IsInterface: true } owner && owner.Assembly != declaring.Assembly)))
                {
                    return target;
                }
            }
            return null;
        }
        if (!type.IsSubclassOf(declaring))
        {
            return null;
        }
        MethodInfo root = virtualMethod.GetBaseDefinition();
        foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name == virtualMethod.Name && m.GetBaseDefinition() is { } b && b.MetadataToken == root.MetadataToken && b.Module == root.Module)
            {
                return m;
            }
        }
        return null;
    }
}

/// <summary>
/// What code is loaded, for the caches of the overriders checks (CodeSet, HookSkip's helper groups; A4 and C4, re-audit round 2 of
/// 2026-11-17): a key that changes when an assembly loads (counted by the AssemblyLoad event) or a dynamic assembly gains types
/// (sampled at most every 2 s: a mod emitting a type into an assembly already loaded raises no event), and the candidate types
/// another assembly's override can be in. Allocates nothing while the loaded code stays the same.
/// </summary>
internal static class LoadedCode
{
    private static int _loads, _counting;

    private sealed record Snapshot(long Key, long At, int Loads);

    private static Snapshot? _snapshot;

    /// <summary>
    /// The current key: the assembly count and each dynamic assembly's created types. A dynamic assembly that can't list its types
    /// counts as one unreadable assembly (R3-4, re-audit round 3: it made every sample a new key before, so the name gates' types
    /// and the override answers were worked out again every 2 s on the main thread). The answers given while one is unreadable are
    /// the conservative ones (the candidates table says it is incomplete: "can't tell"; a name gate on nothing), and the key changes
    /// when it becomes readable or another assembly loads.
    /// </summary>
    public static long Key()
    {
        if (System.Threading.Interlocked.Exchange(ref _counting, 1) == 0)
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, _) => System.Threading.Interlocked.Increment(ref _loads);
        }
        int loads = System.Threading.Volatile.Read(ref _loads);
        long now = Environment.TickCount64;
        Snapshot? last = _snapshot;
        if (last != null && last.Loads == loads && now - last.At < 2000)
        {
            return last.Key;
        }
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        long key = assemblies.Length;
        foreach (Assembly assembly in assemblies)
        {
            if (!assembly.IsDynamic)
            {
                continue;
            }
            try
            {
                key += (long)CreatedTypes(assembly).Length << 20;
            }
            catch
            {
                key += 1L << 52;
            }
        }
        _snapshot = new Snapshot(key, now, loads);
        return key;
    }

    /// <summary>
    /// The types an override of a game method can be in, for one key: every type of the loaded assemblies depending on the game's
    /// assembly, directly or through another loaded assembly (C4: a class deriving from a game type through another mod's base
    /// class needs no reference to the game itself), and the created types of dynamic assemblies (A4: a mod emitting classes at
    /// run time); never the game's own nor this mod's. Complete is false when one of them couldn't list its types.
    /// </summary>
    public sealed record TypeTable(long Key, Type[] Types, bool Complete);

    private static TypeTable? _table;
    private static readonly object _tableLock = new();

    public static TypeTable Types()
    {
        long key = Key();
        TypeTable? table = _table;
        if (table != null && table.Key == key)
        {
            return table;
        }
        lock (_tableLock)
        {
            table = _table;
            if (table != null && table.Key == key)
            {
                return table;
            }
            Assembly game = typeof(MegaCrit.Sts2.Core.Hooks.Hook).Assembly;
            string gameName = game.GetName().Name!;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var byName = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (Assembly assembly in assemblies)
            {
                if (!assembly.IsDynamic && assembly.GetName().Name is { } name)
                {
                    byName.TryAdd(name, assembly);
                }
            }
            var depends = new Dictionary<string, bool>(StringComparer.Ordinal);
            var types = new List<Type>();
            bool complete = true;
            foreach (Assembly assembly in assemblies)
            {
                if (assembly == game || assembly == typeof(LoadedCode).Assembly)
                {
                    continue;
                }
                try
                {
                    if (assembly.IsDynamic)
                    {
                        types.AddRange(CreatedTypes(assembly));
                        continue;
                    }
                    if (!DependsOn(assembly, gameName, byName, depends, 0))
                    {
                        continue;
                    }
                    try
                    {
                        types.AddRange(assembly.GetTypes());
                    }
                    catch (ReflectionTypeLoadException e)
                    {
                        // Types it couldn't load can't be instantiated either.
                        types.AddRange(e.Types.Where(t => t != null)!);
                    }
                }
                catch
                {
                    complete = false;
                }
            }
            return _table = new TypeTable(key, types.ToArray(), complete);
        }
    }

    /// <summary>A dynamic assembly's types already created (an unfinished TypeBuilder has no instance and no method to run).</summary>
    private static Type[] CreatedTypes(Assembly assembly) => assembly.GetTypes()
        .Where(t => t is not System.Reflection.Emit.TypeBuilder and not System.Reflection.Emit.EnumBuilder and not System.Reflection.Emit.GenericTypeParameterBuilder)
        .ToArray();

    /// <summary>Whether the assembly references the game's assembly, directly or through loaded assemblies (memo per name; depth 16).</summary>
    private static bool DependsOn(Assembly assembly, string game, Dictionary<string, Assembly> byName, Dictionary<string, bool> memo, int depth)
    {
        string name = assembly.GetName().Name ?? "";
        if (memo.TryGetValue(name, out bool known))
        {
            return known;
        }
        memo[name] = false; // A reference cycle answers from the other paths.
        bool found = false;
        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            if (reference.Name == game || (depth < 16 && reference.Name is { } referenced && byName.TryGetValue(referenced, out Assembly? loaded)
                && DependsOn(loaded, game, byName, memo, depth + 1)))
            {
                found = true;
                break;
            }
        }
        return memo[name] = found;
    }
}
