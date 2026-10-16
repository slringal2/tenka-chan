using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TenkaChan.Mcp.Agent;

/// <summary>
/// Checks the actual registered C# parameter shapes BEFORE SDK binding and before
/// allocating a Tenka investigation session. This is not a general JSON Schema engine.
/// Domain validation, authorization and snapshot checks still belong to the gateway.
/// </summary>
internal static class AgentToolCallFilter
{
    private sealed record Parameter(string Name, Type Type, bool Required, bool Nullable);
    private static readonly Lazy<IReadOnlyDictionary<string, Parameter[]>> Contracts = new(BuildContracts);

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, ct) => InvokeAsync(context.Params.Name, context.Params.Arguments, () => next(context, ct), ct);

    internal static async ValueTask<CallToolResult> InvokeAsync(string name,
        IDictionary<string, JsonElement>? arguments, Func<ValueTask<CallToolResult>> next, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await AgentToolResponse.GuardAsync(async () =>
        {
            var invalid = Validate(name, arguments);
            return invalid ?? await next();
        });
    }

    private static IReadOnlyDictionary<string, Parameter[]> BuildContracts()
    {
        var nullability = new NullabilityInfoContext();
        var result = new Dictionary<string, Parameter[]>(StringComparer.Ordinal);
        // These are the same two classes registered in Program.cs. No assembly/plugin scan.
        foreach (var type in new[] { typeof(DashboardAgentTools), typeof(ReviewProgressTools) })
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute?.Name is not string name) continue;
            var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(DashboardAgentClient)
                && p.ParameterType != typeof(AgentSessionClient) && p.ParameterType != typeof(CancellationToken))
                .Select(p => new Parameter(p.Name!, Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType,
                    !p.HasDefaultValue, Nullable.GetUnderlyingType(p.ParameterType) is not null
                        || nullability.Create(p).ReadState == NullabilityState.Nullable)).ToArray();
            if (parameters.Any(p => p.Type != typeof(string) && p.Type != typeof(bool) && p.Type != typeof(int)
                && p.Type != typeof(long) && p.Type != typeof(string[])))
                throw new InvalidOperationException("Update the tool parameter validator for the newly registered parameter type.");
            result.Add(name, parameters);
        }
        return result;
    }

    internal static CallToolResult? Validate(string name, IDictionary<string, JsonElement>? arguments)
    {
        // Unknown tools keep the SDK's protocol-level handling; never reinterpret a
        // malformed JSON-RPC envelope as a successful tool result.
        if (!Contracts.Value.TryGetValue(name, out var parameters)) return null;
        var args = arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var permitted = parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = parameters.Where(p => p.Required && !args.ContainsKey(p.Name)).Select(p => p.Name).ToArray();
        var invalid = parameters.Where(p => args.TryGetValue(p.Name, out var value) && !Accepts(p, value))
            .Select(p => new { name = p.Name, expected = Expected(p) }).ToArray();
        var unknownCount = args.Keys.Count(key => !permitted.Contains(key));
        if (missing.Length == 0 && invalid.Length == 0 && unknownCount == 0) return null;
        return AgentToolResponse.Error(JsonSerializer.SerializeToElement(new
        {
            code = "invalid_arguments",
            message = "Correct the named missing/invalid arguments before calling this tool. Argument values were not echoed.",
            sessionId = (string?)null, requestId = (string?)null, callId = (string?)null, httpStatus = (int?)null,
            automaticRetry = false, phase = "argument_validation", operationStarted = false,
            missingArguments = missing, invalidArguments = invalid, unknownArgumentCount = unknownCount,
            allowedArguments = parameters.Select(p => p.Name).ToArray(),
            nextAction = "Use tools/list and the expected argument types below. Do not stringify numbers or booleans. No Tenka session or target operation was started by this call."
        }));
    }

    private static bool Accepts(Parameter parameter, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return parameter.Nullable;
        if (parameter.Type == typeof(string)) return value.ValueKind == JsonValueKind.String;
        if (parameter.Type == typeof(bool)) return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (parameter.Type == typeof(int)) return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
        if (parameter.Type == typeof(long)) return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
        return parameter.Type == typeof(string[]) && value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
    }

    private static string Expected(Parameter parameter)
    {
        var value = parameter.Type == typeof(string) ? "string" : parameter.Type == typeof(bool) ? "boolean"
            : parameter.Type == typeof(int) ? "32-bit integer" : parameter.Type == typeof(long) ? "64-bit integer"
            : "array of non-null strings";
        return value + (parameter.Nullable ? " or null" : " (not null)");
    }
}
