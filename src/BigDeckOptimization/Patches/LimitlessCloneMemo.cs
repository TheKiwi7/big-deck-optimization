using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Limitless's (1.1.7 to 1.2.1, same code) <c>MultiEnchantment.DeepCloneFields</c> rebuilds every saved inner card when a card is cloned:
/// <c>CardModel.FromSerializable(c).MutableClone().ToSerializable()</c>, for every card of the deck at each combat start
/// (~210 ms with 2 x 10,000 cards). That result depends only on the saved card's content, never changed once made (no code of the
/// game, Limitless or BaseLib writes to a <c>SerializableCard</c> after making it), and on BaseLib's registered saves
/// (<see cref="LimitlessReserialize.MemoStamp"/>; -1 when unknown, then always rebuilt). The result made for a content is given for the
/// same content again, read-only like any saved card: a big deck holds few different inner cards, a first combat gains as much as the
/// next ones, and so do other players' decks, rebuilt as new objects at each co-op combat. A saved card with real BaseLib data isn't kept,
/// nor one whose card or enchantment type has, below CardModel / EnchantmentModel, an override on the way that isn't pinned or a
/// mod's saved property (<see cref="OnTheWay"/>: what is checked is those overrides and the saved properties' accessors, run by
/// reflection), and only while the game's own code of that clone is the checked one (<see cref="GameChain"/>).
///
/// One answer in <see cref="VerifyEvery"/> is rebuilt and both serialized: a byte difference turns it off for the session.
/// The memo is a bounded cache across runs by design (<see cref="MaxContents"/> saved contents, pure data with no model or
/// run reference): a later run with the same inner cards reuses it, and it is cleared when the stamp changes.
/// </summary>
public static class LimitlessCloneMemo
{
    public const string Name = "LimitlessCloneMemo";
    internal static int VerifyEvery { get; set; } = 256;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Verified { get; private set; }

    /// <summary>Limitless 1.1.7 to 1.2.1 (same code): the method and the lambda it maps each saved inner card with.</summary>
    private const string DeepCloneFieldsFingerprint = "B0598AD4D39BD71C";
    private const string LambdaFingerprint = "70B31715ABF34F7B";

    /// <summary>A saved card by content, and what BaseLib holds for it (<see cref="LimitlessReserialize.StoredDataState"/>).</summary>
    private readonly record struct Key(SerializableCard Saved, int Data);

    private sealed class ContentComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key a, Key b) => a.Data == b.Data && (ReferenceEquals(a.Saved, b.Saved) || LimitlessReserialize.SameContent(a.Saved, b.Saved));

        public int GetHashCode(Key key) =>
            HashCode.Combine(key.Saved.Id, key.Saved.CurrentUpgradeLevel, key.Saved.FloorAddedToDeck, key.Saved.Enchantment?.Id, key.Saved.Enchantment?.Amount, key.Data);
    }

    /// <summary>Holds saved cards only, no model. Main thread.</summary>
    private static readonly Dictionary<Key, SerializableCard> _memo = new(new ContentComparer());
    private const int MaxContents = 16384;
    private static long _memoStamp = -1;
    private static MethodInfo? _lambda;
    private static int _sinceVerify;
    [ThreadStatic] private static PacketWriter? _memoWriter, _freshWriter;

    public static void Apply(Harmony harmony)
    {
        if (!LimitlessReserialize.ComparisonReady)
        {
            throw new PatchSkippedException("needs LimitlessReserialize (BaseLib and Limitless)");
        }
        Type multi = AccessTools.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        MethodInfo deepClone = AccessTools.Method(multi, "DeepCloneFields")
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "DeepCloneFields");
        PatchSafety.RequireFingerprint(deepClone, DeepCloneFieldsFingerprint);
        Type closure = multi.GetNestedType("<>c", BindingFlags.NonPublic) ?? throw new PatchSkippedException("Limitless has no lambda class");
        _lambda = closure.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(m => m.Name.StartsWith("<DeepCloneFields>", StringComparison.Ordinal)
                && m.ReturnType == typeof(SerializableCard) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(SerializableCard))
            ?? throw new PatchSkippedException("DeepCloneFields maps the saved cards another way");
        PatchSafety.RequireFingerprint(_lambda, LambdaFingerprint);
        // Fixed for the session: a changed game chain leaves the whole memo to the game (Stamp() checks it too).
        if (!GameChainKnown())
        {
            throw new PatchSkippedException("the game's clone code changed: " + _gameChainChanged);
        }
        PatchGuard.Patch(harmony, _lambda, prefix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(Postfix)));
        Applied = true;
        // What Stamp asks about (display only): the lambda, the game's clone chain (built at the preflight, each method with
        // its allowed patches), and the overrides on the way of the card and enchantment types met so far (those of types
        // first cloned later show when checked).
        PatchSafety.Watch(Name, _lambda);
        WatchChain();
        PatchSafety.WatchAll(Name, () => _onTheWay.Values.ToArray().Where(methods => methods != null).SelectMany(methods => methods!).ToArray());
        // Only saves work: without it Limitless's options are read at every inner card, as before.
        try
        {
            PatchGuard.Patch(harmony, deepClone, prefix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(CloneEnter)),
                finalizer: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(CloneExit)));
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: Limitless's options read at every inner card: {e.Message}");
        }
    }

    /// <summary>
    /// Nesting of <c>MultiEnchantment.DeepCloneFields</c> calls on this thread, and Limitless's options read once in the
    /// outermost one: nothing a clone's inner cards run can start or end a run or change the config, while the options'
    /// getters each ask the run by reflection (~2 calls per inner card, ~60,000 at a 2 x 10,000-card combat start).
    /// </summary>
    [ThreadStatic] private static int _cloneDepth;
    [ThreadStatic] private static bool _cloneOptionsKnown;
    [ThreadStatic] private static long _cloneOptions;
#if BENCH
    /// <summary>Options answered from the clone's first read (the effects check).</summary>
    public static long OptionsReused { get; private set; }
#endif

    private static void CloneEnter() => _cloneDepth++;

    private static void CloneExit()
    {
        // Never below 0: an exit without its enter (should a prefix ever be skipped) only makes the next reads uncached.
        if (--_cloneDepth <= 0)
        {
            _cloneDepth = 0;
            _cloneOptionsKnown = false;
        }
    }

    /// <summary>
    /// What a memo hit skips besides ToSerializable (LimitlessReserialize watches that): the lambda's
    /// <c>CardModel.FromSerializable</c> (the card's creation, its saved properties, its enchantment and upgrades) and
    /// <c>MutableClone</c> (its dynamic variables' copies). The known patches there only shape the clone, which the lambda
    /// serializes and drops, from the saved card, the loaded types (in the stamp) and Limitless's uncapped options (in the
    /// stamp): BaseLib's extended data, SavedSpireField, modifier, resource and dynamic variable patches, Limitless's
    /// enchantment, upgrade and status scaling ones (fingerprinted), and BaseLib's custom piles lookup on the card's node
    /// (none for a clone). Any other patch there, or on what that code calls (the last block): no memo.
    /// </summary>
    private static readonly (Type Type, string Method, (string Patch, string Fingerprint)[] Allowed)[] Chain =
    {
        (typeof(CardModel), nameof(CardModel.FromSerializable), new[] { ("BaseLib.Patches.Saves.ExtendedSavePatches+LoadExtendedCardData:InsertLoad", "62A49D6ABB10527F") }),
        (typeof(MegaCrit.Sts2.Core.Saves.SaveUtil), "CardOrDeprecated", Array.Empty<(string, string)>()),
        (typeof(CardModel), nameof(CardModel.ToMutable), Array.Empty<(string, string)>()),
        (typeof(SavedProperties), nameof(SavedProperties.Fill), Array.Empty<(string, string)>()),
        (typeof(SavedProperties), "FillInternal", new[] { ("BaseLib.Patches.Utils.SavedSpireFieldPatch:PostfixFillInternal", "9CB88B5257A66DA1") }),
        (typeof(CardModel), "set_FloorAddedToDeck", Array.Empty<(string, string)>()),
        (typeof(CardModel), "AfterDeserialized", Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable), new[] { ("BaseLib.Patches.Saves.ExtendedSavePatches+LoadExtendedEnchantmentData:LoadExtendedData", "33677253623C472C") }),
        (typeof(CardModel), "EnchantInternal", new[] { ("Limitless.LimitlessCode.UncappedEnchantments.CardModelPatches.Patch_EnchantInternal:Prefix", "62B7BA3E5675B36E") }),
        (typeof(CardModel), "set_Enchantment", new[] { ("Limitless.LimitlessCode.UncappedEnchantments.CardModelPatches.Patch_set_Enchantment:Prefix", "A9E9749AE65A7B1A") }),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ModifyCard), Array.Empty<(string, string)>()),
        (typeof(CardModel), "UpgradeInternal", new[]
        {
            ("BaseLib.Abstracts.UpgradeModifiers:UpgradeModifiersOnCard", "0E5CD63A10E2CE93"),
            ("BaseLib.Patches.Utils.UpgradeInternalPatch:InsertVarUpgrade", "7FFE6E867A734819"),
        }),
        (typeof(CardModel), "set_CurrentUpgradeLevel", new[] { ("Limitless.LimitlessCode.UncappedUpgrades.DynamicVarPatches.CardModelSetPreviousLevel:Prefix", "8BC779DEFF15A360") }),
        (typeof(CardModel), "FinalizeUpgradeInternal", new[]
        {
            ("BaseLib.Abstracts.CustomResourcePatches:FinalizeAdditionalResourceUpgrades", "66100426C96AA720"),
            ("BaseLib.Abstracts.FinalizeModifierUpgrade:FinalizeModifiersOnCard", "120EDCC5332F3B4F"),
        }),
        (typeof(AbstractModel), nameof(AbstractModel.MutableClone), new[]
        {
            ("BaseLib.Utils.ICloneableField+CloneSpireFields:ModifyResult", "F81AFBBAEBB315D5"),
            ("Limitless.LimitlessCode.UncappedActs.AbstractModelPatches.Patch_MutableClone:Postfix", "4FCF0FF046F3588C"),
        }),
        (typeof(CardModel), "DeepCloneFields", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar), "Clone", new[] { ("BaseLib.Extensions.DynamicVarExtensions+CloneTooltips:Copy", "443436F93886072A") }),
        // Called by Limitless's set_Enchantment prefix when a card gets a second enchantment.
        (typeof(MegaCrit.Sts2.Core.Commands.CardCmd), "Enchant", new[] { ("Limitless.LimitlessCode.UncappedEnchantments.CardCmdPatches.Patch_Enchant:Transpiler", "46E29C1A39F295FA") }),
        // The enchantment's side of FromSerializable and of the clone, and what ModifyCard, UpgradeInternal and
        // FinalizeUpgradeInternal call (this mod's own patches there, e.g. on set_Amount, don't count).
        (typeof(MegaCrit.Sts2.Core.Saves.SaveUtil), "EnchantmentOrDeprecated", Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ToMutable), Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), "set_Amount", Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ApplyInternal), Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), "OnEnchant", Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.RecalculateValues), Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), "DeepCloneFields", Array.Empty<(string, string)>()),
        (typeof(CardModel), "OnUpgrade", Array.Empty<(string, string)>()),
        (typeof(CardModel), "AfterCloned", Array.Empty<(string, string)>()),
        (typeof(AbstractModel), "DeepCloneFields", Array.Empty<(string, string)>()),
        (typeof(AbstractModel), "AfterCloned", Array.Empty<(string, string)>()),
        (typeof(AbstractModel), nameof(AbstractModel.ClonePreservingMutability), Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), "Clone", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), "RecalculateForUpgradeOrEnchant", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), "FinalizeUpgrade", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "Clone", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "FinalizeUpgrade", Array.Empty<(string, string)>()),
        // What the game code above and GameOverrides call in turn, one level down (a patch deeper still isn't seen; their
        // effects there touch only keywords, energy costs, dynamic variables and nodes, none saved): keywords (Goopy, RoyallyApproved, Steady, TezcatarasEmber, SoulsPower's OnEnchant),
        // the energy cost and dynamic variable upgrades (TezcatarasEmber, BaseLib's UpgradeVars), the upgrade cap
        // set_CurrentUpgradeLevel reads, the card's node lookup (none for a clone). A memo hit skips them all.
        (typeof(CardModel), "AddKeyword", Array.Empty<(string, string)>()),
        (typeof(CardModel), "RemoveKeyword", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Commands.CardCmd), "RemoveKeyword", Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Nodes.Cards.NCard), "FindOnTable", new[] { ("BaseLib.Patches.Content.GetNCardPile:CheckCustomPiles", "0831B6FE3286A8E1") }),
        (typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "UpgradeBy", new[] { ("Limitless.LimitlessCode.UncappedUpgrades.CardEnergyCostPatches.Patch_UpgradeBy:Prefix", "2BAB48D331AC40BF") }),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar), "UpgradeValueBy", new[]
        {
            ("Limitless.LimitlessCode.UncappedUpgrades.DynamicVarPatches.Patch_UpgradeValueBy:Prefix", "54AD057553603BD4"),
            ("Limitless.LimitlessCode.UncappedActs.DynamicVarPatches.Patch_UpgradeValueBy:Prefix", "A2B0BBF41BF2C079"),
        }),
        (typeof(CardModel), "get_MaxUpgradeLevel", new[] { ("Limitless.LimitlessCode.UncappedUpgrades.CardModelPatches.Patch_MaxUpgradeLevel:Postfix", "2E571E10224C0E6E") }),
    };

    /// <summary>
    /// The game's code on the way of the lambda (FromSerializable, then MutableClone) that shapes what ToSerializable saves:
    /// the memo (and <see cref="LimitlessHookSkip"/>'s drops, which skip the getter's FromSerializable of a clone's inner
    /// cards) take its result to depend on the saved card alone. <see cref="Chain"/> only sees other mods' patches there;
    /// this pins the game's own code (game v0.107.1; other versions in EquivalentFingerprints). ToSerializable and
    /// SavedProperties.From are LimitlessReserialize's. Dynamic variables, energy costs and keywords aren't saved: their
    /// copies don't count here.
    /// </summary>
    private static readonly (Type Type, string Method, string Fingerprint)[] GameChain =
    {
        (typeof(CardModel), nameof(CardModel.FromSerializable), "663431D80AC6CCDF"),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable), "F59AC9E4E33F6805"),
        (typeof(MegaCrit.Sts2.Core.Saves.SaveUtil), "CardOrDeprecated", "3F09E2699DE4B996"),
        (typeof(MegaCrit.Sts2.Core.Saves.SaveUtil), "EnchantmentOrDeprecated", "989D62C62B0166EC"),
        (typeof(CardModel), nameof(CardModel.ToMutable), "83A5F204B06DB078"),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ToMutable), "A845F2C516B8D068"),
        (typeof(AbstractModel), nameof(AbstractModel.MutableClone), "D5F5F0EF9B5E49FA"),
        (typeof(AbstractModel), nameof(AbstractModel.ClonePreservingMutability), "25E8279545E0824F"),
        (typeof(AbstractModel), "DeepCloneFields", "17D6CE246AF07277"),
        (typeof(AbstractModel), "AfterCloned", "0909DD3790FCD0E0"),
        (typeof(CardModel), "DeepCloneFields", "036591BD9285256E"),
        (typeof(CardModel), "AfterCloned", "08693739A0C17DF7"),
        (typeof(CardModel), "AfterDeserialized", "23869126C936D164"),
        (typeof(EnchantmentModel), "DeepCloneFields", "7C21E4E1C30BF6B3"),
        (typeof(SavedProperties), nameof(SavedProperties.Fill), "0A6CCB839E76DE14"),
        (typeof(SavedProperties), "FillInternal", "86A1328A9431BCAD"),
        (typeof(CardModel), "set_FloorAddedToDeck", "F0F067C73D0BEF95"),
        (typeof(CardModel), "set_Enchantment", "E04944BE7A27DF3C"),
        (typeof(CardModel), "set_CurrentUpgradeLevel", "6B87B0AA361E74B7"),
        (typeof(CardModel), "EnchantInternal", "B0DE3FEAF9893C57"),
        (typeof(EnchantmentModel), "set_Amount", "30F7A016B90135D3"),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ApplyInternal), "68F74C5B4950FF0C"),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ModifyCard), "33FF4E3FFE023615"),
        (typeof(EnchantmentModel), "OnEnchant", "23869126C936D164"),
        // Empty in every version (LimitlessRecalcSkip's DefaultRecalculateFingerprint).
        (typeof(EnchantmentModel), nameof(EnchantmentModel.RecalculateValues), "23869126C936D164"),
        (typeof(CardModel), "UpgradeInternal", "61E294C9661E9368"),
        (typeof(CardModel), "OnUpgrade", "23869126C936D164"),
        (typeof(CardModel), "FinalizeUpgradeInternal", "A16D32BDC1BFE3B8"),
    };

    private static readonly object _gameChainLock = new();
    /// <summary>Null until checked, then "" (every method has the checked code) or the changed ones. Fixed for the session.</summary>
    private static volatile string? _gameChainChanged;

    /// <summary>Whether the game's code of <see cref="GameChain"/> is the checked one (checked once, then a field read).</summary>
    internal static bool GameChainKnown()
    {
        string? changed = _gameChainChanged;
        if (changed == null)
        {
            lock (_gameChainLock)
            {
                changed = _gameChainChanged ??= CheckGameChain();
            }
        }
        return changed.Length == 0;
    }

    private static string CheckGameChain()
    {
        var changed = new List<string>();
        foreach ((Type type, string method, string fingerprint) in GameChain)
        {
            try
            {
                // Declared on that type (an overload added or the method moved counts as changed).
                System.Reflection.MethodInfo? found = AccessTools.DeclaredMethod(type, method);
                if (found == null || !PatchSafety.FingerprintMatches(MethodFingerprint.Of(found), fingerprint))
                {
                    changed.Add(type.Name + "." + method);
                }
            }
            catch (Exception e)
            {
                changed.Add($"{type.Name}.{method} ({e.GetType().Name})");
            }
        }
        if (changed.Count == 0)
        {
            return "";
        }
        string result = string.Join(", ", changed);
        Log.Info($"[{ModEntry.Id}] {Name}: the game's clone code changed ({result}): stacked enchantments' inner cards are rebuilt "
            + "at every clone, and never skipped, as the game does");
        return result;
    }

    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed)[]? _chain;
    private static long _chainCheckedAt = long.MinValue / 2;
    private static bool _chainPatched;

    /// <summary>
    /// Asked per stacked card: answered from a result rechecked each second. Also <see cref="LimitlessHookSkip"/>'s: a dropped
    /// forwarder skips the same build of a clone's inner cards.
    /// </summary>
    internal static bool ChainPatched()
    {
        long now = Environment.TickCount64;
        if (now - _chainCheckedAt < 1000)
        {
            return _chainPatched;
        }
        // Published once known: LimitlessHookSkip may ask from another thread meanwhile.
        bool patched = false;
        foreach ((System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed) in ChainMethods())
        {
            if (PatchSafety.IsPatchedByOthers(method, allowed))
            {
                patched = true;
                break;
            }
        }
        _chainPatched = patched;
        _chainCheckedAt = now;
        return patched;
    }

    /// <summary>
    /// Every overload of each chain method (FromSerializable, Fill) with its allowed patches, built once: any of them patched
    /// counts. The overrides the clone reaches in the saved card's own types are checked per type (OnTheWay), not every type's.
    /// </summary>
    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed)[] ChainMethods() =>
        _chain ??= Chain.SelectMany(c => c.Type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.Name == c.Method)
                .Select(m => ((System.Reflection.MethodBase)m, c.Allowed.Select(a => Allowed(a.Patch, a.Fingerprint)).OfType<System.Reflection.MethodInfo>().ToArray())))
            .ToArray();

    /// <summary>
    /// Registers the chain <see cref="ChainPatched"/> asks about for the back-off preflight, under this class's name (where
    /// its runtime check records, LimitlessHookSkip's asks included); built inside the preflight. Never throws.
    /// </summary>
    internal static void WatchChain() =>
        PatchSafety.WatchPairs(Name, () => ChainMethods().Select(c => ((System.Reflection.MethodBase?)c.Method, (System.Reflection.MethodInfo[]?)c.Allowed)));

    /// <summary>
    /// The virtuals the chain reaches in the card's and the enchantment's own types: FromSerializable's AfterDeserialized,
    /// the clone's DeepCloneFields and AfterCloned, UpgradeInternal's OnUpgrade, ModifyCard's OnEnchant and RecalculateValues
    /// (ModifyCard itself isn't virtual: until v1.0.2 this list named it, which matched nothing).
    /// </summary>
    private static readonly string[] OverriddenOnTheWay = { "AfterDeserialized", "DeepCloneFields", "AfterCloned", "OnUpgrade", "OnEnchant", "RecalculateValues" };

    /// <summary>
    /// Overrides on the way outside the game's code whose code is known to depend on nothing but the model: Limitless
    /// 1.2.1's storage card (every inner card) upgrades nothing.
    /// </summary>
    private static readonly Dictionary<string, string> KnownOverrides = new()
    {
        ["Limitless.LimitlessCode.UncappedEnchantments.EnchantmentStorageCard::OnUpgrade"] = "18AEDCC402F01079",
    };

    /// <summary>
    /// The game's own overrides on the way, pinned like the rest of the chain (game v0.107.1; other versions in
    /// EquivalentFingerprints): an update making one read more than the model (the run, the combat) would otherwise go
    /// unseen. Today they only touch keywords, dynamic variables and energy costs, none saved. Only enchantment types are
    /// listed: inner cards are always Limitless's storage card, so a game card type with an override on the way (500+
    /// OnUpgrade alone) is simply never kept or dropped.
    /// </summary>
    private static readonly Dictionary<string, string> GameOverrides = new()
    {
        ["MegaCrit.Sts2.Core.Models.Enchantments.Adroit::RecalculateValues"] = "A33A1012839B9D01",
        ["MegaCrit.Sts2.Core.Models.Enchantments.Goopy::OnEnchant"] = "6242499F022CD140",
        ["MegaCrit.Sts2.Core.Models.Enchantments.RoyallyApproved::OnEnchant"] = "55D752A6AE93E27C",
        ["MegaCrit.Sts2.Core.Models.Enchantments.SoulsPower::OnEnchant"] = "B7E6090026BA003C",
        ["MegaCrit.Sts2.Core.Models.Enchantments.Steady::OnEnchant"] = "9A04D191E5323152",
        ["MegaCrit.Sts2.Core.Models.Enchantments.TezcatarasEmber::OnEnchant"] = "D794311BEE023C2F",
    };

    /// <summary>Per type, never changed once added: read without a lock (asked per inner card by the filters).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo[]?> _onTheWay = new();

    /// <summary>
    /// The overrides of <see cref="OverriddenOnTheWay"/> a card or enchantment type has (its own and its ancestors' below
    /// CardModel / EnchantmentModel), when each is pinned (<see cref="GameOverrides"/>, <see cref="KnownOverrides"/>); null when
    /// one isn't, or when a mod's type there declares a saved property (its accessors run by reflection in the game's
    /// SavedProperties.Fill and From, unchecked): other code there may read more than the saved card (the run, an RNG, a
    /// counter), so what the clone gives can't be kept, and a build of it skipped (<see cref="LimitlessHookSkip"/>) may not
    /// give what is saved. Fixed per type: cached (computed twice at worst by two threads, same answer).
    /// </summary>
    internal static System.Reflection.MethodInfo[]? OnTheWay(Type type) => _onTheWay.GetOrAdd(type, FindOnTheWay);

    private static System.Reflection.MethodInfo[]? FindOnTheWay(Type type)
    {
        var found = new List<System.Reflection.MethodInfo>();
        System.Reflection.Assembly game = typeof(CardModel).Assembly;
        for (Type? t = type; t != null && t != typeof(CardModel) && t != typeof(EnchantmentModel) && t != typeof(AbstractModel); t = t.BaseType)
        {
            // A mod's saved property: the game's Fill sets it and SavedProperties.From reads it back on the clone's way, code
            // no fingerprint here covers (none today: Limitless's storage card and BaseLib's bases declare none, and
            // MultiEnchantment, which does, is never an inner type). The game's own are the game's code, as the rest.
            if (t.Assembly != game && t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                .Any(p => p.IsDefined(typeof(SavedPropertyAttribute), true)))
            {
                return null;
            }
            foreach (System.Reflection.MethodInfo method in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!OverriddenOnTheWay.Contains(method.Name) || method.GetParameters().Length != 0 || method.GetBaseDefinition() == method)
                {
                    continue;
                }
                string key = t.FullName + "::" + method.Name;
                bool gameCode = t.Assembly == game;
                string? fingerprint = null;
                bool listed = gameCode
                    ? typeof(EnchantmentModel).IsAssignableFrom(t) && GameOverrides.TryGetValue(key, out fingerprint)
                    : KnownOverrides.TryGetValue(key, out fingerprint);
                if (!listed || fingerprint == null || !PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint))
                {
                    if (gameCode && GameOverrides.ContainsKey(key))
                    {
                        Log.Info($"[{ModEntry.Id}] {Name}: the game's {key} changed: {type.Name}'s stacked inner cards are rebuilt "
                            + "at every clone and never skipped, as the game does");
                    }
                    return null;
                }
                found.Add(method);
            }
        }
        return found.ToArray();
    }

    /// <summary>Whether another mod patches one of these overrides (PatchSafety caches each answer for a while).</summary>
    internal static bool AnyPatched(System.Reflection.MethodInfo[] methods)
    {
        foreach (System.Reflection.MethodInfo method in methods)
        {
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly Dictionary<ModelId, System.Reflection.MethodInfo[]?> _cardTypes = new(), _enchantmentTypes = new();

    /// <summary>
    /// Whether the saved card's own types (what <c>FromSerializable</c> makes of its ids) have only known overrides on the
    /// way, none patched by another mod. Main thread (the prefix's stamp checks it).
    /// </summary>
    private static bool KnownTypes(SerializableCard saved)
    {
        if (saved.Id is not { } cardId || OnTheWayOf(_cardTypes, cardId, static id => MegaCrit.Sts2.Core.Saves.SaveUtil.CardOrDeprecated(id).GetType()) is not { } card
            || AnyPatched(card))
        {
            return false;
        }
        if (saved.Enchantment == null)
        {
            return true;
        }
        return saved.Enchantment.Id is { } enchantmentId
            && OnTheWayOf(_enchantmentTypes, enchantmentId, static id => MegaCrit.Sts2.Core.Saves.SaveUtil.EnchantmentOrDeprecated(id).GetType()) is { } enchantment
            && !AnyPatched(enchantment);
    }

    private static System.Reflection.MethodInfo[]? OnTheWayOf(Dictionary<ModelId, System.Reflection.MethodInfo[]?> byId, ModelId id, Func<ModelId, Type> type)
    {
        if (!byId.TryGetValue(id, out System.Reflection.MethodInfo[]? methods))
        {
            methods = OnTheWay(type(id));
            byId[id] = methods;
        }
        return methods;
    }

    /// <summary>The patch method when its code is the one checked, else null (not allowed).</summary>
    private static System.Reflection.MethodInfo? Allowed(string patch, string fingerprint) =>
        AccessTools.Method(patch) is { } method && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint) ? method : null;

    private static Func<bool>? _uncappedEnchantments, _uncappedUpgrades;

    /// <summary>Limitless's options its patches on the chain read (fixed in a run, from the config outside one).</summary>
    private static long OptionsStamp()
    {
        if (_uncappedEnchantments == null)
        {
            Type context = AccessTools.TypeByName("Limitless.LimitlessCode.Config.ContextManager") ?? throw new MissingMemberException("Limitless ContextManager");
            _uncappedEnchantments = AccessTools.MethodDelegate<Func<bool>>(AccessTools.PropertyGetter(context, "UncappedEnchantmentsEnabled")
                ?? throw new MissingMemberException("ContextManager", "UncappedEnchantmentsEnabled"));
            _uncappedUpgrades = AccessTools.MethodDelegate<Func<bool>>(AccessTools.PropertyGetter(context, "UncappedUpgradesEnabled")
                ?? throw new MissingMemberException("ContextManager", "UncappedUpgradesEnabled"));
        }
        return (_uncappedEnchantments() ? 1L : 0L) << 61 | (_uncappedUpgrades!() ? 1L : 0L) << 60;
    }

    /// <summary>The stamp memos are valid for, or -1 when this can't answer (then the lambda runs and nothing is kept).</summary>
    private static long Stamp()
    {
        if (!Enabled || ListenerCache.IsSuspended || LimitlessReserialize.Faulted || !LimitlessReserialize.CanCompareHere || !GameChainKnown() || LimitlessReserialize.PatchedByOthers()
            || PatchSafety.IsPatchedByOthers(_lambda) || ChainPatched())
        {
            return -1;
        }
        long stamp = LimitlessReserialize.MemoStamp();
        return stamp < 0 ? -1 : stamp | CloneOptionsStamp();
    }

    /// <summary><see cref="OptionsStamp"/>, read once per outermost <c>DeepCloneFields</c> call (every call outside one).</summary>
    private static long CloneOptionsStamp()
    {
        if (_cloneDepth <= 0)
        {
            return OptionsStamp();
        }
        if (_cloneOptionsKnown)
        {
#if BENCH
            OptionsReused++;
#endif
            return _cloneOptions;
        }
        _cloneOptions = OptionsStamp();
        _cloneOptionsKnown = true;
        return _cloneOptions;
    }

    private static bool Prefix(SerializableCard __0, ref SerializableCard __result, out int __state)
    {
        __state = -1;
        try
        {
            long stamp = Stamp();
            if (stamp < 0 || __0 == null)
            {
                return true;
            }
            if (stamp != _memoStamp)
            {
                _memo.Clear();
                _memoStamp = stamp;
            }
            // Saved cards of types with a mod's code on the way, or patched there: the lambda runs, nothing kept.
            if (!KnownTypes(__0))
            {
                return true;
            }
            __state = LimitlessReserialize.StoredDataState(__0);
            if (__state < 0 || !_memo.TryGetValue(new Key(__0, __state), out SerializableCard? memo))
            {
                return true;
            }
            if (++_sinceVerify >= VerifyEvery)
            {
                // This one runs for real and the postfix compares.
                _sinceVerify = 0;
                return true;
            }
            Reused++;
            __result = memo;
            return false;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
            __state = -1;
            return true;
        }
    }

    private static void Postfix(SerializableCard __0, SerializableCard __result, int __state, bool __runOriginal)
    {
        if (!__runOriginal || __state < 0 || __0 == null || __result == null)
        {
            return;
        }
        try
        {
            Key key = new(__0, __state);
            if (_memo.TryGetValue(key, out SerializableCard? memo))
            {
                Verified++;
                if (!SameBytes(memo, __result))
                {
                    Fail($"a kept inner card differs from a rebuilt one ({__0.Id})");
                }
                return;
            }
            if (_memo.Count >= MaxContents)
            {
                _memo.Clear();
            }
            _memo[key] = __result;
        }
        catch (Exception e)
        {
            Fail(e.ToString());
        }
    }

    private static bool SameBytes(SerializableCard a, SerializableCard b)
    {
        _memoWriter ??= new PacketWriter { WarnOnGrow = false };
        _freshWriter ??= new PacketWriter { WarnOnGrow = false };
        _memoWriter.Reset();
        _freshWriter.Reset();
        a.Serialize(_memoWriter);
        b.Serialize(_freshWriter);
        return _memoWriter.BitPosition == _freshWriter.BitPosition
            && _memoWriter.Buffer.AsSpan(0, _memoWriter.BytePosition).SequenceEqual(_freshWriter.Buffer.AsSpan(0, _freshWriter.BytePosition));
    }

    private static void Fail(string details)
    {
        Enabled = false;
        _memo.Clear();
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, inner cards rebuilt at every clone as Limitless does: {details}");
    }
}
