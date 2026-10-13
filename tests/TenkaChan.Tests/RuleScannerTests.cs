using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class RuleScannerTests
{
    private static IReadOnlyList<RuleFinding> Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Case.cs");
        var assemblyPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        var refs = assemblyPaths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("rule-fixture", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        return RuleScanner.Scan(tree.GetRoot(), compilation.GetSemanticModel(tree), "Case.cs");
    }

    [Fact]
    public void DetectsLinqMaterializationInsideLoop()
    {
        var findings = Run("""
            using System.Collections.Generic;
            using System.Linq;
            class Case {
                static void Run(IEnumerable<int> items) {
                    for (int i = 0; i < 3; i++) { var x = items.ToList(); }
                }
            }
            """);
        Assert.Contains(findings, f => f.RuleId == "PERF1001");
    }

    [Fact]
    public void SkipsMaterializationOutsideLoop()
    {
        var findings = Run("""
            using System.Collections.Generic;
            using System.Linq;
            class Case {
                static void Run(IEnumerable<int> items) {
                    var x = items.ToList();
                }
            }
            """);
        Assert.DoesNotContain(findings, f => f.RuleId == "PERF1001");
    }

    [Fact]
    public void DetectsLoopStringConcatenation()
    {
        var findings = Run("""
            class Case {
                static string Run() {
                    var s = "";
                    for (int i = 0; i < 3; i++) s += i;
                    return s;
                }
            }
            """);
        Assert.Contains(findings, f => f.RuleId == "PERF1002");
    }

    [Fact]
    public void DetectsRegexConstructionInLoop()
    {
        var findings = Run("""
            using System.Text.RegularExpressions;
            class Case {
                static void Run() {
                    for (int i = 0; i < 3; i++) { var r = new Regex("abc"); }
                }
            }
            """);
        Assert.Contains(findings, f => f.RuleId == "PERF1003");
    }

    [Fact]
    public void DetectsBlockingTaskResultInAsync()
    {
        var findings = Run("""
            using System.Threading.Tasks;
            class Case {
                static async Task<int> Run(Task<int> t) {
                    await Task.Yield();
                    return t.Result;
                }
            }
            """);
        Assert.Contains(findings, f => f.RuleId == "ASYNC1001");
    }
}
