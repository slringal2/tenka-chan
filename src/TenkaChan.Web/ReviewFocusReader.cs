using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Web;

/// <summary>
/// Durable targetId -> bounded source and deterministic inspection in ONE
/// snapshot-gated read. No binary inspection, MSBuild or target execution.
/// </summary>
public static class ReviewFocusReader
{
    public static Task<object> ReadAsync(
        DashboardState dashboard, WorkspaceGuard guard, ReviewProgressStore store,
        string snapshotId, string targetId, int sourceOffset, int sourceLimit,
        CancellationToken cancellationToken) =>
        dashboard.ExploreAsync(snapshotId, async (index, roslyn) =>
        {
            var catalog = await index.ReviewCatalogAsync(guard, cancellationToken);
            var target = catalog.Require(targetId);
            var stale = await catalog.StaleProjectsAsync(guard, false, cancellationToken);
            var state = ReviewProgress.State(target,
                store.Latest().GetValueOrDefault(target.Id), stale.Contains(target.ProjectId));
            ExplorerMethod? method = null;
            MethodInspection? inspected = null;
            if (target.Kind == "method")
            {
                method = index.Method(target.SelectionId);
                inspected = await roslyn.InspectAsync(method.Id, true, cancellationToken);
            }
            return (object)ReviewFocusedSlices.Build(index.SnapshotId,
                target, state, method, inspected, sourceOffset, sourceLimit);
        }, cancellationToken, waitForRead: true);
}
