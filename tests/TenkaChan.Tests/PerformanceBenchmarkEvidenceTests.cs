using TenkaChan.Mcp.Experiments;
using Xunit;

namespace TenkaChan.Tests;

public sealed class PerformanceBenchmarkEvidenceTests
{
    [Fact]
    public void SummarizesBenchmarkSamplesAsSeparateExperimentObservations()
    {
        const string id = "benchmark-fixture";
        var manifest = new ExperimentManifest(id, "benchmark", "/repo",
            "App.csproj", "created", "SOURCE", "PROJECT",
            "net8.0");
        var observations = new[]
        {
            new BenchmarkObservation("Bench.Run(N=128)", "Bench", "Run",
                "N=128", 25, 125.7, 124.8, 64, "linux|cpu|net8")
        };
        var result = new ExecutionResult(id, "benchmark",
            "benchmark_completed", 0, "start", "end", null,
            observations, [], "", "", "fixture")
        { TargetFramework = "net8.0" };

        var evidence = PerformanceEvidenceService.SummarizeBenchmark(
            id, manifest, result);
        Assert.Equal("net8.0", evidence.TargetFramework);
        Assert.Equal("benchmark_completed", evidence.Status);
        var single = Assert.Single(evidence.Cases);
        Assert.Equal(125.7, single.MeanNanoseconds);
        Assert.Equal(64d, single.AllocatedBytesPerOperation);
        Assert.Equal(25, single.SampleCount);
        Assert.Contains(evidence.Limitations, message =>
            message.Contains("do not uniquely map", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("benchmark_unverified")]
    [InlineData("benchmark_failed")]
    public void RefusesToTreatUnverifiedOrFailedRunnerAsBenchmarkEvidence(string status)
    {
        const string id = "benchmark-fixture";
        var manifest = new ExperimentManifest(id, "benchmark", "/repo",
            "App.csproj", "created", "SOURCE");
        var result = new ExecutionResult(id, "benchmark", status,
            1, "start", "end", null,
            [new BenchmarkObservation("Bench.Case()", "Bench", "Case",
                "", 0, null, null, null, "")],
            [], "", "", "fixture");

        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.SummarizeBenchmark(id, manifest, result));
    }

    [Fact]
    public void RejectsCrossExperimentResultSubstitution()
    {
        var manifest = new ExperimentManifest("expected", "benchmark",
            "/repo", "App.csproj", "created", "SOURCE");
        var result = new ExecutionResult("different", "benchmark",
            "benchmark_completed", 0, "start", "end", null,
            [new BenchmarkObservation("Bench.Run()", "Bench", "Run",
                "", 12, 100d, 99d, 0d, "host")],
            [], "", "", "fixture");
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.SummarizeBenchmark("expected", manifest, result));
    }
}
