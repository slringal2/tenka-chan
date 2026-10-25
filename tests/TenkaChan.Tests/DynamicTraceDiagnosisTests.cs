using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using Xunit;

namespace TenkaChan.Tests;

// Pure synthetic evidence, not a CPU/GC measurement of the test runner.
public sealed class DynamicTraceDiagnosisTests
{
    private const string TraceSha = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static TraceCaptureReport Capture(string id = "capture") =>
        new(id, "trace_completed", "/test/capture.json", "/test/Host.dll", "net8.0",
            "eventpipe_sampled_thread_time", "2026-10-18T00:00:00Z", "2026-10-18T00:01:00Z",
            "/test/dotnet-trace", "1", 0, 0, [],
            [new("Host.dll", TraceSha, 1234)], new("workload.nettrace", TraceSha, 12345),
            new("workload.speedscope.json", TraceSha, 2000),
            [new(0, "demo-thread", "sampled", "milliseconds", 6)],
            "", [])
        { RuntimeProviderConfiguration = TrustedTraceCapture.RuntimeCostProviders };

    private static SampledHotspotReport Profile(int totalFrames = 4) =>
        new("/test/workload.speedscope.json", "demo-thread", "milliseconds", 6,
            100, totalFrames, 3, 1,
            [
                new("App.Work.Hot()", 2, 60, 40, 60, 40, "hot", "matched_exact_qualified_name"),
                new("App.Work.Hot() [tiered]", 2, 45, 20, 45, 20, "hot", "matched_exact_qualified_name"),
                new("App.Work.Other()", 1, 25, 25, 25, 25, "not-indexed", "matched_exact_qualified_name"),
                new("App.Work.Unclear()", 1, 10, 10, 10, 10, null, "ambiguous_overload_or_type")
            ], ["synthetic fixture"]);

    private static RuntimeCostReport Costs(string captureId = "capture", string sha = TraceSha,
        bool lost = false)
    {
        var events = new RuntimeCostAccumulator();
        events.Add(new("gc_start", 0, 123, 1, Count: 1, Generation: 0));
        events.Add(new("gc_stop", 2, 123, 1, Count: 1));
        events.Add(new("suspend_start", 3, 123, 1, ThreadId: 11, Reason: 1));
        events.Add(new("suspend_stop", 4, 123, 1, ThreadId: 11));
        events.Add(new("restart_start", 6, 123, 1, ThreadId: 11));
        events.Add(new("restart_stop", 7, 123, 1, ThreadId: 11));
        events.Add(new("allocation_tick", 8, 123, 1, AllocationBytes: 4096,
            TypeName: "App.Work", ObjectSize: 56));
        events.Add(new("contention_start", 9, 123, 1, ThreadId: 11, Flags: 0));
        events.Add(new("contention_stop", 11, 123, 1, ThreadId: 11,
            Flags: 0, DurationNs: 5_000_000));
        events.Add(new("exception_thrown", 12, 123, 1)
        { ExceptionType = "System.InvalidOperationException", ExceptionFlags = 0 });
        events.Add(new("exception_thrown", 13, 123, 1)
        { ExceptionType = "System.InvalidOperationException", ExceptionFlags = 0x04 });
        return new(captureId, "/test/capture.json", sha, "fixture-parser",
            lost ? "partial_reported_event_loss" : "observed_events",
            11, lost ? 3 : 0, false, null,
            [new("Host.dll", TraceSha, 1234)], events.Build(), [])
        { RuntimeProviderConfiguration = TrustedTraceCapture.RuntimeCostProviders };
    }

    private static readonly IReadOnlyDictionary<string, MethodSummary> Methods =
        new Dictionary<string, MethodSummary>(StringComparer.Ordinal)
        {
            ["hot"] = new("hot", "App", "App.Work.Hot()", "/src/Work.cs", 42)
        };

    [Fact]
    public void LinksObservedExclusiveTimeToMethodButKeepsRuntimeCostsProcessScoped()
    {
        var report = DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
            Capture(), 0, Profile(), Costs(), Methods);
        var hot = Assert.Single(report.Methods);
        Assert.Equal("hot", hot.MethodId);
        Assert.Equal(42, hot.Line);
        Assert.Equal(60, hot.ExclusiveWeight);
        Assert.Equal(60, hot.ExclusivePercent);
        Assert.Equal(2, hot.ContributingFrames);
        Assert.Equal(4, hot.FrameObservations); // Not unique sample count.
        Assert.Null(hot.StaticSuspicionScore);
        Assert.False(hot.BinariesVerified);
        Assert.False(report.BinariesVerified);
        Assert.Equal("profile_frame_name_to_loaded_roslyn_method__build_unverified", hot.Association);
        Assert.False(report.PerMethodCpuVerified);
        Assert.Equal("dynamic_observation_unverified", report.Status);
        Assert.Equal(60, report.LinkedExclusiveWeight);
        Assert.Equal(35, report.UnlinkedExclusiveWeight);
        Assert.Equal(5, report.HiddenOrUnassignedExclusiveWeight);
        Assert.Contains(report.UnlinkedExamples, u =>
            u.MatchStatus == "ambiguous_overload_or_type");
        Assert.Contains(report.UnlinkedExamples, u =>
            u.Name == "App.Work.Other()" && u.MatchStatus == "roslyn_method_not_in_current_type_index");

        var runtime = Assert.Single(report.Runtime);
        Assert.Equal(123, runtime.ProcessId);
        Assert.Equal(1, runtime.CompletedGcCollections);
        Assert.Equal(1, runtime.GcSuspensionWindows);
        Assert.Equal(4, runtime.GcSuspensionWindowTotalMs);
        Assert.Equal(1, runtime.AllocationTicks);
        Assert.Equal((ulong)4096, runtime.TickReportedAllocationBytes);
        Assert.Equal(1, runtime.MonitorContentionDurations);
        Assert.Equal(5, runtime.OverlappingMonitorWaitMs);
        Assert.Contains("App.Work", runtime.AllocationTriggerTypes);
        Assert.Equal("process_clr_only__not_per_method", runtime.SourceAttribution);
        Assert.Equal(2, runtime.ExceptionThrows);
        Assert.Equal(1, runtime.ExceptionKindsRetained);
        Assert.Equal(2, report.TotalExceptionThrows);
        Assert.False(report.ExceptionHotspotsTruncated);
        var hotspot = Assert.Single(report.ExceptionHotspots);
        Assert.Equal("System.InvalidOperationException", hotspot.TypeName);
        Assert.Equal(123, hotspot.ProcessId);
        Assert.Equal(1, hotspot.ClrInstanceId);
        Assert.Equal(2, hotspot.ObservedThrows);
        Assert.False(hotspot.CrashConfirmed);
        Assert.Equal("pid_clr_exception_type_only__throw_site_unresolved", hotspot.SourceAttribution);
        Assert.False(runtime.ExceptionTypeListTruncated);
        var exception = Assert.Single(runtime.ExceptionTypes);
        Assert.Equal("System.InvalidOperationException", exception.TypeName);
        Assert.Equal(2, exception.ObservedThrows);
        Assert.Equal(1, exception.RethrowFlagsObserved);
        Assert.Contains(report.Limitations, text => text.Contains("including expected/caught exceptions", StringComparison.Ordinal));
        Assert.Contains(report.Limitations, text =>
            text.Contains("NOT added to method weights", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticSuspicionRemainsSeparateFromObservedWeights()
    {
        var method = Methods["hot"];
        var ranking = new HotspotMethodRanking(method, 1234, 1200, 3, 2, 4, 2, 0, 0, 0, 0, 0, false,
            ["nested_loop_candidate"]);
        var site = new HotspotSite("s", "hot", "allocation_in_loop", method.File,
            43, 5, 3, 8, "new byte[1]", "Candidate", "Benchmark it");
        var @static = new StaticHotspotReport("snapshot", "static_candidate",
            1, 1, 1, 1, false, false, [ranking], [site], [], []);
        var report = DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
            Capture(), 0, Profile(), Costs(), Methods, @static);
        var hot = Assert.Single(report.Methods);
        Assert.Equal(60, hot.ExclusiveWeight); // Static suspicion never added.
        Assert.Equal(1234, hot.StaticSuspicionScore);
        Assert.Contains(hot.StaticCandidates, s => s.Contains("allocation_in_loop", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncationAndLostEventsNeverBecomeVerifiedAbsence()
    {
        var report = DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
            Capture(), 0, Profile(totalFrames: 15), Costs(lost: true), Methods,
            maxMethods: 1);
        Assert.True(report.FrameListTruncated);
        Assert.Equal(3, report.ReportedEventsLost);
        Assert.Contains(report.Limitations, x => x.Contains("truncated", StringComparison.Ordinal));
        Assert.Contains(report.Limitations, x => x.Contains("dropped", StringComparison.Ordinal));
        Assert.Equal("partial_reported_event_loss", report.RuntimeEventStatus);
    }

    [Fact]
    public void RefusesMixedTraceIdentitiesOrStaleStaticSnapshots()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
                Capture(), 0, Profile(), Costs(sha: new string('B', 64)), Methods));
        Assert.Throws<InvalidOperationException>(() =>
            DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
                Capture(), 0, Profile(), Costs(captureId: "other"), Methods));
        Assert.Throws<InvalidOperationException>(() =>
            DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
                Capture(), 0, Profile() with { ProfileName = "another" }, Costs(), Methods));
        var old = new StaticHotspotReport("old-snapshot", "static_candidate",
            0, 0, 0, 0, false, false, [], [], [], []);
        Assert.Throws<InvalidOperationException>(() =>
            DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
                Capture(), 0, Profile(), Costs(), Methods, old));
    }

    [Fact]
    public void UnknownSourceMethodIsNotAttributedEvenIfFrameHasAnId()
    {
        var report = DynamicTraceDiagnosisBuilder.Build("snapshot", "artifact",
            Capture(), 0, Profile(), Costs(), new Dictionary<string, MethodSummary>());
        Assert.Empty(report.Methods);
        Assert.Equal(0, report.LinkedExclusiveWeight);
        Assert.Equal(95, report.UnlinkedExclusiveWeight);
    }
}
