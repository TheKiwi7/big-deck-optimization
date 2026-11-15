using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Squeeze, Perfected Strike and Crescent Spear count every card of their owner's combat piles each time their value is
/// calculated: for each of them in the hand, at each refresh and preview (~0.35 ms at 10,000 cards, several times per play).
///
/// The count is kept per player and per card, as of a <see cref="FastListeners.PileVersion"/>. When every pile change since is
/// in <see cref="FastListeners"/>' moves, those are applied to it; otherwise the piles are counted again, as the game does.
/// What's counted (tags, star cost) must be fixed in a card: each card type's getters are read once (<see cref="FlagsOf"/>), and
/// while a card whose type may answer differently over time is in the piles (a content mod's card computing its tags or star
/// cost from its state), or is the asking card, the game counts. None of the three reads anything else. Squeeze leaves itself
/// out: <see cref="PileIndex"/> says whether it's in those piles, and when unsure the game's code runs. One answer in
/// <see cref="VerifyEvery"/> lets the game's code run and compares; a difference turns this off.
/// </summary>
public static class DeckCountMemo
{
    public const string Name = "DeckCountMemo";

    /// <summary>v0.107.1 code mirrored: each card's multiplier and what it counts.</summary>
    private const string SqueezeFingerprint = "44A0327D171704CF";
    private const string SqueezeCountsFingerprint = "8E1D145CE41BBD4F";
    private const string StrikeFingerprint = "5763108E0FCB3099";
    private const string StrikeCountsFingerprint = "E8CCC3F56A47163F";
    private const string SpearFingerprint = "21EE28A0E953DF12";
    private const string SpearCountsFingerprint = "DA71AF536A9834A6";
    /// <summary>
    /// <c>CardModel.Tags</c>: <c>_tags ?? (_tags = CanonicalTags)</c> (v0.107.1, v0.111, v0.113): a card's tags are its type's
    /// <c>CanonicalTags</c>, kept from the first read, so fixed when that getter returns a constant.
    /// </summary>
    private const string BaseTagsFingerprint = "63319788A75CA671";
    /// <summary>
    /// <c>PlayerCombatState.AllCards</c>: <c>AllPiles.SelectMany(p =&gt; p.Cards)</c> (v0.107.1, v0.111, v0.113), what the game counts;
    /// the memo counts <c>AllPiles</c> itself (same constant as <c>LimitlessRecalcSkip</c>).
    /// </summary>
    private const string AllCardsFingerprint = "7893F6315D894A5A";

    private const int SqueezeKind = 0;
    private const int StrikeKind = 1;
    private const int SpearKind = 2;

#if BENCH
    internal static int VerifyEvery { get; set; } = 16;
#else
    internal static int VerifyEvery { get; set; } = 256;
#endif

#if BENCH
    /// <summary>Positive control (<c>bdperf deckcount wrong</c>): added to every answer.</summary>
    internal static int WrongForTest { get; set; }
#endif

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Kept { get; private set; }
    public static long Updated { get; private set; }
    public static long Walks { get; private set; }
    public static long Unsure { get; private set; }
    public static long Checks { get; private set; }
    public static long Mismatches { get; private set; }

    private static readonly Func<CardModel, bool>[] _counted =
    {
        c => c.Tags.Contains(CardTag.OstyAttack),
        c => c.Tags.Contains(CardTag.Strike),
        c => c.CanonicalStarCost >= 0 || c.HasStarCostX,
    };

    /// <summary>What may change over a card's life, per type (<see cref="FlagsOf"/>): its tags, its star cost.</summary>
    private const byte TagsMove = 1;
    private const byte StarMove = 2;
    private static readonly byte[] _moveMask = { TagsMove, TagsMove, StarMove };

    /// <summary>Per kind, the cards whose type may change what that kind counts: only their type is read, nothing of the card.</summary>
    private static readonly Func<CardModel, bool>[] _moving =
    {
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & TagsMove) != 0,
        c => (FlagsOf(c.GetType()) & StarMove) != 0,
    };

    private sealed class Counts
    {
        public readonly long[] Version = { -1, -1, -1 };
        public readonly int[] Count = new int[3];
        /// <summary>Cards in the piles of a type that may change what the kind counts: while any, the game counts.</summary>
        public readonly int[] Moving = new int[3];
        /// <summary><see cref="_flagsEpoch"/> the counts were taken with: the moving counts depend on the type flags.</summary>
        public readonly long[] Epoch = new long[3];
    }

    /// <summary>Tied to the combat's piles: nothing here outlives them.</summary>
    private static readonly ConditionalWeakTable<PlayerCombatState, Counts> _counts = new();
    private static readonly MethodBase?[] _targets = new MethodBase?[3];
    /// <summary>Per card, what a kept count skips calling: the count lambda, the piles' card list, what it reads of each card.</summary>
    private static readonly MethodBase?[][] _skipped = new MethodBase?[3][];
    private static int _untilCheck;

    private static MethodInfo? _tagsGetter, _canonicalTagsGetter, _starCostGetter, _starXGetter;
    private static bool _baseTagsKnown;

    public static void Apply(Harmony harmony)
    {
        MethodBase? allCards = AccessTools.PropertyGetter(typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState), "AllCards");
        // What the three cards count is AllCards; the memo walks AllPiles: only while AllCards is that walk (else all three vanilla).
        PatchSafety.RequireFingerprint(allCards ?? throw new PatchSkippedException("PlayerCombatState.AllCards not found"), AllCardsFingerprint);
        // The game's count reads each pile through CardPile.Cards: another mod answering another list there gets the game's count.
        MethodBase? pileCards = AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards));
        _tagsGetter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Tags));
        _canonicalTagsGetter = AccessTools.PropertyGetter(typeof(CardModel), "CanonicalTags");
        _starCostGetter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.CanonicalStarCost));
        _starXGetter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.HasStarCostX));
        try
        {
            // Kept from the first read: only while nothing else writes the kept set.
            PatchSafety.RequireFingerprint(_tagsGetter ?? throw new MissingMethodException("CardModel.Tags"), BaseTagsFingerprint);
            PatchSafety.RequireFieldWriters(typeof(CardModel), "_tags", "get_Tags");
            _baseTagsKnown = true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            _baseTagsKnown = false;
            Log.Info($"[{ModEntry.Id}] {Name}: cards without their own Tags counted by the game: {e.Message}");
        }
        _readers[SqueezeKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        _readers[StrikeKind] = new[] { allCards, pileCards, _tagsGetter, _canonicalTagsGetter };
        _readers[SpearKind] = new[] { allCards, pileCards, _starCostGetter, _starXGetter };
        bool squeeze = Card(SqueezeKind, typeof(Squeeze), nameof(SqueezePrefix), harmony, SqueezeFingerprint, SqueezeCountsFingerprint);
        bool strike = Card(StrikeKind, typeof(PerfectedStrike), nameof(StrikePrefix), harmony, StrikeFingerprint, StrikeCountsFingerprint);
        bool spear = Card(SpearKind, typeof(CrescentSpear), nameof(SpearPrefix), harmony, SpearFingerprint, SpearCountsFingerprint);
        if (!squeeze && !strike && !spear)
        {
            throw new PatchSkippedException("none of its cards has the known code");
        }
        Applied = true;
        // What Answer asks about for each card kind patched (display only), and the card types' getters FlagsOf read so far
        // (types met in combats: asked again at each preflight).
        for (int kind = 0; kind < _targets.Length; kind++)
        {
            if (_targets[kind] != null)
            {
                PatchSafety.Watch(Name, _targets[kind]);
                PatchSafety.Watch(Name, _skipped[kind]);
            }
        }
        PatchSafety.WatchAll(Name, () => _watched.Keys.ToArray());
    }

    /// <summary>One card, on its own: a card that changed only leaves that card to the game.</summary>
    private static bool Card(int kind, Type card, string prefix, Harmony harmony, string fingerprint, string countsFingerprint)
    {
        try
        {
            MethodInfo multiplier = Lambda(card, typeof(decimal));
            PatchSafety.RequireFingerprint(multiplier, fingerprint);
            MethodInfo counts = Lambda(card, typeof(bool));
            PatchSafety.RequireFingerprint(counts, countsFingerprint);
            _skipped[kind] = _readers[kind].Append(counts).ToArray();
            PatchGuard.Patch(harmony, multiplier, prefix: new HarmonyMethod(typeof(DeckCountMemo), prefix), postfix: new HarmonyMethod(typeof(DeckCountMemo), nameof(Postfix)));
            _targets[kind] = multiplier;
            return true;
        }
        catch (Exception e) when (e is PatchSkippedException or MissingMemberException)
        {
            Log.Info($"[{ModEntry.Id}] {Name}: {card.Name} left vanilla: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The compiler's method for a lambda of the card's variables, found by what it returns in whichever generated class holds it: their
    /// names carry a number that shifts when the game adds a member, with the same code.
    /// </summary>
    private static MethodInfo Lambda(Type card, Type returns)
    {
        MethodInfo[] found = card.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(holder => holder.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("get_CanonicalVars") && m.ReturnType == returns).ToArray();
        return found.Length == 1 ? found[0] : throw new MissingMethodException(card.Name, "CanonicalVars lambda");
    }

    private static bool SqueezePrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(SqueezeKind, __0, ref __result, out __state);

    private static bool StrikePrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(StrikeKind, __0, ref __result, out __state);

    private static bool SpearPrefix(CardModel __0, ref decimal __result, out decimal? __state) => Answer(SpearKind, __0, ref __result, out __state);

    private static readonly MethodBase?[][] _readers = new MethodBase?[3][];

    /// <summary>Another mod patches what a kept count skips (a missing one counts as patched).</summary>
    private static bool SkippedPatched(int kind)
    {
        foreach (MethodBase? method in _skipped[kind])
        {
            if (PatchSafety.IsPatchedByOthers(method))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Answer(int kind, CardModel card, ref decimal result, out decimal? expected)
    {
        expected = null;
        // Inside ListenerCache.RunVanilla the result is a reference others are compared with: the game's own.
        if (!Enabled || ListenerCache.IsSuspended || !FastListeners.PilesKnown || PatchSafety.IsPatchedByOthers(_targets[kind]) || SkippedPatched(kind))
        {
            return true;
        }
        try
        {
            if (card.Owner.PlayerCombatState is not { } state)
            {
                return true;
            }
            RecheckFlags();
            // The asking card's own tags are read below (Squeeze): only when they can't depend on when they're read.
            if ((FlagsOf(card.GetType()) & _moveMask[kind]) != 0)
            {
                Unsure++;
                return true;
            }
            int own = 0;
            if (kind == SqueezeKind && _counted[SqueezeKind](card))
            {
                own = PileIndex.PlacesIn(card, state.AllPiles);
                if (own < 0)
                {
                    Unsure++;
                    return true;
                }
            }
            int count = Count(kind, state, out bool moving) - own;
            if (moving)
            {
                Unsure++;
                return true;
            }
#if BENCH
            count += WrongForTest;
#endif
            if (--_untilCheck <= 0)
            {
                _untilCheck = VerifyEvery;
                expected = count;
                return true;
            }
            result = count;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    private static void Postfix(decimal __result, decimal? __state)
    {
        if (__state is not { } expected)
        {
            return;
        }
        Checks++;
        if (expected != __result)
        {
            Mismatches++;
            Disable(new InvalidOperationException($"counted {expected}, the game {__result}"));
        }
    }

    /// <summary>The kind's count over the piles, and whether a card of a type that may change it is among them.</summary>
    private static int Count(int kind, PlayerCombatState state, out bool moving)
    {
        Counts counts = _counts.GetOrCreateValue(state);
        long version = FastListeners.PileVersion;
        if (counts.Epoch[kind] != _flagsEpoch)
        {
            // Type flags read again since (another mod's patch came or went): the moving count is the old flags'.
            counts.Version[kind] = -1;
            counts.Epoch[kind] = _flagsEpoch;
        }
        if (counts.Version[kind] == version)
        {
            Kept++;
            moving = counts.Moving[kind] > 0;
            return counts.Count[kind];
        }
        IReadOnlyList<CardPile> piles = state.AllPiles;
        int count = counts.Count[kind];
        int movingCount = counts.Moving[kind];
        // Both from the same moves (MovesSince gives the same answer twice): the count, then the cards of changing types.
        // Only from a count taken with no card of a changing type in the piles: a move is judged when applied, so such a
        // card that entered under one answer and leaves under another would leave the count off once it is gone.
        if (counts.Version[kind] >= 0 && counts.Moving[kind] == 0
            && FastListeners.ApplyMoves(counts.Version[kind], piles, _counted[kind], ref count)
            && FastListeners.ApplyMoves(counts.Version[kind], piles, _moving[kind], ref movingCount))
        {
            Updated++;
        }
        else
        {
            Walks++;
            count = 0;
            movingCount = 0;
            Func<CardModel, bool> counted = _counted[kind];
            byte mask = _moveMask[kind];
            Type? lastType = null;
            byte lastFlags = 0;
            foreach (CardPile pile in piles)
            {
                IReadOnlyList<CardModel> cards = pile.Cards;
                for (int i = 0; i < cards.Count; i++)
                {
                    CardModel card = cards[i];
                    if (counted(card))
                    {
                        count++;
                    }
                    Type type = card.GetType();
                    if (type != lastType)
                    {
                        lastType = type;
                        lastFlags = FlagsOf(type);
                    }
                    if ((lastFlags & mask) != 0)
                    {
                        movingCount++;
                    }
                }
            }
        }
        counts.Count[kind] = count;
        counts.Moving[kind] = movingCount;
        counts.Version[kind] = version;
        moving = movingCount > 0;
        return count;
    }

    // --- Which card types may answer differently over time ---

    /// <summary>Per card type, <see cref="TagsMove"/> and <see cref="StarMove"/>. Main thread only (<see cref="FastListeners.PilesKnown"/>).</summary>
    private static readonly Dictionary<Type, byte> _flags = new();

    /// <summary>Each type's getter whose patches <see cref="FlagsOf"/> read, and what it found (main thread).</summary>
    private static readonly Dictionary<MethodInfo, bool> _watched = new();
    /// <summary>Bumped when <see cref="_flags"/> are read again: counts taken with the old flags walk the piles again.</summary>
    private static long _flagsEpoch;
    private static long _nextFlagsCheck;
    /// <summary>As often as <see cref="PatchSafety"/> sees a patch come or go on a method.</summary>
    private const long FlagsCheckMs = 2000;

    /// <summary>Whether another mod patches the getter, remembered so a later change reads every type's flags again.</summary>
    private static bool Watched(MethodInfo getter)
    {
        bool patched = PatchSafety.IsPatchedByOthers(getter);
        _watched[getter] = patched;
        return patched;
    }

    /// <summary>
    /// A type's flags hold its getters' patches as they were when first read: every <see cref="FlagsCheckMs"/>, those getters are
    /// asked again, and any change reads every type's flags again (a patch on one card type's override, which the base
    /// getters <see cref="SkippedPatched"/> checks don't show).
    /// </summary>
    private static void RecheckFlags()
    {
        long now = Environment.TickCount64;
        if (now < _nextFlagsCheck || _watched.Count == 0)
        {
            return;
        }
        _nextFlagsCheck = now + FlagsCheckMs;
        bool changed = false;
        foreach (KeyValuePair<MethodInfo, bool> watched in _watched)
        {
            if (PatchSafety.IsPatchedByOthers(watched.Key) != watched.Value)
            {
                changed = true;
                break;
            }
        }
        if (changed)
        {
            _flags.Clear();
            _watched.Clear();
            _flagsEpoch++;
            Log.Info($"[{ModEntry.Id}] {Name}: another mod's patch on a card's tags or star cost came or went: card types read again");
        }
    }

    /// <summary>
    /// Read from the type's code once, whichever assembly it comes from (a content mod loaded later included: its cards are
    /// seen when they reach a pile or ask). Tags fixed: the type keeps <c>CardModel.Tags</c> (known code: kept from the first
    /// read of <c>CanonicalTags</c>) and its <c>CanonicalTags</c> returns a constant, or its own <c>Tags</c> returns a constant.
    /// Star cost fixed: its <c>CanonicalStarCost</c> and <c>HasStarCostX</c> return constants. A getter another mod patches
    /// counts as changing. Anything that can't be read: changing.
    /// </summary>
    private static byte FlagsOf(Type type)
    {
        if (_flags.TryGetValue(type, out byte flags))
        {
            return flags;
        }
        try
        {
            MethodInfo? tags = Implementation(type, _tagsGetter);
            if (tags == null || Watched(tags))
            {
                flags |= TagsMove;
            }
            else if (tags.DeclaringType == typeof(CardModel))
            {
                if (!_baseTagsKnown || Implementation(type, _canonicalTagsGetter) is not { } canonical || !ReturnsConstant(canonical)
                    || Watched(canonical))
                {
                    flags |= TagsMove;
                }
            }
            else if (!ReturnsConstant(tags))
            {
                flags |= TagsMove;
            }
            foreach (MethodInfo? star in new[] { _starCostGetter, _starXGetter })
            {
                if (Implementation(type, star) is not { } getter || !ReturnsConstant(getter) || Watched(getter))
                {
                    flags |= StarMove;
                }
            }
        }
        catch (Exception e)
        {
            flags = TagsMove | StarMove;
            Log.Info($"[{ModEntry.Id}] {Name}: {type.Name}'s tags and star cost not read, counted by the game: {e.Message}");
        }
        _flags[type] = flags;
        return flags;
    }

    /// <summary>The getter a virtual call on a card of this type runs: its most derived override (a <c>new</c> one hides nothing from it).</summary>
    private static MethodInfo? Implementation(Type type, MethodInfo? baseGetter)
    {
        if (baseGetter == null)
        {
            return null;
        }
        MethodInfo definition = baseGetter.GetBaseDefinition();
        for (Type? t = type; t != null; t = t.BaseType)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name == baseGetter.Name && m.GetParameters().Length == 0)
                {
                    MethodInfo root = m.GetBaseDefinition();
                    if (root.MetadataToken == definition.MetadataToken && root.Module == definition.Module)
                    {
                        return m;
                    }
                }
            }
            if (t == baseGetter.DeclaringType)
            {
                break;
            }
        }
        return null;
    }

    private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>
    /// Whether the getter gives the same value whenever it's called: it reads neither the card (<c>this</c>, its only
    /// argument) nor any field, and calls only constructors and methods of the collections the game builds such values with
    /// (<c>new HashSet&lt;CardTag&gt; { CardTag.Strike }</c>, the compiler's read-only collections, array initialization). A
    /// lambda (<c>ldftn</c>) or any other call, field or argument: not constant.
    /// </summary>
    private static bool ReturnsConstant(MethodInfo getter)
    {
        MethodBody? body = getter.GetMethodBody();
        if (body == null)
        {
            return false;
        }
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Type[]? typeArgs = getter.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        int pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                if (pos >= il.Length)
                {
                    return false;
                }
                value = (short)(0xFE00 | il[pos++]);
            }
            if (!_opCodes.TryGetValue(value, out OpCode op))
            {
                return false;
            }
            if (op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_S || op == OpCodes.Ldarg || op == OpCodes.Ldarga_S || op == OpCodes.Ldarga
                || op == OpCodes.Starg_S || op == OpCodes.Starg || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn || op == OpCodes.Calli)
            {
                return false;
            }
            switch (op.OperandType)
            {
                case OperandType.InlineField:
                    return false;
                case OperandType.InlineMethod:
                    if (getter.Module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, null) is not { } called || !BuildsConstants(called.DeclaringType))
                    {
                        return false;
                    }
                    pos += 4;
                    break;
                case OperandType.InlineTok:
                    // ldtoken: a type, or an array initializer's data (a field of <PrivateImplementationDetails>).
                    if (getter.Module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, null) is FieldInfo data
                        && data.DeclaringType?.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal) != true)
                    {
                        return false;
                    }
                    pos += 4;
                    break;
                case OperandType.InlineSwitch:
                    pos += 4 + 4 * BitConverter.ToInt32(il, pos);
                    break;
                default:
                    pos += op.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        _ => 4,
                    };
                    break;
            }
        }
        return true;
    }

    /// <summary>The types a constant getter may call into: collections it fills with literals, nothing reading other state.</summary>
    private static bool BuildsConstants(Type? type)
    {
        if (type == null)
        {
            return false;
        }
        return type.Namespace is "System.Collections.Generic" or "System.Collections.ObjectModel"
            || type.Name.StartsWith("<>z__", StringComparison.Ordinal)
            || type == typeof(RuntimeHelpers) || type == typeof(Array);
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, the game counts its cards: {e}");
    }
}
