using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ProfileGuidedTriageTests
{
    private static readonly ProfileMethodIdentity[] Identities =
        [new("hot", "App.Work.Hot"), new("cold", "App.Work.Cold")];
    private const string Trace = """
        {"shared":{"frames":[
          {"name":"Runtime.Root()"}, {"name":"App.Work.Hot(int)"},
          {"name":"Module!App.Work.Hot(int)"}, {"name":"App.Work.Cold()"},
          {"name":"Runtime.External()"}]},
         "profiles":[{"type":"sampled","name":"workload","unit":"milliseconds",
           "startValue":0,"endValue":10,
           "samples":[[0,1,2],[0,3],[4],[]],"weights":[6,2,1,1]}]}
        """;

    internal static StaticHotspotReport StaticReport() => new("snapshot", "static_candidate", 3, 0, 3, 0,
        false, false,
        [Rank("hot", 0), Rank("cold", 1000), Rank("unseen", 9999)], [], [], []);
    private static HotspotMethodRanking Rank(string id, double score) => new(
        new MethodSummary(id, "App", "App.Work." + id, "Work.cs", 1),
        score, score, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, []);

    [Fact]
    public void AllFrameAttributionSurvivesDisplayLimitAndDeduplicatesMethodAliases()
    {
        var report = SpeedscopeHotspotParser.Parse(Trace, "trace.json", Identities, limit: 1);
        Assert.Single(report.Frames);
        Assert.True(report.FramesTruncated);
        var hot = Assert.Single(report.MethodCosts, m => m.MethodId == "hot");
        Assert.Equal(6, hot.ExclusiveWeight);
        Assert.Equal(6, hot.InclusiveWeight); // NOT 12 for two names of the same method.
        Assert.Equal(1, hot.ObservationCount);
        Assert.Equal(2, hot.ContributingFrames);
        Assert.Equal(2, Assert.Single(report.MethodCosts, m => m.MethodId == "cold").ExclusiveWeight);
        Assert.Equal(1, report.UnassignedWeight);
        Assert.Equal(1, report.UnmappedExclusiveWeight);
        Assert.Equal(10, report.TotalWeight);
    }

    [Fact]
    public void MeasuredWorkOutranksLargeStaticScoresAndUnseenDoesNotBecomeZero()
    {
        var profile = SpeedscopeHotspotParser.Parse(Trace, "trace", Identities, limit: 1);
        var triage = PerformanceTriageEngine.Build(StaticReport(), profile);
        Assert.Equal(["hot", "cold", "unseen"], triage.Entries.Select(e => e.MethodId));
        Assert.Equal(60, triage.Entries[0].ExclusivePercent);
        Assert.Null(triage.Entries[2].ExclusiveWeight);
        Assert.Equal("not_observed_not_proven_cold", triage.Entries[2].ObservationStatus);
        Assert.Equal("profile_guided", triage.Status);
        Assert.Equal("milliseconds", triage.Unit);
        Assert.True(triage.ProfileFramesTruncated);
    }

    [Fact]
    public void SelfAndInclusiveRankingDifferWithoutAddingOverlappingInclusiveWeight()
    {
        var profile = SpeedscopeHotspotParser.Parse(Trace.Replace("[0,3]", "[1,3]"), "trace", Identities);
        var hot = Assert.Single(profile.MethodCosts, m => m.MethodId == "hot");
        Assert.Equal(8, hot.InclusiveWeight);
        Assert.Equal(6, hot.ExclusiveWeight);
        Assert.Equal(80, PerformanceTriageEngine.Build(StaticReport(), profile, "inclusive").Entries[0].InclusivePercent);
        Assert.Equal("inclusive_profile_weight", PerformanceTriageEngine.Build(StaticReport(), profile, "inclusive").RankingBasis);
        Assert.Throws<ArgumentException>(() => PerformanceTriageEngine.Build(StaticReport(), profile, "cpu"));
    }

    [Fact]
    public void EventedRecursiveAliasesHaveOneInclusiveMethodContributionPerInterval()
    {
        const string trace = """
            {"shared":{"frames":[{"name":"App.Work.Hot()"},{"name":"module!App.Work.Hot()"}]},
             "profiles":[{"type":"evented","name":"evented","unit":"seconds","startValue":0,"endValue":5,
             "events":[{"type":"O","at":0,"frame":0},{"type":"O","at":1,"frame":1},
              {"type":"C","at":4,"frame":1},{"type":"C","at":5,"frame":0}]}]}
            """;
        var profile = SpeedscopeHotspotParser.Parse(trace, "evented", Identities);
        var cost = Assert.Single(profile.MethodCosts);
        Assert.Equal(5, cost.InclusiveWeight);
        Assert.Equal(5, cost.ExclusiveWeight);
        Assert.Equal(3, cost.ObservationCount);
        Assert.Equal("weighted_intervals", profile.ObservationCountKind);
        Assert.Equal("evented", profile.ProfileType);
    }

    [Theory]
    [InlineData("seconds")]
    [InlineData("bytes")]
    [InlineData("none")]
    public void PreservesWeightUnitsWithoutRoundingSmallMeasurementsToZero(string unit)
    {
        var trace = Trace.Replace("milliseconds", unit).Replace("[6,2,1,1]", "[6e-10,2e-10,1e-10,1e-10]");
        var profile = SpeedscopeHotspotParser.Parse(trace, "trace", Identities);
        Assert.True(profile.TotalWeight > 0);
        var triage = PerformanceTriageEngine.Build(StaticReport(), profile);
        Assert.Equal(unit, triage.Unit);
        Assert.InRange(triage.Entries[0].ExclusivePercent!.Value, 59.999, 60.001);
    }

    [Fact]
    public void UnmappedOverloadsRemainVisibleAndDoNotAcquireInventedMethodCost()
    {
        var profile = SpeedscopeHotspotParser.Parse(Trace, "trace",
            [new("one", "App.Work.Hot"), new("two", "App.Work.Hot")]);
        Assert.Empty(profile.MethodCosts);
        Assert.Equal(9, profile.UnmappedExclusiveWeight);
        Assert.Equal(2, profile.AmbiguousFrames);
        var triage = PerformanceTriageEngine.Build(StaticReport(), profile);
        Assert.NotEmpty(triage.UnmatchedFrames);
        Assert.All(triage.Entries, e => Assert.Null(e.ExclusiveWeight));
    }

    [Fact]
    public void FileIdentityHashesActualBytesIncludingBomAndProfileSelectionIsExplicit()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(file, Trace, new UTF8Encoding(true));
            var result = SpeedscopeHotspotParser.Read(file, Identities);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), result.SourceSha256);
            Assert.Equal("file_bytes_sha256", result.SourceIdentityKind);
            Assert.Equal(0, result.ProfileIndex);
            Assert.Throws<ArgumentOutOfRangeException>(() => SpeedscopeHotspotParser.Read(file, Identities, 1));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void StaticOnlyAndDisplayCapsAreReportedHonestly()
    {
        var result = PerformanceTriageEngine.Build(StaticReport(), null, limit: 1);
        Assert.Equal("static_only", result.Status);
        Assert.Equal("unseen", Assert.Single(result.Entries).MethodId);
        Assert.Null(result.TotalWeight);
        Assert.True(result.CandidatesTruncated);
    }

    [Fact]
    public void CorruptOrLegacyAttributionCannotSilentlyJoinEvidence()
    {
        var profile = SpeedscopeHotspotParser.Parse(Trace, "trace", Identities);
        Assert.Throws<InvalidOperationException>(() => PerformanceTriageEngine.Build(StaticReport(), profile with { CompleteMethodAttribution = false }));
        Assert.Throws<InvalidDataException>(() => PerformanceTriageEngine.Build(StaticReport(), profile with { TotalWeight = 2 }));
        Assert.Throws<InvalidDataException>(() => PerformanceTriageEngine.Build(StaticReport(), profile with { UnmappedExclusiveWeight = double.NaN }));
    }

    [Theory]
    [InlineData("int App.Work::Hot(int)", "App.Work.Hot(int)", true)]
    [InlineData("int App.Work::Hot(int[])", "App.Work.Hot(int[])", true)]
    [InlineData("int App.Work::Other(int)", "App.Work.Hot(int)", false)]
    [InlineData("int App.Work::Hot(long)", "App.Work.Hot(int)", false)]
    public void BinaryMethodSelectionDoesNotGuessADifferentMethodOrOverload(string binary, string source, bool matches)
    {
        if (matches) PerformanceInvestigationService.RequireSameSimpleSignature(binary, source);
        else Assert.Throws<InvalidOperationException>(() => PerformanceInvestigationService.RequireSameSimpleSignature(binary, source));
    }
}
