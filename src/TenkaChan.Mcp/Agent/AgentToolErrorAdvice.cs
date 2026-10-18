using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TenkaChan.Mcp.Agent;

/// <summary>
/// Turns the ModelContextProtocol SDK's generic tool-invocation failure into an actionable message.
/// When argument binding fails (for example a required argument is missing or null) the SDK throws a
/// non-<c>McpException</c>, so the client only receives "An error occurred invoking 'name'." with no detail.
/// This inspects the tool's input schema so the caller can self-correct instead of guessing which field is wrong.
/// </summary>
public sealed record AgentArgumentTypeMismatch(string Field, string ExpectedType);

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

    /// <summary>
    /// Validate only primitive input types explicitly advertised by tools/list.
    /// This runs BEFORE invoking MCP binding and therefore does not mistake
    /// server-side ArgumentExceptions for tool-input failures. Input values and
    /// deserializer exception texts are never reflected into the error response.
    /// </summary>
    public static IReadOnlyList<AgentArgumentTypeMismatch> InvalidTypes(
        JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        var invalid = new List<AgentArgumentTypeMismatch>();
        if (arguments is null || inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return invalid;

        foreach (var pair in arguments.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!properties.TryGetProperty(pair.Key, out var property) ||
                property.ValueKind != JsonValueKind.Object)
                continue; // Do not guess types not represented by the SDK's schema.
            var expected = ExpectedKinds(property);
            if (expected.Length > 0 && !Accepts(property, expected, pair.Value))
                invalid.Add(new(pair.Key, Describe(expected, property)));

            if (expected.Contains("array", StringComparer.Ordinal) &&
                pair.Value.ValueKind == JsonValueKind.Array &&
                property.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Object)
            {
                var elementKinds = ExpectedKinds(items);
                if (elementKinds.Length > 0)
                {
                    var index = 0;
                    foreach (var element in pair.Value.EnumerateArray())
                    {
                        if (!Accepts(items, elementKinds, element))
                            invalid.Add(new($"{pair.Key}[{index}]", Describe(elementKinds, items)));
                        index++;
                        if (invalid.Count >= 10) break;
                    }
                }
            }
            if (invalid.Count >= 10) break;
        }
        return invalid.Take(10).ToArray();
    }

    private static string[] ExpectedKinds(JsonElement schema)
    {
        var types = new List<string>();
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
                types.Add(type.GetString()!);
            else if (type.ValueKind == JsonValueKind.Array)
                types.AddRange(type.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!));
        }
        // Nullable generated SDK signatures may be expressed as anyOf / oneOf
        // instead of a ["string","null"] type array.
        foreach (var union in new[] { "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(union, out var options) || options.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in options.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    types.AddRange(ExpectedKinds(item));
        }
        return types.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool Accepts(JsonElement schema, IReadOnlyList<string> kinds, JsonElement value)
    {
        foreach (var kind in kinds)
        {
            var accepted = kind switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number &&
                    (schema.TryGetProperty("format", out var format) &&
                     format.ValueKind == JsonValueKind.String && format.GetString() == "int32"
                        ? value.TryGetInt32(out _) : value.TryGetInt64(out _)),
                "number" => value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out var number) && double.IsFinite(number),
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                "null" => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                _ => true // Unknown JSON-schema extension: leave binding to the SDK.
            };
            if (accepted) return true;
        }
        return false;
    }

    private static string Describe(string[] kinds, JsonElement schema)
    {
        var type = string.Join(" or ", kinds);
        if (kinds.Contains("integer", StringComparer.Ordinal) &&
            schema.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String && format.GetString() is "int32" or "int64")
            type += " (" + format.GetString() + ")";
        return type;
    }

    public static CallToolResult TypeFailure(string toolName, IReadOnlyList<AgentArgumentTypeMismatch> invalid) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock
            {
                Text = $"An error occurred invoking '{toolName}'. Argument type mismatch: " +
                    string.Join("; ", invalid.Select(x => x.Field + " expects " + x.ExpectedType)) +
                    ". Correct the named field(s) using tools/list inputSchema and retry."
            }]
        };

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
