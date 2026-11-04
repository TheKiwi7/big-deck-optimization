// Dumps a WPR trace (.etl) of the game to text files etwreport.py reads: every CPU sample and off-CPU wait of the game's processes
// with its stack, as module + offset (the engine's native frames have no names here: dwarfsyms reads them from its DWARF).
//   dotnet run --project tools/profiling/etwdump -c Release -- <trace.etl> <out dir> [process name, default SlayTheSpire2]
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
var etl = args[0];
var outDir = args[1];
var name = args.Length > 2 ? args[2] : "SlayTheSpire2";
Directory.CreateDirectory(outDir);
var etlx = Path.Combine(outDir, "trace.etlx");
if (!File.Exists(etlx))
    TraceLog.CreateFromEventTraceLogFile(etl, etlx, new TraceLogOptions { ContinueOnError = true, ConversionLog = TextWriter.Null, MaxEventCount = int.MaxValue });
using var log = new TraceLog(etlx);

var pids = new HashSet<int>();
foreach (var p in log.Processes)
    if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
    {
        pids.Add(p.ProcessID);
        Console.WriteLine($"process {p.ProcessID} {p.Name} start {p.StartTime:HH:mm:ss.fff} cpu {p.CPUMSec:F0} ms cmd {p.CommandLine}");
    }

var stacks = new Dictionary<int, int>();
var addrs = new Dictionary<int, int>();
using var stackOut = new StreamWriter(Path.Combine(outDir, "stacks.tsv"));
using var addrOut = new StreamWriter(Path.Combine(outDir, "addrs.tsv"));
int AddrId(CodeAddressIndex a)
{
    if (addrs.TryGetValue((int)a, out var id)) return id;
    id = addrs.Count;
    addrs[(int)a] = id;
    var ca = log.CodeAddresses;
    var file = ca.ModuleFile(a);
    var rva = file != null ? ca.Address(a) - file.ImageBase : ca.Address(a);
    var mi = ca.MethodIndex(a);
    var method = mi != MethodIndex.Invalid ? ca.Methods.FullMethodName(mi) : "";
    addrOut.WriteLine($"{id}\t{file?.Name ?? "?"}\t{rva:x}\t{method.Replace('\t', ' ')}");
    return id;
}
int StackId(CallStackIndex s)
{
    if (s == CallStackIndex.Invalid) return -1;
    if (stacks.TryGetValue((int)s, out var id)) return id;
    var parent = StackId(log.CallStacks.Caller(s));
    id = stacks.Count;
    stacks[(int)s] = id;
    stackOut.WriteLine($"{id}\t{parent}\t{AddrId(log.CallStacks.CodeAddressIndex(s))}");
    return id;
}

using var samples = new StreamWriter(Path.Combine(outDir, "samples.tsv"));
var off = new Dictionary<int, double>();
long nSamples = 0, nWaits = 0;
samples.WriteLine($"start\t{log.SessionStartTime.ToUniversalTime():O}");
foreach (var ev in log.Events)
{
    if (ev is SampledProfileTraceData s)
    {
        if (!pids.Contains(s.ProcessID)) continue;
        samples.WriteLine($"S\t{s.TimeStampRelativeMSec:F3}\t{s.ProcessID}\t{s.ThreadID}\t{s.ProcessorNumber}\t{StackId(s.CallStackIndex())}");
        nSamples++;
    }
    else if (ev is CSwitchTraceData c)
    {
        var t = c.TimeStampRelativeMSec;
        if (pids.Contains(c.OldProcessID)) off[c.OldThreadID] = t;
        if (pids.Contains(c.NewProcessID) && off.Remove(c.NewThreadID, out var since))
        {
            samples.WriteLine($"W\t{since:F3}\t{c.NewProcessID}\t{c.NewThreadID}\t{t - since:F3}\t{StackId(c.CallStackIndex())}");
            nWaits++;
        }
    }
}
Console.WriteLine($"{nSamples} samples, {nWaits} waits, {stacks.Count} stacks, {addrs.Count} addresses");
