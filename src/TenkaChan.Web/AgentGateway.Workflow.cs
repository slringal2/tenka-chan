using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public sealed partial class AgentGateway
{
    private readonly AgentSessionApprovals _approvals = new();

    public void EndSession(string session)
    {
        _approvals.Revoke(session);
        if (_investigationStore.IsValueCreated) Investigations.Unbind(session);
    }

    private object SessionStatus(string session)
    {
        var monitor = journal.Monitor(policy.Enabled, policy.AllowBuild, policy.AllowExecution);
        var current = monitor.Sessions.Single(s => s.Id == session);
        return new
        {
            sessionId = session, current.Status, calls = current.Calls,
            remainingCalls = Math.Max(0, AgentJournal.MaxCallsPerSession - current.Calls),
            approval = _approvals.Get(session),
            investigation = InvestigationSummary(session),
            limits = new { maxSessions = AgentJournal.MaxSessions, maxCallsPerSession = AgentJournal.MaxCallsPerSession,
                maxConcurrentCallsPerSession = 1, idleExpiryMinutes = 15, maximumApprovalMinutes = AgentSessionApprovals.MaximumMinutes,
                readWaitSeconds = AgentWorkspaceAccess.ReadWaitSeconds },
            ownership = new { experiments = "current_investigation", evidenceCallIds = "completed_calls_in_current_or_resumed_investigation",
                results = "live_session_or_retained_investigation", workspace = "shared_current_snapshot", approval = "current_session_only" },
            nextAction = "Finish unused sessions using tenka_finish in their owning clients. Reconnect explicitly before exhausting the call budget; Explicitly resume investigationId after reconnecting to reuse saved experiments/results/evidence; execution approval and old snapshot IDs do not transfer. Approval expires independently and is never extended by reads."
        };
    }

    private Task<object> ApproveSessionAsync(string session, JsonElement a, CancellationToken ct) =>
        Get<DashboardState>().ExploreAsync(Str(a, "snapshotId"), (index, _) =>
        {
            var live = journal.Monitor(policy.Enabled, policy.AllowBuild, policy.AllowExecution).Sessions
                .Where(s => s.Status is "idle" or "running").Select(s => s.Id);
            _approvals.RetainSessions(live);
            var project = index.Project(Str(a, "projectId"));
            var grant = _approvals.Grant(session, index.SnapshotId, project.Path,
                Strings(a, "tools") ?? throw new ArgumentException("Missing field: tools"),
                Number(a, "minutes", AgentSessionApprovals.DefaultMinutes), Bool(a, "approved"),
                policy.AllowBuild, policy.AllowExecution);
            return Task.FromResult((object)new { status = "session_approval_granted", approval = grant });
        }, ct);

    private async Task<bool> ExplorerApprovalAsync(string session, string tool, JsonElement a, CancellationToken ct)
    {
        if (!AgentSessionApprovals.Supports(tool)) return Bool(a, "approved");
        if (Bool(a, "approved"))
        {
            AgentSessionApprovals.RequirePermissions(tool, true, policy.AllowBuild, policy.AllowExecution);
            return true;
        }
        // Resolve scope from authoritative IDs under the gate. ExecuteAsync then rechecks
        // the SAME snapshot under its own gate before any side effect. No automatic retries.
        return (bool)await Get<DashboardState>().ExploreAsync(Str(a, "snapshotId"), (index, _) =>
        {
            string projectId;
            if (tool == "build") projectId = Str(a, "projectId");
            else if (tool == "run_companion")
                projectId = ExplorerProjectActions.Companions(index, Get<WorkspaceGuard>())
                    .SingleOrDefault(c => c.Id == Str(a, "companionId"))?.ProjectId
                    ?? throw new WorkspaceBuildConflictException("Choose a currently discovered test or benchmark companion.");
            else
                projectId = ExplorerProjectActions.Assemblies(index, Get<WorkspaceBinaryDiscovery>())
                    .SingleOrDefault(x => x.Id == Str(a, "assemblyId"))?.ProjectId
                    ?? throw new WorkspaceBuildConflictException("Choose a currently discovered executable assembly.");
            var path = index.Project(projectId).Path;
            return Task.FromResult((object)_approvals.Authorize(session, path, tool, false, policy.AllowBuild, policy.AllowExecution));
        }, ct);
    }
}
