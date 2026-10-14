using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class PerformanceEvidenceJoinerTests
{
    private static readonly MethodSummary Hot = new("m-hot", "App",
        "App.Work.Hot(int[])", "/src/Work.cs", 10);
    private static readonly MethodSummary Cold = new("m-cold", "App",
        "App.Work.Cold()", "/src/Work.cs", 25);
    private static readonly MethodSummary StaticOnly = new("m-never", "App",
        "App.Work.NotObserved()", "/src/Work.cs", 40);

    private static StaticHotspotReport StaticReport() => new(
        "snapshot", "static_candidate", 3, 2, 3, 2, false, false,
        [
            Ranking(Hot, 100, ["nested loops", "allocations"]),
            Ranking(Cold, 1, []),
            Ranking(StaticOnly, 20, ["possible allocation"])
        ],
        [
            new HotspotSite("s1", "m-hot", "allocation_per_iteration",
                Hot.File, 12, 5, 2, 8, "new byte[32]",
                "Repeated allocation", "Benchmark allocation"),
            new HotspotSite("s2", "m-never", "io_per_iteration",
                StaticOnly.File, 45, 2, 1, 12, "File.ReadAllBytes(path)",
                "I/O candidate", "Profile workloads")
        ],
        [], ["Static ranking is heuristic."]);

    private static HotspotMethodRanking Ranking(
        MethodSummary m, double score, string[] reasons) =>
        new(m, score, score, 1, 0, 1, 0, 0,
            0, 0, 0, 0, false, reasons);

    private static SampledHotspotReport Profile(string unit = "milliseconds") =>
        new("trace.json", "representative workload", unit, 10, 100, 4, 2, 1,
            [
                new SampledFrameCost("App.Work.Hot(int[])", 8, 65, 12,
                    65, 12, "m-hot", "matched_exact_qualified_name"),
                new SampledFrameCost("App.Work.Cold()", 9, 75, 68,
                    75, 68, "m-cold", "matched_exact_qualified_name"),
                new SampledFrameCost("Unknown.External()", 3, 15, 10,
                    15, 10, null, "unmatched"),
                new SampledFrameCost("App.Work.Overloaded()", 2, 10, 8,
                    10, 8, null, "ambiguous_overload_or_type")
            ], ["Do not infer CPU time from thread-time samples."]);

    private static NativeCodeReport Native(string snapshot = "snapshot")
    {
        var code = new NativeMethodDisassembly(
            "App.Work:Hot(int[]):int", "App.Work", "Hot", "FullOpts",
            "x64", 4,
            [new NativeInstruction(0, "C3", "ret", "", "return")],
            [], new Dictionary<string, int> { ["return"] = 1 },
            "m-hot", "matched_method_signature")
        {
            OffsetsReliable = true, OffsetValidation = "verified_complete_bytes"
        };
        return new NativeCodeReport("net8.0", "sdk8",
            "Work:Hot", "jit.txt", 1, 1, false, [code], [])
        { RoslynSnapshotId = snapshot };
    }

    [Fact]
    public void ObservedExclusiveWeightWinsOverStaticHeuristic()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), Profile(), Native(), "jit-fixture");

        Assert.Equal("observed_exclusive_milliseconds", report.PriorityBasis);
        Assert.Equal("m-cold", report.Observed[0].MethodId);
        Assert.Equal("m-hot", report.Observed[1].MethodId);
        Assert.Equal(68, report.Observed[0].ExclusivePercent);
        Assert.Equal(1, report.Observed[0].StaticSuspicionScore);
        Assert.Equal(100, report.Observed[1].StaticSuspicionScore);
        Assert.Single(report.Observed[1].NativeMethods);
        Assert.Equal("jit-fixture", report.JitExperimentId);
        Assert.Contains(report.Observed[1].StaticSites,
            s => s.Category == "allocation_per_iteration");
        Assert.Equal("m-never", Assert.Single(report.StaticOnly).MethodId);
        Assert.Null(report.StaticOnly[0].ExclusivePercent);
        Assert.Contains(report.Limitations,
            text => text.Contains("incomparable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefusesToLinkAmbiguousObservedFramesToSource()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), Profile(), null);
        var ambiguous = Assert.Single(report.Observed,
            frame => frame.MatchStatus == "ambiguous_overload_or_type");
        Assert.Null(ambiguous.MethodId);
        Assert.Empty(ambiguous.NativeMethods);
        Assert.Null(ambiguous.StaticSuspicionScore);
        Assert.Contains(ambiguous.Caveats, x => x.Contains("unambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void UnitIsPreservedInsteadOfConvertingBytesToCpuTime()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), Profile("bytes"), null);
        Assert.Equal("observed_exclusive_bytes", report.PriorityBasis);
        Assert.Equal("bytes", report.Observed[0].Unit);
    }

    [Fact]
    public void StaticOnlyModeDoesNotPretendToHaveMeasuredValues()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), null, null);
        Assert.Equal("static_heuristic_only", report.PriorityBasis);
        Assert.Empty(report.Observed);
        Assert.Equal(3, report.StaticOnly.Count);
        Assert.All(report.StaticOnly, item =>
        {
            Assert.Equal("static_candidate_only", item.EvidenceOrigin);
            Assert.Null(item.ExclusiveWeight);
        });
    }

    [Fact]
    public void RejectsMismatchedRoslynSnapshotProvenance()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceJoiner.Build("other", StaticReport(),
                Profile(), null));
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceJoiner.Build("snapshot", StaticReport(),
                Profile(), Native("other")));
    }

    [Fact]
    public void OutputLimitsAreExplicit()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), Profile(), null, maxObserved: 1, maxStatic: 1);
        Assert.Single(report.Observed);
        Assert.Single(report.StaticOnly);
        Assert.True(report.ObservedOutputTruncated);
        Assert.True(report.StaticOutputTruncated);
    }

    [Fact]
    public void DossierPreservesPdbSourceMismatchAndNativeBoundary()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), Profile(), Native());
        var point = new IlSourceRegion(
            "/historical/Work.cs", 10, 1, 10, 24,
            "SHA256", "abc", "checksum_mismatch", null);
        var evidence = new BinarySourceEvidence(
            "captured.dll", "SHA", "MVID", 0x06000001,
            "int App.Work::Hot(int[])", 8, "ILSHA",
            "verified_external_portable_pdb", "PDBSHA",
            2, false, [],
            [new IlCostSite(1, "il_boxing", "box System.Int32",
                "mapped", point, "IL site is not a runtime cost")],
            []);
        var native = Assert.Single(Native().Methods);
        var dossier = PerformanceEvidenceJoiner.Dossier(
            report, "m-hot", native, evidence,
            "captured_il_pdb_verified__source_checksum_not_verified__native_method_name_only");
        Assert.Equal(0x06000001, dossier.IlAndSource?.MetadataToken);
        Assert.Contains(dossier.Limitations,
            s => s.Contains("Source bytes differ", StringComparison.Ordinal));
        Assert.Contains(dossier.Limitations,
            s => s.Contains("native", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CannotInspectMethodNotInBoundedTriage()
    {
        var report = PerformanceEvidenceJoiner.Build("snapshot",
            StaticReport(), Profile(), null, maxStatic: 1, maxObserved: 1);
        Assert.Throws<KeyNotFoundException>(() =>
            PerformanceEvidenceJoiner.Dossier(report, "m-not-in-report",
                null, null, "none"));
    }
}
