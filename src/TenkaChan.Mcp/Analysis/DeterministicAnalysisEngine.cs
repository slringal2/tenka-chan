using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Pure helpers for converting Roslyn diagnostics and semantic candidates
/// into deterministic, auditable records. No LLM inference is involved.
/// </summary>
public static class DeterministicAnalysisEngine
{
    public static DeterministicFinding FromRule(
        RuleFinding candidate, Project project, SourceText text,
        string? methodId)
    {
        var descriptor = DeterministicRuleCatalog.Get(candidate.RuleId);
        var line = candidate.Line;
        var column = candidate.Column;
        var path = RelativePath(project, candidate.File);
        var excerpt = LineExcerpt(text, line);
        return new DeterministicFinding(
            Fingerprint(project.Name, path, candidate.RuleId, line, column, excerpt),
            candidate.RuleId, descriptor.Title, descriptor.Category,
            descriptor.DefaultSeverity, descriptor.EvidenceLevel, "roslyn_semantic_rule",
            project.Name, candidate.File, path, line, column,
            methodId, candidate.Message, excerpt, candidate.Verification);
    }

    /// <summary>
    /// Execute analyzers explicitly installed in a trusted project. Analyzer
    /// code executes on the caller's process and must never run on projects
    /// that have not been explicitly trusted.
    /// </summary>
    public static async Task<IReadOnlyList<Diagnostic>> RunAnalyzersAsync(
        Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers,
        AnalyzerOptions options, CancellationToken cancellationToken = default)
    {
        if (analyzers.IsDefaultOrEmpty) return [];
        var settings = new CompilationWithAnalyzersOptions(options,
            onAnalyzerException: null, concurrentAnalysis: false,
            logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
        var diagnostics = await compilation.WithAnalyzers(analyzers, settings)
            .GetAnalyzerDiagnosticsAsync(cancellationToken);
        return diagnostics.OrderBy(d => d.Location.GetLineSpan().Path,
                StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id,
                StringComparer.Ordinal).ToArray();
    }

    public static DeterministicFinding FromDiagnostic(
        Diagnostic diagnostic, Project project, SourceText? text,
        string? methodId, bool projectAnalyzer = false)
    {
        var path = diagnostic.Location.IsInSource
            ? diagnostic.Location.GetLineSpan().Path ?? ""
            : project.FilePath ?? project.Name;
        var hasLine = diagnostic.Location.IsInSource;
        var pos = hasLine ? diagnostic.Location.GetLineSpan().StartLinePosition : default;
        var line = hasLine ? pos.Line + 1 : 0;
        var col = hasLine ? pos.Character + 1 : 0;
        var relative = RelativePath(project, path);
        var excerpt = text is null ? "" : LineExcerpt(text, line);
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info"
        };
        return new DeterministicFinding(
            Fingerprint(project.Name, relative,
                (projectAnalyzer ? "ANALYZER:" : "COMPILER:") + diagnostic.Id,
                line, col, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)),
            diagnostic.Id,
            (projectAnalyzer ? "Analyzer diagnostic: " : "Compiler diagnostic: ") + diagnostic.Id,
            projectAnalyzer ? "analyzer" : "compiler", severity,
            projectAnalyzer ? "analyzer" : "compiler",
            projectAnalyzer ? "roslyn_project_analyzer" : "roslyn_compiler",
            project.Name, path, relative, line, col, methodId,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), excerpt,
            projectAnalyzer
                ? "Review the installed analyzer's rule documentation and verify the issue."
                : severity == "error"
                    ? "Fix the compilation error and verify that the project builds."
                    : "Inspect the compiler diagnostic and determine whether action is required.");
    }

    public static DeterministicMethodMetrics BuildMetrics(
        ReviewTarget target, SyntaxNode syntax,
        SemanticModel model, CancellationToken cancellationToken = default)
    {
        int? cfgBlocks = null;
        int? cfgBranches = null;
        if (CallableSyntax.Body(syntax) is not null)
        {
            try
            {
                var graph = CallableSyntax.TryCreateGraph(syntax, model, cancellationToken);
                if (graph is not null)
                {
                    cfgBlocks = graph.Blocks.Length;
                    cfgBranches = graph.Blocks.Count(block =>
                        block.IsReachable && block.BranchValue is not null);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Incomplete code, expression bodies and some compiler-generated
                // constructs cannot always be converted to a CFG.
            }
        }

        var calls = CallableSyntax.OwnNodes(syntax)
            .OfType<InvocationExpressionSyntax>()
            .Count(inv => model.GetSymbolInfo(inv, cancellationToken).Symbol is IMethodSymbol);

        return new DeterministicMethodMetrics(
            target.Method.Id, target.Method.Signature, target.Method.Project,
            target.Method.File, target.Method.Line,
            target.SourceLines, target.LoopCount, target.ConditionalCount,
            target.AwaitCount, target.ObjectCreationSites,
            target.ApproximateComplexity, cfgBlocks, cfgBranches,
            calls, target.Priority, target.RiskHints);
    }

    public static string RelativePath(Project project, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var baseDirectory = Path.GetDirectoryName(project.FilePath);
        if (baseDirectory is null || !Path.IsPathRooted(path))
            return path.Replace('\\', '/');
        return Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
    }

    public static string Fingerprint(
        string projectName, string relativeFile, string ruleId,
        int line, int column, string evidence)
    {
        var canonical = string.Join("\n", DeterministicRuleCatalog.Version, projectName,
            relativeFile, ruleId, line.ToString(System.Globalization.CultureInfo.InvariantCulture),
            column.ToString(System.Globalization.CultureInfo.InvariantCulture), evidence.Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..24];
    }

    private static string LineExcerpt(SourceText source, int line)
    {
        if (line <= 0 || line > source.Lines.Count) return "";
        var raw = source.Lines[line - 1].ToString().Trim();
        return raw.Length <= 240 ? raw : raw[..240] + "...";
    }
}
