using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BigDeckOptimization.Patches;

/// <summary>
/// A method's code described as <see cref="MethodFingerprint.Describe"/> does (instructions with their operands resolved to member
/// names, locals, exception clauses, then the compiler-generated code it uses), with its signature first and every type of its
/// own assembly named without namespace (<c>~Name</c>, nested ones <c>~Outer+Inner</c>): two copies of the same source compiled
/// into two mods' assemblies (BaseLib's SavedSpireFieldPatch bundled in another mod, rule B) describe alike, while any member of
/// another assembly (the game's, the runtime's, BaseLib's own when the copy calls it) keeps its full name. Self-contained (it only
/// needs <see cref="MethodFingerprint"/>): tools/fingerprint compiles it for its <c>own</c> mode, which gives the stored constants.
/// </summary>
public static class OwnNormalizedIl
{
    private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static readonly Regex _generatedNumber = new(@"(d__|DisplayClass|b__|9__|\||AnonymousType|<)\d+(_\d+)?(?=\W|$)", RegexOptions.Compiled);

    /// <summary>First 16 hex digits of the SHA-256 of <see cref="Describe"/>.</summary>
    public static string Of(MethodBase method) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Describe(method, null))))[..16];

    /// <summary>
    /// The description; <paramref name="used"/> gets every member the code (the generated code included) uses. Throws when a token
    /// doesn't resolve (the caller then refuses).
    /// </summary>
    public static string Describe(MethodBase method, Action<MemberInfo>? used)
    {
        Assembly own = method.Module.Assembly;
        var sb = new StringBuilder();
        sb.Append("sig ").Append(method.IsStatic ? "static " : "instance ")
            .Append(method is MethodInfo info ? TypeName(info.ReturnType, own) : "ctor").Append(" (")
            .Append(string.Join(", ", method.GetParameters().Select(p => TypeName(p.ParameterType, own) + " " + p.Name + (p.IsOut ? " out" : ""))))
            .Append(")\n");
        var seen = new HashSet<MethodBase> { method };
        var queue = new Queue<MethodBase>();
        sb.Append(DescribeOne(method, own, m =>
        {
            used?.Invoke(m);
            Enqueue(m, method.Module, seen, queue);
        }));
        while (queue.Count > 0)
        {
            MethodBase generated = queue.Dequeue();
            sb.Append("== ").Append(Normalize(MemberName(generated, own))).Append('\n');
            sb.Append(generated.GetMethodBody() == null ? "(no body)\n" : DescribeOne(generated, own, m =>
            {
                used?.Invoke(m);
                Enqueue(m, method.Module, seen, queue);
            }));
        }
        return sb.ToString();
    }

    private static string Normalize(string name) => name.Contains('<') ? _generatedNumber.Replace(name, "$1#") : name;

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
            Type? type = member as Type ?? member.DeclaringType;
            if (type != null && IsGeneratedType(type)
                && (typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(type) || typeof(System.Collections.IEnumerator).IsAssignableFrom(type)))
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

    private static string DescribeOne(MethodBase method, Assembly own, Action<MemberInfo> used)
    {
        MethodBody body = method.GetMethodBody() ?? throw new ArgumentException($"{method} has no body");
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var sb = new StringBuilder();
        sb.Append("init ").Append(body.InitLocals).Append('\n');
        foreach (LocalVariableInfo local in body.LocalVariables)
        {
            sb.Append("local ").Append(Normalize(TypeName(local.LocalType, own))).Append(local.IsPinned ? " pinned" : "").Append('\n');
        }
        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            sb.Append("eh ").Append(clause.Flags).Append(' ').Append(clause.TryOffset).Append(' ').Append(clause.TryLength)
                .Append(' ').Append(clause.HandlerOffset).Append(' ').Append(clause.HandlerLength).Append(' ')
                .Append(clause.Flags == ExceptionHandlingClauseOptions.Clause ? Normalize(TypeName(clause.CatchType, own)) : "")
                .Append(clause.Flags == ExceptionHandlingClauseOptions.Filter ? clause.FilterOffset.ToString() : "")
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
                    // A calli: its signature isn't compared, so a copy holding one never matches a constant without one.
                    sb.Append(" sig");
                    pos += 4;
                    break;
                default: // InlineField, InlineMethod, InlineType, InlineTok
                    MemberInfo member = module.ResolveMember(BitConverter.ToInt32(il, pos), typeArgs, methodArgs)
                        ?? throw new MissingMemberException($"{method.Name}: a token at {pos} doesn't resolve");
                    sb.Append(' ').Append(Normalize(MemberName(member, own)));
                    used(member);
                    pos += 4;
                    break;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary><see cref="MethodFingerprint.MemberName"/> with the own assembly's types named by <see cref="TypeName"/>.</summary>
    public static string MemberName(MemberInfo member, Assembly own) => member switch
    {
        Type type => TypeName(type, own),
        MethodBase m => $"{TypeName(m.DeclaringType, own)}::{m.Name}"
            + (m.IsGenericMethod ? "<" + string.Join(",", m.GetGenericArguments().Select(a => TypeName(a, own))) + ">" : "")
            + "(" + string.Join(",", m.GetParameters().Select(p => TypeName(p.ParameterType, own))) + ")",
        FieldInfo f => $"{TypeName(f.DeclaringType, own)}::{f.Name} {TypeName(f.FieldType, own)}{(f.IsStatic ? " static" : "")}",
        _ => $"{TypeName(member.DeclaringType, own)}::{member.Name}",
    };

    /// <summary>A type's name, generic arguments included; a type of <paramref name="own"/> without its namespace (<c>~</c> first).</summary>
    public static string TypeName(Type? type, Assembly own)
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
            string element = TypeName(type.GetElementType(), own);
            return type.IsArray ? element + "[" + new string(',', type.GetArrayRank() - 1) + "]" : type.IsByRef ? element + "&" : element + "*";
        }
        string name = type.IsNested ? TypeName(type.DeclaringType, own) + "+" + type.Name
            : type.Assembly == own ? "~" + type.Name : type.Namespace + "." + type.Name;
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            name += "[" + string.Join(",", type.GetGenericArguments().Select(a => TypeName(a, own))) + "]";
        }
        return name;
    }
}
