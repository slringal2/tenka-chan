using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

// The existing check-tenka filter FullyQualifiedName~ProposalValidationWorkflowTests
// also selects this class. Fake process reports test orchestration, NOT MSBuild/BDN.
[Collection("ProposalValidationEnvironment")]
public sealed class ProposalValidationWorkflowTestsForGeneratedExperiments : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tenka-generated-pair-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new();
    private readonly HashSet<string> _owned = new(StringComparer.Ordinal);
    private readonly WorkspaceGuard _guard;
    private readonly ChangeProposalStore _proposals;
    private readonly ExperimentStore _experiments;
    private readonly ChangeProposal _proposal;
    private const string TestCode = "using Xunit; public class Specs { [Fact] public void ValueIsOne() => Assert.Equal(1, Subject.Value()); }";
    private const string BenchCode = "using BenchmarkDotNet.Attributes; public class Workload { [Benchmark] public int Value() => Subject.Value(); }";

    public ProposalValidationWorkflowTestsForGeneratedExperiments()
    {
        foreach (var key in new[] { "TENKA_DATA_DIR", "TENKA_ENABLE_LOCAL_EXEC", "TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS" })
        {
            _environment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, key == "TENKA_DATA_DIR" ? Path.Combine(_home, "data") : "1");
        }
        var root = Path.Combine(_home, "source");
        foreach (var relative in new[] { "Subject/Subject.csproj", "Tests/Tests.csproj", "Benchmarks/Benchmarks.csproj", "Other/Other.csproj" })
        {
            var project = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        }
        var file = Path.Combine(root, "Subject/Subject.cs");
        const string source = "public static class Subject { public static int Value() => 1; }";
        File.WriteAllText(file, source);
        _guard = new WorkspaceGuard([root], true, false);
        _proposals = new ChangeProposalStore(_guard, Path.Combine(_home, "data"));
        _experiments = new ExperimentStore(_guard);
        var target = new ReviewTarget("target", "method", "method", "p", "Subject", Path.Combine(root, "Subject/Subject.csproj"),
            "Value", file, 1, ReviewProgress.Hash(source), "context", true, null, source);
        _proposal = _proposals.Create(Guid.NewGuid().ToString("N"), "snapshot", target, root,
            InvestigationFiles.Hash(File.ReadAllBytes(file)), source.Replace("=> 1;", "=> 1 + 0;"),
            [new("test", "p", "Tests", Path.Combine(root, "Tests/Tests.csproj"), "test"),
             new("bench", "p", "Benchmarks", Path.Combine(root, "Benchmarks/Benchmarks.csproj"), "benchmark")], default);
    }
    private ExperimentReceipt Prepare(string kind, string? project = null)
    {
        var relative = project ?? _proposal.Project;
        var receipt = _experiments.Create(_proposal.Root, relative, kind, kind == "test" ? TestCode : BenchCode, "net8.0");
        _owned.Add(receipt.Id); return receipt;
    }
    private void Authorize(string id)
    {
        if (!_owned.Contains(id)) throw new UnauthorizedAccessException("Experiment is not in the active investigation.");
    }
    private Task<ProposalValidation> Run(FakeProcess process, string? test, string? benchmark,
        string? testCompanion = null, string? benchmarkCompanion = null, bool approved = true, string? filter = null) =>
        new ProposalValidationRunner(_proposals, _guard, process, _experiments).RunAsync(_proposal.Id,
            testCompanion, benchmarkCompanion, filter, approved, default, test, benchmark, Authorize);

    [Fact] public async Task GeneratedPairBuildsSeparateCopiedSubjectsAndKeepsIdenticalWorkloads()
    {
        var test = Prepare("test"); var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        var result = await Run(process, test.Id, benchmark.Id);
        Assert.Equal("validation_completed", result.Status); Assert.True(ProposalValidationRunner.CanApply(result));
        Assert.Equal(8, process.Calls.Count);
        Assert.All(process.Calls, call => Assert.DoesNotContain(_proposal.Root, call.Project, StringComparison.Ordinal));
        foreach (var fixture in new[] { result.GeneratedTest!, result.GeneratedBenchmark! })
        {
            var sources = new List<string>();
            foreach (var variant in new[] { "baseline", "candidate" })
            {
                var project = ProposalGeneratedExperiments.ProjectPath(_proposals, _proposal, fixture, variant);
                var xml = XDocument.Load(project);
                var reference = Assert.Single(xml.Descendants("ProjectReference"));
                var referencedProject = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference.Attribute("Include")!.Value));
                Assert.Equal(ChangeProposalStore.Within(_proposals.CopyRoot(_proposal.Id, variant), _proposal.Project), referencedProject);
                Assert.Equal("TargetFramework=net8.0", reference.Element("SetTargetFramework")!.Value);
                Assert.Equal("net8.0", xml.Descendants("TargetFramework").Single().Value);
                Assert.Empty(xml.Descendants("Reference")); // No original DLL HintPath.
                Assert.Equal("false", xml.Descendants("EnableDefaultCompileItems").Single().Value);
                sources.Add(File.ReadAllText(Path.Combine(Path.GetDirectoryName(project)!, "Generated.cs")));
            }
            Assert.Equal(sources[0], sources[1]);
            Assert.Equal(InvestigationFiles.Hash(System.Text.Encoding.UTF8.GetBytes(sources[0])), fixture.SourceSha256);
        }
        Assert.False(File.Exists(Path.Combine(test.Directory, "execution-report.json")));
        Assert.False(File.Exists(Path.Combine(benchmark.Directory, "execution-report.json")));
        Assert.DoesNotContain("1 + 0", File.ReadAllText(Path.Combine(_proposal.Root, _proposal.File)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachKindCanIndependentlyUseAnExistingOrGeneratedFixture(bool generatedBenchmark)
    {
        var experiment = Prepare(generatedBenchmark ? "benchmark" : "test");
        var result = await Run(new(), generatedBenchmark ? null : experiment.Id, generatedBenchmark ? experiment.Id : null,
            testCompanion: generatedBenchmark ? "test" : null, benchmarkCompanion: generatedBenchmark ? null : "bench");
        Assert.Equal("validation_completed", result.Status);
        Assert.Equal(generatedBenchmark, result.GeneratedBenchmark is not null);
        Assert.Equal(!generatedBenchmark, result.GeneratedTest is not null);
    }
    [Fact] public async Task NoExistingCompanionsAreNeededWhenBothExperimentsAreSelected()
    {
        _proposals.Save(_proposal with { Companions = [] });
        var test = Prepare("test"); var benchmark = Prepare("benchmark");
        Assert.Equal("validation_completed", (await Run(new(), test.Id, benchmark.Id)).Status);
    }
    [Fact] public async Task MissingApprovalDoesNotStageOrRunAnything()
    {
        var test = Prepare("test"); var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(process, test.Id, benchmark.Id, approved: false));
        Assert.Empty(process.Calls); Assert.False(Directory.Exists(Path.Combine(_proposals.Folder(_proposal.Id), "generated")));
    }
    [Fact] public async Task OwnershipOfBothExperimentsIsRequiredBeforeStaging()
    {
        var test = Prepare("test"); var benchmark = Prepare("benchmark"); _owned.Remove(benchmark.Id); var process = new FakeProcess();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(process, test.Id, benchmark.Id));
        Assert.Empty(process.Calls); Assert.False(Directory.Exists(Path.Combine(_proposals.Folder(_proposal.Id), "generated")));
    }
    [Fact] public async Task RunnerCannotUseGeneratedIdsWithoutAnOwnershipCheck()
    {
        var test = Prepare("test"); var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ProposalValidationRunner(_proposals, _guard, process, _experiments)
            .RunAsync(_proposal.Id, null, null, null, true, default, test.Id, benchmark.Id));
        Assert.Empty(process.Calls);
    }
    [Fact] public async Task CompanionAndExperimentForTheSameKindAreRejected()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        await Assert.ThrowsAsync<ArgumentException>(() => Run(process, null, benchmark.Id, benchmarkCompanion: "bench"));
        Assert.Empty(process.Calls);
    }
    [Fact] public async Task WrongExperimentKindIsNotUsedAsATest()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        await Assert.ThrowsAsync<ArgumentException>(() => Run(process, benchmark.Id, benchmark.Id));
        Assert.Empty(process.Calls);
    }
    [Fact] public async Task ExperimentFromAnotherProjectIsNotSilentlyRetargeted()
    {
        var benchmark = Prepare("benchmark", "Other/Other.csproj"); var process = new FakeProcess();
        await Assert.ThrowsAsync<ArgumentException>(() => Run(process, null, benchmark.Id));
        Assert.Empty(process.Calls);
    }
    [Fact] public async Task EditedPreparedSourceIsRejectedBeforeAnyChildStarts()
    {
        var benchmark = Prepare("benchmark"); File.AppendAllText(Path.Combine(benchmark.Directory, "Generated.cs"), "// changed"); var process = new FakeProcess();
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(process, null, benchmark.Id));
        Assert.Empty(process.Calls);
    }
    [Theory]
    [InlineData("Generated.cs")]
    [InlineData("GeneratedBenchmarks.csproj")]
    [InlineData("Program.cs")]
    [InlineData("NuGet.Config")]
    [InlineData("Directory.Build.props")]
    [InlineData("global.json")]
    public async Task ChangedFrozenWorkloadCannotAuthorizeApply(string fileName)
    {
        var benchmark = Prepare("benchmark"); var report = await Run(new(), null, benchmark.Id);
        var fixtureFile = report.GeneratedBenchmark!.Files.First(f => Path.GetFileName(f.Path) == fileName);
        File.AppendAllText(ChangeProposalStore.Within(_proposals.Folder(_proposal.Id), fixtureFile.Path), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(new(), null, null));
        Assert.Throws<InvalidDataException>(() => _proposals.Replace(_proposal.Id, false, true, default));
        Assert.Equal("prepared", _proposals.Read(_proposal.Id).Status);
    }
    [Fact] public async Task AddedSourceFileDoesNotEnterTheFrozenBuild()
    {
        var benchmark = Prepare("benchmark"); var report = await Run(new(), null, benchmark.Id);
        var project = ProposalGeneratedExperiments.ProjectPath(_proposals, _proposal, report.GeneratedBenchmark!, "candidate");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "Extra.cs"), "class Extra {}");
        Assert.Throws<InvalidDataException>(() => _proposals.Replace(_proposal.Id, false, true, default));
    }
    [Fact] public async Task MissingGeneratedDescriptorDoesNotDowngradeToLegacyValidation()
    {
        var benchmark = Prepare("benchmark"); var report = await Run(new(), null, benchmark.Id);
        _proposals.SaveValidation(report with { GeneratedBenchmark = null });
        Assert.Throws<InvalidDataException>(() => _proposals.Replace(_proposal.Id, false, true, default));
    }
    [Fact] public async Task CompletedWorkloadCannotBeSwappedEvenForEqualSource()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess(); await Run(process, null, benchmark.Id);
        var other = Prepare("benchmark");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(process, null, other.Id));
        Assert.Equal(8, process.Calls.Count);
    }
    [Fact] public async Task FrozenFilterCannotChangeOnContinuation()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess(); await Run(process, null, benchmark.Id, filter: "*Value*");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(process, null, null, filter: "*Other*"));
        Assert.Equal(8, process.Calls.Count);
    }
    [Fact] public async Task ResumeUsesFrozenFilesNotMutableExperimentStoreOrOldReports()
    {
        var test = Prepare("test"); var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        File.WriteAllText(Path.Combine(benchmark.Directory, "execution-report.json"), "not an execution receipt for this proposal");
        await Run(process, test.Id, benchmark.Id); Assert.Equal(8, process.Calls.Count);
        Directory.Delete(test.Directory, true); Directory.Delete(benchmark.Directory, true);
        var resumed = await new ProposalValidationRunner(_proposals, _guard, process).RunAsync(_proposal.Id, null, null, null, true, default,
            authorizeExperiment: Authorize);
        Assert.Equal("validation_completed", resumed.Status); Assert.Equal(8, process.Calls.Count);
    }
    [Fact] public async Task OwnershipIsCheckedAgainForPersistedSelectionsOnResume()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess(); await Run(process, null, benchmark.Id); _owned.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(process, null, null));
        Assert.Equal(8, process.Calls.Count);
    }
    [Fact] public async Task AFailedCandidateTestStopsGeneratedBenchmarks()
    {
        var test = Prepare("test"); var benchmark = Prepare("benchmark"); var process = new FakeProcess { FailCandidateTest = true };
        var result = await Run(process, test.Id, benchmark.Id);
        Assert.Equal("validation_failed", result.Status); Assert.Equal(4, process.Calls.Count);
        Assert.False(ProposalValidationRunner.CanApply(result));
    }
    [Fact] public async Task AWorkloadChangedDuringExecutionStopsThePipeline()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess();
        process.AfterCall = command =>
        {
            if (command == "test") File.AppendAllText(Path.Combine(_proposals.Folder(_proposal.Id), "generated/benchmark/candidate/Generated.cs"), "// mutate");
        };
        var result = await Run(process, null, benchmark.Id);
        Assert.Equal("validation_failed", result.Status); Assert.Equal(2, process.Calls.Count);
        Assert.False(ProposalValidationRunner.CanApply(result));
    }
    [Fact] public async Task TooFewSamplesCannotEnableApply()
    {
        var benchmark = Prepare("benchmark"); var result = await Run(new() { Samples = 3 }, null, benchmark.Id);
        Assert.Equal("comparison_incomplete", result.Status); Assert.False(ProposalValidationRunner.CanApply(result));
    }
    [Fact] public async Task UnknownStartedGeneratedStageIsNotReplayed()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess(); var report = await Run(process, null, benchmark.Id);
        _proposals.SaveValidation(report with { Status = "running", Stages = [report.Stages[0] with { Status = "running" }] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(process, null, null));
        Assert.Equal(8, process.Calls.Count);
    }
    [Fact] public async Task CancelledInvocationDoesNotCreateGeneratedStaging()
    {
        var benchmark = Prepare("benchmark"); var process = new FakeProcess(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProposalValidationRunner(_proposals, _guard, process, _experiments)
            .RunAsync(_proposal.Id, null, null, null, true, cts.Token, benchmarkExperimentId: benchmark.Id, authorizeExperiment: Authorize));
        Assert.Empty(process.Calls); Assert.False(Directory.Exists(Path.Combine(_proposals.Folder(_proposal.Id), "generated")));
    }
    [Theory]
    [InlineData("netstandard2.0", "net8.0")]
    [InlineData("netstandard2.1", "net8.0")]
    [InlineData("net8.0", "net8.0")]
    [InlineData("net9.0", "net9.0")]
    public void StagingPreservesSubjectRuntimeAndNestedSdkPolicy(string subjectFramework, string runtimeFramework)
    {
        var root = Path.Combine(_home, "framework"); var sourceFolder = Path.Combine(root, "Src"); Directory.CreateDirectory(sourceFolder);
        var project = Path.Combine(sourceFolder, "Library.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>" + subjectFramework + "</TargetFramework></PropertyGroup></Project>");
        const string sdk = "{\"sdk\":{\"version\":\"10.0.100\"}}";
        File.WriteAllText(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"9.0.100\"}}");
        File.WriteAllText(Path.Combine(sourceFolder, "global.json"), sdk);
        const string source = "public static class Subject { public static int Value() => 1; }";
        var file = Path.Combine(sourceFolder, "Subject.cs"); File.WriteAllText(file, source);
        var guard = new WorkspaceGuard([root], true, false); var proposals = new ChangeProposalStore(guard, Path.Combine(_home, "framework-data"));
        var target = new ReviewTarget("target", "method", "method", "p", "Library", project, "Value", file, 1, ReviewProgress.Hash(source), "context", true, null, source);
        var proposal = proposals.Create(Guid.NewGuid().ToString("N"), "snapshot", target, root,
            InvestigationFiles.Hash(File.ReadAllBytes(file)), source.Replace("=> 1;", "=> 1 + 0;"), [], default);
        var experiments = new ExperimentStore(guard);
        var receipt = experiments.Create(root, "Src/Library.csproj", "benchmark", BenchCode, subjectFramework, runtimeFramework: runtimeFramework);
        var frozen = new ProposalGeneratedExperiments(proposals, experiments).Prepare(proposal, receipt.Id, "benchmark");
        Assert.Equal(subjectFramework, frozen.SubjectTargetFramework); Assert.Equal(runtimeFramework, frozen.RuntimeTargetFramework);
        foreach (var variant in new[] { "baseline", "candidate" })
        {
            var generated = ProposalGeneratedExperiments.ProjectPath(proposals, proposal, frozen, variant);
            var xml = XDocument.Load(generated);
            Assert.Equal(runtimeFramework, xml.Descendants("TargetFramework").Single().Value);
            Assert.Equal("TargetFramework=" + subjectFramework, xml.Descendants("SetTargetFramework").Single().Value);
            Assert.Equal(sdk, File.ReadAllText(Path.Combine(Path.GetDirectoryName(generated)!, "global.json")));
            Assert.Equal(subjectFramework, XDocument.Load(ChangeProposalStore.Within(proposals.CopyRoot(proposal.Id, variant), proposal.Project)).Descendants("TargetFramework").Single().Value);
        }
    }
    private sealed class FakeProcess : IWorkspaceBuildProcess
    {
        public readonly List<(string Command, string Project)> Calls = new();
        public bool FailCandidateTest;
        public int Samples = 5;
        public Action<string>? AfterCall;
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
                File.WriteAllText(Path.Combine(output, "bench-report-full.json"), JsonSerializer.Serialize(new
                {
                    HostEnvironmentInfo = new { ProcessorName = "cpu", OsVersion = "os", Architecture = "x64", RuntimeVersion = "net8", DotNetCliVersion = "10", Configuration = "Release" },
                    Benchmarks = new[] { new { Type = "Workload", Method = "Value", Parameters = "", Statistics = new { N = Samples, Mean = mean, Median = mean }, Memory = new { BytesAllocatedPerOperation = 16 } } }
                }));
            }
            AfterCall?.Invoke(command);
            return Task.FromResult(new WorkspaceBuildProcessResult(0, "synthetic process result", ""));
        }
    }
    public void Dispose()
    {
        foreach (var item in _environment) Environment.SetEnvironmentVariable(item.Key, item.Value);
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }
}
