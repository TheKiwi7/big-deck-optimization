// Function table of a PE file carrying DWARF (the game's engine: no .pdb, ~800 MB of .debug_* sections in the exe):
// every subprogram with code, as "start rva, end rva, qualified name", sorted, for etwreport.py.
//   dotnet run --project tools/profiling/dwarfsyms -c Release -- <SlayTheSpire2.exe> <funcs.tsv>
using System.Buffers.Binary;
using System.Text;

var bytes = File.ReadAllBytes(args[0]);
int pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3c));
int nSec = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6));
long symPtr = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe + 12));
int nSym = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pe + 16));
int optSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
int opt = pe + 24;
ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(opt + 24));
long strTab = symPtr + 18L * nSym;
var sections = new Dictionary<string, (int off, int size)>();
for (int i = 0; i < nSec; i++)
{
    int o = opt + optSize + i * 40;
    var raw = Encoding.ASCII.GetString(bytes, o, 8).TrimEnd('\0');
    if (raw.StartsWith('/'))
    {
        int s = (int)strTab + int.Parse(raw[1..]);
        int e = Array.IndexOf(bytes, (byte)0, s);
        raw = Encoding.ASCII.GetString(bytes, s, e - s);
    }
    int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(o + 8));
    sections[raw] = (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(o + 20)), size);
}
var info = sections[".debug_info"];
var abbrevSec = sections[".debug_abbrev"];
var strSec = sections[".debug_str"];
var rangesSec = sections.GetValueOrDefault(".debug_ranges");

string Str(int off)
{
    int s = strSec.off + off;
    int e = Array.IndexOf(bytes, (byte)0, s);
    return Encoding.UTF8.GetString(bytes, s, e - s);
}

var funcs = new List<(ulong lo, ulong hi, string name)>();
var abbrevCache = new Dictionary<long, Dictionary<ulong, Abbrev>>();
var versions = new Dictionary<int, int>();
int pos = info.off, end = info.off + info.size, units = 0;

while (pos < end)
{
    int unitStart = pos;
    long len = U32(ref pos);
    int unitEnd = pos + (int)len;
    int version = U16(ref pos);
    versions[version] = versions.GetValueOrDefault(version) + 1;
    long abbrevOff;
    if (version >= 5)
    {
        int unitType = bytes[pos++];
        pos++;
        abbrevOff = U32(ref pos);
        if (unitType is 4 or 5) pos += 8;
        if (unitType is 2 or 6) pos += 12;
    }
    else
    {
        abbrevOff = U32(ref pos);
        pos++;
    }
    var abbrevs = Abbrevs(abbrevOff);
    units++;
    // Names by DIE offset within this unit, with their scope: subprograms point at them through specification or abstract origin.
    var names = new Dictionary<int, string>();
    var scope = new List<(int depth, string name)>();
    int depth = 0;
    ulong cuBase = 0;
    while (pos < unitEnd)
    {
        int dieOff = pos - unitStart;
        ulong code = Uleb(ref pos);
        if (code == 0)
        {
            depth--;
            while (scope.Count > 0 && scope[^1].depth >= depth) scope.RemoveAt(scope.Count - 1);
            continue;
        }
        var ab = abbrevs[code];
        string? name = null, linkage = null;
        ulong lo = 0, hi = 0, rangesOff = ulong.MaxValue;
        bool hiIsOffset = false, hasLo = false, hasHi = false;
        int spec = -1;
        foreach (var (at, form0, implicitConst) in ab.Attrs)
        {
            ulong v = 0;
            string? s = null;
            int f = form0;
            if (f == 0x16) f = (int)Uleb(ref pos);
            bool isRef = false;
            switch (f)
            {
                case 0x01: v = U64(ref pos); break; // addr
                case 0x0b: case 0x0c: case 0x11: v = bytes[pos++]; isRef = f == 0x11; break;
                case 0x05: case 0x12: v = (ulong)U16(ref pos); isRef = f == 0x12; break;
                case 0x06: case 0x13: v = (ulong)(uint)U32(ref pos); isRef = f == 0x13; break;
                case 0x07: case 0x14: v = U64(ref pos); isRef = f == 0x14; break;
                case 0x20: v = U64(ref pos); break; // ref_sig8
                case 0x0d: Sleb(ref pos); break;
                case 0x0f: v = Uleb(ref pos); break;
                case 0x15: v = Uleb(ref pos); isRef = true; break;
                case 0x08: { int e = Array.IndexOf(bytes, (byte)0, pos); s = Encoding.UTF8.GetString(bytes, pos, e - pos); pos = e + 1; break; }
                case 0x0e: s = Str(U32(ref pos)); break;
                case 0x1f: U32(ref pos); break; // line_strp
                case 0x10: case 0x17: v = (ulong)(uint)U32(ref pos); break; // ref_addr, sec_offset
                case 0x18: case 0x09: { ulong n = Uleb(ref pos); pos += (int)n; break; }
                case 0x0a: { int n = bytes[pos++]; pos += n; break; }
                case 0x03: { int n = U16(ref pos); pos += n; break; }
                case 0x04: { int n = U32(ref pos); pos += n; break; }
                case 0x19: v = 1; break; // flag_present
                case 0x21: v = (ulong)implicitConst; break;
                case 0x1e: pos += 16; break;
                case 0x1a: case 0x1b: case 0x22: case 0x23: Uleb(ref pos); break;
                case 0x25: case 0x29: pos += 1; break;
                case 0x26: case 0x2a: pos += 2; break;
                case 0x27: case 0x2b: pos += 3; break;
                case 0x28: case 0x2c: pos += 4; break;
                case 0x1f20: case 0x1f21: U32(ref pos); break; // GNU_ref_alt, GNU_strp_alt
                default: throw new InvalidDataException($"form 0x{f:x} at {pos}");
            }
            switch (at)
            {
                case 0x03: name = s; break;
                case 0x6e: case 0x2007: linkage = s; break;
                case 0x11: lo = v; hasLo = true; break;
                case 0x12: hi = v; hasHi = true; hiIsOffset = f != 0x01; break;
                case 0x55: rangesOff = v; break;
                case 0x47: case 0x31:
                    if (isRef) spec = (int)v;
                    else if (f == 0x10) spec = (int)v - (unitStart - info.off);
                    break;
            }
        }
        if (ab.Tag == 0x11 && hasLo) cuBase = lo;
        string? full = null;
        if (name != null)
        {
            full = name;
            for (int i = scope.Count - 1; i >= 0; i--) full = scope[i].name + "::" + full;
        }
        else if (spec >= 0)
            names.TryGetValue(spec, out full);
        if (full != null) names[dieOff] = full;
        if (ab.Tag == 0x2e)
        {
            var n = full ?? linkage ?? "?";
            if (hasLo && hasHi && lo != 0)
                funcs.Add((lo, hiIsOffset ? lo + hi : hi, n));
            else if (rangesOff != ulong.MaxValue && rangesSec.size > 0)
            {
                int r = rangesSec.off + (int)rangesOff;
                ulong b = cuBase;
                while (true)
                {
                    ulong a = U64(ref r), c = U64(ref r);
                    if (a == 0 && c == 0) break;
                    if (a == ulong.MaxValue) { b = c; continue; }
                    funcs.Add((a + b, c + b, n));
                }
            }
        }
        if (ab.Children)
        {
            if (ab.Tag is 0x39 or 0x02 or 0x13 or 0x17 && name != null) scope.Add((depth, name));
            depth++;
        }
    }
    pos = unitEnd;
}
funcs.Sort((a, b) => a.lo.CompareTo(b.lo));
using (var w = new StreamWriter(args[1]))
    foreach (var (lo, hi, n) in funcs)
        w.WriteLine($"{lo - imageBase:x}\t{hi - imageBase:x}\t{n}");
Console.WriteLine($"image base {imageBase:x}, {units} units, versions {string.Join(",", versions.Select(k => $"v{k.Key}:{k.Value}"))}, {funcs.Count} functions");

Dictionary<ulong, Abbrev> Abbrevs(long off)
{
    if (abbrevCache.TryGetValue(off, out var d)) return d;
    d = new();
    int p = abbrevSec.off + (int)off;
    while (true)
    {
        ulong code = Uleb(ref p);
        if (code == 0) break;
        int tag = (int)Uleb(ref p);
        bool children = bytes[p++] != 0;
        var attrs = new List<(int at, int form, long implicitConst)>();
        while (true)
        {
            int at = (int)Uleb(ref p), form = (int)Uleb(ref p);
            long ic = form == 0x21 ? Sleb(ref p) : 0;
            if (at == 0 && form == 0) break;
            attrs.Add((at, form, ic));
        }
        d[code] = new Abbrev(tag, children, attrs);
    }
    abbrevCache[off] = d;
    return d;
}
int U16(ref int p) { int v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p)); p += 2; return v; }
int U32(ref int p) { int v = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p)); p += 4; return v; }
ulong U64(ref int p) { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p)); p += 8; return v; }
ulong Uleb(ref int p)
{
    ulong r = 0;
    int shift = 0;
    while (true)
    {
        byte b = bytes[p++];
        r |= (ulong)(b & 0x7f) << shift;
        if ((b & 0x80) == 0) return r;
        shift += 7;
    }
}
long Sleb(ref int p)
{
    long r = 0;
    int shift = 0;
    byte b;
    do
    {
        b = bytes[p++];
        r |= (long)(b & 0x7f) << shift;
        shift += 7;
    } while ((b & 0x80) != 0);
    if (shift < 64 && (b & 0x40) != 0) r |= -1L << shift;
    return r;
}

record Abbrev(int Tag, bool Children, List<(int at, int form, long implicitConst)> Attrs);
