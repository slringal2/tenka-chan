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
