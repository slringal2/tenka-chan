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
            if (args.SequenceEqual(new[] { "--help" }))
            {
                Console.WriteLine("GameLab: --scenario inventory|report|damage|contention --size 1..3000 --iterations 1..100 [--seconds 1..20] | --verify");
                return 0;
            }
            if (args.SequenceEqual(new[] { "--verify" })) return Verify();
            var settings = Options.Parse(args);
            var data = GameData.Create(settings.Size);
            long Work() => settings.Scenario switch
            {
                "inventory" => InventorySearch.CountOwned(data.Owned, data.Requested),
                "report" => LootReport.Build(data.Owned).Length,
                "damage" => DamageBuckets.SumBuckets(data.Requested, 8),
                "contention" => CounterContention.Run(settings.Size),
                _ => throw new ArgumentException("Unknown scenario.")
            };
            Work();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew(); long checksum = 0; var completed = 0;
            do { checksum = Work(); completed++; }
            while (settings.Seconds > 0 ? watch.Elapsed.TotalSeconds < settings.Seconds : completed < settings.Iterations);
            watch.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Console.WriteLine(FormattableString.Invariant($"checksum={checksum}, completed={completed}, elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}, process_allocated_bytes={allocated}"));
            Console.WriteLine("REFERENCE ONLY: single-process timing, not a controlled benchmark.");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        { Console.Error.WriteLine(ex.Message + " Use --help."); return 2; }
    }

    private static int Verify()
    {
        var assertions = 0;
        void Equal<T>(T expected, T actual)
        {
            assertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Verification failed.");
        }
        Equal(0, InventorySearch.CountOwned([], []));
        Equal(2, InventorySearch.CountOwned([-1, 2, 2], [-1, 2, 2, 5]));
        Equal(2, InventorySearch.CountOwned([1, 2, 3], [1, 3, 4]));
        Equal("", LootReport.Build([]));
        Equal("-1,0,7,", LootReport.Build([-1, 0, 7]));
        Equal(0L, DamageBuckets.SumBuckets([], 8));
        Equal(8L, DamageBuckets.SumBuckets([0, 7, 8, 9], 8));
        Equal(40, CounterContention.Run(10));
        Console.WriteLine(FormattableString.Invariant($"Verification passed: {assertions} assertions. No performance claim."));
        return 0;
    }
}

public sealed record Options(string Scenario, int Size, int Iterations, int Seconds)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (i + 1 >= args.Length || args[i] is not ("--scenario" or "--size" or "--iterations" or "--seconds") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, duplicate or incomplete argument.");
        int Number(string key, int fallback, int maximum)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > maximum)
                throw new ArgumentException($"{key} must be between 1 and {maximum}.");
            return n;
        }
        var scenario = values.GetValueOrDefault("--scenario", "inventory");
        if (scenario is not ("inventory" or "report" or "damage" or "contention")) throw new ArgumentException("Unknown scenario.");
        var seconds = Number("--seconds", 0, 20);
        if (seconds > 0 && values.ContainsKey("--iterations")) throw new ArgumentException("Choose duration or iterations, not both.");
        return new(scenario, Number("--size", 600, 3000), Number("--iterations", 4, 100), seconds);
    }
}
