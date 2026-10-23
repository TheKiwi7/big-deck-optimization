// Allocation sampling (GCAllocationTick, ~100 KB each) of a gc-verbose nettrace: bytes per type, per allocating
// game frame (first non-System frame) and per inclusive game frame, within [from, to] seconds of the trace.
// Record: tools/autotest/run.ps1 -Name <scenario> -TraceSec 30 -TraceProfile gc-verbose
// Usage: dotnet run --project tools/profiling/allocs -- <file.nettrace> <from s> <to s> [top]
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

string etlx = TraceLog.CreateFromEventPipeDataFile(args[0]);
double from = double.Parse(args[1]), to = double.Parse(args[2]);
int top = args.Length > 3 ? int.Parse(args[3]) : 30;
using var log = new TraceLog(etlx);
var byType = new Dictionary<string, long>();
var bySite = new Dictionary<string, long>();
var inclusive = new Dictionary<string, long>();
long total = 0;
int gcs = 0;
foreach (var ev in log.Events)
{
    double t = ev.TimeStampRelativeMSec / 1000;
    if (t < from || t > to)
    {
        continue;
    }
    if (ev is GCStartTraceData)
    {
        gcs++;
    }
    if (ev is not GCAllocationTickTraceData a)
    {
        continue;
    }
    long bytes = a.AllocationAmount64;
    total += bytes;
    byType[a.TypeName ?? "?"] = byType.GetValueOrDefault(a.TypeName ?? "?") + bytes;
    var seen = new HashSet<string>();
    string? site = null;
    for (TraceCallStack? s = ev.CallStack(); s != null; s = s.Caller)
    {
        string name = s.CodeAddress.FullMethodName;
        string module = s.CodeAddress.ModuleName;
        if (string.IsNullOrEmpty(name))
        {
            continue;
        }
        string frame = module + "!" + name;
        bool system = module.StartsWith("System", StringComparison.OrdinalIgnoreCase) || module.StartsWith("mscorlib") || module == "coreclr";
        if (site == null && !system)
        {
            site = frame;
        }
        if (!system && seen.Add(frame))
        {
            inclusive[frame] = inclusive.GetValueOrDefault(frame) + bytes;
        }
    }
    site ??= "?";
    bySite[site] = bySite.GetValueOrDefault(site) + bytes;
}
Console.WriteLine($"window {from}-{to} s: sampled {total / 1048576.0:F0} MB, GC starts {gcs}");
void Print(string title, Dictionary<string, long> d)
{
    Console.WriteLine($"--- {title}");
    foreach (var (k, v) in d.OrderByDescending(p => p.Value).Take(top))
    {
        Console.WriteLine($"{v / 1048576.0,8:F1} MB  {(k.Length > 170 ? k[..170] : k)}");
    }
}
Print("by type", byType);
Print("by allocating game frame", bySite);
Print("inclusive game frames", inclusive);
