using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class SolutionExplorerTests
{
    private static ProjectInfo Project(ProjectId id, string name, string folder) => ProjectInfo.Create(
        id, VersionStamp.Create(), name, folder, LanguageNames.CSharp,
        filePath: Path.Combine(Path.GetTempPath(), "tenka-solution-fixture", folder, name + ".csproj"),
        compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
        parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
        metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

    private static async Task<ExplorerMethodSeed[]> Seeds(Solution solution)
    {
        var seeds = new List<ExplorerMethodSeed>();
        foreach (var project in solution.Projects)
        foreach (var doc in project.Documents)
        {
            var root = await doc.GetSyntaxRootAsync();
            var model = await doc.GetSemanticModelAsync();
            foreach (var syntax in root!.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var symbol = model!.GetDeclaredSymbol(syntax)!;
                var summary = new MethodSummary(Guid.NewGuid().ToString("N"), project.Name,
                    symbol.ToDisplayString(), doc.FilePath!, 1);
                seeds.Add(new(summary, doc, syntax, symbol));
            }
        }
        return seeds.ToArray();
    }

    [Fact]
    public async Task SameNamedProjectsAndTypesHaveSeparateSelectionsAndOutputs()
    {
        using var workspace = new AdhocWorkspace();
        var a = ProjectId.CreateNewId(); var b = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(Project(a, "Core", "first"))
            .AddProject(Project(b, "Core", "second"));
        foreach (var id in new[] { a, b })
            solution = solution.AddDocument(DocumentId.CreateNewId(id), "Worker.cs",
                SourceText.From("namespace Shared; public class Worker { public int Run() => 1; }"),
                filePath: Path.Combine(Path.GetTempPath(), id.Id.ToString("N"), "Worker.cs"));
        var index = await SourceExplorerIndex.CreateAsync("solution-snapshot", solution, await Seeds(solution));
        Assert.Equal(2, index.Projects.Count);
        var all = index.Types(null, "Shared.Worker");
        Assert.Equal(2, all.Total);
        Assert.Equal("current_loaded_roslyn_snapshot_only", all.SelectionScope);
        Assert.Contains("tenka_review_queue", all.NextAction);
        Assert.Contains("tenka_inspect_type", all.NextAction);
        Assert.NotEqual(all.Items[0].Id, all.Items[1].Id);
        Assert.NotEqual(all.Items[0].ProjectId, all.Items[1].ProjectId);
        var first = await index.TypeAsync(all.Items[0].Id);
        var second = await index.TypeAsync(all.Items[1].Id);
        Assert.NotEqual(first.Methods.Single().Id, second.Methods.Single().Id);
        Assert.Equal(first.Type.ProjectId, first.Methods.Single().ProjectId);
        Assert.Single(index.Types(a.Id.ToString("N"), "Shared.Worker").Items);
    }

    [Fact]
    public async Task CrossProjectReferencesPointToTheCallingProjectMethod()
    {
        using var workspace = new AdhocWorkspace();
        var library = ProjectId.CreateNewId(); var app = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(Project(library, "Core", "core"))
            .AddProject(Project(app, "App", "app")).AddProjectReference(app, new ProjectReference(library));
        solution = solution.AddDocument(DocumentId.CreateNewId(library), "Worker.cs",
            SourceText.From("namespace Shared; public static class Worker { public static int Run() => 7; }"),
            filePath: Path.Combine(Path.GetTempPath(), "core", "Worker.cs"));
        solution = solution.AddDocument(DocumentId.CreateNewId(app), "App.cs",
            SourceText.From("namespace Client; public static class App { public static int Execute() => Shared.Worker.Run(); }"),
            filePath: Path.Combine(Path.GetTempPath(), "app", "App.cs"));
        var index = await SourceExplorerIndex.CreateAsync("cross-project", solution, await Seeds(solution));
        var core = await index.TypeAsync(index.Types(library.Id.ToString("N"), "Worker").Items.Single().Id);
        var caller = await index.TypeAsync(index.Types(app.Id.ToString("N"), "Client.App").Items.Single().Id);
        Assert.Equal(library.Id.ToString("N"), index.Project(app.Id.ToString("N")).ProjectReferenceIds.Single());
        var refs = await index.ReferencesAsync(core.Methods.Single().Id, false);
        Assert.Contains(refs.Items, x => x.Project == "App" && x.MethodId == caller.Methods.Single().Id);
    }

    [Fact]
    public async Task EvaluatedProjectGraphPreservesDirectReferenceDirection()
    {
        using var workspace = new AdhocWorkspace();
        var core = ProjectId.CreateNewId(); var service = ProjectId.CreateNewId(); var app = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(Project(core, "Core", "core"))
            .AddProject(Project(service, "Service", "service")).AddProject(Project(app, "App", "app"))
            .AddProjectReference(service, new ProjectReference(core)).AddProjectReference(app, new ProjectReference(service));
        var index = await SourceExplorerIndex.CreateAsync("graph", solution, []);
        Assert.Empty(index.Project(core.Id.ToString("N")).ProjectReferenceIds);
        Assert.Equal(core.Id.ToString("N"), index.Project(service.Id.ToString("N")).ProjectReferenceIds.Single());
        Assert.Equal(service.Id.ToString("N"), index.Project(app.Id.ToString("N")).ProjectReferenceIds.Single());
    }
}
