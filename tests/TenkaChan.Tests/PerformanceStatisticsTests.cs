using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class PerformanceStatisticsTests
{
    [Fact]
    public void DetectsLargeConsistentSlowdown()
    {
        var before = new double[] { 99, 100, 101, 100, 99, 100, 101, 100 };
        var after = new double[] { 120, 121, 122, 120, 121, 122, 121, 120 };
        var result = PerformanceStatistics.Compare(before, after, "linux|net10|cpu1", "linux|net10|cpu1");
        Assert.Equal("regression_candidate", result.Status);
        Assert.True(result.Lower95Percent > 0);
    }

    [Fact]
    public void RejectsDifferentMachines()
    {
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Compare(
            [100, 100, 100, 100, 100], [120, 120, 120, 120, 120], "cpu-a", "cpu-b"));
    }

    [Fact]
    public void RejectsTooFewSamples()
    {
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Compare(
            [100, 100], [120, 120], "cpu-a", "cpu-a"));
    }
}
