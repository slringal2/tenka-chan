using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewPriorityTestsForMergeProvenance
{
    [Theory]
    [InlineData("info")]
    [InlineData("warning")]
    public void StrongerStaticRepresentativeRetainsOriginalDiagnosticMetadata(string severity)
    {
        var finding = new DeterministicFinding("diagnostic-fp", "PERF1001", "Materialization", "allocation",
            severity, "candidate", "builtin", "App", "/repo/A.cs", "A.cs", 5, 9, "method", "pattern", "x.ToArray()", "measure");
        var diagnostics = new DeterministicAnalysisReport(new("snapshot", "v", 1, 1, 1,
            0, 0, 0, 1, 1, 0, 0, false, false, false, []), [finding], []);
        var method = new MethodSummary("method", "App", "App.A.Run()", "/repo/A.cs", 1);
        var site = new HotspotSite("site-fp", "method", "linq_materialization", "/repo/A.cs", 5, 9, 4, 80,
            "x.ToArray()", "nested repetition", "measure");
        var hotspots = new StaticHotspotReport("snapshot", "static_candidate", 1, 1, 1, 1, false, false,
            [new(method, 80, 80, 4, 1, 1, 1, 0, 0, 0, 0, 0, false, [])], [site], [], []);
        var result = ReviewPriorityEngine.Build(diagnostics, hotspots);
        var merged = Assert.Single(result.Items);
        Assert.Equal("P2", merged.Priority);
        Assert.Equal(severity, merged.OriginalSeverity);
        Assert.Contains("diagnostic-fp", merged.SourceFingerprints);
        Assert.Contains("site-fp", merged.SourceFingerprints);
        Assert.Equal(80, merged.StaticScore);
        Assert.False(merged.DefectConfirmed);
    }
}
