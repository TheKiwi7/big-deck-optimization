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
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Describe(method)));
        return Convert.ToHexString(hash)[..16];
    }

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
