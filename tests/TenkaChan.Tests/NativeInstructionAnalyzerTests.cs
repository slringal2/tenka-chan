using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativeInstructionAnalyzerTests
{
    private static NativeMethodDisassembly Method(params string[] encodings)
    {
        var offset = 0;
        var instructions = encodings.Select(bytes =>
        {
            var result = new NativeInstruction(offset, bytes, "untrusted", "", "unknown");
            offset += bytes.Length / 2;
            return result;
        }).ToArray();
        return new("App:Work():int", "App", "Work", "FullOpts", "x64", offset,
            instructions, [], new Dictionary<string, int>()) { OffsetsReliable = true };
    }

    [Fact]
    public void DecodesBytesInsteadOfBelievingFakeMnemonics()
    {
        var report = NativeInstructionAnalyzer.Analyze(Method("31C0", "C3"));
        Assert.True(report.BytesDecoded);
        Assert.Equal("xor", report.Instructions[0].Mnemonic);
        Assert.Equal("Return", report.Instructions[1].FlowControl);
        Assert.All(report.Instructions, i => Assert.StartsWith("different_mnemonic", i.ListingAgreement));
        Assert.Equal(64, report.CodeSha256!.Length);
    }

    [Theory]
    [InlineData("x64", true)]
    [InlineData("x86", true)]
    [InlineData("arm64", false)]
    [InlineData("unknown", false)]
    public void ArchitectureIsExplicit(string arch, bool supported)
    {
        var report = NativeInstructionAnalyzer.Analyze(Method("C3") with { Architecture = arch });
        Assert.Equal(supported, report.BytesDecoded);
        if (!supported) Assert.Empty(report.Blocks);
    }

    [Fact]
    public void GraphUsesActualRelativeBranchesAndInstructionBoundaries()
    {
        // xor eax,eax; inc eax; cmp eax,10; jl offset 2; ret
        var report = NativeInstructionAnalyzer.Analyze(Method("31C0", "FFC0", "83F80A", "7CF9", "C3"));
        Assert.True(report.BytesDecoded);
        Assert.Equal(2, report.Instructions[3].LocalBranchTarget);
        Assert.Equal(3, report.Blocks.Count);
        Assert.Contains(report.Edges, e => e.FromBlock == 1 && e.ToBlock == 1 && e.Kind == "branch");
        Assert.Contains(report.InvestigationSites, s => s.Kind == "backward_branch_candidate");
        Assert.All(report.InvestigationSites, s => { Assert.Equal("P2", s.Priority); Assert.False(s.DefectConfirmed); });
    }

    [Fact]
    public void BranchIntoInstructionMiddleNeverInventsABlock()
    {
        // jmp offset 3, which is inside the following multi-byte mov
        var report = NativeInstructionAnalyzer.Analyze(Method("EB01", "B878563412", "C3"));
        Assert.True(report.BytesDecoded);
        Assert.Equal("inside_instruction_unresolved", report.Instructions[0].BranchTargetStatus);
        Assert.DoesNotContain(report.Blocks, b => b.StartOffset == 3);
        Assert.Equal("normal_flow_partial", report.ControlFlowStatus);
    }

    [Fact]
    public void IndirectAndOutsideTargetsRemainUnresolved()
    {
        var report = NativeInstructionAnalyzer.Analyze(Method("FFD0", "EB7F", "C3"));
        Assert.Equal("IndirectCall", report.Instructions[0].FlowControl);
        Assert.Null(report.Instructions[0].LocalBranchTarget);
        Assert.Contains(report.Edges, e => e.Kind == "outside_method_unresolved" && e.ToBlock is null);
        Assert.Contains(report.InvestigationSites, s => s.Kind == "indirect_call");
    }

    [Fact]
    public void RejectsBadSizesMissingBytesAndInstructionRowSplits()
    {
        Assert.False(NativeInstructionAnalyzer.Analyze(Method("31C0C3")).BytesDecoded);
        Assert.False(NativeInstructionAnalyzer.Analyze(Method("0F")).BytesDecoded);
        Assert.False(NativeInstructionAnalyzer.Analyze(Method("C3") with { CodeSizeBytes = 5 }).BytesDecoded);
        Assert.False(NativeInstructionAnalyzer.Analyze(Method("C3") with { OffsetsReliable = false }).BytesDecoded);
        Assert.False(NativeInstructionAnalyzer.Analyze(Method("ZZ")).BytesDecoded);
        Assert.False(NativeInstructionAnalyzer.Analyze(Method("C3") with {
            Instructions = [new NativeInstruction(9, "C3", "ret", "", "return")] }).BytesDecoded);
    }

    [Fact]
    public void ReportsOperandSemanticsWithoutPretendingToMeasureSpillsOrSimdSpeed()
    {
        var report = NativeInstructionAnalyzer.Analyze(Method("488B442408", "C5F258C2", "F7F9", "C3"));
        Assert.True(report.BytesDecoded);
        Assert.Contains(report.Instructions[0].Memory, m => m.Base == "RSP");
        Assert.Equal("vaddss", report.Instructions[1].Mnemonic);
        Assert.Contains("AVX", report.CpuFeatures);
        Assert.Contains(report.InvestigationSites, s => s.Kind == "integer_division");
        Assert.Contains(report.Limitations, x => x.Contains("Scalar AVX"));
    }

    [Fact]
    public void ReadsManagedAssemblyWithoutRunningItAndRefusesPathEscape()
    {
        var dll = typeof(NativeInstructionAnalyzer).Assembly.Location;
        var guard = new WorkspaceGuard([Path.GetDirectoryName(dll)!], false, false);
        var binary = new BinarySourceInspector(guard);
        var catalog = binary.Methods(dll, "NativeInstructionAnalyzer", 200);
        Assert.NotEmpty(catalog.Methods);
        Assert.All(catalog.Methods, m => Assert.True(m.MetadataToken >= 0x06000001));
        var entry = catalog.Methods.First(m => m.HasBody);
        Assert.NotEmpty(binary.Inspect(dll, entry.MetadataToken).Instructions);
        Assert.Throws<ArgumentException>(() => binary.Inspect(dll, 1));
        Assert.Throws<UnauthorizedAccessException>(() =>
            new BinarySourceInspector(new WorkspaceGuard([], false, false)).Methods(dll));
    }

    [Fact]
    public async Task IndependentlyDecodesRealNet8RyuJitOutput()
    {
        var fixture = Environment.GetEnvironmentVariable("TENKA_ASSEMBLY_SMOKE_DLL");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var guard = new WorkspaceGuard([Path.GetDirectoryName(fixture)!], false, false);
        var runner = new TrustedLocalJitRunner(guard, true);
        var captured = await runner.CaptureAsync(fixture, "LocalJitFixture:Compute", true, 45);
        Assert.Equal("jit_completed", captured.Status);
        var reports = captured.NativeDisassembly!.Methods.Select(NativeInstructionAnalyzer.Analyze).ToArray();
        Assert.Contains(reports, r => r.BytesDecoded && r.Instructions.Count > 0);
        var path = Environment.GetEnvironmentVariable("TENKA_ASSEMBLY_TEST_REPORT");
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }
}
