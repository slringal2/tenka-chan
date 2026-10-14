using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace TenkaChan.Mcp.Analysis;

public sealed record ExplorerProject(string Id, string Name, string Path, int Documents);
public sealed record ExplorerType(string Id, string ProjectId, string Project, string Name,
    string Kind, int MethodCount, int DeclarationCount);
public sealed record ExplorerTypePage(string SnapshotId, int Total, int Offset,
    IReadOnlyList<ExplorerType> Items, bool IndexLimited);
public sealed record ExplorerMethod(string Id, string TypeId, string ProjectId, string Signature,
    string File, int Line, string Kind, string MetadataType, string MetadataName,
    string? JitFilter, string? JitUnavailableReason);
public sealed record ExplorerSource(string File, int Line, string Text, bool Truncated);
public sealed record ExplorerMember(string Kind, string Signature);
public sealed record ExplorerTypeDetails(ExplorerType Type, string? BaseType,
    IReadOnlyList<string> Interfaces, IReadOnlyList<ExplorerMember> Members,
    IReadOnlyList<ExplorerMethod> Methods, IReadOnlyList<ExplorerSource> Sources,
    IReadOnlyList<string> Notes);
public sealed record ExplorerReference(string Project, string File, int Line, string Preview,
    string? MethodId);
public sealed record ExplorerReferences(IReadOnlyList<ExplorerReference> Items, bool Limited, string Note);
public sealed record ExplorerMethodSeed(MethodSummary Summary, Document Document,
    SyntaxNode Syntax, IMethodSymbol Symbol);

/// <summary>
/// Immutable index over an ALREADY opened Roslyn solution. Does not reopen MSBuild,
/// execute the target, follow PDB source paths, or parse class names from display strings.
/// Caller serializes workspace actions and validates SnapshotId before using this index.
/// </summary>
public sealed class SourceExplorerIndex
{
    public const int MaximumTypes = 20000;
    private readonly Solution _solution;
    private readonly Dictionary<string, (ExplorerType Row, INamedTypeSymbol Symbol)> _types = new();
    private readonly Dictionary<string, (ExplorerMethod Row, ExplorerMethodSeed Seed)> _methods = new();
    public string SnapshotId { get; }
    public IReadOnlyList<ExplorerProject> Projects { get; }
    public bool Limited { get; private set; }
    private SourceExplorerIndex(string snapshot, Solution solution)
    {
        SnapshotId = snapshot;
        _solution = solution;
        Projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .Select(p => new ExplorerProject(p.Id.Id.ToString("N"), p.Name, p.FilePath!, p.DocumentIds.Count))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Path, StringComparer.Ordinal).ToArray();
    }

    public static async Task<SourceExplorerIndex> CreateAsync(string snapshot, Solution solution,
        IEnumerable<ExplorerMethodSeed> methods, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) throw new ArgumentException("Snapshot is required.");
        var index = new SourceExplorerIndex(snapshot, solution);
        var seeds = methods.ToArray();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null))
        {
            var projectId = project.Id.Id.ToString("N");
            var byType = seeds.Where(m => m.Document.Project.Id == project.Id)
                .GroupBy(m => MetadataTypeName(m.Symbol.ContainingType), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(ct);
                var model = await document.GetSemanticModelAsync(ct);
                if (root is null || model is null) continue;
                foreach (var syntax in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                {
                    ct.ThrowIfCancellationRequested();
                    if (model.GetDeclaredSymbol(syntax, ct) is not INamedTypeSymbol type) continue;
                    var metadataName = MetadataTypeName(type);
                    var typeId = MakeId(snapshot, projectId, metadataName);
                    if (index._types.ContainsKey(typeId)) continue; // Partial declarations share a symbol.
                    if (index._types.Count >= MaximumTypes) { index.Limited = true; return index; }
                    var ownMethods = byType.GetValueOrDefault(metadataName) ?? [];
                    var row = new ExplorerType(typeId, projectId, project.Name,
                        type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                        type.IsRecord ? "record " + type.TypeKind.ToString().ToLowerInvariant() : type.TypeKind.ToString().ToLowerInvariant(),
                        ownMethods.Length, type.DeclaringSyntaxReferences.Length);
                    index._types.Add(typeId, (row, type));
                    foreach (var method in ownMethods)
                    {
                        var (filter, reason) = JitSelection(method.Symbol);
                        var m = new ExplorerMethod(method.Summary.Id, typeId, projectId,
                            method.Summary.Signature, method.Summary.File, method.Summary.Line,
                            method.Symbol.MethodKind.ToString(), metadataName, method.Symbol.MetadataName, filter, reason);
                        index._methods[method.Summary.Id] = (m, method);
                    }
                }
            }
        }
        return index;
    }

    public ExplorerTypePage Types(string? projectId, string? query, int offset = 0, int limit = 80)
    {
        var q = (query ?? "").Trim();
        if (q.Length > 200) throw new ArgumentException("Search is limited to 200 characters.");
        if (!string.IsNullOrEmpty(projectId) && !Projects.Any(p => p.Id == projectId))
            throw new KeyNotFoundException("Project selection is no longer available.");
        var rows = _types.Values.Select(t => t.Row)
            .Where(t => (string.IsNullOrEmpty(projectId) || t.ProjectId == projectId)
                && (q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.ProjectId, StringComparer.Ordinal).ToArray();
        var skip = Math.Clamp(offset, 0, MaximumTypes);
        return new(SnapshotId, rows.Length, skip, rows.Skip(skip).Take(Math.Clamp(limit, 1, 200)).ToArray(), Limited);
    }

    public ExplorerProject Project(string id) => Projects.FirstOrDefault(p => p.Id == id)
        ?? throw new KeyNotFoundException("Project selection is no longer available.");
    public ExplorerMethod Method(string id) => _methods.TryGetValue(id, out var method) ? method.Row
        : throw new KeyNotFoundException("Method selection is no longer available. Refresh the class list.");
    public ExplorerMethodSeed MethodSeed(string id) => _methods.TryGetValue(id, out var method) ? method.Seed
        : throw new KeyNotFoundException("Method selection is no longer available.");

    public async Task<ExplorerTypeDetails> TypeAsync(string id, CancellationToken ct = default)
    {
        if (!_types.TryGetValue(id, out var type)) throw new KeyNotFoundException("Type selection is no longer available.");
        var sources = new List<ExplorerSource>();
        var remaining = 80000;
        foreach (var reference in type.Symbol.DeclaringSyntaxReferences.Take(16))
        {
            ct.ThrowIfCancellationRequested();
            var syntax = await reference.GetSyntaxAsync(ct);
            // Only source documents that the existing workspace opened, not arbitrary generated/PDB paths.
            if (_solution.GetDocument(syntax.SyntaxTree) is not { } document) continue;
            var text = syntax.ToString();
            var take = Math.Min(remaining, Math.Min(text.Length, 20000));
            sources.Add(new(document.FilePath ?? document.Name,
                syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                text[..take], take < text.Length));
            remaining -= take;
            if (remaining <= 0) break;
        }
        var members = type.Symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared && m is not IMethodSymbol)
            .Select(m => new ExplorerMember(m.Kind.ToString(), m.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)))
            .Take(500).ToArray();
        var methods = _methods.Values.Select(m => m.Row).Where(m => m.TypeId == id)
            .OrderBy(m => m.Signature, StringComparer.Ordinal).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line)
            .Take(1000).ToArray();
        return new(type.Row, type.Symbol.BaseType?.ToDisplayString(),
            type.Symbol.Interfaces.Select(i => i.ToDisplayString()).Take(100).ToArray(), members, methods, sources,
            ["ソースは読み込み時のRoslynスナップショットです。変更後は再読み込みしてください。",
             "partial型はまとめて表示します。入れ子型は別項目、メソッドのオーバーロードは別の選択肢です。",
             "表示上限: 宣言16個・ソース計80,000文字・メンバー500個・メソッド1,000個。暗黙の生成メンバーとトップレベル文は型一覧の対象外です。"]);
    }

    public async Task<ExplorerReferences> ReferencesAsync(string id, bool isType, CancellationToken ct = default)
    {
        ISymbol symbol = isType ? (_types.TryGetValue(id, out var type) ? type.Symbol
            : throw new KeyNotFoundException("Type selection is no longer available.")) : MethodSeed(id).Symbol;
        var references = await SymbolFinder.FindReferencesAsync(symbol, _solution, cancellationToken: ct);
        var rows = new List<ExplorerReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in references)
        foreach (var location in group.Locations)
        {
            ct.ThrowIfCancellationRequested();
            if (!location.Location.IsInSource) continue;
            var doc = location.Document;
            var span = location.Location.SourceSpan;
            if (!seen.Add(doc.Id + ":" + span.Start + ":" + span.Length)) continue;
            if (rows.Count == 300) return new(rows, true, ReferenceNote);
            var text = await doc.GetTextAsync(ct);
            var line = text.Lines.GetLineFromPosition(span.Start);
            var preview = line.ToString().Trim();
            var enclosing = _methods.Values.Where(m => m.Seed.Document.Id == doc.Id && m.Seed.Syntax.Span.Contains(span))
                .OrderBy(m => m.Seed.Syntax.Span.Length).FirstOrDefault();
            rows.Add(new(doc.Project.Name, doc.FilePath ?? doc.Name, line.LineNumber + 1,
                preview.Length <= 300 ? preview : preview[..300], enclosing.Row?.Id));
        }
        return new(rows, false, ReferenceNote);
    }
    private const string ReferenceNote = "参照は読み込み済みソリューション内の静的な参照です。実行頻度、reflection、実行時の仮想呼び出し先は確定しません。";

    public static string MetadataTypeName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType) names.Push(current.MetadataName);
        var ns = type.ContainingNamespace;
        return (ns is null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString() + ".") + string.Join("+", names);
    }
    public static string MakeId(string snapshot, string projectId, string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot + "|" + projectId + "|" + name)))[..24];

    public static (string? Filter, string? Reason) JitSelection(IMethodSymbol method)
    {
        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet))
            return (null, "この種類のメソッドは自動JIT選択の対象外です。ソース・ILから調査できます。");
        for (var type = method.ContainingType; type is not null; type = type.ContainingType)
            if (type.Arity != 0) return (null, "ジェネリック型の具体的な型引数が必要です。ILから調査できます。");
        if (method.IsGenericMethod || method.IsAbstract || method.IsExtern)
            return (null, "具体的な実装または型引数が必要です。ILから調査できます。");
        var selector = MetadataTypeName(method.ContainingType) + ":" + method.MetadataName;
        try { return (Experiments.TargetFrameworkPolicy.ValidateJitFilter(selector), null); }
        catch (ArgumentException) { return (null, "このメソッド名は自動JIT選択に対応していません。ILから調査できます。"); }
    }
}
