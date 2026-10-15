using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

/// <summary>Private loopback HTTP for the stdio adapter, NOT an MCP Streamable HTTP endpoint.</summary>
public static class AgentGatewayEndpoints
{
    public static void MapAgentGatewayEndpoints(this WebApplication app)
    {
        var journal = new AgentJournal();
        var port = int.TryParse(Environment.GetEnvironmentVariable("TENKA_WEB_PORT"), out var p) && p is >= 1024 and <= 65535 ? p : 5077;
        var connection = new AgentConnection(journal.InstanceId, port);
        var gateway = new AgentGateway(app.Services, journal, connection);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try { connection.Publish(); app.Logger.LogInformation("External agent connection descriptor: {Path}", connection.FilePath); }
            catch (Exception ex) { app.Logger.LogError(ex, "Could not publish agent connection descriptor. External agent access may be unavailable."); }
        });
        app.Lifetime.ApplicationStopped.Register(connection.RemoveOwnFile);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/agent/v1"))
            {
                // Never exempt the browser /api routes from CSRF. This is a separate authenticated machine API.
                if (!connection.Authorize(context.Request.Headers.Authorization.ToString(),
                    context.Request.Headers.ContainsKey("Origin"), context.Request.Headers.ContainsKey("Sec-Fetch-Site")))
                { context.Response.StatusCode = 403; return; }
                var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 256000;
                if (context.Request.ContentLength > 256000) { context.Response.StatusCode = 413; return; }
            }
            await next();
        });
        app.MapPost("/agent/v1/begin", (AgentSessionRequest request) => Protect(() => Task.FromResult((object)
            journal.Begin(request.ClientName, request.Goal))));
        app.MapPost("/agent/v1/call", (AgentCallRequest request, CancellationToken ct) =>
            Protect(() => gateway.CallAsync(request, ct)));
        app.MapPost("/agent/v1/end", (AgentEndRequest request) => Protect(() =>
        { journal.Finish(request.SessionId); return Task.FromResult((object)new { status = "finished" }); }));
        // Monitor never supplies tokens, source text, model URLs or model keys.
        app.MapGet("/api/agent/result", (string callId) => Protect(() => Task.FromResult(journal.MonitorResult(callId))));
        app.MapGet("/api/agent/monitor", () => Results.Ok(journal.Monitor(connection.Enabled && connection.Published, connection.AllowBuild, connection.AllowExecution)));
    }
    public sealed record AgentEndRequest(string SessionId);
    private static async Task<IResult> Protect(Func<Task<object>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Agent operation is not authorized by the local server configuration." }, statusCode: 403); }
        catch (OperationCanceledException) { return Results.Json(new { error = "Agent action cancelled." }, statusCode: 408); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
    }
}

public sealed class AgentGateway(IServiceProvider services, AgentJournal journal, AgentConnection policy)
{
    private readonly Dictionary<string, HashSet<string>> _experiments = new(StringComparer.Ordinal);
    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string> ExplorerActions = new(StringComparer.Ordinal)
    {
        ["inspect_type"] = "type", ["type_references"] = "type-references",
        ["inspect_method"] = "method", ["method_references"] = "method-references",
        ["scan"] = "scan", ["priorities"] = "priorities", ["test_inputs"] = "test-plan",
        ["binary_methods"] = "binary-methods", ["il"] = "il", ["build"] = "build",
        ["jit"] = "jit", ["trace"] = "trace", ["artifact"] = "result",
        ["native"] = "native", ["compare_native"] = "compare-native",
        ["runtime_costs"] = "runtime-costs", ["import_thread"] = "import-thread",
        ["run_companion"] = "run-companion"
    };
    private static readonly HashSet<string> OtherTools = new(["context", "sample", "open_project", "list_types", "rules", "note", "read_result",
        "prepare_experiment", "run_experiment", "experiment_result", "compare_benchmarks",
        "review_queue", "review_target", "review_history", "review_diff", "review_mark"], StringComparer.Ordinal);
    private static readonly HashSet<string> Fields = new(["snapshotId", "projectId", "typeId", "methodId", "assemblyId", "companionId", "artifactId",
        "baselineId", "nativeIndex", "baselineIndex", "profileIndex", "metadataToken", "assemblySha256", "approved", "allowRestore", "arguments",
        "sample", "path", "expectedSnapshotId", "query", "offset", "limit", "callId", "pointer", "kind", "message", "evidenceCallIds",
        "generatedCode", "targetFramework", "experimentId", "baselineExperimentId", "candidateExperimentId",
        "state", "targetId", "sourceHash", "contextHash", "expectedRevision", "status", "summary", "fromRevision", "toRevision"], StringComparer.Ordinal);

    public async Task<object> CallAsync(AgentCallRequest request, CancellationToken ct)
    {
        if (!ExplorerActions.ContainsKey(request.Tool) && !OtherTools.Contains(request.Tool)) throw new ArgumentException("Unknown agent tool.");
        if (request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.GetRawText().Length > 120000)
            throw new ArgumentException("Tool arguments must be a bounded JSON object.");
        if (request.Arguments.EnumerateObject().Any(p => !Fields.Contains(p.Name))) throw new ArgumentException("Unexpected tool argument. Arbitrary commands and paths are not accepted.");
        var (callId, cached) = journal.Start(request);
        if (cached is not null) return cached;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        try
        {
            var result = await Execute(request.SessionId, request.Tool, request.Arguments, deadline.Token);
            return journal.Complete(callId, result);
        }
        catch (OperationCanceledException) { return journal.Complete(callId, null, "Action cancelled or time budget reached.", true); }
        catch (Exception ex)
        {
            // Nothing is automatically retried. The journal reports operational failure, not a target-code defect.
            return journal.Complete(callId, null, ex.GetType().Name + ": " + (ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message));
        }
    }
    private void BuildPermission(JsonElement a)
    {
        if (!policy.AllowBuild || !Bool(a, "approved")) throw new UnauthorizedAccessException("Agent build/open is disabled. Enable TENKA_AGENT_ALLOW_BUILD on the server, then approve the specific tool in the agent client.");
    }
    private void ExecutionPermission(JsonElement a)
    {
        if (!policy.AllowExecution || !Bool(a, "approved")) throw new UnauthorizedAccessException("Agent execution is disabled. Enable TENKA_AGENT_ALLOW_EXECUTION on the server, then approve this invocation in the agent client.");
    }
    private async Task<object> Execute(string session, string tool, JsonElement a, CancellationToken ct)
    {
        var dashboard = Get<DashboardState>(); var guard = Get<WorkspaceGuard>();
        var discovery = Get<WorkspaceBinaryDiscovery>();
        if (ExplorerActions.TryGetValue(tool, out var action))
        {
            if (tool == "build") BuildPermission(a);
            if (tool is "jit" or "trace" or "run_companion") ExecutionPermission(a);
            if (tool == "run_companion") BuildPermission(a);
            var request = new ExplorerRequest(Str(a, "snapshotId"), action,
                ProjectId: Optional(a, "projectId"), TypeId: Optional(a, "typeId"), MethodId: Optional(a, "methodId"),
                AssemblyId: Optional(a, "assemblyId"), CompanionId: Optional(a, "companionId"), ArtifactId: Optional(a, "artifactId"),
                BaselineId: Optional(a, "baselineId"), NativeIndex: Number(a, "nativeIndex"), BaselineIndex: Number(a, "baselineIndex"),
                ProfileIndex: NullableNumber(a, "profileIndex"), MetadataToken: NullableNumber(a, "metadataToken"),
                AssemblySha256: Optional(a, "assemblySha256"), Approved: Bool(a, "approved"), AllowRestore: Bool(a, "allowRestore"),
                Arguments: Strings(a, "arguments"));
            return await ExplorerEndpoints.ExecuteAsync(request, dashboard, guard, discovery, Get<TrustedLocalJitRunner>(),
                Get<ExperimentStore>(), Get<AutonomousReviewRunner>(), ct);
        }
        switch (tool)
        {
            case "review_queue": return await Get<ReviewTrackingService>().Queue(Str(a,"snapshotId"), Optional(a,"projectId"),
                Optional(a,"kind")??"method", Optional(a,"state")??"pending", Optional(a,"query"),Number(a,"offset"),Number(a,"limit",50),ct);
            case "review_target": return await Get<ReviewTrackingService>().Target(Str(a,"snapshotId"),Str(a,"targetId"),ct);
            case "review_history": return await Get<ReviewTrackingService>().History(Str(a,"snapshotId"),Str(a,"targetId"),Number(a,"offset"),Number(a,"limit",50),ct);
            case "review_diff": return await Get<ReviewTrackingService>().Diff(Str(a,"snapshotId"),Str(a,"targetId"),Long(a,"fromRevision"),Long(a,"toRevision"),Number(a,"offset"),Number(a,"limit",150),ct);
            case "review_mark":
            {
                var attribution=journal.ReviewAttribution(session,Strings(a,"evidenceCallIds"));
                var request=new TenkaChan.Mcp.Tracking.ReviewMarkRequest(Str(a,"snapshotId"),Str(a,"targetId"),
                    Str(a,"sourceHash"),Str(a,"contextHash"),Long(a,"expectedRevision")??throw new ArgumentException("expectedRevision is required"),
                    Str(a,"status"),Str(a,"summary"));
                return await Get<ReviewTrackingService>().Mark(request,attribution.Agent,session,attribution.Evidence,ct);
            }
            case "context":
            {
                // An unloaded server is a usable context, not an instruction to send arbitrary paths repeatedly.
                var overview = JsonSerializer.SerializeToElement(dashboard.Overview(), Json);
                object? workspace = overview.GetProperty("loaded").GetBoolean()
                    ? await dashboard.ExplorerContextAsync(guard, discovery, ct) : null;
                return new { instanceId = journal.InstanceId, workspace, overview,
                    permissions = new { build = policy.AllowBuild, execution = policy.AllowExecution },
                    workflow = "Inspect context -> tenka_review_queue for pending/changed work -> enumerate types -> inspect method/references -> inspect IL -> request authorized execution -> read actual results -> report a qualified conclusion. Always refresh context after a build. Record checked/optimized checkpoints only after completing the work; these are agent reports, not verified speedups. No model endpoint is configured in Tenka." };
            }
            case "sample":
                return (Optional(a, "sample") ?? "game-lab") switch
                {
                    "game-lab" => (object)new BundledSampleCatalog(guard).FindGameLab(Get<IWebHostEnvironment>().ContentRootPath),
                    "raid-lab" => new RaidLabSampleCatalog(guard).Find(Get<IWebHostEnvironment>().ContentRootPath),
                    _ => throw new ArgumentException("Select game-lab or raid-lab. Arbitrary sample paths are not accepted.")
                };
            case "open_project":
                BuildPermission(a);
                return await dashboard.AgentOpenAsync(Str(a, "path"), Optional(a, "expectedSnapshotId"), ct);
            case "list_types":
                return await dashboard.ExploreAsync(Str(a, "snapshotId"), (index, _) => Task.FromResult((object)
                    index.Types(Optional(a, "projectId"), Optional(a, "query"), Number(a, "offset"), Number(a, "limit", 80))), ct);
            case "rules": return new { version = DeterministicRuleCatalog.Version, rules = DeterministicRuleCatalog.Rules };
            case "note":
                journal.Report(session, new AgentNote(Str(a, "kind"), Str(a, "message"), Strings(a, "evidenceCallIds")));
                return new { status = "agent_reported_unverified", note = "Displayed as an agent statement, not promoted to verified evidence." };
            case "read_result": return journal.ReadResult(session, Str(a, "callId"), Optional(a, "pointer") ?? "", Number(a, "offset"), Number(a, "limit", 30));
            case "prepare_experiment":
                return await dashboard.ExploreAsync(Str(a, "snapshotId"), (index, _) =>
                {
                    var kind = Str(a, "kind"); if (kind is not ("test" or "benchmark")) throw new ArgumentException("Generated experiment kind must be test or benchmark.");
                    var code = Str(a, "generatedCode"); if (code.Length > 40000) throw new ArgumentException("Generated code exceeds 40,000 characters.");
                    var project = index.Project(Str(a, "projectId"));
                    var result = Get<ExperimentStore>().Create(Path.GetDirectoryName(project.Path)!, Path.GetFileName(project.Path), kind, code, Optional(a, "targetFramework"), null);
                    var id = JsonSerializer.SerializeToElement(result, Json).GetProperty("id").GetString()!;
                    lock (_experiments)
                    {
                        var live = journal.Monitor(policy.Enabled, policy.AllowBuild, policy.AllowExecution).Sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                        foreach (var old in _experiments.Keys.Where(k => !live.Contains(k)).ToArray()) _experiments.Remove(old);
                        if (!_experiments.TryGetValue(session, out var ids)) _experiments[session] = ids = new(StringComparer.Ordinal);
                        ids.Add(id);
                    }
                    return Task.FromResult((object)result);
                }, ct);
            case "run_experiment":
                BuildPermission(a); ExecutionPermission(a); OwnExperiment(session, Str(a, "experimentId"));
                // The existing executor has its own generated-code permission flag and isolated work directory.
                return await Get<TrustedHostExperimentExecutor>().RunAsync(Str(a, "experimentId"), true, ct);
            case "experiment_result":
                OwnExperiment(session, Str(a, "experimentId")); return Get<ExperimentStore>().ReadExecutionResult(Str(a, "experimentId"));
            case "compare_benchmarks":
                OwnExperiment(session, Str(a, "baselineExperimentId")); OwnExperiment(session, Str(a, "candidateExperimentId"));
                return Get<BenchmarkRunComparer>().Compare(Str(a, "baselineExperimentId"), Str(a, "candidateExperimentId"));
            default: throw new ArgumentException("Unknown agent tool.");
        }
    }
    private void OwnExperiment(string session, string id)
    {
        lock (_experiments)
            if (!_experiments.TryGetValue(session, out var ids) || !ids.Contains(id))
                throw new UnauthorizedAccessException("Only experiments created in this agent session can be used.");
    }
    private static string Str(JsonElement a, string key) => Optional(a, key) ?? throw new ArgumentException("Missing field: " + key);
    private static string? Optional(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;
    private static long? Long(JsonElement a, string key) => a.TryGetProperty(key,out var p) && p.ValueKind!=JsonValueKind.Null ? p.GetInt64() : null;
    private static int Number(JsonElement a, string key, int fallback = 0) => NullableNumber(a, key) ?? fallback;
    private static int? NullableNumber(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetInt32() : null;
    private static bool Bool(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null && p.GetBoolean();
    private static string[]? Strings(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.Deserialize<string[]>() : null;
}
