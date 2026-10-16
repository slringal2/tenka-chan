using System.Text.Json;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DurableInvestigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-durable-" + Guid.NewGuid().ToString("N"));
    private static string Id() => Guid.NewGuid().ToString("N");
    private static AgentReply Reply(string call, string outcome = "returned") => new(call, outcome, "tests_passed", "snapshot", null,
        JsonSerializer.SerializeToElement(new { items = new[] { 1, 2, 3 } }), true, ["items"]);

    [Fact] public void ExplicitResumeRestoresEvidenceAndExperimentOwnershipAcrossStoreRestart()
    {
        var session = Id(); var call = Id(); var experiment = Id(); var instance = Id();
        var first = new AgentInvestigationStore(_root);
        var investigation = first.CurrentId(session);
        using (first.Enter(session)) { first.AddExperiment(session, experiment); first.Record(session, instance, "run_experiment", Reply(call)); }
        var second = new AgentInvestigationStore(_root); var next = Id();
        Assert.Throws<UnauthorizedAccessException>(() => second.RequireExperiment(next, experiment));
        Assert.Throws<UnauthorizedAccessException>(() => second.Resume(next, investigation, false));
        second.Resume(next, investigation, true);
        second.RequireExperiment(next, experiment);
        var evidence = Assert.Single(second.Evidence(next, [call]));
        Assert.Equal(instance, evidence.InstanceId); Assert.Equal(call, evidence.CallId);
        var result = JsonSerializer.SerializeToElement(second.ReadResult(next, call, "/items", 1, 1));
        Assert.Equal(2, result.GetProperty("items")[0].GetInt32());
    }
    [Fact] public void FreshSessionsDoNotImplicitlyInheritAnotherInvestigationsReceipts()
    {
        var store = new AgentInvestigationStore(_root); var a = Id(); var b = Id(); var call = Id();
        store.Record(a, Id(), "context", Reply(call));
        Assert.Throws<ArgumentException>(() => store.Evidence(b, [call]));
        Assert.NotEqual(store.CurrentId(a), store.CurrentId(b));
    }
    [Fact] public void ResumeRefusesAnInFlightInvestigationWithoutCancellingIt()
    {
        var store = new AgentInvestigationStore(_root); var session = Id(); var investigation = store.CurrentId(session);
        using var running = store.Enter(session);
        Assert.Throws<InvalidOperationException>(() => new AgentInvestigationStore(_root).Resume(Id(), investigation, true));
        Assert.Equal(investigation, store.CurrentId(session));
    }
    [Fact] public void RecordingTheSameCompletedCallIsIdempotent()
    {
        var store = new AgentInvestigationStore(_root); var s = Id(); var call = Id(); var instance = Id();
        store.Record(s, instance, "context", Reply(call)); store.Record(s, instance, "context", Reply(call));
        Assert.Single(store.Current(s).Evidence);
    }
    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void OutcomeAndOriginalInstanceAreNeverPromoted(string outcome)
    {
        var store = new AgentInvestigationStore(_root); var s = Id(); var call = Id(); var instance = Id();
        store.Record(s, instance, "run_experiment", Reply(call, outcome));
        var evidence = Assert.Single(store.Evidence(s, [call]));
        Assert.Equal(outcome, evidence.Outcome); Assert.Equal(instance, evidence.InstanceId);
    }
    [Fact] public void RunningCallsCannotBePersistedAsEvidence()
    {
        var store = new AgentInvestigationStore(_root);
        Assert.Throws<InvalidDataException>(() => store.Record(Id(), Id(), "test", Reply(Id(), "running")));
    }
    [Fact] public void LargeLiveOnlyResultsAreNotInventedAfterRestart()
    {
        var store = new AgentInvestigationStore(_root); var s = Id(); var call = Id();
        store.Record(s, Id(), "scan", Reply(call) with { Data = null });
        Assert.Throws<KeyNotFoundException>(() => store.ReadResult(s, call, "", 0, 30));
        Assert.Single(store.Evidence(s, [call]));
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("")]
    [InlineData("not-an-id")]
    public void StorageIdsCannotBePaths(string id)
    {
        var store = new AgentInvestigationStore(_root);
        Assert.Throws<ArgumentException>(() => store.Resume(Id(), id, true));
    }
    [Fact] public void ProposalOwnershipSurvivesResumeButNeedsExplicitSelection()
    {
        var store = new AgentInvestigationStore(_root); var first = Id(); var next = Id(); var proposal = Id();
        store.AddProposal(first, proposal); Assert.Throws<UnauthorizedAccessException>(() => store.RequireProposal(next, proposal));
        store.Resume(next, store.CurrentId(first), true); store.RequireProposal(next, proposal);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
