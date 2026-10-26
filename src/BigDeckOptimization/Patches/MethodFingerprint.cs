using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace BigDeckOptimization.Patches;

/// <summary>
/// Fingerprint of a method body that survives recompiling the assembly: metadata tokens (which shift whenever anything
/// else in the assembly changes) are replaced by the names of the members they point to. Two builds give the same
/// fingerprint exactly when the method's own code is unchanged.
///
/// Patches that mirror another method's logic store the fingerprint of the code they mirror and only turn on when it
/// matches, so an update of the game or of another mod disables them only if it touches that exact code.
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

    /// <summary>One line per instruction, tokens resolved to member names, plus locals and exception clauses.</summary>
    public static string Describe(MethodBase method)
    {
        MethodBody body = method.GetMethodBody() ?? throw new ArgumentException($"{method} has no body");
        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        Module module = method.Module;
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        StringBuilder sb = new();
        foreach (LocalVariableInfo local in body.LocalVariables)
        {
            sb.Append("local ").Append(TypeName(local.LocalType)).Append('\n');
        }
        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            sb.Append("eh ").Append(clause.Flags).Append(' ').Append(clause.TryOffset).Append(' ').Append(clause.TryLength)
                .Append(' ').Append(clause.HandlerOffset).Append(' ').Append(clause.HandlerLength).Append(' ')
                .Append(clause.Flags == ExceptionHandlingClauseOptions.Clause ? TypeName(clause.CatchType) : "")
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
                    sb.Append(' ').Append(MemberName(member));
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

    private static string MemberName(MemberInfo? member) => member switch
    {
        null => "?",
        Type type => TypeName(type),
        MethodBase m => $"{TypeName(m.DeclaringType)}::{m.Name}"
            + (m.IsGenericMethod ? "<" + string.Join(",", m.GetGenericArguments().Select(TypeName)) + ">" : "")
            + "(" + string.Join(",", m.GetParameters().Select(p => TypeName(p.ParameterType))) + ")",
        _ => $"{TypeName(member.DeclaringType)}::{member.Name}",
    };

    /// <summary>Full name without assembly versions (FullName embeds them in generic arguments).</summary>
    private static string TypeName(Type? type)
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
