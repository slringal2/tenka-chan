using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DeterministicSemanticRuleTests
{
    private static IReadOnlyList<RuleFinding> Scan(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Rules.cs");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("rule-fixture", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        return DeterministicSemanticRules.Scan(tree.GetRoot(),
            compilation.GetSemanticModel(tree), "Rules.cs");
    }

    [Fact]
    public void CaughtExceptionRethrowIsPreciseSemanticFact()
    {
        var results = Scan("""
            using System;
            public class Sample {
                public void Bad() {
                    try { throw new InvalidOperationException(); }
                    catch (Exception ex) { throw ex; }
                }
                public void Good() {
                    try { throw new InvalidOperationException(); }
                    catch (Exception ex) { throw; }
                }
            }
            """);
        var rethrows = results.Where(x => x.RuleId == "CORRECT2001").ToArray();
        Assert.Single(rethrows);
        Assert.Contains("stack trace", rethrows[0].Message);
    }

    [Fact]
    public void ShadowedIdentifierDoesNotCountAsCaughtException()
    {
        var results = Scan("""
            using System;
            public class Sample {
                public void Try() {
                    try { throw new Exception(); }
                    catch (Exception ex) {
                        if (DateTime.UtcNow.Ticks == 0) {
                            var other = new Exception("new");
                            throw other;
                        }
                        throw;
                    }
                }
            }
            """);
        Assert.DoesNotContain(results, x => x.RuleId == "CORRECT2001");
    }

    [Fact]
    public void DetectsBothAssignmentAndLocalVariableBoxingInLoops()
    {
        var results = Scan("""
            public static class Sample {
                public static void Run(int[] numbers) {
                    object saved = new object();
                    foreach (var n in numbers) {
                        object snapshot = n;
                        saved = n;
                    }
                }
            }
            """);
        Assert.Equal(2, results.Count(x => x.RuleId == "PERF2001"));
    }

    [Fact]
    public void AvoidsBoxingFalsePositiveForReferenceConversions()
    {
        var results = Scan("""
            public static class Sample {
                public static void Run(string[] text) {
                    object saved = new object();
                    foreach (var s in text) {
                        saved = s;
                        object copy = s;
                    }
                }
            }
            """);
        Assert.DoesNotContain(results, x => x.RuleId == "PERF2001");
    }

    [Fact]
    public void AvoidsReportingNestedLambdaAsOuterLoopExecution()
    {
        var results = Scan("""
            using System;
            public static class Sample {
                public static void Run(int[] numbers) {
                    for (var i = 0; i < 3; i++) {
                        Func<int> get = () => {
                            object boxed = numbers[0];
                            return boxed.GetHashCode();
                        };
                    }
                }
            }
            """);
        Assert.DoesNotContain(results, x => x.RuleId == "PERF2001");
    }

    [Fact]
    public void CatalogExposesEveryAddedRule()
    {
        var ids = DeterministicRuleCatalog.Rules.Select(x => x.RuleId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("CORRECT2001", ids);
        Assert.Contains("PERF2001", ids);
        Assert.Equal("semantic_fact",
            DeterministicRuleCatalog.Get("CORRECT2001").EvidenceLevel);
        Assert.Throws<KeyNotFoundException>(() =>
            DeterministicRuleCatalog.Get("RULE_DOES_NOT_EXIST"));
    }
}
