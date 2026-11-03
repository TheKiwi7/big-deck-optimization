using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using BigDeckOptimization.Patches;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf relics [n]</c>: what serializing every player's relics costs, as the co-op checksum does after every action: the game's
/// <c>ToSerializable</c>, <c>SavedProperties.From</c> alone, and <see cref="RelicStateReuse"/>'s path; how many relics have no saved
/// property at all (their type saves nothing: <c>From</c> is always null for them) and how many the reuse path builds again.
/// </summary>
public static class RelicBench
{
    public static CmdResult Run(int iterations)
    {
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return new CmdResult(success: false, "No run.");
        }
        List<RelicModel> relics = state.Players.SelectMany(p => p.Relics).ToList();
        MethodInfo? lookup = typeof(SavedProperties).Assembly.GetTypes()
            .Select(t => t.GetMethod("GetJsonPropertiesForType", BindingFlags.Public | BindingFlags.Static, new[] { typeof(Type) }))
            .FirstOrDefault(m => m != null);
        Dictionary<Type, int> propertyCounts = relics.Select(r => r.GetType()).Distinct()
            .ToDictionary(t => t, t => (lookup?.Invoke(null, new object[] { t }) as List<PropertyInfo>)?.Count ?? -1);
        int withoutProps = relics.Count(r => propertyCounts[r.GetType()] == 0);

        double Time(Action pass)
        {
            pass();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                pass();
            }
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        double vanilla = Time(() => { foreach (RelicModel r in relics) r.ToSerializable(); });
        double from = Time(() => { foreach (RelicModel r in relics) SavedProperties.From(r); });
        int verifyEvery = RelicStateReuse.ScalarVerifyEvery;
        RelicStateReuse.ScalarVerifyEvery = 256; // As release builds check.
        long built = RelicStateReuse.Built, reused = RelicStateReuse.Reused, scalar = RelicStateReuse.ScalarReused;
        double reuse = Time(() => { foreach (RelicModel r in relics) RelicStateReuse.SerializeForBench(r); });
        long builtPerPass = (RelicStateReuse.Built - built) / (iterations + 1), reusedPerPass = (RelicStateReuse.Reused - reused) / (iterations + 1);
        long scalarPerPass = (RelicStateReuse.ScalarReused - scalar) / (iterations + 1);
        RelicStateReuse.ScalarVerifyEvery = verifyEvery;
        string why = $"{RelicStateReuse.ScalarWhy} (enabled {RelicStateReuse.ScalarEnabled})";
        bool wasScalar = RelicStateReuse.ScalarEnabled;
        RelicStateReuse.ScalarEnabled = false;
        double reuseFrom = Time(() => { foreach (RelicModel r in relics) RelicStateReuse.SerializeForBench(r); });
        RelicStateReuse.ScalarEnabled = wasScalar;
        string rebuiltTypes = string.Join(", ", relics.Where(r => propertyCounts[r.GetType()] != 0).GroupBy(r => r.GetType().Name)
            .Select(g => $"{g.Key} {g.Count()}x{propertyCounts[g.First().GetType()]}").Take(40));
        string msg = $"relics x{iterations}: {relics.Count} relics of {propertyCounts.Count} types, {withoutProps} with no saved property (lookup {(lookup != null ? "found" : "missing")}); "
            + $"per pass: ToSerializable {vanilla:F3} ms, From {from:F3} ms, reuse path {reuse:F3} ms (built {builtPerPass}, reused {reusedPerPass}, of those without From {scalarPerPass}), reuse path through From {reuseFrom:F3} ms; "
            + $"scalar part applied {RelicStateReuse.ScalarApplied}, verified {RelicStateReuse.ScalarVerified}, last miss: {why}; with properties: {rebuiltTypes}";
        return new CmdResult(success: true, msg);
    }
}
