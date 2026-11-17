using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Fingerprint of a method's code that changes exactly when what the method does changes:
/// <list type="bullet">
/// <item>metadata tokens (which shift whenever anything else in the assembly changes) are replaced by the names of the
/// members they point to;</item>
/// <item>the compiler's numbering of generated members (<c>&lt;Hook&gt;d__42</c>, <c>&lt;&gt;c__DisplayClass12_0</c>,
/// <c>b__12_0</c>...) is ignored: it shifts when the game adds a member above, with the same code;</item>
/// <item>the code of the generated methods it uses is included (lambdas, local functions, the state machine of an async or
/// iterator method), recursively: they are part of the method's source even though they live in other methods.</item>
/// </list>
/// Patches that mirror or rely on another method's code store its fingerprint and only turn on when it matches, so an
/// update of the game or of another mod disables them only if it touches that code (tools/fpdiff reviews the others).
/// </summary>
public static class MethodFingerprint
{
    private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>First 16 hex digits of the SHA-256 of <see cref="Describe"/>.</summary>
    public static string Of(MethodBase method)
    {
        // A stand-in object another mod's reflection answered with (not the runtime's own method, or a body from another
        // module than its type's) has no code of its own to compare: a fingerprint no method matches, so what relies on it
        // stays vanilla.
        if (method.GetType().Assembly != typeof(object).Assembly || (method.DeclaringType != null && method.Module != method.DeclaringType.Module))
        {
            return ForeignMember;
        }
#if BENCH
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Describe(method)));
#if BENCH
        LoadTicks[0] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
#endif
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>The fingerprint of a method that isn't the runtime's own (not hex: never equal to a real one).</summary>
    public const string ForeignMember = "foreign-member!!";

#if BENCH
    /// <summary>Load time spent in: fingerprints, field writer scans, load checks of patch classes, Harmony patching.</summary>
    internal static readonly long[] LoadTicks = new long[4];
    internal static string LoadBreakdown() => string.Join(", ", new[] { "fingerprints", "field writers", "load checks", "Harmony" }
        .Select((name, i) => $"{name} {LoadTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms"));
#endif

    /// <summary>
    /// The method's own code (<see cref="DescribeOne"/>), then, after a <c>== name</c> line each, the code of every
    /// compiler-generated method it uses, in first-use order, with generated-member numbering normalized.
    /// </summary>
    public static string Describe(MethodBase method)
    {
        StringBuilder sb = new();
        var seen = new HashSet<MethodBase> { method };
        var queue = new Queue<MethodBase>();
        sb.Append(DescribeOne(method, m => Enqueue(m, method.Module, seen, queue)));
        while (queue.Count > 0)
        {
            MethodBase generated = queue.Dequeue();
            sb.Append("== ").Append(Normalize(MemberName(generated))).Append('\n');
            sb.Append(generated.GetMethodBody() == null ? "(no body)\n" : DescribeOne(generated, m => Enqueue(m, method.Module, seen, queue)));
        }
        return sb.ToString();
    }

    /// <summary>Compiler-generated numbering: d__N, DisplayClassN_M, b__N_M, &lt;&gt;9__N_M, local functions |N_M, &lt;N&gt;__ caches, anonymous types.</summary>
    private static readonly Regex _generatedNumber = new(@"(d__|DisplayClass|b__|9__|\||AnonymousType|<)\d+(_\d+)?(?=\W|$)", RegexOptions.Compiled);

    private static string Normalize(string name) => name.Contains('<') ? _generatedNumber.Replace(name, "$1#") : name;

    /// <summary>Queues the generated code a member of the same module stands for (the lambda itself, a state machine's MoveNext).</summary>
    private static void Enqueue(MemberInfo member, Module module, HashSet<MethodBase> seen, Queue<MethodBase> queue)
    {
        if (member.Module != module)
        {
            return;
        }
        MethodBase? target = null;
        if (member is MethodBase m && !m.IsConstructor && IsGenerated(m))
        {
            target = m;
        }
        else if (member is Type or MethodBase or FieldInfo)
        {
            // A state machine type (its construction, fields or type token): the code runs in its MoveNext.
            Type? type = member as Type ?? member.DeclaringType;
            if (type != null && IsGeneratedType(type)
                && (typeof(IAsyncStateMachine).IsAssignableFrom(type) || typeof(System.Collections.IEnumerator).IsAssignableFrom(type)))
            {
                target = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
        }
        if (target != null && seen.Add(target))
        {
            queue.Enqueue(target);
        }
    }

    private static bool IsGenerated(MethodBase m) => m.Name.Contains('<') || (m.DeclaringType != null && IsGeneratedType(m.DeclaringType));

    private static bool IsGeneratedType(Type type)
    {
        for (Type? t = type; t != null; t = t.DeclaringType)
        {
            if (t.Name.Contains('<'))
            {
                return true;
            }
            if (!t.IsNested)
            {
                break;
            }
        }
        return false;
    }

    /// <summary>One line per instruction of this method only, tokens resolved to (normalized) member names, plus locals and exception clauses.</summary>
    public static string DescribeOne(MethodBase method, Action<MemberInfo>? used = null)
    {
        MethodBody body = method.GetMethodBody() ?? throw new ArgumentException($"{method} has no body");
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        StringBuilder sb = new();
        foreach (LocalVariableInfo local in body.LocalVariables)
        {
            sb.Append("local ").Append(Normalize(TypeName(local.LocalType))).Append('\n');
        }
        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            sb.Append("eh ").Append(clause.Flags).Append(' ').Append(clause.TryOffset).Append(' ').Append(clause.TryLength)
                .Append(' ').Append(clause.HandlerOffset).Append(' ').Append(clause.HandlerLength).Append(' ')
                .Append(clause.Flags == ExceptionHandlingClauseOptions.Clause ? Normalize(TypeName(clause.CatchType)) : "")
                .Append('\n');
        }
        int pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[pos++]);
            }
            OpCode op = _opCodes[value];
            sb.Append(op.Name);
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    sb.Append(' ').Append(il[pos]);
                    pos += 1;
                    break;
                case OperandType.InlineVar:
                    sb.Append(' ').Append(BitConverter.ToUInt16(il, pos));
                    pos += 2;
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.ShortInlineR:
                    sb.Append(' ').Append(BitConverter.ToInt32(il, pos));
                    pos += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    sb.Append(' ').Append(BitConverter.ToInt64(il, pos));
                    pos += 8;
                    break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    for (int i = 0; i < count; i++, pos += 4)
                    {
                        sb.Append(' ').Append(BitConverter.ToInt32(il, pos));
                    }
                    break;
                case OperandType.InlineString:
                    sb.Append(" \"").Append(module.ResolveString(BitConverter.ToInt32(il, pos))).Append('"');
                    pos += 4;
                    break;
                case OperandType.InlineSig:
                    // Calli signatures: rare and token-based; keep only the opcode.
                    pos += 4;
                    break;
                default: // InlineField, InlineMethod, InlineType, InlineTok
                    MemberInfo? member = module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, methodArgs);
                    sb.Append(' ').Append(Normalize(MemberName(member)));
                    if (member != null)
                    {
                        used?.Invoke(member);
                    }
                    pos += 4;
                    break;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The types and members the method's code uses (locals, catch types, instruction operands). Enumerating throws
    /// (MissingMethodException, MissingFieldException, TypeLoadException...) when the loaded assemblies lack one.
    /// </summary>
    public static IEnumerable<MemberInfo> UsedMembers(MethodBase method)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            yield break;
        }
        foreach (LocalVariableInfo local in body.LocalVariables)
        {
            yield return local.LocalType;
        }
        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            if (clause.Flags == ExceptionHandlingClauseOptions.Clause)
            {
                yield return clause.CatchType!;
            }
        }
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[pos++]);
            }
            OpCode op = _opCodes[value];
            switch (op.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, methodArgs);
                    if (member != null)
                    {
                        yield return member;
                    }
                    pos += 4;
                    break;
                case OperandType.InlineSwitch:
                    pos += 4 + 4 * BitConverter.ToInt32(il, pos);
                    break;
                default:
                    pos += op.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        _ => 4,
                    };
                    break;
            }
        }
    }

    /// <summary>
    /// The compiler-generated code that is part of this method's source, recursively: lambdas and local functions it uses,
    /// and every method of the state machines (async, iterator) and display classes it creates. Each token is resolved on
    /// its own and one that doesn't resolve is passed over: the method may use members another game version lacks
    /// (<see cref="PatchPartAttribute"/>), and what it uses after them still counts.
    /// </summary>
    public static List<MethodBase> GeneratedCode(MethodBase method)
    {
        var found = new List<MethodBase>();
        var seen = new HashSet<MethodBase> { method };
        var types = new HashSet<Type>();
        var queue = new Queue<MethodBase>();
        queue.Enqueue(method);
        void Add(MethodBase m)
        {
            if (seen.Add(m))
            {
                found.Add(m);
                queue.Enqueue(m);
            }
        }
        void AddType(Type type)
        {
            if (!types.Add(type))
            {
                return;
            }
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (MethodBase m in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                Add(m);
            }
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                AddType(nested);
            }
        }
        while (queue.Count > 0)
        {
            MethodBase current = queue.Dequeue();
            byte[] il = current.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            Type[]? typeArgs = current.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
            Type[]? methodArgs = current.IsGenericMethod ? current.GetGenericArguments() : null;
            int pos = 0;
            while (pos < il.Length)
            {
                short value = il[pos++];
                if (value == 0xFE)
                {
                    value = (short)(0xFE00 | il[pos++]);
                }
                OpCode op = _opCodes[value];
                if (op.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
                {
                    MemberInfo? member;
                    try
                    {
                        member = current.Module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, methodArgs);
                    }
                    catch (Exception)
                    {
                        member = null; // Missing in this game version: not generated code of this module either way.
                    }
                    pos += 4;
                    if (member == null || member.Module != method.Module)
                    {
                        continue;
                    }
                    Type? owner = member as Type ?? member.DeclaringType;
                    if (owner != null && IsGeneratedType(owner)
                        && (typeof(IAsyncStateMachine).IsAssignableFrom(owner) || typeof(System.Collections.IEnumerator).IsAssignableFrom(owner)))
                    {
                        // A state machine (<Method>d__N): its own, all of it (not <PrivateImplementationDetails>, anonymous
                        // types or <>c, which the whole class shares).
                        AddType(owner);
                    }
                    else if (owner != null && owner.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
                    {
                        // A display class: the captured variables of this method's scopes (shared by its lambdas only).
                        AddType(owner);
                    }
                    else if (member is MethodBase m && IsGenerated(m))
                    {
                        // A lambda (in <>c, shared by the class: only the method itself) or a local function.
                        Add(m);
                    }
                    continue;
                }
                pos += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
                    _ => 4,
                };
            }
        }
        return found;
    }

    /// <summary>
    /// The compiler-generated code of each part method of a patch class (<paramref name="partOf"/>: its part, or null) -> that
    /// part: its lambdas, local functions and state machines, nested members that can't carry the part attribute. Generated
    /// code also used by a method outside the part (or by two parts) is left out: it belongs to the whole class. Shared by the
    /// load check (PatchSafety) and tools/compat, so both judge the same code as a part's.
    /// </summary>
    public static Dictionary<MethodBase, string> PartCode(Type outermost, Func<MethodBase, string?> partOf)
    {
        var code = new Dictionary<MethodBase, string>();
        List<MethodBase> methods = DeclaredMethods(outermost).ToList();
        if (!methods.Any(m => partOf(m) != null))
        {
            return code; // Nearly every patch class: no scan.
        }
        var shared = new HashSet<MethodBase>();
        foreach (MethodBase method in methods)
        {
            if (method.Name.Contains('<') || method.DeclaringType?.Name.Contains('<') == true)
            {
                continue; // Generated itself: reached from the method it belongs to.
            }
            string? part = partOf(method);
            foreach (MethodBase generated in GeneratedCode(method))
            {
                if (part == null || (code.TryGetValue(generated, out string? other) && other != part))
                {
                    shared.Add(generated);
                }
                else
                {
                    code[generated] = part;
                }
            }
        }
        foreach (MethodBase method in shared)
        {
            code.Remove(method);
        }
        return code;
    }

    /// <summary>Every method and constructor of the type and of its nested types, recursively.</summary>
    public static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in type.GetMethods(all))
        {
            yield return method;
        }
        foreach (ConstructorInfo constructor in type.GetConstructors(all))
        {
            yield return constructor;
        }
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (MethodBase method in DeclaredMethods(nested))
            {
                yield return method;
            }
        }
    }

    /// <summary>
    /// Whether code of the assembly <paramref name="own"/> may use the member directly: its own members, or public ones
    /// (protected only from a class deriving from the owner, or nested in one) of public types. The load check's rule
    /// (PatchSafety.RequireResolvable), shared with tools/compat: an update making a used member private or protected is a
    /// clean "skipped" in game, and a problem for compat.
    /// </summary>
    public static bool Accessible(MemberInfo member, Assembly own, Type? user)
    {
        Type? owner = member as Type ?? member.DeclaringType;
        if (owner == null || owner.Assembly == own || owner.IsGenericParameter)
        {
            return true;
        }
        bool visible = member is Type type ? IsVisible(type, own) : IsVisible(owner, own);
        bool family = Derives(user, owner);
        return visible && member switch
        {
            MethodBase m => m.IsPublic || ((m.IsFamily || m.IsFamilyOrAssembly) && family),
            FieldInfo f => f.IsPublic || ((f.IsFamily || f.IsFamilyOrAssembly) && family),
            _ => true,
        };
    }

    private static bool Derives(Type? user, Type owner)
    {
        Type definition = owner.IsGenericType ? owner.GetGenericTypeDefinition() : owner;
        for (Type? outer = user; outer != null; outer = outer.DeclaringType)
        {
            for (Type? t = outer.BaseType; t != null; t = t.BaseType)
            {
                if (t == owner || (t.IsGenericType && t.GetGenericTypeDefinition() == definition))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Public all the way out (nested types included); array, pointer and generic arguments checked too.</summary>
    private static bool IsVisible(Type type, Assembly own)
    {
        if (type.HasElementType)
        {
            return IsVisible(type.GetElementType()!, own);
        }
        if (type.IsGenericParameter || type.Assembly == own)
        {
            return true;
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition && !type.GetGenericArguments().All(t => IsVisible(t, own)))
        {
            return false;
        }
        Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.IsNested ? (definition.IsNestedPublic || definition.IsNestedFamily || definition.IsNestedFamORAssem) && IsVisible(definition.DeclaringType!, own) : definition.IsPublic;
    }

    /// <summary>
    /// The methods of <paramref name="type"/> and its nested types (lambdas, state machines) whose code uses the field
    /// (read, write or address), by name (<c>Nested+Name</c> for nested types, generated numbering normalized), sorted.
    /// A private field can't be used from elsewhere (reflection aside): this is every place that can change it.
    /// </summary>
    public static IReadOnlyList<string> FieldUsers(Type type, string field, bool writersOnly = false)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var users = new SortedSet<string>(StringComparer.Ordinal);
        Type? fieldType = type.GetField(field, all)?.FieldType;
        // The collection's type and its mutable interfaces (IList<T>, ICollection<T>...): calls through them, or passing
        // the collection to a method taking one, can change it.
        var mutable = new HashSet<Type>();
        if (fieldType != null)
        {
            mutable.Add(fieldType);
            foreach (Type i in fieldType.GetInterfaces().Where(i => i.IsGenericType))
            {
                Type d = i.GetGenericTypeDefinition();
                if (d == typeof(IList<>) || d == typeof(ICollection<>) || d == typeof(ISet<>) || d == typeof(IDictionary<,>))
                {
                    mutable.Add(i);
                }
            }
        }
        void Scan(Type t, string prefix)
        {
            foreach (MethodBase m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                MemberInfo[] used;
                lock (_usedCache)
                {
                    if (!_usedCache.TryGetValue(m, out used!))
                    {
                        _usedCache[m] = used = UsedMembers(m).ToArray();
                    }
                }
                if (!used.Any(u => u is FieldInfo f && f.Name == field && f.DeclaringType == type))
                {
                    continue;
                }
                // Writers: an assignment of the field, a mutating call on a collection of its type, or a call taking such a
                // collection (a superset of the real writers: the call may be on another collection of that type).
                if (!writersOnly || Assigns(m, field, type) || used.Any(u => u is MethodInfo call
                    && ((call.DeclaringType != null && mutable.Contains(call.DeclaringType) && Mutators.Contains(call.Name))
                        || call.GetParameters().Any(p => mutable.Contains(p.ParameterType)))))
                {
                    users.Add(Normalize(prefix + m.Name));
                }
            }
            foreach (Type nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                Scan(nested, prefix + nested.Name + "+");
            }
        }
        Scan(type, "");
        return users.ToList();
    }

    /// <summary>Members each scanned method uses: several checks scan the same types at load (CardModel, Player...).</summary>
    private static readonly Dictionary<MethodBase, MemberInfo[]> _usedCache = new();

    private static readonly HashSet<string> Mutators = new()
    {
        "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "set_Item",
        "Sort", "Reverse", "CopyTo", "EnsureCapacity", "TrimExcess", "Push", "Pop", "Enqueue", "Dequeue", "TryAdd", "TryRemove",
        // HashSet<T> / ISet<T> in-place set operations (a pinned HashSet field changed through them has a writer too).
        "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith", "RemoveWhere",
    };

    /// <summary>Whether the method's code stores to the field (stfld / stsfld) or takes its address (ldflda / ldsflda).</summary>
    private static bool Assigns(MethodBase method, string field, Type type)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        int pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[pos++]);
            }
            OpCode op = _opCodes[value];
            if (op.OperandType == OperandType.InlineField)
            {
                if ((op == OpCodes.Stfld || op == OpCodes.Stsfld || op == OpCodes.Ldflda || op == OpCodes.Ldsflda)
                    && method.Module.ResolveField(BitConverter.ToInt32(il, pos), typeArgs, methodArgs) is { } f && f.Name == field && f.DeclaringType == type)
                {
                    return true;
                }
                pos += 4;
                continue;
            }
            pos += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
                _ => 4,
            };
        }
        return false;
    }

    /// <summary>One decoded instruction: its offset, opcode, the offset after it, and its operand (a branch's absolute target,
    /// a variable index, an integer constant, a resolved member; null when it has none or doesn't resolve).</summary>
    public readonly record struct Instruction(int Offset, OpCode Op, int Next, object? Operand);

    /// <summary>
    /// The method's own instructions in order (<see cref="PatchSafety"/>'s shape checks of other mods' patch methods). Tokens
    /// that don't resolve give a null operand rather than a throw; a switch's operand is its absolute targets.
    /// </summary>
    public static List<Instruction> Instructions(MethodBase method) => Instructions(method, strict: false);

    /// <summary><see cref="Instructions(MethodBase)"/>; <paramref name="strict"/>: a token that doesn't resolve throws.</summary>
    public static List<Instruction> Instructions(MethodBase method, bool strict)
    {
        MethodBody body = method.GetMethodBody() ?? throw new ArgumentException($"{method} has no body");
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var list = new List<Instruction>();
        int pos = 0;
        while (pos < il.Length)
        {
            int start = pos;
            short value = il[pos++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[pos++]);
            }
            OpCode op = _opCodes[value];
            object? operand = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                    operand = pos + 1 + (sbyte)il[pos];
                    pos += 1;
                    break;
                case OperandType.ShortInlineI:
                    operand = op == OpCodes.Ldc_I4_S ? (sbyte)il[pos] : il[pos];
                    pos += 1;
                    break;
                case OperandType.ShortInlineVar:
                    operand = (int)il[pos];
                    pos += 1;
                    break;
                case OperandType.InlineVar:
                    operand = (int)BitConverter.ToUInt16(il, pos);
                    pos += 2;
                    break;
                case OperandType.InlineBrTarget:
                    operand = pos + 4 + BitConverter.ToInt32(il, pos);
                    pos += 4;
                    break;
                case OperandType.InlineI:
                    operand = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    break;
                case OperandType.ShortInlineR:
                    pos += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    pos += 8;
                    break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(il, pos);
                    pos += 4;
                    int end = pos + 4 * count;
                    var targets = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        targets[i] = end + BitConverter.ToInt32(il, pos + 4 * i);
                    }
                    operand = targets;
                    pos = end;
                    break;
                case OperandType.InlineString:
                    // The literal (PatchSafety's name gates read it: round 4, it was left null and no name gate was ever found).
                    try
                    {
                        operand = method.Module.ResolveString(BitConverter.ToInt32(il, pos));
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or BadImageFormatException)
                    {
                        operand = null;
                    }
                    pos += 4;
                    break;
                case OperandType.InlineSig:
                    pos += 4;
                    break;
                default: // InlineField, InlineMethod, InlineType, InlineTok
                    try
                    {
                        operand = method.Module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, methodArgs);
                    }
                    catch (Exception e) when (!strict && (e is ArgumentException or MissingMemberException or TypeLoadException or BadImageFormatException))
                    {
                        operand = null;
                    }
                    pos += 4;
                    break;
            }
            list.Add(new Instruction(start, op, pos, operand));
        }
        return list;
    }

    /// <summary>
    /// The game's methods a method's code may call (its generated code included: lambdas, local functions, state machines), as
    /// their definitions (an open generic type's or method's own), in first-use order: call, callvirt, newobj, ldftn, ldvirtftn
    /// targets in <paramref name="game"/>; a callvirt/ldvirtftn of a virtual or interface method adds every implementation in
    /// the game's assembly (every instantiation of a generic interface, default interface methods included); a game type whose
    /// static members or constructor the code uses adds its type initializer (it may run then). Shared by tools/fpdiff (HookSkip's
    /// helper sets) and the patches computing what a skipped call leaves out (RelicFlashMerge, CardFlightLimit).
    /// Strict: an operand that doesn't resolve throws (a callee this can't see would go unchecked), and so does a delegate's
    /// Invoke when <paramref name="delegates"/> is null (code reached through a delegate isn't known here); with a list, each
    /// such call is added to it (the delegate type's Invoke) for the caller to judge. Not seen (C5/C7, re-audit round 2): calli
    /// and jmp, and game methods the runtime, Godot or SmartFormat call back (comparers, signal handlers, format extensions,
    /// engine-called node methods unless their class is a root). Generic members are named by their open
    /// definitions, while Harmony patches a closed instantiation's own method: a patch on one instantiation is seen on the
    /// definition only if Harmony lists it there (theoretical: no generic member is in a set the patches rely on today).
    /// </summary>
    public static List<MethodBase> GameCallees(Assembly game, MethodBase method, List<MethodInfo>? delegates = null, bool initializers = true, bool dispatch = true,
        List<(MethodInfo Callee, List<MethodBase> Implementations)>? dispatches = null)
    {
        var found = new List<MethodBase>();
        var seen = new HashSet<MethodBase>();
        void Add(MethodBase m)
        {
            MethodBase definition = Definition(m);
            if (definition.Module.Assembly == game && definition.GetMethodBody() != null && seen.Add(definition))
            {
                found.Add(definition);
            }
        }
        void Initializer(Type? type)
        {
            if (initializers && type != null && !type.IsGenericParameter && type.Module.Assembly == game)
            {
                Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                if (definition.TypeInitializer is { } cctor)
                {
                    Add(cctor);
                }
            }
        }
        foreach (MethodBase code in new[] { method }.Concat(GeneratedCode(method)))
        {
            if (code.GetMethodBody() == null)
            {
                continue;
            }
            foreach (Instruction ins in Instructions(code, strict: true))
            {
                if (ins.Operand is FieldInfo { IsStatic: true } staticField)
                {
                    Initializer(staticField.DeclaringType);
                    continue;
                }
                if (ins.Operand is not MethodBase callee || !(ins.Op == OpCodes.Call || ins.Op == OpCodes.Callvirt || ins.Op == OpCodes.Newobj
                    || ins.Op == OpCodes.Ldftn || ins.Op == OpCodes.Ldvirtftn))
                {
                    continue;
                }
                if (callee.IsStatic || ins.Op == OpCodes.Newobj)
                {
                    Initializer(callee.DeclaringType);
                }
                if (callee.Name == "Invoke" && callee.DeclaringType is { } delegateType && typeof(MulticastDelegate).IsAssignableFrom(delegateType)
                    && delegateType != typeof(MulticastDelegate))
                {
                    if (delegates == null)
                    {
                        throw new InvalidOperationException($"{MemberName(code)} invokes a delegate ({TypeName(delegateType)}): its targets aren't known here");
                    }
                    delegates.Add((MethodInfo)callee);
                    continue;
                }
                if (callee.Module.Assembly == game)
                {
                    Add(callee);
                }
                if ((dispatch || callee.Module.Assembly == game) && (ins.Op == OpCodes.Callvirt || ins.Op == OpCodes.Ldvirtftn)
                    && callee is MethodInfo { IsVirtual: true, IsFinal: false } dispatched)
                {
                    List<MethodBase> implementations = Implementations(game, dispatched);
                    foreach (MethodBase implementation in implementations)
                    {
                        Add(implementation);
                    }
                    dispatches?.Add((dispatched, implementations.Select(Definition).Distinct().ToList()));
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The transitive closure of <see cref="GameCallees"/> from <paramref name="roots"/> (the roots included, whatever their
    /// assembly: a mod's override reached by a virtual call is followed into the game's code it calls), at most
    /// <paramref name="limit"/> methods (more throws: a caller then backs off rather than check a set it can't bound).
    /// <paramref name="dispatches"/>: each virtual or interface call made (the callee as called, its game implementations), an
    /// abstract or interface callee included although it has no body (CodeSet: another assembly's implementation of it runs
    /// in its place). <paramref name="stopAt"/>: methods neither added nor followed (a subtree the caller judges on its own).
    /// </summary>
    public static List<MethodBase> GameClosure(Assembly game, IEnumerable<MethodBase> roots, int limit, List<MethodInfo>? delegates = null, Dictionary<MethodBase, MethodBase>? parents = null,
        bool initializers = true, bool dispatch = true, List<(MethodInfo Callee, List<MethodBase> Implementations)>? dispatches = null,
        ICollection<MethodBase>? stopAt = null)
    {
        var found = new List<MethodBase>();
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();
        foreach (MethodBase root in roots)
        {
            if (seen.Add(root))
            {
                found.Add(root);
                queue.Enqueue(root);
            }
        }
        while (queue.Count > 0)
        {
            MethodBase caller = queue.Dequeue();
            foreach (MethodBase callee in GameCallees(game, caller, delegates, initializers, dispatch, dispatches))
            {
                if (stopAt != null && stopAt.Contains(callee))
                {
                    continue;
                }
                if (seen.Add(callee))
                {
                    if (parents != null)
                    {
                        parents[callee] = caller;
                    }
                    if (found.Count >= limit)
                    {
                        throw new InvalidOperationException($"more than {limit} game methods reached (at {MemberName(callee)})");
                    }
                    found.Add(callee);
                    queue.Enqueue(callee);
                }
            }
        }
        return found;
    }

    /// <summary>
    /// One fingerprint for a set of methods (a closure): their names and code fingerprints, and the delegates it invokes, sorted.
    /// Pinned per game version by a patch relying on what the set is (CodeSet; tools/fingerprint prints it: <c>closure</c>).
    /// </summary>
    public static string SetFingerprint(IEnumerable<MethodBase> methods, IEnumerable<string>? delegates = null)
    {
        string text = string.Join("\n", methods.Select(m => MemberName(m) + " " + Of(m))
            .Concat((delegates ?? Enumerable.Empty<string>()).Select(d => "delegate " + d)).Distinct().OrderBy(line => line, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static readonly Dictionary<Assembly, Type[]> _gameTypes = new();

    private static Type[] TypesOf(Assembly game)
    {
        lock (_gameTypes)
        {
            if (!_gameTypes.TryGetValue(game, out Type[]? types))
            {
                try
                {
                    types = game.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray()!;
                }
                _gameTypes[game] = types;
            }
            return types;
        }
    }

    /// <summary>
    /// Every implementation in the game's assembly a virtual call to <paramref name="target"/> may reach: overrides in derived
    /// classes; for an interface method, each implementing class's target for every instantiation of the interface (default
    /// interface methods included, wherever declared).
    /// </summary>
    public static List<MethodBase> Implementations(Assembly game, MethodInfo target)
    {
        var found = new List<MethodBase>();
        Type declaring = target.DeclaringType!;
        Type? openDeclaring = declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : null;
        MethodInfo baseDefinition = target.GetBaseDefinition();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (Type type in TypesOf(game))
        {
            if (type == declaring || type.IsInterface)
            {
                continue; // An interface's default methods are reached through the implementing classes' maps.
            }
            try
            {
                if (declaring.IsInterface)
                {
                    foreach (Type implemented in type.GetInterfaces())
                    {
                        // A closed instantiation on a closed type: the implementations a call through it reaches (variance
                        // included); a call or an implementing type still naming generic parameters (generic code, a generic
                        // class whose instances may be of any instantiation): every instantiation of the interface.
                        if (declaring.ContainsGenericParameters || implemented.ContainsGenericParameters
                            ? !(openDeclaring != null && implemented.IsGenericType && implemented.GetGenericTypeDefinition() == openDeclaring)
                            : !declaring.IsAssignableFrom(implemented))
                        {
                            continue;
                        }
                        InterfaceMapping map = type.GetInterfaceMap(implemented);
                        for (int i = 0; i < map.InterfaceMethods.Length; i++)
                        {
                            // The game's own code only (a target inherited from a runtime base class isn't the game's).
                            if (map.InterfaceMethods[i].Name == target.Name && map.TargetMethods[i] is { } implementation
                                && map.InterfaceMethods[i].GetParameters().Length == target.GetParameters().Length && implementation.Module.Assembly == game)
                            {
                                found.Add(implementation);
                            }
                        }
                    }
                }
                else if (DerivesFrom(type, openDeclaring ?? declaring))
                {
                    foreach (MethodInfo m in type.GetMethods(all))
                    {
                        if (m.Name == target.Name && m.GetBaseDefinition() is { } root && root.MetadataToken == baseDefinition.MetadataToken && root.Module == baseDefinition.Module)
                        {
                            found.Add(m);
                        }
                    }
                }
            }
            catch (Exception e) when (e is TypeLoadException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                // A type that can't be inspected (another version's dependency missing): not an implementation the game runs here.
            }
        }
        return found;
    }

    private static bool DerivesFrom(Type type, Type baseType)
    {
        for (Type? t = type.BaseType; t != null; t = t.BaseType)
        {
            if (t == baseType || (t.IsGenericType && t.GetGenericTypeDefinition() == baseType))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The key <see cref="Resolve"/> finds a method by: <c>Namespace.Type::Name(ParamType,...)</c> of its definition, parameter
    /// types by short name (tools/fpdiff checks each key it prints round-trips).
    /// </summary>
    public static string Key(MethodBase method)
    {
        MethodBase definition = Definition(method);
        return $"{TypeName(definition.DeclaringType)}::{definition.Name}({string.Join(",", definition.GetParameters().Select(p => p.ParameterType.Name))})";
    }

    /// <summary>
    /// The methods a virtual call in <paramref name="caller"/>'s code (its generated code included) named <paramref name="callee"/>
    /// (<see cref="MemberName"/>) may reach in the game: the keys of its implementations, sorted, joined with '|' (what tools/fpdiff
    /// prints as a Dispatch line). Null when the caller makes no such call.
    /// </summary>
    public static string? DispatchTargets(Assembly game, MethodBase caller, string callee) => DispatchCallee(caller, callee) is { } target
        ? string.Join("|", Implementations(game, target).Select(Key).Distinct().OrderBy(k => k, StringComparer.Ordinal))
        : null;

    /// <summary>The virtual method a callvirt/ldvirtftn in <paramref name="caller"/>'s code (generated code included) names <paramref name="callee"/>, or null.</summary>
    public static MethodInfo? DispatchCallee(MethodBase caller, string callee)
    {
        foreach (MethodBase code in new[] { caller }.Concat(GeneratedCode(caller)))
        {
            if (code.GetMethodBody() == null)
            {
                continue;
            }
            foreach (Instruction ins in Instructions(code, strict: true))
            {
                if ((ins.Op == OpCodes.Callvirt || ins.Op == OpCodes.Ldvirtftn) && ins.Operand is MethodInfo { IsVirtual: true, IsFinal: false } target
                    && MemberName(target) == callee)
                {
                    return target;
                }
            }
        }
        return null;
    }

    /// <summary>A method as declared (an open generic type's or method's own definition), whatever instantiation the IL named.</summary>
    public static MethodBase Definition(MethodBase m)
    {
        try
        {
            if (m is MethodInfo { IsGenericMethod: true } generic)
            {
                m = generic.GetGenericMethodDefinition();
            }
            if (m.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } constructed)
            {
                Type open = constructed.GetGenericTypeDefinition();
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                return open.GetMethods(all).Cast<MethodBase>().Concat(open.GetConstructors(all)).First(d => d.MetadataToken == m.MetadataToken);
            }
            return m;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return m;
        }
    }

    /// <summary>
    /// The method a key <c>Namespace.Type::Name(ParamType,...)</c> names (parameter types by short name, nested types as
    /// <c>Outer+Inner</c>) in an assembly, or null when that version doesn't have it.
    /// </summary>
    public static MethodBase? Resolve(Assembly assembly, string key)
    {
        int split = key.IndexOf("::", StringComparison.Ordinal), open = key.IndexOf('(', split);
        Type? type = assembly.GetType(key[..split]);
        if (type == null)
        {
            return null;
        }
        string name = key[(split + 2)..open];
        string parameters = key[(open + 1)..^1];
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        return type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all))
            .FirstOrDefault(m => m.Name == name && string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) == parameters);
    }

    /// <summary>Type::Name(parameter types), as fingerprints and tools/fpdiff print it.</summary>
    public static string MemberName(MemberInfo? member) => member switch
    {
        null => "?",
        Type type => TypeName(type),
        MethodBase m => $"{TypeName(m.DeclaringType)}::{m.Name}"
            + (m.IsGenericMethod ? "<" + string.Join(",", m.GetGenericArguments().Select(TypeName)) + ">" : "")
            + "(" + string.Join(",", m.GetParameters().Select(p => TypeName(p.ParameterType))) + ")",
        _ => $"{TypeName(member.DeclaringType)}::{member.Name}",
    };

    /// <summary>Full name without assembly versions (FullName embeds them in generic arguments).</summary>
    public static string TypeName(Type? type)
    {
        if (type == null)
        {
            return "?";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (type.HasElementType)
        {
            string element = TypeName(type.GetElementType());
            return type.IsArray ? element + "[]" : type.IsByRef ? element + "&" : element + "*";
        }
        string name = type.IsNested ? TypeName(type.DeclaringType) + "+" + type.Name : type.Namespace + "." + type.Name;
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            name += "[" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "]";
        }
        return name;
    }
}
