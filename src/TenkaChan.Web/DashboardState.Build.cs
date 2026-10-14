using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;

namespace TenkaChan.Web;

public sealed record WorkspaceBuildOptions(bool Loaded, bool BuildEnabled, string? Path,
    string? SnapshotId, IReadOnlyList<ProjectSummary> Projects);
public sealed record BuildWorkspaceRequest(string ExpectedSnapshotId, string ProjectPath,
    string? TargetFramework = null, bool AllowRestore = false,
    bool ApproveUnisolatedBuild = false, bool AnalyzeAfterBuild = true, int TimeoutSeconds = 180);
public sealed record WorkspaceAnalysisSummary(int Findings, int CompilerErrors,
    int DeterministicFindings, int HotspotCandidates);
public sealed record WorkspaceBuildResponse(WorkspaceBuildResult Build, bool WorkspaceReloaded,
    string? SnapshotId, IReadOnlyList<WorkspaceAssemblyChoice> Assemblies,
    WorkspaceAnalysisSummary? Analysis, string Note);
public sealed class WorkspaceBuildConflictException(string message) : InvalidOperationException(message) { }

public sealed partial class DashboardState
{
    private static readonly StringComparer BuildPaths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<WorkspaceBuildOptions> BuildOptionsAsync(bool enabled, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            throw new WorkspaceBuildConflictException("Workspace is busy; retry after the current operation.");
        try
        {
            var projects = _solution?.Projects
                .Where(p => p.Path is not null && p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Path!, BuildPaths).Select(g => g.First()).ToArray() ?? [];
            return new(_solution is not null, enabled, _path, _solution?.SnapshotId, projects);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceBuildResponse> BuildLoadedProjectAsync(BuildWorkspaceRequest request,
        TrustedWorkspaceBuilder builder, WorkspaceBinaryDiscovery discovery, CancellationToken ct)
    {
        // Do not queue a second click that could repeat arbitrary MSBuild side effects.
        if (!await _gate.WaitAsync(0, ct))
            throw new WorkspaceBuildConflictException("Workspace is busy. No build was queued.");
        try
        {
            EnsureOpen();
            if (!string.Equals(request.ExpectedSnapshotId, _solution!.SnapshotId, StringComparison.Ordinal))
                throw new WorkspaceBuildConflictException("The opened workspace changed. Refresh the project choices before building.");
            var project = builder.Validate(request.ProjectPath, request.TargetFramework,
                request.ApproveUnisolatedBuild, request.TimeoutSeconds);
            if (!_solution.Projects.Any(p => p.Path is not null && BuildPaths.Equals(p.Path, project)))
                throw new UnauthorizedAccessException("Choose a project from the currently opened workspace.");
            var workspacePath = _path!;

            // Even a failed build can replace DLLs or generate source. Do not keep old
            // source/profile associations or advertise yesterday's report as the new result.
            InvalidatePreparedWorkspace();
            var result = await builder.BuildAsync(project, request.TargetFramework, request.AllowRestore,
                request.ApproveUnisolatedBuild, request.TimeoutSeconds, ct);
            if (result.Status != "build_succeeded")
            {
                var log = result.Output + "\n" + result.Stderr;
                var missingAssets = result.Status == "build_failed" &&
                    (log.Contains("NETSDK1004", StringComparison.OrdinalIgnoreCase) ||
                     log.Contains("project.assets.json", StringComparison.OrdinalIgnoreCase));
                var message = missingAssets
                    ? "NuGetの資産ファイルが見つかりません。プロジェクトを再読み込みし、NuGetの復元を許可してビルドしてください。復元済みでも続く場合は復元ログ・対象フレームワーク・obj出力先を確認してください。"
                    : "ビルドは完了していません。以前の解析結果を無効化しました。古いDLLは自動選択しません。プロジェクトを再読み込みしてから再試行してください。";
                return new(result, false, null, [], null, message);
            }

            bool reloaded = false;
            IReadOnlyList<WorkspaceAssemblyChoice> assemblies = [];
            WorkspaceAnalysisSummary? analysis = null;
            try
            {
                // Keep the same session gate through reload and optional static scans.
                // Calling OpenAsync/ScanAsync here would acquire the gate a second time.
                _solution = await roslyn.OpenAsync(workspacePath, ct);
                _lastOpened = DateTimeOffset.UtcNow;
                reloaded = true;
                assemblies = discovery.Discover(_solution.Projects);
                if (request.AnalyzeAfterBuild)
                {
                    var findings = (await roslyn.ScanAsync(2000, ct)).ToArray();
                    var diagnostics = await roslyn.GetCompilationDiagnosticsAsync(400, ct);
                    var deterministic = await roslyn.AnalyzeDeterministicAsync(500, 100, ct);
                    var hotspots = await roslyn.AnalyzePerformanceHotspotsAsync(100, 300, ct);
                    // Commit the combined static report only after every stage succeeded.
                    _findings = findings;
                    _diagnostics = diagnostics;
                    _deterministic = deterministic;
                    _hotspots = hotspots;
                    _lastScanned = DateTimeOffset.UtcNow;
                    analysis = new(findings.Length, diagnostics.ErrorCount,
                        deterministic.Summary.TotalFindings, hotspots.TotalSites);
                }
                return new(result, true, _solution.SnapshotId, assemblies, analysis,
                    "Workspace refreshed. DLLs are discovered candidates, not verified source/build identity. Static findings are investigation candidates, not measured performance. No target application, JIT capture, tests or benchmarks were launched.");
            }
            catch (OperationCanceledException)
            {
                return new(result, reloaded, _solution?.SnapshotId, assemblies, null,
                    "Build succeeded, but workspace reload or static analysis was cancelled. No completed combined analysis report is available.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                or UnauthorizedAccessException or InvalidDataException or BadImageFormatException)
            {
                var message = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
                return new(result, reloaded, _solution?.SnapshotId, assemblies, null,
                    "Build succeeded, but workspace reload or analysis failed: " + message);
            }
        }
        finally { _gate.Release(); }
    }

    private void InvalidatePreparedWorkspace()
    {
        _solution = null;
        _findings = [];
        _diagnostics = null;
        _deterministic = null;
        _hotspots = null;
        _lastOpened = null;
        _lastScanned = null;
        // Retain _path only to help the operator reopen after a build/reload failure.
    }
}
