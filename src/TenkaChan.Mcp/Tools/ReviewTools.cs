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
    private static string Json(object? data) => JsonSerializer.Serialize(data, JsonOptions);

    [McpServerTool(Name = "workspace_open")]
    [Description("Open a trusted .NET solution/project via Roslyn MSBuildWorkspace. Requires TENKA_ALLOWED_ROOTS and TENKA_TRUST_MSBUILD=1.")]
    public static async Task<string> Open(RoslynReviewService roslyn,
        AutonomousReviewRunner runner,
        [Description("Absolute path to a .sln, .slnx or .csproj")] string path,
        CancellationToken cancellationToken)
    {
        if (runner.IsActive)
            throw new InvalidOperationException("Stop active review before replacing workspace.");
        return Json(await roslyn.OpenAsync(path, cancellationToken));
    }

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

    [McpServerTool(Name = "deterministic_analyze")]
    [Description("Analyze a loaded .NET solution deterministically using Roslyn compiler diagnostics, semantic rules, and bounded method/CFG metrics. No LLM is used.")]
    public static async Task<string> DeterministicAnalyze(
        RoslynReviewService roslyn, int maxFindings = 500, int maxMethodMetrics = 100,
        CancellationToken cancellationToken = default) =>
        Json(await roslyn.AnalyzeDeterministicAsync(
            maxFindings, maxMethodMetrics, cancellationToken));

    [McpServerTool(Name = "deterministic_rules")]
    [Description("List deterministic rule IDs, descriptions, severity, evidence levels and recommended independent verification.")]
    public static string DeterministicRules() => Json(new
    {
        version = DeterministicRuleCatalog.Version,
        rules = DeterministicRuleCatalog.Rules
    });

    [McpServerTool(Name = "performance_evidence_triage")]
    [Description("Prioritize observed profiler frames by exclusive weight, with separate Roslyn static cost candidates and optional method-level JIT evidence. NEVER adds static risk scores to measured time.")]
    public static async Task<string> PerformanceEvidenceTriage(
        PerformanceEvidenceService service, string? jitExperimentId = null,
        int maxObserved = 40, int maxStatic = 20,
        CancellationToken cancellationToken = default) =>
        Json(await service.TriageAsync(jitExperimentId, maxObserved,
            maxStatic, cancellationToken));

    [McpServerTool(Name = "performance_method_dossier")]
    [Description("Inspect a profiler-matched Roslyn method; optionally attach native JIT instructions and an explicitly selected, hash-verified captured MethodDef IL/PDB source mapping. No native-to-IL offset inference.")]
    public static async Task<string> PerformanceMethodDossier(
        PerformanceEvidenceService service, string methodId,
        string? jitExperimentId = null, string? assemblyFile = null,
        int? methodToken = null, string? sourceFilePath = null,
        int maxInstructions = 1200,
        CancellationToken cancellationToken = default) =>
        Json(await service.InvestigateAsync(methodId, jitExperimentId,
            assemblyFile, methodToken, sourceFilePath, maxInstructions,
            cancellationToken));

    [McpServerTool(Name = "performance_hotspots")]
    [Description("Deterministic static hotspot screening: nested loops, repeated allocations/enumerations, blocking/I/O, and bounded callee-risk propagation. Scores are NOT CPU measurements.")]
    public static async Task<string> PerformanceHotspots(
        RoslynReviewService roslyn, int maxMethods = 100, int maxSites = 300,
        CancellationToken cancellationToken = default) =>
        Json(await roslyn.AnalyzePerformanceHotspotsAsync(maxMethods, maxSites, cancellationToken));

    [McpServerTool(Name = "performance_profile_import")]
    [Description("Read an allowlisted sampled speedscope JSON workload profile without executing code. Report observed inclusive/exclusive weights and conservatively match qualified method names.")]
    public static async Task<string> PerformanceProfileImport(
        RoslynReviewService roslyn, string filePath,
        int profileIndex = 0, int maxFrames = 100,
        CancellationToken cancellationToken = default) =>
        Json(await roslyn.ImportHotspotProfileAsync(filePath, profileIndex,
            maxFrames, cancellationToken));

    [McpServerTool(Name = "performance_profile_current")]
    [Description("Return the last imported measured sampled profile, or null if no profile is loaded.")]
    public static string PerformanceProfileCurrent(RoslynReviewService roslyn) =>
        Json(roslyn.CurrentHotspotProfile);

    [McpServerTool(Name = "review_scan")]
    [Description("Run conservative Roslyn optimization and async-blocking candidate rules. Findings require independent validation.")]
    public static async Task<string> Scan(RoslynReviewService roslyn,
        int limit = 200, CancellationToken cancellationToken = default) =>
        Json(await roslyn.ScanAsync(limit, cancellationToken));

    [McpServerTool(Name = "experiment_prepare")]
    [Description("Syntax-check generated xUnit test or BenchmarkDotNet C# code; write a detached verification project. Never touches the original repository.")]
    public static string Prepare(ExperimentStore experiments,
        string repositoryRoot, string projectRelativePath,
        [Description("test, benchmark or jit")] string kind,
        [Description("Standalone C# source for the generated experiment or a JIT execution harness")] string generatedCode,
        [Description("Optional explicit portable TFM net8.0 or later; otherwise infer from project .csproj")] string? targetFramework = null,
        [Description("Required for kind=jit: RyuJIT class:method filter, e.g. Calc:Sum")] string? jitDisasmFilter = null) =>
        Json(experiments.Create(repositoryRoot, projectRelativePath,
            kind, generatedCode, targetFramework, jitDisasmFilter));

    [McpServerTool(Name = "experiment_run")]
    [Description("Opt-in run of a prepared experiment in offline resource-limited Docker. Blocked unless TENKA_ENABLE_DOCKER=1.")]
    public static async Task<string> Execute(DockerExecutor executor,
        string experimentId, CancellationToken cancellationToken = default) =>
        Json(await executor.RunAsync(experimentId, cancellationToken));

    [McpServerTool(Name = "experiments_list")]
    [Description("List recent prepared and executed tests/benchmarks from the persistent experiment store.")]
    public static string ExperimentsList(ExperimentStore experiments, int limit = 50) =>
        Json(experiments.ListRecent(limit));

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

    [McpServerTool(Name = "benchmark_compare_runs")]
    [Description("Compare two saved BenchmarkDotNet runs using matching source hashes and runtime fingerprints. Returns descriptive changes, not statistical significance.")]
    public static string CompareSavedBenchmarks(BenchmarkRunComparer comparer,
        string baselineExperimentId, string candidateExperimentId, double thresholdPercent = 5) =>
        Json(comparer.Compare(baselineExperimentId, candidateExperimentId, thresholdPercent));

    [McpServerTool(Name = "performance_compare")]
    [Description("Compare independent benchmark samples from the same environment; return exploratory bootstrap interval and candidate regression classification.")]
    public static string Compare(double[] baseline, double[] candidate,
        string baselineEnvironment, string candidateEnvironment, string unit = "ns/op",
        double thresholdPercent = 5) =>
        Json(PerformanceStatistics.Compare(baseline, candidate,
            baselineEnvironment, candidateEnvironment, unit, thresholdPercent));

    [McpServerTool(Name = "autonomous_review_start")]
    [Description("Start a bounded local-LLM review of high-priority Roslyn methods. Saves unverified hypotheses; never runs generated code.")]
    public static async Task<string> StartAutonomousReview(
        AutonomousReviewRunner runner, int maxMethods = 10, int maxMinutes = 15,
        CancellationToken cancellationToken = default) =>
        Json(await runner.StartAsync(new AutonomousReviewOptions(maxMethods, maxMinutes),
            cancellationToken));

    [McpServerTool(Name = "autonomous_review_status")]
    [Description("Read a running or completed autonomous review by ID, or the current run if no ID is provided.")]
    public static string AutonomousReviewStatus(AutonomousReviewRunner runner,
        string? runId = null) =>
        Json(runId is null ? (object?)runner.Current : runner.Get(runId));

    [McpServerTool(Name = "autonomous_review_stop")]
    [Description("Cancel an active local-LLM review by its run ID.")]
    public static string StopAutonomousReview(AutonomousReviewRunner runner, string runId) =>
        Json(runner.Stop(runId));

    [McpServerTool(Name = "autonomous_review_history")]
    [Description("List prior persisted autonomous review runs, including unfinished runs marked interrupted after restart.")]
    public static string AutonomousReviewHistory(AutonomousReviewRunner runner, int limit = 20) =>
        Json(runner.History(limit));

    [McpServerTool(Name = "native_compare_runs")]
    [Description("Compare two completed, equivalent JIT harness runs: native code size, instruction families and call/vector patterns. NOT a benchmark or measured speedup.")]
    public static string NativeCompareRuns(NativeExperimentComparer comparer,
        string baselineExperimentId, string candidateExperimentId) =>
        Json(comparer.Compare(baselineExperimentId, candidateExperimentId));

    [McpServerTool(Name = "native_disassembly_result")]
    [Description("Return structured RyuJIT native machine instructions and code bytes from a completed kind=jit experiment. Never interprets instruction counts as elapsed time.")]
    public static string NativeDisassemblyResult(ExperimentStore experiments, string experimentId) =>
        Json(experiments.ReadExecutionResult(experimentId).NativeDisassembly);

    [McpServerTool(Name = "native_roslyn_correlate")]
    [Description("Match JIT method signatures to uniquely named Roslyn symbols in the loaded project. This is method-level matching, NOT exact native-instruction-to-C#-line mapping.")]
    public static string NativeRoslynCorrelate(ExperimentStore experiments,
        RoslynReviewService roslyn, string experimentId)
    {
        var native = experiments.ReadExecutionResult(experimentId).NativeDisassembly
            ?? throw new InvalidOperationException("Experiment has no native JIT disassembly.");
        return Json(roslyn.CorrelateNativeCode(native));
    }

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
