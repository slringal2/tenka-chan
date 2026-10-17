using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using Core = Chan.Core.Benchmarks;

namespace TenkaChan.Mcp.Tracking;

/// <summary>A server observation at execution time, not a PDB/source-build identity proof.</summary>
public sealed record ReviewRunObservation(string ExperimentId, string TargetId, string ProjectPath,
    string SnapshotId, string SourceHash, string ContextHash, bool SourceInputsUnchanged,
    string ReportSha256, string CompletedUtc);

public sealed record ReviewBenchmarkEvidence(string InvestigationId, string TargetId,
    string CandidateSourceHash, string CandidateContextHash, string Status, string RecordedUtc,
    string WorkloadSha256, string RuntimeFramework, string SubjectFramework,
    ReviewRunObservation Baseline, ReviewRunObservation Candidate,
    BenchmarkRunComparison Comparison, IReadOnlyList<string> Limitations);

public sealed record ReviewPerformanceState(string Status, bool HasMeasurements,
    bool IndependentlyVerified, string? BaselineExperimentId, string? CandidateExperimentId,
    int ComparedCases, int IncomparableCases)
{
    public static ReviewPerformanceState Unmeasured => new("unmeasured", false, false, null, null, 0, 0);
    public static ReviewPerformanceState From(ReviewBenchmarkEvidence? evidence, bool stale)
        => evidence is null ? Unmeasured : new(stale ? "stale_measurement" : evidence.Status,
            true, false, evidence.Baseline.ExperimentId, evidence.Candidate.ExperimentId,
            evidence.Comparison.ComparedCases, evidence.Comparison.IncomparableCases);
}

/// <summary>
/// Compares server-read reports and their execution-time target observations. No
/// caller-supplied mean/ratio enters this API. Descriptive results NEVER change
/// checked/optimized, verify behavioral equivalence, or assert significance.
/// </summary>
public sealed record StoredBenchmarkReport(ExecutionResult Report, string Sha256);

public static class ReviewBenchmarkMeasurements
{
    public static StoredBenchmarkReport ReadReport(ExperimentStore store, string id)
    {
        var (_, directory) = store.Read(id); // Retain manifest and workspace path validation.
        var bytes = InvestigationFiles.Bytes(Path.Combine(directory, "execution-report.json"), 1_000_000);
        var report = JsonSerializer.Deserialize<ExecutionResult>(bytes, InvestigationFiles.Json)
            ?? throw new InvalidDataException("Stored execution report is invalid.");
        if (report.ExperimentId != id) throw new InvalidDataException("Stored execution report ID mismatch.");
        return new(report, InvestigationFiles.Hash(bytes)); // Exact saved bytes, not version-sensitive reserialization.
    }

    public static ReviewBenchmarkEvidence Create(string investigationId, ReviewTarget target,
        ExperimentManifest baselineManifest, StoredBenchmarkReport baselineSaved, ReviewRunObservation before,
        ExperimentManifest candidateManifest, StoredBenchmarkReport candidateSaved, ReviewRunObservation after)
    {
        InvestigationFiles.Id(investigationId);
        ValidateRun(target, baselineManifest, baselineSaved, before);
        ValidateRun(target, candidateManifest, candidateSaved, after);
        var baseline = baselineSaved.Report; var candidate = candidateSaved.Report;
        if (before.ExperimentId == after.ExperimentId)
            throw new ArgumentException("Choose different before/after experiments.");
        if (after.SourceHash != target.SourceHash || after.ContextHash != target.ContextHash)
            throw new ReviewConflictException("measurement_candidate_stale: Candidate measurement does not match the current source/context. Do not attach it to changed code.");
        if (baselineManifest.SourceSha256 != candidateManifest.SourceSha256 ||
            baselineManifest.ProjectSha256 != candidateManifest.ProjectSha256 ||
            baselineManifest.RunnerSha256 != candidateManifest.RunnerSha256)
            throw new InvalidOperationException("measurement_workload_mismatch: Benchmark code, template and runner must match.");
        var subject = candidateManifest.SubjectTargetFramework ?? candidateManifest.TargetFramework;
        if (baselineManifest.TargetFramework != candidateManifest.TargetFramework ||
            (baselineManifest.SubjectTargetFramework ?? baselineManifest.TargetFramework) != subject)
            throw new InvalidOperationException("measurement_framework_mismatch: Subject and runtime frameworks must match.");
        if (!DateTimeOffset.TryParse(baseline.CompletedUtc, out var completedBefore) ||
            !DateTimeOffset.TryParse(candidate.StartedUtc, out var startedAfter) || completedBefore > startedAfter)
            throw new InvalidOperationException("measurement_order_mismatch: The baseline must complete before the candidate starts.");
        var compared = Core.BenchmarkSummaryComparer.Compare(
            baseline.Benchmarks.Select(ToSummary).ToArray(), candidate.Benchmarks.Select(ToSummary).ToArray());
        var comparison = new BenchmarkRunComparison(before.ExperimentId, after.ExperimentId,
            compared.Status, compared.ComparedCases, compared.IncomparableCases,
            compared.Cases.Select(c => new BenchmarkCaseComparison(c.CaseId, c.Status,
                c.BaselineMeanNanoseconds, c.CandidateMeanNanoseconds, c.MeanChangePercent,
                c.BaselineAllocatedBytes, c.CandidateAllocatedBytes, c.BaselineSamples, c.CandidateSamples)).ToArray(), compared.Note);
        var status = compared.ComparedCases == 0 || compared.IncomparableCases > 0 ? "incomparable"
            : compared.Cases.Any(c => c.Status == "insufficient_samples") ? "insufficient_samples" : "measured_comparison";
        var evidence = new ReviewBenchmarkEvidence(investigationId, target.Id,
            target.SourceHash, target.ContextHash, status, DateTimeOffset.UtcNow.ToString("O"),
            candidateManifest.SourceSha256, candidateManifest.TargetFramework, subject, before, after, comparison,
            ["Values come from saved BenchmarkDotNet execution reports, not the agent's optimization claim.",
             "Target source/context was observed under the workspace gate during each run. The existing built DLL may still differ from that source: binary/source identity is NOT verified.",
             "The benchmark author must independently justify workload coverage and test oracles. Attaching a report does not prove this method was exercised or behavior was preserved.",
             "Matching captured environment fingerprints and aggregate means are descriptive, not statistical significance or production performance guarantees.",
             "Local reports and fingerprints are not tamper-proof against code running as the same OS user."]);
        ValidateAttachment(target, evidence);
        return evidence;
    }
    private static void ValidateRun(ReviewTarget target, ExperimentManifest manifest,
        StoredBenchmarkReport saved, ReviewRunObservation observation)
    {
        var result = saved.Report;
        InvestigationFiles.Id(manifest.Id);
        if (manifest.Kind != "benchmark" || result.Kind != "benchmark" || result.Status != "benchmark_completed" || result.ExitCode != 0 ||
            result.ExperimentId != manifest.Id || observation.ExperimentId != manifest.Id)
            throw new InvalidOperationException("measurement_not_completed: Both runs must be completed successful benchmark reports with matching IDs.");
        if (observation.TargetId != target.Id || !AgentInvestigationStore.SameProject(target.ProjectPath, observation.ProjectPath) ||
            !AgentInvestigationStore.SameProject(target.ProjectPath, Path.Combine(manifest.RepositoryRoot, manifest.ProjectRelativePath)))
            throw new InvalidOperationException("measurement_target_mismatch: Both experiments and observations must belong to this target/project.");
        if (!observation.SourceInputsUnchanged)
            throw new InvalidOperationException("measurement_inputs_changed: Source inputs changed or could not be checked during the measurement. No current measurement claim can be attached.");
        if (!IsHash(saved.Sha256) || saved.Sha256 != observation.ReportSha256)
            throw new InvalidDataException("measurement_report_changed: Stored report differs from its execution-time receipt.");
        if (!IsHash(manifest.SourceSha256) || !IsHash(manifest.ProjectSha256) || !IsHash(manifest.RunnerSha256) ||
            !IsHash(observation.SourceHash) || !IsHash(observation.ContextHash) || result.TargetFramework != manifest.TargetFramework)
            throw new InvalidDataException("Measurement provenance is incomplete.");
        if (result.Benchmarks.Count is 0 or > 200 || result.Benchmarks.Any(b => b.CaseId.Length > 2000 ||
            b.SampleCount <= 0 || b.MeanNanoseconds is not > 0 || !double.IsFinite(b.MeanNanoseconds.Value) ||
            b.AllocatedBytesPerOperation is double bytes && (!double.IsFinite(bytes) || bytes < 0)))
            throw new InvalidDataException("Measured cases contain missing, invalid, or excessive values.");
    }
    public static void ValidateAttachment(ReviewTarget target, ReviewBenchmarkEvidence evidence)
    {
        if (evidence.TargetId != target.Id || evidence.CandidateSourceHash != target.SourceHash ||
            evidence.CandidateContextHash != target.ContextHash)
            throw new ReviewConflictException("Measurement attachment does not match the target's current source/context.");
        if (evidence.Comparison.Cases.Count > 200 || JsonSerializer.SerializeToUtf8Bytes(evidence, InvestigationFiles.Json).Length > 256_000)
            throw new InvalidOperationException("Measurement attachment exceeds budget; use a narrower benchmark workload.");
    }
    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static Core.BenchmarkSummary ToSummary(BenchmarkObservation b) =>
        new(b.CaseId, b.SampleCount, b.MeanNanoseconds, b.AllocatedBytesPerOperation, b.EnvironmentFingerprint);
}
