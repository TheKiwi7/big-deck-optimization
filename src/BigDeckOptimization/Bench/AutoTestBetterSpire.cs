using System;
using System.Linq;
using System.Reflection;
using BigDeckOptimization.Patches;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;

namespace BigDeckOptimization.Bench;

/// <summary>
/// BetterSpire2's teammate-hand panel (DeckTracker, shown in combat when its <c>ShowTeammateHand</c> setting is on, the default)
/// in a test session without Steam: its section titles ask the game for each player's Steam name
/// (<c>PlatformUtil.GetPlayerName(PlatformType.Steam, netId)</c>, which throws "Steamworks is not initialized" when the game runs
/// without Steam, so the panel was never built). Test builds only, on request: the name is answered as the game's null platform
/// does when Steam isn't initialized (display text, nothing checksummed).
/// </summary>
public static partial class AutoTest
{
    private static bool _bs2Installed;
    private static long _bs2Refreshes, _bs2Answered, _bs2Refused, _bs2Checks, _bs2Walks, _bs2TeammateAnswered;
    private static (long Refreshes, long Answered, long Refused, long Checks, long Walks, long Teammate) _bs2Mark;
    private static FieldInfo? _bs2Player;

    /// <summary>
    /// Script <c>bs2panel show|mark|check|upgrade</c>: <c>upgrade</c> upgrades the local hand's first Squeeze or Perfected Strike below
    /// its max level through the game's networked <c>upgrade</c> console command. <c>show</c> answers the Steam name (above), counts the panel's card refreshes
    /// (<c>PlayerHandSection.RefreshCards</c>: DeckCountMemo's display answers, refusals and scheduled checks while one runs) and
    /// shows the panel (<c>DeckTracker.ShowIfInCombat</c>); fails if BetterSpire2 isn't loaded or the panel doesn't show.
    /// <c>mark</c> notes the counts; <c>check</c> fails unless the panel refreshed since the mark and, with DeckCountMemo's display
    /// part on, its refreshes got display answers and refusals (its <c>OnCardUpgraded</c> entry is a game event).
    /// </summary>
    [ScriptCommand("bs2panel")]
    private static async System.Threading.Tasks.Task Bs2Panel(SceneTree tree, string arg)
    {
        if (arg.Trim().StartsWith("makeroom", StringComparison.Ordinal))
        {
            // The local hand down to n cards through the game's networked remove_card console command (its last card each time): room
            // for the counting cards a scenario adds, the same on every peer and both sides of an on/off pair.
            int keep = int.Parse(arg.Trim().Substring("makeroom".Length).Trim());
            MegaCrit.Sts2.Core.Entities.Players.Player local = Me();
            for (int guard = 0; guard < 12 && PileType.Hand.GetPile(local).Cards.Count > keep; guard++)
            {
                int before = PileType.Hand.GetPile(local).Cards.Count;
                string id = PileType.Hand.GetPile(local).Cards[before - 1].Id.Entry;
                await Command($"remove_card {id} hand");
                await Until(tree, () => PileType.Hand.GetPile(local).Cards.Count < before, 30, "card removed");
            }
            Say($"  BS2PANEL local hand {PileType.Hand.GetPile(local).Cards.Count} cards");
            return;
        }
        if (arg.Trim() == "upgrade")
        {
            // The local player's first Squeeze or Perfected Strike in hand below its max level, upgraded through the game's console
            // command (networked in co-op: every peer runs it): BetterSpire2's OnCardUpgraded refreshes that hand from a game event.
            MegaCrit.Sts2.Core.Entities.Players.Player me = Me();
            var cards = PileType.Hand.GetPile(me).Cards;
            int index = -1;
            for (int i = 0; i < cards.Count && index < 0; i++)
            {
                if (cards[i] is MegaCrit.Sts2.Core.Models.Cards.PerfectedStrike or MegaCrit.Sts2.Core.Models.Cards.Squeeze && cards[i].CurrentUpgradeLevel < cards[i].MaxUpgradeLevel)
                {
                    index = i;
                }
            }
            if (index < 0)
            {
                throw new InvalidOperationException("no Squeeze or Perfected Strike to upgrade in the local hand");
            }
            Say($"  BS2PANEL upgrading {cards[index].Id.Entry} at hand index {index}");
            await Command($"upgrade {index}");
            return;
        }
        Assembly bs2 = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "BetterSpire2")
            ?? throw new InvalidOperationException("BetterSpire2 is not loaded");
        Type tracker = bs2.GetType("BetterSpire2.DeckTracker") ?? throw new MissingMemberException("BetterSpire2.DeckTracker");
        switch (arg.Trim())
        {
            case "show":
            {
                if (!_bs2Installed)
                {
                    Harmony harmony = new("thekiwi7.BigDeckOptimization.Bench.Bs2Panel");
                    harmony.Patch(AccessTools.Method(typeof(PlatformUtil), nameof(PlatformUtil.GetPlayerName), new[] { typeof(PlatformType), typeof(ulong) }),
                        prefix: new HarmonyMethod(typeof(AutoTest), nameof(Bs2SteamName)));
                    Type section = tracker.GetNestedType("PlayerHandSection", BindingFlags.NonPublic | BindingFlags.Public)
                        ?? throw new MissingMemberException("DeckTracker.PlayerHandSection");
                    harmony.Patch(AccessTools.Method(section, "RefreshCards") ?? throw new MissingMethodException("PlayerHandSection", "RefreshCards"),
                        prefix: new HarmonyMethod(typeof(AutoTest), nameof(Bs2RefreshPrefix)), postfix: new HarmonyMethod(typeof(AutoTest), nameof(Bs2RefreshPostfix)));
                    _bs2Installed = true;
                }
                bool visible = (bool)(tracker.GetMethod("ShowIfInCombat", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null)
                    ?? throw new MissingMethodException("DeckTracker", "ShowIfInCombat"));
                int sections = (tracker.GetField("_sections", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as System.Collections.ICollection)?.Count ?? -1;
                Say($"  BS2PANEL shown {visible}, player sections {sections}, refreshes so far {_bs2Refreshes}");
                if (!visible || sections < 1)
                {
                    throw new InvalidOperationException("BetterSpire2's hand panel didn't show");
                }
                return;
            }
            case "mark":
                _bs2Mark = (_bs2Refreshes, _bs2Answered, _bs2Refused, _bs2Checks, _bs2Walks, _bs2TeammateAnswered);
                Say($"  BS2PANEL marked: refreshes {_bs2Refreshes}, display answered {_bs2Answered}, refused {_bs2Refused}, checks {_bs2Checks}");
                return;
            case "check":
            case "check teammate":
            {
                long refreshes = _bs2Refreshes - _bs2Mark.Refreshes, answered = _bs2Answered - _bs2Mark.Answered;
                long refused = _bs2Refused - _bs2Mark.Refused, checks = _bs2Checks - _bs2Mark.Checks, walks = _bs2Walks - _bs2Mark.Walks;
                long teammate = _bs2TeammateAnswered - _bs2Mark.Teammate;
                bool memoOn = !ModEntry.NotApplied(DeckCountMemo.DisplayName) && DeckCountMemo.Enabled && DeckCountMemo.DisplayEnabled;
                bool ok = refreshes > 0 && (!memoOn || answered > 0 && refused > 0 && (arg.Trim() == "check" || teammate > 0));
                string line = $"BS2PANEL since the mark: refreshes {refreshes}, in them display answered {answered} (a teammate's hand {teammate}), refused {refused}, "
                    + $"checks {checks}, walks {walks} (display part {(memoOn ? "on" : "off")})";
                if (!ok)
                {
                    throw new InvalidOperationException(line + $" (MISMATCH: expected refreshes{(memoOn ? ", display answers and refusals" : "")})");
                }
                Say("  " + line + ": ok");
                return;
            }
        }
        throw new ArgumentException("bs2panel show|mark|check|upgrade|makeroom <n>");
    }

    /// <summary>The game's null platform's answer when Steam isn't initialized (a test session): BetterSpire2's titles only.</summary>
    private static bool Bs2SteamName(PlatformType platformType, ulong playerId, ref string __result)
    {
        if (platformType != PlatformType.Steam || SteamInitializer.Initialized)
        {
            return true;
        }
        __result = PlatformUtil.GetPlayerName(PlatformType.None, playerId);
        return false;
    }

    private static void Bs2RefreshPrefix(out (long Answered, long Refused, long Checks, long Walks) __state) =>
        __state = (DeckCountMemo.AnsweredDisplay, DeckCountMemo.DisplayRefused, DeckCountMemo.DisplayChecks, DeckCountMemo.DisplayWalks);

    private static void Bs2RefreshPostfix(object __instance, (long Answered, long Refused, long Checks, long Walks) __state)
    {
        _bs2Refreshes++;
        long answered = DeckCountMemo.AnsweredDisplay - __state.Answered;
        _bs2Answered += answered;
        _bs2Refused += DeckCountMemo.DisplayRefused - __state.Refused;
        _bs2Checks += DeckCountMemo.DisplayChecks - __state.Checks;
        _bs2Walks += DeckCountMemo.DisplayWalks - __state.Walks;
        _bs2Player ??= AccessTools.Field(__instance.GetType(), "_player");
        if (_bs2Player?.GetValue(__instance) is MegaCrit.Sts2.Core.Entities.Players.Player player && player != Me())
        {
            _bs2TeammateAnswered += answered;
        }
    }
}
