using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ExtendedRuleScannerTests
{
    private static IReadOnlyList<RuleFinding> Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Case.cs");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("rule-test-extended", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return RuleScanner.Scan(tree.GetRoot(), compilation.GetSemanticModel(tree), "Case.cs");
    }

    [Fact]
    public void FlagsBlockedAsyncAndSwallowedExceptions()
    {
        var findings = Run("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Example
            {
                public async void DangerousAsync(Task task)
                {
                    task.Wait();
                    Thread.Sleep(1);
                    await Task.Yield();
                }
                public void Swallow()
                {
                    try { throw new Exception("bad"); }
                    catch { }
                }
            }
            """);
        foreach (var id in new[] { "ASYNC1002", "ASYNC1003", "ASYNC1004", "CORRECT1001" })
            Assert.Contains(findings, finding => finding.RuleId == id);
    }

    [Fact]
    public void FlagsHttpClientInsideLoopAndDoesNotFlagOutside()
    {
        var findings = Run("""
            using System.Net.Http;
            class Example
            {
                public void Mixed()
                {
                    var outside = new HttpClient();
                    for (int i = 0; i < 3; i++)
                    {
                        using var inside = new HttpClient();
                    }
                }
            }
            """);
        Assert.Single(findings, f => f.RuleId == "PERF1005");
    }

    [Fact]
    public void DoesNotFlagCorrectAwaitAsBlocking()
    {
        var findings = Run("""
            using System.Threading.Tasks;
            class Example
            {
                public async Task Safe(Task other)
                {
                    await other;
                }
            }
            """);
        Assert.DoesNotContain(findings, f => f.Category == "async-blocking");
    }
}
