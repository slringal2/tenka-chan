using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Analysis;

public sealed record IlInstruction(int Offset, string OpCode, string OperandHex);
public sealed record IlMethodReport(string Assembly, string Type, string Method, int MetadataToken,
    IReadOnlyList<IlInstruction> Instructions, bool Truncated, string Note);

/// <summary>
/// Parses IL directly from PE metadata; never loads or executes the inspected assembly.
/// Does not claim to represent tiered JIT machine code.
/// </summary>
public sealed class IlInspector
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> Opcodes =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(op => unchecked((ushort)op.Value));
    private readonly WorkspaceGuard _guard;

    public IlInspector(WorkspaceGuard guard) => _guard = guard;

    public IlMethodReport Inspect(string assemblyPath, string typeName, string methodName,
        int maxInstructions = 250)
    {
        var ext = Path.GetExtension(assemblyPath).ToLowerInvariant();
        if (ext is not (".dll" or ".exe"))
            throw new ArgumentException("Expected .dll or .exe assembly.");
        var path = _guard.RequireFile(assemblyPath, ext);
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        if (!pe.HasMetadata)
            throw new InvalidDataException("Not a managed metadata assembly.");
        var metadata = pe.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var name = metadata.GetString(type.Name);
            var qualified = metadata.GetString(type.Namespace);
            var full = string.IsNullOrEmpty(qualified) ? name : qualified + "." + name;
            if (typeName != name && typeName != full) continue;
            foreach (var handle in type.GetMethods())
            {
                var definition = metadata.GetMethodDefinition(handle);
                var method = metadata.GetString(definition.Name);
                if (method != methodName) continue;
                if (definition.RelativeVirtualAddress == 0)
                    throw new InvalidOperationException("Method has no IL body (abstract/extern).");
                var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
                var bytes = body.GetILBytes().ToArray();
                var instructions = Decode(bytes, Math.Clamp(maxInstructions, 1, 1000), out var truncated);
                return new IlMethodReport(path, full, method,
                    MetadataTokens.GetToken(handle), instructions, truncated,
                    "IL is not native JIT assembly. Metadata tokens/branch operands are shown as raw bytes; verify semantics and benchmark separately.");
            }
        }
        throw new KeyNotFoundException("Type or method not found (overloads: first matching method only).");
    }

    private static List<IlInstruction> Decode(byte[] bytes, int limit, out bool truncated)
    {
        var instructions = new List<IlInstruction>();
        var cursor = 0;
        while (cursor < bytes.Length && instructions.Count < limit)
        {
            var offset = cursor;
            var first = bytes[cursor++];
            var value = first == 0xFE && cursor < bytes.Length
                ? (ushort)(0xFE00 | bytes[cursor++])
                : (ushort)first;
            if (!Opcodes.TryGetValue(value, out var op))
                throw new InvalidDataException($"Unknown IL opcode at 0x{offset:X4}.");
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineI or OperandType.ShortInlineBrTarget or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => cursor + 4 <= bytes.Length
                    ? checked(4 + 4 * BitConverter.ToInt32(bytes, cursor))
                    : throw new InvalidDataException("Truncated switch operand."),
                _ => throw new InvalidDataException("Unsupported operand type.")
            };
            if (size < 0 || size > bytes.Length - cursor)
                throw new InvalidDataException("Truncated or invalid IL operand.");
            var operandHex = Convert.ToHexString(bytes.AsSpan(cursor, size));
            cursor += size;
            instructions.Add(new IlInstruction(offset, op.Name ?? value.ToString("X"), operandHex));
        }
        truncated = cursor < bytes.Length;
        return instructions;
    }
}
