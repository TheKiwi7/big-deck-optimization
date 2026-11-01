// Which Harmony-patched methods the JIT inlines in a session: a caller compiled that way runs the original body, and the
// patch on that call site silently doesn't run. Harmless for a patch that only saves time, wrong for one that counts writes.
// Record: tools/autotest/run.ps1 -Name <scenario with 'patchlist'> -TraceSec 300 -TraceProviders "Microsoft-Windows-DotNETRuntime:0x1000:5"
// Usage: dotnet run --project tools/profiling/inlines -- <file.nettrace> <session log>
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

var patched = new Dictionary<string, string>();
foreach (string line in File.ReadLines(args[1]))
{
    Match m = Regex.Match(line, @"PATCHED (\S+) (.*)$");
    if (m.Success)
    {
        patched[m.Groups[1].Value] = m.Groups[2].Value;
    }
}
Console.WriteLine($"{patched.Count} patched methods in the log");

var inlined = new Dictionary<string, (int Count, HashSet<string> Into)>();
long succeeded = 0;
using var source = new EventPipeEventSource(args[0]);
source.Clr.MethodInliningSucceeded += (MethodJitInliningSucceededTraceData e) =>
{
    succeeded++;
    string key = e.InlineeNamespace.Replace('/', '+') + "::" + e.InlineeName;
    if (patched.ContainsKey(key))
    {
        inlined.TryGetValue(key, out var seen);
        (seen.Into ??= new HashSet<string>()).Add(e.MethodBeingCompiledNamespace + "::" + e.MethodBeingCompiledName);
        inlined[key] = (seen.Count + 1, seen.Into);
    }
};
var refused = new Dictionary<string, HashSet<string>>();
source.Clr.MethodInliningFailed += (MethodJitInliningFailedTraceData e) =>
{
    string key = e.InlineeNamespace.Replace('/', '+') + "::" + e.InlineeName;
    if (patched.ContainsKey(key))
    {
        if (!refused.TryGetValue(key, out HashSet<string>? reasons))
        {
            refused[key] = reasons = new HashSet<string>();
        }
        reasons.Add(e.FailReason);
    }
};
source.Process();
Console.WriteLine($"{succeeded} inlinings in the trace, {inlined.Count} of patched methods:");
foreach (var (key, seen) in inlined.OrderBy(p => p.Key))
{
    Console.WriteLine($"{key}  [{patched[key]}]  x{seen.Count}");
    foreach (string into in seen.Into.Take(6))
    {
        Console.WriteLine($"    into {into}");
    }
}
Console.WriteLine($"{refused.Count} patched methods refused an inlining:");
foreach (var (key, reasons) in refused.OrderBy(p => p.Key))
{
    Console.WriteLine($"{key}  [{patched[key]}]  {string.Join(" | ", reasons)}");
}
