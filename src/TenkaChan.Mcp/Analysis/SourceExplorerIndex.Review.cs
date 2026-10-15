using System.Text;
// A same-named alias outside this namespace is hidden by Analysis.ReviewTarget.
using TrackedReviewTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;
using Microsoft.CodeAnalysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Analysis;

public sealed partial class SourceExplorerIndex
{
    private ReviewSourceCatalog? _reviewCatalog;
    public async Task<ReviewSourceCatalog> ReviewCatalogAsync(WorkspaceGuard guard, CancellationToken ct)
    {
        if (_reviewCatalog is not null) return _reviewCatalog;
        var inputs = new List<ReviewSourceInput>();
        var projectHashes = new Dictionary<string,string>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string,string[]>(StringComparer.Ordinal);
        var projects = Projects.ToDictionary(p=>p.Id, StringComparer.Ordinal);
        long budget=0;
        foreach (var p in _solution.Projects.Where(p=>projects.ContainsKey(p.Id.Id.ToString("N"))))
        {
            var id=p.Id.Id.ToString("N"); var fragments=new List<string>();
            foreach (var d in p.Documents.OrderBy(d=>d.FilePath,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (d.FilePath is null) throw new InvalidOperationException("On-disk source required for review tracking.");
                var text=await d.GetTextAsync(ct); budget+=text.Length;
                if (budget>64000000 || text.Length>4000000) throw new InvalidOperationException("Review input budget exceeded. Open a smaller project.");
                var hash=ReviewProgress.Hash(text.ToString());
                inputs.Add(new(id,d.FilePath,hash,text.Encoding ?? Encoding.UTF8));
                fragments.Add(ReviewProgress.CanonicalProject(d.FilePath)+"="+hash);
            }
            // Explicit project files are included; arbitrary imported targets and runtime state are not.
            var path=guard.RequireFile(projects[id].Path,".csproj");
            var xml=await ReviewSourceCatalog.ReadTextAsync(path,Encoding.UTF8,ct);
            var projectHash=ReviewProgress.Hash(xml); inputs.Add(new(id,path,projectHash,Encoding.UTF8));
            fragments.Add("project="+projectHash);
            if(p.ParseOptions is Microsoft.CodeAnalysis.CSharp.CSharpParseOptions parse)
                fragments.Add("parse="+parse.LanguageVersion+"|"+parse.Kind+"|"+parse.DocumentationMode+"|"+string.Join(",",parse.PreprocessorSymbolNames.Order(StringComparer.Ordinal)));
            if(p.CompilationOptions is Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions options)
                fragments.Add("compile="+options.OutputKind+"|"+options.OptimizationLevel+"|"+options.Platform+"|"+options.AllowUnsafe+"|"+options.CheckOverflow+"|"+options.NullableContextOptions);
            fragments.Add("references="+string.Join("|",p.MetadataReferences.Select(r=>r.Display??"").Order(StringComparer.Ordinal)));
            projectHashes[id]=ReviewProgress.Hash(string.Join("\n",fragments));
            dependencies[id]=p.ProjectReferences.Select(r=>r.ProjectId.Id.ToString("N")).ToArray();
        }
        var contexts=new Dictionary<string,string>(StringComparer.Ordinal);
        var closure=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        foreach (var id in projects.Keys)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);var pending=new Stack<string>();pending.Push(id);
            while(pending.Count>0)
            {
                var next=pending.Pop();if(!seen.Add(next))continue;
                foreach(var dep in dependencies.GetValueOrDefault(next)??[])pending.Push(dep);
            }
            closure[id]=seen;
            contexts[id]=ReviewProgress.Hash(string.Join("\n",seen.Where(projects.ContainsKey)
                .OrderBy(k=>ReviewProgress.CanonicalProject(projects[k].Path),StringComparer.Ordinal)
                .Select(k=>ReviewProgress.CanonicalProject(projects[k].Path)+"="+projectHashes.GetValueOrDefault(k))));
        }
        var rows=new List<TrackedReviewTarget>(); var positions=new Dictionary<string,int>(StringComparer.Ordinal); long sourceBudget=0; var limited=Limited;
        async Task Add(string selection,string kind,string projectId,string name,ISymbol symbol,
            IEnumerable<SyntaxReference> declarations,string? fallbackIdentity=null)
        {
            if(rows.Count>=20000){limited=true;return;}
            var p=projects[projectId];var parts=new List<(string Path,int Start,int Line,string Text)>();
            foreach(var reference in declarations)
            {
                ct.ThrowIfCancellationRequested();
                var syntax=await reference.GetSyntaxAsync(ct);
                if(_solution.GetDocument(syntax.SyntaxTree) is not {FilePath:not null} doc)continue;
                parts.Add((doc.FilePath,syntax.SpanStart,syntax.GetLocation().GetLineSpan().StartLinePosition.Line+1,syntax.ToFullString()));
            }
            parts=parts.DistinctBy(x=>(x.Path,x.Start)).OrderBy(x=>x.Path,StringComparer.Ordinal).ThenBy(x=>x.Start).ToList();
            var source=ReviewProgress.Normalize(string.Join("\n",parts.Select(x=>
                "// " + Path.GetRelativePath(Path.GetDirectoryName(p.Path)!,x.Path).Replace('\\','/')+"\n"+x.Text)));
            // Accessors without a documentation ID use an exact metadata/signature identity.
            // Anonymous/local functions remain unsupported rather than inheriting an outer method's badge.
            var identity=symbol.GetDocumentationCommentId() ?? fallbackIdentity;
            var supported=identity is not null && parts.Count>0 && source.Length<=ReviewProgress.MaxSourceCharacters
                && sourceBudget+source.Length<=64000000;
            sourceBudget+=supported?source.Length:0;
            var key=ReviewProgress.TargetId(p.Path,kind,identity??("unsupported:"+selection));
            var target=new TrackedReviewTarget(key,selection,kind,projectId,p.Name,p.Path,name,
                parts.Count>0?parts[0].Path:p.Path,parts.Count>0?parts[0].Line:1,
                ReviewProgress.Hash(source),contexts[projectId],supported,
                supported?null:"安定した識別子または完全なソースを取得できません。局所関数・匿名関数や保存上限超過は個別記録できません。",
                supported?source:"");
            // Partial methods may be indexed twice. Do not attach one checkpoint to two different bodies.
            if(positions.TryGetValue(key,out var prior))
            {
                if(rows[prior].SourceHash!=target.SourceHash)
                    rows[prior]=rows[prior] with { Trackable=false,UnavailableReason="複数の宣言が同じ識別子に対応しています。型単位で確認してください。",Source="" };
                return;
            }
            positions[key]=rows.Count; rows.Add(target);
        }
        foreach(var entry in _types.Values)
            await Add(entry.Row.Id,"type",entry.Row.ProjectId,entry.Row.Name,entry.Symbol,entry.Symbol.DeclaringSyntaxReferences);
        foreach(var entry in _methods.Values)
        {
            var symbol=entry.Seed.Symbol;
            var stable=symbol.MethodKind is not (MethodKind.LocalFunction or MethodKind.AnonymousFunction);
            var declarations=symbol.DeclaringSyntaxReferences.AsEnumerable();
            if(symbol.PartialDefinitionPart is { } definition)declarations=declarations.Concat(definition.DeclaringSyntaxReferences);
            if(symbol.PartialImplementationPart is { } implementation)declarations=declarations.Concat(implementation.DeclaringSyntaxReferences);
            var fallback=stable?entry.Row.MetadataType+"::"+entry.Row.MetadataName+"|"+symbol.MethodKind+"|"+
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat):null;
            await Add(entry.Row.Id,"method",entry.Row.ProjectId,entry.Row.Signature,symbol,declarations,fallback);
        }
        return _reviewCatalog=new(SnapshotId,rows,inputs,closure,limited);
    }
}
