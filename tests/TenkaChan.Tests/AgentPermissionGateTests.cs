using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentPermissionGateTests
{
    [Fact]
    public void DisabledServerNamesOnlyTheEnvironmentSwitch()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            AgentPermissionGate.Require("build/open", "TENKA_AGENT_ALLOW_BUILD", allowedByServer: false, approvedByCall: true));
        Assert.Contains("TENKA_AGENT_ALLOW_BUILD", ex.Message);
        Assert.Contains("disabled on the server", ex.Message);
        Assert.DoesNotContain("Re-invoke this tool with approved=true", ex.Message);
    }

    [Fact]
    public void MissingApprovalNamesTheArgumentNotTheEnvironmentSwitch()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            AgentPermissionGate.Require("build/open", "TENKA_AGENT_ALLOW_BUILD", allowedByServer: true, approvedByCall: false));
        Assert.Contains("approved=true", ex.Message);
        Assert.Contains("already enabled", ex.Message);
        Assert.DoesNotContain("disabled on the server", ex.Message);
    }

    [Fact]
    public void BothGatesOpenDoesNotThrow()
    {
        AgentPermissionGate.Require("execution", "TENKA_AGENT_ALLOW_EXECUTION", allowedByServer: true, approvedByCall: true);
    }
}
