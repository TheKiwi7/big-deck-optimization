using System;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The mods screen shows <c>res://&lt;mod id&gt;/mod_image.png</c>, which only exists inside a Godot resource pack. This
/// mod has none: when the screen shows this mod without an image, the image is loaded from <c>mod_image.png</c> next to
/// the DLL instead. UI only.
/// </summary>
public static class ModImage
{
    public const string Name = "ModImage";
    private const string FileName = "mod_image.png";

    private static AccessTools.FieldRef<NModInfoContainer, TextureRect>? _image;
    private static Texture2D? _texture;
    private static bool _failed;

    public static void Apply(Harmony harmony)
    {
        _image = AccessTools.FieldRefAccess<NModInfoContainer, TextureRect>("_image");
        harmony.Patch(AccessTools.Method(typeof(NModInfoContainer), nameof(NModInfoContainer.Fill)) ?? throw new MissingMethodException("NModInfoContainer.Fill"),
            postfix: new HarmonyMethod(typeof(ModImage), nameof(FillPostfix)));
    }

    private static void FillPostfix(NModInfoContainer __instance, Mod mod)
    {
        if (_failed || mod.manifest?.id != ModEntry.Id)
        {
            return;
        }
        try
        {
            TextureRect image = _image!(__instance);
            if (image.Texture != null)
            {
                return;
            }
            if (_texture == null)
            {
                string path = Path.Combine(mod.path, FileName);
                if (!File.Exists(path))
                {
                    return;
                }
                _texture = ImageTexture.CreateFromImage(Image.LoadFromFile(path));
            }
            image.Texture = _texture;
        }
        catch (Exception e)
        {
            _failed = true;
            Log.Warn($"[{ModEntry.Id}] {Name}: could not show the mod image: {e.Message}");
        }
    }
}
