using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class TrustedLocalProcessProfilerTests
{
    [Fact]
    public async Task DisabledAndUntrustedRunsAreRejectedBeforeProcessLaunch()
    {
        var guard = new WorkspaceGuard([Path.GetTempPath()], false, false);
        var disabled = new TrustedLocalProcessProfiler(guard, enabled: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            disabled.MeasureAsync("not-an-app.dll", trustedBinary: true));
        var enabled = new TrustedLocalProcessProfiler(guard, enabled: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            enabled.MeasureAsync("not-an-app.dll", trustedBinary: false));
    }

    [Fact]
    public async Task RejectsExcessiveSampleAndTimeBudgets()
    {
        var profiler = new TrustedLocalProcessProfiler(
            new WorkspaceGuard([Path.GetTempPath()], false, false), enabled: true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            profiler.MeasureAsync("not-an-app.dll", true, repetitions: 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            profiler.MeasureAsync("not-an-app.dll", true, timeoutSeconds: 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            profiler.MeasureAsync("not-an-app.dll", true, warmups: 4));
    }

    [Fact]
    public async Task MeasuresTrustedNet8ProcessAndPersistsWholeProcessEvidence()
    {
        var before = Environment.GetEnvironmentVariable("TENKA_LOCAL_PERF_BEFORE");
        if (string.IsNullOrWhiteSpace(before)) return;
        var guard = new WorkspaceGuard([Path.GetDirectoryName(before)!], false, false);
        var profiler = new TrustedLocalProcessProfiler(guard, enabled: true);
        var result = await profiler.MeasureAsync(before, trustedBinary: true,
            repetitions: 5, warmups: 1);
        Assert.Equal("completed", result.Status);
        Assert.Equal("net8.0", result.TargetFramework);
        Assert.Equal(6, result.Samples.Count);
        Assert.Equal(5, result.Samples.Count(x => !x.IsWarmup));
        Assert.Null(result.Comparison);
        Assert.All(result.Samples, x =>
        {
            Assert.True(x.WallMilliseconds > 0);
            Assert.True(x.CpuMilliseconds is null or >= 0);
            Assert.True(x.PeakWorkingSetBytes is null or >= 0);
            Assert.Contains(x.CounterStatus,
                new[] { "cpu_counter_unavailable", "cpu_sampled_before_exit",
                    "cpu_final_process_counter" });
            Assert.Equal(0, x.ExitCode);
            Assert.Equal(64, x.StdoutSha256.Length);
        });
        var stored = profiler.Get(result.Id);
        Assert.Equal(result.BaselineSha256, stored.BaselineSha256);
        Assert.Equal(result.Samples.Count, stored.Samples.Count);
        Assert.Contains(stored.Limitations, x =>
            x.Contains("process start", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComparesEquivalentTrustedNet8BinariesWithoutClaimingMethodHotness()
    {
        var before = Environment.GetEnvironmentVariable("TENKA_LOCAL_PERF_BEFORE");
        var after = Environment.GetEnvironmentVariable("TENKA_LOCAL_PERF_AFTER");
        if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)) return;
        var guard = new WorkspaceGuard([Path.GetDirectoryName(before)!,
            Path.GetDirectoryName(after)!], false, false);
        var profiler = new TrustedLocalProcessProfiler(guard, enabled: true);
        var result = await profiler.MeasureAsync(before, trustedBinary: true,
            candidateAssembly: after, repetitions: 5, warmups: 1);
        Assert.Equal("completed", result.Status);
        Assert.Equal(12, result.Samples.Count);
        Assert.Equal(10, result.Samples.Count(x => !x.IsWarmup));
        Assert.Equal(5, result.Samples.Count(x =>
            !x.IsWarmup && x.Variant == "baseline"));
        Assert.Equal(5, result.Samples.Count(x =>
            !x.IsWarmup && x.Variant == "candidate"));
        Assert.Single(result.Samples.Where(x => !x.IsWarmup)
            .Select(x => x.StdoutSha256).Distinct());
        var comparison = Assert.IsType<TrustedProcessComparison>(result.Comparison);
        Assert.Equal(5, result.IterationsPerVariant);
        Assert.Contains("process", comparison.ComparisonBasis,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(double.IsFinite(comparison.ChangePercent));
        Assert.NotEqual(result.BaselineSha256, result.CandidateSha256);
    }

    [Fact]
    public async Task RefusesHostExecutableOutsideAllowlist()
    {
        var before = Environment.GetEnvironmentVariable("TENKA_LOCAL_PERF_BEFORE");
        if (string.IsNullOrWhiteSpace(before)) return;
        var temp = Path.Combine(Path.GetTempPath(), "tenka-denied-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var profiler = new TrustedLocalProcessProfiler(
                new WorkspaceGuard([temp], false, false), enabled: true);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                profiler.MeasureAsync(before, trustedBinary: true));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task IdenticalBinaryCannotProduceARevisionSpeedupClaim()
    {
        var before = Environment.GetEnvironmentVariable("TENKA_LOCAL_PERF_BEFORE");
        if (string.IsNullOrWhiteSpace(before)) return;
        var profiler = new TrustedLocalProcessProfiler(
            new WorkspaceGuard([Path.GetDirectoryName(before)!], false, false),
            enabled: true);
        var run = await profiler.MeasureAsync(before, trustedBinary: true,
            candidateAssembly: before, repetitions: 5, warmups: 0);
        Assert.Equal("identical_binaries_no_revision_comparison", run.Status);
        Assert.Null(run.Comparison);
        Assert.Equal(10, run.Samples.Count);
    }

    [Fact]
    public async Task NonEquivalentOutputInvalidatesPerformanceComparison()
    {
        var before = Environment.GetEnvironmentVariable("TENKA_LOCAL_PERF_BEFORE");
        var different = Environment.GetEnvironmentVariable("TENKA_LOCAL_JIT_SMOKE_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(before) ||
            string.IsNullOrWhiteSpace(different)) return;
        var profiler = new TrustedLocalProcessProfiler(
            new WorkspaceGuard([Path.GetDirectoryName(before)!,
                Path.GetDirectoryName(different)!], false, false), enabled: true);
        var run = await profiler.MeasureAsync(before, trustedBinary: true,
            candidateAssembly: different, repetitions: 5, warmups: 0);
        Assert.Equal("non_equivalent_or_nondeterministic_output", run.Status);
        Assert.Null(run.Comparison);
    }

    [Fact]
    public void RejectsMalformedResultIdentifiers()
    {
        var profiler = new TrustedLocalProcessProfiler(
            new WorkspaceGuard([Path.GetTempPath()], false, false), enabled: true);
        Assert.Throws<ArgumentException>(() => profiler.Get("../../escape"));
    }
}
