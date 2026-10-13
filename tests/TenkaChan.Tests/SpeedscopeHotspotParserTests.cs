using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class SpeedscopeHotspotParserTests
{
    private const string Source = """
        {
          "$schema":"https://www.speedscope.app/file-format-schema.json",
          "shared":{"frames":[
            {"name":"Main.App.Run()"},
            {"name":"Sample.Engine.Hot(System.Int32)"},
            {"name":"Sample.Engine.Cold()"},
            {"name":"Another.Library.Foo()"}
          ]},
          "profiles":[{
            "type":"sampled","name":"workload","unit":"milliseconds",
            "startValue":0,"endValue":12,
            "samples":[[0,1],[0,1],[0,2],[0,1,1]],
            "weights":[1,2,4,5]
          }]
        }
        """;

    private static readonly ProfileMethodIdentity[] Methods =
    [
        new("hot", "Sample.Engine.Hot"),
        new("cold", "Sample.Engine.Cold")
    ];

    [Fact]
    public void UsesExclusiveAndDeduplicatedInclusiveWeight()
    {
        var report = SpeedscopeHotspotParser.Parse(Source, "test.speedscope.json", Methods);
        Assert.Equal("milliseconds", report.Unit);
        Assert.Equal(12, report.TotalWeight);
        Assert.Equal(4, report.TotalSamples);
        var hot = Assert.Single(report.Frames, f => f.MatchedMethodId == "hot");
        var cold = Assert.Single(report.Frames, f => f.MatchedMethodId == "cold");
        Assert.Equal(8, hot.ExclusiveWeight);
        Assert.Equal(8, hot.InclusiveWeight); // repeated recursive frame counted once
        Assert.Equal(4, cold.ExclusiveWeight);
        Assert.Equal(66.667, hot.ExclusivePercent);
        Assert.Equal(2, report.LinkedFrames);
        Assert.Equal("matched_exact_qualified_name", hot.MatchStatus);
    }

    [Fact]
    public void NeverSilentlyPicksOneOverload()
    {
        var result = SpeedscopeHotspotParser.Parse(Source, "trace.json",
        [
            new("overload1", "Sample.Engine.Hot"),
            new("overload2", "Sample.Engine.Hot")
        ]);
        var hot = Assert.Single(result.Frames, f =>
            f.FrameName.StartsWith("Sample.Engine.Hot", StringComparison.Ordinal));
        Assert.Equal("ambiguous_overload_or_type", hot.MatchStatus);
        Assert.Null(hot.MatchedMethodId);
        Assert.Equal(1, result.AmbiguousFrames);
    }

    [Fact]
    public void RejectsUnrelatedNamespaceAndUnknownProfiles()
    {
        var result = SpeedscopeHotspotParser.Parse(Source, "trace.json",
            [new("different", "Engine.Hot")]);
        Assert.DoesNotContain(result.Frames, f => f.MatchedMethodId == "different");
        var evented = Source.Replace("\"type\":\"sampled\"", "\"type\":\"evented\"");
        Assert.Throws<InvalidDataException>(() =>
            SpeedscopeHotspotParser.Parse(evented, "trace.json", Methods));
    }

    [Fact]
    public void RejectsInvalidWeightsAndFrameIndices()
    {
        var invalidWeights = Source.Replace("[1,2,4,5]", "[1,-2,4,5]");
        Assert.Throws<InvalidDataException>(() =>
            SpeedscopeHotspotParser.Parse(invalidWeights, "trace.json", Methods));
        var invalidIndex = Source.Replace("[0,2]", "[0,100]");
        Assert.Throws<InvalidDataException>(() =>
            SpeedscopeHotspotParser.Parse(invalidIndex, "trace.json", Methods));
        var fewerWeights = Source.Replace("[1,2,4,5]", "[1,2]");
        Assert.Throws<InvalidDataException>(() =>
            SpeedscopeHotspotParser.Parse(fewerWeights, "trace.json", Methods));
    }

    [Fact]
    public void SupportsEventedProfilesAndComputesExclusiveDurations()
    {
        const string evented = """
            {
              "shared":{"frames":[
                {"name":"Sample.Engine.Run()"},
                {"name":"Sample.Engine.Hot()"},
                {"name":"Sample.Engine.Cold()"}]},
              "profiles":[{
                "type":"evented","name":"duration-profile",
                "unit":"milliseconds","startValue":0,"endValue":8,
                "events":[
                  {"type":"O","at":0,"frame":0},
                  {"type":"O","at":1,"frame":1},
                  {"type":"C","at":4,"frame":1},
                  {"type":"O","at":4,"frame":2},
                  {"type":"C","at":7,"frame":2},
                  {"type":"C","at":8,"frame":0}
                ]
              }]
            }
            """;
        var profile = SpeedscopeHotspotParser.Parse(evented, "evented.json",
        [
            new("run", "Sample.Engine.Run"),
            new("hot", "Sample.Engine.Hot"),
            new("cold", "Sample.Engine.Cold")
        ]);
        Assert.Equal("evented", profile.ProfileType);
        Assert.Equal("weighted_intervals", profile.ObservationCountKind);
        Assert.Equal(8, profile.TotalWeight);
        var run = Assert.Single(profile.Frames, f => f.MatchedMethodId == "run");
        var hot = Assert.Single(profile.Frames, f => f.MatchedMethodId == "hot");
        var cold = Assert.Single(profile.Frames, f => f.MatchedMethodId == "cold");
        Assert.Equal(8, run.InclusiveWeight);
        Assert.Equal(2, run.ExclusiveWeight);
        Assert.Equal(3, hot.ExclusiveWeight);
        Assert.Equal(3, cold.ExclusiveWeight);
        Assert.Equal(37.5, hot.ExclusivePercent);
    }

    [Fact]
    public void RejectsUnbalancedOrOutOfOrderEventedProfile()
    {
        const string bad = """
            {
              "shared":{"frames":[{"name":"A"},{"name":"B"}]},
              "profiles":[{
                "type":"evented","name":"bad","unit":"seconds",
                "startValue":0,"endValue":2,
                "events":[
                  {"type":"O","at":0,"frame":0},
                  {"type":"O","at":1,"frame":1},
                  {"type":"C","at":2,"frame":0}
                ]
              }]
            }
            """;
        Assert.Throws<InvalidDataException>(() =>
            SpeedscopeHotspotParser.Parse(bad, "bad.json", []));
    }

    [Fact]
    public void SupportsProfileSelectionAndLimits()
    {
        var result = SpeedscopeHotspotParser.Parse(Source, "trace.json", Methods,
            profileIndex: 0, limit: 1);
        Assert.Single(result.Frames);
        Assert.Equal(4, result.TotalSamples);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpeedscopeHotspotParser.Parse(Source, "trace.json", Methods, profileIndex: 2));
    }
}
