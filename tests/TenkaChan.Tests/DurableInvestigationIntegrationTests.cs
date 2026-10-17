using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DurableInvestigationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-durable-" + Guid.NewGuid().ToString("N"));
    private static string Id() => Guid.NewGuid().ToString("N");
    private AgentInvestigationStore Store() => new(_root);
    private string Project => Path.Combine(_root, "Target.csproj");
    private static AgentReply Reply(string call, string outcome = "returned", object? data = null) =>
        new(call, outcome, null, null, outcome == "returned" ? null : "test failure",
            JsonSerializer.SerializeToElement(data ?? new { values = new[] { 1, 2, 3 } }), true, ["values"]);

    [Fact]
    public void ResumeAcrossStoreRestartRestoresExperimentsEvidenceAndSmallResults()
    {
        var a = Store(); var session = Id(); var experiment = Id(); var call = Id();
        var investigation = a.CurrentId(session);
        Assert.NotEqual(session, investigation);
        using (a.Enter(session))
        {
            a.AddExperiment(session, experiment, Project);
            a.Record(session, Id(), "inspect_method", Reply(call));
        }
        a.Unbind(session);
        var b = Store(); var next = Id();
        Assert.Throws<UnauthorizedAccessException>(() => b.RequireExperiment(next, experiment));
        using var resumed = b.ResumeAndEnter(next, investigation, true);
        Assert.Equal(Project, b.RequireExperiment(next, experiment));
        Assert.Equal(call, Assert.Single(b.Evidence(next, [call])).CallId);
        var page = JsonSerializer.SerializeToElement(b.ReadResult(next, call, "/values", 1, 1));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("items")[0].GetInt32());
    }
    [Fact]
    public void ResumeNeedsExplicitConsentAndFailedResumeDoesNotRebind()
    {
        var store = Store(); var original = store.CurrentId(Id()); var session = Id(); var prior = store.CurrentId(session);
        Assert.Throws<UnauthorizedAccessException>(() => store.ResumeAndEnter(session, original, false));
        Assert.Equal(prior, store.CurrentId(session));
    }
    [Fact]
    public void ResumingMissingRecordDoesNotCreateOrBindIt()
    {
        var store = Store(); var session = Id(); var prior = store.CurrentId(session); var missing = Id();
        Assert.Throws<KeyNotFoundException>(() => store.ResumeAndEnter(session, missing, true));
        Assert.Equal(prior, store.CurrentId(session));
        Assert.False(Directory.Exists(Path.Combine(_root, "investigations-v1", missing)));
    }
    [Fact]
    public void PerInvestigationLeasePreventsParallelExecutionAcrossStoreInstances()
    {
        var a = Store(); var session = Id(); var id = a.CurrentId(session);
        using var held = a.Enter(session);
        var b = Store(); var next = Id(); var prior = b.CurrentId(next);
        Assert.Throws<InvalidOperationException>(() => b.ResumeAndEnter(next, id, true));
        Assert.Equal(prior, b.CurrentId(next));
    }
    [Fact]
    public void FinishingOrPruningSessionsDoesNotDeleteTheirDurableRecords()
    {
        var store = Store(); var session = Id(); var id = store.CurrentId(session); var call = Id();
        store.Record(session, Id(), "context", Reply(call));
        store.RetainSessions([]);
        var next = Id();
        using var lease = store.ResumeAndEnter(next, id, true);
        Assert.Single(store.Evidence(next, [call]));
    }
    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void FailureReceiptsAreNotPromotedToSuccessfulEvidence(string outcome)
    {
        var store = Store(); var session = Id(); var call = Id();
        store.Record(session, Id(), "run_experiment", Reply(call, outcome));
        Assert.Equal(outcome, Assert.Single(store.Evidence(session, [call])).Outcome);
    }
    [Fact]
    public void RecordingSameCallTwiceDoesNotDuplicateIt()
    {
        var store = Store(); var session = Id(); var call = Id(); var reply = Reply(call);
        store.Record(session, Id(), "context", reply); store.Record(session, Id(), "context", reply);
        Assert.Single(store.Current(session).Evidence);
    }
    [Fact]
    public void LargeLiveOnlyDataIsExplicitlyUnavailableAfterResume()
    {
        var store = Store(); var session = Id(); var call = Id();
        store.Record(session, Id(), "scan", Reply(call) with { Data = null, ResultAvailable = true });
        var ex = Assert.Throws<KeyNotFoundException>(() => store.ReadResult(session, call, "", 0, 10));
        Assert.Contains("result_not_retained", ex.Message);
        Assert.Single(store.Evidence(session, [call]));
    }
    [Fact]
    public void ForeignEvidenceAndExperimentCannotBeBorrowedByName()
    {
        var store = Store(); var a = Id(); var b = Id(); var experiment = Id(); var call = Id();
        store.AddExperiment(a, experiment, Project); store.Record(a, Id(), "context", Reply(call));
        Assert.Throws<UnauthorizedAccessException>(() => store.RequireExperiment(b, experiment));
        Assert.Throws<KeyNotFoundException>(() => store.Evidence(b, [call]));
    }
    [Fact]
    public void ExperimentProjectCannotBeRebound()
    {
        var store = Store(); var session = Id(); var experiment = Id();
        store.AddExperiment(session, experiment, Project);
        Assert.Throws<InvalidDataException>(() => store.AddExperiment(session, experiment, Project + ".other"));
        Assert.Equal(Project, store.RequireExperiment(session, experiment));
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void OpaqueIdsCannotBecomeArbitraryFilePaths(string id) =>
        Assert.Throws<ArgumentException>(() => Store().ResumeAndEnter(Id(), id, true));
    [Fact]
    public void MeasurementReceiptSurvivesResumeAndCannotBeOverwritten()
    {
        var store = Store(); var session = Id(); var experiment = Id();
        var inv = store.CurrentId(session); store.AddExperiment(session, experiment, Project);
        var observed = new ReviewRunObservation(experiment, "target", Project, "snapshot", "source", "context", true, "report", "utc");
        store.SaveMeasurement(session, observed);
        Assert.Throws<InvalidOperationException>(() => store.SaveMeasurement(session, observed));
        var reopened = Store(); var next = Id(); using var lease = reopened.ResumeAndEnter(next, inv, true);
        Assert.Equal(observed, reopened.Measurement(next, experiment));
    }
    [Fact]
    public void MissingObservationIsNotInferredFromAResult()
    {
        var store = Store(); var session = Id(); var experiment = Id(); store.AddExperiment(session, experiment, Project);
        Assert.Contains("measurement_binding_missing", Assert.Throws<InvalidOperationException>(() => store.Measurement(session, experiment)).Message);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
