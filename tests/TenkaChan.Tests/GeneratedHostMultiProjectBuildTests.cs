using System.Diagnostics;
using System.Xml.Linq;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Offline .NET SDK compile regression for CS0234/CS0246 when a generated harness
/// uses public API types owned by a dependency of the selected project.
/// No generated benchmark or untrusted executable is run by this test.
/// </summary>
public sealed class GeneratedHostMultiProjectBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tenka-host-closure-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingBuiltDependencyIsAvailableToGeneratedCompiler()
    {
        Directory.CreateDirectory(_root);
        var domain = Path.Combine(_root, "Domain");
        var app = Path.Combine(_root, "Application");
        var generated = Path.Combine(_root, "Generated");
        Directory.CreateDirectory(domain);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(_root, "NuGet.Config"),
            "<configuration><packageSources><clear /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(domain, "Domain.csproj"),
            Project() );
        File.WriteAllText(Path.Combine(domain, "Dataset.cs"),
            "namespace Domain; public sealed record RaidDataset(int Players);");
        File.WriteAllText(Path.Combine(app, "Application.csproj"),
            Project() .Replace("</Project>",
                "<ItemGroup><ProjectReference Include=\"../Domain/Domain.csproj\" /></ItemGroup></Project>"));
        File.WriteAllText(Path.Combine(app, "Analyzer.cs"),
            "namespace Application; public static class RaidAnalyzer { public static int Analyze(Domain.RaidDataset data) => data.Players; }");
        RunDotnet(app, "restore", "Application.csproj", "--configfile",
            Path.Combine(_root, "NuGet.Config"), "-p:NuGetAudit=false");
        RunDotnet(app, "build", "Application.csproj", "--no-restore", "-c", "Release");
        var subject = Path.Combine(app, "bin", "Release", "net8.0", "Application.dll");
        Assert.True(File.Exists(subject));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(subject)!, "Domain.dll")));

        var project = XDocument.Parse(Project() .Replace("</Project>",
            "<ItemGroup><ProjectReference Include=\"../Application/Application.csproj\" /></ItemGroup></Project>"));
        var guard = new WorkspaceGuard([_root], false, false);
        var referenceCount = TrustedExperimentReferenceClosure.ReplaceProjectReferences(project, guard, subject);
        Assert.Equal(2, referenceCount);
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Equal(new[] { "Application", "Domain" },
            project.Descendants("Reference").Select(x => (string)x.Attribute("Include")!).OrderBy(x => x, StringComparer.Ordinal));
        project.Save(Path.Combine(generated, "Generated.csproj"));
        File.WriteAllText(Path.Combine(generated, "Generated.cs"),
            "using Application; using Domain; public static class Generated { public static int Run() => RaidAnalyzer.Analyze(new RaidDataset(40)); }");
        RunDotnet(generated, "restore", "Generated.csproj", "--configfile",
            Path.Combine(_root, "NuGet.Config"), "-p:NuGetAudit=false");
        RunDotnet(generated, "build", "Generated.csproj", "--no-restore", "-c", "Release");
    }

    private static string Project() =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>" +
        "<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";

    private static void RunDotnet(string directory, params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
            { WorkingDirectory = directory, UseShellExecute = false,
              RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
            var combined = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0, string.Join(" ", args) + " failed: " +
                combined[^Math.Min(combined.Length, 4000)..]);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Offline multi-project build fixture exceeded 120 seconds.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
