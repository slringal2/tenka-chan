using Tenka.Sample.RaidLab.Domain;

namespace Tenka.Sample.RaidLab.Application;

public sealed class RaidReportService(IRaidRepository repository, IRaidAnalyzer analyzer, bool optimizedFormat)
{
    public async Task<ReportEnvelope> GenerateAsync(ReportOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var data = await repository.ReadAsync(ct).ConfigureAwait(false);
        var report = analyzer.Analyze(data, options, ct);
        ct.ThrowIfCancellationRequested();
        return new(report, optimizedFormat ? ReportFormatter.BuildOptimized(report) : ReportFormatter.BuildBaseline(report));
    }
}

public sealed class RaidBatchService(RaidReportService service)
{
    // CPU/JSON work: bounded Task.Run is intentional here, not a claim that async alone makes CPU code faster.
    public async Task<ReportEnvelope[]> GenerateAsync(int count, int concurrency, ReportOptions options, CancellationToken ct = default)
    {
        if (count is < 1 or > 16 || concurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();
        using var slots = new SemaphoreSlim(concurrency);
        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try { return await Task.Run(() => service.GenerateAsync(options, ct), ct).ConfigureAwait(false); }
            finally { slots.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public static class LegacyEndpointExamples
{
    // STATIC-ONLY exercise: never invoked by the Host, verifier or benchmarks.
    public static async Task<ReportEnvelope> BlockingAsync(RaidReportService service)
    {
        var result = service.GenerateAsync(new()).Result;
        Thread.Sleep(5);
        await Task.Yield();
        return result;
    }
}
