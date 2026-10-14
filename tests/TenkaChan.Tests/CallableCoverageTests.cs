using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class CallableCoverageTests
{
    private static (SourceCallable[] Callables, SemanticModel Model) Parse(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "/fixture/Callables.cs");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Callables", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var model = compilation.GetSemanticModel(tree);
        return (CallableSyntax.Enumerate(tree.GetRoot(), model).ToArray(), model);
    }

    [Theory]
    [InlineData("public C(){ for(int i=0;i<3;i++){ var x=new byte[16]; } }", "Constructor")]
    [InlineData("static C(){ for(int i=0;i<3;i++){ var x=new byte[16]; } }", "StaticConstructor")]
    [InlineData("~C(){ for(int i=0;i<3;i++){ var x=new byte[16]; } }", "Destructor")]
    [InlineData("public int P { get { for(int i=0;i<3;i++){ var x=new byte[16]; } return 0; } }", "PropertyGet")]
    [InlineData("public int P { set { for(int i=0;i<3;i++){ var x=new byte[16]; } } }", "PropertySet")]
    [InlineData("public static C operator +(C a,C b){ for(int i=0;i<3;i++){ var x=new byte[16]; } return a; }", "UserDefinedOperator")]
    [InlineData("public static implicit operator int(C a){ for(int i=0;i<3;i++){ var x=new byte[16]; } return 0; }", "Conversion")]
    [InlineData("public event System.Action E { add { for(int i=0;i<3;i++){ var x=new byte[16]; } } remove {} }", "EventAdd")]
    [InlineData("public void Outer(){ void L(){ for(int i=0;i<3;i++){ var x=new byte[16]; } } }", "LocalFunction")]
    [InlineData("public void Outer(){ System.Action f=()=>{ for(int i=0;i<3;i++){ var x=new byte[16]; } }; }", "AnonymousFunction")]
    public void CostSitesBelongToEachExplicitCallable(string member, string kind)
    {
        var (callables, model) = Parse("public class C { " + member + " }");
        var definitions = callables.Select((c,i) => new HotspotMethodDefinition(
            new MethodSummary(i.ToString(), "Callables", c.Symbol.ToDisplayString(), "/fixture/Callables.cs", 1)
            { CallableKind = c.Symbol.MethodKind.ToString() }, c.Syntax, c.Symbol, model, "/fixture")).ToArray();
        var report = PerformanceHotspotAnalyzer.Analyze("fixture", definitions);
        var owner = Assert.Single(report.Methods.Where(m => m.Method.CallableKind == kind));
        Assert.Contains(report.Sites, s => s.MethodId == owner.Method.Id && s.Category == "allocation_per_iteration");
        Assert.Equal(1, owner.MaximumLoopDepth);
    }

    [Fact]
    public void ExpressionPropertiesAndIndexersHaveGetSymbolsWithoutDuplicates()
    {
        var (callables, _) = Parse("public class C { public int P=>1; public int this[int i]=>i; }");
        Assert.Equal(2, callables.Length);
        Assert.All(callables, c => Assert.Equal(MethodKind.PropertyGet, c.Symbol.MethodKind));
        Assert.All(callables, c => Assert.NotNull(CallableSyntax.Body(c.Syntax)));
    }

    [Fact]
    public void DeferredBodiesNeverInheritOuterLoopOrParentCfg()
    {
        var (callables, model) = Parse("""
            public class C { public void Outer(){ for(int n=0;n<3;n++) {
                System.Action f=()=>{ for(int i=0;i<3;i++){var a=new byte[8];} };
                void L(){ for(int i=0;i<3;i++){var a=new byte[8];} }
            } } }
            """);
        var parent = callables.Single(c => c.Symbol.MethodKind == MethodKind.Ordinary);
        Assert.Empty(CallableSyntax.OwnNodes(parent.Syntax).OfType<ArrayCreationExpressionSyntax>());
        var summary = new MethodSummary("p", "P", "C.Outer", "/fixture/Callables.cs", 1);
        Assert.Equal(0, ReviewPrioritizer.Assess(summary, parent.Syntax).ObjectCreationSites);
        foreach(var child in callables.Where(c=>c.Symbol.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction))
        {
            Assert.False(CallableSyntax.CanCorrelateByMetadataName(child.Symbol));
            Assert.Null(CallableSyntax.TryCreateGraph(child.Syntax, model));
            Assert.Single(CallableSyntax.OwnNodes(child.Syntax).OfType<ForStatementSyntax>());
        }
    }

    [Fact]
    public async Task RealWorkspaceIndexesInspectsAndPrioritizesNonOrdinaryCallables()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        var root=Path.Combine(Path.GetTempPath(),"tenka-callables-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project=Path.Combine(root,"Sample.csproj");
            File.WriteAllText(project,"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root,"Work.cs"),"""
                using System; using System.Linq;
                namespace CallableFixture;
                public class Work {
                    public Work(){ for(int i=0;i<3;i++){ var x=new[]{1,2}.ToList(); } }
                    public int Value { get { for(int i=0;i<3;i++){var x=new byte[8];} return 1; } }
                    public int Expr=>Value;
                    public void Outer(){ Action f=()=>{for(int i=0;i<2;i++){var x=new byte[8];}}; void L(){for(int i=0;i<2;i++){var x=new byte[8];}} }
                }
                """);
            using var service=new RoslynReviewService(new WorkspaceGuard([root],true,false));
            var opened=await service.OpenAsync(project);
            var all=service.FindMethods("",200);
            Assert.Contains(all,m=>m.CallableKind=="Constructor");
            Assert.Contains(all,m=>m.CallableKind=="PropertyGet");
            Assert.Contains(all,m=>m.CallableKind=="LocalFunction");
            Assert.Contains(all,m=>m.CallableKind=="AnonymousFunction");
            var hotspots=await service.AnalyzePerformanceHotspotsAsync(200,1000);
            Assert.Equal(opened.MethodCount,hotspots.TotalMethods);
            var constructor=all.Single(m=>m.CallableKind=="Constructor");
            Assert.Contains(hotspots.Sites,s=>s.MethodId==constructor.Id&&s.Category=="linq_materialization");
            var audit=await service.AnalyzeDeterministicAsync();
            Assert.Contains(audit.Findings,f=>f.RuleId=="PERF1001"&&f.MethodId==constructor.Id);
            foreach(var item in all) {
                var inspection=await service.InspectAsync(item.Id);
                Assert.NotEmpty(inspection.Source);
                if(item.CallableKind is "LocalFunction" or "AnonymousFunction") Assert.Empty(inspection.Blocks);
            }
            var getter=all.Single(m=>m.Signature.Contains("Value.get"));
            var native=NativeJitDisassemblyParser.Parse("; Assembly listing for method CallableFixture.Work:get_Value():int (FullOpts)\n; Emitting BLENDED_CODE for X64\n       C3 ret\n; Total bytes of code 1", "net8.0","test","Work:get_Value");
            Assert.Equal(getter.Id,Assert.Single(service.CorrelateNativeCode(native).Methods).RoslynMethodId);
        }
        finally { Directory.Delete(root,true); }
    }
}
