using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
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
            _workspace.WorkspaceFailed += (_, args) =>
            {
                if (_warnings.Count < 100) _warnings.Add(args.Diagnostic.Message);
            };
            _solution = Path.GetExtension(safePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await _workspace.OpenProjectAsync(safePath, cancellationToken: ct)).Solution
                : await _workspace.OpenSolutionAsync(safePath, cancellationToken: ct);
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
                if (model.GetOperation(entry.Syntax.Body, ct) is IBlockOperation op)
                {
                    var graph = ControlFlowGraph.Create(op, ct);
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

