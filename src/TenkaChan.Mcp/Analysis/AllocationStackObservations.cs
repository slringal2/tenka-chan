using System.Collections.ObjectModel;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// One CLR AllocationTick with an optional managed call stack (leaf to root).
/// ObjectSize is the sampled triggering object, NOT all bytes allocated since
/// the previous tick. No arbitrary exception messages or addresses are kept.
/// </summary>
public sealed record AllocationStackObservation(
    int ProcessId, int ClrInstanceId, string TriggerType,
    ulong? TriggerObjectSize, IReadOnlyList<string> Frames);

public sealed record AllocationStackHotspot(
    string MethodId, string QualifiedMember, int ProcessId, int ClrInstanceId,
    int SampledTriggers, ulong TriggerObjectBytesObserved,
    int TriggerObjectSizesAvailable, IReadOnlyList<string> TriggerTypes,
    IReadOnlyList<string> ExampleStacks)
{
    public string SourceAssociation => "nearest_unambiguous_managed_stack_frame__build_unverified";
    public bool AllocatedBytesVerified => false;
    public bool BinariesVerified => false;
    public string? SourceFile { get; init; }
    public int? SourceLine { get; init; }
}

public sealed record AllocationStackSummary(
    string CaptureId, string TraceSha256, string Status,
    int EventsRead, int ReportedEventsLost,
    int AllocationTicksObserved, int StacksAvailable,
    int SourceLinkedTicks, int AmbiguousTicks, int UnmatchedTicks,
    int MissingStackTicks, int TriggerGroupLimitHits, bool EventLimitReached,
    bool HotspotsTruncated, IReadOnlyList<AllocationStackHotspot> Hotspots,
    IReadOnlyList<string> Limitations)
{
    public bool BinariesVerified => false;
    public bool TotalAllocatedBytesVerified => false;
}

/// <summary>
/// Pure, bounded reducer. Unknown frames are not an error and ambiguous
/// overloads never fall back to an outer caller which happens to match.
/// Only one nearest, uniquely matched source frame is selected per tick.
/// This reducer can be unit-tested without TraceLog, .NET apps or any I/O.
/// </summary>
public sealed class AllocationStackAccumulator
{
    private sealed class Entry(string id, string qualified, int pid, int clr)
    {
        public string MethodId = id, QualifiedMember = qualified;
        public int ProcessId = pid, ClrInstanceId = clr;
        public int Count, Sizes;
        public ulong ObjectBytes;
        public readonly HashSet<string> Types = new(StringComparer.Ordinal);
        public readonly HashSet<string> ExampleStacks = new(StringComparer.Ordinal);
    }

    public const int MaxGroups = 2048;
    private readonly ProfileMethodIdentity[] _members;
    private readonly Dictionary<(string MethodId, int Pid, int Clr), Entry> _groups = new();
    private int _ticks, _stacks, _linked, _ambiguous, _unmatched, _missing, _limited;
    private readonly int _maxRows;

    public AllocationStackAccumulator(IReadOnlyList<ProfileMethodIdentity> methods, int maxRows = 30)
    {
        ArgumentNullException.ThrowIfNull(methods);
        _maxRows = Math.Clamp(maxRows, 1, 100);
        _members = methods.Where(m => !string.IsNullOrWhiteSpace(m.MethodId) &&
                !string.IsNullOrWhiteSpace(m.QualifiedMember))
            .Distinct().Take(20000).ToArray();
    }

    public void Add(AllocationStackObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _ticks++;
        if (observation.Frames.Count == 0) { _missing++; return; }
        _stacks++;
        // An allocation event is associated with the closest available
        // source frame; it does not establish the exact allocation statement.
        string? selectedId = null;
        string? qualified = null;
        var ambiguous = false;
        foreach (var raw in observation.Frames.Take(96))
        {
            var frame = Normalize(raw, 400);
            if (frame.Length == 0) continue;
            var candidates = _members.Where(m =>
                SpeedscopeHotspotParser.FrameMatches(frame.Replace('+', '.'),
                    m.QualifiedMember.Replace('+', '.')))
                .GroupBy(m => m.MethodId, StringComparer.Ordinal).Take(2).ToArray();
            if (candidates.Length == 0) continue;
            if (candidates.Length > 1) { ambiguous = true; break; }
            selectedId = candidates[0].Key;
            qualified = candidates[0].First().QualifiedMember;
            break;
        }
        if (ambiguous) { _ambiguous++; return; }
        if (selectedId is null) { _unmatched++; return; }
        _linked++;
        var key = (selectedId, observation.ProcessId, observation.ClrInstanceId);
        if (!_groups.TryGetValue(key, out var group))
        {
            if (_groups.Count >= MaxGroups) { _limited++; return; }
            _groups.Add(key, group = new(selectedId, qualified!,
                observation.ProcessId, observation.ClrInstanceId));
        }
        group.Count++;
        if (observation.TriggerObjectSize is ulong objectSize)
        {
            group.Sizes++;
            // Avoid arithmetic wrap when a corrupted trace supplies huge sizes.
            group.ObjectBytes = ulong.MaxValue - group.ObjectBytes < objectSize
                ? ulong.MaxValue : group.ObjectBytes + objectSize;
        }
        if (group.Types.Count < 12)
            group.Types.Add(Normalize(observation.TriggerType, 160));
        if (group.ExampleStacks.Count < 3)
            group.ExampleStacks.Add(string.Join(" <- ", observation.Frames
                .Take(6).Select(x => Normalize(x, 180))));
    }

    private static string Normalize(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = new string(text.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    public AllocationStackSummary Build(string captureId, string traceSha,
        int eventsRead, int eventsLost, bool eventLimitReached)
    {
        var rows = _groups.Values.OrderByDescending(e => e.Count)
            .ThenBy(e => e.MethodId, StringComparer.Ordinal)
            .ThenBy(e => e.ProcessId).ThenBy(e => e.ClrInstanceId).ToArray();
        var notes = new List<string>
        {
            "The reported samples are GCAllocationTick triggering objects and observed call stacks; they are NOT every allocation, whole-process allocation totals or per-method allocated bytes.",
            "Source linking chooses the nearest uniquely matched managed frame by name only. The exact allocating instruction, inlining, caller build and DLL/source identity are NOT verified.",
            "Ticks without a resolved stack or source match are kept as separate counters, not attributed by type or timing proximity.",
            "No symbols are downloaded or arbitrary trace-referenced files opened by the attribution reducer; an ETLX conversion is required to read EventPipe stacks.",
            "Raw traces and derived temporary ETLX files may contain sensitive event data. Only bounded names, counts and sampled trigger object sizes are returned."
        };
        if (eventsLost > 0 || eventLimitReached)
            notes.Add("Event loss or a read limit makes all sample counts partial; absent methods cannot be considered cheap.");
        if (_limited > 0 || rows.Length > _maxRows)
            notes.Add("Some aggregated groups were omitted by bounds; this report is not exhaustive.");
        return new AllocationStackSummary(captureId, traceSha,
            eventLimitReached ? "partial_event_limit" :
            eventsLost > 0 ? "partial_event_loss" :
            _stacks == 0 ? "no_stacks_available" :
            _linked == 0 ? "stacks_without_unique_source_links" : "observed_stack_samples",
            eventsRead, eventsLost, _ticks, _stacks, _linked, _ambiguous,
            _unmatched, _missing, _limited, eventLimitReached,
            rows.Length > _maxRows || _limited > 0,
            rows.Take(_maxRows).Select(g => new AllocationStackHotspot(
                g.MethodId, g.QualifiedMember, g.ProcessId, g.ClrInstanceId,
                g.Count, g.ObjectBytes, g.Sizes,
                g.Types.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                g.ExampleStacks.OrderBy(x => x, StringComparer.Ordinal).ToArray())).ToArray(), notes);
    }
}
