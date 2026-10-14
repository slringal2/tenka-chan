using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Analysis;

public sealed record PrioritySuppression(string Id, string Reason);
public sealed record ReviewPriorityOptions(
    double HotSharePercent = 10, int MinimumObservations = 20,
    double StaticSiteThreshold = 40, int MaxItems = 100,
    string? PriorityFilter = null, string? EvidenceFilter = null,
    IReadOnlyList<PrioritySuppression>? Suppressions = null,
    bool IncludeSuppressed = false);

public sealed record PrioritizedFinding(
    string Id, string Kind, string Title, string Priority, string EvidenceLevel,
    string Impact, string Action, string? MethodId, string? File, int? Line, int? Column,
    IReadOnlyList<string> RuleIds, IReadOnlyList<string> SourceFingerprints,
    IReadOnlyList<string> Reasons)
{
    // No test oracle or confirmed runtime violation enters this read-only engine.
    public bool DefectConfirmed => false;
    public string? OriginalSeverity { get; init; }
    public double? StaticScore { get; init; }
    public double? SelfWeight { get; init; }
    public double? SelfPercent { get; init; }
    public double? InclusivePercent { get; init; }
    public string? Unit { get; init; }
    public int? Observations { get; init; }
    public string? ObservationCountKind { get; init; }
    public string ProfileAssociation { get; init; } = "none";
    public bool Suppressed { get; init; }
    public string? SuppressionReason { get; init; }
}

public sealed record ReviewPriorityReport(
    string SnapshotId, string PolicyVersion, ReviewPriorityOptions Policy,
    int InputCount, int DistinctCount, int DuplicateCount, int SuppressedCount,
    int UnmatchedSuppressionCount, int MatchingCount, int ReturnedCount,
    int CompilerErrors, bool SourceTruncated, bool OutputTruncated,
    string ProfileStatus, IReadOnlyDictionary<string, int> PriorityCounts,
    IReadOnlyList<PrioritizedFinding> Items, IReadOnlyList<string> Limitations);

/// <summary>
/// A deterministic investigation queue, NOT a defect classifier. Diagnostic
/// severity, source facts, observed weights and static heuristics remain distinct.
/// P0 is reserved: nothing here can prove a production incident or data corruption.
/// </summary>
public static class ReviewPriorityEngine
{
    public const string Version = "2026.10-priority.1";
    private static readonly string[] Evidence = ["compiler", "analyzer", "semantic_fact", "candidate", "observed_profile"];

    public static void Validate(ReviewPriorityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.HotSharePercent) || options.HotSharePercent <= 0 || options.HotSharePercent > 100 ||
            !double.IsFinite(options.StaticSiteThreshold) || options.StaticSiteThreshold <= 0 || options.StaticSiteThreshold > 10000 ||
            options.MinimumObservations is < 1 or > 1000000 || options.MaxItems is < 1 or > 1000)
            throw new ArgumentException("Invalid priority threshold, observation count or output limit.");
        if (options.PriorityFilter is not (null or "P0" or "P1" or "P2" or "P3") ||
            (options.EvidenceFilter is not null && !Evidence.Contains(options.EvidenceFilter, StringComparer.Ordinal)))
            throw new ArgumentException("Unknown priority/evidence filter.");
        if (options.Suppressions is { Count: > 200 }) throw new ArgumentException("At most 200 exact-ID suppressions are allowed.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var suppression in options.Suppressions ?? [])
        {
            if (suppression is null || string.IsNullOrWhiteSpace(suppression.Id) || suppression.Id.Length > 100 ||
                string.IsNullOrWhiteSpace(suppression.Reason) || suppression.Reason.Length > 500 || !ids.Add(suppression.Id))
                throw new ArgumentException("Each suppression requires a unique item ID and a nonempty reason of at most 500 characters.");
        }
    }

    public static ReviewPriorityReport Build(DeterministicAnalysisReport diagnostics,
        StaticHotspotReport hotspots, SampledHotspotReport? profile = null,
        ReviewPriorityOptions? options = null)
    {
        options ??= new();
        Validate(options);
        if (string.IsNullOrWhiteSpace(diagnostics.Summary.SnapshotId) ||
            diagnostics.Summary.SnapshotId != hotspots.SnapshotId)
            throw new InvalidOperationException("Cannot combine different Roslyn snapshots.");
        var methods = hotspots.Methods.ToDictionary(x => x.Method.Id, StringComparer.Ordinal);
        var items = new List<PrioritizedFinding>();
        foreach (var finding in diagnostics.Findings)
        {
            var evidence = Evidence.Contains(finding.EvidenceLevel, StringComparer.Ordinal)
                && finding.EvidenceLevel != "observed_profile" ? finding.EvidenceLevel : "candidate";
            var severity = finding.Severity.ToLowerInvariant();
            var buildBlocker = severity == "error" && evidence is "compiler" or "analyzer";
            var priority = buildBlocker ? "P1" : severity == "warning" || evidence == "semantic_fact" ? "P2" : "P3";
            items.Add(new PrioritizedFinding(
                SiteId(finding.Project, finding.File, finding.Line, finding.Column, Family(finding.RuleId)),
                "diagnostic", finding.Title, priority, evidence,
                buildBlocker ? "build_blocking_diagnostic" : "application_impact_unverified",
                buildBlocker ? "fix_build" : priority == "P2" ? "review" : "reference",
                finding.MethodId, finding.File, finding.Line, finding.Column,
                [finding.RuleId], [finding.Fingerprint],
                [buildBlocker ? "Compiler/configured analyzer error: repair the build, not a proven production incident."
                    : "Diagnostic severity is not application impact or defect probability.",
                 finding.Message, finding.RecommendedCheck]) { OriginalSeverity = finding.Severity });
        }
        foreach (var site in hotspots.Sites)
        {
            // A loop or a local call alone is not a defect. Retain it in the static
            // hotspot report, without flooding the actionable finding queue.
            if (site.Category is "loop_body_candidate" or "repeated_local_call") continue;
            methods.TryGetValue(site.MethodId, out var method);
            var priority = site.Weight >= options.StaticSiteThreshold ? "P2" : "P3";
            items.Add(new PrioritizedFinding(
                SiteId(method?.Method.Project ?? "unresolved-project", site.File, site.Line, site.Column, site.Category),
                "static_site", site.Category, priority, "candidate", "performance_impact_unmeasured",
                priority == "P2" ? "review" : "reference", site.MethodId, site.File, site.Line, site.Column,
                ["HOTSPOT:" + site.Category], [site.Fingerprint],
                [site.Explanation, site.Verification, "Static weight never establishes runtime cost or a P1/P0 incident."])
            { StaticScore = site.Weight });
        }
        foreach (var frame in profile?.Frames ?? [])
        {
            ValidateFrame(frame);
            var prominent = frame.ExclusivePercent >= options.HotSharePercent;
            var sufficient = frame.SampleCount >= options.MinimumObservations;
            var priority = prominent && sufficient ? "P1" : frame.InclusivePercent >= options.HotSharePercent ? "P2" : "P3";
            var linked = frame.MatchedMethodId is not null && frame.MatchStatus.StartsWith("matched_", StringComparison.Ordinal);
            HotspotMethodRanking? method = null;
            if (linked) methods.TryGetValue(frame.MatchedMethodId!, out method);
            items.Add(new PrioritizedFinding(
                Id("profile", profile!.SourcePath, profile.Unit, frame.FrameName), "observed_frame", frame.FrameName,
                priority, "observed_profile", "observed_weight_not_proven_waste",
                priority == "P1" ? "investigate" : priority == "P2" ? "review" : "reference",
                linked ? frame.MatchedMethodId : null, method?.Method.File, method?.Method.Line, null, [], [],
                [prominent && sufficient ? "Self share and observation count meet the configurable investigation policy; this is not a confirmed defect."
                    : !sufficient ? "Too few observations for P1 under the current policy; this is not evidence that the method is cheap."
                    : "Inclusive weight can belong to callees; do not sum inclusive weights or attribute them to caller instructions.",
                 "The observation-count threshold is a policy gate, not a statistical confidence interval.",
                 linked ? "Method name association only; source/build equality and causal source location are unverified."
                    : "Frame is unmatched/ambiguous; retain its observed importance without attaching arbitrary source."])
            {
                SelfWeight = frame.ExclusiveWeight, SelfPercent = frame.ExclusivePercent,
                InclusivePercent = frame.InclusivePercent, Unit = profile.Unit,
                Observations = frame.SampleCount, ObservationCountKind = profile.ObservationCountKind,
                ProfileAssociation = linked ? "method_name_only_not_causal_site" : "unmatched_or_ambiguous"
            });
        }
        // Only explicitly equivalent rule families at the exact same source
        // coordinate are joined. Different rules on the same line remain separate.
        // Duplicate profiler labels use MAX/one row, never a recursive-weight sum.
        var distinct = items.GroupBy(x => x.Id, StringComparer.Ordinal).Select(group =>
        {
            var selected = Sort(group).First();
            return selected with
            {
                RuleIds = group.SelectMany(x => x.RuleIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                SourceFingerprints = group.SelectMany(x => x.SourceFingerprints).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                Reasons = group.SelectMany(x => x.Reasons).Distinct(StringComparer.Ordinal).ToArray(),
                StaticScore = group.Select(x => x.StaticScore).Max()
            };
        }).ToArray();
        var suppressed = (options.Suppressions ?? []).ToDictionary(x => x.Id, x => x.Reason, StringComparer.Ordinal);
        var all = distinct.Select(x => suppressed.TryGetValue(x.Id, out var reason)
            ? x with { Suppressed = true, SuppressionReason = reason } : x).ToArray();
        var counts = new[] { "P0", "P1", "P2", "P3" }.ToDictionary(p => p, p => all.Count(x => x.Priority == p));
        var matching = Sort(all.Where(x => (options.IncludeSuppressed || !x.Suppressed) &&
            (options.PriorityFilter is null || options.PriorityFilter == x.Priority) &&
            (options.EvidenceFilter is null || options.EvidenceFilter == x.EvidenceLevel))).ToArray();
        var limited = diagnostics.Summary.FindingsTruncated || diagnostics.Summary.IncompleteRuleScanning ||
            hotspots.MethodsTruncated || hotspots.SitesTruncated || (profile?.TotalFrames ?? 0) > (profile?.Frames.Count ?? 0);
        return new ReviewPriorityReport(diagnostics.Summary.SnapshotId, Version, options,
            items.Count, all.Length, items.Count - all.Length, all.Count(x => x.Suppressed),
            suppressed.Keys.Count(key => !all.Any(x => x.Id == key)), matching.Length,
            Math.Min(matching.Length, options.MaxItems), diagnostics.Summary.CompilerErrors,
            limited, matching.Length > options.MaxItems, profile is null ? "no_profile" : "loaded_name_association_only",
            counts, matching.Take(options.MaxItems).ToArray(),
            [
                "Priority is action order, not severity, calibrated confidence or defect probability. No P0/confirmed-defect verdict is emitted without independent validation, which this engine does not consume.",
                "Profiles may contain sampled thread time including waiting/blocking, bytes or counts; retained units are never converted into CPU time or added to static scores.",
                "Observed frames and source candidates remain separate: a hot method does not prove that an individual warning caused the cost. Duplicate frame labels are not summed.",
                "Counts are before filters, suppressions and output limits. Suppression affects this request only; it does not delete evidence or change the source reports.",
                limited ? "Source reports are bounded or incomplete. Absent findings and low priority are not evidence of correctness or low cost." : "No source truncation reported, but unexecuted workloads and unsupported code patterns remain blind spots.",
                "Policy thresholds are engineering defaults, not empirically calibrated detection rates. Source IDs depend on project/path/rule/coordinate; profile IDs are scoped to the captured profile path."
            ]);
    }

    private static IOrderedEnumerable<PrioritizedFinding> Sort(IEnumerable<PrioritizedFinding> values) => values
        .OrderBy(x => x.Priority, StringComparer.Ordinal)
        .ThenBy(x => x.Action == "fix_build" ? 0 : x.Kind == "observed_frame" ? 1 : 2)
        .ThenByDescending(x => x.Kind == "observed_frame" ? x.SelfWeight : null)
        .ThenByDescending(x => x.Kind == "observed_frame" ? null : x.StaticScore)
        .ThenBy(x => x.EvidenceLevel, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal);

    private static void ValidateFrame(SampledFrameCost frame)
    {
        if (frame.SampleCount < 0 || !double.IsFinite(frame.ExclusiveWeight) || frame.ExclusiveWeight < 0 ||
            !double.IsFinite(frame.InclusiveWeight) || frame.InclusiveWeight < frame.ExclusiveWeight ||
            !double.IsFinite(frame.ExclusivePercent) || frame.ExclusivePercent < 0 || frame.ExclusivePercent > 100 ||
            !double.IsFinite(frame.InclusivePercent) || frame.InclusivePercent < frame.ExclusivePercent || frame.InclusivePercent > 100)
            throw new InvalidDataException("Invalid profile frame weight, percentage or observation count.");
    }

    private static string Family(string rule) => rule switch
    {
        "PERF1001" => "linq_materialization", "PERF1002" => "string_concat_per_iteration",
        "PERF1003" => "regex_per_iteration", "PERF2001" => "boxing_per_iteration",
        "ASYNC1001" => "blocking_task_result", _ => rule
    };
    private static string SiteId(string project, string file, int line, int column, string family) =>
        Id("site", project, file.Replace('\\', '/'), line.ToString(System.Globalization.CultureInfo.InvariantCulture),
            column.ToString(System.Globalization.CultureInfo.InvariantCulture), family);
    private static string Id(params string[] values) => "priority-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
}
