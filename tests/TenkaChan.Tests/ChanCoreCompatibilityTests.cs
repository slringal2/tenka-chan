using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using SharedStatistics = Chan.Core.Performance.PerformanceStatistics;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ChanCoreCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-core-" + Guid.NewGuid().ToString("N"));

    public ChanCoreCompatibilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void StatisticsFacadeKeepsItsPublicTypeAndJsonShape()
    {
        double[] baseline = [99, 100, 101, 102, 98, 100];
        double[] candidate = [79, 80, 81, 82, 78, 80];
        var legacy = PerformanceStatistics.Compare(baseline, candidate, "host", "host");
        var shared = SharedStatistics.Compare(baseline, candidate, "host", "host");
        Assert.Equal("TenkaChan.Mcp.Analysis.PerformanceComparison", legacy.GetType().FullName);
        Assert.Equal(JsonSerializer.Serialize(shared), JsonSerializer.Serialize(legacy));
        Assert.NotEqual(legacy.GetType().Assembly, shared.GetType().Assembly);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExecutionPermissionsRemainOnTenka(bool trustMsBuild, bool allowDocker)
    {
        var guard = new WorkspaceGuard([_root], trustMsBuild, allowDocker);
        Assert.Equal(trustMsBuild, guard.TrustMsBuild);
        Assert.Equal(allowDocker, guard.AllowDocker);
        Assert.Equal(_root, guard.RequireDirectory(_root));
    }

    [Fact]
    public void AllowlistDiagnosticStillNamesTheTenkaSetting()
    {
        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            new WorkspaceGuard([], false, false).RequireDirectory(_root));
        Assert.Equal("Path is not in TENKA_ALLOWED_ROOTS. Configure an explicit directory allowlist.", error.Message);
    }

    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    [InlineData(".csproj")]
    public void TenkaStillAcceptsItsExistingProjectTypes(string extension)
    {
        var file = Path.Combine(_root, "Example" + extension);
        File.WriteAllText(file, "fixture");
        Assert.Equal(file, new WorkspaceGuard([_root], false, false).RequireSolutionOrProject(file));
    }

    [Fact]
    public void SharingPathChecksDoesNotBroadenTenkaToCppProjects()
    {
        var guard = new WorkspaceGuard([_root], false, false);
        Assert.Throws<ArgumentException>(() =>
            guard.RequireSolutionOrProject(Path.Combine(_root, "Example.vcxproj")));
    }

    [Fact]
    public void SelectedRepositoryBoundaryRemainsStricterThanGlobalAllowlist()
    {
        var chosen = Directory.CreateDirectory(Path.Combine(_root, "chosen")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "Example.csproj"), "fixture");
        var guard = new WorkspaceGuard([_root], false, false);
        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            guard.RequireProjectWithin(chosen, Path.Combine("..", "other", "Example.csproj")));
        Assert.Equal("Project path escapes the chosen repository root.", error.Message);
        Assert.Throws<ArgumentException>(() =>
            guard.RequireProjectWithin(chosen, Path.Combine(other, "Example.csproj")));
    }

    [Fact]
    public void StoredBenchmarkAdapterKeepsIdsAndCaseJsonShape()
    {
        File.WriteAllText(Path.Combine(_root, "Example.csproj"),
            "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var store = new ExperimentStore(new WorkspaceGuard([_root], false, false));
        ExperimentReceipt? baseline = null, candidate = null;
        try
        {
            baseline = store.Create(_root, "Example.csproj", "benchmark", "public class Work { }");
            candidate = store.Create(_root, "Example.csproj", "benchmark", "public class Work { }");
            Save(store, baseline, 100);
            Save(store, candidate, 120);
            var actual = new BenchmarkRunComparer(store).Compare(baseline.Id, candidate.Id);
            var expected = Chan.Core.Benchmarks.BenchmarkSummaryComparer.Compare(
                [new("Work.Run(N=100)", 10, 100, 64, "host")],
                [new("Work.Run(N=100)", 10, 120, 64, "host")]);
            Assert.Equal(baseline.Id, actual.BaselineExperimentId);
            Assert.Equal(candidate.Id, actual.CandidateExperimentId);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.ComparedCases, actual.ComparedCases);
            Assert.Equal(expected.IncomparableCases, actual.IncomparableCases);
            Assert.Equal(expected.Note, actual.Note);
            Assert.Equal(JsonSerializer.Serialize(expected.Cases), JsonSerializer.Serialize(actual.Cases));
            Assert.Equal("TenkaChan.Mcp.Experiments.BenchmarkCaseComparison",
                Assert.Single(actual.Cases).GetType().FullName);
        }
        finally
        {
            if (baseline is not null) Directory.Delete(baseline.Directory, true);
            if (candidate is not null) Directory.Delete(candidate.Directory, true);
        }
    }

    private static void Save(ExperimentStore store, ExperimentReceipt receipt, double mean) =>
        store.SaveExecutionResult(new ExecutionResult(receipt.Id, "benchmark",
            "benchmark_completed", 0, "start", "end", null,
            [new BenchmarkObservation("Work.Run(N=100)", "Work", "Run", "N=100",
                10, mean, mean, 64, "host")], [], "", "", "Test data"));
}
