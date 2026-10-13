namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Static rankings are screening heuristics, not CPU time, Big-O proofs,
/// allocation measurements, or declarations of performance regressions.
/// </summary>
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

/// <summary>
/// An observed speedscope sampled profile. A frame is linked to a Roslyn method
/// only when the qualified name unambiguously identifies one method.
/// </summary>
public sealed record SampledFrameCost(
    string FrameName, int SampleCount, double InclusiveWeight,
    double ExclusiveWeight, double InclusivePercent, double ExclusivePercent,
    string? MatchedMethodId, string MatchStatus);

public sealed record SampledHotspotReport(
    string SourcePath, string ProfileName, string Unit, int TotalSamples,
    double TotalWeight, int TotalFrames, int LinkedFrames,
    int AmbiguousFrames, IReadOnlyList<SampledFrameCost> Frames,
    IReadOnlyList<string> Limitations)
{
    public string ProfileType { get; init; } = "sampled";
    public string ObservationCountKind => ProfileType == "evented"
        ? "weighted_intervals" : "samples";
}
