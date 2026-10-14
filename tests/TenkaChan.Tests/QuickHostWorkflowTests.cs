using System.Security.Cryptography;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;
public sealed class QuickHostWorkflowTests
{
    private static string? Fixture => Environment.GetEnvironmentVariable("TENKA_HOST_FIXTURE_PROJECT");

    [Fact]
    public void FindsExistingCompiledLibraryWithoutExecutingIt()
    {
        if (Fixture is not { } project) return;
        var guard=new WorkspaceGuard([Path.GetDirectoryName(project)!],true,false);
        var discovery=new WorkspaceBinaryDiscovery(guard);
        var assemblies=discovery.Discover([new ProjectSummary("PrioritySample",project,"net8.0",1)]);
        var choice=Assert.Single(assemblies.Where(x=>x.Configuration=="Release" && x.TargetFramework=="net8.0"));
        Assert.False(choice.Runnable);
        Assert.EndsWith("PrioritySample.dll",choice.AssemblyPath);
        Assert.Equal(choice.AssemblyPath,discovery.RequireBuiltProject(project,"net8.0").AssemblyPath);
    }

    [Fact]
    public async Task GeneratedXunitAndBenchmarkAreHostRunnableWithSeparateApproval()
    {
        if (Fixture is not { } project || Environment.GetEnvironmentVariable("TENKA_TEST_HOST_EXPERIMENTS")!="1") return;
        var home=Path.Combine(Path.GetTempPath(),"tenka-ci-host-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var old=Environment.GetEnvironmentVariable("TENKA_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TENKA_DATA_DIR",home);
            var guard=new WorkspaceGuard([Path.GetDirectoryName(project)!],true,false);
            var discovery=new WorkspaceBinaryDiscovery(guard);
            var store=new ExperimentStore(guard);
            var original=discovery.RequireBuiltProject(project,"net8.0");
            var oldHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original.AssemblyPath)));
            var executor=new TrustedHostExperimentExecutor(guard,store,discovery);
            Assert.True(executor.Enabled);

            var test=store.Create(Path.GetDirectoryName(project)!,Path.GetFileName(project),"test",
                "using Xunit; using PriorityFixture; public class TestCode { [Fact] public void Valid() => Assert.Equal(2, Work.Run(new int[]{1})); }",
                "net8.0");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>executor.RunAsync(test.Id,false));
            var tests=await executor.RunAsync(test.Id,true);
            Assert.True(tests.Status=="tests_passed", tests.Status+"\n"+tests.Output+"\n"+tests.Stderr+"\n"+tests.Note);
            Assert.True(tests.Tests?.Passed>=1,tests.Stderr+"\n"+tests.Output);
            Assert.Contains("UNSANDBOXED",tests.Note);
            Assert.Equal(tests.Status,store.ReadExecutionResult(test.Id).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>executor.RunAsync(test.Id,true));

            var bench=store.Create(Path.GetDirectoryName(project)!,Path.GetFileName(project),"benchmark",
                "using BenchmarkDotNet.Attributes; using PriorityFixture; [DryJob] public class BenchCode { [Benchmark] public int Run() => Work.Run(new int[]{1,2,3}); }",
                "net8.0");
            var result=await executor.RunAsync(bench.Id,true);
            Assert.True(result.Status=="benchmark_completed", result.Status+"\n"+result.Output+"\n"+result.Stderr+"\n"+result.Note);
            Assert.Contains(result.Benchmarks,x=>x.MeanNanoseconds is > 0);
            Assert.Equal(oldHash,Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(original.AssemblyPath))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TENKA_DATA_DIR",old);
            try { Directory.Delete(home,true); } catch(IOException) {}
        }
    }
}
