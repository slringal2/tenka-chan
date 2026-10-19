using Microsoft.Build.Locator;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Real MSBuildWorkspace test: validates deterministic full-solution reports
/// including compiler diagnostics, semantic facts, stable IDs and CFG metrics.
/// </summary>
public sealed class DeterministicAnalysisIntegrationTests
{
    [Fact]
    public async Task ProducesRepeatableReportWithCompilerEvidenceAndMethodLinks()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        var root = Path.Combine(Path.GetTempPath(), "tenka-deterministic-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Sample.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Analysis.cs"), """
                using System;
                using System.Collections.Generic;
                using System.Linq;
                public static class Analysis
                {
                    public static int Sum(IEnumerable<int> numbers)
                    {
                        int unused;
                        var total = 0;
                        for (var i = 0; i < 2; i++)
                        {
                            var data = numbers.ToList();
                            foreach (var n in data)
                            {
                                object boxed = n;
                                if (n > 0) total += (int)boxed;
                            }
                        }
                        return total;
                    }
                    public static void Rethrow()
                    {
                        try { throw new Exception(); }
                        catch (Exception ex) { throw ex; }
                    }
                }
                """);

            using var service = new RoslynReviewService(new WorkspaceGuard([root], true, false));
            var workspace = await service.OpenAsync(project);
            Assert.Equal(2, workspace.MethodCount);

            var first = await service.AnalyzeDeterministicAsync(500, 100);
            var second = await service.AnalyzeDeterministicAsync(500, 100);
            Assert.Equal(workspace.SnapshotId, first.Summary.SnapshotId);
            Assert.Equal(first.Summary.RulesVersion, second.Summary.RulesVersion);
            Assert.Equal(first.Summary.TotalFindings, second.Summary.TotalFindings);
            Assert.Equal(first.Findings.Select(x => x.Fingerprint),
                second.Findings.Select(x => x.Fingerprint));
            // New Roslyn session IDs are ephemeral, but findings remain
            // reproducible across reloading the same source and settings.
            await service.OpenAsync(project);
            var reloaded = await service.AnalyzeDeterministicAsync(500, 100);
            Assert.Equal(first.Findings.Select(x => x.Fingerprint),
                reloaded.Findings.Select(x => x.Fingerprint));

            Assert.Equal(CompilerDiagnosticContext.Scope, first.Summary.CompilerDiagnosticsScope);
            Assert.True(first.Summary.TopCompilerErrorCodes.Sum(x => x.Count)
                <= first.Summary.CompilerErrors);
            Assert.Contains(first.Summary.Notes,
                note => note.Contains("partial .slnx", StringComparison.Ordinal));
            Assert.True(first.Summary.DocumentsAnalyzed >= 1);
            Assert.Equal(2, first.Summary.MethodsIndexed);
            Assert.Contains(first.Findings, x => x.RuleId == "PERF1001");
            Assert.Contains(first.Findings, x => x.RuleId == "PERF2001");
            Assert.Contains(first.Findings, x => x.RuleId == "CORRECT2001"
                && x.EvidenceLevel == "semantic_fact");
            Assert.Contains(first.Findings, x => x.RuleId == "CS0168"
                && x.Source == "roslyn_compiler");
            // MSBuildWorkspace may include generated obj/**/*.cs compiler
            // diagnostics; only source-rule findings must point at Analysis.cs.
            Assert.All(first.Findings.Where(x => x.Source == "roslyn_semantic_rule"),
                x => Assert.Equal("Analysis.cs", x.RelativePath));
            Assert.All(first.Findings.Where(x => x.Source == "roslyn_semantic_rule"),
                x => Assert.False(string.IsNullOrEmpty(x.MethodId)));

            Assert.Equal(2, first.MethodMetrics.Count);
            var sum = Assert.Single(first.MethodMetrics,
                x => x.Signature.Contains("Analysis.Sum"));
            Assert.True(sum.LoopCount >= 2);
            Assert.True(sum.CfgBlockCount > 0);
            Assert.True(sum.ResolvedInvocationCount >= 1);
            Assert.True(sum.ApproximateComplexity >= 3);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MarksLimitedOutputAsTruncatedRatherThanClaimingComplete()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        var root = Path.Combine(Path.GetTempPath(), "tenka-deterministic-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Sample.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Sample.cs"), """
                using System.Collections.Generic;
                using System.Linq;
                public static class Sample {
                    public static void First(IEnumerable<int> items) {
                        for (var i=0; i<2; i++) { var a = items.ToList(); var b = items.ToArray(); }
                    }
                    public static void Second(IEnumerable<int> items) {
                        for (var i=0; i<2; i++) { var a = items.ToList(); }
                    }
                }
                """);
            using var service = new RoslynReviewService(new WorkspaceGuard([root], true, false));
            await service.OpenAsync(project);
            var limited = await service.AnalyzeDeterministicAsync(1, 1);
            Assert.True(limited.Summary.TotalFindings > limited.Summary.ReturnedFindings);
            Assert.True(limited.Summary.FindingsTruncated);
            Assert.True(limited.Summary.MethodMetricsTruncated);
            Assert.Single(limited.Findings);
            Assert.Single(limited.MethodMetrics);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
