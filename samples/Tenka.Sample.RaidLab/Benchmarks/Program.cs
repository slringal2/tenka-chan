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
    private readonly IRaidAnalyzer _analyzer = new RaidAnalyzer();
    [GlobalSetup] public void Setup() => _data = DeterministicRaidData.Create(Players, 3000);
    [Benchmark] public RaidReport Analyze() => _analyzer.Analyze(_data, _options);
}

[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private RaidReportService _service = null!;
    private readonly ReportOptions _options = new();
    [GlobalSetup] public void Setup() => _service = new(
        new InMemoryJsonRepository(DeterministicRaidData.Create(100, 3000)), new RaidAnalyzer());
    [Benchmark] public Task<ReportEnvelope> Generate() => _service.GenerateAsync(_options);
}
