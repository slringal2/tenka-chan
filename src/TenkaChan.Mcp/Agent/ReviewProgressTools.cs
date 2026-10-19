using System.ComponentModel;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Tracking;
namespace TenkaChan.Mcp.Agent;

[McpServerToolType]
public sealed class ReviewProgressTools
{
    [McpServerTool(Name = "tenka_review_queue")]
    [Description("Find unreviewed or changed classes/methods before repeating work. Persistent checkpoints survive server restarts. state=pending|all|unreviewed|needs_review|checked|optimized; kind=all (default)|method|type. coverage always includes the entire loaded index; check pendingHiddenByFilters and pending type counts even when items is empty. Use tenka_review_context with a targetId to obtain source IDs and ready-to-use IL candidate arguments. Context changes conservatively require recheck; requiresReload means reopen the project first. Use returned nextCursor as cursor with offset=0 to continue while marking earlier targets; never increment offsets in a shrinking pending set. trackableOnly filters unsupported individual marks, not verified work. Check pending from the beginning after a pass; hasMore=false does not prove whole-codebase coverage.")]
    public static Task<string> ReviewQueue(DashboardAgentClient client, string snapshotId, string state = "pending", string kind = ReviewQueueViews.DefaultKind,
        string? projectId = null, string? query = null, int offset = 0, int limit = 20, string? cursor = null, bool trackableOnly = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_queue", new {snapshotId,state,kind,projectId,query,offset,limit,cursor,trackableOnly},cancellationToken);

    [McpServerTool(Name = "tenka_review_context")]
    [Description("Resolve a durable targetId to source details, and fresh snapshot-specific typeId/methodId/projectId selection IDs, review hashes/revision and bounded binary candidates in one snapshot-gated read. includeBinary=true inspects at most 8 discovered assemblies and returns at most 50 exact type/name candidates per assembly. Choose the intended signature/configuration and pass a candidate's arguments to tenka_il; no overload is chosen automatically and source/build identity is not proven. No build, reload or execution. Large results are available through tenka_read_result with the returned callId.")]
    public static Task<string> ReviewContext(DashboardAgentClient client, string snapshotId, string targetId,
        bool includeBinary = true, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_context", new {snapshotId,targetId,includeBinary},cancellationToken);

    [McpServerTool(Name = "tenka_review_target")]
    [Description("Read a target's persistent review state, source/context hashes, latest revision and history. Use these exact hashes and revision for tenka_mark_review; source selection IDs remain snapshot-specific.")]
    public static Task<string> ReviewTarget(DashboardAgentClient client, string snapshotId, string targetId, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_target", new {snapshotId,targetId},cancellationToken);

    [McpServerTool(Name = "tenka_mark_review")]
    [Description("Record completed inspection (checked), reported optimization work (optimized), or explicit reset (unreviewed). NOT proof of correctness or speedup. Requires exact source/context hashes and expectedRevision (0 initially); conflicts/stale source are rejected. Explain the work and limitations in summary; evidenceCallIds must be completed calls in this session or its explicitly resumed investigation. Optional baselineExperimentId and candidateExperimentId attach server-read before/after reports recorded through tenka_measure_target or tenka_measure_companion; both must be supplied. Companion result metadata includes benchmarkJob; short jobs with small sample counts remain insufficient_samples. Pass investigationId to bind directly to the owning investigation without a separate tenka_investigation resume round-trip; also pass approved=true to authorize access to that stored investigation. Without them performance is unmeasured, including for optimized. Attachments do not prove correctness, source/DLL identity or significance. Does not edit source or execute code; type/method marks do not cascade.")]
    public static Task<string> MarkReview(DashboardAgentClient client, string snapshotId, string targetId, string sourceHash, string contextHash,
        long expectedRevision, string status, string summary, string[]? evidenceCallIds = null, CancellationToken cancellationToken = default,
        string? baselineExperimentId = null, string? candidateExperimentId = null, string? investigationId = null, bool approved = false) =>
        client.CallAsync("review_mark", new {snapshotId,targetId,sourceHash,contextHash,expectedRevision,status,summary,evidenceCallIds,baselineExperimentId,candidateExperimentId,investigationId,approved},cancellationToken);

    [McpServerTool(Name = "tenka_review_diff")]
    [Description("Read paged +/- line differences between a recorded checkpoint and loaded source, or two saved revisions. Default fromRevision=latest, toRevision=loaded source. Select an earlier revision to see optimization changes after marking. Not a Git diff or semantic equivalence check. Reopen if source changed since loading.")]
    public static Task<string> ReviewDiff(DashboardAgentClient client, string snapshotId, string targetId, long? fromRevision = null,
        long? toRevision = null, int offset = 0, int limit = 150, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_diff", new {snapshotId,targetId,fromRevision,toRevision,offset,limit},cancellationToken);

    [McpServerTool(Name = "tenka_review_history")]
    [Description("Read persistent checkpoint metadata: revision, source/context hashes, agent attribution, summary and copied tool-evidence metadata. These marks survive session expiry. No claim is promoted to verified just because it has evidence links.")]
    public static Task<string> ReviewHistory(DashboardAgentClient client, string snapshotId, string targetId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_history", new {snapshotId,targetId,offset,limit},cancellationToken);

}
