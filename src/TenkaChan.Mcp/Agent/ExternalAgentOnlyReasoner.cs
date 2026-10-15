using TenkaChan.Mcp.Review;
namespace TenkaChan.Mcp.Agent;

// Compatibility for stored legacy review history and legacy runner dependencies.
// Production DI registers this instead of an inference client. It cannot send network requests.
public sealed class ExternalAgentOnlyReasoner : IReviewReasoner
{
    public string ModelName => "external-agent";
    public Task<ReviewModelOutput> AnalyzeAsync(ReviewMethodContext context, CancellationToken cancellationToken) =>
        Task.FromException<ReviewModelOutput>(new InvalidOperationException(
            "Internal LLM inference is retired. Use an external MCP agent and observe /agent.html."));
}
