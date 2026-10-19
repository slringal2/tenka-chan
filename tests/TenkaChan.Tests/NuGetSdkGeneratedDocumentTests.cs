using System.Security;
using Microsoft.Build.Locator;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NuGetSdkGeneratedDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tenka-nuget-open-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_root, "workspace");
    private string Packages => Path.Combine(_root, "packages");
    private string Synthetic => Path.Combine(Packages, "microsoft.net.test.sdk", "17.14.1",
        "build", "net8.0", "Microsoft.NET.Test.Sdk.Program.cs");
    private string Injected => Path.Combine(Packages, "tunnelvisionlabs.referenceassemblyannotator",
        "1.0.0-alpha.160", "build", "NullableAttributes.cs");

    private string Fixture(string? additionalExternal = null, string? additionalPackageInjected = null)
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(Synthetic)!);
        var app = Path.Combine(Workspace, "App");
        var tests = Path.Combine(Workspace, "Tests");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(tests);
        File.WriteAllText(Path.Combine(app, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(app, "Run.cs"), "namespace App; public static class Run { public static int Value() => 42; }");
        File.WriteAllText(Synthetic, "namespace Microsoft.NET.Test.Sdk; public static class Program { public static int Main() => 0; }");
        var extra = additionalExternal is null ? "" :
            "<Compile Include=\"" + SecurityElement.Escape(additionalExternal) + "\" Link=\"External.cs\" />";
        var injected = "";
        if (additionalPackageInjected is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(additionalPackageInjected)!);
            File.WriteAllText(additionalPackageInjected,
                "namespace TunnelVisionLabs; internal static class NullableAttributes { }");
            injected = "<Compile Include=\"" + SecurityElement.Escape(additionalPackageInjected) + "\" Link=\"Injected.cs\" />";
        }
        File.WriteAllText(Path.Combine(tests, "Tests.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../App/App.csproj" />
                <Compile Include="{SecurityElement.Escape(Synthetic)}" Link="Sdk.Program.cs" />
                {extra}
                {injected}
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(tests, "Check.cs"),
            "namespace Tests; public static class Check { public static int Go() => App.Run.Value(); }");
        var solution = Path.Combine(Workspace, "Fixture.slnx");
        File.WriteAllText(solution, """
            <Solution>
              <Project Path="App/App.csproj" />
              <Project Path="Tests/Tests.csproj" />
            </Solution>
            """);
        return solution;
    }

    [Fact]
    public void OnlyExactKnownSdkSourceInActualPackageLayoutIsExcludable()
    {
        Fixture();
        var policy = new NuGetSdkGeneratedDocumentPolicy(Packages);
        Assert.True(policy.CanExclude(Synthetic));
        Assert.False(policy.CanExclude(Path.Combine(Workspace, "App", "Run.cs")));
        var other = Path.Combine(Packages, "some.other.package", "1.0.0", "build", "net8.0",
            "Microsoft.NET.Test.Sdk.Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "unrelated");
        Assert.False(policy.CanExclude(other));
        var linked = Path.Combine(Packages, "microsoft.net.test.sdk", "17.14.1", "build", "net8.0", "Sensitive.cs");
        File.WriteAllText(linked, "unrelated");
        Assert.False(policy.CanExclude(linked));
        Assert.False(policy.CanExclude(Path.Combine(Packages, "microsoft.net.test.sdk", "..",
            "..", "outside.cs")));
    }

    [Fact]
    public void ReviewedPackageInjectedSourceCatalogIsExactAndNarrow()
    {
        Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(Injected)!);
        File.WriteAllText(Injected, "namespace TunnelVisionLabs; internal static class NullableAttributes { }");
        var policy = new NuGetSdkGeneratedDocumentPolicy(Packages);

        Assert.True(policy.CanExclude(Injected));
        // Same file name under an unreviewed package id stays outside the allowlist.
        var otherPackage = Path.Combine(Packages, "some.other.package", "1.0.0", "build", "NullableAttributes.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPackage)!);
        File.WriteAllText(otherPackage, "unrelated");
        Assert.False(policy.CanExclude(otherPackage));
        // A different file inside the reviewed package is not exempt.
        var otherFile = Path.Combine(Packages, "tunnelvisionlabs.referenceassemblyannotator",
            "1.0.0-alpha.160", "build", "Sensitive.cs");
        File.WriteAllText(otherFile, "unrelated");
        Assert.False(policy.CanExclude(otherFile));
        // The reviewed layout has no TFM folder; an unexpected nested folder is rejected.
        var nested = Path.Combine(Packages, "tunnelvisionlabs.referenceassemblyannotator",
            "1.0.0-alpha.160", "build", "net8.0", "NullableAttributes.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "unrelated");
        Assert.False(policy.CanExclude(nested));
        // Only the package's build folder is recognised.
        var notBuild = Path.Combine(Packages, "tunnelvisionlabs.referenceassemblyannotator",
            "1.0.0-alpha.160", "lib", "NullableAttributes.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(notBuild)!);
        File.WriteAllText(notBuild, "unrelated");
        Assert.False(policy.CanExclude(notBuild));
    }

    [Fact]
    public async Task OpensMultiProjectSolutionWithoutIndexingExternalSdkSyntheticSource()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        var solution = Fixture();
        var guard = new WorkspaceGuard([Workspace], true, false);
        using var service = new RoslynReviewService(guard, new NuGetSdkGeneratedDocumentPolicy(Packages));
        var result = await service.OpenAsync(solution);
        Assert.Equal(2, result.Projects.Count);
        var app = Assert.Single(result.Projects, p => p.Name == "App");
        var tests = Assert.Single(result.Projects, p => p.Name == "Tests");
        // The synthetic SDK source is removed from the Tests project, leaving it with the
        // same document set as the App project (one user source plus any SDK-generated
        // documents, whose count varies by SDK version). Do not pin an absolute count.
        Assert.Equal(app.Documents, tests.Documents);
        Assert.Contains(result.WorkspaceWarnings, w => w.Contains("package-injected source document"));
        Assert.NotEmpty(service.FindMethods("Check.Go"));
        Assert.DoesNotContain(service.GetReviewTargets(), t => t.Method.File == Synthetic);
    }

    [Fact]
    public async Task OpensSolutionExcludingReviewedPackageInjectedSource()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        var solution = Fixture(additionalPackageInjected: Injected);
        var guard = new WorkspaceGuard([Workspace], true, false);
        using var service = new RoslynReviewService(guard, new NuGetSdkGeneratedDocumentPolicy(Packages));
        var result = await service.OpenAsync(solution);
        Assert.Equal(2, result.Projects.Count);
        Assert.Contains(result.WorkspaceWarnings, w => w.Contains("package-injected source document"));
        Assert.NotEmpty(service.FindMethods("Check.Go"));
        Assert.DoesNotContain(service.GetReviewTargets(), t => t.Method.File == Injected);
    }

    [Fact]
    public async Task ArbitraryLinkedSourceOutsideAllowlistStillFailsClosed()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        var outside = Path.Combine(_root, "outside", "Injected.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "public class Injected { public int Exploit() => 1; }");
        var solution = Fixture(outside);
        var guard = new WorkspaceGuard([Workspace], true, false);
        using var service = new RoslynReviewService(guard, new NuGetSdkGeneratedDocumentPolicy(Packages));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenAsync(solution));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
