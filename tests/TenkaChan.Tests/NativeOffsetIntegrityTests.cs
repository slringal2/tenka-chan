using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativeOffsetIntegrityTests
{
    [Fact]
    public void RetainsUnclassifiedOpcodeAndCountsItsBytes()
    {
        var report = Parse("""
            ; Assembly listing for method Calc:Sum(int):int (FullOpts)
            ; Emitting BLENDED_CODE for X64
                   55          push rbp
                   0F38FF      unknownop eax
                   C3          ret
            ; Total bytes of code 5
            """);
        var method = Assert.Single(report.Methods);
        Assert.Equal(3, method.Instructions.Count);
        Assert.True(method.OffsetsReliable);
        Assert.Equal("unknown", method.Instructions[1].Family);
        Assert.Equal(4, method.Instructions[2].NativeOffset);
        Assert.Contains(method.ParseWarnings, w => w.Contains("Unclassified"));
    }

    [Fact]
    public void WithholdsAllOffsetsAfterMissingBytesOrInconsistentCodeSize()
    {
        foreach (var text in new[]
        {
            "; Assembly listing for method Calc:Sum(int):int (FullOpts)\n        push rbp\n        C3 ret\n; Total bytes of code 2",
            "; Assembly listing for method Calc:Sum(int):int (FullOpts)\n        C3 ret\n; Total bytes of code 8"
        })
        {
            var method = Assert.Single(Parse(text).Methods);
            Assert.False(method.OffsetsReliable);
            Assert.All(method.Instructions, i => Assert.Null(i.NativeOffset));
        }
    }

    [Fact]
    public void KeepsNoOperandInstructionsAndMethodColonsWithoutEncodedBytes()
    {
        var method = Assert.Single(Parse("""
            ; Assembly listing for method Calc:Sum(int):int (FullOpts)
                    call [Calc:Other(int):int]
                    ret
            ; Total bytes of code 7
            """).Methods);
        Assert.Equal(2, method.Instructions.Count);
        Assert.Equal("call", method.Instructions[0].Family);
        Assert.Equal("ret", method.Instructions[1].Mnemonic);
        Assert.False(method.OffsetsReliable);
    }

    [Fact]
    public void DoesNotTreatScalarAvxOrVectorStateAsPackedSimdEvidence()
    {
        var report = Parse("""
            ; Assembly listing for method Calc:Sum(double):double (FullOpts)
            ; Emitting BLENDED_CODE for X64 with AVX
                    C5FB58C1   vaddsd xmm0,xmm0,xmm1
                    C5F877     vzeroupper
                    C3         ret
            ; Total bytes of code 8
            """);
        var method = Assert.Single(report.Methods);
        Assert.Contains(method.Instructions, i => i.Family == "scalar_floating");
        Assert.Contains(method.Instructions, i => i.Family == "vector_state");
        Assert.DoesNotContain(method.Signals, s => s.Kind == "vector_instruction");
    }

    [Fact]
    public void TruncationCannotLeaveApparentExactNativeOffsets()
    {
        var method = Assert.Single(NativeJitDisassemblyParser.Parse("""
            ; Assembly listing for method Calc:Sum(int):int (FullOpts)
                    55 push rbp
                    C3 ret
            ; Total bytes of code 2
            """, "net8.0", "sdk8", "Calc:Sum", maxInstructions: 1).Methods);
        Assert.False(method.OffsetsReliable);
        Assert.Equal("instruction_limit_reached", method.OffsetValidation);
        Assert.Null(method.Instructions[0].NativeOffset);
    }

    private static NativeCodeReport Parse(string text) =>
        NativeJitDisassemblyParser.Parse(text, "net8.0", "sdk8", "Calc:Sum");
}
