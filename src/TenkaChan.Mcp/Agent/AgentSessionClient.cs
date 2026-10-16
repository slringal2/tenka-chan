using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

/// <summary>
/// Session recovery must work without allocating a session. Uses only two fixed,
/// machine-authenticated routes; it never calls begin, call, or a target tool.
/// </summary>
public sealed class AgentSessionClient(DashboardAgentOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<HttpMessageHandler> _handlerFactory = () => new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    internal AgentSessionClient(DashboardAgentOptions options, Func<HttpMessageHandler> handlerFactory, TimeSpan timeout) : this(options)
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _handlerFactory = handlerFactory;
        _timeout = timeout;
    }
    public Task<string> ListAsync(CancellationToken ct = default) => SendAsync(false, null, ct);
    public Task<string> EndAsync(string? sessionId, bool approved = false, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
            throw new AgentClientFailure("invalid_arguments", "Missing or invalid argument: sessionId.",
                "Use tenka_sessions_list and pass the exact sessionId to end; it must be a GUID in N format.");
        if (!approved)
            throw new AgentClientFailure("approval_required", "Ending a selected session requires explicit client approval and approved:true.",
                "Inspect tenka_sessions_list, obtain approval for this session, then pass approved:true. No session was ended.");
        return SendAsync(true, sessionId, ct);
    }
    private async Task<string> SendAsync(bool end, string? sessionId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        AgentConnectionFile? saved = null;
        const string next = "Use tenka_sessions_list without tenka_connect. Check the selected server and its activity before any retry; running actions are never cancelled by session management.";
        try
        {
            Uri endpoint;
            try
            {
                if (!File.Exists(options.ConnectionFile))
                    throw new AgentClientFailure("connection_file_missing", "The agent connection descriptor was not found.", "Start Tenka or select the correct --connection-file. Never paste its credentials into tool arguments.");
                await using var file = File.OpenRead(options.ConnectionFile);
                var descriptor = await ReadBoundedAsync(file, 16000, deadline.Token)
                    ?? throw new InvalidDataException();
                saved = JsonSerializer.Deserialize<AgentConnectionFile>(descriptor, Json) ?? throw new InvalidDataException();
                endpoint = DashboardAgentClient.ValidateConnection(saved);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                throw new AgentClientFailure("connection_file_invalid", "The connection descriptor could not be read or validated.", "Check the selected local instance and start Tenka again. Do not share its credentials.");
            }
            using var http = new HttpClient(_handlerFactory()) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saved.Token);
            var route = end ? "/agent/v1/sessions/end" : "/agent/v1/sessions/list";
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            { Content = new StringContent(end ? JsonSerializer.Serialize(new { sessionId, approved = true }, Json) : "{}", Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var body = await ReadBoundedAsync(stream, response.IsSuccessStatusCode ? 1_000_000 : 16_000, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var detail = AgentClientFailure.ReadHttpDetail(body, response.Content.Headers.ContentType?.MediaType, status);
                throw new AgentClientFailure("http_" + status, $"Tenka session management returned HTTP {status}. " + (detail ?? "No client-safe detail was supplied."),
                    next, saved.Token, httpStatus: status);
            }
            if (body is null) throw new AgentClientFailure("response_limit", "Session response exceeded the bounded size.", next, saved.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                (!end && (!root.TryGetProperty("instanceId", out var instance) || instance.ValueKind != JsonValueKind.String || instance.GetString() != saved.InstanceId ||
                    !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 128)) ||
                (end && (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != sessionId ||
                    !root.TryGetProperty("status", out var endStatus) || endStatus.ValueKind != JsonValueKind.String || endStatus.GetString() is not ("finished" or "expired"))))
                throw new AgentClientFailure("invalid_response", "The server returned an invalid session-management response.", next, saved.Token);
            return root.GetRawText();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AgentClientFailure("request_timeout", "Session management timed out; its outcome may be unknown. It was not retried.", next, saved?.Token); }
        catch (HttpRequestException)
        { throw new AgentClientFailure("transport_error", "Session management communication failed. It was not retried.", next, saved?.Token); }
        catch (IOException)
        { throw new AgentClientFailure("transport_error", "Session management response could not be read. It was not retried.", next, saved?.Token); }
        catch (JsonException)
        { throw new AgentClientFailure("invalid_response", "Session management returned invalid JSON. Its raw body was not exposed.", next, saved?.Token); }
    }
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, limit + 1L - buffer.Length)), ct);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) return null;
            buffer.Write(bytes, 0, read);
        }
    }
}
