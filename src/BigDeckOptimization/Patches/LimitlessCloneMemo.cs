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
#if BENCH
    /// <summary>The memo's limit; tests lower it (<c>bdperf clonememocap N</c>) so a small run reaches it. Its clears: <see cref="MemoClears"/>.</summary>
    internal static int MaxContents = 16384;
    public static long MemoClears;
#else
    private const int MaxContents = 16384;
#endif
    private static long _memoStamp = -1, _memoPatchStamp = -1;
    private static MethodInfo? _lambda;
    private static int _sinceVerify;
    [ThreadStatic] private static PacketWriter? _memoWriter, _freshWriter;

    public static void Apply(Harmony harmony)
    {
        if (!LimitlessReserialize.ComparisonReady)
        {
            throw new PatchSkippedException("needs LimitlessReserialize (BaseLib and Limitless)");
        }
        Type multi = Reflect.TypeByName(LimitlessReserialize.MultiEnchantmentType)
            ?? throw new PatchSkippedException("Limitless has no " + LimitlessReserialize.MultiEnchantmentType);
        MethodInfo deepClone = Reflect.Method(multi, "DeepCloneFields")
            ?? throw new MissingMethodException(LimitlessReserialize.MultiEnchantmentType, "DeepCloneFields");
        PatchSafety.RequireFingerprint(deepClone, DeepCloneFieldsFingerprint);
        Type closure = multi.GetNestedType("<>c", BindingFlags.NonPublic) ?? throw new PatchSkippedException("Limitless has no lambda class");
        _lambda = closure.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(m => m.Name.StartsWith("<DeepCloneFields>", StringComparison.Ordinal)
                && m.ReturnType == typeof(SerializableCard) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(SerializableCard))
            ?? throw new PatchSkippedException("DeepCloneFields maps the saved cards another way");
        PatchSafety.RequireFingerprint(_lambda, LambdaFingerprint);
        StartClosures();
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
    /// Rule L (2026-11-18): judged per content, for the groups (<see cref="Reach"/>) its build reaches; the overrides on the way are
    /// judged with their closures (<see cref="OverrideClosure"/>) and the allowed patches with the game methods they call
    /// (<see cref="_allowedCallees"/>).
    /// </summary>
    private static readonly (Type Type, string Method, Reach Reach, (string Patch, string Fingerprint)[] Allowed)[] Chain =
    {
        (typeof(CardModel), nameof(CardModel.FromSerializable), Reach.Spine, new[] { ("BaseLib.Patches.Saves.ExtendedSavePatches+LoadExtendedCardData:InsertLoad", "62A49D6ABB10527F") }),
        (typeof(MegaCrit.Sts2.Core.Saves.SaveUtil), "CardOrDeprecated", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(CardModel), nameof(CardModel.ToMutable), Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(SavedProperties), nameof(SavedProperties.Fill), Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(SavedProperties), "FillInternal", Reach.Spine, new[] { ("BaseLib.Patches.Utils.SavedSpireFieldPatch:PostfixFillInternal", "9CB88B5257A66DA1") }),
        (typeof(CardModel), "set_FloorAddedToDeck", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(CardModel), "AfterDeserialized", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable), Reach.Enchant, new[] { ("BaseLib.Patches.Saves.ExtendedSavePatches+LoadExtendedEnchantmentData:LoadExtendedData", "33677253623C472C") }),
        (typeof(CardModel), "EnchantInternal", Reach.Enchant, new[] { ("Limitless.LimitlessCode.UncappedEnchantments.CardModelPatches.Patch_EnchantInternal:Prefix", "62B7BA3E5675B36E") }),
        (typeof(CardModel), "set_Enchantment", Reach.Enchant, new[] { ("Limitless.LimitlessCode.UncappedEnchantments.CardModelPatches.Patch_set_Enchantment:Prefix", "A9E9749AE65A7B1A") }),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ModifyCard), Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(CardModel), "UpgradeInternal", Reach.Upgrade, new[]
        {
            ("BaseLib.Abstracts.UpgradeModifiers:UpgradeModifiersOnCard", "0E5CD63A10E2CE93"),
            ("BaseLib.Patches.Utils.UpgradeInternalPatch:InsertVarUpgrade", "7FFE6E867A734819"),
        }),
        (typeof(CardModel), "set_CurrentUpgradeLevel", Reach.Upgrade, new[] { ("Limitless.LimitlessCode.UncappedUpgrades.DynamicVarPatches.CardModelSetPreviousLevel:Prefix", "8BC779DEFF15A360") }),
        (typeof(CardModel), "FinalizeUpgradeInternal", Reach.Enchant | Reach.Upgrade, new[]
        {
            ("BaseLib.Abstracts.CustomResourcePatches:FinalizeAdditionalResourceUpgrades", "66100426C96AA720"),
            ("BaseLib.Abstracts.FinalizeModifierUpgrade:FinalizeModifiersOnCard", "120EDCC5332F3B4F"),
        }),
        (typeof(AbstractModel), nameof(AbstractModel.MutableClone), Reach.Spine, new[]
        {
            ("BaseLib.Utils.ICloneableField+CloneSpireFields:ModifyResult", "F81AFBBAEBB315D5"),
            ("Limitless.LimitlessCode.UncappedActs.AbstractModelPatches.Patch_MutableClone:Postfix", "4FCF0FF046F3588C"),
        }),
        (typeof(CardModel), "DeepCloneFields", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar), "Clone", Reach.Spine, new[] { ("BaseLib.Extensions.DynamicVarExtensions+CloneTooltips:Copy", "443436F93886072A") }),
        // Called by Limitless's set_Enchantment prefix when a card gets a second enchantment.
        (typeof(MegaCrit.Sts2.Core.Commands.CardCmd), "Enchant", Reach.SecondEnchant, new[] { ("Limitless.LimitlessCode.UncappedEnchantments.CardCmdPatches.Patch_Enchant:Transpiler", "46E29C1A39F295FA") }),
        // The enchantment's side of FromSerializable and of the clone, and what ModifyCard, UpgradeInternal and
        // FinalizeUpgradeInternal call (this mod's own patches there, e.g. on set_Amount, don't count).
        (typeof(MegaCrit.Sts2.Core.Saves.SaveUtil), "EnchantmentOrDeprecated", Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ToMutable), Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), "set_Amount", Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.ApplyInternal), Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), "OnEnchant", Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), nameof(EnchantmentModel.RecalculateValues), Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(EnchantmentModel), "DeepCloneFields", Reach.Enchant, Array.Empty<(string, string)>()),
        (typeof(CardModel), "OnUpgrade", Reach.Upgrade, Array.Empty<(string, string)>()),
        (typeof(CardModel), "AfterCloned", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(AbstractModel), "DeepCloneFields", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(AbstractModel), "AfterCloned", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(AbstractModel), nameof(AbstractModel.ClonePreservingMutability), Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), "Clone", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), "RecalculateForUpgradeOrEnchant", Reach.Enchant | Reach.Upgrade, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet), "FinalizeUpgrade", Reach.Enchant | Reach.Upgrade, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "Clone", Reach.Spine, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "FinalizeUpgrade", Reach.Enchant | Reach.Upgrade, Array.Empty<(string, string)>()),
        // Rule L: what the game's overrides on the way call is their closure (OverrideClosure), not listed here. These entries
        // (Reach.None) only give the closures their allowed patches (the card's node lookup: BaseLib's custom piles transpiler) and
        // the preflight its display (the card's keywords: judged strictly, as every method inside a closure since the review of
        // 2026-11-19, C6: the closure's code may call them on another card). The upgrade cap set_CurrentUpgradeLevel reads and the dynamic
        // variable and energy cost upgrades BaseLib's UpgradeVars and the cards' OnUpgrade call: the upgrade group.
        (typeof(CardModel), "AddKeyword", Reach.None, Array.Empty<(string, string)>()),
        (typeof(CardModel), "RemoveKeyword", Reach.None, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Commands.CardCmd), "RemoveKeyword", Reach.None, Array.Empty<(string, string)>()),
        (typeof(MegaCrit.Sts2.Core.Nodes.Cards.NCard), "FindOnTable", Reach.None, new[] { ("BaseLib.Patches.Content.GetNCardPile:CheckCustomPiles", "0831B6FE3286A8E1") }),
        (typeof(MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost), "UpgradeBy", Reach.Upgrade, new[] { ("Limitless.LimitlessCode.UncappedUpgrades.CardEnergyCostPatches.Patch_UpgradeBy:Prefix", "2BAB48D331AC40BF") }),
        (typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar), "UpgradeValueBy", Reach.Upgrade, new[]
        {
            ("Limitless.LimitlessCode.UncappedUpgrades.DynamicVarPatches.Patch_UpgradeValueBy:Prefix", "54AD057553603BD4"),
            ("Limitless.LimitlessCode.UncappedActs.DynamicVarPatches.Patch_UpgradeValueBy:Prefix", "A2B0BBF41BF2C079"),
        }),
        (typeof(CardModel), "get_MaxUpgradeLevel", Reach.Upgrade, new[] { ("Limitless.LimitlessCode.UncappedUpgrades.CardModelPatches.Patch_MaxUpgradeLevel:Postfix", "2E571E10224C0E6E") }),
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
                System.Reflection.MethodInfo? found = Reflect.DeclaredMethod(type, method);
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

    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed, bool Gated, Reach Reach)[]? _chain;

    /// <summary>
    /// Rule L (2026-11-18): which part of the chain a saved inner card's build reaches. The spine always (FromSerializable's
    /// card, its saved properties, the clone); the enchantment group when it has an enchantment; the upgrade group when its
    /// <c>CurrentUpgradeLevel</c> is above 0 (FromSerializable's upgrade loop; set_CurrentUpgradeLevel's cap); CardCmd.Enchant
    /// only for an enchanted card that isn't Limitless's storage card (its pinned set_Enchantment prefix calls it then only).
    /// <see cref="None"/>: reached only inside an override's closure (<see cref="OverrideClosure"/>), listed for its allowed
    /// patches and gates.
    /// </summary>
    [Flags]
    internal enum Reach
    {
        None = 0,
        Spine = 1,
        Enchant = 2,
        Upgrade = 4,
        SecondEnchant = 8,
    }

    /// <summary>
    /// The chain's answer, published as one object (LimitlessHookSkip may ask from another thread): the groups (<see cref="Reach"/>)
    /// holding another mod's patch no rule lets stand, and the card methods whose other mods' patches are all gated on the card's
    /// type, with those types and their group (<see cref="ChainPatchedFor"/>).
    /// </summary>
    private sealed class ChainAnswer
    {
        public readonly Reach Patched;
        public readonly long CheckedAt;
        /// <summary>The patch stamp it was made at (<see cref="PatchStamp"/>): a Harmony change makes it stale at once.</summary>
        public readonly long Stamp;
        public readonly (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed, Type[] Gates, Reach Reach)[] Gated;

        public ChainAnswer(Reach patched, long checkedAt, long stamp, (System.Reflection.MethodBase, System.Reflection.MethodInfo[], Type[], Reach)[] gated)
        {
            Patched = patched;
            CheckedAt = checkedAt;
            Stamp = stamp;
            Gated = gated;
        }
    }

    private static volatile ChainAnswer _chainAnswer = new(Reach.None, long.MinValue / 2, -1, Array.Empty<(System.Reflection.MethodBase, System.Reflection.MethodInfo[], Type[], Reach)>());

    /// <summary>
    /// The chain's card methods run on the inner card itself: the upgrade cap (get_MaxUpgradeLevel, the card's own), so a patch
    /// there that provably does nothing unless the card is of some types (<see cref="PatchTolerance.TypeGated"/>) changes nothing
    /// for a card of another type: only a card of a gate type loses the memo (and the drops, <see cref="LimitlessHookSkip"/>: its
    /// inner cards are Limitless's storage card).
    /// Rule A (2026-11-18): the CardModel-declared methods of the chain itself too (FromSerializable's ToMutable, AfterDeserialized,
    /// EnchantInternal, set_Enchantment, UpgradeInternal, FinalizeUpgradeInternal): each runs on that same inner card (ToMutable on
    /// its canonical card, of the same type). On ToMutable a postfix gated on <c>__result</c> counts as one on the instance (the
    /// pinned code returns <c>MemberwiseClone</c>'s copy: the same runtime type; <see cref="PatchSafety.SetResultIsInstance"/>).
    /// Not the AbstractModel-declared ones (MutableClone, ClonePreservingMutability, DeepCloneFields, AfterCloned): they also run
    /// on the inner card's enchantment, whose type a gate on card types isn't asked about (strict there).
    /// The gates hold for the chain's own entries only (<see cref="ChainPatchedFor"/>): inside an override's closure the same methods
    /// are judged strictly (review of 2026-11-19, C6), the closure's code may call them on another card (a card it makes or finds);
    /// so AddKeyword and RemoveKeyword, reached only there, are no longer listed (until then CardAscension's AddKeyword prefix gated on
    /// its own card let Goopy, RoyallyApproved, Steady and TezcatarasEmber contents be kept).
    /// </summary>
    private static readonly HashSet<string> GatedChain = new(StringComparer.Ordinal)
    {
        "get_MaxUpgradeLevel",
        nameof(CardModel.ToMutable), "AfterDeserialized", "EnchantInternal", "set_Enchantment", "UpgradeInternal", "FinalizeUpgradeInternal",
    };

    /// <summary>
    /// Whether the spine (what every inner card's build runs) holds another mod's patch no rule lets stand: then nothing is kept
    /// or dropped. The groups are judged per content (<see cref="ChainPatchedFor"/>). Answered from a result rechecked each second
    /// and at each patch stamp.
    /// </summary>
    internal static bool ChainPatched() => (CurrentChainAnswer().Patched & Reach.Spine) != 0;

    /// <summary>
    /// For a content of that card type reaching those groups: a patched group it reaches, or a gated card method of a group it
    /// reaches whose gate the card's type matches (then the strict check of that method answers and records the back-off).
    /// </summary>
    internal static bool ChainPatchedFor(Type cardType, Reach reach)
    {
        ChainAnswer answer = CurrentChainAnswer();
        if ((answer.Patched & (reach | Reach.Spine)) != 0)
        {
            return true;
        }
        foreach ((System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed, Type[] gates, Reach group) in answer.Gated)
        {
            if ((group & (reach | Reach.Spine)) == 0)
            {
                continue;
            }
            foreach (Type gate in gates)
            {
                // No tolerance for a card the gated patch runs on: its call is skipped with the chain.
                if (gate.IsAssignableFrom(cardType) && PatchSafety.IsPatchedByOthers(method, allowed))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static ChainAnswer CurrentChainAnswer()
    {
        // Kept a second at most, and only while the patch set holds (a patch landing within that second is seen at once: the memo
        // is cleared on the same stamp, so an answer made before it must not refill it).
        long now = Environment.TickCount64, stamp = PatchStamp();
        ChainAnswer answer = _chainAnswer;
        if (answer.Stamp == stamp && now - answer.CheckedAt < 1000)
        {
            return answer;
        }
        // Until the allowed patches' callees are known (a worker reads them at Apply), every group counts as patched.
        Reach patched = _allowedCallees == null ? Reach.Spine | Reach.Enchant | Reach.Upgrade | Reach.SecondEnchant : Reach.None;
        var gated = new List<(System.Reflection.MethodBase, System.Reflection.MethodInfo[], Type[], Reach)>();
        foreach ((System.Reflection.MethodBase method, Reach reach) in _allowedCallees ?? Array.Empty<(System.Reflection.MethodBase, Reach)>())
        {
            // No tolerance: skipped with the allowed patch that calls it.
            if ((patched & reach) != reach && (PatchSafety.IsPatchedByOthers(method) || InstancePatched(method, m => PatchSafety.IsPatchedByOthers(m))))
            {
                patched |= reach;
            }
        }
        foreach ((System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed, bool isGated, Reach reach) in ChainMethods())
        {
            // Every group asked (each records its back-off); a method only reached inside a closure is judged there.
            if (reach == Reach.None || (patched & reach) == reach)
            {
                continue;
            }
            if (isGated)
            {
                if (PatchSafety.IsPatchedByOthers(method, allowed, out Type[] gates))
                {
                    patched |= reach;
                }
                else if (gates.Length > 0)
                {
                    gated.Add((method, allowed, gates, reach));
                }
            }
            // No tolerance: the clone chain is skipped for a kept content (CardCmd.Enchant<T>: each patched instantiation too).
            else if (PatchSafety.IsPatchedByOthers(method, AllowedNow(method, allowed))
                || InstancePatched(method, m => PatchSafety.IsPatchedByOthers(m, AllowedNow(method, allowed))))
            {
                patched |= reach;
            }
        }
        answer = new ChainAnswer(patched, now, stamp, gated.ToArray());
        _chainAnswer = answer;
        return answer;
    }

    /// <summary>
    /// Every overload of each chain method (FromSerializable, Fill) with its allowed patches, built once: any of them patched
    /// counts. The overrides the clone reaches in the saved card's own types are checked per type (OnTheWay), not every type's.
    /// Built entry by entry (<see cref="ChainPart"/>): the startup preflight resolves one entry per step, the whole chain at
    /// once took ~10 ms of one main menu frame (reflection over the game types' methods, other mods' allowed patches looked up
    /// and fingerprinted), over the 3 ms step budget.
    /// </summary>
    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed, bool Gated, Reach Reach)[] ChainMethods() =>
        _chain ??= Enumerable.Range(0, Chain.Length).SelectMany(ChainPart).ToArray();

    /// <summary>One <see cref="Chain"/> entry's methods (every overload) with their allowed patches, built once (idempotent: a race builds the same twice).</summary>
    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed, bool Gated, Reach Reach)[] ChainPart(int index)
    {
        var parts = _chainParts ??= new (System.Reflection.MethodBase, System.Reflection.MethodInfo[], bool, Reach)[]?[Chain.Length];
        if (parts[index] is { } known)
        {
            return known;
        }
        (Type type, string name, Reach reach, (string Patch, string Fingerprint)[] allowedPatches) = Chain[index];
        System.Reflection.MethodInfo[] allowed = allowedPatches.Select(a => Allowed(a.Patch, a.Fingerprint)).OfType<System.Reflection.MethodInfo>().ToArray();
        var built = DeclaredMethods(type).Where(m => m.Name == name)
            .Select(m => ((System.Reflection.MethodBase)m, allowed, type == typeof(CardModel) && !m.IsStatic && GatedChain.Contains(name), reach))
            .ToArray();
        // Before any check of it: CardModel.ToMutable's result is its instance's copy (pinned in GameChain), so a postfix gated on
        // __result is gated on the instance's type.
        if (type == typeof(CardModel) && name == nameof(CardModel.ToMutable))
        {
            foreach ((System.Reflection.MethodBase method, _, bool gated, _) in built)
            {
                if (gated && method.GetParameters().Length == 0)
                {
                    PatchSafety.SetResultIsInstance(method);
                }
            }
        }
        return parts[index] = built;
    }

    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed, bool Gated, Reach Reach)[]?[]? _chainParts;

    /// <summary>
    /// A chain method's allowed patches as of now: on <c>SavedProperties.FillInternal</c>, BaseLib's postfix and the bundled copies
    /// of it whose own list is empty (rule B, <see cref="BaseLibTypes.WithTwins"/>; one holding a field backs off).
    /// </summary>
    private static System.Reflection.MethodInfo[] AllowedNow(System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed) =>
        method.DeclaringType == typeof(SavedProperties) && method.Name == "FillInternal" ? BaseLibTypes.WithTwins(allowed, fromInternal: false) : allowed;

    /// <summary>A type's declared methods, read once per type (several chain entries share CardModel's, AbstractModel's...).</summary>
    private static System.Reflection.MethodInfo[] DeclaredMethods(Type type) =>
        _declared.GetOrAdd(type, t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo[]> _declared = new();

    /// <summary>
    /// Registers the chain <see cref="ChainPatched"/> asks about for the back-off preflight, under this class's name (where
    /// its runtime check records, LimitlessHookSkip's asks included) or a patch relying on it (<paramref name="patch"/>:
    /// LimitlessHookSkip, so bdopt why and the menu show its back-offs while this one isn't running); the gated card methods
    /// with their tolerance. One entry per chain entry (each its own preflight step), built inside the preflight. Never throws.
    /// </summary>
    internal static void WatchChain(string patch = Name)
    {
        for (int i = 0; i < Chain.Length; i++)
        {
            int index = i;
            PatchSafety.WatchPairs(patch, () => ChainPart(index).Where(c => !c.Gated).Select(c => ((System.Reflection.MethodBase?)c.Method, (System.Reflection.MethodInfo[]?)AllowedNow(c.Method, c.Allowed))));
            if (Chain[index].Type == typeof(CardModel) && GatedChain.Contains(Chain[index].Method))
            {
                PatchSafety.WatchPairs(patch, () => ChainPart(index).Where(c => c.Gated).Select(c => ((System.Reflection.MethodBase?)c.Method, (System.Reflection.MethodInfo[]?)c.Allowed)),
                    PatchTolerance.TypeGated);
            }
        }
    }

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
            // No tolerance: overrides skipped with the chain.
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    // --- Rule L (2026-11-18): what the overrides on the way call, from their code ---

    /// <summary>Methods at most in one override's closure (SoulsPower's OnEnchant reaches ~3,700 on v0.113.1): more, that override's types aren't kept.</summary>
    private const int ClosureLimit = 8000;

    /// <summary>
    /// The game's code one override on the way reaches (<see cref="MethodFingerprint.GameClosure"/> on the running game's code: calls
    /// followed through the game, the game's own overrides of the game's virtual methods it calls included, no type initializers):
    /// replaces the hand table of "what the enchantments' OnEnchant call" (AddKeyword, CardCmd.RemoveKeyword, FindOnTable,
    /// UpgradeBy...), and so also holds what that table missed (TezcatarasEmber's CardEnergyCost.GetWithModifiers). Only the set of
    /// methods whose other mods' patches matter: worked out again at each launch, never pinned (a game update just gives a new set;
    /// the override's own code stays pinned in <see cref="GameOverrides"/>, which guards the game's code as before rule L). A chain
    /// method in it is judged with its allowed patches, never its gates (review of 2026-11-19, C6: the closure's code may call a card
    /// method on another card than the inner one, a card it makes or finds, whose type no gate is asked about); every other method
    /// strictly. Another assembly's override of a virtual
    /// method it calls isn't a patch and doesn't count: the content's own types' overrides on the way are <see cref="OnTheWay"/>'s
    /// (pinned), and another type's run only on an object of that type, which an inner card's build (no owner, no combat) never holds.
    /// </summary>
    private sealed class OverrideClosure
    {
        public OverrideClosure(System.Reflection.MethodBase[] methods, (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed)[] known)
        {
            Methods = methods;
            Known = known;
        }

        /// <summary>The closure's methods but the chain's (strict).</summary>
        public readonly System.Reflection.MethodBase[] Methods;
        /// <summary>The chain methods in the closure, judged with their allowed patches (no gate).</summary>
        public readonly (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed)[] Known;

        private sealed record StrictAnswer(long Stamp, long CheckedAt, bool Patched);

        /// <summary>
        /// Whether another mod patches one of <see cref="Methods"/>, per patch stamp and for <see cref="RecheckMs"/> at most (read and
        /// replaced whole: any thread).
        /// </summary>
        private volatile StrictAnswer? _strict;

        public bool Patched()
        {
            long stamp = PatchStamp(), now = Environment.TickCount64;
            StrictAnswer? known = _strict;
            if (known == null || known.Stamp != stamp || now - known.CheckedAt >= RecheckMs)
            {
                // Only the methods Harmony lists as patched are asked (a method without any patch isn't another mod's), on the
                // MethodBase Harmony holds (a generic member here is its definition, Harmony's patch is on a closed instantiation);
                // every one of those asked (each back-off recorded under this class's name), as the strict check of each would.
                Dictionary<(System.Reflection.Module, int), System.Reflection.MethodBase[]>? patchedNow = PatchedMethods();
                bool patched = patchedNow == null;
                foreach (System.Reflection.MethodBase method in patched ? Array.Empty<System.Reflection.MethodBase>() : Methods)
                {
                    if (patchedNow!.TryGetValue((method.Module, method.MetadataToken), out System.Reflection.MethodBase[]? instances))
                    {
                        foreach (System.Reflection.MethodBase instance in instances)
                        {
                            patched |= PatchSafety.IsPatchedByOthers(instance);
                        }
                    }
                }
                _strict = known = new StrictAnswer(stamp, now, patched);
            }
            if (known.Patched)
            {
                return true;
            }
            foreach ((System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed) in Known)
            {
                // No tolerance (no gate either, C6): called with the closure's code, on whatever card that code holds.
                if (PatchSafety.IsPatchedByOthers(method, AllowedNow(method, allowed))
                    || InstancePatched(method, m => PatchSafety.IsPatchedByOthers(m, AllowedNow(method, allowed))))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The game methods the allowed BaseLib / Limitless patches of <see cref="Chain"/> call, themselves or through their own assembly's
    /// helpers (<see cref="MethodFingerprint.GameCalleesThroughOwn"/>, review of 2026-11-19, C3: Limitless's get_MaxUpgradeLevel postfix
    /// reads the card in GetConstructedCardMax; one level into the game: the patches are pinned, what they call is judged by its
    /// patches, as the chain's own callees are), with the group of the method they patch (an entry only reached inside a closure: the
    /// spine). Not the chain's own methods (judged with their entries), nor the methods LimitlessReserialize judges (its answer is
    /// required by this patch and LimitlessHookSkip: CardModel.ToSerializable, which Limitless's set_Enchantment prefix reaches when it
    /// stacks a second enchantment, with BaseLib's postfix there), nor the game's logger (a log line saves nothing). A transpiler's own
    /// code runs once at patch time: the helpers it inserts aren't followed (BaseLib's UpgradeVars calls DynamicVar.UpgradeValueBy, an
    /// entry of its own). Null until worked out (<see cref="StartClosures"/>): every group then counts as patched.
    /// </summary>
    private static volatile (System.Reflection.MethodBase Method, Reach Reach)[]? _allowedCallees;

    /// <summary>Methods of their own assembly the allowed patches may reach at most (more: nothing is kept or dropped).</summary>
    private const int OwnHelperLimit = 500;

    /// <param name="byChainMethod">Per chain method, what its allowed patches call (the chain's methods among them): a closure reaching
    /// that method reaches these too (C3), whatever the content's groups.</param>
    private static (System.Reflection.MethodBase Method, Reach Reach)[] AllowedCallees(out Dictionary<System.Reflection.MethodBase, System.Reflection.MethodBase[]> byChainMethod)
    {
        System.Reflection.Assembly game = typeof(CardModel).Assembly;
        var chain = ChainMethods();
        var chainMethods = new HashSet<System.Reflection.MethodBase>(chain.Select(c => MethodFingerprint.Definition(c.Method)));
        var judgedElsewhere = new HashSet<(System.Reflection.Module, int)>(LimitlessReserialize.JudgedMethods().Select(m => (m.Module, m.MetadataToken)));
        var found = new Dictionary<System.Reflection.MethodBase, Reach>();
        byChainMethod = new Dictionary<System.Reflection.MethodBase, System.Reflection.MethodBase[]>();
        var calleesOfPatch = new Dictionary<System.Reflection.MethodInfo, System.Reflection.MethodBase[]>();
        foreach ((System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed, _, Reach reach) in chain)
        {
            var mine = new HashSet<System.Reflection.MethodBase>();
            foreach (System.Reflection.MethodInfo patch in allowed)
            {
                if (!calleesOfPatch.TryGetValue(patch, out System.Reflection.MethodBase[]? callees))
                {
                    calleesOfPatch[patch] = callees = MethodFingerprint.GameCalleesThroughOwn(game, patch, OwnHelperLimit)
                        .Select(MethodFingerprint.Definition)
                        .Where(d => d.Module.Assembly == game && !judgedElsewhere.Contains((d.Module, d.MetadataToken))
                            && d.DeclaringType?.Namespace != typeof(MegaCrit.Sts2.Core.Logging.Logger).Namespace)
                        .Distinct().ToArray();
                }
                foreach (System.Reflection.MethodBase definition in callees)
                {
                    mine.Add(definition);
                    if (!chainMethods.Contains(definition))
                    {
                        found[definition] = found.GetValueOrDefault(definition) | (reach == Reach.None ? Reach.Spine : reach);
                    }
                }
            }
            if (mine.Count > 0)
            {
                byChainMethod[method] = mine.ToArray();
            }
        }
        return found.Select(e => (e.Key, e.Value)).ToArray();
    }

    /// <summary>
    /// What a closure reaching these chain methods also runs (C3): their allowed patches' callees (<paramref name="byChainMethod"/>), and
    /// those of the chain methods they call in turn. The chain methods found join <paramref name="known"/>; the others are returned.
    /// </summary>
    private static List<System.Reflection.MethodBase> AllowedCalleesIn(HashSet<System.Reflection.MethodBase> known, HashSet<System.Reflection.MethodBase> chainMethods,
        Dictionary<System.Reflection.MethodBase, System.Reflection.MethodBase[]> byChainMethod)
    {
        var others = new List<System.Reflection.MethodBase>();
        var queue = new Queue<System.Reflection.MethodBase>(known);
        while (queue.Count > 0)
        {
            if (!byChainMethod.TryGetValue(queue.Dequeue(), out System.Reflection.MethodBase[]? callees))
            {
                continue;
            }
            foreach (System.Reflection.MethodBase callee in callees)
            {
                if (chainMethods.Contains(callee))
                {
                    if (known.Add(callee))
                    {
                        queue.Enqueue(callee);
                    }
                }
                else
                {
                    others.Add(callee);
                }
            }
        }
        return others;
    }

    /// <summary>
    /// Per override (<c>Type::Method</c>, as <see cref="FindOnTheWay"/> keys them): its closure (<see cref="OverrideClosure"/>), the
    /// method its walk stopped at (<see cref="BlockedClosure"/>: patched by another mod), or <see cref="_noClosure"/> when it can't be
    /// bounded. Published whole by the worker; read from any thread.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _closures = new();
    private static readonly object _noClosure = new();
    private static int _closuresStarted, _closuresDone;
    /// <summary>Each publication of the worker (part of <see cref="PatchStamp"/>).</summary>
    private static long _closureGeneration;

    /// <summary>A walk stopped at a method another mod patches: the override counts as patched while that patch stays.</summary>
    private sealed record BlockedClosure(System.Reflection.MethodBase Blocker);

    /// <summary>Wakes the worker to resume a walk whose blocking patch went away.</summary>
    private static readonly System.Threading.AutoResetEvent _resume = new(false);

    /// <summary>
    /// Starts, once and only for the Limitless parts (LimitlessCloneMemo's and LimitlessHookSkip's Apply, after their Limitless
    /// checks; not when the game's clone chain changed), the worker that reads what the allowed patches call
    /// (<see cref="_allowedCallees"/>: until then nothing is kept or dropped) and walks every pinned override's closure (until one is
    /// published, the types having that override aren't kept or dropped, as an unknown override). Its own background thread at
    /// below-normal priority; the main thread never waits on it (published results only; MethodFingerprint's types cache is read
    /// outside its lock). Never throws.
    /// </summary>
    internal static void StartClosures()
    {
        if (System.Threading.Volatile.Read(ref _closuresStarted) != 0 || !GameChainKnown() || System.Threading.Interlocked.Exchange(ref _closuresStarted, 1) != 0)
        {
            return;
        }
        try
        {
            new System.Threading.Thread(ClosureWorker)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
                Name = ModEntry.Id + " closures",
            }.Start();
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: the closures' worker didn't start ({e.Message}): stacked enchantments' inner cards are rebuilt at every clone "
                + "and never skipped, as the game does");
        }
    }

    private static void ClosureWorker()
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double cpuStart = ThreadCpuMs();
            Log.Info($"[{ModEntry.Id}] {Name}: closures worker started at {DateTime.Now:HH:mm:ss.fff}");
            System.Reflection.Assembly game = typeof(CardModel).Assembly;
            // First the allowed patches' callees (every group waits for them), then each override's closure.
            var calleesOf = new Dictionary<System.Reflection.MethodBase, System.Reflection.MethodBase[]>();
            try
            {
                _allowedCallees = AllowedCallees(out calleesOf);
            }
            catch (Exception e)
            {
                Log.Info($"[{ModEntry.Id}] {Name}: what the allowed patches on the clone's way call couldn't be read ({e.Message}): stacked enchantments' "
                    + "inner cards are rebuilt at every clone and never skipped, as the game does");
            }
            System.Threading.Interlocked.Increment(ref _closuresDone);
            System.Threading.Interlocked.Increment(ref _closureGeneration);
            var chain = ChainMethods();
            var chainMethods = new HashSet<System.Reflection.MethodBase>(chain.Select(c => c.Method));
            var graph = new CalleeGraph(game);
            var walks = new Dictionary<string, ClosureWalk?>();
            foreach (string key in GameOverrides.Keys.Concat(KnownOverrides.Keys))
            {
                int split = key.IndexOf("::", StringComparison.Ordinal);
                Type? type = game.GetType(key[..split], false) ?? Reflect.TypeByName(key[..split]);
                System.Reflection.MethodInfo? method = type?.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly).SingleOrDefault(m => m.Name == key[(split + 2)..] && m.GetParameters().Length == 0);
                walks[key] = method == null ? null : new ClosureWalk(graph, method, ClosureLimit);
            }
            bool first = true;
            while (true)
            {
                // A walk stops at a method another mod patches (not a chain method: judged with its allowed patches). Asked without
                // recording a back-off (review C7): no content may use this override; a content asking records it (Patched, the
                // blocker check).
                Dictionary<(System.Reflection.Module, int), System.Reflection.MethodBase[]>? patchedNow = PatchedMethods();
                bool Patched(System.Reflection.MethodBase m) =>
                    patchedNow != null && !chainMethods.Contains(m) && patchedNow.TryGetValue((m.Module, m.MetadataToken), out System.Reflection.MethodBase[]? instances)
                    && instances.Any(PatchSafety.HasOtherPatches);
                foreach (string key in walks.Keys.ToArray())
                {
                    ClosureWalk? walk = walks[key];
                    object published = _noClosure;
                    _closures.TryGetValue(key, out object? before);
                    if (before is OverrideClosure || (walk == null && before != null))
                    {
                        continue; // Complete, or not bounded: nothing left to walk.
                    }
                    try
                    {
                        if (walk != null)
                        {
                            if (walk.Advance(Patched))
                            {
                                // The chain methods reached, with what their allowed patches call (C3: those patches run with the
                                // closure's code, whatever groups the content reaches).
                                var known = new HashSet<System.Reflection.MethodBase>(walk.Found.Where(chainMethods.Contains));
                                List<System.Reflection.MethodBase> viaAllowed = AllowedCalleesIn(known, chainMethods, calleesOf);
                                published = new OverrideClosure(walk.Found.Where(m => !chainMethods.Contains(m)).Concat(viaAllowed).Distinct().ToArray(),
                                    chain.Where(c => known.Contains(c.Method)).Select(c => (c.Method, c.Allowed)).ToArray());
                            }
                            else
                            {
                                published = new BlockedClosure(walk.Blocker!);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        walks[key] = null;
                        Log.Info($"[{ModEntry.Id}] {Name}: what {key} calls couldn't be bounded ({e.Message}): its types' stacked inner cards are rebuilt "
                            + "at every clone and never skipped, as the game does");
                    }
                    if (before is BlockedClosure { } still && published is BlockedClosure { } now && ReferenceEquals(still.Blocker, now.Blocker))
                    {
                        continue; // Still stopped at the same patch: nothing changed (the stamp stays).
                    }
                    _closures[key] = published;
                    System.Threading.Interlocked.Increment(ref _closureGeneration);
                    if (first)
                    {
                        System.Threading.Interlocked.Increment(ref _closuresDone);
                    }
                }
                if (first)
                {
                    first = false;
                    double cpu = ThreadCpuMs();
                    Log.Info($"[{ModEntry.Id}] {Name}: closures worker done at {DateTime.Now:HH:mm:ss.fff}: {watch.ElapsedMilliseconds} ms wall, "
                        + (cpu >= 0 && cpuStart >= 0 ? $"{cpu - cpuStart:0} ms CPU" : "CPU unknown") + $", {graph.Read} methods read; " + ClosureSummary());
                }
                _resume.WaitOne();
            }
        }
        catch (Exception e)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: the closures' worker stopped ({e.Message}): stacked enchantments' inner cards are rebuilt at every clone "
                + "for the types not judged yet, as the game does");
        }
    }

    /// <summary>Each override's state (display, the worker's log line and the bench).</summary>
    private static string ClosureSummary() => string.Join(", ", _closures.OrderBy(e => e.Key, StringComparer.Ordinal)
        .Select(e => e.Key[(e.Key.LastIndexOf('.') + 1)..] + e.Value switch
        {
            OverrideClosure c => " " + (c.Methods.Length + c.Known.Length) + " methods",
            BlockedClosure b => " stopped at " + MethodFingerprint.MemberName(b.Blocker) + " (patched by another mod)",
            _ => " not bounded",
        }));

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>This thread's CPU time (Windows), -1 elsewhere or when it can't tell (the log line only).</summary>
    private static double ThreadCpuMs()
    {
        try
        {
            return OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? (kernel + user) / 10000.0 : -1;
        }
        catch
        {
            return -1;
        }
    }

    private sealed record PatchedSet(long Stamp, long CheckedAt, Dictionary<(System.Reflection.Module, int), System.Reflection.MethodBase[]>? Methods);

    private static volatile PatchedSet? _patchedSet;

    /// <summary>
    /// How long an answer made for one patch stamp stands (<see cref="PatchedMethods"/>, <see cref="OverrideClosure"/>'s strict answer):
    /// as PatchSafety's own answers, which these are built from. A stamp alone would keep for good an answer PatchSafety gave from a
    /// cache filled just before a patch landed (a check on another thread reading Harmony before the patch, storing after it).
    /// </summary>
    private const long RecheckMs = 2000;

    /// <summary>
    /// The methods Harmony lists as patched now, by module and token, each key with the MethodBase objects Harmony holds for it: a
    /// generic member's closed instantiations share their definition's key (the closures and the chain name definitions, while Harmony
    /// patches, and GetPatchInfo answers for, a closed instantiation's own MethodBase). Per patch stamp and for <see cref="RecheckMs"/>
    /// at most; null when Harmony can't tell (then everything counts as patched). Read and replaced whole: any thread.
    /// </summary>
    private static Dictionary<(System.Reflection.Module, int), System.Reflection.MethodBase[]>? PatchedMethods()
    {
        long stamp = PatchStamp(), now = Environment.TickCount64;
        PatchedSet? known = _patchedSet;
        if (known == null || known.Stamp != stamp || now - known.CheckedAt >= RecheckMs)
        {
            Dictionary<(System.Reflection.Module, int), System.Reflection.MethodBase[]>? methods;
            try
            {
                methods = Harmony.GetAllPatchedMethods().GroupBy(m => (m.Module, m.MetadataToken)).ToDictionary(g => g.Key, g => g.ToArray());
            }
            catch
            {
                methods = null;
            }
            _patchedSet = known = new PatchedSet(stamp, now, methods);
        }
        return known.Methods;
    }

    /// <summary>
    /// For a member of a generic definition (CardCmd.Enchant&lt;T&gt;, ModelDb.Card&lt;T&gt;, a generic type's method), as the chain and
    /// the closures name them: whether one of the instantiations Harmony patches answers yes to <paramref name="patched"/> (review C2:
    /// asked on the definition, Harmony sees none of them). False for any other member, which the caller asks about itself; true when
    /// Harmony can't tell.
    /// </summary>
    private static bool InstancePatched(System.Reflection.MethodBase method, Func<System.Reflection.MethodBase, bool> patched)
    {
        if (!method.IsGenericMethodDefinition && method.DeclaringType is not { IsGenericTypeDefinition: true })
        {
            return false;
        }
        if (PatchedMethods() is not { } patchedNow)
        {
            return true;
        }
        if (patchedNow.TryGetValue((method.Module, method.MetadataToken), out System.Reflection.MethodBase[]? instances))
        {
            foreach (System.Reflection.MethodBase instance in instances)
            {
                if (!instance.Equals(method) && patched(instance))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether one of these overrides on the way (<see cref="OnTheWay"/>) is patched, or what it calls (<see cref="OverrideClosure"/>,
    /// strictly whatever the inner card's type: review C6), or its closure isn't known (yet).
    /// </summary>
    internal static bool OverridesPatched(System.Reflection.MethodInfo[] overrides)
    {
        if (overrides.Length == 0)
        {
            return false;
        }
        // Asked per inner card by the filters: the answer for this set of overrides (OnTheWay's array, fixed per type) is kept
        // while the patch stamp holds, for at most a second (as the chain's answer).
        long stamp = PatchStamp(), second = Environment.TickCount64 / 1000;
        if (_overridesPatched.TryGetValue(overrides, out var known) && known.Stamp == stamp && known.Second == second)
        {
            return known.Patched;
        }
        bool patched = OverridesPatchedNow(overrides);
        _overridesPatched[overrides] = (stamp, second, patched);
        return patched;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, (long Stamp, long Second, bool Patched)> _overridesPatched = new();

    private static bool OverridesPatchedNow(System.Reflection.MethodInfo[] overrides)
    {
        if (AnyPatched(overrides))
        {
            return true;
        }
        foreach (System.Reflection.MethodInfo method in overrides)
        {
            _closures.TryGetValue(method.DeclaringType?.FullName + "::" + method.Name, out object? known);
            if (known is BlockedClosure blocked && !PatchSafety.IsPatchedByOthers(blocked.Blocker) && !InstancePatched(blocked.Blocker, m => PatchSafety.IsPatchedByOthers(m)))
            {
                // The patch it stopped at went away: the worker walks on (patched until it publishes).
                _resume.Set();
                return true;
            }
            if (known is not OverrideClosure closure || closure.Patched())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Changes whenever a judgment above may (a Harmony change, every 2 s without HarmonyWatch, a closure worked out): the memo is
    /// cleared and LimitlessHookSkip's filters judge again, so nothing kept before a patch arrives is served after it.
    /// </summary>
    internal static long PatchStamp() =>
        unchecked((HarmonyWatch.Applied ? HarmonyWatch.Epoch : Environment.TickCount64 / 2000) * 1_048_576L + System.Threading.Interlocked.Read(ref _closureGeneration));

    private static Type? _storageCardType;
    private static int _storageLooked;

    /// <summary>Limitless's EnchantmentStorageCard (the type of every inner card), or null.</summary>
    internal static Type? StorageCardType
    {
        get
        {
            if (System.Threading.Volatile.Read(ref _storageLooked) == 0)
            {
                _storageCardType = Reflect.TypeByName("Limitless.LimitlessCode.UncappedEnchantments.EnchantmentStorageCard");
                System.Threading.Volatile.Write(ref _storageLooked, 1);
            }
            return _storageCardType;
        }
    }

    /// <summary>The chain groups a saved card's build reaches (<see cref="Reach"/>).</summary>
    internal static Reach ReachOf(SerializableCard saved, Type cardType)
    {
        Reach reach = Reach.Spine;
        if (saved.Enchantment != null)
        {
            reach |= Reach.Enchant;
            // Limitless's pinned set_Enchantment prefix calls CardCmd.Enchant only for a card that isn't exactly its storage card.
            if (cardType != StorageCardType)
            {
                reach |= Reach.SecondEnchant;
            }
        }
        if (saved.CurrentUpgradeLevel > 0)
        {
            reach |= Reach.Upgrade;
        }
        return reach;
    }

    /// <summary>The card and enchantment type each saved id makes, for any thread (LimitlessHookSkip's filters).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ModelId, Type> _cardTypeById = new(), _enchantmentTypeById = new();

    /// <summary>
    /// LimitlessHookSkip's per inner card question (rule L): whether building this saved inner card may reach another mod's code
    /// no rule lets stand: the chain groups it reaches, its card type's and its enchantment type's overrides on the way with what
    /// they call (the chain's gates for its card type; the closures strictly). <see cref="KnownTypes"/> asks the same for the memo,
    /// with main-thread caches.
    /// </summary>
    internal static bool ContentPatched(SerializableCard saved)
    {
        // The spine first (review C7): it holds SaveUtil.CardOrDeprecated, which the type lookup below calls (once per id, maybe off
        // the main thread): with another mod's patch there, that patch would run once more than in the game.
        if (saved.Id is not { } cardId || ChainPatched())
        {
            return true;
        }
        Type cardType = _cardTypeById.GetOrAdd(cardId, static id => MegaCrit.Sts2.Core.Saves.SaveUtil.CardOrDeprecated(id).GetType());
        if (ChainPatchedFor(cardType, ReachOf(saved, cardType)) || OnTheWay(cardType) is not { } overrides || OverridesPatched(overrides))
        {
            return true;
        }
        if (saved.Enchantment == null)
        {
            return false;
        }
        if (saved.Enchantment.Id is not { } enchantmentId)
        {
            return true;
        }
        Type enchantmentType = _enchantmentTypeById.GetOrAdd(enchantmentId, static id => MegaCrit.Sts2.Core.Saves.SaveUtil.EnchantmentOrDeprecated(id).GetType());
        return OnTheWay(enchantmentType) is not { } enchantment || OverridesPatched(enchantment);
    }

    private static readonly Dictionary<ModelId, System.Reflection.MethodInfo[]?> _cardTypes = new(), _enchantmentTypes = new();

    /// <summary>
    /// Whether the saved card's own types (what <c>FromSerializable</c> makes of its ids) have only known overrides on the
    /// way, none patched by another mod nor what they call, and no group of the chain it reaches patched (rule L: per content).
    /// Main thread (the prefix's stamp checks it).
    /// </summary>
    private static bool KnownTypes(SerializableCard saved)
    {
        if (saved.Id is not { } cardId || OnTheWayOf(_cardTypes, cardId, CardTypeOf) is not { } card)
        {
            return false;
        }
        Type cardType = _cardTypeOf[cardId];
        if (OverridesPatched(card) || ChainPatchedFor(cardType, ReachOf(saved, cardType)))
        {
            return false;
        }
        if (saved.Enchantment == null)
        {
            return true;
        }
        return saved.Enchantment.Id is { } enchantmentId
            && OnTheWayOf(_enchantmentTypes, enchantmentId, static id => MegaCrit.Sts2.Core.Saves.SaveUtil.EnchantmentOrDeprecated(id).GetType()) is { } enchantment
            && !OverridesPatched(enchantment);
    }

#if BENCH
    /// <summary>
    /// The bench's view of the per content judgment (foreignpatch limitless reach): whether a saved card of that id, enchantment
    /// and upgrade level would be kept by the memo now (stamp aside), and the groups it reaches.
    /// </summary>
    internal static (bool Kept, Reach Reach) KeptForTest(SerializableCard saved) =>
        (KnownTypes(saved), saved.Id is { } id ? ReachOf(saved, MegaCrit.Sts2.Core.Saves.SaveUtil.CardOrDeprecated(id).GetType()) : Reach.None);

    /// <summary>Whether the allowed patches' callees and every pinned override's closure were worked out (the bench waits for it).</summary>
    internal static bool ClosuresDone => System.Threading.Volatile.Read(ref _closuresDone) >= GameOverrides.Count + KnownOverrides.Count + 1;

    /// <summary>The closures' states, by override (display: <c>foreignpatch limitless reach</c>).</summary>
    internal static string ClosureStates() => ClosureSummary() + $"; allowed callees {(_allowedCallees is { } callees ? callees.Length.ToString() : "unknown")}";

    /// <summary>Contents kept now, and memo clears because the patch set changed (rule L's stamp) while it held some.</summary>
    internal static int MemoCount => _memo.Count;
    public static long PatchStampClears;
#endif

    /// <summary>The card type each saved id makes (filled by <see cref="CardTypeOf"/> before its overrides are cached). Main thread.</summary>
    private static readonly Dictionary<ModelId, Type> _cardTypeOf = new();

    private static Type CardTypeOf(ModelId id)
    {
        Type type = MegaCrit.Sts2.Core.Saves.SaveUtil.CardOrDeprecated(id).GetType();
        _cardTypeOf[id] = type;
        return type;
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
        Reflect.Method(patch) is { } method && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint) ? method : null;

    private static Func<bool>? _uncappedEnchantments, _uncappedUpgrades;

    /// <summary>Limitless's options its patches on the chain read (fixed in a run, from the config outside one).</summary>
    private static long OptionsStamp()
    {
        if (_uncappedEnchantments == null)
        {
            Type context = Reflect.TypeByName("Limitless.LimitlessCode.Config.ContextManager") ?? throw new MissingMemberException("Limitless ContextManager");
            _uncappedEnchantments = AccessTools.MethodDelegate<Func<bool>>(Reflect.PropertyGetter(context, "UncappedEnchantmentsEnabled")
                ?? throw new MissingMemberException("ContextManager", "UncappedEnchantmentsEnabled"));
            _uncappedUpgrades = AccessTools.MethodDelegate<Func<bool>>(Reflect.PropertyGetter(context, "UncappedUpgradesEnabled")
                ?? throw new MissingMemberException("ContextManager", "UncappedUpgradesEnabled"));
        }
        return (_uncappedEnchantments() ? 1L : 0L) << 61 | (_uncappedUpgrades!() ? 1L : 0L) << 60;
    }

    /// <summary>The stamp memos are valid for, or -1 when this can't answer (then the lambda runs and nothing is kept).</summary>
    private static long Stamp()
    {
        if (!Enabled || ListenerCache.IsSuspended || LimitlessReserialize.Faulted || !LimitlessReserialize.CanCompareHere || !GameChainKnown() || LimitlessReserialize.PatchedByOthers()
            // No tolerance: the lambda is skipped for a kept content.
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
            // Rule L: also the patch set (PatchStamp), so an entry made before a patch arrives is never served after it.
            long patches = PatchStamp();
            if (stamp != _memoStamp || patches != _memoPatchStamp)
            {
#if BENCH
                if (patches != _memoPatchStamp && _memo.Count > 0)
                {
                    PatchStampClears++;
                }
#endif
                _memo.Clear();
                _memoStamp = stamp;
                _memoPatchStamp = patches;
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
#if BENCH
                // Test: compared with another content's kept card (the check must see it differ). Taken only once such a card is
                // kept (one content alone: nothing to compare with, the take would prove nothing).
                if (TestWrong.IsArmed("clonememo") && _memo.Values.FirstOrDefault(other => !ReferenceEquals(other, memo) && !SameBytes(other, __result)) is { } wrongMemo
                    && TestWrong.Take("clonememo"))
                {
                    memo = wrongMemo;
                }
#endif
                if (!SameBytes(memo, __result))
                {
                    Fail($"a kept inner card differs from a rebuilt one ({__0.Id})");
                }
                return;
            }
            if (_memo.Count >= MaxContents)
            {
                _memo.Clear();
#if BENCH
                MemoClears++;
#endif
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
