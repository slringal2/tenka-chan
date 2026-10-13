using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativeJitDisassemblyParserTests
{
    private const string JitText = """
        ; Assembly listing for method Calc:Sum(int[]):int (FullOpts)
        ; Emitting BLENDED_CODE for X64 with AVX - Unix
        G_M000_IG01:
               55                   push     rbp
               488BEC               mov      rbp, rsp
               E800000000           call     CORINFO_HELP_NEWSFAST
               C5F96FC1             vmovdqa  xmm0, xmm1
               F7F9                 idiv     ecx
               75F8                 jne      SHORT G_M000_IG01
               C3                   ret
        ; Total bytes of code 18

        ; Assembly listing for method Calc:Other(int):int (Tier0)
        ; Emitting BLENDED_CODE for X64 - Unix
        G_M001_IG01:
               33C0                 xor      eax, eax
               C3                   ret
        ; Total bytes of code 3
        """;

    [Fact]
    public void ParsesActualCodeBytesAndBothJitMethodBodies()
    {
        var parsed = NativeJitDisassemblyParser.Parse(JitText,
            "net8.0", "mcr.microsoft.com/dotnet/sdk:8.0", "Calc:*");
        Assert.Equal(2, parsed.MethodCount);
        Assert.Equal(9, parsed.InstructionCount);
        var sum = Assert.Single(parsed.Methods, m => m.MethodName == "Sum");
        Assert.Equal("Calc", sum.DeclaringType);
        Assert.Equal("FullOpts", sum.JitMode);
        Assert.Equal("x64", sum.Architecture);
        Assert.Equal(18, sum.CodeSizeBytes);
        Assert.Equal("55", sum.Instructions[0].EncodedBytes);
        Assert.Equal(0, sum.Instructions[0].NativeOffset);
        Assert.Contains(sum.Instructions, i => i.Mnemonic == "vmovdqa" &&
            i.Family == "vector_candidate");
        Assert.Contains(sum.Signals, s => s.Kind == "allocation_helper");
        Assert.Contains(sum.Signals, s => s.Kind == "division");
        Assert.Contains(sum.Signals, s => s.Kind == "vector_instruction");
        Assert.Contains(sum.Instructions, i => i.Family == "conditional_branch");
        Assert.Equal(1, sum.InstructionFamilies["call"]);
        Assert.Null(sum.RoslynMethodId);
        Assert.Null(parsed.RoslynSnapshotId);
    }

    [Fact]
    public void FlagsTruncatedAnalysisWithoutPretendingToHaveCompleteDisassembly()
    {
        var result = NativeJitDisassemblyParser.Parse(JitText,
            "net10.0", "sdk10", "Calc:*", maxMethods: 1, maxInstructions: 2);
        Assert.True(result.Truncated);
        Assert.Single(result.Methods);
        Assert.Equal(2, result.InstructionCount);
    }

    [Fact]
    public void RejectsOversizedReport()
    {
        var large = new string('A', 8_500_001);
        Assert.Throws<InvalidDataException>(() =>
            NativeJitDisassemblyParser.Parse(large,
                "net8.0", "sdk8", "Calc:Sum"));
    }

    [Fact]
    public void ReportDoesNotInventIlOrCSharpLineMappings()
    {
        var parsed = NativeJitDisassemblyParser.Parse(JitText,
            "net8.0", "sdk8", "Calc:Sum");
        Assert.Contains(parsed.Limitations,
            s => s.Contains("method-level only", StringComparison.Ordinal));
        Assert.All(parsed.Methods, method =>
        {
            Assert.Null(method.RoslynMethodId);
            Assert.Equal("not_matched", method.MatchStatus);
        });
    }
}
