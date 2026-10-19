using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record RuntimeCostReport(string CaptureId, string CaptureReportPath, string TraceSha256,
    string ParserVersion, string Status, int EventsRead, int ReportedEventsLost,
    bool EventLimitReached, int? SelectedProcessId, IReadOnlyList<TraceFileIdentity> RecordedInputs,
    IReadOnlyList<RuntimeCostSummary> Runtimes, IReadOnlyList<string> Limitations)
{
    public string SourceAssociation => "capture_input_identity_only__no_native_or_source_attribution";
    public string? RuntimeProviderConfiguration { get; init; }
}

/// <summary>Reads verified saved .nettrace bytes; never executes code or downloads symbols.</summary>
public sealed class RuntimeCostReader(WorkspaceGuard guard)
{
    private sealed class ReadLimitReached : Exception;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<RuntimeCostReport> ReadAsync(string reportPath, int? processId = null,
        RuntimeCostOptions? options = null, CancellationToken ct = default)
    {
        options ??= new(); options.Validate();
        if (processId is <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (!await Gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another runtime-event analysis is active.");
        try { return await Task.Run(() => ReadCore(reportPath, processId, options, ct), ct); }
        finally { Gate.Release(); }
    }
    private RuntimeCostReport ReadCore(string reportPath, int? processId, RuntimeCostOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var capture = new TrustedTraceCapture(guard).Read(reportPath);
        if (capture.Status != "trace_completed" || capture.NetTrace is null)
            throw new InvalidOperationException("A completed capture with verified original nettrace is required.");
        var path = guard.RequireFile(Path.Combine(Path.GetDirectoryName(capture.ReportPath)!, "workload.nettrace"), ".nettrace");
        var bytes = BoundedBinaryFile.Read(path, 128000000);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash != capture.NetTrace.Sha256 || bytes.LongLength != capture.NetTrace.Length)
            throw new InvalidDataException("The nettrace changed between manifest verification and analysis.");
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual("Nettrace"u8))
            throw new InvalidDataException("Expected an EventPipe Nettrace file.");
        var accumulator = new RuntimeCostAccumulator(options);
        var read = 0; var limited = false;
        using var stream = new MemoryStream(bytes, writable: false);
        using var source = new EventPipeEventSource(stream);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var cancel = deadline.Token.Register(source.StopProcessing);
        // Register CLR templates, but reduce through one dispatcher callback so
        // event-limit accounting and cost observations have exactly one boundary.
        source.Clr.All += _ => { };
        source.AllEvents += e =>
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (read >= options.MaxEvents) throw new ReadLimitReached();
            read++;
            if (e.ProviderGuid != ClrTraceEventParser.ProviderGuid || processId is int pid && e.ProcessID != pid) return;
            var kind = ClrEventKind((int)e.ID);
            if (kind is null) return;
            var clr = Int(e, "ClrInstanceID");
            if (clr is null) throw new InvalidDataException("Runtime event has no CLR instance identity.");
            accumulator.Add(new RuntimeCostEvent(kind, e.TimeStampRelativeMSec, e.ProcessID, clr.Value, e.ThreadID,
                Int(e, "Count"), Int(e, "Depth"), Int(e, "Reason"), Int(e, "Type"),
                Unsigned(e, "AllocationAmount64") ?? Unsigned(e, "AllocationAmount"),
                Value(e, "TypeName") as string, Unsigned(e, "ObjectSize"), Int(e, "ContentionFlags") ?? Int(e, "Flags"), Number(e, "DurationNs"))
            {
                ExceptionType = kind == "exception_thrown" ? Value(e, "ExceptionType") as string : null,
                ExceptionFlags = kind == "exception_thrown" ? Int(e, "ExceptionFlags") : null
            });
        };
        try { source.Process(); }
        catch (ReadLimitReached) { limited = true; }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        { throw new InvalidDataException("Malformed or unsupported runtime-event trace.", ex); }
        deadline.Token.ThrowIfCancellationRequested();
        var runtimes = accumulator.Build();
        if (!limited && processId is not null && runtimes.Count == 0)
            throw new ArgumentException("The selected process has no supported runtime cost events.");
        return new RuntimeCostReport(capture.Id, capture.ReportPath, hash, "TraceEvent/3.2.8;runtime-costs/2",
            limited ? "partial_event_limit" : source.EventsLost > 0 ? "partial_reported_event_loss" :
                runtimes.Any(r => r.IncompleteEvidence.Count > 0) ? "observed_with_incomplete_pairs" :
                runtimes.Count == 0 ? "no_supported_events_observed" : "observed_events",
            read, source.EventsLost, limited, processId, capture.Inputs, runtimes,
            [
                "GC cycle start/end is collection elapsed time, NOT application pause duration; background cycles may overlap foreground cycles.",
                "GC suspension windows run from SuspendEE start through RestartEE stop. Fully-suspended intervals need both intermediate phase events. Neither is measured GC CPU time.",
                "Allocation ticks occur after an allocation-volume threshold. Tick bytes cover multiple objects/types and are not attributed in full to the triggering type. Trigger counts/object sizes are sampled, not a heap census.",
                "Contention DurationNs measures reported managed Monitor waiter durations, converted to milliseconds. Waiters overlap; their sum may exceed wall time. It excludes other waits and does not identify a source method or prove a deadlock.",
                "ExceptionThrown_V1 (event ID 80) counts first-chance throws by type per PID/CLR, including caught and expected exceptions. No ExceptionMessage, stack trace or source location is retained; rethrow flags are observations, not a handled/unhandled classification.",
                "No CPU time is derived by subtracting GC or contention from sampled thread time. On-CPU time and other-wait time are explicitly null.",
                "Missing events can mean disabled providers, insufficient level, trace boundaries or event loss. ReportedEventsLost=0 is not a guarantee of completeness; on early stopping it covers only the processed prefix. Counters describe observed records only.",
                "Recorded DLL SHA256/MVID are capture input identities, not authenticated loaded-module or source/native-code associations. Saved manifests are unsigned; no symbols, source paths or executables embedded in trace data are opened.",
                "At most 128 MB of input, 64 PID/CLR groups, bounded pending/group/detail counts and a cooperative 45-second parsing deadline are accepted. This is not a hostile-file parser sandbox.",
                "P1 is only an investigation-budget exceedance when an explicit pause budget is supplied. It does not declare a defect, production incident, frame drop or performance regression."
            ]) { RuntimeProviderConfiguration = capture.RuntimeProviderConfiguration };
    }
    /// <summary>Known Microsoft-Windows-DotNETRuntime event IDs; never interpret another provider's ID as CLR.</summary>
    internal static string? ClrEventKind(int eventId) => eventId switch
    {
        1 => "gc_start", 2 => "gc_stop", 9 => "suspend_start",
        8 => "suspend_stop", 7 => "restart_start", 3 => "restart_stop",
        10 => "allocation_tick", 80 => "exception_thrown",
        81 => "contention_start", 91 => "contention_stop", _ => null
    };

    private static object? Value(TraceEvent e, string name)
    {
        for (var i = 0; i < e.PayloadNames.Length; i++)
            if (string.Equals(e.PayloadNames[i], name, StringComparison.OrdinalIgnoreCase)) return e.PayloadValue(i);
        return null;
    }
    private static int? Int(TraceEvent e, string key)
    {
        var value = Value(e, key); if (value is null) return null;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException) { return null; }
    }
    private static ulong? Unsigned(TraceEvent e, string key)
    {
        var value = Value(e, key); if (value is null) return null;
        try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException) { return null; }
    }
    private static double? Number(TraceEvent e, string key)
    {
        var value = Value(e, key); if (value is null) return null;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException) { return null; }
    }
}
