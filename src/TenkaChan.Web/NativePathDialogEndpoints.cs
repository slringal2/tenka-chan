using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public static class NativePathDialogEndpoints
{
    public static void MapNativePathDialogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dialogs/status", () => Results.Ok(new { available = NativePathDialog.Available }));
        // These routes inherit the normal local-only Host/Origin/CSRF middleware.
        app.MapPost("/api/dialogs/pick", async (PickPathRequest request, WorkspaceGuard guard, CancellationToken ct) =>
        {
            try { return Results.Ok(await new NativePathDialog(guard).PickAsync(request.Kind, request.InitialPath, ct)); }
            catch (UnauthorizedAccessException ex)
            { return Results.Json(new { error = ex.Message }, statusCode: 403); }
            catch (InvalidOperationException ex)
            { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (PlatformNotSupportedException ex)
            { return Results.Json(new { error = ex.Message }, statusCode: 501); }
            catch (OperationCanceledException)
            { return Results.Json(new { error = "選択を中止しました（制限時間3分）。パスは変更していません。" }, statusCode: 408); }
            catch (Exception ex) when (ex is ArgumentException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                app.Logger.LogWarning(ex, "Native picker failed");
                return Results.Json(new { error = "選択画面を利用できませんでした。パスを手入力するか、start.batで再起動してください。" }, statusCode: 400);
            }
        });
    }
    public sealed record PickPathRequest(string Kind, string? InitialPath = null);
}
