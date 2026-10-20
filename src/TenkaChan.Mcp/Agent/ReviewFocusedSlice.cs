using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Tracking;
using TrackedReviewTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;

namespace TenkaChan.Mcp.Agent;

/// <summary>One bounded CFG block. Operations are previews, not complete IL or runtime observations.</summary>
public sealed record ReviewFocusedBlock(int Ordinal, string Kind, int TotalOperations,
    IReadOnlyList<string> Operations);

public sealed record ReviewFocusedAnalysis(
    int TotalCallees, IReadOnlyList<string> Callees,
    int TotalCallers, IReadOnlyList<string> Callers,
    int TotalBlocks, IReadOnlyList<ReviewFocusedBlock> Blocks,
    int TotalDiagnostics, IReadOnlyList<string> Diagnostics,
    bool CalleesTruncated, bool CallersTruncated, bool BlocksTruncated, bool DiagnosticsTruncated,
    string? AnalysisNote);

/// <summary>
/// Bounded review content at the response root, NOT source.inspection.source or
/// repeated read_result /data/data paths. IDs refer to the currently loaded
/// snapshot; TargetId/mark history is independently durable.
/// </summary>
public sealed record ReviewFocusedSlice(
    string SnapshotId, string TargetId, string Kind, string Name,
    string ProjectId, string? TypeId, string? MethodId,
    string Status, string Reason, long ExpectedRevision,
    bool SourceChanged, bool ContextChanged, bool RequiresReload,
    string SourceHash, string ContextHash,
    int SourceOffset, int SourceLength, int SourceTotalChars, bool SourceHasMore,
    string Source, ReviewFocusedAnalysis? Analysis,
    string? ContextChangeStatus, IReadOnlyList<string> ContextChangedComponents,
    string NextAction)
{
    public string SelectionScope => "source selection IDs are for current loaded snapshot; targetId is durable";
    public string ContextSemantics =>
        "ContextHash includes sourceFiles (raw source content) from the project/dependency closure, " +
        "plus generated sources and compilation/reference inputs. Whitespace-only changes can change " +
        "both sourceHash and contextHash. Only CRLF/CR line endings are normalized; no whitespace " +
        "edits are silently ignored. ContextChanged is independent of semantic behavior.";
    public bool SourceBinaryVerified => false;
    public string EvidenceStatus => "source_inspection_unverified";
}

/// <summary>
/// Deterministic, pure response formatter. Caller must resolve the durable
/// review ID under the workspace snapshot gate before passing source/analysis.
/// </summary>
public static class ReviewFocusedSlices
{
    public const int DefaultSourceLimit = 12000;
    public const int MaxSourceLimit = 16000;

    public static ReviewFocusedSlice Build(string snapshotId, TrackedReviewTarget target,
        ReviewState state, ExplorerMethod? method, MethodInspection? inspection,
        int sourceOffset = 0, int sourceLimit = DefaultSourceLimit)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new ArgumentException("A current source snapshot is required.");
        if (sourceOffset < 0 || sourceLimit is < 1 or > MaxSourceLimit)
            throw new ArgumentOutOfRangeException(nameof(sourceLimit),
                "sourceOffset must be nonnegative and sourceLimit must be 1..16000 characters.");
        if (target.Kind == "method")
        {
            if (method is null || inspection is null ||
                target.SelectionId != method.Id || inspection.Method.Id != method.Id ||
                target.ProjectId != method.ProjectId)
                throw new InvalidOperationException("Method selection no longer matches the loaded review target. Refresh context; never guess methodId.");
        }
        else if (target.Kind != "type" || method is not null || inspection is not null)
            throw new ArgumentException("Only one resolved method or type target can be inspected.");

        // The source reviewed for this durable mark is the normalized tracker
        // source, which can be longer than InspectAsync's 20k display limit.
        // The hash is not a claim that the backing files are unchanged on disk.
        var text = target.Source;
        if (text.Length > ReviewProgress.MaxSourceCharacters ||
            ReviewProgress.Hash(text) != target.SourceHash)
            throw new InvalidDataException("Loaded review source does not match its expected hash.");
        if (sourceOffset > text.Length)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset),
                "sourceOffset exceeds the available tracked source characters.");
        var length = Math.Min(sourceLimit, text.Length - sourceOffset);
        var analysis = inspection is null ? null : BoundedAnalysis(inspection);
        return new ReviewFocusedSlice(
            snapshotId, target.Id, target.Kind, target.Name,
            target.ProjectId, method?.TypeId ?? (target.Kind == "type" ? target.SelectionId : null),
            method?.Id, state.Status, state.Reason, state.Revision,
            state.SourceChanged, state.ContextChanged, state.RequiresReload,
            target.SourceHash, target.ContextHash,
            sourceOffset, length, text.Length, sourceOffset + length < text.Length,
            text.Substring(sourceOffset, length),
            analysis, state.ContextChange?.Status,
            state.ContextChange?.ChangedComponents ?? [],
            state.RequiresReload
                ? "Files changed since this Roslyn snapshot. Ask for explicit reload_workspace approval, then refresh snapshotId/targetId and re-inspect before recording a mark."
                : sourceOffset + length < text.Length
                    ? "Continue with tenka_review_focus using the SAME snapshotId/targetId and sourceOffset=" + (sourceOffset + length) +
                      ". No experiment, build or additional inspect_method call is required."
                    : "Inspection content is complete for this tracked source. Review markBasis sourceHash/contextHash and expectedRevision again before tenka_mark_review; an agent-reported checked mark is not a proof of correctness.");
    }

    private static ReviewFocusedAnalysis BoundedAnalysis(MethodInspection info)
    {
        const int names = 12, blocks = 12, diagnostics = 10;
        static string Bounded(string? value, int maximum)
        {
            value ??= "";
            return value.Length > maximum ? value[..maximum] : value;
        }
        return new ReviewFocusedAnalysis(
            info.Callees.Count, info.Callees.Take(names).Select(x => Bounded(x, 240)).ToArray(),
            info.Callers.Count, info.Callers.Take(names).Select(x => Bounded(x, 240)).ToArray(),
            info.Blocks.Count, info.Blocks.Take(blocks).Select(block => new ReviewFocusedBlock(
                block.Ordinal, Bounded(block.Kind, 80), block.Operations.Length,
                block.Operations.Take(2).Select(x => Bounded(x, 160)).ToArray())).ToArray(),
            info.Diagnostics.Count, info.Diagnostics.Take(diagnostics)
                .Select(x => Bounded(x, 300)).ToArray(),
            info.Callees.Count > names, info.Callers.Count > names,
            info.Blocks.Count > blocks, info.Diagnostics.Count > diagnostics,
            info.AnalysisNote is null ? null : Bounded(info.AnalysisNote, 300));
    }
}
