using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DashboardAgentClientFailureTests
{
    private const string Session = "11111111111111111111111111111111";
    private const string Call = "22222222222222222222222222222222";
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("failed", "tool_failed")]
    [InlineData("cancelled", "tool_cancelled")]
    public async Task FailedToolKeepsSafeDetailAndCorrelationWithoutReplay(string outcome, string code)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Reply(outcome,
            "Enable TENKA_AGENT_ALLOW_BUILD, then approve this invocation. Bearer " + Token)));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("open_project", new { approved = false }));
        Assert.IsAssignableFrom<McpException>(ex);
        using var doc = JsonDocument.Parse(ex.Message);
        var root = doc.RootElement;
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.Equal(Session, root.GetProperty("sessionId").GetString());
        Assert.Equal(Call, root.GetProperty("callId").GetString());
        Assert.Equal(f.LastRequestId, root.GetProperty("requestId").GetString());
        Assert.False(root.GetProperty("automaticRetry").GetBoolean());
        Assert.Contains("TENKA_AGENT_ALLOW_BUILD", root.GetProperty("message").GetString());
        Assert.DoesNotContain(Token, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, f.Calls);
        Assert.Equal(1, f.Begins);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(413)]
    public async Task HttpErrorsKeepOnlyTheGatewaysSafeDto(int status)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(status,
            JsonSerializer.Serialize(new { error = "Session expired. " + Token, stackTrace = "private-stack" }), "application/json")));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("context", new { }));
        using var doc = JsonDocument.Parse(ex.Message);
        Assert.Equal("http_" + status, ex.Code);
        Assert.Equal(status, doc.RootElement.GetProperty("httpStatus").GetInt32());
        Assert.Contains("Session expired", doc.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("private-stack", ex.Message);
        Assert.Equal(1, f.Calls);
    }

    [Theory]
    [InlineData(500, "application/json", "{\"error\":\"private-stack\"}")]
    [InlineData(409, "text/html", "<h1>private-stack</h1>")]
    [InlineData(409, "application/json", "{\"detail\":\"private-stack\"}")]
    [InlineData(409, "application/json", "{\"error\":{\"stack\":\"private-stack\"}}")]
    [InlineData(409, "application/json", "private-stack")]
    public async Task UnexpectedErrorBodiesAreNotPublished(int status, string type, string body)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(status, body, type)));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("context", new { }));
        Assert.Equal("http_" + status, ex.Code);
        Assert.DoesNotContain("private-stack", ex.Message);
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task OversizedHttpErrorKeepsStatusWithoutPublishingBody()
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(409,
            JsonSerializer.Serialize(new { error = "private-stack" + new string('x', 20000) }), "application/json")));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("context", new { }));
        Assert.Equal("http_409", ex.Code);
        Assert.DoesNotContain("private-stack", ex.Message);
    }

    [Theory]
    [InlineData("<html>private-stack</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"callId\":\"bad-id\",\"outcome\":\"returned\"}")]
    [InlineData("{\"callId\":\"22222222222222222222222222222222\",\"outcome\":\"unknown\"}")]
    [InlineData("{\"callId\":123,\"outcome\":\"returned\"}")]
    public async Task MalformedSuccessCannotBeReportedAsToolSuccess(string body)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Http(200, body, "application/json")));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("context", new { }));
        Assert.Equal("invalid_response", ex.Code);
        Assert.DoesNotContain("private-stack", ex.Message);
        Assert.Contains(f.LastRequestId!, ex.Message);
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task TransportFailureDoesNotLeakExceptionOrReplay()
    {
        using var f = new Fixture((_, _) => throw new HttpRequestException("private-stack " + Token));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("build", new { approved = true }));
        Assert.Equal("transport_error", ex.Code);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("private-stack", ex.Message);
        Assert.Contains(f.LastRequestId!, ex.Message);
        Assert.Equal(1, f.Calls);
    }

    [Fact]
    public async Task BodyTimeoutRetainsRequestIdAndDoesNotReplay()
    {
        using var f = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new WaitingStream()) }), TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("build", new { approved = true }).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("request_timeout", ex.Code);
        Assert.Contains(f.LastRequestId!, ex.Message);
        Assert.Equal(1, f.Calls);
        Assert.Equal(1, f.Begins);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToTimeoutAndGateIsReleased()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new Fixture(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply();
        });
        using var cancel = new CancellationTokenSource();
        var running = f.Client.CallAsync("scan", new { }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, f.Calls);
        Assert.Contains("finished", await f.Client.FinishAsync());
    }

    [Fact]
    public async Task ConcurrentCallIsRejectedWithoutQueueing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new Fixture(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Reply();
        });
        var running = f.Client.CallAsync("scan", new { });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.CallAsync("scan", new { }));
            Assert.Equal("busy", ex.Code);
            Assert.Equal(1, f.Calls);
        }
        finally { release.TrySetResult(); }
        await running;
    }

    [Fact]
    public async Task ExplicitReconnectCanRecoverWhenOldEndIsRejected()
    {
        using var f = new Fixture((_, _) => Task.FromResult(Reply())) { RejectEnd = true };
        await f.Client.CallAsync("context", new { });
        await f.Client.ConnectAsync("continue");
        Assert.Equal(2, f.Begins);
        Assert.Equal(1, f.Ends);
        Assert.Equal(1, f.Calls); // Reconnection never repeats the prior context/tool.
    }

    [Fact]
    public async Task SuccessEnvelopeAndFinishRemainCompatible()
    {
        using var f = new Fixture((_, _) => Task.FromResult(Reply()));
        using var doc = JsonDocument.Parse(await f.Client.CallAsync("context", new { }));
        Assert.Equal("returned", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("ready", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("{\"status\":\"finished\"}", await f.Client.FinishAsync());
        Assert.Equal("{\"status\":\"not_connected\"}", await f.Client.FinishAsync());
        Assert.Equal(1, f.Ends);
        Assert.Equal("Bearer " + Token, f.Authorization);
    }

    [Theory]
    [InlineData(null, "connection_file_missing")]
    [InlineData("not JSON", "connection_file_invalid")]
    [InlineData("{}", "connection_file_invalid")]
    [InlineData("null", "connection_file_invalid")]
    public async Task BadDescriptorFailsBeforeAnyNetworkRequest(string? descriptor, string code)
    {
        using var f = new Fixture((_, _) => Task.FromResult(Reply()));
        if (descriptor is null) File.Delete(f.Path);
        else File.WriteAllText(f.Path, descriptor);
        var ex = await Assert.ThrowsAsync<AgentClientFailure>(() => f.Client.ConnectAsync());
        Assert.Equal(code, ex.Code);
        Assert.Equal(0, f.Begins);
        Assert.Equal(0, f.Calls);
    }

    [Fact]
    public void RedactionHappensBeforeTruncationAndRemovesControlCharacters()
    {
        var value = new string('x', 1590) + Token + "\r\n";
        var clean = AgentClientFailure.SafeDetail(value, Token)!;
        Assert.Equal(1600, clean.Length);
        Assert.DoesNotContain(Token[..10], clean);
        Assert.DoesNotContain("\r", AgentClientFailure.SafeDetail("a\r\nb", Token));
        Assert.DoesNotContain(Token, AgentClientFailure.SafeDetail(Token.ToUpperInvariant(), Token), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Reply(string outcome = "returned", string? error = null) =>
        Http(200, JsonSerializer.Serialize(new AgentReply(Call, outcome, "ready", null, error,
            JsonSerializer.SerializeToElement(new { status = "ready" }), true, ["status"]), Json), "application/json");
    private static HttpResponseMessage Http(int status, string body, string type) => new((HttpStatusCode)status)
    { Content = new StringContent(body, Encoding.UTF8, type) };

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tenka-client-" + Guid.NewGuid().ToString("N") + ".json");
        public DashboardAgentClient Client { get; }
        public int Calls, Begins, Ends;
        public string? LastRequestId, Authorization;
        public bool RejectEnd;
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> call, TimeSpan? timeout = null)
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(new
            { schema = 1, instanceId = Guid.NewGuid().ToString("N"), endpoint = "http://127.0.0.1:5077/", token = Token }));
            Client = new DashboardAgentClient(new DashboardAgentOptions(Path, "test-agent"), () => new Handler(async (request, ct) =>
            {
                Authorization = request.Headers.Authorization?.ToString();
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/agent/v1/begin":
                        Begins++;
                        return Http(200, "{\"id\":\"" + Session + "\"}", "application/json");
                    case "/agent/v1/end":
                        Ends++;
                        return RejectEnd ? Http(403, "", "application/json") : Http(200, "{\"status\":\"finished\"}", "application/json");
                    case "/agent/v1/call":
                        Calls++;
                        using (var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                            LastRequestId = doc.RootElement.GetProperty("requestId").GetString();
                        return await call(request, ct);
                    default: throw new InvalidOperationException("Unexpected route in test.");
                }
            }), timeout ?? TimeSpan.FromSeconds(10));
        }
        public void Dispose() { Client.Dispose(); File.Delete(Path); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
