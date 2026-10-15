using System.Text.Json;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentJournalTests
{
    private static AgentCallRequest Request(string session, object? args = null, string tool = "inspect_method", string? id = null) =>
        new(session, id ?? Guid.NewGuid().ToString("N"), tool, JsonSerializer.SerializeToElement(args ?? new { snapshotId = "snapshot" }));
    [Fact] public void CompletedRequestIsNotRunTwice()
    {
        var j = new AgentJournal(); var s = j.Begin("client", "goal"); var r = Request(s.Id);
        var first = j.Start(r); var completed = j.Complete(first.CallId, new { status = "jit_completed" });
        var duplicate = j.Start(r);
        Assert.Equal(first.CallId, duplicate.CallId); Assert.Equal(completed, duplicate.Cached);
        Assert.Equal(1, j.Monitor(true, false, false).Sessions.Single().Calls);
    }
    [Fact] public void RequestIdCannotBeReusedForDifferentArguments()
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); var r = Request(s.Id);
        var call = j.Start(r); j.Complete(call.CallId, new { });
        Assert.Throws<InvalidOperationException>(() => j.Start(r with { Tool = "build" }));
    }
    [Fact] public void DuplicateOrParallelRequestsAreNotQueued()
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); var r = Request(s.Id); j.Start(r);
        Assert.Throws<InvalidOperationException>(() => j.Start(r));
        Assert.Throws<InvalidOperationException>(() => j.Start(Request(s.Id)));
    }
    [Fact] public void OperationalReturnDoesNotHideApplicationFailure()
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); var c = j.Start(Request(s.Id));
        var result = j.Complete(c.CallId, new { build = new { status = "build_failed" }, snapshotId = "s" });
        Assert.Equal("returned", result.Outcome); Assert.Equal("build_failed", result.ObservedStatus);
        Assert.Equal("s", result.SnapshotId);
    }
    [Fact] public void CancellationAndErrorsAreNotConfirmedDefects()
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); var c = j.Start(Request(s.Id));
        Assert.Equal("cancelled", j.Complete(c.CallId, null, "cancelled", true).Outcome);
        Assert.Equal("idle", j.Monitor(true, false, false).Sessions.Single().Status);
    }
    [Theory]
    [InlineData("plan")][InlineData("progress")][InlineData("hypothesis")][InlineData("conclusion")]
    public void AgentStatementsRemainUnverified(string kind)
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); j.Report(s.Id, new(kind, "Looks fixed"));
        var e = j.Monitor(true, false, false).Events.Last();
        Assert.Equal("agent_" + kind, e.Kind); Assert.Equal("agent_reported_unverified", e.ObservedStatus);
    }
    [Fact] public void NotesCannotForgeEvidenceFromAnotherSession()
    {
        var j = new AgentJournal(); var a = j.Begin("a", ""); var b = j.Begin("b", "");
        var call = j.Start(Request(a.Id)); j.Complete(call.CallId, new { });
        Assert.Throws<ArgumentException>(() => j.Report(b.Id, new("conclusion", "verified", [call.CallId])));
    }
    [Fact] public void ResultPagingIsSessionScoped()
    {
        var j = new AgentJournal(); var a = j.Begin("a", ""); var b = j.Begin("b", "");
        var call = j.Start(Request(a.Id)); j.Complete(call.CallId, new { findings = new[] { 1, 2, 3, 4 } });
        var page = JsonSerializer.SerializeToElement(j.ReadResult(a.Id, call.CallId, "/findings", 1, 2));
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(new[] { 2, 3 }, page.GetProperty("items").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Throws<KeyNotFoundException>(() => j.ReadResult(b.Id, call.CallId, "", 0, 2));
    }
    [Fact] public void MonitorDoesNotKeepDisconnectedAgentsAlive()
    {
        var time = new Clock(); var j = new AgentJournal(time); var s = j.Begin("c", "");
        time.Now += TimeSpan.FromMinutes(16);
        var monitor = j.Monitor(true, false, false);
        Assert.Equal("expired", monitor.Sessions.Single().Status);
        Assert.Equal(s.LastSeen, monitor.Sessions.Single().LastSeen);
        Assert.Throws<InvalidOperationException>(() => j.Start(Request(s.Id)));
    }
    [Fact] public void FinishIsExplicitAndDoesNotCancelAStillRunningOperation()
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); var c = j.Start(Request(s.Id));
        Assert.Throws<InvalidOperationException>(() => j.Finish(s.Id));
        j.Complete(c.CallId, new { }); j.Finish(s.Id);
        Assert.Throws<InvalidOperationException>(() => j.Start(Request(s.Id)));
    }
    [Fact] public void CallBudgetCannotBeBypassedWithNewRequestIds()
    {
        var j = new AgentJournal(); var s = j.Begin("c", "");
        for (var i = 0; i < AgentJournal.MaxCallsPerSession; i++) { var c = j.Start(Request(s.Id)); j.Complete(c.CallId, new { }); }
        Assert.Throws<InvalidOperationException>(() => j.Start(Request(s.Id)));
        Assert.True(j.Monitor(true, false, false).Events.Count <= AgentJournal.MaxEvents);
    }
    [Fact] public void LargeResultsRequireExplicitFieldRetrieval()
    {
        var j = new AgentJournal(); var s = j.Begin("c", ""); var c = j.Start(Request(s.Id));
        var r = j.Complete(c.CallId, new { summary = "available", source = new string('x', 70000) });
        Assert.Null(r.Data); Assert.True(r.ResultAvailable); Assert.Contains("source", r.AvailableFields);
        var result = JsonSerializer.SerializeToElement(j.ReadResult(s.Id, c.CallId, "/summary", 0, 10));
        Assert.Equal("available", result.GetProperty("data").GetString());
    }
    [Fact] public void SessionCountIsBounded()
    {
        var j = new AgentJournal();
        var sessions = Enumerable.Range(0, AgentJournal.MaxSessions).Select(_ => j.Begin("c", "")).ToArray();
        Assert.Throws<InvalidOperationException>(() => j.Begin("overflow", ""));
        j.Finish(sessions[0].Id); j.Begin("replacement", "");
        Assert.Equal(AgentJournal.MaxSessions, j.Monitor(true, false, false).Sessions.Count);
    }
    [Theory]
    [InlineData("https://127.0.0.1:5077")][InlineData("http://example.com:5077")]
    [InlineData("http://127.0.0.1:5077/evil")][InlineData("http://user:pass@127.0.0.1:5077")]
    [InlineData("http://127.0.0.1:5077/?x=1")]
    public void AdapterNeverSendsCredentialsToExternalOrArbitraryEndpoints(string endpoint) =>
        Assert.Throws<InvalidDataException>(() => DashboardAgentClient.ValidateConnection(new(1, endpoint, new string('A', 64), Guid.NewGuid().ToString("N"))));
    [Fact] public void AdapterNormalizesLocalhostWithoutUsingDns()
    {
        var uri = DashboardAgentClient.ValidateConnection(new(1, "http://localhost:5077", new string('A', 64), Guid.NewGuid().ToString("N")));
        Assert.Equal("127.0.0.1", uri.Host);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
