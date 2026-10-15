using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace TenkaChan.Mcp.Agent;

/// <summary>
/// Only deliberately client-safe failures use McpException. The SDK otherwise
/// hides exception messages. Never wrap arbitrary exception text here.
/// The JSON is the exception's text, not an MCP structuredContent contract.
/// </summary>
internal sealed class AgentClientFailure : McpException
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Bearer = new(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HexCredential = new(@"[a-fA-F0-9]{64}", RegexOptions.CultureInvariant);

    public string Code { get; }
    public AgentClientFailure(string code, string message, string nextAction,
        string? token = null, string? sessionId = null, string? requestId = null,
        string? callId = null, int? httpStatus = null)
        : base(JsonSerializer.Serialize(new
        {
            code, message = SafeDetail(message, token),
            sessionId = SafeDetail(sessionId, token, 100),
            requestId = SafeDetail(requestId, token, 100),
            callId = SafeDetail(callId, token, 100), httpStatus,
            automaticRetry = false, nextAction
        }, Json)) => Code = code;

    internal static string? SafeDetail(string? value, string? token, int limit = 1600)
    {
        if (value is null) return null;
        // Redact BEFORE truncation, including a credential which straddles the limit.
        if (!string.IsNullOrEmpty(token)) value = value.Replace(token, "[redacted]", StringComparison.OrdinalIgnoreCase);
        value = Bearer.Replace(value, "Bearer [redacted]");
        value = HexCredential.Replace(value, "[redacted]");
        return new string(value.Select(c => char.IsControl(c) ? ' ' : c).Take(limit).ToArray());
    }

    internal static string? ReadHttpDetail(byte[]? body, string? mediaType, int status)
    {
        // Only the gateway's explicit error DTO is eligible. Do not publish HTML,
        // problem details, a developer exception page, or unhandled server errors.
        if (status is not (400 or 403 or 408 or 409 or 413) || body is null ||
            !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static string HttpNextAction(int status) => status switch
    {
        401 or 403 => "Check the running server, connection descriptor and host permissions. After a server restart, call tenka_connect explicitly. Execution still requires separate approval.",
        409 => "Check the active session and activity monitor. Call tenka_context for current selection IDs; use tenka_connect explicitly if the session expired or the server restarted.",
        400 or 413 => "Check the tool arguments against tools/list and reduce the request size if necessary. Do not resend an execution request without checking its activity first.",
        _ => "Check the server activity monitor before any further action. The outcome may be unknown; no tool was automatically retried."
    };
}
