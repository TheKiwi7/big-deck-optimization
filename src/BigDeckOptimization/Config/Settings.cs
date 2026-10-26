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
    public sealed class Option(string key, string label, string description, string[] faults, Action<bool> setEnabled,
        Func<string?>? unavailable = null, bool defaultOn = true)
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
    }

    public const string MasterKey = "enabled";

    /// <summary><see cref="Option.Unavailable"/> answers (bdopt shows them as is, the menu explains them).</summary>
    public const string GameChanged = "not applied (game code changed)";
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
        null when option.Fault != null => Translations.Get(language, "why.fault",
            "Turned off for this session after an error (normal game instead, details in the log). Back on next time you start the game."),
        null => null,
    };

    public const string StartedOffText =
        "Inactive: the mod was turned off when the game started, so none of it is loaded. Turn the mod on, then restart the game.";

    public static string StartedOffWhy(string? language) => Translations.Get(language, "why.started_off", StartedOffText);

    /// <summary>
    /// The mods whose patches currently keep part of this optimization vanilla (it backs off from methods another mod
    /// patches); empty when none. Known once the optimization has checked those methods (when it first runs).
    /// </summary>
    public static IReadOnlyList<string> ConflictingMods(Option option) => PatchSafety.ConflictingMods(option.Faults);

    /// <summary>The menu's sentence about <see cref="ConflictingMods"/>, or null when there is none.</summary>
    public static string? Conflict(Option option, string? language)
    {
        IReadOnlyList<string> mods = ConflictingMods(option);
        return mods.Count == 0 ? null
            : Translations.Get(language, "conflict",
                "Partly inactive: {mods} also changes what this optimization speeds up (normal game for that part).").Replace("{mods}", string.Join(", ", mods));
    }

    public static bool Master { get; private set; } = true;

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
        // Combat pace first (same order as the menu).
        new("instant_combats", "Instant combat animations",
            "Visual, off by default: every animation wait of every combat is skipped (attacks, hits, enemy turns), only for you. The rest of the game keeps its pace. Same results.",
            new[] { FastCombatStart.Name }, on => FastCombatStart.InstantCombats = on, defaultOn: false),
        new("fast_combat_start", "Fast combat start",
            "Visual: plays the combat-start relic effects without their animation waits (like the game's Instant speed), from the number of relics set below (0 = always, the default). Same results. Solo only: always on in co-op, so every player starts the combat at the same pace.",
            new[] { FastCombatStart.Name }, on => FastCombatStart.Enabled = on),
        new("coop_checksum", "Co-op state check",
            "Faster state check after every action in co-op (the biggest co-op lag with huge decks).",
            new[] { ListenerCache.Name, ListenerCache.SnapshotFault }, on => ListenerCache.SnapshotEnabled = on),
        new("combat_refresh", "Combat refresh",
            "Faster card and UI refresh after every action in combat.",
            new[] { ListenerCache.Name }, on => ListenerCache.RefreshEnabled = on),
        new("hook_filter", "Skip empty hooks",
            "Inside the two optimizations above, only asks the cards and relics that actually react to each check.",
            new[] { HookFilter.Name }, on => HookFilter.Enabled = on),
        new("relic_input", "Relic bar input",
            "Stops hundreds of idle relic buttons from processing every mouse move.",
            new[] { IdleButtonInput.Name }, on => IdleButtonInput.Enabled = on),
        new("relic_flashes", "Merge repeated relic flashes",
            "Visual: a relic flashing several times in the same frame (Bing Bong copying each cloned card...) shows one flash instead of thousands stacked on the same spot, which could black out the screen or crash the game. Same results.",
            new[] { RelicFlashMerge.Name }, on => RelicFlashMerge.Enabled = on),
        new("grid_sort", "Card grid sorting",
            "Faster sorting of the deck view and other card grids.",
            new[] { GridSortCache.Name }, on => GridSortCache.Enabled = on),
        new("grid_pool", "Card grid recycling",
            "Faster recycling of card nodes when a card grid opens or is sorted.",
            new[] { PoolSignalScan.Name }, on => PoolSignalScan.Enabled = on,
            () => PoolSignalScan.Applied ? null : GameChanged),
        new("pile_lookup", "Card pile lookup",
            "Finds which pile a card is in without scanning every card (used by costs, keywords, playability...), and checks a card added to a pile (draws, reshuffles, clones) without scanning that pile.",
            new[] { PileIndex.Name }, on => PileIndex.Enabled = on,
            () => PileIndex.Applied ? null : GameChanged),
        new("card_ids", "Combat card IDs",
            "Skips re-scanning whole card piles for new cards on every card move (reshuffles, end of combat).",
            new[] { CardDbScan.Name }, on => CardDbScan.Enabled = on,
            () => CardDbScan.Applied ? null : GameChanged),
        new("pile_counters", "Pile counters at combat end",
            "Updates the draw/discard/exhaust counters once when piles are emptied, instead of once per card.",
            new[] { PileCounterBatch.Name }, on => PileCounterBatch.Enabled = on,
            () => PileCounterBatch.Applied ? null : GameChanged),
        new("listeners", "Effect listener lists",
            "Builds the list of cards, relics and powers that each triggered effect asks with less copying and memory churn, and keeps it between effects while nothing changed (cloning thousands of cards: seconds instead of minutes). Same list, same order.",
            new[] { FastListeners.Name }, on => FastListeners.Enabled = on,
            () => FastListeners.Applied ? null : GameChanged),
        new("hook_skip", "Skip non-reacting cards",
            "Each time the game processes an effect (card played, damage, turn end...), only asks the cards and enchantments whose type reacts to it (same results).",
            new[] { HookSkip.Name }, on => HookSkip.Enabled = on,
            () => HookSkip.Applied ? null : GameChanged),
        new("hover_tips", "Creature hover tips",
            "While the mouse is on a creature, rebuilds its tooltips after an action only when their content changed (they stay live).",
            new[] { HoverTipReuse.Name }, on => HoverTipReuse.Enabled = on,
            () => HoverTipReuse.Applied ? null : GameChanged),
        new("limitless_tabs", "Limitless enchantment tabs",
            "With Limitless: reuses enchantment tabs instead of rebuilding them on every card render.",
            new[] { EnchantmentTabReuse.Name }, on => EnchantmentTabReuse.Enabled = on,
            () => EnchantmentTabReuse.Applied ? null : LimitlessReason()),
        new("limitless_saves", "Limitless enchantment copies",
            "With Limitless: keeps the saved copy of stacked enchantments when rebuilding it would give the same content, instead of rebuilding it after every action.",
            new[] { LimitlessReserialize.Name }, on => LimitlessReserialize.Enabled = on,
            () => LimitlessReserialize.Applied ? null : LimitlessReason()),
        new("card_type_line", "BaseLib card type line",
            "With BaseLib: shows each card's type line without searching every card in combat for mods that change it, while no installed mod does (scrolling the draw pile, refreshing the hand). Same text.",
            new[] { TypePlaqueSkip.Name }, on => TypePlaqueSkip.Enabled = on,
            () => TypePlaqueSkip.Applied ? null : BaseLibMissing),
    };

    /// <summary>Raised after a choice changed through bdopt (not from the menu or when loading), so the menu saves it.</summary>
    public static event Action<string, bool>? Changed;

    public static Option? Find(string key) => Options.FirstOrDefault(o => o.Key == key);

    public static void SetMaster(bool on, bool notify = true)
    {
        bool changed = Master != on;
        Master = on;
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

    /// <summary>Pushes the effective state to every patch flag. Never turns a faulted patch back on.</summary>
    public static void ApplyAll()
    {
        foreach (Option option in Options)
        {
            option.SetEnabled(option.Effective);
        }
    }

    /// <summary>One line per optimization, for bdopt.</summary>
    public static string Status()
    {
        if (ModEntry.StartedOff)
        {
            return $"BigDeckOptimization: inactive this session (turned off when the game started, nothing applied: unmodified game). "
                + (Master ? "Turned on: takes effect when you restart the game." : "To use it: bdopt on, then restart the game.");
        }
        StringBuilder sb = new($"BigDeckOptimization: {(Master ? "on" : "OFF (vanilla)")}\n");
        foreach (Option option in Options)
        {
            string state = option.Unavailable() is { } why ? why
                : option.Fault is { } fault ? $"OFF, disabled after an error ({fault}), see log"
                : !option.Wanted ? (option.Key == "fast_combat_start" && Master ? "OFF in solo (setting), always on in co-op" : "OFF (setting)")
                : Master ? "on" : "off";
            if (ConflictingMods(option) is { Count: > 0 } mods && option.Unavailable() == null && option.Fault == null)
            {
                state += $" (partly vanilla: also patched by {string.Join(", ", mods)})";
            }
            sb.Append($"  {option.Label} [{option.Key}]: {state}\n");
            if (option.Key == "fast_combat_start")
            {
                sb.Append($"  {FastStartRelicsLabel} [{FastStartRelicsKey}]: {FastStartRelics} (solo; co-op: always)\n");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
