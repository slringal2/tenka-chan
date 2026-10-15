using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentWorkflowDefaultsTests
{
    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tenka-default-tests");
    private static ExplorerProject Project(string id, string name) => new(id, name, Path.Combine(Root, name + ".csproj"), 1);

    [Fact] public void MissingSnapshotIsPinnedWithoutMutatingTheCaller()
    {
        var a = Args("{\"state\":\"pending\"}");
        var result = AgentSelectionDefaults.Resolve(a, "current", [], null, false);
        Assert.Equal("current", result.GetProperty("snapshotId").GetString());
        Assert.Equal("pending", result.GetProperty("state").GetString());
        Assert.False(a.TryGetProperty("snapshotId", out _));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"snapshotId\":null}")]
    public void MissingAndNullSnapshotRequestResolution(string json) =>
        Assert.True(AgentSelectionDefaults.NeedsResolution(Args(json), false));

    [Theory]
    [InlineData("{\"snapshotId\":\"\"}")]
    [InlineData("{\"snapshotId\":\"   \"}")]
    [InlineData("{\"snapshotId\":42}")]
    [InlineData("{\"snapshotId\":false}")]
    [InlineData("{\"snapshotId\":[]}")]
    public void InvalidExplicitSnapshotIsNotTreatedAsOmitted(string json) =>
        Assert.Contains("snapshotId", Assert.Throws<ArgumentException>(() => AgentSelectionDefaults.NeedsResolution(Args(json), false)).Message);

    [Fact] public void ExplicitStaleSnapshotIsNeverReplaced()
    {
        Assert.Contains("snapshot_changed", Assert.Throws<InvalidOperationException>(() =>
            AgentSelectionDefaults.Resolve(Args("{\"snapshotId\":\"old\"}"), "current", [], null, false)).Message);
        Assert.False(AgentSelectionDefaults.NeedsResolution(Args("{\"snapshotId\":\"old\"}"), false));
        // The no-resolution path still goes through the existing operation's explicit snapshot guard.
    }
    [Fact] public void DirectlyOpenedProjectWinsOverItsDependencies()
    {
        var app = Project("app", "App"); var dependency = Project("dep", "Dependency");
        var result = AgentSelectionDefaults.Resolve(Args("{}"), "s", [dependency, app], app.Path, true);
        Assert.Equal("app", result.GetProperty("projectId").GetString());
    }
    [Fact] public void SoleProjectCanBeSelectedFromASolution()
    {
        var result = AgentSelectionDefaults.Resolve(Args("{}"), "s", [Project("p", "Only")], Path.Combine(Root, "All.sln"), true);
        Assert.Equal("p", result.GetProperty("projectId").GetString());
    }
    [Fact] public void MultiProjectSolutionDoesNotSilentlyChooseItsFirstEntry()
    {
        Assert.Contains("ambiguous_project", Assert.Throws<ArgumentException>(() =>
            AgentSelectionDefaults.Resolve(Args("{}"), "s", [Project("a", "A"), Project("b", "B")], Path.Combine(Root, "All.sln"), true)).Message);
    }
    [Fact] public void BrowseOmissionKeepsSolutionWideScope()
    {
        var result = AgentSelectionDefaults.Resolve(Args("{}"), "s", [Project("a", "A"), Project("b", "B")], null, false);
        Assert.False(result.TryGetProperty("projectId", out _));
    }
    [Fact] public void ExplicitProjectIsPreservedNotReplacedWithTheRoot()
    {
        var a = Project("a", "A"); var b = Project("b", "B");
        var result = AgentSelectionDefaults.Resolve(Args("{\"projectId\":\"b\"}"), "s", [a, b], a.Path, true);
        Assert.Equal("b", result.GetProperty("projectId").GetString());
    }
    [Fact] public void UnknownExplicitProjectIsRejected()
    {
        Assert.Contains("invalid_project", Assert.Throws<ArgumentException>(() =>
            AgentSelectionDefaults.Resolve(Args("{\"projectId\":\"missing\"}"), "s", [Project("a", "A")], null, true)).Message);
    }
    [Theory]
    [InlineData("{\"projectId\":\"\"}")]
    [InlineData("{\"projectId\":123}")]
    public void InvalidExplicitProjectIsRejected(string json) =>
        Assert.Contains("projectId", Assert.Throws<ArgumentException>(() =>
            AgentSelectionDefaults.Resolve(Args(json), "s", [Project("a", "A")], null, true)).Message);

    [Fact] public void DefaultsDoNotInventApprovalOrReplaceReviewGuards()
    {
        var result = AgentSelectionDefaults.Resolve(Args("{\"approved\":false,\"sourceHash\":\"source\",\"contextHash\":\"context\",\"expectedRevision\":7}"), "s", [], null, false);
        Assert.False(result.GetProperty("approved").GetBoolean());
        Assert.Equal("source", result.GetProperty("sourceHash").GetString());
        Assert.Equal("context", result.GetProperty("contextHash").GetString());
        Assert.Equal(7, result.GetProperty("expectedRevision").GetInt32());
    }
    [Fact] public void MissingWorkspaceCannotBeResolved() =>
        Assert.Throws<InvalidOperationException>(() => AgentSelectionDefaults.Resolve(Args("{}"), "", [], null, false));

    [Theory]
    [InlineData(false, false, "host_build_disabled")]
    [InlineData(false, true, "host_build_disabled")]
    [InlineData(true, false, "approval_required")]
    public void BuildPolicyExplainsTheActualFailedGate(bool host, bool approved, string code)
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() => AgentOperationPermissions.RequireBuild(host, approved));
        Assert.StartsWith(code + ":", ex.Message);
        if (host) Assert.DoesNotContain("TENKA_AGENT_ALLOW_BUILD", ex.Message);
    }
    [Theory]
    [InlineData(false, false, "host_execution_disabled")]
    [InlineData(false, true, "host_execution_disabled")]
    [InlineData(true, false, "approval_required")]
    public void ExecutionPolicyExplainsTheActualFailedGate(bool host, bool approved, string code)
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() => AgentOperationPermissions.RequireExecution(host, approved));
        Assert.StartsWith(code + ":", ex.Message);
        if (host) Assert.DoesNotContain("TENKA_AGENT_ALLOW_EXECUTION", ex.Message);
    }
    [Fact] public void BothHostPolicyAndApprovalAreRequired()
    {
        AgentOperationPermissions.RequireBuild(true, true);
        AgentOperationPermissions.RequireExecution(true, true);
    }
    [Fact] public void PermissionDescriptionNeverClaimsBlanketApprovalOrSandboxing()
    {
        var p = JsonSerializer.SerializeToElement(AgentOperationPermissions.Describe(true, true, false, false));
        Assert.True(p.GetProperty("build").GetBoolean()); // Compatibility host switch.
        Assert.True(p.GetProperty("invocation").GetProperty("approvalRequired").GetBoolean());
        Assert.False(p.GetProperty("invocation").GetProperty("canExecuteWithoutApproval").GetBoolean());
        Assert.False(p.GetProperty("localPrerequisites").GetProperty("trustedMsBuild").GetBoolean());
        Assert.False(p.GetProperty("executionIsSandboxed").GetBoolean());
    }
}
