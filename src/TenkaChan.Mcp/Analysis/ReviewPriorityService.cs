namespace TenkaChan.Mcp.Analysis;

/// <summary>Read existing workspace/profile evidence. Never launch a target, profiler, LLM or benchmark.</summary>
public sealed class ReviewPriorityService(RoslynReviewService roslyn)
{
    public async Task<ReviewPriorityReport> BuildAsync(ReviewPriorityOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        ReviewPriorityEngine.Validate(options);
        var snapshot = roslyn.CurrentSnapshotId;
        var profile = roslyn.CurrentHotspotProfile;
        // Analyze before applying display/filter limits, within existing server caps.
        var diagnostics = await roslyn.AnalyzeDeterministicAsync(5000, 100, cancellationToken);
        var hotspots = await roslyn.AnalyzePerformanceHotspotsAsync(500, 2000, cancellationToken);
        if (snapshot != roslyn.CurrentSnapshotId || diagnostics.Summary.SnapshotId != snapshot ||
            hotspots.SnapshotId != snapshot || !ReferenceEquals(profile, roslyn.CurrentHotspotProfile))
            throw new InvalidOperationException("Workspace or profile changed while collecting priority evidence. Retry with a stable session.");
        return ReviewPriorityEngine.Build(diagnostics, hotspots, profile, options);
    }
}
