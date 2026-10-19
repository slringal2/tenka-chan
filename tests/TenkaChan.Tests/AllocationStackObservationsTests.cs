using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>Deterministic synthetic stack tests. No target process is executed.</summary>
public sealed class AllocationStackObservationsTests
{
    private static readonly ProfileMethodIdentity[] SourceMethods =
    [
        new("work", "App.Engine.Work"),
        new("caller", "App.Engine.Caller"),
        new("overload-a", "App.Engine.Overload"),
        new("overload-b", "App.Engine.Overload"),
        new("other", "Another.Engine.Work")
    ];

    [Fact]
    public void NearestUnambiguousSourceFrameWinsOverOuterCaller()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        a.Add(new(100, 1, "System.Byte[]", 128,
            ["System.GC.AllocateArray()", "App.Engine.Work()", "App.Engine.Caller()"]));
        a.Add(new(100, 1, "System.Byte[]", 32,
            ["System.Byte[].ctor()", "App.Engine.Work()", "App.Engine.Caller()"]));
        var result = a.Build("capture", new string('A', 64), 20, 0, false);
        var item = Assert.Single(result.Hotspots);
        Assert.Equal("work", item.MethodId);
        Assert.Equal(100, item.ProcessId);
        Assert.Equal(1, item.ClrInstanceId);
        Assert.Equal(2, item.SampledTriggers);
        Assert.Equal(160UL, item.TriggerObjectBytesObserved);
        Assert.Equal(2, item.TriggerObjectSizesAvailable);
        Assert.False(item.AllocatedBytesVerified);
        Assert.False(item.BinariesVerified);
        Assert.Equal("observed_stack_samples", result.Status);
        Assert.Equal(2, result.SourceLinkedTicks);
        Assert.Equal(2, result.StacksAvailable);
        Assert.False(result.TotalAllocatedBytesVerified);
    }

    [Fact]
    public void AmbiguousOverloadNeverFallsBackToOuterCaller()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        a.Add(new(1, 1, "T", 32,
            ["App.Engine.Overload()", "App.Engine.Caller()"]));
        var r = a.Build("c", "hash", 1, 0, false);
        Assert.Equal(1, r.AmbiguousTicks);
        Assert.Equal(0, r.SourceLinkedTicks);
        Assert.Empty(r.Hotspots);
        Assert.Equal("stacks_without_unique_source_links", r.Status);
    }

    [Fact]
    public void NamespaceSubstringAndUnresolvedAddressesCannotForgeSourceMatch()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        a.Add(new(1, 1, "T", 1, ["NotApp.Engine.Caller()"]));
        a.Add(new(1, 1, "T", null, []));
        a.Add(new(1, 1, "T", 1, ["0x000000DEADBEEF", "???"]));
        var r = a.Build("c", "hash", 3, 0, false);
        Assert.Equal(3, r.AllocationTicksObserved);
        Assert.Equal(1, r.MissingStackTicks);
        Assert.Equal(2, r.UnmatchedTicks);
        Assert.Empty(r.Hotspots);
        Assert.Equal(0, r.SourceLinkedTicks);
    }

    [Fact]
    public void SameSourceMethodRemainsSeparatePerPidClr()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        a.Add(new(1, 1, "X", null, ["App.Engine.Caller()"]));
        a.Add(new(1, 2, "X", null, ["App.Engine.Caller()"]));
        a.Add(new(2, 1, "X", null, ["App.Engine.Caller()"]));
        var r = a.Build("c", "hash", 3, 0, false);
        Assert.Equal(3, r.Hotspots.Count);
        Assert.All(r.Hotspots, h => Assert.Equal(1, h.SampledTriggers));
        Assert.All(r.Hotspots, h => Assert.Equal(0UL, h.TriggerObjectBytesObserved));
    }

    [Fact]
    public void OutputIsTruncatedButSampleTotalsAreNotLost()
    {
        var a = new AllocationStackAccumulator(SourceMethods, maxRows: 1);
        a.Add(new(1, 1, "T", null, ["App.Engine.Caller()"]));
        a.Add(new(2, 1, "T", null, ["App.Engine.Caller()"]));
        a.Add(new(3, 1, "T", null, ["App.Engine.Caller()"]));
        var r = a.Build("c", "hash", 3, 4, false);
        Assert.Equal(3, r.SourceLinkedTicks);
        Assert.Equal(3, r.AllocationTicksObserved);
        Assert.Single(r.Hotspots);
        Assert.True(r.HotspotsTruncated);
        Assert.Equal("partial_event_loss", r.Status);
    }

    [Fact]
    public void DistinctGroupCapKeepsGlobalCountsAndSignalsPartialResult()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        for (var i = 0; i < AllocationStackAccumulator.MaxGroups + 3; i++)
            a.Add(new(i + 1, 1, "T", 8, ["App.Engine.Caller()"]));
        var r = a.Build("c", "hash", AllocationStackAccumulator.MaxGroups + 3, 0, false);
        Assert.Equal(AllocationStackAccumulator.MaxGroups + 3, r.SourceLinkedTicks);
        Assert.Equal(3, r.TriggerGroupLimitHits);
        Assert.True(r.HotspotsTruncated);
        Assert.Equal(AllocationStackAccumulator.MaxGroups + 3, r.AllocationTicksObserved);
    }

    [Fact]
    public void MetadataNamesAreBoundedAndControlCharsDoNotLeakToResult()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        a.Add(new(1, 1, "My.Type\r\n\t" + new string('x', 300), 12,
            ["App.Engine.Caller()", "User.Stack\r\nSensitive()"]));
        var r = a.Build("c", "hash", 1, 0, false);
        var item = Assert.Single(r.Hotspots);
        Assert.All(item.TriggerTypes, type =>
        {
            Assert.DoesNotContain("\n", type);
            Assert.DoesNotContain("\r", type);
            Assert.True(type.Length <= 160);
        });
        Assert.All(item.ExampleStacks, stack =>
        {
            Assert.DoesNotContain("\n", stack);
            Assert.DoesNotContain("\r", stack);
        });
    }

    [Fact]
    public void EventLimitHasExplicitPrecedenceOverApparentSuccess()
    {
        var a = new AllocationStackAccumulator(SourceMethods);
        a.Add(new(1, 1, "X", null, ["App.Engine.Caller()"]));
        var report = a.Build("c", "hash", 100000, 0, true);
        Assert.Equal("partial_event_limit", report.Status);
        Assert.True(report.EventLimitReached);
        Assert.False(report.BinariesVerified);
    }
}
