using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using TrackedReviewTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;
using Xunit;
using Xunit.Abstractions;

namespace TenkaChan.Tests;

/// <summary>
/// Actual SDK/MSBuild + Roslyn probe using only authored temporary fixtures.
/// Requires the .NET 10 SDK used by the test project. Missing SDK/build failure FAILS,
/// never silently skips or reports a synthetic success. No target application is run.
/// JSON evidence is written to xUnit output and can be retained by the TRX logger.
/// </summary>
public sealed class ReferencePathMsBuildProbeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-reference-probe-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ConsumerSource = "public class Consumer { public int Run() => new External.Widget().Value; }";
    private const string ConsumerProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Reference Include=\"FixtureDependency\"><HintPath>$(FixtureReference)</HintPath></Reference></ItemGroup></Project>";
    private sealed record Observation(string SnapshotId, TrackedReviewTarget Target, int CompilerErrors,
        IReadOnlyList<string> WorkspaceWarnings, ReviewReferenceDiagnosticReport ReferenceDiagnostics);

    [Fact]
    [Trait("Category", "ReferencePathIntegration")]
    public async Task MsbuildRelocationSeparatesLabelsProjectEditsAndMissingReferences()
    {
        Directory.CreateDirectory(_root);
        var dependency = Path.Combine(_root, "dependency");
        var consumer = Path.Combine(_root, "consumer");
        Directory.CreateDirectory(dependency); Directory.CreateDirectory(consumer);
        var dependencyProject = Path.Combine(dependency, "FixtureDependency.csproj");
        var consumerProject = Path.Combine(consumer, "Consumer.csproj");
        File.WriteAllText(dependencyProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dependency, "Widget.cs"), "namespace External; public class Widget { public int Value => 1; }");
        File.WriteAllText(consumerProject, ConsumerProject);
        File.WriteAllText(Path.Combine(consumer, "Consumer.cs"), ConsumerSource);
        var config = Path.Combine(_root, "NuGet.Config");
        File.WriteAllText(config, "<configuration><packageSources><clear /></packageSources></configuration>");
        await BuildFixture(dependencyProject, config);
        var dll = Path.Combine(dependency, "bin", "Debug", "net10.0", "FixtureDependency.dll");
        var first = Path.Combine(_root, "first-cache", "FixtureDependency.dll");
        var second = Path.Combine(_root, "moved-cache", "FixtureDependency.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!); Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.Copy(dll, first); File.Copy(dll, second);
        var expectedBytes = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first)));
        Assert.Equal(expectedBytes, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second))));
        SetImportedPath(consumer, first);
        await BuildFixture(consumerProject, config);
        var baseline = await Observe(consumerProject, "baseline");
        Assert.Equal(0, baseline.CompilerErrors);

        // Change the evaluated reference location WITHOUT changing the csproj or source.
        // A new MSBuildWorkspace reads the imported property; no global environment is mutated.
        SetImportedPath(consumer, second);
        var relocated = await Observe(consumerProject, "relocated_by_imported_property");
        var beforeReference = Assert.Single(baseline.ReferenceDiagnostics.Items.Where(i => i.FilePath == first));
        var afterReference = Assert.Single(relocated.ReferenceDiagnostics.Items.Where(i => i.FilePath == second));
        Assert.Equal(0, relocated.CompilerErrors);
        Assert.Equal(beforeReference.IdentityHash, afterReference.IdentityHash);
        Assert.Equal(baseline.Target.Id, relocated.Target.Id);
        Assert.Equal(baseline.Target.SourceHash, relocated.Target.SourceHash);
        Assert.NotEqual(baseline.Target.LegacyContextHash, relocated.Target.LegacyContextHash);
        Assert.Equal(baseline.Target.ContextHash, relocated.Target.ContextHash);
        var checkpoint = Mark(baseline.Target);
        Assert.Equal("checked", ReviewProgress.State(relocated.Target, checkpoint, false).Status);
        output.WriteLine(JsonSerializer.Serialize(new { schema = "tenka.reference-path-probe/v1", stage = "relocation_assertions_passed", dllSha256 = expectedBytes }, Json));

        // Same effective DLL, but now change explicit project text: projectFiles MUST differ.
        File.WriteAllText(consumerProject, ConsumerProject.Replace("$(FixtureReference)", SecurityElement.Escape(second)));
        var projectEdited = await Observe(consumerProject, "explicit_hintpath_edit");
        Assert.Equal(0, projectEdited.CompilerErrors);
        Assert.Equal("projectFiles", Assert.Single(ReviewProgress.State(projectEdited.Target, checkpoint, false).ContextChange!.ChangedComponents));

        // Actual reference resolution failure is a separate negative control, not hash churn.
        var absent = Path.Combine(_root, "not-present.dll");
        File.WriteAllText(consumerProject, ConsumerProject.Replace("$(FixtureReference)", SecurityElement.Escape(absent)));
        var missing = await Observe(consumerProject, "missing_physical_reference");
        Assert.True(missing.CompilerErrors > 0, "An absent dependency must produce compiler errors, regardless of metadata identity completeness.");
        output.WriteLine(JsonSerializer.Serialize(new { schema = "tenka.reference-path-probe/v1", stage = "all_assertions_passed" }, Json));
    }
    private static void SetImportedPath(string directory, string path) =>
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"),
            "<Project><PropertyGroup><FixtureReference>" + SecurityElement.Escape(path) + "</FixtureReference></PropertyGroup></Project>");
    private async Task<Observation> Observe(string path, string stage)
    {
        // The assembly's existing TestMsbuildInitialization registers MSBuildLocator.
        using var roslyn = new RoslynReviewService(new WorkspaceGuard([_root], true, false));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var summary = await roslyn.OpenAsync(path, deadline.Token);
        var diagnostics = await roslyn.GetCompilationDiagnosticsAsync(400, deadline.Token);
        var index = await roslyn.ExplorerAsync(deadline.Token);
        var catalog = await index.ReviewCatalogAsync(new WorkspaceGuard([_root], true, false), deadline.Token);
        var target = catalog.Targets.Single(t => t.Kind == "method" && t.Name.Contains("Consumer.Run()", StringComparison.Ordinal));
        var result = new Observation(summary.SnapshotId, target, diagnostics.ErrorCount,
            summary.WorkspaceWarnings, catalog.ReferencesFor(target.ProjectId));
        output.WriteLine(JsonSerializer.Serialize(new { schema = "tenka.reference-path-probe/v1", stage, observation = result }, Json));
        return result;
    }
    private static ReviewCheckpoint Mark(TrackedReviewTarget target) =>
        new(1, target.Id, target.Kind, target.Name, target.ProjectPath, "checked", target.SourceHash, target.ContextHash,
            "Fixture checkpoint only", "test", "test-session", "test", []) { ContextFingerprint = target.ContextFingerprint };
    private async Task BuildFixture(string project, string config)
    {
        await Dotnet("restore", project, "--configfile", config, "-p:NuGetAudit=false", "--verbosity", "quiet");
        await Dotnet("build", project, "--no-restore", "-c", "Debug", "--nologo", "--verbosity", "quiet", "-p:UseSharedCompilation=false");
    }
    private async Task Dotnet(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch the installed dotnet SDK.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        var text = await stdout + "\n" + await stderr;
        output.WriteLine(JsonSerializer.Serialize(new { schema = "tenka.reference-path-probe/v1", stage = "fixture_sdk_step", operation = arguments[0], process.ExitCode, output = text.Length > 8000 ? text[..8000] : text }, Json));
        Assert.True(process.ExitCode == 0, "Fixture SDK step failed; inspect fixture_sdk_step output. This is not a successful reference-path reproduction.");
    }
    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { } // Failed/locked fixtures remain available for manual diagnosis.
        catch (UnauthorizedAccessException) { }
    }
}
