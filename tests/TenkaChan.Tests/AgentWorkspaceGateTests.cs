using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentWorkspaceGateTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("ABC", "ABC")]
    public void MatchingSnapshotsProceed(string? expected, string? current)
    {
        Assert.True(AgentWorkspaceGate.Matches(expected, current));
    }

    [Theory]
    [InlineData(null, "ABC")]
    [InlineData("ABC", null)]
    [InlineData("ABC", "DEF")]
    public void MismatchedSnapshotsBlock(string? expected, string? current)
    {
        Assert.False(AgentWorkspaceGate.Matches(expected, current));
    }

    [Fact]
    public void LoadedWorkspaceMessageNamesCurrentSnapshotAndRetry()
    {
        var message = AgentWorkspaceGate.ConflictMessage(expectedSnapshotId: null, currentSnapshotId: "ABC123");
        Assert.Contains("ABC123", message);
        Assert.Contains("expectedSnapshotId=\"ABC123\"", message);
        Assert.DoesNotContain("(none loaded)", message);
    }

    [Fact]
    public void UnloadedWorkspaceMessageTellsCallerToOpenWithoutExpectedSnapshot()
    {
        var message = AgentWorkspaceGate.ConflictMessage(expectedSnapshotId: "STALE", currentSnapshotId: null);
        Assert.Contains("(none loaded)", message);
        Assert.Contains("no expectedSnapshotId", message);
    }
}
