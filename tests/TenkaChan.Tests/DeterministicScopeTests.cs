using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DeterministicScopeTests
{
    private static IReadOnlyList<RuleFinding> Scan(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Scopes.cs");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(MetadataReference.CreateFromFile).ToArray();
        var compilation = CSharpCompilation.Create("Scopes", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        return RuleScanner.Scan(tree.GetRoot(), compilation.GetSemanticModel(tree), "Scopes.cs");
    }

    [Fact]
    public void CountWithNonzeroThresholdIsNotAnyCandidate()
    {
        var findings = Scan("""
            using System.Collections.Generic;
            using System.Linq;
            class Example {
                public bool Check(IEnumerable<int> values) {
                    bool found = false;
                    for (var i=0; i<4; i++) {
                        found |= values.Count() > 3;
                    }
                    return found;
                }
            }
            """);
        Assert.DoesNotContain(findings, f => f.RuleId == "PERF1004");
    }

    [Fact]
    public void CountComparedWithZeroCanBeAnyCandidate()
    {
        var findings = Scan("""
            using System.Collections.Generic;
            using System.Linq;
            class Example {
                public bool Check(IEnumerable<int> values) {
                    bool found = false;
                    for (var i=0; i<4; i++) {
                        found |= values.Count() > 0;
                    }
                    return found;
                }
            }
            """);
        Assert.Contains(findings, f => f.RuleId == "PERF1004");
    }

    [Fact]
    public void LambdaDeclaredInsideLoopDoesNotExecuteItsBodyPerIteration()
    {
        var findings = Scan("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            class Example {
                public void Run(IEnumerable<int> input) {
                    for (var i=0; i<4; i++) {
                        Func<int> later = () => input.ToList().Count;
                    }
                }
            }
            """);
        Assert.DoesNotContain(findings, f => f.RuleId == "PERF1001");
    }

    [Fact]
    public void BlockingInAsyncLambdaIsDetectedWithinCallableBoundary()
    {
        var findings = Scan("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Example {
                public Func<Task> Factory() {
                    return async () => { Thread.Sleep(1); await Task.Yield(); };
                }
            }
            """);
        Assert.Contains(findings, f => f.RuleId == "ASYNC1003");
    }

    [Fact]
    public void BlockingInSyncLocalFunctionNestedInAsyncMethodIsNotReportedAsAsync()
    {
        var findings = Scan("""
            using System.Threading;
            using System.Threading.Tasks;
            class Example {
                public async Task Run() {
                    void Later() { Thread.Sleep(1); }
                    await Task.Yield();
                }
            }
            """);
        Assert.DoesNotContain(findings, f => f.RuleId == "ASYNC1003");
    }
}
