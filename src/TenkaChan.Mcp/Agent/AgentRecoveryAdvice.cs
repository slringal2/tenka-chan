using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

public sealed record AgentRecoveryHint(string Code, string NextAction);

/// <summary>
/// Bounded, operational recovery advice, not permission to execute or an automatic retry policy.
/// Classifies the current gateway's exception prefixes and known gate messages. Unknown
/// messages retain conservative guidance; message text is never copied into nextAction.
/// </summary>
public static class AgentRecoveryAdvice
{
    public static AgentRecoveryHint ForFailure(string tool, string? error, bool cancelled = false)
    {
        var text = error ?? "";
        if (cancelled)
            return new("action_cancelled", "Inspect this callId in the activity monitor before any retry. A build or target process may have started; cancellation is not proof of rollback. Re-run only after checking the outcome and obtaining any required approval.");
        if (text.Contains("investigation_busy:", StringComparison.Ordinal))
            return new("investigation_busy", "Another session is using this investigation. Inspect its activity and retry the read/resume explicitly after it finishes. No second execution was queued; do not terminate another client's work.");
        if (text.Contains("investigation_resume_approval_required:", StringComparison.Ordinal))
            return new("investigation_resume_approval_required", "Obtain approval to access the saved investigation, then call tenka_investigation with action=resume, investigationId and approved=true. This does not restore execution approval; refresh context and authorize only intended operations.");
        if (text.Contains("experiment_investigation_mismatch:", StringComparison.Ordinal))
            return new("experiment_investigation_mismatch", "Keep tenka_prepare_experiment and tenka_measure_target in the same connected MCP investigation when possible. If using a fresh adapter/session, use investigationId returned by prepare_experiment: tenka_investigation action=resume, investigationId=<saved ID>, approved=true. If you lost the ID, list investigations and identify the owner before explicit resume. Then refresh tenka_context and authorize execution again. Never replay an experiment merely to transfer ownership.");
        if (text.Contains("measurement_", StringComparison.Ordinal))
            return new("measurement_rejected", "Check the named measurement failure. Prepare two generated experiments with tenka_measure_target, or run the same existing BenchmarkDotNet companion twice using tenka_measure_companion with the selected target. Do not mix measurement origins. The candidate source/context and companion workload must still match. Inspect saved results; never replay an executed experiment or fabricate missing binding evidence.");
        if (text.Contains("investigation_not_found:", StringComparison.Ordinal))
            return new("investigation_not_found", "Call tenka_investigation action=list and use an existing investigationId from this server's data directory. Old in-memory sessions are not automatically imported.");
        if (text.StartsWith("InvalidOperationException:", StringComparison.Ordinal) &&
            (text.Contains("Open a trusted .sln or .csproj first.", StringComparison.Ordinal) ||
             text.Contains("Call workspace_open first.", StringComparison.Ordinal) ||
             text.Contains("読み込み済みプロジェクトがありません。", StringComparison.Ordinal)))
            return new("workspace_unloaded", "No workspace is loaded. Call tenka_context to confirm the server, then open a trusted .sln/.slnx/.csproj using tenka_open_project with explicit approval and expectedSnapshotId=null. Do not invent snapshot/project IDs. If the UI already has the intended workspace, use tenka_attach_workspace instead of reopening it.");
        if (text.StartsWith("UnauthorizedAccessException:", StringComparison.Ordinal))
        {
            if (text.Contains("Session approval requires explicit user consent.", StringComparison.Ordinal))
                return new("session_consent_required", "Obtain user consent for the exact project, named tools and duration before tenka_approve_session with approved=true. Consent is not implied by connecting; the server switches remain mandatory.");
            if (text.Contains("requires explicit per-call approval.", StringComparison.Ordinal))
                return new("approval_required", "Obtain explicit user approval, then call this tool with approved=true. For repeated operations, use tenka_approve_session once for the agreed project/tools/duration. Check tenka_session_status for scope or expiry. open_project still needs per-call approval; do not auto-approve or change server switches.");
            if (text.Contains("Agent build/open is disabled on the server.", StringComparison.Ordinal))
                return new("build_disabled", "Ask the user to enable TENKA_AGENT_ALLOW_BUILD=1 on the intended server and restart it only if they authorize local build/open. Reconnect explicitly and refresh selection; per-call approval is still required. Do not change execution permissions.");
            if (text.Contains("Agent execution is disabled on the server.", StringComparison.Ordinal))
                return new("execution_disabled", "Ask the user to enable TENKA_AGENT_ALLOW_EXECUTION=1 on the intended server only if they authorize target execution. Reconnect explicitly after a restart, refresh selection and obtain per-call approval. Do not replay the failed action automatically.");
            if (text.Contains("Only experiments created in this agent session", StringComparison.Ordinal))
                return new("experiment_session_mismatch", "Use an experimentId created in this session, or return to its original live session. Reconnecting does not transfer experiment ownership. Do not re-create or re-run generated code automatically to bypass this boundary.");
            return new("operation_not_authorized", "Check the specific permission or allowed workspace path named in the error. Do not weaken path restrictions or enable all execution switches. Ask the user to authorize only the intended operation.");
        }
        if (tool == "review_queue")
        {
            if (text.Contains("cursor", StringComparison.OrdinalIgnoreCase) || text.Contains("続き", StringComparison.Ordinal))
                return new("review_cursor_invalid", "Call tenka_review_queue with the intended filters, cursor=null and offset=0. Cursors are tied to snapshot and filters; refresh tenka_context first if the snapshot changed. Use kind=all for a full loaded-index pass, and inspect coverage before declaring no pending work.");
            if (text.StartsWith("ArgumentException:", StringComparison.Ordinal))
                return new("review_filter_invalid", "Use kind=all|type|method, state=pending|all|unreviewed|needs_review|checked|optimized, limit=1..200 and offset=0..20000. query is at most 200 characters; do not combine a cursor with nonzero offset.");
        }
        if (tool == "open_project" && text.StartsWith("WorkspaceBuildConflictException:", StringComparison.Ordinal) &&
            text.Contains("Workspace changed. Current snapshot is ", StringComparison.Ordinal))
        {
            if (text.Contains("(none loaded)", StringComparison.Ordinal))
                return new("workspace_unloaded", "No workspace is loaded. Only after explicit user approval, call tenka_open_project with the intended path, approved=true and expectedSnapshotId=null (or omit it). Do not pass the display text '(none loaded)' as a snapshot ID.");
            return new("workspace_conflict", "Check the current snapshotId supplied in the error and confirm the workspace should be replaced. Pass that value as expectedSnapshotId to tenka_open_project, preserving the intended path. Approval is still required; do not overwrite another client's workspace automatically.");
        }
        if (text.StartsWith("WorkspaceBuildConflictException:", StringComparison.Ordinal) && tool is not ("il" or "binary_methods"))
            return new("workspace_conflict", "Read tenka_context, confirm the intended workspace, and reselect IDs from its current snapshot. An active review/build must finish or be cancelled by its owner. Do not automatically reopen or replace the shared workspace.");
        if (tool == "review_mark")
            return new("review_mark_rejected", "Call tenka_review_target for the intended target and snapshot. Reinspect changed source/context before using its current hashes and revision; use only completed evidenceCallIds from this session or its explicitly resumed investigation. Do not copy an old checked/optimized claim onto new code.");
        if (tool is "il" or "binary_methods")
            return new("binary_selection_invalid", "Use tenka_review_context for the intended target, or tenka_binary_methods with current snapshotId/methodId/assemblyId. Choose the exact signature and pass its metadataToken and assemblySha256 to tenka_il. After a build, refresh snapshot IDs; never guess a token or reuse a stale hash.");
        if (tool is "review_context" or "review_target" or "review_history" or "review_diff")
            return new("review_target_unavailable", "Refresh tenka_context and tenka_review_queue, kind=all, to obtain the current snapshotId and intended targetId. If source changed, reload only with approval. Use exact saved revision numbers for history/diff; do not substitute another target.");
        if (tool == "read_result")
            return new("result_unavailable", "Use a completed callId from this session or its explicitly resumed investigation, and an available JSON pointer. Only retained data can be paged; large live-only results may be unavailable after restart. Do not rerun execution just to retrieve output; inspect the activity and saved artifacts first.");
        return new("tool_failed", "Inspect the error and callId for this specific operation. Refresh tenka_context only when workspace selection changed. Do not retry automatically or treat an operational failure as a target-code defect.");
    }
    public static string NextAction(string tool, AgentReply reply)
    {
        // New servers supply structured advice in failure data. Older servers use the
        // same local classifier, so a bridge update does not require a lockstep upgrade.
        if (reply.Data is JsonElement data && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("nextAction", out var action) && action.ValueKind == JsonValueKind.String &&
            action.GetString() is { Length: > 0 and <= 2000 } text && !string.IsNullOrWhiteSpace(text))
            return text;
        return ForFailure(tool, reply.Error, reply.Outcome == "cancelled").NextAction;
    }
    public static string HttpNextAction(int status, string? detail)
    {
        var text = detail ?? "";
        if (status == 409 && text.Contains("session limit reached", StringComparison.OrdinalIgnoreCase))
            return "Finish unused sessions through their owning clients with tenka_finish before starting another. The server keeps at most 16 sessions; tenka_session_status shows limits in an existing usable session; do not terminate another client's active work or restart the server just to clear capacity.";
        if (status == 409 && text.Contains("call budget exhausted", StringComparison.OrdinalIgnoreCase))
            return "The session call budget is exhausted. Preserve durable review checkpoints, then use tenka_finish and tenka_connect explicitly. Explicitly resume the saved investigationId to reuse experiments and evidence. Execution approval and old snapshot IDs do not transfer; never replay execution automatically.";
        if (status == 409 && (text.Contains("expired", StringComparison.OrdinalIgnoreCase) || text.Contains("Unknown agent session", StringComparison.OrdinalIgnoreCase)))
            return "Reconnect explicitly with tenka_connect and refresh tenka_context. Recover durable review checkpoints, not stale session IDs; explicitly resume the saved investigationId to reuse experiments and evidence. Execution approval and old snapshot IDs do not transfer. Do not replay execution automatically.";
        return AgentClientFailure.HttpNextAction(status);
    }
}
