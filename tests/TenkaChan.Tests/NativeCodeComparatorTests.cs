using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativeCodeComparatorTests
{
    private static NativeCodeReport Code(string framework, bool vector)
    {
        var instructions = vector
            ? """
                ; Assembly listing for method Demo:Sum(int):int (FullOpts)
                ; Emitting BLENDED_CODE for X64
                       C5F858C1 vaddps xmm0, xmm1, xmm1
                       C3 ret
                ; Total bytes of code 5
                """
            : """
                ; Assembly listing for method Demo:Sum(int):int (FullOpts)
                ; Emitting BLENDED_CODE for X64
                       33C0 xor eax, eax
                       03C1 add eax, ecx
                       C3 ret
                ; Total bytes of code 5
                """;
        return NativeJitDisassemblyParser.Parse(instructions, framework,
            "sdk-" + framework, "Demo:Sum");
    }

    [Fact]
    public void ReportsCodegenDifferencesWithoutClaimingSpeedup()
    {
        var comparison = NativeCodeComparator.Compare(Code("net8.0", false),
            Code("net10.0", true));
        Assert.Equal("codegen_compared", comparison.Status);
        var method = Assert.Single(comparison.Methods);
        Assert.Equal("x64", method.Architecture);
        Assert.Equal(3, method.BaselineInstructionCount);
        Assert.Equal(2, method.CandidateInstructionCount);
        Assert.Contains(method.Families, f =>
            f.Family == "vector_candidate" && f.Delta == 1);
        Assert.Contains(comparison.Limitations, note =>
            note.Contains("NOT benchmark", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesMachineCodeComparisonsAcrossArchitectures()
    {
        var baseline = Code("net8.0", false);
        var different = Code("net10.0", true);
        var arm = different with
        {
            Methods = different.Methods.Select(m => m with
            { Architecture = "arm64" }).ToArray()
        };
        Assert.Equal("no_comparable_methods",
            NativeCodeComparator.Compare(baseline, arm).Status);
    }

    [Fact]
    public void PersistentExperimentComparisonRequiresSameHarnessAndJitMethod()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-native-compare-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
            <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
            </PropertyGroup></Project>
            """);
        var files = new List<ExperimentReceipt>();
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], false, false));
            const string same = "public static class Entry { public static int Main() => 0; }";
            var left = store.Create(root, "Demo.csproj", "jit", same,
                targetFramework: "net8.0", jitDisasmFilter: "Demo:Sum");
            var right = store.Create(root, "Demo.csproj", "jit", same,
                targetFramework: "net10.0", jitDisasmFilter: "Demo:Sum");
            files.Add(left);
            files.Add(right);
            Save(store, left, Code("net8.0", false));
            Save(store, right, Code("net10.0", true));

            var compared = new NativeExperimentComparer(store).Compare(left.Id, right.Id);
            Assert.Equal("net8.0", compared.BaselineFramework);
            Assert.Equal("net10.0", compared.CandidateFramework);
            Assert.Single(compared.Methods);

            var different = store.Create(root, "Demo.csproj", "jit",
                "public static class Entry { public static int Main() => 2; }",
                jitDisasmFilter: "Demo:Sum");
            files.Add(different);
            Assert.Throws<InvalidOperationException>(() =>
                new NativeExperimentComparer(store).Compare(left.Id, different.Id));
        }
        finally
        {
            foreach (var receipt in files)
                if (Directory.Exists(receipt.Directory))
                    Directory.Delete(receipt.Directory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Save(ExperimentStore store, ExperimentReceipt receipt,
        NativeCodeReport native)
    {
        store.SaveExecutionResult(new ExecutionResult(receipt.Id, "jit",
            "jit_completed", 0, "start", "end", null, [], [], "", "", "test")
        {
            TargetFramework = native.TargetFramework, NativeDisassembly = native
        });
    }
}
