using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public sealed partial class DashboardState
{
    // Opening MSBuild is explicitly authorized by the caller. A source snapshot selected
    // in another browser/agent cannot be silently replaced by a stale open request.
    /// <summary>
    /// Explicitly approved MSBuild re-evaluation of the currently loaded
    /// workspace. No caller-supplied path, automatic restore, or permission
    /// carryover. Reject stale requests before clearing the old snapshot.
    /// </summary>
    public async Task<SolutionSummary> AgentReloadAsync(string expectedSnapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSnapshotId))
            throw new ArgumentException("reload_workspace requires the current snapshotId from tenka_context.");
        if (!await _gate.WaitAsync(0, ct))
            throw new WorkspaceBuildConflictException("Workspace is busy. No reload was queued.");
        try
        {
            EnsureOpen();
            if (!AgentWorkspaceGate.Matches(expectedSnapshotId, _solution!.SnapshotId))
                throw new WorkspaceBuildConflictException(
                    AgentWorkspaceGate.ConflictMessage(expectedSnapshotId, _solution!.SnapshotId));
            var path = _path ?? throw new InvalidOperationException("No previously opened workspace path is available.");
            InvalidatePreparedWorkspace();
            roslyn.ClearExplorerMeasurement();
            // Explicitly use the previously loaded absolute .slnx/.sln/.csproj.
            // RoslynReviewService repeats MSBuild trust and path-allowlist checks.
            var result = await roslyn.OpenAsync(path, ct);
            _solution = result;
            _path = path;
            _lastOpened = DateTimeOffset.UtcNow;
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<SolutionSummary> AgentOpenAsync(string path, string? expectedSnapshotId, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new WorkspaceBuildConflictException("Workspace is busy. No open was queued.");
        try
        {
            if (!AgentWorkspaceGate.Matches(expectedSnapshotId, _solution?.SnapshotId))
                throw new WorkspaceBuildConflictException(AgentWorkspaceGate.ConflictMessage(expectedSnapshotId, _solution?.SnapshotId));
            InvalidatePreparedWorkspace();
            _path = null;
            var result = await roslyn.OpenAsync(path, ct);
            _solution = result; _path = Path.GetFullPath(path); _lastOpened = DateTimeOffset.UtcNow;
            return result;
        }
        finally { _gate.Release(); }
    }
}
