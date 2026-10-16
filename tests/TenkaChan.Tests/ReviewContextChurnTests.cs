using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using TrackedReviewTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>In-process Roslyn + real catalog tests. No MSBuild, execution permission, or CI required.</summary>
public sealed class ReviewContextChurnTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-context-" + Guid.NewGuid().ToString("N"));
    private readonly List<AdhocWorkspace> _workspaces = [];
    private readonly byte[] _core = File.ReadAllBytes(typeof(object).Assembly.Location);
    private WorkspaceGuard Guard => new([_root], true, false);

    private async Task<TrackedReviewTarget> Catalog(string snapshot, string referencePath = "cache-one.dll",
        string code = "class C { public int M()=>1; public int N()=>1; }", bool release = false,
        string[]? symbols = null, string? generated = null, bool dependency = false, int dependencyValue = 1,
        bool reverseDocuments = false, string[]? referenceAliases = null, bool diskReference = false)
    {
        Directory.CreateDirectory(_root);
        var workspace = new AdhocWorkspace(); _workspaces.Add(workspace);
        var id = ProjectId.CreateNewId(); var project = Path.Combine(_root, "P.csproj");
        File.WriteAllText(project, "<Project/>");
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), "P", "P",
            LanguageNames.CSharp, filePath: project,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: release ? OptimizationLevel.Release : OptimizationLevel.Debug),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols ?? [])));
        var referenceFile = Path.Combine(_root, referencePath);
        PortableExecutableReference reference;
        if (diskReference)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(referenceFile)!);
            File.WriteAllBytes(referenceFile, _core);
            reference = MetadataReference.CreateFromFile(referenceFile);
        }
        else reference = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(_core), filePath: referenceFile);
        if (referenceAliases is not null) reference = reference.WithAliases(ImmutableArray.CreateRange(referenceAliases));
        solution = solution.AddMetadataReference(id, reference);
        var documents = new List<(string Name, string Code)> { ("A.cs", code) };
        if (generated is not null) documents.Add(("P.AssemblyInfo.cs", generated));
        if (reverseDocuments) documents.Reverse();
        foreach (var document in documents)
        {
            var path = Path.Combine(_root, document.Name); File.WriteAllText(path, document.Code);
            solution = solution.AddDocument(DocumentId.CreateNewId(id), document.Name,
                SourceText.From(document.Code, Encoding.UTF8), filePath: path);
        }
        if (dependency)
        {
            var depId = ProjectId.CreateNewId(); var depPath = Path.Combine(_root, "D.csproj");
            var file = Path.Combine(_root, "D.cs"); var depCode = "public class D { public int Value()=>" + dependencyValue + "; }";
            File.WriteAllText(depPath, "<Project/>"); File.WriteAllText(file, depCode);
            solution = solution.AddProject(ProjectInfo.Create(depId, VersionStamp.Create(), "D", "D", LanguageNames.CSharp,
                filePath: depPath, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview)))
                .AddMetadataReference(depId, reference)
                .AddDocument(DocumentId.CreateNewId(depId), "D.cs", SourceText.From(depCode, Encoding.UTF8), filePath: file)
                .AddProjectReference(id, new ProjectReference(depId));
        }
        var seeds = new List<ExplorerMethodSeed>();
        foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
        {
            var syntax = await doc.GetSyntaxRootAsync(); var model = await doc.GetSemanticModelAsync();
            foreach (var method in syntax!.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var symbol = model!.GetDeclaredSymbol(method)!;
                seeds.Add(new(new MethodSummary(Guid.NewGuid().ToString("N"), doc.Project.Name,
                    symbol.ToDisplayString(), doc.FilePath!, 1), doc, method, symbol));
            }
        }
        var index = await SourceExplorerIndex.CreateAsync(snapshot, solution, seeds);
        var catalog = await index.ReviewCatalogAsync(Guard, default);
        return catalog.Targets.Single(t => t.Kind == "method" && t.Name.Contains("C.M()", StringComparison.Ordinal));
    }
    private static ReviewCheckpoint Mark(TrackedReviewTarget target) => new(1, target.Id, target.Kind, target.Name,
        target.ProjectPath, "checked", target.SourceHash, target.ContextHash, "Inspected", "test", "session", "utc", [])
        { ContextFingerprint = target.ContextFingerprint };

    [Fact]
    public async Task ReferencePathOnlyChurnIsReproducedByV1AndRemovedByV2()
    {
        var a = await Catalog("one", referencePath: "cache-one.dll");
        var b = await Catalog("two", referencePath: "cache-two.dll");
        Assert.Equal(a.Id, b.Id); Assert.Equal(a.SourceHash, b.SourceHash);
        Assert.NotEqual(a.LegacyContextHash, b.LegacyContextHash);
        Assert.Equal(a.ContextHash, b.ContextHash);
        Assert.Equal("checked", ReviewProgress.State(b, Mark(a), false).Status);
    }
    [Fact]
    public async Task PhysicalDllRelocationKeepsV2CheckpointAndChangesLegacyHash()
    {
        var left = Path.Combine("first-cache", "core.dll");
        var right = Path.Combine("relocated-cache", "core.dll");
        var before = await Catalog("before", referencePath: left, diskReference: true);
        var after = await Catalog("after", referencePath: right, diskReference: true);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_root, left)), File.ReadAllBytes(Path.Combine(_root, right)));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.SourceHash, after.SourceHash);
        Assert.NotEqual(before.LegacyContextHash, after.LegacyContextHash);
        Assert.Equal(before.ContextHash, after.ContextHash);
        var state = ReviewProgress.State(after, Mark(before), false);
        Assert.Equal("checked", state.Status);
        Assert.Empty(state.ContextChange!.ChangedComponents);
    }
    [Fact]
    public async Task RepeatedReopenDoesNotChangeTheFingerprint()
    {
        var first = await Catalog("one"); var checkpoint = Mark(first);
        for (var i = 0; i < 5; i++)
        {
            var reopened = await Catalog("reload-" + i);
            Assert.Equal(first.ContextHash, reopened.ContextHash);
            Assert.Equal("checked", ReviewProgress.State(reopened, checkpoint, false).Status);
        }
    }
    [Fact]
    public async Task DocumentEnumerationOrderIsNotContextChange()
    {
        const string generated = "[assembly: System.Reflection.AssemblyTitle(\"P\")]";
        var a = await Catalog("one", generated: generated);
        var b = await Catalog("two", generated: generated, reverseDocuments: true);
        Assert.Equal(a.ContextHash, b.ContextHash);
    }
    [Fact]
    public async Task DebugReleaseIsARealConfigurationChange()
    {
        var a = await Catalog("one"); var b = await Catalog("two", release: true);
        Assert.Equal(a.SourceHash, b.SourceHash);
        var state = ReviewProgress.State(b, Mark(a), false);
        Assert.Equal("needs_review", state.Status);
        Assert.Equal("compilationOptions", Assert.Single(state.ContextChange!.ChangedComponents));
    }
    [Fact]
    public async Task PreprocessorChangesAreNotSuppressed()
    {
        var a = await Catalog("one", symbols: ["A"]); var b = await Catalog("two", symbols: ["B"]);
        Assert.Equal("parseOptions", Assert.Single(ReviewProgress.State(b, Mark(a), false).ContextChange!.ChangedComponents));
    }
    [Fact]
    public async Task GeneratedVersionChangesAreDiagnosedNotSilentlyIgnored()
    {
        var a = await Catalog("one", generated: "[assembly: System.Reflection.AssemblyInformationalVersion(\"1.0+aaa\")]");
        var b = await Catalog("two", generated: "[assembly: System.Reflection.AssemblyInformationalVersion(\"1.0+bbb\")]");
        Assert.Equal(a.SourceHash, b.SourceHash);
        var state = ReviewProgress.State(b, Mark(a), false);
        Assert.Equal("needs_review", state.Status);
        Assert.Equal("generatedSources", Assert.Single(state.ContextChange!.ChangedComponents));
    }
    [Fact]
    public async Task AnotherMethodChangeRemainsConservativeAndRemarkCanBecomeStable()
    {
        var a = await Catalog("one");
        const string changed = "class C { public int M()=>1; public int N()=>2; }";
        var b = await Catalog("two", code: changed);
        Assert.Equal(a.SourceHash, b.SourceHash);
        Assert.Equal("sourceFiles", Assert.Single(ReviewProgress.State(b, Mark(a), false).ContextChange!.ChangedComponents));
        var c = await Catalog("three", code: changed);
        Assert.Equal("checked", ReviewProgress.State(c, Mark(b), false).Status);
    }
    [Fact]
    public async Task DependencyProjectIdsAreNotPersistentIdentity()
    {
        var a = await Catalog("one", dependency: true); var b = await Catalog("two", dependency: true);
        Assert.NotEqual(a.ProjectId, b.ProjectId);
        Assert.Equal(a.ContextHash, b.ContextHash);
    }
    [Fact]
    public async Task DependencySourceChangesRequireReinspection()
    {
        var a = await Catalog("one", dependency: true); var b = await Catalog("two", dependency: true, dependencyValue: 2);
        Assert.Equal(a.SourceHash, b.SourceHash);
        var state = ReviewProgress.State(b, Mark(a), false);
        Assert.Equal("needs_review", state.Status);
        Assert.Contains("sourceFiles", state.ContextChange!.ChangedComponents);
    }
    [Fact]
    public async Task ReferenceAliasesAreNotDisplayOnlyNoise()
    {
        var a = await Catalog("one"); var b = await Catalog("two", referenceAliases: ["global", "Alias"]);
        Assert.Equal(a.LegacyContextHash, b.LegacyContextHash); // v1 omitted aliases.
        Assert.NotEqual(a.ContextHash, b.ContextHash);
        Assert.Contains("metadataReferences", ReviewProgress.State(b, Mark(a), false).ContextChange!.ChangedComponents);
    }
    public void Dispose()
    {
        foreach (var workspace in _workspaces) workspace.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
