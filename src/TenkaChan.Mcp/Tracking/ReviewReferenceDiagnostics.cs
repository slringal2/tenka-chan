namespace TenkaChan.Mcp.Tracking;

/// <summary>Diagnostic labels, never hash inputs. Paths are not opened by this record.</summary>
public sealed record ReviewReferenceDiagnostic(string ProjectId, string ProjectPath, string Kind,
    string? Display, string? FilePath, string IdentityHash, string Identity,
    bool IdentityComplete, string? FailureCode, bool LabelsTruncated)
{
    public static ReviewReferenceDiagnostic Create(string projectId, string projectPath, string kind,
        string? display, string? filePath, string identity, bool complete, string? failureCode)
    {
        const int max = 2048;
        string? Label(string? value) => value is { Length: > max } ? value[..max] : value;
        return new(projectId, Label(projectPath)!, kind, Label(display), Label(filePath),
            ReviewProgress.Hash(identity), Label(identity)!, complete, failureCode,
            projectPath.Length > max || (display?.Length ?? 0) > max || (filePath?.Length ?? 0) > max || identity.Length > max);
    }
}
public sealed record ReviewReferenceDiagnosticReport(bool Available, int Total, int UnresolvedIdentities,
    bool Limited, IReadOnlyList<ReviewReferenceDiagnostic> Items, string Scope, string Note);

public sealed record ReviewReferenceDiagnosticSummary(bool Available, int Total, int UnresolvedIdentities,
    bool FailuresLimited, IReadOnlyList<ReviewReferenceDiagnostic> Failures, string DetailsTool, string Note);

/// <summary>
/// Keeps per-project bounded observations from the SAME reads that produced the context hash.
/// Failure-first ordering prevents successful framework references from hiding the failure.
/// No filesystem probes, assembly loads, compiler invocations or checkpoint changes.
/// </summary>
public static class ReviewReferenceDiagnostics
{
    public const int MaximumItems = 100;
    public const string Scope = "loaded_project_and_transitive_source_dependencies";
    public const string Note = "Captured when this source snapshot's review catalog was created, not a live filesystem probe. " +
        "identityComplete means metadata/project identity was available; it does NOT prove all compiler or runtime references resolve. " +
        "display/filePath are diagnostic labels and are not added to contextHash. Compare identityHash and state.contextChange; " +
        "a changed HintPath inside the csproj still changes projectFiles. Inspect compiler diagnostics separately. " +
        "No automatic restore, build, reload or execution. Missing/truncated observations are not success evidence.";

    public static ReviewReferenceDiagnosticSummary Summarize(ReviewReferenceDiagnosticReport report) =>
        new(report.Available, report.Total, report.UnresolvedIdentities, report.UnresolvedIdentities > 5,
            report.Items.Where(r => !r.IdentityComplete).Take(5).ToArray(), "tenka_review_target", Note);

    public static ReviewReferenceDiagnosticReport Capture(IEnumerable<ReviewReferenceDiagnostic> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.ToArray();
        return new(true, rows.Length, rows.Count(r => !r.IdentityComplete), rows.Length > MaximumItems,
            Ordered(rows).Take(MaximumItems).ToArray(), Scope, Note);
    }
    public static ReviewReferenceDiagnosticReport Combine(IEnumerable<ReviewReferenceDiagnosticReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var parts = reports.ToArray();
        var total = parts.Sum(r => r.Total);
        return new(parts.Length > 0 && parts.All(r => r.Available), total, parts.Sum(r => r.UnresolvedIdentities),
            parts.Any(r => r.Limited) || total > MaximumItems,
            Ordered(parts.SelectMany(r => r.Items)).Take(MaximumItems).ToArray(), Scope, Note);
    }
    private static IOrderedEnumerable<ReviewReferenceDiagnostic> Ordered(IEnumerable<ReviewReferenceDiagnostic> rows) =>
        rows.OrderBy(r => r.IdentityComplete).ThenBy(r => r.ProjectPath, StringComparer.Ordinal)
            .ThenBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => Path.GetFileName(r.FilePath), StringComparer.Ordinal)
            .ThenBy(r => r.FilePath, StringComparer.Ordinal)
            .ThenBy(r => r.Display, StringComparer.Ordinal).ThenBy(r => r.IdentityHash, StringComparer.Ordinal);
}
