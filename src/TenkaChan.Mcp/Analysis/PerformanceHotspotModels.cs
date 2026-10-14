namespace TenkaChan.Mcp.Analysis;

/// <summary>Static scores are screening hints, not runtime costs or Big-O proofs.</summary>
public sealed record HotspotSite(
    string Fingerprint, string MethodId, string Category, string File,
    int Line, int Column, int LoopDepth, double Weight,
    string Expression, string Explanation, string Verification);

public sealed record HotspotCallEdge(
    string CallerMethodId, string CalleeMethodId, string File, int Line,
    int LoopDepth, string Dispatch);

public sealed record HotspotMethodRanking(
    MethodSummary Method, double SuspicionScore, double LocalScore,
    int MaximumLoopDepth, int NestedLoopCount, int AllocationCandidates,
    int EnumerationCandidates, int BlockingCandidates,
    int KnownCallSites, int RepeatedLocalCalls,
    int IncomingCallSites, int OutgoingCallSites,
    bool PotentialRecursion, IReadOnlyList<string> Reasons);

public sealed record StaticHotspotReport(
    string SnapshotId, string EvidenceLevel, int TotalMethods,
    int TotalSites, int ReturnedMethods, int ReturnedSites,
    bool MethodsTruncated, bool SitesTruncated,
    IReadOnlyList<HotspotMethodRanking> Methods,
    IReadOnlyList<HotspotSite> Sites,
    IReadOnlyList<HotspotCallEdge> CallEdges,
    IReadOnlyList<string> Limitations);

public sealed record SampledFrameCost(
    string FrameName, int SampleCount, double InclusiveWeight,
    double ExclusiveWeight, double InclusivePercent, double ExclusivePercent,
    string? MatchedMethodId, string MatchStatus);

/// <summary>
/// Attribution by matched method across ALL frames, before frame display limits.
/// Inclusive weight counts a method only once in an observation, even when
/// recursion or several frame aliases refer to that method.
/// </summary>
public sealed record ProfileMethodCost(string MethodId, string DisplayName,
    int ObservationCount, double InclusiveWeight, double ExclusiveWeight,
    int ContributingFrames);

public sealed record SampledHotspotReport(
    string SourcePath, string ProfileName, string Unit, int TotalSamples,
    double TotalWeight, int TotalFrames, int LinkedFrames,
    int AmbiguousFrames, IReadOnlyList<SampledFrameCost> Frames,
    IReadOnlyList<string> Limitations)
{
    public string ProfileType { get; init; } = "sampled";
    public string ObservationCountKind => ProfileType == "evented"
        ? "weighted_intervals" : "samples";
    public int ProfileIndex { get; init; }
    public string? SourceSha256 { get; init; }
    public string SourceIdentityKind { get; init; } = "unknown";
    public bool FramesTruncated => Frames.Count < TotalFrames;
    public bool CompleteMethodAttribution { get; init; }
    public IReadOnlyList<ProfileMethodCost> MethodCosts { get; init; } = [];
    public double UnmappedExclusiveWeight { get; init; }
    public double UnassignedWeight { get; init; }
}
