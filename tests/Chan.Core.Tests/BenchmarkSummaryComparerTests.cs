using Chan.Core.Benchmarks;
using Xunit;

namespace Chan.Core.Tests;

public sealed class BenchmarkSummaryComparerTests
{
    private static BenchmarkSummary Sample(string id = "Work(N=100)", double? mean = 100,
        int samples = 10, string environment = "host") =>
        new(id, samples, mean, 64, environment);

    [Theory]
    [InlineData(120d, "slower_observed", 20d)]
    [InlineData(80d, "faster_observed", -20d)]
    [InlineData(101d, "difference_below_threshold", 1d)]
    public void DescribesMeansWithoutClaimingSignificance(double mean, string status, double change)
    {
        var result = BenchmarkSummaryComparer.Compare([Sample()], [Sample(mean: mean)]);
        var item = Assert.Single(result.Cases);
        Assert.Equal(status, item.Status);
        Assert.Equal(change, item.MeanChangePercent);
        Assert.Equal(64d, item.CandidateAllocatedBytes);
        Assert.Equal(10, item.BaselineSamples);
        Assert.Equal("summaries_compared", result.Status);
        Assert.Contains("do not establish statistical significance", result.Note);
    }

    [Theory]
    [InlineData(4, 10)]
    [InlineData(10, 4)]
    [InlineData(0, 0)]
    public void MarksInsufficientSamples(int baseline, int candidate)
    {
        var result = BenchmarkSummaryComparer.Compare([Sample(samples: baseline)],
            [Sample(mean: 200, samples: candidate)]);
        Assert.Equal("insufficient_samples", Assert.Single(result.Cases).Status);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("a", "b")]
    public void DoesNotCompareUnmatchedEnvironments(string baseline, string candidate)
    {
        var result = BenchmarkSummaryComparer.Compare([Sample(environment: baseline)],
            [Sample(environment: candidate)]);
        Assert.Empty(result.Cases);
        Assert.Equal(1, result.IncomparableCases);
        Assert.Equal("partial_comparison", result.Status);
    }

    [Fact]
    public void CountsMissingCasesFromBothSides()
    {
        var result = BenchmarkSummaryComparer.Compare([Sample("a"), Sample("b")],
            [Sample("b"), Sample("c")]);
        Assert.Equal("b", Assert.Single(result.Cases).CaseId);
        Assert.Equal(1, result.ComparedCases);
        Assert.Equal(2, result.IncomparableCases);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteOrNonPositiveMeansAreIncomparable(double mean)
    {
        Assert.Empty(BenchmarkSummaryComparer.Compare([Sample(mean: mean)], [Sample()]).Cases);
        Assert.Empty(BenchmarkSummaryComparer.Compare([Sample()], [Sample(mean: mean)]).Cases);
    }

    [Fact]
    public void NullMeansAndOverflowingRatiosAreIncomparable()
    {
        var missing = BenchmarkSummaryComparer.Compare([Sample(mean: null)], [Sample()]);
        var overflow = BenchmarkSummaryComparer.Compare([Sample(mean: double.Epsilon)],
            [Sample(mean: double.MaxValue)]);
        Assert.Equal(1, missing.IncomparableCases);
        Assert.Equal(1, overflow.IncomparableCases);
        Assert.Empty(overflow.Cases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsDuplicatesOnEitherSide(bool duplicateBaseline)
    {
        BenchmarkSummary[] single = [Sample()];
        BenchmarkSummary[] repeated = [Sample(), Sample()];
        var error = Assert.Throws<InvalidDataException>(() => BenchmarkSummaryComparer.Compare(
            duplicateBaseline ? repeated : single, duplicateBaseline ? single : repeated));
        Assert.Contains(duplicateBaseline ? "baseline" : "candidate", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeepsTheCaseCountLimit(bool oversizedBaseline)
    {
        var oversized = Enumerable.Range(0, 501).Select(i => Sample(i.ToString())).ToArray();
        BenchmarkSummary[] empty = [];
        Assert.Throws<InvalidDataException>(() => BenchmarkSummaryComparer.Compare(
            oversizedBaseline ? oversized : empty, oversizedBaseline ? empty : oversized));
    }

    [Fact]
    public void CaseMatchingRemainsOrdinal()
    {
        var result = BenchmarkSummaryComparer.Compare([Sample("Run")], [Sample("run")]);
        Assert.Empty(result.Cases);
        Assert.Equal(2, result.IncomparableCases);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(101d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidThresholds(double threshold) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BenchmarkSummaryComparer.Compare([], [], threshold));

    [Theory]
    [InlineData(125d, "slower_observed")]
    [InlineData(75d, "faster_observed")]
    public void ThresholdBoundaryIsInclusive(double mean, string status)
    {
        var result = BenchmarkSummaryComparer.Compare([Sample()], [Sample(mean: mean)], 25);
        Assert.Equal(status, Assert.Single(result.Cases).Status);
    }
}
