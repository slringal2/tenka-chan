using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Explicitly enabled in the dedicated opt-in CI Docker smoke job. No
/// generated source is executed directly in the CI host process.
/// </summary>
public sealed class DockerNet8AndJitSmokeTests
{
    [Fact]
    public async Task Net8GeneratedXunitRunsOnNet8Runtime()
    {
        if (Environment.GetEnvironmentVariable("TENKA_NET8_DOCKER_SMOKE") != "1")
            return;
        await WithNet8Fixture(async (root, store, guard, cleanup) =>
        {
            var receipt = store.Create(root, "Calc.csproj", "test", """
                using Xunit;
                public class Net8Test
                {
                    [Fact] public void Works() {
                        Assert.Equal(20, Calc.Sum(new[] { 1, 2, 3 }));
                    }
                }
                """);
            cleanup.Add(receipt.Directory);
            Assert.Equal("net8.0", receipt.TargetFramework);
            var execution = await new DockerExecutor(guard, store)
                .RunAsync(receipt.Id, CancellationToken.None);
            Assert.Equal("tests_passed", execution.Status);
            Assert.Equal("net8.0", execution.TargetFramework);
            Assert.Equal(1, execution.Tests?.Passed);
        });
    }

    [Fact]
    public async Task CollectsRealNet8JitInstructionsForSelectedMethod()
    {
        if (Environment.GetEnvironmentVariable("TENKA_NET8_DOCKER_SMOKE") != "1")
            return;
        await WithNet8Fixture(async (root, store, guard, cleanup) =>
        {
            var receipt = store.Create(root, "Calc.csproj", "jit", """
                using System;
                public static class Entry
                {
                    public static int Main()
                    {
                        var data = new int[512];
                        for (int i = 0; i < data.Length; i++) data[i] = i;
                        var answer = Calc.Sum(data);
                        Console.WriteLine("JIT result: " + answer);
                        return 0;
                    }
                }
                """, jitDisasmFilter: "Calc:Sum");
            cleanup.Add(receipt.Directory);

            var execution = await new DockerExecutor(guard, store)
                .RunAsync(receipt.Id, CancellationToken.None);
            Assert.True(execution.Status == "jit_completed",
                $"JIT status={execution.Status} exit={execution.ExitCode} " +
                $"stdout={execution.Output} stderr={execution.Stderr}");
            Assert.Equal("net8.0", execution.TargetFramework);
            var report = Assert.IsType<TenkaChan.Mcp.Analysis.NativeCodeReport>(
                execution.NativeDisassembly);
            Assert.NotEmpty(report.Methods);
            var native = Assert.Contains(report.Methods,
                m => m.MethodName == "Sum" && m.DeclaringType.Contains("Calc"));
            Assert.True(native.Instructions.Count > 0,
                $"RyuJIT produced a method listing but zero parsed instructions. " +
                $"Summary={native.JitSignature}");
            Assert.True(native.CodeSizeBytes > 0);
            Assert.Contains(native.Instructions, i => i.EncodedBytes.Length > 0);
            Assert.Contains(execution.Artifacts, a => a == "jit-disassembly.txt");
            Assert.Equal("jit_completed", store.ReadExecutionResult(receipt.Id).Status);
        });
    }

    private static async Task WithNet8Fixture(
        Func<string, ExperimentStore, WorkspaceGuard, List<string>, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-net8-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Calc.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Calc.cs"), """
            using System.Runtime.CompilerServices;
            public static class Calc
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                public static int Sum(int[] input)
                {
                    int total = 0;
                    foreach (int value in input) total += (value * 7) ^ (value >> 1);
                    return total;
                }
            }
            """);
        var directories = new List<string>();
        try
        {
            var guard = new WorkspaceGuard([root], false, true);
            var store = new ExperimentStore(guard);
            await action(root, store, guard, directories);
        }
        finally
        {
            foreach (var dir in directories)
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }
}
