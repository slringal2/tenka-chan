using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Tenka.Sample.RaidLab.Application;
using Tenka.Sample.RaidLab.Domain;
using Tenka.Sample.RaidLab.Infrastructure;

BenchmarkSwitcher.FromAssembly(typeof(AnalyzerBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class AnalyzerBenchmarks
{
    [Params(40, 200)] public int Players { get; set; }
    private RaidDataset _data = null!;
    private readonly ReportOptions _options = new();
    private readonly IRaidAnalyzer _baseline = new BaselineRaidAnalyzer();
    private readonly IRaidAnalyzer _optimized = new IndexedRaidAnalyzer();
    [GlobalSetup] public void Setup() => _data = DeterministicRaidData.Create(Players, 3000);
    [Benchmark(Baseline = true)] public RaidReport Baseline() => _baseline.Analyze(_data, _options);
    [Benchmark] public RaidReport Indexed() => _optimized.Analyze(_data, _options);
}

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private RaidReportService _baseline = null!;
    private RaidReportService _optimized = null!;
    private readonly ReportOptions _options = new();
    [GlobalSetup]
    public void Setup()
    {
        var repository = new InMemoryJsonRepository(DeterministicRaidData.Create(100, 3000));
        _baseline = new(repository, new BaselineRaidAnalyzer(), false);
        _optimized = new(repository, new IndexedRaidAnalyzer(), true);
    }
    // Deserialize + analyze + format. Index construction and validation remain inside the measured operation.
    [Benchmark(Baseline = true)] public Task<ReportEnvelope> Baseline() => _baseline.GenerateAsync(_options);
    [Benchmark] public Task<ReportEnvelope> Indexed() => _optimized.GenerateAsync(_options);
}
