// This is an in-process test fixture, never shipped as a compiler extension.
#pragma warning disable RS1038, RS1041, RS1036, RS2008
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DeterministicProjectAnalyzerTests
{
    [Fact]
    public async Task RunsConfiguredAnalyzerAsDistinctDeterministicEvidence()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public static class Sample {
                public static int Calculate(int a, int b) => a + b;
            }
            """, path: "Sample.cs");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("Sample", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new SampleAnalyzer());
        var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty);

        var first = await DeterministicAnalysisEngine.RunAnalyzersAsync(
            compilation, analyzers, options);
        var second = await DeterministicAnalysisEngine.RunAnalyzersAsync(
            compilation, analyzers, options);
        Assert.Single(first);
        Assert.Equal("TENKA9001", first[0].Id);
        Assert.Equal(first.Select(x => x.Id), second.Select(x => x.Id));
        Assert.Equal(DiagnosticSeverity.Warning, first[0].Severity);

        var noAnalyzers = await DeterministicAnalysisEngine.RunAnalyzersAsync(
            compilation, ImmutableArray<DiagnosticAnalyzer>.Empty, options);
        Assert.Empty(noAnalyzers);
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class SampleAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TENKA9001", "Fixture method syntax", "Fixture diagnostic",
            "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(ctx =>
                ctx.ReportDiagnostic(Diagnostic.Create(Rule, ctx.Node.GetLocation())),
                SyntaxKind.MethodDeclaration);
        }
    }
}
