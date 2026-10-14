using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;

namespace TenkaChan.Web;

public sealed record WorkspaceJitMethodChoice(string MethodId, string Project,
    string Signature, string JitFilter);
public sealed record WorkspaceQuickConfig(string SnapshotId,
    IReadOnlyList<WorkspaceAssemblyChoice> Assemblies,
    IReadOnlyList<WorkspaceJitMethodChoice> JitMethods);

public sealed record FindingsPage(int Total, int Offset, int Limit, IReadOnlyList<RuleFinding> Items);

/// <summary>
/// Per-process Roslyn session and immutable scan snapshot. All calls into
/// the Roslyn workspace are serialized because workspace_open replaces its
/// underlying symbol index; concurrent reads during that operation are unsafe.
/// </summary>
public sealed class DashboardState(RoslynReviewService roslyn) : IReviewContextSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SolutionSummary? _solution;
    private RuleFinding[] _findings = [];
    private CompilationDiagnosticsReport? _diagnostics;
    private DeterministicAnalysisReport? _deterministic;
    private StaticHotspotReport? _hotspots;
    private string? _path;
    private DateTimeOffset? _lastOpened;
    private DateTimeOffset? _lastScanned;

    public object Overview()
    {
        var snapshot = _solution;
        var findings = _findings;
        return new
        {
            loaded = snapshot is not null,
            path = _path,
            snapshotId = snapshot?.SnapshotId,
            projectCount = snapshot?.Projects.Count ?? 0,
            methodCount = snapshot?.MethodCount ?? 0,
            projects = snapshot?.Projects ?? [],
            warnings = snapshot?.WorkspaceWarnings ?? [],
            findingCount = findings.Length,
            findingsByCategory = findings
                .GroupBy(x => x.Category)
                .OrderByDescending(x => x.Count())
                .Select(x => new { category = x.Key, count = x.Count() }).ToArray(),
            diagnosticErrors = _diagnostics?.ErrorCount ?? 0,
            diagnosticWarnings = _diagnostics?.WarningCount ?? 0,
            deterministicFindingCount = _deterministic?.Summary.TotalFindings ?? 0,
            deterministicComplete = _deterministic is not null,
            hotspotCandidates = _hotspots?.TotalSites ?? 0,
            hotspotMeasuredFrames = roslyn.CurrentHotspotProfile?.LinkedFrames ?? 0,
            scanned = _lastScanned is not null,
            lastOpened = _lastOpened,
            lastScanned = _lastScanned
        };
    }

    public WorkspaceQuickConfig QuickConfig(WorkspaceBinaryDiscovery discovery)
    {
        var opened = _solution ?? throw new InvalidOperationException(
            "Open a trusted .sln/.csproj before preparing a one-click analysis.");
        return new WorkspaceQuickConfig(opened.SnapshotId,
            discovery.Discover(opened.Projects), roslyn.ListJitMethods());
    }

    public async Task<SolutionSummary> OpenAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // RoslynService validates both the allowlist and explicit MSBuild trust.
            _solution = null;
            _findings = [];
            _diagnostics = null;
            _deterministic = null;
            _hotspots = null;
            _path = null;
            _lastOpened = null;
            _lastScanned = null;
            var result = await roslyn.OpenAsync(path, ct);
            _solution = result;
            _path = Path.GetFullPath(path);
            _lastOpened = DateTimeOffset.UtcNow;
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<object> ScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            var findings = (await roslyn.ScanAsync(2000, ct)).ToArray();
            var diagnostics = await roslyn.GetCompilationDiagnosticsAsync(400, ct);
            _findings = findings;
            _diagnostics = diagnostics;
            _lastScanned = DateTimeOffset.UtcNow;
            return new { findingCount = findings.Length,
                diagnosticErrors = diagnostics.ErrorCount,
                diagnosticWarnings = diagnostics.WarningCount,
                scannedAt = _lastScanned };
        }
        finally { _gate.Release(); }
    }

    public async Task<PerformanceTriageReport> TriageEvidenceAsync(
        PerformanceEvidenceService service, string? jitExperimentId,
        int maxObserved, int maxStatic, string? benchmarkExperimentId,
        string? localJitCaptureId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            return await service.TriageAsync(jitExperimentId,
                maxObserved, maxStatic, benchmarkExperimentId, localJitCaptureId, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<PerformanceMethodDossier> InvestigateEvidenceAsync(
        PerformanceEvidenceService service, string methodId,
        string? jitExperimentId, string? assemblyFile, int? methodToken,
        string? sourceFilePath, int maxInstructions,
        string? localJitCaptureId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            return await service.InvestigateAsync(methodId,
                jitExperimentId, assemblyFile, methodToken, sourceFilePath,
                maxInstructions, localJitCaptureId, ct);
        }
        finally { _gate.Release(); }
    }

    public StaticHotspotReport? StaticHotspotReport => _hotspots;
    public SampledHotspotReport? MeasuredHotspotReport =>
        roslyn.CurrentHotspotProfile;

    public async Task<StaticHotspotReport> ScanHotspotsAsync(
        int maxMethods, int maxSites, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            _hotspots = await roslyn.AnalyzePerformanceHotspotsAsync(
                maxMethods, maxSites, ct);
            return _hotspots;
        }
        finally { _gate.Release(); }
    }

    public async Task<SampledHotspotReport> ImportHotspotProfileAsync(
        string path, int profileIndex, int maxFrames, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            return await roslyn.ImportHotspotProfileAsync(path,
                profileIndex, maxFrames, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<NativeCodeReport> CorrelateNativeAsync(
        NativeCodeReport native, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            return roslyn.CorrelateNativeCode(native);
        }
        finally { _gate.Release(); }
    }

    public DeterministicAnalysisReport? DeterministicReport => _deterministic;

    public async Task<DeterministicAnalysisReport> AnalyzeDeterministicAsync(
        int maxFindings, int maxMethods, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            _deterministic = await roslyn.AnalyzeDeterministicAsync(
                maxFindings, maxMethods, ct);
            return _deterministic;
        }
        finally { _gate.Release(); }
    }

    public FindingsPage Findings(string? query, string? category, int offset, int limit)
    {
        var results = _findings.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query))
            results = results.Where(x => x.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.RuleId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.File.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category) && category != "all")
            results = results.Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        var items = results.ToArray();
        var skip = Math.Clamp(offset, 0, 100_000);
        var take = Math.Clamp(limit, 1, 200);
        return new FindingsPage(items.Length, skip, take, items.Skip(skip).Take(take).ToArray());
    }

    public async Task<IReadOnlyList<ReviewTarget>> TargetsAsync(
        string? query, int offset, int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { EnsureOpen(); return roslyn.GetReviewTargets(limit, offset, query); }
        finally { _gate.Release(); }
    }

    public async Task<MethodInspection> InspectAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { EnsureOpen(); return await roslyn.InspectAsync(id, false, ct); }
        finally { _gate.Release(); }
    }

    public async Task<SuggestedTestPlan> PlanAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { EnsureOpen(); return roslyn.SuggestTestPlan(id); }
        finally { _gate.Release(); }
    }

    public async Task<CompilationDiagnosticsReport?> DiagnosticsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _diagnostics; }
        finally { _gate.Release(); }
    }


    public async Task<ReviewContext> GetContextAsync(int maxMethods, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            var targets = roslyn.GetReviewTargets(maxMethods, 0, null);
            // A prior explicit scan is optional: the LLM can review methods
            // using Roslyn semantics even before rules have been scanned.
            return new ReviewContext(_solution!.SnapshotId, targets, _findings);
        }
        finally { _gate.Release(); }
    }

    public async Task<ReviewMethodContext> GetMethodAsync(string methodId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            var inspection = await roslyn.InspectAsync(methodId, includeCallers: true, ct);
            var plan = roslyn.SuggestTestPlan(methodId);
            var rules = _findings.Where(x => x.MethodId == methodId).Take(12).ToArray();
            return new ReviewMethodContext(inspection, plan, rules);
        }
        finally { _gate.Release(); }
    }

    private void EnsureOpen()
    {
        if (_solution is null) throw new InvalidOperationException("Open a trusted .sln or .csproj first.");
    }
}
