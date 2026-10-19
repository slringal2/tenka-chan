using TenkaChan.Mcp.Experiments;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// A name-correlated profile is observed evidence, not a verified association
/// between the currently loaded Roslyn source, executed module or CPU cycles.
/// </summary>
public sealed record DynamicObservedMethod(
    string MethodId, string Project, string Signature, string File, int Line,
    double ExclusiveWeight, double ExclusivePercent, int ContributingFrames,
    int FrameObservations, IReadOnlyList<string> ExampleFrames,
    double? StaticSuspicionScore, IReadOnlyList<string> StaticCandidates)
{
    public string Association => "profile_frame_name_to_loaded_roslyn_method__build_unverified";
    public bool BinariesVerified => false;
}

public sealed record DynamicUnlinkedFrame(
    string Name, string MatchStatus, double ExclusiveWeight, double ExclusivePercent);

/// <summary>Exception type frequencies cannot verify throw sites or crashes.</summary>
public sealed record DynamicExceptionHotspot(
    int ProcessId, int ClrInstanceId, string TypeName, int ObservedThrows,
    int RethrowFlagsObserved, double FirstThrownMs, double LastThrownMs)
{
    public string SourceAttribution => "pid_clr_exception_type_only__throw_site_unresolved";
    public bool CrashConfirmed => false;
}

/// <summary>
/// Runtime events are process/CLR observations only. They cannot be attributed
/// to any source method without reliable event-to-stack/assembly linkage.
/// </summary>
public sealed record DynamicRuntimeContext(
    int ProcessId, int ClrInstanceId, int ObservedEvents,
    int CompletedGcCollections, int GcSuspensionWindows,
    double GcSuspensionWindowTotalMs, double MaximumGcSuspensionWindowMs,
    int AllocationTicks, ulong TickReportedAllocationBytes,
    int MonitorContentionDurations, double OverlappingMonitorWaitMs,
    IReadOnlyList<string> AllocationTriggerTypes,
    IReadOnlyList<string> InvestigationHints,
    IReadOnlyDictionary<string, int> IncompleteEvidence)
{
    public string SourceAttribution => "process_clr_only__not_per_method";
    public int ExceptionThrows { get; init; }
    public int ExceptionKindsRetained { get; init; }
    public IReadOnlyList<RuntimeExceptionType> ExceptionTypes { get; init; } = [];
    public bool ExceptionTypeListTruncated { get; init; }
}

public sealed record DynamicTraceDiagnosis(
    string SnapshotId, string ArtifactId, string CaptureId, int ProfileIndex,
    string ProfileName, string ProfileType, string Unit, int TotalObservations,
    double TotalWeight, int TotalObservedFrames, int LinkedFrames, int AmbiguousFrames,
    double LinkedExclusiveWeight, double UnlinkedExclusiveWeight,
    double HiddenOrUnassignedExclusiveWeight, bool FrameListTruncated,
    bool MethodListTruncated, bool RuntimeListTruncated,
    IReadOnlyList<DynamicObservedMethod> Methods,
    IReadOnlyList<DynamicUnlinkedFrame> UnlinkedExamples,
    string RuntimeEventStatus, int RuntimeEventsRead, int ReportedEventsLost,
    bool RuntimeEventLimitReached, IReadOnlyList<DynamicRuntimeContext> Runtime,
    IReadOnlyList<string> Limitations)
{
    public bool BinariesVerified => false;
    public bool PerMethodCpuVerified => false;
    public string Status => "dynamic_observation_unverified";
    public string EvidenceBasis => "sampled_or_evented_speedscope_plus_separate_process_runtime_events";
    public int TotalExceptionThrows { get; init; }
    public bool ExceptionHotspotsTruncated { get; init; }
    public IReadOnlyList<DynamicExceptionHotspot> ExceptionHotspots { get; init; } = [];
}

/// <summary>
/// Deterministic read-only fusion of saved trace measurements. Never
/// incorporates GC/Monitor/allocation counters into per-method costs.
/// </summary>
public static class DynamicTraceDiagnosisBuilder
{
    public static DynamicTraceDiagnosis Build(
        string snapshotId, string artifactId, TraceCaptureReport capture,
        int profileIndex, SampledHotspotReport profile,
        RuntimeCostReport runtime, IReadOnlyDictionary<string, MethodSummary> methods,
        StaticHotspotReport? staticReport = null, int maxMethods = 30)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || string.IsNullOrWhiteSpace(artifactId))
            throw new ArgumentException("A current snapshot and a trace artifact ID are required.");
        if (capture.Status != "trace_completed" || capture.NetTrace is null ||
            capture.Speedscope is null || capture.Id != runtime.CaptureId ||
            capture.NetTrace.Sha256 != runtime.TraceSha256)
            throw new InvalidOperationException("The runtime observations and sampled profile must belong to one completed, hash-matched trace.");
        if (staticReport is not null && staticReport.SnapshotId != snapshotId)
            throw new InvalidOperationException("Static findings belong to another Roslyn snapshot.");
        if (profileIndex < 0 || profileIndex >= capture.Profiles.Count)
            throw new ArgumentOutOfRangeException(nameof(profileIndex));
        if (!string.Equals(profile.ProfileName, capture.Profiles[profileIndex].Name, StringComparison.Ordinal) ||
            !string.Equals(profile.ProfileType, capture.Profiles[profileIndex].Type, StringComparison.Ordinal) ||
            !string.Equals(profile.Unit, capture.Profiles[profileIndex].Unit, StringComparison.Ordinal))
            throw new InvalidOperationException("Selected profile does not match the saved trace's profile catalog.");
        if (profile.TotalWeight <= 0 || !double.IsFinite(profile.TotalWeight) ||
            profile.Frames.Count > profile.TotalFrames)
            throw new InvalidDataException("Invalid sampled-profile totals.");
        maxMethods = Math.Clamp(maxMethods, 1, 100);

        var ranked = staticReport?.Methods.ToDictionary(m => m.Method.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, HotspotMethodRanking>(StringComparer.Ordinal);
        var sites = staticReport?.Sites.GroupBy(s => s.MethodId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Weight).Take(4).ToArray(),
                StringComparer.Ordinal)
            ?? new Dictionary<string, HotspotSite[]>(StringComparer.Ordinal);

        // Frame labels can differ for the same source method (e.g. JIT variants).
        // Exclusive leaf weights may be summed because one stack has one leaf.
        // Inclusive weights and counts must NOT be added: repeated/recursive
        // frames can occur in one stack, and the identities remain name-based.
        var linked = profile.Frames.Where(f =>
            f.MatchedMethodId is not null &&
            f.MatchStatus == "matched_exact_qualified_name" &&
            methods.ContainsKey(f.MatchedMethodId)).ToArray();

        var groups = linked.GroupBy(f => f.MatchedMethodId!, StringComparer.Ordinal)
            .Select(group =>
            {
                var method = methods[group.Key];
                ranked.TryGetValue(group.Key, out var staticMethod);
                sites.TryGetValue(group.Key, out var staticSites);
                var ordered = group.OrderByDescending(f => f.ExclusiveWeight)
                    .ThenBy(f => f.FrameName, StringComparer.Ordinal).ToArray();
                var exclusive = group.Sum(f => f.ExclusiveWeight);
                return new DynamicObservedMethod(group.Key, method.Project, method.Signature,
                    method.File, method.Line, Math.Round(exclusive, 6),
                    Math.Round(exclusive / profile.TotalWeight * 100, 3),
                    group.Count(), group.Sum(f => f.SampleCount),
                    ordered.Take(4).Select(f => f.FrameName).ToArray(),
                    staticMethod?.SuspicionScore,
                    staticSites?.Select(s => s.Category + " at line " + s.Line)
                        .Distinct(StringComparer.Ordinal).Take(4).ToArray() ?? []);
            })
            .OrderByDescending(x => x.ExclusiveWeight)
            .ThenBy(x => x.MethodId, StringComparer.Ordinal).ToArray();

        var unlinked = profile.Frames.Except(linked).ToArray();
        var linkedWeight = linked.Sum(x => x.ExclusiveWeight);
        var unlinkedWeight = unlinked.Sum(x => x.ExclusiveWeight);
        var retainedWeight = linkedWeight + unlinkedWeight;
        // Speedscope can contain empty stacks and the parser intentionally
        // returns only a bounded number of observed frames.
        var unassigned = Math.Max(0d, profile.TotalWeight - retainedWeight);
        var processes = runtime.Runtimes.OrderByDescending(r => r.GcSuspensionWindowTotalMs)
            .ThenByDescending(r => r.ContentionWaitMilliseconds)
            .ThenBy(r => r.ProcessId).ThenBy(r => r.ClrInstanceId).ToArray();
        var processRows = processes.Take(12).Select(r => new DynamicRuntimeContext(
            r.ProcessId, r.ClrInstanceId, r.ObservedEvents,
            r.CollectionsCompleted, r.GcSuspensionWindows,
            r.GcSuspensionWindowTotalMs, r.MaximumGcSuspensionWindowMs,
            r.AllocationTicks, r.TickReportedAllocationBytes,
            r.ContentionDurationsReported, r.ContentionWaitMilliseconds,
            r.AllocationTriggerTypes.OrderByDescending(t => t.TickCount)
                .ThenBy(t => t.TriggerType, StringComparer.Ordinal)
                .Take(5).Select(t => t.TriggerType).ToArray(),
            r.InvestigationHints.Take(6).Select(h => h.Kind + ": " + h.Evidence).ToArray(),
            r.IncompleteEvidence)
        {
            ExceptionThrows = r.ExceptionThrows,
            ExceptionKindsRetained = r.ExceptionKindsRetained,
            ExceptionTypes = r.ExceptionTypes.Take(5).ToArray(),
            ExceptionTypeListTruncated = r.ExceptionTypeListTruncated || r.ExceptionTypes.Count > 5
        }).ToArray();

        // Keep throw frequencies separately visible even if a process falls
        // outside the GC-suspension-ranked 12 process context rows. Frequencies
        // are not added to any sampled-method weight.
        var exceptionRows = runtime.Runtimes.SelectMany(r => r.ExceptionTypes.Select(e =>
            new DynamicExceptionHotspot(r.ProcessId, r.ClrInstanceId, e.TypeName,
                e.ObservedThrows, e.RethrowFlagsObserved,
                e.FirstThrownMs, e.LastThrownMs)))
            .OrderByDescending(e => e.ObservedThrows)
            .ThenBy(e => e.TypeName, StringComparer.Ordinal)
            .ThenBy(e => e.ProcessId).ThenBy(e => e.ClrInstanceId).ToArray();
        var totalExceptionThrows = runtime.Runtimes.Sum(r => r.ExceptionThrows);
        var exceptionTruncated = exceptionRows.Length > 20 ||
            runtime.Runtimes.Any(r => r.ExceptionTypeListTruncated);

        var limitations = new List<string>
        {
            "Sampled frame exclusive weights are observed thread-time or other declared units, NOT independently measured per-method on-CPU time.",
            "Frames are linked to source by qualified names only. The recorded entry DLL SHA-256/MVID is not proof the loaded source or all runtime dependencies match that binary; binariesVerified=false.",
            "GC suspension, allocation ticks and Monitor contention are PID/CLR-level context only. They are NOT added to method weights or assigned as the cause of any method.",
            "Summed allocation tick bytes are approximate allocation-volume observations, NOT byte allocations of the named trigger type. Monitor waiter durations can overlap.",
            "Frame observation counts across frame labels may include duplicate samples; only exclusive weights are safely summed. Inclusive weights are intentionally not summed.",
            "ExceptionThrown events count first-chance throws by type, including expected/caught exceptions. They are not proof of crashes, unhandled exceptions, throw-site methods, or CPU cost; exception messages are not retained.",
            "No deadlock, heap-leak, true on-CPU time or per-method allocation-stack diagnosis is inferred from this report."
        };
        if (profile.TotalFrames > profile.Frames.Count)
            limitations.Add("The frame list was truncated by its 500-frame parser limit; missing methods and their weight are not considered zero.");
        if (staticReport?.MethodsTruncated == true || staticReport?.SitesTruncated == true)
            limitations.Add("Static screening findings were truncated; absence of a static candidate is not evidence of safety.");
        if (runtime.EventLimitReached || runtime.ReportedEventsLost > 0)
            limitations.Add("The original event trace is partial or reports dropped events; runtime counters are lower-bound observations, not totals.");
        if (runtime.Status == "no_supported_events_observed")
            limitations.Add("No supported GC/allocation/contention/exception events were observed. Providers or workload coverage may be insufficient; do not interpret missing events as zero.");
        if (runtime.RuntimeProviderConfiguration != TrustedTraceCapture.RuntimeCostProviders)
            limitations.Add("Verbose CLR runtime-cost providers were not confirmed in this capture; allocation/contention observations may be absent.");

        return new DynamicTraceDiagnosis(snapshotId, artifactId, capture.Id, profileIndex,
            profile.ProfileName, profile.ProfileType, profile.Unit, profile.TotalSamples,
            profile.TotalWeight, profile.TotalFrames, profile.LinkedFrames, profile.AmbiguousFrames,
            Math.Round(linkedWeight, 6), Math.Round(unlinkedWeight, 6),
            Math.Round(unassigned, 6), profile.TotalFrames > profile.Frames.Count,
            groups.Length > maxMethods, processes.Length > processRows.Length,
            groups.Take(maxMethods).ToArray(),
            unlinked.OrderByDescending(x => x.ExclusiveWeight)
                .ThenBy(x => x.FrameName, StringComparer.Ordinal).Take(10)
                .Select(x => new DynamicUnlinkedFrame(x.FrameName,
                    x.MatchedMethodId is not null && !methods.ContainsKey(x.MatchedMethodId)
                        ? "roslyn_method_not_in_current_type_index" : x.MatchStatus,
                    x.ExclusiveWeight, x.ExclusivePercent)).ToArray(),
            runtime.Status, runtime.EventsRead, runtime.ReportedEventsLost,
            runtime.EventLimitReached, processRows, limitations)
        {
            TotalExceptionThrows = totalExceptionThrows,
            ExceptionHotspotsTruncated = exceptionTruncated,
            ExceptionHotspots = exceptionRows.Take(20).ToArray()
        };
    }
}
