using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Config;

/// <summary>
/// Menu texts in the game's other languages: one flat JSON object per game language code (Localization/fra.json...),
/// embedded in the DLL. Keys (fra.json has them all): general, optimizations, enabled.title/desc, coop_note,
/// why.limitless/limitless_changed/game/baselib/not_needed/fault/started_off/coop_run/coop_run_on, parts_off, conflict, conflict_summary, other_mods,
/// fast_combat_start_relics.title/desc, the join refusal's explanation (join.*: <see cref="Patches.JoinMismatchNote"/>)
/// and &lt;option key&gt;.title/desc. English stays in the code (<see cref="Settings"/>, BaseLibSettings, bdopt, JoinMismatchNote) and is the
/// fallback for a language without a file or a key missing from one.
/// </summary>
public static class Translations
{
    private static readonly Dictionary<string, Dictionary<string, string>?> Loaded = new();

    /// <summary>The text for this game language code ("fra", "zhs"...), or <paramref name="english"/>.</summary>
    public static string Get(string? language, string key, string english)
        => language != null && Table(language) is { } table && table.TryGetValue(key, out string? text) && text.Length > 0 ? text : english;

    public static bool Has(string language) => Table(language) != null;

    private static Dictionary<string, string>? Table(string language)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(language, out Dictionary<string, string>? table))
            {
                table = Load(language);
                Loaded[language] = table;
            }
            return table;
        }
    }

    private static Dictionary<string, string>? Load(string language)
    {
        try
        {
            using Stream? stream = typeof(Translations).Assembly.GetManifestResourceStream($"BigDeckOptimization.Localization.{language}.json");
            return stream == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] menu texts for '{language}' unreadable, English instead: {e.Message}");
            return null;
        }
    }
}
