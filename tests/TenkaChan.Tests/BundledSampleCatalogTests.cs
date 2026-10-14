using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;
public sealed class BundledSampleCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tenka-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string sample;
    public BundledSampleCatalogTests()
    {
        sample = Path.Combine(root, "samples", "Tenka.Sample.GameLab");
        Directory.CreateDirectory(Path.Combine(root, "src", "TenkaChan.Web"));
        File.WriteAllText(Path.Combine(root, "TenkaChan.slnx"), "not evaluated");
        File.WriteAllText(Path.Combine(root, "src", "TenkaChan.Web", "TenkaChan.Web.csproj"), "not evaluated");
        Directory.CreateDirectory(Path.Combine(sample, "Tests"));
        Directory.CreateDirectory(Path.Combine(sample, "Benchmarks"));
        Directory.CreateDirectory(Path.Combine(sample, "profiles"));
        File.WriteAllText(Path.Combine(sample, "Tenka.Sample.GameLab.csproj"), "not evaluated");
        File.WriteAllText(Path.Combine(sample, "Tests", "Tenka.Sample.GameLab.Tests.csproj"), "not evaluated");
        File.WriteAllText(Path.Combine(sample, "Benchmarks", "Tenka.Sample.GameLab.Benchmarks.csproj"), "not evaluated");
    }
    private BundledSampleCatalog Catalog(params string[] roots) => new(new WorkspaceGuard(roots, false, false));
    [Fact] public void FindsPathsWithoutMsBuildTrustOrExecution()
    {
        var result = Catalog(root).FindGameLab(Path.Combine(root, "src", "TenkaChan.Web"));
        Assert.True(result.Available); Assert.Equal(sample, result.SampleDirectory);
        Assert.False(result.AssemblyExists); Assert.EndsWith(".csproj", result.ProjectPath);
    }
    [Fact] public void RejectsFilesOutsideAllowlist()
    {
        var result = Catalog(Path.Combine(root, "src")).FindGameLab(root);
        Assert.False(result.Available); Assert.Null(result.ProjectPath);
    }
    [Fact] public void MissingProjectIsNotReady()
    {
        File.Delete(Path.Combine(sample, "Tenka.Sample.GameLab.csproj"));
        Assert.False(Catalog(root).FindGameLab(root).Available);
    }
    [Fact] public void ExistingDllIsOnlyAnExistenceHint()
    {
        var folder = Path.Combine(sample, "bin", "Release", "net8.0");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Tenka.Sample.GameLab.dll"), "not loaded or executed");
        Assert.True(Catalog(root).FindGameLab(root).AssemblyExists);
    }
    [Theory][InlineData("")][InlineData("relative/path")]
    public void RejectsRelativeContentRoot(string directory) => Assert.False(Catalog(root).FindGameLab(directory).Available);
    [Fact] public void UnknownCheckoutHasNoSample()
    {
        File.Delete(Path.Combine(root, "TenkaChan.slnx"));
        Assert.False(Catalog(root).FindGameLab(root).Available);
    }
    public void Dispose() => Directory.Delete(root, true);
}
