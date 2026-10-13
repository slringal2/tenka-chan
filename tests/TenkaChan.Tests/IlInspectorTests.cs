using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class IlInspectorTests
{
    [Fact]
    public void ReadsILWithoutAssemblyLoad()
    {
        var assembly = typeof(PerformanceStatistics).Assembly.Location;
        var guard = new WorkspaceGuard([Path.GetDirectoryName(assembly)!],
            trustMsBuild: false, allowDocker: false);
        var result = new IlInspector(guard).Inspect(assembly,
            "TenkaChan.Mcp.Analysis.PerformanceStatistics", "Compare");
        Assert.NotEmpty(result.Instructions);
        Assert.Contains(result.Instructions, i => i.OpCode == "ret");
    }

    [Fact]
    public void RejectsAssemblyOutsideAllowlist()
    {
        var guard = new WorkspaceGuard([Path.GetTempPath()],
            trustMsBuild: false, allowDocker: false);
        var assembly = typeof(PerformanceStatistics).Assembly.Location;
        // If CI uses a temp-directory SDK build, use a guaranteed disjoint guard root.
        var disjoint = Path.Combine(Path.GetPathRoot(assembly)!, "this-path-does-not-exist");
        guard = new WorkspaceGuard([disjoint], false, false);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new IlInspector(guard).Inspect(assembly, "A", "B"));
    }
}
