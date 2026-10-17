using CoreBenchmarks = Chan.Core.Benchmarks;

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
        CoreBenchmarks.BenchmarkSummaryComparer.ValidateThreshold(thresholdPercent);
        if (baselineId == candidateId)
            throw new ArgumentException("Choose different experiment IDs.");

        var (baselineManifest, _) = _experiments.Read(baselineId);
        var (candidateManifest, _) = _experiments.Read(candidateId);
        if (baselineManifest.Kind != candidateManifest.Kind ||
            baselineManifest.Kind is not ("benchmark" or "companion_benchmark"))
            throw new ArgumentException("Both benchmark reports must have the same generated/companion origin.");
        if (baselineManifest.Kind == "companion_benchmark" &&
            (baselineManifest.CompanionBenchmarkJob != candidateManifest.CompanionBenchmarkJob ||
             baselineManifest.CompanionProjectPath is null || candidateManifest.CompanionProjectPath is null ||
             !TenkaChan.Mcp.Agent.AgentInvestigationStore.SameProject(baselineManifest.CompanionProjectPath, candidateManifest.CompanionProjectPath)))
            throw new InvalidOperationException("Different companion projects; workloads are not reliably comparable.");
        if (!string.Equals(baselineManifest.SourceSha256, candidateManifest.SourceSha256,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Different benchmark source hashes. Equivalent workloads are required.");

        var baseline = _experiments.ReadExecutionResult(baselineId);
        var candidate = _experiments.ReadExecutionResult(candidateId);
        if (baseline.Status != "benchmark_completed" || candidate.Status != "benchmark_completed")
            throw new InvalidOperationException("Both runs need completed BenchmarkDotNet reports.");

        var result = CoreBenchmarks.BenchmarkSummaryComparer.Compare(
            baseline.Benchmarks.Select(ToSummary).ToArray(),
            candidate.Benchmarks.Select(ToSummary).ToArray(), thresholdPercent);
        return new BenchmarkRunComparison(baselineId, candidateId,
            result.Status, result.ComparedCases, result.IncomparableCases,
            result.Cases.Select(item => new BenchmarkCaseComparison(
                item.CaseId, item.Status, item.BaselineMeanNanoseconds,
                item.CandidateMeanNanoseconds, item.MeanChangePercent,
                item.BaselineAllocatedBytes, item.CandidateAllocatedBytes,
                item.BaselineSamples, item.CandidateSamples)).ToArray(), result.Note);
    }

    private static CoreBenchmarks.BenchmarkSummary ToSummary(BenchmarkObservation item) =>
        new(item.CaseId, item.SampleCount, item.MeanNanoseconds,
            item.AllocatedBytesPerOperation, item.EnvironmentFingerprint);
}
