using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game reads its save files as text through <c>GodotFileIo.ReadFile</c>: <c>fileAccess.GetAsText()</c>, Godot decoding the file
/// into its own UTF-32 string, then marshalled into a .NET one. At 10,300 cards the run save is 9.7 MB: ~45 ms a read, on the
/// main thread. The main menu loads the run save once when it opens (<c>NMainMenu</c>: <c>HasRunSave</c>, then <c>LoadRunSave</c>),
/// and with BaseLib up to three times in one frame (its <c>HasRunSave</c> postfix loads it too); continuing a run reads it again,
/// and some mods read it themselves (RitsuLib, PengoTarot, DungeonMode).
///
/// A transpiler replaces that one call with <see cref="ReadText"/>: the file's bytes read as they are (its length, its position,
/// <c>Seek(0)</c>, <c>GetBuffer</c>, the position put back as <c>GetAsText</c> puts it back), and when they are inside
/// <see cref="SaveBackupCheck.InGate"/> (strict UTF-8, no NUL byte, no UTF-8 BOM) and all of them were read, decoded with
/// <c>Encoding.UTF8.GetString</c> (the game's own <c>ReadFileAsync</c> decodes so). Otherwise the game's <c>GetAsText</c> runs.
/// Nothing is kept between reads (the file may change in between: Steam Cloud, other mods' writes).
///
/// Exact: inside the gate Godot's <c>GetAsText()</c> (skipCr false: CR kept) gives <c>Encoding.UTF8.GetString</c>'s string (it strips
/// a leading BOM, stops at a NUL, replaces invalid sequences: none occurs); the same equivalence <see cref="SaveBackupCheck"/> ships
/// on. A net: the session's first decoded read and one in <see cref="VerifyEvery"/> after it (every read in test builds) also run
/// the game's <c>GetAsText</c> and compare the two strings; a difference turns this part off (the game's string is returned).
/// Both game versions (v0.111.1 and v0.113.1 share the method).
///
/// The transpiler runs at <see cref="Priority.Last"/>: it reads what other mods' transpilers left, so one that changed the
/// GetAsText call keeps its change (nothing of this mod there then), and any transpiler backs the part off at run time
/// (<see cref="PatchTolerance.TranspiledOnly"/> refuses them). Residual: another mod's transpiler also at Priority.Last and
/// registered after this mod's runs after it and sees the helper call; it backs the part off all the same (the helper then calls
/// the game's GetAsText), as inner patches do.
/// </summary>
public static class SaveReadDecode
{
    public const string Name = "SaveReadDecode";

    /// <summary><c>GodotFileIo.ReadFile(string)</c>, v0.107.1 to v0.113.1: open, the open error handled, <c>GetAsText()</c>, close.</summary>
    private const string ReadFileFingerprint = "7916961A4B5E2E82";

#if BENCH
    internal static int VerifyEvery { get; set; } = 1;

    /// <summary>
    /// Positive control (<c>bdperf saveread wrong</c>): the decoded text made different on the reads the net compares (never handed
    /// to the game: the net returns the game's text then), so the net must catch it and turn the part off.
    /// </summary>
    internal static bool WrongForTest { get; set; }

    /// <summary>Decoded reads of a run save (a path containing <c>current_run</c>): the scenarios' proof that the run save went through.</summary>
    public static long RunSaveReads;

    /// <summary>The last run save decoded (its Godot path), for the bench.</summary>
    internal static string? LastRunSave;
#else
    internal static int VerifyEvery { get; set; } = 64;
#endif

    private static volatile bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static bool Applied { get; private set; }

    // Counters (any thread: Interlocked). Decoded is the effect counter; Checks the net's comparisons; the misses by reason.
    public static long Decoded;
    public static long Checks;
    public static long Mismatches;
    public static long MissGate;
    public static long MissShort;
    public static long MissBackOff;

    private static MethodInfo? _readFile;
    private static MethodInfo? _getAsText;

    /// <summary>
    /// The FileAccess calls this part makes and the game's read doesn't (its text is read natively by GetAsText): a mod patching
    /// one of them (decrypting in GetBuffer, say) would show this part other bytes than GetAsText decodes.
    /// </summary>
    private static MethodInfo[] _fileReads = Array.Empty<MethodInfo>();
    private static int _replaced;

    public static void Apply(Harmony harmony)
    {
        MethodInfo readFile = Reflect.DeclaredMethod(typeof(GodotFileIo), nameof(GodotFileIo.ReadFile), new[] { typeof(string) })
            ?? throw new MissingMethodException(nameof(GodotFileIo), nameof(GodotFileIo.ReadFile));
        PatchSafety.RequireFingerprint(readFile, ReadFileFingerprint);
        _getAsText = Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetAsText), new[] { typeof(bool) })
            ?? throw new MissingMethodException(nameof(Godot.FileAccess), nameof(Godot.FileAccess.GetAsText));
        MethodInfo?[] reads =
        {
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetLength), Type.EmptyTypes),
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetPosition), Type.EmptyTypes),
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.Seek), new[] { typeof(ulong) }),
            Reflect.Method(typeof(Godot.FileAccess), nameof(Godot.FileAccess.GetBuffer), new[] { typeof(long) }),
        };
        if (reads.Any(m => m == null))
        {
            throw new MissingMethodException(nameof(Godot.FileAccess), "GetLength, GetPosition, Seek or GetBuffer");
        }
        _fileReads = reads.Cast<MethodInfo>().ToArray();
        _readFile = readFile;
        _replaced = 0;
        // Priority.Last: other mods' transpilers run first. One that replaced or moved the GetAsText call keeps its change (this
        // one finds no such call and leaves the code as it is), and the part backs off from it at run time (a transpiler).
        PatchGuard.Patch(harmony, readFile, transpiler: new HarmonyMethod(typeof(SaveReadDecode), nameof(Transpiler)) { priority = Priority.Last });
        if (_replaced != 1)
        {
            // ModEntry.TryApply removes the transpiler that replaced nothing.
            throw new PatchSkippedException($"GodotFileIo.ReadFile: {_replaced} reads as text found where 1 was expected, left vanilla");
        }
        // ReadFile itself: this mod's only code there is the transpiler above, which keeps what the body returns (the same string),
        // so other mods' prefixes and postfixes run there as without this mod (TranspiledOnly: a prefix rewriting the path, a
        // postfix rewriting __result); a transpiler, a finalizer that isn't a pass-through or an inner patch backs off.
        PatchSafety.Watch(Name, readFile, PatchTolerance.TranspiledOnly);
        // What a decoded read skips: the game's GetAsText (a patch there would not run). Strict.
        PatchSafety.Watch(Name, _getAsText);
        // The reads only this part makes: strict (what any patch there returns could differ from what GetAsText reads).
        PatchSafety.Watch(Name, _fileReads, PatchTolerance.None);
        Applied = true;
    }

    /// <summary><c>fileAccess.GetAsText(false)</c> in ReadFile becomes <c>ReadText(fileAccess, false)</c>: the same stack in and out.</summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo helper = Reflect.Method(typeof(SaveReadDecode), nameof(ReadText));
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Calls(_getAsText))
            {
                code[i] = new CodeInstruction(OpCodes.Call, helper).WithLabels(code[i].labels).WithBlocks(code[i].blocks);
                _replaced++;
            }
        }
        return code;
    }

    /// <summary>Another mod patches what this part relies on (each checked, so each back-off is recorded).</summary>
    private static bool BackedOff()
    {
        bool patched = PatchSafety.IsPatchedByOthers(_readFile, PatchTolerance.TranspiledOnly);
        patched |= PatchSafety.IsPatchedByOthers(_getAsText);
        foreach (MethodInfo read in _fileReads)
        {
            patched |= PatchSafety.IsPatchedByOthers(read);
        }
        return patched;
    }

    /// <summary>What ReadFile asks: the file's text.</summary>
    internal static string ReadText(Godot.FileAccess file, bool skipCr)
    {
        if (_enabled && !skipCr && file != null)
        {
            string? text = null;
            ulong position = 0;
            bool moved = false;
            try
            {
                if (BackedOff())
                {
                    Interlocked.Increment(ref MissBackOff);
                }
                else
                {
                    text = Decode(file, ref position, ref moved);
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
                text = null;
            }
            if (text != null)
            {
                long n = Interlocked.Increment(ref Decoded);
#if BENCH
                try
                {
                    if (file.GetPath() is { } path && path.Contains("current_run", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref RunSaveReads);
                        LastRunSave = path;
                    }
                }
                catch (Exception)
                {
                    // Test bookkeeping only.
                }
#endif
                // The session's first decoded read and one in VerifyEvery after it: the game's read too.
                if ((n - 1) % Math.Max(1, VerifyEvery) != 0)
                {
                    return text;
                }
#if BENCH
                if (WrongForTest)
                {
                    text += "\u0001";
                }
#endif
                // The game's call once, outside any try of this mod: whatever it throws reaches ReadFile unchanged, as without this
                // mod. Its string is the one returned.
                Interlocked.Increment(ref Checks);
                string game = file.GetAsText(skipCr);
                if (!string.Equals(game, text, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref Mismatches);
                    Disable($"MISMATCH on {SafePath(file)}: decoded {text.Length} chars, the game's text {game?.Length ?? -1} chars{FirstDifference(text, game)}");
                    // SaveBackupCheck's verdicts rest on the same premise (Godot's text is the UTF-8 decoding of the bytes).
                    try
                    {
                        SaveBackupCheck.TurnOffAfterReadMismatch();
                    }
                    catch (Exception)
                    {
                        // Its class unloadable on this game version: nothing of it runs anyway.
                    }
                }
                return game!;
            }
        }
        // The game's call (a null file throws there as it would have).
        return file!.GetAsText(skipCr);
    }

    /// <summary>
    /// The file's text decoded from its bytes, or null when this part leaves the read to the game (bytes outside the gate, fewer
    /// bytes read than its length). The position is put back as found.
    /// </summary>
    internal static string? Decode(Godot.FileAccess file, ref ulong position, ref bool moved)
    {
        ulong length = file.GetLength();
        if (length > (ulong)Array.MaxLength)
        {
            Interlocked.Increment(ref MissShort);
            return null;
        }
        // As GetAsText reads: from the start, the position put back after.
        position = file.GetPosition();
        moved = true;
        file.Seek(0);
        byte[]? bytes = file.GetBuffer((long)length);
        file.Seek(position);
        moved = false;
        if (bytes == null || (ulong)bytes.LongLength != length)
        {
            // Godot's GetAsText returns an empty string with an error for a short read: the game's call says so itself.
            Interlocked.Increment(ref MissShort);
            return null;
        }
        if (!SaveBackupCheck.InGate(bytes))
        {
            Interlocked.Increment(ref MissGate);
            return null;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string SafePath(Godot.FileAccess file)
    {
        try
        {
            return file.GetPath();
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string FirstDifference(string ours, string? game)
    {
        if (game == null)
        {
            return "";
        }
        int i = 0;
        while (i < ours.Length && i < game.Length && ours[i] == game[i])
        {
            i++;
        }
        return $", first difference at char {i}";
    }

    private static void Disable(string why)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, save files are read as the game reads them: {why}");
    }
}
