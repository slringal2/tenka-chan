using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>Synthetic PE metadata only. These probes are never loaded as executable code.</summary>
public sealed class StandardLibraryDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-standard-" + Guid.NewGuid().ToString("N"));
    private (WorkspaceBinaryDiscovery Discovery, ProjectSummary Project, string Dll) Fixture(string tfm,
        string attribute, bool executable = false, bool custom = false)
    {
        Directory.CreateDirectory(_root);
        var project = Path.Combine(_root, "Library.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>" + tfm +
            "</TargetFramework>" + (custom ? "<OutDir>out/</OutDir>" : "") + "</PropertyGroup></Project>");
        var folder = custom ? Path.Combine(_root, "out") : Path.Combine(_root, "bin", "Release", tfm);
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "Library.dll");
        var source = "[assembly:System.Runtime.Versioning.TargetFramework(\"" + attribute +
            "\")] public static class Probe { public static void Main() {} public static int Value()=>42; }";
        var refs = new[] { typeof(object).Assembly.Location, typeof(TargetFrameworkAttribute).Assembly.Location }
            .Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Library", [CSharpSyntaxTree.ParseText(source)], refs,
            new CSharpCompilationOptions(executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(dll);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return (new(new WorkspaceGuard([_root], false, false)), new("Library", project, tfm, 0), dll);
    }
    [Theory] [InlineData("2.0")] [InlineData("2.1")]
    public void FindsStandardLibrariesAsReadOnlyIlCandidates(string version)
    {
        var f = Fixture("netstandard" + version, ".NETStandard,Version=v" + version);
        var choice = Assert.Single(f.Discovery.Discover([f.Project]));
        Assert.False(choice.Runnable); Assert.Equal(f.Dll, choice.AssemblyPath);
        Assert.Contains("inspection only", choice.Explanation);
        var inspector = new BinarySourceInspector(new WorkspaceGuard([_root], false, false));
        var method = Assert.Single(inspector.Methods(choice.AssemblyPath, "Value").Methods);
        Assert.NotEmpty(inspector.Inspect(choice.AssemblyPath, method.MetadataToken).Instructions);
    }
    [Fact] public void RuntimeConfigAndEntryPointNeverMakeNetStandardDirectlyRunnable()
    {
        var f = Fixture("netstandard2.0", ".NETStandard,Version=v2.0", executable: true);
        File.WriteAllText(Path.ChangeExtension(f.Dll, ".runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"netstandard2.0\"}}");
        Assert.False(Assert.Single(f.Discovery.Discover([f.Project])).Runnable);
        Assert.Throws<ArgumentException>(() => f.Discovery.RequireBuiltProject(f.Project.Path!, "netstandard2.0"));
    }
    [Fact] public void CustomOutputHintsCanDiscoverLibraryMetadata()
    {
        var f = Fixture("netstandard2.0", ".NETStandard,Version=v2.0", custom: true);
        Assert.Equal(f.Dll, Assert.Single(f.Discovery.Discover([f.Project])).AssemblyPath);
    }
    [Theory]
    [InlineData("netstandard2.0", ".NETCoreApp,Version=v8.0")]
    [InlineData("net8.0", ".NETStandard,Version=v2.0")]
    [InlineData("netstandard1.6", ".NETStandard,Version=v1.6")]
    [InlineData("netstandard2.0", ".NETStandardApplication,Version=v2.0")]
    public void MismatchedOrUnsupportedFrameworksAreNotDiscovered(string tfm, string attribute)
    {
        var f = Fixture(tfm, attribute); Assert.Empty(f.Discovery.Discover([f.Project]));
    }
    [Fact] public void ModernRuntimeDiscoveryStillRequiresMatchingConfig()
    {
        var f = Fixture("net8.0", ".NETCoreApp,Version=v8.0", executable: true);
        Assert.False(Assert.Single(f.Discovery.Discover([f.Project])).Runnable);
        File.WriteAllText(Path.ChangeExtension(f.Dll, ".runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}");
        Assert.True(Assert.Single(f.Discovery.Discover([f.Project])).Runnable);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
