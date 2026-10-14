using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class RuntimeCostTests
{
    private static RuntimeCostEvent E(string kind, double time, int thread = 1, int pid = 10, int clr = 1) => new(kind, time, pid, clr, thread);
    private static RuntimeCostSummary One(params RuntimeCostEvent[] events)
    {
        var a = new RuntimeCostAccumulator(); foreach (var e in events) a.Add(e); return Assert.Single(a.Build());
    }
    [Fact]
    public void CollectionLifetimeIsNotPauseTime()
    {
        var s = One(E("gc_start", 0) with { Count = 1, Generation = 2, GcType = 1 },
            E("suspend_start", 10) with { Reason = 1 }, E("suspend_stop", 12),
            E("restart_start", 15), E("restart_stop", 16), E("gc_stop", 100) with { Count = 1 });
        Assert.Equal(100d, Assert.Single(s.Collections).DurationMs);
        Assert.Equal(6d, s.GcSuspensionWindowTotalMs); Assert.Equal(3d, s.FullySuspendedTotalMs);
        Assert.Null(s.CpuMilliseconds); Assert.Null(s.OtherWaitMilliseconds);
        Assert.All(s.InvestigationHints, h => Assert.False(h.DefectConfirmed));
    }
    [Fact]
    public void OverlappingBackgroundAndForegroundCollectionsStaySeparate()
    {
        var s = One(E("gc_start", 0) with { Count = 1, Generation = 2, GcType = 1 },
            E("gc_start", 1) with { Count = 2, Generation = 0, GcType = 2 },
            E("gc_stop", 3) with { Count = 2 }, E("gc_stop", 9) with { Count = 1 });
        Assert.Equal(2, s.CollectionsCompleted); Assert.Equal(0, s.GcSuspensionWindows);
        Assert.Equal(2d, s.Collections[0].DurationMs); Assert.Equal(9d, s.Collections[1].DurationMs);
    }
    [Fact]
    public void WaiterTimeMayExceedWallTimeWithoutCreatingNegativeCpu()
    {
        var s = One(E("contention_start", 1, 1) with { Flags = 0 }, E("contention_start", 1, 2) with { Flags = 0 },
            E("contention_stop", 101, 1) with { Flags = 0, DurationNs = 100000000 },
            E("contention_stop", 101, 2) with { Flags = 0, DurationNs = 100000000 });
        Assert.Equal(200d, s.ContentionWaitMilliseconds); Assert.Equal(2, s.ContentionThreads.Count);
        Assert.Null(s.CpuMilliseconds);
    }
    [Fact]
    public void TickAmountIsNotAttributedToItsTriggeringType()
    {
        var s = One(E("allocation_tick", 1) with { AllocationBytes = 100000, TypeName = "SmallObject", ObjectSize = 24 });
        Assert.Equal(100000UL, s.TickReportedAllocationBytes);
        var type = Assert.Single(s.AllocationTriggerTypes); Assert.Equal(24UL, type.ObservedTriggerObjectBytes);
        Assert.Equal(1, type.TickCount);
    }
    [Fact]
    public void OrphansUnknownFlagsAndMissingDurationsRemainExplicit()
    {
        var s = One(E("gc_stop", 1) with { Count = 1 }, E("restart_stop", 2),
            E("contention_stop", 3) with { Flags = 0 }, E("contention_stop", 4) with { Flags = 1, DurationNs = 5000000 });
        Assert.Equal(0, s.CollectionsCompleted); Assert.Equal(0d, s.ContentionWaitMilliseconds);
        Assert.Contains("unmatched_gc_stop", s.IncompleteEvidence.Keys);
        Assert.Contains("missing_or_invalid_contention_duration", s.IncompleteEvidence.Keys);
        Assert.Equal("not_observed__may_require_verbose_capture", s.Coverage["allocation"]);
    }
    [Fact]
    public void UnmatchedStopCanRetainDurationWithoutInventingLockIdentity()
    {
        var s = One(E("contention_stop", 100) with { Flags = 0, DurationNs = 2000000 });
        Assert.Equal(2d, s.ContentionWaitMilliseconds); Assert.Equal(1, s.IncompleteEvidence["unmatched_contention_stop"]);
    }
    [Fact]
    public void SameGcCountCannotJoinDifferentProcessOrClr()
    {
        var a = new RuntimeCostAccumulator();
        a.Add(E("gc_start", 1) with { Count = 1 });
        a.Add(E("gc_stop", 2, pid: 20) with { Count = 1 });
        a.Add(E("gc_stop", 3, clr: 2) with { Count = 1 });
        Assert.Equal(3, a.Build().Count); Assert.All(a.Build(), s => Assert.Equal(0, s.CollectionsCompleted));
    }
    [Fact]
    public void DuplicateStartsDoNotProduceFabricatedPairs()
    {
        var s = One(E("gc_start", 1) with { Count = 1 }, E("gc_start", 2) with { Count = 1 }, E("gc_stop", 5) with { Count = 1 });
        Assert.Empty(s.Collections); Assert.Contains("duplicate_gc_start", s.IncompleteEvidence.Keys);
    }
    [Fact]
    public void NonGcSuspensionsAreNotReportedAsGcPausesOrDataLoss()
    {
        var s = One(E("suspend_start", 1) with { Reason = 5 }, E("restart_stop", 10));
        Assert.Equal(0, s.GcSuspensionWindows); Assert.Equal(1, s.ExcludedSuspensionStartsByReason[5]);
        Assert.Empty(s.IncompleteEvidence);
    }
    [Theory]
    [InlineData(5)] [InlineData(9)] [InlineData(0)]
    public void InterleavedNonGcThreadCannotConsumeGcSuspensionPhases(int otherReason)
    {
        var s = One(E("suspend_start", 1, 2) with { Reason = otherReason },
            E("suspend_start", 2, 1) with { Reason = 1 },
            E("suspend_stop", 3, 2), E("restart_start", 4, 2), E("restart_stop", 5, 2),
            E("suspend_stop", 6, 1), E("restart_start", 9, 1), E("restart_stop", 10, 1));
        var pause = Assert.Single(s.Pauses); Assert.Equal(1, pause.InitiatingThreadId);
        Assert.Equal(8d, pause.SuspensionWindowMs); Assert.Equal(3d, pause.FullySuspendedMs);
        Assert.Equal(1, s.ExcludedSuspensionStartsByReason[otherReason]); Assert.Empty(s.IncompleteEvidence);
    }
    [Fact]
    public void WrongThreadRestartCannotCloseGcWindow()
    {
        var s = One(E("suspend_start", 1, 1) with { Reason = 1 }, E("restart_stop", 10, 2));
        Assert.Empty(s.Pauses); Assert.Contains("unclosed_suspension", s.IncompleteEvidence.Keys);
        Assert.Contains("unmatched_restart_stop", s.IncompleteEvidence.Keys);
    }
    [Fact]
    public void DuplicateSameThreadSuspensionStillFailsClosed()
    {
        var s = One(E("suspend_start", 1) with { Reason = 1 }, E("suspend_start", 2) with { Reason = 1 }, E("restart_stop", 10));
        Assert.Empty(s.Pauses); Assert.Contains("duplicate_suspend_start", s.IncompleteEvidence.Keys);
    }
    [Fact]
    public void IncompletePhasesDoNotInventFullySuspendedTime()
    {
        var s = One(E("suspend_start", 1) with { Reason = 1 }, E("restart_stop", 10));
        Assert.Equal(9d, s.GcSuspensionWindowTotalMs); Assert.Null(Assert.Single(s.Pauses).FullySuspendedMs);
        Assert.Equal(0, s.FullySuspendedIntervals);
    }
    [Fact]
    public void OutputLimitsPreserveTotalsAndMarkTruncation()
    {
        var a = new RuntimeCostAccumulator(new(MaxRows: 1));
        for (var i = 0; i < 3; i++) { a.Add(E("suspend_start", i * 10) with { Reason = 1 }); a.Add(E("restart_stop", i * 10 + 5)); }
        var s = Assert.Single(a.Build()); Assert.Single(s.Pauses); Assert.Equal(3, s.GcSuspensionWindows);
        Assert.Equal(15d, s.GcSuspensionWindowTotalMs); Assert.True(s.DetailsTruncated);
    }
    [Fact]
    public void PauseBudgetOnlyChangesInvestigationPriority()
    {
        var a = new RuntimeCostAccumulator(new(PauseBudgetMs: 3));
        a.Add(E("suspend_start", 0) with { Reason = 1 }); a.Add(E("restart_stop", 5));
        var hint = Assert.Single(Assert.Single(a.Build()).InvestigationHints);
        Assert.Equal("P1", hint.Priority); Assert.False(hint.DefectConfirmed);
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void InvalidTimestampsFailClosed(double time) => Assert.Throws<InvalidDataException>(() => new RuntimeCostAccumulator().Add(E("gc_stop", time)));
    [Fact]
    public void ClockReorderingInvalidatesPendingPairs()
    {
        var s = One(E("gc_start", 10) with { Count = 1 }, E("allocation_tick", 5), E("gc_stop", 20) with { Count = 1 });
        Assert.Empty(s.Collections); Assert.Contains("out_of_order_timestamp", s.IncompleteEvidence.Keys);
    }
    [Fact]
    public void AllocationOverflowIsRejected()
    {
        var a = new RuntimeCostAccumulator(); a.Add(E("allocation_tick", 1) with { AllocationBytes = ulong.MaxValue });
        Assert.Throws<InvalidDataException>(() => a.Add(E("allocation_tick", 2) with { AllocationBytes = 1 }));
    }
    [Fact]
    public void DetailedCaptureIsExplicitAndDoesNotChangeDefault()
    {
        var standard = TrustedTraceCapture.BuildCollectCommand("tool", "dotnet", "app.dll", ".", []);
        var detailed = TrustedTraceCapture.BuildCollectCommand("tool", "dotnet", "app.dll", ".", [], runtimeCostEvents: true);
        Assert.Contains(TrustedTraceCapture.Providers, standard.ArgumentList);
        Assert.Contains(TrustedTraceCapture.RuntimeCostProviders, detailed.ArgumentList);
        Assert.EndsWith(":4", TrustedTraceCapture.Providers); Assert.EndsWith(":5", TrustedTraceCapture.RuntimeCostProviders);
    }
    [Fact]
    public async Task CancelledReaderNeverRequiresOrExecutesTarget()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RuntimeCostReader(new WorkspaceGuard([], false, false)).ReadAsync("missing.json", ct: cts.Token));
    }
}
