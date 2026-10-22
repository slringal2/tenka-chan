using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public sealed record ExplorerRequest(string SnapshotId, string Action, string? ProjectId = null,
    string? TypeId = null, string? MethodId = null, string? AssemblyId = null, string? CompanionId = null,
    string? ArtifactId = null, string? BaselineId = null, int NativeIndex = 0, int BaselineIndex = 0,
    int? ProfileIndex = null, int? MetadataToken = null, string? AssemblySha256 = null,
    bool Approved = false, bool AllowRestore = false, string[]? Arguments = null);

public static class ExplorerEndpoints
{
    public static void MapExplorerEndpoints(this WebApplication app)
    {
        app.MapGet("/project", () => Results.Redirect("/project.html"));
        app.MapGet("/api/explorer/context", (DashboardState dashboard, WorkspaceGuard guard,
            WorkspaceBinaryDiscovery discovery, CancellationToken ct) => Safe(app,
                () => dashboard.ExplorerContextAsync(guard, discovery, ct)));
        app.MapGet("/api/explorer/types", (string snapshotId, string? projectId, string? q, int? offset,
            DashboardState dashboard, CancellationToken ct) => Safe(app, () =>
                dashboard.ExploreAsync(snapshotId, (index, _) => Task.FromResult((object)
                    index.Types(projectId, q, offset ?? 0)), ct)));
        app.MapPost("/api/explorer/action", (ExplorerRequest request, DashboardState dashboard,
            WorkspaceGuard guard, WorkspaceBinaryDiscovery discovery, TrustedLocalJitRunner jit,
            ExperimentStore experiments, AutonomousReviewRunner review, CancellationToken ct) => Safe(app, () =>
            ExecuteAsync(request, dashboard, guard, discovery, jit, experiments, review, ct)));
    }

    public static async Task<object> ExecuteAsync(ExplorerRequest request, DashboardState dashboard,
        WorkspaceGuard guard, WorkspaceBinaryDiscovery discovery, TrustedLocalJitRunner jit,
        ExperimentStore experiments, AutonomousReviewRunner review, CancellationToken ct)
    {
            if (string.IsNullOrWhiteSpace(request.SnapshotId)) throw new ArgumentException("状態を更新してから操作してください。");
            if (review.IsActive) throw new WorkspaceBuildConflictException("AIレビューの完了または停止を待ってください。");
            if (request.Action == "build")
            {
                // Resolve the opaque project ID first, then let the existing build service
                // reacquire the gate and recheck the SAME snapshot before executing MSBuild.
                var projectPath = (string)await dashboard.ExploreAsync(request.SnapshotId,
                    (index, _) => Task.FromResult((object)index.Project(Required(request.ProjectId)).Path), ct);
                return await dashboard.BuildLoadedProjectAsync(new(request.SnapshotId, projectPath,
                    AllowRestore: request.AllowRestore, ApproveUnisolatedBuild: request.Approved),
                    new TrustedWorkspaceBuilder(guard), discovery, ct);
            }
            return await dashboard.ExploreAsync(request.SnapshotId, async (index, roslyn) =>
            {
                ExplorerMethod Method() => index.Method(Required(request.MethodId));
                ExplorerAssembly Assembly(bool runnable = false)
                {
                    var selected = ExplorerProjectActions.Assemblies(index, discovery)
                        .SingleOrDefault(a => a.Id == request.AssemblyId)
                        ?? throw new WorkspaceBuildConflictException("ビルド出力を選んでください。見つからない場合はReleaseビルドを実行してください。");
                    if (runnable && !selected.Runnable) throw new InvalidOperationException("このDLL単体は起動できません。呼び出し元のアプリを一覧から選んでください。");
                    return selected;
                }
                void Approve()
                {
                    if (!request.Approved || Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") != "1")
                        throw new UnauthorizedAccessException("隔離なしのローカル実行への承認と、有効な起動設定が必要です。");
                }
                switch (request.Action)
                {
                    case "type":
                    {
                        var detail = await index.TypeAsync(Required(request.TypeId), ct);
                        return new { detail, findings = dashboard.ExplorerTypeFindings(detail) };
                    }
                    case "type-references": return await index.ReferencesAsync(Required(request.TypeId), true, ct);
                    case "method-references": return await index.ReferencesAsync(Method().Id, false, ct);
                    case "method": return new {
                        method = Method(), inspection = await roslyn.InspectAsync(Method().Id, true, ct),
                        selectionScope = "current_loaded_roslyn_snapshot_only",
                        note = "methodId is ephemeral. Keep snapshotId and current Web instance; durable targetId is a separate key resolved via tenka_review_context."
                    };
                    case "test-plan": return roslyn.SuggestTestPlan(Method().Id);
                    case "scan": return await dashboard.ExplorerScanAsync(ct);
                    case "priorities": return await new ReviewPriorityService(roslyn).BuildAsync(new(), ct);
                    case "binary-methods":
                    case "il":
                    {
                        var method = Method();
                        var assembly = Assembly();
                        if (assembly.ProjectId != method.ProjectId) throw new ArgumentException("ILには調査対象メソッドのプロジェクトのDLLを選んでください。");
                        var inspector = new BinarySourceInspector(guard);
                        var catalog = inspector.Methods(assembly.AssemblyPath, method.MetadataType + "::" + method.MetadataName, 2000);
                        var matches = catalog.Methods.Where(m => m.Type == method.MetadataType && m.Name == method.MetadataName).ToArray();
                        if (request.Action == "binary-methods") return new { catalog.AssemblySha256, methods = matches,
                            catalog.Truncated, note = "型名・メソッド名で対応するバイナリ候補です。オーバーロードは署名を選びます。ソースと同じビルドである証明ではありません。" };
                        if (request.AssemblySha256 != catalog.AssemblySha256)
                            throw new WorkspaceBuildConflictException("DLLが変更されました。ILの候補を更新してください。");
                        var selected = matches.SingleOrDefault(m => m.MetadataToken == request.MetadataToken)
                            ?? throw new ArgumentException("一覧からバイナリの署名を選んでください。");
                        var result = inspector.Inspect(assembly.AssemblyPath, selected.MetadataToken,
                            sourceFilePath: method.File, maxInstructions: 2000); // Neighbor PDB is discovered by the existing inspector.
                        if (result.AssemblySha256 != catalog.AssemblySha256)
                            throw new WorkspaceBuildConflictException("読み取り中にDLLが変更されました。結果は採用していません。");
                        return result;
                    }
                    case "jit":
                    {
                        Approve(); var method = Method(); var assembly = Assembly(true);
                        if (method.JitFilter is null) throw new InvalidOperationException(method.JitUnavailableReason);
                        var result = await jit.CaptureAsync(assembly.AssemblyPath, method.JitFilter, true, 60, request.Arguments, ct);
                        // Use the bounded saved representation so the method picker and later decoder agree.
                        return dashboard.RememberExplorerResult(index, "jit", method.Signature, jit.Get(result.Id));
                    }
                    case "trace":
                    {
                        Approve(); var assembly = Assembly(true);
                        var output = ExplorerProjectActions.EnsureOutputDirectory(index.Project(assembly.ProjectId).Path, guard, "profiles");
                        var result = await new TrustedTraceCapture(guard).CaptureAsync(assembly.AssemblyPath, output,
                            true, 90, request.Arguments, ct, runtimeCostEvents: true);
                        return dashboard.RememberExplorerResult(index, "trace", assembly.Project, result);
                    }
                    case "result": return dashboard.ExplorerResult(index, request.ArtifactId);
                    case "native":
                    {
                        var captured = (LocalJitCaptureResult)dashboard.ExplorerResult(index, request.ArtifactId, "jit").Data;
                        return new AssemblyWorkbench(guard, experiments, jit).Analyze(new(LocalJitCaptureId: captured.Id, MethodIndex: request.NativeIndex));
                    }
                    case "compare-native":
                    {
                        var left = (LocalJitCaptureResult)dashboard.ExplorerResult(index, request.BaselineId, "jit").Data;
                        var right = (LocalJitCaptureResult)dashboard.ExplorerResult(index, request.ArtifactId, "jit").Data;
                        return new AssemblyWorkbench(guard, experiments, jit).Compare(
                            new(LocalJitCaptureId: left.Id, MethodIndex: request.BaselineIndex),
                            new(LocalJitCaptureId: right.Id, MethodIndex: request.NativeIndex));
                    }
                    case "runtime-costs":
                    {
                        var capture = (TraceCaptureReport)dashboard.ExplorerResult(index, request.ArtifactId, "trace").Data;
                        return await new RuntimeCostReader(guard).ReadAsync(capture.ReportPath, null, new(500000, 100, null), ct);
                    }
                    case "import-thread":
                    {
                        var capture = (TraceCaptureReport)dashboard.ExplorerResult(index, request.ArtifactId, "trace").Data;
                        if (request.ProfileIndex is not int profile) throw new ArgumentException("計測したスレッドを選んでください。");
                        var service = new TrustedTraceCapture(guard);
                        var verified = service.Read(capture.ReportPath);
                        var file = service.RequireProfile(verified, profile);
                        // Caller holds dashboard gate: do not reacquire via dashboard.ImportHotspotProfileAsync.
                        return await roslyn.ImportHotspotProfileAsync(file, profile, 150, ct);
                    }
                    case "run-companion":
                    {
                        Approve();
                        var companion = ExplorerProjectActions.Companions(index, guard).SingleOrDefault(c => c.Id == request.CompanionId)
                            ?? throw new WorkspaceBuildConflictException("読み込み済みのテストまたはベンチマークを選んでください。");
                        // Building a companion can rebuild its referenced target. Invalidate source reports first.
                        dashboard.InvalidateExplorerReports();
                        var result = await ExplorerProjectActions.RunAsync(companion, guard, true, request.AllowRestore, ct);
                        try
                        {
                            var fresh = await dashboard.ReloadExplorerWorkspaceAsync(ct);
                            return dashboard.RememberExplorerResult(fresh, companion.Kind, companion.Name,
                                result with { Note = result.Note + " ビルド後のソースを再読み込みしました。静的解析は再実行してください。" });
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
                        {
                            return dashboard.RememberExplorerResult(index, companion.Kind, companion.Name,
                                result with { Note = result.Note + " ソースの再読み込みは完了していません。最初のプロジェクト欄から再読み込みしてください。" });
                        }
                    }
                    default: throw new ArgumentException("対応していない操作です。");
                }
            }, ct);
    }
    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value
        : throw new ArgumentException("調査する対象を一覧から選んでください。");
    private static async Task<IResult> Safe(WebApplication app, Func<Task<object>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (WorkspaceBuildConflictException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        catch (OperationCanceledException) { return Results.Json(new { error = "処理が中止されました。対象アプリの終了はベストエフォートです。" }, statusCode: 408); }
        catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or KeyNotFoundException or BadImageFormatException or JsonException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex)
        { app.Logger.LogError(ex, "Project explorer action failed"); return Results.Json(new { error = "処理に失敗しました。甜花ちゃんのログを確認してください。" }, statusCode: 500); }
    }
}
