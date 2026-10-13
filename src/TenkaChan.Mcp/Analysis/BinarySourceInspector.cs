using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Analysis;

public sealed record BinaryMethodEntry(int MetadataToken, string Type, string Name, string Signature, bool HasBody);
public sealed record BinaryMethodCatalog(string AssemblyPath, string AssemblySha256, string ModuleVersionId,
    int TotalMatches, bool Truncated, IReadOnlyList<BinaryMethodEntry> Methods);
public sealed record IlCostSite(int IlOffset, string Kind, string Operand, string MappingStatus,
    IlSourceRegion? Source, string Caveat);
public sealed record BinarySourceEvidence(string AssemblyPath, string AssemblySha256, string ModuleVersionId,
    int MetadataToken, string Signature, int IlSizeBytes, string IlSha256, string PdbStatus, string? PdbSha256,
    int TotalInstructions, bool Truncated, IReadOnlyList<ResolvedIlInstruction> Instructions,
    IReadOnlyList<IlCostSite> CostSites, IReadOnlyList<string> Limitations);

/// <summary>
/// Deterministic metadata/IL/PDB inspection. All path access is explicitly
/// allowlisted; PDB document paths and symbol-server links are never followed.
/// </summary>
public sealed class BinarySourceInspector(WorkspaceGuard guard)
{
    public BinaryMethodCatalog Methods(string assemblyPath, string query = "", int limit = 100)
    {
        var path = AssemblyPath(assemblyPath);
        var bytes = BoundedBinaryFile.Read(path, 64_000_000);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("The file is not a managed PE assembly.");
        var reader = pe.GetMetadataReader();
        var formatter = new BinarySignatureFormatter();
        var entries = new List<BinaryMethodEntry>();
        var count = 0;
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            var signature = formatter.Method(reader, handle);
            if (!signature.Contains(query ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            if (entries.Count >= Math.Clamp(limit, 1, 2000)) continue;
            entries.Add(new BinaryMethodEntry(MetadataTokens.GetToken(handle),
                formatter.TypeName(reader, method.GetDeclaringType()), reader.GetString(method.Name),
                signature, method.RelativeVirtualAddress != 0));
        }
        return new BinaryMethodCatalog(path, Hash(bytes), ModuleId(reader), count,
            count > entries.Count, entries);
    }

    public BinarySourceEvidence Inspect(string assemblyPath, int methodToken,
        string? pdbPath = null, string? sourceFilePath = null, int maxInstructions = 2000)
    {
        var path = AssemblyPath(assemblyPath);
        var bytes = BoundedBinaryFile.Read(path, 64_000_000);
        byte[]? source = sourceFilePath is null ? null : BoundedBinaryFile.Read(
            guard.RequireFile(sourceFilePath, ".cs"), 4_000_000);
        byte[]? pdb = null;
        if (pdbPath is not null)
            pdb = BoundedBinaryFile.Read(guard.RequireFile(pdbPath, ".pdb"), 32_000_000);
        else
        {
            // Only the same-directory .pdb is considered. Paths embedded in
            // the target PE or PDB never cause arbitrary host file reads.
            var neighbor = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(neighbor)) pdb = BoundedBinaryFile.Read(guard.RequireFile(neighbor, ".pdb"), 32_000_000);
        }
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidDataException("The file is not a managed PE assembly.");
        var reader = pe.GetMetadataReader();
        var row = methodToken & 0xFFFFFF;
        if ((methodToken & unchecked((int)0xFF000000)) != 0x06000000 || row == 0 || row > reader.MethodDefinitions.Count)
            throw new ArgumentException("methodToken must be a valid MethodDef token from assembly_methods (0x06000001 and later).");
        var handle = MetadataTokens.MethodDefinitionHandle(row);
        var method = reader.GetMethodDefinition(handle);
        var il = method.RelativeVirtualAddress == 0 ? [] : pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        var map = PortablePdbSourceMap.Read(pe, handle, il.Length, pdb, source);
        var all = ResolvedIlDecoder.Decode(il, reader, map.Points);
        var returned = all.Take(Math.Clamp(maxInstructions, 1, 10000)).ToArray();
        var sites = returned.Where(i => i.OpCode is "box" or "newobj" or "newarr" or "call" or "callvirt")
            .Take(2000).Select(i => new IlCostSite(i.Offset,
                i.OpCode == "box" ? "il_boxing" : i.OpCode == "newarr" ? "il_array_creation"
                : i.OpCode == "newobj" ? "il_constructor" : "il_call",
                i.Operand, i.SourceMappingStatus, i.Source,
                "An IL operation is not a measured runtime cost. JIT elimination/inlining and input frequency must be checked.")).ToArray();
        return new BinarySourceEvidence(path, Hash(bytes), ModuleId(reader), methodToken,
            new BinarySignatureFormatter().Method(reader, handle), il.Length, Hash(il), map.Status,
            map.PdbSha256, all.Count, returned.Length < all.Count, returned, sites,
            [
                "PDB identity is checked against the PE CodeView GUID/stamp. This binds ordinary build artifacts, not authenticity against a maliciously forged binary/PDB.",
                "IL source spans are Portable PDB sequence-point ranges, not a bijection between instructions and source expressions. Hidden ranges remain unmapped.",
                "Current source text is shown only when its original bytes match the PDB document checksum. PDB source paths are metadata; they are never read automatically.",
                "Native machine instruction to IL mapping needs a JIT native/IL map from the same code version. IL sequence points alone do not establish native instruction source locations.",
                "Source-mapped boxing, constructors, arrays and calls are investigation sites, not proof of allocations or slow execution."
            ]);
    }

    private string AssemblyPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".dll" or ".exe")) throw new ArgumentException("Expected a .dll or .exe.");
        return guard.RequireFile(path, extension);
    }
    private static string ModuleId(MetadataReader reader) => reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D");
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal static class BoundedBinaryFile
{
    public static byte[] Read(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("Binary artifact exceeds the read budget.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Artifact changed while being read.");
        return bytes;
    }
}
