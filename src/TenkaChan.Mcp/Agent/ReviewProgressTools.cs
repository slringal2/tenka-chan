using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TenkaChan.Mcp.Agent;

[McpServerToolType]
public sealed class ReviewProgressTools
{
    [McpServerTool(Name = "tenka_review_queue")]
    [Description("Find unreviewed or changed classes/methods before repeating work. Persistent checkpoints survive server restarts. state=pending|all|unreviewed|needs_review|checked|optimized; kind=method|type|all. Context changes conservatively require recheck; requiresReload means reopen the project first. Use returned nextCursor as cursor with offset=0 to continue while marking earlier targets; never increment offsets in a shrinking pending set. trackableOnly filters unsupported individual marks, not verified work. Check pending from the beginning after a pass; hasMore=false does not prove whole-codebase coverage. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<string> ReviewQueue(DashboardAgentClient client, string? snapshotId = null, string state = "pending", string kind = "method", string? projectId = null, string? query = null, int offset = 0, int limit = 50, string? cursor = null, bool trackableOnly = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_queue", new {snapshotId,state,kind,projectId,query,offset,limit,cursor,trackableOnly},cancellationToken);

    [McpServerTool(Name = "tenka_review_target")]
    [Description("Read a target's persistent review state, source/context hashes, latest revision and history. Use these exact hashes and revision for tenka_mark_review; source selection IDs remain snapshot-specific. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<string> ReviewTarget(DashboardAgentClient client, string targetId, string? snapshotId = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_target", new {snapshotId,targetId},cancellationToken);

    [McpServerTool(Name = "tenka_mark_review")]
    [Description("Record completed inspection (checked), reported optimization work (optimized), or explicit reset (unreviewed). NOT proof of correctness or speedup. Requires exact source/context hashes and expectedRevision (0 initially); conflicts/stale source are rejected. Explain the work and limitations in summary; evidenceCallIds must be completed calls in this session. Does not edit source or execute code; type/method marks do not cascade. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<string> MarkReview(DashboardAgentClient client, string targetId, string sourceHash, string contextHash, long expectedRevision, string status, string summary, string? snapshotId = null, string[]? evidenceCallIds = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_mark", new {snapshotId,targetId,sourceHash,contextHash,expectedRevision,status,summary,evidenceCallIds},cancellationToken);

    [McpServerTool(Name = "tenka_review_diff")]
    [Description("Read paged +/- line differences between a recorded checkpoint and loaded source, or two saved revisions. Default fromRevision=latest, toRevision=loaded source. Select an earlier revision to see optimization changes after marking. Not a Git diff or semantic equivalence check. Reopen if source changed since loading. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<string> ReviewDiff(DashboardAgentClient client, string targetId, string? snapshotId = null, long? fromRevision = null, long? toRevision = null, int offset = 0, int limit = 150, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_diff", new {snapshotId,targetId,fromRevision,toRevision,offset,limit},cancellationToken);

    [McpServerTool(Name = "tenka_review_history")]
    [Description("Read persistent checkpoint metadata: revision, source/context hashes, agent attribution, summary and copied tool-evidence metadata. These marks survive session expiry. No claim is promoted to verified just because it has evidence links. Omit snapshotId to use the current workspace; explicit stale IDs are never replaced.")]
    public static Task<string> ReviewHistory(DashboardAgentClient client, string targetId, string? snapshotId = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        client.CallAsync("review_history", new {snapshotId,targetId,offset,limit},cancellationToken);

}
