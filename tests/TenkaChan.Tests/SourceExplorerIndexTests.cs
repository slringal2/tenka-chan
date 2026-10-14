using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class SourceExplorerIndexTests
{
    private static async Task<(AdhocWorkspace Workspace, SourceExplorerIndex Index)> Build(params string[] sources)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(),
            "TestProject", "TestProject", LanguageNames.CSharp,
            filePath: Path.Combine(Path.GetTempPath(), "TestProject.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview)))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        for (var i = 0; i < sources.Length; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "File" + i + ".cs",
                SourceText.From(sources[i]), filePath: Path.Combine(Path.GetTempPath(), "File" + i + ".cs"));
        var seeds = await Seeds(solution);
        return (workspace, await SourceExplorerIndex.CreateAsync("snapshot", solution, seeds));
    }
    private static async Task<ExplorerMethodSeed[]> Seeds(Solution solution)
    {
        var seeds = new List<ExplorerMethodSeed>();
        foreach (var project in solution.Projects)
        foreach (var doc in project.Documents)
        {
            var root = await doc.GetSyntaxRootAsync(); var model = await doc.GetSemanticModelAsync();
            foreach (var method in root!.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var symbol = model!.GetDeclaredSymbol(method)!;
                var summary = new MethodSummary(Guid.NewGuid().ToString("N"), project.Name,
                    symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), doc.FilePath!,
                    method.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                seeds.Add(new(summary, doc, method, symbol));
            }
        }
        return seeds.ToArray();
    }

    [Fact]
    public async Task TypesWithNoMethodsStillAppear()
    {
        var fixture = await Build("class Empty {} struct S { public int Value; } interface I {} enum E { A } delegate void D();");
        using var workspace = fixture.Workspace;
        Assert.Equal(5, fixture.Index.Types(null, "").Total);
        Assert.All(fixture.Index.Types(null, "").Items, t => Assert.Equal(0, t.MethodCount));
    }
    [Fact]
    public async Task PartialDeclarationsAreMergedWithoutLosingSourcesOrMethods()
    {
        var fixture = await Build("namespace N; public partial class A { public int One() => 1; }",
            "namespace N; public partial class A { public string Name {get;set;} public int Two() => 2; }");
        using var workspace = fixture.Workspace;
        var row = Assert.Single(fixture.Index.Types(null, "N.A").Items);
        var detail = await fixture.Index.TypeAsync(row.Id);
        Assert.Equal(2, row.DeclarationCount); Assert.Equal(2, row.MethodCount);
        Assert.Equal(2, detail.Sources.Count); Assert.Equal(2, detail.Methods.Count);
        Assert.Contains(detail.Members, m => m.Signature.Contains("Name"));
    }
    [Fact]
    public async Task EqualSimpleNamesInDifferentNamespacesRemainSeparate()
    {
        var fixture = await Build("namespace Left { class A { public void Run(){} } } namespace Right { class A { public void Run(){} } }");
        using var workspace = fixture.Workspace;
        var rows = fixture.Index.Types(null, "A").Items;
        Assert.Equal(2, rows.Count); Assert.NotEqual(rows[0].Id, rows[1].Id);
        var a = await fixture.Index.TypeAsync(rows[0].Id); var b = await fixture.Index.TypeAsync(rows[1].Id);
        Assert.NotEqual(a.Methods[0].JitFilter, b.Methods[0].JitFilter);
    }
    [Fact]
    public async Task NestedGenericTypesUseMetadataNamesAndRequireConcreteTypesForJit()
    {
        var fixture = await Build("namespace N; class Outer<T> { public class Inner<U> { public void Run(){} } }");
        using var workspace = fixture.Workspace;
        var row = fixture.Index.Types(null, "Inner").Items.Single();
        var method = (await fixture.Index.TypeAsync(row.Id)).Methods.Single();
        Assert.Equal("N.Outer`1+Inner`1", method.MetadataType); Assert.Null(method.JitFilter);
        Assert.NotNull(method.JitUnavailableReason);
    }
    [Fact]
    public async Task OverloadsHaveSeparateSelectionsButSharedJitSelectorIsNotAUniqueOverloadClaim()
    {
        var fixture = await Build("namespace N; class A { public int Run(int x)=>x; public string Run(string x)=>x; }");
        using var workspace = fixture.Workspace;
        var methods = (await fixture.Index.TypeAsync(fixture.Index.Types(null, "").Items.Single().Id)).Methods;
        Assert.Equal(2, methods.Count); Assert.NotEqual(methods[0].Id, methods[1].Id);
        Assert.Equal("N.A:Run", methods[0].JitFilter); Assert.Equal(methods[0].JitFilter, methods[1].JitFilter);
    }
    [Fact]
    public async Task ReferencesLinkToTheContainingMethod()
    {
        var fixture = await Build("class A { public static int Run()=>1; } class B { public int Caller()=>A.Run(); }");
        using var workspace = fixture.Workspace;
        var a = await fixture.Index.TypeAsync(fixture.Index.Types(null, "A").Items.Single().Id);
        var b = await fixture.Index.TypeAsync(fixture.Index.Types(null, "B").Items.Single().Id);
        var refs = await fixture.Index.ReferencesAsync(a.Methods.Single().Id, false);
        Assert.Contains(refs.Items, r => r.MethodId == b.Methods.Single().Id && r.Preview.Contains("A.Run"));
    }
    [Fact]
    public async Task InterfacesAndBaseTypesAreVisible()
    {
        var fixture = await Build("interface I {} class Base {} class Derived : Base, I {} ");
        using var workspace = fixture.Workspace;
        var detail = await fixture.Index.TypeAsync(fixture.Index.Types(null, "Derived").Items.Single().Id);
        Assert.Equal("Base", detail.BaseType); Assert.Contains("I", detail.Interfaces);
    }
    [Theory]
    [InlineData("class A { public T Run<T>(T value)=>value; }")]
    [InlineData("abstract class A { public abstract void Run(); }")]
    public async Task JitDoesNotInventImplementationsOrTypeArguments(string source)
    {
        var fixture = await Build(source); using var workspace = fixture.Workspace;
        var method = (await fixture.Index.TypeAsync(fixture.Index.Types(null, "").Items.Single().Id)).Methods.Single();
        Assert.Null(method.JitFilter); Assert.NotNull(method.JitUnavailableReason);
    }
    [Fact]
    public async Task QueryAndPagingAreBoundedAndStable()
    {
        var fixture = await Build("class Zebra {} class Alpha {} class Middle {}"); using var workspace = fixture.Workspace;
        var first = fixture.Index.Types(null, "", 0, 2); var next = fixture.Index.Types(null, "", 2, 2);
        Assert.Equal(new[]{"Alpha", "Middle"}, first.Items.Select(t => t.Name).ToArray());
        Assert.Equal("Zebra", Assert.Single(next.Items).Name);
        Assert.Throws<ArgumentException>(() => fixture.Index.Types(null, new string('x', 201)));
        Assert.Throws<KeyNotFoundException>(() => fixture.Index.Project("not-an-id"));
        Assert.Throws<KeyNotFoundException>(() => fixture.Index.Method("not-an-id"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Index.TypeAsync("not-an-id"));
    }
    [Fact]
    public void IdentityIncludesProjectAndSnapshot()
    {
        Assert.NotEqual(SourceExplorerIndex.MakeId("a", "p", "T"), SourceExplorerIndex.MakeId("b", "p", "T"));
        Assert.NotEqual(SourceExplorerIndex.MakeId("a", "p", "T"), SourceExplorerIndex.MakeId("a", "q", "T"));
    }
    [Fact]
    public async Task CancellationDoesNotReturnACompletedIndex()
    {
        using var workspace = new AdhocWorkspace();
        var id = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), "p", "p", LanguageNames.CSharp,
            filePath: Path.Combine(Path.GetTempPath(), "p.csproj")))
            .AddDocument(DocumentId.CreateNewId(id), "A.cs", SourceText.From("class A {}"));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceExplorerIndex.CreateAsync("s", solution, [], cts.Token));
    }
    [Fact]
    public async Task CompanionDiscoveryIsReadOnlyAndProducesOpaqueChoices()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-explorer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Tests")); Directory.CreateDirectory(Path.Combine(root, "Benchmarks"));
        try
        {
            var file = Path.Combine(root, "App.csproj"); File.WriteAllText(file, "<Project/>");
            File.WriteAllText(Path.Combine(root, "Tests", "Unit.csproj"), "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Benchmarks", "Bench.csproj"), "<Project><ItemGroup><PackageReference Include=\"BenchmarkDotNet\" Version=\"0.15.8\"/></ItemGroup></Project>");
            using var workspace = new AdhocWorkspace();
            var id = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), "App", "App", LanguageNames.CSharp, filePath: file));
            var index = await SourceExplorerIndex.CreateAsync("s", solution, []);
            var guard = new WorkspaceGuard([root], true, false);
            var choices = ExplorerProjectActions.Companions(index, guard);
            Assert.Equal(2, choices.Count); Assert.Contains(choices, c => c.Kind == "test");
            Assert.Contains(choices, c => c.Kind == "benchmark"); Assert.All(choices, c => Assert.Equal(24, c.Id.Length));
            Assert.False(Directory.Exists(Path.Combine(root, ".tenka")));
            var output = ExplorerProjectActions.EnsureOutputDirectory(file, guard, "profiles");
            Assert.Equal(Path.Combine(root, ".tenka", "profiles"), output);
            Assert.Throws<ArgumentException>(() => ExplorerProjectActions.EnsureOutputDirectory(file, guard, "../elsewhere"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void OutputCreationCannotEscapeAllowedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-explorer-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "App.csproj"); File.WriteAllText(file, "<Project/>");
            var guard = new WorkspaceGuard([Path.Combine(root, "allowed")], true, false);
            Assert.Throws<UnauthorizedAccessException>(() => ExplorerProjectActions.EnsureOutputDirectory(file, guard, "profiles"));
            Assert.False(Directory.Exists(Path.Combine(root, ".tenka")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void OutputCreationDoesNotOverwriteAnExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-explorer-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "App.csproj"); File.WriteAllText(file, "<Project/>");
            var occupied = Path.Combine(root, ".tenka"); File.WriteAllText(occupied, "keep");
            var guard = new WorkspaceGuard([root], true, false);
            Assert.Throws<IOException>(() => ExplorerProjectActions.EnsureOutputDirectory(file, guard, "profiles"));
            Assert.Equal("keep", File.ReadAllText(occupied));
        }
        finally { Directory.Delete(root, true); }
    }

}
