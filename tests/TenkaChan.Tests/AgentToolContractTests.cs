using System.Text.Json;
using ModelContextProtocol.Protocol;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentToolContractTests
{
    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static JsonElement Detail(CallToolResult result)
    {
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var document = JsonDocument.Parse(text); // A prefixed SDK sentence would fail here.
        Assert.Equal(document.RootElement.GetRawText(), result.StructuredContent!.Value.GetRawText());
        Assert.False(document.RootElement.GetProperty("automaticRetry").GetBoolean());
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("tenka_inspect_method", "methodId")]
    [InlineData("tenka_inspect_type", "typeId")]
    [InlineData("tenka_open_project", "path")]
    [InlineData("tenka_review_target", "targetId")]
    [InlineData("tenka_mark_review", "expectedRevision")]
    [InlineData("tenka_prepare_experiment", "generatedCode")]
    public async Task MissingArgumentsNeverReachTheTool(string tool, string expected)
    {
        var invoked = false;
        var result = await AgentToolCallFilter.InvokeAsync(tool, null, () =>
        { invoked = true; return ValueTask.FromResult(new CallToolResult()); }, default);
        var detail = Detail(result);
        Assert.Equal("invalid_arguments", detail.GetProperty("code").GetString());
        Assert.Contains(expected, detail.GetProperty("missingArguments").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("client", detail.GetProperty("missingArguments").EnumerateArray().Select(x => x.GetString()));
        Assert.False(detail.GetProperty("operationStarted").GetBoolean());
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("tenka_review_queue", "{\"limit\":\"50\"}", "limit")]
    [InlineData("tenka_review_queue", "{\"limit\":2147483648}", "limit")]
    [InlineData("tenka_review_queue", "{\"limit\":1.5}", "limit")]
    [InlineData("tenka_review_queue", "{\"limit\":null}", "limit")]
    [InlineData("tenka_review_queue", "{\"snapshotId\":[]}", "snapshotId")]
    [InlineData("tenka_review_queue", "{\"trackableOnly\":\"true\"}", "trackableOnly")]
    [InlineData("tenka_mark_review", "{\"expectedRevision\":9223372036854775808}", "expectedRevision")]
    [InlineData("tenka_note", "{\"kind\":\"progress\",\"message\":null}", "message")]
    [InlineData("tenka_note", "{\"kind\":\"progress\",\"message\":\"ok\",\"evidenceCallIds\":[null]}", "evidenceCallIds")]
    [InlineData("tenka_note", "{\"kind\":\"progress\",\"message\":\"ok\",\"evidenceCallIds\":[1]}", "evidenceCallIds")]
    public void WrongTypesNameTheActualParameter(string tool, string json, string expected)
    {
        var detail = Detail(AgentToolCallFilter.Validate(tool, Args(json))!);
        Assert.Contains(detail.GetProperty("invalidArguments").EnumerateArray(), item => item.GetProperty("name").GetString() == expected);
    }

    [Theory]
    [InlineData("tenka_review_queue", "{}")]
    [InlineData("tenka_review_queue", "{\"snapshotId\":null,\"projectId\":null,\"limit\":50}")]
    [InlineData("tenka_build", "{\"approved\":false}")]
    [InlineData("tenka_review_diff", "{\"targetId\":\"target\",\"fromRevision\":null,\"toRevision\":9223372036854775807}")]
    [InlineData("tenka_note", "{\"kind\":\"progress\",\"message\":\"ok\",\"evidenceCallIds\":[]}")]
    [InlineData("tenka_session_end", "{}")]
    public async Task ValidShapesReachTheToolOnceWithoutInventingApproval(string tool, string json)
    {
        var args = Args(json); var original = JsonSerializer.Serialize(args); var count = 0;
        var success = new CallToolResult { Content = [new TextContentBlock { Text = "unchanged" }] };
        var result = await AgentToolCallFilter.InvokeAsync(tool, args, () =>
        { count++; return ValueTask.FromResult(success); }, default);
        Assert.Same(success, result); Assert.Equal(1, count);
        Assert.Equal(original, JsonSerializer.Serialize(args));
    }

    [Fact] public void UnknownArgumentNamesAndValuesAreNotEchoed()
    {
        var secret = new string('a', 64);
        var args = new Dictionary<string, JsonElement> { [secret] = JsonSerializer.SerializeToElement("private source or credentials") };
        var detail = Detail(AgentToolCallFilter.Validate("tenka_context", args)!);
        Assert.Equal(1, detail.GetProperty("unknownArgumentCount").GetInt32());
        Assert.DoesNotContain(secret, detail.GetRawText());
        Assert.DoesNotContain("private source", detail.GetRawText());
    }

    [Fact] public async Task SuccessKeepsTheOriginalPayloadText()
    {
        const string text = "{ \"callId\" : \"x\", \"data\" : {\"items\":[]} }";
        var result = await AgentToolResponse.RunAsync(() => Task.FromResult(text));
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(text, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Null(result.StructuredContent);
    }

    [Fact] public async Task SafeFailuresKeepCorrelationWithoutSdkPrefix()
    {
        var token = new string('a', 64);
        var result = await AgentToolResponse.RunAsync(() => throw new AgentClientFailure("tool_failed",
            "Missing approval. Bearer " + token, "Request approval.", token, "session", "request", "call", 409));
        var detail = Detail(result);
        Assert.Equal("tool_failed", detail.GetProperty("code").GetString());
        Assert.Equal("call", detail.GetProperty("callId").GetString());
        Assert.Equal("request", detail.GetProperty("requestId").GetString());
        Assert.Equal(409, detail.GetProperty("httpStatus").GetInt32());
        Assert.DoesNotContain(token, detail.GetRawText());
    }

    [Fact] public async Task UnknownExceptionsAndSdkErrorsNeverPublishTheirDetails()
    {
        var direct = await AgentToolResponse.RunAsync(() => throw new InvalidOperationException("secret exception"));
        var sdk = await AgentToolResponse.GuardAsync(() => ValueTask.FromResult(new CallToolResult
        { IsError = true, Content = [new TextContentBlock { Text = "secret SDK exception" }] }));
        foreach (var result in new[] { direct, sdk })
        {
            var detail = Detail(result);
            Assert.Equal("internal_error", detail.GetProperty("code").GetString());
            Assert.DoesNotContain("secret", detail.GetRawText());
        }
    }

    [Fact] public async Task CancellationIsNotConvertedToAnErrorOrRetried()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel(); var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await AgentToolCallFilter.InvokeAsync("tenka_context", null, () =>
            { count++; return ValueTask.FromResult(new CallToolResult()); }, cts.Token));
        Assert.Equal(0, count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AgentToolResponse.RunAsync(() => Task.FromCanceled<string>(cts.Token)));
    }

    [Fact] public async Task TheActualContextWrapperReturnsStructuredMissingDescriptorFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), "tenka-missing-" + Guid.NewGuid().ToString("N"), "connection.json");
        using var client = new DashboardAgentClient(new(missing, "test"));
        var detail = Detail(await DashboardAgentTools.Context(client));
        Assert.Equal("connection_file_missing", detail.GetProperty("code").GetString());
    }

    [Fact] public void UnknownToolsStayWithTheSdkProtocolHandler() =>
        Assert.Null(AgentToolCallFilter.Validate("not_a_tenka_tool", null));
}
