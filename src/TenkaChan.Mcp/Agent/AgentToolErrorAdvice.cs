using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TenkaChan.Mcp.Agent;

/// <summary>
/// Turns the ModelContextProtocol SDK's generic tool-invocation failure into an actionable message.
/// When argument binding fails (for example a required argument is missing or null) the SDK throws a
/// non-<c>McpException</c>, so the client only receives "An error occurred invoking 'name'." with no detail.
/// This inspects the tool's input schema so the caller can self-correct instead of guessing which field is wrong.
/// </summary>
public static class AgentToolErrorAdvice
{
    public static string Generic(string toolName) => $"An error occurred invoking '{toolName}'.";

    public static bool IsGenericInvocationFailure(string toolName, string? content) =>
        content == Generic(toolName);

    public static IReadOnlyList<string> MissingRequired(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        var missing = new List<string>();
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return missing;
        }

        foreach (var entry in required.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            var name = entry.GetString()!;
            if (arguments is null || !arguments.TryGetValue(name, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                missing.Add(name);
            }
        }

        return missing;
    }

    public static string Message(string toolName, IReadOnlyList<string> missing) =>
        $"An error occurred invoking '{toolName}'. Required argument(s) missing or null: {string.Join(", ", missing)}. " +
        "Provide the named argument(s) and retry. tools/list reports each tool's inputSchema, including every required field.";

    public static CallToolResult Failure(string toolName, IReadOnlyList<string> missing) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = Message(toolName, missing) }],
        };
}
