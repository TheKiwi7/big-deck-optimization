using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Config;

/// <summary>
/// Optional ModConfig integration, by reflection only (no reference, so the mod loads without it): registers the
/// master switch and one toggle per optimization in ModConfig's settings tab, applies the saved values, and mirrors
/// bdopt changes back so the menu stays in sync. ModConfig saves the values itself (user://ModConfig/).
/// </summary>
public static class ModConfigBridge
{
    public const string Name = "ModConfig";
    private const string AssemblyName = "ModConfig";
    private const string DisplayName = "Big Deck Optimization";

    private static Type? _api;
    private static Type? _entry;
    private static Type? _configType;
    private static bool _scheduled;

    public static bool Registered { get; private set; }

    public static void Init()
    {
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
        if (loaded != null)
        {
            Schedule(loaded);
            return;
        }
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        Log.Info($"[{ModEntry.Id}] {Name} not loaded (yet): settings are session-only (bdopt)");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == AssemblyName)
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            Schedule(args.LoadedAssembly);
        }
    }

    /// <summary>ModConfig initializes itself when its mod loads: register a couple of frames later, once it's ready.</summary>
    private static void Schedule(Assembly assembly)
    {
        if (_scheduled)
        {
            return;
        }
        _scheduled = true;
        try
        {
            _api = assembly.GetType("ModConfig.ModConfigApi");
            _entry = assembly.GetType("ModConfig.ConfigEntry");
            _configType = assembly.GetType("ModConfig.ConfigType");
            if (_api == null || _entry == null || _configType == null)
            {
                Log.Warn($"[{ModEntry.Id}] {Name} API not found (incompatible version?): settings are session-only (bdopt)");
                return;
            }
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                Register();
                return;
            }
            int frames = 0;
            void OnFrame()
            {
                if (++frames >= 2)
                {
                    tree.ProcessFrame -= OnFrame;
                    Register();
                }
            }
            tree.ProcessFrame += OnFrame;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name} integration failed, settings are session-only (bdopt): {e}");
        }
    }

    private static void Register()
    {
        try
        {
            List<object> entries = new()
            {
                Toggle(Settings.MasterKey, "Optimizations enabled", "Optimisations activées",
                    "Turns every optimization on or off (off = unmodified game). Same as the bdopt console command.",
                    "Active ou désactive toutes les optimisations (désactivé = jeu non modifié). Comme la commande console bdopt."),
                Entry("Header", "", new() { ["en"] = "Optimizations", ["fr"] = "Optimisations" }, null),
            };
            foreach (Settings.Option option in Settings.Options.Where(o => o.Unavailable() == null))
            {
                entries.Add(Toggle(option.Key, option.Label, option.LabelFr, option.Description, option.DescriptionFr));
            }
            Array array = Array.CreateInstance(_entry!, entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                array.SetValue(entries[i], i);
            }
            MethodInfo register = _api!.GetMethod("Register", new[] { typeof(string), typeof(string), array.GetType() })
                ?? throw new MissingMethodException("ModConfigApi.Register(string, string, ConfigEntry[])");
            register.Invoke(null, new object[] { ModEntry.Id, DisplayName, array });

            // ModConfig loads saved values on Register without calling OnChanged: read them back.
            MethodInfo getValue = _api.GetMethod("GetValue")?.MakeGenericMethod(typeof(bool))
                ?? throw new MissingMethodException("ModConfigApi.GetValue<T>");
            foreach (string key in new[] { Settings.MasterKey }.Concat(Settings.Options.Select(o => o.Key)))
            {
                bool on = (bool)getValue.Invoke(null, new object[] { ModEntry.Id, key })!;
                Apply(key, on);
            }
            Settings.Changed += PushToModConfig;
            Registered = true;
            Log.Info($"[{ModEntry.Id}] {Name} settings registered ({entries.Count} entries). {Settings.Status().Replace("\n", " |")}");
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name} integration failed, settings are session-only (bdopt): {e}");
        }
    }

    private static void Apply(string key, bool on)
    {
        if (key == Settings.MasterKey)
        {
            Settings.SetMaster(on, notify: false);
        }
        else
        {
            Settings.SetOption(key, on, notify: false);
        }
    }

    /// <summary>bdopt changed a setting: show it in the menu and save it.</summary>
    private static void PushToModConfig(string key, bool on)
    {
        try
        {
            _api!.GetMethod("SetValue")!.Invoke(null, new object[] { ModEntry.Id, key, on });
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: could not save '{key}': {e.Message}");
        }
    }

    private static object Toggle(string key, string label, string labelFr, string description, string descriptionFr)
    {
        object entry = Entry("Toggle", key, new() { ["en"] = label, ["fr"] = labelFr },
            new() { ["en"] = description, ["fr"] = descriptionFr });
        Set(entry, "DefaultValue", true);
        Set(entry, "OnChanged", new Action<object>(value =>
        {
            if (value is bool on)
            {
                Apply(key, on);
            }
        }));
        return entry;
    }

    private static object Entry(string type, string key, Dictionary<string, string> labels, Dictionary<string, string>? descriptions)
    {
        object entry = Activator.CreateInstance(_entry!)!;
        Set(entry, "Type", Enum.Parse(_configType!, type));
        Set(entry, "Key", key);
        Set(entry, "Label", labels["en"]);
        Set(entry, "Labels", labels);
        if (descriptions != null)
        {
            Set(entry, "Description", descriptions["en"]);
            Set(entry, "Descriptions", descriptions);
        }
        return entry;
    }

    /// <summary>Optional properties of newer/older ModConfig versions are skipped when absent.</summary>
    private static void Set(object entry, string property, object value)
    {
        entry.GetType().GetProperty(property)?.SetValue(entry, value);
    }
}
