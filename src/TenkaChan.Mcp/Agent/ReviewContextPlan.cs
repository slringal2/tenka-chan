using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Agent;

public sealed record ReviewSelection(string SnapshotId, string TargetId, string Kind,
    string ProjectId, string TypeId, string? MethodId);
public sealed record ReviewIlArguments(string SnapshotId, string MethodId, string AssemblyId,
    int MetadataToken, string AssemblySha256);
public sealed record ReviewIlCandidate(string Tool, string Signature, bool HasBody, ReviewIlArguments Arguments);
public sealed record ReviewBinaryCandidates(string AssemblyId, string TargetFramework, string Configuration,
    string AssemblySha256, IReadOnlyList<ReviewIlCandidate> Candidates, bool Limited);

/// <summary>Builds read-only call arguments from one authorized snapshot. Never picks an overload.</summary>
public static class ReviewContextPlan
{
    public const int MaximumAssemblies = 8, MaximumCandidatesPerAssembly = 50;
    public static ReviewSelection Selection(string snapshotId, ReviewTarget target, ExplorerMethod? method)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) throw new ArgumentException("Snapshot is required.");
        if (target.Kind == "type" && method is null)
            return new(snapshotId, target.Id, target.Kind, target.ProjectId, target.SelectionId, null);
        if (target.Kind != "method" || method is null || method.Id != target.SelectionId || method.ProjectId != target.ProjectId)
            throw new ArgumentException("Review target and method must belong to the same source selection.");
        return new(snapshotId, target.Id, target.Kind, method.ProjectId, method.TypeId, method.Id);
    }
    public static ReviewBinaryCandidates Binary(ReviewSelection selection, ExplorerMethod method,
        ExplorerAssembly assembly, BinaryMethodCatalog catalog)
    {
        if (selection.Kind != "method" || selection.MethodId != method.Id || selection.TypeId != method.TypeId ||
            selection.ProjectId != method.ProjectId || assembly.ProjectId != method.ProjectId)
            throw new ArgumentException("Binary candidates must belong to the selected method's project.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(Path.GetFullPath(assembly.AssemblyPath), Path.GetFullPath(catalog.AssemblyPath)))
            throw new ArgumentException("The metadata catalog belongs to a different assembly.");
        if (catalog.AssemblySha256.Length != 64 || !catalog.AssemblySha256.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact assembly SHA-256 is required.");
        var matches = catalog.Methods.Where(m => m.Type == method.MetadataType && m.Name == method.MetadataName).ToArray();
        var candidates = matches.Take(MaximumCandidatesPerAssembly).Select(m => new ReviewIlCandidate(
            "tenka_il", m.Signature, m.HasBody,
            new(selection.SnapshotId, method.Id, assembly.Id, m.MetadataToken, catalog.AssemblySha256))).ToArray();
        return new(assembly.Id, assembly.TargetFramework, assembly.Configuration, catalog.AssemblySha256,
            candidates, catalog.Truncated || matches.Length > MaximumCandidatesPerAssembly);
    }
}
