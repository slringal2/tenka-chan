namespace TenkaChan.Mcp.Analysis;

public sealed record ProjectSummary(string Name, string? Path, string? TargetFramework, int Documents);
public sealed record SolutionSummary(string SnapshotId, int MethodCount, IReadOnlyList<ProjectSummary> Projects,
    IReadOnlyList<string> WorkspaceWarnings);
public sealed record MethodSummary(string Id, string Project, string Signature, string File, int Line)
{
    public string CallableKind { get; init; } = "Ordinary";
}
public sealed record RuleFinding(string RuleId, string Category, string Severity, string Message,
    string File, int Line, int Column, string? MethodId, string Verification,
    string Status = "candidate");
public sealed record FlowBlock(int Ordinal, string Kind, string[] Operations,
    int? ConditionalSuccessor, int? FallThroughSuccessor, string? BranchCondition);
public sealed record MethodInspection(
    MethodSummary Method,
    string Source,
    IReadOnlyList<string> Callees,
    IReadOnlyList<string> Callers,
    IReadOnlyList<FlowBlock> Blocks,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> Diagnostics,
    string? AnalysisNote);

public sealed record CompilationDiagnostic(
    string Project, string DiagnosticId, string Severity, string Message,
    string? File, int? Line, int? Column);

public sealed record CompilationDiagnosticsReport(
    int ErrorCount, int WarningCount, int InformationCount,
    int ReturnedCount, IReadOnlyList<CompilationDiagnostic> Diagnostics)
{
    public string Scope => CompilerDiagnosticContext.Scope;
    public string Interpretation => CompilerDiagnosticContext.Interpretation;
    public IReadOnlyList<CompilerDiagnosticCodeCount> TopErrorCodes { get; init; } = [];
    public int PotentialReferenceResolutionErrors { get; init; }
}

public sealed record ReferenceHit(
    string Project, string File, int Line, int Column, string Preview);
