using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentWorkspaceAccessTests
{
    [Fact]
    public async Task ReaderWaitsForAdmissionWithoutReplayingAnOperation()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var task = AgentWorkspaceAccess.EnterAsync(gate, true, default);
        Assert.False(task.IsCompleted);
        gate.Release(); Assert.True(await task); Assert.Equal(0, gate.CurrentCount);
    }
    [Fact]
    public async Task WriterStillFailsImmediatelyAndDoesNotQueue()
    {
        using var gate = new SemaphoreSlim(0, 1);
        Assert.False(await AgentWorkspaceAccess.EnterAsync(gate, false, default));
        gate.Release(); Assert.Equal(1, gate.CurrentCount);
    }
    [Fact]
    public async Task CancelledReaderDoesNotConsumeTheNextPermit()
    {
        using var gate = new SemaphoreSlim(0, 1); using var ct = new CancellationTokenSource();
        var task = AgentWorkspaceAccess.EnterAsync(gate, true, ct.Token);
        ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        gate.Release(); Assert.Equal(1, gate.CurrentCount);
    }
    [Theory]
    [InlineData(".csproj")]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    public void AttachingRequiresTheSameLoadedPath(string extension)
    {
        var path = Path.GetFullPath("A" + extension);
        Assert.True(AgentWorkspaceAccess.MatchesLoadedPath(path, path));
        Assert.False(AgentWorkspaceAccess.MatchesLoadedPath(path, Path.GetFullPath("B" + extension)));
        Assert.False(AgentWorkspaceAccess.MatchesLoadedPath(path, null));
    }
    [Fact]
    public void AttachDoesNotResolveArbitraryPathsOrTreatChildrenAsSameWorkspace()
    {
        Assert.Throws<ArgumentException>(() => AgentWorkspaceAccess.MatchesLoadedPath("relative.csproj", null));
        Assert.Throws<ArgumentException>(() => AgentWorkspaceAccess.MatchesLoadedPath(Path.GetFullPath("A.dll"), null));
        Assert.False(AgentWorkspaceAccess.MatchesLoadedPath(Path.GetFullPath("A.csproj"), Path.GetFullPath("A.sln")));
    }
    [Theory]
    [InlineData("review_queue")]
    [InlineData("review_context")]
    [InlineData("review_target")]
    [InlineData("list_types")]
    [InlineData("inspect_method")]
    [InlineData("binary_methods")]
    [InlineData("il")]
    [InlineData("attach_workspace")]
    [InlineData("approve_session")]
    public void UnloadedWorkspaceAdvicePrecedesToolSpecificFallback(string tool)
    {
        var advice = AgentRecoveryAdvice.ForFailure(tool, "InvalidOperationException: Open a trusted .sln or .csproj first.");
        Assert.Equal("workspace_unloaded", advice.Code);
        Assert.Contains("tenka_open_project", advice.NextAction);
        Assert.Contains("expectedSnapshotId=null", advice.NextAction);
    }
    [Fact]
    public void UnrelatedErrorsAreNotMisclassifiedAsUnloaded()
    {
        Assert.NotEqual("workspace_unloaded", AgentRecoveryAdvice.ForFailure("il", "InvalidOperationException: other failure").Code);
    }
    [Fact]
    public void SessionConsentAndPerCallApprovalHaveDifferentRecovery()
    {
        var advice = AgentRecoveryAdvice.ForFailure("approve_session", "UnauthorizedAccessException: Session approval requires explicit user consent.");
        Assert.Equal("session_consent_required", advice.Code);
        Assert.Contains("project", advice.NextAction);
        Assert.Contains("tenka_approve_session", advice.NextAction);
    }
}
