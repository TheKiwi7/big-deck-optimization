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
/// Five of the game's text effects (<c>[sine]</c>, fade-in, fly-in, jitter, thinking dots) read <c>charFx.Env</c> for every glyph at
/// every frame: GodotSharp gives a new <c>Godot.Collections.Dictionary</c> each time, an object with a finalizer, registered in its
/// disposables tracker (a weak reference and a dictionary node each). On the map at a few hundred frames a second they were three
/// quarters of everything allocated, and dead objects with finalizers are what made each ephemeral collection last: their marking
/// (the "new finalization queue" step) took ~85% of every gen1 pause, 70-95 ms each, mod off as well.
///
/// There, the dictionary the effect read is disposed of once the effect returns: what its finalizer does later (the native
/// reference released, its tracker entry removed), done on the main thread right after its last use. None of these effects keeps it
/// beyond the call (checked by fingerprint); the values they read from it are colors, numbers and booleans. Same text, same pixels.
/// </summary>
public static class RichTextEnvRelease
{
    public const string Name = "RichTextEnvRelease";

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Released { get; private set; }

    /// <summary>The effects and their <c>_ProcessCustomFX</c> (v0.107.1 and v0.111): <c>Env</c> read into a local, used, never stored.</summary>
    private static readonly (string Type, string Fingerprint)[] Effects =
    {
        ("RichTextSine", "F115E6206403A0BF"),
        ("RichTextFadeIn", "3807CB16B275C4CC"),
        ("RichTextFlyIn", "D379078722018CDE"),
        ("RichTextJitter", "954F4DE27F361120"),
        ("RichTextThinkyDots", "44813E7C11DF1717"),
    };

    private static readonly MethodInfo _env = AccessTools.PropertyGetter(typeof(CharFXTransform), nameof(CharFXTransform.Env));
    private static readonly List<MethodBase> _patched = new();
    [ThreadStatic] private static List<Dictionary>? _taken;
    private static long _checkedAt = long.MinValue;
    private static bool _othersPatched;

    public static void Apply(Harmony harmony)
    {
        List<string> left = new();
        foreach ((string type, string fingerprint) in Effects)
        {
            MethodInfo? method = AccessTools.TypeByName("MegaCrit.Sts2.Core.RichTextTags." + type) is { } effect
                ? AccessTools.DeclaredMethod(effect, "_ProcessCustomFX", new[] { typeof(CharFXTransform) }) : null;
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
        Applied = true;
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
        Dictionary env = charFx.Env;
        if (Enabled && !OthersPatched())
        {
            (_taken ??= new List<Dictionary>(4)).Add(env);
        }
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
            _othersPatched = _patched.Any(m => PatchSafety.IsPatchedByOthers(m));
        }
        return _othersPatched;
    }
}
