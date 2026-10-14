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

// A normalized event is an observation, never an inferred source defect.
public sealed record RuntimeCostEvent(string Kind, double TimeMs, int ProcessId, int ClrInstanceId,
    int ThreadId = 0, int? Count = null, int? Generation = null, int? Reason = null,
    int? GcType = null, ulong? AllocationBytes = null, string? TypeName = null,
    ulong? ObjectSize = null, int? Flags = null, double? DurationNs = null);
public sealed record RuntimeGcCycle(int Count, int? Generation, int? Type, int? Reason,
    double StartMs, double EndMs)
{
    public double DurationMs => EndMs - StartMs;
}
public sealed record RuntimeGcPause(double StartMs, double? SuspendedMs, double? RestartMs,
    double EndMs, int Reason)
{
    public double SuspensionWindowMs => EndMs - StartMs;
    public double? FullySuspendedMs => SuspendedMs is double s && RestartMs is double r ? r - s : null;
}
public sealed record RuntimeAllocationType(string TriggerType, int TickCount,
    ulong ObservedTriggerObjectBytes, int ObjectSizesReported);
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
    IReadOnlyDictionary<string, int> IncompleteEvidence,
    IReadOnlyDictionary<string, string> Coverage,
    bool DetailsTruncated, IReadOnlyList<RuntimeCostHint> InvestigationHints)
{
    public double? CpuMilliseconds => null;
    public double? OtherWaitMilliseconds => null;
}

/// <summary>
/// Bounded streaming reducer. GC lifetimes, process suspension windows and
/// overlapping waiter durations are different quantities. No subtraction from
/// thread time is used to invent CPU time. Pending pairs never cross PID/CLR.
/// </summary>
public sealed class RuntimeCostAccumulator
{
    private readonly RuntimeCostOptions _options;
    private readonly Dictionary<(int Pid, int Clr), State> _states = [];
    private sealed class Pause(RuntimeCostEvent start)
    {
        public RuntimeCostEvent Start = start;
        public double? Suspended;
        public double? Restart;
        public bool Invalid;
    }
    private sealed class Allocation(string type)
    {
        public string Type = type;
        public int Count, Sizes;
        public ulong Bytes;
    }
    private sealed class Contention(int thread)
    {
        public int Thread = thread, Starts, Stops, Durations;
        public double Wait, Max;
    }
    private sealed class State
    {
        public int Events, GcStarts, GcStops, PauseCount, FullyCount, Allocations, MissingAmounts;
        public int ContentionStarts, ContentionStops, ContentionDurations;
        public double? First, Last;
        public double PauseTotal, PauseMax, FullyTotal, WaitTotal, WaitMax;
        public ulong AllocationBytes;
        public Pause? PendingPause;
        public readonly Dictionary<int, RuntimeCostEvent?> PendingGc = [];
        public readonly Dictionary<int, double?> PendingContention = [];
        public readonly Dictionary<int, int> Generations = [];
        public readonly Dictionary<string, int> Issues = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Allocation> Types = new(StringComparer.Ordinal);
        public readonly Dictionary<int, Contention> Threads = [];
        public readonly List<RuntimeGcCycle> Cycles = [];
        public readonly List<RuntimeGcPause> Pauses = [];
        public void Issue(string name) => Issues[name] = Issues.GetValueOrDefault(name) + 1;
    }
    public RuntimeCostAccumulator(RuntimeCostOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }
    public void Add(RuntimeCostEvent e)
    {
        if (!double.IsFinite(e.TimeMs) || e.TimeMs < 0)
            throw new InvalidDataException("Non-finite or negative runtime event timestamp.");
        if (!_states.TryGetValue((e.ProcessId, e.ClrInstanceId), out var s))
        {
            if (_states.Count >= 64) throw new InvalidDataException("More than 64 process/CLR groups.");
            _states.Add((e.ProcessId, e.ClrInstanceId), s = new());
        }
        s.Events++;
        if (s.Last is double last && e.TimeMs < last)
        {
            s.Issue("out_of_order_timestamp");
            // Existing open pairs are now untrustworthy; do not fabricate intervals.
            foreach (var key in s.PendingGc.Keys.ToArray()) s.PendingGc[key] = null;
            foreach (var key in s.PendingContention.Keys.ToArray()) s.PendingContention[key] = null;
            if (s.PendingPause is not null) s.PendingPause.Invalid = true;
            return;
        }
        s.First ??= e.TimeMs; s.Last = e.TimeMs;
        switch (e.Kind)
        {
            case "gc_start":
                s.GcStarts++;
                if (e.Generation is int gen) s.Generations[gen] = s.Generations.GetValueOrDefault(gen) + 1;
                if (e.Count is not int count) { s.Issue("gc_count_missing"); break; }
                if (s.PendingGc.ContainsKey(count)) { s.PendingGc[count] = null; s.Issue("duplicate_gc_start"); }
                else if (s.PendingGc.Count < 1024) s.PendingGc.Add(count, e);
                else s.Issue("pending_gc_limit");
                break;
            case "gc_stop":
                if (e.Count is not int endCount || !s.PendingGc.Remove(endCount, out var start) || start is null)
                { s.Issue("unmatched_gc_stop"); break; }
                s.GcStops++;
                if (s.Cycles.Count < _options.MaxRows)
                    s.Cycles.Add(new(start.Count!.Value, start.Generation, start.GcType, start.Reason, start.TimeMs, e.TimeMs));
                break;
            case "suspend_start":
                if (s.PendingPause is not null) { s.PendingPause.Invalid = true; s.Issue("duplicate_suspend_start"); }
                else s.PendingPause = new(e);
                break;
            case "suspend_stop":
                if (s.PendingPause is null) s.Issue("orphan_suspend_complete");
                else if (s.PendingPause.Suspended is not null || s.PendingPause.Restart is not null)
                { s.PendingPause.Invalid = true; s.Issue("invalid_suspend_sequence"); }
                else s.PendingPause.Suspended = e.TimeMs;
                break;
            case "restart_start":
                if (s.PendingPause is null) s.Issue("orphan_restart_start");
                else if (s.PendingPause.Restart is not null)
                { s.PendingPause.Invalid = true; s.Issue("duplicate_restart_start"); }
                else s.PendingPause.Restart = e.TimeMs;
                break;
            case "restart_stop":
                var pause = s.PendingPause; s.PendingPause = null;
                if (pause is null || pause.Invalid) { s.Issue("unmatched_restart_stop"); break; }
                if (pause.Start.Reason is not (1 or 6)) { s.Issue("non_gc_or_unknown_suspension"); break; }
                var window = new RuntimeGcPause(pause.Start.TimeMs, pause.Suspended, pause.Restart, e.TimeMs, pause.Start.Reason.Value);
                s.PauseCount++; s.PauseTotal += window.SuspensionWindowMs;
                s.PauseMax = Math.Max(s.PauseMax, window.SuspensionWindowMs);
                if (window.FullySuspendedMs is double full && full >= 0) { s.FullyCount++; s.FullyTotal += full; }
                else s.Issue("incomplete_suspension_phases");
                if (s.Pauses.Count < _options.MaxRows) s.Pauses.Add(window);
                break;
            case "allocation_tick":
                s.Allocations++;
                if (e.AllocationBytes is ulong bytes) s.AllocationBytes = AddBytes(s.AllocationBytes, bytes);
                else s.MissingAmounts++;
                // A tick's allocation amount spans many allocations/types. It is
                // intentionally NOT charged in full to the triggering object's type.
                var name = string.IsNullOrEmpty(e.TypeName) ? "(type unavailable)" : e.TypeName;
                if (name.Length > 512) { name = name[..512]; s.Issue("type_name_truncated"); }
                if (!s.Types.TryGetValue(name, out var a))
                {
                    if (s.Types.Count >= 4096) { s.Issue("type_group_limit"); break; }
                    s.Types.Add(name, a = new(name));
                }
                a.Count++;
                if (e.ObjectSize is ulong size) { a.Bytes = AddBytes(a.Bytes, size); a.Sizes++; }
                break;
            case "contention_start":
                if (e.Flags != 0) { s.Issue("non_managed_or_unknown_contention"); break; }
                s.ContentionStarts++;
                if (s.PendingContention.ContainsKey(e.ThreadId)) { s.PendingContention[e.ThreadId] = null; s.Issue("duplicate_contention_start"); }
                else if (s.PendingContention.Count < 4096) s.PendingContention.Add(e.ThreadId, e.TimeMs);
                else s.Issue("pending_contention_limit");
                Thread(s, e.ThreadId)?.Starts++;
                break;
            case "contention_stop":
                if (e.Flags != 0) { s.Issue("non_managed_or_unknown_contention"); break; }
                s.ContentionStops++;
                var t = Thread(s, e.ThreadId); if (t is not null) t.Stops++;
                if (!s.PendingContention.Remove(e.ThreadId, out var begin) || begin is null)
                    s.Issue("unmatched_contention_stop");
                if (e.DurationNs is not double duration || !double.IsFinite(duration) || duration < 0)
                { s.Issue("missing_or_invalid_contention_duration"); break; }
                var ms = duration / 1000000d;
                s.ContentionDurations++; s.WaitTotal = AddFinite(s.WaitTotal, ms); s.WaitMax = Math.Max(s.WaitMax, ms);
                if (t is not null) { t.Durations++; t.Wait = AddFinite(t.Wait, ms); t.Max = Math.Max(t.Max, ms); }
                break;
            default: throw new ArgumentException("Unsupported normalized runtime event kind.");
        }
    }
    private static Contention? Thread(State s, int thread)
    {
        if (s.Threads.TryGetValue(thread, out var result)) return result;
        if (s.Threads.Count >= 4096) { s.Issue("thread_group_limit"); return null; }
        s.Threads.Add(thread, result = new(thread)); return result;
    }
    private static ulong AddBytes(ulong a, ulong b)
    {
        if (ulong.MaxValue - a < b) throw new InvalidDataException("Allocation counter overflow.");
        return a + b;
    }
    private static double AddFinite(double a, double b)
    {
        var sum = a + b;
        if (!double.IsFinite(sum)) throw new InvalidDataException("Runtime duration sum overflow.");
        return sum;
    }
    public IReadOnlyList<RuntimeCostSummary> Build() => _states.OrderBy(x => x.Key.Pid).ThenBy(x => x.Key.Clr)
        .Select(pair =>
        {
            var s = pair.Value;
            var issues = new Dictionary<string, int>(s.Issues, StringComparer.Ordinal);
            if (s.PendingGc.Count > 0) issues["unclosed_gc_starts"] = s.PendingGc.Count;
            if (s.PendingPause is not null) issues["unclosed_suspension"] = 1;
            if (s.PendingContention.Count > 0) issues["unclosed_contention_starts"] = s.PendingContention.Count;
            var hints = new List<RuntimeCostHint>();
            if (s.PauseCount > 0)
                hints.Add(new("gc_suspension", _options.PauseBudgetMs is double budget && s.PauseMax > budget ? "P1" : "P2",
                    $"{s.PauseCount} paired GC suspension windows; maximum {s.PauseMax:F3} ms.",
                    "Compare against the workload's latency budget. The suspension window includes rendezvous/resumption; it is not GC CPU time."));
            if (s.ContentionDurations > 0)
                hints.Add(new("monitor_contention", "P2", $"{s.ContentionDurations} recorded managed Monitor contention durations.",
                    "Inspect contending threads/locks separately; overlapping waiter durations cannot be subtracted from wall or CPU time."));
            if (s.Allocations > 0)
                hints.Add(new("allocation_sampling", "P3", $"{s.Allocations} allocation ticks observed.",
                    "Ticks are sampled allocation-volume evidence, not all objects or exact per-type totals. Use allocation stacks/benchmarks before blaming a method."));
            return new RuntimeCostSummary(pair.Key.Pid, pair.Key.Clr, s.Events, s.First, s.Last,
                s.GcStarts, s.GcStops, new Dictionary<int,int>(s.Generations), s.PauseCount, s.PauseTotal,
                s.PauseMax, s.FullyCount, s.FullyTotal, s.Allocations, s.AllocationBytes, s.MissingAmounts,
                s.ContentionStarts, s.ContentionStops, s.ContentionDurations, s.WaitTotal, s.WaitMax,
                s.Cycles.ToArray(), s.Pauses.ToArray(),
                s.Types.Values.OrderByDescending(a => a.Count).ThenBy(a => a.Type, StringComparer.Ordinal)
                    .Take(_options.MaxRows).Select(a => new RuntimeAllocationType(a.Type, a.Count, a.Bytes, a.Sizes)).ToArray(),
                s.Threads.Values.OrderByDescending(t => t.Wait).ThenBy(t => t.Thread).Take(_options.MaxRows)
                    .Select(t => new RuntimeContentionThread(t.Thread, t.Starts, t.Stops, t.Durations, t.Wait, t.Max)).ToArray(),
                issues, new Dictionary<string,string>
                {
                    ["gc"] = s.GcStarts > 0 ? "events_observed" : "not_observed__not_proof_of_zero",
                    ["allocation"] = s.Allocations > 0 ? "sampled_ticks_observed" : "not_observed__may_require_verbose_capture",
                    ["monitorContention"] = s.ContentionStops > 0 ? "stop_events_observed" : "not_observed__not_proof_of_zero",
                    ["onCpu"] = "not_collected_by_this_analysis",
                    ["otherWaits"] = "not_collected_by_this_analysis"
                }, s.GcStops > s.Cycles.Count || s.PauseCount > s.Pauses.Count || s.Types.Count > _options.MaxRows ||
                    s.Threads.Count > _options.MaxRows || s.Issues.Keys.Any(k => k.EndsWith("_limit", StringComparison.Ordinal)), hints);
        }).ToArray();
}
