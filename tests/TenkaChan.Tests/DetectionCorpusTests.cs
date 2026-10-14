using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class DetectionCorpusTests
{
    private sealed record Case(string Id, string Rule, bool Expected, string Source);
    // Labels mean the targeted source pattern is present, NOT that a performance
    // defect exists. This small synthetic regression set is not a held-out benchmark.
    private static readonly Case[] Corpus =
    [
        new("box-object", "PERF2001", true, "class C { void M() { for(int i=0;i<3;i++) { object x=i; } } }"),
        new("box-interface-local", "PERF2001", true, "class C { void M() { for(int i=0;i<3;i++) { System.IComparable x=i; } } }"),
        new("box-object-argument", "PERF2001", true, "class C { void Use(object x){} void M() { for(int i=0;i<3;i++) Use(i); } }"),
        new("box-interface-argument", "PERF2001", true, "class C { void Use(System.IComparable x){} void M() { for(int i=0;i<3;i++) Use(i); } }"),
        new("box-explicit", "PERF2001", true, "class C { void M() { for(int i=0;i<3;i++) { var x=(object)i; } } }"),
        new("box-assignment", "PERF2001", true, "class C { void M() { object x=null; for(int i=0;i<3;i++) x=i; } }"),
        new("box-constructor", "PERF2001", true, "class C { C() { for(int i=0;i<3;i++) { System.IComparable x=i; } } }"),
        new("box-accessor", "PERF2001", true, "class C { object P { get { while(true) { return 1; } } } }"),
        new("box-struct-generic", "PERF2001", true, "class C { void Use(object x){} void M<T>(T x) where T:struct { for(int i=0;i<3;i++) Use(x); } }"),
        new("box-nullable", "PERF2001", true, "class C { void M(int? n) { for(int i=0;i<3;i++) { object x=n; } } }"),
        new("no-box-integer", "PERF2001", false, "class C { void M() { for(int i=0;i<3;i++) { int x=i; } } }"),
        new("no-box-numeric", "PERF2001", false, "class C { void M() { for(int i=0;i<3;i++) { long x=i; } } }"),
        new("no-box-object-argument", "PERF2001", false, "class C { void Use(object x){} void M(object x) { for(int i=0;i<3;i++) Use(x); } }"),
        new("no-box-outside-loop", "PERF2001", false, "class C { void M(int i) { object x=i; } }"),
        new("no-box-deferred-lambda", "PERF2001", false, "class C { void Use(object x){} void M() { for(int i=0;i<3;i++) { System.Action a=()=>Use(i); } } }"),
        new("no-box-local-function", "PERF2001", false, "class C { void Use(object x){} void M() { for(int i=0;i<3;i++) { void Later() { Use(i); } } } }"),
        new("no-box-reference-interface", "PERF2001", false, "class C { void M(string s) { for(int i=0;i<3;i++) { System.IComparable x=s; } } }"),
        new("no-box-unconstrained-generic", "PERF2001", false, "class C { void Use(object x){} void M<T>(T x) { for(int i=0;i<3;i++) Use(x); } }"),
        new("linq-loop", "PERF1001", true, "using System.Linq; class C { void M(int[] a) { for(int i=0;i<3;i++) { var x=a.ToArray(); } } }"),
        new("linq-outside", "PERF1001", false, "using System.Linq; class C { void M(int[] a) { var x=a.ToArray(); } }"),
        new("string-loop", "PERF1002", true, "class C { string M() { var s=\"\"; for(int i=0;i<3;i++) s+=i; return s; } }"),
        new("numeric-add-loop", "PERF1002", false, "class C { int M() { var s=0; for(int i=0;i<3;i++) s+=i; return s; } }"),
        new("rethrow-expression", "CORRECT2001", true, "class C { void M() { try { throw new System.Exception(); } catch(System.Exception e) { throw e; } } }"),
        new("rethrow-bare", "CORRECT2001", false, "class C { void M() { try { throw new System.Exception(); } catch(System.Exception e) { throw; } } }")
    ];

    [Fact]
    public void LabeledPositiveAndNegativePatternsProduceAnAuditableEvaluation()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var predictions = new List<string>();
        var actual = new Dictionary<string, bool>();
        foreach (var sample in Corpus)
        {
            var tree = CSharpSyntaxTree.ParseText(sample.Source, path: "/corpus/" + sample.Id + ".cs");
            var compilation = CSharpCompilation.Create("Corpus", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
            var root = tree.GetRoot(); var model = compilation.GetSemanticModel(tree);
            var findings = RuleScanner.Scan(root, model, tree.FilePath, 1000)
                .Concat(DeterministicSemanticRules.Scan(root, model, tree.FilePath)).ToArray();
            var hit = findings.Any(f => f.RuleId == sample.Rule);
            actual.Add(sample.Id, hit);
            if (hit) predictions.Add(sample.Id);
        }
        var metric = DetectionEvaluation.Evaluate(Corpus.Select(x => new DetectionLabel(x.Id, x.Expected)).ToArray(), predictions);
        var evaluation = new
        {
            corpus = "synthetic-pattern-regression-v1", scope = "target-rule matches, NOT actual defects or real-project precision",
            ranking = "fixture order, NOT product priority ranking", metric,
            cases = Corpus.Select(x => new { x.Id, x.Rule, x.Expected, actual = actual[x.Id],
                sourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Source))) })
        };
        if (Environment.GetEnvironmentVariable("TENKA_EVALUATION_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(evaluation, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(metric.FalsePositives == 0 && metric.FalseNegatives == 0, JsonSerializer.Serialize(evaluation));
        Assert.Equal(24, metric.Cases);
    }
}
