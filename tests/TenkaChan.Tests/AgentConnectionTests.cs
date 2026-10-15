using System.Text.Json;
using TenkaChan.Mcp.Agent;
using Xunit;
namespace TenkaChan.Tests;

public sealed class AgentConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-agent-" + Guid.NewGuid().ToString("N"));
    private AgentConnection Connection(bool enabled = true) => new(Guid.NewGuid().ToString("N"), 5077,
        enabled, false, false, Path.Combine(_root, "connection.json"));
    [Fact] public void AuthenticationNeedsThePublishedTokenAndRejectsAllBrowserOrigins()
    {
        var c = Connection(); c.Publish();
        var saved = JsonSerializer.Deserialize<AgentConnectionFile>(File.ReadAllText(c.FilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(c.Published);
        Assert.True(c.Authorize("Bearer " + saved.Token, false, false));
        Assert.False(c.Authorize(null, false, false));
        Assert.False(c.Authorize("Bearer " + new string('0', 64), false, false));
        Assert.False(c.Authorize("Bearer " + saved.Token, true, false));
        Assert.False(c.Authorize("Bearer " + saved.Token, false, true));
        Assert.False(c.AllowBuild); Assert.False(c.AllowExecution);
    }
    [Fact] public void DisabledGatewayPublishesNoCredential()
    {
        var c = Connection(false); c.Publish(); Assert.False(c.Published);
        Assert.False(File.Exists(c.FilePath)); Assert.False(c.Authorize("Bearer anything", false, false));
    }
    [Fact] public void CredentialsRotateBetweenServerInstances()
    {
        var a = Connection(); a.Publish(); var old = File.ReadAllText(a.FilePath);
        var b = Connection(); b.Publish(); Assert.NotEqual(old, File.ReadAllText(b.FilePath));
        a.RemoveOwnFile(); Assert.True(File.Exists(b.FilePath));
        b.RemoveOwnFile(); Assert.False(File.Exists(b.FilePath));
    }
    [Fact] public void MonitorNeverContainsBearerCredentials()
    {
        var j = new AgentJournal(); var c = Connection(); c.Publish();
        var token = JsonSerializer.Deserialize<AgentConnectionFile>(File.ReadAllText(c.FilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Token;
        Assert.DoesNotContain(token, JsonSerializer.Serialize(j.Monitor(c.Enabled, c.AllowBuild, c.AllowExecution)));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
