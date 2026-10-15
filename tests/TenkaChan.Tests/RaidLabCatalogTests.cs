using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class RaidLabCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-raid-" + Guid.NewGuid().ToString("N"));
    private string Sample => Path.Combine(_root, "samples", "Tenka.Sample.RaidLab");
    public RaidLabCatalogTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "TenkaChan.Web"));
        File.WriteAllText(Path.Combine(_root, "TenkaChan.slnx"), "sentinel only");
        File.WriteAllText(Path.Combine(_root, "src", "TenkaChan.Web", "TenkaChan.Web.csproj"), "sentinel only");
        Directory.CreateDirectory(Sample);
        File.WriteAllText(Path.Combine(Sample, "Tenka.Sample.RaidLab.sln"), "not evaluated");
        foreach (var name in new[] { "Domain", "Infrastructure", "Application", "Host", "Tests", "Benchmarks" })
        {
            Directory.CreateDirectory(Path.Combine(Sample, name));
            File.WriteAllText(Path.Combine(Sample, name, "Tenka.Sample.RaidLab." + name + ".csproj"), "not evaluated");
        }
    }
    private RaidLabSampleCatalog Catalog(string? root = null) => new(new WorkspaceGuard([root ?? _root], false, false));
    [Fact] public void ResolvesSixProjectsWithoutBuildTrust()
    {
        var r = Catalog().Find(Path.Combine(_root, "src", "TenkaChan.Web"));
        Assert.True(r.Available); Assert.Equal(6, r.ProjectCount); Assert.EndsWith(".sln", r.ProjectPath);
        Assert.EndsWith("Tenka.Sample.RaidLab.Host.csproj", r.EntryProjectPath); Assert.False(r.AssemblyExists);
        Assert.False(Directory.Exists(Path.Combine(Sample, "artifacts")));
    }
    [Fact] public void MissingCompanionIsNotComplete()
    {
        File.Delete(Path.Combine(Sample, "Tests", "Tenka.Sample.RaidLab.Tests.csproj"));
        Assert.False(Catalog().Find(_root).Available);
    }
    [Fact] public void AllowlistIsNotExpanded() => Assert.False(Catalog(Path.Combine(_root, "src")).Find(_root).Available);
    [Fact] public void UnknownCheckoutIsRejected()
    {
        File.Delete(Path.Combine(_root, "TenkaChan.slnx")); Assert.False(Catalog().Find(_root).Available);
    }
    [Fact] public void BinaryPresenceIsOnlyAHint()
    {
        var dir = Path.Combine(Sample, "Host", "bin", "Release", "net8.0"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Tenka.Sample.RaidLab.Host.dll"), "never loaded");
        Assert.True(Catalog().Find(_root).AssemblyExists);
    }
    [Theory][InlineData("")][InlineData("relative")]
    public void RootMustBeAbsolute(string path) => Assert.False(Catalog().Find(path).Available);
    public void Dispose() => Directory.Delete(_root, true);
}
