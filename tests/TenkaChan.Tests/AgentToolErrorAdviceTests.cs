using System.Text.Json;
using ModelContextProtocol.Protocol;
using TenkaChan.Mcp.Agent;
using Xunit;

namespace TenkaChan.Tests;

public sealed class AgentToolErrorAdviceTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void ReportsMissingRequiredArguments()
    {
        var missing = AgentToolErrorAdvice.MissingRequired(
            Schema("""{"type":"object","required":["snapshotId","projectId"],"properties":{"snapshotId":{},"projectId":{}}}"""),
            Args("""{"snapshotId":"ABC"}"""));

        Assert.Equal(new[] { "projectId" }, missing);
    }

    [Fact]
    public void ReportsAllRequiredWhenNoArguments()
    {
        var missing = AgentToolErrorAdvice.MissingRequired(
            Schema("""{"type":"object","required":["snapshotId","projectId"]}"""),
            null);

        Assert.Equal(new[] { "snapshotId", "projectId" }, missing);
    }

    [Fact]
    public void TreatsNullArgumentAsMissing()
    {
        var missing = AgentToolErrorAdvice.MissingRequired(
            Schema("""{"type":"object","required":["projectId"]}"""),
            Args("""{"projectId":null}"""));

        Assert.Equal(new[] { "projectId" }, missing);
    }

    [Fact]
    public void ReturnsEmptyWhenEverythingIsPresent()
    {
        var missing = AgentToolErrorAdvice.MissingRequired(
            Schema("""{"type":"object","required":["snapshotId"]}"""),
            Args("""{"snapshotId":"ABC"}"""));

        Assert.Empty(missing);
    }

    [Fact]
    public void ReturnsEmptyWhenSchemaHasNoRequiredList()
    {
        var missing = AgentToolErrorAdvice.MissingRequired(
            Schema("""{"type":"object","properties":{"query":{}}}"""),
            Args("""{}"""));

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("tenka_build", "An error occurred invoking 'tenka_build'.", true)]
    [InlineData("tenka_build", "An error occurred invoking 'tenka_il'.", false)]
    [InlineData("tenka_build", "An error occurred invoking 'tenka_build': something", false)]
    public void MatchesOnlyTheExactGenericFailure(string tool, string content, bool expected)
    {
        Assert.Equal(expected, AgentToolErrorAdvice.IsGenericInvocationFailure(tool, content));
    }

    [Fact]
    public void ExpectedRevisionNullIsNamedBeforeMcpBinding()
    {
        var schema = Schema("""
            {"type":"object","required":["expectedRevision"],"properties":{
              "expectedRevision":{"type":"integer","format":"int64"}
            }}
            """);
        var arguments = Args("""{"expectedRevision":null}""");
        Assert.Equal(new[] { "expectedRevision" },
            AgentToolErrorAdvice.MissingRequired(schema, arguments));
        var response = AgentToolErrorAdvice.Failure("tenka_mark_review",
            AgentToolErrorAdvice.MissingRequired(schema, arguments));
        var text = Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text;
        Assert.Contains("expectedRevision", text);
        Assert.True(response.IsError);
    }

    [Theory]
    [InlineData("{\"expectedRevision\":\"2\"}")]
    [InlineData("{\"expectedRevision\":1.4}")]
    [InlineData("{\"expectedRevision\":9223372036854775808}")]
    public void WrongInt64TypeIsReportedWithoutEchoingItsValue(string json)
    {
        var schema = Schema("""
            {"type":"object","properties":{"expectedRevision":{"type":"integer","format":"int64"}}}
            """);
        var invalid = AgentToolErrorAdvice.InvalidTypes(schema, Args(json));
        Assert.Equal("expectedRevision", Assert.Single(invalid).Field);
        var result = AgentToolErrorAdvice.TypeFailure("tenka_mark_review", invalid);
        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("expectedRevision expects integer (int64)", message);
        Assert.DoesNotContain("9223372036854775808", message);
    }

    [Fact]
    public void ValidNullableArgumentsAndArrayMembersRemainValid()
    {
        var schema = Schema("""
            {"type":"object","properties":{
              "query":{"type":["string","null"]},
              "minutes":{"type":"integer","format":"int32"},
              "tools":{"type":"array","items":{"type":"string"}}
            }}
            """);
        Assert.Empty(AgentToolErrorAdvice.InvalidTypes(schema,
            Args("""{"query":null,"minutes":10,"tools":["build","run_companion"]}""")));
        var wrong = AgentToolErrorAdvice.InvalidTypes(schema,
            Args("""{"query":null,"minutes":2147483648,"tools":["build",17]}"""));
        Assert.Equal(new[] { "minutes", "tools[1]" }, wrong.Select(x => x.Field).ToArray());
    }

    [Fact]
    public void NullableAnyOfAcceptsNullButNotUnrelatedKind()
    {
        var schema = Schema("""
            {"type":"object","properties":{"targetId":{
              "anyOf":[{"type":"string"},{"type":"null"}]
            }}}
            """);
        Assert.Empty(AgentToolErrorAdvice.InvalidTypes(schema, Args("""{"targetId":null}""")));
        Assert.Single(AgentToolErrorAdvice.InvalidTypes(schema, Args("""{"targetId":true}""")));
    }

    [Fact]
    public void MessageNamesTheMissingArguments()
    {
        var message = AgentToolErrorAdvice.Message("tenka_build", new[] { "projectId" });

        Assert.Contains("tenka_build", message);
        Assert.Contains("projectId", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void FailureReturnsErrorResultNamingTheArguments()
    {
        var result = AgentToolErrorAdvice.Failure("tenka_build", new[] { "projectId", "snapshotId" });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("projectId", text.Text);
        Assert.Contains("snapshotId", text.Text);
    }
}
