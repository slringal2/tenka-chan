using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using TrackedTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;

namespace TenkaChan.Web;

public sealed record ProposalSelection(string SnapshotId, string Root, TrackedTarget Target, IReadOnlyList<ExplorerCompanion> Companions);

public sealed partial class DashboardState
{
    public Task<object> WithProposalTargetAsync(string? snapshotId, string targetId, WorkspaceGuard guard,
        Func<ProposalSelection, Task<object>> operation, CancellationToken ct) => ExploreAsync(snapshotId, async (index, _) =>
    {
        var catalog = await index.ReviewCatalogAsync(guard, ct);
        var target = catalog.ResolveForRead(targetId);
        if (!target.Trackable) throw new InvalidOperationException(target.UnavailableReason);
        if ((await catalog.StaleProjectsAsync(guard, true, ct)).Contains(target.ProjectId))
            throw new ReviewConflictException("snapshot_outdated: Reload before preparing a change; the loaded source has changed.");
        var root = Path.GetDirectoryName(_path ?? target.ProjectPath)!;
        var paths = index.Projects.Select(p => p.Path).Append(target.File).ToArray();
        bool Contains(string directory, string file)
        {
            var relative = Path.GetRelativePath(directory, file);
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        while (paths.Any(p => !Contains(root, p)))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("No bounded common workspace root.");
        if (root == Path.GetPathRoot(root)) throw new InvalidOperationException("Project roots span a drive. Open a bounded solution before proposing changes.");
        guard.RequireDirectory(root);
        return await operation(new(index.SnapshotId, root, target, ExplorerProjectActions.Companions(index, guard)));
    }, ct);
}
