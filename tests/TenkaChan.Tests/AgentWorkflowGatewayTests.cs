using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using TenkaChan.Web;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>Exercises the actual gateway/journal without MSBuild, HTTP or process execution.</summary>
public sealed class AgentWorkflowGatewayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-workflow-" + Guid.NewGuid().ToString("N"));
    private readonly AgentJournal _journal = new();
    private readonly ServiceProvider _services;
    private readonly RoslynReviewService _roslyn;
    private readonly AgentGateway _gateway;
    private readonly string _session;
    public AgentWorkflowGatewayTests()
    {
        Directory.CreateDirectory(_root);
        var guard = new WorkspaceGuard([_root], false, false);
        _roslyn = new RoslynReviewService(guard);
        var dashboard = new DashboardState(_roslyn);
        var store = new ReviewProgressStore(Path.Combine(_root, "review.sqlite"));
        _services = new ServiceCollection().AddSingleton(guard).AddSingleton(dashboard)
            .AddSingleton(new WorkspaceBinaryDiscovery(guard)).AddSingleton(store)
            .AddSingleton<ReviewTrackingService>().BuildServiceProvider();
        var policy = new AgentConnection(_journal.InstanceId, 5077, enabled: true, allowBuild: false,
            allowExecution: false, filePath: Path.Combine(_root, "unused-connection.json"));
        _gateway = new AgentGateway(_services, _journal, policy, new AgentInvestigationStore(_root));
        _session = _journal.Begin("test", "workflow regression").Id;
    }
    private AgentCallRequest Request(string tool, object args) => new(_session, Guid.NewGuid().ToString("N"), tool,
        JsonSerializer.SerializeToElement(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private async Task<AgentReply> Call(string tool, object args) =>
        Assert.IsType<AgentReply>(await _gateway.CallAsync(Request(tool, args), default));

    [Fact]
    public async Task SessionStatusWorksWithoutWorkspaceAndShowsBudgetAndOwnership()
    {
        var reply = await Call("session_status", new { });
        Assert.Equal("returned", reply.Outcome);
        var data = reply.Data!.Value;
        Assert.Equal(AgentJournal.MaxCallsPerSession - 1, data.GetProperty("remainingCalls").GetInt32());
        Assert.Equal(AgentJournal.MaxSessions, data.GetProperty("limits").GetProperty("maxSessions").GetInt32());
        Assert.Equal("current_investigation", data.GetProperty("ownership").GetProperty("experiments").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("approval").ValueKind);
        Assert.False(File.Exists(Path.Combine(_root, "unused-connection.json")));
    }
    [Theory]
    [InlineData("review_queue")]
    [InlineData("review_focus")]
    [InlineData("review_target")]
    [InlineData("review_context")]
    [InlineData("list_types")]
    [InlineData("attach_workspace")]
    [InlineData("approve_session")]
    public async Task UnloadedWorkspaceHasCommonActionableFailure(string tool)
    {
        var reply = await Call(tool, new { snapshotId = "old", targetId = "target", projectId = "p",
            path = Path.Combine(_root, "A.csproj"), tools = new[] { "build" }, approved = true });
        Assert.Equal("failed", reply.Outcome);
        Assert.Equal("workspace_unloaded", reply.Data!.Value.GetProperty("code").GetString());
        Assert.Contains("tenka_open_project", reply.Data.Value.GetProperty("nextAction").GetString()!);
    }
    [Fact]
    public async Task ReloadDoesNotBypassBuildPermission()
    {
        var denied = await Call("reload_workspace",
            new { snapshotId = "not-loaded", approved = false });
        Assert.Equal("failed", denied.Outcome);
        Assert.Equal("build_disabled", denied.Data!.Value.GetProperty("code").GetString());
        Assert.Contains("TENKA_AGENT_ALLOW_BUILD", denied.Error);
    }

    [Fact]
    public async Task ContextStillReturnsUnloadedStateInsteadOfReopening()
    {
        var reply = await Call("context", new { });
        Assert.Equal("returned", reply.Outcome);
        Assert.Equal(JsonValueKind.Null, reply.Data!.Value.GetProperty("workspace").ValueKind);
        Assert.False(reply.Data.Value.GetProperty("overview").GetProperty("loaded").GetBoolean());
        Assert.True(reply.Data.Value.TryGetProperty("session", out _));
        Assert.Contains("tenka_review_focus", reply.Data.Value.GetProperty("workflow").GetString());
    }
    [Fact]
    public async Task RevocationIsIdempotentAndDuplicateRequestIsNotExecutedTwice()
    {
        var request = Request("revoke_approval", new { });
        var first = Assert.IsType<AgentReply>(await _gateway.CallAsync(request, default));
        var second = Assert.IsType<AgentReply>(await _gateway.CallAsync(request, default));
        Assert.Same(first, second);
        Assert.Equal("returned", first.Outcome);
        Assert.Equal(1, _journal.Monitor(true, false, false).Sessions.Single().Calls);
    }
    [Fact]
    public async Task UnownedExperimentIsRejectedBeforeAnyExecutorIsResolved()
    {
        var reply = await Call("run_experiment", new { experimentId = Guid.NewGuid().ToString("N"), approved = true });
        Assert.Equal("failed", reply.Outcome);
        Assert.Equal("experiment_investigation_mismatch", reply.Data!.Value.GetProperty("code").GetString());
    }
    [Fact]
    public async Task EndedSessionsCannotReuseTheWorkflow()
    {
        _journal.Finish(_session); _gateway.EndSession(_session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _gateway.CallAsync(Request("session_status", new { }), default));
    }
    private async Task<AgentReply> CallFor(string session, string tool, object args) =>
        Assert.IsType<AgentReply>(await _gateway.CallAsync(new AgentCallRequest(session, Guid.NewGuid().ToString("N"), tool,
            JsonSerializer.SerializeToElement(args, new JsonSerializerOptions(JsonSerializerDefaults.Web))), default));

    [Fact]
    public async Task ActualGatewayCanResumeAndReadPriorSessionResultWithoutReexecution()
    {
        var first = await Call("context", new { });
        var id = first.Data!.Value.GetProperty("session").GetProperty("investigation").GetProperty("investigationId").GetString()!;
        _journal.Finish(_session); _gateway.EndSession(_session);
        var next = _journal.Begin("same-client-name", "continued").Id;
        var denied = await CallFor(next, "read_result", new { callId = first.CallId });
        Assert.Equal("failed", denied.Outcome);
        var resumed = await CallFor(next, "investigation", new { action = "resume", investigationId = id, approved = true });
        Assert.Equal("returned", resumed.Outcome);
        var read = await CallFor(next, "read_result", new { callId = first.CallId });
        Assert.Equal("returned", read.Outcome);
        Assert.False(read.Data!.Value.GetProperty("data").GetProperty("overview").GetProperty("loaded").GetBoolean());
        var note = await CallFor(next, "note", new { kind = "progress", message = "resumed evidence", evidenceCallIds = new[] { first.CallId } });
        Assert.Equal("returned", note.Outcome);
        Assert.Equal(first.CallId, note.Data!.Value.GetProperty("evidence")[0].GetProperty("callId").GetString());
    }
    [Fact]
    public async Task ResumeDoesNotCarryAnExistingExecutionGrantIntoAnotherInvestigation()
    {
        var first = await Call("session_status", new { });
        var id = first.Data!.Value.GetProperty("investigation").GetProperty("investigationId").GetString()!;
        var next = _journal.Begin("test", "separate").Id;
        var approvals = (AgentSessionApprovals)typeof(AgentGateway)
            .GetField("_approvals", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_gateway)!;
        approvals.Grant(next, "fixture", Path.Combine(_root, "Target.csproj"), ["run_experiment"], 10, true, true, true);
        Assert.NotNull(approvals.Get(next));
        var resumed = await CallFor(next, "investigation", new { action = "resume", investigationId = id, approved = true });
        Assert.Equal("returned", resumed.Outcome);
        Assert.Null(approvals.Get(next));
    }
    [Fact]
    public async Task CallerCannotInjectMeasurementNumbersIntoAGatewayMark()
    {
        var request = Request("review_mark", new { measurement = new { mean = 1, independentlyVerified = true } });
        await Assert.ThrowsAsync<ArgumentException>(() => _gateway.CallAsync(request, default));
        Assert.Equal(0, _journal.Monitor(true, false, false).Sessions.Single().Calls);
    }
    [Fact]
    public async Task ASecondInvestigationReaderDoesNotQueueDuplicateActions()
    {
        var first = await Call("session_status", new { });
        var id = first.Data!.Value.GetProperty("investigation").GetProperty("investigationId").GetString()!;
        using var held = new AgentInvestigationStore(_root).ResumeAndEnter(Guid.NewGuid().ToString("N"), id, true);
        var busy = await Call("context", new { });
        Assert.Equal("failed", busy.Outcome);
        Assert.Equal("investigation_busy", busy.Data!.Value.GetProperty("code").GetString());
    }
    public void Dispose()
    {
        _services.Dispose(); _roslyn.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
