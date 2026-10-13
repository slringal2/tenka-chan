using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DockerNet8AndJitSmokeTests
{
    [Fact]
    public async Task Net8GeneratedXunitRunsOnNet8Runtime()
    {
        if (Environment.GetEnvironmentVariable("TENKA_NET8_DOCKER_SMOKE") != "1") return;
        await WithNet8Fixture(async (root, store, guard, cleanup) =>
        {
            var receipt = store.Create(root, "Calc.csproj", "test", """
                using Xunit;
                public class Net8Test
                {
                    [Fact] public void Works() => Assert.Equal(42, Calc.Sum(new[] { 1, 2, 3 }));
                }
                """);
            cleanup.Add(receipt.Directory);
            Assert.Equal("net8.0", receipt.TargetFramework);
            var execution = await new DockerExecutor(guard, store).RunAsync(receipt.Id, CancellationToken.None);
            Assert.True(execution.Status == "tests_passed", $"status={execution.Status}; stdout={execution.Output}; stderr={execution.Stderr}");
            Assert.Equal("net8.0", execution.TargetFramework);
            Assert.Equal(1, execution.Tests?.Passed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerExecutor(guard, store).RunAsync(receipt.Id, CancellationToken.None));
        });
    }

    [Fact]
    public async Task CollectsRealNet8JitInstructionsForSelectedMethod()
    {
        if (Environment.GetEnvironmentVariable("TENKA_NET8_DOCKER_SMOKE") != "1") return;
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
                        Console.WriteLine("JIT result: " + Calc.Sum(data));
                        return 0;
                    }
                }
                """, jitDisasmFilter: "Calc:Sum");
            cleanup.Add(receipt.Directory);
            var execution = await new DockerExecutor(guard, store).RunAsync(receipt.Id, CancellationToken.None);
            Assert.True(execution.Status == "jit_completed", $"JIT status={execution.Status}; stdout={execution.Output}; stderr={execution.Stderr}; note={execution.Note}");
            Assert.Equal("net8.0", execution.TargetFramework);
            var report = Assert.IsType<TenkaChan.Mcp.Analysis.NativeCodeReport>(execution.NativeDisassembly);
            var native = Assert.Single(report.Methods, m => m.MethodName == "Sum" && m.DeclaringType.Contains("Calc"));
            Assert.NotEmpty(native.Instructions);
            Assert.True(native.CodeSizeBytes > 0);
            Assert.True(native.OffsetsReliable, "Native offset verification: " + native.OffsetValidation + "\n" +
                File.ReadAllText(Path.Combine(receipt.Directory, "artifacts", "jit-disassembly.txt")));
            Assert.All(native.Instructions, i => Assert.NotNull(i.NativeOffset));
            Assert.Contains(execution.Artifacts, a => a == "jit-disassembly.txt");
            Assert.Contains(execution.BinaryArtifacts, a => a.FileName == "binary-Calc.dll" && a.Sha256.Length == 64);
            Assert.Contains(execution.BinaryArtifacts, a => a.FileName == "binary-Calc.pdb");
            Assert.False(execution.BinaryCaptureTruncated);
            Assert.Contains("8.0", execution.RuntimeInfo);

            var service = new JitBinarySourceService(store, guard);
            var catalog = service.Methods(receipt.Id, "binary-Calc.dll", "::Sum(");
            var method = Assert.Single(catalog.Methods);
            var sourcePath = Path.Combine(root, "Calc.cs");
            var evidence = service.Inspect(receipt.Id, "binary-Calc.dll", method.MetadataToken, sourcePath);
            Assert.Equal("verified_external_portable_pdb", evidence.PdbStatus);
            Assert.Contains(evidence.Instructions, i => i.Source?.LocalSourceStatus == "checksum_verified");
            Assert.Equal(catalog.AssemblySha256, evidence.AssemblySha256);
            File.AppendAllText(sourcePath, "\n// source changed after the JIT run\n");
            var stale = service.Inspect(receipt.Id, "binary-Calc.dll", method.MetadataToken, sourcePath);
            Assert.Contains(stale.Instructions, i => i.Source?.LocalSourceStatus == "checksum_mismatch");
            Assert.All(stale.Instructions.Where(i => i.Source is not null), i => Assert.Null(i.Source!.VerifiedSourceExcerpt));
            Assert.Equal("jit_completed", store.ReadExecutionResult(receipt.Id).Status);
        });
    }

    private static async Task WithNet8Fixture(Func<string, ExperimentStore, WorkspaceGuard, List<string>, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-net8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Calc.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><DebugType>portable</DebugType></PropertyGroup>
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
            await action(root, new ExperimentStore(guard), guard, directories);
        }
        finally
        {
            foreach (var dir in directories) if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Delete(root, true);
        }
    }
}
