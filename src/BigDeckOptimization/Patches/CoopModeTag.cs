using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// (master switch off, or turned off when the game started), this mod's entry reads <c>BigDeckOptimization-v1.0.3+off</c>:
/// an "on" and an "off" player can't play together; two "off" players can. Anyone can host either way. The marker is in the
/// version part, without a dash: a mod splitting entries at their last dash (a mod sync or compatibility screen) reads this
/// mod's id and a version "v1.0.3+off", not a mod "BigDeckOptimization-v1.0.3" nobody has.
///
/// When the list is read: v0.107.1 builds it at each connection (the host's <c>InitialGameInfoMessage</c>, the client's
/// <c>JoinFlow</c> check), so it is the state at that moment. v0.111+ builds it once, in <c>PeerVersionInfo.LocalDefault</c>
/// when hosting or joining starts (the net service and its <c>HandshakeManager</c> keep that struct, so the list by reference)
/// and sends that same list at every later handshake: <see cref="Refresh"/> retags the lists handed out in place when the
/// switch changes, so a player joining later is compared with this player's state at that handshake, not the one from when
/// hosting started.
///
/// A second tag, <c>+exactsnap</c> (owner's rule of 2026-11-17: a desync this mod could cause must never be reproducible): with
/// the mod on, when other mods' postfixes are on the listener walks and this player's co-op state checks can't reuse their
/// outputs (the "Co-op state check" option off, a fault, the net missing), or <c>+exactsnap-c</c> / <c>+exactsnap-r</c> when only
/// the combat / run walk's can't be reused while the other's can (other mods' patches loaded in another order):
/// <see cref="ListenerCache.SnapshotWalksTag"/>, the per-walk decisions.
/// A player whose state checks reuse them and one whose checks can't would compute different checksums at the same point every
/// time if a premise violation escaped the reuse's net; with the tag they can't join each other (the game's mod mismatch
/// error), whatever the number of re-hosts. Retagged as the off tag is, at every settings change (<see cref="Config.Settings.ApplyAll"/>).
///
/// Applied even when the mod was turned off at the start (<see cref="ModEntry.StartedOff"/>: then nothing else is). It
/// changes no game state: only this list, which the game uses for that comparison (the host's message, the client's check;
/// v0.111+ through <c>PeerVersionInfo</c>, whose other readers only test whether any mod is listed). If the list's format
/// changes (entry not found), it stays the game's, logged once.
/// </summary>
public static class CoopModeTag
{
    public const string Name = "CoopModeTag";

    public const string OffSuffix = "+off";

    /// <summary>The mod on, its co-op state checks unable to reuse the walks' outputs other mods' postfixes make (see the class).</summary>
    public const string ExactSnapSuffix = "+exactsnap";

    public static bool Applied { get; private set; }

#if BENCH
    /// <summary>Times this mod's entry was tagged off in a list (handed out, or retagged by <see cref="Refresh"/>): the coopmodetag check.</summary>
    public static long Tagged { get; private set; }
#endif

    private static bool _notFoundLogged;

    /// <summary>
    /// The game's join check tells this mod's on and off apart: the postfix is in place and has not met a list without this
    /// mod's entry (a game change of the list's format). Another mod replacing the list afterwards isn't seen. The texts
    /// promising that check (Settings.CoopOffOnlyNow, CoopRejoinNow) say it is missing otherwise. Faulted (HarmonyWatch found the
    /// method without its detour, so no postfix of any mod runs there): not working either.
    /// </summary>
    public static bool Working => Applied && !_notFoundLogged && !PatchSafety.IsFaulted(Name);

    /// <summary>
    /// The lists handed out that hold this mod's entry, weakly (v0.111+ keeps one per net service for its whole life): a few
    /// per lobby or join, the dead ones dropped as new ones come.
    /// </summary>
    private static readonly List<WeakReference<List<string>>> _given = new();

    /// <summary>This player has the whole mod off: what the other co-op players must have too.</summary>
    public static bool Off => ModEntry.StartedOff || !Config.Settings.Master;

    public static void Apply(Harmony harmony)
    {
        var original = Reflect.Method(typeof(ModManager), nameof(ModManager.GetGameplayRelevantModNameList))
            ?? throw new MissingMethodException(nameof(ModManager), nameof(ModManager.GetGameplayRelevantModNameList));
        // Last of the postfixes: another mod's postfix returning a new or filtered list runs before, so the list tagged and
        // tracked (Refresh) is the one the game gets.
        PatchGuard.Patch(harmony, original, postfix: new HarmonyMethod(typeof(CoopModeTag), nameof(Postfix)) { priority = Priority.Last });
        _original = original;
        Applied = true;
    }

    private static System.Reflection.MethodBase? _original;
    private static bool _orderChecked;

    /// <summary>
    /// Once, at the first list: another mod's postfix that Harmony runs after this one (also Priority.Last, registered later, or
    /// ordered after it with HarmonyAfter/HarmonyBefore) may replace the list, which a mid-session switch then can't retag: logged,
    /// so a join refusal's cause shows in the log. The order is Harmony's own sort (priorities, before/after ids, registration),
    /// or priority and registration only when a postfix is made by a factory (Harmony's sort would call it). Only logs.
    /// </summary>
    private static List<Patch> CheckOrder()
    {
        _orderChecked = true;
        if (_original == null || Harmony.GetPatchInfo(_original) is not { } info)
        {
            return new List<Patch>();
        }
        Patch[] postfixes = info.Postfixes.ToArray();
        Patch? ours = postfixes.FirstOrDefault(p => p.owner.StartsWith(ModEntry.Harmony.Id, StringComparison.Ordinal) && p.PatchMethod?.Name == nameof(Postfix));
        if (ours == null)
        {
            return new List<Patch>();
        }
        List<Patch> after;
        MethodInfo?[] methods = postfixes.Select(PatchSafety.PatchMethodOf).ToArray();
        if (methods.All(m => m != null && !PatchSafety.IsFactory(m)))
        {
            for (int i = 0; i < postfixes.Length; i++)
            {
                postfixes[i].PatchMethod = methods[i];
            }
            List<MethodInfo> sorted = PatchProcessor.GetSortedPatchMethods(_original, postfixes);
            int own = sorted.FindIndex(m => m.MethodHandle == ours.PatchMethod.MethodHandle);
            after = own < 0 ? new List<Patch>()
                : postfixes.Where(p => !PatchGuard.IsOurs(p.owner) && sorted.FindIndex(m => m.MethodHandle == p.PatchMethod.MethodHandle) > own).ToList();
        }
        else
        {
            after = postfixes.Where(p => !PatchGuard.IsOurs(p.owner) && (p.priority < ours.priority || (p.priority == ours.priority && p.index > ours.index))).ToList();
        }
        foreach (Patch patch in after)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: {patch.owner}'s postfix on the gameplay mod list runs after this mod's: a list it returns isn't retagged when the mod is switched mid-session");
        }
        return after;
    }

#if BENCH
    /// <summary>
    /// <c>bdperf modorder [none|&lt;owner&gt;]</c>: runs the order check again (it runs once per session otherwise) and lists the
    /// other mods' postfixes Harmony runs after this mod's on the gameplay mod list. <c>none</c>: none may be (a control);
    /// an owner: that Harmony id must be among them. Throws when the check disagrees (the command fails).
    /// </summary>
    internal static string BenchOrder(string? require)
    {
        List<Patch> after = CheckOrder();
        string listed = after.Count == 0 ? "none" : string.Join(", ", after.Select(p => $"{p.owner} (priority {p.priority})"));
        string line = $"MODORDER postfixes after this mod's on the gameplay mod list: {listed}";
        if (require == "none" && after.Count > 0 || require is { Length: > 0 } and not "none" && !after.Any(p => p.owner == require))
        {
            throw new InvalidOperationException($"{line} (expected {require})");
        }
        return line;
    }
#endif

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
                // After the tag and its bookkeeping, in a try of its own: a logging step never leaves a list untagged.
                if (!_orderChecked)
                {
                    try
                    {
                        CheckOrder();
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"[{ModEntry.Id}] {Name}: order of the postfixes on the gameplay mod list not checked: {e.Message}");
                    }
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
    /// After a switch changed (<see cref="Config.Settings.SetMaster"/>, <see cref="Config.Settings.ApplyAll"/>): every list handed out and still alive gets this
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

    /// <summary>This player's tag now: <see cref="OffSuffix"/> with the mod off, <see cref="ExactSnapSuffix"/> (see the class), or none.</summary>
    public static string Suffix => Off ? OffSuffix : ListenerCache.SnapshotWalksTag;

    /// <summary>Every tag this class writes, the longest first (stripped before the current one is written).</summary>
    private static readonly string[] Suffixes = { ExactSnapSuffix + "-c", ExactSnapSuffix + "-r", ExactSnapSuffix, OffSuffix };

    /// <summary>
    /// The entry of this mod in <paramref name="list"/> as <see cref="Suffix"/> says (tagged or not): the game's own value when on
    /// with no tag. False when the list has no entry of this mod.
    /// </summary>
    private static bool Tag(List<string> list)
    {
        string prefix = ModEntry.Id + "-";
        string suffix = Suffix;
        for (int i = 0; i < list.Count; i++)
        {
            string entry = list[i];
            if (entry == null || !entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            string bare = entry;
            foreach (string known in Suffixes)
            {
                if (entry.EndsWith(known, StringComparison.Ordinal))
                {
                    bare = entry.Substring(0, entry.Length - known.Length);
                    break;
                }
            }
            string wanted = bare + suffix;
            if (entry != wanted)
            {
                list[i] = wanted;
#if BENCH
                if (suffix == OffSuffix)
                {
                    Tagged++;
                }
#endif
            }
            return true;
        }
        return false;
    }
}
