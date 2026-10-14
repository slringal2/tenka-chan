using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewPriorityTests
{
    private static readonly MethodSummary Method = new("m", "App", "App.C.Run()", "/repo/Code.cs", 10);
    private static DeterministicFinding Finding(string rule = "PERF1001", string severity = "info", string level = "candidate", int column = 3) =>
        new("fp-" + rule, rule, rule, "test", severity, level, "builtin", "App", "/repo/Code.cs", "Code.cs", 12, column, "m", "candidate", "code", "verify");
    private static DeterministicAnalysisReport Diagnostics(params DeterministicFinding[] findings) => new(
        new("s", "v", 1, 1, 1, findings.Count(f => f.EvidenceLevel == "compiler" && f.Severity == "error"), 0, 0,
            findings.Length, findings.Length, 0, 0, false, false, false, []), findings, []);
    private static HotspotSite Site(double weight = 4, string category = "linq_materialization", int column = 3) =>
        new("site", "m", category, "/repo/Code.cs", 12, column, 1, weight, "code", "pattern", "measure");
    private static StaticHotspotReport Hotspots(params HotspotSite[] sites) => new("s", "static_candidate", 1, sites.Length, 1, sites.Length, false, false,
        [new(Method, 1000, 1000, 1, 0, 1, 0, 0, 0, 0, 0, 0, false, [])], sites, [], []);
    private static SampledHotspotReport Profile(double percent = 60, int count = 100, string unit = "milliseconds", string status = "matched_exact_qualified_name") =>
        new("/trace.json", "test", unit, 100, 100, 1, 1, 0,
            [new("App.C.Run()", count, 100, percent, 100, percent, "m", status)], []);

    [Theory]
    [InlineData("error", "compiler", "P1")]
    [InlineData("error", "analyzer", "P1")]
    [InlineData("error", "candidate", "P3")]
    [InlineData("warning", "candidate", "P2")]
    [InlineData("info", "semantic_fact", "P2")]
    [InlineData("info", "candidate", "P3")]
    public void SeverityAndEvidenceAreNotDefectCertainty(string severity, string evidence, string expected)
    {
        var report = ReviewPriorityEngine.Build(Diagnostics(Finding(severity: severity, level: evidence)), Hotspots());
        var item = Assert.Single(report.Items);
        Assert.Equal(expected, item.Priority);
        Assert.False(item.DefectConfirmed);
        Assert.Equal(evidence, item.EvidenceLevel);
        Assert.Equal(0, report.PriorityCounts["P0"]);
    }

    [Theory]
    [InlineData(10, 20, "P1")]
    [InlineData(9.99, 20, "P2")]
    [InlineData(80, 19, "P2")]
    [InlineData(0, 100, "P2")]
    public void ProminentSelfNeedsObservationGateNotJustAnAncestorCost(double self, int observations, string expected)
    {
        var report = ReviewPriorityEngine.Build(Diagnostics(), Hotspots(), Profile(self, observations));
        Assert.Equal(expected, Assert.Single(report.Items).Priority);
        Assert.All(report.Items, x => Assert.False(x.DefectConfirmed));
    }

    [Fact]
    public void StaticScoresNeverBecomeMeasuredP1OrP0()
    {
        var report = ReviewPriorityEngine.Build(Diagnostics(), Hotspots(Site(10000)));
        Assert.Equal("P2", Assert.Single(report.Items).Priority);
        Assert.Null(report.Items[0].SelfWeight);
        Assert.Equal("no_profile", report.ProfileStatus);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("milliseconds")]
    [InlineData("none")]
    public void PreservesUnitAndDoesNotPromoteEverySourceWarningInAHotMethod(string unit)
    {
        var report = ReviewPriorityEngine.Build(Diagnostics(Finding()), Hotspots(Site()), Profile(unit: unit));
        Assert.Equal("observed_frame", report.Items[0].Kind);
        Assert.Equal("P1", report.Items[0].Priority);
        Assert.Equal(unit, report.Items[0].Unit);
        Assert.Equal("P3", report.Items[1].Priority);
        Assert.Null(report.Items[1].SelfPercent);
        Assert.Equal("method_name_only_not_causal_site", report.Items[0].ProfileAssociation);
    }

    [Fact]
    public void AmbiguousFrameStaysImportantWithoutAttachingSource()
    {
        var report = ReviewPriorityEngine.Build(Diagnostics(), Hotspots(), Profile(status: "ambiguous_overload_or_type"));
        var item = Assert.Single(report.Items);
        Assert.Equal("P1", item.Priority);
        Assert.Null(item.MethodId); Assert.Null(item.File);
    }

    [Fact]
    public void EquivalentSourceRulesDeduplicateButOtherRulesAndCoordinatesDoNot()
    {
        var report = ReviewPriorityEngine.Build(Diagnostics(Finding(), Finding("CORRECT1001")), Hotspots(Site(), Site(column: 4)));
        Assert.Equal(4, report.InputCount); Assert.Equal(3, report.DistinctCount); Assert.Equal(1, report.DuplicateCount);
        var joined = Assert.Single(report.Items, x => x.RuleIds.Contains("PERF1001"));
        Assert.Contains("HOTSPOT:linq_materialization", joined.RuleIds);
        Assert.Equal(2, joined.SourceFingerprints.Count);
    }

    [Fact]
    public void DuplicateProfileLabelsAreNotSummed()
    {
        var profile = Profile();
        profile = profile with { Frames = [profile.Frames[0], profile.Frames[0]], TotalFrames = 2 };
        var report = ReviewPriorityEngine.Build(Diagnostics(), Hotspots(), profile);
        Assert.Equal(60, Assert.Single(report.Items).SelfWeight);
        Assert.Equal(1, report.DuplicateCount);
    }

    [Fact]
    public void CountsRemainVisibleBeforeReasonedSuppressionFilteringAndLimits()
    {
        var diagnostics = Diagnostics(Finding(), Finding("ASYNC1001", "warning"));
        var all = ReviewPriorityEngine.Build(diagnostics, Hotspots(), Profile());
        var id = all.Items[0].Id;
        var options = new ReviewPriorityOptions(MaxItems: 1, Suppressions: [new(id, "known fixture workload")]);
        var report = ReviewPriorityEngine.Build(diagnostics, Hotspots(), Profile(), options);
        Assert.Equal(3, report.DistinctCount); Assert.Equal(1, report.SuppressedCount);
        Assert.Equal(1, report.PriorityCounts["P1"]); Assert.Equal(2, report.MatchingCount);
        Assert.True(report.OutputTruncated); Assert.Single(report.Items);
        var visible = ReviewPriorityEngine.Build(diagnostics, Hotspots(), Profile(), options with { IncludeSuppressed = true });
        Assert.True(visible.Items[0].Suppressed);
        Assert.Equal("known fixture workload", visible.Items[0].SuppressionReason);
        var filtered = ReviewPriorityEngine.Build(diagnostics, Hotspots(), Profile(), new(PriorityFilter: "P0"));
        Assert.Empty(filtered.Items); Assert.Equal(3, filtered.DistinctCount);
    }

    [Fact]
    public void InvalidPoliciesAndUnreasonedSuppressionsAreRejected()
    {
        foreach (var options in new ReviewPriorityOptions[]
        {
            new(HotSharePercent: double.NaN), new(HotSharePercent: 0), new(MinimumObservations: 0),
            new(MaxItems: 1001), new(PriorityFilter: "critical"), new(EvidenceFilter: "confirmed"),
            new(Suppressions: [new("id", " ")]), new(Suppressions: [new("id", "one"), new("id", "two")])
        }) Assert.Throws<ArgumentException>(() => ReviewPriorityEngine.Validate(options));
    }

    [Fact]
    public void RefusesMixedSnapshotsAndInvalidWeights()
    {
        Assert.Throws<InvalidOperationException>(() => ReviewPriorityEngine.Build(Diagnostics(), Hotspots() with { SnapshotId = "other" }));
        Assert.Throws<InvalidDataException>(() => ReviewPriorityEngine.Build(Diagnostics(), Hotspots(), Profile(double.NaN)));
    }

    [Fact]
    public void SourceLimitsAndRepeatabilityAreExplicit()
    {
        var diagnostics = Diagnostics(Finding());
        var limited = Hotspots(Site()) with { SitesTruncated = true };
        var first = ReviewPriorityEngine.Build(diagnostics, limited);
        Assert.True(first.SourceTruncated);
        var second = ReviewPriorityEngine.Build(diagnostics, limited);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void LoopSyntaxAloneDoesNotFloodTheFindingQueue() =>
        Assert.Empty(ReviewPriorityEngine.Build(Diagnostics(), Hotspots(Site(category: "loop_body_candidate"))).Items);
}
