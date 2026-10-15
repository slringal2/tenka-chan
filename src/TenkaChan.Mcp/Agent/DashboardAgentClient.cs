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
    public async Task<string> ConnectAsync(string goal = "", CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("An agent tool is running. No reconnect was queued.");
        try
        {
            if (_session is not null && _http is not null)
            {
                // Explicit reconnect only. Failure to close is not permission to replay a tool.
                try { await SendAsync("/agent/v1/end", new { sessionId = _session }, ct); }
                catch (HttpRequestException) { }
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
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("An agent tool is already running. No duplicate was queued.");
        try
        {
            if (_session is null) await BeginAsync("", ct);
            var request = new AgentCallRequest(_session!, Guid.NewGuid().ToString("N"), tool,
                JsonSerializer.SerializeToElement(arguments, Json));
            var data = await SendAsync("/agent/v1/call", request, ct);
            var reply = data.Deserialize<AgentReply>(Json) ?? throw new InvalidDataException("Missing agent reply.");
            if (reply.Outcome is "failed" or "cancelled")
                throw new InvalidOperationException($"{reply.Outcome}; callId={reply.CallId}: {reply.Error}. No automatic retry. Read context before changing selection.");
            return data.GetRawText();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Tenka communication failed. A tool may already have started; it was NOT retried. Check the activity monitor. If the server restarted, call tenka_connect explicitly. " + ex.Message, ex);
        }
        finally { _gate.Release(); }
    }
    public async Task<string> FinishAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Wait for or cancel the active tool before finishing.");
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
        if (!File.Exists(options.ConnectionFile)) throw new FileNotFoundException("Start Tenka with start.bat first. Its agent connection file was not found. Use --connection-file or --port for another instance.", options.ConnectionFile);
        if (new FileInfo(options.ConnectionFile).Length > 16000) throw new InvalidDataException("Invalid connection descriptor size.");
        var saved = JsonSerializer.Deserialize<AgentConnectionFile>(await File.ReadAllTextAsync(options.ConnectionFile, ct), Json)
            ?? throw new InvalidDataException("Invalid connection descriptor.");
        var endpoint = ValidateConnection(saved);
        _connection = saved;
        _http?.Dispose();
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5) }) { BaseAddress = endpoint, Timeout = TimeSpan.FromMinutes(7) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saved.Token);
        var result = await SendAsync("/agent/v1/begin", new AgentSessionRequest(options.ClientName, goal), ct);
        _session = result.GetProperty("id").GetString() ?? throw new InvalidDataException("Missing agent session ID.");
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
    private async Task<JsonElement> SendAsync(string route, object body, CancellationToken ct)
    {
        if (_http is null) throw new InvalidOperationException("Agent transport is not initialized.");
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Tenka returned HTTP {(int)response.StatusCode}. Check authorization, session state, or the server activity monitor.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var bytes = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(bytes.AsMemory(), ct)) != 0)
        {
            if (buffer.Length + read > 12_000_000) throw new InvalidDataException("Agent response limit reached. The tool is not retried.");
            buffer.Write(bytes, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray()); return document.RootElement.Clone();
    }
    public void Dispose() { _http?.Dispose(); _gate.Dispose(); }
}
