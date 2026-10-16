namespace Chan.Core.Benchmarks;

/// <summary>
/// Adapter-normalized measurements in nanoseconds/op and bytes/op. Case IDs must
/// include all workload/job parameters; fingerprints must include the relevant
/// hardware, runtime/compiler and build settings. Provenance checks belong to adapters.
/// </summary>
public sealed record BenchmarkSummary(
    string CaseId, int SampleCount, double? MeanNanoseconds,
    double? AllocatedBytesPerOperation, string EnvironmentFingerprint = "");

public sealed record BenchmarkCaseComparison(
    string CaseId, string Status, double BaselineMeanNanoseconds,
    double CandidateMeanNanoseconds, double MeanChangePercent,
    double? BaselineAllocatedBytes, double? CandidateAllocatedBytes,
    int BaselineSamples, int CandidateSamples);

public sealed record BenchmarkSummaryComparison(
    string Status, int ComparedCases, int IncomparableCases,
    IReadOnlyList<BenchmarkCaseComparison> Cases, string Note);

/// <summary>
/// Descriptive comparison only. The caller must establish equivalent workloads
/// and completed runs before using this engine; matching case IDs are not proof.
/// </summary>
public static class BenchmarkSummaryComparer
{
    public static void ValidateThreshold(double thresholdPercent)
    {
        if (!double.IsFinite(thresholdPercent) || thresholdPercent <= 0 || thresholdPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent));
    }

    public static BenchmarkSummaryComparison Compare(
        IReadOnlyList<BenchmarkSummary> baseline, IReadOnlyList<BenchmarkSummary> candidate,
        double thresholdPercent = 5)
    {
        ValidateThreshold(thresholdPercent);
        var baselineCases = GroupUnique(baseline, "baseline");
        var candidateCases = GroupUnique(candidate, "candidate");
        var cases = new List<BenchmarkCaseComparison>();
        var incomparable = 0;
        foreach (var old in baselineCases)
        {
            if (!candidateCases.TryGetValue(old.Key, out var current))
            {
                incomparable++;
                continue;
            }

            var previous = old.Value;
            if (string.IsNullOrWhiteSpace(previous.EnvironmentFingerprint)
                || previous.EnvironmentFingerprint != current.EnvironmentFingerprint
                || previous.MeanNanoseconds is not > 0
                || current.MeanNanoseconds is not > 0
                || !double.IsFinite(previous.MeanNanoseconds.Value)
                || !double.IsFinite(current.MeanNanoseconds.Value))
            {
                incomparable++;
                continue;
            }

            var change = (current.MeanNanoseconds.Value /
                          previous.MeanNanoseconds.Value - 1d) * 100d;
            if (!double.IsFinite(change))
            {
                incomparable++;
                continue;
            }
            var status = previous.SampleCount < 5 || current.SampleCount < 5
                ? "insufficient_samples"
                : change >= thresholdPercent ? "slower_observed"
                : change <= -thresholdPercent ? "faster_observed"
                : "difference_below_threshold";

            cases.Add(new BenchmarkCaseComparison(old.Key, status,
                previous.MeanNanoseconds.Value, current.MeanNanoseconds.Value,
                Math.Round(change, 2),
                previous.AllocatedBytesPerOperation, current.AllocatedBytesPerOperation,
                previous.SampleCount, current.SampleCount));
        }
        incomparable += candidateCases.Keys.Count(key => !baselineCases.ContainsKey(key));

        return new BenchmarkSummaryComparison(
            incomparable == 0 ? "summaries_compared" : "partial_comparison",
            cases.Count, incomparable, cases,
            "Comparisons are descriptive only, even when above threshold. " +
            "Aggregated mean values do not establish statistical significance or " +
            "prove a Git regression. Re-run with interleaved launches and raw samples.");
    }

    private static Dictionary<string, BenchmarkSummary> GroupUnique(
        IReadOnlyList<BenchmarkSummary> entries, string label)
    {
        if (entries.Count > 500)
            throw new InvalidDataException("Too many benchmarks for a pair comparison.");
        var dict = new Dictionary<string, BenchmarkSummary>(StringComparer.Ordinal);
        foreach (var item in entries)
        {
            if (!dict.TryAdd(item.CaseId, item))
                throw new InvalidDataException($"Ambiguous repeated benchmark case in {label}: {item.CaseId}");
        }
        return dict;
    }
}
