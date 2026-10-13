using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class BenchmarkRunComparerTests
{
    [Fact]
    public void ComparesMatchedEnvironmentWithSameBenchmarkCode()
    {
        WithBenchmarkPair((store, first, second) =>
        {
            Store(store, first, "same-runtime", 100d, 64);
            Store(store, second, "same-runtime", 120d, 128);
            var comparison = new BenchmarkRunComparer(store).Compare(first.Id, second.Id);
            var item = Assert.Single(comparison.Cases);
            Assert.Equal("slower_observed", item.Status);
            Assert.Equal(20, item.MeanChangePercent);
            Assert.Equal(128d, item.CandidateAllocatedBytes);
            Assert.Equal("summaries_compared", comparison.Status);
        });
    }

    [Fact]
    public void RefusesMixedRuntimeAndUnverifiableTiming()
    {
        WithBenchmarkPair((store, first, second) =>
        {
            Store(store, first, "cpu-a|runtime-10", 100d, 0);
            Store(store, second, "cpu-b|runtime-10", 120d, 0);
            var comparison = new BenchmarkRunComparer(store).Compare(first.Id, second.Id);
            Assert.Equal("partial_comparison", comparison.Status);
            Assert.Equal(0, comparison.ComparedCases);
            Assert.Equal(1, comparison.IncomparableCases);
        });
    }

    [Fact]
    public void RefusesDifferentBenchmarkCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Example.csproj"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        ExperimentReceipt? first = null, second = null;
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], false, false));
            first = store.Create(root, "Example.csproj", "benchmark", "public class First { }");
            second = store.Create(root, "Example.csproj", "benchmark", "public class Second { }");
            Assert.Throws<InvalidOperationException>(() =>
                new BenchmarkRunComparer(store).Compare(first.Id, second.Id));
        }
        finally
        {
            if (first is not null) Directory.Delete(first.Directory, true);
            if (second is not null) Directory.Delete(second.Directory, true);
            Directory.Delete(root, true);
        }
    }

    private static void WithBenchmarkPair(
        Action<ExperimentStore, ExperimentReceipt, ExperimentReceipt> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Example.csproj"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        ExperimentReceipt? first = null, second = null;
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], false, false));
            first = store.Create(root, "Example.csproj", "benchmark", "public class BenchmarkCase { }");
            second = store.Create(root, "Example.csproj", "benchmark", "public class BenchmarkCase { }");
            action(store, first, second);
        }
        finally
        {
            if (first is not null) Directory.Delete(first.Directory, true);
            if (second is not null) Directory.Delete(second.Directory, true);
            Directory.Delete(root, true);
        }
    }

    private static void Store(ExperimentStore store, ExperimentReceipt receipt,
        string fingerprint, double mean, double allocated)
    {
        store.SaveExecutionResult(new ExecutionResult(receipt.Id, "benchmark",
            "benchmark_completed", 0, "start", "end", null,
            [new BenchmarkObservation("Bench.Run(N=100)", "Bench", "Run", "N=100",
                10, mean, mean, allocated, fingerprint)],
            [], "", "", "Test data"));
    }
}
