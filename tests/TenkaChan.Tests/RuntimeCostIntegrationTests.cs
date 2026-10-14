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
            Assert.All(report.Runtimes, r => { Assert.Null(r.CpuMilliseconds); Assert.Null(r.OtherWaitMilliseconds); });
            Assert.Equal(capture.NetTrace!.Sha256, report.TraceSha256);
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
