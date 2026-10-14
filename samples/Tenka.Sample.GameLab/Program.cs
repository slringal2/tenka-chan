using System.Diagnostics;
using System.Globalization;
using Tenka.Sample.GameLab;

return GameLabProgram.Run(args);

public static class GameLabProgram
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Contains("--help", StringComparer.Ordinal))
            {
                Console.WriteLine("GameLab: --scenario inventory|report|damage|contention --mode compare|baseline|optimized --size 1..3000 --iterations 1..100 [--seconds 1..20] | --verify");
                return 0;
            }
            if (args.SequenceEqual(new[] { "--verify" })) return Verify();
            var settings = Options.Parse(args);
            var data = GameData.Create(settings.Size);
            long Work(bool fast) => settings.Scenario switch
            {
                "inventory" => fast ? InventorySearch.CountOwnedFast(data.Owned, data.Requested) : InventorySearch.CountOwnedSlow(data.Owned, data.Requested),
                "report" => fast ? LootReport.BuildFast(data.Owned).Length : LootReport.BuildSlow(data.Owned).Length,
                "damage" => fast ? DamageBuckets.SumMask(data.Requested, 8) : DamageBuckets.SumModulo(data.Requested, 8),
                "contention" => CounterContention.Run(fast, settings.Size),
                _ => throw new ArgumentException("Unknown scenario.")
            };
            if (settings.Mode == "compare")
            {
                Console.WriteLine("REFERENCE ONLY: these single-process timings are not BenchmarkDotNet or a statistical comparison.");
                long? expected = null;
                foreach (var fast in new[] { false, true })
                {
                    Work(fast); // Warmup, not a claim of steady-state JIT/PGO.
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var watch = Stopwatch.StartNew();
                    long checksum = 0;
                    for (var i = 0; i < settings.Iterations; i++) checksum += Work(fast);
                    watch.Stop();
                    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                    if (expected.HasValue && expected.Value != checksum) throw new InvalidOperationException("Result mismatch.");
                    expected = checksum;
                    Console.WriteLine(FormattableString.Invariant($"{(fast ? "optimized" : "baseline")}: checksum={checksum}, elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}, process_allocated_bytes={allocated}"));
                }
                // Also call BOTH damage methods in the no-argument demo so the JIT lesson's
                // default filter produces output without constructing a separate harness.
                if (args.Length == 0)
                {
                    var reportBefore = LootReport.BuildSlow(data.Owned);
                    var reportAfter = LootReport.BuildFast(data.Owned);
                    if (reportBefore != reportAfter) throw new InvalidOperationException("Report mismatch.");
                    var a = DamageBuckets.SumModulo(data.Requested, 8);
                    var b = DamageBuckets.SumMask(data.Requested, 8);
                    if (a != b) throw new InvalidOperationException("Damage mismatch.");
                    Console.WriteLine(FormattableString.Invariant($"damage_checksum={a}"));
                }
                Console.WriteLine("Use --verify for semantic checks and Benchmarks for a controlled comparison.");
            }
            else
            {
                var fast = settings.Mode == "optimized";
                long checksum = 0;
                if (settings.Seconds > 0)
                {
                    var watch = Stopwatch.StartNew();
                    do { checksum = Work(fast); } while (watch.Elapsed.TotalSeconds < settings.Seconds);
                    // Duration mode is for traces, not before/after whole-process timing.
                }
                else for (var i = 0; i < settings.Iterations; i++) checksum += Work(fast);
                // Baseline/optimized have identical stdout for the same fixed workload.
                Console.WriteLine(FormattableString.Invariant($"checksum={checksum}"));
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        { Console.Error.WriteLine(ex.Message + " Use --help."); return 2; }
    }

    private static int Verify()
    {
        var assertions = 0;
        void Equal<T>(T a, T b)
        {
            assertions++;
            if (!EqualityComparer<T>.Default.Equals(a, b)) throw new InvalidOperationException("Verification failed.");
        }
        foreach (var size in new[] { 1, 2, 17, 128, 600 })
        {
            var data = GameData.Create(size);
            Equal(InventorySearch.CountOwnedSlow(data.Owned, data.Requested), InventorySearch.CountOwnedFast(data.Owned, data.Requested));
            Equal(LootReport.BuildSlow(data.Owned), LootReport.BuildFast(data.Owned));
            foreach (var buckets in new[] { 1, 2, 8, 1024 })
                Equal(DamageBuckets.SumModulo(data.Requested, buckets), DamageBuckets.SumMask(data.Requested, buckets));
        }
        Equal(InventorySearch.CountOwnedSlow([], []), 0);
        Equal(InventorySearch.CountOwnedFast([-1, 2, 2], [-1, 2, 2, 5]), 2);
        Equal(LootReport.BuildSlow([]), LootReport.BuildFast([]));
        Equal(CounterContention.Run(false, 10), 40);
        Equal(CounterContention.Run(true, 10), 40);
        Console.WriteLine(FormattableString.Invariant($"Verification passed: {assertions} assertions. No performance claim."));
        return 0;
    }
}

public sealed record Options(string Scenario, string Mode, int Size, int Iterations, int Seconds)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--scenario" or "--mode" or "--size" or "--iterations" or "--seconds") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, duplicate or incomplete argument.");
        }
        int Number(string key, int fallback, int maximum)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > maximum)
                throw new ArgumentException($"{key} must be between 1 and {maximum}.");
            return n;
        }
        var scenario = values.GetValueOrDefault("--scenario", "inventory");
        var mode = values.GetValueOrDefault("--mode", "compare");
        if (scenario is not ("inventory" or "report" or "damage" or "contention")) throw new ArgumentException("Unknown scenario.");
        if (mode is not ("baseline" or "optimized" or "compare")) throw new ArgumentException("Unknown mode.");
        var seconds = Number("--seconds", 0, 20);
        if (seconds > 0 && (mode == "compare" || values.ContainsKey("--iterations")))
            throw new ArgumentException("--seconds requires baseline/optimized and cannot be combined with --iterations.");
        return new(scenario, mode, Number("--size", 600, 3000), Number("--iterations", 4, 100), seconds);
    }
}
