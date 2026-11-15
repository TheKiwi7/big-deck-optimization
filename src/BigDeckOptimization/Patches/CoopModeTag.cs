using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Co-op players must all have this mod on, or all off. With the whole mod off, a player's combats start at the game's
/// pace while the others' start fast (FastCombatStart is always on in co-op): with many relics that player starts each
/// combat far later, and the game drops the actions reaching a player still starting a combat (desync or a stuck turn,
/// DESIGN "Co-op with a player without the mod").
///
/// The game already refuses a player whose gameplay mods differ from the host's: the client compares the host's
/// <see cref="ModManager.GetGameplayRelevantModNameList"/> (<c>id-version</c> of each mod affecting gameplay, sent in the
/// host's first message) with its own and stops the join with its "mod mismatch" error, naming both. With the mod off
/// (master switch off, or turned off when the game started), this mod's entry reads <c>BigDeckOptimization-v1.0.3-off</c>:
/// an "on" and an "off" player can't play together; two "off" players can. Anyone can host either way.
///
/// Applied even when the mod was turned off at the start (<see cref="ModEntry.StartedOff"/>: then nothing else is). It
/// changes no game state: only this list, which the game uses for that comparison (the host's message, the client's check;
/// v0.111+ through <c>PeerVersionInfo</c>). If the list's format changes (entry not found), it stays the game's, logged once.
/// </summary>
public static class CoopModeTag
{
    public const string Name = "CoopModeTag";

    public const string OffSuffix = "-off";

    public static bool Applied { get; private set; }

#if BENCH
    /// <summary>Lists given with this mod's entry tagged off (the effects check).</summary>
    public static long Tagged { get; private set; }
#endif

    private static bool _notFoundLogged;

    /// <summary>This player has the whole mod off: what the other co-op players must have too.</summary>
    public static bool Off => ModEntry.StartedOff || !Config.Settings.Master;

    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(ModManager), nameof(ModManager.GetGameplayRelevantModNameList))
            ?? throw new MissingMethodException(nameof(ModManager), nameof(ModManager.GetGameplayRelevantModNameList));
        PatchGuard.Patch(harmony, original, postfix: new HarmonyMethod(typeof(CoopModeTag), nameof(Postfix)));
        Applied = true;
    }

    private static void Postfix(List<string>? __result)
    {
        try
        {
            if (__result == null || !Off)
            {
                return;
            }
            string prefix = ModEntry.Id + "-";
            for (int i = 0; i < __result.Count; i++)
            {
                string entry = __result[i];
                if (entry != null && entry.StartsWith(prefix, StringComparison.Ordinal) && !entry.EndsWith(OffSuffix, StringComparison.Ordinal))
                {
                    __result[i] = entry + OffSuffix;
#if BENCH
                    Tagged++;
#endif
                    return;
                }
            }
            if (!_notFoundLogged)
            {
                _notFoundLogged = true;
                Log.Warn($"[{ModEntry.Id}] {Name}: this mod's entry not found in the game's gameplay mod list ({string.Join(", ", __result)}): "
                    + "co-op players with the mod off are not told apart from those with it on.");
            }
        }
        catch (Exception e)
        {
            // Runs in the game's join and lobby code: never throw there; the list stays the game's.
            Log.Warn($"[{ModEntry.Id}] {Name}: list left as the game made it: {e.Message}");
        }
    }
}
