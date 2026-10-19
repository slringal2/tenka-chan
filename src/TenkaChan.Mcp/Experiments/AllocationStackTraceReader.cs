using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// Read-only for the target program: convert an already captured nettrace to a
/// temporary ETLX, resolve only managed method names already present in the
/// capture/rundown, aggregate AllocationTick stacks, then delete the ETLX.
/// EventPipeEventSource itself cannot resolve CallStack(); TraceLog can.
/// Never download symbols, load a captured DLL or launch the target process.
/// </summary>
public sealed class AllocationStackTraceReader(WorkspaceGuard guard)
{
    private sealed class LimitReached : Exception;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public const long MaxInputBytes = 16_000_000;
    public const long MaxTemporaryEtlxBytes = 256_000_000;
    public const int MaxEvents = 100_000;

    public async Task<AllocationStackSummary> ReadAsync(
        string captureReportPath, IReadOnlyList<ProfileMethodIdentity> sourceMethods,
        int limit = 30, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceMethods);
        if (!await Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another allocation-stack conversion is running.");
        try
        {
            // Conversion may be expensive. A per-process gate and strict file
            // limits guard routine use; there is no hostile-input sandbox.
            return await Task.Run(() => ReadCore(captureReportPath, sourceMethods,
                Math.Clamp(limit, 1, 100), ct), ct);
        }
        finally { Gate.Release(); }
    }

    private AllocationStackSummary ReadCore(
        string capturePath, IReadOnlyList<ProfileMethodIdentity> sourceMethods,
        int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var reader = new TrustedTraceCapture(guard);
        var capture = reader.Read(capturePath);
        if (capture.Status != "trace_completed" || capture.NetTrace is null)
            throw new InvalidOperationException("A completed, hash-verified trace is required.");
        if (capture.NetTrace.Length > MaxInputBytes)
            throw new InvalidOperationException(
                "Allocation-stack ETLX conversion is currently limited to nettraces of at most 16 MB. Use a shorter approved capture; never delete the original trace automatically.");
        var folder = guard.RequireDirectory(Path.GetDirectoryName(capture.ReportPath)!);
        var original = guard.RequireFile(Path.Combine(folder, "workload.nettrace"), ".nettrace");
        var temporary = Path.Combine(folder, "tenka-allocation-" + Guid.NewGuid().ToString("N") + ".etlx");
        var accumulator = new AllocationStackAccumulator(sourceMethods, limit);
        var processed = 0; var limited = false; var lost = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            var created = TraceLog.CreateFromEventPipeDataFile(original, temporary);
            if (!string.Equals(Path.GetFullPath(created), Path.GetFullPath(temporary),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected TraceLog conversion destination.");
            guard.RequireFile(temporary, ".etlx"); // No symlink or outside-root intermediate.
            if (new FileInfo(temporary).Length > MaxTemporaryEtlxBytes)
                throw new InvalidDataException("Converted trace exceeds the 256 MB temporary-file budget.");
            using (var log = new TraceLog(temporary))
            {
                lost = log.EventsLost;
                using var source = log.Events.GetSource();
                source.AllEvents += e =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (processed >= MaxEvents) throw new LimitReached();
                    processed++;
                    if (e.ProviderGuid != ClrTraceEventParser.ProviderGuid ||
                        (int)e.ID != 10) return; // GCAllocationTick only.
                    var frames = new List<string>(32);
                    var stack = e.CallStack();
                    var depth = 0;
                    while (stack is not null && depth++ < 96)
                    {
                        // FullMethodName for managed code may be unresolved,
                        // e.g. when the trace lacks rundown information.
                        var name = stack.CodeAddress.FullMethodName;
                        if (!string.IsNullOrWhiteSpace(name)) frames.Add(name);
                        stack = stack.Caller;
                    }
                    var clr = Integer(e, "ClrInstanceID") ?? -1;
                    var type = Value(e, "TypeName") as string ?? "(unknown trigger type)";
                    accumulator.Add(new AllocationStackObservation(
                        e.ProcessID, clr, type, Unsigned(e, "ObjectSize"), frames));
                };
                try { source.Process(); }
                catch (LimitReached) { limited = true; }
            }
            ct.ThrowIfCancellationRequested();
            var after = reader.Read(capture.ReportPath);
            if (capture.NetTrace != after.NetTrace || capture.Speedscope != after.Speedscope)
                throw new InvalidDataException("Trace identity changed during stack conversion.");
            return accumulator.Build(capture.Id, capture.NetTrace.Sha256,
                processed, lost, limited);
        }
        finally
        {
            // ETLX can include raw event payloads with secrets. Always delete
            // only this GUID-named artifact and never the user's original trace.
            // The captured nettrace remains protected by the existing allowlist.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static object? Value(TraceEvent data, string name)
    {
        for (var i = 0; i < data.PayloadNames.Length; i++)
            if (string.Equals(data.PayloadNames[i], name, StringComparison.OrdinalIgnoreCase))
                return data.PayloadValue(i);
        return null;
    }

    private static int? Integer(TraceEvent data, string name)
    {
        var value = Value(data, name);
        if (value is null) return null;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
        { return null; }
    }

    private static ulong? Unsigned(TraceEvent data, string name)
    {
        var value = Value(data, name);
        if (value is null) return null;
        try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
        { return null; }
    }
}
