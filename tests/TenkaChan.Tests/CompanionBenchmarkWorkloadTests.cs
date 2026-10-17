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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
