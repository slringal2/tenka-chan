using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Analysis;

public sealed record IlInstruction(int Offset, string OpCode, string OperandHex);
public sealed record IlMethodReport(string Assembly, string Type, string Method, int MetadataToken,
    IReadOnlyList<IlInstruction> Instructions, bool Truncated, string Note);

/// <summary>
/// Compatibility facade. Ambiguous type names/overloads are errors; use
/// assembly_methods and binary_source_inspect with a token for exact selection.
/// </summary>
public sealed class IlInspector(WorkspaceGuard guard)
{
    public IlMethodReport Inspect(string assemblyPath, string typeName, string methodName, int maxInstructions = 250)
    {
        var catalog = new BinarySourceInspector(guard).Methods(assemblyPath, methodName, 2000);
        if (catalog.Truncated)
            throw new InvalidOperationException("Too many matching names. Use assembly_methods and an exact MethodDef token.");
        var candidates = catalog.Methods.Where(m => m.Name == methodName &&
            (m.Type == typeName || m.Type.Split('.').Last() == typeName)).ToArray();
        if (candidates.Length == 0) throw new KeyNotFoundException("Type or method not found.");
        if (candidates.Length != 1)
            throw new InvalidOperationException("Ambiguous method. Select a token via assembly_methods: " +
                string.Join("; ", candidates.Take(8).Select(m => $"0x{m.MetadataToken:X8} {m.Signature}")));
        var selected = candidates[0];
        var bytes = BoundedBinaryFile.Read(catalog.AssemblyPath, 64_000_000);
        if (BinarySourceInspector.Hash(bytes) != catalog.AssemblySha256)
            throw new InvalidDataException("Assembly changed during selection.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var method = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(selected.MetadataToken & 0xFFFFFF));
        if (method.RelativeVirtualAddress == 0) throw new InvalidOperationException("Method has no IL body (abstract/extern).");
        var all = ResolvedIlDecoder.Decode(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [], metadata, []);
        var instructions = all.Take(Math.Clamp(maxInstructions, 1, 1000))
            .Select(i => new IlInstruction(i.Offset, i.OpCode, i.OperandHex)).ToArray();
        return new IlMethodReport(catalog.AssemblyPath, selected.Type, selected.Name, selected.MetadataToken,
            instructions, all.Count > instructions.Length,
            "IL, not native machine code. For resolved operands and checksum-verified Portable PDB source ranges, use binary_source_inspect with this token.");
    }
}
