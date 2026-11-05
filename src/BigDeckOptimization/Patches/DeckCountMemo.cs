using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// What's counted (tags, star cost) is fixed in a card, none of the three reads anything else. Squeeze leaves itself out:
/// <see cref="PileIndex"/> says whether it's in those piles, and when unsure the game's code runs. One answer in
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

    private sealed class Counts
    {
        public readonly long[] Version = { -1, -1, -1 };
        public readonly int[] Count = new int[3];
    }

    /// <summary>Tied to the combat's piles: nothing here outlives them.</summary>
    private static readonly ConditionalWeakTable<PlayerCombatState, Counts> _counts = new();
    private static readonly MethodBase?[] _targets = new MethodBase?[3];
    private static int _untilCheck;

    public static void Apply(Harmony harmony)
    {
        bool squeeze = Card(SqueezeKind, typeof(Squeeze), nameof(SqueezePrefix), harmony, SqueezeFingerprint, SqueezeCountsFingerprint);
        bool strike = Card(StrikeKind, typeof(PerfectedStrike), nameof(StrikePrefix), harmony, StrikeFingerprint, StrikeCountsFingerprint);
        bool spear = Card(SpearKind, typeof(CrescentSpear), nameof(SpearPrefix), harmony, SpearFingerprint, SpearCountsFingerprint);
        if (!squeeze && !strike && !spear)
        {
            throw new PatchSkippedException("none of its cards has the known code");
        }
        Applied = true;
    }

    /// <summary>One card, on its own: a card that changed only leaves that card to the game.</summary>
    private static bool Card(int kind, Type card, string prefix, Harmony harmony, string fingerprint, string countsFingerprint)
    {
        try
        {
            MethodInfo multiplier = Lambda(card, typeof(decimal));
            PatchSafety.RequireFingerprint(multiplier, fingerprint);
            PatchSafety.RequireFingerprint(Lambda(card, typeof(bool)), countsFingerprint);
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

    private static bool Answer(int kind, CardModel card, ref decimal result, out decimal? expected)
    {
        expected = null;
        if (!Enabled || !FastListeners.PilesKnown || PatchSafety.IsPatchedByOthers(_targets[kind]))
        {
            return true;
        }
        try
        {
            if (card.Owner.PlayerCombatState is not { } state)
            {
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
            int count = Count(kind, state) - own;
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

    private static int Count(int kind, PlayerCombatState state)
    {
        Counts counts = _counts.GetOrCreateValue(state);
        long version = FastListeners.PileVersion;
        if (counts.Version[kind] == version)
        {
            Kept++;
            return counts.Count[kind];
        }
        IReadOnlyList<CardPile> piles = state.AllPiles;
        int count = counts.Count[kind];
        if (counts.Version[kind] >= 0 && FastListeners.ApplyMoves(counts.Version[kind], piles, _counted[kind], ref count))
        {
            Updated++;
        }
        else
        {
            Walks++;
            count = 0;
            Func<CardModel, bool> counted = _counted[kind];
            foreach (CardPile pile in piles)
            {
                IReadOnlyList<CardModel> cards = pile.Cards;
                for (int i = 0; i < cards.Count; i++)
                {
                    if (counted(cards[i]))
                    {
                        count++;
                    }
                }
            }
        }
        counts.Count[kind] = count;
        counts.Version[kind] = version;
        return count;
    }

    private static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled, the game counts its cards: {e}");
    }
}
