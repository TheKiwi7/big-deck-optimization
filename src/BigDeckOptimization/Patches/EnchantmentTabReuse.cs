using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.addons.mega_text;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Limitless (optional) replaces the card's enchantment tab with one tab per stacked enchantment, and on every
/// <c>NCard.UpdateVisuals</c> frees all tabs and builds new ones with <c>Node.Duplicate()</c>. Scrolling a card grid
/// re-renders a row of cards per frame, and those duplicates were ~half of the scroll cost (~10 ms per card).
///
/// This mirrors Limitless' <c>CardExtensions.UpdateEnchantmentVisuals</c> but reuses the tabs already on the card,
/// setting the same properties Limitless sets (everything else a fresh duplicate would copy from the template is never
/// changed on either). Only active when the mirrored method's fingerprint matches; Limitless absent = not applied.
/// UI only: no game state involved.
/// </summary>
public static class EnchantmentTabReuse
{
    public const string Name = "EnchantmentTabReuse";

    private const string LimitlessAssembly = "Limitless";
    private const string CardExtensionsType = "Limitless.LimitlessCode.UncappedEnchantments.CardPatches.CardExtensions";

    /// <summary>Limitless v1.1.7 to v1.2.1 (same code) <c>CardExtensions.UpdateEnchantmentVisuals</c>, the code mirrored below.</summary>
    private const string MirroredFingerprint = "045A3C7DEE0B15C2";

    /// <summary>
    /// Limitless v1.2.1 code the mirror calls and relies on: SetEnchantmentStatus sets the same properties in both of its
    /// branches (so a tab reused across a Disabled/Normal change ends up as a fresh duplicate would), TabStride gives the
    /// layout, EnsureAttached only attaches the host once.
    /// </summary>
    private const string SetEnchantmentStatusFingerprint = "3954DA23C0363FCE";
    private const string TabStrideFingerprint = "5EFCC271DBF5A1D9";
    private const string EnsureAttachedFingerprint = "CC9FE40565198C1E";

    public static bool Enabled { get; set; } = true;

    public static bool Applied { get; private set; }

    /// <summary>Cards rendered by reusing tabs, and calls left to Limitless.</summary>
    public static long Reused { get; private set; }

    public static long LeftToLimitless { get; private set; }

    private static MethodInfo? _original;
    private static Type? _multiEnchantment;
    private static Func<EnchantmentModel?, IReadOnlyList<EnchantmentModel>>? _innerEnchantments;
    private static Func<NCard, Control?>? _ensureAttached;
    private static Func<Control, Vector2>? _tabStride;
    private static Action<NCard, EnchantmentStatus, Control, TextureRect, MegaLabel>? _setEnchantmentStatus;
    private static Func<EnchantmentModel, bool>? _hideForAnim;
    private static AccessTools.FieldRef<NCard, Control>? _enchantmentTab;
    private static AccessTools.FieldRef<NCard, Vector2>? _defaultEnchantmentPosition;

    public static void Apply(Harmony harmony)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == LimitlessAssembly))
        {
            ApplyNow(harmony);
            return;
        }
        // Mod load order isn't guaranteed: apply when Limitless loads, if it ever does.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        throw new PatchSkippedException("Limitless not loaded (applies if it loads later)");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetName().Name == LimitlessAssembly)
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            ModEntry.TryApply(Name, ApplyNow);
        }
    }

    private static void ApplyNow(Harmony harmony)
    {
        Type extensions = AccessTools.TypeByName(CardExtensionsType)
            ?? throw new PatchSkippedException("Limitless has no " + CardExtensionsType);
        MethodInfo original = AccessTools.Method(extensions, "UpdateEnchantmentVisuals", new[] { typeof(NCard) });
        PatchSafety.RequireFingerprint(original, MirroredFingerprint);

        _multiEnchantment = AccessTools.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.MultiEnchantment");
        _innerEnchantments = AccessTools.Method(_multiEnchantment, "InnerEnchantments")
            .CreateDelegate<Func<EnchantmentModel?, IReadOnlyList<EnchantmentModel>>>();
        MethodInfo ensureAttached = AccessTools.Method("Limitless.LimitlessCode.UncappedEnchantments.UI.EnchantmentTabHost:EnsureAttached");
        MethodInfo tabStride = AccessTools.Method(extensions, "TabStride");
        MethodInfo setEnchantmentStatus = AccessTools.Method(extensions, "SetEnchantmentStatus");
        PatchSafety.RequireFingerprint(ensureAttached, EnsureAttachedFingerprint);
        PatchSafety.RequireFingerprint(tabStride, TabStrideFingerprint);
        PatchSafety.RequireFingerprint(setEnchantmentStatus, SetEnchantmentStatusFingerprint);
        _ensureAttached = ensureAttached.CreateDelegate<Func<NCard, Control?>>();
        _tabStride = tabStride.CreateDelegate<Func<Control, Vector2>>();
        _setEnchantmentStatus = setEnchantmentStatus.CreateDelegate<Action<NCard, EnchantmentStatus, Control, TextureRect, MegaLabel>>();
        object hideForAnim = AccessTools.Field(
            AccessTools.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.EnchantmentModelPatches.SpireFields_EnchantmentModel"),
            "HideForAnim").GetValue(null)!;
        _hideForAnim = hideForAnim.GetType().GetMethod("Get")!.CreateDelegate<Func<EnchantmentModel, bool>>(hideForAnim);
        _enchantmentTab = AccessTools.FieldRefAccess<NCard, Control>("_enchantmentTab");
        _defaultEnchantmentPosition = AccessTools.FieldRefAccess<NCard, Vector2>("_defaultEnchantmentPosition");

        _original = original;
        PatchGuard.Patch(harmony, original, prefix: new HarmonyMethod(typeof(EnchantmentTabReuse), nameof(Prefix)));
        Applied = true;
        PatchSafety.Watch(Name, original); // What TryRender asks about (display only).
    }

    /// <summary>Renders by reusing the card's tabs; returns true (run Limitless) for anything outside that case.</summary>
    private static bool Prefix(NCard nCard)
    {
        if (!Enabled)
        {
            return true;
        }
        try
        {
            if (TryRender(nCard))
            {
                Reused++;
                return false;
            }
        }
        catch (Exception e)
        {
            // Limitless then frees every tab and rebuilds them from scratch, so a half-done render leaves no trace.
            Enabled = false;
            PatchSafety.Fault(Name);
            Log.Error($"[{ModEntry.Id}] {Name} disabled after exception: {e}");
        }
        LeftToLimitless++;
        return true;
    }

    private static bool TryRender(NCard nCard)
    {
        CardModel? model = nCard.Model;
        // Limitless throws on a null model and logs bare (non-multi) enchantments once: let it do both.
        // No tolerance: mirrors the method with other node operations (UI).
        if (model == null || !nCard.IsInsideTree() || PatchSafety.IsPatchedByOthers(_original))
        {
            return false;
        }
        EnchantmentModel? enchantment = model.Enchantment;
        if (enchantment != null && !_multiEnchantment!.IsInstanceOfType(enchantment))
        {
            return false;
        }
        // Card in the tree: EnsureAttached only attaches the host the first time, so Limitless calling it again is fine.
        Control? host = _ensureAttached!(nCard);
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            return false;
        }
        int existing = host.GetChildCount();
        if (existing == 0)
        {
            return false; // Nothing to reuse.
        }
        for (int i = 0; i < existing; i++)
        {
            if (!IsTab(host.GetChild(i)))
            {
                return false;
            }
        }

        IReadOnlyList<EnchantmentModel> inner = _innerEnchantments!(enchantment);
        Control template = _enchantmentTab!(nCard);
        // The tabs are duplicates of it: a template of another shape (another mod, a Limitless update) is Limitless's to render.
        if (!GodotObject.IsInstanceValid(template) || !IsTab(template))
        {
            return false;
        }
        TextureRect templateIcon = (TextureRect)template.GetChild(0);
        MegaLabel templateLabel = (MegaLabel)template.GetChild(1);
        Vector2 size = _tabStride!(template);
        for (int i = existing - 1; i >= inner.Count; i--)
        {
            Node extra = host.GetChild(i);
            host.RemoveChild(extra);
            extra.QueueFree();
        }
        // A new tab is added to the host only once configured, in Limitless's order (MegaLabel's _Ready runs after its text
        // is set): until then it is ours to free if anything throws, before Limitless rebuilds the tabs.
        Node? pending = null;
        try
        {
            for (int i = 0; i < inner.Count; i++)
            {
                EnchantmentModel enchantmentModel = inner[i];
                bool isNew = i >= existing;
                if (isNew)
                {
                    pending = template.Duplicate();
                }
                TextureRect textureRect = isNew ? (TextureRect)pending! : (TextureRect)host.GetChild(i);
                TextureRect icon = (TextureRect)textureRect.GetChild(0);
                MegaLabel label = (MegaLabel)textureRect.GetChild(1);
                textureRect.Visible = true;
                bool flag = !_hideForAnim!(enchantmentModel);
                // Limitless reads the Modulate of a fresh duplicate, i.e. the template's.
                textureRect.SelfModulate = new Color(template.Modulate.R, template.Modulate.G, template.Modulate.B, flag ? 1f : 0f);
                icon.SelfModulate = new Color(templateIcon.Modulate.R, templateIcon.Modulate.G, templateIcon.Modulate.B, flag ? 1f : 0f);
                label.SelfModulate = new Color(templateLabel.Modulate.R, templateLabel.Modulate.G, templateLabel.Modulate.B, flag ? 1f : 0f);
                icon.Texture = enchantmentModel.Icon;
                label.SetTextAutoSize(enchantmentModel.DisplayAmount.ToString());
                label.Visible = enchantmentModel.ShowAmount;
                _setEnchantmentStatus!(nCard, enchantmentModel.Status, textureRect, icon, label);
                textureRect.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                textureRect.Position = new Vector2((float)(i / 3) * size.X, (float)(i % 3) * size.Y);
                textureRect.Size = size;
                if (isNew)
                {
                    host.AddChild(textureRect, forceReadableName: false, Node.InternalMode.Disabled);
                    pending = null;
                }
            }
        }
        catch
        {
            if (pending != null && GodotObject.IsInstanceValid(pending) && pending.GetParent() == null)
            {
                pending.Free();
            }
            throw;
        }
        Vector2 defaultPosition = _defaultEnchantmentPosition!(nCard);
        host.Position = model.HasStarCostX || model.CurrentStarCost >= 0 ? defaultPosition : defaultPosition + Vector2.Up * 45f;
        return true;
    }

#if BENCH
    /// <summary>Bench: every property of the card's enchantment tabs that rendering sets or a duplicate copies.</summary>
    internal static string DescribeTabs(NCard nCard)
    {
        Control? host = _ensureAttached?.Invoke(nCard);
        if (host == null)
        {
            return "no host";
        }
        System.Text.StringBuilder sb = new($"host {host.Position} children {host.GetChildCount()}");
        foreach (Node child in host.GetChildren())
        {
            if (child is not Control tab || !IsTab(child))
            {
                sb.Append($" | {child.GetType().Name}");
                continue;
            }
            TextureRect icon = (TextureRect)tab.GetChild(0);
            MegaLabel label = (MegaLabel)tab.GetChild(1);
            sb.Append($" | tab vis {tab.Visible} pos {tab.Position} size {tab.Size} anchors {tab.AnchorLeft},{tab.AnchorTop},{tab.AnchorRight},{tab.AnchorBottom}")
                .Append($" mod {tab.Modulate} self {tab.SelfModulate} scale {tab.Scale} rot {tab.Rotation}")
                .Append($" icon {icon.Texture?.ResourcePath} vis {icon.Visible} self {icon.SelfModulate} mod {icon.Modulate} parentMat {icon.UseParentMaterial}")
                .Append($" label '{label.Text}' vis {label.Visible} self {label.SelfModulate} mod {label.Modulate}")
                .Append($" font {(label.HasThemeFontSizeOverride("font_size") ? label.GetThemeFontSize("font_size") : -1)}");
        }
        return sb.ToString();
    }
#endif

    /// <summary>A tab Limitless built: a duplicate of the template (icon + amount label), not queued for deletion.</summary>
    private static bool IsTab(Node node) =>
        node is TextureRect && !node.IsQueuedForDeletion() && node.GetChildCount() >= 2
        && node.GetChild(0) is TextureRect && node.GetChild(1) is MegaLabel;
}
