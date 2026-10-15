using System.Net;
using System.Text;
using System.Text.Json;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentSessionClientTests
{
    private const string Instance = "11111111111111111111111111111111";
    private const string Session = "22222222222222222222222222222222";
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact] public async Task ListUsesOnlyManagementRouteAndNoInvestigationSession()
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(200, "{\"instanceId\":\"" + Instance + "\",\"items\":[]}")));
        using var result = JsonDocument.Parse(await f.Client.ListAsync());
        Assert.Equal(0, result.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("/agent/v1/sessions/list", f.Route); Assert.Equal("{}", f.Body);
        Assert.Equal("Bearer " + Token, f.Authorization); Assert.Equal(1, f.Calls);
    }
    [Theory]
    [InlineData(null, true, "invalid_arguments")]
    [InlineData("bad", true, "invalid_arguments")]
    [InlineData(Session, false, "approval_required")]
    public async Task InvalidOrUnapprovedEndNeverSends(string? sessionId, bool approved, string code)
    {
        using var f = new Fixture((_, _) => throw new Exception("Must not send"));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.EndAsync(sessionId, approved));
        Assert.Equal(code, ex.Code); Assert.Equal(0, f.Calls);
    }
    [Theory]
    [InlineData("finished")]
    [InlineData("expired")]
    public async Task EndUsesOnlyExplicitIdAndApproval(string status)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(200, "{\"id\":\"" + Session + "\",\"status\":\"" + status + "\"}")));
        await f.Client.EndAsync(Session, true);
        Assert.Equal("/agent/v1/sessions/end", f.Route); Assert.Equal(1, f.Calls);
        using var request = JsonDocument.Parse(f.Body!);
        Assert.Equal(Session, request.RootElement.GetProperty("sessionId").GetString());
        Assert.True(request.RootElement.GetProperty("approved").GetBoolean());
    }
    [Theory]
    [InlineData(409, "application/json", "{\"error\":\"session_busy\"}", true)]
    [InlineData(500, "application/json", "{\"error\":\"private-stack\"}", false)]
    [InlineData(409, "text/html", "<h1>private-stack</h1>", false)]
    public async Task ErrorDetailsAreAllowlistedAndNeverRetried(int status, string mediaType, string body, bool expectedDetail)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(status, body, mediaType)));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync());
        Assert.Equal("http_" + status, ex.Code); Assert.Equal(1, f.Calls);
        Assert.DoesNotContain("private-stack", ex.Message);
        if (expectedDetail) Assert.Contains("session_busy", ex.Message);
    }
    [Fact] public async Task ErrorCredentialsAreRedacted()
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(409, JsonSerializer.Serialize(new { error = "Bearer " + Token }))));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync());
        Assert.DoesNotContain(Token, ex.Message); Assert.Contains("redacted", ex.Message);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"instanceId\":\"wrong\",\"items\":[]}")]
    [InlineData("{\"instanceId\":\"11111111111111111111111111111111\",\"items\":null}")]
    public async Task InvalidListIsNotReportedAsSuccess(string body)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(200, body)));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync())).Code);
        Assert.Equal(1, f.Calls);
    }
    [Fact] public async Task EndRejectsWrongSessionOrRunningStatus()
    {
        foreach (var body in new[] { "{\"id\":\"wrong\",\"status\":\"finished\"}", "{\"id\":\"" + Session + "\",\"status\":\"running\"}" })
        {
            using var f = new Fixture((_, _) => Task.FromResult(Http(200, body)));
            Assert.Equal("invalid_response", (await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.EndAsync(Session, true))).Code);
        }
    }
    [Fact] public async Task SuccessBodyHasBoundedSize()
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(200, new string('x', 1_000_001))));
        Assert.Equal("response_limit", (await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync())).Code);
    }
    [Fact] public async Task TransportFailureDoesNotLeakOrRetry()
    {
        using var f = new Fixture((_, _) => throw new HttpRequestException("private " + Token));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync());
        Assert.Equal("transport_error", ex.Code); Assert.DoesNotContain(Token, ex.Message); Assert.Equal(1, f.Calls);
    }
    [Fact] public async Task ResponseBodyWaitHasDeadline()
    {
        using var f = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new WaitingStream()) }), TimeSpan.FromMilliseconds(100));
        Assert.Equal("request_timeout", (await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync().WaitAsync(TimeSpan.FromSeconds(5)))).Code);
        Assert.Equal(1, f.Calls);
    }
    [Fact] public async Task CallerCancellationRemainsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new Fixture(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Http(200, "{}"); });
        using var cts = new CancellationTokenSource(); var pending = f.Client.ListAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(1, f.Calls);
    }
    [Theory]
    [InlineData(null, "connection_file_missing")]
    [InlineData("{}", "connection_file_invalid")]
    [InlineData("null", "connection_file_invalid")]
    [InlineData("bad-json", "connection_file_invalid")]
    public async Task InvalidDescriptorNeverSends(string? descriptor, string code)
    {
        using var f = new Fixture((_, _) => throw new Exception("Must not send"));
        if (descriptor is null) File.Delete(f.Path); else File.WriteAllText(f.Path, descriptor);
        Assert.Equal(code, (await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ListAsync())).Code);
        Assert.Equal(0, f.Calls);
    }
    private static HttpResponseMessage Http(int status, string body, string mediaType = "application/json") => new((HttpStatusCode)status)
    { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tenka-sessions-" + Guid.NewGuid().ToString("N") + ".json");
        public AgentSessionClient Client { get; }
        public int Calls; public string? Route, Body, Authorization;
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null)
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(new { schema = 1, instanceId = Instance, endpoint = "http://127.0.0.1:5077/", token = Token }));
            Client = new AgentSessionClient(new DashboardAgentOptions(Path, "test"), () => new Handler(async (request, ct) =>
            {
                Calls++; Route = request.RequestUri!.AbsolutePath; Authorization = request.Headers.Authorization?.ToString();
                Body = await request.Content!.ReadAsStringAsync(ct); return await send(request, ct);
            }), timeout ?? TimeSpan.FromSeconds(10));
        }
        public void Dispose() => File.Delete(Path);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
