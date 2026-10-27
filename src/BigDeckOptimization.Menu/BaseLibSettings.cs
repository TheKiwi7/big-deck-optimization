using System;
using System.Collections.Generic;
using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Config;

/// <summary>
/// The settings in BaseLib's mod configuration menu (registered by <see cref="SettingsMenu"/> once BaseLib is loaded): one switch per <see cref="Settings"/> entry, saved by BaseLib in
/// mod_configs/BigDeckOptimization.cfg. Property names give the option keys (CoopChecksum = coop_checksum).
/// Labels and hover texts are added to the game's settings_ui table at runtime (this mod has no resource pack).
/// </summary>
[ConfigHoverTipsByDefault]
public sealed class BaseLibSettings : SimpleModConfig
{
    public const string Name = "BaseLibSettings";

    private static BaseLibSettings? _instance;
    private static bool _loading;

    [ConfigSection("General")]
    public static bool Enabled { get => Settings.Master; set => Apply(nameof(Enabled), value); }

    // Combat pace first: the opt-in instant combats, then the fast combat start and its relic threshold.
    [ConfigSection("Optimizations")]
    public static bool InstantCombats { get => Get(nameof(InstantCombats)); set => Apply(nameof(InstantCombats), value); }
    public static bool FastCombatStart { get => Get(nameof(FastCombatStart)); set => Apply(nameof(FastCombatStart), value); }
    [ConfigSlider(0, 100, Settings.FastStartRelicsStep)]
    public static int FastCombatStartRelics { get => Settings.FastStartRelics; set => ApplyRelics(value); }
    public static bool CoopChecksum { get => Get(nameof(CoopChecksum)); set => Apply(nameof(CoopChecksum), value); }
    public static bool CombatRefresh { get => Get(nameof(CombatRefresh)); set => Apply(nameof(CombatRefresh), value); }
    public static bool HookFilter { get => Get(nameof(HookFilter)); set => Apply(nameof(HookFilter), value); }
    public static bool PileLookup { get => Get(nameof(PileLookup)); set => Apply(nameof(PileLookup), value); }
    public static bool CardIds { get => Get(nameof(CardIds)); set => Apply(nameof(CardIds), value); }
    public static bool PileCounters { get => Get(nameof(PileCounters)); set => Apply(nameof(PileCounters), value); }
    public static bool GridSort { get => Get(nameof(GridSort)); set => Apply(nameof(GridSort), value); }
    public static bool GridPool { get => Get(nameof(GridPool)); set => Apply(nameof(GridPool), value); }
    public static bool RelicInput { get => Get(nameof(RelicInput)); set => Apply(nameof(RelicInput), value); }
    public static bool RelicFlashes { get => Get(nameof(RelicFlashes)); set => Apply(nameof(RelicFlashes), value); }
    public static bool Listeners { get => Get(nameof(Listeners)); set => Apply(nameof(Listeners), value); }
    public static bool HookSkip { get => Get(nameof(HookSkip)); set => Apply(nameof(HookSkip), value); }
    public static bool HoverTips { get => Get(nameof(HoverTips)); set => Apply(nameof(HoverTips), value); }
    // Optimizations of other mods' code (Limitless, BaseLib), at the bottom.
    [ConfigIgnoreRestoreDefaults]
    public static bool LimitlessTabs { get => Shown(nameof(LimitlessTabs)); set => ApplyShown(nameof(LimitlessTabs), value); }
    [ConfigIgnoreRestoreDefaults]
    public static bool LimitlessSaves { get => Shown(nameof(LimitlessSaves)); set => ApplyShown(nameof(LimitlessSaves), value); }
    public static bool CardTypeLine { get => Get(nameof(CardTypeLine)); set => Apply(nameof(CardTypeLine), value); }

    // Limitless-only optimizations: their box is ticked only while Limitless is there (patch applied) and the option is
    // wanted. The wanted state is saved apart, hidden: a box shown unticked without Limitless comes back ticked (and on)
    // once Limitless is installed, unless the player turned it off.
    [ConfigHideInUI]
    public static bool LimitlessTabsWanted { get => Get(nameof(LimitlessTabs)); set => Apply(nameof(LimitlessTabs), value); }
    [ConfigHideInUI]
    public static bool LimitlessSavesWanted { get => Get(nameof(LimitlessSaves)); set => Apply(nameof(LimitlessSaves), value); }

    private static string Key(string property) => StringHelper.Slugify(property).ToLowerInvariant();

    private static bool Shown(string property) => Settings.Find(Key(property)) is { } option && option.Unavailable() == null && option.Wanted;

    /// <summary>
    /// A shown box changed from the menu (Limitless there). Ignored when loading: the saved box state isn't the
    /// preference (unticked while Limitless was absent), the hidden ...Wanted value is.
    /// </summary>
    private static void ApplyShown(string property, bool on)
    {
        if (_loading || Settings.Find(Key(property))?.Unavailable() != null)
        {
            return;
        }
        Apply(property, on);
    }

    private static bool Get(string property) => Settings.Find(Key(property))?.Wanted ?? true;

    /// <summary>The fast combat start's relic threshold, from the menu's slider or the saved file.</summary>
    private static void ApplyRelics(int relics)
    {
        Settings.SetFastStartRelics(relics, notify: false);
        if (!_loading)
        {
            _instance?.SaveDebounced();
        }
    }

    /// <summary>From the menu or BaseLib loading the saved file: applied without echoing back to BaseLib.</summary>
    private static void Apply(string property, bool on)
    {
        if (property == nameof(Enabled))
        {
            Settings.SetMaster(on, notify: false);
        }
        else
        {
            Settings.SetOption(Key(property), on, notify: false);
        }
        if (!_loading)
        {
            _instance?.SaveDebounced();
        }
    }

    /// <summary>
    /// Built each time the page opens: an optimization that can't run here (optional mod absent, game or mod code
    /// changed, turned off after an error) is greyed out and can't be clicked; its hover text says why.
    /// </summary>
    public override void SetupConfigUI(Control optionContainer)
    {
        try
        {
            SettingsText.Add(); // Current reasons in the hover texts.
        }
        catch (Exception e)
        {
            // Runs inside BaseLib's settings screen: never break it (the texts then keep their previous values).
            Log.Warn($"[{ModEntry.Id}] {Name}: could not update the menu texts: {e.Message}");
        }
        base.SetupConfigUI(optionContainer);
        try
        {
            GreyOut(optionContainer);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: could not grey out the unavailable options: {e.Message}");
        }
        try
        {
            AddCoopNote(optionContainer);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: could not add the co-op note: {e.Message}");
        }
    }

    public const string CoopNote =
        "Co-op: every player needs this mod, same version. The fast combat start is always on there, so everyone starts each combat at the same pace; the other switches can be changed freely (same results either way, no desync).";

    /// <summary>A short note above the options, in the game's language: what co-op requires and forces.</summary>
    private static void AddCoopNote(Control optionContainer)
    {
        string text = Translations.Get(LocManager.Instance?.Language, "coop_note", CoopNote);
        var label = ModConfig.CreateRawLabelControl(text, 24);
        label.Name = "CoopNote";
        label.FitContent = true;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.Modulate = new Color(1f, 1f, 1f, 0.8f);
        var margin = new MarginContainer { Name = "CoopNoteMargin" };
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        margin.AddChild(label);
        optionContainer.AddChild(margin);
        optionContainer.MoveChild(margin, 0);
    }

    private static void GreyOut(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is NConfigOptionRow row)
            {
                if (Settings.Find(Key(row.Name)) is { } option && Settings.Why(option, language: null) != null)
                {
                    row.Modulate = new Color(1f, 1f, 1f, 0.45f);
                    if (row.SettingControl is NConfigTickbox tickbox)
                    {
                        tickbox.Disable();
                    }
                }
                continue;
            }
            GreyOut(child);
        }
    }

    public static void Register()
    {
        SettingsText.Install();
        _loading = true;
        try
        {
            _instance = new BaseLibSettings(); // Loads the saved values through the setters.
        }
        finally
        {
            _loading = false;
        }
        ModConfigRegistry.Register(ModEntry.Id, _instance);
        // bdopt changes: show them in the menu and save them.
        Settings.Changed += (_, _) => _instance?.SaveDebounced();
        Log.Info($"[{ModEntry.Id}] {Name}: registered in BaseLib's mod configuration. {Settings.Status().Replace("\n", " |")}");
    }

    /// <summary>
    /// The menu texts, in the game's settings_ui table (re-added when the language changes): the game's language when
    /// this mod has a translation for it (<see cref="Translations"/>), English otherwise.
    /// </summary>
    private static class SettingsText
    {
        private const string Prefix = "BIGDECKOPTIMIZATION";

        /// <summary>Mods initialize before the game's localization exists: retried on the next frames until it does.</summary>
        public static void Install()
        {
            if (LocManager.Instance == null)
            {
                Godot.Callable.From(Install).CallDeferred();
                return;
            }
            Add();
            LocManager.Instance.SubscribeToLocaleChange(Add);
        }

        private static string? _loggedLanguage;

        public static void Add()
        {
            try
            {
                LocTable? table = LocManager.Instance?.GetTable("settings_ui");
                if (table == null)
                {
                    return;
                }
                string language = LocManager.Instance!.Language;
                if (language != _loggedLanguage)
                {
                    _loggedLanguage = language;
                    Log.Info($"[{ModEntry.Id}] {Name}: menu texts for game language '{language}': {(Translations.Has(language) ? "translated" : "English")}");
                }
                var texts = new Dictionary<string, string>
                {
                    [$"{Prefix}.mod_title"] = "Big Deck Optimization",
                    [$"{Prefix}-GENERAL.title"] = Translations.Get(language, "general", "General"),
                    [$"{Prefix}-OPTIMIZATIONS.title"] = Translations.Get(language, "optimizations", "Optimizations"),
                    [$"{Prefix}-ENABLED.title"] = Translations.Get(language, "enabled.title", "Optimizations enabled"),
                    [$"{Prefix}-ENABLED.hover.desc"] = Translations.Get(language, "enabled.desc",
                        "Turns the whole mod on or off (off = unmodified game), no restart needed. If it's off when the game starts, none of the mod is loaded: turning it back on then takes effect at the next start."),
                    [$"{Prefix}-{Settings.FastStartRelicsKey.ToUpperInvariant()}.title"] =
                        Translations.Get(language, Settings.FastStartRelicsKey + ".title", Settings.FastStartRelicsLabel),
                    [$"{Prefix}-{Settings.FastStartRelicsKey.ToUpperInvariant()}.hover.desc"] =
                        Translations.Get(language, Settings.FastStartRelicsKey + ".desc", Settings.FastStartRelicsDescription),
                };
                foreach (Settings.Option option in Settings.Options)
                {
                    string slug = option.Key.ToUpperInvariant();
                    texts[$"{Prefix}-{slug}.title"] = Translations.Get(language, option.Key + ".title", option.Label);
                    string description = Translations.Get(language, option.Key + ".desc", option.Description);
                    // Greyed out (Why), or still switchable but partly vanilla because of another mod (Conflict).
                    texts[$"{Prefix}-{slug}.hover.desc"] = (Settings.Why(option, language) ?? Settings.Conflict(option, language)) is { } why
                        ? $"[color=#e0a040]{why}[/color]\n{description}"
                        : description;
                }
                table.MergeWith(texts);
            }
            catch (Exception e)
            {
                Log.Warn($"[{ModEntry.Id}] {Name}: could not add the menu texts: {e.Message}");
            }
        }
    }
}
