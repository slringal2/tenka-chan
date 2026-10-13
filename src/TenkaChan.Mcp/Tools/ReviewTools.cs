using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;

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
}
