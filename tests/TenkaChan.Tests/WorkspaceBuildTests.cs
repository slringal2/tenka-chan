using System.Diagnostics;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

[CollectionDefinition("Workspace build environment", DisableParallelization = true)]
public sealed class WorkspaceBuildEnvironmentCollection { }

[Collection("Workspace build environment")]
public sealed class WorkspaceBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-build-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldEnabled = Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC");
    private readonly string _project;
    public WorkspaceBuildTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "Project with spaces.csproj");
        File.WriteAllText(_project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        Environment.SetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC", "1");
    }
    private WorkspaceGuard Guard(bool trusted = true) => new([_root], trusted, false);

    [Fact]
    public async Task ApprovalIsRequiredEvenWhenHostAndMsBuildAreEnabled()
    {
        var process = new FakeProcess();
        var runner = new TrustedWorkspaceBuilder(Guard(), process);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.BuildAsync(_project, null, false, false));
        Assert.Equal(0, process.Calls);
    }

    [Fact]
    public async Task MsBuildTrustIsRequired()
    {
        var process = new FakeProcess();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new TrustedWorkspaceBuilder(Guard(false), process).BuildAsync(_project, null, false, true));
        Assert.Equal(0, process.Calls);
    }

    [Fact]
    public async Task LocalExecutionFlagIsRequired()
    {
        Environment.SetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC", "0");
        var process = new FakeProcess();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new TrustedWorkspaceBuilder(Guard(), process).BuildAsync(_project, null, false, true));
        Assert.Equal(0, process.Calls);
    }

    [Fact]
    public async Task ProjectOutsideAllowedRootsCannotStartAProcess()
    {
        var process = new FakeProcess();
        var other = Path.Combine(Path.GetDirectoryName(_root)!, "other.csproj");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new TrustedWorkspaceBuilder(Guard(), process).BuildAsync(other, null, false, true));
        Assert.Equal(0, process.Calls);
    }

    [Theory]
    [InlineData("net7.0")]
    [InlineData("net48")]
    [InlineData("net8.0;Something=1")]
    [InlineData("--configuration=Debug")]
    [InlineData("net8.0\n-property:Bad=true")]
    public async Task UnsupportedOrInjectedFrameworkIsRejected(string framework)
    {
        var process = new FakeProcess();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new TrustedWorkspaceBuilder(Guard(), process).BuildAsync(_project, framework, false, true));
        Assert.Equal(0, process.Calls);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(301)]
    public async Task InvalidTimeoutCannotStartBuild(int timeout)
    {
        var process = new FakeProcess();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new TrustedWorkspaceBuilder(Guard(), process).BuildAsync(_project, null, false, true, timeout));
        Assert.Equal(0, process.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("net8.0")]
    [InlineData("net8.0-windows")]
    [InlineData("net10.0")]
    public void BuildUsesArgumentListAndDisablesImplicitRestoreByDefault(string? framework)
    {
        var info = TrustedWorkspaceBuilder.CreateStartInfo(_project, framework, false);
        Assert.Equal("dotnet", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal("", info.Arguments);
        Assert.Equal(_project, info.ArgumentList[1]);
        Assert.Contains("Release", info.ArgumentList);
        Assert.Contains("--no-restore", info.ArgumentList);
        Assert.Contains("--disable-build-servers", info.ArgumentList);
        Assert.Equal(_root, info.WorkingDirectory);
        if (framework is not null) Assert.Equal(framework, info.ArgumentList.Last());
    }

    [Fact]
    public void RestoreRequiresExplicitFlag()
    {
        Assert.DoesNotContain("--no-restore", TrustedWorkspaceBuilder.CreateStartInfo(_project, null, true).ArgumentList);
    }

    [Fact]
    public void UnrelatedEnvironmentAndProfilingHooksAreNotForwarded()
    {
        var key = "TENKA_TEST_AMBIENT_CREDENTIAL";
        var old = Environment.GetEnvironmentVariable(key);
        var oldHook = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        try
        {
            Environment.SetEnvironmentVariable(key, "not-a-real-secret");
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "must-not-be-forwarded.dll");
            var info = TrustedWorkspaceBuilder.CreateStartInfo(_project, null, false);
            Assert.False(info.Environment.ContainsKey(key));
            Assert.False(info.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
            Assert.False(info.Environment.ContainsKey("TENKA_ENABLE_LOCAL_EXEC"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, old);
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", oldHook);
        }
    }

    [Theory]
    [InlineData(0, false, false, "build_succeeded")]
    [InlineData(1, false, false, "build_failed")]
    [InlineData(0, true, false, "timed_out")]
    [InlineData(0, false, true, "cancelled")]
    public async Task ExitAndCancellationStatesRemainDistinct(int exit, bool timeout, bool cancelled, string status)
    {
        var process = new FakeProcess { Result = new(exit, "out", "err", timeout, cancelled) };
        var result = await new TrustedWorkspaceBuilder(Guard(), process).BuildAsync(_project, "net8.0", false, true);
        Assert.Equal(status, result.Status);
        Assert.Equal("out", result.Output);
        Assert.Equal("err", result.Stderr);
        Assert.Contains("UNSANDBOXED", result.Note);
        Assert.Equal(1, process.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestIsNotQueuedOrRunTwice()
    {
        var process = new BlockingProcess();
        var runner = new TrustedWorkspaceBuilder(Guard(), process);
        var first = runner.BuildAsync(_project, null, false, true);
        await process.Started.Task;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.BuildAsync(_project, null, false, true));
        }
        finally { process.Complete.TrySetResult(new WorkspaceBuildProcessResult(0, "", "")); }
        Assert.Equal("build_succeeded", (await first).Status);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotStartAProcess()
    {
        var process = new FakeProcess();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TrustedWorkspaceBuilder(Guard(), process).BuildAsync(_project, null, false, true, ct: cancelled.Token));
        Assert.Equal(0, process.Calls);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC", _oldEnabled);
        Directory.Delete(_root, true);
    }
    private sealed class FakeProcess : IWorkspaceBuildProcess
    {
        public int Calls { get; private set; }
        public WorkspaceBuildProcessResult Result { get; init; } = new(0, "", "");
        public Task<WorkspaceBuildProcessResult> RunAsync(ProcessStartInfo info, int timeout, CancellationToken ct)
        { Calls++; return Task.FromResult(Result); }
    }
    private sealed class BlockingProcess : IWorkspaceBuildProcess
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkspaceBuildProcessResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkspaceBuildProcessResult> RunAsync(ProcessStartInfo info, int timeout, CancellationToken ct)
        { Started.TrySetResult(true); return Complete.Task; }
    }
}
