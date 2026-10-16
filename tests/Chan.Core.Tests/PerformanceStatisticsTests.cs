using Chan.Core.Performance;
using Xunit;

namespace Chan.Core.Tests;

public sealed class PerformanceStatisticsTests
{
    [Theory]
    [InlineData(120d, "regression_candidate", 20d)]
    [InlineData(80d, "improvement_candidate", -20d)]
    [InlineData(101d, "inconclusive", 1d)]
    public void ConstantSamplesPreserveClassification(double candidate, string status, double change)
    {
        var result = PerformanceStatistics.Compare(Enumerable.Repeat(100d, 5).ToArray(),
            Enumerable.Repeat(candidate, 5).ToArray(), "host", "host");
        Assert.Equal(status, result.Status);
        Assert.Equal(100d, result.BaselineMedian);
        Assert.Equal(candidate, result.CandidateMedian);
        Assert.Equal(change, result.PercentChange, 10);
        Assert.Equal(change, result.Lower95Percent, 10);
        Assert.Equal(change, result.Upper95Percent, 10);
        Assert.Contains("Exploratory", result.Note);
    }

    [Fact]
    public void IsRepeatableAndDoesNotMutateInputs()
    {
        double[] baseline = [103, 98, 100, 102, 99, 101];
        double[] candidate = [83, 78, 80, 82, 79, 81];
        var baselineCopy = baseline.ToArray();
        var candidateCopy = candidate.ToArray();
        var first = PerformanceStatistics.Compare(baseline, candidate, "host", "host", "us/op");
        var second = PerformanceStatistics.Compare(baseline, candidate, "host", "host", "us/op");
        Assert.Equal(first, second);
        Assert.Equal(baselineCopy, baseline);
        Assert.Equal(candidateCopy, candidate);
        Assert.Equal(100.5, first.BaselineMedian);
        Assert.Equal(80.5, first.CandidateMedian);
        Assert.Equal("us/op", first.Unit);
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    public void RejectsUnmatchedOrMissingEnvironments(string baseline, string candidate) =>
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Compare(
            [100, 100, 100, 100, 100], [80, 80, 80, 80, 80], baseline, candidate));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    public void RequiresFiveSamplesOnBothSides(int baseline, int candidate) =>
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Compare(
            Enumerable.Repeat(100d, baseline).ToArray(),
            Enumerable.Repeat(80d, candidate).ToArray(), "host", "host"));

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsInvalidSamples(double value) =>
        Assert.Throws<ArgumentException>(() => PerformanceStatistics.Compare(
            [100, 100, 100, 100, 100], [80, 80, 80, 80, value], "host", "host"));

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(101d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidThresholds(double threshold) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceStatistics.Compare(
            [100, 100, 100, 100, 100], [80, 80, 80, 80, 80], "host", "host",
            thresholdPercent: threshold));

    [Fact]
    public void SharedAssemblyDoesNotReferenceTenkaOrAnalyzerPackages()
    {
        var assembly = typeof(PerformanceStatistics).Assembly;
        Assert.Equal("Chan.Core", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("TenkaChan", StringComparison.Ordinal) == true
            || reference.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true
            || reference.Name?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true
            || reference.Name?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true);
    }
}
