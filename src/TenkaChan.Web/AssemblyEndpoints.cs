using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public static class AssemblyEndpoints
{
    public static void MapAssemblyEndpoints(this WebApplication app)
    {
        app.MapWorkspaceBuildEndpoints();
        // Same loopback, Origin and CSRF middleware as every other local API.
        app.MapGet("/assembly", () => Results.Redirect("/assembly.html"));
        app.MapPost("/api/assembly/methods", (MethodsRequest request, WorkspaceGuard guard) =>
            Safe(app, () => new BinarySourceInspector(guard).Methods(request.AssemblyPath, request.Query, request.Limit)));
        app.MapPost("/api/assembly/il", (IlRequest request, WorkspaceGuard guard) => Safe(app, () =>
        {
            if (request.ExpectedAssemblySha256 is null || request.ExpectedAssemblySha256.Length != 64 ||
                !request.ExpectedAssemblySha256.All(Uri.IsHexDigit))
                throw new ArgumentException("The assembly catalog SHA-256 is required; choose a token from the current catalog.");
            var result = new BinarySourceInspector(guard).Inspect(request.AssemblyPath, request.MethodToken,
                request.PdbPath, request.SourceFilePath, request.MaxInstructions);
            if (!string.Equals(result.AssemblySha256, request.ExpectedAssemblySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The DLL changed after the catalog was read. Reload the method list before using its tokens.");
            return result;
        }));
        app.MapPost("/api/assembly/native", (NativeAssemblySelection request, WorkspaceGuard guard,
            ExperimentStore store, TrustedLocalJitRunner local) =>
            Safe(app, () => new AssemblyWorkbench(guard, store, local).Analyze(request)));
        app.MapPost("/api/assembly/compare", (CompareRequest request, WorkspaceGuard guard,
            ExperimentStore store, TrustedLocalJitRunner local) => Safe(app, () =>
        {
            if (request.Baseline is null || request.Candidate is null) throw new ArgumentException("Both selections are required.");
            return new AssemblyWorkbench(guard, store, local).Compare(request.Baseline, request.Candidate);
        }));
    }

    private static IResult Safe(WebApplication app, Func<object> action)
    {
        try { return Results.Ok(action()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
            InvalidDataException or UnauthorizedAccessException or BadImageFormatException or System.Text.Json.JsonException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Assembly analysis failed");
            return Results.Json(new { error = "Assembly analysis failed. Check the local server log." }, statusCode: 500);
        }
    }
    public sealed record MethodsRequest(string AssemblyPath, string Query = "", int Limit = 200);
    public sealed record IlRequest(string AssemblyPath, int MethodToken, string ExpectedAssemblySha256,
        string? PdbPath = null, string? SourceFilePath = null, int MaxInstructions = 1200);
    public sealed record CompareRequest(NativeAssemblySelection Baseline, NativeAssemblySelection Candidate);
}
