using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentRecoveryAdviceTests
{
    [Theory]
    [InlineData("build/open", "TENKA_AGENT_ALLOW_BUILD", false, "build_disabled")]
    [InlineData("execution", "TENKA_AGENT_ALLOW_EXECUTION", false, "execution_disabled")]
    [InlineData("build/open", "TENKA_AGENT_ALLOW_BUILD", true, "approval_required")]
    [InlineData("execution", "TENKA_AGENT_ALLOW_EXECUTION", true, "approval_required")]
    public void ExistingPermissionGateMessagesHaveDistinctRecovery(string scope, string variable, bool allowed, string expected)
    {
        var exception = Assert.Throws<UnauthorizedAccessException>(() => AgentPermissionGate.Require(scope, variable, allowed, false));
        var hint = AgentRecoveryAdvice.ForFailure("run_experiment", exception.GetType().Name + ": " + exception.Message);
        Assert.Equal(expected, hint.Code);
        if (allowed) Assert.Contains("approved=true", hint.NextAction);
        else Assert.Contains(variable + "=1", hint.NextAction);
    }
    [Theory]
    [InlineData("review_queue", "ReviewConflictException: cursor is stale", "review_cursor_invalid", "cursor=null")]
    [InlineData("review_queue", "ArgumentException: Invalid review page.", "review_filter_invalid", "limit=1..200")]
    [InlineData("open_project", "WorkspaceBuildConflictException: Workspace changed. Current snapshot is current", "workspace_conflict", "expectedSnapshotId")]
    [InlineData("review_mark", "ReviewConflictException: revision changed", "review_mark_rejected", "tenka_review_target")]
    [InlineData("il", "WorkspaceBuildConflictException: DLL changed", "binary_selection_invalid", "assemblySha256")]
    [InlineData("run_experiment", "UnauthorizedAccessException: Only experiments created in this agent session can be used.", "experiment_session_mismatch", "original live session")]
    [InlineData("review_context", "KeyNotFoundException: target missing", "review_target_unavailable", "targetId")]
    public void AdviceNamesTheNextOperation(string tool, string error, string expected, string field)
    {
        var hint = AgentRecoveryAdvice.ForFailure(tool, error);
        Assert.Equal(expected, hint.Code);
        Assert.Contains(field, hint.NextAction);
    }
    [Fact]
    public void UnloadedWorkspaceDoesNotUseDisplayTextAsAnId()
    {
        var error = "WorkspaceBuildConflictException: " + AgentWorkspaceGate.ConflictMessage("old", null);
        var hint = AgentRecoveryAdvice.ForFailure("open_project", error);
        Assert.Equal("workspace_unloaded", hint.Code);
        Assert.Contains("expectedSnapshotId=null", hint.NextAction);
    }
    [Fact]
    public void BusyWorkspaceDoesNotClaimToHaveANewSnapshotId()
    {
        var hint = AgentRecoveryAdvice.ForFailure("open_project", "WorkspaceBuildConflictException: Another operation is active.");
        Assert.Equal("workspace_conflict", hint.Code);
        Assert.Contains("owner", hint.NextAction);
        Assert.DoesNotContain("supplied in the error", hint.NextAction);
    }
    [Fact]
    public void UnknownPermissionDoesNotSuggestEnablingEverythingOrEchoSecrets()
    {
        var hint = AgentRecoveryAdvice.ForFailure("il", "UnauthorizedAccessException: secret-sentinel outside workspace");
        Assert.Equal("operation_not_authorized", hint.Code);
        Assert.DoesNotContain("secret-sentinel", hint.NextAction);
        Assert.DoesNotContain("=1", hint.NextAction);
    }
    [Fact]
    public void ForeignExperimentRecoveryExplainsSafeCrossAdapterResume()
    {
        var hint = AgentRecoveryAdvice.ForFailure("measure_target",
            "UnauthorizedAccessException: experiment_investigation_mismatch: not owned here");
        Assert.Equal("experiment_investigation_mismatch", hint.Code);
        Assert.Contains("same connected MCP investigation", hint.NextAction);
        Assert.Contains("investigationId", hint.NextAction);
        Assert.Contains("action=resume", hint.NextAction);
        Assert.Contains("approved=true", hint.NextAction);
    }
    [Fact]
    public void AlreadyExecutedBenchmarkExplainsPrepareDirectlyToMeasure()
    {
        var advice = AgentRecoveryAdvice.ForFailure("measure_target",
            "InvalidOperationException: Experiment has already been executed. Prepare a new one.");
        Assert.Equal("experiment_already_executed", advice.Code);
        Assert.Contains("prepare_experiment(kind=benchmark) -> tenka_measure_target", advice.NextAction);
        Assert.Contains("Do NOT insert tenka_run_experiment", advice.NextAction);
        Assert.Contains("never replay", advice.NextAction);
    }

    [Fact]
    public void OversizedCrossSessionResultCannotBeRecoveredByResumingReceipt()
    {
        var advice = AgentRecoveryAdvice.ForFailure("read_result",
            "KeyNotFoundException: evidence_unavailable: No completed receipt in this investigation.");
        Assert.Equal("result_unavailable", advice.Code);
        Assert.Contains("SAME live MCP transport session", advice.NextAction);
        Assert.Contains("NOT persisted across sessions", advice.NextAction);
    }

    [Fact]
    public void EphemeralTypeSelectionGivesExactReselectionRoute()
    {
        var advice = AgentRecoveryAdvice.ForFailure("inspect_type",
            "KeyNotFoundException: Type selection is no longer available.");
        Assert.Equal("selection_expired", advice.Code);
        Assert.Contains("tenka_list_types", advice.NextAction);
        Assert.Contains("tenka_review_context", advice.NextAction);
        Assert.Contains("persistent", advice.NextAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancellationDoesNotImplyExecutionWasRolledBack()
    {
        var hint = AgentRecoveryAdvice.ForFailure("build", null, cancelled: true);
        Assert.Equal("action_cancelled", hint.Code);
        Assert.Contains("not proof of rollback", hint.NextAction);
        Assert.Contains("approval", hint.NextAction);
    }
    [Fact]
    public void AdapterUsesStructuredServerAdvice()
    {
        var hint = new AgentRecoveryHint("custom", "Read the current target without replaying execution.");
        var data = JsonSerializer.SerializeToElement(hint, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reply = new AgentReply("call", "failed", null, null, "error", data, true, ["code", "nextAction"]);
        Assert.Equal(hint.NextAction, AgentRecoveryAdvice.NextAction("il", reply));
    }
    [Fact]
    public void OldServerAndInvalidHintsUseConservativeFallback()
    {
        var reply = new AgentReply("call", "failed", null, null, "ArgumentException: wrong token", null, false, []);
        Assert.Contains("metadataToken", AgentRecoveryAdvice.NextAction("il", reply));
        var oversized = JsonSerializer.SerializeToElement(new { nextAction = new string('x', 2001) });
        Assert.Contains("metadataToken", AgentRecoveryAdvice.NextAction("il", reply with { Data = oversized }));
        var invalid = JsonSerializer.SerializeToElement(new { nextAction = 42 });
        Assert.Contains("metadataToken", AgentRecoveryAdvice.NextAction("il", reply with { Data = invalid }));
    }
    [Theory]
    [InlineData("Agent session limit reached. Finish an existing session.", "owning clients")]
    [InlineData("Agent call budget exhausted. Finish this session before starting another.", "do not transfer")]
    [InlineData("Unknown agent session or expired session.", "tenka_connect")]
    public void Http409HasSpecificSessionRecovery(string detail, string expected) =>
        Assert.Contains(expected, AgentRecoveryAdvice.HttpNextAction(409, detail));
    [Fact]
    public void UnrecognizedHttpFailureKeepsExistingTransportGuidance() =>
        Assert.Equal(AgentClientFailure.HttpNextAction(403), AgentRecoveryAdvice.HttpNextAction(403, "denied"));
}
