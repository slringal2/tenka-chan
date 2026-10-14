using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Review;

namespace TenkaChan.Web;

public static class PriorityEndpoints
{
    public static void MapReviewPriorityEndpoints(this WebApplication app)
    {
        app.MapGet("/priorities", () => Results.Redirect("/priorities.html"));
        app.MapGet("/api/priorities/policy", () => Results.Ok(new
        {
            version = ReviewPriorityEngine.Version, defaults = new ReviewPriorityOptions(),
            meaning = "Investigation priority, not confirmed defect severity or probability. P0 is reserved."
        }));
        // Existing Program.cs loopback/Origin/CSRF middleware applies here too.
        // No request accepts client-authored evidence, arbitrary commands or source paths.
        app.MapPost("/api/priorities/report", (ReviewPriorityOptions options,
            RoslynReviewService roslyn, AutonomousReviewRunner review, CancellationToken ct) =>
            Build(app, roslyn, review, options, ct));
    }

    private static async Task<IResult> Build(WebApplication app, RoslynReviewService roslyn,
        AutonomousReviewRunner review, ReviewPriorityOptions options, CancellationToken ct)
    {
        try
        {
            if (review.IsActive) throw new InvalidOperationException("Stop the active LLM review before building priorities.");
            return Results.Ok(await new ReviewPriorityService(roslyn).BuildAsync(options, ct));
        }
        catch (OperationCanceledException) { return Results.Json(new { error = "Request cancelled." }, statusCode: 408); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Priority report failed");
            return Results.Json(new { error = "Priority report failed; inspect the local server log." }, statusCode: 500);
        }
    }
}
