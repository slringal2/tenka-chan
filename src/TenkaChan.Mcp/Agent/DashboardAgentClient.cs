using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

public sealed record DashboardAgentOptions(string ConnectionFile, string ClientName)
{
    public static DashboardAgentOptions Parse(string[] args)
    {
        string? path = Environment.GetEnvironmentVariable("TENKA_AGENT_CONNECTION_FILE");
        var name = "external-agent"; var port = 5077; var portGiven = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dashboard") continue; // Compatibility with explicit bridge configurations.
            if (i + 1 >= args.Length) throw new ArgumentException("Expected a value after " + args[i]);
            switch (args[i])
            {
                case "--connection-file": path = args[++i]; break;
                case "--client-name": name = AgentJournal.Text(args[++i], 100); break;
                case "--port":
                    if (!int.TryParse(args[++i], out port) || port is < 1024 or > 65535) throw new ArgumentException("Invalid local port.");
                    portGiven = true; break;
                default: throw new ArgumentException("Unknown argument: " + args[i]);
            }
        }
        if (path is null && !portGiven)
        {
            // Match the per-checkout setup.bat state directory. This file contains the
            // machine credential; no token is stored in MCP client configuration.
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
            {
                if (!File.Exists(Path.Combine(current.FullName, "TenkaChan.slnx"))) continue;
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(current.FullName.ToLowerInvariant())))[..16];
                var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TenkaChan", "launcher", key, "agent-connection.json");
                if (File.Exists(candidate)) path = candidate;
                break;
            }
        }
        return new(Path.GetFullPath(path ?? AgentConnection.DefaultPath(port)), name);
    }
}

/// <summary>
/// Standard MCP stdio tool adapter to the already-running Web process. No Roslyn,
/// MSBuild, inference client or target execution lives in this subprocess.
/// </summary>
public sealed class DashboardAgentClient(DashboardAgentOptions options) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient? _http;
    private AgentConnectionFile? _connection;
    private string? _session;
    private readonly Func<HttpMessageHandler> _handlerFactory = () => new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
    private readonly TimeSpan _requestTimeout = TimeSpan.FromMinutes(7);

    // Test-only seam. Production always uses the loopback/no-proxy/no-redirect handler.
    internal DashboardAgentClient(DashboardAgentOptions options, Func<HttpMessageHandler> handlerFactory, TimeSpan requestTimeout)
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _handlerFactory = handlerFactory;
        _requestTimeout = requestTimeout;
    }
    private AgentClientFailure Failure(string code, string message, string nextAction,
        string? requestId = null, string? callId = null, int? httpStatus = null) =>
        new(code, message, nextAction, _connection?.Token, _session, requestId, callId, httpStatus);
    public async Task<string> ConnectAsync(string goal = "", CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) throw Failure("busy", "An agent tool is running. No reconnect was queued.", "Wait for or cancel the active tool; check its activity before reconnecting.");
        try
        {
            if (_session is not null && _http is not null)
            {
                // Explicit reconnect only. Failure to close is not permission to replay a tool.
                try { await SendAsync("/agent/v1/end", new { sessionId = _session }, ct); }
                catch (AgentClientFailure) { } // Only this explicit reconnect may abandon the old session.
            }
            _session = null; _http?.Dispose(); _http = null;
            await BeginAsync(goal, ct);
            return JsonSerializer.Serialize(new { sessionId = _session, instanceId = _connection!.InstanceId,
                note = "Connected to the running Tenka workspace. Browser is a monitor only. Call tenka_context before choosing targets. Execution requires host permission and explicit client approval." }, Json);
        }
        finally { _gate.Release(); }
    }
    public async Task<string> CallAsync(string tool, object arguments, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) throw Failure("busy", "An agent tool is already running. No duplicate was queued.", "Wait for or cancel the active tool; do not queue duplicate execution.");
        try
        {
            if (_session is null) await BeginAsync("", ct);
            var request = new AgentCallRequest(_session!, Guid.NewGuid().ToString("N"), tool,
                JsonSerializer.SerializeToElement(arguments, Json));
            var data = await SendAsync("/agent/v1/call", request, ct, request.RequestId);
            AgentReply? reply;
            try { reply = data.Deserialize<AgentReply>(Json); }
            catch (JsonException)
            { throw Failure("invalid_response", "The server returned an invalid agent reply.", AgentClientFailure.HttpNextAction(0), request.RequestId); }
            if (reply is null || !Guid.TryParseExact(reply.CallId, "N", out _) ||
                reply.Outcome is not ("returned" or "failed" or "cancelled"))
                throw Failure("invalid_response", "The server returned an incomplete agent reply.", AgentClientFailure.HttpNextAction(0), request.RequestId);
            if (reply.Outcome is "failed" or "cancelled")
                throw Failure("tool_" + reply.Outcome, reply.Error ?? "The server did not supply an error detail.",
                    AgentRecoveryAdvice.NextAction(tool, reply),
                    request.RequestId, reply.CallId);
            return data.GetRawText();
        }
        finally { _gate.Release(); }
    }
    public async Task<string> FinishAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) throw Failure("busy", "An agent tool is running. The session was not finished.", "Wait for or cancel the active tool before finishing.");
        try
        {
            if (_session is null) return "{\"status\":\"not_connected\"}";
            var result = await SendAsync("/agent/v1/end", new { sessionId = _session }, ct);
            _session = null; return result.GetRawText();
        }
        finally { _gate.Release(); }
    }
    private async Task BeginAsync(string goal, CancellationToken ct)
    {
        AgentConnectionFile saved;
        Uri endpoint;
        try
        {
            if (!File.Exists(options.ConnectionFile))
                throw Failure("connection_file_missing", "The agent connection file was not found.",
                    "Start Tenka with start.bat. Check --connection-file or --port for another instance; do not paste credentials into the client.");
            if (new FileInfo(options.ConnectionFile).Length > 16000) throw new InvalidDataException();
            saved = JsonSerializer.Deserialize<AgentConnectionFile>(await File.ReadAllTextAsync(options.ConnectionFile, ct), Json)
                ?? throw new InvalidDataException();
            endpoint = ValidateConnection(saved);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            throw Failure("connection_file_invalid", "The connection descriptor could not be read or validated.",
                "Check the selected instance and regenerate its descriptor by starting Tenka. Do not share or manually paste the token.");
        }
        _connection = saved;
        _http?.Dispose();
        _http = new HttpClient(_handlerFactory()) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saved.Token);
        var result = await SendAsync("/agent/v1/begin", new AgentSessionRequest(options.ClientName, goal), ct);
        if (!result.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "N", out _))
            throw Failure("invalid_response", "The server returned no valid agent session ID.", AgentClientFailure.HttpNextAction(0));
        _session = id.GetString();
    }
    public static Uri ValidateConnection(AgentConnectionFile saved)
    {
        if (saved is null || saved.Token is null || saved.Schema != 1 || saved.Token.Length != 64 || !saved.Token.All(Uri.IsHexDigit) ||
            !Guid.TryParseExact(saved.InstanceId, "N", out _) || !Uri.TryCreate(saved.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1") ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || uri.Port is < 1024 or > 65535)
            throw new InvalidDataException("The agent connection must target a loopback HTTP origin with a valid local credential.");
        // Avoid DNS even for localhost. Never follow redirects or send credentials via a proxy.
        return new UriBuilder(uri) { Host = uri.Host.Contains(':') ? "::1" : "127.0.0.1" }.Uri;
    }
    private async Task<JsonElement> SendAsync(string route, object body, CancellationToken ct, string? requestId = null)
    {
        if (_http is null) throw Failure("not_connected", "Agent transport is not initialized.", "Call tenka_connect explicitly.");
        // ResponseHeadersRead makes HttpClient.Timeout insufficient for body reads.
        // Apply one deadline to headers AND body, without replaying any request.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var bodyBytes = await ReadBoundedAsync(response.Content, response.IsSuccessStatusCode ? 12_000_000 : 16_000, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var detail = AgentClientFailure.ReadHttpDetail(bodyBytes, response.Content.Headers.ContentType?.MediaType, status);
                throw Failure("http_" + status, $"Tenka returned HTTP {status}. " + (detail ?? "No client-safe error detail was supplied."),
                    AgentRecoveryAdvice.HttpNextAction(status, detail), requestId, httpStatus: status);
            }
            if (bodyBytes is null)
                throw Failure("response_limit", "Agent response limit reached. The tool was not retried.", AgentClientFailure.HttpNextAction(0), requestId);
            using var document = JsonDocument.Parse(bodyBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Failure("invalid_response", "The server response was not a JSON object.", AgentClientFailure.HttpNextAction(0), requestId);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Failure("request_timeout", "The Tenka request timed out. A tool may already have started; its final outcome is unknown.",
                AgentClientFailure.HttpNextAction(0), requestId);
        }
        catch (HttpRequestException)
        {
            throw Failure("transport_error", "Tenka communication failed. A tool may already have started; it was NOT retried.",
                AgentClientFailure.HttpNextAction(0), requestId);
        }
        catch (IOException)
        {
            throw Failure("transport_error", "The Tenka response could not be read completely. The outcome is unknown; it was NOT retried.",
                AgentClientFailure.HttpNextAction(0), requestId);
        }
        catch (JsonException)
        {
            throw Failure("invalid_response", "The Tenka response was not valid JSON. Its body was not exposed and the tool was not retried.",
                AgentClientFailure.HttpNextAction(0), requestId);
        }
    }
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) return null;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        while (true)
        {
            // Read at most one byte beyond the limit, even without Content-Length.
            var count = (int)Math.Min(bytes.Length, limit + 1L - buffer.Length);
            var read = await stream.ReadAsync(bytes.AsMemory(0, count), ct);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) return null;
            buffer.Write(bytes, 0, read);
        }
    }
    public void Dispose() { _http?.Dispose(); _gate.Dispose(); }
}