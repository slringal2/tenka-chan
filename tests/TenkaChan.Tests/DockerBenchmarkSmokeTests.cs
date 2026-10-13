using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Heavy integration test enabled only in the CI Docker benchmark job.
/// No workload is measured on the host test runner.
/// </summary>
public sealed class DockerBenchmarkSmokeTests
{
    [Fact]
    public async Task RunsIsolatedBenchmarkAndParsesFullJson()
    {
        if (Environment.GetEnvironmentVariable("TENKA_BENCHMARK_SMOKE") != "1")
            return;

        var root = Path.Combine(Path.GetTempPath(), "tenka-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Calc.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Calc.cs"),
            "public static class Calc { public static int Add(int a, int b) => a+b; }");

        ExperimentReceipt? receipt = null;
        try
        {
            var guard = new WorkspaceGuard([root], false, true);
            var store = new ExperimentStore(guard);
            receipt = store.Create(root, "Calc.csproj", "benchmark", """
                using BenchmarkDotNet.Attributes;
                [ShortRunJob]
                [MemoryDiagnoser]
                public class GeneratedBenchmark
                {
                    private int _value = 1;
                    [Benchmark]
                    public int Add() => Calc.Add(_value, 3);
                }
                """);

            var result = await new DockerExecutor(guard, store).RunAsync(
                receipt.Id, CancellationToken.None);
            Assert.True(result.Status == "benchmark_completed",
                $"Docker benchmark status={result.Status}, exit={result.ExitCode}; " +
                $"stderr={result.Stderr}; stdout={result.Output}");
            Assert.NotEmpty(result.Benchmarks);
            Assert.True(result.Benchmarks.Any(
                b => b.Method == "Add" && b.MeanNanoseconds is > 0),
                "No valid timing values. Observed=" +
                System.Text.Json.JsonSerializer.Serialize(result.Benchmarks) +
                "; Stdout=" + result.Output + "; Stderr=" + result.Stderr);
            Assert.Equal("benchmark_completed",
                store.ReadExecutionResult(receipt.Id).Status);
        }
        finally
        {
            if (receipt is not null && Directory.Exists(receipt.Directory))
                Directory.Delete(receipt.Directory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }
}
