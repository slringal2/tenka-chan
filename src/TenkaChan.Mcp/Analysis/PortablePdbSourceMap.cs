using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace TenkaChan.Mcp.Analysis;

public sealed record IlSourceRegion(string Document, int StartLine, int StartColumn,
    int EndLine, int EndColumn, string ChecksumAlgorithm, string DocumentChecksum,
    string LocalSourceStatus, string? VerifiedSourceExcerpt);
public sealed record IlSourcePoint(int Offset, bool Hidden, IlSourceRegion? Region);
internal sealed record PortableSourceMap(string Status, string? PdbSha256,
    IReadOnlyList<IlSourcePoint> Points);

/// <summary>
/// PDB paths are data, never paths to open. Only explicitly supplied source
/// bytes are checksummed. Hidden sequence points form unmapped ranges.
/// </summary>
internal static class PortablePdbSourceMap
{
    private static readonly Guid Sha1Id = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256Id = new("8829d00f-11b8-4213-878b-770e8597ac16");

    public static PortableSourceMap Read(PEReader pe, MethodDefinitionHandle method,
        int ilSize, byte[]? externalPdb, byte[]? sourceBytes)
    {
        MetadataReaderProvider? provider = null;
        MemoryStream? input = null;
        var status = "missing_pdb";
        string? pdbHash = null;
        try
        {
            if (externalPdb is not null)
            {
                pdbHash = Convert.ToHexString(SHA256.HashData(externalPdb));
                input = new MemoryStream(externalPdb, writable: false);
                provider = MetadataReaderProvider.FromPortablePdbStream(input);
                status = "verified_external_portable_pdb";
            }
            else
            {
                var embedded = pe.ReadDebugDirectory().Where(e =>
                    e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
                if (embedded.Length == 0) return new PortableSourceMap(status, null, []);
                if (embedded.Length != 1) throw new InvalidDataException("Ambiguous embedded PDB entries.");
                // The PEReader verifies the embedded format. Bound its declared
                // decompressed size before asking it to allocate a buffer.
                var bytes = pe.GetSectionData(embedded[0].DataRelativeVirtualAddress)
                    .GetContent(0, Math.Min(embedded[0].DataSize, 8));
                if (bytes.Length < 8) throw new InvalidDataException("Truncated embedded PDB header.");
                var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
                if (size < 0 || size > 32_000_000)
                    throw new InvalidDataException("Embedded PDB exceeds decompressed size budget.");
                provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
                status = "verified_embedded_portable_pdb";
            }

            var reader = provider.GetMetadataReader();
            var header = reader.DebugMetadataHeader
                ?? throw new InvalidDataException("Portable PDB has no debug metadata header.");
            var id = new BlobContentId(header.Id);
            var matched = pe.ReadDebugDirectory().Any(entry =>
            {
                if (entry.Type != DebugDirectoryEntryType.CodeView) return false;
                var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
                return codeView.Age == 1 && codeView.Guid == id.Guid && entry.Stamp == id.Stamp;
            });
            if (!matched)
                throw new InvalidDataException("PDB identity does not match this PE's CodeView GUID/stamp. No source mapping is permitted.");

            var row = MetadataTokens.GetRowNumber(method);
            if (row < 1 || row > reader.MethodDebugInformation.Count)
                throw new InvalidDataException("PDB does not contain the requested method token.");
            var debug = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row));
            var points = new List<IlSourcePoint>();
            var documents = new Dictionary<DocumentHandle, (string Name, string Algorithm, string Hash, string Status)>();
            string[]? lines = null;
            var lastOffset = -1;
            foreach (var point in debug.GetSequencePoints())
            {
                if (points.Count >= 50000) throw new InvalidDataException("Excessive PDB sequence points.");
                if (point.Offset <= lastOffset || point.Offset < 0 || point.Offset >= ilSize)
                    throw new InvalidDataException("Invalid PDB sequence point offset.");
                lastOffset = point.Offset;
                if (point.IsHidden)
                {
                    points.Add(new IlSourcePoint(point.Offset, true, null));
                    continue;
                }
                var handle = point.Document.IsNil ? debug.Document : point.Document;
                if (handle.IsNil) { points.Add(new IlSourcePoint(point.Offset, true, null)); continue; }
                if (!documents.TryGetValue(handle, out var info))
                {
                    var doc = reader.GetDocument(handle);
                    var algorithm = doc.HashAlgorithm.IsNil ? Guid.Empty : reader.GetGuid(doc.HashAlgorithm);
                    var hash = reader.GetBlobBytes(doc.Hash);
                    var name = reader.GetString(doc.Name);
                    if (name.Length > 4096) throw new InvalidDataException("PDB document path too long.");
                    var label = algorithm == Sha256Id ? "SHA256" : algorithm == Sha1Id ? "SHA1" : "unsupported";
                    var verification = sourceBytes is null ? "source_not_supplied"
                        : hash.Length == 0 ? "missing_document_checksum"
                        : label == "unsupported" ? "unsupported_checksum"
                        : CryptographicOperations.FixedTimeEquals(hash,
                            label == "SHA256" ? SHA256.HashData(sourceBytes) : SHA1.HashData(sourceBytes))
                            ? "checksum_verified" : "checksum_mismatch";
                    info = (name, label, Convert.ToHexString(hash), verification);
                    documents.Add(handle, info);
                }
                string? excerpt = null;
                if (info.Status == "checksum_verified" && sourceBytes is not null)
                {
                    if (lines is null)
                    {
                        using var text = new StreamReader(new MemoryStream(sourceBytes), Encoding.UTF8, true);
                        lines = text.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                    }
                    if (point.StartLine > 0 && point.StartLine <= lines.Length)
                    {
                        excerpt = string.Join("\n", lines.Skip(point.StartLine - 1)
                            .Take(Math.Clamp(point.EndLine - point.StartLine + 1, 1, 5)));
                        if (excerpt.Length > 1000) excerpt = excerpt[..1000] + "…";
                    }
                }
                points.Add(new IlSourcePoint(point.Offset, false,
                    new IlSourceRegion(info.Name, point.StartLine, point.StartColumn,
                        point.EndLine, point.EndColumn, info.Algorithm, info.Hash, info.Status, excerpt)));
            }
            return new PortableSourceMap(status, pdbHash, points);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException("Malformed or non-portable PDB. Windows PDBs are not supported.", ex);
        }
        finally { provider?.Dispose(); input?.Dispose(); }
    }
}
