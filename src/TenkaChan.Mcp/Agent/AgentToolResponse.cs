using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace TenkaChan.Mcp.Agent;

/// <summary>One error shape for tool validation and execution; success text stays byte-for-byte compatible.</summary>
internal static class AgentToolResponse
{
    internal static CallToolResult Error(JsonElement detail) => new()
    {
        IsError = true,
        StructuredContent = detail.Clone(),
        Content = [new TextContentBlock { Text = detail.GetRawText() }]
    };

    internal static CallToolResult Unexpected() => Error(JsonSerializer.SerializeToElement(new
    {
        code = "internal_error",
        message = "The adapter could not complete the tool call. Internal exception details were not exposed.",
        sessionId = (string?)null, requestId = (string?)null, callId = (string?)null, httpStatus = (int?)null,
        automaticRetry = false,
        nextAction = "Check the Tenka activity monitor before another call: an operation may already have started. No automatic retry."
    }));

    private static CallToolResult KnownFailure(AgentClientFailure failure)
    {
        // Only our deliberately redacted exception is eligible. Never parse arbitrary
        // SDK error text, HTTP bodies or exception messages looking for embedded JSON.
        using var document = JsonDocument.Parse(failure.Message);
        return Error(document.RootElement);
    }

    public static async Task<CallToolResult> RunAsync(Func<Task<string>> operation)
    {
        try { return new() { Content = [new TextContentBlock { Text = await operation() }] }; }
        catch (AgentClientFailure failure) { return KnownFailure(failure); }
        catch (OperationCanceledException) { throw; }
        catch (McpProtocolException) { throw; }
        catch (Exception) { return Unexpected(); }
    }

    internal static async ValueTask<CallToolResult> GuardAsync(Func<ValueTask<CallToolResult>> operation)
    {
        try
        {
            var result = await operation();
            // A remaining SDK binding/internal error has no safe typed detail. Keep
            // the transport shape uniform without publishing its raw text.
            return result.IsError is true && result.StructuredContent is null ? Unexpected() : result;
        }
        catch (AgentClientFailure failure) { return KnownFailure(failure); }
        catch (OperationCanceledException) { throw; }
        catch (McpProtocolException) { throw; }
        catch (Exception) { return Unexpected(); }
    }
}
