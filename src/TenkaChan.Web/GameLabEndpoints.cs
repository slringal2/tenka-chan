using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public static class GameLabEndpoints
{
    public static void MapGameLabEndpoints(this WebApplication app)
    {
        // Same loopback-only middleware as the dashboard; this endpoint only reads paths.
        app.MapGet("/api/samples/game-lab", (WorkspaceGuard guard) =>
            Results.Ok(new BundledSampleCatalog(guard).FindGameLab(app.Environment.ContentRootPath)));
    }
}
