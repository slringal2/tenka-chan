namespace TenkaChan.Mcp.Analysis;

public sealed record RuntimeCostOptions(int MaxEvents = 500000, int MaxRows = 100, double? PauseBudgetMs = null)
{
    public void Validate()
    {
        if (MaxEvents is < 100 or > 2000000 || MaxRows is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaxEvents), "MaxEvents must be 100..2000000 and MaxRows 1..1000.");
        if (PauseBudgetMs is double budget && (!double.IsFinite(budget) || budget <= 0 || budget > 60000))
            throw new ArgumentOutOfRangeException(nameof(PauseBudgetMs));
    }
}

// Normalized observations; these do not establish source defects or CPU cost.
public sealed record RuntimeCostEvent(string Kind, double TimeMs, int ProcessId, int ClrInstanceId,
    int ThreadId = 0, int? Count = null, int? Generation = null, int? Reason = null,
    int? GcType = null, ulong? AllocationBytes = null, string? TypeName = null,
    ulong? ObjectSize = null, int? Flags = null, double? DurationNs = null)
{
    // ExceptionThrown_V1: never retain ExceptionMessage or arbitrary throw IPs.
    public string? ExceptionType { get; init; }
    public int? ExceptionFlags { get; init; }
}
public sealed record RuntimeGcCycle(int Count, int? Generation, int? Type, int? Reason, double StartMs, double EndMs)
{
    public double DurationMs => EndMs - StartMs;
}
public sealed record RuntimeGcPause(double StartMs, double? SuspendedMs, double? RestartMs, double EndMs, int Reason)
{
    public int InitiatingThreadId { get; init; }
    public double SuspensionWindowMs => EndMs - StartMs;
    public double? FullySuspendedMs => SuspendedMs is double s && RestartMs is double r ? r - s : null;
}
public sealed record RuntimeAllocationType(string TriggerType, int TickCount, ulong ObservedTriggerObjectBytes, int ObjectSizesReported);

/// <summary>
/// Bounded first-chance ExceptionThrown_V1 observations. A throw can be handled;
/// neither the exception message nor a source location is retained here.
/// </summary>
public sealed record RuntimeExceptionType(string TypeName, int ObservedThrows,
    int RethrowFlagsObserved, double FirstThrownMs, double LastThrownMs)
{
    public bool DefectConfirmed => false;
    public string SourceAttribution => "pid_clr_exception_type_only__throw_site_unresolved";
}
public sealed record RuntimeContentionThread(int ThreadId, int StartCount, int StopCount,
    int DurationsReported, double WaitMilliseconds, double MaximumWaitMilliseconds);
public sealed record RuntimeCostHint(string Kind, string Priority, string Evidence, string NextCheck)
{
    public bool DefectConfirmed => false;
}
public sealed record RuntimeCostSummary(int ProcessId, int ClrInstanceId, int ObservedEvents,
    double? FirstEventMs, double? LastEventMs,
    int CollectionsStarted, int CollectionsCompleted, IReadOnlyDictionary<int, int> CollectionsByGeneration,
    int GcSuspensionWindows, double GcSuspensionWindowTotalMs, double MaximumGcSuspensionWindowMs,
    int FullySuspendedIntervals, double FullySuspendedTotalMs,
    int AllocationTicks, ulong TickReportedAllocationBytes, int AllocationAmountsMissing,
    int ContentionStarts, int ContentionStops, int ContentionDurationsReported,
    double ContentionWaitMilliseconds, double MaximumContentionWaitMilliseconds,
    IReadOnlyList<RuntimeGcCycle> Collections, IReadOnlyList<RuntimeGcPause> Pauses,
    IReadOnlyList<RuntimeAllocationType> AllocationTriggerTypes,
    IReadOnlyList<RuntimeContentionThread> ContentionThreads,
    IReadOnlyDictionary<string, int> IncompleteEvidence, IReadOnlyDictionary<string, string> Coverage,
    bool DetailsTruncated, IReadOnlyList<RuntimeCostHint> InvestigationHints)
{
    public double? CpuMilliseconds => null;
    public double? OtherWaitMilliseconds => null;
    public string DurationAggregation => "sum_of_observed_windows_and_waiters_not_disjoint_wall_time";
    public IReadOnlyDictionary<int,int> ExcludedSuspensionStartsByReason { get; init; } = new Dictionary<int,int>();
    public int ExceptionThrows { get; init; }
    public int ExceptionKindsRetained { get; init; }
    public bool ExceptionTypeListTruncated { get; init; }
    public IReadOnlyList<RuntimeExceptionType> ExceptionTypes { get; init; } = [];
}
