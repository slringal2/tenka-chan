namespace TenkaChan.Mcp.Analysis;

public sealed record PerformanceTriageEntry(string MethodId, string DisplayName,
    string ObservationStatus, double? ExclusiveWeight, double? InclusiveWeight,
    double? ExclusivePercent, double? InclusivePercent, int? ObservationCount,
    double? StaticScore, IReadOnlyList<HotspotSite> StaticSites,
    IReadOnlyList<string> NextChecks);

public sealed record PerformanceTriageReport(string SnapshotId, string Status,
    string RankingBasis, string? ProfileName, string? ProfileSha256, int? ProfileIndex,
    string? Unit, double? TotalWeight, double? UnmappedExclusiveWeight,
    double? UnassignedWeight, int ObservedMethods, int TotalCandidates,
    bool CandidatesTruncated, bool ProfileFramesTruncated, bool StaticDetailsTruncated,
    IReadOnlyList<PerformanceTriageEntry> Entries,
    IReadOnlyList<SampledFrameCost> UnmatchedFrames,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Combines independent screening/observation evidence without converting
/// heuristic scores into runtime costs or treating missing samples as zero.
/// </summary>
public static class PerformanceTriageEngine
{
    public static PerformanceTriageReport Build(StaticHotspotReport screening,
        SampledHotspotReport? profile, string ranking = "self", int limit = 100)
    {
        if (ranking is not ("self" or "inclusive"))
            throw new ArgumentException("ranking must be self or inclusive.");
        limit = Math.Clamp(limit, 1, 200);
        if (profile is not null) Validate(profile);
        var staticById = screening.Methods.ToDictionary(m => m.Method.Id, StringComparer.Ordinal);
        var sitesById = screening.Sites.GroupBy(s => s.MethodId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<HotspotSite>)g.Take(20).ToArray(), StringComparer.Ordinal);
        var observations = (profile?.MethodCosts ?? []).ToDictionary(m => m.MethodId, StringComparer.Ordinal);
        var ids = staticById.Keys.Concat(observations.Keys).Distinct(StringComparer.Ordinal);
        var entries = new List<PerformanceTriageEntry>();
        foreach (var id in ids)
        {
            observations.TryGetValue(id, out var observed);
            staticById.TryGetValue(id, out var hint);
            var sites = sitesById.GetValueOrDefault(id) ?? [];
            // Zero-cost static-only methods add noise; observed methods are
            // retained even when no static pattern was found or returned.
            if (observed is null && (hint is null || hint.SuspicionScore <= 0)) continue;
            entries.Add(new PerformanceTriageEntry(id,
                hint?.Method.Signature ?? observed!.DisplayName,
                observed is null ? (profile is null ? "profile_not_loaded" : "not_observed_not_proven_cold")
                    : "observed_qualified_name_only",
                observed?.ExclusiveWeight, observed?.InclusiveWeight,
                observed is null ? null : observed.ExclusiveWeight / profile!.TotalWeight * 100,
                observed is null ? null : observed.InclusiveWeight / profile!.TotalWeight * 100,
                observed?.ObservationCount, hint?.SuspicionScore, sites,
                Checks(observed, sites)));
        }
        var ordered = entries.OrderByDescending(e => e.ExclusiveWeight.HasValue)
            .ThenByDescending(e => ranking == "self" ? e.ExclusiveWeight : e.InclusiveWeight)
            .ThenByDescending(e => e.StaticScore)
            .ThenBy(e => e.MethodId, StringComparer.Ordinal).ToArray();
        return new PerformanceTriageReport(screening.SnapshotId,
            profile is null ? "static_only" : "profile_guided",
            profile is null ? "static_suspicion_not_time" : ranking + "_profile_weight",
            profile?.ProfileName, profile?.SourceSha256, profile?.ProfileIndex,
            profile?.Unit, profile?.TotalWeight, profile?.UnmappedExclusiveWeight,
            profile?.UnassignedWeight, observations.Count, ordered.Length,
            ordered.Length > limit, profile?.FramesTruncated ?? false,
            screening.MethodsTruncated || screening.SitesTruncated,
            ordered.Take(limit).ToArray(),
            profile?.Frames.Where(f => f.MatchedMethodId is null).Take(30).ToArray() ?? [],
            [
                "Observed method totals use all frames, even when the frame view is truncated. Missing observations are unknown, not zero-cost methods.",
                "Self attribution is additive. Inclusive attribution overlaps across methods and must not be summed into a total or cumulative share.",
                "Time-like units alone do not establish CPU usage. Thread-time, elapsed intervals, allocation bytes and sample counts have different meanings.",
                "Profile-to-source links are qualified-name associations, not proof of the same binary build. Native/IL evidence must state its own identity.",
                "Static details are bounded; a missing site/score is not proof that a measured method is inexpensive. Unmatched frames and empty-stack weight remain visible.",
                "No target code, profiler, benchmark, test or LLM is executed by this investigation. Performance changes require matched repeated measurements."
            ]);
    }

    private static IReadOnlyList<string> Checks(ProfileMethodCost? observed, IReadOnlyList<HotspotSite> sites)
    {
        var checks = new List<string>();
        if (observed is null) checks.Add("Capture a representative workload before prioritizing this static candidate.");
        else
        {
            checks.Add("Confirm collector semantics, representative inputs, and the profile's application build.");
            if (observed.InclusiveWeight > observed.ExclusiveWeight * 2)
                checks.Add("Inspect callees: much of this method's attributed weight is below it in the stack.");
        }
        if (sites.Any(s => s.Category.Contains("allocation", StringComparison.Ordinal) ||
            s.Category.Contains("boxing", StringComparison.Ordinal) || s.Category.Contains("materialization", StringComparison.Ordinal)))
            checks.Add("Measure allocated bytes/op; inspect captured IL and native allocation helpers without assuming every IL allocation survives JIT.");
        if (sites.Any(s => s.Category.Contains("lock", StringComparison.Ordinal) || s.Category.Contains("blocking", StringComparison.Ordinal) || s.Category.Contains("io_", StringComparison.Ordinal)))
            checks.Add("Separate CPU work from waiting, I/O latency and contention before changing compute instructions.");
        checks.Add("Inspect same-build DLL/PDB evidence and the matching JIT mode, then benchmark equivalent behavior before and after the change.");
        return checks;
    }

    private static void Validate(SampledHotspotReport profile)
    {
        if (!profile.CompleteMethodAttribution)
            throw new InvalidOperationException("Re-import this profile to compute complete method attribution.");
        if (!double.IsFinite(profile.TotalWeight) || profile.TotalWeight <= 0 ||
            !double.IsFinite(profile.UnmappedExclusiveWeight) || profile.UnmappedExclusiveWeight < 0 ||
            !double.IsFinite(profile.UnassignedWeight) || profile.UnassignedWeight < 0)
            throw new InvalidDataException("Invalid profile weight summary.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        double attributed = profile.UnmappedExclusiveWeight + profile.UnassignedWeight;
        foreach (var m in profile.MethodCosts)
        {
            if (!ids.Add(m.MethodId) || !double.IsFinite(m.InclusiveWeight) || !double.IsFinite(m.ExclusiveWeight) ||
                m.ExclusiveWeight < 0 || m.InclusiveWeight < m.ExclusiveWeight ||
                m.InclusiveWeight > profile.TotalWeight * (1 + 1e-9))
                throw new InvalidDataException("Invalid/duplicated method attribution.");
            attributed += m.ExclusiveWeight;
        }
        if (!double.IsFinite(attributed) || Math.Abs(attributed - profile.TotalWeight) > profile.TotalWeight * 1e-9)
            throw new InvalidDataException("Profile self weights do not reconcile to the selected profile total.");
    }
}
