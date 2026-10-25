using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;
public sealed class RuntimeCostIntegrationTests
{
    [Fact]
    public async Task RealNet8CaptureSeparatesGcAllocationAndMonitorContention()
    {
        if (Environment.GetEnvironmentVariable("TENKA_RUNTIME_INTEGRATION") != "1") return;
        var fixture = Environment.GetEnvironmentVariable("TENKA_RUNTIME_FIXTURE") ?? throw new InvalidOperationException("Missing runtime fixture.");
        var root = Path.Combine(Path.GetTempPath(), "tenka-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var guard = new WorkspaceGuard([root, Path.GetDirectoryName(fixture)!], false, false);
            var collector = new TrustedTraceCapture(guard, true, Environment.GetEnvironmentVariable("TENKA_TEST_TRACE_TOOL"));
            var capture = await collector.CaptureAsync(fixture, root, true, 90, runtimeCostEvents: true);
            Assert.True(capture.Status == "trace_completed", capture.Status + "\n" + capture.Output);
            Assert.Equal(TrustedTraceCapture.RuntimeCostProviders, capture.RuntimeProviderConfiguration);
            var report = await new RuntimeCostReader(guard).ReadAsync(capture.ReportPath, options: new(PauseBudgetMs: 10));
            var dest = Environment.GetEnvironmentVariable("TENKA_RUNTIME_REPORT");
            if (dest is not null) File.WriteAllText(dest, JsonSerializer.Serialize(report,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Assert.False(report.EventLimitReached);
            Assert.NotEmpty(report.Runtimes);
            Assert.Contains(report.Runtimes, r => r.CollectionsCompleted >= 4 && r.GcSuspensionWindows >= 4);
            Assert.Contains(report.Runtimes, r => r.AllocationTicks > 0 && r.TickReportedAllocationBytes > 0);
            Assert.Contains(report.Runtimes, r => r.ContentionDurationsReported > 0 && r.ContentionWaitMilliseconds > 0);
            Assert.Contains(report.Runtimes, r => r.ExceptionThrows >= 6 &&
                r.ExceptionTypes.Any(t => t.TypeName == "System.InvalidOperationException" &&
                    t.ObservedThrows >= 6));
            Assert.DoesNotContain("PRIVATE-FIXTURE-MESSAGE-DO-NOT-EXPOSE",
                JsonSerializer.Serialize(report));
            Assert.All(report.Runtimes, r => { Assert.Null(r.CpuMilliseconds); Assert.Null(r.OtherWaitMilliseconds); });
            Assert.Equal(capture.NetTrace!.Sha256, report.TraceSha256);
            // A trace can contain multiple active thread profiles.
            var selectedProfile = capture.Profiles.First(p => p.Observations > 0);
            var profilePath = new TrustedTraceCapture(guard).RequireProfile(capture, selectedProfile.Index);
            var parsedProfile = SpeedscopeHotspotParser.Read(profilePath, [], selectedProfile.Index, 100);
            var fused = DynamicTraceDiagnosisBuilder.Build("trace-fixture", "fixture-artifact",
                capture, selectedProfile.Index, parsedProfile, report,
                new Dictionary<string, MethodSummary>(StringComparer.Ordinal));
            Assert.Equal("dynamic_observation_unverified", fused.Status);
            Assert.True(fused.TotalExceptionThrows >= 6);
            Assert.Contains(fused.ExceptionHotspots,
                e => e.TypeName == "System.InvalidOperationException" && e.ObservedThrows >= 6);
            Assert.Empty(fused.Methods); // No source identity was supplied; never guess.

            // An additional offline ETLX conversion must leave neither a
            // derived sensitive file nor an invented source association.
            if (capture.NetTrace.Length <= AllocationStackTraceReader.MaxInputBytes)
            {
                var allocation = await new AllocationStackTraceReader(guard)
                    .ReadAsync(capture.ReportPath, []);
                Assert.Equal(capture.Id, allocation.CaptureId);
                Assert.Equal(capture.NetTrace.Sha256, allocation.TraceSha256);
                Assert.Empty(allocation.Hotspots);
                Assert.False(allocation.BinariesVerified);
                Assert.False(allocation.TotalAllocatedBytesVerified);
                Assert.Empty(Directory.EnumerateFiles(
                    Path.GetDirectoryName(capture.ReportPath)!, "tenka-allocation-*.etlx"));
            }

            var selected = await new RuntimeCostReader(guard).ReadAsync(capture.ReportPath, report.Runtimes[0].ProcessId);
            Assert.All(selected.Runtimes, r => Assert.Equal(report.Runtimes[0].ProcessId, r.ProcessId));
            var bounded = await new RuntimeCostReader(guard).ReadAsync(capture.ReportPath, options: new(MaxEvents: 100, MaxRows: 1));
            Assert.True(bounded.EventLimitReached); Assert.Equal("partial_event_limit", bounded.Status);
            Assert.Equal(100, bounded.EventsRead);
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(capture.ReportPath)!, "workload.nettrace"), "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => new RuntimeCostReader(guard).ReadAsync(capture.ReportPath));
        }
        finally { Directory.Delete(root, true); }
    }
}
