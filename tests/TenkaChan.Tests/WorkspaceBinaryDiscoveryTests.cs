using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Synthetic PE fixtures are compiled but NEVER executed. Tests do not require a
/// fixture environment variable, Docker, or an installed .NET 8 target runtime.
/// </summary>
public sealed class WorkspaceBinaryDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tenka-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceGuard guard;
    public WorkspaceBinaryDiscoveryTests()
    {
        Directory.CreateDirectory(root);
        guard = new WorkspaceGuard([root], false, false);
    }

    [Fact]
    public void FindsReleaseLibraryWithoutRuntimeConfigOrExecutingIt()
    {
        var project = Project("Sample");
        var dll = Emit("Sample/bin/Release/net8.0/Sample.dll");
        var discovery = new WorkspaceBinaryDiscovery(guard);
        var choice = Assert.Single(discovery.Discover([Summary(project)]));
        Assert.Equal(dll, choice.AssemblyPath);
        Assert.False(choice.Runnable);
        Assert.Equal("net8.0", choice.TargetFramework);
        Assert.Equal(dll, discovery.RequireBuiltProject(project, "net8.0").AssemblyPath);
    }

    [Theory]
    [InlineData("OutputPath", "out/$(Configuration)/", "out/Release/net8.0", "Release")]
    [InlineData("OutDir", "shared/", "shared", "Unknown")]
    [InlineData("BaseOutputPath", "artifacts/", "artifacts/Release/net8.0", "Release")]
    [InlineData("OutputPath", "out\\$(Configuration)\\$(TargetFramework)\\", "out/Release/net8.0", "Release")]
    public void FindsExplicitCustomOutputs(string property, string value, string location, string configuration)
    {
        var project = Project("Custom", $"<{property}>{value}</{property}>");
        var dll = Emit($"Custom/{location}/Custom.dll");
        var discovery = new WorkspaceBinaryDiscovery(guard);
        var choice = Assert.Single(discovery.Discover([Summary(project)]));
        Assert.Equal(dll, choice.AssemblyPath);
        Assert.Equal(configuration, choice.Configuration);
        Assert.Equal(dll, discovery.RequireBuiltProject(project, "net8.0").AssemblyPath);
    }

    [Fact]
    public void UsesLiteralAssemblyNameOverride()
    {
        var project = Project("Sample", "<AssemblyName>Renamed</AssemblyName>");
        var dll = Emit("Sample/bin/Release/net8.0/Renamed.dll");
        Assert.Equal(dll, Assert.Single(new WorkspaceBinaryDiscovery(guard).Discover([Summary(project)])).AssemblyPath);
    }

    [Fact]
    public void BadProjectDoesNotHideHealthyProjects()
    {
        var project = Project("Healthy");
        Emit("Healthy/bin/Release/net8.0/Healthy.dll");
        var missing = Path.Combine(root, "Missing", "Missing.csproj");
        var choices = new WorkspaceBinaryDiscovery(guard).Discover([Summary(missing), Summary(project)]);
        Assert.Equal("Healthy", Assert.Single(choices).Project);
    }

    [Fact]
    public void DoesNotTreatFrameworkDirectoryNameAsProofOfBinaryFramework()
    {
        var project = Project("Mismatch");
        Emit("Mismatch/bin/Release/net8.0/Mismatch.dll", frameworkVersion: "9.0");
        Assert.Empty(new WorkspaceBinaryDiscovery(guard).Discover([Summary(project)]));
    }

    [Fact]
    public void PrefersReleaseOverDebug()
    {
        var project = Project("Sample");
        var release = Emit("Sample/bin/Release/net8.0/Sample.dll");
        Emit("Sample/bin/Debug/net8.0/Sample.dll");
        Assert.Equal(release, new WorkspaceBinaryDiscovery(guard).RequireBuiltProject(project, "net8.0").AssemblyPath);
    }

    [Fact]
    public void RejectsAmbiguousEquallyPreferredOutputs()
    {
        var project = Project("Sample", "<AssemblyName>Renamed</AssemblyName>");
        Emit("Sample/bin/Release/net8.0/Sample.dll");
        Emit("Sample/bin/Release/net8.0/Renamed.dll");
        Assert.Throws<InvalidOperationException>(() => new WorkspaceBinaryDiscovery(guard)
            .RequireBuiltProject(project, "net8.0"));
    }

    [Fact]
    public void DoesNotLaunchForeignRuntimeIdentifierOutput()
    {
        var project = Project("Sample");
        var foreign = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        Emit($"Sample/bin/Release/net8.0/{foreign}/Sample.dll");
        var discovery = new WorkspaceBinaryDiscovery(guard);
        Assert.Equal(foreign, Assert.Single(discovery.Discover([Summary(project)])).RuntimeIdentifier);
        Assert.Throws<FileNotFoundException>(() => discovery.RequireBuiltProject(project, "net8.0"));
    }

    [Fact]
    public void ForeignExecutableIsNotOfferedAsRunnable()
    {
        var project = Project("ForeignApp");
        var foreign = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var dll = Emit($"ForeignApp/bin/Release/net8.0/{foreign}/ForeignApp.dll", executable: true);
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}");
        Assert.False(Assert.Single(new WorkspaceBinaryDiscovery(guard).Discover([Summary(project)])).Runnable);
    }

    [Theory]
    [InlineData("{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}", true)]
    [InlineData("{\"runtimeOptions\":{\"tfm\":\"net9.0\"}}", false)]
    [InlineData("{\"runtimeOptions\":null}", false)]
    [InlineData("broken JSON", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    public void RunnableFlagRequiresMatchingReadableRuntimeConfig(string config, bool runnable)
    {
        var project = Project("App");
        var dll = Emit("App/bin/Release/net8.0/App.dll", executable: true);
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), config);
        Assert.Equal(runnable, Assert.Single(new WorkspaceBinaryDiscovery(guard)
            .Discover([Summary(project)])).Runnable);
    }

    [Fact]
    public void IgnoresUnresolvedPropertyExpressions()
    {
        var project = Project("Custom", "<OutputPath>$(UnknownProperty)/</OutputPath>");
        Emit("Custom/$(UnknownProperty)/net8.0/Custom.dll");
        Assert.Empty(new WorkspaceBinaryDiscovery(guard).Discover([Summary(project)]));
    }

    [Fact]
    public void OutsideAllowlistIsNeverDiscovered()
    {
        var project = Project("Sample");
        Emit("Sample/bin/Release/net8.0/Sample.dll");
        var unrelated = Path.Combine(root, "Unrelated");
        Directory.CreateDirectory(unrelated);
        var restricted = new WorkspaceGuard([unrelated], false, false);
        Assert.Empty(new WorkspaceBinaryDiscovery(restricted).Discover([Summary(project)]));
        Assert.Throws<UnauthorizedAccessException>(() => new WorkspaceBinaryDiscovery(restricted)
            .RequireBuiltProject(project, "net8.0"));
    }

    [Fact]
    public void DoesNotDescendIntoReferenceOrPublishDirectories()
    {
        var project = Project("Sample");
        Emit("Sample/bin/Release/net8.0/ref/Sample.dll");
        Emit("Sample/bin/Release/net8.0/publish/Sample.dll");
        Assert.Empty(new WorkspaceBinaryDiscovery(guard).Discover([Summary(project)]));
    }

    [Fact]
    public void CorruptDllDoesNotHideAnotherConfiguration()
    {
        var project = Project("Sample");
        var bad = Path.Combine(root, "Sample/bin/Release/net8.0/Sample.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "not a PE image");
        var good = Emit("Sample/bin/Debug/net8.0/Sample.dll");
        Assert.Equal(good, Assert.Single(new WorkspaceBinaryDiscovery(guard)
            .Discover([Summary(project)])).AssemblyPath);
    }

    private string Project(string name, string properties = "")
    {
        var path = Path.Combine(root, name, name + ".csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net8.0</TargetFramework>" + properties + "</PropertyGroup></Project>");
        return path;
    }
    private static ProjectSummary Summary(string project) => new(Path.GetFileNameWithoutExtension(project), project, "net8.0", 0);

    private string Emit(string relative, string frameworkVersion = "8.0", bool executable = false)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var source = "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v" +
            frameworkVersion + "\")] public static class Probe { public static void Main() {} public static int Value() => 42; }";
        var references = new[] { typeof(object).Assembly.Location, typeof(TargetFrameworkAttribute).Assembly.Location }
            .Distinct().Select(x => MetadataReference.CreateFromFile(x));
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
