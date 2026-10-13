using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace TenkaChan.Mcp.Analysis;

public sealed record ResolvedIlInstruction(int Offset, string OpCode, string OperandHex,
    string Operand, int? ReferencedToken, IReadOnlyList<int> BranchTargets,
    string SourceMappingStatus, IlSourceRegion? Source);

internal static class ResolvedIlDecoder
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Static | BindingFlags.Public)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => unchecked((ushort)o.Value));

    public static IReadOnlyList<ResolvedIlInstruction> Decode(byte[] bytes, MetadataReader metadata,
        IReadOnlyList<IlSourcePoint> points)
    {
        if (bytes.Length > 1_000_000) throw new InvalidDataException("Method IL exceeds 1 MB budget.");
        var formatter = new BinarySignatureFormatter();
        var instructions = new List<ResolvedIlInstruction>();
        var cursor = 0;
        var sequence = -1;
        while (cursor < bytes.Length)
        {
            if (instructions.Count >= 200000) throw new InvalidDataException("Excessive IL instruction count.");
            var offset = cursor;
            var first = bytes[cursor++];
            if (first == 0xFE && cursor == bytes.Length)
                throw new InvalidDataException("Truncated two-byte IL opcode.");
            var value = first == 0xFE ? (ushort)(0xFE00 | bytes[cursor++]) : (ushort)first;
            if (!Opcodes.TryGetValue(value, out var opcode))
                throw new InvalidDataException($"Unknown IL opcode at IL_{offset:X4}.");
            var count = 0;
            if (opcode.OperandType == OperandType.InlineSwitch)
            {
                Require(bytes, cursor, 4);
                count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor, 4));
                if (count < 0 || count > 100000 || count > (bytes.Length - cursor - 4) / 4)
                    throw new InvalidDataException("Invalid switch operand count.");
            }
            var size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineField or OperandType.InlineMethod or
                    OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or
                    OperandType.InlineType or OperandType.InlineBrTarget or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + count * 4,
                _ => throw new InvalidDataException("Unsupported IL operand type.")
            };
            Require(bytes, cursor, size);
            var operand = bytes.AsSpan(cursor, size);
            var next = cursor + size;
            var targets = new List<int>();
            int? reference = null;
            string rendered;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: rendered = ""; break;
                case OperandType.ShortInlineBrTarget:
                    // The operand is signed two's complement. A checked cast
                    // byte->sbyte would reject valid negative loop backedges.
                    targets.Add(BranchTarget(next, unchecked((sbyte)operand[0]), bytes.Length));
                    rendered = $"IL_{targets[0]:X4}"; break;
                case OperandType.InlineBrTarget:
                    targets.Add(BranchTarget(next, BinaryPrimitives.ReadInt32LittleEndian(operand), bytes.Length));
                    rendered = $"IL_{targets[0]:X4}"; break;
                case OperandType.InlineSwitch:
                    for (var i = 0; i < count; i++)
                        targets.Add(BranchTarget(next, BinaryPrimitives.ReadInt32LittleEndian(operand.Slice(4 + i * 4, 4)), bytes.Length));
                    rendered = string.Join(", ", targets.Take(100).Select(t => $"IL_{t:X4}"));
                    if (targets.Count > 100) rendered += " …";
                    break;
                case OperandType.InlineString:
                {
                    var token = BinaryPrimitives.ReadInt32LittleEndian(operand);
                    if ((token & unchecked((int)0xFF000000)) != 0x70000000)
                        throw new InvalidDataException("Invalid user-string token.");
                    reference = token;
                    var text = metadata.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
                    rendered = "\"" + text[..Math.Min(text.Length, 180)].Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
                    break;
                }
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                {
                    var token = BinaryPrimitives.ReadInt32LittleEndian(operand);
                    reference = token;
                    var handle = MetadataTokens.EntityHandle(token);
                    if (handle.IsNil) throw new InvalidDataException("Nil IL metadata operand.");
                    rendered = formatter.Entity(metadata, handle);
                    break;
                }
                case OperandType.InlineI: rendered = BinaryPrimitives.ReadInt32LittleEndian(operand).ToString(CultureInfo.InvariantCulture); break;
                case OperandType.InlineI8: rendered = BinaryPrimitives.ReadInt64LittleEndian(operand).ToString(CultureInfo.InvariantCulture); break;
                case OperandType.ShortInlineI: rendered = unchecked((sbyte)operand[0]).ToString(CultureInfo.InvariantCulture); break;
                case OperandType.ShortInlineVar: rendered = operand[0].ToString(CultureInfo.InvariantCulture); break;
                case OperandType.InlineVar: rendered = BinaryPrimitives.ReadUInt16LittleEndian(operand).ToString(CultureInfo.InvariantCulture); break;
                case OperandType.ShortInlineR:
                    rendered = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(operand)).ToString("R", CultureInfo.InvariantCulture); break;
                case OperandType.InlineR:
                    rendered = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(operand)).ToString("R", CultureInfo.InvariantCulture); break;
                default: rendered = Convert.ToHexString(operand); break;
            }
            while (sequence + 1 < points.Count && points[sequence + 1].Offset <= offset) sequence++;
            var point = sequence >= 0 ? points[sequence] : null;
            var mapping = point is null ? "no_sequence_point" : point.Hidden
                ? "hidden_sequence_point" : "pdb_sequence_point_range";
            instructions.Add(new ResolvedIlInstruction(offset, opcode.Name ?? "unknown",
                Convert.ToHexString(operand), rendered, reference, targets, mapping, point?.Region));
            cursor = next;
        }
        var starts = instructions.Select(i => i.Offset).ToHashSet();
        if (instructions.SelectMany(i => i.BranchTargets).Any(target => !starts.Contains(target)))
            throw new InvalidDataException("An IL branch points into an operand instead of an instruction.");
        return instructions;
    }

    private static int BranchTarget(int next, int displacement, int length)
    {
        var target = (long)next + displacement;
        if (target < 0 || target >= length)
            throw new InvalidDataException("IL branch target is outside the method body.");
        return (int)target;
    }

    private static void Require(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || count > data.Length - offset)
            throw new InvalidDataException("Truncated IL operand.");
    }
}
