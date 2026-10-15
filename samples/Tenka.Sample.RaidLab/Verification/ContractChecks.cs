using System.Text;
using System.Text.Json;
using Tenka.Sample.RaidLab.Application;
using Tenka.Sample.RaidLab.Domain;
using Tenka.Sample.RaidLab.Infrastructure;

namespace Tenka.Sample.RaidLab.Verification;

// Shared test implementation, linked into the dependency-free Host and the xUnit project.
// Expected golden values below are specified independently of either analyzer.
public static class ContractChecks
{
    private static readonly IReadOnlyDictionary<string, Func<Task>> Checks = CreateChecks();
    public static IReadOnlyList<string> Names { get; } = Checks.Keys.ToArray();
    public static Task RunAsync(string name) => Checks.TryGetValue(name, out var check) ? check() : throw new ArgumentException("Unknown check.");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static RaidDataset Golden() => new(
        [new(1, 10, "A", [1, 2]), new(2, 10, "B", [1]), new(3, 20, "C", [2])],
        [new(1, 0), new(2, 50)],
        [new(1, 1, 1, 10), new(2, 1, 2, 20), new(3, 2, 1, 5), new(4, 3, 2, 8),
         new(2, 1, 2, 999), new(5, 99, 1, 20), new(6, 2, 2, 20), new(7, 1, 1, 20, true),
         new(8, 1, 1, -1), new(9, 1, 99, 20), new(10, 1, 1, 1), new(0, 1, 1, 10),
         new(11, 99, 1, 20), new(11, 1, 1, 500)]);
    public const string ExpectedGolden = "events|14|4|2|8\nplayer|1|10|40|2\nplayer|3|20|12|1\nguild|10|45|3|2\nguild|20|12|1|1\n";
    private static IReadOnlyDictionary<string, Func<Task>> CreateChecks()
    {
        var result = new Dictionary<string, Func<Task>>(StringComparer.Ordinal);
        void Add(string name, Action action) => result.Add(name, () => { action(); return Task.CompletedTask; });
        foreach (var fast in new[] { false, true })
        {
            IRaidAnalyzer Analyzer() => fast ? new IndexedRaidAnalyzer() : new BaselineRaidAnalyzer();
            var prefix = fast ? "optimized/" : "baseline/";
            Add(prefix + "golden-and-format", () =>
            {
                var report = Analyzer().Analyze(Golden(), new(5, 2));
                Equal(ExpectedGolden, ReportFormatter.BuildBaseline(report));
                Equal(ExpectedGolden, ReportFormatter.BuildOptimized(report));
            });
            Add(prefix + "empty", () => Equal("events|0|0|0|0\n", ReportFormatter.BuildOptimized(Analyzer().Analyze(new([], [], []), new()))));
            Add(prefix + "duplicate-player-rejected", () => Throws<ArgumentException>(() =>
                Analyzer().Analyze(Golden() with { Players = [new(1, 1, "a", []), new(1, 1, "b", [])] }, new())));
            Add(prefix + "duplicate-skill-rejected", () => Throws<ArgumentException>(() =>
                Analyzer().Analyze(Golden() with { Skills = [new(1, 0), new(1, 0)] }, new())));
            Add(prefix + "null-events-rejected", () => Throws<ArgumentException>(() => Analyzer().Analyze(Golden() with { Events = [null!] }, new())));
            Add(prefix + "invalid-options-rejected", () => Throws<ArgumentOutOfRangeException>(() => Analyzer().Analyze(Golden(), new(-1, 0))));
            Add(prefix + "cancellation", () => Throws<OperationCanceledException>(() => Analyzer().Analyze(Golden(), new(), new(true))));
            Add(prefix + "ties-are-ordered-by-id", () =>
            {
                var data = new RaidDataset([new(3, 3, "C", [1]), new(1, 1, "A", [1])], [new(1, 0)], [new(1, 3, 1, 9), new(2, 1, 1, 9)]);
                var report = Analyzer().Analyze(data, new());
                Require(report.Players.Select(x => x.PlayerId).SequenceEqual([1, 3]), "Player tie order.");
                Require(report.Guilds.Select(x => x.GuildId).SequenceEqual([1, 3]), "Guild tie order.");
            });
            Add(prefix + "integer-overflow-prevented", () =>
            {
                var data = new RaidDataset([new(1, 1, "A", [1])], [new(1, 100)], [new(1, 1, 1, int.MaxValue)]);
                Equal(2L * int.MaxValue, Analyzer().Analyze(data, new()).Players[0].Damage);
            });
            Add(prefix + "zero-and-invalid-event-ids", () =>
            {
                var data = new RaidDataset([new(1, 1, "A", [1, 1])], [new(1, 50)],
                    [new(0, 1, 1, 8), new(0, 1, 1, 8), new(1, 1, 1, 0), new(2, 1, 1, 1)]);
                var r = Analyzer().Analyze(data, new(0));
                Equal(2, r.RejectedEvents); Equal(0, r.DuplicateEvents); Equal(2, r.AcceptedEvents); Equal(1L, r.Players[0].Damage);
            });
        }
        foreach (var size in new[] { 1, 17, 120 })
        foreach (var seed in new uint[] { 0, 17, 12345 })
        foreach (var threshold in new[] { 0, 5, 400 })
        {
            Add($"differential/{size}/{seed}/{threshold}", () =>
            {
                var data = DeterministicRaidData.Create(size, size * 13, seed);
                var before = JsonSerializer.Serialize(data); var options = new ReportOptions(threshold, 7);
                var a = new BaselineRaidAnalyzer().Analyze(data, options); var b = new IndexedRaidAnalyzer().Analyze(data, options);
                Equal(ReportFormatter.BuildOptimized(a), ReportFormatter.BuildOptimized(b));
                Equal(data.Events.Length, b.AcceptedEvents + b.RejectedEvents + b.DuplicateEvents);
                Equal(before, JsonSerializer.Serialize(data)); // Neither analyzer may mutate input arrays.
            });
        }
        result.Add("pipeline/json-roundtrip", async () =>
        {
            foreach (var fast in new[] { false, true })
            {
                IRaidAnalyzer analyzer = fast ? new IndexedRaidAnalyzer() : new BaselineRaidAnalyzer();
                var service = new RaidReportService(new InMemoryJsonRepository(Golden()), analyzer, fast);
                Equal(ExpectedGolden, (await service.GenerateAsync(new(5, 2))).Text);
            }
        });
        result.Add("pipeline/invalid-json", () => ThrowsAsync<JsonException>(() => new InMemoryJsonRepository(Encoding.UTF8.GetBytes("{" )).ReadAsync()));
        result.Add("pipeline/null-json", () => ThrowsAsync<InvalidDataException>(() => new InMemoryJsonRepository(Encoding.UTF8.GetBytes("null")).ReadAsync()));
        result.Add("pipeline/cancellation", () => ThrowsAsync<OperationCanceledException>(() => new InMemoryJsonRepository(Golden()).ReadAsync(new(true))));
        result.Add("pipeline/repository-failure-propagates", () => ThrowsAsync<IOException>(() =>
            new RaidReportService(new BrokenRepository(), new IndexedRaidAnalyzer(), true).GenerateAsync(new())));
        result.Add("batch/bounded-equivalence", async () =>
        {
            var service = new RaidReportService(new InMemoryJsonRepository(Golden()), new IndexedRaidAnalyzer(), true);
            foreach (var concurrency in new[] { 1, 2, 4 })
            {
                var reports = await new RaidBatchService(service).GenerateAsync(8, concurrency, new(5, 2));
                Equal(8, reports.Length); Require(reports.All(r => r.Text == ExpectedGolden), "Parallel output mismatch.");
            }
        });
        result.Add("batch/cancellation", () => ThrowsAsync<OperationCanceledException>(() =>
            new RaidBatchService(new RaidReportService(new InMemoryJsonRepository(Golden()), new IndexedRaidAnalyzer(), true)).GenerateAsync(2, 2, new(), new(true))));
        result.Add("batch/invalid-limit", () => ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new RaidBatchService(new RaidReportService(new InMemoryJsonRepository(Golden()), new IndexedRaidAnalyzer(), true)).GenerateAsync(17, 4, new())));
        Add("fixture/deterministic", () => Equal(JsonSerializer.Serialize(DeterministicRaidData.Create(50, 500, 7)), JsonSerializer.Serialize(DeterministicRaidData.Create(50, 500, 7))));
        return result;
    }
    private sealed class BrokenRepository : IRaidRepository
    {
        public Task<RaidDataset> ReadAsync(CancellationToken cancellationToken = default) => Task.FromException<RaidDataset>(new IOException("Synthetic repository failure."));
    }
}
