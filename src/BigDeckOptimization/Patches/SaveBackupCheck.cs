using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Game v0.113 checks, before every save, that the file about to be replaced is a whole save before copying it to its
/// <c>.backup</c> (<c>GodotFileIo.CopyBackup</c>: <c>SaveContent.IsWhole(fileAccess.GetAsText())</c>, the previous save read as
/// text and parsed as JSON). At 10,300 cards the run save is 9.7 MB: ~45 ms to read it as text and 10-45 ms to parse it, on the
/// main thread, twice per room (v0.111 copies the bytes without that check: nothing to do there).
///
/// A copy of the bytes the game writes to each file is kept (a postfix of <c>WriteFile</c>/<c>WriteFileAsync(string, byte[])</c>:
/// it reads the argument every other mod's prefix left, the bytes the game was given to write), and judged off the main thread.
/// When CopyBackup's check finds the file holding exactly those bytes (read and compared, the file's position put back as
/// <c>GetAsText</c> puts it back), that judgment answers; otherwise (nothing kept for that file, other bytes, the judgment not
/// ready, bytes outside the gate below) the game's two calls run. Only the check changes: the logs, the copy, fsync and rename
/// stay the game's.
///
/// Exact: the answer is a function of the file's bytes alone, judged only inside a gate (strict UTF-8, no NUL byte, no UTF-8
/// BOM). There Godot's <c>GetAsText()</c> (skipCr false: CR kept) gives <c>Encoding.UTF8.GetString</c>'s string (it strips a
/// leading BOM, stops at a NUL and replaces invalid sequences: none occurs), and <c>JsonDocument.Parse</c> over those bytes gives
/// the verdict <c>Parse</c> over that string gives (the string overload transcodes it back to the same bytes; default options
/// on both sides, MaxDepth 64). "Every save is whole JSON" is not assumed: other mods rewrite the bytes, and the game's
/// <c>.pre-repair</c> copies go through WriteFile with whatever they hold. The first answer and one in <see cref="VerifyEvery"/>
/// after it also run the game's check and compare; a difference turns this off (the game's answer is used).
/// </summary>
public static class SaveBackupCheck
{
    public const string Name = "SaveBackupCheck";

    /// <summary><c>GodotFileIo.CopyBackup</c>, game v0.113.0 and v0.113.1: the check replaced.</summary>
    private const string CopyBackupFingerprint = "B81356B15D0BE661";
    /// <summary><c>GodotFileIo.CopyBackup</c> on v0.107.1, v0.111.0 and v0.111.1: no check (bytes copied as they are).</summary>
    private const string UncheckedCopyBackupFingerprint = "F111F53F2E6146E6";
    /// <summary><c>SaveContent.IsWhole</c> (v0.113.0, v0.113.1): <c>JsonDocument.Parse(content)</c> in a try, false on a JsonException.</summary>
    private const string IsWholeFingerprint = "40D66DE5B7BD29FD";
    /// <summary>
    /// <c>GodotFileIo.WriteFile(string, byte[])</c>, its state machine-free body with the closure writing the bytes, and
    /// <c>WriteFileAsync(string, byte[])</c> with its state machine and lambda (v0.107.1 to v0.113.1): CopyBackup on the full path,
    /// then the bytes given written to it.
    /// </summary>
    private const string WriteFileFingerprint = "100877490F5094D4";
    private const string WriteFileAsyncFingerprint = "1374131C1EDE53C8";
    /// <summary><c>GodotFileIo.StoreBufferToFile</c> (v0.107.1 to v0.113.1): the bytes stored as they are.</summary>
    private const string StoreBufferFingerprint = "9C8F31F54BB7C8E7";
    /// <summary>
    /// <c>GodotFileIo.DeleteFile(string)</c> (v0.113.0, v0.113.1): <c>DirAccess.RemoveAbsolute(GetFullPath(path))</c>, an error
    /// logged. Only lets go of a kept copy (memory), so a change leaves just that postfix out.
    /// </summary>
    private const string DeleteFileFingerprint = "3B9E3B6A0886B492";

    /// <summary>Judgments: not done yet, whole, not whole, none (outside the gate, or the parse threw something else).</summary>
    internal const int Pending = 0, Whole = 1, Broken = 2, NoVerdict = 3;

    /// <summary>Files kept at most (the oldest written goes first), and their bytes in all (the newest is always kept).</summary>
    private const int MaxFiles = 8;
    private const long MaxBytes = 128L * 1024 * 1024;

#if BENCH
    internal static int VerifyEvery { get; set; } = 4;

    /// <summary>
    /// Positive control (<c>bdperf savecheck wrong</c>): the shortcut's answers the net compares inverted (never handed to the
    /// game: the net returns the game's answer then), so the net must catch the first one and turn the part off.
    /// </summary>
    internal static bool WrongForTest { get; set; }
#else
    internal static int VerifyEvery { get; set; } = 64;
#endif

    /// <summary>Read on any thread (the main thread's saves, WriteFileAsync's callers): volatile.</summary>
    private static volatile bool _enabled = true;

    /// <summary>Off: nothing is kept any more and what was kept is let go.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                Forget();
            }
        }
    }

    public static bool Applied { get; private set; }

    /// <summary>This game version's CopyBackup copies without the check (v0.107.1, v0.111): not applied, nothing missing.</summary>
    public static bool NotNeeded { get; private set; }

    // Counters (any thread: Interlocked). Shortcuts is the effect counter; the misses by reason.
    public static long Shortcuts;
    public static long MissNone;
    public static long MissDifferent;
    public static long MissNotReady;
    public static long MissGate;
    public static long MissBackOff;
    public static long Checks;
    public static long Mismatches;
    public static long Remembered;
    /// <summary>Writes not kept: their file not checked by CopyBackup yet this session (<see cref="_asked"/>).</summary>
    public static long NotKept;

    private static MethodInfo? _copyBackup;
    private static MethodInfo? _isWholeMethod;
    private static MethodInfo? _getAsText;

    /// <summary>
    /// The FileAccess calls the shortcut makes and the game's check doesn't (its text is read natively by GetAsText): a mod
    /// patching one of them (decrypting in GetBuffer, say) would show the shortcut other bytes than the game parses.
    /// </summary>
    private static MethodInfo[] _fileReads = Array.Empty<MethodInfo>();
    private static Func<string, bool>? _isWhole;
    private static int _replaced;

    private sealed class Entry(byte[] bytes, long order)
    {
        public readonly byte[] Bytes = bytes;
        public readonly long Order = order;
        public int Verdict = Pending;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    /// <summary>
    /// The files CopyBackup asked about this session (<see cref="IsWholeSave"/>, it asks only when the file exists): only their
    /// writes are kept. A file written once to a new path (a run history's <c>.run</c>, ~10 MB at 10,000 cards) is never checked
    /// again, so a copy of it would only cost memory (a large-object allocation on the main thread). Survives deletes (the run
    /// save is deleted at a run's end and written again by the next run). The cost: one miss on a file's first check of the
    /// session (nothing was kept for it before).
    /// </summary>
    private static readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private static long _order;
    private static long _keptBytes;

    public static void Apply(Harmony harmony)
    {
        MethodInfo copyBackup = Reflect.DeclaredMethod(typeof(GodotFileIo), "CopyBackup", new[] { typeof(string) })
            ?? throw new MissingMethodException(nameof(GodotFileIo), "CopyBackup");
        // By name: SaveContent is v0.113's (a compiled reference would fail the load check on v0.111).
        Type? saveContent = Reflect.TypeByName("MegaCrit.Sts2.Core.Saves.SaveContent");
        if (saveContent == null && MethodFingerprint.Of(copyBackup) == UncheckedCopyBackupFingerprint)
        {
            NotNeeded = true;
            throw new PatchSkippedException("not needed: GodotFileIo.CopyBackup copies the previous save without reading it as text on this game version");
        }
        PatchSafety.RequireFingerprint(copyBackup, CopyBackupFingerprint);
        MethodInfo isWhole = Reflect.DeclaredMethod(saveContent, "IsWhole", new[] { typeof(string) })
            ?? throw new MissingMethodException("SaveContent", "IsWhole");
        PatchSafety.RequireFingerprint(isWhole, IsWholeFingerprint);
        MethodInfo writeFile = Reflect.DeclaredMethod(typeof(GodotFileIo), nameof(GodotFileIo.WriteFile), new[] { typeof(string), typeof(byte[]) })
            ?? throw new MissingMethodException(nameof(GodotFileIo), nameof(GodotFileIo.WriteFile));
        MethodInfo writeFileAsync = Reflect.DeclaredMethod(typeof(GodotFileIo), nameof(GodotFileIo.WriteFileAsync), new[] { typeof(string), typeof(byte[]) })
            ?? throw new MissingMethodException(nameof(GodotFileIo), nameof(GodotFileIo.WriteFileAsync));
        MethodInfo storeBuffer = Reflect.DeclaredMethod(typeof(GodotFileIo), "StoreBufferToFile", new[] { typeof(string), typeof(byte[]), typeof(string) })
            ?? throw new MissingMethodException(nameof(GodotFileIo), "StoreBufferToFile");
        PatchSafety.RequireFingerprint(writeFile, WriteFileFingerprint);
        PatchSafety.RequireFingerprint(writeFileAsync, WriteFileAsyncFingerprint);
        PatchSafety.RequireFingerprint(storeBuffer, StoreBufferFingerprint);
        _getAsText = Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetAsText), new[] { typeof(bool) })
            ?? throw new MissingMethodException(nameof(Godot.FileAccess), nameof(Godot.FileAccess.GetAsText));
        _fileReads = new[]
        {
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetLength), Type.EmptyTypes),
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetPosition), Type.EmptyTypes),
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.Seek), new[] { typeof(ulong) }),
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetBuffer), new[] { typeof(long) }),
        };
        if (_fileReads.Any(m => m == null))
        {
            throw new MissingMethodException(nameof(Godot.FileAccess), "GetLength, GetPosition, Seek or GetBuffer");
        }
        _isWhole = isWhole.CreateDelegate<Func<string, bool>>();
        _isWholeMethod = isWhole;
        _copyBackup = copyBackup;
        _replaced = 0;
        // Priority.Last, as SaveReadDecode's: other mods' transpilers run first. One that replaced or moved the check keeps its
        // change (this one finds no such pair of calls and leaves the code as it is: not applied, or, for a transpiler patched
        // later, the part backs off from it at run time: TranspiledOnly refuses a transpiler).
        PatchGuard.Patch(harmony, copyBackup, transpiler: new HarmonyMethod(typeof(SaveBackupCheck), nameof(Transpiler)) { priority = Priority.Last });
        if (_replaced != 1)
        {
            // ModEntry.TryApply removes the transpiler that replaced nothing.
            throw new PatchSkippedException($"GodotFileIo.CopyBackup: {_replaced} checks of the previous save found where 1 was expected, left vanilla");
        }
        // Postfixes: the argument as every other mod's prefix left it (RitsuLib, MomoLib and others replace the bytes there).
        // Whatever they do, a wrong copy only costs the shortcut: the file's bytes are compared before any answer.
        var remember = new HarmonyMethod(typeof(SaveBackupCheck), nameof(AfterWrite));
        PatchGuard.Patch(harmony, writeFile, postfix: remember);
        PatchGuard.Patch(harmony, writeFileAsync, postfix: remember);
        // A deleted file's copy let go (the run save at a run's end or abandon: ~10 MB at 10,000 cards, a co-op host's more).
        // Memory only: an entry left or dropped never changes an answer (the file's bytes are compared before any). Its own
        // fingerprint: a changed DeleteFile leaves only this postfix out (the copies then go as the oldest, or on a miss).
        MethodInfo? deleteFile = Reflect.DeclaredMethod(typeof(GodotFileIo), nameof(GodotFileIo.DeleteFile), new[] { typeof(string) });
        if (deleteFile != null && MethodFingerprint.Of(deleteFile) == DeleteFileFingerprint)
        {
            PatchGuard.Patch(harmony, deleteFile, postfix: new HarmonyMethod(typeof(SaveBackupCheck), nameof(AfterDelete)));
        }
        else
        {
            Log.Info($"[{ModEntry.Id}] {Name}: GodotFileIo.DeleteFile {(deleteFile == null ? "not found" : "changed")}: copies of deleted files go as the oldest");
        }
        // What the shortcut skips: the game's two calls. Another mod's patch on either would be skipped too: the game's check
        // runs then (no tolerance: a prefix or postfix there would not run). CopyBackup itself: this mod's only code there is
        // the transpiler above, which keeps what the body does (the same answer to the same question), and the read-only guard,
        // so other mods' prefixes and postfixes run there as without this mod (TranspiledOnly: a prefix rewriting fullPath
        // is read by the helper as by the game's code, ldarg.1 after it); a transpiler or a finalizer that isn't a pass-through
        // backs off.
        PatchSafety.Watch(Name, copyBackup, PatchTolerance.TranspiledOnly);
        PatchSafety.Watch(Name, isWhole);
        PatchSafety.Watch(Name, _getAsText);
        // The reads only the shortcut makes: strict (what any patch there returns could differ from what GetAsText reads).
        PatchSafety.Watch(Name, _fileReads, PatchTolerance.None);
        Applied = true;
    }

    /// <summary>
    /// <c>SaveContent.IsWhole(fileAccess.GetAsText(false))</c> in CopyBackup becomes <c>IsWholeSave(fileAccess, false, fullPath)</c>:
    /// the same stack plus CopyBackup's argument, one call for the two.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo helper = Reflect.Method(typeof(SaveBackupCheck), nameof(IsWholeSave));
        for (int i = 0; i + 1 < code.Count; i++)
        {
            // No branch may land between the two calls (it would find the string on the stack): none in the game's code.
            if (code[i].Calls(_getAsText) && code[i + 1].Calls(_isWholeMethod) && code[i + 1].labels.Count == 0 && code[i + 1].blocks.Count == 0)
            {
                code[i] = new CodeInstruction(OpCodes.Ldarg_1).WithLabels(code[i].labels).WithBlocks(code[i].blocks);
                code[i + 1] = new CodeInstruction(OpCodes.Call, helper);
                _replaced++;
            }
        }
        return code;
    }

    /// <summary>What CopyBackup asks: whether the file it opened is a whole save.</summary>
    internal static bool IsWholeSave(Godot.FileAccess file, bool skipCr, string fullPath)
    {
        if (_enabled && !skipCr && file != null)
        {
            ulong position = 0;
            bool moved = false;
            bool answered = false, answer = false, verify = false;
            try
            {
                lock (_lock)
                {
                    _asked.Add(fullPath);
                }
                int verdict = Shortcut(file, fullPath, ref position, ref moved);
                if (verdict is Whole or Broken)
                {
                    answered = true;
                    answer = verdict == Whole;
                    long n = Interlocked.Increment(ref Shortcuts);
                    // The first answer of the session and one in VerifyEvery after it: the game's check too.
                    verify = (n - 1) % Math.Max(1, VerifyEvery) == 0;
#if BENCH
                    // Only the answers the net compares are inverted (never handed to the game: the net returns the game's
                    // answer then), as SaveReadDecode's control does.
                    if (WrongForTest && verify)
                    {
                        answer = !answer;
                    }
#endif
                }
            }
            catch (Exception e)
            {
                if (moved)
                {
                    try
                    {
                        file.Seek(position);
                    }
                    catch (Exception)
                    {
                        // The game's own read below meets whatever state the file is in.
                    }
                }
                Disable(e.ToString());
                answered = false;
            }
            if (answered && !verify)
            {
                return answer;
            }
            if (answered)
            {
                // The game's two calls, once, outside any try of this mod: whatever they throw (an OutOfMemoryException on a huge
                // save) reaches CopyBackup unchanged, as without this mod. Their answer is the one returned.
                Interlocked.Increment(ref Checks);
                bool game = _isWhole!(file.GetAsText(skipCr));
                if (game != answer)
                {
                    Interlocked.Increment(ref Mismatches);
                    Disable($"MISMATCH on {fullPath}: answered {(answer ? "whole" : "not whole")}, the game's check {(game ? "whole" : "not whole")}");
                }
                return game;
            }
        }
        // The game's two calls (a null file throws there as it would have).
        return _isWhole!(file!.GetAsText(skipCr));
    }

    /// <summary>Another mod patches one of <see cref="_fileReads"/> (each checked, so each back-off is recorded).</summary>
    private static bool ReadsPatched()
    {
        bool patched = false;
        foreach (MethodInfo read in _fileReads)
        {
            patched |= PatchSafety.IsPatchedByOthers(read);
        }
        return patched;
    }

    /// <summary>The kept judgment when the file holds exactly the bytes it was made from, else <see cref="NoVerdict"/>.</summary>
    private static int Shortcut(Godot.FileAccess file, string fullPath, ref ulong position, ref bool moved)
    {
        if (PatchSafety.IsPatchedByOthers(_copyBackup, PatchTolerance.TranspiledOnly) || PatchSafety.IsPatchedByOthers(_isWholeMethod)
            || PatchSafety.IsPatchedByOthers(_getAsText) || ReadsPatched())
        {
            Interlocked.Increment(ref MissBackOff);
            return NoVerdict;
        }
        Entry? entry;
        lock (_lock)
        {
            _entries.TryGetValue(fullPath, out entry);
        }
        if (entry == null)
        {
            Interlocked.Increment(ref MissNone);
            return NoVerdict;
        }
        int verdict = Volatile.Read(ref entry.Verdict);
        if (verdict == Pending)
        {
            Interlocked.Increment(ref MissNotReady);
            return NoVerdict;
        }
        if (verdict == NoVerdict)
        {
            Interlocked.Increment(ref MissGate);
            return NoVerdict;
        }
        ulong length = file.GetLength();
        if (length != (ulong)entry.Bytes.Length)
        {
            Interlocked.Increment(ref MissDifferent);
            ForgetEntry(fullPath, entry);
            return NoVerdict;
        }
        // As GetAsText reads: from the start, the position put back after (CopyBackup's own read of the bytes relies on it).
        position = file.GetPosition();
        moved = true;
        file.Seek(0);
        byte[]? now = file.GetBuffer((long)length);
        file.Seek(position);
        moved = false;
        if (now == null || !now.AsSpan().SequenceEqual(entry.Bytes))
        {
            Interlocked.Increment(ref MissDifferent);
            ForgetEntry(fullPath, entry);
            return NoVerdict;
        }
        return verdict;
    }

    /// <summary>
    /// The file no longer holds the kept bytes (rewritten outside WriteFile, by Steam Cloud or another mod): the copy can never
    /// answer again, let go of (unless a newer write replaced it meanwhile). The write that follows keeps the new bytes.
    /// </summary>
    private static void ForgetEntry(string fullPath, Entry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(fullPath, out Entry? current) && current == entry)
            {
                _entries.Remove(fullPath);
                _keptBytes -= entry.Bytes.Length;
            }
        }
    }

    /// <summary>
    /// Postfix of <c>GodotFileIo.DeleteFile(string path)</c>: the copy kept for that file let go (memory only). Runs after the
    /// delete returned (not when it threw: the file may still be there, the copy then waits as any other).
    /// </summary>
    private static void AfterDelete(GodotFileIo __instance, string path)
    {
        if (path == null || __instance == null)
        {
            return;
        }
        try
        {
            Forget(__instance.GetFullPath(path));
        }
        catch (Exception e)
        {
            Disable(e.ToString());
        }
    }

    /// <summary>
    /// Postfix of <c>GodotFileIo.WriteFile/WriteFileAsync(string path, byte[] bytes)</c>: the bytes given to the game's write,
    /// kept for that file when CopyBackup already asked about it this session (<see cref="_asked"/>). After the write
    /// (WriteFileAsync: once its CopyBackup ran, before its first await), so that CopyBackup still found the previous bytes kept,
    /// and a file's first check has just added it to the set.
    /// </summary>
    private static void AfterWrite(GodotFileIo __instance, string path, byte[] bytes)
    {
        if (!_enabled || bytes == null || path == null || __instance == null)
        {
            return;
        }
        try
        {
            // WriteFile's argument is the full path already; GetFullPath leaves a full path as it is.
            string fullPath = __instance.GetFullPath(path);
            lock (_lock)
            {
                if (!_asked.Contains(fullPath))
                {
                    Interlocked.Increment(ref NotKept);
                    return;
                }
            }
            Remember(fullPath, bytes, judgeNow: false);
        }
        catch (Exception e)
        {
            Disable(e.ToString());
        }
    }

    /// <summary>
    /// Keeps a copy of <paramref name="bytes"/> for <paramref name="fullPath"/> (the copy that file had before goes) and judges it
    /// off the main thread, or now (<paramref name="judgeNow"/>: the bench).
    /// </summary>
    internal static void Remember(string fullPath, byte[] bytes, bool judgeNow)
    {
        var entry = new Entry((byte[])bytes.Clone(), Interlocked.Increment(ref _order));
        lock (_lock)
        {
            if (_entries.TryGetValue(fullPath, out Entry? old))
            {
                _keptBytes -= old.Bytes.Length;
            }
            _entries[fullPath] = entry;
            _keptBytes += entry.Bytes.Length;
            while (_entries.Count > 1 && (_entries.Count > MaxFiles || _keptBytes > MaxBytes))
            {
                KeyValuePair<string, Entry> oldest = _entries.MinBy(e => e.Value.Order);
                _entries.Remove(oldest.Key);
                _keptBytes -= oldest.Value.Bytes.Length;
            }
        }
        Interlocked.Increment(ref Remembered);
        if (judgeNow)
        {
            Judge(entry);
        }
        else
        {
            ThreadPool.UnsafeQueueUserWorkItem(Judge, entry, preferLocal: false);
        }
    }

    private static void Judge(Entry entry)
    {
        int verdict;
        try
        {
            verdict = Verdict(entry.Bytes);
        }
        catch (Exception)
        {
            verdict = NoVerdict;
        }
        Volatile.Write(ref entry.Verdict, verdict);
    }

    /// <summary>
    /// The game's check on a file holding <paramref name="bytes"/>, when they are inside the gate (strict UTF-8, no NUL, no BOM); <see cref="NoVerdict"/> outside it, or when the parse throws anything but a JsonException (the game's own call then
    /// meets it).
    /// </summary>
    internal static int Verdict(byte[] bytes)
    {
        if (!InGate(bytes))
        {
            return NoVerdict;
        }
        try
        {
            using (JsonDocument.Parse(bytes.AsMemory()))
            {
                return Whole;
            }
        }
        catch (JsonException)
        {
            return Broken;
        }
        catch (Exception)
        {
            return NoVerdict;
        }
    }

    /// <summary>
    /// Bytes whose Godot text is <c>Encoding.UTF8.GetString</c>'s: strict UTF-8 (no invalid, overlong, surrogate or cut
    /// sequence), no NUL byte, no UTF-8 BOM (Godot strips a leading one; one inside is left out too, whatever Godot does with it).
    /// </summary>
    internal static bool InGate(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf((byte)0) < 0
        && bytes.IndexOf(Bom) < 0
        && System.Text.Unicode.Utf8.IsValid(bytes);

    private static ReadOnlySpan<byte> Bom => new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>The game's own check of a text (the bench's reference).</summary>
    internal static bool GameIsWhole(string content) => _isWhole!(content);

    /// <summary>The file kept last whose path contains <paramref name="part"/>, or null.</summary>
    internal static string? LatestKept(string part)
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Key.Contains(part, StringComparison.Ordinal)).OrderByDescending(e => e.Value.Order).Select(e => e.Key).FirstOrDefault();
        }
    }

    /// <summary>Whether bytes are kept for that file, and their judgment (<see cref="Pending"/>...), or -1.</summary>
    internal static int KeptVerdict(string fullPath)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(fullPath, out Entry? entry) ? Volatile.Read(ref entry.Verdict) : -1;
        }
    }

    /// <summary>The bench: a file counted as checked by CopyBackup this session (its writes kept), or no longer.</summary>
    internal static void SetAskedForTest(string fullPath, bool asked)
    {
        lock (_lock)
        {
            if (asked)
            {
                _asked.Add(fullPath);
            }
            else
            {
                _asked.Remove(fullPath);
            }
        }
    }

    /// <summary>Lets go of what was kept for one file (the bench's), or of everything.</summary>
    internal static void Forget(string? fullPath = null)
    {
        lock (_lock)
        {
            if (fullPath == null)
            {
                _entries.Clear();
                _keptBytes = 0;
            }
            else if (_entries.Remove(fullPath, out Entry? entry))
            {
                _keptBytes -= entry.Bytes.Length;
            }
        }
    }

    /// <summary>
    /// SaveReadDecode found Godot's text of a file not the UTF-8 decoding of its bytes (its own MISMATCH, logged there as an
    /// error): this part's verdicts rest on the same premise, so it turns off for the session too (defence in depth).
    /// </summary>
    internal static void TurnOffAfterReadMismatch()
    {
        if (!Applied || PatchSafety.IsFaulted(Name))
        {
            return;
        }
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Info($"[{ModEntry.Id}] {Name} turned off for this session with SaveReadDecode (one of its reads found Godot's text other than the UTF-8 decoding of the bytes), saves check the previous save as the game does");
    }

    private static void Disable(string why)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, saves check the previous save as the game does: {why}");
    }
}
