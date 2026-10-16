using System.Text.Json;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentSessionLifecycleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static AgentCallRequest Request(string session) => new(session, Guid.NewGuid().ToString("N"), "scan", JsonSerializer.SerializeToElement(new { }));

    [Fact] public void OptionsHaveCompatibleDefaultsAndConfigurableLimits()
    {
        Assert.Equal(new AgentSessionOptions(16, 15), AgentSessionOptions.Parse(null, null));
        Assert.Equal(new AgentSessionOptions(32, 60), AgentSessionOptions.Parse("32", "60"));
        Assert.Equal(new AgentSessionOptions(1, 1), AgentSessionOptions.Parse("1", "1"));
        Assert.Equal(new AgentSessionOptions(128, 1440), AgentSessionOptions.Parse("128", "1440"));
    }
    [Theory]
    [InlineData("0", "15")]
    [InlineData("129", "15")]
    [InlineData("NaN", "15")]
    [InlineData("16", "0")]
    [InlineData("16", "1441")]
    [InlineData("16", "1.5")]
    public void InvalidConfigurationIsNotSilentlyAccepted(string maximum, string ttl) =>
        Assert.Throws<ArgumentException>(() => AgentSessionOptions.Parse(maximum, ttl));

    [Fact] public void FullJournalCanBeListedAndRecoveredWithoutCreatingAnotherSession()
    {
        var journal = new AgentJournal(options: new(1, 15));
        var original = journal.Begin("agent", "work");
        var ex = Assert.Throws<InvalidOperationException>(() => journal.Begin("next", "work"));
        Assert.Contains("tenka_sessions_list", ex.Message);
        Assert.Contains("tenka_session_end", ex.Message);
        var list = journal.ListSessions();
        Assert.Single(list.Items); Assert.Equal(0, list.AvailableSlots);
        Assert.Equal(0, list.Items[0].Calls);
        Assert.Equal("finished", journal.EndIdleSession(original.Id, true).Status);
        Assert.Equal(1, journal.ListSessions().AvailableSlots);
        var next = journal.Begin("next", "work");
        Assert.NotEqual(original.Id, next.Id);
        Assert.Single(journal.ListSessions().Items);
    }
    [Fact] public void ListingDoesNotKeepIdleSessionsAlive()
    {
        var clock = new Clock(); var journal = new AgentJournal(clock, new(1, 1));
        var original = journal.Begin("agent", "work");
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal(original.LastSeen, journal.ListSessions().Items.Single().LastSeen);
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal("expired", journal.ListSessions().Items.Single().Status);
        Assert.Equal(original.LastSeen, journal.ListSessions().Items.Single().LastSeen);
    }
    [Fact] public void RunningActionIsNeitherExpiredNorEndedNorEvicted()
    {
        var clock = new Clock(); var journal = new AgentJournal(clock, new(1, 1));
        var session = journal.Begin("agent", "work"); var call = journal.Start(Request(session.Id));
        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal("running", journal.ListSessions().Items.Single().Status);
        Assert.Contains("session_busy", Assert.Throws<InvalidOperationException>(() => journal.EndIdleSession(session.Id, true)).Message);
        Assert.Throws<InvalidOperationException>(() => journal.Begin("next", "work"));
        journal.Complete(call.CallId, new { status = "completed" });
        Assert.Equal("idle", journal.ListSessions().Items.Single().Status);
        Assert.Equal("finished", journal.EndIdleSession(session.Id, true).Status);
    }
    [Fact] public void EndingRequiresApprovalAndAnExactId()
    {
        var journal = new AgentJournal(); var session = journal.Begin("agent", "work");
        Assert.Throws<InvalidOperationException>(() => journal.EndIdleSession(session.Id, false));
        Assert.Throws<ArgumentException>(() => journal.EndIdleSession("", true));
        Assert.Throws<KeyNotFoundException>(() => journal.EndIdleSession(Guid.NewGuid().ToString("N"), true));
        Assert.Equal("idle", journal.ListSessions().Items.Single().Status);
    }
    [Fact] public void EndingFinishedOrExpiredSessionsIsIdempotent()
    {
        var clock = new Clock(); var journal = new AgentJournal(clock, new(2, 1));
        var ended = journal.Begin("a", ""); var expired = journal.Begin("b", "");
        journal.EndIdleSession(ended.Id, true);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal("finished", journal.EndIdleSession(ended.Id, true).Status);
        Assert.Equal("expired", journal.EndIdleSession(expired.Id, true).Status);
        Assert.Equal(2, journal.ListSessions().AvailableSlots);
    }
    [Fact] public void PinnedSnapshotIsKeptInCachedReplyWhenResultHasNoSnapshot()
    {
        var journal = new AgentJournal(); var session = journal.Begin("agent", ""); var request = Request(session.Id);
        var call = journal.Start(request);
        var reply = journal.Complete(call.CallId, new { value = 42 }, resolvedSnapshot: "pinned");
        Assert.Equal("pinned", reply.SnapshotId);
        Assert.Equal("pinned", journal.Start(request).Cached!.SnapshotId);
    }
    [Fact] public void BuildResultsCanReportTheNewSnapshotInsteadOfTheOldPinnedOne()
    {
        var journal = new AgentJournal(); var session = journal.Begin("agent", ""); var call = journal.Start(Request(session.Id));
        Assert.Equal("new", journal.Complete(call.CallId, new { snapshotId = "new" }, resolvedSnapshot: "old").SnapshotId);
    }
    [Fact] public void SelfReportedClientNameDoesNotGrantCrossSessionEvidenceAccess()
    {
        var journal = new AgentJournal(); var first = journal.Begin("same-name", ""); var second = journal.Begin("same-name", "");
        var call = journal.Start(Request(first.Id)); journal.Complete(call.CallId, new { status = "completed" });
        Assert.Throws<ArgumentException>(() => journal.ReviewAttribution(second.Id, [call.CallId]));
    }
}
