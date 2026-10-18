using System.Text.Json;
using Microsoft.Data.Sqlite;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

// Synthetic reports exercise validation only. These tests do NOT measure performance.
public sealed class ReviewBenchmarkAttachmentTests
{
    private static readonly string Project = Path.GetFullPath("MeasurementTarget.csproj");
    private static readonly string Hash = ReviewProgress.Hash("workload");
    private static readonly string Investigation = Guid.NewGuid().ToString("N");
    private static ReviewTarget Target() => new(ReviewProgress.Hash("target"), "method", "method", "project", "P", Project,
        "C.M()", "C.cs", 1, ReviewProgress.Hash("source"), ReviewProgress.Hash("context"), true, null, "source");
    private static ExperimentManifest Manifest(string id) => new(id, "benchmark", Path.GetDirectoryName(Project)!,
        Path.GetFileName(Project), "2026-10-17T00:00:00Z", Hash, Hash, "net8.0", null, Hash);
    private static ExecutionResult Report(string id, double mean, bool after = false, string env = "same-env", int samples = 10) =>
        new(id, "benchmark", "benchmark_completed", 0,
            after ? "2026-10-17T00:02:00Z" : "2026-10-17T00:00:00Z",
            after ? "2026-10-17T00:03:00Z" : "2026-10-17T00:01:00Z", null,
            [new("C.Work(N=100)", "C", "Work", "N=100", samples, mean, mean, 64, env)], [], "", "", "fixture")
        { TargetFramework = "net8.0" };
    private static StoredBenchmarkReport Stored(ExecutionResult result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
        return new(result, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
    private static ReviewRunObservation Observation(ReviewTarget target, ExecutionResult result) => new(result.ExperimentId,
        target.Id, target.ProjectPath, "observed-snapshot", target.SourceHash, target.ContextHash, true,
        Stored(result).Sha256, result.CompletedUtc);
    private static ReviewBenchmarkEvidence Evidence(ReviewTarget? target = null, string env = "same-env", int samples = 10)
    {
        target ??= Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100); var after = Report(b, 80, true, env, samples);
        return ReviewBenchmarkMeasurements.Create(Investigation, target, Manifest(a), Stored(before), Observation(target, before),
            Manifest(b), Stored(after), Observation(target, after));
    }
    [Fact]
    public void AttachmentContainsServerReportNumbersAndNeverClaimsIndependentVerification()
    {
        var target = Target(); var evidence = Evidence(target); var row = Assert.Single(evidence.Comparison.Cases);
        Assert.Equal("measured_comparison", evidence.Status);
        Assert.Equal(100d, row.BaselineMeanNanoseconds); Assert.Equal(80d, row.CandidateMeanNanoseconds);
        Assert.Equal(-20d, row.MeanChangePercent); Assert.Equal("faster_observed", row.Status);
        Assert.False(ReviewPerformanceState.From(evidence, false).IndependentlyVerified);
    }
    [Fact]
    public void StoredCompanionMeasurementsSupportTheSameConservativeComparison()
    {
        var target = Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100); var after = Report(b, 90, true);
        var companion = Path.Combine(Path.GetDirectoryName(Project)!, "Benchmarks.csproj");
        var baseline = Manifest(a) with { Kind = "companion_benchmark", CompanionProjectPath = companion, CompanionBenchmarkJob = "medium" };
        var candidate = Manifest(b) with { Kind = "companion_benchmark", CompanionProjectPath = companion, CompanionBenchmarkJob = "medium" };
        var result = ReviewBenchmarkMeasurements.Create(Investigation, target, baseline, Stored(before),
            Observation(target, before), candidate, Stored(after), Observation(target, after));
        Assert.Equal("measured_comparison", result.Status);
        Assert.Equal("medium", result.BenchmarkJob);
        var state = ReviewPerformanceState.From(result, false);
        Assert.Equal("medium", state.BenchmarkJob);
        Assert.False(state.IndependentlyVerified);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"benchmarkJob\":\"medium\"", json);
        Assert.Contains(result.Limitations, x => x.Contains("Companion workload hashes"));
        Assert.Throws<InvalidOperationException>(() => ReviewBenchmarkMeasurements.Create(Investigation,
            target, baseline, Stored(before), Observation(target, before),
            candidate with { CompanionBenchmarkJob = "short" }, Stored(after), Observation(target, after)));
    }
    [Fact]
    public void HistoricalCheckpointWithoutBenchmarkJobRemainsReadable()
    {
        var state = JsonSerializer.Deserialize<ReviewPerformanceState>(
            """
            {"status":"insufficient_samples","hasMeasurements":true,
             "independentlyVerified":false,"baselineExperimentId":"old","candidateExperimentId":"new",
             "comparedCases":2,"incomparableCases":0}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(state);
        Assert.Null(state.BenchmarkJob);
        Assert.Equal("insufficient_samples", state.Status);
    }

    [Fact]
    public void GeneratedAndCompanionResultsCannotBeMixed()
    {
        var target = Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100); var after = Report(b, 90, true);
        var companion = Manifest(b) with { Kind = "companion_benchmark", CompanionBenchmarkJob = "medium",
            CompanionProjectPath = Path.Combine(Path.GetDirectoryName(Project)!, "Benchmarks.csproj") };
        Assert.Throws<InvalidOperationException>(() => ReviewBenchmarkMeasurements.Create(Investigation, target,
            Manifest(a), Stored(before), Observation(target, before),
            companion, Stored(after), Observation(target, after)));
    }

    [Fact]
    public void DifferentEnvironmentIsExplicitlyIncomparable()
    {
        var evidence = Evidence(env: "other-env");
        Assert.Equal("incomparable", evidence.Status); Assert.Equal(1, evidence.Comparison.IncomparableCases);
    }
    [Fact]
    public void SmallSamplesDoNotBecomeAConfirmedSpeedup() => Assert.Equal("insufficient_samples", Evidence(samples: 2).Status);
    [Theory]
    [InlineData("checked")]
    [InlineData("optimized")]
    public void MarksWithoutReportsAreUnmeasured(string status)
    {
        var target = Target(); var mark = new ReviewCheckpoint(1, target.Id, target.Kind, target.Name, target.ProjectPath,
            status, target.SourceHash, target.ContextHash, "report", "agent", "session", "utc", []);
        Assert.Equal(status, ReviewProgress.State(target, mark, false).Status);
        Assert.Equal("unmeasured", ReviewProgress.State(target, mark, false).Performance.Status);
    }
    [Fact]
    public void OldCheckpointJsonWithoutMeasurementStillLoads()
    {
        var target = Target(); var mark = new ReviewCheckpoint(1, target.Id, target.Kind, target.Name, target.ProjectPath,
            "checked", target.SourceHash, target.ContextHash, "report", "agent", "session", "utc", []);
        var json = JsonSerializer.Serialize(mark, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("measurement", json);
        Assert.Null(JsonSerializer.Deserialize<ReviewCheckpoint>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Measurement);
    }
    [Fact]
    public void ChangedSourceMakesMeasurementStaleWithoutDeletingHistoricalNumbers()
    {
        var target = Target(); var evidence = Evidence(target);
        var mark = new ReviewCheckpoint(1, target.Id, target.Kind, target.Name, target.ProjectPath, "optimized",
            target.SourceHash, target.ContextHash, "report", "agent", "session", "utc", []) { Measurement = evidence };
        var state = ReviewProgress.State(target with { SourceHash = ReviewProgress.Hash("changed") }, mark, false);
        Assert.Equal("needs_review", state.Status); Assert.Equal("stale_measurement", state.Performance.Status);
        Assert.Same(evidence, state.Checkpoint!.Measurement);
    }
    [Fact]
    public void CompactQueueKeepsPerformanceSummaryWithoutRepeatingAllCases()
    {
        var target = Target(); var evidence = Evidence(target);
        var mark = new ReviewCheckpoint(1, target.Id, target.Kind, target.Name, target.ProjectPath, "optimized",
            target.SourceHash, target.ContextHash, "report", "agent", "session", "utc", []) { Measurement = evidence };
        var row = ReviewContextFingerprints.CompactForQueue(new(target, ReviewProgress.State(target, mark, false)));
        Assert.Null(row.State.Checkpoint!.Measurement);
        Assert.Equal("measured_comparison", row.State.Performance.Status);
        Assert.Same(evidence, mark.Measurement);
    }
    [Theory]
    [InlineData("target")]
    [InlineData("source")]
    [InlineData("context")]
    [InlineData("inputs")]
    [InlineData("report")]
    [InlineData("project")]
    public void WrongOrChangedProvenanceCannotBeAttached(string change)
    {
        var target = Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100); var after = Report(b, 80, true); var observation = Observation(target, after);
        observation = change switch
        {
            "target" => observation with { TargetId = "other" },
            "source" => observation with { SourceHash = ReviewProgress.Hash("other") },
            "context" => observation with { ContextHash = ReviewProgress.Hash("other") },
            "inputs" => observation with { SourceInputsUnchanged = false },
            "report" => observation with { ReportSha256 = ReviewProgress.Hash("other") },
            _ => observation with { ProjectPath = Project + ".other" }
        };
        Action attach = () => ReviewBenchmarkMeasurements.Create(Investigation, target,
            Manifest(a), Stored(before), Observation(target, before), Manifest(b), Stored(after), observation);
        if (change == "report") Assert.Throws<InvalidDataException>(attach);
        else Assert.ThrowsAny<InvalidOperationException>(attach);
    }
    [Theory]
    [InlineData("workload")]
    [InlineData("runner")]
    [InlineData("template")]
    [InlineData("framework")]
    public void DifferentWorkloadTemplateRunnerOrFrameworkIsRejected(string change)
    {
        var target = Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100); var after = Report(b, 80, true);
        var candidate = change switch
        {
            "workload" => Manifest(b) with { SourceSha256 = ReviewProgress.Hash("different") },
            "runner" => Manifest(b) with { RunnerSha256 = ReviewProgress.Hash("different") },
            "template" => Manifest(b) with { ProjectSha256 = ReviewProgress.Hash("different") },
            _ => Manifest(b) with { SubjectTargetFramework = "netstandard2.0" }
        };
        Assert.Throws<InvalidOperationException>(() => ReviewBenchmarkMeasurements.Create(Investigation, target,
            Manifest(a), Stored(before), Observation(target, before), candidate, Stored(after), Observation(target, after)));
    }
    [Theory]
    [InlineData("benchmark_failed")]
    [InlineData("cancelled")]
    [InlineData("benchmark_unverified")]
    public void NoncompletedReportsAreRejected(string status)
    {
        var target = Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100); var after = Report(b, 80, true) with { Status = status };
        Assert.Throws<InvalidOperationException>(() => ReviewBenchmarkMeasurements.Create(Investigation, target,
            Manifest(a), Stored(before), Observation(target, before), Manifest(b), Stored(after), Observation(target, after)));
    }
    [Fact]
    public void SwappedChronologyIsRejectedEvenWhenSourceIsUnchanged()
    {
        var target = Target(); var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        var before = Report(a, 100, true); var after = Report(b, 80);
        Assert.Throws<InvalidOperationException>(() => ReviewBenchmarkMeasurements.Create(Investigation, target,
            Manifest(a), Stored(before), Observation(target, before), Manifest(b), Stored(after), Observation(target, after)));
    }
    [Fact]
    public void SqliteRoundtripRetainsMeasurementWithoutUpgradingTheClaim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tenka-measurement-db-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Target(); var evidence = Evidence(target); var path = Path.Combine(dir, "review.sqlite");
            var request = new ReviewMarkRequest("snapshot", target.Id, target.SourceHash, target.ContextHash, 0, "optimized", "fixture only") { Measurement = evidence };
            var saved = new ReviewProgressStore(path).Save(target, request, "agent", "session", []);
            var reloaded = new ReviewProgressStore(path).Read(target.Id, saved.Revision).Checkpoint;
            Assert.Equal(evidence.Candidate.ReportSha256, reloaded.Measurement!.Candidate.ReportSha256);
            Assert.Equal("optimized", reloaded.Status);
            Assert.False(ReviewProgress.State(target, reloaded, false).Performance.IndependentlyVerified);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
