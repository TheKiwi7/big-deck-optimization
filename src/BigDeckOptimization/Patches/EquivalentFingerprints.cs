// Game code of other versions checked to behave exactly like the code patches expect: keys are the expected
// fingerprints (game v0.107.1), values the fingerprints accepted instead. Read by PatchSafety.FingerprintMatches.
// Fingerprints already ignore the compiler's numbering of generated members (MethodFingerprint), so only real code
// changes land here, each reviewed by hand (tools/fpdiff review prints the diff and the new callees) and saying what changed.
using System.Collections.Generic;

namespace BigDeckOptimization.Patches;

public static partial class PatchSafety
{
    private static readonly Dictionary<string, string[]> EquivalentFingerprints = new()
    {
        // CardPile.RemoveInternal, game v0.111 (beta): `this.CardRemoved?.Invoke(card)` moved into the new public
        // InvokeCardRemoved(card), which does just that; same _cards writes as before. Used by PileIndex and FastListeners.
        ["7CF07A4BBD094E41"] = new[] { "33F987AD6ED5E516" },

        // SavedProperties.FromInternal, game v0.111 (beta): its saved-property lookup moved from
        // SavedPropertiesTypeCache.GetJsonPropertiesForType to ModelIdSerializationCache.GetJsonPropertiesForType (the
        // caches merged; same lookup, which also throws if used before the game initialized it); nothing else changed.
        // LimitlessReserialize calls whichever exists (JsonPropertiesLookup).
        ["F73EEC2208D6E517"] = new[] { "B8EF45DA081344A5" },

        // CombatManager.SetupPlayerTurn (state machine), game v0.111 (beta): takes a CombatTurnState and reads the combat
        // state and cancellation token from it instead of CombatManager's fields; null check and warning text simplified.
        // The first-turn block ExtraScopes wraps (its two ToList calls over the same two lambdas, Innate and
        // bottom-of-pile filters) is unchanged.
        ["98A103A1D8204ED3"] = new[] { "0AEAF122C2970558" },

        // CardPile.AddInternal, game v0.113 (beta): a Shoot card entering a combat pile of a player whose character has
        // no base arrow slot and no arrow gets one (ArrowCmd.GainArrowSlot: PlayerCombatState.GainArrow adds an ArrowSlot,
        // not a model, and raises ArrowsChanged, whose only handlers are the Fool's combat UI). The pile still gets the one
        // card, the same _cards write: what PileIndex and FastListeners rely on.
        ["0A80C8EF3B697A4E"] = new[] { "360AAE0A38A7FBA6" },

        // Hook.IterateCombatHookListeners (iterator), game v0.113 (beta): its "combat ending" check is now
        // CombatManager.ShouldSkipCombatEffect, which is `IsOverOrEnding && !IsStarting`, the expression it replaced.
        // HookSkip's CombatHookFiltered keeps that expression.
        ["B3B83A82B69E17FC"] = new[] { "07DE71D51C8FADF8" },

        // NCardFlyShuffleVfx.PlayAnim (state machine), game v0.113 (beta): TweenHelper.AwaitFinished takes a
        // CancellationToken (default here) and returns a TweenOutcome instead of a bool, a result PlayAnim discards.
        // The soul still lands, plays and frees itself as before: what CardFlightLimit relies on.
        ["8986E87813FC7715"] = new[] { "C90B71D2B9C78A77" },

        // NHoverTipSet.Init, game v0.113 (beta): each text tip control gets ApplyRtlLayout (right-to-left layout when the
        // language is). HoverTipReuse only reuses a panel built for identical tips; a language change changes their text.
        ["CF7925571216B6E1"] = new[] { "A0C3581A09BB9658" },

        // NClickableControl.CheckMouseDragThreshold, game v0.113 (beta): compares squared distances (same result, the
        // threshold being positive there). Still only acts on a pressed button: what IdleButtonInput relies on.
        ["60208EA15DC82512"] = new[] { "C7E455CD4FA9F327" },

        // BaseLib 3.4.8 ModifyHealAmountPatches.ModifyAdditive / ModifyMultiplicative: the combat state parameter is
        // typed ICombatState instead of object, and the walk calls runState.IterateHookListeners(combatState) directly
        // instead of through BetaMainCompatibility's reflection wrapper (whose null result fell back to an empty array:
        // never null here). Still one RunState walk per method, the one HookSkip's BaseLibHooks prefix filters.
        ["895AD94736FEED8E"] = new[] { "CE826596CCBABDCE" },
        ["98B29C743F42C15F"] = new[] { "275DEC0945924F89" },
    };
}
