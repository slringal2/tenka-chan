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
                Console.WriteLine("RaidLab: --scope analyzer|pipeline|batch --players 1..2000 --events 0..100000 --iterations 1..20 [--seconds 1..10] --concurrency 1..4 | --verify");
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
                Console.WriteLine($"Verification: {ContractChecks.Names.Count - failures}/{ContractChecks.Names.Count}; failures={failures}.");
                return failures == 0 ? 0 : 1;
            }
            var o = WorkloadOptions.Parse(args);
            var data = DeterministicRaidData.Create(o.Players, o.Events);
            var analyzer = new RaidAnalyzer();
            var service = new RaidReportService(new InMemoryJsonRepository(data), analyzer);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ct = deadline.Token;
            async Task<RaidReport> WorkAsync()
            {
                if (o.Scope == "analyzer") return analyzer.Analyze(data, new(), ct);
                if (o.Scope == "pipeline") return (await service.GenerateAsync(new(), ct)).Report;
                var batch = await new RaidBatchService(service).GenerateAsync(4, o.Concurrency, new(), ct);
                var first = ReportFormatter.Fingerprint(batch[0].Report);
                if (batch.Any(b => ReportFormatter.Fingerprint(b.Report) != first)) throw new InvalidOperationException("Batch output mismatch.");
                return batch[0].Report;
            }
            await WorkAsync();
            var before = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew(); var completed = 0; RaidReport? report = null;
            do { report = await WorkAsync(); completed++; }
            while (o.Seconds > 0 ? watch.Elapsed.TotalSeconds < o.Seconds : completed < o.Iterations);
            watch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(true) - before;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                scope = o.Scope, completed, fingerprint = ReportFormatter.Fingerprint(report!),
                elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, processAllocatedBytes = allocated,
                measurement = "reference_only_not_a_controlled_benchmark"
            }));
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled: 90-second cooperative budget reached."); return 3; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or JsonException)
        { Console.Error.WriteLine(ex.Message + " See --help."); return 2; }
    }
}

public sealed record WorkloadOptions(string Scope, int Players, int Events, int Iterations, int Seconds, int Concurrency)
{
    public static WorkloadOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (i + 1 == args.Length || args[i] is not ("--scope" or "--players" or "--events" or "--iterations" or "--seconds" or "--concurrency") ||
                !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown, duplicate or incomplete argument.");
        int Read(string key, int fallback, int min, int max)
        {
            if (!values.TryGetValue(key, out var text)) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
                throw new ArgumentException($"{key} must be in {min}..{max}.");
            return n;
        }
        var scope = values.GetValueOrDefault("--scope", "pipeline");
        if (scope is not ("analyzer" or "pipeline" or "batch")) throw new ArgumentException("Unknown scope.");
        var seconds = Read("--seconds", 0, 1, 10);
        if (seconds > 0 && values.ContainsKey("--iterations")) throw new ArgumentException("Choose duration or iterations, not both.");
        return new(scope, Read("--players", 200, 1, 2000), Read("--events", 6000, 0, 100000),
            Read("--iterations", 2, 1, 20), seconds, Read("--concurrency", 1, 1, 4));
    }
}
