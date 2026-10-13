using System.Net;
using System.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Review;
using Xunit;

namespace TenkaChan.Tests;

public sealed class LocalLlmReviewReasonerTests
{
    [Fact]
    public void EnforcesLoopbackEndpoint()
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalLlmSettings(new Uri("https://example.com/v1/chat/completions"), "local").Validate());
        Assert.Throws<ArgumentException>(() =>
            new LocalLlmSettings(new Uri("http://192.168.1.20:8080/v1/chat/completions"), "local").Validate());
        Assert.Throws<ArgumentException>(() =>
            new LocalLlmSettings(new Uri("http://127.0.0.1:8080/other"), "local").Validate());
        new LocalLlmSettings(new Uri("http://127.0.0.1:8080/v1/chat/completions"), "local").Validate();
    }

    [Fact]
    public void ParserRejectsInvalidAndDeduplicatesHypotheses()
    {
        Assert.Throws<InvalidDataException>(() => LlmHypothesisParser.Parse("{\"notes\":\"no list\"}"));
        var text = """
            {"hypotheses":[
              {"category":"correctness","claim":"May divide by zero","failureCondition":"b == 0","reason":"unguarded"},
              {"category":"correctness","claim":"May divide by zero","failureCondition":"b == 0"},
              {"category":"wrong","claim":"invalid","failureCondition":"something"},
              {"category":"performance","claim":"Allocation in loop","failureCondition":"large input"},
              {"category":"resource","claim":"Leak","failureCondition":"dispose missing"},
              {"category":"contract","claim":"Never read due to cap","failureCondition":"not applicable"}
            ],"notes":"Unverified."}
            """;
        var output = LlmHypothesisParser.Parse(text);
        Assert.Equal(3, output.Hypotheses.Count);
        Assert.Equal("Unverified.", output.Notes);
        Assert.Single(output.Hypotheses.Where(x => x.Category == "correctness"));
    }

    [Fact]
    public async Task SendsRoslynContextAndParsesOpenAiCompatibleResponse()
    {
        var reply = """
            {"choices":[{"message":{"role":"assistant",
            "content":"{\"hypotheses\":[{\"category\":\"correctness\",\"claim\":\"Negative inputs may be mishandled\",\"failureCondition\":\"x < 0\",\"reason\":\"boundary assumption\",\"testIdeas\":[\"test -1\"]}],\"notes\":\"Potential only\"}"}}]}
            """;
        var handler = new FakeHttpHandler(reply);
        using var http = new HttpClient(handler);
        using var model = new LocalLlmReviewReasoner(
            new LocalLlmSettings(new Uri("http://127.0.0.1:8080/v1/chat/completions"), "test-model"), http);
        var result = await model.AnalyzeAsync(CreateContext(), CancellationToken.None);
        Assert.Single(result.Hypotheses);
        Assert.Equal("correctness", result.Hypotheses[0].Category);
        Assert.Contains("Example.Analyze", handler.Request ?? "");
        Assert.Contains("test-model", handler.Request ?? "");
        Assert.Contains("unguarded", handler.Request ?? "");
        Assert.Equal("test-model", model.ModelName);
    }

    private static ReviewMethodContext CreateContext()
    {
        var method = new MethodSummary("id", "Example", "Example.Analyze(int x)", "Example.cs", 3);
        var inspect = new MethodInspection(method, "return x;", [], [], [], [], [], [], null);
        var plan = new SuggestedTestPlan("id", method.Signature, true, [], [], "unresolved");
        return new ReviewMethodContext(inspect, plan,
            [new RuleFinding("PERF1001", "allocation", "info", "unguarded",
                "Example.cs", 3, 1, "id", "benchmark")]);
    }

    private sealed class FakeHttpHandler(string response) : HttpMessageHandler
    {
        public string? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri?.AbsolutePath);
            Request = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
