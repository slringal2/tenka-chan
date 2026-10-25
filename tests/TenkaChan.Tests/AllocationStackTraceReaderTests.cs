using System.Text.Json;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>No ETLX conversion or target execution is needed for these guards.</summary>
public sealed class AllocationStackTraceReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tenka-allocation-bound-" + Guid.NewGuid().ToString("N"));

    private string Manifest(long size)
    {
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_root, "tenka-trace-" + id);
        Directory.CreateDirectory(folder);
        var trace = Path.Combine(folder, "workload.nettrace");
        using (var file = new FileStream(trace, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.SetLength(size);
        var scope = Path.Combine(folder, "workload.speedscope.json");
        File.WriteAllText(scope,
            """{"shared":{"frames":[]},"profiles":[{"type":"sampled","name":"t","unit":"none","samples":[],"weights":[]}]}""");
        var reportPath = Path.Combine(folder, "capture.json");
        var report = new TraceCaptureReport(id, "trace_completed",
            reportPath, "test.dll", "net8.0", "eventpipe_sampled_thread_time",
            "start", "end", "tool", "version", 0, 0, [], [],
            TrustedTraceCapture.Fingerprint(trace, 128_000_000),
            TrustedTraceCapture.Fingerprint(scope, 25_000_000),
            [new(0, "t", "sampled", "none", 0)], "", []);
        File.WriteAllText(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return reportPath;
    }

    [Fact]
    public async Task ValidButOversizeTraceFailsBeforeEtlxConversion()
    {
        var path = Manifest(AllocationStackTraceReader.MaxInputBytes + 1);
        var reader = new AllocationStackTraceReader(new WorkspaceGuard([_root], false, false));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(path, []));
        Assert.Contains("16 MB", error.Message);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
            "tenka-allocation-*.etlx"));
    }

    [Fact]
    public async Task TamperedManifestFailsBeforeConversion()
    {
        var path = Manifest(32);
        var source = Path.Combine(Path.GetDirectoryName(path)!, "workload.nettrace");
        File.AppendAllText(source, "tampered");
        var reader = new AllocationStackTraceReader(new WorkspaceGuard([_root], false, false));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(path, []));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
            "tenka-allocation-*.etlx"));
    }

    [Fact]
    public async Task OutOfAllowlistPathIsNotReadOrConverted()
    {
        Directory.CreateDirectory(_root);
        var external = Path.Combine(Path.GetTempPath(),
            "not-allowed-" + Guid.NewGuid().ToString("N"), "capture.json");
        var reader = new AllocationStackTraceReader(new WorkspaceGuard([_root], false, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(external, []));
    }

    [Fact]
    public async Task AlreadyCancelledCallNeverCreatesTemporaryFiles()
    {
        var path = Manifest(32);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new AllocationStackTraceReader(new WorkspaceGuard([_root], false, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(path, [], ct: cts.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
            "tenka-allocation-*.etlx"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
