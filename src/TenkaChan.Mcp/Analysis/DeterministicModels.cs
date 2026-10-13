namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Deterministic rule metadata. "candidate" is a potential problem to verify,
/// "semantic_fact" is a compiler-backed description of what code does, not
/// proof that behavior violates the application's specification.
/// </summary>
public sealed record DeterministicRuleDescriptor(
    string RuleId, string Title, string Category, string DefaultSeverity,
    string EvidenceLevel, string Description, string RecommendedCheck);

public sealed record DeterministicFinding(
    string Fingerprint, string RuleId, string Title, string Category,
    string Severity, string EvidenceLevel, string Source,
    string Project, string File, string RelativePath, int Line, int Column,
    string? MethodId, string Message, string CodeExcerpt, string RecommendedCheck);

public sealed record DeterministicMethodMetrics(
    string MethodId, string Signature, string Project, string File, int Line,
    int SourceLines, int LoopCount, int BranchCount, int AwaitCount,
    int ObjectCreationSites, int ApproximateComplexity, int? CfgBlockCount,
    int? CfgBranchCount, int ResolvedInvocationCount, double ReviewPriority,
    IReadOnlyList<string> RiskHints);

public sealed record DeterministicAnalysisSummary(
    string SnapshotId, string RulesVersion,
    int ProjectCount, int DocumentsAnalyzed, int MethodsIndexed,
    int CompilerErrors, int CompilerWarnings, int CompilerInfos,
    int TotalFindings, int ReturnedFindings,
    int TotalMethodMetrics, int ReturnedMethodMetrics,
    bool FindingsTruncated, bool MethodMetricsTruncated,
    bool IncompleteRuleScanning,
    string[] Notes);

public sealed record DeterministicAnalysisReport(
    DeterministicAnalysisSummary Summary,
    IReadOnlyList<DeterministicFinding> Findings,
    IReadOnlyList<DeterministicMethodMetrics> MethodMetrics);

public static class DeterministicRuleCatalog
{
    public const string Version = "2026.10.1";

    public static readonly IReadOnlyList<DeterministicRuleDescriptor> Rules =
    [
        new("COMPILER", "C# compiler diagnostic", "compiler", "varies",
            "compiler", "A diagnostic returned by the Roslyn compilation.",
            "Fix errors, then re-run the build and existing tests."),
        new("PERF1001", "LINQ materialization in loop", "allocation", "info",
            "candidate", "Repeated ToList/ToArray may allocate unnecessarily.",
            "Check semantics and compare allocation/time for real inputs."),
        new("PERF1002", "String concatenation in loop", "allocation", "info",
            "candidate", "Repeated string += may create temporary strings.",
            "Compare string builder and original implementation on real input sizes."),
        new("PERF1003", "Regex allocation in loop", "allocation", "info",
            "candidate", "Repeated Regex constructor may be avoidable.",
            "Compare matching semantics and measure throughput/allocations."),
        new("PERF1004", "LINQ Count used in comparison", "enumeration", "info",
            "candidate", "Some Count queries can enumerate, unlike Any.",
            "Check comparison semantics and Enumerable/Queryable behavior."),
        new("PERF1005", "HttpClient construction in loop", "resource", "info",
            "candidate", "Handler/connection lifecycle may cause overhead.",
            "Evaluate HttpClient lifetime and measure connection reuse."),
        new("ASYNC1001", "Task.Result in async method", "async-blocking", "warning",
            "candidate", "Blocking Result can consume worker threads.",
            "Inspect scheduling and concurrency, test cancellation/latency."),
        new("ASYNC1002", "Task.Wait in async method", "async-blocking", "warning",
            "candidate", "Blocking Wait can cause thread starvation.",
            "Inspect scheduling and concurrency, test cancellation/latency."),
        new("ASYNC1003", "Thread.Sleep in async method", "async-blocking", "warning",
            "candidate", "Thread.Sleep blocks rather than asynchronously waits.",
            "Inspect scheduling and compare Task.Delay semantics."),
        new("ASYNC1004", "Async void method", "async-error-handling", "warning",
            "candidate", "Async void lacks an awaitable Task result, except event handlers.",
            "Confirm whether the method is an event handler and check exceptions."),
        new("CORRECT1001", "Empty catch", "correctness", "warning",
            "candidate", "Empty catch silently suppresses an exception.",
            "Check the failure contract and inject an error."),
        new("CORRECT2001", "Caught exception rethrown by expression", "correctness", "warning",
            "semantic_fact", "throw ex rethrows the exception with a different stack trace.",
            "If preserving the existing stack trace is intended, consider bare throw;."),
        new("PERF2001", "Value type boxed in loop", "allocation", "info",
            "candidate", "Conversion of a value type to object in a loop may allocate.",
            "Confirm boxing in IL and compare allocated bytes/op.")
    ];

    private static readonly IReadOnlyDictionary<string, DeterministicRuleDescriptor> ById =
        Rules.ToDictionary(x => x.RuleId, StringComparer.Ordinal);

    public static DeterministicRuleDescriptor Get(string id) =>
        ById.TryGetValue(id, out var rule) ? rule
        : throw new KeyNotFoundException("Unknown deterministic rule: " + id);
}
