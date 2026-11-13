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
/// Limitless 1.1.7's <c>MultiEnchantment.DeepCloneFields</c> rebuilds every saved inner card when a card is cloned:
/// <c>CardModel.FromSerializable(c).MutableClone().ToSerializable()</c>, for every card of the deck at each combat start
/// (~210 ms with 2 x 10,000 cards). That result depends only on the saved card's content, never changed once made (no code of the
/// game, Limitless or BaseLib writes to a <c>SerializableCard</c> after making it), and on BaseLib's registered saves
/// (<see cref="LimitlessReserialize.MemoStamp"/>; -1 when unknown, then always rebuilt). The result made for a content is given for the
/// same content again, read-only like any saved card: a big deck holds few different inner cards, a first combat gains as much as the
/// next ones, and so do other players' decks, rebuilt as new objects at each co-op combat. A saved card with real BaseLib data isn't kept.
///
/// One answer in <see cref="VerifyEvery"/> is rebuilt and both serialized: a byte difference turns it off for the session.
/// </summary>
public static class LimitlessCloneMemo
{
    public const string Name = "LimitlessCloneMemo";
    internal static int VerifyEvery { get; set; } = 256;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Reused { get; private set; }
    public static long Verified { get; private set; }

    /// <summary>Limitless 1.1.7: the method and the lambda it maps each saved inner card with.</summary>
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
        PatchGuard.Patch(harmony, _lambda, prefix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(Prefix)),
            postfix: new HarmonyMethod(typeof(LimitlessCloneMemo), nameof(Postfix)));
        Applied = true;
    }

    /// <summary>
    /// What a memo hit skips besides ToSerializable (LimitlessReserialize watches that): the lambda's
    /// <c>CardModel.FromSerializable</c> (the card's creation, its saved properties, its enchantment and upgrades) and
    /// <c>MutableClone</c> (its dynamic variables' copies). The known patches there only shape the clone, which the lambda
    /// serializes and drops, from the saved card, the loaded types (in the stamp) and Limitless's uncapped options (in the
    /// stamp): BaseLib's extended data, SavedSpireField, modifier, resource and dynamic variable patches, Limitless's
    /// enchantment, upgrade and status scaling ones (fingerprinted). Any other patch there: no memo.
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
    };

    private static (System.Reflection.MethodBase Method, System.Reflection.MethodInfo[] Allowed)[]? _chain;
    private static long _chainCheckedAt = long.MinValue / 2;
    private static bool _chainPatched;

    /// <summary>Asked per stacked card: answered from a result rechecked each second.</summary>
    private static bool ChainPatched()
    {
        long now = Environment.TickCount64;
        if (now - _chainCheckedAt < 1000)
        {
            return _chainPatched;
        }
        _chainCheckedAt = now;
        // Every overload of each method (FromSerializable, Fill): any of them patched counts. Plus the overrides the clone
        // reaches in card and enchantment types (their own code, no known patch).
        _chain ??= Chain.SelectMany(c => c.Type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => m.Name == c.Method)
                .Select(m => ((System.Reflection.MethodBase)m, c.Allowed.Select(a => Allowed(a.Patch, a.Fingerprint)).OfType<System.Reflection.MethodInfo>().ToArray())))
            .Concat(Overrides().Select(m => (m, Array.Empty<System.Reflection.MethodInfo>())))
            .ToArray();
        _chainPatched = false;
        foreach ((System.Reflection.MethodBase method, System.Reflection.MethodInfo[] allowed) in _chain)
        {
            if (PatchSafety.IsPatchedByOthers(method, allowed))
            {
                _chainPatched = true;
                break;
            }
        }
        return _chainPatched;
    }

    private static readonly string[] OverriddenOnTheWay = { "AfterDeserialized", "DeepCloneFields", "ModifyCard" };

    /// <summary>The overrides of the chain's virtuals in the game's and mods' card and enchantment types.</summary>
    private static IEnumerable<System.Reflection.MethodBase> Overrides()
    {
        string game = typeof(CardModel).Assembly.GetName().Name!;
        var found = new List<System.Reflection.MethodBase>();
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type?[] types;
            try
            {
                if (assembly.IsDynamic || (assembly != typeof(CardModel).Assembly && !assembly.GetReferencedAssemblies().Any(r => r.Name == game)))
                {
                    continue;
                }
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            catch
            {
                continue;
            }
            foreach (Type? type in types)
            {
                if (type == null || type == typeof(CardModel) || type == typeof(EnchantmentModel)
                    || !(typeof(CardModel).IsAssignableFrom(type) || typeof(EnchantmentModel).IsAssignableFrom(type)))
                {
                    continue;
                }
                found.AddRange(type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
                    .Where(m => OverriddenOnTheWay.Contains(m.Name) && m.GetParameters().Length == 0 && m.GetBaseDefinition() != m));
            }
        }
        found.AddRange(typeof(EnchantmentModel).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "DeepCloneFields"));
        return found;
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
        if (!Enabled || ListenerCache.IsSuspended || LimitlessReserialize.Faulted || !LimitlessReserialize.CanCompareHere || LimitlessReserialize.PatchedByOthers()
            || PatchSafety.IsPatchedByOthers(_lambda) || ChainPatched())
        {
            return -1;
        }
        long stamp = LimitlessReserialize.MemoStamp();
        return stamp < 0 ? -1 : stamp | OptionsStamp();
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
