using System.ComponentModel;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
namespace TenkaChan.Mcp.Agent;

[McpServerToolType]
public sealed class ReviewProgressTools
{
    [McpServerTool(Name = "tenka_review_queue")]
    [Description("Find unreviewed or changed classes/methods before repeating work. Persistent checkpoints survive server restarts. state=pending|all|unreviewed|needs_review|checked|optimized; kind=method|type|all. Context changes conservatively require recheck; requiresReload means reopen the project first. Use returned nextCursor as cursor with offset=0 to continue while marking earlier targets; never increment offsets in a shrinking pending set. trackableOnly filters unsupported individual marks, not verified work. Check pending from the beginning after a pass; hasMore=false does not prove whole-codebase coverage. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<CallToolResult> ReviewQueue(DashboardAgentClient client, string? snapshotId = null, string state = "pending", string kind = "method", string? projectId = null, string? query = null, int offset = 0, int limit = 50, string? cursor = null, bool trackableOnly = false, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("review_queue", new {snapshotId,state,kind,projectId,query,offset,limit,cursor,trackableOnly},cancellationToken));

    [McpServerTool(Name = "tenka_review_target")]
    [Description("Read a target's persistent review state (targetId also accepts an exact current methodId/typeId for lookup only), source/context hashes, latest revision and history. Use these exact hashes and revision for tenka_mark_review; source selection IDs remain snapshot-specific. The response includes canonical targetId, methodId/typeId, and ready-to-use read-only nextActions; marking still requires the canonical targetId. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<CallToolResult> ReviewTarget(DashboardAgentClient client, string targetId, string? snapshotId = null, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("review_target", new {snapshotId,targetId},cancellationToken));

    [McpServerTool(Name = "tenka_mark_review")]
    [Description("Record completed inspection (checked), reported optimization work (optimized), or explicit reset (unreviewed). NOT proof of correctness or speedup. Requires exact source/context hashes and expectedRevision (0 initially); conflicts/stale source are rejected. Explain the work and limitations in summary; evidenceCallIds must be completed calls in this investigation; explicitly resume a prior investigation to reuse its durable receipts. Does not edit source or execute code; type/method marks do not cascade. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<CallToolResult> MarkReview(DashboardAgentClient client, string targetId, string sourceHash, string contextHash, long expectedRevision, string status, string summary, string? snapshotId = null, string[]? evidenceCallIds = null, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("review_mark", new {snapshotId,targetId,sourceHash,contextHash,expectedRevision,status,summary,evidenceCallIds},cancellationToken));

    [McpServerTool(Name = "tenka_review_diff")]
    [Description("Read paged +/- line differences between a recorded checkpoint and loaded source, or two saved revisions. Default fromRevision=latest, toRevision=loaded source. Select an earlier revision to see optimization changes after marking. Also returns source/context hash comparisons; input-level context differences are not stored. Not a Git diff or semantic equivalence check. Reopen if source changed since loading. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<CallToolResult> ReviewDiff(DashboardAgentClient client, string targetId, string? snapshotId = null, long? fromRevision = null, long? toRevision = null, int offset = 0, int limit = 150, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("review_diff", new {snapshotId,targetId,fromRevision,toRevision,offset,limit},cancellationToken));

    [McpServerTool(Name = "tenka_review_history")]
    [Description("Read persistent checkpoint metadata: revision, source/context hashes, agent attribution, summary and copied tool-evidence metadata. These marks survive session expiry. No claim is promoted to verified just because it has evidence links. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<CallToolResult> ReviewHistory(DashboardAgentClient client, string targetId, string? snapshotId = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("review_history", new {snapshotId,targetId,offset,limit},cancellationToken));


    [McpServerTool(Name = "tenka_investigation")]
    [Description("Separate durable investigation state from the transport session. action=status|list|resume. Resume requires an exact investigationId and approved:true under this local server's machine credential; clientName is NOT identity. Completed evidence and experiment ownership survive restart. Nothing is executed/replayed. Large live-only results and pre-upgrade ownership cannot be reconstructed.")]
    public static Task<CallToolResult> Investigation(DashboardAgentClient client, string action = "status", string? investigationId = null, bool approved = false, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("investigation", new { action, investigationId, approved }, cancellationToken));

    [McpServerTool(Name = "tenka_propose_change")]
    [Description("Prepare a complete replacement for ONE existing C# file belonging to a current target, in independent baseline/candidate workspace copies. Get the file and expectedFileSha256 from tenka_review_workflow first. No source apply, build or execution. Bounded SDK-style projects only; unsupported external/dynamic build paths are rejected rather than run against originals.")]
    public static Task<CallToolResult> ProposeChange(DashboardAgentClient client, string targetId, string expectedFileSha256, string replacementCode, string? snapshotId = null, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("propose_change", new { targetId, expectedFileSha256, replacementCode, snapshotId }, cancellationToken));

    [McpServerTool(Name = "tenka_diff_proposal")]
    [Description("Read the stored old/new source diff of a proposal owned by this investigation. Does not modify source or execute anything. Inspect this before approval to validate or apply.")]
    public static Task<CallToolResult> DiffProposal(DashboardAgentClient client, string proposalId, int offset = 0, int limit = 150, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("diff_proposal", new { proposalId, offset, limit }, cancellationToken));

    [McpServerTool(Name = "tenka_apply_change")]
    [Description("Apply this investigation's single-file proposal only after both selected test runs pass and comparable benchmarks finish. Requires host build policy and approved:true. Checks live captured inputs and exact file bytes; refuses changed files. Retains a displaced-file backup; concurrent non-cooperating editors remain a limitation. Never marks reviewed/optimized automatically. Reload afterwards.")]
    public static Task<CallToolResult> ApplyChange(DashboardAgentClient client, string proposalId, bool approved = false, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("apply_change", new { proposalId, approved }, cancellationToken));

    [McpServerTool(Name = "tenka_revert_change")]
    [Description("Restore the exact pre-proposal file bytes only while live bytes still match this applied proposal. Requires host build policy and approved:true. Does not overwrite later edits, force-reset Git, or delete backups. A crash without a completed receipt needs manual recovery, not blind retry. Reload afterwards.")]
    public static Task<CallToolResult> RevertChange(DashboardAgentClient client, string proposalId, bool approved = false, CancellationToken cancellationToken = default) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("revert_change", new { proposalId, approved }, cancellationToken));

    [McpServerTool(Name = "tenka_review_workflow")]
    [Description("One entrypoint for edit preparation and frozen baseline/candidate validation. Default mode=inspect with targetId returns the complete file/hash and existing companions without execution. With proposalId, mode=validate runs the same test/benchmark projects on both copies; mode=result reads saved receipts. Select companion IDs when ambiguous, or use testExperimentId/benchmarkExperimentId from tenka_prepare_experiment for this subject project. Each kind independently selects a companion OR a generated experiment. Generated code and frameworks are frozen once and used against each copied subject; old standalone results are not reused. Omit experiment IDs on continuation to reuse the frozen plan. Validation is UNSANDBOXED and requires host build/execution/generated-code flags and approved:true. awaiting_next_stage means explicitly continue this same proposal; terminal and unknown-started stages are not rerun. No apply or review mark occurs here.")]
    public static Task<CallToolResult> ReviewWorkflow(DashboardAgentClient client, string? targetId = null, string? proposalId = null, string mode = "inspect", string? testCompanionId = null, string? benchmarkCompanionId = null, string? benchmarkFilter = null, bool approved = false, CancellationToken cancellationToken = default, string? testExperimentId = null, string? benchmarkExperimentId = null) =>
        AgentToolResponse.RunAsync(() => client.CallAsync("review_workflow", new { targetId, proposalId, mode, testCompanionId, benchmarkCompanionId, benchmarkFilter, approved, testExperimentId, benchmarkExperimentId }, cancellationToken));
}
