namespace TenkaChan.Mcp.Experiments;

public sealed record BenchmarkCaseComparison(
    string CaseId, string Status, double BaselineMeanNanoseconds,
    double CandidateMeanNanoseconds, double MeanChangePercent,
    double? BaselineAllocatedBytes, double? CandidateAllocatedBytes,
    int BaselineSamples, int CandidateSamples);

public sealed record BenchmarkRunComparison(
    string BaselineExperimentId, string CandidateExperimentId,
    string Status, int ComparedCases, int IncomparableCases,
    IReadOnlyList<BenchmarkCaseComparison> Cases, string Note);

/// <summary>
/// Compare two stored BenchmarkDotNet reports if they were created from the
/// same benchmark source and on a matching host/runtime. No statistical
/// significance can be inferred from aggregated means alone.
/// </summary>
public sealed class BenchmarkRunComparer
{
    private readonly ExperimentStore _experiments;

    public BenchmarkRunComparer(ExperimentStore experiments) => _experiments = experiments;

    public BenchmarkRunComparison Compare(string baselineId, string candidateId,
        double thresholdPercent = 5)
    {
        if (!double.IsFinite(thresholdPercent) || thresholdPercent <= 0 || thresholdPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent));
        if (baselineId == candidateId)
            throw new ArgumentException("Choose different experiment IDs.");

        var (baselineManifest, _) = _experiments.Read(baselineId);
        var (candidateManifest, _) = _experiments.Read(candidateId);
        if (baselineManifest.Kind != "benchmark" || candidateManifest.Kind != "benchmark")
            throw new ArgumentException("Both experiments must be benchmarks.");
        if (!string.Equals(baselineManifest.SourceSha256, candidateManifest.SourceSha256,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Different benchmark source hashes. Equivalent workloads are required.");

        var baseline = _experiments.ReadExecutionResult(baselineId);
        var candidate = _experiments.ReadExecutionResult(candidateId);
        if (baseline.Status != "benchmark_completed" || candidate.Status != "benchmark_completed")
            throw new InvalidOperationException("Both runs need completed BenchmarkDotNet reports.");

        var baselineCases = GroupUnique(baseline.Benchmarks, "baseline");
        var candidateCases = GroupUnique(candidate.Benchmarks, "candidate");
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
                || current.MeanNanoseconds is not > 0)
            {
                incomparable++;
                continue;
            }

            var change = (current.MeanNanoseconds.Value /
                          previous.MeanNanoseconds.Value - 1d) * 100d;
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

        return new BenchmarkRunComparison(baselineId, candidateId,
            incomparable == 0 ? "summaries_compared" : "partial_comparison",
            cases.Count, incomparable, cases,
            "Comparisons are descriptive only, even when above threshold. " +
            "Aggregated mean values do not establish statistical significance or " +
            "prove a Git regression. Re-run with interleaved launches and raw samples.");
    }

    private static Dictionary<string, BenchmarkObservation> GroupUnique(
        IReadOnlyList<BenchmarkObservation> entries, string label)
    {
        if (entries.Count > 500)
            throw new InvalidDataException("Too many benchmarks for a pair comparison.");
        var dict = new Dictionary<string, BenchmarkObservation>(StringComparer.Ordinal);
        foreach (var item in entries)
        {
            if (!dict.TryAdd(item.CaseId, item))
                throw new InvalidDataException($"Ambiguous repeated benchmark case in {label}: {item.CaseId}");
        }
        return dict;
    }
}
