using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class PerformanceHotspotAnalyzerTests
{
    private static StaticHotspotReport Analyze(string source, int methodLimit = 50, int siteLimit = 500)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "/mock/Project/Code.cs");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("HotspotFixture", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        var root = tree.GetRoot();
        var model = compilation.GetSemanticModel(tree);
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(method =>
        {
            var symbol = (IMethodSymbol)model.GetDeclaredSymbol(method)!;
            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var summary = new MethodSummary(method.Identifier.ValueText, "Project",
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                tree.FilePath, line);
            return new HotspotMethodDefinition(summary, method, symbol, model, "/mock/Project");
        }).ToArray();
        return PerformanceHotspotAnalyzer.Analyze("snapshot", methods, methodLimit, siteLimit);
    }

    [Fact]
    public void RanksNestedLoopsAndRepeatedMaterializationHigherThanCheapMethod()
    {
        var report = Analyze("""
            using System.Collections.Generic;
            using System.Linq;
            public class Engine {
                public static int Hot(List<int> items) {
                    var value = 0;
                    var text = "";
                    foreach (var item in items) {
                        for (var i = 0; i < 10; i++) {
                            var copy = items.ToArray();
                            object boxed = item;
                            if (items.Contains(item)) value += Helper(item);
                            text += item.ToString();
                        }
                    }
                    return value + text.Length;
                }
                public static int Helper(int n) {
                    var total = 0;
                    for (var i = 0; i < 10; i++) total += n;
                    return total;
                }
                public static int Wrapper(List<int> items) {
                    var sum = 0;
                    for (var i = 0; i < 10; i++) sum += Hot(items);
                    return sum;
                }
                public static int Cheap(int a) => a + 1;
            }
            """);
        var hot = Assert.Single(report.Methods, m => m.Method.Id == "Hot");
        var wrapper = Assert.Single(report.Methods, m => m.Method.Id == "Wrapper");
        var cheap = Assert.Single(report.Methods, m => m.Method.Id == "Cheap");

        Assert.True(hot.SuspicionScore > cheap.SuspicionScore);
        Assert.True(wrapper.SuspicionScore > cheap.SuspicionScore);
        Assert.True(hot.MaximumLoopDepth >= 2);
        Assert.True(hot.NestedLoopCount > 0);
        Assert.True(hot.AllocationCandidates >= 2);
        Assert.Contains(report.Sites, s => s.Category == "linq_materialization");
        Assert.Contains(report.Sites, s => s.Category == "list_linear_search");
        Assert.Contains(report.Sites, s => s.Category == "boxing_per_iteration");
        Assert.Contains(report.Sites, s => s.Category == "nested_loops");
        Assert.Contains(report.Sites, s => s.Category == "string_concat_per_iteration");
        Assert.Contains(report.CallEdges, e => e.CallerMethodId == "Wrapper" &&
            e.CalleeMethodId == "Hot" && e.LoopDepth > 0);
        Assert.Equal("static_candidate", report.EvidenceLevel);
    }

    [Fact]
    public void IgnoresDeferredLambdaBodyDeclaredInLoop()
    {
        var report = Analyze("""
            using System;
            public class Sample {
                public static void Test() {
                    for (var i = 0; i < 10; i++) {
                        Func<object> later = () => new byte[1000];
                    }
                }
            }
            """);
        Assert.DoesNotContain(report.Sites,
            s => s.Category == "allocation_per_iteration");
    }

    [Fact]
    public void DetectsIOLockAndSynchronousWaitInLoop()
    {
        var report = Analyze("""
            using System.IO;
            using System.Threading;
            public class Example {
                private static readonly object Gate = new();
                public static void Run() {
                    for (var i = 0; i < 5; i++) {
                        lock (Gate) {
                            var text = File.ReadAllText("input.txt");
                            Thread.Sleep(1);
                        }
                    }
                }
            }
            """);
        Assert.Contains(report.Sites, s => s.Category == "io_per_iteration");
        Assert.Contains(report.Sites, s => s.Category == "lock_per_iteration");
        Assert.Contains(report.Sites, s => s.Category == "blocking_call");
    }

    [Fact]
    public void StableSiteFingerprintsAndHonestPagination()
    {
        var source = """
            using System.Linq;
            public class Sample {
                public static void Run(int[] data) {
                    for (var i=0; i<4; i++) {
                        var a = data.ToList();
                        var b = data.ToArray();
                    }
                }
                public static int Cheap(int x) => x;
            }
            """;
        var one = Analyze(source, 1, 1);
        var other = Analyze(source, 1, 1);
        Assert.Equal(one.Sites.Select(s => s.Fingerprint),
            other.Sites.Select(s => s.Fingerprint));
        Assert.Equal(one.Methods.Select(m => m.Method.Id),
            other.Methods.Select(m => m.Method.Id));
        Assert.True(one.MethodsTruncated);
        Assert.True(one.SitesTruncated);
        Assert.True(one.TotalSites > one.ReturnedSites);
    }

    [Fact]
    public void RecursiveCallGraphCannotDiverge()
    {
        var report = Analyze("""
            public class Example {
                public static int Recur(int n) {
                    if (n == 0) return 0;
                    return Recur(n - 1) + 1;
                }
            }
            """);
        var method = Assert.Single(report.Methods);
        Assert.True(method.PotentialRecursion);
        Assert.InRange(method.SuspicionScore, 0, 2000);
    }
}
