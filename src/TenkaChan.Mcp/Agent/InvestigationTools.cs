using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TenkaChan.Mcp.Agent;

[McpServerToolType]
public sealed class InvestigationTools
{
    [McpServerTool(Name = "tenka_investigation")]
    [Description("Read durable investigation status/list, or explicitly resume an investigation in this new transport session. action=status|list|resume. Resume requires investigationId and approved=true after user approval to access saved data. It restores experiments, completed evidence and small results, NOT source snapshot IDs or execution approval. Refresh tenka_context and approve only intended operations. One operation at a time per investigation; storage is local-machine scoped, not identity by clientName.")]
    public static Task<string> Investigation(DashboardAgentClient client, string action = "status",
        string? investigationId = null, bool approved = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("investigation", new { action, investigationId, approved }, cancellationToken);

    [McpServerTool(Name = "tenka_measure_target")]
    [Description("Run a prepared benchmark and durably bind its actual report to the selected target's source/context observed during this run. UNSANDBOXED: same run_experiment host switches, generated-experiment gate and per-call or scoped session approval. Requires an experiment owned by this investigation and the target's project. Holds the workspace gate; never automatically builds the target or retries execution. Run before and after using separate experiments with identical benchmark code, then pass their IDs to tenka_mark_review. Source snapshot binding does NOT prove the existing DLL matches the source, workload coverage, correctness, or statistical significance.")]
    public static Task<string> MeasureTarget(DashboardAgentClient client, string snapshotId, string targetId,
        string experimentId, bool approved = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("measure_target", new { snapshotId, targetId, experimentId, approved }, cancellationToken);
}
