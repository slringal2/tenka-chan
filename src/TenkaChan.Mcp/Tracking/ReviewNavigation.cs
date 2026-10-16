using System.Text.Json;

namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewNextAction(string Tool, JsonElement Arguments);
public sealed record ReviewNavigationResult(string TargetId, string? MethodId, string? TypeId,
    IReadOnlyList<ReviewNextAction> NextActions, JsonElement? ReviewInputs, string Note);
public sealed record ReviewHashComparison(bool HasBaseline, string? FromSourceHash, string ToSourceHash,
    string? FromContextHash, string ToContextHash, bool? SourceChanged, bool? ContextChanged,
    bool ContextInputsAvailable, string Note);

/// <summary>Read-only navigation DTOs: no inspection, execution, approval or checkpoint writes.</summary>
public static class ReviewNavigation
{
    public static ReviewNavigationResult ForTarget(string snapshotId, ReviewTarget target, ReviewState state)
    {
        var actions = new List<ReviewNextAction>();
        JsonElement? reviewInputs = null;
        if (!state.RequiresReload)
        {
            if (target.MethodId is string methodId)
            {
                actions.Add(new("tenka_inspect_method", JsonSerializer.SerializeToElement(new { snapshotId, methodId })));
                actions.Add(new("tenka_method_references", JsonSerializer.SerializeToElement(new { snapshotId, methodId })));
            }
            else if (target.TypeId is string typeId)
                actions.Add(new("tenka_inspect_type", JsonSerializer.SerializeToElement(new { snapshotId, typeId })));
            if (target.Trackable)
            {
                actions.Add(new("tenka_review_diff", JsonSerializer.SerializeToElement(new { snapshotId, targetId = target.Id })));
                reviewInputs = JsonSerializer.SerializeToElement(new
                {
                    snapshotId, targetId = target.Id, sourceHash = target.SourceHash,
                    contextHash = target.ContextHash, expectedRevision = state.Revision
                });
            }
        }
        return new(target.Id, target.MethodId, target.TypeId, actions, reviewInputs,
            state.RequiresReload ? "Reload the workspace first; no next action or mark inputs are advertised for stale source."
            : "nextActions are read-only suggestions, not executed steps. reviewInputs are concurrency guards, not a completed review: supply your own status, summary and valid evidence only after inspection. Refresh after source changes or a revision conflict.");
    }

    public static ReviewHashComparison CompareHashes(ReviewCheckpoint? from, ReviewCheckpoint? to, ReviewTarget loaded)
    {
        var source = to?.SourceHash ?? loaded.SourceHash;
        var context = to?.ContextHash ?? loaded.ContextHash;
        return new(from is not null, from?.SourceHash, source, from?.ContextHash, context,
            from is null ? null : from.SourceHash != source,
            from is null ? null : from.ContextHash != context, false,
            from is null ? "No saved baseline. A missing comparison is not evidence of unchanged code."
            : "These hashes compare the selected checkpoints, or the loaded snapshot. Context includes project documents, settings, references and dependencies. Historical context inputs were not stored, so changed components cannot be identified from hashes alone. Zero source-line differences do not imply unchanged context.");
    }
}
