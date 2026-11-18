using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Config;
using BigDeckOptimization.Patches;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf joinmsg [backoff]</c>: <see cref="JoinMismatchNote"/> without a network. Each case builds the game's own error
/// (<c>new NetErrorInfo(ConnectionFailureReason.ModMismatch, extra info)</c>, the extra info filled as this game version's join
/// check fills it: v0.107.1 the client's two missing-mods lists, v0.111+ both sides' <c>PeerVersionInfo.gameplayAffectingMods</c>
/// and <c>localIsHost</c>) from synthetic lists, and calls the patched <c>GetErrorString</c> twice: the patch off (the game's
/// text) and on. Required per case: the game's text names the lists' entries (the extra info is read as the game reads it); the
/// kind <see cref="JoinMismatchNote.Explain"/> gives is the case's; the patched text is the game's text alone (no kind) or that
/// explanation in place of the game's two lines (<see cref="JoinMismatchNote.Compose"/>, no raw entry left); the effect counter grew by one exactly when explained. The joining player's side
/// only: the host cases (v0.111+, where the error says who hosts) are controls, their text the game's own. Then, outside the
/// game's method: the English texts exactly (two cases), and in every language file the mod embeds, each kind's text
/// translated (differs from English), its placeholders
/// filled, no square bracket (the popup reads BBCode). <c>backoff</c>: another mod's patch this patch can't run around is on
/// <c>GetErrorString</c> (scenario <c>joinmsg</c>'s <c>foreignpatch</c>: a postfix sorted after this one): every case must give
/// the game's text alone. A postfix sorted before this one is let stand: run without <c>backoff</c>, every case explained. Changes no state: the patch's
/// switch is put back, and the effect counters (bdperf's freeze).
/// </summary>
internal static class JoinMismatchBench
{
    private const string Off = "+off";

    private static string Ours(string version, string tag = "") => ModEntry.Id + "-" + version + tag;

    /// <summary>
    /// Name, this player's entries the other side lacks, the other side's this player lacks, host, the kind
    /// <see cref="JoinMismatchNote.Explain"/> must give (null: none; explained only on the joining player's side).
    /// </summary>
    private static readonly (string Name, string[] Own, string[] Theirs, bool Host, string? Kind, string Reason)[] Cases =
    {
        ("you off, host on", new[] { Ours("v1.1.0", Off) }, new[] { Ours("v1.1.0") }, false, "off-you", "ModMismatch"),
        ("you on, host off", new[] { Ours("v1.1.0") }, new[] { Ours("v1.1.0", Off) }, false, "on-you", "ModMismatch"),
        ("you off, host exactsnap", new[] { Ours("v1.1.0", Off) }, new[] { Ours("v1.1.0", "+exactsnap") }, false, "off-you", "ModMismatch"),
        ("you exactsnap, host none", new[] { Ours("v1.1.0", "+exactsnap") }, new[] { Ours("v1.1.0") }, false, "exactsnap", "ModMismatch"),
        ("you exactsnap-c, host exactsnap-r", new[] { Ours("v1.1.0", "+exactsnap-c") }, new[] { Ours("v1.1.0", "+exactsnap-r") }, false, "exactsnap", "ModMismatch"),
        ("you none, host exactsnap-c", new[] { Ours("v1.1.0") }, new[] { Ours("v1.1.0", "+exactsnap-c") }, false, "exactsnap", "ModMismatch"),
        ("versions differ", new[] { Ours("v1.1.0") }, new[] { Ours("v1.0.3") }, false, "version", "ModMismatch"),
        ("versions and tags differ", new[] { Ours("v1.1.0", Off) }, new[] { Ours("v1.0.3") }, false, "version", "ModMismatch"),
        // Controls: the game's text alone. The host's failed-join message (v0.111+) never gets the explanation (the owner's choice),
        // even for lists the joining player's popup explains.
        ("host's message: you on, joining player off", new[] { Ours("v1.1.0") }, new[] { Ours("v1.1.0", Off) }, true, "on-you", "ModMismatch"),
        ("host's message: versions differ", new[] { Ours("v1.0.3") }, new[] { Ours("v1.1.0") }, true, "version", "ModMismatch"),
        ("another mod differs too", new[] { Ours("v1.1.0", Off), "OtherMod-v1.0" }, new[] { Ours("v1.1.0") }, false, null, "ModMismatch"),
        ("this mod missing on the host", new[] { Ours("v1.1.0", Off) }, Array.Empty<string>(), false, null, "ModMismatch"),
        ("this mod missing here", Array.Empty<string>(), new[] { Ours("v1.1.0") }, false, null, "ModMismatch"),
        ("another mod only", new[] { "OtherMod-v1.0" }, new[] { "OtherMod-v2.0" }, false, null, "ModMismatch"),
        ("a mod whose id starts with this one's", new[] { ModEntry.Id + "-Extra-v1.0" }, new[] { Ours("v1.1.0") }, false, null, "ModMismatch"),
        ("two entries of this mod on one side", new[] { Ours("v1.1.0", Off), Ours("v1.0.3", Off) }, new[] { Ours("v1.1.0") }, false, null, "ModMismatch"),
        ("not a mod mismatch", new[] { Ours("v1.1.0", Off) }, new[] { Ours("v1.1.0") }, false, null, "VersionMismatch"),
    };

    internal static string Run(string? mode)
    {
        bool backoff = mode == "backoff";
        if (mode is { Length: > 0 } && !backoff)
        {
            throw new InvalidOperationException("bdperf joinmsg [backoff]");
        }
        if (!JoinMismatchNote.Applied)
        {
            throw new InvalidOperationException("JOINMSG JoinMismatchNote is not applied (see the log): nothing to check");
        }
        string? language = LocManager.Instance?.Language;
        var lines = new List<string>();
        var problems = new List<string>();
        bool enabled = JoinMismatchNote.Enabled;
        int ran = 0;
        try
        {
            foreach ((string name, string[] own, string[] theirs, bool host, string? kind, string reason) in Cases)
            {
                if (host && !JoinMismatchNote.HasHostVariant)
                {
                    lines.Add($"JOINMSG case '{name}': skipped (the game's error doesn't say who hosts on this version: the host gets no lists)");
                    continue;
                }
                NetErrorInfo info = Make(reason, own.ToList(), theirs.ToList(), host);
                JoinMismatchNote.Enabled = false;
                string game = info.GetErrorString();
                JoinMismatchNote.Enabled = true;
                long before = JoinMismatchNote.Explained;
                string patched = info.GetErrorString();
                long grew = JoinMismatchNote.Explained - before;
                (string Kind, string Text)? note = reason == "ModMismatch" ? JoinMismatchNote.Explain(own, theirs, language) : null;
                string? expectedKind = backoff || host ? null : kind;
                bool replaced = false;
                string expected = note is { } n && expectedKind != null ? JoinMismatchNote.Compose(game, own[0], theirs[0], n.Text, out replaced) : game;
                var wrong = new List<string>();
                // The game's text on the supported versions is exactly its two lines: the explanation takes their place (the
                // fallback, after them, is for a game text of another shape: a change to review here).
                if (expectedKind != null && !replaced)
                {
                    wrong.Add($"the game's two lines kept, the explanation after them (its text's shape changed?): '{Flat(game)}'");
                }
                if (expectedKind != null && (patched.Contains(own[0], StringComparison.Ordinal) || patched.Contains(theirs[0], StringComparison.Ordinal)))
                {
                    wrong.Add($"the patched text still names a raw join list entry: '{Flat(patched)}'");
                }
                if (reason == "ModMismatch" && own.Concat(theirs).Any(entry => !game.Contains(entry, StringComparison.Ordinal)))
                {
                    wrong.Add($"the game's text doesn't name every entry (extra info not read as the game reads it): '{Flat(game)}'");
                }
                if (reason == "ModMismatch" && note?.Kind != kind)
                {
                    wrong.Add($"kind {note?.Kind ?? "none"}, expected {kind ?? "none"}");
                }
                if (patched != expected)
                {
                    wrong.Add($"text '{Flat(patched)}', expected '{Flat(expected)}'");
                }
                if (grew != (expectedKind != null ? 1 : 0))
                {
                    wrong.Add($"effect counter grew by {grew}");
                }
                ran++;
                lines.Add($"JOINMSG case '{name}' ({(host ? "host" : "client")}): {(wrong.Count == 0 ? "ok" : "WRONG: " + string.Join("; ", wrong))}, "
                    + (expectedKind != null ? $"explained ({expectedKind}{(replaced ? ", in place of the game's two lines" : "")})" : "the game's text alone"));
                problems.AddRange(wrong.Select(w => $"'{name}': {w}"));
            }
        }
        finally
        {
            JoinMismatchNote.Enabled = enabled;
        }
        if (!backoff)
        {
            Texts(lines, problems);
        }
        string verdict = problems.Count == 0
            ? $"JOINMSG all {ran} cases ok{(backoff ? " (backoff: the game's text alone everywhere)" : "")}, game language {language ?? "none"}"
            : $"JOINMSG {problems.Count} problem(s): {string.Join(" | ", problems)}";
        lines.Add(verdict);
        string report = string.Join("\n", lines);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(report);
        }
        return report;
    }

    /// <summary>The English texts exactly, and every embedded language's translation of each kind.</summary>
    private static void Texts(List<string> lines, List<string> problems)
    {
        string[] on = { Ours("v1.1.0") }, off = { Ours("v1.1.0", Off) }, snap = { Ours("v1.1.0", "+exactsnap") }, old = { Ours("v1.0.3") };
        string? english = JoinMismatchNote.Explain(off, on, null)?.Text;
        if (english != JoinMismatchNote.OffYouText + "\n" + JoinMismatchNote.MustMatchText)
        {
            problems.Add($"English off-you text: '{Flat(english)}'");
        }
        string? version = JoinMismatchNote.Explain(on, old, null)?.Text;
        if (version == null || !version.StartsWith("Every player needs the same version of Big Deck Optimization (yours: v1.1.0, the host's: v1.0.3).\n", StringComparison.Ordinal))
        {
            problems.Add($"English version text: '{Flat(version)}'");
        }
        // Compose's shapes: two lines each naming its own entry whole are replaced; any other shape keeps the game's lines.
        string a = Ours("v1.1.0", Off), b = Ours("v1.1.0");
        var shapes = new (string Game, bool Replaced)[]
        {
            ($"We have: {a}\nThey have: {b}\n", true),
            ($"We have: {a}\r\nThey have: {b}\r\n", true),
            ($"Header\nWe have: {a}\nThey have: {b}\n", false),
            ($"We have: {a}, {b}\nThey have: {b}\n", false),
            ($"They have: {b}\nWe have: {a}\n", false),
            ($"We have: {a}\n", false),
            ($"We have: {b}+offx\nThey have: {b}\n", false),
        };
        foreach ((string game, bool expectReplaced) in shapes)
        {
            string composed = JoinMismatchNote.Compose(game, a, b, "NOTE", out bool replaced);
            if (replaced != expectReplaced || composed != (expectReplaced ? "NOTE" : JoinMismatchNote.Append(game, "NOTE")))
            {
                problems.Add($"Compose of '{Flat(game)}': replaced {replaced}, expected {expectReplaced} ('{Flat(composed)}')");
            }
        }
        var kinds = new (string[] Own, string[] Theirs)[] { (off, on), (on, off), (snap, on), (on, old) };
        const string prefix = "BigDeckOptimization.Localization.";
        string[] languages = typeof(Translations).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(prefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal))
            .Select(r => r.Substring(prefix.Length, r.Length - prefix.Length - ".json".Length)).OrderBy(l => l, StringComparer.Ordinal).ToArray();
        int texts = 0;
        foreach (string language in languages)
        {
            foreach ((string[] own, string[] theirs) in kinds)
            {
                string? text = JoinMismatchNote.Explain(own, theirs, language)?.Text;
                string? inEnglish = JoinMismatchNote.Explain(own, theirs, null)?.Text;
                string what = $"{language} {JoinMismatchNote.Explain(own, theirs, null)?.Kind}";
                if (text == null || text == inEnglish || text.Split('\n').Intersect(inEnglish?.Split('\n') ?? Array.Empty<string>()).Any())
                {
                    problems.Add($"{what}: not translated ('{Flat(text)}')");
                }
                else if (text.IndexOfAny(new[] { '{', '}', '[', ']' }) >= 0)
                {
                    problems.Add($"{what}: placeholder or square bracket left ('{Flat(text)}')");
                }
                else if ((own == old || theirs == old) && !(text.Contains("v1.1.0", StringComparison.Ordinal) && text.Contains("v1.0.3", StringComparison.Ordinal)))
                {
                    problems.Add($"{what}: versions not named ('{Flat(text)}')");
                }
                texts++;
            }
        }
        lines.Add($"JOINMSG texts: {shapes.Length} text shapes composed as expected, English exact, {texts} translated texts checked in {languages.Length} languages ({string.Join(", ", languages)})");
    }

    private static string Flat(string? text) => text?.Replace("\r", "").Replace("\n", " / ") ?? "null";

    /// <summary>The game's error for these lists, its extra info filled as this game version's join check fills it.</summary>
    private static NetErrorInfo Make(string reason, List<string> own, List<string> theirs, bool host)
    {
        Type extraType = typeof(ConnectionFailureExtraInfo);
        object extra = Activator.CreateInstance(extraType) ?? throw new InvalidOperationException("ConnectionFailureExtraInfo not created");
        if (JoinMismatchNote.HasHostVariant)
        {
            FieldInfo localField = Reflect.Field(extraType, "localInfo") ?? throw new MissingFieldException(extraType.Name, "localInfo");
            FieldInfo remoteField = Reflect.Field(extraType, "remoteInfo") ?? throw new MissingFieldException(extraType.Name, "remoteInfo");
            Type peer = localField.FieldType;
            FieldInfo mods = Reflect.Field(peer, "gameplayAffectingMods") ?? throw new MissingFieldException(peer.Name, "gameplayAffectingMods");
            object local = Activator.CreateInstance(peer)!;
            mods.SetValue(local, own);
            object remote = Activator.CreateInstance(peer)!;
            mods.SetValue(remote, theirs);
            localField.SetValue(extra, local);
            remoteField.SetValue(extra, remote);
            (Reflect.Field(extraType, "localIsHost") ?? throw new MissingFieldException(extraType.Name, "localIsHost")).SetValue(extra, host);
        }
        else
        {
            (Reflect.Field(extraType, "missingModsOnHost") ?? throw new MissingFieldException(extraType.Name, "missingModsOnHost")).SetValue(extra, own);
            (Reflect.Field(extraType, "missingModsOnLocal") ?? throw new MissingFieldException(extraType.Name, "missingModsOnLocal")).SetValue(extra, theirs);
        }
        var value = (ConnectionFailureReason)Enum.Parse(typeof(ConnectionFailureReason), reason);
        return new NetErrorInfo(value, (ConnectionFailureExtraInfo)extra);
    }
}
