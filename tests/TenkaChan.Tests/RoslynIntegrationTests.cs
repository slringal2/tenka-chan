using Microsoft.Build.Locator;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class RoslynIntegrationTests
{
    [Fact]
    public async Task OpensRealProjectAndInspectsCfg()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        var root = Path.Combine(Path.GetTempPath(), "tenka-roslyn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "Fixture.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                    </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Demo.cs"), """
                using System.Linq;
                namespace Fixture;
                public static class Demo
                {
                    public static int Add(int a, int b)
                    {
                        if (a < 0) return b;
                        return a + b;
                    }
                    public static int Call(int value) => Add(value, 1);
                    public static void Use(int[] items)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            var copy = items.ToList();
                        }
                    }
                }
                """);

            var guard = new WorkspaceGuard([root], true, false);
            using var service = new RoslynReviewService(guard);
            var summary = await service.OpenAsync(projectPath);
            Assert.True(summary.MethodCount >= 2);
            // A read-only dynamic diagnosis must not replace the user's
            // selected shared workload profile. Import remains explicit.
            var traceJson = Path.Combine(root, "demo.speedscope.json");
            File.WriteAllText(traceJson, """
                {"shared":{"frames":[{"name":"Fixture.Demo.Add(System.Int32,System.Int32)"}]},
                 "profiles":[{"type":"sampled","name":"read-only-thread","unit":"milliseconds",
                   "samples":[[0]],"weights":[5]}]}
                """);
            Assert.Null(service.CurrentHotspotProfile);
            var preview = await service.AnalyzeHotspotProfileAsync(traceJson);
            Assert.Equal(5, preview.TotalWeight);
            Assert.Contains(preview.Frames, f => f.MatchedMethodId is not null);
            Assert.Null(service.CurrentHotspotProfile);
            var imported = await service.ImportHotspotProfileAsync(traceJson);
            Assert.NotNull(service.CurrentHotspotProfile);
            Assert.Equal(imported, service.CurrentHotspotProfile);

            var add = Assert.Single(service.FindMethods("Demo.Add"));
            var details = await service.InspectAsync(add.Id);
            Assert.Contains("return a + b", details.Source);
            Assert.NotEmpty(details.Blocks);
            var native = NativeJitDisassemblyParser.Parse("""
                ; Assembly listing for method Fixture.Demo:Add(int,int):int (FullOpts)
                ; Emitting BLENDED_CODE for X64
                       33C0                 xor eax, eax
                       C3                   ret
                ; Total bytes of code 3
                """, "net8.0", "sdk:8.0", "Demo:Add");
            var correlation = service.CorrelateNativeCode(native);
            var compiled = Assert.Single(correlation.Methods);
            Assert.Equal(add.Id, compiled.RoslynMethodId);
            Assert.Equal("matched_method_signature", compiled.MatchStatus);
            Assert.Equal(summary.SnapshotId, correlation.RoslynSnapshotId);

            var plan = service.SuggestTestPlan(add.Id);
            Assert.True(plan.ExternallyCallable);
            Assert.Equal("unresolved", plan.OracleStatus);
            Assert.Contains(plan.Inputs, input => input.CSharpExpressions.Contains("int.MinValue"));

            var references = await service.FindReferencesAsync(add.Id);
            Assert.Contains(references, location => location.Preview.Contains("Add(value, 1)"));

            var targets = service.GetReviewTargets();
            Assert.NotEmpty(targets);
            Assert.Contains(targets, target => target.Method.Id == add.Id);
            Assert.True(targets[0].Priority >= targets[^1].Priority);

            var diagnostics = await service.GetCompilationDiagnosticsAsync();
            Assert.True(diagnostics.ErrorCount >= 0);
            Assert.True(diagnostics.ReturnedCount <= 200);


            var results = await service.ScanAsync();
            Assert.Contains(results, f => f.RuleId == "PERF1001" && f.MethodId is not null);
            var trace = Path.Combine(root, "workload.speedscope.json");
            File.WriteAllText(trace, """
                {
                  "shared":{"frames":[
                    {"name":"Fixture.Demo.Use(int[])"},
                    {"name":"Fixture.Demo.Add(int,int)"}
                  ]},
                  "profiles":[{
                    "type":"sampled","name":"fixture-workload","unit":"milliseconds",
                    "samples":[[0],[0],[1]],"weights":[4,3,2]
                  }]
                }
                """);
            await service.ImportHotspotProfileAsync(trace);
            var store = new ExperimentStore(guard);
            var evidence = new PerformanceEvidenceService(
                service, store, new JitBinarySourceService(store, guard));
            var triage = await evidence.TriageAsync(maxObserved: 10, maxStatic: 10);
            Assert.Equal(summary.SnapshotId, triage.SnapshotId);
            Assert.Equal("observed_exclusive_milliseconds", triage.PriorityBasis);
            var first = Assert.Single(triage.Observed,
                item => item.Name.StartsWith("Fixture.Demo.Use", StringComparison.Ordinal));
            Assert.NotNull(first.MethodId);
            Assert.Equal(7d, first.ExclusiveWeight);
            Assert.NotNull(first.StaticSuspicionScore);
            var dossier = await evidence.InvestigateAsync(first.MethodId!);
            Assert.Equal(first.MethodId, dossier.MethodId);
            Assert.NotNull(dossier.Triage?.ExclusivePercent);
            Assert.Null(dossier.NativeCode);
            Assert.Null(dossier.IlAndSource);
            Assert.Contains(dossier.Limitations,
                note => note.Contains("No captured IL", StringComparison.Ordinal));

        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
