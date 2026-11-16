using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using Godot.Collections;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Five of the game's text effects (<c>[sine]</c>, fade-in, fly-in, jitter, thinking dots; from v0.111 a sixth, <c>[scramble]</c>, which
/// reads it three times) read <c>charFx.Env</c> for every glyph at
/// every frame: GodotSharp gives a new <c>Godot.Collections.Dictionary</c> each time, an object with a finalizer, registered in its
/// disposables tracker (a weak reference and a dictionary node each). On the map at a few hundred frames a second they were three
/// quarters of everything allocated, and dead objects with finalizers are what made each ephemeral collection last: their marking
/// (the "new finalization queue" step) took ~85% of every gen1 pause, 70-95 ms each, mod off as well.
///
/// There, the dictionary the effect read is disposed of once the effect returns: what its finalizer does later (the native
/// reference released, its tracker entry removed), done on the main thread right after its last use. None of these effects keeps it
/// beyond the call (checked by fingerprint); the values they read from it are colors, numbers and booleans. Same text, same pixels.
///
/// Disposed or not, each one still went through the tracker: a <c>WeakReference</c>, which has a finalizer of its own that nothing
/// suppresses, and a node added to and removed from a concurrent dictionary (two thirds of what idle frames allocate). The tracker
/// only serves to dispose of what is left when the engine shuts down; these live for one call. They now skip it
/// (<c>Dictionary.Dispose</c> expects that, and its finalizer stays as a net).
/// </summary>
public static class RichTextEnvRelease
{
    public const string Name = "RichTextEnvRelease";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Released { get; private set; }
    public static long Untracked { get; private set; }

    /// <summary>GodotSharp 4.5.1: the tracker's registration, and what <c>Dictionary.Dispose</c> does with an object it never saw.</summary>
    private const string RegisterFingerprint = "1A192A2B1982B847";
    private const string DisposeFingerprint = "66658970BA5C94BB";

    /// <summary>
    /// The effects and their <c>_ProcessCustomFX</c> (v0.107.1, v0.111.0, v0.113.0 and v0.113.1): <c>Env</c> read into a local or
    /// inline, used, never stored. <c>Later</c>: only in later game versions (<c>RichTextScramble</c> from v0.111), its absence is
    /// no change (not reported as left vanilla on v0.107.1).
    /// </summary>
    private static readonly (string Type, string Fingerprint, bool Later)[] Effects =
    {
        ("RichTextSine", "F115E6206403A0BF", false),
        ("RichTextFadeIn", "3807CB16B275C4CC", false),
        ("RichTextFlyIn", "D379078722018CDE", false),
        ("RichTextJitter", "954F4DE27F361120", false),
        ("RichTextThinkyDots", "44813E7C11DF1717", false),
        ("RichTextScramble", "9AE255C80AF67670", true),
    };

    private static readonly MethodInfo _env = AccessTools.PropertyGetter(typeof(CharFXTransform), nameof(CharFXTransform.Env));
    private static readonly List<MethodBase> _patched = new();
    [ThreadStatic] private static List<Dictionary>? _taken;
    [ThreadStatic] private static bool _reading;
    private static bool _tracking;
    private static long _checkedAt = long.MinValue;
    private static bool _othersPatched;

    public static void Apply(Harmony harmony)
    {
        List<string> left = new();
        foreach ((string type, string fingerprint, bool later) in Effects)
        {
            System.Type? effect = AccessTools.TypeByName("MegaCrit.Sts2.Core.RichTextTags." + type);
            if (effect == null && later)
            {
                continue;
            }
            MethodInfo? method = effect == null ? null : AccessTools.DeclaredMethod(effect, "_ProcessCustomFX", new[] { typeof(CharFXTransform) });
            if (method == null || !PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint))
            {
                left.Add(type);
                continue;
            }
            PatchGuard.Patch(harmony, method, transpiler: new HarmonyMethod(typeof(RichTextEnvRelease), nameof(Transpiler)),
                postfix: new HarmonyMethod(typeof(RichTextEnvRelease), nameof(Postfix)));
            _patched.Add(method);
        }
        if (_patched.Count == 0)
        {
            throw new PatchSkippedException("no text effect has the known code");
        }
        if (left.Count > 0)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: changed or missing, left vanilla: {string.Join(", ", left)}");
        }
        MethodInfo? register = null;
        try
        {
            register = AccessTools.Method(AccessTools.TypeByName("Godot.DisposablesTracker"), "RegisterDisposable")
                ?? throw new MissingMethodException("DisposablesTracker", "RegisterDisposable");
            MethodInfo dispose = AccessTools.Method(typeof(Dictionary), nameof(Dictionary.Dispose), new[] { typeof(bool) })
                ?? throw new MissingMethodException(nameof(Dictionary), nameof(Dictionary.Dispose));
            PatchSafety.RequireFingerprint(register, RegisterFingerprint);
            PatchSafety.RequireFingerprint(dispose, DisposeFingerprint);
            PatchGuard.Patch(harmony, register, prefix: new HarmonyMethod(typeof(RichTextEnvRelease), nameof(RegisterPrefix)));
            _tracking = true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: the tracker's code changed, dictionaries stay registered: {e.Message}");
        }
        catch (Exception e)
        {
            // The tracker part is optional: its failure (Harmony couldn't patch it) mustn't take the effects with it. Only when
            // what it may have installed came off; otherwise the whole patch comes off (the caller removes everything).
            bool removed;
            try
            {
                removed = register == null || RemoveTrackerPatch(harmony, register);
            }
            catch (Exception)
            {
                removed = false;
            }
            if (!removed)
            {
                throw;
            }
            Log.Warn($"[{ModEntry.Id}] {Name}: the tracker could not be patched, dictionaries stay registered: {e}");
        }
        Applied = true;
        PatchSafety.Watch(Name, _patched.ToArray()); // What OthersPatched asks about (display only).
        PatchSafety.WholePatch(Name);
    }

    private static bool RemoveTrackerPatch(Harmony harmony, MethodInfo register)
    {
        List<PatchGuard.Installed> installed = PatchGuard.Owned(harmony).Where(p => p.Original == register).ToList();
        if (installed.Count > 0)
        {
            PatchGuard.Remove(installed);
        }
        return !PatchGuard.Owned(harmony).Any(p => p.Original == register);
    }

    private static bool RegisterPrefix(ref WeakReference<IDisposable>? __result)
    {
        if (!_reading)
        {
            return true;
        }
        Untracked++;
        __result = null;
        return false;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(_env))
            {
                // Same stack: (charFx) -> env.
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RichTextEnvRelease), nameof(Env)))
                    .WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                continue;
            }
            yield return instruction;
        }
    }

    private static Dictionary Env(CharFXTransform charFx)
    {
        if (!Enabled || OthersPatched())
        {
            return charFx.Env;
        }
        _reading = _tracking;
        Dictionary env;
        try
        {
            env = charFx.Env;
        }
        finally
        {
            _reading = false;
        }
        (_taken ??= new List<Dictionary>(4)).Add(env);
        return env;
    }

    private static void Postfix()
    {
        List<Dictionary>? taken = _taken;
        if (taken == null || taken.Count == 0)
        {
            return;
        }
        try
        {
            foreach (Dictionary env in taken)
            {
                env.Dispose();
            }
            Released += taken.Count;
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, text effects' dictionaries left to their finalizers: {e}");
        }
        finally
        {
            taken.Clear();
        }
    }

    /// <summary>Another mod's patch on one of these effects might keep the dictionary: then none is disposed. Read once per tick.</summary>
    private static bool OthersPatched()
    {
        long now = System.Environment.TickCount64;
        if (now != _checkedAt)
        {
            _checkedAt = now;
            // No tolerance: the effects' dictionary is disposed of, a postfix reading it would find it disposed.
            _othersPatched = _patched.Any(m => PatchSafety.IsPatchedByOthers(m));
        }
        return _othersPatched;
    }
}
