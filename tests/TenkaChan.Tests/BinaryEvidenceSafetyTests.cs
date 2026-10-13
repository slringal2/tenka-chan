using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class BinaryEvidenceSafetyTests
{
    [Fact]
    public void CapturedFileHashesPreventPostRunReplacementAndPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "binary-Subject.dll");
            File.WriteAllBytes(file, [1, 2, 3]);
            var records = NativeBinaryArtifacts.Snapshot(root);
            Assert.Single(records);
            Assert.Equal(file, NativeBinaryArtifacts.Verify(root, records, "binary-Subject.dll"));
            Assert.Throws<ArgumentException>(() => NativeBinaryArtifacts.Verify(root, records, "../binary-Subject.dll"));
            File.WriteAllBytes(file, [3, 2, 1]);
            Assert.Throws<InvalidDataException>(() => NativeBinaryArtifacts.Verify(root, records, "binary-Subject.dll"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnExperimentCannotRunTwiceOrReuseOldArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-reserve-" + Guid.NewGuid().ToString("N"));
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);
        try
        {
            using (var first = ExperimentExecutionReservation.Acquire(root, artifacts))
                Assert.Throws<InvalidOperationException>(() => ExperimentExecutionReservation.Acquire(root, artifacts));
            Assert.Throws<InvalidOperationException>(() => ExperimentExecutionReservation.Acquire(root, artifacts));
            File.WriteAllText(Path.Combine(artifacts, "old.trx"), "stale");
            Assert.Throws<InvalidOperationException>(() => ExperimentExecutionReservation.Acquire(root, artifacts));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyIlToolDoesNotGuessAnOverload()
    {
        var assembly = typeof(BinaryEvidenceSafetyTests).Assembly.Location;
        var guard = new WorkspaceGuard([Path.GetDirectoryName(assembly)!], false, false);
        Assert.Throws<InvalidOperationException>(() => new IlInspector(guard).Inspect(assembly,
            typeof(BinaryEvidenceSafetyTests).FullName!, nameof(OverloadFixture)));
    }
    public static int OverloadFixture(int value) => value;
    public static long OverloadFixture(long value) => value;

    [Fact]
    public void RejectsTruncatedOpcodesAndInvalidBranchTargets()
    {
        using var file = File.OpenRead(typeof(BinaryEvidenceSafetyTests).Assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        Assert.Throws<InvalidDataException>(() => ResolvedIlDecoder.Decode([0xFE], metadata, []));
        Assert.Throws<InvalidDataException>(() => ResolvedIlDecoder.Decode([0x2B, 0x7F, 0x2A], metadata, []));
        Assert.Throws<InvalidDataException>(() => ResolvedIlDecoder.Decode([0x45, 0xFF, 0xFF, 0xFF, 0xFF], metadata, []));
    }

    [Fact]
    public void NeverComparesDifferentJitModesOrTruncatedEvidenceAsImprovement()
    {
        const string code = "; Assembly listing for method Calc:Sum(int):int (FullOpts)\n; Emitting BLENDED_CODE for X64\n        C3 ret\n; Total bytes of code 1";
        var baseline = NativeJitDisassemblyParser.Parse(code, "net8.0", "sdk8", "Calc:Sum");
        var tier0 = NativeJitDisassemblyParser.Parse(code.Replace("FullOpts", "Tier0"), "net8.0", "sdk8", "Calc:Sum");
        var different = NativeCodeComparator.Compare(baseline, tier0);
        Assert.Empty(different.Methods);
        Assert.Contains(different.UnpairedMethods, m => m.Contains("compilation tier"));
        var incomplete = NativeCodeComparator.Compare(baseline, baseline with { Truncated = true });
        Assert.Equal("incomplete_codegen_evidence", incomplete.Status);
        Assert.Empty(incomplete.Methods);
    }
}
