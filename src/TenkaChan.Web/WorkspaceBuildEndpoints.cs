using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public static class WorkspaceBuildEndpoints
{
    public static void MapWorkspaceBuildEndpoints(this WebApplication app)
    {
        app.MapGet("/workspace-build", () => Results.Redirect("/workspace-build.html"));
        app.MapGet("/api/workspace/build-options", (DashboardState dashboard,
            WorkspaceGuard guard, CancellationToken ct) => Safe(app, async () =>
                await dashboard.BuildOptionsAsync(new TrustedWorkspaceBuilder(guard).Enabled, ct)));
        // Registered from the existing assembly workbench; inherits loopback, Origin and CSRF checks.
        app.MapPost("/api/workspace/build", (BuildWorkspaceRequest request,
            DashboardState dashboard, WorkspaceGuard guard, WorkspaceBinaryDiscovery discovery,
            AutonomousReviewRunner review, CancellationToken ct) => Safe(app, async () =>
        {
            if (review.IsActive)
                throw new WorkspaceBuildConflictException("Stop the active AI review before building.");
            return await dashboard.BuildLoadedProjectAsync(request,
                new TrustedWorkspaceBuilder(guard), discovery, ct);
        }));
    }

    private static async Task<IResult> Safe(WebApplication app, Func<Task<object>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (WorkspaceBuildConflictException ex)
        { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        catch (UnauthorizedAccessException ex)
        { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        catch (OperationCanceledException)
        { return Results.Json(new { error = "Request cancelled." }, statusCode: 408); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or InvalidDataException or BadImageFormatException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Workspace build or preparation failed");
            return Results.Json(new { error = "Workspace preparation failed. See the local server log." }, statusCode: 500);
        }
    }
}
