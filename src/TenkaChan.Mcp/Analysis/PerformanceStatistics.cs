namespace TenkaChan.Mcp.Analysis;

public sealed record PerformanceComparison(
    string Status, string Unit, string Environment,
    double BaselineMedian, double CandidateMedian, double PercentChange,
    double Lower95Percent, double Upper95Percent, string Note);

public static class PerformanceStatistics
{
    public static PerformanceComparison Compare(double[] baseline, double[] candidate,
        string baselineEnvironment, string candidateEnvironment,
        string unit = "ns/op", double thresholdPercent = 5)
    {
        if (baselineEnvironment != candidateEnvironment || string.IsNullOrWhiteSpace(baselineEnvironment))
            throw new ArgumentException("Benchmark environment fingerprints must match.");
        if (baseline.Length < 5 || candidate.Length < 5)
            throw new ArgumentException("Provide at least five independent samples per revision.");
        if (baseline.Concat(candidate).Any(x => !double.IsFinite(x) || x <= 0))
            throw new ArgumentException("Samples must be finite and positive.");
        if (!double.IsFinite(thresholdPercent) || thresholdPercent <= 0 || thresholdPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent));

        var oldMedian = Median(baseline);
        var newMedian = Median(candidate);
        var change = (newMedian / oldMedian - 1) * 100;
        var rng = new Random(1847); // reproducible bootstrap, heuristic rather than a formal significance test
        var ratios = new double[2000];
        for (var i = 0; i < ratios.Length; i++)
        {
            var oldDraw = new double[baseline.Length];
            var newDraw = new double[candidate.Length];
            for (var j = 0; j < oldDraw.Length; j++) oldDraw[j] = baseline[rng.Next(baseline.Length)];
            for (var j = 0; j < newDraw.Length; j++) newDraw[j] = candidate[rng.Next(candidate.Length)];
            ratios[i] = (Median(newDraw) / Median(oldDraw) - 1) * 100;
        }
        Array.Sort(ratios);
        var lower = ratios[49];
        var upper = ratios[1949];
        var status = change >= thresholdPercent && lower > 0 ? "regression_candidate"
            : change <= -thresholdPercent && upper < 0 ? "improvement_candidate"
            : "inconclusive";
        return new PerformanceComparison(status, unit, baselineEnvironment,
            oldMedian, newMedian, change, lower, upper,
            "Exploratory bootstrap interval only. Ensure matched workloads, steady-state JIT, randomized run order and comparable hardware. Reconfirm before reporting a regression.");
    }

    private static double Median(double[] values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 != 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
