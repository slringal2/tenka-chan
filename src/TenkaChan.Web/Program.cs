using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Locator;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;
using TenkaChan.Web;

var builder = WebApplication.CreateBuilder(args);
var port = int.TryParse(Environment.GetEnvironmentVariable("TENKA_WEB_PORT"), out var configuredPort)
    && configuredPort is >= 1024 and <= 65535 ? configuredPort : 5077;

// Deliberately ignore externally configured ASPNETCORE_URLS. No network
// listening or reverse proxy access is supported by this local-only dashboard.
builder.WebHost.ConfigureKestrel(server => server.ListenLocalhost(port));

builder.Services.AddSingleton<WorkspaceGuard>();
builder.Services.AddSingleton<RoslynReviewService>();
builder.Services.AddSingleton<DashboardState>();
builder.Services.AddSingleton<ExperimentStore>();
builder.Services.AddSingleton<DockerExecutor>();
builder.Services.AddSingleton<TrustedLocalJitRunner>();
builder.Services.AddSingleton<BenchmarkRunComparer>();
builder.Services.AddSingleton<NativeExperimentComparer>();
builder.Services.AddSingleton<ReviewLedger>();
builder.Services.AddSingleton<ReviewRunStore>();
builder.Services.AddSingleton<IReviewContextSource>(
    services => services.GetRequiredService<DashboardState>());
builder.Services.AddSingleton<IReviewReasoner, LocalLlmReviewReasoner>();
builder.Services.AddSingleton<AutonomousReviewRunner>();

// MSBuild project evaluation can execute custom build targets: opt-in only.
if (Environment.GetEnvironmentVariable("TENKA_TRUST_MSBUILD") == "1"
    && !MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

var app = builder.Build();
var csrf = RandomNumberGenerator.GetHexString(32);

// A malicious web page must not be able to make the local service evaluate
// MSBuild files or start a Docker container through cross-site POST requests.
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]")
        || context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; " +
        "img-src 'self' data:; connect-src 'self'; font-src 'self'; " +
        "object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    if (context.Request.Path.StartsWithSegments("/api")
        && context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expected = context.Request.Scheme + "://" + context.Request.Host;
        var header = context.Request.Headers["X-Tenka-CSRF"].ToString();
        var equalLength = header.Length == csrf.Length;
        var tokenValid = equalLength && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(header), Encoding.ASCII.GetBytes(csrf));

        if ((origin.Length > 0 && !origin.Equals(expected, StringComparison.OrdinalIgnoreCase))
            || !tokenValid)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Denied: invalid origin or missing dashboard CSRF token."
            });
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/session", (WorkspaceGuard guard) => Results.Ok(new
{
    csrfToken = csrf,
    listening = "loopback",
    msbuildEnabled = guard.TrustMsBuild,
    dockerEnabled = guard.AllowDocker,
    trustedLocalExecutionEnabled = Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1",
    model = Environment.GetEnvironmentVariable("TENKA_LLM_MODEL") ?? "local",
    llmEndpoint = LocalLlmSettings.FromEnvironment().Endpoint.ToString(),
    port
}));

app.MapGet("/api/overview", (DashboardState dashboard) => Results.Ok(dashboard.Overview()));

app.MapPost("/api/workspace/open", (OpenWorkspaceRequest request, DashboardState dashboard,
    AutonomousReviewRunner runner, CancellationToken ct) => Safe(async () =>
{
    if (runner.IsActive) throw new InvalidOperationException("Stop active review before opening another workspace.");
    return (object)await dashboard.OpenAsync(request.Path, ct);
}));

app.MapPost("/api/workspace/scan", (DashboardState dashboard,
    AutonomousReviewRunner runner, CancellationToken ct) =>
    Safe(async () =>
    {
        if (runner.IsActive) throw new InvalidOperationException("Stop active review before rescanning.");
        return await dashboard.ScanAsync(ct);
    }));

app.MapPost("/api/local-jit/capture",
    (LocalJitCaptureRequest request, TrustedLocalJitRunner runner, CancellationToken ct) =>
    Safe(async () => (object)await runner.CaptureAsync(
        request.AssemblyPath, request.JitFilter, request.TrustedBinary,
        request.TimeoutSeconds, request.Arguments, ct)));

app.MapGet("/api/local-jit/{id}", (TrustedLocalJitRunner runner, string id) =>
    Safe(() => Task.FromResult((object)runner.Get(id))));

app.MapGet("/api/hotspots/report", (DashboardState dashboard) =>
    Results.Ok(new
    {
        staticAnalysis = dashboard.StaticHotspotReport,
        sampledProfile = dashboard.MeasuredHotspotReport
    }));

app.MapPost("/api/hotspots/scan", (HotspotScanRequest request,
    DashboardState dashboard, AutonomousReviewRunner runner,
    CancellationToken ct) => Safe(async () =>
{
    if (runner.IsActive)
        throw new InvalidOperationException("Stop active LLM review before scanning hotspots.");
    return (object)await dashboard.ScanHotspotsAsync(
        request.MaxMethods, request.MaxSites, ct);
}));

app.MapPost("/api/hotspots/profile/import", (HotspotProfileRequest request,
    DashboardState dashboard, AutonomousReviewRunner runner,
    CancellationToken ct) => Safe(async () =>
{
    if (runner.IsActive)
        throw new InvalidOperationException("Stop active LLM review before importing a profile.");
    return (object)await dashboard.ImportHotspotProfileAsync(
        request.FilePath, request.ProfileIndex, request.MaxFrames, ct);
}));

app.MapGet("/api/deterministic/rules", () =>
    Results.Ok(new { version = DeterministicRuleCatalog.Version,
        rules = DeterministicRuleCatalog.Rules }));

app.MapGet("/api/deterministic/report", (DashboardState dashboard) =>
    Results.Ok(dashboard.DeterministicReport));

app.MapPost("/api/deterministic/run", (
    DeterministicRunRequest request, DashboardState dashboard,
    AutonomousReviewRunner runner, CancellationToken ct) =>
    Safe(async () =>
    {
        if (runner.IsActive)
            throw new InvalidOperationException(
                "Stop active LLM review before rebuilding the deterministic report.");
        return (object)await dashboard.AnalyzeDeterministicAsync(
            request.MaxFindings, request.MaxMethodMetrics, ct);
    }));

app.MapGet("/api/findings", (DashboardState dashboard, string? q,
    string? category, int? offset, int? limit) =>
    Results.Ok(dashboard.Findings(q, category, offset ?? 0, limit ?? 100)));

app.MapGet("/api/methods", (DashboardState dashboard, string? q,
    int? offset, int? limit, CancellationToken ct) =>
    Safe(async () => (object)await dashboard.TargetsAsync(q, offset ?? 0, limit ?? 80, ct)));

app.MapGet("/api/methods/{id}", (DashboardState dashboard, string id,
    CancellationToken ct) =>
    Safe(async () => (object)await dashboard.InspectAsync(id, ct)));

app.MapGet("/api/methods/{id}/plan", (DashboardState dashboard, string id,
    CancellationToken ct) =>
    Safe(async () => (object)await dashboard.PlanAsync(id, ct)));

app.MapGet("/api/diagnostics", (DashboardState dashboard, CancellationToken ct) =>
    Safe(async () => (object?)await dashboard.DiagnosticsAsync(ct) ?? new { diagnostics = Array.Empty<object>() }));

app.MapGet("/api/hypotheses", (ReviewLedger ledger, string? status, int? limit) =>
    Safe(() => Task.FromResult((object)ledger.List(status, limit ?? 80))));

app.MapGet("/api/hypotheses/{id}", (ReviewLedger ledger, string id) =>
    Safe(() => Task.FromResult((object)ledger.Get(id))));

app.MapPost("/api/hypotheses", (CreateHypothesisRequest request, ReviewLedger ledger) =>
    Safe(() => Task.FromResult((object)ledger.Create(request.SnapshotId, request.MethodId,
        request.Category, request.Claim, request.FailureCondition))));

app.MapGet("/api/experiments", (ExperimentStore store, int? limit) =>
    Safe(() => Task.FromResult((object)store.ListRecent(limit ?? 80))));

app.MapPost("/api/native/compare", (CompareNativeRequest request,
    NativeExperimentComparer comparer) =>
    Safe(() => Task.FromResult((object)comparer.Compare(
        request.BaselineExperimentId, request.CandidateExperimentId))));

app.MapGet("/api/native/{id}/correlate", (ExperimentStore store,
    DashboardState dashboard, string id, CancellationToken ct) =>
    Safe(async () =>
    {
        var report = store.ReadExecutionResult(id).NativeDisassembly
            ?? throw new InvalidOperationException("No native JIT report in this experiment.");
        return (object)await dashboard.CorrelateNativeAsync(report, ct);
    }));

app.MapGet("/api/experiments/{id}", (ExperimentStore store, string id) =>
    Safe(() => Task.FromResult((object)store.ReadExecutionResult(id))));

app.MapPost("/api/experiments/prepare", (PrepareExperimentRequest request, ExperimentStore store) =>
    Safe(() => Task.FromResult((object)store.Create(request.RepositoryRoot,
        request.ProjectRelativePath, request.Kind, request.GeneratedCode,
        request.TargetFramework, request.JitDisasmFilter))));

app.MapPost("/api/experiments/{id}/run", (DockerExecutor executor, string id, CancellationToken ct) =>
    Safe(async () => (object)await executor.RunAsync(id, ct)));

app.MapPost("/api/benchmarks/compare", (CompareBenchmarksRequest request,
    BenchmarkRunComparer comparer) =>
    Safe(() => Task.FromResult((object)comparer.Compare(
        request.BaselineExperimentId, request.CandidateExperimentId,
        request.ThresholdPercent ?? 5))));

app.MapGet("/api/reviews/current", (AutonomousReviewRunner runner) =>
    Results.Ok(runner.Current));

app.MapGet("/api/reviews", (AutonomousReviewRunner runner, int? limit) =>
    Results.Ok(runner.History(limit ?? 20)));

app.MapGet("/api/reviews/{id}", (AutonomousReviewRunner runner, string id) =>
    Safe(() => Task.FromResult((object)runner.Get(id))));

app.MapPost("/api/reviews/start", (StartAutonomousReviewRequest request,
    AutonomousReviewRunner runner, CancellationToken ct) =>
    Safe(async () => (object)await runner.StartAsync(
        new AutonomousReviewOptions(request.MaxMethods, request.MaxMinutes), ct)));

app.MapPost("/api/reviews/{id}/stop", (AutonomousReviewRunner runner, string id) =>
    Safe(() => Task.FromResult((object)runner.Stop(id))));

app.Logger.LogInformation("Tenka-chan dashboard: http://127.0.0.1:{Port}", port);
await app.RunAsync();

async Task<IResult> Safe(Func<Task<object>> operation)
{
    try
    {
        return Results.Ok(await operation());
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Request cancelled." }, statusCode: 408);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
        or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
        or InvalidDataException or KeyNotFoundException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 400);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Local dashboard API operation failed");
        return Results.Json(new { error = "Internal error. See local server log." }, statusCode: 500);
    }
}

public sealed record HotspotScanRequest(int MaxMethods = 100, int MaxSites = 300);
public sealed record HotspotProfileRequest(string FilePath, int ProfileIndex = 0,
    int MaxFrames = 100);
public sealed record DeterministicRunRequest(int MaxFindings = 500,
    int MaxMethodMetrics = 100);
public sealed record StartAutonomousReviewRequest(int MaxMethods = 10, int MaxMinutes = 15);
public sealed record LocalJitCaptureRequest(
    string AssemblyPath, string JitFilter, bool TrustedBinary,
    int TimeoutSeconds = 45, string[]? Arguments = null);
public sealed record OpenWorkspaceRequest(string Path);
public sealed record CreateHypothesisRequest(string SnapshotId, string MethodId,
    string Category, string Claim, string FailureCondition);
public sealed record PrepareExperimentRequest(string RepositoryRoot,
    string ProjectRelativePath, string Kind, string GeneratedCode,
    string? TargetFramework = null, string? JitDisasmFilter = null);
public sealed record CompareNativeRequest(
    string BaselineExperimentId, string CandidateExperimentId);
public sealed record CompareBenchmarksRequest(string BaselineExperimentId,
    string CandidateExperimentId, double? ThresholdPercent);
