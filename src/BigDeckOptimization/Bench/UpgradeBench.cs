using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace BigDeckOptimization.Bench;

/// <summary>
/// <c>bdperf upgradebench [n]</c>, in combat: what an upgrade costs over the local player's combat cards (what an Apotheosis
/// walks): the walk of their variables, Limitless's <c>PreviousLevel</c> table read and its setter prefix (writing the levels
/// already stored, its first pass adding the entries), then each step of an upgrade on fresh copies of the same cards.
/// </summary>
public static class UpgradeBench
{
    public static CmdResult Run(int iterations)
    {
        if (!CombatManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run || LocalContext.GetMe(run) is not { } me
            || me.PlayerCombatState is not { } combat)
        {
            return new CmdResult(success: false, "Start a combat first.");
        }
        List<CardModel> cards = combat.AllCards.ToList();
        List<DynamicVar> vars = cards.SelectMany(c => c.DynamicVars.Values).ToList();
        object? field = HarmonyLib.AccessTools.Field("Limitless.LimitlessCode.UncappedUpgrades.DynamicVarPatches.DynamicVarPreviousLevelField:PreviousLevel")?.GetValue(null);
        ConditionalWeakTable<DynamicVar, object?>? table = field == null ? null
            : HarmonyLib.AccessTools.Field(field.GetType(), "_table")?.GetValue(field) as ConditionalWeakTable<DynamicVar, object?>;

        double Time(Action pass)
        {
            pass();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                pass();
            }
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        int sink = 0;
        double walk = Time(() => { foreach (CardModel c in cards) foreach (DynamicVar v in c.DynamicVars.Values) sink += v.GetHashCode() & 1; });
        double hash = Time(() => { foreach (DynamicVar v in vars) sink += RuntimeHelpers.GetHashCode(v) & 1; });
        double read = table == null ? -1 : Time(() => { foreach (DynamicVar v in vars) sink += table.TryGetValue(v, out _) ? 1 : 0; });
        int found = table == null ? 0 : vars.Count(v => table.TryGetValue(v, out _));
        Dictionary<DynamicVar, int> plain = new(ReferenceEqualityComparer.Instance as IEqualityComparer<DynamicVar> ?? EqualityComparer<DynamicVar>.Default);
        foreach (DynamicVar v in vars) plain[v] = 0;
        double dict = Time(() => { foreach (DynamicVar v in vars) sink += plain.TryGetValue(v, out int x) ? x : 0; });
        int entries = table == null ? 0 : table.Count();
        System.Reflection.MethodInfo? limitlessPrefix = HarmonyLib.AccessTools.Method("Limitless.LimitlessCode.UncappedUpgrades.DynamicVarPatches.CardModelSetPreviousLevel:Prefix");
        double original = -1, originalFirst = -1;
        if (limitlessPrefix != null)
        {
            Action<CardModel> runOriginal = HarmonyLib.AccessTools.MethodDelegate<Action<CardModel>>(limitlessPrefix);
            long first = Stopwatch.GetTimestamp();
            foreach (CardModel c in cards) runOriginal(c);
            originalFirst = Stopwatch.GetElapsedTime(first).TotalMilliseconds;
            original = Time(() => { foreach (CardModel c in cards) runOriginal(c); });
        }

        // An upgrade's steps on fresh copies of the same cards (canonical ToMutable: in no pile, no combat, no node): each
        // pass copies them again, timed apart.
        List<CardModel> canonical = cards.Select(c => ModelDb.GetById<CardModel>(c.Id)).ToList();
        double copy = 0, upgradable = 0, upgrade = 0, finalize = 0;
        int upgraded = 0;
        for (int i = 0; i <= iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            List<CardModel> copies = canonical.Select(c => c.ToMutable()).ToList();
            long t1 = Stopwatch.GetTimestamp();
            List<CardModel> eligible = copies.Where(c => c.IsUpgradable).ToList();
            long t2 = Stopwatch.GetTimestamp();
            foreach (CardModel c in eligible) c.UpgradeInternal();
            long t3 = Stopwatch.GetTimestamp();
            foreach (CardModel c in eligible) c.FinalizeUpgradeInternal();
            long t4 = Stopwatch.GetTimestamp();
            if (i == 0)
            {
                continue; // JIT
            }
            copy += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            upgradable += Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
            upgrade += Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
            finalize += Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds;
            upgraded = eligible.Count;
        }
        // UpgradeInternal's steps one by one on other fresh copies: the level set (with every mod's prefixes), OnUpgrade, BaseLib's
        // variable upgrades (its transpiler adds them after OnUpgrade), the calculated variables.
        Action<CardModel, int> setLevel = HarmonyLib.AccessTools.MethodDelegate<Action<CardModel, int>>(
            HarmonyLib.AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.CurrentUpgradeLevel)));
        Action<CardModel> onUpgrade = HarmonyLib.AccessTools.MethodDelegate<Action<CardModel>>(HarmonyLib.AccessTools.Method(typeof(CardModel), "OnUpgrade"), virtualCall: true);
        System.Reflection.MethodInfo? baseLibVars = HarmonyLib.AccessTools.Method("BaseLib.Patches.Utils.UpgradeInternalPatch:UpgradeVars");
        Action<CardModel>? upgradeVars = baseLibVars == null ? null : HarmonyLib.AccessTools.MethodDelegate<Action<CardModel>>(baseLibVars);
        double level = 0, hook = 0, vars2 = 0, recalc = 0;
        for (int i = 0; i <= iterations; i++)
        {
            List<CardModel> eligible = canonical.Select(c => c.ToMutable()).Where(c => c.IsUpgradable).ToList();
            long t0 = Stopwatch.GetTimestamp();
            foreach (CardModel c in eligible) setLevel(c, c.CurrentUpgradeLevel + 1);
            long t1 = Stopwatch.GetTimestamp();
            foreach (CardModel c in eligible) onUpgrade(c);
            long t2 = Stopwatch.GetTimestamp();
            if (upgradeVars != null) foreach (CardModel c in eligible) upgradeVars(c);
            long t3 = Stopwatch.GetTimestamp();
            foreach (CardModel c in eligible) c.DynamicVars.RecalculateForUpgradeOrEnchant();
            long t4 = Stopwatch.GetTimestamp();
            if (i == 0)
            {
                continue;
            }
            level += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            hook += Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
            vars2 += Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
            recalc += Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds;
        }
        return new CmdResult(success: true, $"UPGRADEBENCH {cards.Count} cards, {vars.Count} variables, {found} in PreviousLevel ({entries} entries), per pass ms: "
            + $"walk {walk:F2}, hash codes {hash:F2}, table reads {read:F2}, dictionary reads {dict:F2}, Limitless's prefix {original:F2} (its first pass {originalFirst:F2}) ({sink & 0}); "
            + $"on {upgraded} fresh copies: copies {copy / iterations:F2}, IsUpgradable {upgradable / iterations:F2}, UpgradeInternal {upgrade / iterations:F2}, FinalizeUpgradeInternal {finalize / iterations:F2}; its steps: level set {level / iterations:F2}, OnUpgrade {hook / iterations:F2}, "
            + $"BaseLib's variables {(upgradeVars == null ? "-" : (vars2 / iterations).ToString("F2"))}, calculated variables {recalc / iterations:F2}");
    }
}
