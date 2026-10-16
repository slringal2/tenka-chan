using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentSessionApprovalTests
{
    private static string Project(string name = "A") => Path.GetFullPath(name + ".csproj");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static void Grant(AgentSessionApprovals store, string session = "s", string[]? tools = null, int minutes = 10) =>
        store.Grant(session, "snapshot", Project(), tools ?? ["build", "jit"], minutes, true, true, true);

    [Fact]
    public void GrantRequiresAnExplicitConsentCall()
    {
        var store = new AgentSessionApprovals();
        Assert.Throws<UnauthorizedAccessException>(() => store.Grant("s", "snapshot", Project(), ["build"], 10, false, true, true));
        Assert.Null(store.Get("s"));
    }
    [Fact]
    public void MatchingProjectToolAndSessionCanOmitPerCallApproval()
    {
        var store = new AgentSessionApprovals(); Grant(store);
        Assert.True(store.Authorize("s", Project(), "build", false, true, true));
        Assert.True(store.Authorize("s", Project(), "jit", false, true, true));
    }
    [Theory]
    [InlineData("other", "A", "build")]
    [InlineData("s", "B", "build")]
    [InlineData("s", "A", "trace")]
    public void GrantCannotLeakAcrossSessionsProjectsOrTools(string session, string project, string tool)
    {
        var store = new AgentSessionApprovals(); Grant(store);
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize(session, Project(project), tool, false, true, true));
    }
    [Fact]
    public void ExpiryIsExactAndReadsAndCallsDoNotExtendIt()
    {
        var time = new Clock(); var store = new AgentSessionApprovals(time); Grant(store, minutes: 1);
        var expiry = store.Get("s")!.ExpiresUtc;
        time.Now = expiry.AddTicks(-1);
        Assert.True(store.Authorize("s", Project(), "build", false, true, true));
        Assert.Equal(expiry, store.Get("s")!.ExpiresUtc);
        time.Now = expiry;
        Assert.Null(store.Get("s"));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "build", false, true, true));
    }
    [Fact]
    public void RevokeDoesNotAffectAnotherSession()
    {
        var store = new AgentSessionApprovals(); Grant(store); Grant(store, "other");
        Assert.True(store.Revoke("s")); Assert.False(store.Revoke("s"));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "build", false, true, true));
        Assert.True(store.Authorize("other", Project(), "build", false, true, true));
    }
    [Theory]
    [InlineData("build", false, true)]
    [InlineData("jit", true, false)]
    [InlineData("run_companion", false, true)]
    [InlineData("run_companion", true, false)]
    [InlineData("run_experiment", false, true)]
    [InlineData("run_experiment", true, false)]
    public void GrantNeverOverridesDisabledHostGates(string tool, bool build, bool execution)
    {
        var store = new AgentSessionApprovals();
        Assert.Throws<UnauthorizedAccessException>(() => store.Grant("s", "snapshot", Project(), [tool], 10, true, build, execution));
        Assert.Null(store.Get("s"));
    }
    [Fact]
    public void HostGatesAreRecheckedAfterAGrant()
    {
        var store = new AgentSessionApprovals(); Grant(store);
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "build", false, false, true));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "jit", false, true, false));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "jit", true, true, false));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void DurationIsBounded(int minutes)
    {
        var store = new AgentSessionApprovals();
        Assert.Throws<ArgumentException>(() => Grant(store, minutes: minutes));
    }
    [Theory]
    [InlineData("open_project")]
    [InlineData("*")]
    [InlineData("tenka_build")]
    [InlineData("review_mark")]
    public void OtherToolsCannotBeGranted(string tool)
    {
        var store = new AgentSessionApprovals();
        Assert.Throws<ArgumentException>(() => Grant(store, tools: [tool]));
        Assert.Throws<ArgumentException>(() => store.Authorize("s", Project(), tool, true, true, true));
    }
    [Fact]
    public void ToolListAndProjectScopeAreValidated()
    {
        var store = new AgentSessionApprovals();
        Assert.Throws<ArgumentException>(() => Grant(store, tools: []));
        Assert.Throws<ArgumentException>(() => Grant(store, tools: ["build", "build", "build", "build", "build", "build"]));
        Assert.Throws<ArgumentException>(() => store.Grant("s", "snapshot", "relative.csproj", ["build"], 10, true, true, true));
        Assert.Throws<ArgumentException>(() => store.Grant("s", "snapshot", Path.GetFullPath("A.sln"), ["build"], 10, true, true, true));
    }
    [Fact]
    public void ReturnedCollectionsCannotMutateTheGrant()
    {
        var store = new AgentSessionApprovals(); var tools = new[] { "build" };
        var grant = store.Grant("s", "snapshot", Project(), tools, 10, true, true, true);
        tools[0] = "trace"; grant.Tools[0] = "trace"; store.Get("s")!.Tools[0] = "trace";
        Assert.True(store.Authorize("s", Project(), "build", false, true, true));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "trace", false, true, true));
    }
    [Fact]
    public void NewGrantReplacesRatherThanAccumulatesPermissions()
    {
        var store = new AgentSessionApprovals(); Grant(store);
        store.Grant("s", "new-snapshot", Project("B"), ["trace"], 1, true, true, true);
        Assert.True(store.Authorize("s", Project("B"), "trace", false, true, true));
        Assert.Throws<UnauthorizedAccessException>(() => store.Authorize("s", Project(), "build", false, true, true));
    }
    [Fact]
    public void ExplicitOneOffApprovalStillWorksWithoutCreatingAGrant()
    {
        var store = new AgentSessionApprovals();
        Assert.True(store.Authorize("s", Project("B"), "trace", true, true, true));
        Assert.Null(store.Get("s"));
    }
    [Fact]
    public void StaleSessionsCanBePrunedWithoutEvictingLiveGrants()
    {
        var store = new AgentSessionApprovals();
        for (var i = 0; i < AgentJournal.MaxSessions; i++) Grant(store, "s" + i);
        Assert.Throws<InvalidOperationException>(() => Grant(store, "overflow"));
        store.RetainSessions(["s0"]);
        Assert.NotNull(store.Get("s0")); Assert.Null(store.Get("s1"));
        Grant(store, "new"); Assert.NotNull(store.Get("new"));
    }
}
