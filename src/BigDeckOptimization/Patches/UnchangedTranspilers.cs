using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Other mods' transpilers that leave a method exactly as the game wrote it, let stand where a part relies on that method's code
/// (DESIGN "Transpilers leaving the game's code unchanged"). Generic, judged on the code Harmony compiled, never on who wrote it:
/// a mod transpiling every method of some shape (Unlimited's <c>PacketSizePatch</c> rewrites a list-width constant 3 before
/// <c>WriteList</c>/<c>ReadList</c> in every public <c>Serialize</c>/<c>Deserialize</c>; the relic, card and pile serializers
/// have none) changes nothing in the methods where its pattern isn't found, yet each counts as patched.
/// <para>
/// The rule: on a registered method (<see cref="Register"/>: the serializers RelicStateReuse hands a reused object to, those
/// whose output SnapshotCardReuse keeps), every other mod's transpiler is let stand together when their combined result, as
/// Harmony compiled it, is the game's code instruction for instruction (<see cref="TranspilerJudge.Unchanged"/>: same opcodes and
/// operands, branch targets, exception blocks). The compiled body is then the game's body: whatever the part assumes of the
/// method holds as with no patch. Their prefixes, postfixes, finalizers and inner patches are not concerned (still another
/// mod's patch), nor anything else.
/// </para>
/// <para>
/// What Harmony compiled: a transpiler of this mod at <c>Priority.Last</c> (<see cref="Capture"/>, which changes nothing) keeps
/// what it is handed at each rebuild, confirmed once the rebuild ended well (TranspilerJudge's capture, HarmonyWatch's
/// finalizer). It is installed only where another mod's transpiler is (at the part's load, or the frame after another mod
/// patched such a method: <see cref="AfterForeignRebuild"/>), never runs another mod's transpiler again itself (Harmony's own
/// rebuild does, as at any other mod's patch), and stays (a method left without other transpilers is judged as before).
/// Refused (the part backs off): no confirmed capture yet (HarmonyWatch not applied, a rebuild failed or under way), another
/// mod's transpiler sorted after the capture (it would change what the capture saw), another transpiler of this mod there, a
/// transpiler made by a factory, a generic method (captures are kept per method definition), any difference at all: a constant
/// replaced by a call (even a pure one returning the same value), an inserted <c>nop</c>, a moved branch.
/// </para>
/// </summary>
internal static class UnchangedTranspilers
{
    internal const string Name = "UnchangedTranspilers";

    private static readonly ConcurrentDictionary<MethodBase, bool> _registered = new();
    private static readonly MethodInfo _capture = Reflect.Method(typeof(UnchangedTranspilers), nameof(Capture))!;
    private static Harmony? _harmony;

    private sealed class Answer
    {
        public MethodInfo[] Allowed = Array.Empty<MethodInfo>();
        public int Epoch;
        public int CaptureVersion;
    }

    private static readonly ConcurrentDictionary<MethodBase, Answer> _answers = new();
    /// <summary>The verdict last logged per method (logged once per change).</summary>
    private static readonly ConcurrentDictionary<MethodBase, string> _logged = new();
    private static readonly ConcurrentDictionary<MethodBase, byte> _queued = new();
    /// <summary>Decide sets the patches' methods before Harmony's sort: one at a time.</summary>
    private static readonly object _deciding = new();

#if BENCH
    /// <summary>Test builds: methods whose other mods' transpilers were let stand, and refused (counted per answer computed).</summary>
    public static long Kept, Refused;
#endif

    /// <summary>
    /// Methods whose other mods' transpilers a part lets stand when they leave the game's code unchanged (<see cref="Allowed"/>).
    /// Generic methods and members of generic types are left out (always strict). Installs the capture at once where another
    /// mod's transpiler already is (a part's Apply: main thread). Never throws.
    /// </summary>
    internal static void Register(IEnumerable<MethodBase?> methods)
    {
        foreach (MethodBase? method in methods)
        {
            try
            {
                if (method == null || method.IsGenericMethod || method.DeclaringType?.IsGenericType == true || !_registered.TryAdd(method, true))
                {
                    continue;
                }
                InstallIfNeeded(method);
            }
            catch (Exception e)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: {Describe(method)} not registered: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The frame after another mod's Patch or Unpatch of <paramref name="original"/> (HarmonyWatch, main thread): the capture
    /// installed when a registered method now carries another mod's transpiler without it. Never throws.
    /// </summary>
    internal static void AfterForeignRebuild(MethodBase original)
    {
        try
        {
            if (_registered.ContainsKey(original))
            {
                InstallIfNeeded(original);
            }
        }
        catch (Exception)
        {
            // The method stays judged without a capture: refused.
        }
    }

    /// <summary>
    /// The other mods' transpilers on <paramref name="method"/> to let stand: all of them when their combined result is the game's
    /// code (see the class summary), else none. Empty for a method not registered. Cached per <see cref="HarmonyWatch.Epoch"/> and
    /// capture; never throws (a question it can't answer lets nothing stand).
    /// </summary>
    internal static MethodInfo[] Allowed(MethodBase? method)
    {
        if (method == null || !_registered.ContainsKey(method) || !HarmonyWatch.Applied)
        {
            return Array.Empty<MethodInfo>();
        }
        try
        {
            int epoch = HarmonyWatch.Epoch, capture = TranspilerJudge.CaptureVersion;
            if (_answers.TryGetValue(method, out Answer? known) && known.Epoch == epoch && known.CaptureVersion == capture)
            {
                return known.Allowed;
            }
            MethodInfo[] allowed;
            lock (_deciding)
            {
                allowed = Decide(method);
            }
            _answers[method] = new Answer { Allowed = allowed, Epoch = epoch, CaptureVersion = capture };
            return allowed;
        }
        catch (Exception)
        {
            return Array.Empty<MethodInfo>();
        }
    }

    /// <summary><paramref name="others"/> (the part's own allowed patches) plus <see cref="Allowed"/> for <paramref name="method"/>.</summary>
    internal static MethodInfo[] With(MethodInfo[] others, MethodBase? method)
    {
        MethodInfo[] allowed = Allowed(method);
        return allowed.Length == 0 ? others : others.Concat(allowed).ToArray();
    }

    private static MethodInfo[] Decide(MethodBase method)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        Patch[] transpilers = info?.Transpilers.ToArray() ?? Array.Empty<Patch>();
        Patch[] foreign = transpilers.Where(p => !PatchGuard.IsOurs(p.owner)).ToArray();
        if (foreign.Length == 0)
        {
            return Array.Empty<MethodInfo>();
        }
        string owners = string.Join(", ", foreign.Select(p => p.owner).Distinct());
        var methods = new MethodInfo[transpilers.Length];
        for (int i = 0; i < transpilers.Length; i++)
        {
            MethodInfo? patch = PatchSafety.PatchMethodOf(transpilers[i]);
            if (patch == null)
            {
                return Refuse(method, owners, "a transpiler's method not found");
            }
            // Harmony's sort would call a factory: never run another mod's code here.
            if (PatchSafety.IsFactory(patch))
            {
                return Refuse(method, owners, "a transpiler made by a factory method");
            }
            methods[i] = patch;
        }
        Patch[] ours = transpilers.Where(p => PatchGuard.IsOurs(p.owner)).ToArray();
        if (ours.Length == 0)
        {
            // Installed the next frame (main thread): refused until its rebuild is confirmed.
            Queue(method);
            return Refuse(method, owners, "no capture of the compiled code yet", log: false);
        }
        if (ours.Length != 1 || PatchSafety.PatchMethodOf(ours[0])?.MethodHandle != _capture.MethodHandle)
        {
            return Refuse(method, owners, "another transpiler of this mod is there");
        }
        for (int i = 0; i < transpilers.Length; i++)
        {
            transpilers[i].PatchMethod = methods[i];
        }
        List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(method, transpilers);
        if (sorted.Count == 0 || sorted[^1].MethodHandle != _capture.MethodHandle)
        {
            return Refuse(method, owners, "another mod's transpiler runs after this mod's capture");
        }
        if (!TranspilerJudge.TryCaptured(method, out TranspilerJudge.Ins[] compiled))
        {
            return Refuse(method, owners, "no confirmed capture of the compiled code yet", log: false);
        }
        TranspilerJudge.Ins[] original = PatchProcessor.GetOriginalInstructions(method).Select(TranspilerJudge.Ins.From).ToArray();
        if (TranspilerJudge.Unchanged(original, compiled) is { } why)
        {
            return Refuse(method, owners, why);
        }
        Note(method, $"other mods' transpilers ({owners}) leave the game's code unchanged ({original.Length} instructions, the same): let stand");
#if BENCH
        System.Threading.Interlocked.Increment(ref Kept);
#endif
        return foreign.Select(p => PatchSafety.PatchMethodOf(p)!).ToArray();
    }

    private static MethodInfo[] Refuse(MethodBase method, string owners, string why, bool log = true)
    {
        if (log)
        {
            Note(method, $"other mods' transpilers ({owners}) refused ({why}): what relies on it backs off");
        }
#if BENCH
        System.Threading.Interlocked.Increment(ref Refused);
#endif
        return Array.Empty<MethodInfo>();
    }

    private static void Note(MethodBase method, string text)
    {
        if (_logged.TryGetValue(method, out string? last) && last == text)
        {
            return;
        }
        _logged[method] = text;
        Log.Info($"[{ModEntry.Id}] {Name}: {Describe(method)}: {text}");
    }

    /// <summary>The capture installed on the next frame (main thread), once per method until it ran.</summary>
    private static void Queue(MethodBase method)
    {
        if (_queued.TryAdd(method, 0))
        {
            Godot.Callable.From(() =>
            {
                _queued.TryRemove(method, out _);
                AfterForeignRebuild(method);
            }).CallDeferred();
        }
    }

    /// <summary>
    /// Adds the capture where another mod's transpiler is and this mod's capture isn't (a rebuild: Harmony runs every transpiler
    /// again, as at any mod's patch; refused by HarmonyRebuild where that can't be done safely).
    /// </summary>
    private static void InstallIfNeeded(MethodBase method)
    {
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        if (info == null || !info.Transpilers.Any(p => !PatchGuard.IsOurs(p.owner))
            || info.Transpilers.Any(p => PatchGuard.IsOurs(p.owner) && PatchSafety.PatchMethodOf(p)?.MethodHandle == _capture.MethodHandle))
        {
            return;
        }
        try
        {
            _harmony ??= new Harmony(ModEntry.Harmony.Id + "." + Name);
            HarmonyRebuild.Run(method, "capturing other mods' transpilers",
                () => _harmony.Patch(method, transpiler: new HarmonyMethod(_capture) { priority = Priority.Last }));
            Log.Info($"[{ModEntry.Id}] {Name}: {Describe(method)}: other mods' transpilers there, the code they produce is captured to be judged");
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {Describe(method)}: other mods' transpilers can't be captured, what relies on it backs off: {e.Message}");
        }
    }

    /// <summary>
    /// What every other mod's transpiler made of a registered method, kept for <see cref="Decide"/> and handed on unchanged.
    /// Harmony runs it again at every rebuild of the method, inside any mod's Patch call: never throws.
    /// </summary>
    private static IEnumerable<CodeInstruction> Capture(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> input = instructions as List<CodeInstruction> ?? instructions.ToList();
        TranspilerJudge.Capture(__originalMethod, input);
        return input;
    }

    private static string Describe(MethodBase? method) => $"{method?.DeclaringType?.Name}.{method?.Name}";
}
