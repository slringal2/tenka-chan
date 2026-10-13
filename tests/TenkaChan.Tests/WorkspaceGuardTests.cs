using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class WorkspaceGuardTests
{
    [Fact]
    public void AllowsKnownProjectButNotParentTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-guard-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "tenka-outside-" + Guid.NewGuid().ToString("N") + ".csproj");
        Directory.CreateDirectory(root);
        var inside = Path.Combine(root, "Sample.csproj");
        try
        {
            File.WriteAllText(inside, "<Project />");
            File.WriteAllText(outside, "<Project />");
            var guard = new WorkspaceGuard([root], trustMsBuild: true, allowDocker: false);
            Assert.Equal(inside, guard.RequireFile(inside, ".csproj"));
            Assert.Throws<UnauthorizedAccessException>(() => guard.RequireFile(outside, ".csproj"));
            Assert.Throws<UnauthorizedAccessException>(() =>
                guard.RequireProjectWithin(root, Path.GetRelativePath(root, outside)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }
}
