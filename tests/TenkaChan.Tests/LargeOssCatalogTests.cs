using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class LargeOssCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-oss-" + Guid.NewGuid().ToString("N"));
    private void FileAt(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }
    private void Fixture()
    {
        FileAt("TenkaChan.slnx"); FileAt("src/TenkaChan.Web/TenkaChan.Web.csproj");
        FileAt("samples/External/ILSpy-10.0/ILSpy.Analysis.slnx");
        FileAt("samples/External/ILSpy-10.0/source/ILSpy.sln");
        foreach (var name in new[] { "ICSharpCode.Decompiler", "ICSharpCode.ILSpyX", "ICSharpCode.ILSpyCmd" })
            FileAt($"samples/External/ILSpy-10.0/source/{name}/{name}.csproj");
    }
    private LargeOssSampleCatalog Catalog() => new(new WorkspaceGuard([_root], true, false));
    [Fact] public void CompleteCheckoutIsSelectedWithoutBuilding()
    {
        Fixture(); var result = Catalog().Find(Path.Combine(_root, "src/TenkaChan.Web"));
        Assert.True(result.Available); Assert.False(result.AssemblyExists);
        Assert.EndsWith("ILSpy.Analysis.slnx", result.ProjectPath); Assert.Equal(3, result.ProjectCount);
        Assert.Contains("tenka_measure_target", result.MeasurementGuidance);
        Assert.Contains("UNEXECUTED", result.MeasurementGuidance);
        Assert.False(Directory.Exists(Path.Combine(_root, "samples/External/ILSpy-10.0/source/ICSharpCode.ILSpyCmd/bin")));
    }
    [Fact] public void MissingCloneOffersTheBatchFileWithoutCreatingDirectories()
    {
        FileAt("TenkaChan.slnx"); FileAt("src/TenkaChan.Web/TenkaChan.Web.csproj");
        var result = Catalog().Find(_root);
        Assert.False(result.Available); Assert.Contains("run-large-oss.bat", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "samples/External")));
    }
    [Fact] public void PartialCheckoutIsNotReportedAsReady()
    {
        Fixture(); File.Delete(Path.Combine(_root, "samples/External/ILSpy-10.0/source/ICSharpCode.ILSpyX/ICSharpCode.ILSpyX.csproj"));
        Assert.False(Catalog().Find(_root).Available);
    }
    [Fact] public void AllowlistIsNotExpandedByChoosingSample()
    {
        Fixture();
        var guard = new WorkspaceGuard([Path.Combine(_root, "src")], true, false);
        Assert.False(new LargeOssSampleCatalog(guard).Find(_root).Available);
    }
    [Fact] public void RelativeStartPathsAreRejected() => Assert.False(Catalog().Find("relative/path").Available);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
