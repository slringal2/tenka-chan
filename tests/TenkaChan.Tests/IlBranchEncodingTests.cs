using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class IlBranchEncodingTests
{
    [Theory]
    [InlineData(new byte[] { 0x00, 0x2B, 0xFD, 0x2A }, 1)]
    [InlineData(new byte[] { 0x2B, 0xFE, 0x2A }, 0)]
    [InlineData(new byte[] { 0x00, 0x38, 0xFA, 0xFF, 0xFF, 0xFF, 0x2A }, 1)]
    public void DecodesBackwardBranchesWithoutCheckedSignedCastOverflow(byte[] il, int index)
    {
        using var file = File.OpenRead(typeof(IlBranchEncodingTests).Assembly.Location);
        using var pe = new PEReader(file);
        var instructions = ResolvedIlDecoder.Decode(il, pe.GetMetadataReader(), []);
        Assert.Equal(0, Assert.Single(instructions[index].BranchTargets));
        Assert.Equal("IL_0000", instructions[index].Operand);
    }

    [Fact]
    public void RejectsUnrepresentableRelativeTargetsAsInvalidData()
    {
        using var file = File.OpenRead(typeof(IlBranchEncodingTests).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        Assert.Throws<InvalidDataException>(() => ResolvedIlDecoder.Decode(
            [0x38, 0xFF, 0xFF, 0xFF, 0x7F, 0x2A], metadata, []));
        Assert.Throws<InvalidDataException>(() => ResolvedIlDecoder.Decode(
            [0x38, 0x00, 0x00, 0x00, 0x80, 0x2A], metadata, []));
    }
}
