using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewCatalogTypeBindingTests
{
    [Theory]
    [InlineData("ReviewTarget", "TenkaChan.Mcp.Analysis.ReviewTarget")]
    [InlineData("TrackedReviewTarget", "TenkaChan.Mcp.Tracking.ReviewTarget")]
    public void OuterAliasMustNotReuseTheNameOfAnAnalysisNamespaceMember(string alias, string expected)
    {
        // Both real ReviewTarget records live in the referenced Tenka assembly.
        // The first case reproduces the original lookup; the second exercises the fix.
        var tree = CSharpSyntaxTree.ParseText($$"""
            using {{alias}} = global::TenkaChan.Mcp.Tracking.ReviewTarget;
            namespace TenkaChan.Mcp.Analysis;
            public sealed class BindingProbe
            {
                public {{alias}} Target { get; set; }
            }
            """);
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ??
            typeof(object).Assembly.Location).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(SourceExplorerIndex).Assembly.Location)
            .Distinct(StringComparer.Ordinal);
        var references = paths.Select(file => MetadataReference.CreateFromFile(file)).ToArray();
        var compilation = CSharpCompilation.Create("ReviewTargetBindingProbe", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var property = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        var resolved = compilation.GetSemanticModel(tree).GetTypeInfo(property.Type).Type;
        Assert.NotNull(resolved);
        Assert.Equal(expected, resolved!.ToDisplayString());
    }

    [Fact]
    public async Task ActualExplorerCatalogueBuildsTrackingRecordsForClassesAndMethods()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "Example.csproj");
            var sourcePath = Path.Combine(root, "Example.cs");
            const string code = "namespace Demo; public static class Example { public static int Read() => 7; }";
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(sourcePath, code, new UTF8Encoding(false));
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId,
                VersionStamp.Create(), "Example", "Example", LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview)))
                .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument(documentId, "Example.cs", SourceText.From(code, Encoding.UTF8), filePath: sourcePath);
            var document = solution.GetDocument(documentId)!;
            var syntax = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
            var model = (await document.GetSemanticModelAsync())!;
            var symbol = model.GetDeclaredSymbol(syntax)!;
            var seed = new ExplorerMethodSeed(new MethodSummary("read-method", "Example",
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), sourcePath, 1),
                document, syntax, symbol);
            var index = await SourceExplorerIndex.CreateAsync("binding-snapshot", solution, [seed]);
            var guard = new WorkspaceGuard([root], false, false);
            var catalogue = await index.ReviewCatalogAsync(guard, CancellationToken.None);

            Assert.Equal(2, catalogue.Targets.Count);
            Assert.Contains(catalogue.Targets, t => t.Kind == "type");
            Assert.Contains(catalogue.Targets, t => t.Kind == "method");
            foreach (var target in catalogue.Targets)
            {
                Assert.IsType<global::TenkaChan.Mcp.Tracking.ReviewTarget>(target);
                Assert.True(target.Trackable, target.UnavailableReason);
                Assert.Equal(ReviewProgress.Hash(target.Source), target.SourceHash);
                Assert.False(string.IsNullOrWhiteSpace(target.ContextHash));
                var mark = new ReviewMarkRequest("binding-snapshot", target.Id, target.SourceHash,
                    target.ContextHash, 0, "checked", "Type-binding regression fixture; not a correctness claim.");
                ReviewProgress.ValidateMark(target, mark, false);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
