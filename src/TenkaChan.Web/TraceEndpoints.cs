using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public static class TraceEndpoints
{
    public static void MapTrustedTraceEndpoints(this WebApplication app)
    {
        // All mutations run under Program.cs's loopback, Origin and CSRF middleware.
        app.MapGet("/trace", () => Results.Redirect("/trace.html"));
        app.MapPost("/api/traces/capture", (CaptureRequest request, WorkspaceGuard guard, CancellationToken ct) =>
            Safe(app, async () => (object)await new TrustedTraceCapture(guard).CaptureAsync(
                request.AssemblyPath, request.OutputDirectory, request.TrustedBinary,
                request.TimeoutSeconds, request.Arguments, ct, request.RuntimeCostEvents)));
        app.MapPost("/api/traces/runtime-costs", (RuntimeCostRequest request, WorkspaceGuard guard, CancellationToken ct) =>
            Safe(app, async () => (object)await new RuntimeCostReader(guard).ReadAsync(request.ReportPath,
                request.ProcessId, new(request.MaxEvents, request.MaxRows, request.PauseBudgetMs), ct)));
        app.MapGet("/api/traces/report", (string reportPath, WorkspaceGuard guard) =>
            Safe(app, () => Task.FromResult((object)new TrustedTraceCapture(guard).Read(reportPath))));
        app.MapPost("/api/traces/compare-binary", (CompareRequest request, WorkspaceGuard guard) =>
            Safe(app, () => Task.FromResult((object)new TrustedTraceCapture(guard).CompareBinary(request.ReportPath, request.AssemblyPath))));
        app.MapPost("/api/traces/import", (ImportRequest request, WorkspaceGuard guard,
            DashboardState dashboard, AutonomousReviewRunner review, CancellationToken ct) => Safe(app, async () =>
        {
            if (review.IsActive) throw new InvalidOperationException("Stop the active LLM review before importing a profile.");
            if (request.ProfileIndex is not int index) throw new ArgumentException("Choose an explicit profile/thread index.");
            var reader = new TrustedTraceCapture(guard);
            var capture = reader.Read(request.ReportPath);
            var file = reader.RequireProfile(capture, index);
            var report = await dashboard.ImportHotspotProfileAsync(file, index, request.MaxFrames, ct);
            return (object)new { capture.Id, capture.MeasurementKind, capture.Speedscope,
                profileIndex = index, association = "profile_names_only__source_build_not_verified", report };
        }));
    }
    private static async Task<IResult> Safe(WebApplication app, Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (OperationCanceledException) { return Results.Json(new { error = "Request cancelled or runtime analysis deadline exceeded." }, statusCode: 408); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Trace API failed");
            return Results.Json(new { error = "Trace operation failed. See the local server log." }, statusCode: 500);
        }
    }
    public sealed record CaptureRequest(string AssemblyPath, string OutputDirectory,
        bool TrustedBinary, int TimeoutSeconds = 60, string[]? Arguments = null, bool RuntimeCostEvents = false);
    public sealed record RuntimeCostRequest(string ReportPath, int? ProcessId = null,
        int MaxEvents = 500000, int MaxRows = 100, double? PauseBudgetMs = null);
    public sealed record ImportRequest(string ReportPath, int? ProfileIndex, int MaxFrames = 200);
    public sealed record CompareRequest(string ReportPath, string AssemblyPath);
}
