using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BigDeckOptimization.Config;

/// <summary>
/// The settings menu comes from BaseLib, an optional mod. It lives in BigDeckOptimization.Menu.dll (BaseLibSettings
/// derives from a BaseLib class), loaded here once BaseLib has loaded: the game reads every type of this mod's DLL when
/// loading it, so none of them may depend on BaseLib. Mods load in the player's order, so BaseLib may come after this
/// one; without it (absent, disabled or failed) the optimizations all run and bdopt still changes them, for the session.
/// </summary>
public static class SettingsMenu
{
    public const string Name = "SettingsMenu";
    private const string BaseLibId = "BaseLib";
    private const string MenuAssembly = "BigDeckOptimization.Menu";
    private const string MenuType = "BigDeckOptimization.Config.BaseLibSettings";

    public static void Init()
    {
        if (ModManager.Mods.Any(IsLoadedBaseLib))
        {
            Register();
            return;
        }
        ModManager.OnModDetected += OnModDetected;
        Log.Info($"[{ModEntry.Id}] {Name}: BaseLib not loaded (yet): the settings menu appears if it loads; bdopt works either way.");
    }

    private static bool IsLoadedBaseLib(Mod mod) => mod.manifest?.id == BaseLibId && mod.state == ModLoadState.Loaded;

    /// <summary>Raised for every mod as it finishes loading: BaseLib's initializer has run when it is Loaded.</summary>
    private static void OnModDetected(Mod mod)
    {
        if (IsLoadedBaseLib(mod))
        {
            ModManager.OnModDetected -= OnModDetected;
            Register();
        }
    }

    private static void Register()
    {
        try
        {
            Assembly main = typeof(SettingsMenu).Assembly;
            string path = Path.Combine(Path.GetDirectoryName(main.Location)!, MenuAssembly + ".dll");
            // Same load context as this mod and BaseLib, so the menu DLL resolves both.
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(main) ?? AssemblyLoadContext.Default;
            Assembly menu = context.LoadFromAssemblyPath(path);
            MethodInfo register = menu.GetType(MenuType, throwOnError: true)!.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(MenuType + ".Register");
            register.Invoke(null, null);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: settings menu unavailable (bdopt still works): {e}");
        }
    }
}
