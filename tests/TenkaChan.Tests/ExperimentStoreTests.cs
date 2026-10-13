using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ExperimentStoreTests
{
    [Fact]
    public void GeneratedTestIsDetachedAndHasProjectReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Sample.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], true, false));
            var receipt = store.Create(root, "Sample.csproj", "test",
                "using Xunit; public sealed class GeneratedTests { [Fact] public void Smoke() => Assert.True(true); }");
            Assert.True(File.Exists(Path.Combine(receipt.Directory, "GeneratedTests.csproj")));
            Assert.Contains("../repo/Sample.csproj",
                File.ReadAllText(Path.Combine(receipt.Directory, "GeneratedTests.csproj")));
            Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", File.ReadAllText(project));
            Assert.Equal("test", store.Read(receipt.Id).Manifest.Kind);
            Assert.Equal("net8.0", store.Read(receipt.Id).Manifest.TargetFramework);
            Directory.Delete(receipt.Directory, recursive: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsInvalidGeneratedSyntax()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Sample.csproj"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], true, false));
            Assert.Throws<ArgumentException>(() =>
                store.Create(root, "Sample.csproj", "test", "public class {"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
