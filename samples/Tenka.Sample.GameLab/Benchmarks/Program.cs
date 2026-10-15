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
    [Benchmark] public int Search() => InventorySearch.CountOwned(owned, requested);
}
[MemoryDiagnoser]
public class ReportBenchmarks
{
    [Params(100, 600)] public int Size { get; set; }
    private int[] items = [];
    [GlobalSetup] public void Setup() => items = GameData.Create(Size).Owned;
    [Benchmark] public string Report() => LootReport.Build(items);
}
[MemoryDiagnoser]
public class DamageBenchmarks
{
    private int[] hits = [];
    [GlobalSetup] public void Setup() => hits = GameData.Create(600).Requested;
    [Benchmark] public long Sum() => DamageBuckets.SumBuckets(hits, 8);
}
