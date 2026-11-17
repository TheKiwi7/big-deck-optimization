using System;
using System.Collections.Generic;
using System.Reflection;
using BigDeckOptimization.Patches;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Bench;

/// <summary>
/// The effect counters AutoTest's <c>effects</c> lines print (<c>AutoTest.EffectCounts</c>, keep both lists in step), saved
/// before a <c>bdperf</c> bench and put back after it: a bench drives the optimized code itself (it frees grid holders, renders
/// tabs, reads piles), so without this an <c>effects</c> line after a bench would show a part as run even when the game no
/// longer reaches it (effects.py and mutations.ps1's "effect lost" check). The benches still read their own deltas inside.
/// </summary>
internal static class EffectCounters
{
    private static readonly (Type Type, string Member)[] Members =
    {
        (typeof(ListenerCache), nameof(ListenerCache.Served)), (typeof(ListenerCache), nameof(ListenerCache.CreatureInvalidations)),
        (typeof(ListenerCache), nameof(ListenerCache.SuspendedRevalidations)),
        (typeof(HookFilter), nameof(HookFilter.ListenersDropped)),
        (typeof(ExtraScopes), nameof(ExtraScopes.FirstTurnPasses)), (typeof(IdleButtonInput), nameof(IdleButtonInput.Muted)),
        (typeof(GridSortCache), nameof(GridSortCache.Sorts)), (typeof(EnchantmentTabReuse), nameof(EnchantmentTabReuse.Reused)),
        (typeof(PoolSignalScan), nameof(PoolSignalScan.Nodes)), (typeof(CardDbScan), nameof(CardDbScan.Skipped)),
        (typeof(PileIndex), nameof(PileIndex.Fast)), (typeof(PileIndex), nameof(PileIndex.FastDuplicateChecks)),
        (typeof(PileCounterBatch), nameof(PileCounterBatch.Batched)), (typeof(FastCombatStart), nameof(FastCombatStart.SkippedWaits)),
        (typeof(HoverTipReuse), nameof(HoverTipReuse.Reused)),
        (typeof(FastListeners), nameof(FastListeners.CombatWalks)), (typeof(FastListeners), nameof(FastListeners.RunWalks)),
        (typeof(FastListeners), nameof(FastListeners.SegmentHits)), (typeof(FastListeners), nameof(FastListeners.SegmentMoveHits)),
        (typeof(FastListeners), nameof(FastListeners.SnapshotCatchUps)), (typeof(FastListeners), nameof(FastListeners.KindsSkipped)),
        (typeof(FastListeners), nameof(FastListeners.OthersDropped)), (typeof(HookSkip), nameof(HookSkip.FilteredWalks)),
        (typeof(LimitlessReserialize), nameof(LimitlessReserialize.Kept)), (typeof(LimitlessRecalcSkip), nameof(LimitlessRecalcSkip.Skipped)),
        (typeof(LimitlessRecalcSkip), nameof(LimitlessRecalcSkip.PassesSkipped)),
        (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.Reused)), (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.CombatCardsReleased)),
        (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.PilesReused)), (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.PilesTrusted)),
        (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.MovedReused)), (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.DirtyPiles)),
        (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.PilesWithHooks)), (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.BlockCopiedCards)),
        (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.BitsReused)), (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.UpgradeBitsPatched)),
        (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.PileBitsReused)), (typeof(SnapshotCardReuse), nameof(SnapshotCardReuse.PileBitsComposed)),
        (typeof(DeckCountMemo), nameof(DeckCountMemo.Kept)), (typeof(DeckCountMemo), nameof(DeckCountMemo.Updated)),
        (typeof(DeckCountMemo), nameof(DeckCountMemo.Checks)),
        (typeof(LimitlessHookSkip), nameof(LimitlessHookSkip.Dropped)), (typeof(LimitlessHookSkip), nameof(LimitlessHookSkip.NotesFromSaved)),
        (typeof(LimitlessCloneMemo), nameof(LimitlessCloneMemo.Reused)), (typeof(TextPoolReuse), nameof(TextPoolReuse.Served)),
        (typeof(ThemeColorSkip), nameof(ThemeColorSkip.Skipped)), (typeof(ReplayStateSkip), nameof(ReplayStateSkip.Skipped)),
        (typeof(GcPacing), nameof(GcPacing.Regions)), (typeof(RelicFlashMerge), nameof(RelicFlashMerge.Merged)),
        (typeof(CardFlightLimit), nameof(CardFlightLimit.Skipped)), (typeof(TypePlaqueSkip), nameof(TypePlaqueSkip.Skipped)),
        (typeof(ControllerActionSkip), nameof(ControllerActionSkip.Skipped)), (typeof(RoomCollect), nameof(RoomCollect.Background)),
        (typeof(PooledCardRelease), nameof(PooledCardRelease.Released)), (typeof(SyncedCardRelease), nameof(SyncedCardRelease.Released)),
        (typeof(SavedPropertyLookup), nameof(SavedPropertyLookup.Served)), (typeof(RelicStateReuse), nameof(RelicStateReuse.Reused)),
        (typeof(RelicStateReuse), nameof(RelicStateReuse.ScalarReused)), (typeof(RichTextEnvRelease), nameof(RichTextEnvRelease.Released)),
        (typeof(RichTextEnvRelease), nameof(RichTextEnvRelease.Untracked)), (typeof(InlineGuard), nameof(InlineGuard.Protected)), (typeof(InlineGuard), nameof(InlineGuard.Refreshed)),
        (typeof(CardSortKeys), nameof(CardSortKeys.Sorts)), (typeof(CardSortKeys), nameof(CardSortKeys.Answered)),
        (typeof(SnapshotVerifier), nameof(SnapshotVerifier.Verified)), (typeof(ListenerCache), nameof(ListenerCache.Reused)),
        (typeof(FastListeners), nameof(FastListeners.SkippedModifierWalks)), (typeof(HookSkip), nameof(HookSkip.BaseLibWalks)),
        (typeof(HookSkip), nameof(HookSkip.NoBadgeWalks)), (typeof(HookSkip), nameof(HookSkip.EmptiedWalks)), (typeof(FastListeners), nameof(FastListeners.FirstKept)),
        (typeof(ExtraScopes), nameof(ExtraScopes.BetterSpireScopes)), (typeof(CardDbScan), nameof(CardDbScan.Verified)),
        (typeof(FastListeners), nameof(FastListeners.DominionTails)), (typeof(FastListeners), nameof(FastListeners.ModeRunModelTails)),
        (typeof(ListenerCache), nameof(ListenerCache.DominionInvalidations)), (typeof(ListenerCache), nameof(ListenerCache.ModeRunModelInvalidations)),
        (typeof(ListenerCache), nameof(ListenerCache.ForeignDispatches)), (typeof(ListenerCache), nameof(ListenerCache.GameHandlerReuses)),
        (typeof(ListenerCache), nameof(ListenerCache.ForeignHandlerCalls)), (typeof(ListenerCache), nameof(ListenerCache.KeptDroppedAfterForeign)),
        (typeof(ListenerCache), nameof(ListenerCache.KeptAfterForeign)), (typeof(ListenerCache), nameof(ListenerCache.ReusedAfterForeign)),
    };

    /// <summary>Each counter's getter and setter (private setters included), resolved once.</summary>
    private static List<(string Name, Func<object?> Get, Action<object?> Set)>? _accessors;

    private static List<(string Name, Func<object?> Get, Action<object?> Set)> Accessors()
    {
        if (_accessors != null)
        {
            return _accessors;
        }
        var accessors = new List<(string, Func<object?>, Action<object?>)>();
        foreach ((Type type, string member) in Members)
        {
            string name = type.Name + "." + member;
            if (AccessTools.DeclaredProperty(type, member) is { } property && property.GetSetMethod(nonPublic: true) is { } setter)
            {
                accessors.Add((name, () => property.GetValue(null), value => setter.Invoke(null, new[] { value })));
            }
            else if (AccessTools.DeclaredField(type, member) is { IsStatic: true, IsInitOnly: false } field)
            {
                accessors.Add((name, () => field.GetValue(null), value => field.SetValue(null, value)));
            }
            else
            {
                Log.Warn($"[{ModEntry.Id}] bdperf: effect counter {name} can't be put back after a bench (no setter): a bench may count in it.");
            }
        }
        return _accessors = accessors;
    }

    /// <summary>Saves every effect counter now; disposing puts them back as they were.</summary>
    public static IDisposable Freeze()
    {
        var saved = new List<(Action<object?> Set, object? Value)>();
        foreach ((string _, Func<object?> get, Action<object?> set) in Accessors())
        {
            saved.Add((set, get()));
        }
        return new Restore(saved);
    }

    private sealed class Restore(List<(Action<object?> Set, object? Value)> saved) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }
            _done = true;
            foreach ((Action<object?> set, object? value) in saved)
            {
                try
                {
                    set(value);
                }
                catch (Exception e)
                {
                    Log.Warn($"[{ModEntry.Id}] bdperf: an effect counter wasn't put back after a bench: {e.GetBaseException().Message}");
                }
            }
        }
    }
}
