using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Web;

public sealed record ExplorerArtifact(string Id, string SnapshotId, string Kind, string Label,
    string CreatedUtc, object Data);

public sealed partial class DashboardState
{
    private readonly Dictionary<string, ExplorerArtifact> _explorerResults = new(StringComparer.Ordinal);

    // A single gate covers selection resolution AND the complete operation. No path or
    // stale method ID can be substituted by opening a different workspace mid-operation.
    public async Task<object> ExploreAsync(string? expectedSnapshot,
        Func<SourceExplorerIndex, TenkaChan.Mcp.Analysis.RoslynReviewService, Task<object>> operation,
        CancellationToken ct, bool waitForRead = false)
    {
        if (!await AgentWorkspaceAccess.EnterAsync(_gate, waitForRead, ct))
            throw new WorkspaceBuildConflictException("別の処理を実行中です。完了後にもう一度操作してください。追加の実行は予約しません。");
        try
        {
            EnsureOpen();
            if (expectedSnapshot is not null && expectedSnapshot != _solution!.SnapshotId)
                throw new WorkspaceBuildConflictException("プロジェクトが更新されました。『状態を更新』を押して対象を選び直してください。");
            var index = await roslyn.ExplorerAsync(ct);
            return await operation(index, roslyn);
        }
        finally { _gate.Release(); }
    }

    public Task<object> ExplorerContextAsync(WorkspaceGuard guard, WorkspaceBinaryDiscovery discovery, CancellationToken ct,
        string? expectedPath = null) =>
        ExploreAsync(null, (index, _) =>
        {
            if (expectedPath is not null && !AgentWorkspaceAccess.MatchesLoadedPath(expectedPath, _path))
                throw new WorkspaceBuildConflictException("The requested workspace is not the loaded workspace. Read tenka_context; attach does not open or replace a workspace.");
            return Task.FromResult((object)new
        {
            snapshotId = index.SnapshotId, path = _path, projects = index.Projects,
            assemblies = ExplorerProjectActions.Assemblies(index, discovery),
            companions = ExplorerProjectActions.Companions(index, guard),
            capabilities = new
            {
                build = guard.TrustMsBuild && Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1",
                localExecution = Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1",
                trace = Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1"
                    && File.Exists(Environment.GetEnvironmentVariable("TENKA_DOTNET_TRACE_PATH"))
            },
            scanned = _deterministic is not null,
            summary = _deterministic?.Summary,
            history = _explorerResults.Values.Where(r => r.SnapshotId == index.SnapshotId)
                .OrderByDescending(r => r.CreatedUtc).Select(r => new { r.Id, r.Kind, r.Label, r.CreatedUtc }).ToArray(),
            note = "対象は読み込み済みのスナップショットです。クラス一覧は参照のみ。実行・ビルドはボタンを押して承認した時だけ行います。"
        });
        }, ct, waitForRead: true);

    public ExplorerArtifact RememberExplorerResult(SourceExplorerIndex index, string kind, string label, object result)
    {
        // Called only while ExploreAsync holds the session gate. Results are session-local,
        // bounded and not reusable against another source snapshot.
        var item = new ExplorerArtifact(Guid.NewGuid().ToString("N"), index.SnapshotId, kind,
            label, DateTimeOffset.UtcNow.ToString("O"), result);
        _explorerResults.Add(item.Id, item);
        while (_explorerResults.Count > 20)
            _explorerResults.Remove(_explorerResults.Values.OrderBy(r => r.CreatedUtc).First().Id);
        return item;
    }
    public ExplorerArtifact ExplorerResult(SourceExplorerIndex index, string? id, string? kind = null)
    {
        if (id is null || !_explorerResults.TryGetValue(id, out var result) || result.SnapshotId != index.SnapshotId
            || kind is not null && result.Kind != kind)
            throw new WorkspaceBuildConflictException("このプロジェクトの取得結果ではありません。結果一覧から選び直してください。");
        return result;
    }

    public async Task<object> ExplorerScanAsync(CancellationToken ct)
    {
        // Caller owns _gate. Publish only the complete bundle, never a mix of stages.
        var findings = (await roslyn.ScanAsync(5000, ct)).ToArray();
        var diagnostics = await roslyn.GetCompilationDiagnosticsAsync(400, ct);
        var deterministic = await roslyn.AnalyzeDeterministicAsync(5000, 2000, ct);
        var hotspots = await roslyn.AnalyzePerformanceHotspotsAsync(300, 1500, ct);
        _findings = findings; _diagnostics = diagnostics; _deterministic = deterministic; _hotspots = hotspots;
        _lastScanned = DateTimeOffset.UtcNow;
        return new { summary = deterministic.Summary, findings = deterministic.Findings,
            methods = deterministic.MethodMetrics, hotspots, note = "静的な調査候補です。実測時間やバグの確定ではありません。表示上限はsummaryを確認してください。" };
    }

    public void InvalidateExplorerReports()
    {
        // The companion can rebuild the target even if execution/report parsing fails.
        // Fail closed: no old source snapshot remains advertised as current.
        InvalidatePreparedWorkspace();
        roslyn.ClearExplorerMeasurement();
    }

    public async Task<SourceExplorerIndex> ReloadExplorerWorkspaceAsync(CancellationToken ct)
    {
        var path = _path ?? throw new InvalidOperationException("読み込み済みプロジェクトがありません。");
        InvalidatePreparedWorkspace();
        _solution = await roslyn.OpenAsync(path, ct);
        _lastOpened = DateTimeOffset.UtcNow;
        return await roslyn.ExplorerAsync(ct);
    }

    public object ExplorerTypeFindings(ExplorerTypeDetails type)
    {
        var methods = type.Methods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return new { scanned = _deterministic is not null,
            findings = _deterministic?.Findings.Where(f => f.MethodId is not null && methods.Contains(f.MethodId)).ToArray() ?? [],
            limited = _deterministic?.Summary.FindingsTruncated ?? false,
            note = "この型の索引済みメソッドに対応した指摘のみです。フィールド初期化などメソッド未対応の指摘は全体結果で確認してください。" };
    }
}
