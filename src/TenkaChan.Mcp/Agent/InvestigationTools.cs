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

    [McpServerTool(Name = "tenka_measure_companion")]
    [Description("Build/run a discovered EXISTING BenchmarkDotNet companion while observing a specific review target under the workspace gate. Requires snapshotId and targetId. companionId is optional: select a unique benchmark from the current workspace by its ProjectReference chain, or reject ambiguity without execution. Supplied companionId must also reference the target. UNSANDBOXED; requires run_companion host switches and explicit approval. Saves a real companion experimentId/report and target observation for later baseline/candidate attachment in tenka_mark_review. Existing old run_companion IDs are not retroactively bound. Each baseline/candidate needs a fresh run with the same benchmarkJob (medium by default, short optional); small sample counts remain preliminary. Pass investigationId (typically the baseline run's returned investigationId) to bind this run to a stored investigation without a separate tenka_investigation resume round-trip; the response echoes benchmarkJob and preliminary.")]
    public static Task<string> MeasureCompanion(DashboardAgentClient client, string snapshotId, string targetId,
        string? companionId = null, bool approved = false, bool allowRestore = false, string benchmarkJob = "medium",
        string? investigationId = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("measure_companion", new { snapshotId, targetId, companionId, approved, allowRestore, benchmarkJob, investigationId }, cancellationToken);

    [McpServerTool(Name = "tenka_measure_target")]
    [Description("Run an UNEXECUTED prepared benchmark and durably bind its real result to a review target. Use prepare_experiment(kind=benchmark) -> tenka_measure_target directly; DO NOT call tenka_run_experiment first because executed experiment IDs are rejected and never replayed. UNSANDBOXED; same host execution switches, generated experiment gate and explicit client approval. Must be owned by this investigation/target project; no automatic build/retry. Separate before/after IDs with identical benchmark code are required for tenka_mark_review. investigationId can bind a stored investigation without a resume round-trip (approved=true). This does NOT verify DLL/source identity, workload correctness or significance.")]
    public static Task<string> MeasureTarget(DashboardAgentClient client, string snapshotId, string targetId,
        string experimentId, bool approved = false, string? investigationId = null,
        CancellationToken cancellationToken = default) =>
        client.CallAsync("measure_target", new { snapshotId, targetId, experimentId, approved, investigationId }, cancellationToken);
}
