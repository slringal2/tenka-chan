using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewPrioritizerTests
{
    [Fact]
    public void RanksStatefulLoopAboveTrivialMethod()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Example {
                int Simple(int x) => x + 1;
                int Complex(int[] data) {
                    var result = 0;
                    foreach (var x in data) {
                        if (x > 0 && x < 10) result += x;
                    }
                    return result;
                }
            }
            """);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        ReviewTarget Assess(MethodDeclarationSyntax m) =>
            ReviewPrioritizer.Assess(
                new MethodSummary(m.Identifier.Text, "Fixture", m.Identifier.Text, "fixture.cs", 1), m);
        var simple = Assess(methods.Single(x => x.Identifier.Text == "Simple"));
        var complex = Assess(methods.Single(x => x.Identifier.Text == "Complex"));
        Assert.True(complex.Priority > simple.Priority);
        Assert.True(complex.ApproximateComplexity > simple.ApproximateComplexity);
        Assert.Contains("loops", complex.RiskHints);
        Assert.Empty(simple.RiskHints);
    }
}
