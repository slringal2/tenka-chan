using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class NativeMethodCorrelatorTests
{
    private static NativeRoslynCandidate[] Candidates()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            namespace Target;
            public static class Calculator
            {
                public static int Sum(int x) => x;
                public static long Sum(long x) => x;
                public static int Another(int[] input) => input.Length;
            }
            """);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("Target", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Select((syntax, n) =>
            {
                var symbol = (IMethodSymbol)model.GetDeclaredSymbol(syntax)!;
                return new NativeRoslynCandidate(
                    new MethodSummary("method-" + n, "Target", symbol.ToDisplayString(),
                        "Calculator.cs", syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1),
                    symbol);
            }).ToArray();
    }

    [Fact]
    public void SelectsCorrectOverloadUsingNativeParameterTypes()
    {
        var source = """
            ; Assembly listing for method Target.Calculator:Sum(int):int (FullOpts)
            ; Emitting BLENDED_CODE for X64
                   33C0      xor eax, eax
                   C3        ret
            ; Total bytes of code 3
            """;
        var native = NativeJitDisassemblyParser.Parse(source,
            "net8.0", "sdk8", "Calculator:Sum");
        var linked = NativeMethodCorrelator.Correlate(native, Candidates(), "roslyn-snapshot");
        var method = Assert.Single(linked.Methods);
        Assert.Equal("method-0", method.RoslynMethodId);
        Assert.Equal("matched_method_signature", method.MatchStatus);
        Assert.Equal("Calculator.cs", method.RoslynSourcePath);
        Assert.NotNull(method.RoslynSourceLine);
        Assert.Equal("roslyn-snapshot", linked.RoslynSnapshotId);
    }

    [Fact]
    public void RefusesToChooseBetweenOverloadsWithoutParameterSignature()
    {
        var source = """
            ; Assembly listing for method Target.Calculator:Sum (FullOpts)
                   C3 ret
            ; Total bytes of code 1
            """;
        var native = NativeJitDisassemblyParser.Parse(source,
            "net8.0", "sdk8", "Calculator:Sum");
        var linked = NativeMethodCorrelator.Correlate(native, Candidates(), "snapshot");
        var method = Assert.Single(linked.Methods);
        Assert.Null(method.RoslynMethodId);
        Assert.Equal("ambiguous_no_parameter_signature", method.MatchStatus);
    }

    [Fact]
    public void MatchesArrayParametersOnlyWhenTypeAgrees()
    {
        var source = """
            ; Assembly listing for method Target.Calculator:Another(int[]):int (FullOpts)
                   C3 ret
            """;
        var native = NativeJitDisassemblyParser.Parse(source,
            "net10.0", "sdk10", "Calculator:Another");
        var linked = NativeMethodCorrelator.Correlate(native, Candidates(), "snapshot");
        Assert.Equal("method-2", Assert.Single(linked.Methods).RoslynMethodId);
    }
}
