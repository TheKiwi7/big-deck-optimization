using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace BigDeckOptimization.Patches;

/// <summary>
/// The second fingerprint of a display set (<see cref="CodeSet"/>), asked only when the exact one isn't a reviewed one: what the
/// set does, not how its display code is cut into methods. A game update that moves an expression of shared display code into a
/// helper, or that shifts the compiler's numbering of lambdas (v0.114.0: <c>AssetCache.IsOwnedByAtlasManager</c>), keeps it;
/// anything the set newly touches, or no longer touches, changes it.
///
/// Two parts, hashed together:
/// - exact: the roots' own classes (the code the skipped call is) and every member of the game outside the display namespaces
///   (combat, run, models, cards, random numbers, hooks, commands, saves...): name and code fingerprint, as the exact set
///   fingerprint has them, the compiler's numbering normalized;
/// - the display namespaces (<see cref="Display"/>): not the methods but the union of what their code touches: fields read and
///   written, members of other assemblies called or constructed (generic arguments included), game members of the exact part
///   called, game virtual calls, strings (node paths, names passed to the engine), indirect writes and calls, throws.
/// With the delegates invoked, as the exact fingerprint.
///
/// Why it is enough: the display code can only change the game's state through a field it writes, a collection it reads from a
/// field, or a call; each new one is a new line. What is left is the same accesses in another arrangement, in code that isn't the
/// skipped call's own: what they reach of the game's state is the exact part, which only reads it (checked when a value is
/// pinned: tools/fingerprint <c>effects</c> lists the exact part's field writes). Random numbers: the display code draws none (a draw
/// from it would be a new call line); the exact part's draws come from <c>Rng.Chaotic</c>, in pinned code.
/// </summary>
internal static class SetEffects
{
    private static readonly string[] Display =
    {
        "MegaCrit.Sts2.Core.Nodes", "MegaCrit.Sts2.Core.Assets", "MegaCrit.Sts2.Core.Helpers", "MegaCrit.Sts2.Core.Logging",
        "MegaCrit.Sts2.Core.Debug", "MegaCrit.Sts2.Core.Localization", "MegaCrit.Sts2.Core.Platform",
    };

    /// <summary>Display code, judged by what it touches: a member of the display namespaces, not of a root's class.</summary>
    internal static bool IsDisplay(MethodBase method, HashSet<Type> rootTypes)
    {
        Type? outer = Outermost(method.DeclaringType);
        return outer?.Namespace is { } ns && !rootTypes.Contains(outer)
            && Display.Any(d => ns == d || ns.StartsWith(d + ".", StringComparison.Ordinal));
    }

    internal static HashSet<Type> RootTypes(IEnumerable<MethodBase> roots) =>
        new(roots.Select(r => Outermost(r.DeclaringType)).Where(t => t != null)!);

    private static Type? Outermost(Type? type)
    {
        while (type?.DeclaringType is { } outer)
        {
            type = outer;
        }
        return type is { IsGenericType: true, IsGenericTypeDefinition: false } ? type.GetGenericTypeDefinition() : type;
    }

    /// <summary>The fingerprint of <see cref="Lines"/>.</summary>
    public static string Fingerprint(Assembly game, IReadOnlyCollection<MethodBase> roots, IReadOnlyCollection<MethodBase> set, IEnumerable<string> delegates) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Lines(game, roots, set, delegates)))))[..16];

    /// <summary>What is hashed, sorted. Throws when a method's code can't be read whole (the caller then has no second fingerprint).</summary>
    public static List<string> Lines(Assembly game, IReadOnlyCollection<MethodBase> roots, IReadOnlyCollection<MethodBase> set, IEnumerable<string> delegates)
    {
        HashSet<Type> rootTypes = RootTypes(roots);
        // Every exact member counts, several of one normalized name too (a class's lambdas).
        var exact = new List<string>();
        var touched = new HashSet<string>(delegates.Select(d => "delegate " + MethodFingerprint.Normalize(d)));
        foreach (MethodBase method in set)
        {
            if (IsDisplay(method, rootTypes))
            {
                Touch(game, method, rootTypes, touched);
            }
            else
            {
                exact.Add("= " + Name(method) + " " + MethodFingerprint.Of(method));
            }
        }
        return exact.Concat(touched).OrderBy(line => line, StringComparer.Ordinal).ToList();
    }

    private static string Name(MemberInfo member) => MethodFingerprint.Normalize(MethodFingerprint.MemberName(member));

    private static void Touch(Assembly game, MethodBase method, HashSet<Type> rootTypes, HashSet<string> touched)
    {
        if (method.GetMethodBody() == null)
        {
            touched.Add("no body " + Name(method));
            return;
        }
        foreach (MethodFingerprint.Instruction ins in MethodFingerprint.Instructions(method, strict: true))
        {
            switch (ins.Operand)
            {
                case FieldInfo field:
                    // A lambda's captures and caches, a state machine's locals: the method's own variables.
                    if (field.Module.Assembly != game || !MethodFingerprint.IsGeneratedType(field.DeclaringType!))
                    {
                        bool writes = ins.Op == OpCodes.Stfld || ins.Op == OpCodes.Stsfld;
                        bool address = ins.Op == OpCodes.Ldflda || ins.Op == OpCodes.Ldsflda;
                        touched.Add((writes ? "write " : address ? "address " : "read ") + Name(field));
                    }
                    break;
                case MethodBase callee when callee.Module.Assembly != game:
                    touched.Add(ins.Op.Name + " " + Name(callee));
                    break;
                case MethodBase callee:
                    MethodBase definition = MethodFingerprint.Definition(callee);
                    if (!IsDisplay(definition, rootTypes) || definition is MethodInfo { IsVirtual: true, IsFinal: false })
                    {
                        touched.Add("game " + Name(definition));
                    }
                    break;
                case string text:
                    touched.Add("string " + text);
                    break;
                default:
                    // Writes through a reference, indirect calls, throws, raw memory.
                    if (ins.Op == OpCodes.Calli || ins.Op == OpCodes.Throw || ins.Op == OpCodes.Rethrow || ins.Op == OpCodes.Cpblk || ins.Op == OpCodes.Initblk
                        || ins.Op.Name!.StartsWith("stind", StringComparison.Ordinal) || ins.Op.Name.StartsWith("stelem", StringComparison.Ordinal)
                        || ins.Op == OpCodes.Stobj || ins.Op == OpCodes.Cpobj)
                    {
                        touched.Add(ins.Op.Name + (ins.Operand is Type type ? " " + MethodFingerprint.Normalize(MethodFingerprint.TypeName(type)) : ""));
                    }
                    break;
            }
        }
    }
}
