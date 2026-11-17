using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BigDeckOptimization.Bench;

/// <summary>
/// Synthetic "other mods" that must be in place before this mod applies anything, installed from the launch arguments
/// (<c>--bdforeign &lt;shape&gt;</c>, added by the launch scripts for a scenario's <c>launchforeign &lt;shape&gt;</c> line), under
/// Harmony ids <c>bdtest.foreign.&lt;shape&gt;</c>. A scenario's <c>launchforeign</c> line fails unless the shape was installed.
/// <list type="bullet">
/// <item><c>reflection-intercept</c>: what a cross-version compatibility mod does to Harmony's reflection lookups, for every caller.
/// A failed <c>AccessTools.Method</c>/<c>DeclaredMethod</c> on <c>Hook</c> is answered with the hook of the other game version's
/// name (BeforeTurnEnd and BeforeSideTurnEnd, AfterTurnEnd and AfterSideTurnEnd, both ways); a failed <c>AccessTools.Field</c> of
/// <c>Mod.assembly</c> or <c>Mod.assemblies</c> (the one this game version lacks) with a stand-in FieldInfo giving the core library
/// for every mod. This mod's lookups go through the runtime (Reflect) and its mod names through the game's own field, so:
/// every hook row matches (no "left vanilla" from a substituted hook), the substitutions are logged as such, and mods keep their
/// names (<c>foreignpatch modname</c>).</item>
/// <item><c>model-gated-pushpop</c>: the PushModel postfix and PopModel prefix gated on their <c>model</c> (Squeeze), at the launch:
/// patched before any caller is compiled, on and off must count the same calls (foreign-helpers-load-2k-on/off).</item>
/// </list>
/// </summary>
public static class ForeignLaunch
{
    public const string Arg = "--bdforeign";

    private static readonly HashSet<string> _installed = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> HookNames = new(StringComparer.Ordinal)
    {
        ["BeforeTurnEnd"] = "BeforeSideTurnEnd", ["BeforeSideTurnEnd"] = "BeforeTurnEnd",
        ["AfterTurnEnd"] = "AfterSideTurnEnd", ["AfterSideTurnEnd"] = "AfterTurnEnd",
    };

    /// <summary>Installs the shapes the launch arguments name (AutoTest.Apply, at the mod's start).</summary>
    public static void FromLaunch(string[] launchArgs)
    {
        for (int i = 0; i + 1 < launchArgs.Length; i++)
        {
            if (launchArgs[i] == Arg)
            {
                Install(launchArgs[i + 1]);
            }
        }
    }

    private static void Install(string shape)
    {
        var harmony = new Harmony(ForeignPatches.IdPrefix + shape);
        switch (shape)
        {
            case "reflection-intercept":
                var method = new HarmonyMethod(typeof(ForeignLaunch), nameof(MethodAnswer));
                harmony.Patch(typeof(AccessTools).GetMethod(nameof(AccessTools.Method), new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) }), postfix: method);
                harmony.Patch(typeof(AccessTools).GetMethod(nameof(AccessTools.DeclaredMethod), new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) }), postfix: method);
                harmony.Patch(typeof(AccessTools).GetMethod(nameof(AccessTools.Field), new[] { typeof(Type), typeof(string) }),
                    postfix: new HarmonyMethod(typeof(ForeignLaunch), nameof(FieldAnswer)));
                break;
            case "model-gated-pushpop":
                ForeignPatches.InstallModelGatesAtLaunch(harmony);
                break;
            default:
                throw new ArgumentException($"unknown launch shape '{shape}' (reflection-intercept, model-gated-pushpop)");
        }
        _installed.Add(shape);
        Log.Info($"[{ModEntry.Id}] FOREIGN launch shape {shape} installed as {ForeignPatches.IdPrefix + shape}");
    }

    /// <summary>Script <c>launchforeign &lt;shape&gt;</c>: fails unless the shape was installed at the launch.</summary>
    public static string Require(string shape)
    {
        if (!_installed.Contains(shape))
        {
            throw new InvalidOperationException($"launch shape {shape} not installed: launch with {Arg} {shape} (the launch scripts add it for a 'launchforeign' line)");
        }
        // What the interception answers now, for the log: the substitution must really happen for the test to mean anything.
        if (shape == "reflection-intercept")
        {
            string answers = string.Join(", ", HookNames.Keys.Where(name => typeof(Hook).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null)
                .Select(name => $"{name} -> {AccessTools.Method(typeof(Hook), name)?.Name ?? "nothing"}"));
            // The runtime's own lookup (not AccessTools, which the shape intercepts), any visibility: the name this version lacks.
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            FieldInfo? stand = AccessTools.Field(typeof(Mod), typeof(Mod).GetField("assemblies", any) == null ? "assemblies" : "assembly");
            return $"LAUNCHFOREIGN {shape}: Harmony answers {answers}; Mod field stand-in {(stand is StandInField ? "in place" : "missing")}";
        }
        return $"LAUNCHFOREIGN {shape}";
    }

    public static void MethodAnswer(Type type, string name, Type[]? parameters, ref MethodInfo? __result)
    {
        if (__result == null && type == typeof(Hook) && HookNames.TryGetValue(name, out string? other))
        {
            __result = typeof(Hook).GetMethod(other, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }
    }

    public static void FieldAnswer(Type type, string name, ref FieldInfo? __result)
    {
        if (__result == null && type == typeof(Mod) && name is "assembly" or "assemblies")
        {
            __result = new StandInField(name);
        }
    }

    /// <summary>A FieldInfo that is not the runtime's: every mod's "assembly" is the core library.</summary>
    private sealed class StandInField(string name) : FieldInfo
    {
        public override string Name => name;
        public override Type? DeclaringType => typeof(Mod);
        public override Type? ReflectedType => typeof(Mod);
        public override Type FieldType => name == "assembly" ? typeof(Assembly) : typeof(List<Assembly>);
        public override FieldAttributes Attributes => FieldAttributes.Public;
        public override RuntimeFieldHandle FieldHandle => throw new NotSupportedException();
        public override object? GetValue(object? obj) => name == "assembly" ? typeof(object).Assembly : new List<Assembly> { typeof(object).Assembly };
        public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, CultureInfo? culture) => throw new NotSupportedException();
        public override object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => Array.Empty<object>();
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }
}
