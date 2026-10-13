using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Enabled only in the dedicated CI Docker job. All generated C# and the
/// fixture project are confined to a network-disabled container.
/// </summary>
public sealed class DockerExperimentSmokeTests
{
    [Fact]
    public async Task RunsGeneratedXunitTestAndPersistsTrx()
    {
        if (Environment.GetEnvironmentVariable("TENKA_DOCKER_SMOKE") != "1")
            return;

        var root = Path.Combine(Path.GetTempPath(), "tenka-docker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Calc.cs");
        File.WriteAllText(Path.Combine(root, "Calc.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(source, """
            public static class Calc
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        ExperimentReceipt? receipt = null;
        ExperimentReceipt? failingReceipt = null;
        try
        {
            var guard = new WorkspaceGuard([root], false, true);
            var store = new ExperimentStore(guard);
            receipt = store.Create(root, "Calc.csproj", "test", """
                using Xunit;
                public class GeneratedTests
                {
                    [Fact] public void Calculates() => Assert.Equal(5, Calc.Add(2, 3));
                }
                """);
            var result = await new DockerExecutor(guard, store).RunAsync(
                receipt.Id, CancellationToken.None);
            Assert.True(result.Status == "tests_passed",
                $"Sandbox returned {result.Status}. Exit={result.ExitCode}; " +
                $"Failures={System.Text.Json.JsonSerializer.Serialize(result.Tests?.Failures)}; " +
                $"Stdout={result.Output}; Stderr={result.Stderr}");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(1, result.Tests?.Passed);
            Assert.Contains(result.Artifacts, a => a.EndsWith(".trx", StringComparison.Ordinal));
            Assert.Equal("tests_passed", store.ReadExecutionResult(receipt.Id).Status);

            // Verify an actual failed assertion is retained as evidence
            // instead of being mistaken for a compilation/runner failure.
            failingReceipt = store.Create(root, "Calc.csproj", "test", """
                using Xunit;
                public class GeneratedNegativeTests
                {
                    [Fact] public void FindsBug() => Assert.Equal(999, Calc.Add(2, 3));
                }
                """);
            var failed = await new DockerExecutor(guard, store).RunAsync(
                failingReceipt.Id, CancellationToken.None);
            Assert.Equal("tests_failed", failed.Status);
            Assert.Equal(1, failed.Tests?.Failed);
            Assert.NotEmpty(failed.Tests!.Failures);
            Assert.Equal("tests_failed",
                store.ReadExecutionResult(failingReceipt.Id).Status);
        }
        finally
        {
            if (receipt is not null && Directory.Exists(receipt.Directory))
                Directory.Delete(receipt.Directory, recursive: true);
            if (failingReceipt is not null && Directory.Exists(failingReceipt.Directory))
                Directory.Delete(failingReceipt.Directory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }
}
