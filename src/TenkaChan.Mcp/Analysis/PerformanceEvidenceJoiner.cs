namespace TenkaChan.Mcp.Analysis;

public sealed record NativeCodeHint(
    string JitSignature, string Architecture, string JitMode,
    int? CodeSizeBytes, int InstructionCount, bool OffsetsReliable,
    string OffsetValidation, IReadOnlyList<string> Signals, string MatchStatus);

public sealed record PerformanceTriageEntry(
    string Name, string? MethodId, string EvidenceOrigin,
    string MatchStatus, string? Unit,
    double? ExclusiveWeight, double? InclusiveWeight,
    double? ExclusivePercent, double? InclusivePercent, int? Observations,
    double? StaticSuspicionScore, IReadOnlyList<string> StaticReasons,
    IReadOnlyList<HotspotSite> StaticSites,
    IReadOnlyList<NativeCodeHint> NativeMethods,
    IReadOnlyList<string> Caveats);

/// <summary>
/// BenchmarkDotNet measurements remain an independent experiment-level
/// observation; matching a benchmark case name to a profiled source method
/// would not establish equal workload or shared call-stack attribution.
/// </summary>
public sealed record StandaloneBenchmarkCase(
    string CaseId, int SampleCount,
    double? MeanNanoseconds, double? MedianNanoseconds,
    double? AllocatedBytesPerOperation, string EnvironmentFingerprint);

public sealed record StandaloneBenchmarkEvidence(
    string ExperimentId, string TargetFramework, string Status,
    IReadOnlyList<StandaloneBenchmarkCase> Cases,
    IReadOnlyList<string> Limitations);

public sealed record PerformanceTriageReport(
    string SnapshotId, string PriorityBasis, string ProfileStatus,
    string? ProfileName, string? ProfileType, string? Unit,
    int TotalObservedFrames, int TotalStaticMethods, int UnmatchedObservedFrames,
    int AmbiguousObservedFrames, bool ObservedOutputTruncated,
    bool StaticOutputTruncated, string? JitExperimentId,
    IReadOnlyList<PerformanceTriageEntry> Observed,
    IReadOnlyList<PerformanceTriageEntry> StaticOnly,
    IReadOnlyList<string> Limitations)
{
    public StandaloneBenchmarkEvidence? Benchmarks { get; init; }
    public string? LocalJitCaptureId { get; init; }
}

public sealed record PerformanceMethodDossier(
    string MethodId, string SnapshotId, PerformanceTriageEntry? Triage,
    NativeMethodDisassembly? NativeCode, BinarySourceEvidence? IlAndSource,
    string BindingStatus, IReadOnlyList<string> Limitations);

/// <summary>
/// Deterministic evidence joining. Never adds a static score to measured
/// sample weights or upgrades candidate rules to observed CPU time.
/// </summary>
public static class PerformanceEvidenceJoiner
{
    public static PerformanceTriageReport Build(
        string snapshotId, StaticHotspotReport staticReport,
        SampledHotspotReport? observed, NativeCodeReport? native,
        string? jitExperimentId = null, int maxObserved = 50, int maxStatic = 30)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) ||
            !string.Equals(snapshotId, staticReport.SnapshotId, StringComparison.Ordinal))
            throw new InvalidOperationException("Static evidence belongs to a different Roslyn snapshot.");
        if (native?.RoslynSnapshotId is not null && native.RoslynSnapshotId != snapshotId)
            throw new InvalidOperationException("Native method correspondence belongs to a different Roslyn snapshot.");

        maxObserved = Math.Clamp(maxObserved, 1, 200);
        maxStatic = Math.Clamp(maxStatic, 1, 100);
        var ranked = staticReport.Methods.ToDictionary(m => m.Method.Id, StringComparer.Ordinal);
        var sites = staticReport.Sites.Where(s => !string.IsNullOrEmpty(s.MethodId))
            .GroupBy(s => s.MethodId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Weight).Take(8).ToArray(),
                StringComparer.Ordinal);
        var nativeByMethod = native?.Methods
            .Where(m => m.RoslynMethodId is not null &&
                m.MatchStatus is "matched_method_signature" or "matched_unique_method")
            .GroupBy(m => m.RoslynMethodId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => new NativeCodeHint(
                m.JitSignature, m.Architecture, m.JitMode, m.CodeSizeBytes,
                m.Instructions.Count, m.OffsetsReliable, m.OffsetValidation,
                m.Signals.Select(s => s.Kind).Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal).Take(12).ToArray(),
                m.MatchStatus)).Take(4).ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, NativeCodeHint[]>(StringComparer.Ordinal);

        PerformanceTriageEntry Make(
            string name, string? id, string origin, string match,
            SampledFrameCost? frame = null)
        {
            HotspotMethodRanking? method = null;
            if (id is not null) ranked.TryGetValue(id, out method);
            var foundSites = id is not null && sites.TryGetValue(id, out var available)
                ? (IReadOnlyList<HotspotSite>)available : [];
            var nativeMethods = id is not null &&
                nativeByMethod.TryGetValue(id, out var compiled)
                    ? (IReadOnlyList<NativeCodeHint>)compiled : [];
            var caveats = new List<string>();
            if (frame is not null && id is null)
                caveats.Add("No unambiguous Roslyn method link. Do not attach source or JIT evidence to this frame.");
            if (id is not null && method is null)
                caveats.Add("Static ranking not returned for this method; missing ranking is not evidence of low cost.");
            if (nativeMethods.Count > 0)
                caveats.Add("JIT code is matched by method signature only; profile-to-build identity and native-to-IL offsets have not been verified.");
            return new PerformanceTriageEntry(
                name, id, origin, match, frame is null ? null : observed?.Unit,
                frame?.ExclusiveWeight, frame?.InclusiveWeight,
                frame?.ExclusivePercent, frame?.InclusivePercent, frame?.SampleCount,
                method?.SuspicionScore,
                method?.Reasons.Take(4).ToArray() ?? [],
                foundSites, nativeMethods, caveats);
        }

        var orderedFrames = observed?.Frames
            .OrderByDescending(f => f.ExclusiveWeight)
            .ThenByDescending(f => f.InclusiveWeight)
            .ThenBy(f => f.FrameName, StringComparer.Ordinal)
            .ToArray() ?? [];
        var selected = orderedFrames.Take(maxObserved)
            .Select(f => Make(f.FrameName, f.MatchedMethodId,
                "observed_profile", f.MatchStatus, f)).ToArray();

        var observedMethods = orderedFrames.Where(f => f.MatchedMethodId is not null)
            .Select(f => f.MatchedMethodId!)
            .ToHashSet(StringComparer.Ordinal);
        var unobserved = staticReport.Methods
            .Where(m => !observedMethods.Contains(m.Method.Id))
            .OrderByDescending(m => m.SuspicionScore)
            .ThenBy(m => m.Method.Id, StringComparer.Ordinal)
            .ToArray();
        var remaining = unobserved.Take(maxStatic)
            .Select(m => Make(m.Method.Signature, m.Method.Id, "static_candidate_only",
                "static_method_not_observed"))
            .ToArray();

        var basis = observed is null
            ? "static_heuristic_only"
            : "observed_exclusive_" + observed.Unit;
        var limitations = new List<string>
        {
            "Observed profile order is based only on recorded exclusive weight. Static suspicion scores are separate, incomparable quantities.",
            "Sampled thread time and evented elapsed time may include waiting, blocking, scheduling and overhead; neither is necessarily on-CPU time.",
            "Profile-to-source matching relies on name-based Roslyn method resolution; assembly/build identity is not established by speedscope names.",
            "JIT disassembly and IL/PDB facts are code-generation evidence, not proof of measured CPU or allocation costs.",
            "Methods absent from a limited profile or static report are not necessarily cheap or unused."
        };
        if (staticReport.MethodsTruncated || staticReport.SitesTruncated)
            limitations.Add("Static evidence was truncated; missing static rules/sites must not be interpreted as absent.");
        if (observed is not null && observed.TotalFrames > observed.Frames.Count)
            limitations.Add("Imported profiler output was truncated at its frame limit; rankings are not exhaustive.");

        return new PerformanceTriageReport(
            snapshotId, basis,
            observed is null ? "no_profile_loaded" : "imported_workload_observation",
            observed?.ProfileName, observed?.ProfileType, observed?.Unit,
            observed?.TotalFrames ?? 0, staticReport.TotalMethods,
            observed?.Frames.Count(f => f.MatchStatus == "unmatched") ?? 0,
            observed?.AmbiguousFrames ?? 0,
            (observed?.TotalFrames ?? 0) > Math.Min(maxObserved, orderedFrames.Length),
            staticReport.MethodsTruncated || staticReport.SitesTruncated ||
                unobserved.Length > maxStatic,
            jitExperimentId, selected, remaining, limitations);
    }

    public static PerformanceMethodDossier Dossier(
        PerformanceTriageReport triage, string methodId,
        NativeMethodDisassembly? native, BinarySourceEvidence? il,
        string tokenSelectionStatus)
    {
        if (string.IsNullOrWhiteSpace(methodId))
            throw new ArgumentException("Method ID required.", nameof(methodId));
        var entry = triage.Observed.FirstOrDefault(e => e.MethodId == methodId)
            ?? triage.StaticOnly.FirstOrDefault(e => e.MethodId == methodId);
        if (entry is null)
            throw new KeyNotFoundException("Method was not present in the bounded triage report.");

        var caveats = new List<string>(entry.Caveats);
        var binding = tokenSelectionStatus;
        if (il is not null)
        {
            // A PDB ties IL sequence points to a DLL, not native instructions
            // to individual IL offsets. Preserve this distinction.
            caveats.Add("IL/PDB source mapping is tied to the selected captured DLL and MethodDef token; machine-code-to-IL offset mapping is unavailable.");
            if (il.Truncated)
                caveats.Add("IL instructions were truncated by the requested limit.");
            if (il.CostSites.Any(s => s.Source?.LocalSourceStatus == "checksum_mismatch"))
                caveats.Add("Source bytes differ from the captured PDB checksum; display the historic location only, not current source content.");
        }
        else caveats.Add("No captured IL/PDB evidence selected. Native/code-generation observations remain method-level only.");
        return new PerformanceMethodDossier(methodId, triage.SnapshotId,
            entry, native, il, binding, caveats);
    }
}
