using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewSourceCatalogTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"tenka-catalog-"+Guid.NewGuid().ToString("N"));
    private readonly List<AdhocWorkspace> _workspaces=[];
    private WorkspaceGuard Guard=>new([_root],true,false);
    private async Task<ReviewSourceCatalog> Catalog(string code,string snapshot)
    {
        Directory.CreateDirectory(_root);var projectPath=Path.Combine(_root,"p.csproj");var file=Path.Combine(_root,"A.cs");
        File.WriteAllText(projectPath,"<Project/>");File.WriteAllText(file,code);
        var workspace=new AdhocWorkspace();_workspaces.Add(workspace);var id=ProjectId.CreateNewId();
        var solution=workspace.CurrentSolution.AddProject(ProjectInfo.Create(id,VersionStamp.Create(),"P","P",LanguageNames.CSharp,
            filePath:projectPath,compilationOptions:new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),parseOptions:new CSharpParseOptions(LanguageVersion.Preview)))
            .AddMetadataReference(id,MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(DocumentId.CreateNewId(id),"A.cs",SourceText.From(code),filePath:file);
        var doc=solution.Projects.Single().Documents.Single();var root=await doc.GetSyntaxRootAsync();var model=await doc.GetSemanticModelAsync();
        var seeds=root!.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(s=>new ExplorerMethodSeed(
            new MethodSummary(Guid.NewGuid().ToString("N"),"P",model!.GetDeclaredSymbol(s)!.ToDisplayString(),file,1),doc,s,model!.GetDeclaredSymbol(s)!));
        return await (await SourceExplorerIndex.CreateAsync(snapshot,solution,seeds)).ReviewCatalogAsync(Guard,default);
    }
    [Fact] public async Task StableTargetKeysSurviveNewRoslynProjectAndSnapshotIds()
    {
        const string code="namespace N; public class C { public int Run(int value)=>value; public string Run(string value)=>value; }";
        var a=await Catalog(code,"first");var b=await Catalog(code,"second");
        Assert.Equal(a.Targets.Select(t=>t.Id).Order(),b.Targets.Select(t=>t.Id).Order());
        Assert.Equal(a.Targets.Select(t=>t.SourceHash).Order(),b.Targets.Select(t=>t.SourceHash).Order());
        Assert.Equal(a.Targets.Select(t=>t.ContextHash).Order(),b.Targets.Select(t=>t.ContextHash).Order());
        Assert.Equal(3,a.Targets.Count);Assert.Equal(3,a.Targets.Select(t=>t.Id).Distinct().Count());
    }
    [Fact] public async Task ChangedFileRequiresReloadAndCannotRemainFreshThroughCachedMtime()
    {
        var c=await Catalog("class C { public int M()=>1; }","s");
        Assert.Empty(await c.StaleProjectsAsync(Guard,true,default));
        var file=Path.Combine(_root,"A.cs");var stamp=File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file,"class C { public int M()=>2; }");File.SetLastWriteTimeUtc(file,stamp);
        Assert.NotEmpty(await c.StaleProjectsAsync(Guard,true,default));
    }
    [Fact] public async Task AnotherMethodChangesContextWithoutChangingTheSelectedBody()
    {
        var a=await Catalog("class C { public int M()=>1; public int N()=>1; }","first");
        var b=await Catalog("class C { public int M()=>1; public int N()=>2; }","second");
        var before=a.Targets.Single(t=>t.Kind=="method"&&t.Name.Contains("M()"));var after=b.Targets.Single(t=>t.Id==before.Id);
        Assert.Equal(before.SourceHash,after.SourceHash);Assert.NotEqual(before.ContextHash,after.ContextHash);
    }
    [Fact] public async Task ExplicitProjectFileChangesRequireReload()
    {
        var c=await Catalog("class C {}","s");File.WriteAllText(Path.Combine(_root,"p.csproj"),"<Project><PropertyGroup/></Project>");
        Assert.NotEmpty(await c.StaleProjectsAsync(Guard,true,default));
    }
    [Fact] public async Task MissingSourceIsStaleRatherThanVerified()
    {var c=await Catalog("class C {}","s");File.Delete(Path.Combine(_root,"A.cs"));Assert.NotEmpty(await c.StaleProjectsAsync(Guard,true,default));}
    public void Dispose(){foreach(var w in _workspaces)w.Dispose();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
