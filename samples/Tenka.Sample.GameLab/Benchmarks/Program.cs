using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Tenka.Sample.GameLab;

BenchmarkSwitcher.FromAssembly(typeof(InventoryBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class InventoryBenchmarks
{
    [Params(100, 600)] public int Size { get; set; }
    private int[] owned = [], requested = [];
    [GlobalSetup] public void Setup() => (owned, requested) = GameData.Create(Size);
    [Benchmark(Baseline = true)] public int RebuildAndScan() => InventorySearch.CountOwnedSlow(owned, requested);
    [Benchmark] public int BuildLookupOnce() => InventorySearch.CountOwnedFast(owned, requested);
}
[MemoryDiagnoser]
public class ReportBenchmarks
{
    [Params(100, 600)] public int Size { get; set; }
    private int[] items = [];
    [GlobalSetup] public void Setup() => items = GameData.Create(Size).Owned;
    [Benchmark(Baseline = true)] public string Concatenate() => LootReport.BuildSlow(items);
    [Benchmark] public string Builder() => LootReport.BuildFast(items);
}
[MemoryDiagnoser]
public class DamageBenchmarks
{
    private int[] hits = [];
    [GlobalSetup] public void Setup() => hits = GameData.Create(600).Requested;
    [Benchmark(Baseline = true)] public long Modulo() => DamageBuckets.SumModulo(hits, 8);
    [Benchmark] public long Mask() => DamageBuckets.SumMask(hits, 8);
}
