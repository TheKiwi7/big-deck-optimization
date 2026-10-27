using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

namespace BigDeckOptimization.Patches;

/// <summary>
/// BaseLib (optional) adds a step to every <c>NCard.UpdateTypePlaque</c> (each card shown: grids, hand refresh after
/// every action): <c>AddSubtypesToTypePlaquePatch.TryModifyPlaqueText</c> walks every hook listener of the combat (all
/// cards of every pile, relics, powers) to collect the type-line texts of those implementing its
/// <c>ICardTypeTextModifier</c>. At ~32,000 cards that was ~87% of scrolling the draw pile (~6 ms per card shown).
///
/// With no loaded type implementing that interface the walk can't collect anything, and BaseLib then returns the
/// original text itself: this returns it without the walk. Cards implementing <c>ICustomTypeTextCard</c>, a loaded
/// modifier type (rechecked when an assembly loads), another mod patching the method or a changed method body
/// (fingerprint) leave BaseLib's code as is. UI text only: no game state involved.
/// </summary>
public static class TypePlaqueSkip
{
    public const string Name = "TypePlaqueSkip";

    private const string BaseLibAssembly = "BaseLib";
    private const string PatchType = "BaseLib.Patches.UI.AddSubtypesToTypePlaquePatch";
    private const string ModifierInterface = "BaseLib.Hooks.ICardTypeTextModifier";
    private const string CustomTextInterface = "BaseLib.Abstracts.ICustomTypeTextCard";

    /// <summary>BaseLib 3.4.7 <c>AddSubtypesToTypePlaquePatch.TryModifyPlaqueText</c>.</summary>
    private const string Fingerprint = "2D24047BA6035BAF";

    public static bool Enabled { get; set; } = true;

    public static bool Applied { get; private set; }

    /// <summary>Card renders answered without the walk, and calls left to BaseLib.</summary>
    public static long Skipped { get; private set; }

    public static long LeftToBaseLib { get; private set; }

    private static MethodInfo? _original;
    private static Type? _customText;

    public static void Apply(Harmony harmony)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == BaseLibAssembly))
        {
            ApplyNow(harmony);
            return;
        }
        // Mod load order isn't guaranteed: apply when BaseLib loads, if it ever does.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        throw new PatchSkippedException("BaseLib not loaded (applies if it loads later)");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == BaseLibAssembly)
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            ModEntry.TryApply(Name, ApplyNow);
        }
    }

    private static void ApplyNow(Harmony harmony)
    {
        Type patch = AccessTools.TypeByName(PatchType) ?? throw new PatchSkippedException("BaseLib has no " + PatchType);
        MethodInfo original = AccessTools.Method(patch, "TryModifyPlaqueText", new[] { typeof(LocString), typeof(CardModel) })
            ?? throw new PatchSkippedException("BaseLib has no TryModifyPlaqueText");
        PatchSafety.RequireFingerprint(original, Fingerprint);
        _customText = AccessTools.TypeByName(CustomTextInterface) ?? throw new PatchSkippedException("BaseLib has no " + CustomTextInterface);
        if (AccessTools.TypeByName(ModifierInterface) == null)
        {
            throw new PatchSkippedException("BaseLib has no " + ModifierInterface);
        }
        _original = original;
        PatchGuard.Patch(harmony, original, prefix: new HarmonyMethod(typeof(TypePlaqueSkip), nameof(Prefix)));
        Applied = true;
    }

    /// <summary>Whether BaseLib's walk can only find nothing for this card (its result is then the original text).</summary>
    public static bool CanSkip(CardModel card)
    {
        if (!Enabled || _customText!.IsInstanceOfType(card) || PatchSafety.IsPatchedByOthers(_original))
        {
            return false;
        }
        return BaseLibTypes.NoneDefined(ModifierInterface);
    }

    private static bool Prefix(LocString originalPlaqueText, CardModel card, ref LocString __result)
    {
        try
        {
            if (CanSkip(card))
            {
                Skipped++;
                __result = originalPlaqueText;
                return false;
            }
        }
        catch (Exception e)
        {
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
        }
        LeftToBaseLib++;
        return true;
    }

    /// <summary>BaseLib's method, for bdperf's comparison (runs this prefix too unless <see cref="Enabled"/> is off).</summary>
    public static LocString Invoke(LocString originalPlaqueText, CardModel card)
        => (LocString)_original!.Invoke(null, new object[] { originalPlaqueText, card })!;
}
