using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// What other mods do with Harmony after this mod checked it: every Patch and Unpatch, whoever calls it, ends in Harmony's
/// internal <c>PatchFunctions.UpdateWrapper</c> (the method's replacement built again, then detoured). A finalizer there (its
/// shape checked at load; without it, nothing of this and the checks keep their 2 s recheck) gives:
/// <list type="bullet">
/// <item>Exact invalidation: the cached answers of <see cref="PatchSafety"/>'s checks for that method are dropped at once, so a
/// patch landing mid-combat is seen at the next call instead of up to <c>RecheckAfterMs</c> later (<see cref="Epoch"/> counts the
/// changes, for caches of their own).</item>
/// <item>Lost detours: Harmony 2.4 undoes a method's detour before making the new one, so another mod's failed Patch or Unpatch
/// leaves the method running none of its patches, this mod's tracking ones included, while Harmony still lists them. Checked
/// on the next frame for every method another mod rebuilt that carries a patch of this mod: no applied detour faults each part
/// of this mod patching it (normal game for those parts, logged with the method).</item>
/// <item>Hidden IL hooks: a mod patching Harmony's or MonoMod's own code (a transpiler on <c>MethodCreator.CreateReplacement</c>
/// that edits every replacement Harmony builds) changes methods with no patch Harmony lists. While one is there, a method
/// another mod rebuilds (and, found at load, a patched method listing no patch at all) is treated as patched by that mod in
/// every check: its optimizations back off there. Shown in bdopt why.</item>
/// <item>This mod's own code patched by another mod: noted (bdopt why), since its checks can't vouch for code they don't see.</item>
/// </list>
/// Costs nothing per game call: the work runs per Patch or Unpatch (a stack walk to tell this mod's own calls apart) and per frame
/// after one, never in a hot path.
/// </summary>
public static class HarmonyWatch
{
    public const string Name = "HarmonyWatch";

    private static int _epoch;

    /// <summary>Patch and Unpatch calls seen so far (any mod's): a cache built at one epoch may be stale at another.</summary>
    public static int Epoch => Volatile.Read(ref _epoch);

    public static bool Applied { get; private set; }

    private static readonly object _lock = new();
    /// <summary>Owners of patches on Harmony's or MonoMod's own methods (hidden IL hooks), per patched internal method.</summary>
    private static readonly SortedDictionary<string, SortedSet<string>> _hidden = new(StringComparer.Ordinal);
    /// <summary>Methods another mod rebuilt while hidden IL hooks were there (or listing no patch at load then): treated as patched.</summary>
    private static readonly HashSet<(Module Module, int Token)> _suspects = new();
    private static volatile bool _anySuspect;
    /// <summary>This mod's own methods other mods patch (display).</summary>
    private static readonly SortedSet<string> _ownCodePatched = new(StringComparer.Ordinal);
    /// <summary>Methods to check for a lost detour on the next frame, and whether their rebuild threw.</summary>
    private static readonly Dictionary<MethodBase, bool> _pending = new();
    private static bool _checkScheduled;
    /// <summary>Methods found without their detour while carrying this mod's patches (display).</summary>
    private static readonly SortedSet<string> _lost = new(StringComparer.Ordinal);

    public static void Apply(Harmony harmony)
    {
        Type? functions = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions");
        MethodInfo? update = functions?.GetMethod("UpdateWrapper", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        ParameterInfo[]? parameters = update?.GetParameters();
        if (update == null || update.ReturnType != typeof(MethodInfo) || parameters!.Length != 2 || parameters[0].ParameterType != typeof(MethodBase)
            || parameters[0].Name != "original" || parameters[1].ParameterType.FullName != "HarmonyLib.PatchInfo")
        {
            throw new PatchSkippedException("Harmony's PatchFunctions.UpdateWrapper not as expected (another Harmony): other mods' later patches are seen by the 2 s rechecks only");
        }
        // Found before this mod watched anything: hidden IL hooks already in, and the methods they may have edited.
        ScanAtLoad();
        // No PatchGuard: a finalizer of Harmony's own method, which catches everything itself.
        harmony.Patch(update, finalizer: new HarmonyMethod(typeof(HarmonyWatch), nameof(Changed)) { priority = Priority.First });
        Applied = true;
    }

    /// <summary>Hidden IL hooks already there when this mod loads, and (if any) the patched methods listing no patch at all.</summary>
    private static void ScanAtLoad()
    {
        List<MethodBase> patched = Harmony.GetAllPatchedMethods().ToList();
        foreach (MethodBase method in patched)
        {
            if (IsHarmonyInternal(method) && Harmony.GetPatchInfo(method) is { } listed)
            {
                NoteHidden(method, AllPatches(listed));
            }
        }
        if (_hidden.Count == 0)
        {
            return;
        }
        foreach (MethodBase method in patched)
        {
            if (!IsHarmonyInternal(method) && Harmony.GetPatchInfo(method) is { } info && !AllPatches(info).Any())
            {
                Suspect(method);
            }
        }
    }

    /// <summary>
    /// Finalizer of <c>PatchFunctions.UpdateWrapper</c>: runs after every Patch and Unpatch (thrown or not), passes the exception on.
    /// <paramref name=__1/> is the patch list this rebuild uses (UpdateWrapper's second argument): Harmony stores it only after
    /// UpdateWrapper returns, so <c>Harmony.GetPatchInfo</c> here still gives the list from before this call (none on a first patch).
    /// Read only when <paramref name="__exception"/> is null: a failed call stores nothing, and the list Harmony holds is then the
    /// one installed.
    /// </summary>
    public static void Changed(MethodBase original, PatchInfo? __1, Exception? __exception)
    {
        try
        {
            // HookSkip's transpiler kept what the other mods' transpilers made of a hook during this rebuild: confirmed (or dropped)
            // before the epoch moves, so a judgement redone for the new epoch reads the code now running.
            if (original != null)
            {
                TranspilerJudge.RebuildEnded(original, __exception == null);
            }
            Interlocked.Increment(ref _epoch);
            if (original == null)
            {
                return;
            }
            PatchSafety.Invalidate(original);
            if (_ownDepth > 0 || OwnCall())
            {
                return;
            }
            lock (_lock)
            {
                // The list being built (after this Patch, or after this Unpatch's removal), not the one Harmony still holds. Only
                // when UpdateWrapper succeeded: on a throw Harmony never stores that list nor installs its detour, so the list it
                // still holds is the one in place (a failed patch is no hidden hook, nor another mod's patch on this mod's code).
                Patch[] patches = __exception == null && __1 != null ? AllPatches(__1).ToArray()
                    : Harmony.GetPatchInfo(original) is { } listed ? AllPatches(listed).ToArray() : Array.Empty<Patch>();
                if (IsHarmonyInternal(original))
                {
                    NoteHidden(original, patches);
                }
                else if (_hidden.Count > 0)
                {
                    Suspect(original);
                }
                // The test tooling's synthetic patch methods (Bench, a "gated patched postfix" shape) aren't this mod's code.
                if (original.DeclaringType?.Assembly is { } assembly && (assembly == typeof(HarmonyWatch).Assembly || assembly.GetName().Name?.StartsWith(ModEntry.Id + ".", StringComparison.Ordinal) == true)
                    && original.DeclaringType.Namespace?.StartsWith(ModEntry.Id + ".Bench", StringComparison.Ordinal) != true
                    && patches.Any(p => !PatchGuard.IsOurs(p.owner))
                    && _ownCodePatched.Add($"{original.DeclaringType?.Name}.{original.Name}"))
                {
                    Log.Warn($"[{ModEntry.Id}] {Name}: another mod patches this mod's own {original.DeclaringType?.FullName}.{original.Name} ({string.Join(", ", patches.Where(p => !PatchGuard.IsOurs(p.owner)).Select(p => p.owner).Distinct())})");
                }
                _pending[original] = (_pending.TryGetValue(original, out bool failed) && failed) || __exception != null;
                if (!_checkScheduled)
                {
                    _checkScheduled = true;
                    Godot.Callable.From(CheckPending).CallDeferred();
                }
            }
        }
        catch (Exception)
        {
            // Never in the way of another mod's Patch call: a change missed here is still seen by the 2 s rechecks.
        }
    }

    [ThreadStatic] private static int _ownDepth;

    /// <summary>
    /// Marks this mod's own Patch and Unpatch calls on this thread until disposed (ModEntry.TryApply, HarmonyRebuild.Run,
    /// HookSkip's batches): told apart without walking the stack, the costly part at the mod's start (hundreds of patches).
    /// </summary>
    public static OwnScope Own()
    {
        _ownDepth++;
        return default;
    }

    public readonly struct OwnScope : IDisposable
    {
        public void Dispose() => _ownDepth--;
    }

    /// <summary>
    /// Whether this Patch or Unpatch call comes from this mod's own code (its rebuilds were checked already): the first frame
    /// below Harmony's own (and its generated replacements') is this mod's. A foreign mod patching from inside a game call that
    /// one of this mod's patches made is still foreign.
    /// </summary>
    private static bool OwnCall()
    {
        Assembly harmony = typeof(Harmony).Assembly;
        foreach (StackFrame frame in new StackTrace(1, false).GetFrames())
        {
            Type? type = frame.GetMethod()?.DeclaringType;
            if (type == null || type.Assembly == harmony || type == typeof(HarmonyWatch))
            {
                continue;
            }
            // The test tooling's synthetic "other mods" (Bench, bdtest.foreign.*) call from this assembly too: foreign.
            return type.Assembly == typeof(HarmonyWatch).Assembly && type.Namespace?.StartsWith(ModEntry.Id + ".Bench", StringComparison.Ordinal) != true;
        }
        return false;
    }

    /// <summary>
    /// Harmony's replacement pipeline: its internal types (MethodCreator, PatchFunctions, PatchTools...) and MonoMod's code. Its
    /// public API (AccessTools' lookups, Harmony, PatchProcessor) isn't: what a patch there changes shows in the patch lists, or
    /// is a lookup this mod doesn't use (Reflect).
    /// </summary>
    private static bool IsHarmonyInternal(MethodBase method) =>
        method.DeclaringType is { } type && type.Assembly is { } assembly
        && ((assembly == typeof(Harmony).Assembly && (!type.IsVisible || type.Namespace?.StartsWith("MonoMod", StringComparison.Ordinal) == true))
            || assembly.GetName().Name?.StartsWith("MonoMod", StringComparison.Ordinal) == true);

    private static void NoteHidden(MethodBase internalMethod, IEnumerable<Patch> patches)
    {
        string[] owners = patches.Where(p => !PatchGuard.IsOurs(p.owner))
            .Select(p => PatchSafety.ModNameOf(p.PatchMethod?.DeclaringType?.Assembly) ?? p.owner).Distinct().ToArray();
        if (owners.Length == 0)
        {
            return;
        }
        string key = $"{internalMethod.DeclaringType?.Name}.{internalMethod.Name}";
        if (!_hidden.TryGetValue(key, out SortedSet<string>? set))
        {
            _hidden[key] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        if (owners.Any(set.Add))
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: {string.Join(", ", owners)} patch Harmony's own {key}: hidden IL hooks possible, so a method another mod rebuilds from now on is treated as patched by it (normal game for what relies on it)");
            Config.Settings.NotifyStatesChanged();
        }
    }

    private static void Suspect(MethodBase method)
    {
        try
        {
            if (_suspects.Add((method.Module, method.MetadataToken)))
            {
                _anySuspect = true;
                Log.Info($"[{ModEntry.Id}] {Name}: {method.DeclaringType?.Name}.{method.Name} rebuilt by another mod while hidden IL hooks are there: treated as patched by them");
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The hidden IL hooks' owners when this method may carry their code (see the class summary), else none.</summary>
    internal static string[] HiddenOwners(MethodBase method)
    {
        if (!_anySuspect)
        {
            return Array.Empty<string>();
        }
        lock (_lock)
        {
            try
            {
                return _suspects.Contains((method.Module, method.MetadataToken))
                    ? _hidden.Values.SelectMany(owners => owners).Distinct().Select(owner => owner + " (hidden IL hooks)").ToArray()
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// The frame after other mods' Patch or Unpatch calls: a method carrying this mod's patches that lost its detour (a rebuild
    /// that threw after Harmony undid the old one) runs none of them; the parts patching it turn off (normal game there).
    /// </summary>
    private static void CheckPending()
    {
        KeyValuePair<MethodBase, bool>[] pending;
        lock (_lock)
        {
            pending = _pending.ToArray();
            _pending.Clear();
            _checkScheduled = false;
        }
        foreach ((MethodBase original, bool failed) in pending)
        {
            try
            {
                if (failed)
                {
                    Log.Warn($"[{ModEntry.Id}] {Name}: another mod's Patch or Unpatch of {original.DeclaringType?.Name}.{original.Name} failed");
                }
                if (!HarmonyRebuild.Available || Harmony.GetPatchInfo(original) is not { } info)
                {
                    continue;
                }
                string[] ours = AllPatches(info).Select(p => p.owner).Where(PatchGuard.IsOurs).Distinct().ToArray();
                if (ours.Length == 0 || HarmonyRebuild.Replacement(original) != null)
                {
                    continue;
                }
                string[] parts = ours.Select(owner => owner.Length > ModEntry.Harmony.Id.Length ? owner.Substring(ModEntry.Harmony.Id.Length + 1) : owner).ToArray();
                lock (_lock)
                {
                    _lost.Add($"{original.DeclaringType?.Name}.{original.Name}");
                }
                foreach (string part in parts)
                {
                    PatchSafety.Fault(part);
                }
                Log.Error($"[{ModEntry.Id}] {Name}: {original.DeclaringType?.Name}.{original.Name} lost its detour after another mod's Patch or Unpatch (every mod's patches there stopped running): {string.Join(", ", parts)} turned off for this session (normal game)");
                Config.Settings.ApplyAll();
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: detour check of {original.Name} failed: {e.Message}");
            }
        }
    }

    private static IEnumerable<Patch> AllPatches(PatchInfo info) =>
        info.prefixes.Concat(info.postfixes).Concat(info.transpilers).Concat(info.finalizers).Concat(info.innerprefixes).Concat(info.innerpostfixes);

    private static IEnumerable<Patch> AllPatches(HarmonyLib.Patches info) =>
        info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.InnerPrefixes).Concat(info.InnerPostfixes);

    /// <summary>For bdopt why: hidden IL hooks, this mod's own code patched by others, lost detours (empty when none).</summary>
    public static IEnumerable<string> Report()
    {
        lock (_lock)
        {
            foreach ((string method, SortedSet<string> owners) in _hidden)
            {
                yield return $"Hidden IL hooks: {string.Join(", ", owners)} patch Harmony's own {method}; {_suspects.Count} methods rebuilt since are treated as patched by them";
            }
            if (_ownCodePatched.Count > 0)
            {
                yield return $"This mod's own code patched by other mods: {string.Join(", ", _ownCodePatched)}";
            }
            if (_lost.Count > 0)
            {
                yield return $"Lost detours after other mods' Patch or Unpatch (the parts patching them turned off): {string.Join(", ", _lost)}";
            }
        }
    }
}
