using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf saveread [&lt;file&gt;]</c>: <see cref="SaveReadDecode"/> against the game's read of a save file as text
/// (<c>FileAccess.GetAsText()</c> in <c>GodotFileIo.ReadFile</c>), on the same file.
/// <list type="bullet">
/// <item>A save file (a <c>user://</c> path or a full one; by default the run save read last this session, else the current
/// profile's run save): the game's text and time, the part's text and time (the bytes read, the gate, the decoding), best of 3
/// each; the two strings must be equal.</item>
/// <item>A synthetic set written to a scratch folder (JSON, CRLF and lone CR, non-ASCII text, noncharacters, a BOM inside, empty,
/// whitespace, 1 MB; then a leading BOM, a NUL byte, invalid UTF-8, an encoded surrogate, an overlong sequence, a cut sequence):
/// inside the gate the part's text must be Godot's; outside it the part must decline (the game's call reads).</item>
/// <item>The game's own <c>GodotFileIo.ReadFile</c> (a store on the scratch folder) with the part on, then off: the same string
/// both ways, one decoded read on, none off.</item>
/// </list>
/// <c>wrong</c>: the decoded text made different on the reads the net compares (positive control: the net must catch it);
/// <c>verify &lt;n&gt;</c>: one decoded read in n compared; <c>counters</c>: the part's counters, failing when its net found a difference.
/// </summary>
public static class SaveReadBench
{
    private const string ScratchDir = "user://bdperf_saveread";

    public static CmdResult Run(string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "";
        switch (sub)
        {
            case "wrong":
                SaveReadDecode.WrongForTest = true;
                return new CmdResult(success: true, $"{SaveReadDecode.Name}: the compared reads' decoded text made different from now on (test).");
            case "verify" when args.Length > 2 && int.TryParse(args[2], out int every) && every > 0:
                SaveReadDecode.VerifyEvery = every;
                return new CmdResult(success: true, $"{SaveReadDecode.Name}: one decoded read in {every} compared with the game's.");
            case "counters":
                bool clean = SaveReadDecode.Mismatches == 0;
                return new CmdResult(clean, $"SAVEREAD counters decoded {SaveReadDecode.Decoded} (run saves {SaveReadDecode.RunSaveReads}), checks {SaveReadDecode.Checks}, "
                    + $"mismatches {SaveReadDecode.Mismatches}, served without the game's read {Math.Max(0, SaveReadDecode.Decoded - SaveReadDecode.Checks)}; left to the game: outside the gate {SaveReadDecode.MissGate}, short read {SaveReadDecode.MissShort}, "
                    + $"other mods {SaveReadDecode.MissBackOff}: " + (clean ? "OK: no difference with the game's text" : "the part's net found a difference"));
        }
        if (!SaveReadDecode.Applied)
        {
            return new CmdResult(success: false, $"{SaveReadDecode.Name} not applied (see the log): nothing to compare.");
        }
        if (!SaveReadDecode.Enabled)
        {
            return new CmdResult(success: false, $"{SaveReadDecode.Name} is off (setting or fault): nothing to compare.");
        }
        var report = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= SaveFile(sub.Length > 0 ? string.Join(' ', args.Skip(1)) : null, report);
            ok &= Synthetic(report);
            ok &= EndToEnd(report);
        }
        finally
        {
            Cleanup();
        }
        return new CmdResult(ok, $"SAVEREAD {(ok ? "OK: identical texts" : "MISMATCH (see the lines below)")}\n{report.ToString().TrimEnd()}");
    }

    /// <summary>The current profile's run save, full path (the store's), or null.</summary>
    private static string? CurrentRunSave()
    {
        try
        {
            object? manager = AccessTools.Field(typeof(SaveManager), "_runSaveManager")?.GetValue(SaveManager.Instance);
            object? store = AccessTools.Field(typeof(SaveManager), "_saveStore")?.GetValue(SaveManager.Instance);
            string? relative = manager == null ? null : AccessTools.Property(manager.GetType(), "CurrentRunSavePath")?.GetValue(manager) as string;
            if (store is CloudSaveStore cloud)
            {
                store = cloud.LocalStore;
            }
            return relative != null && store is GodotFileIo io ? io.GetFullPath(relative) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A real save: the game's text and the part's, times best of 3.</summary>
    private static bool SaveFile(string? path, StringBuilder report)
    {
        path ??= SaveReadDecode.LastRunSave ?? CurrentRunSave();
        if (path == null || !Godot.FileAccess.FileExists(path))
        {
            report.AppendLine($"  run save: {(path == null ? "none known" : path.GetFile() + " not there")} (the synthetic set only)");
            return true;
        }
        string name = path.GetFile();
        using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            report.AppendLine($"  {name}: can't be opened ({Godot.FileAccess.GetOpenError()})");
            return false;
        }
        string? game = null, part = null;
        double gameMs = double.MaxValue, partMs = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            game = file.GetAsText();
            long t1 = Stopwatch.GetTimestamp();
            ulong position = 0;
            bool moved = false;
            part = SaveReadDecode.Decode(file, ref position, ref moved);
            long t2 = Stopwatch.GetTimestamp();
            gameMs = Math.Min(gameMs, Ms(t0, t1));
            partMs = Math.Min(partMs, Ms(t1, t2));
        }
        bool positionKept = file.GetPosition() == 0;
        bool declined = part == null;
        bool ok = positionKept && (declined || string.Equals(part, game, StringComparison.Ordinal));
        report.AppendLine($"  {name} {file.GetLength():N0} bytes, {game!.Length:N0} chars: game's GetAsText {gameMs:F1} ms, part {(declined ? "declined (outside the gate: the game reads)" : $"{partMs:F1} ms")}, "
            + $"position {(positionKept ? "kept" : "MOVED")}: {(ok ? declined ? "OK (declined)" : "OK" : "MISMATCH")}");
        return ok;
    }

    /// <summary>Byte sequences on both sides of the gate, written to a scratch file and read back by Godot and by the part.</summary>
    private static bool Synthetic(StringBuilder report)
    {
        var cases = new List<(string Name, byte[] Bytes, bool InGate)>
        {
            ("json", Utf8("{\"a\":1,\"b\":[true,null,\"x\"]}"), true),
            ("crlf", Utf8("{\r\n  \"a\": [1, 2],\r\n  \"b\": \"c\\r\\n\"\r\n}\r\n"), true),
            ("lone-cr", Utf8("a\rb\r\rc\r"), true),
            ("non-ascii", Utf8("{\"é\":\"日本語\",\"emoji\":\"\U0001F600\",\"nbsp\":\" \"}"), true),
            ("noncharacters", Utf8("[\"￾￿﷐\U0010FFFF\"]"), true),
            ("bom-inside", Utf8("{\"a\":\"﻿\"}"), false),
            ("empty", Array.Empty<byte>(), true),
            ("whitespace", Utf8(" \r\n\t "), true),
            ("large", Utf8("{\"cards\":[" + string.Join(",", Enumerable.Range(0, 20000).Select(i => $"{{\"id\":\"CARD_{i}\",\"name\":\"Ça {i} 日\"}}")) + "]}"), true),
            ("bom", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("{\"a\":1}")).ToArray(), false),
            ("nul", Utf8("{\"a\":1}").Append((byte)0).Concat(Utf8("garbage")).ToArray(), false),
            ("invalid-utf8", new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' }, false),
            ("surrogate", new byte[] { (byte)'[', (byte)'"', 0xED, 0xA0, 0x80, (byte)'"', (byte)']' }, false),
            ("overlong", new byte[] { (byte)'[', (byte)'"', 0xC0, 0xAF, (byte)'"', (byte)']' }, false),
            ("cut-utf8", new byte[] { (byte)'[', (byte)'"', 0xE6, 0x97 }, false),
        };
        DirAccess.MakeDirRecursiveAbsolute(ScratchDir);
        string path = ScratchDir + "/synthetic.save";
        bool ok = true;
        var words = new List<string>();
        foreach ((string name, byte[] bytes, bool inGate) in cases)
        {
            Write(path, bytes);
            using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                words.Add($"{name}: not read back");
                ok = false;
                continue;
            }
            string game = file.GetAsText();
            ulong position = 0;
            bool moved = false;
            string? part = SaveReadDecode.Decode(file, ref position, ref moved);
            bool positionKept = file.GetPosition() == 0;
            // Inside the gate the part's text must be Godot's; outside it the part must decline.
            bool agree = inGate == SaveBackupCheck.InGate(bytes) && (inGate ? string.Equals(part, game, StringComparison.Ordinal) : part == null) && positionKept;
            if (!agree)
            {
                ok = false;
                words.Add($"{name}: {(part == null ? "declined" : part.Length + " chars")}, game {game.Length} chars{(positionKept ? "" : ", position moved")} MISMATCH");
            }
            else
            {
                words.Add($"{name} {(inGate ? "decoded" : "gate")}");
            }
        }
        report.AppendLine($"  synthetic ({cases.Count}): {string.Join(", ", words)}: {(ok ? "OK" : "MISMATCH")}");
        return ok;
    }

    /// <summary>The game's own <c>GodotFileIo.ReadFile</c> on a scratch file, the part on then off.</summary>
    private static bool EndToEnd(StringBuilder report)
    {
        DirAccess.MakeDirRecursiveAbsolute(ScratchDir);
        var io = new GodotFileIo(ScratchDir);
        byte[] bytes = Utf8("{\"schema\":1,\"cards\":[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"id\":\"CARD_{i}\",\"text\":\"é{i}\\r\\n\"}}")) + "]}\r\n");
        Write(io.GetFullPath(EndToEndFile), bytes);
        var texts = new List<string?>();
        var decoded = new List<long>();
        try
        {
            foreach (bool on in new[] { true, false })
            {
                SaveReadDecode.Enabled = on;
                long before = SaveReadDecode.Decoded;
                texts.Add(io.ReadFile(EndToEndFile));
                decoded.Add(SaveReadDecode.Decoded - before);
            }
        }
        finally
        {
            // Only the part's own fault keeps it off (the bench ran with it on).
            SaveReadDecode.Enabled = !PatchSafety.IsFaulted(SaveReadDecode.Name);
        }
        string expected = Encoding.UTF8.GetString(bytes);
        bool ok = texts[0] == expected && texts[1] == expected && decoded[0] == 1 && decoded[1] == 0;
        report.AppendLine($"  end to end (game's ReadFile): on: {texts[0]?.Length ?? -1} chars ({decoded[0]} decoded); off: {texts[1]?.Length ?? -1} chars ({decoded[1]} decoded); "
            + $"the file's UTF-8 text {expected.Length} chars: {(ok ? "OK" : "MISMATCH")}");
        return ok;
    }

    private const string EndToEndFile = "endtoend.save";

    private static void Cleanup()
    {
        foreach (string file in new[] { "synthetic.save", EndToEndFile })
        {
            DirAccess.RemoveAbsolute(ScratchDir + "/" + file);
        }
        DirAccess.RemoveAbsolute(ScratchDir);
    }

    private static void Write(string path, byte[] bytes)
    {
        using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file == null)
        {
            throw new InvalidOperationException($"{path} can't be written: {Godot.FileAccess.GetOpenError()}");
        }
        file.StoreBuffer(bytes);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
