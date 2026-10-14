using System.Text;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativePathDialogTests
{
    [Theory]
    [InlineData("project", ".sln")]
    [InlineData("project", ".slnx")]
    [InlineData("project", ".csproj")]
    [InlineData("assembly", ".dll")]
    [InlineData("assembly", ".exe")]
    [InlineData("pdb", ".pdb")]
    [InlineData("source", ".cs")]
    [InlineData("profile", ".json")]
    [InlineData("trace", ".nettrace")]
    [InlineData("listing", ".txt")]
    public void SupportedKindsHaveExplicitExtensionAllowlist(string kind, string extension)
        => Assert.Contains(extension, NativePathDialog.FilterFor(kind).Extensions);

    [Fact]
    public void UnknownKindCannotSupplyArbitraryFilter()
        => Assert.Throws<ArgumentException>(() => NativePathDialog.FilterFor("*.anything"));

    [Fact]
    public void RequestDataNeverBecomesPowerShellCode()
    {
        var path = "C:\\日本語 & !\\$(Write-Output do-not-execute)";
        var start = NativePathDialog.CreateStartInfo(NativePathDialog.FilterFor("project"), path);
        Assert.False(start.UseShellExecute);
        Assert.Contains("-STA", start.ArgumentList);
        Assert.Equal(path, start.Environment["TENKA_PICK_INITIAL"]);
        Assert.DoesNotContain(path, Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList.Last())));
        Assert.DoesNotContain(path, start.ArgumentList);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void SelectedFileStillRequiresExistingAllowlistedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var guard = new WorkspaceGuard([root], false, false);
            var file = Path.Combine(root, "app.sln"); File.WriteAllText(file, "not evaluated");
            Assert.Equal(file, NativePathDialog.ValidateSelection(guard, "project", file));
            Assert.Throws<ArgumentException>(() => NativePathDialog.ValidateSelection(guard, "assembly", file));
            Assert.Throws<UnauthorizedAccessException>(() => NativePathDialog.ValidateSelection(guard, "project", Path.Combine(Path.GetDirectoryName(root)!, "outside.sln")));
            Assert.Throws<FileNotFoundException>(() => NativePathDialog.ValidateSelection(guard, "project", Path.Combine(root, "missing.sln")));
            Assert.Equal(root, NativePathDialog.ValidateSelection(guard, "folder", root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.sln")]
    public void RelativeOrEmptyResultsAreRejected(string path)
        => Assert.Throws<ArgumentException>(() => NativePathDialog.ValidateSelection(new WorkspaceGuard([], false, false), "project", path));
}
