using System.Diagnostics;
using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

[CollectionDefinition("ProposalValidationEnvironment", DisableParallelization = true)]
public sealed class ProposalValidationEnvironment { }

[Collection("ProposalValidationEnvironment")]
public sealed class ProposalValidationWorkflowTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tenka-workflow-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new();
    private readonly ChangeProposalStore _proposals;
    private readonly WorkspaceGuard _guard;
    private readonly ChangeProposal _proposal;
    public ProposalValidationWorkflowTests()
    {
        foreach (var key in new[] { "TENKA_ENABLE_LOCAL_EXEC", "TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS" })
        { _environment[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, "1"); }
        var root = Path.Combine(_home, "source"); Directory.CreateDirectory(root);
        var source = "public class C { public int M() => 1; }";
        var file = Path.Combine(root, "C.cs"); File.WriteAllText(file, source);
        foreach (var project in new[] { "App.csproj", "Tests/Test.csproj", "Benchmarks/Bench.csproj" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, project))!);
            File.WriteAllText(Path.Combine(root, project), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        }
        _guard = new WorkspaceGuard([root], true, false); _proposals = new ChangeProposalStore(_guard, Path.Combine(_home, "data"));
        var target = new ReviewTarget("target", "method", "method", "p", "App", Path.Combine(root, "App.csproj"), "M", file, 1, ReviewProgress.Hash(source), "context", true, null, source);
        _proposal = _proposals.Create(Guid.NewGuid().ToString("N"), "snapshot", target, root, InvestigationFiles.Hash(File.ReadAllBytes(file)), source.Replace("1;", "2;"),
            [new("test", "p", "Test", Path.Combine(root, "Tests/Test.csproj"), "test"), new("bench", "p", "Bench", Path.Combine(root, "Benchmarks/Bench.csproj"), "benchmark")], default);
    }
    [Fact] public async Task SameFrozenFixturesRunInOrderAndCompletedStagesAreNotRepeated()
    {
        var process = new FakeProcess(); var runner = new ProposalValidationRunner(_proposals, _guard, process);
        var result = await runner.RunAsync(_proposal.Id, null, null, null, true, default);
        Assert.Equal("validation_completed", result.Status); Assert.True(ProposalValidationRunner.CanApply(result)); Assert.Equal(8, process.Calls.Count);
        Assert.Equal(new[] { "baseline_test", "candidate_test", "baseline_benchmark", "candidate_benchmark" }, result.Stages.Select(s => s.Name));
        var again = await runner.RunAsync(_proposal.Id, null, null, null, true, default);
        Assert.Equal("validation_completed", again.Status); Assert.Equal(8, process.Calls.Count);
        Assert.Contains("=> 1", File.ReadAllText(Path.Combine(_proposal.Root, "C.cs")));
        Assert.All(process.Calls, call => Assert.DoesNotContain(_proposal.Root, call.Project, StringComparison.Ordinal));
    }
    [Fact] public async Task MissingApprovalRunsNothing()
    {
        var process = new FakeProcess();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProposalValidationRunner(_proposals, _guard, process).RunAsync(_proposal.Id, null, null, null, false, default));
        Assert.Empty(process.Calls);
    }
    [Fact] public async Task AFailedCandidateTestStopsBeforeAnyBenchmark()
    {
        var process = new FakeProcess { FailCandidateTest = true };
        var result = await new ProposalValidationRunner(_proposals, _guard, process).RunAsync(_proposal.Id, null, null, null, true, default);
        Assert.Equal("validation_failed", result.Status); Assert.Equal(2, result.Stages.Count); Assert.Equal(4, process.Calls.Count);
        Assert.False(ProposalValidationRunner.CanApply(result)); Assert.Empty(result.Cases);
    }
    [Fact] public async Task StartedStageWithoutAReceiptIsNotReplayedAfterRestart()
    {
        _proposals.SaveValidation(new(_proposal.Id, _proposal.AfterHash, "Tests/Test.csproj", "Benchmarks/Bench.csproj", "*", "running",
            [new("baseline_test", "running", null, null, [], "", "")], [], ""));
        var process = new FakeProcess();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProposalValidationRunner(_proposals, _guard, process).RunAsync(_proposal.Id, null, null, null, true, default));
        Assert.Empty(process.Calls);
    }
    [Theory]
    [InlineData(0, 0, 0, 0, "Completed")]
    [InlineData(2, 1, 0, 1, "Completed")]
    [InlineData(2, 1, 1, 0, "Completed")]
    [InlineData(1, 1, 0, 0, "Failed")]
    public void EmptySkippedFailedOrBadOutcomeIsNotAllPassed(int total, int passed, int failed, int skipped, string outcome) =>
        Assert.False(ProposalValidationRunner.AllTestsPassed(new(total, passed, failed, skipped, outcome, [])));
    [Fact] public void AllTestsMustBePassedAndHaveNoReportedFailures() =>
        Assert.True(ProposalValidationRunner.AllTestsPassed(new(2, 2, 0, 0, "Completed", [])));
    private static BenchmarkObservation Bench(string name = "Case", double mean = 100, string env = "env", int samples = 5) =>
        new(name, "Type", "Method", "", samples, mean, mean, 16, env);
    [Fact] public void EnvironmentMismatchMissingCasesAndFewSamplesStayVisible()
    {
        Assert.Equal("incomparable", Assert.Single(ProposalValidationRunner.Compare([Bench()], [Bench(env: "other")])).Status);
        Assert.Equal(2, ProposalValidationRunner.Compare([Bench()], [Bench("Other")]).Count);
        Assert.Equal("insufficient_samples", Assert.Single(ProposalValidationRunner.Compare([Bench()], [Bench(samples: 3)])).Status);
    }
    [Fact] public void DuplicateOrAbsentBenchmarkCasesCannotEnableApply()
    {
        Assert.Empty(ProposalValidationRunner.Compare([], [Bench()]));
        Assert.Empty(ProposalValidationRunner.Compare([Bench(), Bench()], [Bench()]));
    }
    [Fact] public void FasterAndSlowerAreObservationsNotAnOptimizationMark()
    {
        Assert.Equal("faster_observed", Assert.Single(ProposalValidationRunner.Compare([Bench()], [Bench(mean: 50)])).Status);
        Assert.Equal("slower_observed", Assert.Single(ProposalValidationRunner.Compare([Bench()], [Bench(mean: 150)])).Status);
    }
    [Theory]
    [InlineData("Tests/Test.csproj")]
    [InlineData("Benchmarks/Bench.csproj")]
    public void EditingTheValidationProjectCannotCountAsAnEquivalentWorkload(string fixture)
    {
        var changed = _proposal with { Project = fixture };
        Assert.Contains("validation_fixture_changed", Assert.Throws<InvalidOperationException>(() =>
            ProposalValidationRunner.ValidateFixtureSeparation(changed, "Tests/Test.csproj", "Benchmarks/Bench.csproj")).Message);
    }
    private sealed class FakeProcess : IWorkspaceBuildProcess
    {
        public readonly List<(string Command, string Project)> Calls = new();
        public bool FailCandidateTest;
        public Task<WorkspaceBuildProcessResult> RunAsync(ProcessStartInfo info, int seconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var args = info.ArgumentList.ToList(); var command = args[0];
            var project = command == "run" ? args[args.IndexOf("--project") + 1] : args[1]; Calls.Add((command, project));
            if (command == "test")
            {
                var output = args[args.IndexOf("--results-directory") + 1]; Directory.CreateDirectory(output);
                var failed = FailCandidateTest && project.Contains("candidate", StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(output, "results.trx"), "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"" + (failed ? "0" : "1") + "\" failed=\"" + (failed ? "1" : "0") + "\" notExecuted=\"0\" /></ResultSummary></TestRun>");
            }
            if (command == "run")
            {
                var output = args[args.IndexOf("--artifacts") + 1]; Directory.CreateDirectory(output);
                var mean = project.Contains("candidate", StringComparison.Ordinal) ? 80 : 100;
                var data = new { HostEnvironmentInfo = new { ProcessorName = "cpu", OsVersion = "os", Architecture = "x64", RuntimeVersion = "net8", DotNetCliVersion = "10", Configuration = "Release" },
                    Benchmarks = new[] { new { Type = "Type", Method = "Method", Parameters = "", Statistics = new { N = 5, Mean = mean, Median = mean }, Memory = new { BytesAllocatedPerOperation = 16 } } } };
                File.WriteAllText(Path.Combine(output, "bench-report-full.json"), JsonSerializer.Serialize(data));
            }
            return Task.FromResult(new WorkspaceBuildProcessResult(0, "fake process", ""));
        }
    }
    public void Dispose()
    {
        foreach (var item in _environment) Environment.SetEnvironmentVariable(item.Key, item.Value);
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }
}
