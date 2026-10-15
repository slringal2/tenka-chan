using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tenka.Sample.RaidLab.Application;
using Tenka.Sample.RaidLab.Domain;
using Tenka.Sample.RaidLab.Infrastructure;
using Tenka.Sample.RaidLab.Verification;

return await RaidLabProgram.RunAsync(args);

public static class RaidLabProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--help" }))
            {
                Console.WriteLine("RaidLab: --mode compare|baseline|optimized --scope analyzer|pipeline|batch --players 1..2000 --events 0..100000 --iterations 1..20 [--seconds 1..10] | --verify");
                return 0;
            }
            if (args.SequenceEqual(new[] { "--verify" }))
            {
                var failures = 0;
                foreach (var name in ContractChecks.Names)
                {
                    try { await ContractChecks.RunAsync(name); Console.WriteLine("PASS " + name); }
                    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
                }
                Console.WriteLine($"Verification: {ContractChecks.Names.Count - failures}/{ContractChecks.Names.Count}; failures={failures}. No performance claim.");
                return failures == 0 ? 0 : 1;
            }
            var o = WorkloadOptions.Parse(args);
            var data = DeterministicRaidData.Create(o.Players, o.Events);
            var repository = new InMemoryJsonRepository(data);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ct = deadline.Token;
            async Task<RaidReport> WorkAsync(bool fast)
            {
                IRaidAnalyzer analyzer = fast ? new IndexedRaidAnalyzer() : new BaselineRaidAnalyzer();
                var service = new RaidReportService(repository, analyzer, fast);
                if (o.Scope == "analyzer") return analyzer.Analyze(data, new(), ct);
                if (o.Scope == "pipeline") return (await service.GenerateAsync(new(), ct)).Report;
                var batch = await new RaidBatchService(service).GenerateAsync(4, fast ? 4 : 1, new(), ct);
                var first = ReportFormatter.Fingerprint(batch[0].Report);
                if (batch.Any(b => ReportFormatter.Fingerprint(b.Report) != first)) throw new InvalidOperationException("Batch output mismatch.");
                return batch[0].Report;
            }
            string? expected = null;
            foreach (var fast in o.Mode == "compare" ? new[] { false, true } : new[] { o.Mode == "optimized" })
            {
                await WorkAsync(fast); // One warmup is not proof of steady-state JIT/PGO.
                var before = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew(); var completed = 0; RaidReport? report = null;
                do { report = await WorkAsync(fast); completed++; }
                while (o.Seconds > 0 ? watch.Elapsed.TotalSeconds < o.Seconds : completed < o.Iterations);
                watch.Stop(); var allocated = GC.GetTotalAllocatedBytes(true) - before;
                var fingerprint = ReportFormatter.Fingerprint(report!);
                if (expected is not null && expected != fingerprint) throw new InvalidOperationException("Baseline/optimized output mismatch.");
                expected = fingerprint;
                if (o.Mode == "compare") Console.WriteLine(JsonSerializer.Serialize(new
                {
                    mode = fast ? "optimized" : "baseline", scope = o.Scope,
                    completed, fingerprint, accepted = report!.AcceptedEvents, rejected = report.RejectedEvents,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, processAllocatedBytes = allocated,
                    measurement = "reference_only_not_a_controlled_benchmark"
                }));
                else Console.WriteLine($"fingerprint={fingerprint}; completed={completed}");
            }
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled: sample exceeded its 90-second cooperative budget."); return 3; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or JsonException)
        { Console.Error.WriteLine(ex.Message + " See --help."); return 2; }
    }
}

public sealed record WorkloadOptions(string Mode, string Scope, int Players, int Events, int Iterations, int Seconds)
{
    public static WorkloadOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--mode" or "--scope" or "--players" or "--events" or "--iterations" or "--seconds") ||
                !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown, duplicate or incomplete argument.");
        }
        int Read(string key, int fallback, int min, int max)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
                throw new ArgumentException($"{key} must be in {min}..{max}.");
            return n;
        }
        var mode = values.GetValueOrDefault("--mode", "compare"); var scope = values.GetValueOrDefault("--scope", "pipeline");
        if (mode is not ("compare" or "baseline" or "optimized") || scope is not ("analyzer" or "pipeline" or "batch"))
            throw new ArgumentException("Unknown mode/scope.");
        var seconds = Read("--seconds", 0, 1, 10);
        if (seconds > 0 && (mode == "compare" || values.ContainsKey("--iterations")))
            throw new ArgumentException("Duration mode requires baseline/optimized and no --iterations.");
        return new(mode, scope, Read("--players", 200, 1, 2000), Read("--events", 6000, 0, 100000), Read("--iterations", 2, 1, 20), seconds);
    }
}
