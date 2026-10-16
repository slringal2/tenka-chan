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
