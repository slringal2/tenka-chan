using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class ReviewTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string Json(object data) => JsonSerializer.Serialize(data, JsonOptions);

    [McpServerTool(Name = "workspace_open")]
    [Description("Open a trusted .NET solution/project via Roslyn MSBuildWorkspace. Requires TENKA_ALLOWED_ROOTS and TENKA_TRUST_MSBUILD=1.")]
    public static async Task<string> Open(RoslynReviewService roslyn,
        [Description("Absolute path to a .sln, .slnx or .csproj")] string path,
        CancellationToken cancellationToken) =>
        Json(await roslyn.OpenAsync(path, cancellationToken));

    [McpServerTool(Name = "symbols_find")]
    [Description("Find method symbols by signature or file path within the loaded solution.")]
    public static string Find(RoslynReviewService roslyn,
        [Description("Search substring, case insensitive")] string query, int limit = 50) =>
        Json(roslyn.FindMethods(query, limit));

    [McpServerTool(Name = "symbol_inspect")]
    [Description("Inspect the C# source, direct calls, CFG blocks and dataflow of a method identified by symbols_find.")]
    public static async Task<string> Inspect(RoslynReviewService roslyn,
        string methodId, bool includeCallers = false, CancellationToken cancellationToken = default) =>
        Json(await roslyn.InspectAsync(methodId, includeCallers, cancellationToken));

    [McpServerTool(Name = "review_scan")]
    [Description("Run conservative Roslyn optimization and async-blocking candidate rules. Findings require independent validation.")]
    public static async Task<string> Scan(RoslynReviewService roslyn,
        int limit = 200, CancellationToken cancellationToken = default) =>
        Json(await roslyn.ScanAsync(limit, cancellationToken));

    [McpServerTool(Name = "experiment_prepare")]
    [Description("Syntax-check generated xUnit test or BenchmarkDotNet C# code; write a detached verification project. Never touches the original repository.")]
    public static string Prepare(ExperimentStore experiments,
        string repositoryRoot, string projectRelativePath,
        [Description("test or benchmark")] string kind,
        [Description("Full standalone C# source for the generated tests/benchmarks")] string generatedCode) =>
        Json(experiments.Create(repositoryRoot, projectRelativePath, kind, generatedCode));

    [McpServerTool(Name = "experiment_run")]
    [Description("Opt-in run of a prepared experiment in offline resource-limited Docker. Blocked unless TENKA_ENABLE_DOCKER=1.")]
    public static async Task<string> Execute(DockerExecutor executor,
        string experimentId, CancellationToken cancellationToken = default) =>
        Json(await executor.RunAsync(experimentId, cancellationToken));

    [McpServerTool(Name = "experiment_result")]
    [Description("Retrieve a persisted, structured Docker execution report including TRX test counters or BenchmarkDotNet metrics.")]
    public static string Result(ExperimentStore experiments, string experimentId) =>
        Json(experiments.ReadExecutionResult(experimentId));

    [McpServerTool(Name = "experiment_attach_result")]
    [Description("Attach observed TRX test outcomes from a stored sandbox execution to a review hypothesis; raw LLM-submitted test evidence is disallowed.")]
    public static string AttachResult(ExperimentStore experiments, ReviewLedger ledger,
        string hypothesisId, string experimentId) =>
        Json(ledger.AttachExperimentResult(hypothesisId,
            experiments.ReadExecutionResult(experimentId)));

    [McpServerTool(Name = "performance_compare")]
    [Description("Compare independent benchmark samples from the same environment; return exploratory bootstrap interval and candidate regression classification.")]
    public static string Compare(double[] baseline, double[] candidate,
        string baselineEnvironment, string candidateEnvironment, string unit = "ns/op",
        double thresholdPercent = 5) =>
        Json(PerformanceStatistics.Compare(baseline, candidate,
            baselineEnvironment, candidateEnvironment, unit, thresholdPercent));
    [McpServerTool(Name = "il_inspect")]
    [Description("Read managed IL instructions from an allowlisted DLL/EXE without loading or executing it. This is not native JIT assembly.")]
    public static string InspectIl(IlInspector inspector, string assemblyPath, string typeName,
        string methodName, int maxInstructions = 250) =>
        Json(inspector.Inspect(assemblyPath, typeName, methodName, maxInstructions));

    [McpServerTool(Name = "test_plan_suggest")]
    [Description("Suggest boundary-value C# inputs from Roslyn parameter types, without making up test oracles or assertions.")]
    public static string SuggestTestPlan(RoslynReviewService roslyn, string methodId) =>
        Json(roslyn.SuggestTestPlan(methodId));

    [McpServerTool(Name = "review_targets")]
    [Description("Rank methods by deterministic syntax-based review priority. This score is not a defect probability.")]
    public static string Targets(RoslynReviewService roslyn, int limit = 50,
        int offset = 0, string? query = null) =>
        Json(roslyn.GetReviewTargets(limit, offset, query));

    [McpServerTool(Name = "workspace_diagnostics")]
    [Description("Collect project compilation diagnostics: errors, warnings, locations and totals.")]
    public static async Task<string> Diagnostics(RoslynReviewService roslyn,
        int limit = 200, CancellationToken cancellationToken = default) =>
        Json(await roslyn.GetCompilationDiagnosticsAsync(limit, cancellationToken));

    [McpServerTool(Name = "symbol_references")]
    [Description("Use Roslyn SymbolFinder to find references to a method across loaded solution projects.")]
    public static async Task<string> References(RoslynReviewService roslyn,
        string methodId, int limit = 100, CancellationToken cancellationToken = default) =>
        Json(await roslyn.FindReferencesAsync(methodId, limit, cancellationToken));

    [McpServerTool(Name = "hypothesis_create")]
    [Description("Persist a review hypothesis and explicit failure condition for later verification. Never assumes the claim is true.")]
    public static string CreateHypothesis(ReviewLedger ledger,
        string snapshotId, string methodId, string category,
        string claim, string failureCondition) =>
        Json(ledger.Create(snapshotId, methodId, category, claim, failureCondition));

    [McpServerTool(Name = "evidence_add")]
    [Description("Attach sourced evidence to a hypothesis: static_analysis, contract, benchmark, profiler, source_inspection, human_review. Test execution evidence must come from experiment_attach_result.")]
    public static string AddEvidence(ReviewLedger ledger, string hypothesisId,
        string kind, string summary, string provenance, string? artifactId = null) =>
        Json(ledger.AddEvidence(hypothesisId, kind, summary, provenance, artifactId));

    [McpServerTool(Name = "hypothesis_set_status")]
    [Description("Record a verdict: suspected, needs_review, reported_reproduced, not_reproduced, refuted. Reported reproduction requires contract and test evidence; it is not formal proof.")]
    public static string SetHypothesisStatus(ReviewLedger ledger, string hypothesisId,
        string status, string rationale) =>
        Json(ledger.SetStatus(hypothesisId, status, rationale));

    [McpServerTool(Name = "hypothesis_get")]
    [Description("Fetch the complete durable hypothesis record and all evidence.")]
    public static string GetHypothesis(ReviewLedger ledger, string hypothesisId) =>
        Json(ledger.Get(hypothesisId));

    [McpServerTool(Name = "hypotheses_list")]
    [Description("List saved review hypotheses, optionally filtering by status. Survives MCP process restart.")]
    public static string ListHypotheses(ReviewLedger ledger, string? status = null,
        int limit = 100) => Json(ledger.List(status, limit));

}
