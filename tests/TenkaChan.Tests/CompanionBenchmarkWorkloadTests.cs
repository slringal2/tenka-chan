using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class CompanionBenchmarkWorkloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-companion-" + Guid.NewGuid().ToString("N"));
    private string Setup()
    {
        var benchmark = Path.Combine(_root, "Benchmarks");
        var app = Path.Combine(_root, "App");
        Directory.CreateDirectory(benchmark);
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(benchmark, "Bench.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../App/App.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(benchmark, "Program.cs"), "public class Bench { public int Run() => 7; }");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project><PropertyGroup /></Project>");
        return Path.Combine(benchmark, "Bench.csproj");
    }
    [Fact]
    public void CapturesStableBenchmarkInputWithoutHashingTargetImplementation()
    {
        var project = Setup(); var guard = new WorkspaceGuard([_root], false, false);
        var first = CompanionBenchmarkWorkloads.Capture(guard, project);
        Assert.True(CompanionBenchmarkWorkloads.DeclaresReferenceTo(guard, project, Path.Combine(_root, "App", "App.csproj")));
        File.WriteAllText(Path.Combine(_root, "App", "Implementation.cs"), "public class NewImplementation {}");
        Assert.Equal(first, CompanionBenchmarkWorkloads.Capture(guard, project));
        File.AppendAllText(Path.Combine(_root, "Benchmarks", "Program.cs"), "\n// benchmark changed");
        var changed = CompanionBenchmarkWorkloads.Capture(guard, project);
        Assert.NotEqual(first.SourceSha256, changed.SourceSha256);
        Assert.NotEqual(first.RunnerSha256, changed.RunnerSha256);
    }
    [Fact]
    public void ImportedProjectPropsChangeTheTemplateHash()
    {
        var project = Setup(); var guard = new WorkspaceGuard([_root], false, false);
        var first = CompanionBenchmarkWorkloads.Capture(guard, project);
        File.AppendAllText(Path.Combine(_root, "Directory.Build.props"), "\n<!-- changed -->");
        var second = CompanionBenchmarkWorkloads.Capture(guard, project);
        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.NotEqual(first.ProjectSha256, second.ProjectSha256);
    }
    [Fact]
    public void DifferentUnrelatedProjectIsNotLinkedByANameGuess()
    {
        var project = Setup(); var guard = new WorkspaceGuard([_root], false, false);
        var unrelated = Path.Combine(_root, "Unrelated", "Unrelated.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "<Project />");
        Assert.False(CompanionBenchmarkWorkloads.DeclaresReferenceTo(guard, project, unrelated));
    }
    [Fact]
    public void AutoCompanionSelectionUsesAnUnambiguousProjectReference()
    {
        var project = Setup();
        var target = Path.Combine(_root, "App", "App.csproj");
        var guard = new WorkspaceGuard([_root], false, false);
        var companion = new ExplorerCompanion("bench-id", "project-id", "Bench", project, "benchmark");
        var unrelated = new ExplorerCompanion("test-id", "project-id", "Tests", project, "test");
        var selected = CompanionBenchmarkWorkloads.SelectForTarget(guard,
            [unrelated, companion], target, null);
        Assert.Equal(companion, selected);
        Assert.Equal(companion, CompanionBenchmarkWorkloads.SelectForTarget(guard,
            [companion], target, companion.Id));
    }
    [Fact]
    public void AutoCompanionSelectionNeverGuessesBetweenMultipleBenchmarks()
    {
        var project = Setup();
        var target = Path.Combine(_root, "App", "App.csproj");
        var otherDir = Path.Combine(_root, "OtherBench"); Directory.CreateDirectory(otherDir);
        var otherProject = Path.Combine(otherDir, "Other.csproj");
        File.WriteAllText(otherProject,
            "<Project><ItemGroup><ProjectReference Include=\"../App/App.csproj\" /></ItemGroup></Project>");
        var guard = new WorkspaceGuard([_root], false, false);
        var first = new ExplorerCompanion("a-id", "p", "A", project, "benchmark");
        var second = new ExplorerCompanion("b-id", "p", "B", otherProject, "benchmark");
        var error = Assert.Throws<InvalidOperationException>(() =>
            CompanionBenchmarkWorkloads.SelectForTarget(guard, [first, second], target, null));
        Assert.Contains("measurement_companion_ambiguous", error.Message);
        Assert.Contains("companionId", error.Message);
        Assert.Equal(second, CompanionBenchmarkWorkloads.SelectForTarget(guard,
            [first, second], target, second.Id));
    }
    [Fact]
    public void UnlinkedOrMissingCompanionDoesNotImplicitlyExecute()
    {
        var project = Setup(); var target = Path.Combine(_root, "App", "App.csproj");
        var unrelated = Path.Combine(_root, "Other", "Other.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "<Project />");
        var guard = new WorkspaceGuard([_root], false, false);
        var item = new ExplorerCompanion("other-id", "p", "Other", unrelated, "benchmark");
        Assert.Throws<InvalidOperationException>(() =>
            CompanionBenchmarkWorkloads.SelectForTarget(guard, [item], target, null));
        Assert.Throws<InvalidOperationException>(() =>
            CompanionBenchmarkWorkloads.SelectForTarget(guard, [item], target, item.Id));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
