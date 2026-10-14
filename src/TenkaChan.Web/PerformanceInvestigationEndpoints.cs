using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public static class PerformanceInvestigationEndpoints
{
    public static void MapPerformanceInvestigation(this WebApplication app)
    {
        app.MapPost("/api/performance/triage", (TriageRequest request,
            PerformanceInvestigationService service, AutonomousReviewRunner runner, CancellationToken ct) =>
            Safe(app, async () =>
            {
                EnsureIdle(runner);
                return await service.TriageAsync(request.Ranking, request.Limit, ct);
            }));
        app.MapPost("/api/performance/investigate", (InvestigationRequest request,
            PerformanceInvestigationService service, AutonomousReviewRunner runner, CancellationToken ct) =>
            Safe(app, async () =>
            {
                EnsureIdle(runner);
                return await service.InvestigateAsync(request.MethodId, request.JitExperimentId,
                    request.AssemblyFile, request.MethodToken, request.SourceFilePath, ct);
            }));
        app.MapGet("/api/performance/experiments/{id}/files", (string id, ExperimentStore store, WorkspaceGuard guard) =>
            Safe(app, () => Task.FromResult<object>(new JitBinarySourceService(store, guard).Files(id))));
        app.MapGet("/api/performance/experiments/{id}/methods", (string id, string assemblyFile,
            string? query, ExperimentStore store, WorkspaceGuard guard) =>
            Safe(app, () => Task.FromResult<object>(new JitBinarySourceService(store, guard).Methods(id, assemblyFile, query ?? "", 200))));
    }

    private static void EnsureIdle(AutonomousReviewRunner runner)
    {
        if (runner.IsActive) throw new InvalidOperationException("Stop active LLM review before opening a performance investigation.");
    }

    private static async Task<IResult> Safe(WebApplication app, Func<Task<object>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (OperationCanceledException) { return Results.Json(new { error = "Investigation cancelled." }, statusCode: 408); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or KeyNotFoundException or BadImageFormatException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Performance investigation failed");
            return Results.Json(new { error = "Investigation failed. See the local server log." }, statusCode: 500);
        }
    }
}

public sealed record TriageRequest(string Ranking = "self", int Limit = 100);
public sealed record InvestigationRequest(string MethodId, string? JitExperimentId = null,
    string? AssemblyFile = null, int? MethodToken = null, string? SourceFilePath = null);
