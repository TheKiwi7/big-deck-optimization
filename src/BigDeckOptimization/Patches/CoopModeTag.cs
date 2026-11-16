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
/// When the list is read: v0.107.1 builds it at each connection (the host's <c>InitialGameInfoMessage</c>, the client's
/// <c>JoinFlow</c> check), so it is the state at that moment. v0.111+ builds it once, in <c>PeerVersionInfo.LocalDefault</c>
/// when hosting or joining starts (the net service and its <c>HandshakeManager</c> keep that struct, so the list by reference)
/// and sends that same list at every later handshake: <see cref="Refresh"/> retags the lists handed out in place when the
/// switch changes, so a player joining later is compared with this player's state at that handshake, not the one from when
/// hosting started.
///
/// Applied even when the mod was turned off at the start (<see cref="ModEntry.StartedOff"/>: then nothing else is). It
/// changes no game state: only this list, which the game uses for that comparison (the host's message, the client's check;
/// v0.111+ through <c>PeerVersionInfo</c>, whose other readers only test whether any mod is listed). If the list's format
/// changes (entry not found), it stays the game's, logged once.
/// </summary>
public static class CoopModeTag
{
    public const string Name = "CoopModeTag";

    public const string OffSuffix = "-off";

    public static bool Applied { get; private set; }

#if BENCH
    /// <summary>Times this mod's entry was tagged off in a list (handed out, or retagged by <see cref="Refresh"/>): the coopmodetag check.</summary>
    public static long Tagged { get; private set; }
#endif

    private static bool _notFoundLogged;

    /// <summary>
    /// The lists handed out that hold this mod's entry, weakly (v0.111+ keeps one per net service for its whole life): a few
    /// per lobby or join, the dead ones dropped as new ones come.
    /// </summary>
    private static readonly List<WeakReference<List<string>>> _given = new();

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
            if (__result == null)
            {
                return;
            }
            if (Tag(__result))
            {
                lock (_given)
                {
                    _given.RemoveAll(w => !w.TryGetTarget(out _));
                    _given.Add(new WeakReference<List<string>>(__result));
                }
                return;
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

    /// <summary>
    /// After the master switch changed (<see cref="Config.Settings.SetMaster"/>): every list handed out and still alive gets this
    /// mod's entry as the switch now says, so the next handshake sends and compares the current state. Never throws.
    /// </summary>
    public static void Refresh()
    {
        try
        {
            lock (_given)
            {
                _given.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (WeakReference<List<string>> given in _given)
                {
                    if (given.TryGetTarget(out List<string>? list))
                    {
                        Tag(list);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: lists already handed out not retagged: {e.Message}");
        }
    }

    /// <summary>
    /// The entry of this mod in <paramref name="list"/> as <see cref="Off"/> says (tagged or not): the game's own value when on.
    /// False when the list has no entry of this mod.
    /// </summary>
    private static bool Tag(List<string> list)
    {
        string prefix = ModEntry.Id + "-";
        bool off = Off;
        for (int i = 0; i < list.Count; i++)
        {
            string entry = list[i];
            if (entry == null || !entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            bool tagged = entry.EndsWith(OffSuffix, StringComparison.Ordinal);
            if (off && !tagged)
            {
                list[i] = entry + OffSuffix;
#if BENCH
                Tagged++;
#endif
            }
            else if (!off && tagged)
            {
                list[i] = entry.Substring(0, entry.Length - OffSuffix.Length);
            }
            return true;
        }
        return false;
    }
}
