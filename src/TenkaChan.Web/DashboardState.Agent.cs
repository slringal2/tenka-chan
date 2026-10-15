using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Web;

public sealed partial class DashboardState
{
    // Opening MSBuild is explicitly authorized by the caller. A source snapshot selected
    // in another browser/agent cannot be silently replaced by a stale open request.
    public async Task<SolutionSummary> AgentOpenAsync(string path, string? expectedSnapshotId, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new WorkspaceBuildConflictException("Workspace is busy. No open was queued.");
        try
        {
            if (_solution?.SnapshotId != expectedSnapshotId)
                throw new WorkspaceBuildConflictException("Workspace changed. Read tenka_context and use its current snapshot before opening.");
            InvalidatePreparedWorkspace();
            _path = null;
            var result = await roslyn.OpenAsync(path, ct);
            _solution = result; _path = Path.GetFullPath(path); _lastOpened = DateTimeOffset.UtcNow;
            return result;
        }
        finally { _gate.Release(); }
    }
}
