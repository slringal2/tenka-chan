using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class TrustedLocalJitRunnerTests
{
    [Fact]
    public async Task DisabledModeNeverLaunchesExecutable()
    {
        var runner = new TrustedLocalJitRunner(
            new WorkspaceGuard([Path.GetTempPath()], false, false), enabled: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runner.CaptureAsync("/not-an-app.dll", "Calc:Sum", trustedBinary: true));
    }

    [Fact]
    public async Task EveryHostRunRequiresExplicitTrust()
    {
        var runner = new TrustedLocalJitRunner(
            new WorkspaceGuard([Path.GetTempPath()], false, false), enabled: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runner.CaptureAsync("/not-an-app.dll", "Calc:Sum", trustedBinary: false));
    }

    [Theory]
    [InlineData("*:*")]
    [InlineData("Calculator:Sum;rm")]
    public async Task NeverPermitsUnboundedOrShellJitFilters(string filter)
    {
        var fixture = Environment.GetEnvironmentVariable("TENKA_LOCAL_JIT_SMOKE_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var runner = new TrustedLocalJitRunner(
            new WorkspaceGuard([Path.GetDirectoryName(fixture)!], false, false), enabled: true);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.CaptureAsync(fixture, filter, trustedBinary: true));
    }

    [Fact]
    public async Task RejectsExecutableOutsideWorkspaceAllowlist()
    {
        var fixture = Environment.GetEnvironmentVariable("TENKA_LOCAL_JIT_SMOKE_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var root = Path.Combine(Path.GetTempPath(), "tenka-allowlist-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runner = new TrustedLocalJitRunner(
                new WorkspaceGuard([root], false, false), enabled: true);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                runner.CaptureAsync(fixture, "LocalJitFixture:Compute", trustedBinary: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CapturesRealNativeCodeUsingHostRuntimeWithoutDocker()
    {
        var fixture = Environment.GetEnvironmentVariable("TENKA_LOCAL_JIT_SMOKE_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var runner = new TrustedLocalJitRunner(
            new WorkspaceGuard([Path.GetDirectoryName(fixture)!], false, false), enabled: true);

        var result = await runner.CaptureAsync(fixture, "LocalJitFixture:Compute",
            trustedBinary: true, timeoutSeconds: 45);
        Assert.True(result.Status == "jit_completed",
            $"status={result.Status} exit={result.ExitCode}\n" +
            $"stdout={result.Stdout}\nstderr={result.Stderr}");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("net10.0", result.TargetFramework);
        Assert.Contains("Fixture=", result.Stdout);
        Assert.True(File.Exists(result.NativeListingPath));
        var native = Assert.IsType<TenkaChan.Mcp.Analysis.NativeCodeReport>(
            result.NativeDisassembly);
        var function = Assert.Single(native.Methods,
            method => method.MethodName == "Compute");
        Assert.True(function.CodeSizeBytes > 0);
        Assert.Contains(function.Instructions, op =>
            !string.IsNullOrEmpty(op.EncodedBytes));
        var saved = runner.Get(result.Id);
        Assert.Equal("jit_completed", saved.Status);
        Assert.Equal(result.AssemblySha256, saved.AssemblySha256);
        Assert.NotEmpty(saved.NativeDisassembly!.Methods);
    }

    [Fact]
    public async Task AbsentMethodNeverCountsAsSuccessfulCapture()
    {
        var fixture = Environment.GetEnvironmentVariable("TENKA_LOCAL_JIT_SMOKE_ASSEMBLY");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var runner = new TrustedLocalJitRunner(
            new WorkspaceGuard([Path.GetDirectoryName(fixture)!], false, false), enabled: true);
        var result = await runner.CaptureAsync(fixture, "LocalJitFixture:DoesNotExist",
            trustedBinary: true, timeoutSeconds: 45);
        Assert.NotEqual("jit_completed", result.Status);
    }
}
