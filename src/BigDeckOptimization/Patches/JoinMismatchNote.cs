using System;
using System.Collections.Generic;
using System.Reflection;
using BigDeckOptimization.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The game's "mod mismatch" error, when it is only about this mod's join list entry (<see cref="CoopModeTag"/>), says why in
/// plain words. The game lists each side's entries the other lacks: "BigDeckOptimization-v1.1.0+off" against
/// "BigDeckOptimization-v1.1.0" tells a player nothing. When each side lists exactly one entry and both are this mod's, a
/// postfix on <c>NetErrorInfo.GetErrorString</c> (the description the joining player's error popup shows) puts in place of the
/// game's two lines (when its text is exactly those two lines: <see cref="Compose"/>; else after them): the mod off on one side (the <see cref="CoopModeTag.OffSuffix"/> tag on one side only), the "Co-op state check" option set differently
/// (<see cref="CoopModeTag.ExactSnapSuffix"/> variants differing), or another version of this mod, then one line saying what
/// must match. Any other difference (another mod, this mod missing on one side) keeps the game's text alone. The joining
/// player only (the owner's choice): on v0.111+ the host's failed-join message is built from the same method, with
/// <c>localIsHost</c> set, and keeps the game's text.
///
/// Display only: the string the popup shows, built after the game decided to refuse the join; the join decision, the
/// handshake and the lists are the game's (read, never changed). Applied even when the mod was turned off at the start
/// (<see cref="ModEntry.StartedOff"/>), as CoopModeTag is, and not tied to the master switch: the player whose mod is off is
/// the one who most needs the explanation. Its own <see cref="Enabled"/> flag (test tooling only); an exception turns it off
/// for the session (the game's text unchanged); another mod patching <c>GetErrorString</c> keeps the game's (and that mod's)
/// text, but for postfixes Harmony runs before this one (Priority.Last; PostfixesBefore, 2026-11-18): they see the game's text
/// as without this mod, and this one explains on the text they leave (in place of the game's two lines when it is still exactly
/// those, else after it). Where the lists come from, per game version (the method's fingerprint picks the reading): v0.107.1 the client's
/// <c>ConnectionFailureExtraInfo.missingModsOnHost</c> / <c>missingModsOnLocal</c> fields (JoinFlow's lists, client only);
/// v0.111+ its <c>GetMissingModsOnRemote</c> / <c>GetMissingModsOnLocal</c> (the calls GetErrorString makes; both pure).
/// </summary>
public static class JoinMismatchNote
{
    public const string Name = "JoinMismatchNote";

    /// <summary><c>NetErrorInfo.GetErrorString</c>, game v0.107.1 (the extra info's two list fields, client only).</summary>
    private const string ErrorStringFingerprint107 = "5887659BB23A2849";

    /// <summary><c>NetErrorInfo.GetErrorString</c>, game v0.111.0 to v0.113.1 (the extra info's list methods, client and host).</summary>
    private const string ErrorStringFingerprint111 = "2BAB11851CD139CA";

    /// <summary><c>ConnectionFailureExtraInfo.GetMissingModsOnRemote</c> / <c>GetMissingModsOnLocal</c>, game v0.111.0 to v0.113.1.</summary>
    private const string MissingOnRemoteFingerprint = "DB9E074A3AF96990";
    private const string MissingOnLocalFingerprint = "0BB3F0782C8F7A99";

    /// <summary>Off: the game's text alone (test tooling's vanilla side; an exception turns it off for the session).</summary>
    public static bool Enabled { get; set; } = true;

    public static bool Applied { get; private set; }

    /// <summary>Error descriptions this patch added its explanation to (the effect counter).</summary>
    public static long Explained { get; private set; }

    private static MethodInfo? _original;
    private static FieldInfo? _reason;

    // v0.107.1: NetErrorInfo._connectionExtraInfo, and its two list fields.
    private static FieldInfo? _extraField;
    private static FieldInfo? _missingOnHostField;
    private static FieldInfo? _missingOnLocalField;

    // v0.111+: NetErrorInfo.ConnectionExtraInfo, its list methods and localIsHost.
    private static MethodInfo? _extraGetter;
    private static MethodInfo? _missingOnRemote;
    private static MethodInfo? _missingOnLocal;
    private static FieldInfo? _localIsHost;

    public static void Apply(Harmony harmony)
    {
        MethodInfo original = Reflect.Method(typeof(NetErrorInfo), nameof(NetErrorInfo.GetErrorString))
            ?? throw new MissingMethodException(nameof(NetErrorInfo), nameof(NetErrorInfo.GetErrorString));
        _reason = Reflect.Field(typeof(NetErrorInfo), "_connectionReason") ?? throw new MissingFieldException(nameof(NetErrorInfo), "_connectionReason");
        Type extra = typeof(ConnectionFailureExtraInfo);
        string actual = MethodFingerprint.Of(original);
        if (PatchSafety.FingerprintMatches(actual, ErrorStringFingerprint107))
        {
            _extraField = Reflect.Field(typeof(NetErrorInfo), "_connectionExtraInfo") ?? throw new MissingFieldException(nameof(NetErrorInfo), "_connectionExtraInfo");
            _missingOnHostField = Reflect.Field(extra, "missingModsOnHost") ?? throw new MissingFieldException(extra.Name, "missingModsOnHost");
            _missingOnLocalField = Reflect.Field(extra, "missingModsOnLocal") ?? throw new MissingFieldException(extra.Name, "missingModsOnLocal");
        }
        else if (PatchSafety.FingerprintMatches(actual, ErrorStringFingerprint111))
        {
            _extraGetter = Reflect.PropertyGetter(typeof(NetErrorInfo), "ConnectionExtraInfo") ?? throw new MissingMemberException(nameof(NetErrorInfo), "ConnectionExtraInfo");
            _missingOnRemote = Reflect.Method(extra, "GetMissingModsOnRemote", new[] { typeof(bool) }) ?? throw new MissingMethodException(extra.Name, "GetMissingModsOnRemote");
            _missingOnLocal = Reflect.Method(extra, "GetMissingModsOnLocal", new[] { typeof(bool) }) ?? throw new MissingMethodException(extra.Name, "GetMissingModsOnLocal");
            PatchSafety.RequireFingerprint(_missingOnRemote, MissingOnRemoteFingerprint);
            PatchSafety.RequireFingerprint(_missingOnLocal, MissingOnLocalFingerprint);
            _localIsHost = Reflect.Field(extra, "localIsHost") ?? throw new MissingFieldException(extra.Name, "localIsHost");
        }
        else
        {
            throw new PatchSkippedException($"NetErrorInfo.GetErrorString changed (fingerprint {actual}, expected {ErrorStringFingerprint107} or {ErrorStringFingerprint111}): the game's mismatch text alone");
        }
        _original = original;
        // Last of the postfixes: another mod's postfix registered later at its default priority runs before it and sees the
        // game's text, as without this mod (only one at Priority.Last registered after this mod's sorts after it).
        PatchGuard.Patch(harmony, original, postfix: new HarmonyMethod(typeof(JoinMismatchNote), nameof(Postfix)) { priority = Priority.Last });
        Applied = true;
        // The one gate in front of everything it does (display only).
        PatchSafety.Watch(Name, original, PatchTolerance.PostfixesBefore);
        PatchSafety.WholePatch(Name);
    }

    /// <summary>The game version's error says which side hosts (v0.111+: the host's failed-join message uses the same text, left as the game's).</summary>
    internal static bool HasHostVariant => _extraGetter != null;

    private static void Postfix(ref NetErrorInfo __instance, ref string __result)
    {
        if (!Enabled || __result == null)
        {
            return;
        }
        try
        {
            object boxed = __instance;
            // The joining player only: the host's failed-join message (v0.111+) keeps the game's text.
            if (_reason!.GetValue(boxed)?.ToString() != "ModMismatch" || !Lists(boxed, out List<string>? own, out List<string>? theirs, out bool isHost) || isHost)
            {
                return;
            }
            // Before the back-off check: a mismatch over other mods records no back-off of this patch.
            if (Explain(own, theirs, LocManager.Instance?.Language) is not { } note || PatchSafety.IsFaulted(Name) || PatchSafety.IsPatchedByOthers(_original, PatchTolerance.PostfixesBefore))
            {
                return;
            }
            __result = Compose(__result, own![0], theirs![0], note.Text, out bool replaced);
            Explained++;
            Log.Info($"[{ModEntry.Id}] {Name}: join refused over this mod's entry ({note.Kind}, client: "
                + $"{string.Join(",", own)} vs {string.Join(",", theirs)}), explanation {Explained} "
                + $"{(replaced ? "in place of the game's two lines" : "added after the game's lines")}: {note.Text.Replace("\n", " / ")}");
        }
        catch (Exception e)
        {
            // Runs in the game's error popup: never throws there; the game's text stays as it is.
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Warn($"[{ModEntry.Id}] {Name}: mod mismatch text left as the game wrote it, off for this session: {e}");
        }
    }

    /// <summary>
    /// The lists the game's text names: this player's entries the other side lacks (<paramref name="own"/>) and the other side's
    /// entries this player lacks (<paramref name="theirs"/>); <paramref name="isHost"/> when this player hosts (v0.111+). False
    /// without the extra info (a refusal the host sent on v0.107.1: the game's text names no mod either).
    /// </summary>
    private static bool Lists(object boxed, out List<string>? own, out List<string>? theirs, out bool isHost)
    {
        own = theirs = null;
        isHost = false;
        if (_extraGetter != null)
        {
            object? extra = _extraGetter.Invoke(boxed, null);
            if (extra == null)
            {
                return false;
            }
            own = _missingOnRemote!.Invoke(extra, new object[] { false }) as List<string>;
            theirs = _missingOnLocal!.Invoke(extra, new object[] { false }) as List<string>;
            isHost = _localIsHost!.GetValue(extra) is true;
        }
        else
        {
            object? extra = _extraField!.GetValue(boxed);
            if (extra == null)
            {
                return false;
            }
            own = _missingOnHostField!.GetValue(extra) as List<string>;
            theirs = _missingOnLocalField!.GetValue(extra) as List<string>;
        }
        return own != null && theirs != null;
    }

    /// <summary>The game's description, then the explanation on lines of its own.</summary>
    internal static string Append(string game, string note) =>
        game.Length == 0 || game.EndsWith("\n", StringComparison.Ordinal) ? game + note : game + "\n" + note;

    /// <summary>
    /// The text the popup shows: the explanation alone in place of the game's two lines when the game's text is exactly those two
    /// lines, the first naming this player's entry <paramref name="own"/> (and not the other), the second the other side's
    /// <paramref name="theirs"/> (and not this one): "BigDeckOptimization-v1.1.0+off" and "BigDeckOptimization-v1.1.0" read as two
    /// different mods, while the explanation names the one mod and what differs. Any other shape (a game update adding a line,
    /// another wording) keeps the game's lines, the explanation after them (<paramref name="replaced"/> false).
    /// </summary>
    internal static string Compose(string game, string own, string theirs, string note, out bool replaced)
    {
        var lines = new List<string>();
        foreach (string line in game.Replace("\r", "").Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                lines.Add(line);
            }
        }
        replaced = lines.Count == 2 && Names(lines[0], own) && !Names(lines[0], theirs) && Names(lines[1], theirs) && !Names(lines[1], own);
        return replaced ? note : Append(game, note);
    }

    /// <summary>The line names this entry whole: not as the start of a longer one ("-v1.1.0" inside "-v1.1.0+off").</summary>
    internal static bool Names(string line, string entry)
    {
        for (int at = line.IndexOf(entry, StringComparison.Ordinal); at >= 0; at = line.IndexOf(entry, at + 1, StringComparison.Ordinal))
        {
            int end = at + entry.Length;
            if ((at == 0 || !EntryChar(line[at - 1])) && (end == line.Length || !EntryChar(line[end])))
            {
                return true;
            }
        }
        return false;
    }

    private static bool EntryChar(char c) => char.IsLetterOrDigit(c) || c is '+' or '-' or '.' or '_';

    /// <summary>
    /// This mod's join list entry (<see cref="CoopModeTag"/>): <c>BigDeckOptimization-&lt;version&gt;[tag]</c>, the tag one of
    /// <see cref="CoopModeTag.Suffixes"/>. False for another mod's entry, including one whose id merely starts with this mod's
    /// ("BigDeckOptimization-Extra-v1": a version never has a dash, the tags' own dash stripped first).
    /// </summary>
    internal static bool TryParse(string? entry, out string version, out string tag)
    {
        version = tag = "";
        string prefix = ModEntry.Id + "-";
        if (entry == null || !entry.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        string rest = entry.Substring(prefix.Length);
        foreach (string known in CoopModeTag.Suffixes)
        {
            if (rest.EndsWith(known, StringComparison.Ordinal))
            {
                tag = known;
                rest = rest.Substring(0, rest.Length - known.Length);
                break;
            }
        }
        version = rest;
        return rest.Length > 0 && rest.IndexOf('-') < 0;
    }

    /// <summary>
    /// The explanation for these lists (see <see cref="Lists"/>), in this game language code (English without a translation),
    /// or null when they differ by more than this mod's entry: exactly one entry on each side, both this mod's. Kind: "version",
    /// "off-you" (this player, joining, has the mod off and the host on), "on-you", "exactsnap". The joining player's wording
    /// (the host's message is left as the game's). Pure (the bench feeds it synthetic lists).
    /// </summary>
    internal static (string Kind, string Text)? Explain(IReadOnlyList<string>? own, IReadOnlyList<string>? theirs, string? language)
    {
        if (own is not { Count: 1 } || theirs is not { Count: 1 }
            || !TryParse(own[0], out string ownVersion, out string ownTag) || !TryParse(theirs[0], out string theirVersion, out string theirTag))
        {
            return null;
        }
        string kind;
        string text;
        if (ownVersion != theirVersion)
        {
            kind = "version";
            text = Translations.Get(language, "join.version", VersionText).Replace("{yours}", ownVersion).Replace("{theirs}", theirVersion);
        }
        else if (ownTag == theirTag)
        {
            return null;
        }
        else if (ownTag == CoopModeTag.OffSuffix)
        {
            kind = "off-you";
            text = Translations.Get(language, "join.off_you", OffYouText);
        }
        else if (theirTag == CoopModeTag.OffSuffix)
        {
            kind = "on-you";
            text = Translations.Get(language, "join.on_you", OnYouText);
        }
        else
        {
            kind = "exactsnap";
            text = Translations.Get(language, "join.exactsnap", ExactSnapText);
        }
        return (kind, text + "\n" + Translations.Get(language, "join.must_match", MustMatchText));
    }

    // English texts (the translations: Localization/<language>.json, same keys). No square brackets: the popup reads BBCode.
    public const string OffYouText =
        "Big Deck Optimization is turned off for you but on for the host. Every player must have it on, or every player off: turn it on (its settings, or bdopt on in the console; restart the game if it asks), or ask the host to turn it off, then join again.";

    public const string OnYouText =
        "Big Deck Optimization is on for you but turned off for the host. Every player must have it on, or every player off: turn it off (its settings, or bdopt off in the console), or ask the host to turn it on, then join again.";

    public const string ExactSnapText =
        "The \"Co-op state check\" option of Big Deck Optimization isn't set the same way as the host's (next to mods like RitsuLib, it decides how that check works): set it the same way, then join again. If it already is, load the same mods in the same order as the host and restart the game.";

    public const string VersionText =
        "Every player needs the same version of Big Deck Optimization (yours: {yours}, the host's: {theirs}).";

    public const string MustMatchText =
        "What every player must have the same for Big Deck Optimization: the mod on or off, its version and, next to mods like RitsuLib, its \"Co-op state check\" option. Its other settings may differ (the fast combat start is on for every player in co-op anyway).";
}
