using Microsoft.Build.Locator;
using TenkaChan.Mcp.Analysis;
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

            using var service = new RoslynReviewService(new WorkspaceGuard([root], true, false));
            var summary = await service.OpenAsync(projectPath);
            Assert.True(summary.MethodCount >= 2);
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
            Assert.Equal("matched_unique_method", compiled.MatchStatus);
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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
