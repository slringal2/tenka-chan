using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Review;

/// <summary>
/// The source owns a coherent Roslyn snapshot. An active automated review must
/// prevent its workspace from being replaced while a method is being inspected.
/// </summary>
public interface IReviewContextSource
{
    Task<ReviewContext> GetContextAsync(int maxMethods, CancellationToken cancellationToken);
    Task<ReviewMethodContext> GetMethodAsync(string methodId, CancellationToken cancellationToken);
}

public sealed record ReviewContext(string SnapshotId, IReadOnlyList<ReviewTarget> Targets,
    IReadOnlyList<RuleFinding> RuleFindings);

public sealed record ReviewMethodContext(MethodInspection Inspection,
    SuggestedTestPlan TestPlan, IReadOnlyList<RuleFinding> Rules);

public sealed record ProposedHypothesis(string Category, string Claim,
    string FailureCondition, string Reason, IReadOnlyList<string> TestIdeas);

public sealed record ReviewModelOutput(IReadOnlyList<ProposedHypothesis> Hypotheses,
    string Notes);

public interface IReviewReasoner
{
    string ModelName { get; }
    Task<ReviewModelOutput> AnalyzeAsync(ReviewMethodContext context,
        CancellationToken cancellationToken);
}

public sealed record AutonomousReviewOptions(int MaxMethods = 10, int MaxMinutes = 15);

public sealed record ReviewActivity(string TimeUtc, string Kind, string Message,
    string? MethodId = null, string? HypothesisId = null);

public sealed record AutonomousReviewStatus(
    string RunId, string SnapshotId, string Model, string Status,
    int TotalMethods, int CompletedMethods, int HypothesisCount, int ErrorCount,
    string? CurrentMethod, string StartedUtc, string? FinishedUtc,
    IReadOnlyList<ReviewActivity> Activity);
