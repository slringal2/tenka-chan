using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativeInstructionAlignmentTests
{
    private static NativeMethodDisassembly Parse(string directive, string arch = "X64") =>
        Assert.Single(NativeJitDisassemblyParser.Parse(
            "; Assembly listing for method Fixture:Work():int (FullOpts)\n; Emitting BLENDED_CODE for " + arch +
            "\n       " + directive + "\n       31C0 xor eax, eax\n       C3 ret\n; Total bytes of code 3",
            "net8.0", "synthetic", "Fixture:Work").Methods);

    [Theory]
    [InlineData("align [0 bytes for IG03]")]
    [InlineData("ALIGN [0 bytes]")]
    public void ZeroByteAlignmentDirectiveDoesNotInventAnInstruction(string directive)
    {
        var method = Parse(directive);
        Assert.True(method.OffsetsReliable);
        Assert.Equal(2, method.Instructions.Count);
        Assert.True(NativeInstructionAnalyzer.Analyze(method).BytesDecoded);
    }

    [Theory]
    [InlineData("align [1 bytes for IG03]")]
    [InlineData("align [10 bytes]")]
    [InlineData("align [0 bytes for IG03] unknown")]
    public void MissingNonzeroOrUnknownAlignmentEncodingIsNotFilledIn(string directive)
    {
        var method = Parse(directive);
        Assert.False(method.OffsetsReliable);
        Assert.False(NativeInstructionAnalyzer.Analyze(method).BytesDecoded);
    }

    [Fact]
    public void ImportedX86HeaderSelects32BitDecoder()
    {
        var method = Parse("align [0 bytes]", "X86");
        Assert.Equal("x86", method.Architecture);
        Assert.True(NativeInstructionAnalyzer.Analyze(method).BytesDecoded);
    }
}
