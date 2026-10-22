using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

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
        { journal.Finish(request.SessionId); gateway.EndSession(request.SessionId); return Task.FromResult((object)new { status = "finished" }); }));
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

public sealed partial class AgentGateway(IServiceProvider services, AgentJournal journal, AgentConnection policy, AgentInvestigationStore? investigations = null)
{
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
    private static readonly HashSet<string> OtherTools = new(["investigation", "measure_target", "measure_companion", "approve_session", "revoke_approval", "session_status", "attach_workspace", "context", "sample", "open_project", "reload_workspace", "list_types", "rules", "note", "read_result",
        "prepare_experiment", "run_experiment", "experiment_result", "compare_benchmarks",
        "review_queue", "review_context", "review_focus", "review_target", "review_history", "review_diff", "review_mark"], StringComparer.Ordinal);
    private static readonly HashSet<string> Fields = new(["action", "investigationId", "tools", "minutes", "snapshotId", "projectId", "typeId", "methodId", "assemblyId", "companionId", "artifactId",
        "baselineId", "nativeIndex", "baselineIndex", "profileIndex", "metadataToken", "assemblySha256", "approved", "allowRestore", "arguments",
        "sample", "path", "expectedSnapshotId", "query", "offset", "limit", "callId", "pointer", "kind", "message", "evidenceCallIds",
        "generatedCode", "targetFramework", "runtimeFramework", "benchmarkJob", "experimentId", "baselineExperimentId", "candidateExperimentId",
        "cursor", "trackableOnly", "includeBinary", "state", "targetId", "sourceOffset", "sourceLimit", "sourceHash", "contextHash", "expectedRevision", "status", "summary", "fromRevision", "toRevision"], StringComparer.Ordinal);

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
        IDisposable? investigationLease = null;
        var entered = false;
        try
        {
            investigationLease = EnterInvestigation(request);
            entered = true;
            var result = await Execute(request.SessionId, request.Tool, request.Arguments, deadline.Token);
            return CompleteDurable(request, journal.Complete(callId, result));
        }
        catch (OperationCanceledException)
        {
            var reply = journal.Complete(callId, AgentRecoveryAdvice.ForFailure(request.Tool, null, cancelled: true),
                "Action cancelled or time budget reached.", true);
            return entered ? CompleteDurable(request, reply) : reply;
        }
        catch (Exception ex)
        {
            // Nothing is automatically retried. The journal reports operational failure, not a target-code defect.
            var error = ex.GetType().Name + ": " + (ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message);
            var reply = journal.Complete(callId, AgentRecoveryAdvice.ForFailure(request.Tool, error), error);
            return entered ? CompleteDurable(request, reply) : reply;
        }
        finally { investigationLease?.Dispose(); }
    }
    private void BuildPermission(JsonElement a) =>
        AgentPermissionGate.Require("build/open", "TENKA_AGENT_ALLOW_BUILD", policy.AllowBuild, Bool(a, "approved"));
    private async Task<object> Execute(string session, string tool, JsonElement a, CancellationToken ct)
    {
        var dashboard = Get<DashboardState>(); var guard = Get<WorkspaceGuard>();
        var discovery = Get<WorkspaceBinaryDiscovery>();
        if (ExplorerActions.TryGetValue(tool, out var action))
        {
            var approved = await ExplorerApprovalAsync(session, tool, a, ct);
            var request = new ExplorerRequest(Str(a, "snapshotId"), action,
                ProjectId: Optional(a, "projectId"), TypeId: Optional(a, "typeId"), MethodId: Optional(a, "methodId"),
                AssemblyId: Optional(a, "assemblyId"), CompanionId: Optional(a, "companionId"), ArtifactId: Optional(a, "artifactId"),
                BaselineId: Optional(a, "baselineId"), NativeIndex: Number(a, "nativeIndex"), BaselineIndex: Number(a, "baselineIndex"),
                ProfileIndex: NullableNumber(a, "profileIndex"), MetadataToken: NullableNumber(a, "metadataToken"),
                AssemblySha256: Optional(a, "assemblySha256"), Approved: approved, AllowRestore: Bool(a, "allowRestore"),
                Arguments: Strings(a, "arguments"));
            return await ExplorerEndpoints.ExecuteAsync(request, dashboard, guard, discovery, Get<TrustedLocalJitRunner>(),
                Get<ExperimentStore>(), Get<AutonomousReviewRunner>(), ct);
        }
        switch (tool)
        {
            case "investigation": return InvestigationAction(session, a);
            case "measure_target": return await MeasureTargetAsync(session, a, ct);
            case "measure_companion": return await MeasureCompanionAsync(session, a, ct);
            case "approve_session": return await ApproveSessionAsync(session, a, ct);
            case "revoke_approval": return new { status = "session_approval_revoked", revoked = _approvals.Revoke(session) };
            case "session_status": return SessionStatus(session);
            case "attach_workspace": return await dashboard.ExplorerContextAsync(guard, discovery, ct, Str(a, "path"));
            case "review_queue":
            {
                var kind = Optional(a, "kind") ?? ReviewQueueViews.DefaultKind;
                var state = Optional(a, "state") ?? "pending";
                // Reject kind=pending before a 14k-method review-catalog scan.
                ReviewQueueViews.ValidateFilters(kind, state);
                return await Get<ReviewTrackingService>().Queue(Str(a,"snapshotId"), Optional(a,"projectId"),
                    kind, state, Optional(a,"query"), Number(a,"offset"), Number(a,"limit",20), ct,
                    Optional(a,"cursor"), Bool(a,"trackableOnly"));
            }
            case "review_context": return await ReviewContextReader.ReadAsync(dashboard, guard, discovery,
                Get<ReviewProgressStore>(), Str(a,"snapshotId"), Str(a,"targetId"), Bool(a,"includeBinary",true), ct);
            case "review_focus": return await ReviewFocusReader.ReadAsync(dashboard, guard,
                Get<ReviewProgressStore>(), Str(a,"snapshotId"), Str(a,"targetId"),
                Number(a,"sourceOffset"), Number(a,"sourceLimit", ReviewFocusedSlices.DefaultSourceLimit), ct);
            case "review_target": return await Get<ReviewTrackingService>().Target(Str(a,"snapshotId"),Str(a,"targetId"),ct);
            case "review_history": return await Get<ReviewTrackingService>().History(Str(a,"snapshotId"),Str(a,"targetId"),Number(a,"offset"),Number(a,"limit",50),ct);
            case "review_diff": return await Get<ReviewTrackingService>().Diff(Str(a,"snapshotId"),Str(a,"targetId"),Long(a,"fromRevision"),Long(a,"toRevision"),Number(a,"offset"),Number(a,"limit",150),ct);
            case "review_mark":
            {
                var attribution=journal.ReviewAttribution(session, null);
                var evidence=ResolveEvidence(session,Strings(a,"evidenceCallIds"));
                var request=new TenkaChan.Mcp.Tracking.ReviewMarkRequest(Str(a,"snapshotId"),Str(a,"targetId"),
                    Str(a,"sourceHash"),Str(a,"contextHash"),Long(a,"expectedRevision")??throw new ArgumentException("expectedRevision is required"),
                    Str(a,"status"),Str(a,"summary"));
                return await Get<ReviewTrackingService>().Mark(request,attribution.Agent,session,evidence,ct, target => MeasurementFor(session,a,target));
            }
            case "context":
            {
                // An unloaded server is a usable context, not an instruction to send arbitrary paths repeatedly.
                var overview = JsonSerializer.SerializeToElement(dashboard.Overview(), Json);
                object? workspace = overview.GetProperty("loaded").GetBoolean()
                    ? await dashboard.ExplorerContextAsync(guard, discovery, ct) : null;
                return new { instanceId = journal.InstanceId, workspace, overview,
                    permissions = new { build = policy.AllowBuild, execution = policy.AllowExecution },
                    session = SessionStatus(session),
                    selectionIds = "typeId/methodId/projectId are ephemeral to the current loaded snapshot. Requery tenka_context/list_types after reload; durable review targetId comes from tenka_review_queue and is resolved by tenka_review_context.",
                    measurementWorkflow = "If workspace.companions has no matching BenchmarkDotNet companion (e.g. partial ILSpy solution), use prepare_experiment(kind=benchmark) -> measure_target directly on an unexecuted experiment. Explicit approval is always required.",
                    workflow = "Inspect tenka_context -> tenka_review_queue(kind=all,state=pending) -> tenka_review_focus(current snapshotId, durable targetId) for tracked source, callers/callees, bounded CFG and mark hashes in ONE call. Continue sourceOffset only if sourceHasMore. Use tenka_review_context(includeBinary=true) only for IL/binary evidence and exact overload selection. If loaded files changed, explicitly approve tenka_reload_workspace and refresh snapshotId; never infer approvals. Use tenka_approve_session only for user-agreed repeated operations. Use tenka_attach_workspace to reuse the workspace without reopening. Keep checked/optimized as agent work records, NOT proven optimizations. For measured benchmarks use prepare_experiment -> measure_target directly. No model endpoint is configured in Tenka." };
            }
            case "sample":
                return (Optional(a, "sample") ?? "game-lab") switch
                {
                    "game-lab" => (object)new BundledSampleCatalog(guard).FindGameLab(Get<IWebHostEnvironment>().ContentRootPath),
                    "raid-lab" => new RaidLabSampleCatalog(guard).Find(Get<IWebHostEnvironment>().ContentRootPath),
                    "ilspy" => new LargeOssSampleCatalog(guard).Find(Get<IWebHostEnvironment>().ContentRootPath),
                    _ => throw new ArgumentException("Select game-lab, raid-lab or ilspy. Arbitrary sample paths are not accepted.")
                };
            case "open_project":
                BuildPermission(a);
                return await dashboard.AgentOpenAsync(Str(a, "path"), Optional(a, "expectedSnapshotId"), ct);
            case "reload_workspace":
                // Same UNSANDBOXED MSBuild trust and per-call approval as open.
                // The loaded path is chosen by the server, never the caller.
                BuildPermission(a);
                return await dashboard.AgentReloadAsync(Str(a, "snapshotId"), ct);
            case "list_types":
                return await dashboard.ExploreAsync(Str(a, "snapshotId"), (index, _) => Task.FromResult((object)
                    index.Types(Optional(a, "projectId"), Optional(a, "query"), Number(a, "offset"), Number(a, "limit", 80))), ct, waitForRead: true);
            case "rules": return new { version = DeterministicRuleCatalog.Version, rules = DeterministicRuleCatalog.Rules };
            case "note":
            {
                var evidence = ResolveEvidence(session, Strings(a, "evidenceCallIds"));
                journal.Report(session, new AgentNote(Str(a, "kind"), Str(a, "message")));
                return new { status = "agent_reported_unverified", evidence,
                    note = "Agent statement, not verified evidence. Referenced completed calls may belong to the resumed investigation." };
            }
            case "read_result": return ReadInvestigationResult(session, a);
            case "prepare_experiment":
                return await dashboard.ExploreAsync(Str(a, "snapshotId"), (index, _) =>
                {
                    var kind = Str(a, "kind"); if (kind is not ("test" or "benchmark")) throw new ArgumentException("Generated experiment kind must be test or benchmark.");
                    var code = Str(a, "generatedCode"); if (code.Length > 40000) throw new ArgumentException("Generated code exceeds 40,000 characters.");
                    var project = index.Project(Str(a, "projectId"));
                    var result = Get<ExperimentStore>().Create(Path.GetDirectoryName(project.Path)!, Path.GetFileName(project.Path), kind, code, Optional(a, "targetFramework"), null, Optional(a, "runtimeFramework"));
                    var id = JsonSerializer.SerializeToElement(result, Json).GetProperty("id").GetString()!;
                    Investigations.AddExperiment(session, id, project.Path);
                    return Task.FromResult((object)(result with { InvestigationId = Investigations.CurrentId(session) }));
                }, ct);
            case "run_experiment":
                _approvals.Authorize(session, OwnExperiment(session, Str(a, "experimentId")), tool,
                    Bool(a, "approved"), policy.AllowBuild, policy.AllowExecution);
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
    private string OwnExperiment(string session, string id)
    {
        var project = Investigations.RequireExperiment(session, id);
        var manifest = Get<ExperimentStore>().Read(id).Manifest;
        if (!AgentInvestigationStore.SameProject(project, Path.Combine(manifest.RepositoryRoot, manifest.ProjectRelativePath)))
            throw new UnauthorizedAccessException("Experiment project does not match its saved investigation provenance.");
        return project;
    }
    private static string Str(JsonElement a, string key) => Optional(a, key) ?? throw new ArgumentException("Missing field: " + key);
    private static string? Optional(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;
    private static long? Long(JsonElement a, string key) => a.TryGetProperty(key,out var p) && p.ValueKind!=JsonValueKind.Null ? p.GetInt64() : null;
    private static int Number(JsonElement a, string key, int fallback = 0) => NullableNumber(a, key) ?? fallback;
    private static int? NullableNumber(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetInt32() : null;
    private static bool Bool(JsonElement a, string key, bool fallback = false) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetBoolean() : fallback;
    private static string[]? Strings(JsonElement a, string key) => a.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.Deserialize<string[]>() : null;
}