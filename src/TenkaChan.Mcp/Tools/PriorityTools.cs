using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Review;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class PriorityTools
{
    [McpServerTool(Name = "review_priority_report")]
    [Description("Build a deterministic investigation queue from current Roslyn findings, static sites and the selected imported profile. Priority is not defect certainty; this tool never starts an executable or LLM. Suppressions are exact item IDs with mandatory reasons, scoped to this call.")]
    public static async Task<string> Report(RoslynReviewService roslyn, AutonomousReviewRunner review,
        double hotSharePercent = 10, int minimumObservations = 20, double staticSiteThreshold = 40,
        int maxItems = 100, string? priorityFilter = null, string? evidenceFilter = null,
        PrioritySuppression[]? suppressions = null, bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
    {
        if (review.IsActive) throw new InvalidOperationException("Stop the active LLM review before building priorities.");
        var options = new ReviewPriorityOptions(hotSharePercent, minimumObservations, staticSiteThreshold,
            maxItems, priorityFilter, evidenceFilter, suppressions, includeSuppressed);
        return JsonSerializer.Serialize(await new ReviewPriorityService(roslyn).BuildAsync(options, cancellationToken),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [McpServerTool(Name = "review_priority_policy")]
    [Description("Read versioned priority defaults and their evidentiary limits; not calibrated detection probabilities.")]
    public static string Policy() => JsonSerializer.Serialize(new
    {
        version = ReviewPriorityEngine.Version, defaults = new ReviewPriorityOptions(),
        p0 = "Reserved; no P0 or confirmed-defect claim can be produced by this engine.",
        p1 = "Repair compiler/configured-analyzer build errors, or investigate a prominent observed frame with enough observations.",
        p2 = "Review warning/semantic facts, larger static sites, or inclusive-heavy observations.",
        p3 = "Reference candidate; not proof of correctness or low cost.",
        measurement = "Imported units stay unchanged; thread time may include waiting. No static score is added to measured weight."
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
