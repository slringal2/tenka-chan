using Microsoft.Data.Sqlite;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Review;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AutonomousReviewRunnerTests
{
    [Fact]
    public async Task ReviewsMethodsAndPersistsSuspectedNotConfirmedHypotheses()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var ledger = new ReviewLedger(Path.Combine(root, "ledger.sqlite"));
            var store = new ReviewRunStore(Path.Combine(root, "runs"));
            using var runner = new AutonomousReviewRunner(
                new FakeSource(2), new FakeReasoner(), ledger, store);

            var started = await runner.StartAsync(new AutonomousReviewOptions(2, 1));
            Assert.Equal(2, started.TotalMethods);
            var done = await WaitForTerminalAsync(runner, started.RunId);
            Assert.Equal("completed", done.Status);
            Assert.Equal(2, done.CompletedMethods);
            Assert.Equal(2, done.HypothesisCount);
            Assert.Equal(0, done.ErrorCount);

            var saved = store.Get(started.RunId);
            Assert.Equal("completed", saved.Status);
            Assert.Equal(2, saved.HypothesisCount);
            Assert.Contains(saved.Activity, a => a.Kind == "hypothesis");
            var hypotheses = ledger.List(null, 100);
            Assert.Equal(2, hypotheses.Count);
            Assert.All(hypotheses, h => Assert.Equal("suspected", h.Status));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task HandlesInvalidMethodWithoutCancellingEntireRun()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var ledger = new ReviewLedger(Path.Combine(root, "ledger.sqlite"));
            var store = new ReviewRunStore(Path.Combine(root, "runs"));
            using var runner = new AutonomousReviewRunner(
                new FakeSource(3), new SometimesFailingReasoner(), ledger, store);
            var started = await runner.StartAsync(new AutonomousReviewOptions(3, 1));
            var finished = await WaitForTerminalAsync(runner, started.RunId);
            Assert.Equal("completed_with_errors", finished.Status);
            Assert.Equal(3, finished.CompletedMethods);
            Assert.Equal(1, finished.ErrorCount);
            Assert.Equal(2, finished.HypothesisCount);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task CancelsAnActiveReview()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var store = new ReviewRunStore(Path.Combine(root, "runs"));
            using var runner = new AutonomousReviewRunner(new FakeSource(1),
                new BlockingReasoner(), new ReviewLedger(Path.Combine(root, "ledger.sqlite")), store);
            var started = await runner.StartAsync(new AutonomousReviewOptions(1, 1));
            var stopped = runner.Stop(started.RunId);
            Assert.Equal("cancelling", stopped.Status);
            var ended = await WaitForTerminalAsync(runner, started.RunId);
            Assert.Equal("cancelled", ended.Status);
            Assert.Equal(0, ended.HypothesisCount);
            Assert.False(runner.IsActive);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DurableStoreMarksOrphanedRunningJobsInterrupted()
    {
        var root = CreateTemporaryFolder();
        try
        {
            var store = new ReviewRunStore(Path.Combine(root, "runs"));
            var id = Guid.NewGuid().ToString("N");
            store.Save(new AutonomousReviewStatus(id, "snapshot", "local", "running",
                1, 0, 0, 0, "Working", DateTimeOffset.UtcNow.ToString("O"), null, []));
            var recovered = new ReviewRunStore(Path.Combine(root, "runs"));
            Assert.Equal("interrupted", recovered.Get(id).Status);
            Assert.Single(recovered.List());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RejectsExcessiveWorkBudgets()
    {
        var root = CreateTemporaryFolder();
        try
        {
            using var runner = new AutonomousReviewRunner(new FakeSource(1),
                new FakeReasoner(), new ReviewLedger(Path.Combine(root, "ledger.sqlite")),
                new ReviewRunStore(Path.Combine(root, "runs")));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                runner.StartAsync(new AutonomousReviewOptions(31, 1)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                runner.StartAsync(new AutonomousReviewOptions(1, 31)));
        }
        finally { Cleanup(root); }
    }

    private static string CreateTemporaryFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string folder)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private static async Task<AutonomousReviewStatus> WaitForTerminalAsync(
        AutonomousReviewRunner runner, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!deadline.IsCancellationRequested)
        {
            var result = runner.Get(id);
            if (!runner.IsActive) return result;
            await Task.Delay(20);
        }
        throw new TimeoutException("Run did not finish within test deadline.");
    }

    private sealed class FakeSource(int methodCount) : IReviewContextSource
    {
        private readonly MethodSummary[] _methods = Enumerable.Range(1, methodCount)
            .Select(i => new MethodSummary("m" + i, "Example", "Example.Method" + i,
                "Example.cs", i)).ToArray();

        public Task<ReviewContext> GetContextAsync(int maxMethods, CancellationToken token)
        {
            IReadOnlyList<ReviewTarget> targets = _methods.Take(maxMethods)
                .Select(m => new ReviewTarget(m, 10, 1, 3, 0, 0, 0, 0, []))
                .ToArray();
            return Task.FromResult(new ReviewContext("snapshot-fake", targets, []));
        }

        public Task<ReviewMethodContext> GetMethodAsync(string id, CancellationToken token)
        {
            var method = _methods.Single(x => x.Id == id);
            var inspection = new MethodInspection(method, "return 1;", [], [], [], [], [], [], null);
            return Task.FromResult(new ReviewMethodContext(inspection,
                new SuggestedTestPlan(method.Id, method.Signature, true, [], [], "unresolved"), []));
        }
    }

    private sealed class FakeReasoner : IReviewReasoner
    {
        public string ModelName => "fake-local";
        public async Task<ReviewModelOutput> AnalyzeAsync(
            ReviewMethodContext context, CancellationToken token)
        {
            await Task.Delay(1, token);
            return new ReviewModelOutput([
                new ProposedHypothesis("correctness", "Check negative input",
                    "argument < 0", "Potential missing boundary check", ["use -1"])
            ], "Not proven.");
        }
    }

    private sealed class SometimesFailingReasoner : IReviewReasoner
    {
        private readonly FakeReasoner _inner = new();
        public string ModelName => "sometimes-failing";
        public Task<ReviewModelOutput> AnalyzeAsync(ReviewMethodContext context,
            CancellationToken token)
        {
            if (context.Inspection.Method.Id == "m2")
                throw new InvalidDataException("Invalid model output");
            return _inner.AnalyzeAsync(context, token);
        }
    }

    private sealed class BlockingReasoner : IReviewReasoner
    {
        public string ModelName => "blocking";
        public async Task<ReviewModelOutput> AnalyzeAsync(
            ReviewMethodContext context, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ReviewModelOutput([], "");
        }
    }
}
