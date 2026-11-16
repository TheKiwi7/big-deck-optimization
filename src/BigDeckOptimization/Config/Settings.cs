using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BigDeckOptimization.Patches;

namespace BigDeckOptimization.Config;

/// <summary>
/// User choices for the optimizations: a master switch plus one switch per optimization, all on by default. Each
/// patch's own Enabled flag is the effective state: master AND its option AND not faulted. A patch that turned itself
/// off after an error (<see cref="PatchSafety.Fault"/>) stays off for the session whatever the settings say.
/// With BaseLib (optional) they are shown in its mod configuration menu and saved by it (see SettingsMenu); bdopt
/// changes them too, for the session only without BaseLib.
/// </summary>
public static class Settings
{
    /// <summary>
    /// A part of an option with its own patch or fault. <paramref name="NeedsMods"/>: it can only apply with Limitless and BaseLib.
    /// <paramref name="NotNeeded"/>: true when this game version has nothing for it to do (not applied, and nothing missing).
    /// </summary>
    public readonly record struct OptionPart(string Name, bool NeedsMods = false, Func<bool>? NotNeeded = null);

    /// <param name="parts">What runs under this option apart from its main patch (<paramref name="faults"/>).</param>
    /// <param name="setParts">Switches the parts: they don't depend on the main patch, so its fault or absence leaves them on.</param>
    /// <param name="wholeFaults">Those of <paramref name="faults"/> that take the parts away too.</param>
    public sealed class Option(string key, string label, string description, string[] faults, Action<bool> setEnabled,
        Func<string?>? unavailable = null, bool defaultOn = true, OptionPart[]? parts = null, Action<bool>? setParts = null, string[]? wholeFaults = null)
    {
        /// <summary>The state before any choice (BaseLib's "restore defaults" goes back to it).</summary>
        public bool DefaultOn { get; } = defaultOn;
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string Description { get; } = description;
        /// <summary>Fault names (<see cref="PatchSafety.Fault"/>) that keep this optimization off.</summary>
        public string[] Faults { get; } = faults;
        public Action<bool> SetEnabled { get; } = setEnabled;
        /// <summary>Why the patch isn't applied here (optional mod absent, code changed), or null when it is.</summary>
        public Func<string?> Unavailable { get; } = unavailable ?? (() => faults.Any(ModEntry.NotApplied) ? GameChanged : null);
        public bool Wanted { get; internal set; } = defaultOn;
        public string? Fault => Faults.FirstOrDefault(PatchSafety.IsFaulted);
        public bool Effective => Master && Wanted && Fault == null;
        public OptionPart[] Parts { get; } = parts ?? Array.Empty<OptionPart>();
        public Action<bool>? SetParts { get; } = setParts;
        public bool PartsEffective => Master && Wanted && !(wholeFaults ?? Array.Empty<string>()).Any(PatchSafety.IsFaulted);

        /// <summary>Its parts still run though the main patch is off after a fault: switching the option still means something.</summary>
        public bool PartsOutliveFault => SetParts != null && !(wholeFaults ?? Array.Empty<string>()).Any(PatchSafety.IsFaulted);

        /// <summary>
        /// What of this option doesn't run although the option is on: parts that failed or didn't apply (one needing Limitless counts only
        /// with Limitless there), or the main patch when its parts run without it. Empty when nothing is missing.
        /// </summary>
        public IEnumerable<string> PartsOff()
        {
            if (SetParts != null)
            {
                foreach (string name in Faults)
                {
                    if (PatchSafety.IsFaulted(name))
                    {
                        yield return name + " (error)";
                    }
                    else if (ModEntry.NotApplied(name))
                    {
                        yield return name + " (not applied)";
                    }
                }
            }
            foreach (OptionPart part in Parts)
            {
                if (PatchSafety.IsFaulted(part.Name))
                {
                    yield return part.Name + " (error)";
                }
                else if (ModEntry.NotApplied(part.Name) && part.NotNeeded?.Invoke() != true && (!part.NeedsMods || LimitlessReason() == LimitlessChanged))
                {
                    yield return part.Name + " (not applied)";
                }
            }
        }
    }

    public const string MasterKey = "enabled";

    /// <summary><see cref="Option.Unavailable"/> answers (bdopt shows them as is, the menu explains them).</summary>
    public const string GameChanged = "not applied (game code changed)";

    /// <summary>Patches apply over the first frames: until they all did, a flag still down says nothing.</summary>
    private static string? Missing(bool applied) => applied || !ModEntry.AllApplied ? null : GameChanged;

    /// <summary>The same for a patch that needs another mod: <paramref name="reason"/> says what's missing.</summary>
    private static string? MissingMod(bool applied, Func<string> reason) => applied || !ModEntry.AllApplied ? null : reason();

    /// <summary>An option with parts is only unavailable when none of them applied.</summary>
    private static string? NoneApplied(params string[] names) => names.All(ModEntry.NotApplied) ? GameChanged : null;
    public const string LimitlessMissing = "not applied (Limitless absent or changed)";
    public const string LimitlessChanged = "not applied (game or Limitless code changed)";

    /// <summary>Why a Limitless optimization isn't applied: Limitless absent, or present but the code it relies on changed.</summary>
    private static string LimitlessReason() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Limitless") ? LimitlessChanged : LimitlessMissing;
    public const string BaseLibMissing = "not applied (BaseLib absent or changed)";

    /// <summary>
    /// Why the menu shows this optimization greyed out, in a sentence in this game language code (English when it has
    /// no translation); null when it can be switched.
    /// </summary>
    public static string? Why(Option option, string? language) => ModEntry.StartedOff ? StartedOffWhy(language) : option.Unavailable() switch
    {
        LimitlessMissing => Translations.Get(language, "why.limitless",
            "Unavailable: Limitless isn't installed, or a Limitless update changed what this optimization speeds up."),
        LimitlessChanged => Translations.Get(language, "why.limitless_changed",
            "Unavailable: a game or Limitless update changed what this optimization speeds up. It stays off (normal game) until the mod is updated."),
        BaseLibMissing => Translations.Get(language, "why.baselib",
            "Unavailable: a BaseLib update changed what this optimization speeds up. It stays off (normal game) until the mod is updated."),
        { } => Translations.Get(language, "why.game",
            "Unavailable: a game update changed what this optimization speeds up. It stays off (normal game) until the mod is updated."),
        null when option.Fault != null && !option.PartsOutliveFault => Translations.Get(language, "why.fault",
            "Turned off for this session after an error (normal game instead, details in the log). Back on next time you start the game."),
        null => null,
    };

    public const string StartedOffText =
        "Inactive: the mod was turned off when the game started, so none of it is loaded. Turn the mod on, then restart the game.";

    public static string StartedOffWhy(string? language) => Translations.Get(language, "why.started_off", StartedOffText);

    public const string CoopRunText =
        "Locked during a co-op run: with the mod off, your combats would start far later than the other players'. To turn it off anyway, type bdopt off yes in the console.";

    public const string CoopRunOnText =
        "Locked during a co-op run: every player must have the mod in the same state, and the others joined with it off, as you did. To turn it on anyway (only if every player does), type bdopt on yes in the console.";

    /// <summary>
    /// Why the menu's master switch is greyed out during a co-op run (<see cref="InCoopRun"/>), whichever way it is: the
    /// players there joined with the same state (<see cref="Patches.CoopModeTag"/>), and switching it makes them differ.
    /// </summary>
    public static string CoopRunWhy(string? language) => Master
        ? Translations.Get(language, "why.coop_run", CoopRunText)
        : Translations.Get(language, "why.coop_run_on", CoopRunOnText);

    /// <summary>The patch names whose back-offs and notes belong to this option (its main patches and its parts).</summary>
    private static IEnumerable<string> PatchNames(Option option) => option.Faults.Concat(option.Parts.Select(part => part.Name));

    /// <summary>
    /// The mods whose patches, types or data currently keep part of this optimization vanilla (it backs off from methods
    /// another mod patches, or a note says why); empty when none. Known from the main menu on: the patches register what
    /// they check, checked after the main menu shows (<see cref="PatchSafety.Preflight"/>), and at each runtime check.
    /// </summary>
    public static IReadOnlyList<string> ConflictingMods(Option option) => PatchSafety.ConflictingMods(PatchNames(option));

    public const string OtherModsText = "other mods";

    /// <summary>The menu's sentence about <see cref="ConflictingMods"/> (notes without a mod name included), or null when there is none.</summary>
    public static string? Conflict(Option option, string? language)
    {
        List<string> mods = ConflictingMods(option).ToList();
        if (PatchSafety.HasUnnamedNotes(PatchNames(option)))
        {
            mods.Add(Translations.Get(language, "other_mods", OtherModsText));
        }
        // A sentence whose verb agrees with no list (one mod, several, or "other mods"): passive in every language.
        return mods.Count == 0 ? null
            : Translations.Get(language, "conflict", ConflictText).Replace("{mods}", string.Join(", ", mods));
    }

    public const string ConflictText = "Partly inactive: what this optimization speeds up is also changed by {mods} (normal game for that part).";

    public const string ConflictSummaryText =
        "With your other mods, part of these optimizations runs the normal game: {mods}. Hover an option for details; bdopt why in the console lists them.";

    /// <summary>
    /// The settings page's line under the co-op note when other mods keep part of the optimizations vanilla: each mod with
    /// its count of back-offs and notes; null when there is none.
    /// </summary>
    public static string? ConflictSummary(string? language)
    {
        if (!Master || ModEntry.StartedOff)
        {
            return null; // Nothing of the mod runs: no part of it is kept vanilla by another mod.
        }
        IReadOnlyList<(string Mod, int Count)> perMod = PatchSafety.BackOffsPerMod(Running);
        bool unnamed = PatchSafety.BackOffs().Any(b => b.Mods.Length == 0 && Running(b.Patch));
        if (perMod.Count == 0 && !unnamed)
        {
            return null;
        }
        IEnumerable<string> mods = perMod.Select(m => $"{m.Mod} ({m.Count})");
        if (unnamed)
        {
            mods = mods.Append(Translations.Get(language, "other_mods", OtherModsText));
        }
        return Translations.Get(language, "conflict_summary", ConflictSummaryText).Replace("{mods}", string.Join(", ", mods));
    }

    /// <summary>
    /// Checks now what other mods keep vanilla (the settings page opening, bdopt): answers younger than 2 s come from the
    /// runtime checks' cache. Never throws.
    /// </summary>
    public static void RefreshConflicts() => PatchSafety.Preflight();

    /// <summary>
    /// bdopt why: every recorded back-off and note, grouped by the options they belong to ("Patch: Type.Method &lt;- mods",
    /// a note as its text), then a count per mod.
    /// </summary>
    public static string WhyReport()
    {
        // The parts that run now: a back-off of an option switched off, faulted or not applied changes nothing there.
        IReadOnlyList<PatchSafety.BackOff> backOffs = PatchSafety.BackOffs().Where(b => !Master || Running(b.Patch)).ToList();
        if (backOffs.Count == 0)
        {
            return "BigDeckOptimization: no other mod keeps any part of the optimizations on the normal game"
                + (ModEntry.StartedOff ? " (the mod is inactive this session)." : !Master ? " (the mod is off: everything runs the normal game now)." : ".");
        }
        var sb = new StringBuilder(!Master
            ? "BigDeckOptimization: the mod is off, everything runs the normal game now. Once on, these parts would still run the normal game because of other mods:\n"
            : "BigDeckOptimization: parts running the normal game because of other mods (the rest of each optimization still runs):\n");
        foreach (IGrouping<string, PatchSafety.BackOff> group in backOffs.GroupBy(b => OptionLabels(b.Patch)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(group.Key).Append(":\n");
            foreach (PatchSafety.BackOff backOff in group)
            {
                sb.Append($"  {backOff.Patch}: {backOff.What} <- {(backOff.Mods.Length > 0 ? string.Join(", ", backOff.Mods) : OtherModsText)}\n");
            }
        }
        IReadOnlyList<(string Mod, int Count)> perMod = PatchSafety.BackOffsPerMod(patch => !Master || Running(patch));
        if (perMod.Count > 0)
        {
            sb.Append("Per mod (methods and notes): ").Append(string.Join(", ", perMod.Select(m => $"{m.Mod} {m.Count}")));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A patch name whose back-offs say something now: it belongs to no option (shown as "Other"), or to one switched on that
    /// applied, and it hasn't turned itself off after an error. The whole mod's switch is the callers' to check.
    /// </summary>
    private static bool Running(string patch)
    {
        bool owned = false;
        foreach (Option option in Options)
        {
            if (!PatchNames(option).Contains(patch))
            {
                continue;
            }
            owned = true;
            if (option.Wanted && option.Unavailable() == null && !PatchSafety.IsFaulted(patch) && !ModEntry.NotApplied(patch))
            {
                return true;
            }
        }
        return !owned;
    }

    /// <summary>The labels of the options a patch name belongs to ("Other" when none).</summary>
    private static string OptionLabels(string patch)
    {
        string[] labels = Options.Where(o => PatchNames(o).Contains(patch)).Select(o => o.Label).ToArray();
        return labels.Length > 0 ? string.Join(" / ", labels) : "Other";
    }

    /// <summary>The menu's sentence when part of a switchable optimization doesn't run, or null.</summary>
    public static string? PartlyOff(Option option, string? language) => !option.PartsOff().Any() ? null
        : Translations.Get(language, "parts_off", "Partly inactive: part of this optimization turned itself off or doesn't apply here (normal game for that part, details in the log).");

    public static bool Master { get; private set; } = true;

    /// <summary>
    /// The player's choice of <see cref="Master"/> (menu, saved file, bdopt), what BaseLib saves: the test tooling's
    /// <c>bdperf opt</c> changes only <see cref="Master"/>, so BaseLib's save on quit (every config, on NGame.Quit) doesn't
    /// record it and the next launch isn't "started off".
    /// </summary>
    public static bool MasterChoice { get; private set; } = true;

    public const string FastStartRelicsKey = "fast_combat_start_relics";
    public const string FastStartRelicsLabel = "Fast combat start: minimum relics";
    public const string FastStartRelicsDescription =
        "Solo: speeds up the combat start only from this many relics (all players' relics counted). 0 = always. In co-op it's always on, whatever this value.";

    /// <summary>Relics (all players) from which the fast combat start applies in solo, 0-100 (0 = always).</summary>
    public static int FastStartRelics { get; private set; }

    /// <summary>The menu's slider steps (its Godot Range snaps any other value, then saves the snapped one).</summary>
    public const int FastStartRelicsStep = 10;

    /// <summary>Sets the threshold, clamped to 0-100 and rounded to the slider's step as the slider would.</summary>
    public static void SetFastStartRelics(int relics, bool notify = true)
    {
        relics = (int)Math.Round(Math.Clamp(relics, 0, 100) / (double)FastStartRelicsStep, MidpointRounding.AwayFromZero) * FastStartRelicsStep;
        bool changed = FastStartRelics != relics;
        FastStartRelics = relics;
        FastCombatStart.RelicThreshold = relics;
        if (changed && notify)
        {
            Changed?.Invoke(FastStartRelicsKey, true);
        }
    }

    public static IReadOnlyList<Option> Options { get; } = new Option[]
    {
        // Combat pace first, as in the menu; the rest of this order (bdopt lists it) isn't the menu's, which is BaseLibSettings' own.
        new("instant_combats", "Instant combat animations",
            "Visual, off by default: every fight plays without animation waits (attacks, hits, enemy turns). Outside of fights, nothing changes. In co-op it only affects your own screen: the other players still see the normal animations. Same results.",
            new[] { FastCombatStart.Name }, on => FastCombatStart.InstantCombats = on, defaultOn: false),
        new("fast_combat_start", "Fast combat start",
            "Visual: plays the combat-start relic effects without their animation waits (like the game's Instant speed), from the number of relics set below (0 = always, the default). Same results. Solo only: always on in co-op, so every player starts the combat at the same pace.",
            new[] { FastCombatStart.Name }, on => FastCombatStart.Enabled = on),
        new("coop_checksum", "Co-op state check",
            "Faster state check after every action in co-op, and in solo too on newer game versions (the biggest co-op lag with huge decks).",
            new[] { ListenerCache.Name, ListenerCache.SnapshotFault }, on => ListenerCache.SnapshotEnabled = on,
            () => NoneApplied(ListenerCache.Name, ReplayStateSkip.Name, SavedPropertyLookup.Name, RelicStateReuse.Name, SnapshotCardReuse.Name),
            parts: new OptionPart[] { new(SnapshotCardReuse.Name, NeedsMods: true), new(ReplayStateSkip.Name), new(SavedPropertyLookup.Name), new(RelicStateReuse.Name), new(RelicStateReuse.ScalarName) },
            setParts: on => { Flag(() => SnapshotCardReuse.Enabled = Part(on, SnapshotCardReuse.Name)); Flag(() => ReplayStateSkip.Enabled = Part(on, ReplayStateSkip.Name)); Flag(() => SavedPropertyLookup.Enabled = Part(on, SavedPropertyLookup.Name)); Flag(() => RelicStateReuse.Enabled = Part(on, RelicStateReuse.Name)); Flag(() => RelicStateReuse.ScalarEnabled = Part(on, RelicStateReuse.ScalarName)); },
            // A snapshot that differed from the game's: anything of the state check may be why.
            wholeFaults: new[] { ListenerCache.SnapshotFault }),
        new("combat_refresh", "Combat refresh",
            "Faster card and UI refresh after every action in combat.",
            new[] { ListenerCache.Name }, on => ListenerCache.RefreshEnabled = on,
            () => NoneApplied(ListenerCache.Name, TextPoolReuse.Name, ThemeColorSkip.Name, DeckCountMemo.Name),
            parts: new OptionPart[] { new(TextPoolReuse.Name), new(ThemeColorSkip.Name), new(DeckCountMemo.Name), new(ListenerCache.ReuseFault), new(ExtraScopes.Name), new(ExtraScopes.Name + ".BetterSpire2") },
            // ExtraScopes reads its own two fault names itself (a scope stays off once faulted): one switch for both.
            setParts: on => { Flag(() => TextPoolReuse.Enabled = Part(on, TextPoolReuse.Name)); Flag(() => ThemeColorSkip.Enabled = Part(on, ThemeColorSkip.Name)); Flag(() => DeckCountMemo.Enabled = Part(on, DeckCountMemo.Name)); Flag(() => ExtraScopes.Enabled = on); }),
        new("hook_filter", "Skip empty hooks",
            "Inside the two optimizations above, only asks the cards and relics that actually react to each check.",
            new[] { HookFilter.Name }, on => HookFilter.Enabled = on),
        new("relic_input", "Relic bar input",
            "Stops hundreds of idle relic buttons from processing every mouse move. Also skips old controller actions that no longer exist and wrote an error to the log at every input.",
            new[] { IdleButtonInput.Name }, on => IdleButtonInput.Enabled = on,
            () => NoneApplied(IdleButtonInput.Name, ControllerActionSkip.Name),
            parts: new OptionPart[] { new(ControllerActionSkip.Name) },
            setParts: on => ControllerActionSkip.Enabled = Part(on, ControllerActionSkip.Name)),
        new("relic_flashes", "Merge repeated relic flashes",
            "Visual: a relic flashing several times in the same frame (Bing Bong copying each cloned card...) shows one flash instead of thousands stacked on the same spot, which could black out the screen or crash the game. Same results.",
            new[] { RelicFlashMerge.Name }, on => RelicFlashMerge.Enabled = on),
        new("card_flights", "Limit flying cards",
            "Visual: when a huge pile moves at once (a reshuffle of thousands of cards), a steady stream of cards flies to the pile instead of one per card, which froze the game for minutes. Small piles look the same. Same results.",
            new[] { CardFlightLimit.Name }, on => CardFlightLimit.Enabled = on,
            () => Missing(CardFlightLimit.Applied)),
        new("gc_pacing", "Memory cleanups at quiet moments",
            "In combats with 1,000 cards or more, .NET's memory cleanups (the short freezes while cards are played) wait for a moment when nothing is animating. The cleanup after each room loads runs in the background, and finished combats are freed from memory (with BaseLib they stayed until the end of the run). Animated texts also free their temporary memory right away, which shortens the cleanups everywhere. Uses up to about 1.4 GB more memory during those combats. Same results.",
            new[] { GcPacing.Name }, on => GcPacing.Enabled = on,
            () => NoneApplied(GcPacing.Name, RoomCollect.Name, PooledCardRelease.Name, SyncedCardRelease.Name, RichTextEnvRelease.Name),
            parts: new OptionPart[] { new(RoomCollect.Name), new(PooledCardRelease.Name), new(SyncedCardRelease.Name), new(RichTextEnvRelease.Name) },
            setParts: on => { Flag(() => RoomCollect.Enabled = Part(on, RoomCollect.Name)); Flag(() => PooledCardRelease.Enabled = Part(on, PooledCardRelease.Name)); Flag(() => SyncedCardRelease.Enabled = Part(on, SyncedCardRelease.Name)); Flag(() => RichTextEnvRelease.Enabled = Part(on, RichTextEnvRelease.Name)); }),
        new("grid_sort", "Card grid sorting",
            "Faster sorting of the deck view and other card grids.",
            new[] { GridSortCache.Name }, on => GridSortCache.Enabled = on),
        new("grid_pool", "Card grid recycling",
            "Faster recycling of card nodes when a card grid opens or is sorted.",
            new[] { PoolSignalScan.Name }, on => PoolSignalScan.Enabled = on,
            () => Missing(PoolSignalScan.Applied)),
        new("pile_lookup", "Card pile lookup",
            "Finds which pile a card is in without scanning every card (used by costs, keywords, playability...), and checks a card added to a pile (draws, reshuffles, clones) without scanning that pile.",
            new[] { PileIndex.Name }, on => PileIndex.Enabled = on,
            () => NoneApplied(PileIndex.Name, CardSortKeys.Name),
            // Sorting a pile before a reshuffle (game v0.113's card comparison): its own part, not needed on earlier versions.
            parts: new OptionPart[] { new(CardSortKeys.Name, NotNeeded: () => CardSortKeys.NotNeeded) },
            // PileIndex.ContainsCardEnabled: the RunState.ContainsCard shortcut reads no index, so a PileIndex fault doesn't stop it.
            setParts: on => { Flag(() => CardSortKeys.Enabled = Part(on, CardSortKeys.Name)); Flag(() => PileIndex.ContainsCardEnabled = on); }),
        new("card_ids", "Combat card IDs",
            "Skips re-scanning whole card piles for new cards on every card move (reshuffles, end of combat).",
            new[] { CardDbScan.Name }, on => CardDbScan.Enabled = on,
            () => Missing(CardDbScan.Applied)),
        new("pile_counters", "Pile counters at combat end",
            "Updates the draw/discard/exhaust counters once when piles are emptied, instead of once per card.",
            new[] { PileCounterBatch.Name }, on => PileCounterBatch.Enabled = on,
            () => Missing(PileCounterBatch.Applied)),
        new("listeners", "Effect listener lists",
            "Builds the list of cards, relics and powers that each triggered effect asks with less copying and memory churn, and keeps it between effects while nothing changed (cloning thousands of cards: seconds instead of minutes). Same list, same order.",
            new[] { FastListeners.Name }, on => FastListeners.Enabled = on,
            () => Missing(FastListeners.CombatMirrored || FastListeners.RunMirrored)),
        new("hook_skip", "Skip non-reacting cards",
            "Each time the game processes an effect (card played, damage, turn end...), only asks the cards, enchantments, relics, potions and powers whose type reacts to it (same results).",
            new[] { HookSkip.Name, FastListeners.Name }, on => { Flag(() => HookSkip.Enabled = on); Flag(() => FastListeners.KindSkipEnabled = on); },
            () => Missing(HookSkip.Applied)),
        new("hover_tips", "Creature hover tips",
            "While the mouse is on a creature, rebuilds its tooltips after an action only when their content changed (they stay live).",
            new[] { HoverTipReuse.Name }, on => HoverTipReuse.Enabled = on,
            () => Missing(HoverTipReuse.Applied)),
        new("limitless_tabs", "Limitless enchantment tabs",
            "With Limitless: reuses enchantment tabs instead of rebuilding them on every card render.",
            new[] { EnchantmentTabReuse.Name }, on => EnchantmentTabReuse.Enabled = on,
            () => MissingMod(EnchantmentTabReuse.Applied, LimitlessReason)),
        new("limitless_saves", "Limitless enchantment copies",
            "With Limitless: keeps the saved copy of stacked enchantments when rebuilding it would give the same content, instead of rebuilding it after every action or each time a card is copied at the start of a combat.",
            new[] { LimitlessReserialize.Name }, on => { Flag(() => LimitlessReserialize.Enabled = on); Flag(() => LimitlessRecalcSkip.Enabled = Part(on, LimitlessRecalcSkip.Name)); Flag(() => LimitlessHookSkip.Enabled = Part(on, LimitlessHookSkip.Name)); Flag(() => LimitlessCloneMemo.Enabled = Part(on, LimitlessCloneMemo.Name)); },
            () => MissingMod(LimitlessReserialize.Applied, LimitlessReason),
            parts: new OptionPart[] { new(LimitlessRecalcSkip.Name, NeedsMods: true), new(LimitlessHookSkip.Name, NeedsMods: true), new(LimitlessCloneMemo.Name, NeedsMods: true) }),
        new("card_type_line", "BaseLib card type line",
            "With BaseLib: shows each card's type line without searching every card in combat for mods that change it, while no installed mod does (scrolling the draw pile, refreshing the hand). Same text.",
            new[] { TypePlaqueSkip.Name }, on => TypePlaqueSkip.Enabled = on,
            () => MissingMod(TypePlaqueSkip.Applied, () => BaseLibMissing)),
    };

    /// <summary>
    /// A part of an optimization that turns itself off alone after an error (the rest of the option keeps working):
    /// on with its option unless it faulted this session.
    /// </summary>
    private static bool Part(bool on, string name) => on && !PatchSafety.IsFaulted(name);

    /// <summary>
    /// Sets one patch's flag, on its own: a patch class a game update left unloadable throws here (its static constructor),
    /// and must not keep the next flags of the same option from being set (bdopt off, a fault would leave them on).
    /// </summary>
    private static void Flag(Action set)
    {
        try
        {
            set();
        }
        catch (Exception e)
        {
            MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.Id}] a switch not set: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Raised after a choice changed through bdopt (not from the menu or when loading), so the menu saves it.</summary>
    public static event Action<string, bool>? Changed;

    public static Option? Find(string key) => Options.FirstOrDefault(o => o.Key == key);

    /// <summary>
    /// A co-op run is in progress here. Turning the whole mod off then also turns off the fast combat start this player's
    /// co-op partners keep: this player starts each combat much later (tens of seconds with many relics) and the game drops
    /// the actions reaching a player still starting a combat (desync or a stuck turn, DESIGN "Co-op with a player without
    /// the mod"). bdopt asks for a confirmation then, and the menu's master switch can't be unticked.
    /// </summary>
    public static bool InCoopRun
    {
        get
        {
            try
            {
                return MegaCrit.Sts2.Core.Runs.RunManager.Instance is { IsInProgress: true, IsSingleplayerOrFakeMultiplayer: false };
            }
            catch
            {
                return false; // Can't tell (game starting or closing): no co-op run to protect.
            }
        }
    }

    /// <summary>
    /// In a co-op run, or in a co-op lobby: the players there were let in with the same state as this player, all on or all off
    /// (<see cref="Patches.CoopModeTag"/> compares it at the join only), so switching the whole mod now, either way, makes this
    /// player's combats start at another pace than theirs. bdopt asks for a confirmation then (bdopt off yes, bdopt on yes).
    /// The menu needs no lobby check: BaseLib's mod configuration can't be opened from a lobby.
    /// </summary>
    public static bool InCoopGame => InCoopRun || InCoopLobby();

    /// <summary>
    /// A lobby screen in the scene tree holds a connected multiplayer lobby (host or client). The game keeps no global
    /// reference to it: every screen holding one has a <c>StartRunLobby</c> field (character select, custom, daily, and later
    /// versions' other modes) or a <c>LoadRunLobby</c> one (continuing a co-op save: the run only exists once everyone is
    /// ready), found by type. Only run when bdopt switches the whole mod: a walk of the tree costs nothing then.
    /// </summary>
    private static bool InCoopLobby()
    {
        try
        {
            return Godot.Engine.GetMainLoop() is Godot.SceneTree { Root: { } root } && HoldsCoopLobby(root);
        }
        catch
        {
            return false; // Can't tell: the run check above still protects the run itself.
        }
    }

    private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> _lobbyFields = new();

    private static bool HoldsCoopLobby(Godot.Node node)
    {
        Type type = node.GetType();
        if (!_lobbyFields.TryGetValue(type, out System.Reflection.FieldInfo[]? fields))
        {
            fields = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby)
                    || f.FieldType == typeof(MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby)).ToArray();
            _lobbyFields[type] = fields;
        }
        foreach (System.Reflection.FieldInfo field in fields)
        {
            MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService? net = field.GetValue(node) switch
            {
                MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby start => start.NetService,
                MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby load => load.NetService,
                _ => null,
            };
            if (net is { IsConnected: true }
                && net.Type is MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host or MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
            {
                return true;
            }
        }
        foreach (Godot.Node child in node.GetChildren())
        {
            if (HoldsCoopLobby(child))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>With the mod off, co-op games only with players who have it off too (<see cref="Patches.CoopModeTag"/>).</summary>
    public const string CoopOffOnly =
        "Co-op: with the mod off you can only play with players who also have it off (the game reports a mod mismatch otherwise).";

    public const string CoopOffWarning =
        "WARNING: this is a co-op game. With the mod off, your combats start at the unmodified game's pace while the other "
        + "players' start fast: with many relics you start each combat far later than them, and the game can then drop their "
        + "actions (desync or a stuck turn).";

    public const string CoopOnWarning =
        "WARNING: this is a co-op game, and every player must have the mod in the same state: you joined with it off, as the "
        + "others did. Turned on by you alone, your combats start fast while theirs start at the unmodified game's pace, and the "
        + "game can then drop actions (desync or a stuck turn). Turn it on only if every player does.";

    /// <summary>After a switch of the whole mod in a co-op game: what it costs a player who disconnects (the game's join check).</summary>
    public const string CoopRejoinNote =
        "A player who disconnects can only rejoin once every player has the same state again (the game reports a mod mismatch otherwise).";

    /// <summary><c>bdopt</c> in a co-op game with the mod off: neutral, the others may have it off too (an all-off game).</summary>
    public const string CoopOffStatus =
        "Co-op game: every player must have the mod in the same state (all on or all off). Turn it on (bdopt on yes) only if every other player has it on.";

    /// <summary>The master switch; <paramref name="sessionOnly"/> (test tooling) leaves <see cref="MasterChoice"/>, what is saved, as it is.</summary>
    public static void SetMaster(bool on, bool notify = true, bool sessionOnly = false)
    {
        bool changed = Master != on;
        Master = on;
        if (!sessionOnly)
        {
            MasterChoice = on;
        }
        // The co-op join lists already handed out (v0.111+ keeps one from when hosting or joining started) say the new state.
        CoopModeTag.Refresh();
        ApplyAll();
        if (changed && notify)
        {
            Changed?.Invoke(MasterKey, on);
        }
    }

    public static void SetOption(string key, bool on, bool notify = true)
    {
        Option? option = Find(key);
        if (option == null)
        {
            return;
        }
        bool changed = option.Wanted != on;
        option.Wanted = on;
        ApplyAll();
        if (changed && notify)
        {
            Changed?.Invoke(key, on);
        }
    }

    /// <summary>
    /// Pushes the effective state to every patch flag. Never turns a faulted patch back on. Each option on its own: one whose
    /// setter throws (a patch class a game update left unloadable) must not leave the others' flags as they were (bdopt off
    /// would leave them on, a fault's flag would stay on).
    /// </summary>
    public static void ApplyAll()
    {
        foreach (Option option in Options)
        {
            try
            {
                option.SetEnabled(option.Effective);
            }
            catch (Exception e)
            {
                MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.Id}] {option.Key}: switch not set: {e.Message}");
            }
            try
            {
                option.SetParts?.Invoke(option.PartsEffective);
            }
            catch (Exception e)
            {
                MegaCrit.Sts2.Core.Logging.Log.Warn($"[{ModEntry.Id}] {option.Key}: parts' switches not set: {e.Message}");
            }
        }
    }

    /// <summary>One line per optimization, for bdopt.</summary>
    public static string Status()
    {
        if (ModEntry.StartedOff)
        {
            return $"BigDeckOptimization: inactive this session (turned off when the game started, nothing applied: unmodified game). "
                + (Master ? "Turned on: takes effect when you restart the game." : "To use it: bdopt on, then restart the game.")
                + " " + CoopOffOnly;
        }
        StringBuilder sb = new($"BigDeckOptimization: {(Master ? "on" : "OFF (vanilla)")}\n");
        if (!Master && InCoopGame)
        {
            sb.Append(CoopOffStatus).Append('\n');
        }
        else if (!Master)
        {
            sb.Append(CoopOffOnly).Append('\n');
        }
        foreach (Option option in Options)
        {
            string state = option.Unavailable() is { } why ? why
                : option.Fault is { } fault ? $"OFF, disabled after an error ({fault}), see log" + (option.PartsOutliveFault && Master && option.Wanted ? ", its other parts still on" : "")
                : !option.Wanted ? (option.Key == "fast_combat_start" && Master ? "OFF in solo (setting), always on in co-op" : "OFF (setting)")
                : Master ? "on" : "off";
            if (ConflictingMods(option).Concat(PatchSafety.HasUnnamedNotes(PatchNames(option)) ? new[] { OtherModsText } : Array.Empty<string>()).ToList() is { Count: > 0 } mods
                && option.Unavailable() == null && option.Fault == null)
            {
                // Patches, or (notes) another mod's types or data.
                state += $" (partly vanilla because of {string.Join(", ", mods)})";
            }
            if (option.Unavailable() == null && option.Fault == null && option.PartsOff().ToList() is { Count: > 0 } off)
            {
                state += $" (partly off: {string.Join(", ", off)})";
            }
            sb.Append($"  {option.Label} [{option.Key}]: {state}\n");
            if (option.Key == "fast_combat_start")
            {
                sb.Append($"  {FastStartRelicsLabel} [{FastStartRelicsKey}]: {FastStartRelics} (solo; co-op: always)\n");
            }
        }
        if (Master && PatchSafety.BackOffs().Any(b => Running(b.Patch)))
        {
            sb.Append("Other mods keep part of the optimizations on the normal game: bdopt why lists what and why.\n");
        }
        return sb.ToString().TrimEnd();
    }
}
