namespace TenkaChan.Mcp.Analysis;

public sealed record PerformanceComparison(
    string Status, string Unit, string Environment,
    double BaselineMedian, double CandidateMedian, double PercentChange,
    double Lower95Percent, double Upper95Percent, string Note);

/// <summary>Compatibility facade; numerical work lives in Chan.Core.</summary>
public static class PerformanceStatistics
{
    public static PerformanceComparison Compare(double[] baseline, double[] candidate,
        string baselineEnvironment, string candidateEnvironment,
        string unit = "ns/op", double thresholdPercent = 5)
    {
        var result = Chan.Core.Performance.PerformanceStatistics.Compare(
            baseline, candidate, baselineEnvironment, candidateEnvironment, unit, thresholdPercent);
        return new PerformanceComparison(result.Status, result.Unit, result.Environment,
            result.BaselineMedian, result.CandidateMedian, result.PercentChange,
            result.Lower95Percent, result.Upper95Percent, result.Note);
    }
}
