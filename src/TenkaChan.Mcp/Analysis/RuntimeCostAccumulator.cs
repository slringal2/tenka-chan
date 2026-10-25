namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Bounded streaming reducer. Collection lifetimes, suspension windows and
/// overlapping waiter durations remain separate. Pairing never crosses PID/CLR.
/// Suspension phases additionally match the initiating thread, following the
/// CLR convention used by TraceEvent's TraceManagedProcess: a SampleProfiler
/// suspension on another thread must not consume a GC restart or pending start.
/// </summary>
public sealed class RuntimeCostAccumulator
{
    private readonly RuntimeCostOptions _options;
    private readonly Dictionary<(int Pid, int Clr), State> _states = [];
    private sealed class Pause(RuntimeCostEvent start)
    {
        public RuntimeCostEvent Start = start;
        public double? Suspended, Restart;
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
    private sealed class Thrown(string typeName, double firstMs)
    {
        public string TypeName = typeName;
        public int Count, RethrowFlags;
        public double First = firstMs, Last = firstMs;
    }
    private sealed class State
    {
        public int Events, GcStarts, GcStops, PauseCount, FullyCount, Allocations, MissingAmounts;
        public int ContentionStarts, ContentionStops, ContentionDurations;
        public int ExceptionsThrown;
        public readonly Dictionary<string, Thrown> ExceptionsByType = new(StringComparer.Ordinal);
        public double? First, Last;
        public double PauseTotal, PauseMax, FullyTotal, WaitTotal, WaitMax;
        public ulong AllocationBytes;
        public readonly Dictionary<int, Pause> PendingPauses = [];
        public readonly Dictionary<int, int> ExcludedSuspensions = [];
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
    { _options = options ?? new(); _options.Validate(); }

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
            foreach (var key in s.PendingGc.Keys.ToArray()) s.PendingGc[key] = null;
            foreach (var key in s.PendingContention.Keys.ToArray()) s.PendingContention[key] = null;
            foreach (var pending in s.PendingPauses.Values) pending.Invalid = true;
            return;
        }
        s.First ??= e.TimeMs; s.Last = e.TimeMs;
        s.PendingPauses.TryGetValue(e.ThreadId, out var pendingPause);
        switch (e.Kind)
        {
            case "gc_start":
                s.GcStarts++;
                if (e.Generation is >= 0 and <= 2) s.Generations[e.Generation.Value] = s.Generations.GetValueOrDefault(e.Generation.Value) + 1;
                else s.Issue("unknown_gc_generation");
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
                if (e.Reason is int reason && reason is not (1 or 6))
                {
                    // Known non-GC reasons are expected observations, not lost GC evidence.
                    if (s.ExcludedSuspensions.Count < 64 || s.ExcludedSuspensions.ContainsKey(reason))
                        s.ExcludedSuspensions[reason] = s.ExcludedSuspensions.GetValueOrDefault(reason) + 1;
                    else s.Issue("suspension_reason_group_limit");
                }
                else if (e.Reason is null) s.Issue("suspension_reason_missing");
                if (pendingPause is not null) { pendingPause.Invalid = true; s.Issue("duplicate_suspend_start"); }
                else if (s.PendingPauses.Count < 1024) s.PendingPauses.Add(e.ThreadId, new(e));
                else s.Issue("pending_suspension_limit");
                break;
            case "suspend_stop":
                if (pendingPause is null) s.Issue("orphan_suspend_complete");
                else if (pendingPause.Suspended is not null || pendingPause.Restart is not null)
                { pendingPause.Invalid = true; s.Issue("invalid_suspend_sequence"); }
                else pendingPause.Suspended = e.TimeMs;
                break;
            case "restart_start":
                if (pendingPause is null) s.Issue("orphan_restart_start");
                else if (pendingPause.Restart is not null)
                { pendingPause.Invalid = true; s.Issue("duplicate_restart_start"); }
                else pendingPause.Restart = e.TimeMs;
                break;
            case "restart_stop":
                s.PendingPauses.Remove(e.ThreadId);
                if (pendingPause is null || pendingPause.Invalid) { s.Issue("unmatched_restart_stop"); break; }
                if (pendingPause.Start.Reason is not (1 or 6)) break;
                var window = new RuntimeGcPause(pendingPause.Start.TimeMs, pendingPause.Suspended, pendingPause.Restart, e.TimeMs, pendingPause.Start.Reason.Value)
                { InitiatingThreadId = e.ThreadId };
                s.PauseCount++; s.PauseTotal = AddFinite(s.PauseTotal, window.SuspensionWindowMs);
                s.PauseMax = Math.Max(s.PauseMax, window.SuspensionWindowMs);
                if (window.FullySuspendedMs is double full && full >= 0) { s.FullyCount++; s.FullyTotal = AddFinite(s.FullyTotal, full); }
                else s.Issue("incomplete_suspension_phases");
                if (s.Pauses.Count < _options.MaxRows) s.Pauses.Add(window);
                break;
            case "allocation_tick":
                s.Allocations++;
                if (e.AllocationBytes is ulong bytes) s.AllocationBytes = AddBytes(s.AllocationBytes, bytes);
                else s.MissingAmounts++;
                // The amount spans many allocations/types, not just the trigger.
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
            case "exception_thrown":
                // The CLR records every throw, including expected exceptions
                // that are caught. Do not report these as unhandled failures.
                // This event has an ExceptionMessage payload; we intentionally
                // do not read or persist that potentially sensitive text.
                s.ExceptionsThrown++;
                var exceptionType = string.IsNullOrWhiteSpace(e.ExceptionType)
                    ? "(exception type unavailable)" : new string(e.ExceptionType
                        .Where(ch => !char.IsControl(ch)).ToArray()).Trim();
                if (exceptionType.Length == 0) exceptionType = "(exception type unavailable)";
                if (exceptionType.Length > 256)
                {
                    exceptionType = exceptionType[..256];
                    s.Issue("exception_type_name_truncated");
                }
                if (!s.ExceptionsByType.TryGetValue(exceptionType, out var observedException))
                {
                    if (s.ExceptionsByType.Count >= 256)
                    {
                        s.Issue("exception_type_group_limit");
                        break;
                    }
                    s.ExceptionsByType.Add(exceptionType,
                        observedException = new(exceptionType, e.TimeMs));
                }
                observedException.Count++;
                observedException.Last = e.TimeMs;
                if ((e.ExceptionFlags.GetValueOrDefault() & 0x04) != 0)
                    observedException.RethrowFlags++;
                break;
            case "contention_start":
                if (e.Flags != 0) { s.Issue("non_managed_or_unknown_contention"); break; }
                s.ContentionStarts++;
                if (s.PendingContention.ContainsKey(e.ThreadId)) { s.PendingContention[e.ThreadId] = null; s.Issue("duplicate_contention_start"); }
                else if (s.PendingContention.Count < 4096) s.PendingContention.Add(e.ThreadId, e.TimeMs);
                else s.Issue("pending_contention_limit");
                var startedThread = GetThread(s, e.ThreadId);
                if (startedThread is not null) startedThread.Starts++;
                break;
            case "contention_stop":
                if (e.Flags != 0) { s.Issue("non_managed_or_unknown_contention"); break; }
                s.ContentionStops++;
                var t = GetThread(s, e.ThreadId); if (t is not null) t.Stops++;
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
    private static Contention? GetThread(State s, int thread)
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
            if (s.PendingPauses.Count > 0) issues["unclosed_suspension"] = s.PendingPauses.Count;
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
            if (s.ExceptionsThrown > 0)
                hints.Add(new("managed_exceptions", "P3",
                    $"{s.ExceptionsThrown} first-chance ExceptionThrown events across {s.ExceptionsByType.Count} retained types.",
                    "Repeated exception throws can merit investigation in hot paths, but throws may be caught and expected. This does not identify a throw site or prove an unhandled exception."));
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
                    ["managedExceptions"] = s.ExceptionsThrown > 0 ? "first_chance_throws_observed" : "not_observed__not_proof_of_zero",
                    ["onCpu"] = "not_collected_by_this_analysis", ["otherWaits"] = "not_collected_by_this_analysis"
                }, s.GcStops > s.Cycles.Count || s.PauseCount > s.Pauses.Count || s.Types.Count > _options.MaxRows ||
                     s.Threads.Count > _options.MaxRows || s.ExceptionsByType.Count > _options.MaxRows ||
                    s.Issues.Keys.Any(k => k.EndsWith("_limit", StringComparison.Ordinal)), hints)
            {
                ExcludedSuspensionStartsByReason = new Dictionary<int,int>(s.ExcludedSuspensions),
                ExceptionThrows = s.ExceptionsThrown,
                ExceptionKindsRetained = s.ExceptionsByType.Count,
                ExceptionTypeListTruncated = s.ExceptionsByType.Count > _options.MaxRows ||
                    s.Issues.ContainsKey("exception_type_group_limit"),
                ExceptionTypes = s.ExceptionsByType.Values.OrderByDescending(x => x.Count)
                    .ThenBy(x => x.TypeName, StringComparer.Ordinal).Take(_options.MaxRows)
                    .Select(x => new RuntimeExceptionType(x.TypeName, x.Count,
                        x.RethrowFlags, x.First, x.Last)).ToArray()
            };
        }).ToArray();
}
