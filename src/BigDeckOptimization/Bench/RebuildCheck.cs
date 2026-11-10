using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>rebuildcheck</c>: whether every mod's patches still run after this mod's Harmony rebuilds (<see cref="HarmonyRebuild"/>).
/// Throws (a FAILED line) on what a player would hit; the <c>raw</c> forms are the controls, v1.0.0's way, and only report.
///   rebuildcheck                  every patched method detoured; in a run, Limitless's relic bar in its scroll container and
///                                 its show/hide postfixes not throwing (the co-op profile's back button, v1.0.0)
///   rebuildcheck again X.M        X.M built again through HarmonyRebuild: refused or done, never left without its detour
///   rebuildcheck againraw X.M     the same straight through Harmony (control)
///   rebuildcheck force X.M        a rebuild through HarmonyRebuild that can't compile (a transpiler emitting invalid IL):
///                                 the previous replacement must be back
///   rebuildcheck forceraw X.M     the same straight through Harmony (control)
///   rebuildcheck owners X.M       who patches X.M (fails when nobody does: a mod expected in the session isn't loaded)
/// </summary>
internal static class RebuildCheck
{
    private static readonly Harmony _harmony = new(ModEntry.Harmony.Id + ".RebuildCheck");

    public static void Run(string arg, Action<string> say)
    {
        string[] words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0] : "";
        if (verb == "")
        {
            CheckAll(say);
            return;
        }
        if (words.Length < 2)
        {
            throw new ArgumentException("rebuildcheck again|againraw|force|forceraw <Type.Method>");
        }
        MethodBase method = Harmony.GetAllPatchedMethods().FirstOrDefault(m => HarmonyRebuild.Describe(m) == words[1])
            ?? throw new ArgumentException($"no patched method {words[1]}");
        if (verb == "owners")
        {
            say($"  {words[1]} patched by {string.Join(", ", (IEnumerable<string>?)Harmony.GetPatchInfo(method)?.Owners ?? Array.Empty<string>())}, detoured {HarmonyRebuild.Replacement(method) != null}");
            return;
        }
        MethodBase? before = HarmonyRebuild.Replacement(method);
        say($"  {words[1]}: detoured {before != null}, blocker {HarmonyRebuild.Blocker(method) ?? "none"}");
        Func<MethodInfo> rebuild = verb switch
        {
            "again" or "againraw" => () => new PatchProcessor(_harmony, method).Patch(),
            "force" or "forceraw" => () => _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(RebuildCheck), nameof(Invalid))),
            _ => throw new ArgumentException($"rebuildcheck: unknown {verb}"),
        };
        bool raw = verb.EndsWith("raw", StringComparison.Ordinal);
        string outcome;
        try
        {
            if (raw)
            {
                rebuild();
            }
            else
            {
                HarmonyRebuild.Run(method, "rebuildcheck " + verb, rebuild);
            }
            outcome = "built again";
        }
        catch (Exception e)
        {
            outcome = $"{e.GetType().Name}: {e.Message}";
        }
        MethodBase? after = HarmonyRebuild.Replacement(method);
        bool kept = after != null && (after == before || outcome == "built again");
        say($"  {words[1]} {verb}: {outcome}; detoured {after != null}, {(after == before ? "same replacement" : "new replacement")}"
            + $" (refused {HarmonyRebuild.Refused}, restored {HarmonyRebuild.Restored}, lost {HarmonyRebuild.Lost})");
        if (raw)
        {
            say($"  control {verb}: {(kept ? "patches kept" : "PATCHES LOST")}");
        }
        else if (!kept)
        {
            throw new InvalidOperationException($"{words[1]} lost its patches after rebuildcheck {verb}");
        }
        else
        {
            say($"  ok: {words[1]} keeps its patches");
        }
    }

    /// <summary>Pops from an empty stack first: the JIT rejects the replacement ("invalid program").</summary>
    private static IEnumerable<CodeInstruction> Invalid(IEnumerable<CodeInstruction> instructions) =>
        instructions.Prepend(new CodeInstruction(OpCodes.Pop));

    private static void CheckAll(Action<string> say)
    {
        List<MethodBase> unapplied = HarmonyRebuild.Unapplied();
        say($"  HarmonyRebuild available {HarmonyRebuild.Available}, patched methods {Harmony.GetAllPatchedMethods().Count()}, without detour {unapplied.Count}"
            + $" (refused {HarmonyRebuild.Refused}, restored {HarmonyRebuild.Restored}, lost {HarmonyRebuild.Lost}, InlineGuard recompiled {InlineGuard.Recompiled})");
        var problems = new List<string>();
        if (!HarmonyRebuild.Available)
        {
            problems.Add("HarmonyRebuild not available");
        }
        if (unapplied.Count > 0)
        {
            problems.Add("without detour: " + string.Join(", ", unapplied.Select(HarmonyRebuild.Describe)));
        }
        if (NRun.Instance?.GlobalUi?.RelicInventory is { } relics)
        {
            bool limitless = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Limitless");
            Node? parent = relics.GetParent();
            say($"  relic bar parent {parent?.GetType().Name}, Limitless {limitless}");
            if (limitless && parent is not ScrollContainer)
            {
                problems.Add($"relic bar not in Limitless's scroll container (parent {parent?.GetType().Name})");
            }
            foreach ((string name, Action action) in new (string, Action)[] { ("AnimHide", relics.AnimHide), ("AnimShow", relics.AnimShow) })
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    problems.Add($"relic bar {name} threw {e.GetType().Name}: {e.Message}");
                }
            }
        }
        else
        {
            say("  no run: relic bar not checked");
        }
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("PROBLEM " + string.Join("; ", problems));
        }
        say("  ok: every patched method detoured" + (NRun.Instance != null ? ", relic bar shows and hides" : ""));
    }
}
