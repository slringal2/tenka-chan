using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using Xunit;

namespace TenkaChan.Tests;

public sealed class LocalJitEvidenceBridgeTests
{
    private static readonly NativeCodeReport Native = new(
        "net8.0", "local-host:test", "App:Hot", "jit.txt", 1, 1, false,
        [new NativeMethodDisassembly(
            "App:Hot():int", "App", "Hot", "FullOpts", "x64", 1,
            [new NativeInstruction(0, "C3", "ret", "", "return")],
            [], new Dictionary<string, int> { ["return"] = 1 })], []);

    private static LocalJitCaptureResult Capture(string status = "jit_completed",
        int? exitCode = 0, string? sha = null,
        NativeCodeReport? report = null) => new(
        "123456781234123412341234567890ab", status, "app.dll",
        "net8.0", "App:Hot", sha ?? new string('a', 64),
        "start", "end", exitCode, "", "", "jit.txt",
        report ?? Native, []);

    [Fact]
    public void DockerAndTrustedHostJitCannotBeAccidentallyCombined()
    {
        Assert.Throws<ArgumentException>(() =>
            PerformanceEvidenceService.ValidateNativeSelection("docker-id", "host-id"));
        PerformanceEvidenceService.ValidateNativeSelection(null, "host-id");
        PerformanceEvidenceService.ValidateNativeSelection("docker-id", null);
    }

    [Fact]
    public void CompletedSavedHostEvidenceIsReusableWithoutStartingAProcess()
    {
        Assert.Same(Native, PerformanceEvidenceService.RequireCompletedLocalCapture(Capture()));
    }

    [Fact]
    public void RejectedOrTamperedCaptureCannotBecomeMethodEvidence()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.RequireCompletedLocalCapture(Capture(status: "timed_out")));
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.RequireCompletedLocalCapture(Capture(exitCode: 1)));
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.RequireCompletedLocalCapture(Capture(sha: "not-sha256")));
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.RequireCompletedLocalCapture(Capture(sha: new string('z', 64))));
        Assert.Throws<InvalidOperationException>(() =>
            PerformanceEvidenceService.RequireCompletedLocalCapture(Capture(report: Native with { MethodCount = 0 })));
    }
}
