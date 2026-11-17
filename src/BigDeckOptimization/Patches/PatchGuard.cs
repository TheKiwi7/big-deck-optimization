using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Removes this mod's Harmony patches when they could hurt the game, so that part runs the unmodified game code:
/// <list type="bullet">
/// <item>an Apply that fails halfway: the patches it already installed are removed (<see cref="Diff"/> / <see cref="Remove"/>);</item>
/// <item>code of a patch class that throws into the game at runtime (an exception leaving a patched method through
/// one of its frames): that patch is faulted (off for the session, see <see cref="PatchSafety.Fault"/>) and all its
/// Harmony patches are removed on the next frame. Patches catch their own errors; this covers what they can't, e.g.
/// a patch method the JIT can't compile, which throws before its own Enabled check and try/catch run.</item>
/// </list>
/// Each patched method gets <see cref="Guard"/> as a finalizer: it only watches and costs one call per call of the method.
/// It is void, and so are this mod's other pass-through finalizers: Harmony 2.4 decides per method, and only when every
/// finalizer on it (every mod's) is void does it rethrow the exception as it was (<c>rethrow</c>). One finalizer returning
/// an Exception makes it <c>throw</c> the exception again, its stack trace then starting at the replacement method, the
/// frames below (the game's or another mod's code that threw) gone. Only HookFilter's mirrored hooks keep one
/// (<c>RethrowFinalizer</c>, which substitutes an exception): it logs the full trace before.
/// </summary>
public static class PatchGuard
{
    public const string Name = "PatchGuard";

    public readonly record struct Installed(MethodBase Original, MethodInfo Patch);

    private static readonly MethodInfo _guard = Reflect.Method(typeof(PatchGuard), nameof(Guard));

    /// <summary>This mod's guard finalizer (void, read-only): for checks listing this mod's own patches on a method.</summary>
    internal static bool IsGuard(MethodInfo method) => method.MethodHandle == _guard.MethodHandle;

    /// <summary>
    /// The guard, first of the method's finalizers (Priority.First): Harmony gives each finalizer the exception the ones before
    /// it left, and another mod's finalizer returning null would hide a fault of this mod's patch from a guard running after it.
    /// Void and read-only, it changes nothing for the finalizers after it.
    /// </summary>
    private static HarmonyMethod Guarding() => new(_guard) { priority = Priority.First };

    private static readonly object _lock = new();

    /// <summary>Patch class (outermost type) -> names of the patches applied from it (<see cref="ModEntry.TryApply"/>).</summary>
    private static readonly Dictionary<Type, HashSet<string>> _names = new();

    private static readonly HashSet<MethodBase> _guarded = new();
    private static readonly HashSet<Type> _removed = new();

    /// <summary>
    /// Whether a Harmony owner id is this mod's: each patch applies with its own id (<c>thekiwi7.BigDeckOptimization.&lt;patch&gt;</c>,
    /// see <see cref="ModEntry.TryApply"/>), so what one Apply installed is just what its id owns.
    /// </summary>
    public static bool IsOurs(string owner) =>
        owner == ModEntry.Harmony.Id || owner.StartsWith(ModEntry.Harmony.Id + ".", StringComparison.Ordinal);

    /// <summary>This mod's patches (guards excluded), every patch id.</summary>
    public static HashSet<Installed> Snapshot()
    {
        var installed = new HashSet<Installed>();
        foreach (MethodBase original in Harmony.GetAllPatchedMethods())
        {
            AddOwned(installed, original, IsOurs);
        }
        return installed;
    }

    /// <summary>Methods patched per Harmony id: asking Harmony would read every method patched so far, for each patch of this mod.</summary>
    private static readonly Dictionary<string, List<MethodBase>> _patchedBy = new();

    /// <summary>The patches (guards excluded) of one patch's Harmony instance: what its Apply installed.</summary>
    public static List<Installed> Owned(Harmony harmony)
    {
        var installed = new HashSet<Installed>();
        List<MethodBase>? patched;
        lock (_lock)
        {
            if (!_patchedBy.TryGetValue(harmony.Id, out patched))
            {
                return new List<Installed>();
            }
            patched = patched.ToList();
        }
        foreach (MethodBase original in patched)
        {
            AddOwned(installed, original, owner => owner == harmony.Id);
        }
        return installed.ToList();
    }

    private static void AddOwned(HashSet<Installed> installed, MethodBase original, Func<string, bool> owned)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(original);
        if (info == null)
        {
            return;
        }
        foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
        {
            if (owned(patch.owner) && patch.PatchMethod != _guard)
            {
                installed.Add(new Installed(original, patch.PatchMethod));
            }
        }
    }

    /// <summary>
    /// <c>harmony.Patch</c>, with the guard finalizer added in the same call when the method isn't guarded yet: one rebuild
    /// of the method instead of two (each costs ~1 ms at load). A processor holds one finalizer: a patch bringing its own
    /// has the guard added by a second call.
    /// </summary>
    public static MethodInfo Patch(Harmony harmony, MethodBase original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
        HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null)
    {
        if (original == null)
        {
            throw new MissingMethodException("a method to patch wasn't found (game or mod update?)");
        }
        InlineGuard.Protect(original);
        PatchProcessor processor = harmony.CreateProcessor(original);
        if (prefix != null) processor.AddPrefix(prefix);
        if (postfix != null) processor.AddPostfix(postfix);
        if (transpiler != null) processor.AddTranspiler(transpiler);
        if (finalizer != null) processor.AddFinalizer(finalizer);
        bool guard = NeedsGuard(original);
        if (guard)
        {
            lock (_lock)
            {
                guard = _guarded.Add(original);
            }
        }
        if (guard && finalizer == null)
        {
            processor.AddFinalizer(Guarding());
        }
        lock (_lock)
        {
            if (!_patchedBy.TryGetValue(harmony.Id, out List<MethodBase>? patched))
            {
                _patchedBy[harmony.Id] = patched = new List<MethodBase>();
            }
            patched.Add(original);
        }
#if BENCH
        long start = Stopwatch.GetTimestamp();
#endif
        try
        {
            MethodInfo replacement = HarmonyRebuild.Run(original, "patching it", processor.Patch);
            if (guard && finalizer != null)
            {
                replacement = HarmonyRebuild.Run(original, "adding the guard", () => harmony.CreateProcessor(original).AddFinalizer(Guarding()).Patch());
            }
#if BENCH
            CheckInstalled(original, prefix, postfix, transpiler, finalizer);
#endif
            return replacement;
        }
        catch
        {
            if (guard)
            {
                lock (_lock)
                {
                    _guarded.Remove(original);
                }
            }
            throw;
        }
#if BENCH
        finally
        {
            MethodFingerprint.LoadTicks[3] += Stopwatch.GetTimestamp() - start;
        }
#endif
    }

#if BENCH
    /// <summary>Test builds: every patch method passed really is on the method (and its guard when one was asked).</summary>
    private static void CheckInstalled(MethodBase original, HarmonyMethod? prefix, HarmonyMethod? postfix, HarmonyMethod? transpiler, HarmonyMethod? finalizer)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(original);
        void Check(HarmonyMethod? wanted, IEnumerable<Patch>? installed, string kind)
        {
            if (wanted != null && installed?.Any(p => p.PatchMethod == wanted.method) != true)
            {
                Log.Error($"[{ModEntry.Id}] {Name}: MISMATCH {kind} {wanted.method.DeclaringType?.Name}.{wanted.method.Name} is not on {original.DeclaringType?.Name}.{original.Name}");
            }
        }
        Check(prefix, info?.Prefixes, "prefix");
        Check(postfix, info?.Postfixes, "postfix");
        Check(transpiler, info?.Transpilers, "transpiler");
        Check(finalizer, info?.Finalizers, "finalizer");
        if (NeedsGuard(original) && info?.Finalizers.Any(p => p.PatchMethod == _guard) != true)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: MISMATCH no guard on {original.DeclaringType?.Name}.{original.Name}");
        }
    }
#endif

    /// <summary>
    /// An async state machine's MoveNext never lets an exception out (it goes into the task): a finalizer there could
    /// never fire. The code of this mod running there falls back by itself (HookSkip.FilterFor, FastListeners.Guarded).
    /// </summary>
    private static bool NeedsGuard(MethodBase original) =>
        !(original.Name == "MoveNext" && original.DeclaringType != null
            && typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(original.DeclaringType));

    /// <summary>After a successful Apply: the patch class's code is watched on every method it patched.</summary>
    public static void Register(string name, Type patchClass, IEnumerable<Installed> installed)
    {
        lock (_lock)
        {
            Type type = Outermost(patchClass);
            if (!_names.TryGetValue(type, out HashSet<string>? names))
            {
                _names[type] = names = new HashSet<string>();
            }
            names.Add(name);
        }
        foreach (MethodBase original in installed.Select(p => p.Original).Distinct())
        {
            InlineGuard.Protect(original);
            Watch(original);
        }
    }

    /// <summary>Adds the guard to a method this mod patched (for patches installed later than their Apply).</summary>
    public static void Watch(MethodBase original)
    {
        // HookSkip transpiles ~140 async state machines: no guard there (see NeedsGuard), each would cost a rebuild.
        if (!NeedsGuard(original))
        {
            return;
        }
        lock (_lock)
        {
            if (!_guarded.Add(original))
            {
                return;
            }
        }
        try
        {
            HarmonyRebuild.Run(original, "adding the guard", () => ModEntry.Harmony.Patch(original, finalizer: Guarding()));
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: could not watch {original.DeclaringType?.Name}.{original.Name}: {e.Message}");
        }
    }

    /// <summary>Removes these patches, and the guard of each method left without patches of this mod.</summary>
    public static void Remove(IEnumerable<Installed> patches)
    {
        var originals = new HashSet<MethodBase>();
        foreach (Installed p in patches)
        {
            originals.Add(p.Original);
            try
            {
                HarmonyRebuild.Run(p.Original, "removing a patch", () => ModEntry.Harmony.Unpatch(p.Original, p.Patch));
            }
            catch (Exception e)
            {
                Log.Error($"[{ModEntry.Id}] {Name}: could not remove {p.Patch.DeclaringType?.Name}.{p.Patch.Name} from {p.Original.DeclaringType?.Name}.{p.Original.Name}: {e}");
            }
        }
        HashSet<MethodBase> stillPatched = Snapshot().Select(p => p.Original).ToHashSet();
        foreach (MethodBase original in originals.Where(o => !stillPatched.Contains(o)))
        {
            lock (_lock)
            {
                if (!_guarded.Remove(original))
                {
                    continue;
                }
            }
            try
            {
                HarmonyRebuild.Run(original, "removing the guard", () => ModEntry.Harmony.Unpatch(original, _guard));
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: could not remove the guard of {original.DeclaringType?.Name}.{original.Name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Runs after every call of a patched method: kept to a null check the JIT can inline (hot methods). Harmony 2.4 calls
    /// every finalizer a second time, in its catch block, when one of them throws after a normal return: this one then sees
    /// that exception once (null the first time), never throws itself (<see cref="Check"/>), and faulting a class twice
    /// does nothing more (<see cref="_removed"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Guard(Exception? __exception)
    {
        if (__exception != null)
        {
            Check(__exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Check(Exception exception)
    {
        try
        {
            FaultAndRemove(exception);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: {e}");
        }
    }

    /// <summary>Faults and removes every patch class the exception went through, from where it was thrown to here.</summary>
    private static void FaultAndRemove(Exception exception)
    {
        var hit = new List<(Type Type, string[] Names)>();
        foreach (StackFrame frame in new StackTrace(exception).GetFrames())
        {
            // From the throw outwards: a replay of the game's own call (GameCallReplayAttribute) ends the walk, the code that
            // threw being the game's or another mod's, called as the game would (this mod's patches inside it still count).
            if (frame.GetMethod() is { } replay && replay.IsDefined(typeof(GameCallReplayAttribute), false))
            {
                break;
            }
            if (frame.GetMethod()?.DeclaringType is not { } declaring || declaring.Assembly != typeof(PatchGuard).Assembly)
            {
                continue;
            }
            Type type = Outermost(declaring);
            lock (_lock)
            {
                if (_names.TryGetValue(type, out HashSet<string>? names) && _removed.Add(type))
                {
                    hit.Add((type, names.ToArray()));
                }
            }
        }
        if (hit.Count == 0)
        {
            return;
        }
        foreach ((Type type, string[] names) in hit)
        {
            foreach (string name in names)
            {
                PatchSafety.Fault(name);
            }
            Log.Error($"[{ModEntry.Id}] {string.Join(", ", names)}: error inside the game's code, turned off and removed for this session (normal game from now on): {exception}");
        }
        // Unpatching rebuilds the method that is running right now: done once it has returned. Scheduled first, so nothing
        // after (the settings) can keep it from happening.
        Type[] types = hit.Select(h => h.Type).ToArray();
        Godot.Callable.From(() => RemoveFaulted(types, 0)).CallDeferred();
        Config.Settings.ApplyAll();
    }

    /// <summary>Seconds after which a faulted patch class's patches that couldn't be removed are tried again.</summary>
    private static readonly double[] RetryRemovalAfter = { 2, 10, 60 };

    /// <summary>
    /// Removes the patches of faulted patch classes. A removal can be refused (<see cref="HarmonyRebuild.Run{T}"/>: another
    /// mod added a patch Harmony can't build again since) or fail: what is left stays installed but falls back to vanilla
    /// through its flag (faulted: <see cref="Config.Settings.ApplyAll"/> keeps it off), and is tried again a few times.
    /// </summary>
    private static void RemoveFaulted(Type[] types, int attempt)
    {
        bool Faulted(Installed p) => p.Patch.DeclaringType != null && types.Contains(Outermost(p.Patch.DeclaringType));
        RemoveWithRetries(() => Snapshot().Where(Faulted).ToList(), attempt);
    }

    /// <summary>
    /// After an Apply that failed (<see cref="ModEntry.TryApply"/>): removes what that patch's Harmony id installed. What can't
    /// be removed (refused or failed) would keep running with its option on: the patch is faulted (flags off, so its code
    /// falls back to the game's) and the removal is tried again later, as for a fault at runtime.
    /// </summary>
    public static void RemoveFailedApply(string name, Harmony harmony)
    {
        Remove(Owned(harmony));
        List<Installed> left = Owned(harmony);
        if (left.Count == 0)
        {
            return;
        }
        PatchSafety.Fault(name);
        Config.Settings.ApplyAll();
        RetryLater(() => Owned(harmony), left, 0);
    }

    /// <summary>Removes these patches (read again at each try); what is left is tried again later.</summary>
    private static void RemoveWithRetries(Func<List<Installed>> patches, int attempt)
    {
        try
        {
            Remove(patches());
            List<Installed> left = patches();
            if (left.Count > 0)
            {
                RetryLater(patches, left, attempt);
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: removing faulted patches: {e}");
        }
    }

    /// <summary>Patches left installed though faulted: kept off, tried again after <see cref="RetryRemovalAfter"/>[attempt].</summary>
    private static void RetryLater(Func<List<Installed>> patches, List<Installed> left, int attempt)
    {
        try
        {
            // Never back on, whatever changed the settings meanwhile.
            Config.Settings.ApplyAll();
            string what = string.Join(", ", left.Select(p => $"{p.Patch.DeclaringType?.Name}.{p.Patch.Name} on {HarmonyRebuild.Describe(p.Original)}"));
            if (attempt >= RetryRemovalAfter.Length || Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
            {
                Log.Error($"[{ModEntry.Id}] {Name}: faulted patches left installed (turned off, normal game through them): {what}");
                return;
            }
            Log.Warn($"[{ModEntry.Id}] {Name}: faulted patches not removed yet (turned off meanwhile), trying again in {RetryRemovalAfter[attempt]:F0} s: {what}");
            tree.CreateTimer(RetryRemovalAfter[attempt]).Timeout += () => RemoveWithRetries(patches, attempt + 1);
        }
        catch (Exception e)
        {
            Log.Error($"[{ModEntry.Id}] {Name}: removing faulted patches: {e}");
        }
    }

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType != null)
        {
            type = type.DeclaringType;
        }
        return type;
    }
}

/// <summary>
/// On a method of this mod that only replays a call the game itself makes at that point, with the same arguments (RelicFlashMerge's
/// sound of a merged flash): an exception from inside it is the game's or another mod's, thrown where the game's own call would
/// throw it and passed on to the game's caller as such. PatchGuard doesn't fault or blame the patch whose code holds the replay
/// (nor any frame outside it); this mod's patches called inside the replay are still judged as anywhere.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class GameCallReplayAttribute : Attribute
{
}
