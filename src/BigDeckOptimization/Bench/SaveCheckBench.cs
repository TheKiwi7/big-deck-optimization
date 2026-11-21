using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using BigDeckOptimization.Patches;
using Godot;
using MegaCrit.Sts2.Core.DevConsole;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf savecheck [&lt;file&gt;]</c>: <see cref="SaveBackupCheck"/> against the game's check of the previous save
/// (<c>SaveContent.IsWhole(FileAccess.GetAsText())</c>), on the same bytes.
/// <list type="bullet">
/// <item>A save file (a <c>user://</c> path or a full one; by default the run save this session wrote last): the game's verdict
/// and time (reading as text, parsing), the part's judgment of the file's bytes and its time (off the main thread in the game),
/// and what CopyBackup's main thread then spends (reading the bytes, comparing them with the ones kept).</item>
/// <item>A synthetic set written to a scratch folder (whole and cut JSON, CRLF, non-ASCII text, depth 64 and 65, comments,
/// trailing commas, empty, a UTF-8 BOM, a NUL byte, invalid UTF-8, an encoded surrogate, an overlong sequence): inside the gate the
/// verdict must be the game's and Godot's text must be <c>Encoding.UTF8.GetString</c>'s; outside it there is no verdict (the
/// game's check runs).</item>
/// <item>The part's own call as CopyBackup makes it, on a scratch file whose bytes were kept: it must answer (a shortcut) with
/// the game's verdict and leave the file's position as it found it; with one byte changed it must leave the answer to the game.</item>
/// </list>
/// <c>wrong</c>: the part's answers inverted from then on (positive control: its own check must catch it); <c>verify &lt;n&gt;</c>:
/// one answer in n checked; <c>counters</c>: the part's counters, failing when its check found a difference.
/// </summary>
public static class SaveCheckBench
{
    private const string ScratchDir = "user://bdperf_savecheck";

    public static CmdResult Run(string[] args)
    {
        string sub = args.Length > 1 ? args[1] : "";
        switch (sub)
        {
            case "wrong":
                SaveBackupCheck.WrongForTest = true;
                return new CmdResult(success: true, $"{SaveBackupCheck.Name}: answers inverted from now on (test).");
            case "verify" when args.Length > 2 && int.TryParse(args[2], out int every) && every > 0:
                SaveBackupCheck.VerifyEvery = every;
                return new CmdResult(success: true, $"{SaveBackupCheck.Name}: one answer in {every} checked against the game's.");
            case "counters":
                bool clean = SaveBackupCheck.Mismatches == 0;
                return new CmdResult(clean, $"SAVECHECK counters shortcuts {SaveBackupCheck.Shortcuts}, checks {SaveBackupCheck.Checks}, mismatches {SaveBackupCheck.Mismatches}; "
                    + $"misses: nothing kept {SaveBackupCheck.MissNone}, other bytes {SaveBackupCheck.MissDifferent}, not judged yet {SaveBackupCheck.MissNotReady}, "
                    + $"outside the gate {SaveBackupCheck.MissGate}, other mods {SaveBackupCheck.MissBackOff}; files kept {SaveBackupCheck.Remembered}: "
                    + (clean ? "OK: no difference with the game's check" : "the part's check found a difference"));
        }
        if (!SaveBackupCheck.Applied)
        {
            // v0.107.1 / v0.111: CopyBackup copies without the check. A legitimate state, no "OK".
            return new CmdResult(success: true, $"{SaveBackupCheck.Name} not applied ({(SaveBackupCheck.NotNeeded ? "not needed on this game version" : "see the log")}): nothing to compare.");
        }
        if (!SaveBackupCheck.Enabled)
        {
            return new CmdResult(success: false, $"{SaveBackupCheck.Name} is off (setting or fault): nothing to compare.");
        }
        var report = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= SaveFile(sub.Length > 0 ? string.Join(' ', args.Skip(1)) : null, report);
            ok &= Synthetic(report);
            ok &= OwnCall(report);
            ok &= EndToEnd(report);
        }
        finally
        {
            Cleanup();
        }
        return new CmdResult(ok, $"SAVECHECK {(ok ? "OK: identical verdicts" : "MISMATCH (see the lines below)")}\n{report.ToString().TrimEnd()}");
    }

    /// <summary>A real save: the game's check and the part's judgment, verdicts and times.</summary>
    private static bool SaveFile(string? path, StringBuilder report)
    {
        path ??= SaveBackupCheck.LatestKept("current_run");
        if (path == null)
        {
            report.AppendLine("  run save: none written yet this session (the synthetic set only)");
            return true;
        }
        string name = path.GetFile();
        using Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            report.AppendLine($"  {name}: can't be opened ({Godot.FileAccess.GetOpenError()})");
            return false;
        }
        long t0 = Stopwatch.GetTimestamp();
        string text = file.GetAsText();
        long t1 = Stopwatch.GetTimestamp();
        bool game = SaveBackupCheck.GameIsWhole(text);
        long t2 = Stopwatch.GetTimestamp();
        byte[] bytes = file.GetBuffer((long)file.GetLength());
        file.Seek(0);
        long t3 = Stopwatch.GetTimestamp();
        int verdict = SaveBackupCheck.Verdict(bytes);
        long t4 = Stopwatch.GetTimestamp();
        byte[] copy = (byte[])bytes.Clone();
        long t5 = Stopwatch.GetTimestamp();
        bool same = bytes.AsSpan().SequenceEqual(copy);
        long t6 = Stopwatch.GetTimestamp();
        int kept = SaveBackupCheck.KeptVerdict(path);
        bool ok = verdict == SaveBackupCheck.NoVerdict || (verdict == SaveBackupCheck.Whole) == game;
        report.AppendLine($"  {name} {bytes.Length:N0} bytes: game {(game ? "whole" : "not whole")} in {Ms(t0, t2):F1} ms (text {Ms(t0, t1):F1} + parse {Ms(t1, t2):F1}); "
            + $"part {Words(verdict)} judged in {Ms(t3, t4):F1} ms off the main thread; main thread on a shortcut: read {Ms(t2, t3):F1} ms + compare {Ms(t5, t6):F1} ms"
            + $"{(same ? "" : " (compare failed)")}; kept for that file: {(kept < 0 ? "nothing" : Words(kept))}: {(ok ? "OK" : "MISMATCH")}");
        return ok && same;
    }

    /// <summary>Byte sequences on both sides of the gate, written to a scratch file and read back by Godot.</summary>
    private static bool Synthetic(StringBuilder report)
    {
        var cases = new List<(string Name, byte[] Bytes, bool InGate)>
        {
            ("whole", Utf8("{\"a\":1,\"b\":[true,null,\"x\"]}"), true),
            ("crlf", Utf8("{\r\n  \"a\": [1, 2],\r\n  \"b\": \"c\\r\\n\"\r\n}\r\n"), true),
            ("non-ascii", Utf8("{\"é\":\"日本語\",\"emoji\":\"\U0001F600\",\"nbsp\":\" \"}"), true),
            // Noncharacters and the last code point: valid UTF-8 the gate lets in, so Godot must decode them as they are.
            ("noncharacters", Utf8("[\"￾￿﷐\U0010FFFF\"]"), true),
            ("bom-inside", Utf8("{\"a\":\"﻿\"}"), false),
            ("cut", Utf8("{\"a\":[1,2"), true),
            ("depth-64", Utf8(new string('[', 64) + new string(']', 64)), true),
            ("depth-65", Utf8(new string('[', 65) + new string(']', 65)), true),
            ("comment", Utf8("{/*x*/\"a\":1}"), true),
            ("trailing-comma", Utf8("{\"a\":1,}"), true),
            ("two-values", Utf8("{} {}"), true),
            ("empty", Array.Empty<byte>(), true),
            ("whitespace", Utf8(" \r\n\t "), true),
            ("bom", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("{\"a\":1}")).ToArray(), false),
            ("nul", Utf8("{\"a\":1}").Append((byte)0).Concat(Utf8("garbage")).ToArray(), false),
            ("invalid-utf8", new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' }, false),
            ("surrogate", new byte[] { (byte)'[', (byte)'"', 0xED, 0xA0, 0x80, (byte)'"', (byte)']' }, false),
            ("overlong", new byte[] { (byte)'[', (byte)'"', 0xC0, 0xAF, (byte)'"', (byte)']' }, false),
            ("cut-utf8", new byte[] { (byte)'[', (byte)'"', 0xE6, 0x97 }, false),
        };
        DirAccess.MakeDirRecursiveAbsolute(ScratchDir);
        string path = ScratchDir + "/synthetic.json";
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
            string text = file.GetAsText();
            bool game = SaveBackupCheck.GameIsWhole(text);
            int verdict = SaveBackupCheck.Verdict(bytes);
            // Inside the gate, the premise itself: Godot's text is the UTF-8 decoding of the bytes.
            bool sameText = !inGate || text == Encoding.UTF8.GetString(bytes);
            // Outside the gate: no verdict (the game's check runs). Inside: the game's verdict.
            // The gate itself is pinned per case (as SaveReadBench does): the part's own InGate must say the same.
            bool agree = inGate == SaveBackupCheck.InGate(bytes)
                && (verdict == SaveBackupCheck.NoVerdict ? !inGate : inGate && (verdict == SaveBackupCheck.Whole) == game);
            if (!sameText || !agree)
            {
                ok = false;
                words.Add($"{name}: game {(game ? "whole" : "not whole")}, part {Words(verdict)}{(sameText ? "" : ", Godot's text not the UTF-8 decoding")} MISMATCH");
            }
            else
            {
                words.Add($"{name} {(inGate ? (game ? "whole" : "not whole") : "gate")}");
            }
        }
        report.AppendLine($"  synthetic ({cases.Count}): {string.Join(", ", words)}: {(ok ? "OK" : "MISMATCH")}");
        return ok;
    }

    /// <summary>The part's own call as CopyBackup makes it, on a scratch file whose bytes were kept, then on changed bytes.</summary>
    private static bool OwnCall(StringBuilder report)
    {
        DirAccess.MakeDirRecursiveAbsolute(ScratchDir);
        string path = ScratchDir + "/kept.save";
        byte[] bytes = Utf8("{\"schema\":1,\"cards\":[" + string.Join(",", Enumerable.Range(0, 2000).Select(i => $"{{\"id\":\"CARD_{i}\",\"upgrades\":{i % 3}}}")) + "]}");
        Write(path, bytes);
        SaveBackupCheck.Remember(path, bytes, judgeNow: true);
        long shortcuts = SaveBackupCheck.Shortcuts, different = SaveBackupCheck.MissDifferent;
        bool answer, game, positionKept;
        using (Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null)
            {
                report.AppendLine("  own call: the scratch file can't be opened");
                return false;
            }
            answer = SaveBackupCheck.IsWholeSave(file, false, path);
            positionKept = file.GetPosition() == 0;
            game = SaveBackupCheck.GameIsWhole(file.GetAsText());
        }
        bool tookShortcut = SaveBackupCheck.Shortcuts == shortcuts + 1;
        // One byte changed (same length): the bytes kept no longer match, the game's check must answer.
        byte[] changed = (byte[])bytes.Clone();
        changed[changed.Length - 1] = (byte)' ';
        Write(path, changed);
        bool answerChanged, gameChanged;
        using (Godot.FileAccess? file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
        {
            if (file == null)
            {
                report.AppendLine("  own call: the changed scratch file can't be opened");
                return false;
            }
            answerChanged = SaveBackupCheck.IsWholeSave(file, false, path);
            gameChanged = SaveBackupCheck.GameIsWhole(file.GetAsText());
        }
        bool leftToGame = SaveBackupCheck.MissDifferent == different + 1 && SaveBackupCheck.Shortcuts == shortcuts + 1;
        bool ok = tookShortcut && answer == game && positionKept && leftToGame && answerChanged == gameChanged;
        report.AppendLine($"  own call: kept bytes {(tookShortcut ? "answered" : "NOT answered")} ({(answer ? "whole" : "not whole")}, game {(game ? "whole" : "not whole")}), "
            + $"position {(positionKept ? "kept" : "MOVED")}; changed bytes {(leftToGame ? "left to the game" : "NOT left to the game")} "
            + $"({(answerChanged ? "whole" : "not whole")}, game {(gameChanged ? "whole" : "not whole")}): {(ok ? "OK" : "MISMATCH")}");
        return ok;
    }

    /// <summary>
    /// Through the game's own <c>GodotFileIo.WriteFile</c> (a store on the scratch folder), with the part on, then off: a file that
    /// isn't JSON written, then a whole save over it (CopyBackup must find the previous file not whole: no <c>.backup</c>, the game's
    /// warning), then another whole save (the <c>.backup</c> must hold the second one's bytes). Both ways must leave the same files;
    /// the part on must have answered both checks (one not whole, one whole), off none.
    /// </summary>
    private static bool EndToEnd(StringBuilder report)
    {
        var io = new MegaCrit.Sts2.Core.Saves.GodotFileIo(ScratchDir);
        string full = io.GetFullPath(EndToEndFile);
        byte[] broken = Utf8("{\"cut\":[1,2"), first = Utf8("{\"save\":1}"), second = Utf8("{\"save\":2,\"more\":[true]}");
        var outcomes = new List<string>();
        var answers = new List<long>();
        try
        {
            foreach (bool on in new[] { true, false })
            {
                SaveBackupCheck.Enabled = on;
                DirAccess.RemoveAbsolute(full);
                DirAccess.RemoveAbsolute(full + ".backup");
                long before = SaveBackupCheck.Shortcuts;
                io.WriteFile(EndToEndFile, broken);
                WaitJudged(full);
                io.WriteFile(EndToEndFile, first);
                string afterFirst = Godot.FileAccess.FileExists(full + ".backup") ? "backup written" : "no backup";
                WaitJudged(full);
                io.WriteFile(EndToEndFile, second);
                byte[]? backup = Godot.FileAccess.FileExists(full + ".backup") ? Godot.FileAccess.GetFileAsBytes(full + ".backup") : null;
                string afterSecond = backup == null ? "no backup" : backup.AsSpan().SequenceEqual(first) ? "backup = first save" : "backup = other bytes";
                bool saveIsSecond = Godot.FileAccess.GetFileAsBytes(full).AsSpan().SequenceEqual(second);
                outcomes.Add($"{afterFirst}, then {afterSecond}{(saveIsSecond ? "" : ", save not the last bytes")}");
                answers.Add(SaveBackupCheck.Shortcuts - before);
            }
        }
        finally
        {
            // Only the part's own fault keeps it off (the bench ran with it on).
            SaveBackupCheck.Enabled = !PatchSafety.IsFaulted(SaveBackupCheck.Name);
            SaveBackupCheck.Forget(full);
        }
        bool ok = outcomes[0] == outcomes[1] && outcomes[0] == "no backup, then backup = first save" && answers[0] == 2 && answers[1] == 0;
        report.AppendLine($"  end to end (game's WriteFile): on: {outcomes[0]} ({answers[0]} answered); off: {outcomes[1]} ({answers[1]} answered): {(ok ? "OK" : "MISMATCH")}");
        return ok;
    }

    private const string EndToEndFile = "endtoend.save";

    /// <summary>Waits (2 s at most) for the part's off-thread judgment of the bytes kept for that file, if any.</summary>
    private static void WaitJudged(string full)
    {
        var clock = Stopwatch.StartNew();
        while (SaveBackupCheck.KeptVerdict(full) == SaveBackupCheck.Pending && clock.ElapsedMilliseconds < 2000)
        {
            System.Threading.Thread.Sleep(1);
        }
    }

    private static void Cleanup()
    {
        SaveBackupCheck.Forget(ScratchDir + "/kept.save");
        foreach (string file in new[] { "synthetic.json", "kept.save", EndToEndFile, EndToEndFile + ".backup" })
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

    private static string Words(int verdict) => verdict switch
    {
        SaveBackupCheck.Pending => "not judged yet",
        SaveBackupCheck.Whole => "whole",
        SaveBackupCheck.Broken => "not whole",
        _ => "no verdict (outside the gate)",
    };

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
