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
    };
}
