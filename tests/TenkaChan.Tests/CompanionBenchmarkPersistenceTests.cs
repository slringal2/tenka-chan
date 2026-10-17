using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class CompanionBenchmarkPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-companion-store-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _artifacts = new();
    [Fact]
    public void CompletedCompanionRunCanBeRetainedWithoutPretendingItWasGenerated()
    {
        var app = Path.Combine(_root, "App");
        var bench = Path.Combine(_root, "Bench");
        Directory.CreateDirectory(app); Directory.CreateDirectory(bench);
        var subject = Path.Combine(app, "App.csproj");
        var companion = Path.Combine(bench, "Bench.csproj");
        File.WriteAllText(subject, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(companion, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(bench, "Program.cs"), "public class BenchmarkWork {}");
        var guard = new WorkspaceGuard([_root], false, false);
        var store = new ExperimentStore(guard);
        var workload = CompanionBenchmarkWorkloads.Capture(guard, companion);
        var output = new ExplorerProjectRun("benchmark_completed", "benchmark", "Bench", 0, "", "", null,
            [new BenchmarkObservation("Bench.Run()", "Bench", "Run", "", 10, 120, 119, 128, "same-host")], "fixture");
        var (receipt, report) = store.RecordCompanionBenchmark(subject, workload, output, "2026-10-17T00:00:00Z");
        _artifacts.Add(receipt.Directory);
        var (manifest, _) = store.Read(receipt.Id);
        Assert.Equal("companion_benchmark", manifest.Kind);
        Assert.Equal(companion, manifest.CompanionProjectPath);
        Assert.Equal("benchmark", report.Kind);
        Assert.Equal("benchmark_completed", store.ReadExecutionResult(receipt.Id).Status);
        Assert.Throws<InvalidOperationException>(() => store.CreateArtifactsDirectory(receipt.Id));
        File.AppendAllText(Path.Combine(bench, "Program.cs"), "// changed later");
        Assert.Equal(manifest.SourceSha256, store.Read(receipt.Id).Manifest.SourceSha256);
    }
    [Fact]
    public void IncompleteRunsAreNotStoredAsCompletedMeasurements()
    {
        var root = Path.Combine(_root, "Src"); Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Src.csproj");
        File.WriteAllText(project, "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Program.cs"), "public class Work {}");
        var guard = new WorkspaceGuard([_root], false, false);
        var store = new ExperimentStore(guard);
        var workload = CompanionBenchmarkWorkloads.Capture(guard, project);
        var failed = new ExplorerProjectRun("benchmark_unverified", "benchmark", "Src", 1, "", "", null, [], "failure");
        Assert.Throws<InvalidOperationException>(() => store.RecordCompanionBenchmark(project, workload, failed, "2026-10-17T00:00:00Z"));
    }
    public void Dispose()
    {
        foreach (var dir in _artifacts) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
