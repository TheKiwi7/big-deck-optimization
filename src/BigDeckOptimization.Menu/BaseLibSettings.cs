using System;
using System.Collections.Generic;
using System.Reflection;
using BaseLib.Config;
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

    [ConfigSection("Optimizations")]
    public static bool CoopChecksum { get => Get(nameof(CoopChecksum)); set => Apply(nameof(CoopChecksum), value); }
    public static bool CombatRefresh { get => Get(nameof(CombatRefresh)); set => Apply(nameof(CombatRefresh), value); }
    public static bool HookFilter { get => Get(nameof(HookFilter)); set => Apply(nameof(HookFilter), value); }
    public static bool PileLookup { get => Get(nameof(PileLookup)); set => Apply(nameof(PileLookup), value); }
    public static bool CardIds { get => Get(nameof(CardIds)); set => Apply(nameof(CardIds), value); }
    public static bool PileCounters { get => Get(nameof(PileCounters)); set => Apply(nameof(PileCounters), value); }
    public static bool GridSort { get => Get(nameof(GridSort)); set => Apply(nameof(GridSort), value); }
    public static bool GridPool { get => Get(nameof(GridPool)); set => Apply(nameof(GridPool), value); }
    public static bool RelicInput { get => Get(nameof(RelicInput)); set => Apply(nameof(RelicInput), value); }
    public static bool LimitlessTabs { get => Get(nameof(LimitlessTabs)); set => Apply(nameof(LimitlessTabs), value); }
    public static bool FastCombatStart { get => Get(nameof(FastCombatStart)); set => Apply(nameof(FastCombatStart), value); }

    private static string Key(string property) => StringHelper.Slugify(property).ToLowerInvariant();

    private static bool Get(string property) => Settings.Find(Key(property))?.Wanted ?? true;

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

    /// <summary>The menu texts, in the game's settings_ui table (re-added when the language changes).</summary>
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

        private static void Add()
        {
            try
            {
                LocTable? table = LocManager.Instance?.GetTable("settings_ui");
                if (table == null)
                {
                    return;
                }
                bool french = LocManager.Instance!.Language.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
                var texts = new Dictionary<string, string>
                {
                    [$"{Prefix}.mod_title"] = "Big Deck Optimization",
                    [$"{Prefix}-GENERAL.title"] = french ? "Général" : "General",
                    [$"{Prefix}-OPTIMIZATIONS.title"] = french ? "Optimisations" : "Optimizations",
                    [$"{Prefix}-ENABLED.title"] = french ? "Optimisations activées" : "Optimizations enabled",
                    [$"{Prefix}-ENABLED.hover.desc"] = french
                        ? "Active ou désactive tout le mod (désactivé = jeu non modifié), sans redémarrer."
                        : "Turns the whole mod on or off (off = unmodified game), no restart needed.",
                };
                foreach (Settings.Option option in Settings.Options)
                {
                    string slug = option.Key.ToUpperInvariant();
                    texts[$"{Prefix}-{slug}.title"] = french ? option.LabelFr : option.Label;
                    texts[$"{Prefix}-{slug}.hover.desc"] = french ? option.DescriptionFr : option.Description;
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
