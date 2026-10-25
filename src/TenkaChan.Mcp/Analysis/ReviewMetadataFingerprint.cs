using Microsoft.CodeAnalysis;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Analysis;

public sealed record ReviewReferenceFingerprint(string Identity, bool Complete)
{
    // Diagnosis only: the existing Identity bytes and fingerprint version are unchanged.
    public string? FailureCode { get; init; }
}

/// <summary>
/// Fingerprints Roslyn reference metadata, not its machine-local display path.
/// GetMetadata may materialize a lazy reference. Display labels are never manually opened;
/// no generators or assemblies are executed.
/// MVID identifies an ordinary compiler-produced module version; it is NOT a content
/// signature/authenticity proof and is not used to relax any IL/PDB/SHA validation.
/// </summary>
public static class ReviewMetadataFingerprint
{
    public static ReviewReferenceFingerprint Read(MetadataReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var properties = reference.Properties;
        var prefix = properties.Kind + "|embed=" + properties.EmbedInteropTypes + "|aliases=" +
            ReviewContextFingerprints.Digest(properties.Aliases);
        if (reference is not PortableExecutableReference portable)
            return new(prefix + "|unresolved:unsupported_reference", false) { FailureCode = "unsupported_reference" };
        try
        {
            // GetMetadata returns a non-owning copy. Do not Dispose Roslyn's metadata.
            var metadata = portable.GetMetadata();
            ModuleMetadata[] modules = metadata switch
            {
                AssemblyMetadata assembly => assembly.GetModules().ToArray(),
                ModuleMetadata module => [module],
                _ => []
            };
            if (modules.Length == 0) return new(prefix + "|unresolved:no_modules", false) { FailureCode = "no_modules" };
            var ids = modules.Select(m => m.GetModuleVersionId()).ToArray();
            if (ids.Any(id => id == Guid.Empty)) return new(prefix + "|unresolved:missing_mvid", false) { FailureCode = "missing_mvid" };
            // Module order is significant: the manifest module stays first.
            return new(prefix + "|modules=" + string.Join(",", ids.Select(id => id.ToString("N"))), true);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException or
            UnauthorizedAccessException or NotSupportedException)
        {
            // Never substitute a display path or random object ID and call it a stable identity.
            return new(prefix + "|unresolved:" + ex.GetType().Name, false)
            {
                FailureCode = ex switch
                {
                    FileNotFoundException => "file_not_found",
                    DirectoryNotFoundException => "directory_not_found",
                    BadImageFormatException => "bad_image",
                    UnauthorizedAccessException => "access_denied",
                    NotSupportedException => "not_supported",
                    IOException => "io_error",
                    _ => "metadata_unavailable"
                }
            };
        }
    }
}
