using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// In-memory session cache for stdio MCP clients. Reopening a solution gives a
/// new snapshot ID. The experiment store, unlike this cache, is durable.
/// </summary>
public sealed class RoslynReviewService : IDisposable
{
    private sealed record Entry(MethodSummary Summary, Document Document,
        MethodDeclarationSyntax Syntax, IMethodSymbol Symbol);

    private readonly WorkspaceGuard _guard;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private readonly Dictionary<string, Entry> _methods = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string _snapshotId = "";

    public RoslynReviewService(WorkspaceGuard guard) => _guard = guard;

    public string CurrentSnapshotId => _solution is not null
        ? _snapshotId : throw new InvalidOperationException("Call workspace_open first.");

    public async Task<SolutionSummary> OpenAsync(string path, CancellationToken ct = default)
    {
        if (!_guard.TrustMsBuild)
            throw new UnauthorizedAccessException(
                "MSBuild evaluation is disabled. Set TENKA_TRUST_MSBUILD=1 only for trusted project files.");
        var safePath = _guard.RequireSolutionOrProject(path);
        await _openLock.WaitAsync(ct);
        try
        {
            _workspace?.Dispose();
            _methods.Clear();
            _warnings.Clear();
            _solution = null;
            _workspace = MSBuildWorkspace.Create();
            _workspace.RegisterWorkspaceFailedHandler(args =>
            {
                if (_warnings.Count < 100) _warnings.Add(args.Diagnostic.Message);
            });
            _solution = Path.GetExtension(safePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await _workspace.OpenProjectAsync(safePath, cancellationToken: ct)).Solution
                : await _workspace.OpenSolutionAsync(safePath, cancellationToken: ct);
            // Reject project documents outside configured allowed roots (linked files too).
            // MSBuild evaluation already requires explicit trust and is not sandboxed.
            foreach (var project in _solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            {
                if (project.FilePath is null)
                    throw new UnauthorizedAccessException("C# project without an on-disk path is unsupported.");
                _guard.RequireFile(project.FilePath, ".csproj");
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is null)
                        throw new UnauthorizedAccessException("C# document without an on-disk path is unsupported.");
                    _guard.RequireFile(document.FilePath, ".cs");
                }
            }
            _snapshotId = Guid.NewGuid().ToString("N");

            foreach (var project in _solution.Projects.Where(x => x.Language == LanguageNames.CSharp))
            {
                foreach (var document in project.Documents)
                {
                    ct.ThrowIfCancellationRequested();
                    var syntaxRoot = await document.GetSyntaxRootAsync(ct);
                    var model = await document.GetSemanticModelAsync(ct);
                    if (syntaxRoot is null || model is null) continue;
                    foreach (var method in syntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        if (model.GetDeclaredSymbol(method, ct) is not IMethodSymbol symbol) continue;
                        var signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        var sourcePath = document.FilePath ?? document.Name;
                        var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        var identity = project.Id.Id + "|" + sourcePath + "|" + signature + "|" + method.SpanStart;
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
                        var summary = new MethodSummary(id, project.Name, signature, sourcePath, line);
                        _methods[id] = new Entry(summary, document, method, symbol);
                    }
                }
            }
            var projects = _solution.Projects.Select(p =>
                new ProjectSummary(p.Name, p.FilePath,
                    null, // Target framework is not safely inferable from ParseOptions.
                    p.DocumentIds.Count)).ToArray();
            return new SolutionSummary(_snapshotId, _methods.Count, projects, _warnings.ToArray());
        }
        finally
        {
            _openLock.Release();
        }
    }

    public IReadOnlyList<MethodSummary> FindMethods(string query, int limit = 50)
    {
        EnsureOpen();
        return _methods.Values.Select(x => x.Summary)
            .Where(x => x.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.File.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 200)).ToArray();
    }

    public async Task<MethodInspection> InspectAsync(
        string methodId, bool includeCallers = false, CancellationToken ct = default)
    {
        var entry = RequireMethod(methodId);
        var model = await entry.Document.GetSemanticModelAsync(ct)
            ?? throw new InvalidOperationException("Semantic model unavailable.");
        var calls = entry.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(s => (model.GetSymbolInfo(s, ct).Symbol as IMethodSymbol)?.ToDisplayString())
            .Where(s => s is not null).Distinct().Take(100).Select(s => s!).ToArray();

        var callers = new List<string>();
        if (includeCallers)
        {
            // Bounded best-effort direct call search; not a reflection/DI call graph.
            foreach (var other in _methods.Values)
            {
                ct.ThrowIfCancellationRequested();
                if (callers.Count >= 100) break;
                var otherModel = await other.Document.GetSemanticModelAsync(ct);
                if (otherModel is null) continue;
                foreach (var invocation in other.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var invoked = otherModel.GetSymbolInfo(invocation, ct).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(
                        (invoked as IMethodSymbol)?.OriginalDefinition, entry.Symbol.OriginalDefinition))
                    {
                        callers.Add(other.Summary.Signature);
                        break;
                    }
                }
            }
        }

        var blocks = new List<FlowBlock>();
        var reads = Array.Empty<string>();
        var writes = Array.Empty<string>();
        string? note = null;
        if (entry.Syntax.Body is not null)
        {
            try
            {
                // The block operation is often a child of an IMethodBodyOperation.
                // ControlFlowGraph.Create requires a root operation with Parent == null.
                var operation = model.GetOperation(entry.Syntax, ct)
                    ?? model.GetOperation(entry.Syntax.Body, ct);
                while (operation?.Parent is not null)
                    operation = operation.Parent;

                var graph = operation switch
                {
                    IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody, ct),
                    IBlockOperation blockBody => ControlFlowGraph.Create(blockBody, ct),
                    _ => ControlFlowGraph.Create(entry.Syntax, model, ct)
                };
                if (graph is not null)
                {
                    foreach (var block in graph.Blocks.Take(250))
                    {
                        blocks.Add(new FlowBlock(
                            block.Ordinal, block.Kind.ToString(),
                            block.Operations.Select(x => Truncate(x.Syntax.ToString(), 350)).Take(30).ToArray(),
                            block.ConditionalSuccessor?.Destination?.Ordinal,
                            block.FallThroughSuccessor?.Destination?.Ordinal,
                            block.BranchValue is null ? null : Truncate(block.BranchValue.Syntax.ToString(), 350)));
                    }
                }
                if (graph is null) note = "CFG unavailable: Roslyn returned no executable root operation.";
                var flow = model.AnalyzeDataFlow(entry.Syntax.Body);
                if (flow.Succeeded)
                {
                    reads = flow.ReadInside.Select(x => x.Name).Distinct().ToArray();
                    writes = flow.WrittenInside.Select(x => x.Name).Distinct().ToArray();
                }
            }
            catch (ArgumentException ex) { note = "CFG/dataflow unavailable: " + ex.Message; }
            catch (InvalidOperationException ex) { note = "CFG/dataflow unavailable: " + ex.Message; }
        }
        else
        {
            note = "No block body (expression-bodied, generated or declaration-only method).";
        }

        var compilation = await entry.Document.Project.GetCompilationAsync(ct);
        var diagnostics = compilation?.GetDiagnostics(ct)
            .Where(x => x.Location.IsInSource
                && x.Location.SourceTree?.FilePath == entry.Document.FilePath
                && x.Location.SourceSpan.IntersectsWith(entry.Syntax.Span))
            .Take(50).Select(x => x.ToString()).ToArray() ?? [];
        return new MethodInspection(entry.Summary,
            Truncate(entry.Syntax.ToFullString(), 20000), calls, callers, blocks, reads, writes,
            diagnostics, note);
    }

    public async Task<IReadOnlyList<RuleFinding>> ScanAsync(int limit = 500, CancellationToken ct = default)
    {
        EnsureOpen();
        var result = new List<RuleFinding>();
        var max = Math.Clamp(limit, 1, 5000);
        foreach (var project in _solution!.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= max) return result;
                var root = await document.GetSyntaxRootAsync(ct);
                var model = await document.GetSemanticModelAsync(ct);
                if (root is null || model is null) continue;
                var findings = RuleScanner.Scan(root, model, document.FilePath ?? document.Name, max - result.Count);
                var methodsInDocument = _methods.Values
                    .Where(entry => entry.Document.Id == document.Id).ToArray();
                result.AddRange(findings.Select(f => f with
                {
                    MethodId = methodsInDocument
                        .Where(entry =>
                        {
                            var span = entry.Syntax.GetLocation().GetLineSpan();
                            var line = f.Line - 1;
                            return span.StartLinePosition.Line <= line
                                && span.EndLinePosition.Line >= line;
                        })
                        .OrderBy(entry => entry.Syntax.Span.Length)
                        .FirstOrDefault()?.Summary.Id
                }));
            }
        }
        return result;
    }


    /// <summary>
    /// Produce a self-contained deterministic report without asking an LLM.
    /// This is not a formal proof of correctness or a measured performance
    /// profile. Findings/metrics are sorted and bounded for MCP/Web clients.
    /// </summary>
    public async Task<DeterministicAnalysisReport> AnalyzeDeterministicAsync(
        int maxFindings = 1000, int maxMethodMetrics = 300,
        CancellationToken ct = default)
    {
        EnsureOpen();
        var findingLimit = Math.Clamp(maxFindings, 1, 5000);
        var metricLimit = Math.Clamp(maxMethodMetrics, 1, 2000);
        var findings = new List<DeterministicFinding>();
        var documentsScanned = 0;
        var compilerErrors = 0;
        var compilerWarnings = 0;
        var compilerInfos = 0;
        var projectAnalyzerDiagnostics = 0;
        var projectAnalyzersExecuted = 0;
        var additionalNotes = new List<string>();
        var incompleteRuleScanning = false;

        var entries = _methods.Values.ToArray();
        var byDocument = entries.GroupBy(x => x.Document.Id)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Syntax.Span.Length).ToArray());
        var documentModels = new Dictionary<DocumentId, SemanticModel>();

        foreach (var project in _solution!.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ThenBy(project => project.FilePath, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var texts = new Dictionary<string, Microsoft.CodeAnalysis.Text.SourceText>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var documents = project.Documents
                .OrderBy(d => d.FilePath, StringComparer.Ordinal).ToArray();

            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(ct);
                var model = await document.GetSemanticModelAsync(ct);
                if (root is null || model is null) continue;
                documentModels[document.Id] = model;
                var text = await document.GetTextAsync(ct);
                if (document.FilePath is { } filePath) texts[filePath] = text;
                documentsScanned++;

                string? IdAt(int line, int col)
                {
                    if (!byDocument.TryGetValue(document.Id, out var methods)
                        || line <= 0 || line > text.Lines.Count) return null;
                    var lineData = text.Lines[line - 1];
                    var position = lineData.Start + Math.Max(0, col - 1);
                    if (position > lineData.End) position = lineData.End;
                    return methods.FirstOrDefault(e => e.Syntax.Span.Contains(position))
                        ?.Summary.Id;
                }

                var oldRules = RuleScanner.Scan(root, model,
                    document.FilePath ?? document.Name, 5000);
                if (oldRules.Count >= 5000) incompleteRuleScanning = true;
                var semanticRules = DeterministicSemanticRules.Scan(root, model,
                    document.FilePath ?? document.Name, ct);
                foreach (var rule in oldRules.Concat(semanticRules))
                {
                    ct.ThrowIfCancellationRequested();
                    var methodId = IdAt(rule.Line, rule.Column);
                    findings.Add(DeterministicAnalysisEngine.FromRule(rule,
                        project, text, methodId));
                }
            }

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            var analyzerDiagnostics = new List<Diagnostic>();
            try
            {
                var analyzers = project.AnalyzerReferences
                    .SelectMany(reference => reference.GetAnalyzers(LanguageNames.CSharp))
                    .Where(analyzer => analyzer is not null)
                    .DistinctBy(analyzer => analyzer.GetType().AssemblyQualifiedName)
                    .ToImmutableArray();
                projectAnalyzersExecuted += analyzers.Length;
                if (analyzers.Length > 0)
                {
                    var reported = await DeterministicAnalysisEngine.RunAnalyzersAsync(
                        compilation, analyzers, project.AnalyzerOptions, ct);
                    analyzerDiagnostics.AddRange(reported);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or System.Reflection.ReflectionTypeLoadException or IOException)
            {
                incompleteRuleScanning = true;
                additionalNotes.Add("Installed analyzers could not be evaluated for project "
                    + project.Name + ": " + ex.GetType().Name);
            }
            projectAnalyzerDiagnostics += analyzerDiagnostics.Count;

            foreach (var diagnostic in compilation.GetDiagnostics(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (diagnostic.Severity == DiagnosticSeverity.Error) compilerErrors++;
                else if (diagnostic.Severity == DiagnosticSeverity.Warning) compilerWarnings++;
                else compilerInfos++;

                var path = diagnostic.Location.IsInSource
                    ? diagnostic.Location.GetLineSpan().Path : null;
                var document = path is null ? null : documents.FirstOrDefault(
                    d => string.Equals(d.FilePath, path, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                var text = path is not null && texts.TryGetValue(path, out var sourceText)
                    ? sourceText : null;
                string? methodId = null;
                if (document is not null && text is not null &&
                    byDocument.TryGetValue(document.Id, out var methodsAtFile))
                {
                    var position = diagnostic.Location.SourceSpan.Start;
                    methodId = methodsAtFile.FirstOrDefault(
                        e => e.Syntax.Span.Contains(position))?.Summary.Id;
                }
                findings.Add(DeterministicAnalysisEngine.FromDiagnostic(
                    diagnostic, project, text, methodId));
            }

            foreach (var diagnostic in analyzerDiagnostics)
            {
                ct.ThrowIfCancellationRequested();
                var path = diagnostic.Location.IsInSource
                    ? diagnostic.Location.GetLineSpan().Path : null;
                var document = path is null ? null : documents.FirstOrDefault(
                    d => string.Equals(d.FilePath, path, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                var text = path is not null && texts.TryGetValue(path, out var sourceText)
                    ? sourceText : null;
                string? methodId = null;
                if (document is not null && text is not null &&
                    byDocument.TryGetValue(document.Id, out var methodsAtFile))
                    methodId = methodsAtFile.FirstOrDefault(
                        e => e.Syntax.Span.Contains(diagnostic.Location.SourceSpan.Start))
                        ?.Summary.Id;

                findings.Add(DeterministicAnalysisEngine.FromDiagnostic(
                    diagnostic, project, text, methodId, projectAnalyzer: true));
            }
        }

        var allTargets = entries
            .Select(entry => (Entry: entry, Target:
                ReviewPrioritizer.Assess(entry.Summary, entry.Syntax)))
            .OrderByDescending(x => x.Target.Priority)
            .ThenBy(x => x.Target.Method.Project, StringComparer.Ordinal)
            .ThenBy(x => x.Target.Method.File, StringComparer.Ordinal)
            .ThenBy(x => x.Target.Method.Line)
            .ThenBy(x => x.Target.Method.Signature, StringComparer.Ordinal)
            .ToArray();
        var metrics = new List<DeterministicMethodMetrics>();
        foreach (var item in allTargets.Take(metricLimit))
        {
            ct.ThrowIfCancellationRequested();
            if (!documentModels.TryGetValue(item.Entry.Document.Id, out var model))
                continue;
            metrics.Add(DeterministicAnalysisEngine.BuildMetrics(
                item.Target, item.Entry.Syntax, model, ct));
        }

        var ordered = findings
            .DistinctBy(f => f.Fingerprint)
            .OrderBy(f => f.Severity switch { "error" => 0, "warning" => 1, _ => 2 })
            .ThenBy(f => f.Project, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToArray();
        var summary = new DeterministicAnalysisSummary(
            CurrentSnapshotId, DeterministicRuleCatalog.Version,
            _solution.Projects.Count(p => p.Language == LanguageNames.CSharp),
            documentsScanned, _methods.Count,
            compilerErrors, compilerWarnings, compilerInfos,
            ordered.Length, Math.Min(ordered.Length, findingLimit),
            _methods.Count, metrics.Count,
            ordered.Length > findingLimit, metrics.Count < _methods.Count,
            incompleteRuleScanning,
            [
                "Compiler diagnostics are compilation facts; rules describe candidates or language semantics, not proven application bugs.",
                "Complexity scores are syntax heuristics; CFG metrics are optional and do not represent runtime hotness.",
                "Findings use stable fingerprints for identical project-relative code and rule versions; snapshot IDs are session-specific.",
                incompleteRuleScanning
                    ? "Some rule or analyzer results may be incomplete; inspect the analyzer notes."
                    : "All loaded documents with a semantic model were evaluated by the built-in rule scanner."
            ].Concat(additionalNotes).ToArray())
        {
            ProjectAnalyzerDiagnostics = projectAnalyzerDiagnostics,
            ProjectAnalyzersExecuted = projectAnalyzersExecuted
        };
        return new DeterministicAnalysisReport(
            summary, ordered.Take(findingLimit).ToArray(), metrics);
    }

    public SuggestedTestPlan SuggestTestPlan(string methodId)
    {
        var method = RequireMethod(methodId);
        return BoundaryTestAdvisor.Suggest(method.Summary, method.Symbol);
    }

    public IReadOnlyList<ReviewTarget> GetReviewTargets(int limit = 50, int offset = 0,
        string? query = null)
    {
        EnsureOpen();
        var all = _methods.Values
            .Where(entry => string.IsNullOrWhiteSpace(query)
                || entry.Summary.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Summary.File.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(entry => ReviewPrioritizer.Assess(entry.Summary, entry.Syntax))
            .OrderByDescending(target => target.Priority)
            .ThenBy(target => target.Method.Signature, StringComparer.Ordinal)
            .ToArray();
        return all.Skip(Math.Clamp(offset, 0, 1_000_000))
            .Take(Math.Clamp(limit, 1, 200)).ToArray();
    }

    public async Task<CompilationDiagnosticsReport> GetCompilationDiagnosticsAsync(
        int limit = 200, CancellationToken ct = default)
    {
        EnsureOpen();
        var returned = new List<CompilationDiagnostic>();
        var errorCount = 0;
        var warningCount = 0;
        var infoCount = 0;
        var take = Math.Clamp(limit, 1, 2000);

        foreach (var project in _solution!.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            foreach (var diagnostic in compilation.GetDiagnostics(ct))
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error) errorCount++;
                else if (diagnostic.Severity == DiagnosticSeverity.Warning) warningCount++;
                else infoCount++;

                if (returned.Count >= take) continue;
                var span = diagnostic.Location.IsInSource
                    ? diagnostic.Location.GetLineSpan() : default;
                returned.Add(new CompilationDiagnostic(
                    project.Name, diagnostic.Id, diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(), diagnostic.Location.IsInSource ? span.Path : null,
                    diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
                    diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null));
            }
        }
        return new CompilationDiagnosticsReport(
            errorCount, warningCount, infoCount, returned.Count, returned);
    }

    public async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(
        string methodId, int limit = 100, CancellationToken ct = default)
    {
        var entry = RequireMethod(methodId);
        var found = await SymbolFinder.FindReferencesAsync(entry.Symbol, _solution!,
            cancellationToken: ct);
        var result = new List<ReferenceHit>();
        var maximum = Math.Clamp(limit, 1, 500);
        foreach (var reference in found)
        {
            foreach (var location in reference.Locations)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= maximum) return result;
                if (!location.Location.IsInSource) continue;
                var filePath = location.Document.FilePath;
                if (string.IsNullOrEmpty(filePath)) continue;
                var text = await location.Document.GetTextAsync(ct);
                var sourceLine = text.Lines.GetLineFromPosition(location.Location.SourceSpan.Start);
                var preview = sourceLine.ToString().Trim();
                if (preview.Length > 250) preview = preview[..250];
                result.Add(new ReferenceHit(location.Document.Project.Name, filePath,
                    sourceLine.LineNumber + 1,
                    location.Location.SourceSpan.Start - sourceLine.Start + 1,
                    preview));
            }
        }
        return result;
    }

    private static string Truncate(string s, int limit) =>
        s.Length > limit ? s[..limit] + "\n...[truncated]" : s;

    private Entry RequireMethod(string methodId)
    {
        EnsureOpen();
        return _methods.TryGetValue(methodId, out var method)
            ? method : throw new KeyNotFoundException("MethodId not present in current snapshot.");
    }

    private void EnsureOpen()
    {
        if (_solution is null)
            throw new InvalidOperationException("Call workspace_open first.");
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _openLock.Dispose();
    }
}

