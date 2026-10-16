using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public sealed partial class AgentGateway
{
    private AgentReply CompleteDurable(AgentCallRequest request, AgentReply reply)
    {
        try
        {
            var result = reply with { InvestigationId = _investigations.CurrentId(request.SessionId) };
            _investigations.Record(request.SessionId, journal.InstanceId, request.Tool, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or InvalidDataException)
        {
            // A target may already have completed. A persistence failure must not turn
            // it into a retryable execution failure or fabricate durable evidence.
            return reply with { PersistenceWarning = "The call completed but its durable receipt was not confirmed. Do not rerun it merely to save evidence; inspect the live result and storage permissions/budget." };
        }
    }
    private object InvestigationSummary(string session)
    {
        var saved = _investigations.Current(session);
        return new { investigationId = saved.Id, evidenceCalls = saved.Evidence.Count, experimentIds = saved.ExperimentIds,
            proposalIds = saved.ProposalIds, recentEvidence = saved.Evidence.TakeLast(10).ToArray(),
            resume = new { tool = "tenka_investigation", action = "resume", investigationId = saved.Id, approvalRequired = true },
            note = "Independent of this transport session. Resume is an explicitly approved local-machine action, not authentication by agent name. Old pre-upgrade sessions cannot be reconstructed." };
    }
    private async Task<object> ExecuteInvestigation(string session, string tool, JsonElement a, CancellationToken ct)
    {
        if (tool == "investigation") return (Optional(a, "action") ?? "status") switch
        {
            "status" or "resume" => InvestigationSummary(session), // Resume was guarded before taking the operation lease.
            "list" => _investigations.List(),
            _ => throw new ArgumentException("action must be status, list or resume.")
        };
        var guard = Get<WorkspaceGuard>(); var proposals = new ChangeProposalStore(guard); var dashboard = Get<DashboardState>();
        if (tool == "propose_change")
        {
            return await dashboard.WithProposalTargetAsync(Optional(a, "snapshotId"), Str(a, "targetId"), guard, selection =>
            {
                var proposal = proposals.Create(_investigations.CurrentId(session), selection.SnapshotId, selection.Target, selection.Root,
                    Str(a, "expectedFileSha256"), Str(a, "replacementCode"), selection.Companions, ct);
                _investigations.AddProposal(session, proposal.Id);
                return Task.FromResult((object)new { proposalId = proposal.Id, investigationId = proposal.InvestigationId, proposal.TargetId,
                    proposal.Status, proposal.File, proposal.BeforeHash, proposal.AfterHash, proposal.Companions,
                    nextAction = new { tool = "tenka_diff_proposal", proposalId = proposal.Id },
                    sourceApplyPerformed = false, note = "One existing C# file in independent baseline/candidate copies. No build, test, source apply or review mark has run." });
            }, ct);
        }
        var proposalId = Optional(a, "proposalId");
        if (tool == "review_workflow" && proposalId is null)
        {
            if ((Optional(a, "mode") ?? "inspect") != "inspect") throw new ArgumentException("proposalId is required for validate/result mode.");
            return await dashboard.WithProposalTargetAsync(null, Str(a, "targetId"), guard, selection => Task.FromResult((object)new
            {
                snapshotId = selection.SnapshotId, selection.Target, workspaceRoot = selection.Root,
                editInput = proposals.EditInput(selection.Target), companions = selection.Companions,
                executionPlan = new[] { "baseline_test", "candidate_test", "baseline_benchmark", "candidate_benchmark" },
                nextAction = "Use tenka_propose_change with targetId, snapshotId, editInput.expectedFileSha256 and complete replacementCode; inspect its diff before validation.",
                note = "Preparation only. No expected test result is invented. Existing test/benchmark projects are required for this first workflow." }), ct);
        }
        if (proposalId is null) throw new ArgumentException("Missing field: proposalId.");
        _investigations.RequireProposal(session, proposalId);
        var saved = proposals.Read(proposalId);
        if (saved.InvestigationId != _investigations.CurrentId(session)) throw new UnauthorizedAccessException("Proposal investigation does not match.");
        if (tool == "diff_proposal") return proposals.Diff(proposalId, Number(a, "offset"), Number(a, "limit", 150));
        if (tool is "apply_change" or "revert_change")
        {
            if (!policy.AllowBuild) throw new UnauthorizedAccessException("host_build_disabled: Source edits require the local agent build policy as well as separate per-invocation approval.");
            return proposals.Replace(proposalId, tool == "revert_change", Bool(a, "approved"), ct);
        }
        if (tool != "review_workflow") throw new ArgumentException("Unknown investigation tool.");
        if (Optional(a, "targetId") is string targetId && targetId != saved.TargetId)
            throw new ArgumentException("targetId does not match the proposal's canonical target. Omit targetId when continuing by proposalId.");
        var mode = Optional(a, "mode") ?? "inspect";
        if (mode == "inspect") return new { proposalId, saved.TargetId, saved.Status, saved.File, saved.Companions,
            executionPlan = new[] { "baseline_test", "candidate_test", "baseline_benchmark", "candidate_benchmark" },
            requiresApproval = true, executionIsSandboxed = false, nextAction = "Inspect tenka_diff_proposal, then approve tenka_review_workflow mode=validate." };
        if (mode == "result") return proposals.ReadValidation(proposalId);
        if (mode != "validate") throw new ArgumentException("mode must be inspect, validate or result.");
        BuildPermission(a); ExecutionPermission(a);
        var result = await new ProposalValidationRunner(proposals, guard).RunAsync(proposalId, Optional(a, "testCompanionId"),
            Optional(a, "benchmarkCompanionId"), Optional(a, "benchmarkFilter"), Bool(a, "approved"), ct);
        return new { proposalId, result.Status, result.Stages, result.Cases, result.Note,
            nextAction = result.Status == "awaiting_next_stage" ? "Continue tenka_review_workflow mode=validate with this proposalId after approval; completed stages are reused."
                : "Inspect results. Applying source and recording checked/optimized remain separate explicit actions.",
            sourceApplyPerformed = false, allStagesComplete = result.Status == "validation_completed" };
    }
}
