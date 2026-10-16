using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TenkaChan.Mcp.Agent;

[McpServerToolType]
public sealed class AgentWorkflowTools
{
    [McpServerTool(Name = "tenka_approve_session")]
    [Description("After explicit user consent, approve only the selected loaded project and named tools for 1..30 minutes (default 10). tools uses build|jit|trace|run_companion|run_experiment, not MCP-prefixed names. Replaces this session's prior grant. Host switches and all lower execution checks remain mandatory. No build/execution/restore occurs. During the grant, approved can be omitted for matching operations; current snapshot IDs and experiment ownership are still checked. Code may change during the interval. Opening/replacing a workspace is NOT covered.")]
    public static Task<string> ApproveSession(DashboardAgentClient client, string snapshotId, string projectId,
        string[] tools, int minutes = AgentSessionApprovals.DefaultMinutes, bool approved = false,
        CancellationToken cancellationToken = default) =>
        client.CallAsync("approve_session", new { snapshotId, projectId, tools, minutes, approved }, cancellationToken);

    [McpServerTool(Name = "tenka_revoke_approval")]
    [Description("Revoke this session's temporary approval immediately. Does not terminate an already-admitted process or remove server switches. No other session is affected.")]
    public static Task<string> RevokeApproval(DashboardAgentClient client, CancellationToken cancellationToken = default) =>
        client.CallAsync("revoke_approval", new { }, cancellationToken);

    [McpServerTool(Name = "tenka_session_status")]
    [Description("Read this session's call budget, server limits, active temporary approval and ownership constraints. Works without a loaded workspace. Does not reconnect or extend approval. The status call itself counts toward the call budget.")]
    public static Task<string> SessionStatus(DashboardAgentClient client, CancellationToken cancellationToken = default) =>
        client.CallAsync("session_status", new { }, cancellationToken);

    [McpServerTool(Name = "tenka_attach_workspace")]
    [Description("Attach read-only to the already loaded workspace at the exact requested .sln/.slnx/.csproj path and obtain its current context/IDs. Does NOT open MSBuild, reload, build, execute or replace UI selection. No approval required. Different or unloaded workspace is an error. Read operations may wait up to 10 seconds for the existing workspace gate; source selection is revalidated after waiting. This is not an old-snapshot pin or a parallel Roslyn workspace.")]
    public static Task<string> AttachWorkspace(DashboardAgentClient client, string path, CancellationToken cancellationToken = default) =>
        client.CallAsync("attach_workspace", new { path }, cancellationToken);
}
