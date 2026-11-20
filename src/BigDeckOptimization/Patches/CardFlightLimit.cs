using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace BigDeckOptimization.Patches;

/// <summary>
/// A card moving between the draw and discard piles without a card on screen spawns a "card soul" flying to its pile
/// (<c>NCardFlyShuffleVfx</c>), with a sound and a trail: a scene instantiated per card, then a few nodes working every
/// frame for 2-4 seconds. A reshuffle squeezes its whole discard pile into 0.8 s whatever its size: at 16,800 cards,
/// 16,800 souls alive at once, each frame taking seconds (5 minutes in all, frames up to a minute).
///
/// At most <see cref="PerSecond"/> souls per second now fly, with a burst of <see cref="Burst"/>: a reshuffle of up to ~64
/// cards and a move of a few cards look as in the game; a bigger pile sends a steady stream. A soul only flies and
/// counts on the pile's button when it lands, so the button of a pile that got no soul counts those cards itself, once
/// per frame. Sound and visuals only, on the local player's screen (the game shows these only for the local player).
/// </summary>
public static class CardFlightLimit
{
    public const string Name = "CardFlightLimit";

    /// <summary>v0.107.1 code relied on: a soul only plays its sound, animates and tells its pile when it lands.</summary>
    private const string CreateFingerprint = "482216868A87BB4C";
    private const string ReadyFingerprint = "C82F2DB6F48B5EC6";
    private const string PlayAnimFingerprint = "8986E87813FC7715";
    private const string AddCardFingerprint = "005700D530E09ECC";

    private const double PerSecond = 80;
    private const double Burst = 12;

    public static bool Enabled { get; set; } = true;
    public static bool Applied { get; private set; }
    public static long Skipped { get; private set; }

    private static System.Reflection.MethodInfo? _create, _addCard, _invokeAddFinished;
    /// <summary>
    /// The game's swoosh sound command a soul plays (<c>SfxCmd.PlayCardSwooshSfx</c>), shared with other card flights. Under the
    /// premise since the owner's decision of 2026-11-17 (option (a): everything a skip removes, the swoosh included, the skip
    /// acting only above <see cref="PerSecond"/> souls a second with a burst of <see cref="Burst"/>); strict only with the shape
    /// rules off, as before them.
    /// </summary>
    private static System.Reflection.MethodBase?[] _skipped = Array.Empty<System.Reflection.MethodBase?>();

    /// <summary>
    /// Everything a skip removes calls to, computed from the game's code (<see cref="CodeSet"/>: <c>NCardFlyShuffleVfx</c>'s and
    /// <c>NCardTrailVfx</c>'s whole classes, a skipped soul never existing, and every game method they reach: the swoosh,
    /// <c>SfxCmd.Play</c> and both <c>NAudioManager.PlayOneShot</c> overloads, <c>PileTypeExtensions.GetTargetPosition</c>,
    /// <c>NonInteractiveMode.IsActive</c>, <c>CombatManager.IsEnding</c>, the scene cache, <c>AddChildSafely</c>,
    /// <c>MoveChildSafely</c>, <c>TaskHelper.RunSafely</c>, <c>MathHelper.BezierCurve</c>, the frame and tween awaits,
    /// <c>QueueFreeSafely</c>, <c>Rng.Chaotic</c>'s draws, v0.113.1's <c>OwnerFreeingTween</c> and <c>OwnedTween</c>, the
    /// Godot-generated members), pinned per version (<see cref="FlightSets"/>). The premise's scope: other mods' patches there
    /// stand (named in the kept note), and so do other mods' overrides of a game virtual method it calls. Strict with the shape
    /// rules off. Built once, in the background while the game loads, else on the main thread (the preflight's note step, else
    /// the first soul made).
    /// </summary>
    private static CodeSet? _flightSet;
    private static bool _flightSetFailed;

    /// <summary>
    /// The game logic a skip removes (A4 of the re-audit round 2 of 2026-11-17; the owner's premise covers display code only): the
    /// combat-end check the swoosh's <c>SfxCmd.Play</c> makes before playing (<c>NonInteractiveMode.IsActive</c>, then
    /// <c>CombatManager.IsEnding</c>), in two parts. Its head (<see cref="_endHead"/>: IsActive and its test hook, IsEnding up to
    /// the listener walk: in progress, pending loss, <c>Enemies.Any(e =&gt; e != null &amp;&amp; e.IsAlive &amp;&amp; e.IsPrimaryEnemy)</c>;
    /// with <c>CombatManager.Instance</c> and <c>DebugOnlyGetState</c>, which <see cref="PrimaryEnemyAlive"/> reads) runs at every
    /// swoosh: another mod's code there (a patch, an override such as a mod power's <c>OwnerIsSecondaryEnemy</c>, a test hook)
    /// keeps every soul (a back-off, shown). Its walk (<see cref="_endWalk"/>: <c>Hook.ShouldStopCombatFromEnding</c>,
    /// <c>IterateHookListeners</c>, the listeners' overrides, the mods' combat hook subscriptions such as BaseLib's) runs only when
    /// no primary enemy is alive: with another mod's code there a soul is skipped only while a primary enemy is alive (the walk
    /// can't run then: read by <see cref="PrimaryEnemyAlive"/> with the head's own game code, free of other mods' code), so the
    /// skip removes no other mod's call. The game's own code in both only reads (its <c>ShouldStopCombatFromEnding</c> overrides
    /// return a constant). Computed (<see cref="CodeSet"/>, interface callees included: a mod's <c>ICombatState</c>) and pinned per
    /// version (tools/fingerprint closure: roots <c>CombatManager::get_IsEnding()</c>, <c>NonInteractiveMode::get_IsActive()</c>,
    /// <c>CombatManager::DebugOnlyGetState()</c>, <c>CombatManager::get_Instance()</c> and stop <c>!Hook::ShouldStopCombatFromEnding(ICombatState)</c>
    /// for the head; root <c>Hook::ShouldStopCombatFromEnding(ICombatState)</c> for the walk). Built with the flight set.
    /// </summary>
    private static CodeSet? _endHead, _endWalk;
    private static readonly string[] EndHeadSets = { "28A87FFE9B87E84F", "850A90B2F90BF687", "850A90B2F90BF687" };
    private static readonly string[] EndWalkSets = { "48D21397A007F655", "48D21397A007F655", "8EF196E566F01EC0" };

    /// <summary>
    /// What a merged pile count runs once instead of once per card: the pile button's <c>NCombatCardPile.AddCard</c> and every game
    /// method it reaches (the pile's card list, <c>MegaLabel.SetTextAutoSize</c> and its font sizing; computed, <see cref="CodeSet"/>),
    /// the same 10 methods on v0.107.1, v0.111.0 and v0.113.1 (<see cref="AddCardSets"/>). Strict: another mod's patch on any of them
    /// would see one call for several cards, so each card then gets its own event. Null when it couldn't be computed (no merge).
    /// </summary>
    private static CodeSet? _addCardSet;
    private static readonly string[] AddCardSets = { "A49A31223F8E575A" };

    /// <summary>The flight set's fingerprints reviewed on the decompiled game (tools/fingerprint closure): v0.107.1, v0.111.0, v0.113.1.</summary>
    /// <remarks>Roots since the re-audit round 2 (A2): the trail scenes' line script <c>NCardTrail</c> too (card_trail_*.tscn's
    /// OuterTrail and InnerTrail nodes, read from the game's pck: a skipped soul's trail never has them).</remarks>
    private static readonly string[] FlightSets = { "B2945863B3FC581A", "A889670CD281D0F1", "FD9ED6B9CD933ECF" };
    /// <summary>
    /// The soul's own display code a skipped soul never runs besides its _Ready: its flight (<c>PlayAnim</c> and its state
    /// machine) and its trail's factory (<c>NCardTrailVfx.Create</c>). Other mods' patches here (and on _Ready) are taken as
    /// display only (DESIGN "Needless back-offs made safe", class 3, the owner's premise as worded on 2026-11-17: the calls a
    /// skip removes are all made from inside a soul's lifetime, its creation path, its _Ready and its tween, and are display).
    /// The game makes souls only on the local player's screen, never in its test mode nor for silent moves, and nothing in them
    /// reaches the game's state but the pile's CardAddFinished event, which this patch sends itself (<see cref="Flush"/>). With
    /// the shape rules off (<see cref="PatchSafety.ShapeRules"/>) these (the methods checked before the rules) are strict again.
    /// </summary>
    private static System.Reflection.MethodBase?[] _ownDisplay = Array.Empty<System.Reflection.MethodBase?>();

    /// <summary>
    /// The rest of the souls' own visual classes' code a skip removes (named in the kept note, under the same premise):
    /// NCardFlyShuffleVfx's constructor and _ExitTree, the trail's constructor, _Ready, _Process, FadeOut (its state machine on
    /// v0.107.1 and v0.111.0, its tween lambda on v0.113.1), StopParticles and _ExitTree; the trail's line script NCardTrail
    /// (constructor, _Ready, _Process, OnToggleVisibility, CreatePoint: A2, re-audit round 2). Never checked, with or without the
    /// shape rules (as before them): a removed soul never runs them, and the premise takes what they do as that soul's display.
    /// Their code is pinned (<see cref="DisplayFingerprints"/>): a change leaves CardFlightLimit off until reviewed.
    /// </summary>
    private static System.Reflection.MethodBase?[] _premiseOnly = Array.Empty<System.Reflection.MethodBase?>();

    /// <summary>
    /// The souls' visual code the skip and the premise rely on, per classified version (v0.107.1 first, v0.113.1 second;
    /// v0.111.0 has v0.107.1's): an async method by its state machine's MoveNext. Reviewed 2026-11-17 on the decompiled game:
    /// node, tween and particle calls only (v0.113.1's FadeOut through OwnerFreeingTween, its constructor making one).
    /// </summary>
    private static readonly (Type Type, string Method, string[] Fingerprints)[] DisplayFingerprints =
    {
        (typeof(NCardFlyShuffleVfx), ".ctor", new[] { "5659246E3B3CB404" }),
        (typeof(NCardFlyShuffleVfx), "_ExitTree", new[] { "BEF6121A78FE9F8D" }),
        (typeof(NCardTrailVfx), "Create", new[] { "F415B9074C7591D1" }),
        (typeof(NCardTrailVfx), ".ctor", new[] { "4F374546541E11AD", "3217D5464A66767F" }),
        (typeof(NCardTrailVfx), "_Ready", new[] { "BFF1D54F64E6A3AA", "D73CA79565FC9333" }),
        (typeof(NCardTrailVfx), "_Process", new[] { "871243FFEBA757F5" }),
        (typeof(NCardTrailVfx), "FadeOut", new[] { "CD8FC8A0F872E473", "091DB0CA25266E23" }),
        (typeof(NCardTrailVfx), "StopParticles", new[] { "D3CAA23B0966EEB1" }),
        (typeof(NCardTrailVfx), "_ExitTree", new[] { "6442E5133D33F467", "5650FCC456258D64" }),
        // A2 (re-audit round 2): the trail scenes' line script (the same on v0.107.1, v0.111.0 and v0.113.1).
        (typeof(NCardTrail), ".ctor", new[] { "4A36BDB20670295A" }),
        (typeof(NCardTrail), "_Ready", new[] { "6FC5836558959E5E" }),
        (typeof(NCardTrail), "_Process", new[] { "9D67BF845DE98DA4" }),
        (typeof(NCardTrail), "OnToggleVisibility", new[] { "74316E9791958CAD" }),
        (typeof(NCardTrail), "CreatePoint", new[] { "C574F47A08529F52" }),
    };

    /// <summary>
    /// v0.113.1's tween helpers the trail's FadeOut goes through (absent on v0.107.1 and v0.111.0: by name, passed over when the
    /// type isn't there): pinned like <see cref="DisplayFingerprints"/>.
    /// </summary>
    private static readonly (string Type, string Method, string[] Fingerprints)[] TweenFingerprints =
    {
        ("MegaCrit.Sts2.Core.Nodes.GodotExtensions.OwnerFreeingTween", ".ctor", new[] { "4C81D21D262EADED" }),
        ("MegaCrit.Sts2.Core.Nodes.GodotExtensions.OwnerFreeingTween", "Play", new[] { "15A72D7349DCE42B" }),
        ("MegaCrit.Sts2.Core.Nodes.GodotExtensions.OwnedTween", "Play", new[] { "6C7A512EC3A77F13" }),
    };

    /// <summary>A method of <see cref="DisplayFingerprints"/> (the constructor without parameters for ".ctor").</summary>
    private static System.Reflection.MethodBase? DisplayMethod(Type type, string name) => name == ".ctor"
        ? type.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes)
        : Reflect.Method(type, name);
    private static System.Reflection.MethodInfo[]? _allowedOnReady;
    private static AccessTools.FieldRef<CardPile, Action?>? _cardAddFinished;

    /// <summary>
    /// <c>CardPile.get_Type</c> as reviewed (v0.107.1 to v0.113.1: <c>ldarg.0; ldfld &lt;Type&gt;k__BackingField; ret</c>, the field
    /// written by the constructor only) for rule P, or null (its code changed: other mods' patches on the pile's event stay strict).
    /// </summary>
    private static System.Reflection.MethodInfo? _pileType;
    private const string PileTypeFingerprint = "4B90F15B09BFA902";

    /// <summary>
    /// The getter's field, read in its place (review of 2026-11-19, C9: the soul's prefix and each flush asked the pile's type
    /// through the getter, which another mod may patch: its patch then ran more often than in the game), null when the getter's
    /// code isn't the pinned one (the getter is called then, as before C9); and the getter.
    /// </summary>
    private static AccessTools.FieldRef<CardPile, PileType>? _pileTypeField;
    private static System.Reflection.MethodInfo? _pileTypeGetter;

    /// <summary>
    /// The pile's type as its getter gives it: from the getter's field while its code is the pinned one (without running it), else
    /// from the getter itself (review D5: a changed getter loses rule P and the field read only, as before C9); null when another
    /// mod patches the getter (its answer may differ from the game's, and calling it would run their patch more often): the pile
    /// is then none of the souls' piles, the game's code for it.
    /// </summary>
    private static PileType? PileTypeOf(CardPile pile) =>
        // No tolerance: the getter is called only while nobody else patches it, whatever their patches would do.
        _pileTypeGetter == null || PatchSafety.IsPatchedByOthers(_pileTypeGetter) ? null
            : _pileTypeField != null ? _pileTypeField(pile) : pile.Type;

    /// <summary>The gated postfixes <see cref="PileGatesAllowed"/> last gave (the same array while its content is the same).</summary>
    private static System.Reflection.MethodInfo[] _pileGates = Array.Empty<System.Reflection.MethodInfo>();

    /// <summary>
    /// Other mods' patches on the soul's _Ready a skipped soul may leave out with the shape rules off: SpeedX's, which only
    /// speeds that soul up (fingerprinted). Any other patch there keeps every soul then. With them on, every patch on the
    /// soul's own display code stands (<see cref="_ownDisplay"/>), this one included.
    /// </summary>
    private static readonly (string Method, string Fingerprint)[] AllowedReadyPatches =
    {
        ("SpeedX.Scripts.Patches.CardFlyShuffleSpeedPatch:Postfix", "9F8A3DE9AFF615B7"),
    };
    private static AccessTools.FieldRef<NCombatCardPile, int>? _currentCount;
    private static int _mainThread;
    private static double _tokens = Burst;
    private static long _lastRefill;

    /// <summary>Souls not spawned per target pile since the last flush (one frame), and the flush's extra count.</summary>
    private static readonly List<(CardPile Pile, int Count)> _pending = new();
    private static int _extra;
    /// <summary>The frame the last flush was queued in: one lost (Godot drops deferred calls when its queue is full) is queued again.</summary>
    private static ulong _flushFrame;

    public static void Apply(Harmony harmony)
    {
        _create = Reflect.Method(typeof(NCardFlyShuffleVfx), nameof(NCardFlyShuffleVfx.Create))
            ?? throw new MissingMethodException(nameof(NCardFlyShuffleVfx), nameof(NCardFlyShuffleVfx.Create));
        PatchSafety.RequireFingerprint(_create, CreateFingerprint);
        PatchSafety.RequireFingerprint(Reflect.Method(typeof(NCardFlyShuffleVfx), "_Ready")
            ?? throw new MissingMethodException(nameof(NCardFlyShuffleVfx), "_Ready"), ReadyFingerprint);
        PatchSafety.RequireFingerprint(Reflect.AsyncMoveNext(Reflect.Method(typeof(NCardFlyShuffleVfx), "PlayAnim"))
            ?? throw new MissingMethodException(nameof(NCardFlyShuffleVfx), "PlayAnim"), PlayAnimFingerprint);
        var addCard = Reflect.Method(typeof(NCombatCardPile), "AddCard") ?? throw new MissingMethodException(nameof(NCombatCardPile), "AddCard");
        PatchSafety.RequireFingerprint(addCard, AddCardFingerprint);
        _currentCount = Reflect.FieldRefAccess<NCombatCardPile, int>("_currentCount");
        _addCard = addCard;
        _invokeAddFinished = Reflect.Method(typeof(CardPile), nameof(CardPile.InvokeCardAddFinished))
            ?? throw new MissingMethodException(nameof(CardPile), nameof(CardPile.InvokeCardAddFinished));
        _cardAddFinished = Reflect.FieldRefAccess<CardPile, Action?>("CardAddFinished");
        // The pile's type is read from the getter's field (C9) while the getter is the pinned one; a changed getter leaves only rule
        // P and the field read (D5): the getter is then called, as before C9.
        System.Reflection.MethodInfo typeGetter = Reflect.PropertyGetter(typeof(CardPile), nameof(CardPile.Type))
            ?? throw new MissingMethodException(nameof(CardPile), "get_Type");
        _pileTypeGetter = typeGetter;
        try
        {
            PatchSafety.RequireFingerprint(typeGetter, PileTypeFingerprint);
            _pileTypeField = Reflect.FieldRefAccess<CardPile, PileType>("<Type>k__BackingField");
            // Rule P: a postfix gated on the pile's type is judged by that type, which only the constructor writes.
            PatchSafety.RequireFieldWriters(typeof(CardPile), "<Type>k__BackingField", ".ctor");
            _pileType = typeGetter;
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: other mods' patches on a pile's card-added event stay strict ({e.Message})");
        }
        try
        {
            // Small (10 methods, ~10 ms): at Apply, so the merged counts are judged from the first reshuffle.
            _addCardSet = CodeSet.Compute(Name + " (pile counts)", new System.Reflection.MethodBase?[] { addCard }, 200, AddCardSets);
        }
        catch (Exception e)
        {
            Log.Warn($"[{ModEntry.Id}] {Name}: what a merged pile count leaves out couldn't be computed (one event per card): {e.Message}");
        }
        // The premise's code (the souls' visual classes): pinned, a change leaves CardFlightLimit off until reviewed.
        var premiseOnly = new List<System.Reflection.MethodBase?>();
        foreach ((Type type, string name, string[] fingerprints) in DisplayFingerprints)
        {
            System.Reflection.MethodBase method = DisplayMethod(type, name) ?? throw new MissingMethodException(type.Name, name);
            System.Reflection.MethodBase body = (method is System.Reflection.MethodInfo info ? Reflect.AsyncMoveNext(info) : null) ?? method;
            string actual = MethodFingerprint.Of(body);
            if (Array.IndexOf(fingerprints, actual) < 0)
            {
                throw new PatchSkippedException($"{type.Name}.{name} changed (fingerprint {actual}): the souls' display code is reviewed per version");
            }
            if (!(type == typeof(NCardTrailVfx) && name == "Create"))
            {
                premiseOnly.Add(method);
                if (!ReferenceEquals(body, method))
                {
                    premiseOnly.Add(body);
                }
            }
        }
        foreach ((string typeName, string name, string[] fingerprints) in TweenFingerprints)
        {
            if (typeof(NCardFlyShuffleVfx).Assembly.GetType(typeName) is not { } type)
            {
                continue; // v0.107.1, v0.111.0: no such helper, FadeOut tweens itself (pinned above).
            }
            System.Reflection.MethodBase method = (name == ".ctor"
                ? (System.Reflection.MethodBase?)type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).FirstOrDefault()
                : Reflect.Method(type, name)) ?? throw new MissingMethodException(type.Name, name);
            string actual = MethodFingerprint.Of(method);
            if (Array.IndexOf(fingerprints, actual) < 0)
            {
                throw new PatchSkippedException($"{type.Name}.{name} changed (fingerprint {actual}): the souls' display code is reviewed per version");
            }
        }
        System.Reflection.MethodInfo ready = Reflect.Method(typeof(NCardFlyShuffleVfx), "_Ready")!;
        System.Reflection.MethodInfo playAnim = Reflect.Method(typeof(NCardFlyShuffleVfx), "PlayAnim")!;
        _skipped = new System.Reflection.MethodBase?[]
        {
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Commands.SfxCmd), "PlayCardSwooshSfx"),
        };
        _ownDisplay = new System.Reflection.MethodBase?[]
        {
            playAnim, Reflect.AsyncMoveNext(playAnim),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NCardTrailVfx), "Create"),
        };
        _premiseOnly = premiseOnly.ToArray();
        _readyMethod = ready;
        _mainThread = Thread.CurrentThread.ManagedThreadId;
        PatchGuard.Patch(harmony, _create, prefix: new HarmonyMethod(typeof(CardFlightLimit), nameof(CreatePrefix)));
        PatchGuard.Patch(harmony, addCard, prefix: new HarmonyMethod(typeof(CardFlightLimit), nameof(AddCardPrefix)));
        Applied = true;
        // What CreatePrefix and OnlyPileButtons ask about (display only); the swoosh and the soul's own display code only with
        // the shape rules off (the whole flight set then, once built), SpeedX's allowed patch then looked up at the preflight.
        PatchSafety.Watch(Name, _create, PatchTolerance.NullSkipped);
        PatchSafety.Watch(Name, _addCard);
        PatchSafety.Watch(Name, _invokeAddFinished, PileGatesAllowed);
        PatchSafety.Watch(Name, _addCardSet?.Checked);
        var whole = new List<System.Reflection.MethodBase?> { _create };
        if (!PatchSafety.ShapeRules)
        {
            PatchSafety.Watch(Name, _skipped);
            PatchSafety.Watch(Name, _readyMethod, () => _allowedOnReady ??= AllowedReady());
            PatchSafety.Watch(Name, _ownDisplay);
            PatchSafety.WatchAll(Name, () => _flightSet?.Checked ?? Array.Empty<System.Reflection.MethodBase>());
            whole.AddRange(_skipped);
            whole.Add(_readyMethod);
            whole.AddRange(_ownDisplay);
        }
        // Display only: Create and what a skipped card skips gate every flight skipped (the menu's red title); AddCard only the
        // pile buttons' merged counts.
        PatchSafety.WholeMethods(Name, whole);
        // The game logic a skip removes (A4): strict whatever the shape rules, every soul kept by a patch there (once built).
        // Rule RO: other mods' read-only postfixes there stand (HeadAllowed), as the runtime check lets them.
        PatchSafety.WatchPairs(Name, () => (_endHead?.Checked ?? Array.Empty<System.Reflection.MethodBase>())
            .Select(m => ((System.Reflection.MethodBase?)m, (System.Reflection.MethodInfo[]?)HeadAllowed(m))));
        PatchSafety.WholeMethods(Name, () => _endHead?.Checked ?? Array.Empty<System.Reflection.MethodBase>());
        PatchSafety.WatchNote(Name, NoteLogic);
        // Other mods' patches on the soul's own display code (and null-gated postfixes on Create) left out for the souls skipped:
        // shown as kept (bdopt why, the menu's hover), not as a back-off.
        PatchSafety.WatchNote(Name, NoteOwnDisplay);
        PatchSafety.WatchNote(Name, NotePileGates);
        PatchSafety.WatchNote(Name, NoteReadOnlyHead);
        // The flight and combat-end sets, off the main thread while the game loads (P1): ready long before the menu's note steps
        // or the first soul need them.
        _background.Start();
    }

    private static bool CreatePrefix(CardPile targetPile, ref NCardFlyShuffleVfx? __result)
    {
        try
        {
            // A lost flush (below) is queued again by the next soul made, kept or skipped: a reshuffle ending with kept souls
            // doesn't wait for a later skip. Cards left pending only (nothing pending: one count read).
            if (_pending.Count > 0 && Thread.CurrentThread.ManagedThreadId == _mainThread)
            {
                QueueFlush(lost: true);
            }
            // A null pile (v0.107.1 reads card.Pile when the tween fires): the game's code, whatever it does with it.
            if (!Enabled || targetPile == null || PileTypeOf(targetPile) is not (PileType.Draw or PileType.Discard) || FlightSet() is not { Reviewed: true }
                // A skipped soul skips the method (and the calls it would make): only postfixes doing nothing for the null this
                // prefix leaves stand (NullSkipped, the game's own test-mode answer); what they do with a real soul is the soul's
                // display (_ownDisplay's premise).
                || Thread.CurrentThread.ManagedThreadId != _mainThread || PatchSafety.IsPatchedByOthers(_create, PatchTolerance.NullSkipped)
                || SkippedPatched(Caller) || !EndCheckAllows(Caller))
            {
                return true;
            }
            long now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(Burst, _tokens + (now - _lastRefill) * PerSecond / Stopwatch.Frequency);
            _lastRefill = now;
            if (_tokens >= 1)
            {
                _tokens--;
                return true;
            }
            Skipped++;
            Count(targetPile);
            // The game's callers add it through AddChildSafely (or ?.), which ignores null: Create is declared nullable and
            // returns null itself in the game's test mode, so a caller that dereferenced it would break that mode too.
            __result = null;
            return false;
        }
        catch (Exception e)
        {
            Disable(e);
            return true;
        }
    }

    private static System.Reflection.MethodInfo? _readyMethod;

    /// <summary>This file's path, as the runtime checks' [CallerFilePath] gives it (SkippedPatched's runtime call site).</summary>
    private static readonly string Caller = CallerPath();

    private static string CallerPath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    /// <summary>
    /// Whether a strict method of the skip is patched: <paramref name="caller"/> the runtime call site (this file) or the
    /// preflight's patch name (the kept note asks too: not counted as a runtime check).
    /// </summary>
    private static bool SkippedPatched(string caller)
    {
        if (PatchSafety.ShapeRules)
        {
            // Everything a skip removes calls to, the swoosh included: other mods' patches there are that soul's display (the
            // premise, option (a) of 2026-11-17).
            return false;
        }
        // The shape rules off: no tolerance, as before them (SpeedX's fingerprinted postfix on _Ready aside), on the swoosh, the
        // soul's own display code, and (since the audit of 2026-11-17) the whole flight set, other mods' overrides included.
        foreach (System.Reflection.MethodBase? method in _skipped)
        {
            if (PatchSafety.IsPatchedByOthers(method, caller))
            {
                return true;
            }
        }
        _allowedOnReady ??= AllowedReady();
        if (PatchSafety.IsPatchedByOthers(_readyMethod, _allowedOnReady, caller))
        {
            return true;
        }
        foreach (System.Reflection.MethodBase? method in _ownDisplay)
        {
            if (PatchSafety.IsPatchedByOthers(method, caller))
            {
                return true;
            }
        }
        // A3 (re-audit round 2): its delegates too, as its overrides (a combat hook subscription, a test hook; the log's listeners
        // and the task helper's exception callback are reached on error paths only, not checked, as the visual sets').
        return FlightSet() is not { } set || set.AnyPatched(caller) || set.ForeignOverrides() || GameHookDelegates.AnyForeign();
    }

    /// <summary>
    /// Whether skipping this soul leaves out none of another mod's code in the combat-end check (<see cref="_endHead"/>): the head
    /// free of it (a patch there recorded as a back-off of <paramref name="caller"/>), and the walk too or no walk possible now (a
    /// primary enemy alive).
    /// </summary>
    private static bool EndCheckAllows(string caller)
    {
        if (_endHead is not { Reviewed: true } head || _endWalk is not { Reviewed: true } walk || head.AnyPatched(caller, HeadAllowed) || head.ForeignOverrides()
            || GameHookDelegates.TestHookForeign())
        {
            return false;
        }
        if (!walk.AnyHasPatches() && !walk.ForeignOverrides() && !GameHookDelegates.Subscribed())
        {
            return true;
        }
        if (PrimaryEnemyAlive())
        {
            return true;
        }
#if BENCH
        KeptAtCombatEnd++;
#endif
        return false;
    }

#if BENCH
    /// <summary>Souls kept (not skipped) because the combat-end walk could run for them with another mod's code in it.</summary>
    public static long KeptAtCombatEnd { get; private set; }
#endif

    /// <summary>
    /// Whether a primary enemy is alive in the combat <c>CombatManager.IsEnding</c> reads (v0.107.1 <c>_state</c>, v0.111.0 and
    /// v0.113.1 <c>_turnState.State</c>: <c>DebugOnlyGetState()</c> on each), by the game's own test (<c>e != null &amp;&amp; e.IsAlive
    /// &amp;&amp; e.IsPrimaryEnemy</c>): then IsEnding returns before its listener walk. Only called with the head free of other mods'
    /// code (but read-only postfixes, rule RO, pure and unable to throw), so these reads have no side effects. False when it can't tell (the soul flies).
    /// </summary>
    private static bool PrimaryEnemyAlive()
    {
        try
        {
            if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() is not { } state)
            {
                return false;
            }
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Creatures.Creature> enemies = state.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] is { IsAlive: true, IsPrimaryEnemy: true })
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The flight set and the combat-end check's two parts, as one build gives them.</summary>
    private sealed record BuiltSets(CodeSet EndHead, CodeSet EndWalk, CodeSet Flight);

    /// <summary>
    /// The sets built on a background thread while the game loads (P1 of 2026-11-20), started at Apply: <see cref="BuildFlightSets"/>
    /// reads only the game's code and what Apply looked up first.
    /// </summary>
    private static readonly BackgroundBuild<BuiltSets> _background = new(Name + " sets", () => BuildFlightSets(null));

#if BENCH
    /// <summary>Test builds: where <see cref="_flightSet"/> came from ("background", "main thread"), null while not built.</summary>
    internal static string? SetsSource { get; private set; }
#endif

    /// <summary>
    /// The flight set (<see cref="_flightSet"/>) and the combat-end check's sets (<see cref="_endHead"/>), computed once (the
    /// preflight's note step on the menu, else the first soul made): taken from the background build when it is done, built on
    /// the main thread otherwise (never waiting for it); null when they can't be (logged once: no soul is skipped then).
    /// </summary>
    private static CodeSet? FlightSet()
    {
        if (_flightSet != null || _flightSetFailed || Thread.CurrentThread.ManagedThreadId != _mainThread)
        {
            return _flightSet;
        }
        if (_background.Result is { } built)
        {
            // In the order the main thread's build sets them.
            _endHead = built.EndHead;
            _endWalk = built.EndWalk;
            _flightSet = built.Flight;
#if BENCH
            SetsSource = "background";
#endif
            return _flightSet;
        }
        try
        {
            // Each part set as soon as it is built, as before (a later part that throws leaves the earlier ones set).
            _flightSet = BuildFlightSets((head, walk) =>
            {
                _endHead = head;
                _endWalk = walk;
            }).Flight;
#if BENCH
            SetsSource = "main thread";
#endif
        }
        catch (Exception e)
        {
            _flightSetFailed = true;
            Log.Warn($"[{ModEntry.Id}] {Name}: what a skipped flight leaves out couldn't be computed (no flight skipped): {e.Message}");
        }
        return _flightSet;
    }

    /// <summary>
    /// The sets from the game's code alone (its IL, the methods Apply looked up): the same on any thread and at any time; throws
    /// when one can't be computed. <paramref name="progress"/>: told each combat-end part once built (the main thread's build).
    /// </summary>
    private static BuiltSets BuildFlightSets(Action<CodeSet, CodeSet?>? progress)
    {
        const System.Reflection.BindingFlags declared = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        static IEnumerable<System.Reflection.MethodBase?> Whole(Type type) => type.GetMethods(declared).Cast<System.Reflection.MethodBase>()
            .Concat(type.GetConstructors(declared)).Where(m => m.GetMethodBody() != null);
        // The combat-end check's two parts first: a flight set without them skips nothing (EndCheckAllows).
        System.Reflection.MethodBase? walkRoot = Reflect.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), nameof(MegaCrit.Sts2.Core.Hooks.Hook.ShouldStopCombatFromEnding),
            new[] { typeof(MegaCrit.Sts2.Core.Combat.ICombatState) });
        Type manager = typeof(MegaCrit.Sts2.Core.Combat.CombatManager);
        CodeSet head = CodeSet.Compute(Name + " (combat end check)", new System.Reflection.MethodBase?[]
        {
            Reflect.Method(manager, "get_" + nameof(MegaCrit.Sts2.Core.Combat.CombatManager.IsEnding)),
            Reflect.Method(typeof(MegaCrit.Sts2.Core.Helpers.NonInteractiveMode), "get_" + nameof(MegaCrit.Sts2.Core.Helpers.NonInteractiveMode.IsActive)),
            Reflect.Method(manager, nameof(MegaCrit.Sts2.Core.Combat.CombatManager.DebugOnlyGetState)),
            Reflect.Method(manager, "get_" + nameof(MegaCrit.Sts2.Core.Combat.CombatManager.Instance)),
        }, 500, EndHeadSets, interfaces: true, stopAt: new[] { walkRoot ?? throw new MissingMethodException(nameof(MegaCrit.Sts2.Core.Hooks.Hook), "ShouldStopCombatFromEnding") },
            constantOverridesHarmless: true);
        progress?.Invoke(head, null);
        // Rule C0 (2026-11-18): another mod's override in either part that only returns a constant (a mod power's
        // OwnerIsSecondaryEnemy => true: Succubus, St2bs, erowatcher, Zone the Spire), unpatched, isn't other mods' code a skip
        // leaves out in any way that matters: it reads, writes and calls nothing (CodeSet.ConstantOverridesHarmless).
        CodeSet walk = CodeSet.Compute(Name + " (combat end walk)", new[] { walkRoot }, 500, EndWalkSets, interfaces: true, constantOverridesHarmless: true);
        progress?.Invoke(head, walk);
        // _Ready is checked with SpeedX's allowed postfix; the pile's event and the button's AddCard are still called (Flush).
        CodeSet flight = CodeSet.Compute(Name, Whole(typeof(NCardFlyShuffleVfx)).Concat(Whole(typeof(NCardTrailVfx))).Concat(Whole(typeof(NCardTrail))), 1500, FlightSets,
            new System.Reflection.MethodBase?[] { _readyMethod, _invokeAddFinished, _addCard });
        return new BuiltSets(head, walk, flight);
    }

#if BENCH
    /// <summary>
    /// Test builds (script <c>startsets</c>): the sets in use (and the background build's) against a build made now on the main
    /// thread, member by member; throws on any difference. The description of the comparison.
    /// </summary>
    internal static string BenchCompareSets()
    {
        CodeSet? flight = FlightSet();
        if (flight == null || _endHead == null || _endWalk == null)
        {
            throw new InvalidOperationException($"{Name}: no sets in use (background: {_background.Describe()})");
        }
        BuiltSets reference = BuildFlightSets(null);
        var diffs = new List<string>();
        void Compare(CodeSet head, CodeSet walk, CodeSet set)
        {
            CodeSet.BenchCompare("combat end check", head, reference.EndHead, diffs);
            CodeSet.BenchCompare("combat end walk", walk, reference.EndWalk, diffs);
            CodeSet.BenchCompare("flight", set, reference.Flight, diffs);
        }
        Compare(_endHead, _endWalk, flight);
        if (_background.Result is { } background && !ReferenceEquals(background.Flight, flight))
        {
            Compare(background.EndHead, background.EndWalk, background.Flight);
        }
        if (diffs.Count > 0)
        {
            throw new InvalidOperationException($"{Name}: sets differ from a main-thread build: {string.Join("; ", diffs)}");
        }
        return $"{Name}: sets from the {SetsSource} (background: {_background.Describe()}) identical to a main-thread build: "
            + $"combat end check {_endHead.Methods.Length} methods {_endHead.Fingerprint}, combat end walk {_endWalk.Methods.Length} {_endWalk.Fingerprint}, "
            + $"flight {flight.Methods.Length} {flight.Fingerprint}";
    }
#endif

    /// <summary>
    /// The kept note (display only): which other mods' patches (or hidden IL hooks) on the souls' own display code (_Ready, its
    /// flight, its trail, the rest of their classes' code a skip removes) and null-gated postfixes on Create don't run for the
    /// souls skipped. Cleared when there is none, with the shape rules off (back-offs then, or unchecked as before them), and
    /// while no soul is skipped because Create or a strict method backs off (the back-off shows instead).
    /// </summary>
    private static void NoteOwnDisplay()
    {
        var patched = new List<System.Reflection.MethodBase>();
        string[] overriders = Array.Empty<string>();
        if (PatchSafety.ShapeRules && _create != null && FlightSet() is { Reviewed: true } set
            && !PatchSafety.IsPatchedByOthers(_create, PatchTolerance.NullSkipped, Name) && !SkippedPatched(Name) && EndHeadClean(Name))
        {
            var own = new List<System.Reflection.MethodBase?> { _readyMethod };
            own.AddRange(_ownDisplay);
            own.AddRange(_premiseOnly);
            own.AddRange(_skipped);
            // Every method a skip removes calls to (the computed set), but those Flush still calls itself.
            own.AddRange(set.Checked);
            overriders = set.ForeignOverriders();
            foreach (System.Reflection.MethodBase? method in own)
            {
                if (method != null && PatchSafety.HasOtherPatches(method))
                {
                    patched.Add(method);
                }
            }
            // Create's postfixes stand only as no-ops for a null result: what they'd do with a real soul is left out too.
            if (PatchSafety.HasOtherPatches(_create))
            {
                patched.Add(_create);
            }
        }
        if (patched.Count == 0 && overriders.Length == 0)
        {
            PatchSafety.ClearKept(Name, "flight_display");
            return;
        }
        var names = new List<string>();
        foreach (System.Reflection.MethodBase method in patched)
        {
            string name = $"{method.DeclaringType?.Name}.{method.Name}";
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }
        // The mods by their Harmony patches, and those with only a hidden IL hook there (HarmonyWatch): named, never an empty list.
        var mods = new List<string>(PatchSafety.OtherModsPatching(patched));
        foreach (System.Reflection.MethodBase method in patched)
        {
            mods.AddRange(HarmonyWatch.HiddenOwners(method));
        }
        if (overriders.Length > 0)
        {
            // Other mods' classes overriding a game virtual method a flight calls: their code is left out the same way.
            names.Add("overridden by " + string.Join(", ", overriders.Take(6)) + (overriders.Length > 6 ? $" and {overriders.Length - 6} more" : ""));
        }
        PatchSafety.Kept(Name, "flight_display", $"other mods' code on a flying card's own visuals ({string.Join(", ", names)}) doesn't run "
            + "for the cards that don't fly: taken as display only (a stated premise: what a skipped flight leaves out is all made inside "
            + "a flight's lifetime, and display)", mods.ToArray());
    }

    /// <summary>The combat-end check's head free of other mods' code (no soul skipped otherwise).</summary>
    private static bool EndHeadClean(string caller) =>
        _endHead is { Reviewed: true } head && _endWalk is { Reviewed: true } && !head.AnyPatched(caller, HeadAllowed) && !head.ForeignOverrides() && !GameHookDelegates.TestHookForeign();

    /// <summary>
    /// Rule RO (2026-11-18): the other mods' postfixes on a method of the combat-end check's head that only read and change its
    /// answer (<see cref="PatchSafety.ReadOnlyPostfixes"/>: YutaMod's on <c>Creature.get_IsSecondaryEnemy</c>, reading the
    /// creature's own auto-properties). The soul a skip removes is the only user of the answer its call gets, and such a postfix
    /// does nothing else (no write, no call of its own beyond getters and its own pure methods, no throw): leaving that call out
    /// leaves out nothing of it. None with the shape rules off. Never throws.
    /// </summary>
    private static System.Reflection.MethodInfo[] HeadAllowed(System.Reflection.MethodBase method) =>
        PatchSafety.ShapeRules ? PatchSafety.ReadOnlyPostfixes(method) : Array.Empty<System.Reflection.MethodInfo>();

    /// <summary>The kept note of rule RO: other mods' read-only postfixes on the combat-end check's head let stand.</summary>
    private static void NoteReadOnlyHead()
    {
        var methods = new List<string>();
        var postfixes = new List<System.Reflection.MethodInfo>();
        if (Applied && !PatchSafety.IsFaulted(Name) && _endHead is { Reviewed: true } head)
        {
            foreach (System.Reflection.MethodBase method in head.Checked)
            {
                System.Reflection.MethodInfo[] allowed = HeadAllowed(method);
                if (allowed.Length > 0)
                {
                    methods.Add($"{method.DeclaringType?.Name}.{method.Name}");
                    postfixes.AddRange(allowed);
                }
            }
        }
        if (methods.Count == 0)
        {
            PatchSafety.ClearKept(Name, "end_readonly");
            return;
        }
        PatchSafety.Kept(Name, "end_readonly", $"other mods' code in the combat-end check a card flight's sound makes ({string.Join(", ", methods.Distinct())}) "
            + "only reads the game and changes that check's answer, which only the flights that don't happen would have used: kept",
            PatchSafety.ModNamesOf(postfixes.Select(p => p.DeclaringType?.Assembly)));
    }

    /// <summary>
    /// What no patch shows about the combat-end check (A4, re-audit round 2; patches on its head are back-offs of their own, the
    /// head being watched): a set not reviewed for this version, or other mods' classes overriding code of its head, or a test
    /// hook of theirs, keep every soul (a back-off note, whole); other mods' code in its walk (a combat hook subscription such as
    /// BaseLib's, classes overriding it, patches) keeps only the souls made while no primary enemy is alive (the log, once per
    /// change: not a back-off). Also builds the sets on the menu's frames.
    /// </summary>
    private static void NoteLogic()
    {
        try
        {
            if (FlightSet() == null || _endHead is not { } head || _endWalk is not { } walk)
            {
                PatchSafety.ClearNote(Name, "combat_end");
                return;
            }
            if (!head.Reviewed || !walk.Reviewed)
            {
                PatchSafety.Note(Name, "combat_end", "the game's combat-end check a card flight's sound makes isn't the one reviewed for this game version: every card flies",
                    Array.Empty<string>(), whole: true);
                return;
            }
            Type[] overriders = head.ForeignOverriderTypes();
            bool testHook = GameHookDelegates.TestHookForeign();
            if (overriders.Length > 0 || testHook)
            {
                var parts = new List<string>();
                if (testHook)
                {
                    parts.Add("a test hook (NonInteractiveMode.AutoSlayerCheck)");
                }
                if (overriders.Length > 0)
                {
                    parts.Add("classes overriding it: " + string.Join(", ", overriders.Select(t => t.FullName ?? t.Name).Distinct().Take(6))
                        + (overriders.Length > 6 ? $" and {overriders.Length - 6} more" : ""));
                }
                PatchSafety.Note(Name, "combat_end", $"other mods' code runs in the combat-end check every card flight's sound makes ({string.Join("; ", parts)}): "
                    + "game logic a skipped flight would leave out, so every card flies",
                    PatchSafety.ModNamesOf(overriders.Select(t => (System.Reflection.Assembly?)t.Assembly)), whole: true);
            }
            else
            {
                PatchSafety.ClearNote(Name, "combat_end");
            }
            Type[] walkOverriders = walk.ForeignOverriderTypes();
            string[] walkMods = PatchSafety.ModNamesOf(GameHookDelegates.ForeignAssemblies().Concat(walkOverriders.Select(t => (System.Reflection.Assembly?)t.Assembly)))
                .Concat(PatchSafety.OtherModsPatching(walk.Checked)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
            string walkText = walkMods.Length == 0 && !GameHookDelegates.Subscribed() && walkOverriders.Length == 0 && !walk.AnyHasPatches() ? ""
                : "other mods' code in the combat's end-of-combat listener walk (Hook.ShouldStopCombatFromEnding): every card flies while no primary enemy is alive ("
                    + (walkMods.Length > 0 ? string.Join(", ", walkMods) : "?") + ")";
            if (walkText != _walkLogged)
            {
                _walkLogged = walkText;
                if (walkText.Length > 0)
                {
                    Log.Info($"[{ModEntry.Id}] {Name}: {walkText}");
                }
            }
        }
        catch
        {
            // Display only (the runtime check backs off on its own).
        }
    }

    private static string _walkLogged = "";

    private static System.Reflection.MethodInfo[] AllowedReady()
    {
        var allowed = new List<System.Reflection.MethodInfo>();
        foreach ((string name, string fingerprint) in AllowedReadyPatches)
        {
            // In SpeedX's assembly only: without SpeedX, no scan of every loaded type (this runs in a main-menu preflight step).
            if (PatchSafety.MethodIn("SpeedX", name) is { } method && PatchSafety.FingerprintMatches(MethodFingerprint.Of(method), fingerprint))
            {
                allowed.Add(method);
            }
        }
        return allowed.ToArray();
    }

    /// <summary>
    /// A flush queued in a frame runs at its end (or the next one's, queued late); cards still pending two frames later mean it
    /// never ran (Godot drops a deferred call when its message queue is full): queued again then, by the next soul made
    /// (<see cref="CreatePrefix"/>) or skipped. Two flushes at worst: the second finds nothing (each takes what is pending and
    /// clears it). With no soul made at all afterwards, a pile button's count stays short until the next card leaves that pile
    /// (its RemoveCard takes the pile's own count back).
    /// </summary>
    private static void QueueFlush(bool lost)
    {
        ulong frame = Engine.GetProcessFrames();
        if ((!lost && _pending.Count == 0) || frame > _flushFrame + 1)
        {
            _flushFrame = frame;
            Callable.From(Flush).CallDeferred();
        }
    }

    private static void Count(CardPile pile)
    {
        QueueFlush(lost: false);
        for (int i = 0; i < _pending.Count; i++)
        {
            if (ReferenceEquals(_pending[i].Pile, pile))
            {
                _pending[i] = (pile, _pending[i].Count + 1);
                return;
            }
        }
        _pending.Add((pile, 1));
    }

    /// <summary>What the skipped souls would have told their pile on landing, one label fit per pile and frame.</summary>
    private static void Flush()
    {
        // A copy: the events run other code (another mod's listener) that may move cards and skip souls again, which
        // then queues them for the next flush instead of changing this list.
        (CardPile Pile, int Count)[] pending = _pending.ToArray();
        _pending.Clear();
        // Each pile on its own, as each soul lands on its own: one that throws doesn't lose what the others were owed.
        foreach ((CardPile pile, int count) in pending)
        {
            try
            {
                // After a fault AddCardPrefix may be gone (PatchGuard removes a faulted patch's methods), and one event would
                // count one card: one per card then, as the souls would, whatever the switches say.
                if (!PatchSafety.IsFaulted(Name) && OnlyPileButtons(pile))
                {
                    _extra = count - 1;
#if BENCH
                    if (count > 1)
                    {
                        MergedFlushes++;
                    }
#endif
                    pile.InvokeCardAddFinished();
                }
                else
                {
                    // Someone else listens (or AddCard is overridden or patched): one event per card, as the souls would.
                    for (int i = 0; i < count; i++)
                    {
                        pile.InvokeCardAddFinished();
                    }
                }
            }
            catch (Exception e)
            {
                if (!PatchSafety.IsFaulted(Name))
                {
                    Disable(e);
                }
                else
                {
                    Log.Error($"[{ModEntry.Id}] {Name}: a pile button's count failed after the fault: {e}");
                }
            }
            finally
            {
                _extra = 0;
            }
        }
    }

    /// <summary>
    /// Whether the pile's CardAddFinished only reaches pile buttons' own <c>NCombatCardPile.AddCard</c> (not overridden,
    /// not patched by another mod): one event then stands for several cards through <see cref="AddCardPrefix"/>.
    /// </summary>
    private static bool OnlyPileButtons(CardPile pile)
    {
        // No tolerance: one event stands for several cards, a patch would run fewer times; on the event's own method too (a
        // patch there would see one call for several cards, whoever listens), but for postfixes doing nothing on this pile's
        // type (rule P: Hermit's HandChangedPatches, acting on the hand only). Souls only target the draw and discard piles.
        if (PileTypeOf(pile) is not (PileType.Draw or PileType.Discard)
            || PatchSafety.IsPatchedByOthers(_addCard) || PatchSafety.IsPatchedByOthers(_invokeAddFinished, PileGatesAllowed())
            // What AddCard calls runs once for the merged cards too (audit of 2026-11-17, C): strict as AddCard itself.
            || _addCardSet is not { Reviewed: true } addCardSet || addCardSet.AnyPatched(Caller))
        {
            return false;
        }
        Action? handlers = _cardAddFinished!(pile);
        if (handlers == null)
        {
            return true;
        }
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            if (handler.Target is not NCombatCardPile || handler.Method.MethodHandle != _addCard!.MethodHandle)
            {
                return false;
            }
        }
        return true;
    }

#if BENCH
    /// <summary>Flushes that sent one pile event for several skipped souls (test builds: rule P's proof that merging still runs).</summary>
    public static long MergedFlushes { get; private set; }
#endif

    /// <summary>
    /// Rule P (2026-11-18): other mods' postfixes on <c>CardPile.InvokeCardAddFinished</c> that do nothing unless the pile's
    /// <c>Type</c> is some K other than Draw and Discard (<see cref="PatchSafety.ValueGatedPostfixes"/>: Downfall's Hermit, its
    /// hand-only <c>HandChangedPatches</c>). A merged event only goes to a draw or discard pile, whose type its constructor set and
    /// nothing changes: such a postfix does nothing for it, once or once per card. Any other patch there keeps one event per
    /// card. Empty with the shape rules off, before Apply, or while <c>get_Type</c> isn't the reviewed code. Never throws.
    /// </summary>
    private static System.Reflection.MethodInfo[] PileGatesAllowed()
    {
        try
        {
            if (!PatchSafety.ShapeRules || _pileType == null || PatchSafety.ValueGatedPostfixes(_invokeAddFinished, _pileType) is not { Length: > 0 } gates)
            {
                return _pileGates = Array.Empty<System.Reflection.MethodInfo>();
            }
            System.Reflection.MethodInfo[] allowed = gates.Where(g => g.Value != (int)PileType.Draw && g.Value != (int)PileType.Discard).Select(g => g.Patch).ToArray();
            if (!allowed.SequenceEqual(_pileGates))
            {
                _pileGates = allowed;
            }
            return _pileGates;
        }
        catch
        {
            return Array.Empty<System.Reflection.MethodInfo>();
        }
    }

    /// <summary>The kept note of rule P: other mods' postfixes on the pile event let stand (bdopt why, the menu's hover).</summary>
    private static void NotePileGates()
    {
        System.Reflection.MethodInfo[] gates = Applied && !PatchSafety.IsFaulted(Name) ? PileGatesAllowed() : Array.Empty<System.Reflection.MethodInfo>();
        if (gates.Length == 0)
        {
            PatchSafety.ClearKept(Name, "pile_gate");
            return;
        }
        PatchSafety.Kept(Name, "pile_gate", "other mods' code on a pile's card-added event (CardPile.InvokeCardAddFinished) that only acts on another kind "
            + "of pile (the hand, ...): the draw and discard piles' buttons still get one event for several cards", PatchSafety.ModNamesOf(gates.Select(g => g.DeclaringType?.Assembly)));
    }

    /// <summary>Vanilla adds one to the count (capped by the pile's size); a flush for several cards adds the others first.</summary>
    private static void AddCardPrefix(NCombatCardPile __instance)
    {
        if (_extra > 0)
        {
            _currentCount!(__instance) += _extra;
        }
    }

    public static void Disable(Exception e)
    {
        Enabled = false;
        PatchSafety.Fault(Name);
        Log.Error($"[{ModEntry.Id}] {Name} disabled after exception, falling back to vanilla: {e}");
    }
}
