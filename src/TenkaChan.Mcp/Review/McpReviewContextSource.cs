using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Review;

/// <summary>
/// MCP stdio adapter for the Roslyn workspace. ReviewTools rejects workspace
/// replacement during active analysis. Each MCP server process owns its own
/// workspace; other processes cannot mutate this snapshot.
/// </summary>
public sealed class McpReviewContextSource(RoslynReviewService roslyn)
    : IReviewContextSource
{
    private IReadOnlyList<RuleFinding> _findings = [];
    public async Task<ReviewContext> GetContextAsync(
        int maxMethods, CancellationToken cancellationToken)
    {
        var targets = roslyn.GetReviewTargets(maxMethods, 0);
        _findings = await roslyn.ScanAsync(1000, cancellationToken);
        return new ReviewContext(roslyn.CurrentSnapshotId, targets, _findings);
    }

    public async Task<ReviewMethodContext> GetMethodAsync(
        string methodId, CancellationToken cancellationToken)
    {
        var inspection = await roslyn.InspectAsync(
            methodId, includeCallers: true, cancellationToken);
        var plan = roslyn.SuggestTestPlan(methodId);
        return new ReviewMethodContext(inspection, plan,
            _findings.Where(x => x.MethodId == methodId).Take(12).ToArray());
    }
}
