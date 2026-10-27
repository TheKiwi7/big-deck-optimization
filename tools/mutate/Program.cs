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
    ["unrelated-body"] = ("a method no patch relies on changed (Log.Info)", m =>
        Prepend(Method(m, "MegaCrit.Sts2.Core.Logging.Log", "Info"), OpCodes.Ldarg_0, OpCodes.Pop)),
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
    ["pile-remove"] = ("CardPile.RemoveInternal changed (PileIndex and FastListeners rely on it)", m =>
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
    ["rename-method"] = ("CardPile.RemoveInternal renamed (the game's own callers follow)", m =>
        Method(m, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "RemoveInternal").Name = "RemoveInternalRenamed"),
    ["rename-field"] = ("CombatState._allies renamed (FastListeners reads it by name)", m =>
        Type(m, "MegaCrit.Sts2.Core.Combat.CombatState").Fields.Single(f => f.Name == "_allies").Name = "_alliesRenamed"),
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
    ["limitless-tabs"] = ("Limitless: CardExtensions.UpdateEnchantmentVisuals changed (EnchantmentTabReuse mirrors it)", m =>
        Prepend(m.GetTypes().SelectMany(t => t.Methods).First(x => x.Name == "UpdateEnchantmentVisuals" && x.HasBody), OpCodes.Ldarg_0, OpCodes.Pop)),
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

// A load and a pop at the start: the method does the same, its code (and fingerprint) changed.
static void Prepend(MethodDefinition method, OpCode load, OpCode pop)
{
    ILProcessor il = method.Body.GetILProcessor();
    Instruction first = method.Body.Instructions[0];
    il.InsertBefore(first, il.Create(load));
    il.InsertBefore(first, il.Create(pop));
}
