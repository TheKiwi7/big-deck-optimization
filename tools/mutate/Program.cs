// Writes a modified copy of a game or mod assembly, to see how the mod reacts to an update of that code
// (tools/autotest/mutations.ps1 runs the game with each one and checks what was left vanilla).
// Usage: dotnet run --project tools/mutate -- <in.dll> <out.dll> <mutation> [assembly search dir...]
//        dotnet run --project tools/mutate -- list
using Mono.Cecil;
using Mono.Cecil.Cil;

var mutations = new Dictionary<string, (string What, Action<ModuleDefinition> Apply)>
{
    // --- game (sts2.dll), minor: same behavior ---
    ["renumber-hooks"] = ("compiler renumbering: every Hook state machine <X>d__N becomes d__N+7 (a member added above them)", m =>
    {
        foreach (TypeDefinition t in Type(m, "MegaCrit.Sts2.Core.Hooks.Hook").NestedTypes.Where(t => t.Name.Contains(">d__")))
        {
            int at = t.Name.LastIndexOf("d__", StringComparison.Ordinal) + 3;
            t.Name = t.Name[..at] + (int.Parse(t.Name[at..]) + 7);
        }
    }),
    ["new-hook"] = ("a new hook method: AbstractModel.AfterSomethingNew() (default `return Task.CompletedTask`) inserted first", m =>
    {
        TypeDefinition model = Type(m, "MegaCrit.Sts2.Core.Models.AbstractModel");
        MethodDefinition template = model.Methods.First(x => x.Name == "BeforeCombatStart" && !x.HasParameters);
        var added = new MethodDefinition("AfterSomethingNew", template.Attributes, template.ReturnType);
        foreach (Instruction i in template.Body.Instructions) added.Body.Instructions.Add(i.Operand is MethodReference r ? Instruction.Create(i.OpCode, r) : Instruction.Create(i.OpCode));
        model.Methods.Insert(0, added);
    }),
    // Not Log.Info any more (round 4): RelicFlashMerge's reviewed code sets are the whole game closure of a flash's visuals, the
    // logger among them (error paths), so a change there rightly leaves the merge off until reviewed. The credits screen's own
    // code is reached by no patch's set.
    ["unrelated-body"] = ("a method no patch relies on changed (NCreditsScreen.InitFmod)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Screens.Credits.NCreditsScreen", "InitFmod"), OpCodes.Ldarg_0, OpCodes.Pop)),
    // --- game, real changes of code a patch mirrors or relies on ---
    ["energy-hook"] = ("Hook.ModifyEnergyCostInCombat changed (HookFilter mirrors it)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Hooks.Hook", "ModifyEnergyCostInCombat"), OpCodes.Ldarg_2, OpCodes.Pop)),
    ["default-hitting"] = ("AbstractModel.ShouldAllowHitting default no longer the plain `return true` (HookFilter and HookSkip skip it)", m =>
    {
        MethodDefinition d = Method(m, "MegaCrit.Sts2.Core.Models.AbstractModel", "ShouldAllowHitting");
        d.Body.Instructions.Clear();
        ILProcessor il = d.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And); il.Emit(OpCodes.Ret);
    }),
    ["default-task"] = ("AbstractModel.AfterCardPlayed default changed (HookSkip's AfterCardPlayed relies on it)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Models.AbstractModel", "AfterCardPlayed"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["helper-push"] = ("PlayerChoiceContext.PushModel changed (hooks using choice contexts rely on it)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext", "PushModel"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["pile-add"] = ("CardPile.AddInternal changed (only moves applied to FastListeners' entries rely on its code; PileIndex checks each call)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "AddInternal"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["combat-walk"] = ("CombatState.IterateHookListeners (its iterator) changed (only FastListeners' mirror of that walk relies on it)", m =>
        Prepend(Type(m, "MegaCrit.Sts2.Core.Combat.CombatState").NestedTypes.First(t => t.Name.Contains("<IterateHookListeners>")).Methods.First(x => x.Name == "MoveNext"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["run-walk"] = ("RunState.IterateHookListeners (its iterator) changed (only FastListeners' mirror of that walk relies on it)", m =>
        Prepend(Type(m, "MegaCrit.Sts2.Core.Runs.RunState").NestedTypes.First(t => t.Name.Contains("<IterateHookListeners>")).Methods.First(x => x.Name == "MoveNext"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["pile-remove"] = ("CardPile.RemoveInternal changed (only moves applied to FastListeners' entries rely on its code; PileIndex checks each call)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "RemoveInternal"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["pile-writer"] = ("a new CardPile.ReverseInternal() reversing _cards (no fingerprinted method changes: PileIndex and FastListeners must notice the new writer)", m =>
    {
        TypeDefinition pile = Type(m, "MegaCrit.Sts2.Core.Entities.Cards.CardPile");
        FieldDefinition cards = pile.Fields.Single(f => f.Name == "_cards");
        var reverse = new MethodReference("Reverse", m.TypeSystem.Void, cards.FieldType) { HasThis = true };
        var added = new MethodDefinition("ReverseInternal", MethodAttributes.Public | MethodAttributes.HideBySig, m.TypeSystem.Void);
        ILProcessor il = added.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, cards); il.Emit(OpCodes.Callvirt, reverse); il.Emit(OpCodes.Ret);
        pile.Methods.Add(added);
    }),
    ["sync-body"] = ("Player.SyncWithSerializedPlayer changed (SyncedCardRelease relies on what it does with the old deck)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Entities.Players.Player", "SyncWithSerializedPlayer"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["allcards-reader"] = ("a new RunState method reading _allCards (SyncedCardRelease removes from that list because nothing but ContainsCard reads it)", m =>
    {
        TypeDefinition run = Type(m, "MegaCrit.Sts2.Core.Runs.RunState");
        FieldDefinition cards = run.Fields.Single(f => f.Name == "_allCards");
        var added = new MethodDefinition("AllCardsForTest", MethodAttributes.Public | MethodAttributes.HideBySig, cards.FieldType);
        ILProcessor il = added.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, cards); il.Emit(OpCodes.Ret);
        run.Methods.Add(added);
    }),
    ["squeeze-count"] = ("what Squeeze counts changed (DeckCountMemo mirrors it)", m =>
        Prepend(Type(m, "MegaCrit.Sts2.Core.Models.Cards.Squeeze").NestedTypes.First(t => t.Name == "<>c").Methods.First(x => x.Name.Contains("get_CanonicalVars") && x.ReturnType.Name == "Decimal"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["renumber-cards"] = ("compiler renumbering: Squeeze's <>c__DisplayClass6_0 becomes 13_0 (a member added above it), same code", m =>
    {
        foreach (TypeDefinition t in Type(m, "MegaCrit.Sts2.Core.Models.Cards.Squeeze").NestedTypes.Where(t => t.Name.Contains("DisplayClass")))
        {
            t.Name = "<>c__DisplayClass13_0";
        }
    }),
    ["hook-rename"] = ("Hook.ShouldAllowHitting renamed (the game's own callers follow): HookFilter's other hooks stay", m =>
        Method(m, "MegaCrit.Sts2.Core.Hooks.Hook", "ShouldAllowHitting").Name = "ShouldAllowHittingRenamed"),
    ["setup-turn"] = ("CombatManager.SetupPlayerTurn changed: only the first-turn filters leave, BetterSpire2's scope stays", m =>
        Prepend(Type(m, "MegaCrit.Sts2.Core.Combat.CombatManager").NestedTypes.First(t => t.Name.StartsWith("<SetupPlayerTurn>d__")).Methods.First(x => x.Name == "MoveNext"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["relic-holder"] = ("NRelicInventoryHolder.OnRelicFlashed changed: the relic bar's merge leaves, the sound's stays", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder", "OnRelicFlashed"), OpCodes.Ldarg_0, OpCodes.Pop)),
    // Every supported version has CopyBackup: on v0.113 SaveBackupCheck transpiles its whole-save check; on v0.111 a CopyBackup
    // that isn't v0.111's known one is no longer "not needed" but changed code. Either way the part is left vanilla.
    ["save-backup"] = ("GodotFileIo.CopyBackup changed (SaveBackupCheck transpiles its check of the previous save)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Saves.GodotFileIo", "CopyBackup"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["button-input"] = ("NButton._Input changed (IdleButtonInput keeps idle buttons from receiving input)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton", "_Input"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["wait-body"] = ("Cmd.Wait's state machine changed (FastCombatStart skips the call)", m =>
        Prepend(Type(m, "MegaCrit.Sts2.Core.Commands.Cmd").NestedTypes.First(t => t.Name.StartsWith("<Wait>d__")).Methods.First(x => x.Name == "MoveNext"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["rename-method"] = ("CardPile.RemoveInternal renamed (the game's own callers follow)", m =>
        Method(m, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "RemoveInternal").Name = "RemoveInternalRenamed"),
    ["rename-field"] = ("CombatState._allies renamed (FastListeners reads it by name)", m =>
        Type(m, "MegaCrit.Sts2.Core.Combat.CombatState").Fields.Single(f => f.Name == "_allies").Name = "_alliesRenamed"),
    // --- game, the parts of the audit rounds since v1.2.0: only that part may leave ---
    ["combat-contains"] = ("CombatState.ContainsCard changed (CombatCardSet answers for it from a count per card)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Combat.CombatState", "ContainsCard"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["read-file"] = ("GodotFileIo.ReadFile changed (SaveReadDecode transpiles its read as text)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Saves.GodotFileIo", "ReadFile"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["dynvar-tryget"] = ("DynamicVarSet.TryGetValue changed (CardSortKeys' shared-key comparison reads variables like it; v0.113+)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet", "TryGetValue"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["card-redraw"] = ("NCard.UpdateVisuals changed (DeckCountMemo answers inside that redraw)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Cards.NCard", "UpdateVisuals"), OpCodes.Ldarg_0, OpCodes.Pop)),
    // --- game, audit of 2026-11-21: fingerprinted methods of HoverTipReuse, GridSortCache, CombatCardSet, RelicFlashMerge ---
    ["show-hover-tips"] = ("NCreature.ShowHoverTips changed (HoverTipReuse transpiles it; its fingerprint pins the hide-then-show sequence)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Combat.NCreature", "ShowHoverTips"), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["grid-setcards"] = ("NCardGrid.SetCards changed (GridSortCache's index map relies on it only refilling _cards after the sort)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid", "SetCards"), OpCodes.Ldarg_1, OpCodes.Pop)),
    // CombatState has two AddCard: the public (card, owner) one calls the private (card) one that CombatCardSet fingerprints.
    ["combat-add-card"] = ("CombatState.AddCard(CardModel) changed (CombatCardSet counts the cards its _allCards.Add adds)", m =>
        Prepend(MethodWithParameters(m, "MegaCrit.Sts2.Core.Combat.CombatState", "AddCard", 1), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["combat-remove-card"] = ("CombatState.RemoveCard changed (CombatCardSet counts the cards its _allCards.Remove removes)", m =>
        Prepend(MethodWithParameters(m, "MegaCrit.Sts2.Core.Combat.CombatState", "RemoveCard", 1), OpCodes.Ldarg_1, OpCodes.Pop)),
    ["relic-doflash"] = ("NRelicInventoryHolder.DoFlash changed: the relic bar's merge leaves (it pins the flash's own code), the player handler's stays", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventoryHolder", "DoFlash"), OpCodes.Ldarg_0, OpCodes.Pop)),
    // --- game, v0.9.0 patches: only that part may leave ---
    ["flight-count"] = ("NCombatCardPile.AddCard changed (CardFlightLimit adds to its count)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile", "AddCard"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["replay-record"] = ("CombatReplayWriter.RecordChecksum changed (ReplayStateSkip skips it)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplayWriter", "RecordChecksum"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["theme-rename"] = ("NHealthBar.RefreshText renamed (ThemeColorSkip transpiles it by name; the game's own callers follow)", m =>
        Method(m, "MegaCrit.Sts2.Core.Nodes.Combat.NHealthBar", "RefreshText").Name = "RefreshTextRenamed"),
    ["upgrade-writer"] = ("a new CardModel method writing _currentUpgradeLevel (trusted piles watch that field's writers)", m =>
    {
        TypeDefinition card = Type(m, "MegaCrit.Sts2.Core.Models.CardModel");
        FieldDefinition level = card.Fields.Single(f => f.Name == "_currentUpgradeLevel");
        var added = new MethodDefinition("SetLevelDirectly", MethodAttributes.Public | MethodAttributes.HideBySig, m.TypeSystem.Void);
        ILProcessor il = added.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, level); il.Emit(OpCodes.Ret);
        card.Methods.Add(added);
    }),
    ["write-list"] = ("PacketWriter.WriteList changed (SnapshotCardReuse mirrors it for piles: only pile bits may leave)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter", "WriteList"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["local-keywords"] = ("CardModel.LocalKeywords changed (SnapshotCardReuse reads it without allocating)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Models.CardModel", "get_LocalKeywords"), OpCodes.Ldarg_0, OpCodes.Pop)),
    // (Making the CardModel.EnergyCost property internal breaks the game itself: its serialization reads public properties.)
    ["internal-member"] = ("CardEnergyCost.GetWithModifiers made internal (HookFilter's cost color calls it directly; the game's own callers are in the same assembly)", m =>
    {
        MethodDefinition method = Method(m, "MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost", "GetWithModifiers");
        method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Assembly;
    }),
    ["signature"] = ("AbstractModel.ShouldPlay gains a parameter (HookFilter's ShouldPlay mirror calls it)", m =>
    {
        MethodDefinition d = Method(m, "MegaCrit.Sts2.Core.Models.AbstractModel", "ShouldPlay");
        d.Parameters.Add(new ParameterDefinition("extra", ParameterAttributes.None, m.TypeSystem.Int32));
        // Every call in the game passes 0 for it; overrides get the parameter too so they still override.
        foreach (TypeDefinition t in m.GetTypes())
        {
            foreach (MethodDefinition o in t.Methods.Where(x => x != d && x.Name == "ShouldPlay" && x.IsVirtual && x.Parameters.Count == d.Parameters.Count - 1))
                o.Parameters.Add(new ParameterDefinition("extra", ParameterAttributes.None, m.TypeSystem.Int32));
            foreach (MethodDefinition caller in t.Methods.Where(x => x.HasBody))
            {
                ILProcessor il = caller.Body.GetILProcessor();
                foreach (Instruction call in caller.Body.Instructions.Where(i => i.Operand is MethodDefinition r && r.Name == "ShouldPlay" && r.Parameters.LastOrDefault()?.Name == "extra").ToList())
                    il.InsertBefore(call, il.Create(OpCodes.Ldc_I4_0));
            }
        }
    }),
    // --- mods ---
    ["limitless-body"] = ("Limitless: MultiEnchantment.SetSerializableCards changed (LimitlessReserialize mirrors it)", m =>
        Prepend(m.GetTypes().Where(t => t.Name == "MultiEnchantment").SelectMany(t => t.Methods).First(x => x.Name == "SetSerializableCards"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["limitless-clone"] = ("Limitless: MultiEnchantment.DeepCloneFields changed (LimitlessCloneMemo stands in for what it rebuilds)", m =>
        Prepend(m.GetTypes().Where(t => t.Name == "MultiEnchantment").SelectMany(t => t.Methods).First(x => x.Name == "DeepCloneFields"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["limitless-tabs"] = ("Limitless: CardExtensions.UpdateEnchantmentVisuals changed (EnchantmentTabReuse mirrors it)", m =>
        Prepend(m.GetTypes().SelectMany(t => t.Methods).First(x => x.Name == "UpdateEnchantmentVisuals" && x.HasBody), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["limitless-forward"] = ("Limitless: MultiEnchantment.AfterCardPlayed changed (LimitlessHookSkip relies on it only forwarding)", m =>
        Prepend(m.GetTypes().First(t => t.Name.Contains("<AfterCardPlayed>") && t.DeclaringType?.Name == "MultiEnchantment").Methods.First(x => x.Name == "MoveNext"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["limitless-writer"] = ("Limitless: a new MultiEnchantment method writing its saved list (trusted piles watch that field's writers)", m =>
    {
        TypeDefinition multi = m.GetTypes().First(t => t.Name == "MultiEnchantment");
        FieldDefinition list = multi.Fields.Single(f => f.Name == "<SerializableEnchantmentsOnCards>k__BackingField");
        var added = new MethodDefinition("ResetList", MethodAttributes.Public | MethodAttributes.HideBySig, m.TypeSystem.Void);
        ILProcessor il = added.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, list); il.Emit(OpCodes.Stfld, list); il.Emit(OpCodes.Ret);
        multi.Methods.Add(added);
    }),
    ["limitless-recalc"] = ("Limitless: MultiEnchantment.RecalculateValues changed (LimitlessRecalcSkip relies on it)", m =>
        Prepend(m.GetTypes().Where(t => t.Name == "MultiEnchantment").SelectMany(t => t.Methods).First(x => x.Name == "RecalculateValues"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["clone-from"] = ("CardModel.FromSerializable changed (LimitlessCloneMemo checks the game's clone chain, GameChain)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Models.CardModel", "FromSerializable"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["execfinished-add"] = ("AbstractModel.add_ExecutionFinished changed (FastListeners caches hooks keeping subscribers only while it knows every writer)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Models.AbstractModel", "add_ExecutionFinished"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["execfinished-clone"] = ("AbstractModel.AfterCloned changed (it clears a clone's ExecutionFinished subscribers: FastListeners relies on it)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Models.AbstractModel", "AfterCloned"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["hand-revalidate"] = ("NPlayerHand.RevalidateSelectionAfterStateChange changed (ListenerCache suspends its dispatch scope around what it resumes)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand", "RevalidateSelectionAfterStateChange"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["hand-select"] = ("NPlayerHand.SelectCards (its state machine) changed (ListenerCache relies on how it makes the selection's task)", m =>
        Prepend(Type(m, "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand").NestedTypes.First(t => t.Name.Contains("<SelectCards>")).Methods.First(x => x.Name == "MoveNext"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["orb-enqueue"] = ("OrbQueue.TryEnqueue changed (ListenerCache invalidates after its first await, which comes after the orb is added)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue", "TryEnqueue"), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["limitless-getter"] = ("Limitless: MultiEnchantment.EnchantmentsOnCards getter changed (LimitlessHookSkip relies on what it hands out)", m =>
        Prepend(m.GetTypes().Where(t => t.Name == "MultiEnchantment").SelectMany(t => t.Methods).First(x => x.Name == "get_EnchantmentsOnCards" && x.HasBody), OpCodes.Ldarg_0, OpCodes.Pop)),
    ["baselib-plaque"] = ("BaseLib: AddSubtypesToTypePlaquePatch.TryModifyPlaqueText changed (TypePlaqueSkip relies on it)", m =>
        Prepend(m.GetTypes().SelectMany(t => t.Methods).First(x => x.Name == "TryModifyPlaqueText" && x.HasBody), OpCodes.Ldarg_0, OpCodes.Pop)),
};

if (args.Length == 0 || args[0] == "list")
{
    foreach (var (name, (what, _)) in mutations) Console.WriteLine($"{name,-16} {what}");
    return 0;
}
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
foreach (string dir in args.Skip(3)) resolver.AddSearchDirectory(dir);
using (ModuleDefinition module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
{
    mutations[args[2]].Apply(module);
    module.Write(args[1]);
}
Console.WriteLine($"{args[2]}: {mutations[args[2]].What} -> {args[1]}");
return 0;

static TypeDefinition Type(ModuleDefinition m, string name) => m.GetType(name) ?? throw new Exception("no type " + name);

static MethodDefinition Method(ModuleDefinition m, string type, string name) => Type(m, type).Methods.First(x => x.Name == name && x.HasBody);

// The overload with that many parameters (the game has two methods of one name where a patch pins one).
static MethodDefinition MethodWithParameters(ModuleDefinition m, string type, string name, int parameters) =>
    Type(m, type).Methods.Single(x => x.Name == name && x.HasBody && x.Parameters.Count == parameters);

// A load and a pop at the start: the method does the same, its code (and fingerprint) changed.
static void Prepend(MethodDefinition method, OpCode load, OpCode pop)
{
    ILProcessor il = method.Body.GetILProcessor();
    Instruction first = method.Body.Instructions[0];
    il.InsertBefore(first, il.Create(load));
    il.InsertBefore(first, il.Create(pop));
}
